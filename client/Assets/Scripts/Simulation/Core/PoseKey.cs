using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// app-94sk T6a (spec §3.6/§3.7): THE POSE, PACKED -- one body's pose in
    /// 14 bytes, for the rows of the rewind history (app-94sk T7) and for the hit
    /// volumes that read a pose back out of them. The LIVE pose is the ten
    /// flat fields of PlayerState (seven of MobState); this is their
    /// compressed copy, and the relationship is a rule rather than a second
    /// fact: every field here is a QUANTIZATION of one of those.
    ///
    /// ⛔ FIELD ORDER IS PART OF THE CONTRACT: both `ushort` first, then the
    /// bytes, is what makes the struct exactly 14 bytes with no padding
    /// (2*2 + 7*1 + 1 + 2*1 = 14). Four numbers hang on it -- the key 14 B,
    /// the reaction 4 B of it, a history record 8 + 1 + pad + 14 = 24 B
    /// (since app-94sk T7) and the history 24 x 6 x 1353 = 190.3 KiB (95.1
    /// before it).
    ///
    /// ⛔ `ushort`/`sbyte` LIVE HERE AND NOWHERE IN THE STATE (spec §3.6,
    /// findings B-I1/D-C6): the reflective guards that sweep PlayerState
    /// hard-fail on both types, and none of them ever looks at this struct.
    /// PoseKeyTests carries its own three-type Bump for the same reason.
    ///
    /// ⛔ THE PACKER LIVES IN ONE PLACE. Building the key out of the flat
    /// fields is needed at the producer (PoseSystem.Update for a mob,
    /// SimulationWorld.TickMovement for the collector -- app-94sk T7: packed
    /// ONCE at judgement time, copied into the history's row by
    /// PositionHistory.Write and handed to the resolver by RewoundBody), in
    /// RestoreState's re-derivation, and in AimLine for the present -- and
    /// copies of one layout drift by a byte each (finding B-I1b). Inside this
    /// file the layout is spelled ONCE too (`Pack`); the two public packers
    /// only pick the fields their struct carries.
    public struct PoseKey                                       // 14 bytes
    {
        public ushort LowerPhase, ReactionPhase;                // 0-3   phases, whole ticks
        public byte   LowerClipA, LowerClipB, LowerBlend;       // 4-6   the blend tree
        public byte   UpperClip, UpperWeight;                   // 7-8   the aim layer
        public byte   ReactionClip, ReactionDir;                // 9-10  the reaction (§3.19)
        public byte   Facing;                                   // 11    Dir, 256 steps
        public sbyte  TiltX, TiltZ;                             // 12-13 the tilt, as a vector

        /// The tilt's quantization step, radians per code -- ⛔ AN ABSOLUTE
        /// STEP AND A CONSTANT OF THE BINARY, not a share of TiltFallAngle
        /// and not a config field (spec §3.6 finding D-I1, plan round 5 A-3).
        /// TiltFallAngle is a hot knob: ApplyConfig clamps a live tilt into a
        /// new value of it, so a code meaning "1/127 of the fall angle" would
        /// mean a DIFFERENT angle after a live edit while six rows of history
        /// still carried bytes of the old scale. Being a constant it is equal
        /// in any two configurations by construction, which is why
        /// ArenaTopologyMatches does not compare it: a comparison that cannot
        /// fail is dead code, and a negative test for it cannot be written.
        /// ⚠ AND THE NUMBER HAS A CEILING: an sbyte saturates at 127 codes =
        /// 1.27 rad = 72.8 deg, while MobConfig declared TiltFallAngle up to
        /// 3.14 until app-94sk T6c. A saturated tilt in the history is a
        /// rewound pose that disagrees with the one the shot was judged
        /// against, and the more interesting the situation the more it
        /// disagrees. Validation rule 15a (SimConfigBuilder.ValidateMob,
        /// against TiltCeiling below) refuses a configuration whose fall angle
        /// the key cannot express, and MobConfig's [Range] offers the
        /// Inspector the same ceiling. ⚠ THAT COVERS THE FOUR ARCHETYPES,
        /// because only they carry a threshold: the collector has no fall
        /// angle (HeroConfig carries his spring and no knockdown), so his
        /// lean is bounded by that spring rather than by a validated number,
        /// and past 1.27 rad his key saturates the same way -- said here
        /// rather than implied closed. QuantizeTilt below clamps by LENGTH,
        /// so the direction of a lean survives the clamp even when its
        /// magnitude does not.
        public const float TiltQuantStep = 0.01f;   // 127 * 0.01 = 1.27 rad = 72.8 deg

        /// The longest lean the key can hold: 127 codes of the step, the
        /// length QuantizeTilt clamps at. ⛔ PUBLIC AND ONE HOME (app-94sk T6c):
        /// validation rule 15a asks the same comparison of every archetype's
        /// TiltFallAngle, and MobConfig's [Range] offers the Inspector the same
        /// ceiling -- a second spelling of `127 * step` in either would be the
        /// drift TiltQuantStep exists to prevent.
        public const float TiltCeiling = sbyte.MaxValue * TiltQuantStep;   // 1.27 rad

        /// The collector's key. `Facing` goes through the SAME codec the wire
        /// uses (ByteCodecs.Dir -- one mapping, not two spellings of it), the
        /// tilt through QuantizeTilt, the phases through a saturating
        /// narrowing. ⛔ THE KEY'S `LowerBlend` IS THE STATE'S `LowerShare`,
        /// byte for byte, NOT a quantization of `PlayerState.LowerBlend`
        /// (app-94sk T6b): the state's float is the tree's damped PARAMETER,
        /// the key's byte is the SHARE within the pair -- two quantities the
        /// producer maps one onto the other (PlayerState's own block says why
        /// they cannot be one). The share is born a byte, so nothing is
        /// rounded here.
        public static PoseKey FromPlayer(in PlayerState p)
            => Pack(p.LowerPhase, p.ReactionPhase, p.LowerClipA, p.LowerClipB, p.LowerShare,
                p.UpperClip, p.UpperWeight, p.ReactionClip, p.ReactionDir, p.Dir, p.Tilt);

        /// The mob's key: the collector's layout minus the fields MobState
        /// does not carry -- the aim layer (a mob has one layer) and the
        /// blend share (a mob has no blend tree), both packed as ZERO. See
        /// MobState's own block for why each absence is a decision.
        public static PoseKey FromMob(in MobState m)
            => Pack(m.LowerPhase, m.ReactionPhase, m.LowerClipA, m.LowerClipB, 0,
                0, 0, m.ReactionClip, m.ReactionDir, m.Dir, m.Tilt);

        /// app-94sk T7: THE LEAN BACK OUT OF THE KEY, as the plan-frame vector
        /// PlayerState.Tilt carries (length = angle, direction = the side the
        /// body goes down towards) -- `TiltX`/`TiltZ` times the step, the
        /// exact inverse of QuantizeTilt's rounding. Read by RewoundBody for a
        /// body answered out of the history: the volumes of that tick lean by
        /// THIS, not by the live lean (fixture 22b, mutant M459). ⚠ Not for
        /// the live path: a live body's lean is read off its struct exactly,
        /// and a round-trip through 0.01 rad codes would move every live
        /// outcome by a hundredth.
        public float2 TiltVector => new float2(TiltX * TiltQuantStep, TiltZ * TiltQuantStep);

        /// app-94sk T7: THE COURSE BACK OUT OF THE KEY, a unit vector through
        /// the wire's own codec (ByteCodecs.DirBack, 256 steps) -- what turns a
        /// rewound body's volumes the way it faced THEN (fixture 22a, mutant
        /// M458). The same "not for the live path" note as TiltVector's.
        public float2 DirVector => ByteCodecs.DirBack(Facing);

        /// The ONE spelling of the layout. Eleven arguments rather than two
        /// structs because the two structs carry different subsets, and a
        /// packer per struct is the duplicated layout the class doc refuses.
        static PoseKey Pack(int lowerPhase, int reactionPhase, byte lowerClipA, byte lowerClipB,
            byte lowerShare, byte upperClip, byte upperWeight, byte reactionClip, byte reactionDir,
            float2 dir, float2 tilt)
        {
            QuantizeTilt(tilt, out sbyte tiltX, out sbyte tiltZ);
            return new PoseKey
            {
                LowerPhase = QuantizePhase(lowerPhase),
                ReactionPhase = QuantizePhase(reactionPhase),
                LowerClipA = lowerClipA,
                LowerClipB = lowerClipB,
                LowerBlend = lowerShare,
                UpperClip = upperClip,
                UpperWeight = upperWeight,
                ReactionClip = reactionClip,
                ReactionDir = reactionDir,
                Facing = ByteCodecs.Dir(dir),
                TiltX = tiltX,
                TiltZ = tiltZ,
            };
        }

        /// A phase in whole ticks into the key's `ushort` -- SATURATING, not
        /// wrapping: a phase past 65535 ticks (36 minutes at 30 Hz, longer
        /// than a raid) is not a pose the history can name, and a wrapped one
        /// would name a different pose with confidence. Negative never
        /// arrives (the producers count up from zero) and clamps to zero all
        /// the same rather than to 65535.
        static ushort QuantizePhase(int phase)
            => (ushort)math.clamp(phase, 0, ushort.MaxValue);

        /// The tilt as two codes of TiltQuantStep each. ⛔ CLAMPED BY LENGTH
        /// FIRST: the vector's length is the angle and its direction the side
        /// the body leans to (PlayerState.Tilt), and a clamp per component
        /// would turn a diagonal lean past the ceiling into a lean of a
        /// different heading. Scaled onto 127 codes along its own direction,
        /// each component then rounds inside the sbyte on its own: the
        /// ceiling is exactly 127 codes in float32, so no finite input can
        /// reach -128 or 128 (measured, not assumed).
        /// ⛔ A RAIL FOR THE NON-FINITE, like every sibling codec: a NaN or
        /// infinite length skips the clamp, and `(sbyte)` of a NaN is a value
        /// the C# spec leaves to the runtime -- two shipped runtimes could
        /// disagree on a byte that feeds the history and the digests
        /// (CRITICAL RULE 2). Such a tilt packs as UPRIGHT, deterministically;
        /// nothing in play produces one (Impact.SpringStep on a validated
        /// spring), so the rail is for the contract, not for a case.
        /// ⚠ NAMING: the simulation's plan is `.xy` and the key's pair is
        /// named X/Z after the body frame the spec writes in (§3.6), so the
        /// plan's second component travels as `TiltZ`. One mapping, here.
        /// The inverse is `TiltVector` above, on the same scale -- it landed
        /// with its first reader (RewoundBody, app-94sk T7), and a second
        /// scale invented there would be the drift the constant exists to
        /// prevent.
        static void QuantizeTilt(float2 tilt, out sbyte x, out sbyte z)
        {
            float len = math.length(tilt);
            if (!math.isfinite(len)) { x = 0; z = 0; return; }
            if (len > TiltCeiling) tilt *= TiltCeiling / len;
            x = (sbyte)math.round(tilt.x / TiltQuantStep);
            z = (sbyte)math.round(tilt.y / TiltQuantStep);
        }

        /// The key into a digest, FIELD BY FIELD -- ⛔ NEVER MemoryMarshal over
        /// the struct: a byte-wise fold would depend on the padding, the
        /// padding on the platform, and CRITICAL RULE 2 would hold by luck.
        /// The narrow fields widen to `int` the way PositionHistory.FoldRecord
        /// widens Flags (StateHash64 has no byte overload). Same shape as that
        /// method and every Hash* helper of SimulationWorld: takes the running
        /// hash, returns it. It reaches the state digest through
        /// PositionHistory.FoldRecord (app-94sk T7); PoseKeyTests keeps every
        /// field in it, and fixture 23 (PoseTableTests) asks the same of the
        /// history's fold.
        public static ulong Fold(ulong h, in PoseKey k)
        {
            h = StateHash64.Add(h, (int)k.LowerPhase);
            h = StateHash64.Add(h, (int)k.ReactionPhase);
            h = StateHash64.Add(h, (int)k.LowerClipA);
            h = StateHash64.Add(h, (int)k.LowerClipB);
            h = StateHash64.Add(h, (int)k.LowerBlend);
            h = StateHash64.Add(h, (int)k.UpperClip);
            h = StateHash64.Add(h, (int)k.UpperWeight);
            h = StateHash64.Add(h, (int)k.ReactionClip);
            h = StateHash64.Add(h, (int)k.ReactionDir);
            h = StateHash64.Add(h, (int)k.Facing);
            h = StateHash64.Add(h, (int)k.TiltX);
            return StateHash64.Add(h, (int)k.TiltZ);
        }
    }
}
