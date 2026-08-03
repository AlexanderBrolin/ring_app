using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class ProjectileHeightTests
    {
        [Test]
        public void Projectile_WithVelZ_AdvancesHeightPerTick()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.SpawnProjectileForTest(ProjectileOwner.Player,
                new float2(0f, 0f), new float2(10f, 0f),
                height: 1f, velZ: -3f, damage: 1f, radius: 0.1f, ttl: 5f);
            w.Tick(new SimInput());
            var p = w.GetProjectileForTest(0);
            Assert.AreEqual(1f - 3f * SimulationWorld.TickDt, p.Height, 1e-5f);
            Assert.AreEqual(1f, p.PrevHeight, 1e-5f);
        }
    }
}
