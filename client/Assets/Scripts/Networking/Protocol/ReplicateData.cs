using FishNet.Object.Prediction;
using Ring.Simulation.Core;

namespace Ring.Networking.Protocol
{
    /// One tick of player input on its way UP to the server (Stage 2 Task 34,
    /// spec §3.8/§3.9, Р34/Р143). FishNet carries it through the prediction
    /// pipeline: the owner builds one per tick, the server runs it, and every
    /// resimulation replays the very same value.
    ///
    /// FULLY QUANTIZED, EIGHT BYTES, NO Unity.Mathematics. The public fields
    /// below ARE the `InputCodec` wire layout, field for byte
    /// (InputCodec.cs's own layout doc is the one normative statement of it):
    /// angle, magnitude, aim x, aim y, aim height, flags — 8 B of payload,
    /// pinned against `InputCodec.SizeBytes` by
    /// `ReconcileCodecTests.ReplicateData_PayloadIsEightBytes` rather than by
    /// a second literal. `Tick` rides OUTSIDE those bytes, in FishNet's own
    /// `GetTick`/`SetTick` (Р143).
    ///
    /// Two consequences follow from quantizing here rather than at the send:
    ///   * Р34 becomes STRUCTURAL. Quantization happens exactly once, when
    ///     this struct is built, and both sides then decode the SAME bytes —
    ///     so a client cannot predict from a finer value than the one it
    ///     sends even by accident. It is no longer a discipline the caller
    ///     has to keep.
    ///   * The Р110 crash cannot reach this struct at all. FishNet 4.7.2's
    ///     IL post-processor emits unverifiable IL for any replicate struct
    ///     carrying a `float2` (see MathCodegenSupport's class doc); with
    ///     nothing but primitives here there is no such field to trip over.
    ///     A comparer is still declared in `WireComparers`, for a different
    ///     reason entirely — see it.
    ///
    /// FISHNET'S IDLE-INPUT SUPPRESSION WILL NEVER ENGAGE FOR THIS FORMAT, and
    /// Task 36's bandwidth measurement on milestone В2 needs to know it up
    /// front (fix-round 1). The package decides whether to let its redundancy
    /// counters run down by asking `IsDefault(data)`, which codegen builds as
    /// `AreEqual(data, default)` (WireComparers' own doc) — i.e. "are all six
    /// payload fields zero". Standing perfectly still is not that: `AimX`/`AimY`
    /// are `Quantize.Aim` codes, and they are zero only at the very bottom of
    /// the aim domain. So the owner sends a replicate EVERY tick for the whole
    /// match — 8 B x 30 Hz plus FishNet's redundancy, which is what §3.8's
    /// uplink budget already assumes, but not because idle frames were
    /// collapsed. The mirror image is the curiosity worth knowing: the ONE
    /// input that does read as default — aim at `(-3R, -3R)`, height 0, no
    /// flags, no movement — is a perfectly legal input, and it is the one that
    /// WOULD have its resends suppressed. Neither effect is worth code: the
    /// first costs nothing we had not budgeted, and the second is a corner of
    /// the arena nobody aims at.
    ///
    /// THE TICK FIELD'S SHAPE IS A CODEGEN CONTRACT, not a style choice.
    /// `PredictionProcessor.TickFieldIsNonSerializable` (:383-416) walks
    /// `GetTick`'s IL looking for a plain `ldfld` and then rejects the field
    /// unless it is private or protected — a computed tick or a public one is
    /// a compile-time failure of the IL post-processor. Kept honest from the
    /// outside by
    /// `ReconcileCodecTests.PredictionData_TickFieldIsAPrivateUint_AsTheCodegenDemands`.
    public struct ReplicateData : IReplicateData
    {
        uint _tick;

        /// Byte 0 of the codec payload — `Quantize.Dir` of MoveDir.
        public byte MoveAngle;
        /// Byte 1 — MoveDir's magnitude, `Quantize.Unit(length, 1)`. Split
        /// from the angle because a partial analog deflection is a slower
        /// walk, not a rounder heading (InputCodec's own doc).
        public byte MoveMagnitude;
        /// Bytes 2..3 — AimPoint.x, `Quantize.Aim`, arena-absolute.
        public ushort AimX;
        /// Bytes 4..5 — AimPoint.y.
        public ushort AimY;
        /// Byte 6 — `Quantize.Unit(AimHeight, Hero.MaxAimHeight)`.
        public byte AimHeight;
        /// Byte 7 — FIVE FLAGS AND A NUMBER, which is why the field's name is
        /// the narrower half of what it holds (review finding B-4). Bits 0-4
        /// are FireHeld / DashRequested / AimHeld / SlideRequested /
        /// InventoryOpen (Stage 3 Task 20); bits 5-7 carry
        /// `SimInput.RewindTicks` as a 3-bit unsigned NUMBER — how far into
        /// the past the server must look at its targets to judge this shot
        /// (app-88jb Т26, spec §3.6/§3.7). `InputCodec`'s layout doc stays the
        /// one normative statement of both halves, including why the writer
        /// saturates instead of masking and why the eighth value reads back
        /// as 6.
        public byte Flags;

        public uint GetTick() => _tick;

        public void SetTick(uint value) => _tick = value;

        /// Nothing to release: a struct of six primitives, exactly like the
        /// package's own demo data. Present because `IReplicateData` requires
        /// it, not because there is anything to clean up.
        public void Dispose() { }

        /// Quantizes one raw input into the wire form, THROUGH THE CODEC.
        ///
        /// Not one quantization formula lives in this file: `InputCodec` owns
        /// every scalar mapping (and the non-finite handling that mirrors
        /// `SimInputSanitizer`), and this method only spreads the bytes it
        /// produced across the fields above. A second copy of `Quantize.Aim`'s
        /// domain here would be a second wire format nobody declared.
        public static ReplicateData FromInput(in SimInput input, in SimConfig cfg)
        {
            System.Span<byte> wire = stackalloc byte[InputCodec.SizeBytes];
            InputCodec.Encode(in input, in cfg, wire);
            return new ReplicateData
            {
                MoveAngle = wire[0],
                MoveMagnitude = wire[1],
                AimX = (ushort)(wire[2] | (wire[3] << 8)),
                AimY = (ushort)(wire[4] | (wire[5] << 8)),
                AimHeight = wire[6],
                Flags = wire[7],
            };
        }

        /// The inverse, through `TryDecode` rather than `Decode`: this is the
        /// SERVER-side path, and what arrives there is whatever a client sent
        /// (Р82, app-ltw). It cannot in fact refuse today — the writer below
        /// always fills all `SizeBytes` — and it is written as a `Try` anyway
        /// so that a future layout change cannot turn "too few bytes" into a
        /// throw inside the tick pipeline. Callers must honor the `false`.
        public bool TryToInput(in SimConfig cfg, out SimInput input)
        {
            System.Span<byte> wire = stackalloc byte[InputCodec.SizeBytes];
            wire[0] = MoveAngle;
            wire[1] = MoveMagnitude;
            wire[2] = (byte)(AimX & 0xFF);
            wire[3] = (byte)((AimX >> 8) & 0xFF);
            wire[4] = (byte)(AimY & 0xFF);
            wire[5] = (byte)((AimY >> 8) & 0xFF);
            wire[6] = AimHeight;
            wire[7] = Flags;
            return InputCodec.TryDecode(wire, in cfg, out input);
        }
    }
}
