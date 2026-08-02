using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    public class DeterminismTests
    {
        const int Ticks = 1000;

        static ulong HashAfterTicks(long seed, int ticks)
        {
            var world = new SimulationWorld(seed);
            for (int i = 0; i < ticks; i++)
                world.Tick();
            return world.StateHash();
        }

        [Test]
        public void SameSeed_SameHash_After1000Ticks()
        {
            Assert.AreEqual(HashAfterTicks(42, Ticks), HashAfterTicks(42, Ticks));
        }

        [Test]
        public void DifferentSeed_DifferentHash()
        {
            Assert.AreNotEqual(HashAfterTicks(42, Ticks), HashAfterTicks(43, Ticks));
        }

        [Test]
        public void HashChangesBetweenTicks()
        {
            var world = new SimulationWorld(42);
            ulong before = world.StateHash();
            world.Tick();
            Assert.AreNotEqual(before, world.StateHash());
        }
    }
}
