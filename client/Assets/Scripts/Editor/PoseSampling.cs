using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Ring.Editor
{
    /// bd `app-w4ca` (spec §3.5): the shared half of the auditor and the baker —
    /// HOW a body's pose is taken off a clip, and in WHICH FRAME it is written
    /// down. Both tools answer those two questions identically or their numbers
    /// drift apart silently, which is the whole reason this file exists rather
    /// than a second copy inside `PoseBaker`.
    ///
    /// ⛔ ONLY `SkeletonAudit` SITS HERE, AND `MobFootprintAudit` DOES NOT —
    /// that is a decision with a recorded cause, not an omission. Its own doc
    /// says it: "SKINNED MESHES ARE MEASURED THROUGH `bounds`, WHICH IS THE
    /// POSE THE MODEL WAS AUTHORED IN… a body circle cannot breathe with the
    /// animation", and "the instance below is never driven by an `Animator`".
    /// A tool that never samples a pose has nothing to share with a sampler.
    ///
    /// ⚠ WHY ITS OWN FILE RATHER THAN `EditorBootstrapUtils`: that one is
    /// signed "Shared guard primitives of the IDEMPOTENT BOOTSTRAPS", and its
    /// precedents for being lifted into (`EnsureChild`, `FindComponentInScene`)
    /// are guards, not measurements. Sampling is not a guard.
    ///
    /// ⛔⛔ THE SAMPLE TARGET AND THE MEASURE ORIGIN ARE A PAIR, NOT ONE VALUE,
    /// and the two halves carry different names on purpose (spec §3.5 round-3
    /// findings A-C5/B-I6):
    ///
    ///   sampleTarget  — THE OBJECT THAT CARRIES THE `Animator`. Curve bindings
    ///                   are stored as paths RELATIVE TO the object a clip is
    ///                   played on, so sampling the prefab root resolves every
    ///                   path against a child that does not exist and silently
    ///                   applies NOTHING. The first run of `SkeletonAudit`
    ///                   proved this the expensive way: every archetype, run
    ///                   cycles included, reported a travel of exactly zero.
    ///                   A humanoid clip is worse still — its curves are muscle
    ///                   values that mean nothing without the Avatar, and the
    ///                   Avatar lives on that same `Animator`.
    ///
    ///   measureOrigin — THE PREFAB ROOT, zeroed and set to identity. Positions
    ///                   come back in ITS space, which is the body's own frame.
    ///
    /// ⛔ THE COST OF CONFUSING THEM IS MEASURABLE, WHICH IS WHY FIXTURE 28
    /// PINS THEM APART: a x100 scale lives on the mesh nodes inside the FBX,
    /// and the gunner's prefab carries a 0.76 scale override on its FBX
    /// instance. A baker measuring from the `Animator`'s object instead of the
    /// prefab root disagrees with the auditor BY 0.76 ON THE GUNNER — and every
    /// number it prints still looks plausible.
    public static class PoseSampling
    {
        /// Bones that skin a mesh but are NOT the body, excluded from the reach
        /// figure. Two families, and both were found by reading the auditor's
        /// first output rather than assumed:
        ///
        ///   * RIG CONTROLS — IK targets and pole vectors. They drive the
        ///     solver, not the silhouette, and they travel absurdly: the
        ///     gunner's `PoleTarget.L` reaches 6.42 m from his own axis in the
        ///     death clip, against a drawn body under a meter wide. A reach
        ///     taken over those would size the gather circle to a control point
        ///     nobody can shoot.
        ///   * FINGERS — real geometry, but no hit volume will ever hang on one
        ///     (eleven volumes for the collector, and not a knuckle among
        ///     them), while a fingertip is the furthest-travelling bone of the
        ///     whole skeleton in a death animation.
        ///
        /// ⚠ `Ring` here is the finger, not the arena: `Ring2.L` is a chaser
        /// knuckle. The sweep convention records that match as legal.
        static readonly string[] NonBodyBoneMarks =
        {
            "PoleTarget", "_PT", "_IK", "IK_",
            "Index", "Middle", "Pinky", "Ring", "Thumb", "Palm",
            "index_", "middle_", "pinky_", "ring_", "thumb_",
        };

        public static bool IsBodyBone(string boneName)
        {
            for (int i = 0; i < NonBodyBoneMarks.Length; i++)
                if (boneName.Contains(NonBodyBoneMarks[i])) return false;
            return true;
        }

        /// Every bone that actually skins a mesh, IN A STABLE ORDER — the index
        /// of a bone here IS its column in the baked table, so the order is a
        /// contract rather than a convenience.
        ///
        /// ⛔ ORDER COMES FROM THE RENDERERS, NOT FROM A HASH SET. The auditor
        /// used to collect these three times in two different shapes: twice
        /// into a `HashSet` (no order at all) and once into this List+Set pair.
        /// Only the ordered shape can index a table, so the ordered one is the
        /// one that survived; the two set-shaped sites now build their set FROM
        /// this list, which keeps `Contains` where recursion needs it without a
        /// second walk of the hierarchy.
        ///
        /// ⚠ Bones that skin nothing are left out on purpose: a hit volume
        /// hangs on geometry, and a bone no vertex is weighted to carries none.
        public static List<Transform> CollectBones(GameObject instance)
        {
            var bones = new List<Transform>();
            var seen = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer smr in
                     instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.bones == null) continue;
                foreach (Transform b in smr.bones)
                    if (b != null && seen.Add(b)) bones.Add(b);
            }
            return bones;
        }

        /// The clips this archetype's CONTROLLER actually plays.
        ///
        /// ⛔ THE CONTROLLER IS THE FILTER THAT MATTERS, and this is the whole
        /// reason the baker may not read the FBX instead: an FBX carries every
        /// clip its author shipped (George alone carries twenty), and a table
        /// baked from all of them would be mostly dances. Without one home for
        /// this, the baker's "wide set" and the auditor's "reach filter" would
        /// drift apart silently — that is, `GatherRadius` would be computed
        /// over clips the auditor never measured.
        ///
        /// ⚠ Duplicates are dropped: one clip reachable from two states is one
        /// clip to bake.
        public static List<AnimationClip> CollectClips(Animator animator)
        {
            var clips = new List<AnimationClip>();
            if (animator == null) return clips;

            RuntimeAnimatorController rac = animator.runtimeAnimatorController;
            if (rac == null) return clips;

            var seen = new HashSet<AnimationClip>();
            foreach (AnimationClip c in rac.animationClips)
                if (c != null && seen.Add(c)) clips.Add(c);
            return clips;
        }

        /// One pose: sample `clip` at `time` onto `sampleTarget`, then write
        /// every bone's position IN `measureOrigin`'s SPACE into `into`, in the
        /// order `CollectBones` returned.
        ///
        /// ⛔ THE ANIMATION MODE IS THE CALLER'S, NOT THIS METHOD'S. Both
        /// callers walk many phases of many clips, and
        /// `StartAnimationMode`/`StopAnimationMode` belong around the whole
        /// walk — that is how the auditor already had it, and bracketing each
        /// single sample instead would be both slower and a different contract.
        /// `BeginSampling`/`EndSampling` DO belong here: they bracket one
        /// sample, which is exactly what this method is.
        public static void SamplePose(GameObject sampleTarget, Transform measureOrigin,
            IReadOnlyList<Transform> bones, AnimationClip clip, float time, float3[] into)
        {
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(sampleTarget, clip, time);
            AnimationMode.EndSampling();

            for (int i = 0; i < bones.Count; i++)
            {
                Vector3 p = measureOrigin.InverseTransformPoint(bones[i].position);
                into[i] = new float3(p.x, p.y, p.z);
            }
        }
    }
}
