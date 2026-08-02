using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class DeterminismTests
    {
        const int Ticks = 1000;

        static ulong HashAfterTicks(long seed, int ticks)
        {
            var world = new SimulationWorld(seed, TestConfigs.Default());
            var idle = default(SimInput);
            for (int i = 0; i < ticks; i++)
                world.Tick(idle);
            return world.StateHash();
        }

        /// Scripted input generator (Task 29 Interfaces) — a separate Random in
        /// TEST code is fine, this is not Simulation. Drives every input axis so
        /// the determinism/golden runs below exercise movement, aiming, firing
        /// and dashing together instead of just idle-input replay.
        static SimInput Scripted(ref Unity.Mathematics.Random rng)
        {
            return new SimInput
            {
                MoveDir = rng.NextFloat2Direction() * rng.NextFloat(),
                AimPoint = rng.NextFloat2(new float2(-30f, -30f), new float2(30f, 30f)),
                FireHeld = rng.NextFloat() < 0.7f,
                DashRequested = rng.NextFloat() < 0.05f
            };
        }

        /// Fixed world seed (42, same as the other tests in this file) driven by
        /// scripted input from an independently-seeded rng — isolates
        /// input-driven determinism from world-seed-driven determinism.
        static ulong RunScripted(uint inputSeed, int ticks)
        {
            var world = new SimulationWorld(42, TestConfigs.Default());
            var rng = new Random(inputSeed);
            for (int i = 0; i < ticks; i++)
                world.Tick(Scripted(ref rng));
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
            var world = new SimulationWorld(42, TestConfigs.Default());
            ulong before = world.StateHash();
            world.Tick(default);
            Assert.AreNotEqual(before, world.StateHash());
        }

        [Test]
        public void ZeroSeed_WorldIsAlive()
        {
            // folded seed 0 must be remapped, not fed to the RNG:
            // xorshift with state 0 silently yields zeros forever in player builds.
            var world = new SimulationWorld(0, TestConfigs.Default());
            ulong before = world.StateHash();
            world.Tick(default);
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

        [Test]
        public void HostileInput_StateStaysFinite_AndDeterministic()
        {
            static ulong Run()
            {
                var w = new SimulationWorld(7, TestConfigs.Default());
                var nan = new SimInput
                {
                    MoveDir = new float2(float.NaN, float.PositiveInfinity),
                    AimPoint = new float2(1e9f, float.NegativeInfinity),
                    FireHeld = true, DashRequested = true
                };
                var tooLong = new SimInput { MoveDir = new float2(100f, -50f) };
                for (int i = 0; i < 50; i++) w.Tick(nan);
                for (int i = 0; i < 50; i++) w.Tick(tooLong); // finite over-length dir
                for (int i = 0; i < 50; i++) w.Tick(default); // zero moveDir
                var p = w.Player;
                Assert.IsTrue(math.all(math.isfinite(p.Pos)) && math.all(math.isfinite(p.Vel)));
                return w.StateHash();
            }
            Assert.AreEqual(Run(), Run()); // two independent worlds, same hash
        }

        [Test]
        public void ScriptedRun_SameSeed_SameHash()
        {
            Assert.AreEqual(RunScripted(123, Ticks), RunScripted(123, Ticks));
            Assert.AreNotEqual(RunScripted(123, Ticks), RunScripted(43, Ticks));
        }

        [Test]
        public void GoldenHash_ScriptedScenario()
        {
            // Pin against a silent simulation-behaviour change (spec §3.13 п.14):
            // world seed 42, scripted input from Random(123), 1000 ticks. First
            // run: the constant below is 0, this assert fails and NUnit prints
            // the actual hash — paste that value in and rerun for a green PASS.
            const ulong GoldenHash = 0x89609433B9F5147BUL; // = 9899074930844439675
            Assert.AreEqual(GoldenHash, RunScripted(123, Ticks));
        }
    }
}
