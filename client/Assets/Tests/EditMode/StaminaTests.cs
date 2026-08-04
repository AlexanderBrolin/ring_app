using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class StaminaTests
    {
        static SimInput Dash => new SimInput { MoveDir = new float2(1,0), DashRequested = true };

        [Test]
        public void StartsAtFullStamina()
        {
            var cfg = TestConfigs.Open();
            Assert.AreEqual(cfg.Hero.StaminaMax, new SimulationWorld(1, cfg).Player.Stamina);
        }

        [Test]
        public void Dash_CostsStamina()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.Tick(Dash);
            Assert.AreEqual(cfg.Hero.StaminaMax - cfg.Hero.DashStaminaCost, w.Player.Stamina, 1e-3f);
        }

        [Test]
        public void Dash_InsufficientStamina_DeniedWithEvent()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;                                 // canon test-seam (QA1)
            p.Stamina = cfg.Hero.DashStaminaCost - 1f;
            w.SetPlayerForTest(p);
            w.Tick(Dash);
            Assert.AreEqual(0, w.Stats.DashesUsed);
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.StaminaDenied));
            // Amount of the event = missing cost (QD8 assertion §3.4)
        }

        [Test]
        public void Regen_WaitsDelayThenRefills()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.Tick(Dash);
            int delayTicks = (int)math.ceil(cfg.Hero.StaminaRegenDelay / SimulationWorld.TickDt);
            for (int i = 0; i < delayTicks - 2; i++) w.Tick(new SimInput());
            float beforeRegen = w.Player.Stamina;               // delay still running
            for (int i = 0; i < 30; i++) w.Tick(new SimInput());
            Assert.Greater(w.Player.Stamina, beforeRegen);      // regen kicked in
        }

        [Test]
        public void Regen_FrozenDuringSlide_OnFixture()
        {
            // RegenFixture (M16): SlideDuration 0.9s outlasts StaminaRegenDelay
            // 0.3s, so a bug that only gated regen on the post-action delay
            // (and not on SlideTimer itself, QD10) would show regen resuming
            // partway through the slide — this fixture is specifically sized
            // to make that failure mode observable within the slide window.
            var cfg = TestConfigs.RegenFixture();
            var w = new SimulationWorld(1, cfg);
            var move = new SimInput { MoveDir = new float2(1f, 0f) };
            for (int i = 0; i < 60; i++) w.Tick(move); // full run-up
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide starts
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            float staminaAfterStart = w.Player.Stamina;

            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt);
            for (int i = 0; i < slideTicks - 1; i++)
            {
                w.Tick(move);
                Assert.AreEqual(staminaAfterStart, w.Player.Stamina, 1e-4f,
                    "stamina regenerated while still sliding");
            }
        }

        // A dedicated HotTweak spot-test is NOT added here: the clamp is covered
        // by the reflective pass in HotTweakTests (QC7).

        [Test]
        public void LinkedDash_CooldownBypassAndRefund_ConsumesWindow()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var move = new SimInput { MoveDir = new float2(1f, 0f) };

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // dash #1
            Assert.AreEqual(1, w.Stats.DashesUsed);
            for (int i = 0; i < 10; i++) w.Tick(move); // dash ends, post-dash window opens
            Assert.Greater(w.Player.PostDashSlideTimer, 0f, "test setup: post-dash window must be open");

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide, via post-dash window
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            // NB (Б1 economy rework): the slide above is itself "linked" (it
            // used the post-dash window) and so already zeroed DashCooldown —
            // LinkedDash_BypassesStillRunningCooldown below owns the case
            // where the cooldown genuinely is still running at request time;
            // this test's own focus is the window/refund arithmetic.

            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt);
            for (int i = 0; i < slideTicks; i++) w.Tick(move); // ride the slide out to its natural end
            Assert.AreEqual(0f, w.Player.SlideTimer, "test setup: slide must have ended");
            Assert.Greater(w.Player.LinkWindowTimer, 0f, "test setup: link window must be open");

            float staminaBeforeLinkedDash = w.Player.Stamina;
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // linked dash, inside the window
            Assert.AreEqual(2, w.Stats.DashesUsed, "the linked dash must have started");
            float expected = staminaBeforeLinkedDash - cfg.Hero.DashStaminaCost + cfg.Hero.LinkRefund;
            Assert.AreEqual(expected, w.Player.Stamina, 1e-3f);
            Assert.AreEqual(0f, w.Player.LinkWindowTimer, "window must be consumed by the linked dash");

            // Immediate re-request (QA14): the window is gone and the fresh
            // cooldown the linked dash just set holds again — no third dash.
            int dashesBefore = w.Stats.DashesUsed;
            float staminaBefore = w.Player.Stamina;
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true });
            Assert.AreEqual(dashesBefore, w.Stats.DashesUsed, "no third dash — the window was already spent");
            Assert.AreEqual(staminaBefore, w.Player.Stamina, 1e-3f, "the ignored attempt must not touch Stamina");
        }

        [Test]
        public void LinkedDash_BypassesStillRunningCooldown()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var move = new SimInput { MoveDir = new float2(1f, 0f) };

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // dash #1
            Assert.AreEqual(1, w.Stats.DashesUsed);

            // Let the post-dash window close on its own (PostDashSlideWindow <
            // DashCooldown, TestConfigs.Open()) while DashCooldown keeps
            // counting down — a subsequent slide gated purely on RunUpTimer
            // (QA1 seam below, same idiom as
            // SlideTests.WallStop_KillsSlide_NoLinkWindow) is NOT "linked"
            // (PostDashSlideTimer is 0), so unlike a post-dash-window slide
            // (SlideTests.LinkedSlide_CancelsDashCooldown_...) it must leave
            // DashCooldown running.
            int postDashWindowTicks = (int)math.ceil(
                (cfg.Hero.DashDuration + cfg.Hero.PostDashSlideWindow) / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < postDashWindowTicks; i++) w.Tick(move);
            Assert.AreEqual(0f, w.Player.PostDashSlideTimer, "test setup: post-dash window must have closed");
            Assert.Greater(w.Player.DashCooldown, 0f, "test setup: dash #1's cooldown must still be running");

            var p = w.Player; // QA1 seam: satisfy the run-up gate without a real run-up
            p.RunUpTimer = cfg.Hero.RunUpSeconds;
            p.Vel = new float2(cfg.Hero.MaxSpeed, 0f);
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // run-up slide, NOT linked
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            Assert.Greater(w.Player.DashCooldown, 0f, "a non-linked (run-up) slide must not touch DashCooldown");

            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt);
            for (int i = 0; i < slideTicks; i++) w.Tick(move);
            Assert.Greater(w.Player.LinkWindowTimer, 0f, "test setup: link window must be open");
            Assert.Greater(w.Player.DashCooldown, 0f,
                "test setup: cooldown must still be running — this is what the bypass is for");

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // linked dash
            Assert.AreEqual(2, w.Stats.DashesUsed, "cooldown bypass: the linked dash must have started");
        }

        [Test]
        public void Slide_ViaFullRunUp_NoRefund()
        {
            // A slide started off a full run-up (not via the post-dash
            // window) is not "linked" — it must cost exactly SlideStaminaCost,
            // with LinkRefund never touched (refund NOT applied outside the
            // window it's scoped to).
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; // QA1 seam, same idiom as WallStop_KillsSlide_NoLinkWindow
            p.RunUpTimer = cfg.Hero.RunUpSeconds;
            p.Vel = new float2(cfg.Hero.MaxSpeed, 0f);
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true });
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            Assert.AreEqual(cfg.Hero.StaminaMax - cfg.Hero.SlideStaminaCost, w.Player.Stamina, 1e-3f);
        }

        [Test]
        public void LinkedSlide_RefundsStamina()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var move = new SimInput { MoveDir = new float2(1f, 0f) };

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // dash
            for (int i = 0; i < 10; i++) w.Tick(move); // dash ends, post-dash window opens
            Assert.Greater(w.Player.PostDashSlideTimer, 0f, "test setup: post-dash window must be open");
            float staminaBeforeSlide = w.Player.Stamina;

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // linked slide
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            float expected = staminaBeforeSlide - cfg.Hero.SlideStaminaCost + cfg.Hero.LinkRefund;
            Assert.AreEqual(expected, w.Player.Stamina, 1e-3f);
        }

        [Test]
        public void LinkedSlide_Refund_ClampsAtStaminaMax()
        {
            // Defensive clamp check (HotTweakTests-style out-of-range seed):
            // LinkRefund < SlideStaminaCost is enforced by SimConfigBuilder, so
            // a real cost-then-refund sequence starting within [0, StaminaMax]
            // can never itself push Stamina over the ceiling — this forces an
            // out-of-range starting value through the QA1 seam specifically to
            // prove the refund's own math.min(StaminaMax, ...) clamp holds
            // rather than compounding an already-desynced value further.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var move = new SimInput { MoveDir = new float2(1f, 0f) };

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // dash
            for (int i = 0; i < 10; i++) w.Tick(move); // dash ends, post-dash window opens
            Assert.Greater(w.Player.PostDashSlideTimer, 0f, "test setup: post-dash window must be open");

            var p = w.Player;
            p.Stamina = cfg.Hero.StaminaMax + 1000f; // out-of-range seam value
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // linked slide
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            Assert.AreEqual(cfg.Hero.StaminaMax, w.Player.Stamina, 1e-3f,
                "linked-slide refund must clamp to StaminaMax, not compound an out-of-range value");
        }
    }
}
