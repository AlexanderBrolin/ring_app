using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WaveTests
    {
        [Test]
        public void FirstWave_SpawnsAfterDelay_WithBaseCount()
        {
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(c.Wave.BaseCount, snap.MobCount);
            Assert.AreEqual(1, snap.Wave.WaveIndex);
        }

        [Test]
        public void SpawnPositions_RespectRules()
        {
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c);
            // snapshot taken right after the first wave spawns — mobs haven't had a
            // chance to move yet
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.Greater(snap.MobCount, 0);
            for (int m = 0; m < snap.MobCount; m++)
            {
                float2 pos = snap.Mobs[m].Pos;
                Assert.Greater(math.distance(pos, w.Player.Pos),
                    c.Wave.MinSpawnDistanceToPlayer - 1f); // minus up to 2 ticks of movement
                for (int o = 0; o < c.Arena.ObstacleCount; o++)
                    Assert.IsFalse(Geometry.CircleOverlap(pos, 0.4f,
                        c.Arena.ObstaclePos[o], c.Arena.ObstacleRadius[o]));
            }
        }

        [Test]
        public void SameSeed_SameWaveComposition()
        {
            ulong Run(long seed)
            {
                var w = new SimulationWorld(seed, TestConfigs.Default());
                for (int i = 0; i < 400; i++) w.Tick(default);
                return w.StateHash();
            }
            Assert.AreEqual(Run(77), Run(77));
            Assert.AreNotEqual(Run(77), Run(78));
        }

        [Test]
        public void FullyBlockedRing_NoHang_DebtCarriesOver()
        {
            var c = TestConfigs.Open();
            c.Wave.FirstWaveDelay = 0.1f;
            c.Wave.MinSpawnDistanceToPlayer = 100f; // no valid points at all
            var w = new SimulationWorld(11, c);
            for (int i = 0; i < 60; i++) w.Tick(default); // not hanging is already success
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(0, snap.MobCount);
            Assert.Greater(snap.Wave.PendingChasers + snap.Wave.PendingGunners, 0);
            // the debt clears once conditions allow it (spec §3.13 item 5)
            var relaxed = c;
            relaxed.Wave.MinSpawnDistanceToPlayer = 8f;
            w.ApplyConfig(relaxed);
            for (int i = 0; i < 60; i++) w.Tick(default);
            w.CaptureSnapshot(snap);
            Assert.Greater(snap.MobCount, 0);
            Assert.AreEqual(0, snap.Wave.PendingChasers + snap.Wave.PendingGunners);
        }

        [Test]
        public void WaveComposition_FollowsGunnerShare()
        {
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            int gunners = 0;
            for (int m = 0; m < snap.MobCount; m++)
                if (snap.Mobs[m].Type == MobType.Gunner) gunners++;
            // wave 1: count = BaseCount = 4; gunners = round(4 x 0.2) = 1
            Assert.AreEqual(c.Wave.BaseCount, snap.MobCount);
            Assert.AreEqual(1, gunners);
        }

        [Test]
        public void MobCap_SkipsSpawnsDeterministically()
        {
            var c = TestConfigs.Default();
            c.Arena.MaxMobs = 2;
            c.Wave.BaseCount = 6;
            var w = new SimulationWorld(11, c);
            for (int i = 0; i < 200; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.LessOrEqual(snap.MobCount, 2);
            Assert.Greater(w.Stats.MobSpawnsSkipped, 0);
            static ulong Run(SimConfig cc)
            {
                var ww = new SimulationWorld(11, cc);
                for (int i = 0; i < 200; i++) ww.Tick(default);
                return ww.StateHash();
            }
            Assert.AreEqual(Run(c), Run(c)); // deterministic degradation under the cap
        }
    }
}
