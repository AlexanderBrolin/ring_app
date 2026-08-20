using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class MovementTests
    {
        static SimulationWorld World() => new SimulationWorld(1, TestConfigs.Open());

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
            var cfg = TestConfigs.Open();
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
            var cfg = TestConfigs.Open();
            var w = World();
            for (int i = 0; i < 60; i++) w.Tick(MoveWindow(1f, 0f)); // 2 s
            float expected = cfg.Hero.MaxSpeed * cfg.Hero.AimMoveSpeedFrac; // fixture expr
            Assert.AreEqual(expected, w.Player.Vel.x, 0.05f);
        }

        [Test]
        public void AimReleased_RestoresMaxSpeed()
        {
            var cfg = TestConfigs.Open();
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
            Assert.AreEqual(TestConfigs.Open().Hero.MaxSpeed, w.Player.Vel.x, 0.05f);
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
            var cfg = TestConfigs.Open();
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
    }
}
