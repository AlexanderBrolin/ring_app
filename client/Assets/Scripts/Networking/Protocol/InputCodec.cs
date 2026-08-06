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
    ///   [6]    AimHeight         — Quantize.Unit(h, cfg.Hero.MaxAimHeight) (byte)
    ///   [7]    flags             — bit0 FireHeld, bit1 DashRequested,
    ///                              bit2 AimHeld, bit3 SlideRequested
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
    /// into a full-speed run: a behavioural change, not a precision loss.
    ///
    /// ZERO MOVEDIR (decision, item 2). At magnitude 0 the angle byte is
    /// whatever `Quantize.Dir(float2.zero)` happens to encode (structurally
    /// the same code as +X, per Quantize.cs's own doc) — nothing here ever
    /// reads that angle back as meaningful, because it is multiplied by the
    /// decoded magnitude. Decode still returns EXACTLY `float2.zero` in
    /// this case: `Quantize.UnitBack(0, 1f)` is exactly `0f`, and any
    /// finite `float2` times exactly `0f` is exactly `(0f, 0f)` under IEEE
    /// 754 — no special-case branch is needed, only the multiplication
    /// itself (pinned directly by InputCodecTests.
    /// MoveDir_Zero_DecodesToExactZero_NotUnitVectorTimesZero).
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
    public static class InputCodec
    {
        public const int SizeBytes = 8;

        const int FireHeldBit = 0;
        const int DashRequestedBit = 1;
        const int AimHeldBit = 2;
        const int SlideRequestedBit = 3;

        public static void Encode(in SimInput input, in SimConfig cfg, System.Span<byte> dst)
        {
            if (dst.Length < SizeBytes)
                throw new System.ArgumentException(
                    $"InputCodec.Encode: dst.Length ({dst.Length}) must be >= SizeBytes ({SizeBytes}).", nameof(dst));

            float magnitude = math.length(input.MoveDir);
            dst[0] = Quantize.Dir(input.MoveDir);
            dst[1] = Quantize.Unit(magnitude, 1f);

            WriteU16(dst, 2, Quantize.Aim(input.AimPoint.x, cfg.Arena.Radius));
            WriteU16(dst, 4, Quantize.Aim(input.AimPoint.y, cfg.Arena.Radius));

            dst[6] = Quantize.Unit(input.AimHeight, cfg.Hero.MaxAimHeight);

            byte flags = 0;
            if (input.FireHeld) flags |= 1 << FireHeldBit;
            if (input.DashRequested) flags |= 1 << DashRequestedBit;
            if (input.AimHeld) flags |= 1 << AimHeldBit;
            if (input.SlideRequested) flags |= 1 << SlideRequestedBit;
            dst[7] = flags;
        }

        public static SimInput Decode(System.ReadOnlySpan<byte> src, in SimConfig cfg)
        {
            if (src.Length < SizeBytes)
                throw new System.ArgumentException(
                    $"InputCodec.Decode: src.Length ({src.Length}) must be >= SizeBytes ({SizeBytes}).", nameof(src));

            float2 dir = Quantize.DirBack(src[0]);
            float magnitude = Quantize.UnitBack(src[1], 1f);

            ushort aimXCode = ReadU16(src, 2);
            ushort aimYCode = ReadU16(src, 4);
            byte flags = src[7];

            return new SimInput
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
            };
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
