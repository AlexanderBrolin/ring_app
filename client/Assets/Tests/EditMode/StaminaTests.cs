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
        public void LinkedDash_DiscountAndCooldownBypass_ConsumesWindow()
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

            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt);
            for (int i = 0; i < slideTicks; i++) w.Tick(move); // ride the slide out to its natural end
            Assert.AreEqual(0f, w.Player.SlideTimer, "test setup: slide must have ended");
            Assert.Greater(w.Player.LinkWindowTimer, 0f, "test setup: link window must be open");
            Assert.Greater(w.Player.DashCooldown, 0f,
                "test setup: dash #1's cooldown must still be running — this is what the bypass is for");

            float staminaBeforeLinkedDash = w.Player.Stamina;
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // linked dash, inside the window
            Assert.AreEqual(2, w.Stats.DashesUsed, "cooldown bypass: the linked dash must have started");
            Assert.AreEqual(staminaBeforeLinkedDash - cfg.Hero.LinkedDashStaminaCost, w.Player.Stamina, 1e-3f);
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
        public void PerfectChain_CostsExactly_StaminaMax()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var move = new SimInput { MoveDir = new float2(1f, 0f) };

            // Fixture premise (Д5 "exactly two links"): dash + two slides + one
            // linked dash must total exactly the starting stamina pool.
            Assert.AreEqual(cfg.Hero.StaminaMax,
                cfg.Hero.DashStaminaCost + 2f * cfg.Hero.SlideStaminaCost + cfg.Hero.LinkedDashStaminaCost,
                1e-4f, "fixture premise: exactly two links must drain StaminaMax");

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // dash #1
            Assert.AreEqual(1, w.Stats.DashesUsed);
            Assert.AreEqual(cfg.Hero.StaminaMax - cfg.Hero.DashStaminaCost, w.Player.Stamina, 1e-3f);

            for (int i = 0; i < 10; i++) w.Tick(move); // dash ends, post-dash window opens
            Assert.Greater(w.Player.PostDashSlideTimer, 0f, "test setup: post-dash window must be open");

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide #1
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            float afterSlide1 = w.Player.Stamina;
            Assert.AreEqual(cfg.Hero.StaminaMax - cfg.Hero.DashStaminaCost - cfg.Hero.SlideStaminaCost,
                afterSlide1, 1e-3f);

            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt);
            for (int i = 0; i < slideTicks; i++) w.Tick(move); // ride slide #1 out to its natural end
            Assert.AreEqual(0f, w.Player.SlideTimer, "test setup: slide #1 must have ended");
            Assert.Greater(w.Player.LinkWindowTimer, 0f, "test setup: link window must be open");

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true }); // linked dash, inside the window
            Assert.AreEqual(2, w.Stats.DashesUsed);
            Assert.AreEqual(afterSlide1 - cfg.Hero.LinkedDashStaminaCost, w.Player.Stamina, 1e-3f);
            Assert.AreEqual(0f, w.Player.LinkWindowTimer, "window must be consumed by the linked dash");

            for (int i = 0; i < 10; i++) w.Tick(move); // linked dash ends, its own post-dash window opens
            Assert.Greater(w.Player.PostDashSlideTimer, 0f, "test setup: second post-dash window must be open");

            float beforeSlide2 = w.Player.Stamina;
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide #2
            Assert.AreEqual(2, w.Stats.SlidesUsed);
            Assert.AreEqual(beforeSlide2 - cfg.Hero.SlideStaminaCost, w.Player.Stamina, 1e-3f);

            // Total spent across the whole chain equals exactly StaminaMax (Д5):
            // regen never gets a chance to run mid-chain — every dash/slide start
            // re-primes StaminaRegenDelayTimer well before it could elapse, and
            // SlideTimer itself blocks regen while a slide is active (QD10) — so
            // this is pure arithmetic, not a coincidence of timing.
            Assert.AreEqual(0f, w.Player.Stamina, 1e-3f);
        }
    }
}
