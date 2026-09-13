using Unity.Mathematics;
using Ring.Simulation.Core;

namespace Ring.Simulation.Combat
{
    /// Which volume of a body this step of a round struck, and where
    /// (app-94sk T2, spec §3.4).
    ///
    /// SEPARATED FROM HitZones ON PURPOSE: HitZones stays the home of the
    /// SILHOUETTE GATE (`Overlaps`, which the barrier and the floor need and
    /// which knows nothing of parts); choosing the part is this class's job.
    ///
    /// THE CHOICE IS A THREE-STEP LADDER: the zone's priority first (head >
    /// torso > arms and legs), then the SMALLER t (first entry), then the
    /// smaller PartId.
    /// ⚠ Arms and legs are ONE step: their multipliers are equal (0.75), and a
    /// step between them would cost a rule and a test while deciding nothing
    /// (Р505).
    /// ⛔ THE ZONE'S PRIORITY, NOT THE LARGEST MULTIPLIER: Rainbow Six does the
    /// second, CS2 the first; we take the first, because a multiplier here is
    /// the owner's balance number while a priority is a rule. The cost of
    /// having neither is measurable: an arm resting on a weapon in front of a
    /// face would take the hit away from the head and pay 0.75 instead of 1.7.
    internal static class HitVolumes
    {
        /// `poseRow` is this body's row of the table on this tick (before T6 the
        /// fixtures hand in the rest row). `t` is the fraction of the step, in
        /// the same [p0, p1] parameterization the caller's min-scan uses.
        ///
        /// ⛔⛔ TWO FRAMES MEET HERE, AND THE BORDER BETWEEN THEM IS ToWorld
        /// (side-quest app-coou). The table's bones are in the BODY frame, the
        /// way SkeletonAudit measures a skeleton and the way the baker will
        /// deliver one: height in `.y`, plan in `.x`/`.z`. Everything else —
        /// `bodyOrigin`, `p0`/`p1`, `hitHeight` — is in the WORLD frame the whole
        /// of Ring.Simulation is written in: plan in `.xy`, height in `.z`
        /// (ShotGeometry builds `new float3(AimPoint, AimHeight)`, and the
        /// vertical speed is named VelZ for that reason). ⇒ THE CALLER BUILDS
        /// ITS STEP THE ORDINARY WAY and weaves nothing; ToWorld, and only
        /// ToWorld, transposes. Witness: fixture 12a, mutant M392.
        ///
        /// ⛔ `bodyTilt` IS TAKEN ALREADY, and every caller hands in float2.zero
        /// until T6b: introducing it later would mean editing seven call sites a
        /// second time.
        public static bool Resolve(HitPart[] parts, in PoseTable table, int poseRow, float2 bodyTilt,
            float3 bodyOrigin, float bodyFacingSin, float bodyFacingCos,
            float3 p0, float3 p1, float projRadius,
            out HitZone zone, out float mult, out float hitHeight, out byte partId, out float t)
        {
            zone = HitZone.None; mult = 1f; hitHeight = 0f; partId = 0; t = 0f;
            if (parts == null || parts.Length == 0) return false;   // no volumes, nothing to hit
            if (table.Bones == null || table.BoneCount <= 0) return false;

            int rowBase = poseRow * table.BoneCount;
            if (rowBase < 0 || rowBase + table.BoneCount > table.Bones.Length) return false;

            // ⛔ THE CHEAP NECESSARY CONDITION GOES THROUGH THE EXISTING HOME
            // RATHER THAN A SECOND ONE OF ITS OWN: the height gate lives in
            // HitZones.Overlaps, the barrier calls it, and a twin here would be
            // exactly the duplication those two homes were separated to avoid.
            // The ceiling is the crown of the LIVE pose (HitParts.PoseTop), not
            // RestTop: the latter describes the REST pose and is wrong in
            // motion. Witness: the instrument fixture 8 (T6c) — the gate must
            // SHORTEN the work without moving a single outcome.
            // ⛔ THE STEP'S HEIGHT IS `.z`: p0/p1 arrive in the WORLD frame.
            if (!HitZones.Overlaps(p0.z, p1.z, projRadius,
                    HitParts.PoseTop(parts, in table, poseRow, bodyTilt)))
                return false;

            int winner = -1;
            int bestRank = int.MaxValue;
            float bestT = 0f, bestHeight = 0f;

            for (int i = 0; i < parts.Length; i++)
            {
                HitPart part = parts[i];
                // This part's bones, turned with the body and moved into its place.
                float3 a = ToWorld(table.Bones[rowBase + part.BoneA], bodyOrigin, bodyFacingSin, bodyFacingCos);
                float3 b = ToWorld(table.Bones[rowBase + part.BoneB], bodyOrigin, bodyFacingSin, bodyFacingCos);
                // ⛔ AFTER ToWorld BOTH SIDES OF THE SOLVER ARE IN ONE FRAME, and
                // that is precisely the obligation Geometry.SegmentCapsule's own
                // doc lays on its caller: it measures distances only and cannot
                // see a mismatch between its two sides.
                if (!Geometry.SegmentCapsule(p0, p1, projRadius, a, b, part.Radius, out float ti)) continue;

                // ⛔ THE ZONE FIRST, t SECOND, PartId THIRD. The order is
                // load-bearing: the capsules are NOT coaxial, so by t alone an
                // arm lying on a weapon in front of a face would take the hit
                // away from the head and pay 0.75 instead of 1.7.
                int rank = ZoneRank(part.Zone);
                bool better = winner < 0 || rank < bestRank
                    || (rank == bestRank && ti < bestT)
                    || (rank == bestRank && ti == bestT && part.PartId < parts[winner].PartId);
                if (!better) continue;

                winner = i; bestRank = rank; bestT = ti;
                // The contact's height is a point ON THE STEP, not the middle of
                // the part: the moment arm is measured from the REAL point of
                // contact (Н61, spec §3.20).
                // ⛔ `.z`, because the step is in the world frame.
                bestHeight = math.lerp(p0.z, p1.z, ti);
            }
            if (winner < 0) return false;

            zone = parts[winner].Zone; mult = parts[winner].DamageMult;
            partId = parts[winner].PartId; t = bestT; hitHeight = bestHeight;
            return true;
        }

        /// Head 0, torso 1, arms and legs 2 — ONE step for the last two (Р505).
        static int ZoneRank(HitZone z) => z == HitZone.Head ? 0 : z == HitZone.Body ? 1 : 2;

        /// ⛔⛔ THE TURN GOES THROUGH Geometry.Rotate AND IS NOT REWRITTEN HERE.
        /// Inlining the matrix `(c*x - s*z, y, s*x + c*z)` would repeat
        /// Geometry.Rotate's body character for character, and the single home
        /// of rotation arithmetic is rule 2's own example in this project.
        ///
        /// ⛔⛔ AND THIS FUNCTION IS THE ONE BORDER BETWEEN TWO FRAMES
        /// (side-quest app-coou). ON THE LEFT the BODY frame, the way
        /// SkeletonAudit measures a skeleton and the way the baker bakes one
        /// (Р527: the baker is carved out of the auditor): height in `.y`, plan
        /// in `.x`/`.z`, because the bones come out of Unity. ON THE RIGHT the
        /// WORLD frame the whole of Ring.Simulation is written in: plan in
        /// `.xy`, height in `.z` (ShotGeometry builds `new float3(AimPoint,
        /// AimHeight)`, and the vertical speed is named VelZ for that reason).
        /// Swapping two components here IS a body laid on its side, and neither
        /// the compiler nor the determinism goldens would see it: the solver
        /// measures distances only. ⇒ the witness is mandatory and is named —
        /// fixture 12a, mutant M392.
        static float3 ToWorld(float3 local, float3 origin, float s, float c)
        {
            // The yaw happens in the BODY's plan, i.e. over the pair (x, z)...
            float2 plane = Geometry.Rotate(new float2(local.x, local.z), s, c);
            // ...and it leaves for the world AS the plan, where the body's own
            // height becomes the third component.
            return origin + new float3(plane, local.y);
        }
    }
}
