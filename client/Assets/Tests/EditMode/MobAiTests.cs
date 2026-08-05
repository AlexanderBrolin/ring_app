using NUnit.Framework;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class TargetingTests
    {
        [Test]
        public void StationaryTarget_AimsExactlyAtIt()
        {
            float2 dir = Targeting.AimWithLead(float2.zero, new float2(10f, 0f),
                float2.zero, 14f, 0.8f);
            Assert.AreEqual(1f, dir.x, 1e-4f);
            Assert.AreEqual(0f, dir.y, 1e-4f);
        }

        [Test]
        public void MovingTarget_LeadsAhead()
        {
            float2 dir = Targeting.AimWithLead(float2.zero, new float2(10f, 0f),
                new float2(0f, 5f), 14f, 1f);
            Assert.Greater(dir.y, 0.1f); // aims ahead along target's movement direction
        }

        [Test]
        public void TargetFasterThanProjectile_FallbackNoNaN()
        {
            float2 dir = Targeting.AimWithLead(float2.zero, new float2(10f, 0f),
                new float2(0f, 50f), 14f, 1f);
            Assert.IsTrue(math.all(math.isfinite(dir)));
            Assert.AreEqual(1f, math.length(dir), 1e-3f);
        }

        [Test]
        public void LineOfFire_BlockedByObstacle()
        {
            var arena = TestConfigs.DefaultArena(); // obstacle (10,4) r2.2
            Assert.IsFalse(Targeting.HasLineOfFire(new float2(10f, 0f),
                new float2(10f, 8f), 0.15f, arena));
            Assert.IsTrue(Targeting.HasLineOfFire(new float2(-20f, -20f),
                new float2(-25f, -20f), 0.15f, arena));
        }

        [Test]
        public void LineOfFire_BlockedByWall()
        {
            // Stage 2 Task 13 (spec §3.3): HasLineOfFire grows a wall loop
            // mirroring the existing obstacle loop above. Vertical wall
            // straddling the ray's crossing point on its flat side (not a cap).
            float2 wallA = new float2(5f, -5f);
            float2 wallB = new float2(5f, 5f);
            float halfW = 1f;
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { halfW },
            };
            Assert.IsFalse(Targeting.HasLineOfFire(new float2(0f, 0f), new float2(10f, 0f),
                0.15f, arena));
        }

        [Test]
        public void LineOfFire_ClearAlongWall()
        {
            // Same wall as LineOfFire_BlockedByWall, but the ray runs PARALLEL
            // to its flat side, offset well clear of halfW + padR (3m vs
            // 1.15m) — catches a "wall always blocks" mutant that
            // LineOfFire_BlockedByWall alone would let survive.
            float2 wallA = new float2(5f, -5f);
            float2 wallB = new float2(5f, 5f);
            float halfW = 1f;
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { halfW },
            };
            Assert.IsTrue(Targeting.HasLineOfFire(new float2(2f, -5f), new float2(2f, 5f),
                0.15f, arena));
        }

        [Test]
        public void LineOfFire_NegativePadClamped()
        {
            // Stage 2 Task 13 (context Р64): a target's own radius is passed
            // as a NEGATIVE padR by upcoming visibility callers (Task 19/21).
            // Geometry.SegmentCircle computes r = padR + cR and squares it, so
            // an unclamped padR deeper than -cR flips the sign and turns this
            // circle into a phantom of radius |r| = 0.25 (padR -0.45 + cR
            // 0.2) — big enough to swallow the 0.1m offset below and falsely
            // block the ray. Clamped to padR' = max(padR, -cR) = -0.2,
            // r' = 0: the circle degenerates to its own centre point, which
            // the ray — offset by 0.1, not colinear with it — genuinely misses.
            float2 circlePos = new float2(5f, 0.1f);
            float circleR = 0.2f;
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 1,
                ObstaclePos = new[] { circlePos },
                ObstacleRadius = new[] { circleR },
                WallCount = 0,
                WallA = System.Array.Empty<float2>(),
                WallB = System.Array.Empty<float2>(),
                WallHalfWidth = System.Array.Empty<float>(),
            };
            Assert.IsTrue(Targeting.HasLineOfFire(new float2(0f, 0f), new float2(10f, 0f),
                -0.45f, arena));
        }

        [Test]
        public void LineOfFire_NegativePadClamped_Wall()
        {
            // Coordinator addition to the plan's circle-only clamp test above:
            // without a SEPARATE clamp on the wall side, halfW would
            // "inflate" by |padR| exactly like an unclamped circle radius,
            // leaving half of Р64 uncovered. Same numbers as the circle case
            // (halfW 0.2, padR -0.45): unclamped total pad = halfW + padR =
            // -0.25 (phantom |r| = 0.25); clamped total pad = halfW +
            // max(padR, -halfW) = 0 (degenerate axis). The ray runs parallel
            // to the wall's axis, offset by 0.1 — inside the unclamped
            // phantom, clear of the clamped (degenerate) one.
            float2 wallA = new float2(0f, 0f);
            float2 wallB = new float2(0f, 10f);
            float halfW = 0.2f;
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { halfW },
            };
            Assert.IsTrue(Targeting.HasLineOfFire(new float2(0.1f, 3f), new float2(0.1f, 7f),
                -0.45f, arena));
        }

        [Test]
        public void LineOfFire_BlockedByWallCap()
        {
            // Coordinator addition: the ray crosses well below the wall's
            // flat-side span [0,10] on the y axis, so only the rounded end
            // cap at wallA can catch it — proves the caps participate in LoS
            // the same way the flat side does (a "flat-side-only" mutant
            // would miss this and read the wall as open there).
            float2 wallA = new float2(0f, 0f);
            float2 wallB = new float2(0f, 10f);
            float halfW = 1f;
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { halfW },
            };
            Assert.IsFalse(Targeting.HasLineOfFire(new float2(-3f, -0.9f), new float2(3f, -0.9f),
                0.15f, arena));
        }

        [Test]
        public void NearestAlivePlayer_ZeroAlive_ReturnsFalseAndMinusOne()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 2);
            w.KillPlayerNoDamage(0);
            w.KillPlayerNoDamage(1); // nobody alive now
            bool found = Targeting.NearestAlivePlayer(w, float2.zero, out int index);
            Assert.IsFalse(found);
            Assert.AreEqual(-1, index);
        }

        [Test]
        public void NearestAlivePlayer_EqualDistance_TieBreaksOnSmallerIndex()
        {
            // Fresh multiplayer world: every player spawns on the ring at the
            // SAME radius from the arena center
            // (MultiPlayerWorldTests.SoloSpawnsAtOrigin_MultiplayerSpawnsOnRing),
            // so querying from the center is an exact three-way tie — the
            // smaller index must win (spec Р85), not spawn/array order coincidence.
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            bool found = Targeting.NearestAlivePlayer(w, float2.zero, out int index);
            Assert.IsTrue(found);
            Assert.AreEqual(0, index);
        }
    }

    public class MobAiTests
    {
        static readonly SimInput Idle = default;

        [Test]
        public void Chaser_ClosesDistanceToPlayer()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.SpawnMobForTest(MobType.Chaser, new float2(15f, 0f));
            float d0 = 15f;
            for (int i = 0; i < 60; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(TestConfigs.Open().Arena);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), d0 - 3f);
        }

        [Test]
        public void Chaser_TelegraphThenStrike_DamagesPlayer()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(1.0f, 0f)); // already within AttackRange
            float hp0 = c.Hero.MaxHp;
            // wait for the strike with margin (FSM may spend a tick or two on Idle→Chase→Telegraph)
            for (int i = 0; i < 40 && w.Player.Hp >= hp0; i++) w.Tick(Idle);
            // exactly one strike: AttackCooldown 0.9s = 27 ticks — the second doesn't make it in time
            Assert.AreEqual(hp0 - c.Chaser.ContactDamage, w.Player.Hp, 1e-3f);
        }

        [Test]
        public void Chaser_TelegraphsAheadOfRunner_AndConnects()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            var run = new SimInput { MoveDir = new float2(1f, 0f) }; // player charges straight at the chaser
            float hp0 = c.Hero.MaxHp;

            bool sawTelegraph = false;
            float distAtEntry = 0f;
            MobAiState prevAi = MobAiState.Idle;
            for (int i = 0; i < 200 && w.Player.Hp >= hp0; i++)
            {
                w.Tick(run);
                MobAiState ai = w.Mobs[0].Ai;
                if (!sawTelegraph && ai == MobAiState.Telegraph && prevAi != MobAiState.Telegraph)
                {
                    sawTelegraph = true;
                    distAtEntry = math.distance(w.Mobs[0].Pos, w.Player.Pos);
                }
                prevAi = ai;
            }

            Assert.IsTrue(sawTelegraph, "chaser never entered Telegraph");
            // Predicted lead pulls the windup earlier than raw contact (A15/D9):
            // entry still fires while the runner is outside melee range.
            Assert.Greater(distAtEntry, c.Chaser.AttackRange);
            // ...and thanks to the strike's honest re-validation, the runner's own
            // continued closing still lands the hit — the early windup is not a
            // free miss.
            Assert.AreEqual(hp0 - c.Chaser.ContactDamage, w.Player.Hp, 1e-3f);
        }

        [Test]
        public void Chaser_Standing_FarPlayer_NoTelegraph()
        {
            var c = TestConfigs.Open();
            // QA15: keeps the chaser from physically closing the gap itself over the
            // 60-tick budget — isolates the entry check from turning into a tick-count race.
            c.Chaser.MaxSpeed = 0f;
            var w = new SimulationWorld(1, c);
            float dist = c.Chaser.AttackRange + c.Chaser.SwingLeadMaxMeters + 0.5f;
            w.SpawnMobForTest(MobType.Chaser, new float2(dist, 0f)); // player stands still at the origin
            for (int i = 0; i < 60; i++) w.Tick(Idle);
            Assert.AreNotEqual(MobAiState.Telegraph, w.Mobs[0].Ai);
        }

        [Test]
        public void Chaser_DashDoesNotBaitFromAfar()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            w.Tick(Idle); // mandatory Idle->Chase warm-up tick (no entry check happens on it)
            var p = w.Player;
            p.Vel = new float2(c.Hero.DashSpeed, 0f); // dash-speed burst toward the chaser
            w.SetPlayerForTest(p);
            w.Tick(Idle); // the tick that evaluates Telegraph entry against this burst velocity
            Assert.AreNotEqual(MobAiState.Telegraph, w.Mobs[0].Ai); // lead is clamped by Hero.MaxSpeed, not the dash speed
        }

        [Test]
        public void Chaser_LeadClampedByMaxMeters()
        {
            var c = TestConfigs.Open();
            c.Chaser.MaxSpeed = 0f; // isolate the cap check from the chaser's own approach
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(20f, 0f));
            var run = new SimInput { MoveDir = new float2(1f, 0f) };

            float distAtEntry = -1f;
            for (int i = 0; i < 200 && distAtEntry < 0f; i++)
            {
                w.Tick(run);
                if (w.Mobs[0].Ai == MobAiState.Telegraph)
                    distAtEntry = math.distance(w.Mobs[0].Pos, w.Player.Pos);
            }

            Assert.GreaterOrEqual(distAtEntry, 0f, "chaser never entered Telegraph");
            // One tick's worth of the runner's own closing speed as discretisation slack.
            float maxEntryDist = c.Chaser.AttackRange + c.Chaser.SwingLeadMaxMeters
                + c.Hero.MaxSpeed * SimulationWorld.TickDt;
            Assert.LessOrEqual(distAtEntry, maxEntryDist);
        }

        [Test]
        public void SwingLeadZero_EntryTickEqualsE1Rule()
        {
            var c = TestConfigs.Open();
            // Factor 0 -> PredictPos degenerates to the raw player position exactly
            // (offset = lead * (seconds * 0) = zero vector) — the pre-Task-13 (E1)
            // raw-distance rule as a special case, bit-exact.
            c.Chaser.SwingLeadFactor = 0f;
            const float spawnX = 8f;

            // Sim A: the tick the AI itself actually enters Telegraph.
            var wa = new SimulationWorld(1, c);
            wa.SpawnMobForTest(MobType.Chaser, new float2(spawnX, 0f));
            int entryTickAi = -1;
            for (int i = 1; i <= 200 && entryTickAi < 0; i++)
            {
                wa.Tick(Idle);
                if (wa.Mobs[0].Ai == MobAiState.Telegraph) entryTickAi = i;
            }

            // Sim B: identical setup/seed, independently tracking the first tick the
            // raw centre-to-centre distance crosses AttackRange — using exactly the
            // positions the entry check itself reads: the mob's position as it stood
            // BEFORE this tick's motion, against the player's position AFTER this
            // tick's movement (movement runs before the AI check inside Tick()).
            var wb = new SimulationWorld(1, c);
            wb.SpawnMobForTest(MobType.Chaser, new float2(spawnX, 0f));
            int entryTickRaw = -1;
            for (int i = 1; i <= 200 && entryTickRaw < 0; i++)
            {
                float2 mobPosBefore = wb.Mobs[0].Pos;
                wb.Tick(Idle);
                float2 playerPosAfter = wb.Player.Pos;
                if (math.distance(mobPosBefore, playerPosAfter) <= c.Chaser.AttackRange)
                    entryTickRaw = i;
            }

            Assert.Greater(entryTickAi, 0, "chaser never entered Telegraph");
            Assert.AreEqual(entryTickRaw, entryTickAi);
        }

        [Test]
        public void Chaser_BehindObstacle_SteersAroundNotStuck()
        {
            var c = TestConfigs.Open();
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(7f, 0f) };
            c.Arena.ObstacleRadius = new[] { 2f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(14f, 0f)); // player at (0,0) behind the obstacle
            for (int i = 0; i < 300; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), 3f); // reached it by going around
        }

        [Test]
        public void Gunner_KeepsPreferredRange_AndFiresOnlyWithLoS()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Gunner, new float2(20f, 0f));
            int fired = 0;
            for (int i = 0; i < 300; i++)
            {
                w.ClearEvents();
                w.Tick(Idle);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileFired) fired++;
            }
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            float dist = math.distance(snap.Mobs[0].Pos, w.Player.Pos);
            Assert.That(dist, Is.InRange(c.Gunner.PreferredRange - 2f, c.Gunner.PreferredRange + 2f));
            Assert.Greater(fired, 0);
        }

        [Test]
        public void Gunner_NoLoS_HoldsFire()
        {
            var c = TestConfigs.Open();
            c.Gunner.StrafeSpeed = 0f; // isolate the LoS gate: strafing would move it out of the shadow in ~60 ticks
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            c.Arena.ObstacleRadius = new[] { 3f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Gunner, new float2(9f, 0f)); // within range tolerance, but behind the wall
            int fired = 0;
            for (int i = 0; i < 120; i++)
            {
                w.ClearEvents();
                w.Tick(Idle);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileFired) fired++;
            }
            Assert.AreEqual(0, fired);
        }

        [Test]
        public void Gunner_LongApproach_FiresAtMostOnceOnFirstWindow()
        {
            // F-1 regression: MobAiSystem.UpdateGunner decrements FireCooldown every
            // tick with no floor clamp, unlike the player's WeaponSystem (WeaponSystem.
            // cs:29, `p.FireCooldown = math.max(0f, p.FireCooldown);` while not firing).
            // A gunner spending several seconds in Reposition (outside PreferredRange
            // +-RangeTolerance) racks up a negative "debt" on FireCooldown; the instant
            // it steps inside the tolerance band (LoS is unobstructed the whole way in
            // this open arena), the un-clamped debt lets it fire on several consecutive
            // ticks instead of the single "shoot immediately on acquiring the target"
            // shot the FSM intends.
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            // Far enough that closing into the tolerance band (PreferredRange 9 +-
            // RangeTolerance 1.5) takes several seconds of pure Reposition — long
            // enough for the un-clamped cooldown to rack up multiple FireIntervals
            // (1.6s) of "debt" before it ever gets a legal shot.
            w.SpawnMobForTest(MobType.Gunner, new float2(60f, 0f));

            int windowFired = 0;
            int windowTicksLeft = -1;
            for (int i = 0; i < 600 && windowTicksLeft != 0; i++)
            {
                w.ClearEvents();
                w.Tick(Idle);
                int firedThisTick = 0;
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileFired) firedThisTick++;

                if (windowTicksLeft < 0 && firedThisTick > 0)
                    windowTicksLeft = 5; // opens the 5-tick observation window on the first shot

                if (windowTicksLeft > 0)
                {
                    windowFired += firedThisTick;
                    windowTicksLeft--;
                }
            }

            Assert.Greater(windowFired, 0); // sanity: the gunner did eventually get a shot off
            Assert.LessOrEqual(windowFired, 1,
                "gunner volley-fired its FireCooldown backlog instead of exactly one shot on LoS acquisition (F-1)");
        }

        [Test]
        public void PlayerDead_MobsGoIdle()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            w.KillPlayerForTest();
            for (int i = 0; i < 30; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(MobAiState.Idle, snap.Mobs[0].Ai);
        }

        [Test]
        public void ZeroAlivePlayers_MobsGoIdle()
        {
            // Stage 2 Task 8: extends the existing solo PlayerDead_MobsGoIdle
            // coverage above to the genuinely multiplayer case — EVERY player
            // dead, not just the one solo player — proving MobAiSystem's Idle
            // branch now reads NearestAlivePlayer's "nobody alive" result
            // instead of the old solo-only w.Player.Alive.
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c, playerCount: 2);
            w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            w.KillPlayerNoDamage(0);
            w.KillPlayerNoDamage(1); // nobody alive now

            var inputs = new SimInput[2];
            for (int i = 0; i < 30; i++) w.TickAll(inputs);

            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(MobAiState.Idle, snap.Mobs[0].Ai);
        }

        [Test]
        public void Chaser_SwitchesTarget_WhenNearestPlayerDies()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c, playerCount: 2);
            // Player 0 sits close to the mob's spawn (east); player 1 sits far
            // away in the OPPOSITE direction (west) — closing in on one vs the
            // other is directionally distinguishable, not just "closer/farther
            // along the same line".
            var p0 = new PlayerState { Pos = new float2(5f, 0f), Hp = c.Hero.MaxHp, Alive = true };
            var p1 = new PlayerState { Pos = new float2(-20f, 0f), Hp = c.Hero.MaxHp, Alive = true };
            w.SetPlayerForTest(0, p0);
            w.SetPlayerForTest(1, p1);
            var mobStart = new float2(0f, 0f);
            w.SpawnMobForTest(MobType.Chaser, mobStart);

            var inputs = new SimInput[2];
            for (int i = 0; i < 10; i++) w.TickAll(inputs); // mob closes on the NEARER player (0)
            Assert.Less(math.distance(w.Mobs[0].Pos, p0.Pos), math.distance(mobStart, p0.Pos),
                "mob should have closed in on player 0 — the nearer alive target");

            w.KillPlayerNoDamage(0); // the nearer target leaves — player 1 is now the only alive one
            float distToP1AtSwitch = math.distance(w.Mobs[0].Pos, p1.Pos);

            for (int i = 0; i < 60; i++) w.TickAll(inputs);
            Assert.Less(math.distance(w.Mobs[0].Pos, p1.Pos), distToP1AtSwitch,
                "after the nearer player dies, the mob must retarget the remaining alive player");
        }

        [Test]
        public void Separation_PreventsStackingSymmetrically()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(11.9f, 10f));
            w.SpawnMobForTest(MobType.Chaser, new float2(12.1f, 10f));
            w.KillPlayerForTest(); // mobs go Idle — only separation acts
            for (int i = 0; i < 60; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            float dist = math.distance(snap.Mobs[0].Pos, snap.Mobs[1].Pos);
            Assert.Greater(dist, 1.0f); // pushed apart
            // symmetry: the pair's midpoint hasn't drifted
            float2 mid = (snap.Mobs[0].Pos + snap.Mobs[1].Pos) * 0.5f;
            Assert.AreEqual(12f, mid.x, 0.05f);
        }

        [Test]
        public void Chaser_NavigatesAroundWall()
        {
            // Mirrors Chaser_BehindObstacle_SteersAroundNotStuck above,
            // substituting a wall for the circular obstacle (Stage 2 Task 14,
            // spec §3.3): SteerAround must treat WallCount the same way it
            // already treats ObstacleCount.
            //
            // The wall STRADDLES the direct mob->player line, exactly like the
            // circular obstacle in the mirrored test — that is the case the
            // detour exists for, and the only one that can witness it. An
            // earlier revision of this fixture placed the wall entirely to one
            // side of that line, which made the test pass on pre-Task-14 code
            // (a straight run never touches such a wall) and therefore proved
            // nothing; it was moved back here together with the coordinator's
            // fix to the tangent-side choice. Before that fix this fixture
            // dead-stopped the chaser against the wall's flat face: the
            // short-way tangent to the end cap cuts through the wall's body,
            // the collide-and-slide cancels the resulting velocity, and the
            // next tick reproduces the same geometry. The mutation that
            // restores the old shorter-turn rule for walls reddens this test.
            var c = TestConfigs.Open();
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(7f, -3f) };
            c.Arena.WallB = new[] { new float2(7f, 3f) };
            c.Arena.WallHalfWidth = new[] { 1f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(14f, 0f)); // player at (0,0) behind the wall
            for (int i = 0; i < 300; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), 3f); // reached it by going around
        }

        [Test]
        public void Chaser_DoesNotRubAlongWall()
        {
            // Coordinator addition (task-14-context.md): the regression this
            // guards against is subtler than "does the chaser eventually get
            // around" (Chaser_NavigatesAroundWall above already covers that).
            // A steering fix that reduced the wall to an equivalent circle at
            // the NEAREST POINT ON ITS AXIS instead of the nearest END would
            // still eventually round the corner via the physical
            // collide-and-slide alone (MoveWithCollisions), just very
            // slowly — the steering keeps re-aiming almost straight back
            // into the wall face every tick instead of committing to a
            // detour. Over a fixed window that shows up as motion that is
            // mostly perpendicular churn against the wall with little
            // progress ALONG it; this test measures exactly that ratio,
            // spawning the chaser squarely on the wall's flat side, far from
            // either end.
            var c = TestConfigs.Open();
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(6f, -15f) };
            c.Arena.WallB = new[] { new float2(6f, 15f) };
            c.Arena.WallHalfWidth = new[] { 1.5f };

            var w = new SimulationWorld(1, c);
            var player = w.Player;
            player.Pos = new float2(-6f, -1f); // west of the wall, off-centre like the chaser below
            w.SetPlayerForTest(player);
            // East of the wall, at the same off-centre offset — squarely on
            // the flat side, far from either rounded end.
            w.SpawnMobForTest(MobType.Chaser, new float2(12f, -1f));

            float2 prevPos = w.Mobs[0].Pos;
            float startY = prevPos.y;
            float pathLength = 0f;
            const int ticks = 200;
            for (int i = 0; i < ticks; i++)
            {
                w.Tick(Idle);
                float2 pos = w.Mobs[0].Pos;
                pathLength += math.distance(pos, prevPos);
                prevPos = pos;
            }

            float axialProgress = math.abs(prevPos.y - startY);
            Assert.Greater(pathLength, 5f, "chaser barely moved at all over the window");
            Assert.GreaterOrEqual(axialProgress, 0.2f * pathLength,
                $"axial progress along the wall ({axialProgress:F2}) should track a " +
                $"meaningful fraction of the total distance travelled ({pathLength:F2}) — " +
                "a mob rubbing along the wall face instead of heading for its end would fail this");
        }

        [Test]
        public void SteerAround_PrefersNearestBlocker_AcrossKinds()
        {
            // Coordinator addition: circles and walls compete for the SAME
            // "nearest blocker" slot (task-14-context.md — same rule
            // SweepArena already uses for circle-then-wall order). The two
            // fixtures below place both an obstacle AND a wall in the mob's
            // lookahead at different distances from it — whichever is
            // nearer must determine the steer, regardless of kind. Checked
            // by the SIGN of the resulting steer's y-component: the circle
            // sits north of the direct line in both fixtures and the wall
            // spans south of it, so "the circle won" and "the wall won"
            // push the tangent to opposite sides of zero — a mutant that
            // always prefers one kind over the other flips the sign in
            // exactly one of the two fixtures below (both distances/signs
            // verified offline against the tangent formula before writing
            // this assertion).
            float2 mobStart = new float2(-10f, 0f);

            // Fixture A: the CIRCLE is nearer along the lookahead segment.
            {
                var c = TestConfigs.Open();
                c.Arena.ObstacleCount = 1;
                c.Arena.ObstaclePos = new[] { new float2(-8f, 0.8f) };
                c.Arena.ObstacleRadius = new[] { 0.5f };
                c.Arena.WallCount = 1;
                c.Arena.WallA = new[] { new float2(-7.2f, -0.3f) };
                c.Arena.WallB = new[] { new float2(-7.2f, 6f) };
                c.Arena.WallHalfWidth = new[] { 0.3f };
                var w = new SimulationWorld(1, c);
                w.SpawnMobForTest(MobType.Chaser, mobStart);
                w.Tick(Idle); // Idle->Chase warm-up, no steering yet
                w.Tick(Idle); // first tick SteerAround actually runs
                Assert.Less(w.Mobs[0].Vel.y, -0.3f,
                    "the nearer CIRCLE should have determined the steer, not the farther wall");
            }

            // Fixture B: same two shapes, swapped distances — the WALL is now nearer.
            {
                var c = TestConfigs.Open();
                c.Arena.ObstacleCount = 1;
                c.Arena.ObstaclePos = new[] { new float2(-7f, 1.4f) };
                c.Arena.ObstacleRadius = new[] { 0.3f };
                c.Arena.WallCount = 1;
                c.Arena.WallA = new[] { new float2(-7.6f, -0.3f) };
                c.Arena.WallB = new[] { new float2(-7.6f, 6f) };
                c.Arena.WallHalfWidth = new[] { 0.7f };
                var w = new SimulationWorld(1, c);
                w.SpawnMobForTest(MobType.Chaser, mobStart);
                w.Tick(Idle);
                w.Tick(Idle);
                float2 steerWithWall = w.Mobs[0].Vel;

                // Same world with the wall removed: whatever the circle alone
                // would have produced. "The nearer wall determined the steer"
                // means the two differ materially — asserting a fixed sign
                // instead is what the first revision of this test did, and the
                // sign it demanded (upwards) was the pre-fix defect: this wall
                // runs from y = -0.3 UP to y = 6, so upwards is straight through
                // its body and the only way past it is below its lower end.
                var noWall = TestConfigs.Open();
                noWall.Arena.ObstacleCount = 1;
                noWall.Arena.ObstaclePos = new[] { new float2(-7f, 1.4f) };
                noWall.Arena.ObstacleRadius = new[] { 0.3f };
                var w2 = new SimulationWorld(1, noWall);
                w2.SpawnMobForTest(MobType.Chaser, mobStart);
                w2.Tick(Idle);
                w2.Tick(Idle);
                float2 steerWithoutWall = w2.Mobs[0].Vel;

                Assert.Greater(math.distance(steerWithWall, steerWithoutWall), 0.3f,
                    "the nearer WALL should have determined the steer, not the farther circle");
                Assert.Less(steerWithWall.y, 0f,
                    "the only way past this wall is below its lower end");
            }
        }
    }
}
