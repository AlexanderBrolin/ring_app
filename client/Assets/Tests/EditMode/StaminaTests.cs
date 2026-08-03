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
    }
}
