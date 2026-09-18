using Unity.Mathematics;
using Ring.Simulation.Core;

namespace Ring.Simulation.Combat
{
    /// The ONE home of the three questions asked about a body's hit volumes
    /// that are not "which one did this round strike" (app-94sk T2, spec §3.4).
    ///
    /// ⛔ IT EXISTS BECAUSE VALIDATION RULE 2 IS BEING WITHDRAWN. While parts
    /// were a sorted column, "the crown" and "the bottom of the head" could be
    /// read off an INDEX, and THREE copies of that index were all equally
    /// right: HitZones.StackTop, SimConfigBuilder.PartsTop and
    /// PersistentPropsDirector.PartHeight. ⚠ A FOURTH place computed the same
    /// crown as a MAXIMUM (MobFootprintAudit) and was therefore the only one the
    /// change left correct -- it moved here anyway, because one home that leaves
    /// a copy behind closes nothing. The zone lookup was written twice more
    /// (MidOfZone; HeadPartBottom was a FOURTH index read until this task turned
    /// it into one). The volume layout of spec §3.2 puts the head
    /// THIRD and a shin LAST, so after the rule goes, ZERO of the three indices
    /// are right -- "the top of the last part" would return the top of a shin,
    /// and "the bottom of the head" would silently become the bottom of a shin.
    ///
    /// ⛔ PUBLIC, NOT internal, and that is not a style choice: Ring.Data calls
    /// it (SimConfigBuilder — today through PartsTop and HeadPartBottom, and
    /// from T4 through validation rules 13 and 16), Ring.Presentation
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

        /// app-94sk T4 (validation rule 9): HOW WIDE THE BROAD PHASE HAS TO BE
        /// for this body — the furthest any VOLUME reaches from the body's own
        /// vertical axis, in any phase of any clip it can be shot during.
        ///
        /// ⛔ PER PART, AND WITH THAT PART'S RADIUS — not "the furthest bone
        /// anywhere plus the widest radius anywhere". Taking the two maxima
        /// apart answers a looser number and hides the case the split exists
        /// for: a thin leg swinging out past a fat torso that never moves.
        ///
        /// ⛔⛔ THE LAST CLIP IS EXCLUDED, AND THAT IS THE DEATH TAKE (Р559,
        /// `PoseBaker`'s ordering contract). A falling body sweeps its bones
        /// far from its axis — the chaser's head reaches 2.37 m in his death
        /// take against 1.36 m in a run — and sizing the gather circle to that
        /// would widen EVERY body by the animation nobody can be hit during.
        /// ⚠ A TABLE WITH ONE CLIP MEASURES ALL OF ITSELF: it has no death take
        /// to leave out, which is the shape every fixture table has.
        ///
        /// ⚠ THE PLAN VIEW IS `(x, z)`: these are BONES, and bones live in the
        /// body frame where `.y` is the height (`HitVolumes.ToWorld` is the one
        /// crossing into the world frame, where height is `.z`).
        public static float GatherReach(HitPart[] parts, in PoseTable table)
        {
            if (parts == null || parts.Length == 0) return 0f;
            if (table.Bones == null || table.BoneCount <= 0) return 0f;

            int stride = table.BoneCount;
            int allRows = table.Bones.Length / stride;
            int rows = table.ClipFirstRow != null && table.ClipFirstRow.Length >= 2
                ? table.ClipFirstRow[table.ClipFirstRow.Length - 2]
                : allRows;
            if (rows <= 0) rows = allRows;

            float widest = 0f;
            for (int r = 0; r < rows; r++)
            {
                int rowBase = r * stride;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].BoneA >= stride || parts[i].BoneB >= stride) continue;
                    float a = PlanReach(table.Bones[rowBase + parts[i].BoneA]);
                    float b = PlanReach(table.Bones[rowBase + parts[i].BoneB]);
                    float reach = math.max(a, b) + parts[i].Radius;
                    if (reach > widest) widest = reach;
                }
            }
            return widest;

            static float PlanReach(float3 bone) => math.length(new float2(bone.x, bone.z));
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
        /// answer is a height, which is frame-free.
        ///
        /// ⛔ TWO FORMS SINCE app-94sk T6b, AND THEY ANSWER TWO READERS. This
        /// one takes a ROW of the table and serves validation rule 16
        /// (SimConfigBuilder asks the slide's crown at
        /// `ClipFirstRow[BakedClips.Collector.Slide]`, in the rest of a state
        /// with no tilt and no blend) and the fixtures that pin that rule. The
        /// overload below takes a READY pose -- the blend of two rows with
        /// the aim layer over it, which one row index cannot name -- and
        /// serves the height gate inside HitVolumes.Resolve. The `bodyTilt`
        /// parameter T2 carried here "for T6b" is gone: a leaning body's crown
        /// is not a function of the untilted pose's heights (every bone turns
        /// by its own plan offset), and Resolve answers that case by not
        /// asking the gate at all (its own doc).
        public static float PoseTop(HitPart[] parts, in PoseTable table, int poseRow)
        {
            if (parts == null || parts.Length == 0) return 0f;
            if (table.Bones == null || table.BoneCount <= 0) return 0f;

            int rowBase = poseRow * table.BoneCount;
            if (rowBase < 0 || rowBase + table.BoneCount > table.Bones.Length) return 0f;

            float top = float.NegativeInfinity;
            for (int i = 0; i < parts.Length; i++)
            {
                HitPart part = parts[i];
                // ⛔ THE BONE INDICES ARE GUARDED HERE TOO, AND NOT BECAUSE
                // VALIDATION RULE 6 IS ABSENT — BECAUSE IT RUNS LATER. Rule 16
                // calls this member from `SimConfigBuilder.Validate` BEFORE
                // `ValidateParts` reaches rule 6, so an out-of-range `BoneB`
                // would arrive here first and either read a row it does not
                // belong to (silently) or throw an IndexOutOfRangeException —
                // which is not a named refusal and, by this project's own rule
                // 332/498, not a RED either. Same guard, same reason, as
                // `GatherReach` right above.
                if (part.BoneA >= table.BoneCount || part.BoneB >= table.BoneCount) continue;
                float a = table.Bones[rowBase + part.BoneA].y;
                float b = table.Bones[rowBase + part.BoneB].y;
                top = math.max(top, math.max(a, b) + part.Radius);
            }
            return top;
        }

        /// The crown of a READY pose -- `pose[0..boneCount)` as PoseTable.Sample
        /// wrote it (body frame, height in `.y`). Same guards as the row form
        /// above and for the same reason; `boneCount` is the table's, because
        /// a memo buffer is as wide as the widest body and keeps a previous
        /// tenant's bones past this body's count.
        public static float PoseTop(HitPart[] parts, float3[] pose, int boneCount)
        {
            if (parts == null || parts.Length == 0) return 0f;
            if (pose == null || boneCount <= 0 || pose.Length < boneCount) return 0f;

            float top = float.NegativeInfinity;
            for (int i = 0; i < parts.Length; i++)
            {
                HitPart part = parts[i];
                if (part.BoneA >= boneCount || part.BoneB >= boneCount) continue;
                top = math.max(top, math.max(pose[part.BoneA].y, pose[part.BoneB].y) + part.Radius);
            }
            return top;
        }
    }
}
