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
            // r' = 0: the circle degenerates to its own center point, which
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
        public void LineOfFire_NegativePadClamped_PerObstacle_NotHoisted()
        {
            // Fix-round T13 tail (coordinator review): every existing negative-
            // padR fixture above has exactly ONE obstacle, so a mutant that
            // hoists the clamp out of the loop and computes it once (from
            // either the first or the largest/smallest obstacle's own radius)
            // is numerically indistinguishable from the correct per-obstacle
            // clamp on any of them — the per-obstacle clamp is unwitnessed.
            // TWO obstacles with DIFFERENT radii on the same ray kills every
            // single-clamp variant: hoisting by the larger radius (0.5) still
            // clamps to -0.45 (since -0.45 > -0.5), but applying THAT shared
            // clamp to the smaller circle (R 0.2) gives r = -0.45 + 0.2 =
            // -0.25, a phantom of radius 0.25 that swallows its 0.1 m offset
            // from the ray and falsely blocks it. Hoisting by the smaller
            // radius (0.2) instead clamps to -0.2, which applied to the
            // LARGER circle (R 0.5) gives r = -0.2 + 0.5 = 0.3, again bigger
            // than its own 0.2 m offset — also a false block. The correct
            // per-obstacle clamp resolves both circles to r <= 0 (no phantom)
            // and the ray is genuinely clear.
            float2 circle0Pos = new float2(5f, 0.2f);
            float circle0R = 0.5f;
            float2 circle1Pos = new float2(7f, 0.1f);
            float circle1R = 0.2f;
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 2,
                ObstaclePos = new[] { circle0Pos, circle1Pos },
                ObstacleRadius = new[] { circle0R, circle1R },
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
        public void LineOfFire_NegativePadClamped_PerWall_NotHoisted()
        {
            // Fixwave Ф3 item 1: mirrors LineOfFire_NegativePadClamped_
            // PerObstacle_NotHoisted above (the same discipline that test
            // pins for CIRCLES) but for the wall loop's own
            // `wallPad = max(padR, -arena.WallHalfWidth[i])` clamp — every
            // existing wall fixture in this file has exactly ONE wall, so a
            // mutant that hoists the clamp out of the loop (computed once
            // from either wall's own half-width) is numerically
            // indistinguishable from the correct per-wall clamp on any of
            // them.
            //
            // Two SHORT walls, each just a rounded end cap facing the ray —
            // wall0's near end sits at (5, 0.2) with HalfWidth 0.5, wall1's at
            // (7, 0.1) with HalfWidth 0.2 (the far end of each is placed well
            // off to the side so only the near cap is in play). Since a
            // wall's rounded end is resolved through the exact same
            // Geometry.SegmentCircle call a circle obstacle uses, this
            // reproduces LineOfFire_NegativePadClamped_PerObstacle_
            // NotHoisted's own numbers (circle0Pos/circle0R, circle1Pos/
            // circle1R) verbatim, just wrapped as walls: hoisting by the
            // larger half-width (0.5) still clamps to -0.45 (since
            // -0.45 > -0.5) but applied to wall1's cap (R 0.2) gives
            // r = -0.45 + 0.2 = -0.25, a phantom of radius 0.25 that
            // swallows wall1's 0.1 m offset from the ray and falsely blocks
            // it; hoisting by the smaller half-width (0.2) clamps to -0.2,
            // which applied to wall0's cap (R 0.5) gives r = -0.2 + 0.5 =
            // 0.3, again bigger than its own 0.2 m offset — also a false
            // block. The correct per-wall clamp resolves both caps to r <= 0
            // (no phantom) and the ray is genuinely clear.
            float2 wall0A = new float2(5f, 0.2f);
            float2 wall0B = new float2(5f, 3.2f); // far end, well clear of the ray
            float wall0HalfWidth = 0.5f;
            float2 wall1A = new float2(7f, 0.1f);
            float2 wall1B = new float2(7f, 3.1f); // far end, well clear of the ray
            float wall1HalfWidth = 0.2f;
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 2,
                WallA = new[] { wall0A, wall1A },
                WallB = new[] { wall0B, wall1B },
                WallHalfWidth = new[] { wall0HalfWidth, wall1HalfWidth },
            };
            Assert.IsTrue(Targeting.HasLineOfFire(new float2(0f, 0f), new float2(10f, 0f),
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

        // --- Stage 3 Task 9 (bd app-35g, spec Р64): HasLineOfFire grows an
        // arc loop mirroring the existing obstacle/wall loops above. ---

        [Test]
        public void LineOfFire_BlockedByArcBody()
        {
            float ringR = 10f, halfW = 1f;
            float[] doorCenter = { math.PI / 2f, 0f }; // index 1 = the door under test
            float[] doorFreeWidth = { 4f, 4f };
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 0,
                WallA = System.Array.Empty<float2>(),
                WallB = System.Array.Empty<float2>(),
                WallHalfWidth = System.Array.Empty<float>(),
                ZoneWallCount = 1,
                ZoneWallRadius = new[] { ringR },
                ZoneWallHalfWidth = new[] { halfW },
                ZoneWallDoorStart = new[] { 0 },
                ZoneWallDoorCount = new[] { 2 },
                DoorCenterRad = doorCenter,
                DoorFreeWidth = doorFreeWidth,
            };
            // Straight through the solid wall at angle pi — clear of both doors.
            Assert.IsFalse(Targeting.HasLineOfFire(new float2(-2f, 0f), new float2(-14f, 0f),
                0.15f, arena));
        }

        [Test]
        public void LineOfFire_PassesThroughDoor()
        {
            // Control for LineOfFire_BlockedByArcBody above (the
            // ThroughDoor_NoContact idiom, ZoneGeometryTests.cs): without it,
            // a mutant that always blocks (InDoorCutout disabled) or that
            // simply forgets the door slicing would still pass the body test
            // alone.
            float ringR = 10f, halfW = 1f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 0,
                WallA = System.Array.Empty<float2>(),
                WallB = System.Array.Empty<float2>(),
                WallHalfWidth = System.Array.Empty<float>(),
                ZoneWallCount = 1,
                ZoneWallRadius = new[] { ringR },
                ZoneWallHalfWidth = new[] { halfW },
                ZoneWallDoorStart = new[] { 0 },
                ZoneWallDoorCount = new[] { 2 },
                DoorCenterRad = doorCenter,
                DoorFreeWidth = doorFreeWidth,
            };
            // Dead down the middle of door 1 (angle 0) — clears the wall untouched.
            Assert.IsTrue(Targeting.HasLineOfFire(new float2(2f, 0f), new float2(14f, 0f),
                0.15f, arena));
        }

        [Test]
        public void LineOfFire_NegativePadClamped_ArcBody()
        {
            // Р64: same clamp discipline as LineOfFire_NegativePadClamped /
            // _Wall above — a target's own radius passed as a negative padR
            // must not phantom-inflate a zone wall's half-width past its own
            // negation. Unclamped, halfW+padR = 0.2-0.45 = -0.25 (negative,
            // undefined per PushOutOfArc/SegmentArc's own doc): the outer and
            // inner effective radii INVERT (effective outer 9.75 < effective
            // core 10.25, since SegmentCircleInterval folds padR into each
            // one's own r), and neither of SegmentArc's two band candidates
            // fires — hand-verified: the ray reads as clear when it must be
            // blocked. Clamped, halfW+max(padR,-halfW) = 0 exactly:
            // degenerate but well-ordered, and the ray is genuinely stopped.
            float ringR = 10f, halfW = 0.2f;
            float[] doorCenter = System.Array.Empty<float>();
            float[] doorFreeWidth = System.Array.Empty<float>();
            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 0,
                WallA = System.Array.Empty<float2>(),
                WallB = System.Array.Empty<float2>(),
                WallHalfWidth = System.Array.Empty<float>(),
                ZoneWallCount = 1,
                ZoneWallRadius = new[] { ringR },
                ZoneWallHalfWidth = new[] { halfW },
                ZoneWallDoorStart = new[] { 0 },
                ZoneWallDoorCount = new[] { 0 },
                DoorCenterRad = doorCenter,
                DoorFreeWidth = doorFreeWidth,
            };
            Assert.IsFalse(Targeting.HasLineOfFire(new float2(2f, 0f), new float2(14f, 0f),
                -0.45f, arena));
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
            // (MultiPlayerWorldTests.SoloTakesTheOnePlayerRingPoint_MultiplayerSpreadsAroundIt),
            // so querying from the center is an exact three-way tie — the
            // smaller index must win (spec Р85), not spawn/array order coincidence.
            // bd app-3cph: the three are PLACED at an exact tie rather than
            // trusted to spawn at one. The ring point is
            // `cos/sin(k * 2pi/3) * Radius * SpawnRingFrac`, and whether those
            // three products come out bit-identical is an accident of the
            // radius: at 103.96 they did, at 159.16 they no longer do, and the
            // test began measuring float noise instead of the tie-break rule
            // it is named for. Three explicit points at the same distance from
            // the query make the premise true by construction, at any arena
            // size the owner ever tunes to.
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            const float Tie = 12f;
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(Tie, 0f));
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, Tie));
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(-Tie, 0f));
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
            var w = new SimulationWorld(1, TestConfigs.OpenField());
            w.SpawnMobForTest(MobType.Chaser, new float2(15f, 0f));
            float d0 = 15f;
            for (int i = 0; i < 60; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(TestConfigs.OpenField());
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), d0 - 3f);
        }

        [Test]
        public void Chaser_TelegraphThenStrike_DamagesPlayer()
        {
            var c = TestConfigs.OpenField();
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
            var c = TestConfigs.OpenField();
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
            var c = TestConfigs.OpenField();
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
            var c = TestConfigs.OpenField();
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
            // raw center-to-center distance crosses AttackRange — using exactly the
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
            var c = TestConfigs.OpenField();
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(7f, 0f) };
            c.Arena.ObstacleRadius = new[] { 2f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(14f, 0f)); // player at (0,0) behind the obstacle
            for (int i = 0; i < 300; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(c);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), 3f); // reached it by going around
        }

        [Test]
        public void Gunner_KeepsPreferredRange_AndFiresOnlyWithLoS()
        {
            var c = TestConfigs.OpenField();
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
            var snap = new RenderSnapshot(c);
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
            // tick with no floor clamp, unlike the player's WeaponSystem.Update
            // (`p.FireCooldown = math.max(0f, p.FireCooldown);` while not firing).
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
            //
            // bd app-3cph: the gunner is placed a fixed distance FROM THE
            // PLAYER, radially inward, instead of at a literal point. The solo
            // collector spawns on the one-player ring (Geometry.SpawnPosFor),
            // so `(60, 0)` was ~44 m from its target while the ring sat at
            // 103.96 — and ~99 m from it once the В1 playtest moved the ring to
            // 159.16, which MaxSpeed 4 cannot close inside this test's 600
            // ticks: the gunner never got its first shot and the sanity
            // assertion below, not the F-1 one, is what failed. 44 m restores
            // the approach this test was written around, at any rim.
            // Inward, so the spawn point stays inside the arena by
            // construction.
            float2 target = w.Player.Pos;
            w.SpawnMobForTest(MobType.Gunner, target - math.normalize(target) * 44f);

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
            var snap = new RenderSnapshot(c);
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

            var snap = new RenderSnapshot(c);
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
            var snap = new RenderSnapshot(c);
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
            // waypoint fix. Fix-round T14 (M-2): this comment used to describe
            // that fix in "tangent-side choice"/"shorter-turn rule" terms —
            // language from the tangent-to-end-cap algorithm the waypoint
            // approach replaced, not what SteerAround actually does now (see
            // its XML doc for the current rule). Before that fix this fixture
            // dead-stopped the chaser against the wall's flat face: a tangent
            // to the nearer end cap cuts through the wall's body, the
            // collide-and-slide cancels the resulting velocity, and the next
            // tick reproduces the same geometry. A mutation that reintroduces
            // tangent-to-end-cap steering for walls reddens this test.
            var c = TestConfigs.OpenField();
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(7f, -3f) };
            c.Arena.WallB = new[] { new float2(7f, 3f) };
            c.Arena.WallHalfWidth = new[] { 1f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(14f, 0f)); // player at (0,0) behind the wall
            for (int i = 0; i < 300; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(c);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), 3f); // reached it by going around
        }

        [Test]
        public void Chaser_DoesNotRubAlongWall()
        {
            // Coordinator addition (task-14-context.md): the regression this
            // guards against is subtler than "does the chaser eventually get
            // around" (Chaser_NavigatesAroundWall above already covers that).
            // Reviewed in fix-round T14 (M-1): this fixture does NOT redden
            // from reducing the wall to an equivalent circle at the nearest
            // point on its axis, as an earlier revision of this comment
            // claimed — that reduction clamps `ratio` to 1 (the mob starts
            // inside the padded circle), `theta` becomes 90 degrees, and the
            // resulting tangent gives the mob its MAXIMUM possible axial
            // component, i.e. exactly the "commit to a detour along the
            // wall" behavior this test wants, not the churn it's meant to
            // catch. What DOES redden it: dropping the wall loop entirely
            // (SteerAround never even sees the wall, so the mob walks
            // straight into its flat side and the physical collide-and-slide
            // alone has to grind it around, very slowly) and a broken `face`
            // normal (aimed into the wall instead of away from it, so the
            // waypoint sits on the wrong side and steering re-aims almost
            // straight back at the face every tick). Over a fixed window
            // that shows up as motion that is mostly perpendicular churn
            // against the wall with little progress ALONG it; this test
            // measures exactly that ratio, spawning the chaser squarely on
            // the wall's flat side, far from either end.
            var c = TestConfigs.Open();
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(6f, -15f) };
            c.Arena.WallB = new[] { new float2(6f, 15f) };
            c.Arena.WallHalfWidth = new[] { 1.5f };

            var w = new SimulationWorld(1, c);
            var player = w.Player;
            player.Pos = new float2(-6f, -1f); // west of the wall, off-center like the chaser below
            w.SetPlayerForTest(player);
            // East of the wall, at the same off-center offset — squarely on
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
        public void Chaser_FindsDoor_InsteadOfPressingIntoArc()
        {
            // Stage 3 Task 9 (spec Р118): SteerAround grows an arc branch —
            // when the direct line to the target crosses a zone wall, the mob
            // heads for a waypoint at the nearest DOOR (not the wall's own
            // tangent — a tangent to a full-circle barrier never converges,
            // the mob would skate the ring forever without finding the
            // opening).
            //
            // RED-discipline note: physical collision (MoveWithCollisions ->
            // SweepArena) is EQUALLY unaware of ZoneWallCount before this
            // task's Step 3 lands, so a plain "did it eventually reach the
            // player" assertion would pass today too — the mob would simply
            // walk straight through the wall, uncollided, and get there fast.
            // The `everEmbeddedInSolidWall` guard below is what actually
            // reddens: it fails the instant the mob's own center is found
            // inside the wall's solid body (OverlapsArc true) at any tick,
            // which the current straight-line walk-through triggers almost
            // immediately (spawn angle 135 degrees is well outside the door's
            // +-17 degree cutout).
            var c = TestConfigs.OpenField();
            float ringR = 10f, halfW = 1f;
            c.Arena.ZoneWallCount = 1;
            c.Arena.ZoneWallRadius = new[] { ringR };
            c.Arena.ZoneWallHalfWidth = new[] { halfW };
            c.Arena.ZoneWallDoorStart = new[] { 0 };
            c.Arena.ZoneWallDoorCount = new[] { 1 };
            c.Arena.DoorCenterRad = new[] { 0f }; // door on the +x side
            c.Arena.DoorFreeWidth = new[] { 4f };

            var w = new SimulationWorld(1, c);
            // Player at the arena center, inside the wall's hole. Chaser
            // spawns outside, 135 degrees from the door, so one direction
            // around is unambiguously shorter — the direct line to the player
            // crosses the wall's SOLID body, so a "press into the arc" mob
            // would either dead-stop (if collision respected it) or, today,
            // walk straight through it (collision does not yet).
            float dist = ringR + halfW + 3f;
            float angle = 3f * math.PI / 4f;
            float2 spawnPos = dist * new float2(math.cos(angle), math.sin(angle));
            w.SpawnMobForTest(MobType.Chaser, spawnPos);

            bool everEmbeddedInSolidWall = false;
            for (int i = 0; i < 600; i++)
            {
                w.Tick(Idle);
                float2 pos = w.Mobs[0].Pos;
                if (Geometry.OverlapsArc(pos, c.Chaser.Radius, ringR, halfW,
                        c.Arena.DoorCenterRad, c.Arena.DoorFreeWidth))
                    everEmbeddedInSolidWall = true;
            }

            var snap = new RenderSnapshot(c);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), 3f); // reached it through the door
            Assert.IsFalse(everEmbeddedInSolidWall,
                "the chaser must never be caught embedded in the wall's solid body — " +
                "collision and steering must both respect it, not just eventually arrive");
        }

        [Test]
        public void SteerAround_WallEndNearTie_BreaksOnIdParity_NotRawComparison()
        {
            // Fixwave Ф3 item 3(a): SteerAround's wall-end tie-break
            // (fix-round T14, I-5) widens an EXACT `costA == costB`
            // comparison to a Geometry.Skin-wide band, specifically so one
            // ULP of rounding noise between costA/costB's two independent
            // sqrt chains can't flip which end of the wall a mob commits to
            // — and falls back to Id parity, not raw magnitude, inside that
            // band. No existing fixture pins the band itself:
            // Chaser_NavigatesAroundWall above is EXACTLY symmetric
            // (costA == costB to the bit), so a mutant that disables the
            // near-tie check (`math.abs(costA - costB) < Geometry.Skin` ->
            // `< 0f`, i.e. never true, falling through to the raw
            // `costA < costB`) still steers the same way there and the test
            // stays green — the raw comparison is bit-identical to the
            // parity answer when costA == costB exactly, so it can't tell
            // the two rules apart. It also can't tell apart a mutated parity
            // check (`(id & 1) == 0` -> `true`/`false`/`== 1`): a symmetric
            // fixture mirrors regardless of which side either mutant picks.
            //
            // Fixture: a symmetric wall (WallA/WallB equidistant from the
            // ray pos->target on the x axis) with the mob's own position
            // nudged 1e-4 off that axis of symmetry. That nudge makes costA
            // and costB differ by ~1.4e-4 — comfortably inside
            // Geometry.Skin's 1e-3 band (the near-tie rule fires) and
            // comfortably above float noise (a raw `costA < costB` gives a
            // STABLE, non-flaky verdict — the same one for every mob at this
            // position, regardless of Id). Two Chasers at the identical
            // position/target, one even Id and one odd, are spawned close
            // enough to the wall that its avoidance padding already overlaps
            // them at spawn (Geometry.SegmentStadium's "already inside at
            // the start" candidate), so the wall branch fires on the very
            // first Chase-state tick, with no drift from ordinary pursuit
            // motion to account for. Under the real near-tie rule the two
            // mobs commit to OPPOSITE ends of the wall (evidenced by
            // opposite-signed Vel.y after that tick); under the disabled-
            // near-tie mutant, Id is never consulted and both commit to the
            // SAME end (raw costA < costB, identical for both mobs) — so at
            // least one of the two sign assertions below must fail.
            //
            // Both mobs share the exact same spawn point, so
            // SeparationSystem (which runs every tick, right after
            // MobAiSystem — spec Task 20) would otherwise push them apart on
            // the very first tick along an ARBITRARY fallback direction
            // (Geometry.normalizesafe's (1,0) default for a zero delta),
            // biased by spawn ORDER rather than Id PARITY — a confound
            // unrelated to the branch under test. Zeroing SeparationRadius
            // switches that system off entirely (its own `threshold <= 0f`
            // early-out) so the only thing that can move the two mobs apart
            // is the wall-end tie-break this test targets.
            var c = TestConfigs.Open();
            c.Chaser.SeparationRadius = 0f;
            // app-88jb Т22: the HARD separation has to go off for the same
            // reason and by the same right — it is a second thing that moves
            // two mobs apart, and this test's subject is the wall-end tie-break
            // alone. Zero iterations is the hard pass's own "off", exactly as
            // a zero SeparationRadius is the soft pass's.
            c.Arena.RelaxIterations = 0;
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(2f, 2f) };
            c.Arena.WallB = new[] { new float2(2f, -2f) };
            c.Arena.WallHalfWidth = new[] { 1f };

            var w = new SimulationWorld(1, c);
            var player = w.Player;
            player.Pos = new float2(20f, 0f);
            w.SetPlayerForTest(player);

            const float eps = 1e-4f;
            int firstId = w.SpawnMobForTest(MobType.Chaser, new float2(0f, eps));
            int secondId = w.SpawnMobForTest(MobType.Chaser, new float2(0f, eps));
            Assert.AreNotEqual(firstId % 2, secondId % 2,
                "test setup: two consecutively-spawned mobs must have opposite Id parity");
            int evenSlot = (firstId & 1) == 0 ? 0 : 1;
            int oddSlot = 1 - evenSlot;

            w.Tick(Idle); // Idle -> Chase (settles in, no steering yet)
            w.Tick(Idle); // Chase: the wall already overlaps at spawn -> SteerAround fires

            float velYEven = w.Mobs[evenSlot].Vel.y;
            float velYOdd = w.Mobs[oddSlot].Vel.y;
            Assert.Greater(velYEven, 0f, "even-Id mob must round the TOP end (WallA, y=2)");
            Assert.Less(velYOdd, 0f, "odd-Id mob must round the BOTTOM end (WallB, y=-2)");
        }

        [Test]
        public void SteerAround_WallFaceNearTie_BreaksOnIdParity_NotRawSign()
        {
            // Fixwave Ф3 item 3(b): SteerAround's SEPARATE near-tie band —
            // which side of the wall's face to offset the waypoint on
            // (`keepFace`, fix-round T14, I-2) — is a different branch from
            // 3(a)'s wall-END tie-break above, and no existing fixture
            // witnesses it either: every fixture in this file keeps the mob
            // well off the wall's own axis line, where `|faceDot|` sits
            // around unity or more (nowhere near Geometry.Skin), so the near-
            // tie rule never fires and a mutant disabling it (`< Geometry.Skin`
            // -> `< 0f`) or corrupting the parity check is unwitnessed.
            //
            // Fixture: an ASYMMETRIC wall (WallB nearer both the mob and the
            // target than WallA, so costA/costB differ by ~0.8 — nowhere
            // near Geometry.Skin, keeping THIS fixture's wall-end choice
            // (roundA -> WallB, deterministic) uncoupled from 3(a)'s
            // near-tie concern) with the mob positioned almost exactly ON
            // the wall's own axis line extended past WallB (a 1e-4 nudge off
            // it), which puts `faceDot` at that same ~1e-4 — inside
            // Geometry.Skin, outside float noise. Under the real near-tie
            // rule the even/odd mobs land on OPPOSITE sides of the wall's
            // face (opposite-signed Vel.x after the steering tick); under a
            // disabled-near-tie mutant, both resolve `keepFace` off the same
            // raw (and here, tiny/borderline) sign, landing on the SAME
            // side.
            //
            // Both mobs share the exact same spawn point, so SeparationSystem
            // (spec Task 20, runs every tick right after MobAiSystem) would
            // otherwise push them apart on the first tick along an ARBITRARY
            // fallback direction biased by spawn ORDER, not Id PARITY — see
            // SteerAround_WallEndNearTie_BreaksOnIdParity_NotRawComparison's
            // own note above for the full reasoning. Zeroing SeparationRadius
            // switches that confound off entirely.
            var c = TestConfigs.Open();
            c.Chaser.SeparationRadius = 0f;
            // app-88jb Т22: the HARD separation has to go off for the same
            // reason and by the same right — it is a second thing that moves
            // two mobs apart, and this test's subject is the wall-end tie-break
            // alone. Zero iterations is the hard pass's own "off", exactly as
            // a zero SeparationRadius is the soft pass's.
            c.Arena.RelaxIterations = 0;
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(2f, 7f) };
            c.Arena.WallB = new[] { new float2(2f, 2f) };
            c.Arena.WallHalfWidth = new[] { 2f };

            var w = new SimulationWorld(1, c);
            var player = w.Player;
            player.Pos = new float2(20f, 20f);
            w.SetPlayerForTest(player);

            const float eps = 1e-4f;
            int firstId = w.SpawnMobForTest(MobType.Chaser, new float2(2f + eps, 1f));
            int secondId = w.SpawnMobForTest(MobType.Chaser, new float2(2f + eps, 1f));
            Assert.AreNotEqual(firstId % 2, secondId % 2,
                "test setup: two consecutively-spawned mobs must have opposite Id parity");
            int evenSlot = (firstId & 1) == 0 ? 0 : 1;
            int oddSlot = 1 - evenSlot;

            w.Tick(Idle); // Idle -> Chase (settles in, no steering yet)
            w.Tick(Idle); // Chase: the wall already overlaps at spawn -> SteerAround fires

            float velXEven = w.Mobs[evenSlot].Vel.x;
            float velXOdd = w.Mobs[oddSlot].Vel.x;
            // Absolute signs, not just "opposite" (fixture geometry: end =
            // WallB, axis points from WallB towards -y, so face starts as
            // +x) — a merely-relative check would miss a mutant that swaps
            // which parity keeps vs. flips the face (`(id & 1) == 0` ->
            // `== 1`), since that still lands the two mobs on opposite
            // sides, just the wrong ones.
            Assert.Greater(velXEven, 0f, "even-Id mob must KEEP the face (not flip it)");
            Assert.Less(velXOdd, 0f, "odd-Id mob must FLIP the face");
        }

        [Test]
        public void SteerAround_PrefersNearestBlocker_AcrossKinds()
        {
            // Coordinator addition, revised in fix-round T14 (I-1): circles
            // and walls compete for the SAME "nearest blocker" slot
            // (task-14-context.md — same rule SweepArena already uses for
            // circle-then-wall order). The two fixtures below place both an
            // obstacle AND a wall in the mob's lookahead at different
            // distances from it — whichever is nearer must determine the
            // steer, regardless of kind. A bare same-sign check stopped
            // being discriminating once the wall branch switched from a
            // tangent to a waypoint detour (the coordinator's Т14 fix):
            // fixture A's geometry now steers the mob south whichever kind
            // wins, so a mutant that always prefers the wall over the
            // circle would still pass a lone "Vel.y &lt; -0.3" assertion here
            // — it did, undetected, until this revision. Each fixture below
            // therefore compares against the SAME world with the
            // non-winning kind removed — a mutant that swaps which kind
            // wins collapses that difference to near zero — plus a sign
            // check as a sanity read on which side the mob actually went.
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
                float2 steerWithCircle = w.Mobs[0].Vel;

                // Same world with the circle removed: whatever the wall
                // alone would have produced.
                var noCircle = TestConfigs.Open();
                noCircle.Arena.WallCount = 1;
                noCircle.Arena.WallA = new[] { new float2(-7.2f, -0.3f) };
                noCircle.Arena.WallB = new[] { new float2(-7.2f, 6f) };
                noCircle.Arena.WallHalfWidth = new[] { 0.3f };
                var w2 = new SimulationWorld(1, noCircle);
                w2.SpawnMobForTest(MobType.Chaser, mobStart);
                w2.Tick(Idle);
                w2.Tick(Idle);
                float2 steerWithoutCircle = w2.Mobs[0].Vel;

                Assert.Greater(math.distance(steerWithCircle, steerWithoutCircle), 0.3f,
                    "the nearer CIRCLE should have determined the steer, not the farther wall");
                Assert.Less(steerWithCircle.y, -0.3f,
                    "the circle sits north of the direct line — the tangent past it steers south");
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

        [Test]
        public void SteerAround_Waypoint_PinsBothFaceAndAxisOffsets()
        {
            // I-7 (fix-round T14): `waypoint = end + axis * clearance +
            // face * clearance` was previously only pinned as a WHOLE — a
            // mutation dropping either addend on its own still greened every
            // existing test (`waypoint = end` is the only combination those
            // caught). This fixture isolates each addend's own sign/
            // dominance in the returned direction.
            //
            // Geometry: a vertical wall from (7,-3) to (7,3), halfWidth 1.
            // The mob sits due WEST of end A=(7,-3), at exactly the
            // physical-contact x-distance from the wall's flat face
            // (wallHalfWidth + Chaser.Radius = 1.5m: pos.x = 5.5) and at the
            // SAME y as A — the direct-line target (9,-3) sits due EAST,
            // clearly closer to A than to B, so A wins with no near-tie
            // involved. With both offsets applied, `end - pos = (1.5, 0)`
            // and the returned direction points WEST and SOUTH:
            // west because `face * clearance` (2.5m off the face, more than
            // the 1.5m the mob already stands off it) overshoots the mob's
            // own position outward past the face; south because
            // `axis * clearance` pushes 2.5m past A along the wall's axis.
            // Dropping `face * clearance` removes the only westward pull,
            // flipping Vel.x positive (the waypoint would sit EAST of the
            // mob, back toward the wall). Dropping `axis * clearance`
            // removes the only southward pull, collapsing Vel.y to ~0 (the
            // waypoint would sit level with the mob, offering no route past
            // the end).
            var c = TestConfigs.Open();
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(7f, -3f) };
            c.Arena.WallB = new[] { new float2(7f, 3f) };
            c.Arena.WallHalfWidth = new[] { 1f };
            var w = new SimulationWorld(1, c);
            var player = w.Player;
            player.Pos = new float2(9f, -3f);
            w.SetPlayerForTest(player);
            float contactX = 7f - (c.Arena.WallHalfWidth[0] + c.Chaser.Radius);
            w.SpawnMobForTest(MobType.Chaser, new float2(contactX, -3f));
            w.Tick(Idle); // Idle->Chase warm-up, no steering yet
            w.Tick(Idle); // first tick SteerAround actually runs
            float2 vel = w.Mobs[0].Vel;

            Assert.Less(vel.x, 0f,
                "face*clearance pin: without it the waypoint sits on the WRONG side of the mob (east, toward the wall)");
            Assert.Less(vel.y, -0.5f,
                "axis*clearance pin: without it the waypoint sits level with the mob (no route past the end)");
        }
    }
}
