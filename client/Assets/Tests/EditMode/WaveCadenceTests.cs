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
    }
}
