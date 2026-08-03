using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class SlideTests
    {
        static SimInput Move(float x, float y) => new SimInput { MoveDir = new float2(x, y) };

        /// Runs enough held-forward ticks to fully accrue RunUpTimer (fixture
        /// expression, not a literal — margin covers both the accel-up-to-
        /// threshold ramp and the RunUpSeconds accrual itself).
        static void RunUp(SimulationWorld w, SimConfig cfg)
        {
            int ticks = (int)math.ceil(cfg.Hero.RunUpSeconds / SimulationWorld.TickDt) + 24;
            for (int i = 0; i < ticks; i++) w.Tick(Move(1f, 0f));
        }

        [Test]
        public void Slide_RequiresRunUpOrPostDash()
        {
            var cfg = TestConfigs.Open();

            // No run-up at all: the very first tick's request must not slide.
            var cold = new SimulationWorld(1, cfg);
            cold.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true });
            Assert.AreEqual(0, cold.Stats.SlidesUsed);

            // After a full RunUpSeconds of sustained movement, the same
            // request starts the slide.
            var warm = new SimulationWorld(1, cfg);
            RunUp(warm, cfg);
            Assert.AreEqual(cfg.Hero.RunUpSeconds, warm.Player.RunUpTimer, 1e-3f);
            warm.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true });
            Assert.AreEqual(1, warm.Stats.SlidesUsed);
        }

        [Test]
        public void PostDash_OpensSlideWindow()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // dash starts
            for (int i = 0; i < 10; i++) w.Tick(Move(1f, 0f)); // dash ends well within this margin
            Assert.Greater(w.Player.PostDashSlideTimer, 0f, "test setup: post-dash window must be open");
            // Post-dash deceleration alone can nudge RunUpTimer off zero (Vel
            // stays above the slide threshold for a few ticks after the dash
            // ends), but must fall well short of the full RunUpSeconds gate —
            // this test is specifically about the post-dash path covering for
            // an incomplete run-up, not a coincidentally-complete one.
            Assert.Less(w.Player.RunUpTimer, cfg.Hero.RunUpSeconds, "test setup: run-up must not be full");

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true });
            Assert.AreEqual(1, w.Stats.SlidesUsed);
        }

        [Test]
        public void RunUp_DecaysBelowThreshold()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            for (int i = 0; i < 60; i++) w.Tick(Move(1f, 0f)); // capped at RunUpSeconds, at MaxSpeed
            Assert.AreEqual(cfg.Hero.RunUpSeconds, w.Player.RunUpTimer, 1e-3f);

            float threshold = cfg.Hero.SlideMinSpeedFrac * cfg.Hero.MaxSpeed;
            float runUpAtCross = -1f;
            int crossTick = -1;
            for (int i = 0; i < 20; i++)
            {
                w.Tick(default); // release input — friction decelerates
                if (math.length(w.Player.Vel) < threshold)
                {
                    crossTick = i;
                    runUpAtCross = w.Player.RunUpTimer;
                    break;
                }
            }
            Assert.GreaterOrEqual(crossTick, 0, "velocity never dropped below the slide threshold");

            // K further idle ticks, once below threshold, decay linearly by
            // RunUpDecayMult * dt per tick (fixture expression, PD5).
            const int K = 5;
            for (int i = 0; i < K; i++) w.Tick(default);
            float expected = math.max(0f, runUpAtCross - cfg.Hero.RunUpDecayMult * K * SimulationWorld.TickDt);
            Assert.AreEqual(expected, w.Player.RunUpTimer, 1e-3f);
        }

        [Test]
        public void Slide_InsufficientStamina_Denied()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            RunUp(w, cfg);
            const float missing = 1f;
            var p = w.Player;
            p.Stamina = cfg.Hero.SlideStaminaCost - missing;
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true });
            Assert.AreEqual(0, w.Stats.SlidesUsed);
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.StaminaDenied));
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.StaminaDenied, out SimEvent e));
            // Regen is unconditional once DashTimer/SlideTimer are both <=0
            // (same tick as the denial here — StaminaRegenDelayTimer is still
            // 0, nothing primed it), and it runs inside Update() before the
            // world reads Stamina back out for the event's Amount — so the
            // reported deficit is already one tick's regen short of `missing`
            // (same mechanism DashDenied relies on, spec §3.4/QD8).
            float expected = missing - cfg.Hero.StaminaRegenPerSec * SimulationWorld.TickDt;
            Assert.AreEqual(expected, e.Amount, 1e-3f);
        }

        [Test]
        public void Slide_ResetsRunUp_NoChain()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            RunUp(w, cfg);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide #1
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            Assert.AreEqual(0f, w.Player.RunUpTimer); // M2: reset on start

            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt) + 1;
            for (int i = 0; i < slideTicks; i++) w.Tick(Move(1f, 0f)); // ride slide #1 out
            Assert.AreEqual(0f, w.Player.SlideTimer, "test setup: slide #1 must have ended");

            // Immediate re-request: neither RunUp nor PostDash had time to
            // rebuild (both zeroed by M2 on slide #1's start) — no chaining.
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true });
            Assert.AreEqual(1, w.Stats.SlidesUsed);
        }

        [Test]
        public void Slide_MutualExclusionWithDash()
        {
            var cfg = TestConfigs.Open();

            // A slide request buffered throughout a dash fires once the
            // post-dash window opens (buffer kept fresh across the dash so it
            // survives the DashDuration == SlideBufferWindow coincidence in
            // TestConfigs.Default()).
            var w = new SimulationWorld(1, cfg);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // dash starts
            for (int i = 0; i < 5; i++)
                w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // buffered through dash-end
            Assert.AreEqual(0, w.Stats.SlidesUsed, "still mid-dash — must not have fired yet");
            w.Tick(Move(1f, 0f)); // post-dash window open, buffer still alive
            Assert.AreEqual(1, w.Stats.SlidesUsed);

            // A dash request while sliding never starts the dash (QD10).
            var w2 = new SimulationWorld(1, cfg);
            RunUp(w2, cfg);
            w2.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide starts
            Assert.AreEqual(1, w2.Stats.SlidesUsed);
            Assert.Greater(w2.Player.SlideTimer, 0f, "test setup: must still be sliding");
            w2.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true });
            Assert.AreEqual(0, w2.Stats.DashesUsed);
        }

        [Test]
        public void SlideDir_FallbackToAim_WhenIdle()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true });
            for (int i = 0; i < 6; i++) w.Tick(Move(1f, 0f)); // dash ends, post-dash window opens
            var p = w.Player;
            Assert.Greater(p.PostDashSlideTimer, 0f, "test setup: post-dash window must be open");
            p.Vel = float2.zero; // idle: no MoveDir, no Vel to fall back on either
            w.SetPlayerForTest(p);

            float2 aimPoint = p.Pos + new float2(0f, 10f);
            w.Tick(new SimInput { MoveDir = float2.zero, AimPoint = aimPoint, SlideRequested = true });
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            Assert.AreEqual(0f, w.Player.SlideDir.x, 1e-4f);
            Assert.AreEqual(1f, w.Player.SlideDir.y, 1e-4f);
        }

        [Test]
        public void Slide_SteerRateIsClamped()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            RunUp(w, cfg);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // SlideDir = (1,0)
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            float2 dirBefore = w.Player.SlideDir;

            w.Tick(Move(-1f, 0f)); // 180-degree steer input for a single tick
            float2 dirAfter = w.Player.SlideDir;
            float angle = math.acos(math.clamp(
                math.dot(math.normalizesafe(dirBefore), math.normalizesafe(dirAfter)), -1f, 1f));
            Assert.LessOrEqual(angle, cfg.Hero.SlideSteerRadPerSec * SimulationWorld.TickDt + 1e-4f);
        }

        [Test]
        public void Slide_ExitKeepsMomentum()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            RunUp(w, cfg);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true });
            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt);
            for (int i = 0; i < slideTicks - 1; i++) w.Tick(Move(1f, 0f));
            Assert.Greater(w.Player.SlideTimer, 0f, "test setup: must still be on the last active tick");

            w.Tick(Move(1f, 0f)); // this tick ends the slide
            Assert.AreEqual(0f, w.Player.SlideTimer);
            Assert.AreEqual(cfg.Hero.SlideSpeed, math.length(w.Player.Vel), 0.05f);

            // Short hold, not 60 ticks: the run-up + slide + this decay leg
            // together approach the Open() arena's wall (radius 35) closely
            // enough that a long hold here would hit it and slam Vel to 0,
            // masking the thing this test actually checks. (SlideSpeed -
            // MaxSpeed) / Accel is ~4.9 ticks to converge — 20 is ample margin.
            for (int i = 0; i < 20; i++) w.Tick(Move(1f, 0f)); // decays towards regular max speed
            Assert.AreEqual(cfg.Hero.MaxSpeed, math.length(w.Player.Vel), 0.05f);
        }

        [Test]
        public void Death_ClearsSlideState()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            RunUp(w, cfg);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true });
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            Assert.Greater(w.Player.SlideTimer, 0f, "test setup: must be mid-slide at death");

            w.KillPlayerForTest();

            var p = w.Player;
            Assert.AreEqual(0f, p.SlideTimer);
            Assert.AreEqual(0f, p.SlideBufferTimer);
            Assert.AreEqual(0f, p.RunUpTimer);
            Assert.AreEqual(0f, p.PostDashSlideTimer);
            Assert.AreEqual(0f, p.LinkWindowTimer);
        }

        [Test]
        public void SlideBuffer_FiresWhenRegenCoversCost()
        {
            var cfg = TestConfigs.RegenFixture();
            var w = new SimulationWorld(1, cfg);
            RunUp(w, cfg);

            const float missing = 1f;
            var p = w.Player;
            p.Stamina = cfg.Hero.SlideStaminaCost - missing;
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // denied, buffer stays alive
            Assert.AreEqual(0, w.Stats.SlidesUsed);
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.StaminaDenied));

            bool fired = false;
            for (int i = 0; i < 4; i++) // remainder of the buffer window
            {
                w.Tick(Move(1f, 0f));
                if (w.Stats.SlidesUsed == 1) { fired = true; break; }
            }
            Assert.IsTrue(fired, "regen should have topped up stamina within the buffer window");
            // StaminaDenied must not have re-fired on the silent retries.
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.StaminaDenied));
        }

        [Test]
        public void SlideStarted_EventCarriesPosAndDir()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            RunUp(w, cfg);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true });
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerSlideStarted, out SimEvent e));
            Assert.AreEqual(w.Player.Pos, e.Pos);
            Assert.AreEqual(w.Player.SlideDir, e.HitDir);
        }

        [Test]
        public void WallStop_KillsSlide_NoLinkWindow()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(5f, 0f) }; // dead ahead — head-on hit
            cfg.Arena.ObstacleRadius = new[] { 1f };
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            // QA1 seam: satisfies the slide gate without ticking through a full
            // run-up — Vel must clear the SlideMinSpeedFrac threshold too, or
            // Update()'s "moving" check decays RunUpTimer back below RunUpSeconds
            // on this very first tick before the gate is even read.
            p.RunUpTimer = cfg.Hero.RunUpSeconds;
            p.Vel = new float2(cfg.Hero.MaxSpeed, 0f);
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide starts
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            Assert.Greater(w.Player.SlideTimer, 0f, "test setup: must be sliding");

            int budget = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt) + 2;
            bool stopped = false;
            for (int i = 0; i < budget; i++)
            {
                w.Tick(new SimInput { MoveDir = new float2(1f, 0f) });
                if (w.Player.SlideTimer <= 0f) { stopped = true; break; }
            }
            Assert.IsTrue(stopped, "test setup: slide never ended");
            Assert.AreEqual(0f, w.Player.SlideTimer);
            Assert.AreEqual(0f, w.Player.LinkWindowTimer, "wall-stop must not open the link window");

            // Proof by consequence (M3): if the window had opened, this dash
            // would be linked and cost less — it must cost the FULL price.
            float staminaBeforeDash = w.Player.Stamina;
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true });
            Assert.AreEqual(1, w.Stats.DashesUsed);
            Assert.AreEqual(staminaBeforeDash - cfg.Hero.DashStaminaCost, w.Player.Stamina, 1e-3f,
                "wall-stop must not leave a link window — the dash must cost the FULL price");
        }

        [Test]
        public void SlideAlongWall_Continues()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            // Offset so the swept-circle path only grazes the obstacle: at this
            // offset dot(-normal, SlideDir) ~= 0.44, comfortably under the
            // fixture's SlideWallStopDot (0.7) — a shallow hit, not a head-on one.
            cfg.Arena.ObstaclePos = new[] { new float2(5f, 1.3f) };
            cfg.Arena.ObstacleRadius = new[] { 1f };
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.RunUpTimer = cfg.Hero.RunUpSeconds; // QA1 seam (see WallStop_KillsSlide_NoLinkWindow)
            p.Vel = new float2(cfg.Hero.MaxSpeed, 0f);
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide starts
            Assert.AreEqual(1, w.Stats.SlidesUsed);

            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt);
            for (int i = 0; i < slideTicks - 1; i++)
                w.Tick(new SimInput { MoveDir = new float2(1f, 0f) });
            Assert.Greater(w.Player.SlideTimer, 0f, "graze must not have wall-stopped the slide early");

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f) }); // this tick ends it naturally
            Assert.AreEqual(0f, w.Player.SlideTimer);
            Assert.Greater(w.Player.LinkWindowTimer, 0f, "natural exit must still open the link window");
        }
    }
}
