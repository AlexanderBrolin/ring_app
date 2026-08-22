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
            InvokeInitializeOnce(RingAssembly, "FishNet.Serializing.Generated.GeneratedWriters___Internal");
            InvokeInitializeOnce(RingAssembly, "FishNet.Serializing.Generated.GeneratedReaders___Internal");
            // AND THE PACKAGE'S OWN TABLE, WHICH IS A SEPARATE ONE (Stage 3
            // Т34). The post-processor emits one generated class PER ASSEMBLY,
            // so `Ring.Networking`'s holds the serializers for Ring's own wire
            // structs while the ELEMENT serializers a `WriteArray<T>` resolves
            // through — `GenericWriter<byte>`, `GenericWriter<int>` — are
            // emitted into `FishNet.Runtime`'s. Registering only the first
            // table is enough for a struct of scalars and silently WRONG for
            // one carrying an array: the write logs "Write method not found
            // for System.Byte" and the field comes back null, which a
            // round-trip assertion reads as a protocol defect that is not
            // there. Measured on `MatchResultsNet` and confirmed against
            // `MatchEndedNet.Loot`, which had never been round-tripped here.
            InvokeInitializeOnce(FishNetAssembly, "FishNet.Serializing.Generated.GeneratedWriters___Internal");
            InvokeInitializeOnce(FishNetAssembly, "FishNet.Serializing.Generated.GeneratedReaders___Internal");
        }

        static System.Reflection.Assembly RingAssembly => typeof(InputCodec).Assembly;

        static System.Reflection.Assembly FishNetAssembly => typeof(FishNet.Serializing.Writer).Assembly;

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
            InvokeInitializeOnce(RingAssembly, "FishNet.Serializing.Generated.GeneratedComparers___Internal");
        }

        static void InvokeInitializeOnce(System.Reflection.Assembly assembly, string typeName)
        {
            System.Type t = assembly.GetType(typeName);
            Assert.IsNotNull(t, $"{typeName} must exist in {assembly.GetName().Name} — FishNet's "
                + "IL post-processor creates it for every assembly it processes, and its absence "
                + "means codegen did not run over this assembly at all");
            MethodInfo init = t.GetMethod("InitializeOnce",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(init, $"{typeName}.InitializeOnce must exist");
            init.Invoke(null, null);
        }
    }
}
