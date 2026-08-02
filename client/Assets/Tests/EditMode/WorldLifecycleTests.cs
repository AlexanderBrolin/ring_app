using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    public class WorldLifecycleTests
    {
        [Test]
        public void SaveRestore_ReplaysToSameHash()
        {
            var w = new SimulationWorld(42, TestConfigs.Default());
            var input = new SimInput { FireHeld = true };
            for (int i = 0; i < 100; i++) w.Tick(input);
            WorldSave save = w.SaveState();
            for (int i = 0; i < 500; i++) w.Tick(input);
            ulong straight = w.StateHash();
            w.RestoreState(save);
            for (int i = 0; i < 500; i++) w.Tick(input);
            Assert.AreEqual(straight, w.StateHash());
        }

        [Test]
        public void TwoWorldsSameSeed_NoStaticState()
        {
            ulong a = Run(42); ulong b = Run(42);
            Assert.AreEqual(a, b);
            static ulong Run(long seed)
            {
                var w = new SimulationWorld(seed, TestConfigs.Default());
                for (int i = 0; i < 300; i++) w.Tick(default);
                return w.StateHash();
            }
        }

        [Test]
        public void EveryPlayerAndStatsFieldAffectsHash() // спека §3.13 п.12 / §3.3
        {
            var w = new SimulationWorld(3, TestConfigs.Default());
            w.Tick(default);
            WorldSave save = w.SaveState();
            ulong baseline = w.StateHash();
            foreach (var field in typeof(PlayerState).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.Player;
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetPlayerForTest((PlayerState)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"PlayerState.{field.Name} не в хеше");
            }
            foreach (var field in typeof(MatchStats).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.Stats;
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetStatsForTest((MatchStats)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"MatchStats.{field.Name} не в хеше");
            }
            // аналогичные проходы для MobState/ProjectileState/WaveState добавляются
            // в Task 16/22 швами SetMobForTest/SetProjectileForTest/SetWaveForTest
        }

        static object Bump(object v) => v switch
        {
            float f => f + 1f,
            int i => i + 1,
            bool b => !b,
            Unity.Mathematics.float2 f2 => f2 + new Unity.Mathematics.float2(1f, 0f),
            _ => throw new System.NotSupportedException(v.GetType().Name)
        };

        [Test]
        public void Snapshot_CopiesPlayerAndCounts()
        {
            var cfg = TestConfigs.Default();
            var w = new SimulationWorld(5, cfg);
            w.Tick(default);
            var snap = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(w.CurrentTick, snap.Tick);
            Assert.AreEqual(w.Player.Pos, snap.Player.Pos);
            Assert.AreEqual(0, snap.MobCount);
        }
    }
}
