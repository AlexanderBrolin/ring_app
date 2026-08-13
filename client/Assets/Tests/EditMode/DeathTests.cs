using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class DeathTests
    {
        static SimulationWorld DeadWorld(out SimConfig c)
        {
            c = TestConfigs.Open();
            var w = new SimulationWorld(2, c);
            w.Tick(default);
            w.KillPlayerForTest();
            return w;
        }

        [Test]
        public void PlayerDied_EmittedOnce_DeathTickRecorded()
        {
            var w = DeadWorld(out _);
            int died = 0;
            for (int e = 0; e < w.EventCount; e++)
                if (w.GetEvent(e).Kind == SimEventKind.PlayerDied) died++;
            Assert.AreEqual(1, died);
            Assert.AreEqual(1, w.Stats.DeathTick);
            w.KillPlayerForTest(); // repeat damage on an already-dead player
            Assert.AreEqual(1, w.Stats.DeathTick);
        }

        [Test]
        public void DeadPlayer_IgnoresInput_WorldKeepsTicking()
        {
            var w = DeadWorld(out _);
            float2 pos = w.Player.Pos;
            float2 aim = w.Player.AimPoint;
            int t0 = w.CurrentTick;
            for (int i = 0; i < 30; i++)
                w.Tick(new SimInput { MoveDir = new float2(1f, 0f), FireHeld = true,
                                      DashRequested = true, AimPoint = new float2(20f, -15f) });
            Assert.AreEqual(t0 + 30, w.CurrentTick);
            Assert.AreEqual(pos, w.Player.Pos);
            Assert.AreEqual(aim, w.Player.AimPoint); // aim also freezes — no reaction to input
            Assert.AreEqual(0, w.Stats.ShotsFired);
            Assert.AreEqual(0, w.Stats.DashesUsed);
        }

        [Test]
        public void StatsFrozen_ProjectileKillAfterDeath_NotCounted()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(2, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(3f, 0f),
                new float2(35f, 0f), 1f, 0f, 100f, 0.12f, 2f);
            w.KillPlayerForTest();
            for (int i = 0; i < 10; i++) w.Tick(default); // projectile arrives and kills
            Assert.AreEqual(0, w.Stats.Kills); // does not count toward the run
        }

        [Test]
        public void DamagePlayer_Death_PlayerDamagedAndPlayerDiedShareSameBlowPos()
        {
            // Fix-round 1 I-1: KillPlayer's extraction briefly dropped the
            // blow's own position for PlayerDied (fell back to the victim's
            // own Pos instead) while the paired PlayerDamaged above it kept
            // the blow's real origin — a damage-caused death must report the
            // SAME Pos on both events, byte-for-byte like before the
            // extraction. A blow position deliberately DIFFERENT from the
            // victim's own Pos (an attacker standing elsewhere, e.g. a
            // Chaser's contact strike — MobAiSystem passes `m.Pos`, not the
            // player's) is what makes a dropped blowPos observable — reusing
            // the victim's own Pos here would not have caught the bug.
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            var blowPos = new float2(123f, 45f); // far from the player's actual Pos (origin)
            // Stage 2 Task 17 signature ripple: victim 0 (the solo player this
            // world has) killed by nobody in particular — ProjectileIds.NoOwner,
            // since this fixture is about the blow POSITION, not about credit.
            w.DamagePlayer(0, ProjectileIds.NoOwner, c.Hero.MaxHp + 1f, blowPos,
                HitZone.Body, new float2(1f, 0f));

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent damaged));
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDied, out SimEvent died));
            Assert.AreEqual(blowPos.x, died.Pos.x, 1e-5f);
            Assert.AreEqual(blowPos.y, died.Pos.y, 1e-5f);
            Assert.AreEqual(damaged.Pos.x, died.Pos.x, 1e-5f);
            Assert.AreEqual(damaged.Pos.y, died.Pos.y, 1e-5f);
        }
    }
}
