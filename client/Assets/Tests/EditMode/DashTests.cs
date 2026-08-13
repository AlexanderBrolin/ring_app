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
            for (int i = 0; i < 6; i++) w.Tick(HoldRight); // dash ended (0.15 s = 4.5 ticks)
            w.Tick(DashRight);                              // cooldown 1.2 s still running
            Assert.AreEqual(1, w.Stats.DashesUsed);
            for (int i = 0; i < 40; i++) w.Tick(HoldRight); // cooldown elapsed
            w.Tick(DashRight);
            Assert.AreEqual(2, w.Stats.DashesUsed);
        }

        [Test]
        public void DashBuffer_LatchedRequestFiresWhenCooldownEnds()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(DashRight);
            for (int i = 0; i < 40; i++) w.Tick(HoldRight);
            // request ~3 ticks before cooldown ends — the 0.15 s (4.5-tick) buffer carries it through
            var w2 = new SimulationWorld(1, TestConfigs.Open());
            w2.Tick(DashRight);                       // tick 1: dash; cooldown 1.2 s = 36 ticks
            for (int i = 0; i < 32; i++) w2.Tick(HoldRight); // ticks 2..33
            w2.Tick(DashRight);                       // tick 34: cooldown still active — goes into the buffer
            Assert.AreEqual(1, w2.Stats.DashesUsed);  // no immediate dash
            for (int i = 0; i < 4; i++) w2.Tick(HoldRight); // cooldown expires — the buffer fires
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
            for (int i = 0; i < 7; i++) w.Tick(HoldRight); // 0.2 s = 6 ticks
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
            cfg.Arena.ObstacleRadius = new[] { 0.6f }; // dash step 0.73 m > the obstacle's diameter — only the sweep keeps this from tunneling
            var w = new SimulationWorld(1, cfg);
            // Stage 2 Task 10: this loop spams DashRequested every tick, so the
            // edge-request rate limit now drops two out of every three requests
            // (Hero.EdgeRequestMinTicks = 3). The invariant under test is
            // unaffected — what drives the player into the obstacle is the
            // ACTIVE dash (DashTimer/DashDir), not the per-tick request — and the
            // number of dashes this run gets is unchanged too: it is bounded by
            // Hero.DashCooldown (1.2 s = 36 ticks, far longer than these 10),
            // not by the gate. Both facts are pinned below rather than left
            // implicit, so a future gate change that silently starved this
            // fixture of its one dash fails here instead of going unnoticed.
            for (int i = 0; i < 10; i++)
            {
                w.Tick(DashRight);
                Assert.IsFalse(Geometry.CircleOverlap(w.Player.Pos, cfg.Hero.Radius - 0.01f,
                    new float2(2f, 0f), 0.6f), "player tunneled into the obstacle");
            }
            Assert.AreEqual(1, w.Stats.DashesUsed,
                "the cooldown, not the rate limit, is what caps this run at a single dash");
            Assert.Greater(w.RejectedEdgeRequestsForTest, 0,
                "per-tick spam must actually be reaching the rate limit here");
        }
    }
}
