using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WaveCadenceTests
    {
        [Test]
        public void SpawnZone_IsSetByTheSpawner_NotByPosition()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            // A MIDDLE-ring mob is placed at a point that geometrically lies
            // in the outer ring: the attribution must follow the spawner,
            // not the coordinate.
            int id = w.SpawnMobForTest(MobType.Chaser,
                new float2(cfg.Arena.Radius - 1f, 0f), Zone.Middle);
            Assert.GreaterOrEqual(id, 0, "моб не заспавнился");
            Assert.AreEqual(Zone.Middle, w.Mobs[w.MobCount - 1].SpawnZone);
        }

        [Test]
        public void ProductionSpawn_HasNoDefaultForZone()
        {
            // Guard Р324: the test seam's convenience must not leak into
            // production.
            var m = typeof(SimulationWorld).GetMethod("SpawnMob",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(m, "SpawnMob не найден");
            var ps = m.GetParameters();
            Assert.AreEqual(3, ps.Length, "у производственного SpawnMob должно быть три параметра");
            Assert.IsFalse(ps[2].HasDefaultValue,
                "зона в производственном SpawnMob обязана быть обязательной");
        }

        [Test]
        public void SaveState_DoesNotAliasTheLiveWaveArray()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, 100);
            WorldSave save = w.SaveState();
            int before = save.Waves[(int)Zone.Outer].PendingTotal;

            WaveState outer = w.WaveRef(Zone.Outer);
            outer.PendingChaser += 99;
            w.SetWaveForTest(Zone.Outer, outer);

            Assert.AreEqual(before, save.Waves[(int)Zone.Outer].PendingTotal,
                "сохранённое состояние алиасит живой массив волн");
        }

        /// The seam Т4's cadence tests need: an emptied ring, so a wave can be
        /// watched clearing. It is deliberately NOT TestWorlds.ClearFirstWave,
        /// which empties the arena by DAMAGING every mob to death — that path
        /// spawns corpses, rolls loot and credits kills, all of which would be
        /// noise in a test about a timer. Hence the container assertion: it is
        /// what tells "taken off the arena" apart from "killed".
        [Test]
        public void ClearMobsForTest_TakesEveryMobOffTheArena_WithoutKillingThem()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, 100);
            int containersBefore = w.ContainerCount;

            Assert.Greater(w.MobCount, 0,
                "премиса: на арене обязаны быть мобы, иначе шов проверяется на пустоте");

            w.ClearMobsForTest();

            Assert.AreEqual(0, w.MobCount, "шов обязан снять с арены КАЖДОГО моба");
            Assert.AreEqual(containersBefore, w.ContainerCount,
                "шов снимает мобов, а не убивает их: ни трупа, ни выпавшего лута");
        }

        [Test]
        public void Snapshot_CarriesTheWorldAggregate_NotTheFirstRing()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            var frame = new RenderSnapshot(in cfg);
            TestWorlds.IdleTicks(w, 100);

            // The rings are DELIBERATELY given different steps and timers: on
            // tick 100 the world itself holds {1,1,1} and something like
            // {58,88,88}, where "the maximum" is indistinguishable from both
            // "the first ring" and "the minimum", and half of the assertions
            // below would be true under any implementation at all.
            WaveState mid = w.WaveRef(Zone.Middle);
            mid.WaveIndex = 5;                    // strictly above outer and core
            mid.PhaseTicks = 3;                   // strictly below the neighboring timers
            w.SetWaveForTest(Zone.Middle, mid);
            w.CaptureSnapshot(frame);

            int sum = w.WaveRef(Zone.Outer).AliveCount + w.WaveRef(Zone.Middle).AliveCount
                + w.WaveRef(Zone.Core).AliveCount;
            Assert.AreEqual(sum, frame.Wave.AliveCount, "агрегат не суммирует живых");
            Assert.AreEqual(5, frame.Wave.WaveIndex,
                "агрегат обязан брать МАКСИМУМ шага по кольцам, а не первое кольцо");
            Assert.AreEqual(3, frame.Wave.PhaseTicks,
                "агрегат обязан брать МИНИМУМ таймера среди незамороженных колец");
            Assert.AreEqual(WavePhase.Active, frame.Wave.Phase,
                "агрегат обязан быть Active, пока активно хоть одно кольцо");
        }
    }
}
