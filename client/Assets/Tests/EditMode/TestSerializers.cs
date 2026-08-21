using System.Reflection;
using NUnit.Framework;
using Ring.Networking.Protocol;

namespace Ring.Simulation.Tests
{
    /// Shared fixture support for tests that put a wire struct through
    /// FishNet's own generated serializers (Stage 2 Task 34, extended in
    /// Stage 3 Т28).
    ///
    /// WHY IT HAS TO BE DRIVEN BY HAND. The generated serializer table is
    /// filled from a `[RuntimeInitializeOnLoadMethod]` — FishNet's own
    /// `WriterProcessor.CreateGeneratedWritersClass` calls
    /// `CreateRuntimeInitializeOnLoadMethodAttribute` with no load type, so
    /// the default `AfterSceneLoad`. EditMode tests never enter play mode and
    /// therefore never trigger it, so this calls the very same entry point
    /// directly. Reflection is required because the generated classes are
    /// internal to `Ring.Networking`.
    ///
    /// EXTRACTED HERE WHEN THE SECOND CALLER ARRIVED (Т28, rule 2), the same
    /// way `SimulationWorld.IndexOfContainer` was extracted at Т17: the first
    /// caller is `ReconcileCodecTests.ReconcileData_SurvivesTheFishNetWireRoundTrip`,
    /// the second is `LootProtocolTests`'s round trips of `LootRequestNet`
    /// and `LootResultNet`. A second copy would drift the moment FishNet
    /// renames either generated class, and both copies would then be wrong in
    /// the same silent way — a test that passes because nothing was checked.
    public static class TestSerializers
    {
        /// Registers FishNet's generated writers and readers for
        /// `Ring.Networking`. Idempotent — `InitializeOnce` is the package's
        /// own name for that property.
        public static void EnsureRegistered()
        {
            InvokeInitializeOnce("FishNet.Serializing.Generated.GeneratedWriters___Internal");
            InvokeInitializeOnce("FishNet.Serializing.Generated.GeneratedReaders___Internal");
        }

        /// The comparer table, filled from its own generated class and asked
        /// about by FishNet's prediction path rather than by a writer —
        /// `ReconcileCodecTests`'s own comparer test is its one caller. A
        /// separate entry point rather
        /// than a third name inside `EnsureRegistered`, because the two
        /// questions are asked by different tests for different reasons and
        /// registering a table nobody is about to read would hide which of
        /// them a failure belongs to.
        public static void EnsureComparersRegistered()
        {
            InvokeInitializeOnce("FishNet.Serializing.Generated.GeneratedComparers___Internal");
        }

        static void InvokeInitializeOnce(string typeName)
        {
            System.Type t = typeof(InputCodec).Assembly.GetType(typeName);
            Assert.IsNotNull(t, $"{typeName} must exist in Ring.Networking — FishNet's IL "
                + "post-processor creates it for every assembly it processes, and its absence "
                + "means codegen did not run over this assembly at all");
            MethodInfo init = t.GetMethod("InitializeOnce",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(init, $"{typeName}.InitializeOnce must exist");
            init.Invoke(null, null);
        }
    }
}
