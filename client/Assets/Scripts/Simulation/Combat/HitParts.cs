using Unity.Mathematics;
using Ring.Simulation.Core;

namespace Ring.Simulation.Combat
{
    /// The ONE home of the three questions asked about a body's hit volumes
    /// that are not "which one did this round strike" (app-94sk T2, spec §3.4).
    ///
    /// ⛔ IT EXISTS BECAUSE VALIDATION RULE 2 IS BEING WITHDRAWN. While parts
    /// were a sorted column, "the crown" and "the bottom of the head" could be
    /// read off an INDEX, and four copies of that index were all equally right:
    /// HitZones.StackTop, SimConfigBuilder.PartsTop, MobFootprintAudit and
    /// PersistentPropsDirector.PartHeight; the zone lookup was written twice
    /// more (MidOfZone, HeadPartBottom). The volume layout of spec §3.2 puts
    /// the head THIRD and a shin LAST, so after the rule goes, ZERO of the four
    /// are right -- "the top of the last part" would return the top of a shin,
    /// and "the bottom of the head" would silently become the bottom of a shin.
    ///
    /// ⛔ PUBLIC, NOT internal, and that is not a style choice: Ring.Data calls
    /// it (SimConfigBuilder, validation rules 13 and 16), Ring.Presentation
    /// calls it (PersistentPropsDirector) and Ring.Editor calls it
    /// (MobFootprintAudit), while this assembly's InternalsVisibleTo is open to
    /// the tests alone.
    public static class HitParts
    {
        /// The body's crown in the REST pose -- the maximum over RestTop.
        ///
        /// AN EMPTY OR ABSENT ARRAY ANSWERS 0, NOT A CRASH, and that half is
        /// inherited verbatim from HitZones.StackTop, which this method
        /// replaces: the call sites are on the hot path, for a body the
        /// simulation was merely asked to shoot at, and a hand-built fixture
        /// that never filled the array must not throw there. A zero crown pairs
        /// with Resolve's own empty-array refusal: a body that declares no hit
        /// volume presents nothing to hit, which is SimConfigBuilder.Validate's
        /// own wording for the same rule ("Parts must not be empty -- a body
        /// with no parts cannot be hit at all" -- cited by its words, not by a
        /// line number, Ruling 196). Every config that went through the builder
        /// is non-empty by validation, so this arm is unreachable in the game
        /// and reachable only from a fixture.
        public static float RestCrown(HitPart[] parts)
        {
            if (parts == null || parts.Length == 0) return 0f;
            float top = parts[0].RestTop;
            for (int i = 1; i < parts.Length; i++)
                if (parts[i].RestTop > top) top = parts[i].RestTop;
            return top;
        }

        /// The first part carrying this zone; on a tie, the one with the SMALLER
        /// PartId. ⛔ THE TIE-BREAK IS THE SAME ONE HitVolumes.Resolve USES, and
        /// that is not a coincidence: a zone now appears on SEVERAL volumes
        /// (validation rule 3, "a zone appears once", goes with rule 2), so
        /// "the first match" stopped being "the only match" and something has
        /// to decide. Two answers to one question is exactly what PartId exists
        /// to prevent.
        public static bool TryFindByZone(HitPart[] parts, HitZone zone, out HitPart part)
        {
            part = default;
            if (parts == null) return false;
            bool found = false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Zone != zone) continue;
                if (found && parts[i].PartId >= part.PartId) continue;
                part = parts[i];
                found = true;
            }
            return found;
        }

        /// The crown of the LIVE pose -- what the body presents right now.
        ///
        /// ⛔ THIS IS NOT RestCrown AND NOT RestTop: those describe the REST
        /// pose, which the baker computed once and stored in HitPart. A moving
        /// body does not stand in it. The name collision is deliberate and
        /// recorded in the project glossary (ADR-003 §9 A10).
        ///
        /// ⛔ ITS HOME IS HERE RATHER THAN IN HitVolumes because validation rule
        /// 16 asks this same question from SimConfigBuilder, i.e. from
        /// Ring.Data, where an internal member of HitVolumes is not visible at
        /// all -- putting it there would have grown a THIRD copy of "the crown
        /// by the table".
        ///
        /// ⚠ HEIGHT IS `.y` HERE because the bones are in the BODY frame; the
        /// answer is a height, which is frame-free. The tilt is taken for the
        /// same reason Resolve takes it: a leaning body presents a different
        /// crown than an upright one, and re-signing seven call sites twice
        /// costs more than carrying the parameter. Until T6b every caller
        /// passes float2.zero.
        /// ⛔ ITS POSE PARAMETERS MIGRATE TOGETHER WITH HitVolumes.Resolve: in
        /// T6b `int poseRow` becomes the blended `float3[] pose` in BOTH.
        public static float PoseTop(HitPart[] parts, in PoseTable table, int poseRow, float2 bodyTilt)
        {
            if (parts == null || parts.Length == 0) return 0f;
            if (table.Bones == null || table.BoneCount <= 0) return 0f;

            int rowBase = poseRow * table.BoneCount;
            if (rowBase < 0 || rowBase + table.BoneCount > table.Bones.Length) return 0f;

            float top = float.NegativeInfinity;
            for (int i = 0; i < parts.Length; i++)
            {
                HitPart part = parts[i];
                float a = table.Bones[rowBase + part.BoneA].y;
                float b = table.Bones[rowBase + part.BoneB].y;
                top = math.max(top, math.max(a, b) + part.Radius);
            }
            return top;
        }
    }
}
