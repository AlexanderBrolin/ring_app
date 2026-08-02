using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below
// are required by the file, not just convenience imports.
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    public class AllocationTests
    {
        [Test]
        public void Tick_DoesNotAllocateGC()
        {
            var w = TestWorlds.Saturated(out SimConfig config);
            // Sanity-check the fixture itself before measuring: every mob slot
            // must actually be filled (TestWorlds.Saturated's SpawnMobForTest
            // loop emits one MobSpawned event per mob, still buffered — nothing
            // in the 100-tick warm-up clears events).
            Assert.AreEqual(config.Arena.MaxMobs, TestEvents.CountOf(w, SimEventKind.MobSpawned));
            // F-4 fix-round (ledger T29): the fixture's whole point is a world
            // under sustained fire — its 100-tick hold-fire warm-up must have
            // actually produced live projectiles for the allocation measurement
            // below to be exercising the projectile hot path at all, not just an
            // idle mob crowd.
            Assert.Greater(w.ProjectileCount, 0);

            var input = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f) };
            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++) w.Tick(input);
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
