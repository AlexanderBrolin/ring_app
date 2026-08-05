using FishNet.CodeGenerating;
using FishNet.Serializing;
using Ring.Networking.Spike;
using Unity.Mathematics;

namespace Ring.Networking
{
    /// Hand-written serializers for Unity.Mathematics types and hand-written
    /// equality comparers for prediction wire structs. Both exist for one reason:
    /// FishNet 4.7.2's IL post-processor produces broken code for any wire struct
    /// that carries a Unity.Mathematics vector, and the package cannot be patched
    /// (it lives in Library/PackageCache and is restored from the UPM pin on every
    /// resolve).
    ///
    /// THIS FILE IS PERMANENT — it is a package limitation, not spike scaffolding.
    /// It outlives Networking/Spike/** (deleted in T30) and hosts the comparer of
    /// the production ReplicateData from T34 on. Nothing in our code calls any of
    /// these methods; the ILPP wires them in at compile time, so "unused, delete
    /// it" is exactly the wrong conclusion. Line numbers below are inside
    /// com.firstgeargames.fishnet 4.7.2 and com.unity.mathematics 1.3.3.
    ///
    /// THE COMMON CAUSE. WriterProcessor.cs:82-86 lists assembly prefixes whose
    /// types must be treated as opaque, and means to name Unity.Mathematics — but
    /// spells it "Unity.Mathmatics", without the 'e'. The filter therefore never
    /// matches, and the generator walks float2 as if it were one of our own
    /// structs: two float fields plus every public get/set property, which for
    /// float2 means the swizzles xy and yx (both of type float2) and the indexer
    /// this[int]. What comes out is broken in three different ways, verified by
    /// disassembling Library/ScriptAssemblies/Ring.Networking.dll before this file
    /// existed:
    ///   1. the generated float2 writer and reader call THEMSELVES for xy and yx
    ///      (the type is registered before its body is filled,
    ///      WriterProcessor.cs:834-835) — unbounded recursion on the first packet;
    ///   2. the generated float2 comparer compares xy/yx with float2.op_Equality,
    ///      which returns bool2, not bool (float2.gen.cs:449), and then branches on
    ///      it with brfalse — unverifiable IL;
    ///   3. the indexer is emitted with no index argument at all.
    /// Failure 2 also hits every struct that merely HAS a float2 field, because
    /// FinishTypeReferenceCompare (GeneralHelper.cs:1297-1336) picks op_Equality
    /// for any field type that implements IEquatable of itself, and float2 does.
    /// That is the crash the spike hit on its first tick: InvalidProgramException
    /// in FishNet.Serializing.Generated.GeneratedComparers___Internal, method
    /// Comparer___(replicate data), thrown from Replicate_Current, which kills the
    /// whole tick pipeline — OnPostTick stops running and nothing is ever sent.
    ///
    /// WHY THE COMPARER IS DECLARED ON THE WIRE STRUCT AND NOT ON float2.
    /// FinishTypeReferenceCompare never consults the registered-comparer table; it
    /// even throws away the comparer it just requested one line above (:1262,
    /// :1278). A [CustomComparer] for float2 would be dead code. Declaring it for
    /// the struct that CONTAINS the float2 is what works: that hits the table
    /// lookup at GeneralHelper.cs:1113 and the generator emits nothing at all.
    /// Serializers are the opposite case — there the table IS consulted
    /// (GetOrCreateWriteMethodReference, WriterProcessor.cs:368-373), so declaring
    /// float2 once covers every struct that contains one.
    ///
    /// SCOPE OF THE WORKAROUND.
    ///   * Serializers: needed once per Unity.Mathematics type that reaches the
    ///     wire. Today that is float2 only — PlayerState, SimInput and the spike's
    ///     wire structs use no other vector type. FishNet does ship Writefloat2 /
    ///     Readfloat2 (Runtime/Serializing/UnityMathmatics/
    ///     Serializers.UnityMathmaticsFloat.cs) but does not mark them
    ///     [DefaultWriter]/[DefaultReader], so codegen never finds them
    ///     (FindInstancedWriters, WriterProcessor.cs:116-141). We reuse the bodies
    ///     rather than re-implement them.
    ///   * Comparers: needed once per [Replicate] data struct that holds a
    ///     Unity.Mathematics vector, directly or through a nested struct. Reconcile
    ///     data needs none — CreateEqualityComparer has exactly one call site and
    ///     it is the replicate parameter (PredictionProcessor.cs:717-718) — which
    ///     is why the full PlayerState carried by ReconcileData (spec 3.9) needs
    ///     nothing here beyond the float2 serializer.
    ///
    /// SHAPE REQUIRED BY THE ILPP. Everything below is registered before any code
    /// is generated (FishNetILPP.cs:78 and :80, both ahead of :86).
    ///   * Serializers (CustomSerializerProcessor.cs:271-320): extension methods on
    ///     exactly FishNet.Serializing.Writer / .Reader, named with a "Write" /
    ///     "Read" prefix, the writer returning void and taking the value second,
    ///     the reader returning the value type.
    ///   * Comparers (CustomSerializerProcessor.cs:104-148): marked
    ///     [CustomComparer], returning bool, exactly two parameters of the compared
    ///     type BY VALUE — the generated IsDefault pushes a `default` local with
    ///     ldloc (GeneralHelper.cs:1428-1445), so in/ref would not match — and
    ///     static, because the delegate is built with `ldnull; ldftn` (:1370-1371).
    ///   * Both must sit on a top-level static type of THIS assembly: the registry
    ///     lives in the per-assembly CodegenSession and the ILPP iterates
    ///     session.Module.Types only (FishNetILPP.cs:171-220), so a declaration
    ///     covers just the assembly that declares it. Ring.Networking is the
    ///     correct one — it owns every wire struct, and the ILPP never touches
    ///     Ring.Simulation at all (WillProcess demands a FishNet.Runtime reference,
    ///     FishNetILPP.cs:24-47; Simulation.asmdef has none).
    public static class MathSerializers
    {
        /// Bodies are FishNet's own; only the registration is ours.
        public static void WriteFloat2(this Writer writer, float2 value) => writer.Writefloat2(value);

        public static float2 ReadFloat2(this Reader reader) => reader.Readfloat2();
    }

    /// Equality comparers for [Replicate] data. See MathSerializers above for why
    /// this file exists and which struct needs an entry here.
    public static class WireComparers
    {
        /// TEMPORARY member of a permanent class (T3 -> T30): it dies together with
        /// Networking/Spike/**, and T34's ReplicateData comparer takes its place.
        /// Mirrors the member set the generator would have used — public fields
        /// only. SpikeReplicateData._tick is private and stays out on purpose:
        /// FishNet stamps the tick before calling IsDefault
        /// (ReplicateDataContainer.cs:60-64, NetworkBehaviour.Prediction.cs:561-566),
        /// so comparing it would make IsDefault permanently false and input resends
        /// would never throttle.
        [CustomComparer]
        public static bool CompareSpikeReplicateData(
            SpikePlayerController.SpikeReplicateData a,
            SpikePlayerController.SpikeReplicateData b)
            => a.MoveDir.Equals(b.MoveDir)
               && a.AimPoint.Equals(b.AimPoint)
               && a.AimHeight == b.AimHeight
               && a.FireHeld == b.FireHeld
               && a.DashRequested == b.DashRequested
               && a.AimHeld == b.AimHeld
               && a.SlideRequested == b.SlideRequested;
    }
}
