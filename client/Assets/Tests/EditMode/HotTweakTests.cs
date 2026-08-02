using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    public class HotTweakTests
    {
        [Test]
        public void ApplyConfig_ClampsHpDown_KeepsTimersInRange()
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            w.Tick(new SimInput { DashRequested = true }); // активный кулдаун — П-12(а)
            var next = TestConfigs.Default();
            next.Hero.MaxHp = 50f;
            w.ApplyConfig(next);
            Assert.LessOrEqual(w.Player.Hp, 50f);
            Assert.GreaterOrEqual(w.Player.DashCooldown, 0f);
            Assert.LessOrEqual(w.Player.DashCooldown, next.Hero.DashCooldown);
        }

        [Test]
        public void ApplyConfig_SameSequence_SameHash()
        {
            ulong Run()
            {
                var w = new SimulationWorld(9, TestConfigs.Default());
                for (int i = 0; i < 50; i++) w.Tick(default);
                var next = TestConfigs.Default(); next.Hero.MaxSpeed = 9f;
                w.ApplyConfig(next);
                for (int i = 0; i < 50; i++) w.Tick(default);
                return w.StateHash();
            }
            Assert.AreEqual(Run(), Run());
        }

        [Test]
        public void ApplyConfig_ArenaTopologyChange_Throws()
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            var next = TestConfigs.Default();
            next.Arena.Radius = 20f;
            Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(next));
        }
    }
}
