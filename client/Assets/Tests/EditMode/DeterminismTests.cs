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

        [Test]
        public void ZeroSeed_WorldIsAlive()
        {
            // folded seed 0 must be remapped, not fed to the RNG:
            // xorshift with state 0 silently yields zeros forever in player builds.
            var world = new SimulationWorld(0);
            ulong before = world.StateHash();
            world.Tick();
            Assert.AreNotEqual(before, world.StateHash());
            Assert.AreNotEqual(HashAfterTicks(0, Ticks), HashAfterTicks(1, Ticks));
        }

        [Test]
        public void SeedsFoldingToZero_SharePinnedWorld()
        {
            // Documented consequence of the 64->32 fold: 0 and -1 both fold to 0
            // and land on the same remapped seed. Pinned so a guard refactor is loud.
            Assert.AreEqual(HashAfterTicks(0, Ticks), HashAfterTicks(-1, Ticks));
        }

        [Test]
        public void NegativeSeed_IsDeterministicAndAlive()
        {
            Assert.AreEqual(HashAfterTicks(-42, Ticks), HashAfterTicks(-42, Ticks));
            Assert.AreNotEqual(HashAfterTicks(-42, Ticks), HashAfterTicks(42, Ticks));
        }

        [Test]
        public void StateHash64_MatchesFnv1a64GoldenVector()
        {
            // FNV-1a 64 of eight zero bytes, verified against an independent
            // implementation. Pins the algorithm across platforms and refactors.
            Assert.AreEqual(0xA8C7F832281A39C5UL, StateHash64.Add(StateHash64.Begin(), 0UL));
        }
    }
}
