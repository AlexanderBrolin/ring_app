using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class DashTests
    {
        static readonly SimInput DashRight = new SimInput
            { MoveDir = new float2(1f, 0f), DashRequested = true };
        static readonly SimInput HoldRight = new SimInput { MoveDir = new float2(1f, 0f) };

        [Test]
        public void Dash_OverridesVelocityForDuration()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(DashRight);
            Assert.AreEqual(TestConfigs.Open().Hero.DashSpeed, w.Player.Vel.x, 0.01f);
            Assert.Greater(w.Player.DashTimer, 0f);
        }

        [Test]
        public void Dash_CooldownBlocksSecondDash()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(DashRight);
            for (int i = 0; i < 6; i++) w.Tick(HoldRight); // дэш кончился (0.15 c = 4.5 тика)
            w.Tick(DashRight);                              // кулдаун 1.2 c ещё идёт
            Assert.AreEqual(1, w.Stats.DashesUsed);
            for (int i = 0; i < 40; i++) w.Tick(HoldRight); // кулдаун прошёл
            w.Tick(DashRight);
            Assert.AreEqual(2, w.Stats.DashesUsed);
        }

        [Test]
        public void DashBuffer_LatchedRequestFiresWhenCooldownEnds()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(DashRight);
            for (int i = 0; i < 40; i++) w.Tick(HoldRight);
            // запрос за ~3 тика до конца кулдауна — буфер 0.15 c (4.5 тика) доносит его
            var w2 = new SimulationWorld(1, TestConfigs.Open());
            w2.Tick(DashRight);                       // тик 1: дэш; кулдаун 1.2 c = 36 тиков
            for (int i = 0; i < 32; i++) w2.Tick(HoldRight); // тики 2..33
            w2.Tick(DashRight);                       // тик 34: кулдаун ещё жив — в буфер
            Assert.AreEqual(1, w2.Stats.DashesUsed);  // немедленного дэша нет
            for (int i = 0; i < 4; i++) w2.Tick(HoldRight); // кулдаун истекает — буфер срабатывает
            Assert.AreEqual(2, w2.Stats.DashesUsed);
        }

        [Test]
        public void ZeroMoveDir_DashesTowardAim()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(new SimInput { AimPoint = new float2(0f, 10f), DashRequested = true });
            Assert.Greater(w.Player.Vel.y, 0f);
        }

        [Test]
        public void Iframes_ActiveDuringWindowThenExpire()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(DashRight);
            Assert.Greater(w.Player.IframeTimer, 0f);
            for (int i = 0; i < 7; i++) w.Tick(HoldRight); // 0.2 c = 6 тиков
            Assert.AreEqual(0f, w.Player.IframeTimer);
        }

        [Test]
        public void DashIntoObstacle_Ricochets_NoTunnel()
        {
            // A9: retargeted from the pre-Task-12 "stops at surface" behaviour
            // — a head-on dash now mirrors off the obstacle instead of just
            // stopping dead, so the surviving invariant is no tunneling, not a
            // pinned position.
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(2f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 0.6f }; // дэш-шаг 0.73 м > диаметра нет, но свип обязан
            var w = new SimulationWorld(1, cfg);
            for (int i = 0; i < 10; i++)
            {
                w.Tick(DashRight);
                Assert.IsFalse(Geometry.CircleOverlap(w.Player.Pos, cfg.Hero.Radius - 0.01f,
                    new float2(2f, 0f), 0.6f), "player tunneled into the obstacle");
            }
        }
    }
}
