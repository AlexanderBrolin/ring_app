using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Shared fixture builders for tests that need a "loaded" world instead of a
    /// bare `new SimulationWorld(seed, TestConfigs.Default())` — introduced by
    /// Task 29 (golden hash / allocation tests) so those two don't each carry
    /// their own copy of the saturation setup. Existing tests are untouched:
    /// only new Task 29 tests consume this.
    public static class TestWorlds
    {
        /// Capacity for a VisibilitySet sized to hold every live mob plus
        /// every player VisibilitySystem.Compute can visit in a single call
        /// (its own constructor doc). Task 21 fix-round 1 (M-2): lifted here
        /// out of VisibilityTests and EventDeliveryTests, which each carried
        /// a byte-identical private copy of this one-line helper — the
        /// project's "existing test helpers are reused" rule applies to test
        /// helpers duplicated across files, not only to production code.
        public static int Capacity(in SimConfig cfg) => cfg.Arena.MaxMobs + cfg.Arena.MaxPlayers;

        /// A world with every mob slot filled (via the SpawnMobForTest seam,
        /// half Chaser/half Gunner so both fire/movement/AI paths are live) and
        /// warmed up under sustained player fire for ~100 ticks, so its
        /// projectile/event population has already reached steady state before
        /// a caller starts measuring it (allocations, a "busy" golden tick,
        /// ...). Returns the config used so callers can read Arena caps etc.
        /// without reconstructing it.
        ///
        /// STILL AT CAP ON RETURN (Stage 3 Task 5 fix-round 2): the warm-up
        /// can now thin the crowd (friendly fire — see this method's own
        /// body), so a second SpawnMobsToCap call tops the population back up
        /// to Arena.MaxMobs AFTER the 100 ticks, before returning — the name
        /// "Saturated" is a promise about what the caller receives, not just
        /// about what got built on tick 0.
        public static SimulationWorld Saturated(out SimConfig config)
        {
            config = TestConfigs.Default();
            var world = new SimulationWorld(1, config);

            SpawnMobsToCap(world);

            var holdFire = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f) };
            for (int i = 0; i < 100; i++) world.Tick(holdFire);

            // Stage 3 Task 5 fix-round 2 (spec Р252, coordinator finding): the
            // crowd above is no longer guaranteed to still BE at cap after 100
            // ticks under sustained fire — friendly fire means a Gunner among
            // it can now connect with a neighboring mob instead of always
            // sailing past it, so some of the 96 die during warm-up (their
            // slots get replaced by the wave director too, but not
            // necessarily all of them within this window). "Saturated" is a
            // promise about the RETURNED world, not just the moment it was
            // built, so top up here, with the SAME seam that built the crowd
            // in the first place: SpawnMobsToCap's own SpawnMob cap-guard
            // makes a second call an EXACT top-up, not an over-fill — it
            // keeps spawning until _mobCount reaches MaxMobs again (however
            // many that takes), then silently no-ops for the remainder of
            // its own loop. This keeps the helper's name true for every
            // caller, not just the one that happens to check.
            SpawnMobsToCap(world);

            return world;
        }

        /// Fills every mob slot of `world` (half Chaser / half Gunner, spread on
        /// a golden-angle spiral so no two sit on the same ray). Stage 2 Task 17:
        /// lifted verbatim out of Saturated above — which now calls it — so the
        /// PvP candidate-scratch fixture can build the same crowd without
        /// inheriting Saturated's 100-tick warm-up under sustained fire. Reads
        /// the cap off the world's OWN config, so the two call sites can never
        /// disagree about how many "every slot" is.
        public static void SpawnMobsToCap(SimulationWorld world)
        {
            int cap = world.Config.Arena.MaxMobs;
            const float goldenAngleRad = 2.399963f; // even angular spread, no periodicity
            for (int i = 0; i < cap; i++)
            {
                float radius = 4f + (i % 24) * 1.2f; // well inside Arena.Radius (65 since Stage 2 Task 16)
                float angle = i * goldenAngleRad;
                float2 pos = radius * new float2(math.cos(angle), math.sin(angle));
                world.SpawnMobForTest((i & 1) == 0 ? MobType.Chaser : MobType.Gunner, pos);
            }
        }

        /// Stage 2 Task 18: three-player counterpart of Saturated above, built
        /// to prove Task 17's own PvP paths (the candidate scratch widened to
        /// MaxMobs + MaxPlayers + 2, ProjectileSystem's per-live-player gather
        /// loop, TickAll's own per-player stepping) actually RUN under a GC
        /// allocation measurement, not merely exist untested. Every mob slot
        /// is filled (the same SpawnMobsToCap call Saturated and
        /// PvpDamageTests' own trio fixture use) and all three players fire
        /// continuously — but the three are relocated off the natural spawn
        /// ring (Geometry.SpawnPosFor, radius Arena.Radius *
        /// PlayerSpawnRingFrac = 52) via the shared RelocatePlayerForTest seam
        /// below, out to a small huddle at radius (Arena.Radius *
        /// PlayerSpawnRingFrac + 6) — 58 on TestConfigs.Default() — clear of
        /// both the mob crowd (SpawnMobsToCap's own doc: radii roughly 4…31)
        /// and every DefaultArena() obstacle/wall (all inside radius ~44) with
        /// room to spare. Two reasons: firing along the NATURAL ring's own
        /// player-to-player chord passes within Arena.Radius *
        /// PlayerSpawnRingFrac * cos(60 deg) = 26 m of the centre — squarely
        /// inside the mob crowd — so whether a round ever clears it to reach
        /// another player would depend on the crowd's exact layout rather than
        /// being a fixture guarantee; and moving clear of the crowd also means
        /// no mob can close that gap during the short warm-up below, so every
        /// hit landed while warming up is unambiguously PvP, not incidental
        /// splash from a mob that wandered into the huddle.
        ///
        /// Players 0 and 1 duel each other point-blank (3 m apart, each
        /// aiming at the other's own static position — hip fire's per-shot
        /// direction, WeaponSystem.Update's normalize(AimPoint - p.Pos), is
        /// therefore exact and unchanging shot after shot). Player 2 fires
        /// the long way instead, back toward the arena centre and into the
        /// mob crowd — the same "long sustained shot" role Saturated's own
        /// holdFire plays, so a live projectile is guaranteed at the end of
        /// warm-up regardless of the duel's own volley timing (the shot needs
        /// roughly (radius - 31) / Weapon.ProjectileSpeed seconds just to
        /// REACH the crowd — see the radius arithmetic above — comfortably
        /// longer than TrioWarmupTicks below, so every copy fired during
        /// warm-up is still in flight when it ends) and the candidate scratch
        /// also sees the HitMob branch, not only HitPlayer.
        ///
        /// Warm-up itself stays SHORT (TrioWarmupTicks, not Saturated's 100):
        /// at 3 m every duel round connects, so a long warm-up would just burn
        /// through the Hp budget below before the caller even starts
        /// measuring. That budget is the fix-round 1 addition (Task 18
        /// review, I-1): a duelist that entered the MEASURED loop at plain
        /// Hero.MaxHp died around tick 14 of it (Weapon.Damage landing every
        /// ~FireInterval seconds at point-blank range comfortably outpaces
        /// MaxHp) — after which the PvP branch this fixture exists to
        /// exercise goes cold for the rest of the window, because the
        /// gather's own `player.Alive` gate (ProjectileSystem.Update) stops
        /// packing either corpse as a candidate. A short warm-up alone only
        /// moved that death INTO the measurement; it never prevented it.
        /// `measuredTicks` is therefore the CALLER's own upcoming loop length,
        /// and Hp is set (through the same SetPlayerForTest seam
        /// RelocatePlayerForTest below wraps) to a budget covering the WHOLE
        /// window — warm-up plus measurement — at a deliberately over-stated
        /// combined damage rate: the duel's own worst-case zone multiplier
        /// (Hero.HeadDamageMult) applied to Weapon.Damage / Weapon.FireInterval,
        /// PLUS every single one of Arena.MaxMobs dealing Chaser.ContactDamage
        /// every Chaser.AttackCooldown at once. That second term is physically
        /// impossible on its own terms (the crowd cannot even reach the huddle
        /// until the mob-to-huddle gap closes, let alone all 96 land a contact
        /// hit in the same cooldown window) — which is exactly the point: the
        /// bound only has to be safe, not tight, and a safe-but-huge Hp costs
        /// this fixture nothing (SetPlayerForTest bypasses Hero.MaxHp's own
        /// clamp entirely — ApplyConfig is the only place that clamp is
        /// enforced, and this fixture never calls it).
        public static SimulationWorld TrioSaturated(out SimConfig config, int measuredTicks)
        {
            config = TestConfigs.Default();
            var world = new SimulationWorld(1, config, playerCount: 3);

            SpawnMobsToCap(world);

            // Clear of the mob crowd (radii roughly 4…31, SpawnMobsToCap's own
            // doc above) and every DefaultArena() obstacle/wall (all inside
            // radius ~44) — tied to the SAME config fields the natural ring
            // itself reads (Arena.Radius, Arena.PlayerSpawnRingFrac), not a
            // bare literal (Task 18 fix-round 1, M-3), so a future
            // arena-layout tuning pass that moves the ring moves this huddle
            // right along with it instead of leaving it silently stranded.
            float huddleRadius = config.Arena.Radius * config.Arena.PlayerSpawnRingFrac + 6f;
            var p0 = new float2(-1.5f, huddleRadius);
            var p1 = new float2(1.5f, huddleRadius);
            var p2 = new float2(0f, huddleRadius + 1.5f);

            // Fix-round 1 (I-1): Hp budget covers TrioWarmupTicks below PLUS
            // the caller's own measuredTicks, at the deliberately over-stated
            // rate this method's own doc derives — see there for why each
            // term is safe rather than tight.
            float totalSeconds = (TrioWarmupTicks + measuredTicks) * SimulationWorld.TickDt;
            float duelDps = config.Hero.HeadDamageMult * config.Weapon.Damage / config.Weapon.FireInterval;
            float mobDps = config.Arena.MaxMobs * config.Chaser.ContactDamage / config.Chaser.AttackCooldown;
            float hpBudget = totalSeconds * (duelDps + mobDps);

            RelocatePlayerForTest(world, 0, p0, hpBudget);
            RelocatePlayerForTest(world, 1, p1, hpBudget);
            RelocatePlayerForTest(world, 2, p2, hpBudget);

            var inputs = new SimInput[3];
            inputs[0] = new SimInput { FireHeld = true, AimPoint = p1 };
            inputs[1] = new SimInput { FireHeld = true, AimPoint = p0 };
            inputs[2] = new SimInput { FireHeld = true, AimPoint = float2.zero };
            for (int i = 0; i < TrioWarmupTicks; i++) world.TickAll(inputs);

            // Fix-round 1 (M-3): prove Geometry.Depenetrate — run
            // unconditionally every tick by PlayerMovementSystem — actually
            // left the huddle where it was placed instead of nudging it. The
            // radius arithmetic this whole fixture leans on (the 26 m chord,
            // the 27 m mob-to-huddle gap, etc., all documented above) is only
            // true of the positions assigned above, not of wherever
            // depenetration might have silently pushed them since.
            const float posTolerance = 1e-3f;
            Assert.Less(math.distance(world.PlayerAt(0).Pos, p0), posTolerance,
                "fixture premise: depenetration must not have moved player 0");
            Assert.Less(math.distance(world.PlayerAt(1).Pos, p1), posTolerance,
                "fixture premise: depenetration must not have moved player 1");
            Assert.Less(math.distance(world.PlayerAt(2).Pos, p2), posTolerance,
                "fixture premise: depenetration must not have moved player 2");

            return world;
        }

        /// See TrioSaturated's own doc above for why this is short rather than
        /// mirroring Saturated's 100.
        const int TrioWarmupTicks = 16;

        /// Moves a player to an exact spot through the SetPlayerForTest seam —
        /// a multiplayer world otherwise spawns its players on the ring
        /// (Geometry.SpawnPosFor), which is no use to a fixture that has to
        /// state a firing line down to the metre. Stage 2 Task 17
        /// (PvpDamageTests) established this exact move under the name
        /// `PlaceAt`; Task 18 fix-round 1 (M-1) lifted it here — its only home
        /// now — so the two test files stop carrying byte-identical copies of
        /// the same three lines and docstring.
        /// `hp` (Task 18 fix-round 1, I-1) is a TRAILING optional override —
        /// NaN (the default) leaves Hp untouched, so every PvpDamageTests call
        /// site keeps compiling and behaving exactly as `PlaceAt` did; only
        /// TrioSaturated passes a real value, to budget survivable Hp for its
        /// whole measured window.
        public static void RelocatePlayerForTest(SimulationWorld world, int index, float2 pos,
            float hp = float.NaN)
        {
            PlayerState p = world.PlayerAt(index);
            p.Pos = pos;
            if (!float.IsNaN(hp)) p.Hp = hp;
            world.SetPlayerForTest(index, p);
        }

        /// Places a batch of mobs in one call (Task 6) — the tuple form keeps a
        /// multi-mob fixture readable as a single statement instead of a column
        /// of SpawnMobForTest calls. Slot order equals argument order, which the
        /// candidate tie-break tests depend on.
        public static void SpawnMobsAt(SimulationWorld world,
            params (MobType type, float2 pos)[] mobs)
        {
            for (int i = 0; i < mobs.Length; i++)
                world.SpawnMobForTest(mobs[i].type, mobs[i].pos);
        }

        /// Test-only 3D-aimed shot (Task 6): builds the velocity from the unit
        /// 3D direction (origin, muzzleH) → (targetXY, targetH) scaled by
        /// Weapon.ProjectileSpeed, so the projectile's height at horizontal
        /// distance d is exactly muzzleH + d·(targetH − muzzleH)/|targetXY −
        /// origin| — the straight line a height-gating fixture reasons about.
        /// Weapon damage/radius/lifetime come from the world's own config, so a
        /// caller only ever states geometry. `ownerIndex` (Stage 2 Task 17) says
        /// which player fired — a TRAILING parameter defaulting to 0 (the solo
        /// player, same test default SpawnProjectileForTest's own `ownerIndex`
        /// documents), so every Э1 call site keeps compiling and behaving
        /// identically while the PvP fixtures can state a real shooter.
        public static int FireAimed3D(SimulationWorld world, float2 origin, float muzzleH,
            float2 targetXY, float targetH, byte ownerIndex = 0)
        {
            WeaponSimConfig weapon = world.Config.Weapon;
            float2 flat = targetXY - origin;
            float dz = targetH - muzzleH;
            float len = math.sqrt(math.lengthsq(flat) + dz * dz);
            float2 dir = len > 1e-6f ? flat / len : new float2(1f, 0f);
            float velZ = len > 1e-6f ? dz / len * weapon.ProjectileSpeed : 0f;
            return world.SpawnProjectileForTest(ProjectileOwner.Player, origin,
                dir * weapon.ProjectileSpeed, muzzleH, velZ,
                weapon.Damage, weapon.ProjectileRadius, weapon.ProjectileLifetime, ownerIndex);
        }

        /// Ticks with idle input until no projectile is left in flight (or the
        /// tick budget runs out) and returns how many ticks that took. `maxTicks`
        /// is a stall guard, not an expectation: a fixture whose mobs shoot back
        /// legitimately runs to the cap, so callers assert on world state, never
        /// on the return value being below it.
        public static int RunUntilProjectilesDie(SimulationWorld world, int maxTicks = 120)
        {
            int ticks = 0;
            while (ticks < maxTicks && world.ProjectileCount > 0)
            {
                world.Tick(default);
                ticks++;
            }
            return ticks;
        }

        /// Ticks `world` with idle input until its first wave has folded into
        /// `WorldStats.WavesCleared` (Stage 2 Task 5) — every mob that spawns is
        /// killed outright via the DamageMob seam (same swap-remove path a real
        /// kill takes) the instant it appears, so the wave's own debt-then-
        /// MobCount==0 clear check (WaveSystem.Update) fires without needing
        /// weapon fire to land. `maxTicks` covers TestConfigs.Default()'s
        /// FirstWaveDelay (75 ticks) plus spawn/clear settling with generous
        /// headroom; it is a stall guard, not an expectation — callers assert on
        /// WorldStats.WavesCleared itself. Returns the number of ticks consumed,
        /// same contract as RunUntilProjectilesDie above (T5 fix-round 1 M-4): if a
        /// caller's own WavesCleared assertion then fails, this return value
        /// tells them whether the fixture ran out of budget (its own bug) or the
        /// wave genuinely never cleared (a product bug) — without it the failure
        /// reads as a silent product regression either way. DamageMob's
        /// `ownerIndex` is REQUIRED (fix-round 1 I-1 — no default on a production
        /// method) — passed explicitly as `0` below (test default: the solo
        /// player, Stage 2 Task 7, see DamageMob's own doc) — every kill this
        /// helper causes incidentally credits ShotsHit/Kills to player 0's
        /// personal MatchStats, so a caller asserting on player 0's own personal
        /// stats after calling this must account for that side effect.
        public static int ClearFirstWave(SimulationWorld world, int maxTicks = 300)
        {
            var inputs = new SimInput[world.PlayerCount];
            int ticks = 0;
            for (; ticks < maxTicks && world.WorldStats.WavesCleared == 0; ticks++)
            {
                world.TickAll(inputs);
                while (world.MobCount > 0)
                    world.DamageMob(0, 1e9f, world.Mobs[0].Pos, HitZone.Body, default, ownerIndex: 0);
            }
            return ticks;
        }
    }
}
