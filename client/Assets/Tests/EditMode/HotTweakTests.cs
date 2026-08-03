using System.Collections.Generic;
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

        /// Reflective clamp-pass (QC7 — home of ApplyConfig's clamp contract,
        /// not WorldLifecycleTests): every float field of PlayerState is pinned
        /// at 1e6 through the canon test-seam, ApplyConfig runs with reduced
        /// maxima, then each field is checked by reflection against a local
        /// field->ceiling map below. A field with no map entry fails LOUDLY —
        /// that's the point: a newly declared PlayerState float field must get
        /// a line here in the SAME task that adds it, whether ApplyConfig
        /// clamps it (map to that ceiling) or deliberately leaves it alone (map
        /// to float.PositiveInfinity, a documented "not clamped", not an
        /// oversight — see RecoilOffset below, re-clamped every tick by
        /// WeaponSystem against RecoilMaxRad instead of by ApplyConfig).
        /// Populated by Task 9 (Stamina, StaminaRegenDelayTimer). Extended by
        /// Task 10 (SlideTimer et al.), Task 11 (LinkWindowTimer), Task 12
        /// (DashSpeedCur), Task 14 (AimSettleTimer) — add a line here as part
        /// of that task's GREEN step, not as an afterthought.
        [Test]
        public void ApplyConfig_ReflectiveClampPass_EveryFloatFieldWithinNewMax()
        {
            var cfg = TestConfigs.Default();
            var next = TestConfigs.Default();
            next.Hero.MaxHp = 40f;
            next.Hero.DashDuration = 0.05f;
            next.Hero.DashCooldown = 0.4f;
            next.Hero.DashIframes = 0.05f;
            next.Hero.DashBufferWindow = 0.05f;
            next.Hero.StaminaMax = 20f;
            next.Hero.StaminaRegenDelay = 0.2f;
            next.Weapon.FireInterval = 0.04f;

            var ceilingByField = new Dictionary<string, float>
            {
                ["Hp"] = next.Hero.MaxHp,
                ["Stamina"] = next.Hero.StaminaMax,
                ["StaminaRegenDelayTimer"] = next.Hero.StaminaRegenDelay,
                ["DashTimer"] = next.Hero.DashDuration,
                ["DashCooldown"] = next.Hero.DashCooldown,
                ["IframeTimer"] = next.Hero.DashIframes,
                ["DashBufferTimer"] = next.Hero.DashBufferWindow,
                ["FireCooldown"] = next.Weapon.FireInterval,
                ["RecoilOffset"] = float.PositiveInfinity, // clamped by WeaponSystem, not ApplyConfig
            };

            var w = new SimulationWorld(5, cfg);
            object boxedPlayer = w.Player;
            foreach (var field in typeof(PlayerState).GetFields())
            {
                if (field.FieldType != typeof(float)) continue;
                field.SetValue(boxedPlayer, 1e6f);
            }
            w.SetPlayerForTest((PlayerState)boxedPlayer);

            w.ApplyConfig(next);

            foreach (var field in typeof(PlayerState).GetFields())
            {
                if (field.FieldType != typeof(float)) continue;
                Assert.IsTrue(ceilingByField.TryGetValue(field.Name, out float ceiling),
                    $"PlayerState.{field.Name} is a new float field with no clamp-pass " +
                    "entry in ApplyConfig_ReflectiveClampPass_EveryFloatFieldWithinNewMax's " +
                    "ceilingByField map — add a line mapping it to its ApplyConfig ceiling, " +
                    "or to float.PositiveInfinity if ApplyConfig intentionally leaves it unclamped.");
                float actual = (float)field.GetValue(w.Player);
                Assert.LessOrEqual(actual, ceiling,
                    $"PlayerState.{field.Name} exceeded its ApplyConfig ceiling after hot-tweak");
            }
        }
    }
}
