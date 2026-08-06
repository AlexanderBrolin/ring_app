using Unity.Mathematics;

namespace Ring.Networking.Protocol
{
    /// Stage 2 Task 24 (spec §3.8, Р30/Р34/Р84/Р134): deterministic,
    /// allocation-free codecs between simulation floats and the fixed-width
    /// wire codes carried by ReplicateData/SnapshotBroadcast. Every codec
    /// here is a CLAMPING codec, not a validating one: a hostile or
    /// numerically-drifted input (including NaN/Infinity) must still
    /// produce a LEGAL wire code, because this runs on the write path of
    /// every tick, not in a rejection pipeline. Values outside the declared
    /// range are clamped to the nearest rail, never thrown.
    ///
    /// `radius`/`max` are always PARAMETERS, never constants baked in here
    /// — the actual numbers (ArenaConfig.Radius, HeroConfig.MaxAimHeight,
    /// ...) live in .asset data (spec §0's "two homes for numbers"); this
    /// class only implements the mapping.
    ///
    /// IDEMPOTENCY (Р34) is a contract, not a side effect: `Q(D(q)) == q`
    /// for every representable code `q`, for every method pair below. This
    /// is what makes client-side prediction over the DECODED value (Р34)
    /// match the server bit-for-bit — a second pass through the codec must
    /// return the exact code that produced it, or Task 30's prediction-
    /// parity test cannot hold: the client predicts from the value it will
    /// actually SEND, which is the decode of what it will encode.
    ///
    /// `math.round` is `MidpointRounding.ToEven` (Р134, `math.cs:2618` ->
    /// `System.Math.Round(double)`): a half-integer input rounds to its
    /// EVEN neighbour. ToEven is an odd function, so this rounding is exact
    /// and symmetric around zero — no cell around the origin is wider than
    /// its neighbours, which is what the `Pos`/`PosBack` symmetry test
    /// (QuantizeTests) actually pins down.
    ///
    /// `Unity.Mathematics`' scalar `min`/`max` (and therefore `clamp`/
    /// `saturate`) are NaN-SAFE: `min(x,y)`/`max(x,y)` return the NON-NaN
    /// operand whenever exactly one side is NaN (`math.cs:929`/`1061`).
    /// Concretely this means `saturate(NaN)` resolves to the UPPER bound
    /// (`1.0f`), not to `NaN` — so a NaN input already comes out finite
    /// BEFORE the final round+cast, with no extra gating required beyond
    /// the formulas below. (Task 24 fact-check: this differs from the
    /// task brief's own rationale for why the NaN test is needed — the
    /// brief states `saturate(NaN)` gives `NaN` in this library, which
    /// does not hold for the installed `com.unity.mathematics` build; the
    /// REQUIRED outcome, "NaN clamps to a legal boundary code and never
    /// throws", holds regardless and is exercised by QuantizeTests as
    /// specified.)
    public static class Quantize
    {
        /// Position on one axis, `[-radius, +radius] -> [0, 65535]`. Step at
        /// `radius` is `2*radius/65535`; round-trip error is at most half a
        /// step (Р134 — round-to-nearest never exceeds half its own cell).
        public static ushort Pos(float v, float radius)
        {
            float t = math.saturate((v + radius) / (2f * radius));
            return (ushort)math.round(t * 65535f);
        }

        public static float PosBack(ushort q, float radius)
        {
            return q / 65535f * (2f * radius) - radius;
        }

        /// Aim point on one axis, `[-3*radius, +3*radius] -> [0, 65535]`
        /// (spec §3.8, Р30: `Sanitize` allows `AimPoint` at `2*Radius` from
        /// the player and `AimProvider` casts its ray out to `Radius*2`,
        /// i.e. up to `3*Radius` from the arena centre — a wider range than
        /// `Pos`, sharing its exact shape). Implemented as `Pos` over the
        /// tripled range (rule "reuse, not duplication") rather than a
        /// second copy of the same formula.
        public static ushort Aim(float v, float radius)
        {
            return Pos(v, 3f * radius);
        }

        public static float AimBack(ushort q, float radius)
        {
            return PosBack(q, 3f * radius);
        }

        /// Heading angle -> `[0, 255]`, step `1.40625` deg. `atan2(0, 0)`
        /// is `0` radians by convention (`System.Math.Atan2`'s own
        /// documented special case), so `Dir(float2.zero)` encodes
        /// identically to `Dir(+X)`. That is deliberate, not a defect: on
        /// the wire `MoveDir` is angle + magnitude (Task 25), and at
        /// magnitude 0 nothing ever reads the angle back.
        ///
        /// `atan2`'s range is `(-pi, +pi]`, so the raw code before wrapping
        /// spans `(0, 256]` inclusive of `256` itself (angle exactly `+pi`,
        /// e.g. `Dir(new float2(-1f, 0f))`) — one MORE value than a byte
        /// holds. The cast through `int` before the explicit `& 0xFF` mask
        /// folds that 257th value (`256`) onto `0`, which is exactly the
        /// code the OTHER side of the same seam (`angle` just above `-pi`)
        /// already produces: `+pi` and `-pi` are the same direction, and
        /// without this fold they would decode to two different codes for
        /// it. A raw `(byte)` cast straight from the rounded FLOAT (instead
        /// of `(byte)((int)... & 0xFF)`) does not reliably reproduce this:
        /// C# only defines integer-to-byte narrowing as truncation, not a
        /// float-to-byte cast of an out-of-range value.
        public static byte Dir(float2 v)
        {
            float angle = math.atan2(v.y, v.x);
            int raw = (int)math.round((angle + math.PI) / (2f * math.PI) * 256f);
            return (byte)(raw & 0xFF);
        }

        public static float2 DirBack(byte q)
        {
            float angle = q / 256f * (2f * math.PI) - math.PI;
            return new float2(math.cos(angle), math.sin(angle));
        }

        /// One-sided `[0, max] -> [0, 255]` — all three current consumers
        /// (HP, analog stick magnitude, `AimHeight` in `[0, Hero.
        /// MaxAimHeight]`, Р84) are non-negative, so unlike `Pos`/`Aim`
        /// there is no negative half of the range to spend codes on.
        public static byte Unit(float v, float max)
        {
            float t = math.saturate(v / max);
            return (byte)math.round(t * 255f);
        }

        public static float UnitBack(byte q, float max)
        {
            return q / 255f * max;
        }
    }
}
