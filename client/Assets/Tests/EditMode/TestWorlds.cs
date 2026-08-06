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
        /// A world with every mob slot filled (via the SpawnMobForTest seam,
        /// half Chaser/half Gunner so both fire/movement/AI paths are live) and
        /// warmed up under sustained player fire for ~100 ticks, so its
        /// projectile/event population has already reached steady state before
        /// a caller starts measuring it (allocations, a "busy" golden tick,
        /// ...). Returns the config used so callers can read Arena caps etc.
        /// without reconstructing it.
        public static SimulationWorld Saturated(out SimConfig config)
        {
            config = TestConfigs.Default();
            var world = new SimulationWorld(1, config);

            SpawnMobsToCap(world);

            var holdFire = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f) };
            for (int i = 0; i < 100; i++) world.Tick(holdFire);

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
        /// PlayerSpawnRingFrac = 52) via the SetPlayerForTest seam
        /// PvpDamageTests established, out to a small huddle at radius ~58
        /// that clears both the mob crowd (SpawnMobsToCap's own doc: radii
        /// roughly 4…31) and every DefaultArena() obstacle/wall (all inside
        /// radius ~44) with room to spare. Two reasons: firing along the
        /// NATURAL ring's own player-to-player chord passes within
        /// Arena.Radius * PlayerSpawnRingFrac * cos(60 deg) = 26 m of the
        /// centre — squarely inside the mob crowd — so whether a round ever
        /// clears it to reach another player would depend on the crowd's
        /// exact layout rather than being a fixture guarantee; and moving
        /// clear of the crowd also means no mob can close that gap during the
        /// short warm-up below, so every hit landed while warming up is
        /// unambiguously PvP, not incidental splash from a mob that wandered
        /// into the huddle.
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
        /// Warm-up is intentionally SHORT (TrioWarmupTicks, not Saturated's
        /// 100): at 3 m every duel round connects, and Weapon.Damage stacked
        /// up every ~FireInterval seconds would kill a duelist in roughly
        /// nine rounds — long before Saturated's own 100 ticks. Task 18's
        /// whole point is a world where all three are still alive to be
        /// measured, not a duel that finishes itself before the fixture does.
        public static SimulationWorld TrioSaturated(out SimConfig config)
        {
            config = TestConfigs.Default();
            var world = new SimulationWorld(1, config, playerCount: 3);

            SpawnMobsToCap(world);

            // Clear of the mob crowd (radii roughly 4…31, SpawnMobsToCap's own
            // doc above) and every DefaultArena() obstacle/wall (all inside
            // radius ~44), with room to spare before Arena.Radius (65).
            const float huddleY = 58f;
            var p0 = new float2(-1.5f, huddleY);
            var p1 = new float2(1.5f, huddleY);
            var p2 = new float2(0f, huddleY + 1.5f);
            RelocatePlayerForTest(world, 0, p0);
            RelocatePlayerForTest(world, 1, p1);
            RelocatePlayerForTest(world, 2, p2);

            var inputs = new SimInput[3];
            inputs[0] = new SimInput { FireHeld = true, AimPoint = p1 };
            inputs[1] = new SimInput { FireHeld = true, AimPoint = p0 };
            inputs[2] = new SimInput { FireHeld = true, AimPoint = float2.zero };
            for (int i = 0; i < TrioWarmupTicks; i++) world.TickAll(inputs);

            return world;
        }

        /// See TrioSaturated's own doc above for why this is short rather than
        /// mirroring Saturated's 100.
        const int TrioWarmupTicks = 16;

        /// Moves a player to an exact spot through the SetPlayerForTest seam
        /// (PvpDamageTests.PlaceAt established the same move for its own duel
        /// fixtures) — a multiplayer world otherwise spawns its players on the
        /// ring (Geometry.SpawnPosFor), which is no use to a fixture that has
        /// to state a firing line down to the metre.
        static void RelocatePlayerForTest(SimulationWorld world, int index, float2 pos)
        {
            PlayerState p = world.PlayerAt(index);
            p.Pos = pos;
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
