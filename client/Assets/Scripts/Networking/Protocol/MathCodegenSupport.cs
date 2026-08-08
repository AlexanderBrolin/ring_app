using FishNet.CodeGenerating;
using FishNet.Serializing;
using Ring.Networking.Protocol;
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
    /// It outlived the Stage 2 Task 3 network spike whose crash first exposed the
    /// limitation (deleted in Stage 2 Task 30, this file explicitly kept) and now
    /// hosts the comparer of the production ReplicateData (Stage 2 Task 34).
    /// Nothing in our own code calls the serializers below; the ILPP wires them in
    /// at compile time, so "unused, delete it" is exactly the wrong conclusion.
    /// Line numbers below are inside com.firstgeargames.fishnet 4.7.2 and
    /// com.unity.mathematics 1.3.3.
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
    ///   * Comparers: needed once per [Replicate] data struct, whether or not it
    ///     holds a Unity.Mathematics vector. A vector makes the generated comparer
    ///     a CRASH; without one it is merely emitted, and emission alone fails the
    ///     project's Р110 grep — see WireComparers' own doc for the measured
    ///     evidence. Reconcile data needs none in either sense:
    ///     CreateEqualityComparer has exactly one call site and it is the
    ///     replicate parameter (PredictionProcessor.cs:717-718), which is why the
    ///     full PlayerState carried by ReconcileData (spec 3.9) needs nothing here
    ///     beyond the float2 serializer.
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
    ///
    /// THE ENTRY BELOW EXISTS FOR THE GATE, NOT FOR THE CRASH — and the
    /// distinction is worth stating plainly, because the reasoning that put the
    /// spike's comparer here does NOT apply to this one. ReplicateData (Stage 2
    /// Task 34) is fully quantized: six primitives, no Unity.Mathematics field
    /// anywhere in it, so the unverifiable-IL failure described above cannot
    /// reach it and the generated comparer would have been perfectly valid.
    ///
    /// What the generator does regardless is EMIT. `CreateEqualityComparer` is
    /// unconditional for the replicate parameter (PredictionProcessor.cs:717),
    /// so a build without the entry below is not hypothetical — it was run, and
    /// `strings -a Library/ScriptAssemblies/Ring.Networking.dll | grep
    /// "Comparer___"` answered with three lines:
    ///     Comparer___System.UInt16
    ///     Comparer___Ring.Networking.Protocol.ReplicateData
    ///     Comparer___System.Byte
    /// (the generator recurses into field types, hence the two primitives). The
    /// project's Р110 gate is a mechanical grep that must come back EMPTY, and
    /// those three lines fail it. Declaring the comparer by hand hits the
    /// registered-comparer table at GeneralHelper.cs:1113 and the generator then
    /// emits none of them, which is what keeps the gate an absolute rather than a
    /// judgement call. The same build proved the serializer half: no
    /// `GWrite___Unity`/`GRead___Unity` line appears even though ReconcileData
    /// carries a whole PlayerState full of float2, exactly as MathSerializers'
    /// doc predicts.
    ///
    /// "EMITS NOTHING" IS ABOUT THE COMPARER, NOT ABOUT EVERYTHING (fix-round 1).
    /// `CreateIsDefaultComparer` runs on the very next line
    /// (PredictionProcessor.cs:718) and never consults the custom table, so
    /// `IsDefault___Ring.Networking.Protocol.ReplicateData` IS generated either
    /// way — and with the entry below in place it is generated as a call INTO
    /// it. That is the proof the hand-written comparer sits on the hot path
    /// rather than beside it, and it is also why the gate grep stays honest:
    /// the pattern `Comparer___` matches neither `IsDefault___…` nor the
    /// container class `GeneratedComparers___Internal` (both are in the shipped
    /// assembly right now, and the gate is still empty).
    public static class WireComparers
    {
        /// Equality for ReplicateData (Stage 2 Task 34).
        ///
        /// WHO ACTUALLY CALLS THIS, precisely — because the obvious guess is
        /// wrong (fix-round 1). FishNet never compares two replicate entries
        /// against each other: `PublicPropertyComparer<T>.Compare` is SET by
        /// codegen (GeneralHelper.cs:1380) and read nowhere in the runtime. The
        /// single consumer is `PublicPropertyComparer<T>.IsDefault`
        /// (NetworkBehaviour.Prediction.cs:524, in `Replicate_Authoritative`),
        /// and codegen builds that as `IsDefault(data) => AreEqual(data,
        /// default)` — literally push the argument, push a zeroed local, call
        /// this method (GeneralHelper.cs:1404-1445). Its answer decides
        /// `resetResends`: a non-default input re-arms the redundancy counters,
        /// a default one lets them run down.
        ///
        /// SO THE TICK MUST NOT PARTICIPATE — for that reason, not for a
        /// deduplication that does not exist. `SetDataTick` runs at
        /// NetworkBehaviour.Prediction.cs:561, four lines BEFORE
        /// `isDefaultDel.Invoke` at :565, so by the time this method sees the
        /// data its tick is already `TimeManager.LocalTick`. A comparer that
        /// compared ticks would therefore answer "not default" on every tick of
        /// the match, and the resend counters would never collapse.
        ///
        /// Shape is dictated by the ILPP, not by taste: [CustomComparer],
        /// static, returning bool, exactly two parameters of the compared type
        /// BY VALUE (the generated IsDefault pushes a `default` local with
        /// ldloc, GeneralHelper.cs:1428-1445, so `in`/`ref` would not match) —
        /// all of it spelled out in MathSerializers' doc above.
        [CustomComparer]
        public static bool AreEqual(ReplicateData a, ReplicateData b)
        {
            return a.MoveAngle == b.MoveAngle
                && a.MoveMagnitude == b.MoveMagnitude
                && a.AimX == b.AimX
                && a.AimY == b.AimY
                && a.AimHeight == b.AimHeight
                && a.Flags == b.Flags;
        }
    }
}
