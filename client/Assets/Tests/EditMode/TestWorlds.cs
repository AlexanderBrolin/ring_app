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
        /// Stage 3 Task 26 (plan errata E-6 C-I3): the formula itself moved to
        /// `VisibilitySet.CapacityFor`, the ONE home it now shares with
        /// SnapshotAssembler.Connection — which spelled the same sum out
        /// separately until this task. This overload stays because DOZENS of
        /// call sites in this suite ask for the MOB capacity by that name.
        /// ⚠ The count deliberately is NOT spelled out here any more: Т26's
        /// review had it counted exactly (71 then), Т29 added five call sites
        /// four days later and the number went stale inside its own phase
        /// (gate Ф6, review m2 — lesson 366: a figure derived from someone
        /// else's state lives until that state next moves). What matters is
        /// the fact, not the census. It states which class it means and
        /// delegates.
        public static int Capacity(in SimConfig cfg)
            => Visibility.VisibilitySet.CapacityFor(in cfg.Arena, Visibility.VisibilityClass.Mobs);

        /// app-88jb Т15: the worst damage multiplier a body's own parts can
        /// apply to a blow — the number a deliberately OVER-STATED Hp budget
        /// has to assume. It used to be read off the collector's head-zone
        /// multiplier in the TWO homes of that budget (TrioSaturated below and
        /// DeterminismTests.BudgetHpForTheWholeRun) until that field left
        /// SimConfig with the rest of the zone column. ONE home now: two copies
        /// of one bound are two chances to drift apart, and this suite has paid
        /// for that shape before (TestWorlds' own Capacity doc above records
        /// the same lesson).
        ///
        /// THE MAXIMUM, NOT "THE LAST PART'S": the budget only has to be SAFE,
        /// and the head carrying the largest multiplier is today's DATA rather
        /// than a rule anything enforces. On the fixture bodies the two answers
        /// coincide at 1.7, which is why replacing the old read moves no golden
        /// digest — the Hp handed out is bit-for-bit the number it was.
        ///
        /// A null or empty stack answers 1, the neutral multiplier: a body with
        /// no parts is budgeted at the weapon's own damage rather than at zero.
        public static float MaxPartDamageMult(HitPart[] parts)
        {
            if (parts == null || parts.Length == 0) return 1f;
            float worst = parts[0].DamageMult;
            for (int i = 1; i < parts.Length; i++)
                if (parts[i].DamageMult > worst) worst = parts[i].DamageMult;
            return worst;
        }

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

            // Stage 3 Task 12: the shooter is moved off the arena center, 7 m
            // inside the innermost zone wall — the SAME huddle radius (58)
            // TrioSaturated uses, and for the same reason, now that the crowd
            // is three times denser (MaxMobs 96 -> 288). At the center the
            // player stands INSIDE the spiral: 12 chasers reach it by tick 17
            // (the ring at radius 4 closes 2.9 m at 5.2 m/s) instead of the 4
            // that used to, the mobs then pile onto it at AttackRange, and
            // every round is absorbed in its own muzzle — the fixture's own
            // "sustained fire" premise (ProjectileCount > 0 on return) died
            // silently, which is what AllocationTests.Tick_DoesNotAllocateGC
            // reported. From 58 the arithmetic is provable rather than lucky:
            // the nearest mob is 26.4 m away and needs 5.1 s (152 ticks) to
            // close it, so nothing touches the shooter inside a 100-tick
            // warm-up; a round covers that gap in 22 ticks against a 45-tick
            // lifetime, so rounds are always both in flight AND reaching the
            // crowd (the HitMob branch every caller of this fixture measures).
            float shooterRadius = config.Arena.ZoneWallRadius[0] - 7f;
            RelocatePlayerForTest(world, 0, new float2(0f, shooterRadius));
            var holdFire = new SimInput { FireHeld = true, AimPoint = float2.zero };
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
                // Radii 4…31.6 regardless of `cap` — the modulo keeps the
                // spiral inside the CORE zone whatever MaxMobs is (96 through
                // Stage 2, 288 since Stage 3 Task 12's own arena), which is
                // what every caller's geometry doc below leans on.
                float radius = 4f + (i % 24) * 1.2f;
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
        /// PlayerSpawnRingFrac = 103.96 since Stage 3 Task 12) via the shared
        /// RelocatePlayerForTest seam below, out to a small huddle 7 m inside
        /// the innermost zone wall — 58 on TestConfigs.Default() — clear of
        /// both the mob crowd (SpawnMobsToCap's own doc: radii roughly 4…31)
        /// and every DefaultArena() obstacle/wall (all inside radius ~44) with
        /// room to spare.
        ///
        /// Stage 3 Task 12 re-derived that radius, and the reason is the whole
        /// point of the fixture rather than tidying. It used to read
        /// `Radius * PlayerSpawnRingFrac + 6`, which was 58 on the Stage 2
        /// arena and becomes 109.96 on the three-zone one — out in the OUTER
        /// zone, with two arc barriers between the huddle and the crowd. Every
        /// premise stated below would have quietly died there: player 2's long
        /// shot toward the center would be stopped by the outer ring at 92
        /// (spec §3.15 offsets the two rings' doors precisely so that no
        /// straight ray from the outer zone reaches the core), so it would
        /// neither still be in flight at the end of warm-up nor ever reach a
        /// mob — the HitMob branch of the candidate scratch would stop being
        /// exercised at all, with every assertion in this file still green.
        /// 58 is the same number as before and keeps every sentence below
        /// true; it is now tied to the zone wall that has to stay out of the
        /// firing line rather than to the spawn ring that moved. Two reasons: firing along the NATURAL ring's own
        /// player-to-player chord passes within Arena.Radius *
        /// PlayerSpawnRingFrac * cos(60 deg) = 26 m of the center — squarely
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
        /// the long way instead, back toward the arena center and into the
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
        /// combined damage rate: the duel's own worst-case part multiplier
        /// (MaxPartDamageMult above) applied to Weapon.Damage / Weapon.FireInterval,
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
            // Stage 3 Т22 (rule of fixtures R-173/351): ZONELESS. Every body in
            // this fixture — the mob crowd at radii 4…31 and the huddle at 58 —
            // stands inside the CORE zone of TestConfigs.Default(), and a live
            // collector standing there is what activates the Director from Т22
            // on. This fixture is about ALLOCATIONS, not about zones, and an
            // arena with no zones has no core to walk into. Nothing it measures
            // moves: the huddle's own radius is derived from ZoneWallRadius (the
            // zone WALLS, which stay), so the geometry every premise below
            // leans on is unchanged to the meter.
            config.Arena.ZoneRadius = System.Array.Empty<float>();
            var world = new SimulationWorld(1, config, playerCount: 3);

            SpawnMobsToCap(world);

            // Clear of the mob crowd (radii roughly 4…31, SpawnMobsToCap's own
            // doc above) and every DefaultArena() obstacle/wall (all inside
            // radius ~44) — tied to the SAME config fields the natural ring
            // itself reads (Arena.Radius, Arena.PlayerSpawnRingFrac), not a
            // bare literal (Task 18 fix-round 1, M-3), so a future
            // arena-layout tuning pass that moves the ring moves this huddle
            // right along with it instead of leaving it silently stranded.
            // Stage 3 Task 12: 7 m inside the innermost zone wall (58 on
            // TestConfigs.Default()) — see this method's own doc for why the
            // pre-Stage-3 expression, tied to the player spawn ring, silently
            // stopped meaning what it said. Still config-derived, not a bare
            // literal (Task 18 fix-round 1, M-3): an owner retune of the core
            // boundary moves this huddle with it.
            float huddleRadius = config.Arena.ZoneWallRadius[0] - 7f;
            var p0 = new float2(-1.5f, huddleRadius);
            var p1 = new float2(1.5f, huddleRadius);
            var p2 = new float2(0f, huddleRadius + 1.5f);

            // Fix-round 1 (I-1): Hp budget covers TrioWarmupTicks below PLUS
            // the caller's own measuredTicks, at the deliberately over-stated
            // rate this method's own doc derives — see there for why each
            // term is safe rather than tight.
            float totalSeconds = (TrioWarmupTicks + measuredTicks) * SimulationWorld.TickDt;
            float duelDps = MaxPartDamageMult(config.Hero.Parts) * config.Weapon.Damage / config.Weapon.FireInterval;
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
        /// state a firing line down to the meter. Stage 2 Task 17
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

        /// Stage 3 Т22: N input-free ticks through the FULL TickAll (not
        /// Tick), so systems that only exist on the all-players path — the
        /// phase machine among them — actually run. Lifted here from
        /// MatchFlowTests' own private Idle the moment a second test class
        /// needed it (rule 2); that file now delegates to this one.
        public static void IdleTicks(SimulationWorld world, int ticks = 1)
        {
            var inputs = new SimInput[world.PlayerCount];
            for (int i = 0; i < ticks; i++) world.TickAll(inputs);
        }

        /// Places a batch of mobs in one call (Task 6) — the tuple form keeps a
        /// multi-mob fixture readable as a single statement instead of a column
        /// of SpawnMobForTest calls. Slot order equals argument order, which the
        /// candidate tie-break tests depend on. (Ф5 gate, review B-2: this doc
        /// had drifted onto IdleTicks above when Т22 inserted that helper.)
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
        /// Goes through `TickAll`, not the solo `Tick(in SimInput)` overload
        /// (app-88jb Т3, Ruling 13): `Tick` throws `InvalidOperationException`
        /// the moment `world.PlayerCount > 1` (its own doc, "the solo overload
        /// — throws for a multiplayer world"), and this helper has no way to
        /// know which kind of world it was handed — a caller building a
        /// two-collector fixture would fail with a raised exception, not a RED
        /// assertion, on ANY implementation. `TickAll` is safe for both: for a
        /// solo world it is byte-for-byte what `Tick` already does internally
        /// (stackalloc a one-element span and forward), so nothing observable
        /// changes for any existing single-player caller.
        public static int RunUntilProjectilesDie(SimulationWorld world, int maxTicks = 120)
        {
            int ticks = 0;
            var inputs = new SimInput[world.PlayerCount];
            while (ticks < maxTicks && world.ProjectileCount > 0)
            {
                world.TickAll(inputs);
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
                    world.DamageMob(0, 1e9f, world.Mobs[0].Pos, HitZone.Body, default, ownerIndex: 0, hitHeight: 0f,
                        projectileMass: 0f, projectileSpeed3D: 0f);
            }
            return ticks;
        }

        /// Stage 3 Т24: the arena's own exit layout, resolved from the config
        /// instead of restated. Lifted here out of ExtractionTests the moment
        /// a second class (ResultsTests) needed the same four helpers — the
        /// same "test helpers duplicated across files" rule Capacity above
        /// records, applied before the copy was made rather than after.
        /// An owner retune of the layout moves every caller with it.
        /// A point comfortably inside the core zone — half its radius out
        /// along +X (Ф5 gate, review B-6). Lifted here after the phase grew
        /// TWO byte-identical private copies of it (MatchFlowTests,
        /// EliteAndDirectorTests) and eight more raw transcriptions of the
        /// same expression across six files. Stated from the config, so a
        /// retune of the zone radii moves every caller with it.
        ///
        /// STANDING A LIVE COLLECTOR HERE WAKES THE DIRECTOR AND HIS RETINUE
        /// (R-173's fixture rule) — which is the point at every call site that
        /// uses it, and the reason a fixture that does NOT want that belongs
        /// on a zoneless arena instead.
        public static float2 InsideCore(in SimConfig cfg)
            => new float2(cfg.Arena.ZoneRadius[0] * 0.5f, 0f);

        public static int IndexOfExit(in SimConfig cfg, ExitKind kind)
        {
            for (int i = 0; i < cfg.Arena.ExtractPos.Length; i++)
                if ((ExitKind)cfg.Arena.ExtractKind[i] == kind) return i;
            return -1;
        }

        public static float2 EarlyPortalPos(in SimConfig cfg)
            => cfg.Arena.ExtractPos[IndexOfExit(in cfg, ExitKind.Portal)];

        public static float2 GatePos(in SimConfig cfg)
            => cfg.Arena.ExtractPos[IndexOfExit(in cfg, ExitKind.Gate)];

        /// TestConfigs.Open() (which ships the real exit layout) with an
        /// extraction channel short enough to hold inside a test, stated in
        /// TICKS and converted through the same arithmetic production
        /// performs. Open() itself is NOT touched — its zones are owner
        /// decision R-76 and two other fixtures stand on them.
        public static SimConfig ExitFixture(int channelTicks = 6)
        {
            SimConfig c = TestConfigs.Open();
            c.Flow.ExtractChannelSeconds = channelTicks * SimulationWorld.TickDt;
            return c;
        }

        /// Walks the raid to GateOpen: someone enters the core, the Director
        /// spawns (Т22) and is put down, and the sharing window elapses. The
        /// caller states GateDelaySeconds = 0 on its own fixture when it wants
        /// that window to be instant.
        public static void OpenTheGate(SimulationWorld world, in SimConfig cfg)
        {
            RelocatePlayerForTest(world, 2, InsideCore(in cfg));
            IdleTicks(world);
            for (int i = 0; i < world.MobCount; i++)
            {
                if (world.Mobs[i].Type != MobType.Director) continue;
                world.DamageMob(i, 1e9f, world.Mobs[i].Pos, HitZone.Body, float2.zero, ownerIndex: 0, hitHeight: 0f,
                    projectileMass: 0f, projectileSpeed3D: 0f);
                break;
            }
            IdleTicks(world, 2);
            Assert.AreEqual(MatchPhase.GateOpen, world.Match.Phase, "premise: the gate is open");
        }

        // ── app-88jb Т22: ONE fixture for every body-push witness ─────────────
        //
        // Seven tests in BodyCollisionTests read the push law from different
        // angles (speed, mass, projection, the guard, the recoil, the combo, the
        // chain). Writing seven fixtures would have been seven chances for one
        // of them to differ in a way nobody meant, so the shape is written once
        // here and the tests differ only in ARGUMENTS -- the same discipline
        // SimConfigBuilder's ValidateMob follows for four archetypes.

        /// How the collector was moving when it met the body.
        internal enum MoveMode { Run, Slide, Dash }

        /// Drives a collector straight along +x into ONE body and reports the
        /// hardest shove it landed, plus its own speed at that moment.
        ///
        /// The run-up distance is PER MODE and that is not a fudge: a dash lasts
        /// 0.15 s and covers 3.3 m, a slide 0.52 s and 7.0 m, while a run needs
        /// runway to reach MaxSpeed at all. Putting the body where each mode is
        /// actually at speed is what makes the three numbers comparable.
        internal static void RunIntoBody(MobType type, MoveMode mode, float offsetY,
            out float bodySpeed, out float collectorSpeed)
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg);
            float x = mode switch { MoveMode.Dash => 1.5f, MoveMode.Slide => 2.5f, _ => 12f };
            SpawnMobsAt(w, (type, new float2(x, offsetY)));
            var m = w.Mobs[0]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; w.SetMobForTest(0, m);

            PlayerState p = w.Player;
            SimInput input = default;
            switch (mode)
            {
                case MoveMode.Run:
                    input = new SimInput { MoveDir = new float2(1f, 0f) };
                    break;
                case MoveMode.Slide:
                    p.SlideTimer = cfg.Hero.SlideDuration;
                    p.SlideDir = new float2(1f, 0f);
                    break;
                case MoveMode.Dash:
                    p.DashTimer = cfg.Hero.DashDuration;
                    p.DashDir = new float2(1f, 0f);
                    p.DashSpeedCur = cfg.Hero.DashSpeed;
                    break;
            }
            w.SetPlayerForTest(0, p);

            bodySpeed = 0f;
            collectorSpeed = math.length(w.Player.Vel);
            for (int i = 0; i < 80; i++)
            {
                w.Tick(input);
                float s = math.length(w.Mobs[0].Vel);
                if (s > bodySpeed)
                {
                    bodySpeed = s;
                    collectorSpeed = math.length(w.Player.Vel);
                }
            }
        }

        /// Puts a collector ALREADY OVERLAPPING a body and slides it SIDEWAYS,
        /// perpendicular to the contact normal; returns the hardest shove the
        /// body took.
        ///
        /// ⚠ THIS IS THE WITNESS FOR THE PROJECTION (session 72, mutation M32),
        /// and the earlier "grazing pass" fixture was not: in a graze the fresh
        /// overlap is tiny, so ruling 117's overlap cap holds the blow down
        /// whether the speed is projected onto the normal or taken whole, and
        /// the mutation survived. Deep overlap plus perpendicular motion is the
        /// one shape where the two disagree — the cap is wide open (the bodies
        /// are 0.45 m into each other) while the projection is zero, because the
        /// collector is going past the body rather than into it.
        internal static float SidewaysPushOnOverlappedChaser()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg);
            SpawnMobsAt(w, (MobType.Chaser, new float2(0.5f, 0f)));
            var m = w.Mobs[0]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; w.SetMobForTest(0, m);

            PlayerState p = w.Player;
            p.SlideTimer = cfg.Hero.SlideDuration;
            p.SlideDir = new float2(0f, 1f);          // ВДОЛЬ, не В тело
            w.SetPlayerForTest(0, p);

            float best = 0f;
            for (int i = 0; i < 3; i++)
            {
                w.Tick(default);
                best = math.max(best, math.length(w.Mobs[0].Vel));
            }
            return best;
        }

        /// Slides a collector through a line of chasers and returns its speed on
        /// the LAST tick of the slide. `count` bodies stand 2 m apart from x = 2,
        /// which is inside the slide's own 7 m reach.
        internal static float SlideThroughChasers(int count) => SlideThroughChasers(count, out _);

        /// `minSpeed` is the DIP -- the lowest speed the slide reached while it
        /// ran. Without it the thruster has no witness at all: by the last tick
        /// the engine has won most of the loss back, so the exit speed alone
        /// cannot tell "a collision cost something and was recovered" from
        /// "nothing ever happened".
        internal static float SlideThroughChasers(int count, out float minSpeed)
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg);
            var spawns = new (MobType, float2)[count];
            for (int i = 0; i < count; i++) spawns[i] = (MobType.Chaser, new float2(2f + 2f * i, 0f));
            SpawnMobsAt(w, spawns);
            for (int i = 0; i < count; i++)
            {
                var m = w.Mobs[i]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; w.SetMobForTest(i, m);
            }

            PlayerState p = w.Player;
            p.SlideTimer = cfg.Hero.SlideDuration;
            p.SlideDir = new float2(1f, 0f);
            w.SetPlayerForTest(0, p);

            float last = math.length(w.Player.Vel);
            minSpeed = float.MaxValue;
            for (int i = 0; i < 40; i++)
            {
                w.Tick(default);
                if (w.Player.SlideTimer <= 0f) break;
                last = math.length(w.Player.Vel);
                minSpeed = math.min(minSpeed, last);
            }
            return last;
        }

        /// Dashes into the FIRST of two chasers standing in line and returns the
        /// hardest shove the SECOND one took — the witness that the law reaches
        /// the mob↔mob pair and not only the collector's own contact.
        ///
        /// ⚠⚠ THE SECOND CHASER STANDS OUTSIDE THE COLLECTOR'S OWN REACH, and
        /// that distance is the whole witness (session 72, mutation M31). The
        /// first form of this fixture put it at 2.6 m — well inside the dash's
        /// 3.3 m travel plus 0.95 m of contact — so the COLLECTOR shoved it
        /// directly and the test passed with the mob↔mob impulse deleted. At
        /// 6.0 m only the flying chaser can reach it, so the assert is about the
        /// chain and nothing else.
        internal static float SecondRowSpeedAfterDash()
        {
            SimConfig cfg = TestConfigs.OpenField();
            // ⚠ THE SOFT SEPARATION IS SWITCHED OFF, and without that this
            // fixture witnesses nothing (session 72, mutation M31 surviving
            // three times). The soft pass adds a FORCE of up to
            // SeparationStrength 6 straight into Vel whenever two mobs come
            // within 2.4 m — so the second chaser was knocked back by a
            // mechanism that predates Т22 entirely, and deleting the momentum
            // law changed nothing the assert could see. With the radius at zero
            // that pass early-outs (its own `threshold <= 0f`), exactly as
            // MobAiTests already does to isolate its wall tie-break, and the
            // only thing left that can move the second body is the impulse
            // carried into it by the first.
            cfg.Chaser.SeparationRadius = 0f;
            var w = new SimulationWorld(7, cfg);
            SpawnMobsAt(w, (MobType.Chaser, new float2(1.5f, 0f)),
                (MobType.Chaser, new float2(6f, 0f)));
            for (int i = 0; i < 2; i++)
            {
                var m = w.Mobs[i]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; w.SetMobForTest(i, m);
            }

            PlayerState p = w.Player;
            p.DashTimer = cfg.Hero.DashDuration;
            p.DashDir = new float2(1f, 0f);
            p.DashSpeedCur = cfg.Hero.DashSpeed;
            w.SetPlayerForTest(0, p);

            // ⚠ THE SIGNED X COMPONENT, NOT THE SPEED (session 72, mutation
            // M31 surviving twice). A chaser reaches 5.2 m/s under its own legs,
            // so any threshold on |Vel| below that is satisfied by the mob
            // simply running — the test could not fail. Both chasers stand
            // BETWEEN the collector and nothing, with the player behind them at
            // x = 0, so their own AI only ever drives them towards NEGATIVE x.
            // A positive x velocity therefore has exactly one possible source:
            // being knocked back by the body in front.
            float best = 0f;
            for (int i = 0; i < 30; i++)
            {
                w.Tick(default);
                best = math.max(best, w.Mobs[1].Vel.x);
            }
            return best;
        }
    }
}
