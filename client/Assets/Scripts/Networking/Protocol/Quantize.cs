using Ring.Simulation.Core;
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
    /// ⚠ FOUR OF THE EIGHT BODIES LIVE IN Simulation.Core.ByteCodecs SINCE
    /// app-94sk T6a -- `Dir`/`DirBack` and `Unit`/`UnitBack` are forwarders
    /// here, because the pose key packs the same bytes and Ring.Simulation
    /// cannot see this assembly. Everything this doc says about clamping,
    /// NaN-safe `saturate`, idempotency and round-to-even describes those
    /// four bodies exactly as it describes `Pos`/`Aim`, and QuantizeTests
    /// still pins all eight through these names.
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
    /// EVEN neighbor. ToEven is an odd function, so the mapping stays
    /// symmetric around zero and no cell around the origin is wider than
    /// its neighbors. Fix-round honesty note (F4): the QuantizeTests
    /// symmetry test does NOT distinguish ToEven from half-up — the whole
    /// suite stays green under either, because no test value lands on
    /// `k + 0.5` with an even `k`. ToEven is a documented FACT about the
    /// library this code sits on, not an invariant these tests pin; if a
    /// future change ever needs it pinned, that needs its own test.
    ///
    /// DEGENERATE PARAMETERS: `radius == 0` (and `max == 0`) make the
    /// mapping meaningless — the division by zero sends every input to a
    /// rail (`v == 0` gives `0/0 = NaN`, which NaN-safe saturate lifts to
    /// the UPPER rail; a negative `v` gives `-Infinity`, which saturates to
    /// the LOWER one — measured, not assumed: `Pos(-5, 0) == 0`,
    /// `Pos(0, 0) == Pos(5, 0) == 65535`), while `PosBack` returns 0 for
    /// every code, so idempotency cannot hold. Neither is reachable through
    /// the shipped data
    /// (`ArenaConfig.Radius` is `[Range(5, 150)]` as of Stage 3 Task 8 — was
    /// `[Range(5, 100)]` before the Т12 three-zone arena needed more room —
    /// `HeroConfig.MaxAimHeight` is `[Range(1, 6)]`), so no guard is spent
    /// here — but a caller
    /// inventing its own scale must not pass zero. A negative `radius`
    /// merely mirrors the axis and stays idempotent.
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
        /// i.e. up to `3*Radius` from the arena center — a wider range than
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

        /// Heading angle -> `[0, 255]`, step `1.40625` deg. ⛔ THE ARITHMETIC
        /// LIVES IN Simulation.Core.ByteCodecs SINCE app-94sk T6a, and this is
        /// a forwarder: the pose key packs the SAME heading into the SAME byte
        /// for the rewind history, and Ring.Simulation cannot reference this
        /// assembly, so the one home both can reach is there (rule 2 -- one
        /// mapping, not two spellings). The contract -- the `atan2(0,0)`
        /// convention, the fold of the `+pi`/`-pi` rails onto one code -- is
        /// documented there and still pinned here by QuantizeTests through
        /// this call.
        public static byte Dir(float2 v) => ByteCodecs.Dir(v);

        public static float2 DirBack(byte q) => ByteCodecs.DirBack(q);

        /// One-sided `[0, max] -> [0, 255]` for the non-negative quantities
        /// (HP, analog stick magnitude, `AimHeight`, Р84). ⛔ FORWARDER, for
        /// Dir's reason above: PoseKey quantizes its blend weight with the
        /// same mapping at `max` 1.
        public static byte Unit(float v, float max) => ByteCodecs.Unit(v, max);

        public static float UnitBack(byte q, float max) => ByteCodecs.UnitBack(q, max);
    }
}
