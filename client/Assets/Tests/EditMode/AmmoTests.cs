using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 2 (spec Р261/Р225): the ammo economy — the shot counter,
    /// its shared spend across the server (Update) and prediction
    /// (AdvanceNoSpawn) paths, and the emergency-synthesis interval once the
    /// magazine runs dry. TestConfigs.Open() is the base fixture throughout
    /// (same reasoning as PredictionParityTests' own doc: waves pushed out of
    /// reach, no obstacle/wall in the way of the fixed +X aim line).
    public class AmmoTests
    {
        [Test]
        public void StartsWithConfiguredAmmo()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            Assert.AreEqual(cfg.Weapon.AmmoStart, w.Player.Ammo);
        }

        [Test]
        public void EveryShotSpendsExactlyOne()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int before = w.Player.Ammo;
            var input = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
            int shots = 0;
            for (int t = 0; t < 30; t++)
            {
                bool fires = WeaponSystem.WouldFireThisTick(w.Player, input, cfg.Weapon);
                w.Tick(input);
                if (fires) shots++;
            }
            Assert.Greater(shots, 0);
            Assert.AreEqual(before - shots, w.Player.Ammo);
        }

        [Test]
        public void AtZero_FiresOnEmergencyInterval_AndSpendsNothing()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; p.Ammo = 0; w.SetPlayerForTest(p);
            var input = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
            int fired = 0;
            // errata E-4/A-C2: `+2` gives TWO shots (t=0 and t=37 at V=1.25);
            // `-1` gives exactly one — the plan body's arithmetic was the bug.
            int ticks = (int)math.ceil(cfg.Weapon.EmergencyFireInterval / SimulationWorld.TickDt) - 1;
            for (int t = 0; t < ticks; t++)
            {
                if (WeaponSystem.WouldFireThisTick(w.Player, input, cfg.Weapon)) fired++;
                w.Tick(input);
            }
            Assert.AreEqual(1, fired, "emergency mode: exactly one shot per interval");
            Assert.AreEqual(0, w.Player.Ammo);
        }

        [Test]
        public void LastRound_UsesNormalInterval_NextOneIsEmergency()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; p.Ammo = 1; p.FireCooldown = 0f; w.SetPlayerForTest(p);
            Assert.AreEqual(cfg.Weapon.FireInterval, WeaponSystem.IntervalFor(w.Player, cfg.Weapon), 1e-6f);
            w.Tick(new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) });
            Assert.AreEqual(0, w.Player.Ammo);
            Assert.AreEqual(cfg.Weapon.EmergencyFireInterval, WeaponSystem.IntervalFor(w.Player, cfg.Weapon), 1e-6f);
        }

        [Test]
        public void RefillClampsEmergencyCooldownDown()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; p.Ammo = 0; p.FireCooldown = cfg.Weapon.EmergencyFireInterval;
            w.SetPlayerForTest(p);
            w.AddAmmoForTest(0, cfg.Weapon.ShotsPerCell);
            Assert.LessOrEqual(w.Player.FireCooldown, cfg.Weapon.FireInterval);
        }

        /// Not in the plan's own Step 1 snippet — added here because
        /// SimulationWorld.AddAmmoForTest/WeaponSystem.AddAmmo's AmmoMax cap
        /// (added to satisfy CR 1, "no half-measures": a refill seam that could
        /// silently overflow the magazine ceiling ApplyConfig otherwise enforces
        /// would be an untested new behavior) is otherwise pinned by nothing —
        /// errata E-6 D-I19 counts exactly this "cap and clamp" pairing among
        /// Т2/Т3's three ammo tests.
        [Test]
        public void RefillCapsAtAmmoMax()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player; p.Ammo = cfg.Weapon.AmmoMax - 1; w.SetPlayerForTest(p);
            w.AddAmmoForTest(0, cfg.Weapon.ShotsPerCell);
            Assert.AreEqual(cfg.Weapon.AmmoMax, w.Player.Ammo);
        }
    }
}
