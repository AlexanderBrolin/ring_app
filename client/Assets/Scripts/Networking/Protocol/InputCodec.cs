using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Protocol
{
    /// Wire codec for one tick of player input (Stage 2 Task 25, spec §3.8,
    /// Р30/Р34/Р84). Packs the fields SimInputSanitizer.Sanitize actually
    /// produces into a fixed 8-byte payload and unpacks them back, using
    /// Quantize (Task 24) for every scalar mapping — no formula lives here
    /// twice.
    ///
    /// WIRE LAYOUT (offsets in bytes; multi-byte fields are little-endian,
    /// written/read by hand instead of via `System.BitConverter` so the
    /// layout does not depend on host endianness):
    ///   [0]    MoveDir angle     — Quantize.Dir (byte)
    ///   [1]    MoveDir magnitude — Quantize.Unit(length, 1f) (byte)
    ///   [2..3] AimPoint.x        — Quantize.Aim (u16, little-endian)
    ///   [4..5] AimPoint.y        — Quantize.Aim (u16, little-endian)
    ///   [6]    AimHeight         — Quantize.Unit(h, cfg.Hero.MaxAimHeight) (byte);
    ///                              non-finite h falls back to cfg.Hero.MuzzleHeight
    ///                              first, mirroring SimInputSanitizer (see below)
    ///   [7]    flags             — bit0 FireHeld, bit1 DashRequested,
    ///                              bit2 AimHeld, bit3 SlideRequested,
    ///                              bit4 InventoryOpen
    /// Total 8 bytes. Spec §3.8 states "9 Б" for this payload — a round-up
    /// with margin, not a contract (task-25-brief §2); the actual layout
    /// above is 8 bytes, and `SizeBytes` — not the spec prose — is the
    /// number tests and callers use.
    ///
    /// ANGLE/MAGNITUDE SPLIT (decision, task-25-brief §2 item 1). MoveDir is
    /// encoded as angle + magnitude rather than as an implicitly-unit
    /// direction: the analog stick can report a PARTIAL deflection, and
    /// SimInputSanitizer.Sanitize only normalizes when `|MoveDir| > 1`
    /// (SimInputSanitizer.cs) — leaving anything below 1 untouched. A codec
    /// that always encoded a unit vector would turn a slow analog approach
    /// into a full-speed run: a behavioral change, not a precision loss.
    ///
    /// ZERO MOVEDIR (decision, item 2). At magnitude 0 the angle byte is
    /// whatever `Quantize.Dir(float2.zero)` happens to encode (structurally
    /// the same code as +X, per Quantize.cs's own doc) — nothing here ever
    /// reads that angle back as meaningful, because it is multiplied by the
    /// decoded magnitude. Decode still returns a vector that COMPARES EQUAL
    /// to `float2.zero`: `Quantize.UnitBack(0, 1f)` is exactly `0f`, and a
    /// finite `float2` times exactly `0f` is a zero of the same sign as each
    /// component (so `-0f` is possible, and `-0f == 0f` holds under IEEE
    /// 754) — no special-case branch is needed, only the multiplication
    /// itself (pinned directly by InputCodecTests.
    /// MoveDir_Zero_DecodesToExactZero_NotUnitVectorTimesZero).
    ///
    /// BYTE-LEVEL IDEMPOTENCY IS NOT UNIVERSAL (fix-round finding F3). For
    /// any input whose magnitude survives quantization the second pass
    /// reproduces byte-identical wire data. It does NOT for a magnitude
    /// below `1/510` (about 0.00196): there the magnitude byte
    /// quantizes to 0, `Decode` returns a zero vector, and re-encoding that
    /// zero yields `Quantize.Dir`'s zero-vector code instead of the original
    /// heading — e.g. `(0, 0.001)` writes angle byte 192 on the first pass
    /// and a different one on the second (0 under Mono; measured, and NOT
    /// pinned to a literal by the test: re-encoding a zero vector runs
    /// `atan2` on SIGNED zeros, whose signs follow `math.cos`'s rounding at
    /// pi/2, so the exact replacement code is a precision detail rather than
    /// a contract — what is invariant is only that it changes). The decoded VALUE is
    /// stable across both passes (zero either way), which is the only thing
    /// prediction parity (Р34) requires; a consumer that ever dedupes or
    /// compares raw bytes — Task 34's redundant input resends are the
    /// candidate — must know that the bytes themselves are not a canonical
    /// form for sub-deadzone input.
    ///
    /// `Tick` IS NOT PART OF THIS PAYLOAD. Spec §3.8 lists it in the
    /// `ReplicateData` format, but it travels through FishNet's own
    /// `IReplicateData.GetTick`/`SetTick` (Task 34), outside `SizeBytes`.
    ///
    /// AIMPOINT IN ARENA-ABSOLUTE COORDINATES (decision, item 3). AimPoint
    /// is encoded with `Quantize.Aim` over `[-3*Radius, +3*Radius]` from the
    /// ARENA CENTRE, matching `Sanitize`'s own domain (Quantize.Aim's own
    /// doc, Р30) — not relative to the sending player. The server's
    /// authoritative `PlayerState.Pos` for a given tick is not guaranteed
    /// to match what the client believed its own position was on that same
    /// tick (that mismatch is exactly what reconciliation, Task 30, exists
    /// to correct), so a player-relative encoding would silently introduce
    /// a second, drifting frame of reference into the wire format.
    ///
    /// DECODED-VALUE SEAM FOR PREDICTION (decision, item 4, Р34). Client
    /// prediction is required to run on the DECODED input, not the raw one
    /// about to be sent — otherwise the client predicts from a value finer
    /// than what the server will ever see, and the two permanently
    /// disagree. This class exposes that seam simply as the ordinary
    /// `Decode(Encode(...))` composition; there is no separate "decode what
    /// I'm about to send" method, because `Decode` already IS that
    /// operation and a second one would duplicate it. Task 30's
    /// prediction-parity test is expected to call `Decode(Encode(raw, cfg),
    /// cfg)` before handing the result to `PlayerPrediction.Step`, exactly
    /// as InputCodecTests.Idempotency_SecondPass_SameBytesAndSameDecodedValue
    /// does below the encode/decode pair it exercises.
    ///
    /// TWO DECODES, ONE FORMAT (Stage 2 Task 34, app-ltw). `Decode` throws
    /// for trusted in-process callers; `TryDecode` refuses, with a `bool`,
    /// for untrusted bytes off the wire. They share one body — the
    /// asymmetry is in the failure contract only, and each method's own doc
    /// says why it is deliberate rather than an inconsistency.
    public static class InputCodec
    {
        public const int SizeBytes = 8;

        const int FireHeldBit = 0;
        const int DashRequestedBit = 1;
        const int AimHeldBit = 2;
        const int SlideRequestedBit = 3;
        // Stage 3 Task 20 (spec §3.8, coordinator: bits 4-7 free, verified by
        // grep against Encode/TryDecode before this task — nothing else in this
        // file has ever set or read them).
        const int InventoryOpenBit = 4;

        public static void Encode(in SimInput input, in SimConfig cfg, System.Span<byte> dst)
        {
            if (dst.Length < SizeBytes)
                throw new System.ArgumentException(
                    $"InputCodec.Encode: dst.Length ({dst.Length}) must be >= SizeBytes ({SizeBytes}).", nameof(dst));

            // NON-FINITE INPUT MIRRORS Sanitize WHEREVER IT CAN (fix-round
            // findings F4 and, for AimHeight, fix-round 2's own correction).
            // Left alone, a NaN reaches Quantize, whose NaN-safe saturate
            // lifts it to the UPPER rail: magnitude 255 (a glitching client
            // decoding to FULL SPEED) and AimHeight at MaxAimHeight (a fully
            // raised aim). SimInputSanitizer gives both fields the OPPOSITE
            // reading — `float2.zero`, i.e. stand still (:19), and
            // `cfg.Hero.MuzzleHeight`, i.e. the standing muzzle line (:31) —
            // and after Task 34 the server never sees a raw input again, so
            // leaving the codec's reading in place would silently invert
            // those rules on the network path only.
            //
            // AimPoint is the one field that genuinely CANNOT be mirrored:
            // its fallback (SimInputSanitizer.cs:22) is `reference.AimPoint`,
            // the sending player's own state, which this codec does not have.
            // It still saturates to a rail — legal input either way, and
            // pinned as deliberate by InputCodecTests.NonFiniteInput_*. (The
            // first version of this comment claimed AimHeight was in the same
            // boat; that was false — `MuzzleHeight` is a plain balance number
            // on the `cfg` this method already takes, and it is read two
            // lines below. Caught by the scoped re-review.)
            //
            // Side effect worth naming (fix-round 2): with NaN stopped here,
            // it never reaches `Quantize.Dir`'s `(int)math.round(NaN)`, whose
            // result C# leaves unspecified and which therefore differed
            // between Mono and IL2CPP. Non-finite input now encodes
            // identically on every platform — a determinism guarantee (CR 2),
            // not just a semantic fix.
            float2 move = math.all(math.isfinite(input.MoveDir)) ? input.MoveDir : float2.zero;
            float aimHeight = math.isfinite(input.AimHeight) ? input.AimHeight : cfg.Hero.MuzzleHeight;

            float magnitude = math.length(move);
            dst[0] = Quantize.Dir(move);
            dst[1] = Quantize.Unit(magnitude, 1f);

            WriteU16(dst, 2, Quantize.Aim(input.AimPoint.x, cfg.Arena.Radius));
            WriteU16(dst, 4, Quantize.Aim(input.AimPoint.y, cfg.Arena.Radius));

            dst[6] = Quantize.Unit(aimHeight, cfg.Hero.MaxAimHeight);

            byte flags = 0;
            if (input.FireHeld) flags |= 1 << FireHeldBit;
            if (input.DashRequested) flags |= 1 << DashRequestedBit;
            if (input.AimHeld) flags |= 1 << AimHeldBit;
            if (input.SlideRequested) flags |= 1 << SlideRequestedBit;
            if (input.InventoryOpen) flags |= 1 << InventoryOpenBit;
            dst[7] = flags;
        }

        /// Decode for a TRUSTED caller — one that allocated `src` itself, in
        /// process, where a short span can only be its own bug. That is why
        /// this one still throws, and why every existing call site keeps
        /// behaving byte for byte as it did (PredictionParityTests and
        /// InputCodecTests both depend on the exception).
        ///
        /// THE NETWORK PATH MUST NOT USE IT — use `TryDecode` (Stage 2 Task
        /// 34, app-ltw, Р82). A truncated datagram is ORDINARY input there:
        /// UDP loss, an MTU cut, a peer of another version, a hostile client.
        /// Left on this method the first such packet would throw out of the
        /// server's own tick. The asymmetry between the two is therefore
        /// deliberate and is the same one `SnapshotReader` documents for the
        /// same reason: an exception is for a programming error, a `bool` is
        /// for untrusted bytes.
        ///
        /// `Encode` has no `TryEncode` twin, and that is not an oversight. Its
        /// destination is always a buffer the CALLER sized — there is no
        /// hostile-input path into it at all — so a short `dst` is exactly the
        /// programming error an exception is for.
        public static SimInput Decode(System.ReadOnlySpan<byte> src, in SimConfig cfg)
        {
            if (!TryDecode(src, in cfg, out SimInput input))
                throw new System.ArgumentException(
                    $"InputCodec.Decode: src.Length ({src.Length}) must be >= SizeBytes ({SizeBytes}).", nameof(src));

            return input;
        }

        /// Decode that REFUSES instead of throwing (Stage 2 Task 34, closes
        /// app-ltw; Р82). Returns false and leaves `input` at `default` for
        /// anything shorter than `SizeBytes` — never a half-decoded value a
        /// caller could mistake for a real input. A longer span is accepted
        /// and its tail ignored, exactly as `Decode` has always done: this
        /// payload is a fixed-size record inside a larger datagram, not the
        /// whole of one.
        ///
        /// The refusal is observable (a `bool`) and deliberately carries no
        /// counter of its own: whoever consumes the network path owns the
        /// statistics for it (Tasks 36/44), the same division of labour
        /// `SnapshotReader` documents for its own per-parse flags.
        public static bool TryDecode(System.ReadOnlySpan<byte> src, in SimConfig cfg, out SimInput input)
        {
            if (src.Length < SizeBytes)
            {
                input = default;
                return false;
            }

            float2 dir = Quantize.DirBack(src[0]);
            float magnitude = Quantize.UnitBack(src[1], 1f);

            ushort aimXCode = ReadU16(src, 2);
            ushort aimYCode = ReadU16(src, 4);
            byte flags = src[7];

            input = new SimInput
            {
                MoveDir = dir * magnitude,
                AimPoint = new float2(
                    Quantize.AimBack(aimXCode, cfg.Arena.Radius),
                    Quantize.AimBack(aimYCode, cfg.Arena.Radius)),
                AimHeight = Quantize.UnitBack(src[6], cfg.Hero.MaxAimHeight),
                FireHeld = (flags & (1 << FireHeldBit)) != 0,
                DashRequested = (flags & (1 << DashRequestedBit)) != 0,
                AimHeld = (flags & (1 << AimHeldBit)) != 0,
                SlideRequested = (flags & (1 << SlideRequestedBit)) != 0,
                InventoryOpen = (flags & (1 << InventoryOpenBit)) != 0,
            };
            return true;
        }

        static void WriteU16(System.Span<byte> dst, int offset, ushort value)
        {
            dst[offset] = (byte)(value & 0xFF);
            dst[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        static ushort ReadU16(System.ReadOnlySpan<byte> src, int offset)
        {
            return (ushort)(src[offset] | (src[offset + 1] << 8));
        }
    }
}
