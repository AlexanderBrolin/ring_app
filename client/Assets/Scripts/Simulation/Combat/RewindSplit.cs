using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// SINGLE HOME OF THE REWIND SPLIT (app-88jb Т27, spec §3.6, owner
    /// decision Н24/Р407). The lag a shot arrives with is not one quantity
    /// spent on one thing: part of it is the time the input really spent on
    /// the wire, and part of it is the interpolation buffer that draws every
    /// body in the past. This class is the only place that says where the
    /// boundary between the two lies.
    ///
    /// WHAT EACH HALF BUYS -- and why charging the whole lag to the round is
    /// the canceled design (Р381) rather than a simplification -- is written
    /// once, in ArenaConfig's own doc beside RewindPictureTicks, together with
    /// the formula these two methods are the code of. It is not restated here:
    /// a rule has one home, and this file is the home of its arithmetic, not
    /// of its justification.
    ///
    /// A FILE OF ITS OWN RATHER THAN A MEMBER OF EITHER CONSUMER, on the
    /// precedent Spread.cs set in this same folder: a formula that more than
    /// one caller depends on gets a home apart from all of them. Here there
    /// are two, and they live in different files -- WeaponSystem spends the
    /// INPUT half on the round it has just spawned (Т27), and the PICTURE half
    /// is the tick ProjectileSystem's target question will be asked at (Т28).
    /// Hanging the rule off WeaponSystem would make the projectile system call
    /// the weapon to learn it; writing it out in both would be one number with
    /// two sources.
    ///
    /// ⚠ WHAT THE WITNESSES OF THESE TWO METHODS COVER, AND WHAT THEY DO NOT
    /// -- said here rather than left for a reader to infer from the word
    /// "covered". THE ARITHMETIC OF THE DIVISION IS CALLED DIRECTLY, by three
    /// tests in RewindTests: the saturation of the picture half at the arena's
    /// depth with the remainder going to the round, the shallow case where the
    /// picture half takes the WHOLE of `k` and the round is owed nothing, and
    /// the identity that the two halves add back up to `k` over the entire
    /// domain.
    ///   The shallow one is why "the consumers cover it anyway" was not an
    /// option. Replace the min below by its right operand and a shallow `k`
    /// yields a NEGATIVE step count -- and a `for` loop declines a negative
    /// bound exactly the way it declines a zero one, so seen through
    /// WeaponSystem's call that mutant is indistinguishable from the truth. A
    /// fixture driving a real Tick cannot tell them apart at all; only a direct
    /// call can.
    ///
    /// WHAT THOSE THREE DO NOT COVER is the whole of the second half: that the
    /// number reaches a projectile at all, that it is spent ONCE on the birth
    /// tick instead of on every tick of flight, and that it comes from the
    /// input of the round's OWN shooter are facts about WeaponSystem and
    /// ProjectileSystem, and their witnesses are the RewindTests fixtures that
    /// drive a real Tick.
    ///
    /// DOMAIN, STATED SO NEITHER METHOD NEEDS A CLAMP OF ITS OWN. `k` reaches
    /// a caller already inside [0, Arena.RewindCapTicks]:
    /// SimInputSanitizer.Sanitize is the one gate every input passes through on
    /// its way into a tick and applies that cap there, and SimInput.RewindTicks
    /// is a byte, so a negative depth is unrepresentable rather than merely
    /// wrong. On the config side SimConfigBuilder refuses a picture depth
    /// deeper than the cap. Both results therefore land in [0, k] by
    /// construction.
    ///
    /// ⚠ THE FLOOR UNDER THE PICTURE DEPTH IS NetInvariants RULE #11, NOT
    /// ArenaConfig's [Range], and the difference is the difference between a
    /// gate and a decoration. [Range] is an inspector attribute: it shapes a
    /// slider and refuses nothing a script, a test or a hand-edited asset
    /// assigns, and a runtime clamp of RewindPictureTicks exists nowhere in the
    /// tree. What holds the floor is rule #11 -- it demands
    /// Arena.RewindPictureTicks == Net.InterpBufferTicks, while rule #1 of the
    /// same validator rejects an InterpBufferTicks of zero or less
    /// -- and it holds it where holding matters, at SERVER START, since
    /// ServerBootstrap fails the process on every violation the validator
    /// reports.
    ///   That is load-bearing for this file rather than a filing note. At
    /// RewindPictureTicks = 0 the min below answers zero for every `k`, the
    /// whole of every shooter's depth would be spent cranking his round, and
    /// the weapon would be the canceled Р381 hitscan again -- reached by
    /// configuration instead of by code.
    internal static class RewindSplit
    {
        /// THE QUESTION HALF: how many ticks of `k` are spent asking where the
        /// bodies stood, which moves nothing in the world at all. Saturates at
        /// the arena's own picture depth, so a shooter lagging worse than the
        /// interpolation buffer gets no deeper look into the past than anyone
        /// else. Т28 is its first consumer; Т27 writes it because the two
        /// halves are one rule and splitting them across two tasks would give
        /// that rule two authors.
        internal static int PictureTicks(int k, in ArenaSimConfig arena)
            => math.min(k, arena.RewindPictureTicks);

        /// THE ROUND HALF: what is left of `k` once the question is paid for,
        /// and the only half that MOVES anything -- it is the number of
        /// catch-up steps a freshly spawned round takes on its birth tick
        /// (ProjectileSystem.CatchUp). Expressed through PictureTicks above
        /// rather than restating the same min, so the two can never disagree
        /// about where the boundary between them runs.
        internal static int InputTicks(int k, in ArenaSimConfig arena)
            => k - PictureTicks(k, in arena);
    }
}
