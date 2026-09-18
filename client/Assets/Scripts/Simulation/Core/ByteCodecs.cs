using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// The byte codes the WIRE and the POSE KEY share (app-94sk T6a). The
    /// four bodies here were Networking.Protocol.Quantize's own, and Quantize
    /// still spells them for its callers -- by delegating here. They moved
    /// because PoseKey packs a heading and a blend weight into the same bytes
    /// the wire packs them into, and Ring.Simulation cannot see
    /// Ring.Networking: the one home the two sides can both reach is this
    /// assembly (rule 2 -- one mapping; a second spelling would be exactly the
    /// drift QuantizeTests' idempotency pins exist to refuse, and those pins
    /// still cover these bodies through Quantize's forwarders). A class of
    /// its own rather than four more members of Geometry (review finding):
    /// a scalar share is not geometry, and that file's header argues for
    /// exactly the two halves it has.
    ///
    /// CLAMPING codecs, not validating ones -- Quantize's class doc carries
    /// the full argument, including the NaN-safety of `saturate` and the
    /// degenerate `max == 0` case, and it describes these bodies as much as
    /// the ones that stayed there: NaN/Infinity and out-of-range inputs land
    /// on a rail rather than throwing, because they run on the write path of
    /// every tick. `math.round` is round-half-to-even (Р134).
    public static class ByteCodecs
    {
        /// Heading angle -> `[0, 255]`, step `1.40625` deg. `atan2(0, 0)`
        /// is `0` radians by convention (`System.Math.Atan2`'s own
        /// documented special case), so `Dir(float2.zero)` encodes
        /// identically to `Dir(+X)`. That is deliberate, not a defect: on
        /// the wire `MoveDir` is angle + magnitude (Task 25), and at
        /// magnitude 0 nothing ever reads the angle back; in the pose key a
        /// zero course is not a legal value in the first place
        /// (PlayerState.Dir's own doc).
        ///
        /// `atan2`'s range is `[-pi, +pi]` — BOTH rails are attainable:
        /// `+pi` from `(-1, 0)` and `-pi` from `(-1, -0)`, because a
        /// negative-zero `y` selects the lower branch (fix-round F3: the
        /// first draft claimed the range was half-open `(-pi, +pi]`, which
        /// is false). So the raw code before wrapping spans `[0, 256]` —
        /// 257 values, one MORE than a byte holds. The cast through `int`
        /// before the explicit `& 0xFF` mask folds the top value (`256`)
        /// onto `0`, which is exactly the code the other rail already
        /// produces: `+pi` and `-pi` are the same direction, and without
        /// this fold they would encode to two different codes for it. A raw
        /// `(byte)` cast straight from the rounded FLOAT (instead of
        /// `(byte)((int)... & 0xFF)`) does not reliably reproduce this: C#
        /// only defines integer-to-byte narrowing as truncation, not a
        /// float-to-byte cast of an out-of-range value.
        public static byte Dir(float2 v)
        {
            float angle = math.atan2(v.y, v.x);
            int raw = (int)math.round((angle + math.PI) / (2f * math.PI) * 256f);
            return (byte)(raw & 0xFF);
        }

        /// The heading back, as a UNIT vector (QuantizeTests pins the length:
        /// a decoder returning anything else would scale whatever it feeds).
        public static float2 DirBack(byte q)
        {
            float angle = q / 256f * (2f * math.PI) - math.PI;
            return new float2(math.cos(angle), math.sin(angle));
        }

        /// One-sided `[0, max] -> [0, 255]` — every consumer's input is
        /// non-negative (HP, analog stick magnitude, `AimHeight` in
        /// `[0, Hero.MaxAimHeight]` (Р84), and the blend weight of PoseKey at
        /// `max` 1), so unlike a position there is no negative half of the
        /// range to spend codes on.
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
