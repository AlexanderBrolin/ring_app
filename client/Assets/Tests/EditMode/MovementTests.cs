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

        [Test]
        public void HoldRight_AcceleratesToMaxSpeed()
        {
            var w = World();
            for (int i = 0; i < 60; i++) w.Tick(Move(1f, 0f)); // 2 c — хватает разогнаться
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
            var w = World();
            for (int i = 0; i < 400; i++) w.Tick(Move(1f, 0f)); // упереться в стену
            float2 atWall = w.Player.Pos;
            Assert.AreEqual(35f - TestConfigs.Open().Hero.Radius, math.length(atWall), 0.05f);
            for (int i = 0; i < 30; i++) w.Tick(Move(1f, 1f)); // диагональ у стены → скользит
            Assert.Greater(w.Player.Pos.y, atWall.y + 0.5f);
            Assert.LessOrEqual(math.length(w.Player.Pos), 35f - 0.44f);
        }

        [Test]
        public void Obstacle_BlocksAndSlides_NoSpeedGain()
        {
            var cfg = TestConfigs.Quiet(); // препятствие (10,4) r=2.2, волны выключены
            var w = new SimulationWorld(1, cfg);
            for (int i = 0; i < 600; i++)
            {
                w.Tick(Move(1f, 0.4f));
                float speed = math.length(w.Player.Vel);
                Assert.LessOrEqual(speed, cfg.Hero.MaxSpeed + 1e-3f); // скольжение не ускоряет
                Assert.IsFalse(Geometry.CircleOverlap(w.Player.Pos, cfg.Hero.Radius - 0.01f,
                    new float2(10f, 4f), 2.2f), "игрок внутри препятствия");
            }
            // скольжение реально продвигает: застывший у препятствия игрок — провал
            Assert.Greater(w.Player.Pos.y, 1.5f, "не обогнул препятствие — застрял");
        }

        [Test]
        public void CornerWallPlusObstacle_NoStuckNoTunnel()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(33f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 1.5f };
            var w = new SimulationWorld(1, cfg);
            float2 start = w.Player.Pos;
            for (int i = 0; i < 500; i++)
            {
                w.Tick(Move(1f, 0.05f));
                Assert.IsTrue(math.all(math.isfinite(w.Player.Pos)));
                Assert.LessOrEqual(math.length(w.Player.Pos), 35f - 0.44f);
            }
            Assert.Greater(math.distance(w.Player.Pos, start), 10f,
                "залип в углу стена+препятствие — не скользит");
        }
    }
}
