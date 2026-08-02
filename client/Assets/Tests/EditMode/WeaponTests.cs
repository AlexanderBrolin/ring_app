using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WeaponTests
    {
        static readonly SimInput Fire = new SimInput
            { AimPoint = new float2(10f, 0f), FireHeld = true };

        [Test]
        public void HoldFire_AverageRpmMatchesInterval()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            for (int i = 0; i < 300; i++) w.Tick(Fire); // 10 s
            // 10 s / 0.12 s = 83.3 -> 83+-1 (fractional remainder carry, not 80 or 90)
            Assert.That(w.Stats.ShotsFired, Is.InRange(82, 84));
        }

        [Test]
        public void Recoil_AccumulatesWhileFiring_DecaysToZeroAfter()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            float peak = 0f;
            for (int i = 0; i < 60; i++)
            {
                w.Tick(Fire);
                peak = math.max(peak, w.Player.RecoilOffset);
            }
            // recoil genuinely accumulates (recovery < accumulation rate), not a phase lottery
            Assert.Greater(peak, cfg.Weapon.RecoilPerShotRad * 2f);
            for (int i = 0; i < 120; i++) w.Tick(default);
            Assert.AreEqual(0f, w.Player.RecoilOffset, 1e-4f);
        }

        [Test]
        public void NoFireWhileDashing_WhenConfigForbids()
        {
            var w = new SimulationWorld(1, TestConfigs.Open()); // CanFireWhileDash=false
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), FireHeld = true,
                                  DashRequested = true, AimPoint = new float2(10f, 0f) });
            Assert.AreEqual(0, w.Stats.ShotsFired);
        }

        [Test]
        public void ProjectileCap_SkipsDeterministically()
        {
            var cfg = TestConfigs.Open();
            cfg.Weapon.ProjectileLifetime = 60f; // projectiles never expire
            cfg.Weapon.FireInterval = 0.001f;    // flood the cap instantly
            static ulong Run(SimConfig c2)
            {
                var w2 = new SimulationWorld(1, c2);
                for (int i = 0; i < 60; i++) w2.Tick(Fire);
                Assert.Greater(w2.Stats.ProjectileSpawnsSkipped, 0);
                return w2.StateHash();
            }
            Assert.AreEqual(Run(cfg), Run(cfg)); // cap degradation is deterministic
        }

        [Test]
        public void FiredEvent_EmittedPerShot()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(Fire); // first shot is instant
            int fired = 0;
            for (int i = 0; i < w.EventCount; i++)
                if (w.GetEvent(i).Kind == SimEventKind.ProjectileFired) fired++;
            Assert.AreEqual(1, fired);
        }

        [Test]
        public void FiredEvent_AmountIsVelocitySimAngle()
        {
            // Amount carries the shot's sim-plane velocity angle (atan2(vel.y, vel.x),
            // Presentation fix-round app-2pl round 2) so MuzzleFlashView can orient the
            // muzzle burst tick-accurately from the event alone, instead of the
            // render-frame's Curr snapshot (wrong during a multi-tick catch-up flush).
            // Zero spread/recoil removes every other source of angle variance, so this
            // isolates exactly the field under test. Diagonal aim (not the shared `Fire`
            // fixture's straight +X, fix-round 3 round 2): a straight +X shot has
            // atan2 == 0, which is indistinguishable from the OLD hardcoded Amount=0f —
            // that scenario can't actually catch a regression back to the hardcoded
            // value. A 45-degree aim gives a non-zero expected angle the old code would
            // fail, making this a genuinely discriminating regression test.
            var cfg = TestConfigs.Open();
            cfg.Weapon.SpreadRad = 0f;
            cfg.Weapon.RecoilPerShotRad = 0f;
            var w = new SimulationWorld(1, cfg);
            var diagonalFire = new SimInput { AimPoint = new float2(10f, 10f), FireHeld = true };
            w.Tick(diagonalFire); // aim (10,10) from Pos (0,0) -> 45 degrees; first shot is instant

            SimEvent fired = default;
            bool found = false;
            for (int i = 0; i < w.EventCount; i++)
            {
                if (w.GetEvent(i).Kind != SimEventKind.ProjectileFired) continue;
                fired = w.GetEvent(i);
                found = true;
                break;
            }
            Assert.IsTrue(found);
            Assert.AreEqual(math.PI / 4f, fired.Amount, 1e-3f);
        }
    }
}
