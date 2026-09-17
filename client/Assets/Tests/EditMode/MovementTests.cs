using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class MovementTests
    {
        static SimulationWorld World() => new SimulationWorld(1, TestConfigs.OpenField());

        static SimInput Move(float x, float y)
            => new SimInput { MoveDir = new float2(x, y) };

        static SimInput MoveAim(float x, float y)
            => new SimInput { MoveDir = new float2(x, y), AimHeld = true };

        /// Stage 3 Task 20 (coordinator D-7): the loot-window counterpart of
        /// MoveAim above.
        static SimInput MoveWindow(float x, float y)
            => new SimInput { MoveDir = new float2(x, y), InventoryOpen = true };

        [Test]
        public void AimHeld_CapsRunSpeed()
        {
            var cfg = TestConfigs.OpenField();
            var w = World();
            for (int i = 0; i < 60; i++) w.Tick(MoveAim(1f, 0f)); // 2 s — enough to reach the capped speed
            float expected = cfg.Hero.MaxSpeed * cfg.Hero.AimMoveSpeedFrac; // fixture expr, PD5
            Assert.AreEqual(expected, w.Player.Vel.x, 0.05f);
        }

        [Test]
        public void InventoryOpen_CapsRunSpeed()
        {
            // Stage 3 Task 20 (spec §3.8/§3.11, coordinator D-1/D-7): the loot
            // window pays the SAME price as AimHeld (Hero.AimMoveSpeedFrac, no
            // second number, Р239) — mirrors AimHeld_CapsRunSpeed exactly, one
            // flag swapped for the other, to prove the shared SlowsMovement
            // predicate really reads InventoryOpen and not just AimHeld.
            var cfg = TestConfigs.OpenField();
            var w = World();
            for (int i = 0; i < 60; i++) w.Tick(MoveWindow(1f, 0f)); // 2 s
            float expected = cfg.Hero.MaxSpeed * cfg.Hero.AimMoveSpeedFrac; // fixture expr
            Assert.AreEqual(expected, w.Player.Vel.x, 0.05f);
        }

        [Test]
        public void AimReleased_RestoresMaxSpeed()
        {
            var cfg = TestConfigs.OpenField();
            var w = World();
            for (int i = 0; i < 60; i++) w.Tick(MoveAim(1f, 0f)); // capped under aim
            float capped = cfg.Hero.MaxSpeed * cfg.Hero.AimMoveSpeedFrac;
            Assert.AreEqual(capped, w.Player.Vel.x, 0.05f, "test setup: must be capped under aim");

            for (int i = 0; i < 60; i++) w.Tick(Move(1f, 0f)); // aim released — cap lifts immediately
            Assert.AreEqual(cfg.Hero.MaxSpeed, w.Player.Vel.x, 0.05f);
        }

        [Test]
        public void AimSettle_GrowsAndDecaysTwiceAsFast()
        {
            var w = World();
            // Grow for a few ticks, well short of the AimSettleSeconds ceiling.
            const int growTicks = 3;
            for (int i = 0; i < growTicks; i++) w.Tick(new SimInput { AimHeld = true });
            float expectedGrown = growTicks * SimulationWorld.TickDt; // fixture expr, PD5
            Assert.AreEqual(expectedGrown, w.Player.AimSettleTimer, 1e-4f);

            // Release: decays at 2x the growth rate (spec — A11 decay x2).
            const int decayTicks = 1;
            for (int i = 0; i < decayTicks; i++) w.Tick(default);
            float expectedDecayed = math.max(0f, expectedGrown - 2f * decayTicks * SimulationWorld.TickDt);
            Assert.AreEqual(expectedDecayed, w.Player.AimSettleTimer, 1e-4f);
        }

        [Test]
        public void HoldRight_AcceleratesToMaxSpeed()
        {
            var w = World();
            for (int i = 0; i < 60; i++) w.Tick(Move(1f, 0f)); // 2 s — enough to reach top speed
            Assert.AreEqual(TestConfigs.OpenField().Hero.MaxSpeed, w.Player.Vel.x, 0.05f);
            Assert.Greater(w.Player.Pos.x, 5f);
        }

        [Test]
        public void ReleaseInput_FrictionStopsPlayer()
        {
            var w = World();
            for (int i = 0; i < 60; i++) w.Tick(Move(1f, 0f));
            for (int i = 0; i < 60; i++) w.Tick(default);
            Assert.AreEqual(0f, math.length(w.Player.Vel), 0.05f);
        }

        [Test]
        public void Wall_StopsAndSlides()
        {
            // Stage 2 Task 16: the ring radius is a FIXTURE EXPRESSION now
            // (convention app-n6g C14) — it used to be the literal 35, which the
            // arena's 35 -> 65 growth silently invalidated.
            SimConfig cfg = TestConfigs.Open();
            float rim = cfg.Arena.Radius - cfg.Hero.Radius;
            var w = World();
            // Stage 3 Task 12: the RUN LENGTH is a fixture expression now, for
            // exactly the reason the ring radius already was. 400 ticks covered
            // 400 * MaxSpeed * TickDt = 93.3 m, which crossed the 65 m arena
            // twice over and stops 20 m short of the 113 m one — the run ended
            // at |pos| = 92.83 with the player still walking, and the assertion
            // below read that as "the wall is in the wrong place" (it is not:
            // 92.83 = 93.3 minus the acceleration ramp, to the centimeter).
            // + 30 ticks of slack covers the ramp and leaves the player pinned
            // against the rim rather than arriving exactly on the last tick.
            int runTicks = (int)math.ceil(rim / (cfg.Hero.MaxSpeed * SimulationWorld.TickDt)) + 30;
            for (int i = 0; i < runTicks; i++) w.Tick(Move(1f, 0f)); // run into the wall
            float2 atWall = w.Player.Pos;
            Assert.AreEqual(rim, math.length(atWall), 0.05f);
            for (int i = 0; i < 30; i++) w.Tick(Move(1f, 1f)); // diagonal into the wall -> slides
            Assert.Greater(w.Player.Pos.y, atWall.y + 0.5f);
            Assert.LessOrEqual(math.length(w.Player.Pos), rim + 0.01f);
        }

        [Test]
        public void Obstacle_BlocksAndSlides_NoSpeedGain()
        {
            var cfg = TestConfigs.Quiet(); // obstacle (10,4) r=2.2, waves disabled
            var w = new SimulationWorld(1, cfg);
            for (int i = 0; i < 600; i++)
            {
                w.Tick(Move(1f, 0.4f));
                float speed = math.length(w.Player.Vel);
                Assert.LessOrEqual(speed, cfg.Hero.MaxSpeed + 1e-3f); // sliding does not accelerate
                Assert.IsFalse(Geometry.CircleOverlap(w.Player.Pos, cfg.Hero.Radius - 0.01f,
                    new float2(10f, 4f), 2.2f), "player inside the obstacle");
            }
            // sliding actually makes progress: a player stuck at the obstacle is a failure
            Assert.Greater(w.Player.Pos.y, 1.5f, "did not go around the obstacle — stuck");
        }

        [Test]
        public void CornerWallPlusObstacle_NoStuckNoTunnel()
        {
            // Stage 2 Task 16: the obstacle sits a fixed 2 m short of the rim
            // (fixture expression, C14) instead of the old literal 33 that only
            // meant "at the rim" while Arena.Radius was 35.
            var cfg = TestConfigs.OpenField();
            float rim = cfg.Arena.Radius - cfg.Hero.Radius;
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(cfg.Arena.Radius - 2f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 1.5f };
            var w = new SimulationWorld(1, cfg);
            float2 start = w.Player.Pos;
            for (int i = 0; i < 500; i++)
            {
                w.Tick(Move(1f, 0.05f));
                Assert.IsTrue(math.all(math.isfinite(w.Player.Pos)));
                Assert.LessOrEqual(math.length(w.Player.Pos), rim + 0.01f);
            }
            Assert.Greater(math.distance(w.Player.Pos, start), 10f,
                "stuck in the wall+obstacle corner — not sliding");
        }

        // ---- app-94sk T5c. The collector's course (spec §3.8) ----

        /// THE MOVING HALF OF THE LAW. Until T5c the collector had no course in
        /// the simulation at all — it was `PlayerVisual._facing`, a private
        /// quaternion turned on FRAME time — so this is the first statement
        /// anywhere that a traveling body turns along its travel, and how
        /// fast.
        ///
        /// THE BUDGET IS ASSERTED AS AN EQUALITY, NOT AS A BOUND, and that is
        /// what tells the two rates apart: the gap here is a full 180 degrees,
        /// far more than either rate can spend in a tick, so the turn is
        /// clamped to exactly one tick's worth of whichever rate the law
        /// picked. A law that read the idle rate here, or crossed the two over,
        /// fails by number rather than by feel.
        ///
        /// BOTH RATES ARE THIS TEST'S OWN INPUTS, deliberately unequal to the
        /// shipped pair and to each other: a law reading a CONSTANT instead of
        /// `Hero.VisualTurnDegPerSec` would pass any fixture whose budget came
        /// from the same shipped 720 (lesson 833 — a number identical in both
        /// sources has no witness by construction). They are set BEFORE the
        /// world is built, because the world copies its configuration in the
        /// constructor.
        [Test]
        public void CollectorsCourse_FollowsItsTravel_AtTheMovingRate()
        {
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.VisualTurnDegPerSec = 333f;    // this test's own rates, not balance
            cfg.Hero.IdleAimTurnDegPerSec = 111f;
            var w = new SimulationWorld(1, cfg);
            // Plant the course against the travel: the body runs +X and looks -X.
            var p = w.PlayerAt(0);
            p.Dir = new float2(-1f, 0f);
            w.SetPlayerForTest(0, p);

            w.Tick(Move(1f, 0f));

            Assert.Greater(w.PlayerAt(0).Vel.x, 0f,
                "fixture premise: one tick of input already gives the body a travel to follow");
            float budget = math.radians(cfg.Hero.VisualTurnDegPerSec) * SimulationWorld.TickDt;
            float turned = math.acos(math.clamp(
                math.dot(w.PlayerAt(0).Dir, new float2(-1f, 0f)), -1f, 1f));
            Assert.AreEqual(budget, turned, 1e-4f,
                "a traveling collector turns by exactly one tick of Hero.VisualTurnDegPerSec");
            Assert.AreEqual(1f, math.length(w.PlayerAt(0).Dir), 1e-4f,
                "and the course stays a unit vector");

            int ticksFor180 = (int)math.ceil(math.PI / budget) + 2;
            for (int i = 0; i < ticksFor180; i++) w.Tick(Move(1f, 0f));
            Assert.Greater(math.dot(w.PlayerAt(0).Dir, new float2(1f, 0f)), 0.99f,
                $"in {ticksFor180} ticks the course never came round to the travel — there is no turn");
        }

        /// THE STANDING HALF OF THE SAME LAW (Б8 in `PlayerVisual`'s own
        /// words): a body that is not traveling turns in towards its AIM, and
        /// at the gentler of the two rates — the doll must never stay
        /// back-to-cursor while shooting on the spot, and it must not whip
        /// round either.
        ///
        /// THE TWO RATES ARE WHAT THIS FIXTURE BUYS. Its twin above pins the
        /// moving one; a law that spent one rate on both branches, or crossed
        /// the two over, passes that twin and fails here.
        ///
        /// "STANDING" IS ASSERTED EXACTLY, WITH NO TOLERANCE, and that is the
        /// premise's whole point rather than a flourish:
        /// `PlayerMovementSystem.MoveTowards` RETURNS its target once inside
        /// one step of it, so friction resolves an unheld collector's velocity
        /// to precisely zero. A tolerance here would have been a second
        /// spelling of `Geometry.MinHeadingLength`, which is exactly what
        /// naming that constant was meant to prevent.
        [Test]
        public void CollectorsCourse_TurnsInTowardsTheAim_AtTheIdleRate()
        {
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.VisualTurnDegPerSec = 333f;    // this test's own rates, not balance
            cfg.Hero.IdleAimTurnDegPerSec = 111f;
            var w = new SimulationWorld(1, cfg);
            // Plant the course against the aim: the body looks -Y, aims +Y.
            var p = w.PlayerAt(0);
            p.Dir = new float2(0f, -1f);
            w.SetPlayerForTest(0, p);
            var aim = new SimInput { AimPoint = new float2(0f, 12f) };

            w.Tick(aim);

            Assert.That(math.lengthsq(w.PlayerAt(0).Vel), Is.EqualTo(0f),
                "fixture premise: the body stands — exactly, not nearly — so the aim branch "
                + "is the one under test");
            float budget = math.radians(cfg.Hero.IdleAimTurnDegPerSec) * SimulationWorld.TickDt;
            float turned = math.acos(math.clamp(
                math.dot(w.PlayerAt(0).Dir, new float2(0f, -1f)), -1f, 1f));
            Assert.AreEqual(budget, turned, 1e-4f,
                "a standing collector turns by exactly one tick of Hero.IdleAimTurnDegPerSec — "
                + "the MOVING rate here would be three times that");

            int ticksFor180 = (int)math.ceil(math.PI / budget) + 2;
            for (int i = 0; i < ticksFor180; i++) w.Tick(aim);
            Assert.Greater(math.dot(w.PlayerAt(0).Dir, new float2(0f, 1f)), 0.99f,
                $"in {ticksFor180} ticks the course never came round to the aim");
        }

        /// app-94sk T5c: A COLLECTOR LANDS IN THE RAID LOOKING INWARD, with a
        /// course of length one — the constructor's half of what
        /// `SimulationWorld.SpawnMob` does for a mob, and needed for the same
        /// reason: `Geometry.RotateTowards` returns `from` unchanged below
        /// `Geometry.MinHeadingLength`, so a course left at zero is an
        /// ABSORBING state and the law above would be dead for the whole match.
        ///
        /// INWARD IS NOT AN INVENTION: a collector spawns on the ring and
        /// `AimPoint` defaults to the arena center, so this is exactly where
        /// the standing branch of the law would steer it anyway — the seed just
        /// saves it the trip.
        ///
        /// THE DIRECTION IS ASSERTED AS A GEOMETRIC CONSEQUENCE (review round
        /// finding): walk from the seat along its own course, by its own
        /// distance from the center, and you must arrive AT the center. A
        /// comparison against `normalizesafe(-pos)` would have been the
        /// product's own formula written twice.
        ///
        /// THREE SEATS, THREE BEARINGS, so no constant satisfies all of them —
        /// and the world is built rather than relocated, because the seeding
        /// under test happens in the constructor.
        [Test]
        public void CollectorsCourseIsSeededInward_AsAUnitVector()
        {
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            Assert.Greater(cfg.Arena.PlayerSpawnRingFrac, 0f,
                "fixture premise: Open() spawns the seats on a ring, not all at the center");

            var seen = new float2[3];
            for (int i = 0; i < 3; i++)
            {
                float2 pos = w.PlayerAt(i).Pos;
                float2 dir = w.PlayerAt(i).Dir;
                seen[i] = dir;
                Assert.AreEqual(1f, math.length(dir), 1e-4f, $"seat {i}: a course is a UNIT vector");
                Assert.That(math.length(pos + dir * math.length(pos)), Is.LessThan(1e-3f),
                    $"seat {i}: walking along the course by its own radius arrives at the arena center");
            }
            Assert.Less(math.dot(seen[0], seen[1]), 0.99f,
                "witness: the three seats do not share one constant course");
            Assert.Less(math.dot(seen[1], seen[2]), 0.99f, "…nor do the other two");
        }
    }
}