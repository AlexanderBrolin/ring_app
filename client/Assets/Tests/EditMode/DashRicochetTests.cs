using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class DashRicochetTests
    {
        static readonly SimInput DashRight = new SimInput
            { MoveDir = new float2(1f, 0f), DashRequested = true };
        static readonly SimInput HoldRight = new SimInput { MoveDir = new float2(1f, 0f) };

        /// Task 12 (C14/PD5): all ricochet-specific arithmetic below rides on
        /// this explicit fixture, not on TestConfigs' shared numbers — DashSpeed
        /// 30 * dt (1/30 s) = 1 m/tick exactly, DashDuration 0.09 s spans 4
        /// dash-owned ticks (start + 3 continuations, the last one clamping
        /// DashTimer to 0 while still applying this tick's dash Vel) = 4.0 m
        /// of straight-line travel with no obstacle in the way.
        static SimConfig Fixture()
        {
            var cfg = TestConfigs.Open();
            cfg.Hero.DashSpeed = 30f;
            cfg.Hero.DashDuration = 0.09f;
            cfg.Hero.RicochetRetention = 0.8f;
            return cfg;
        }

        /// Fixture() plus a single obstacle head-on at (2,0): the dash's very
        /// first tick (1 m step) already reaches the obstacle's padded surface
        /// (Hero.Radius 0.45 + ObstacleRadius 0.6 = 1.05 m from its centre, so
        /// contact lands at x ~= 0.95), giving a perfectly axial normal
        /// (-1, 0) — the contact tick is also the dash-start tick.
        static SimConfig ObstacleFixture()
        {
            var cfg = Fixture();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(2f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 0.6f };
            return cfg;
        }

        [Test]
        public void Ricochet_MirrorsDashDir_NextTick()
        {
            var w = new SimulationWorld(1, ObstacleFixture());
            w.Tick(DashRight); // contact tick: pinned against the obstacle (D16)
            Assert.AreEqual(0f, w.Player.Vel.x, 1e-3f);
            w.Tick(HoldRight); // next dash tick: mirrored DashDir now drives Vel
            Assert.Less(w.Player.Vel.x, 0f);
        }

        [Test]
        public void Ricochet_AppliesRetention()
        {
            var cfg = ObstacleFixture();
            var w = new SimulationWorld(1, cfg);
            w.Tick(DashRight);
            w.Tick(HoldRight);
            Assert.AreEqual(cfg.Hero.DashSpeed * cfg.Hero.RicochetRetention,
                math.length(w.Player.Vel), 1e-3f);
        }

        [Test]
        public void Ricochet_KeepsIframes()
        {
            var w = new SimulationWorld(1, ObstacleFixture());
            w.Tick(DashRight);
            w.Tick(HoldRight);
            Assert.Greater(w.Player.IframeTimer, 0f);
        }

        [Test]
        public void Ricochet_OncePerTick()
        {
            // Corner between the ring wall (r=35, TestConfigs.Open()) and an
            // obstacle wedged against it (same geometry as MovementTests'
            // CornerWallPlusObstacle_NoStuckNoTunnel) — MoveWithCollisions can
            // run up to 3 internal correction iterations against this pocket,
            // but only ITS first contact is ever reported, so at most one
            // DashRicocheted may fire per tick regardless.
            var cfg = Fixture();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(33f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 1.5f };
            var w = new SimulationWorld(1, cfg);
            var dashDiagonal = new SimInput
                { MoveDir = new float2(1f, 0.05f), DashRequested = true };
            for (int i = 0; i < 200; i++)
            {
                w.ClearEvents();
                w.Tick(dashDiagonal);
                Assert.LessOrEqual(TestEvents.CountOf(w, SimEventKind.DashRicocheted), 1,
                    "more than one DashRicocheted reported for a single tick");
            }
        }

        [Test]
        public void Ricochet_EmitsEventWithNormal()
        {
            var w = new SimulationWorld(1, ObstacleFixture());
            w.Tick(DashRight);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.DashRicocheted, out SimEvent e));
            Assert.AreEqual(new float2(-1f, 0f), e.HitDir);
        }

        [Test]
        public void Dash_CoversFixtureMetres()
        {
            var cfg = Fixture();
            var w = new SimulationWorld(1, cfg);
            float2 start = w.Player.Pos;
            w.Tick(DashRight);
            for (int i = 0; i < 3; i++) w.Tick(HoldRight); // 3 more dash ticks = 4 total
            Assert.AreEqual(4.0f, math.distance(w.Player.Pos, start), 1e-3f);
        }
    }
}
