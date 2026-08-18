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
            // Stage 3 Task 12: the ARENA RADIUS joins the numbers this fixture
            // owns. Ricochet_OncePerTick's own doc has said "the ring wall
            // (r=35)" since Task 12 of Stage 1, and the obstacle it wedges
            // against the wall sits at x=33 — but the shared arena grew to 65
            // in Stage 2 and to 113 here, so the wall walked away from the
            // obstacle and the documented corner stopped existing. It survived
            // at 65 because 200 ticks still carried the player past the wall
            // somewhere; at 113 the run covers ~47 m of a 112.55 m radius, the
            // player never reaches any wall, and the fixture health check went
            // red with zero ricochets — the drift finally becoming visible two
            // stages after it started. Pinning the radius here restores the
            // corner exactly as documented and makes every ricochet number in
            // this file independent of arena tuning for good (file convention
            // C14/PD5, stated at the top of this method).
            TestConfigs.ShrinkArena(ref cfg, 35f);
            return cfg;
        }

        /// Fixture() plus a single obstacle head-on at (2,0): the dash's very
        /// first tick (1 m step) already reaches the obstacle's padded surface
        /// (Hero.Radius 0.45 + ObstacleRadius 0.6 = 1.05 m from its center, so
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
            // Stage 2 Task 10: the loop spams DashRequested every tick, so the
            // edge-request rate limit drops two of every three requests. The
            // per-tick cap under test is unaffected (it is a property of
            // MoveWithCollisions reporting only its FIRST contact, not of how
            // often a dash is requested), and the run still reaches the corner
            // and bounces — but "still bounces" is exactly the sort of premise a
            // rate limit could quietly destroy, turning this into a test that
            // passes by never ricocheting at all. So the total is accumulated
            // and asserted non-zero below instead of being assumed.
            int totalRicochets = 0;
            for (int i = 0; i < 200; i++)
            {
                w.ClearEvents();
                w.Tick(dashDiagonal);
                int thisTick = TestEvents.CountOf(w, SimEventKind.DashRicocheted);
                Assert.LessOrEqual(thisTick, 1,
                    "more than one DashRicocheted reported for a single tick");
                totalRicochets += thisTick;
            }
            // Fix-round 1 (M-4): this is a FIXTURE HEALTH CHECK, not a check on
            // the rate limit. With Hero.DashCooldown at 36 ticks against a
            // 3-tick gate window, the gate does not bound this run's dash count
            // at all — what does is the cooldown and the stamina economy. So if
            // this line ever goes red, look at the fixture's own numbers first,
            // not at the gate.
            Assert.Greater(totalRicochets, 0,
                "fixture health check: this run must still produce dashes that reach the " +
                "corner and bounce — otherwise the per-tick cap above is vacuously true");
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

        /// Fixture() plus two obstacles forming a genuine two-bounce corner. A
        /// straight axial back-and-forth CAN'T give two ricochets: whichever
        /// obstacle sits closer to the start is hit first on the way out AND
        /// (being on the very same line) on the way back, so a second, distinct
        /// bounce needs the first one to actually deflect the heading off that
        /// line — hence a diagonal dash and two obstacles placed off-axis from
        /// each other, not the head-on ObstacleFixture() placement above.
        /// Positions/radii were fit (see task-14-report.md fix-wave notes) so
        /// obstacle A is hit on the dash's start tick and, riding the mirrored
        /// DashDir the very next tick, obstacle B is hit before the doubly-
        /// mirrored heading clears both — with comfortable margins (no starting
        /// overlap with the player's spawn, no re-hit of either obstacle) so the
        /// result isn't a hair-trigger coincidence of these exact digits.
        static SimConfig TwoBounceFixture()
        {
            var cfg = Fixture();
            cfg.Arena.ObstacleCount = 2;
            cfg.Arena.ObstaclePos = new[]
                { new float2(1.334f, 0.697f), new float2(-0.288f, 1.634f) };
            cfg.Arena.ObstacleRadius = new[] { 0.5f, 0.5f };
            return cfg;
        }

        [Test]
        public void Ricochet_RetentionCompoundsAcrossTwoBounces()
        {
            var cfg = TwoBounceFixture();
            var w = new SimulationWorld(1, cfg);
            var dashDiagonal = new SimInput { MoveDir = new float2(1f, 1f), DashRequested = true };
            // DashDir/DashSpeedCur alone drive Vel on continuation ticks (the
            // branch never re-reads input.MoveDir) — this input only needs
            // DashRequested to stay false so a fresh dash isn't re-triggered.
            var holdDiagonal = new SimInput { MoveDir = new float2(1f, 1f) };

            w.Tick(dashDiagonal); // start tick: dash clips obstacle A — bounce #1
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.DashRicocheted),
                "bounce #1 must have fired on the dash's start tick");

            w.Tick(holdDiagonal); // mirrored DashDir now drives Vel into obstacle B — bounce #2
            Assert.AreEqual(2, TestEvents.CountOf(w, SimEventKind.DashRicocheted),
                "bounce #2 must have fired on this tick");

            w.Tick(holdDiagonal); // doubly-mirrored DashDir applies at doubly-retained speed, clear of both obstacles
            float expected = cfg.Hero.DashSpeed
                * cfg.Hero.RicochetRetention * cfg.Hero.RicochetRetention; // fixture expr, PD5
            Assert.AreEqual(expected, math.length(w.Player.Vel), 1e-3f);
        }
    }
}
