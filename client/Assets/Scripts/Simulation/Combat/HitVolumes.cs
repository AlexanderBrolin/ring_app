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
        /// `pose` is this body's READY pose on this tick -- `pose[0..BoneCount)`
        /// as PoseTable.Sample wrote it (app-94sk T6b; until then callers
        /// handed in a row index, and the rest row at that). A row index cannot
        /// name a pose any more: the lower layer is TWO rows mixed by a share,
        /// with the aim layer laid over them on the masked bones (spec §3.6).
        /// The table still travels for its BoneCount, which bounds how much of
        /// a memo buffer belongs to this body. `t` is the fraction of the step,
        /// in the same [p0, p1] parameterization the caller's min-scan uses.
        ///
        /// ⛔⛔ TWO FRAMES MEET HERE, AND THE BORDER BETWEEN THEM IS ToWorld
        /// (side-quest app-coou). The pose's bones are in the BODY frame, the
        /// way SkeletonAudit measures a skeleton and the way the baker
        /// delivers one: height in `.y`, plan in `.x`/`.z`. Everything else —
        /// `bodyOrigin`, `p0`/`p1`, `hitHeight` — is in the WORLD frame the whole
        /// of Ring.Simulation is written in: plan in `.xy`, height in `.z`
        /// (ShotGeometry builds `new float3(AimPoint, AimHeight)`, and the
        /// vertical speed is named VelZ for that reason). ⇒ THE CALLER BUILDS
        /// ITS STEP THE ORDINARY WAY and weaves nothing; ToWorld, and only
        /// ToWorld, transposes. Witness: fixture 12a, mutant M392.
        ///
        /// `bodyFacingSin`/`bodyFacingCos` come out of YawOf, from the body's
        /// course and its rig's forward; `bodyTilt` is the lean (PlayerState.
        /// Tilt's own doc: length = angle, direction = the side the body goes
        /// down towards, in the WORLD's plan).
        public static bool Resolve(HitPart[] parts, in PoseTable table, float3[] pose, float2 bodyTilt,
            float3 bodyOrigin, float bodyFacingSin, float bodyFacingCos,
            float3 p0, float3 p1, float projRadius,
            out HitZone zone, out float mult, out float hitHeight, out byte partId, out float t)
        {
            zone = HitZone.None; mult = 1f; hitHeight = 0f; partId = 0; t = 0f;
            if (parts == null || parts.Length == 0) return false;   // no volumes, nothing to hit
            int bones = table.BoneCount;
            if (table.Bones == null || bones <= 0) return false;
            if (pose == null || pose.Length < bones) return false;   // no pose, nothing stands anywhere

            // ⛔ THE CHEAP NECESSARY CONDITION GOES THROUGH THE EXISTING HOME
            // RATHER THAN A SECOND ONE OF ITS OWN: the height gate lives in
            // HitZones.Overlaps, the barrier calls it, and a twin here would be
            // exactly the duplication those two homes were separated to avoid.
            // The ceiling is the crown of the LIVE pose (HitParts.PoseTop), not
            // RestTop: the latter describes the REST pose and is wrong in
            // motion. Witness: the instrument fixture 8 (T6c) — the gate must
            // SHORTEN the work without moving a single outcome.
            // ⛔ THE STEP'S HEIGHT IS `.z`: p0/p1 arrive in the WORLD frame.
            // ⚠ AND THE GATE'S FLOOR IS NOT A GAP, THOUGH A CAPSULE NOW HANGS
            // BELOW THE GROUND (the chaser's leg reaches -0.35 once its radius is
            // counted). Overlaps refuses only a step lying wholly under
            // `-radius`, and a live round never has one: ProjectileSystem removes
            // it the moment its underside reaches the floor, so its center stays
            // at or above its own radius while it exists. The gate therefore
            // cannot refuse a step the honest capsule test would accept, which is
            // what spec §3.4 requires of it.
            // ⛔ AND THE GATE IS ASKED OF AN UPRIGHT BODY ONLY (app-94sk T6b):
            // the crown of a LEANING body is not a function of the pose's
            // heights -- every bone drops by its own plan offset along the
            // fall -- and a gate that read the untilted crown would refuse a
            // step the capsule test accepts, which is the one thing spec §3.4
            // forbids it. A leaning body skips straight to the capsules; the
            // gate is an optimization, not a witness, and leaning bodies are
            // the rare case.
            Lean lean = LeanOf(bodyTilt);
            if (!lean.Leaning && !HitZones.Overlaps(p0.z, p1.z, projRadius,
                    HitParts.PoseTop(parts, pose, bones)))
                return false;

            int winner = -1;
            int bestRank = int.MaxValue;
            float bestT = 0f, bestHeight = 0f;

            for (int i = 0; i < parts.Length; i++)
            {
                HitPart part = parts[i];
                // A bone past the body's count belongs to nobody (the same
                // guard HitParts.PoseTop keeps, and for validation rule 6's
                // own reason there: a hand-built fixture reaches here first).
                if (part.BoneA >= bones || part.BoneB >= bones) continue;
                // This part's bones, turned with the body, leaned with it and
                // moved into its place.
                float3 a = Place(pose[part.BoneA], bodyOrigin, bodyFacingSin, bodyFacingCos, in lean);
                float3 b = Place(pose[part.BoneB], bodyOrigin, bodyFacingSin, bodyFacingCos, in lean);
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

        /// The yaw that turns a rig's forward onto the body's course, as the
        /// (sin, cos) pair Resolve takes (app-94sk T6b).
        ///
        /// ⛔⛔ THE TABLE'S OWN ORIENTATION IS NOT "FACING +x". The baker
        /// measures bones in the prefab root's frame at identity rotation,
        /// where a rig looks down its modeled forward -- +z for the mechs
        /// and the Sci-Fi kit, -z for the collector's UAL2 rig
        /// (BakedClips.CollectorForward / MobForward, the same fact
        /// Presentation carries as GameFeelConfig.PlayerYawOffsetDeg = 180
        /// against MechYawOffsetDeg = 0). The identity yaw is therefore a
        /// mob facing +y of the simulation's plan, and the plan's own
        /// wording "sin = Dir.y, cos = Dir.x" would have turned every body a
        /// quarter turn away from its doll (and the collector three).
        ///
        /// THE LAW: the rotation that carries `rigForward` (a unit vector in
        /// the body plan `(x, z)`) onto `dir` (a unit vector in the world plan
        /// `(x, y)`), read off the pair the way Geometry.Rotate applies it --
        /// `cos` is their dot product, `sin` their cross product. For a +z rig
        /// that is `(sin, cos) = (-Dir.x, Dir.y)`; for a -z rig `(Dir.x,
        /// -Dir.y)`. HitVolumeTests.TheVolumesTurnTheWayTheDollTurns pins it
        /// against the doll's own `LookRotation * AngleAxis(offset)`.
        ///
        /// `normalizesafe`, AND THE FALLBACK IS THE RIG'S OWN FORWARD: a
        /// course of zero length (a mob record the client has not been given
        /// a course for, bd app-4io9) means "as the table stands", the
        /// identity -- never a plan collapsed onto the body's axis.
        internal static void YawOf(float2 dir, float2 rigForward, out float sin, out float cos)
        {
            float2 d = math.normalizesafe(dir, rigForward);
            cos = rigForward.x * d.x + rigForward.y * d.y;
            sin = rigForward.x * d.y - rigForward.y * d.x;
        }

        /// A lean, prepared once per body rather than once per bone: the side
        /// the body goes down to (world plan, unit), and the angle's sine and
        /// cosine. `Leaning` is false under Impact.RestEpsilon, the same
        /// threshold SimSpace.TiltRotation stands a doll up straight at.
        internal readonly struct Lean
        {
            public readonly float2 Axis;
            public readonly float Sin, Cos;
            public readonly bool Leaning;

            public Lean(float2 axis, float sin, float cos, bool leaning)
            {
                Axis = axis; Sin = sin; Cos = cos; Leaning = leaning;
            }
        }

        internal static Lean LeanOf(float2 tilt)
        {
            float len = math.length(tilt);
            if (!(len > Impact.RestEpsilon)) return new Lean(float2.zero, 0f, 1f, false);
            return new Lean(tilt / len, math.sin(len), math.cos(len), true);
        }

        /// app-94sk T6b: HOW WIDE THE BROAD PHASE HAS TO BE FOR THIS BODY AS
        /// IT STANDS NOW. Upright, the table's own reach (GatherRadius,
        /// validation rule 9 -- taken over every live row, so no pose of a
        /// standing body leaves it). Leaning, that circle is WRONG: a chaser
        /// on his side at 85 degrees has his chest bone 1.35 m from his origin
        /// along the fall and that capsule's far end 2.6 m out, against a
        /// standing circle of 0.92, and a gather that kept the standing circle
        /// would never ask the volumes about him at all -- the toppled body
        /// "immune to flat fire" that fixture 26a forbids. So a leaning body's
        /// circle is measured off its READY pose: the furthest any volume
        /// reaches in the plan once turned and leaned, plus that volume's
        /// radius -- and never less than the standing circle. Exact rather
        /// than a bound (the plan distance is convex along a capsule's axis,
        /// so its supremum is at an end), and only ever computed for a body
        /// that leans; the caller samples the pose for it the way it samples
        /// for Resolve, and asks `LeanOf(tilt).Leaning` BEFORE packing a key,
        /// so an upright body -- every body but a knocked one -- costs the
        /// broad phase nothing it did not already pay. The yaw and the lean
        /// are computed here, once per leaning body, from the same inputs
        /// Resolve takes: ONE home of "where does this body's volume reach".
        internal static float GatherRadiusFor(HitPart[] parts, float3[] pose, int bones,
            float2 dir, float2 rigForward, float2 tilt, float gatherRadius)
        {
            Lean lean = LeanOf(tilt);
            if (!lean.Leaning || parts == null || pose == null || pose.Length < bones) return gatherRadius;
            YawOf(dir, rigForward, out float bodyFacingSin, out float bodyFacingCos);
            float widest = gatherRadius;
            for (int i = 0; i < parts.Length; i++)
            {
                HitPart part = parts[i];
                if (part.BoneA >= bones || part.BoneB >= bones) continue;
                float3 a = Place(pose[part.BoneA], float3.zero, bodyFacingSin, bodyFacingCos, in lean);
                float3 b = Place(pose[part.BoneB], float3.zero, bodyFacingSin, bodyFacingCos, in lean);
                float reach = math.max(math.length(a.xy), math.length(b.xy)) + part.Radius;
                if (reach > widest) widest = reach;
            }
            return widest;
        }

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
        ///
        /// ⛔⛔ AND SINCE app-94sk T6b IT IS ALSO WHERE THE LEAN IS APPLIED --
        /// AFTER THE YAW, IN THE WORLD. The tilt vector says which way IN THE
        /// WORLD'S PLAN the body goes down (PlayerState.Tilt's own doc), and
        /// the doll composes it OUTERMOST, about an axis fixed in the world
        /// (`SimSpace.TiltRotation(m.Tilt) * facing`, MobVisual/PlayerVisual).
        /// The plan's snippet leaned the bones in the BODY frame first and
        /// yawed afterwards; on a turned body that sends the fall a quarter
        /// turn off the side the blow pushed it. The order here is the doll's,
        /// and HitVolumeTests.ALeanIsAppliedInTheWorldAfterTheTurn pins it
        /// against the doll's own quaternions. The arithmetic: the component
        /// of the yawed point ALONG the fall and its height rotate together by
        /// the angle (the top goes towards the fall, the fall's far side comes
        /// up), the component ACROSS the fall stays -- a rotation about the
        /// horizontal axis perpendicular to the fall, through the body's
        /// origin on the ground, which is the doll's pivot too.
        ///
        /// `internal`, not private, so the two frame pins in HitVolumeTests
        /// can ask this function directly what the doll's arithmetic answers;
        /// Resolve itself goes through `Place` with a lean prepared once.
        internal static float3 ToWorld(float3 local, float3 origin, float s, float c, float2 tilt)
            => Place(local, origin, s, c, LeanOf(tilt));

        static float3 Place(float3 local, float3 origin, float s, float c, in Lean lean)
        {
            // The yaw happens in the BODY's plan, i.e. over the pair (x, z)...
            float2 plane = Geometry.Rotate(new float2(local.x, local.z), s, c);
            // ...and it leaves for the world AS the plan, where the body's own
            // height becomes the third component.
            float3 p = new float3(plane, local.y);
            if (lean.Leaning)
            {
                float along = p.x * lean.Axis.x + p.y * lean.Axis.y;
                float newAlong = along * lean.Cos + p.z * lean.Sin;
                float newZ = p.z * lean.Cos - along * lean.Sin;
                float d = newAlong - along;
                p = new float3(p.x + d * lean.Axis.x, p.y + d * lean.Axis.y, newZ);
            }
            return origin + p;
        }
    }
}
