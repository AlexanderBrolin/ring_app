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
            int t0 = w.CurrentTick;
            for (int i = 0; i < 30; i++)
                w.Tick(new SimInput { MoveDir = new float2(1f, 0f), FireHeld = true,
                                      DashRequested = true });
            Assert.AreEqual(t0 + 30, w.CurrentTick);
            Assert.AreEqual(pos, w.Player.Pos);
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
                new float2(35f, 0f), 100f, 0.12f, 2f);
            w.KillPlayerForTest();
            for (int i = 0; i < 10; i++) w.Tick(default); // projectile arrives and kills
            Assert.AreEqual(0, w.Stats.Kills); // does not count toward the run
        }
    }
}
