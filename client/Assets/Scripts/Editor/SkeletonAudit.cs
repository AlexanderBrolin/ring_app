using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Ring.Editor
{
    /// bd `app-ryxg`, measuring for `app-94sk`: the one place that answers
    /// "what is this body's SKELETON, what does its animation do to it, and
    /// what would it cost to bake that into data the simulation can read".
    ///
    /// WHY IT HAD TO EXIST. `MobFootprintAudit` (bd `app-lhme`) answers how
    /// WIDE a body is drawn, and it answers it off the rest pose on purpose —
    /// a body circle cannot breathe with the animation. The question here is
    /// the opposite one and it has no instrument at all: hit volumes that
    /// follow the BONES need to know how many bones there are, how they are
    /// strung together, how far the pose moves them, and how many numbers it
    /// would take to write that down. Every one of those was a guess before
    /// this file, and `app-94sk` cannot be specified on guesses.
    ///
    /// WHAT IT PRINTS, in four blocks per archetype:
    ///   1. SKELETON — every bone that actually skins a mesh, its depth in the
    ///      hierarchy, its parent, and the length of the segment joining them,
    ///      in the scale the game draws at. A capsule per bone is a capsule per
    ///      SEGMENT, so those lengths are the geometry itself, not trivia.
    ///   2. CLIPS — what the archetype's own animator controller actually
    ///      plays: length, frame rate, frame count, and how many distinct
    ///      transforms each clip animates. A clip nobody plays costs nothing
    ///      to bake, and a bone nobody animates need not be sampled per phase.
    ///   3. POSE TRAVEL — how far each bone moves across one cycle of each
    ///      clip. This is the number that says whether following the pose is
    ///      worth anything at all for that archetype: a leg that travels 0.9 m
    ///      through a run is a leg worth tracking, a chassis that travels
    ///      0.02 m is not.
    ///   4. BAKE BUDGET — the table size in bytes for a range of sampling
    ///      rates, computed from THIS archetype's own bone and clip counts
    ///      rather than from a round number chosen by hand.
    ///
    /// ⛔ THE INSTRUMENT CHECKS ITSELF, AND THAT IS NOT DECORATION (lessons
    /// 680 and 737). Sampling a clip in the editor can silently do nothing —
    /// a humanoid clip needs an `Animator` with the right `Avatar` in place,
    /// and a legacy path needs the opposite. An audit that sampled nothing
    /// would print a pose-travel column of zeroes and read exactly like an
    /// archetype whose animation does not move its bones. So block 3 states
    /// which sampling path answered, and a zero-travel archetype is reported
    /// as SUSPECT rather than as a finding: the quantity whose answer is
    /// already known is that a RUN cycle must move the legs.
    ///
    /// ⚠ IT REPORTS, IT DOES NOT REPAIR — the same rule `MobFootprintAudit`
    /// carries. Nothing here writes an asset, a config or a prefab.
    public static class SkeletonAudit
    {
        const string PrefabsDir = "Assets/Prefabs";

        /// Sampling rates the bake budget is quoted at. 15 Hz is half the
        /// simulation tick, 30 Hz is the tick itself, 60 Hz is one sample per
        /// half-tick — the range any real answer must lie inside, printed so
        /// the choice is made against numbers instead of against a feeling.
        static readonly int[] BudgetRates = { 15, 30, 60 };

        /// Bytes one bone costs per sampled phase: a position in model space,
        /// three floats. A capsule is the segment between two bones plus a
        /// radius, and the radius is per-bone rather than per-phase, so a
        /// phase costs positions only.
        const int BytesPerBonePerPhase = 12;

        /// How many phases of a clip block 3 walks when measuring travel.
        /// Thirty-two is fine enough that a run's leg swing cannot hide
        /// between samples and coarse enough to stay quick on five bodies.
        const int TravelSamples = 32;

        [MenuItem("Ring/Audit/Skeletons and Clips")]
        public static void Run()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== SKELETON AND CLIP AUDIT (bd app-ryxg, for app-94sk) ===");

            AuditOne(report, "Collector", PrefabsDir + "/PlayerDollView.prefab");
            AuditOne(report, "Chaser", PrefabsDir + "/MobChaserView.prefab");
            AuditOne(report, "Gunner", PrefabsDir + "/MobGunnerView.prefab");
            AuditOne(report, "Elite", PrefabsDir + "/MobEliteView.prefab");
            AuditOne(report, "Director", PrefabsDir + "/MobDirectorView.prefab");

            Debug.Log(report.ToString());
        }

        static void AuditOne(System.Text.StringBuilder report, string name, string prefabPath)
        {
            report.AppendLine();
            report.AppendLine($"########## {name} — {prefabPath}");

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                report.AppendLine($"{name}: PREFAB MISSING");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
            {
                report.AppendLine($"{name}: could not instantiate");
                return;
            }

            try
            {
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;

                var skins = new List<SkinnedMeshRenderer>(
                    instance.GetComponentsInChildren<SkinnedMeshRenderer>(true));
                Animator animator = instance.GetComponentInChildren<Animator>(true);

                ReportSkeleton(report, name, instance, skins);
                List<AnimationClip> clips = ReportClips(report, name, animator);
                ReportPoseTravel(report, name, instance, animator, skins, clips);
                ReportBakeBudget(report, name, skins, clips);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        // ---- block 1: the skeleton -------------------------------------------------

        /// Every bone that skins a mesh, printed as a tree with its segment
        /// length. Bones that skin nothing are left out on purpose: a hit
        /// volume hangs on geometry, and a bone no vertex is weighted to
        /// carries none.
        static void ReportSkeleton(System.Text.StringBuilder report, string name,
            GameObject instance, List<SkinnedMeshRenderer> skins)
        {
            var bones = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer smr in skins)
            {
                if (smr == null || smr.bones == null) continue;
                foreach (Transform b in smr.bones)
                    if (b != null) bones.Add(b);
            }

            int allTransforms = instance.GetComponentsInChildren<Transform>(true).Length;
            report.AppendLine(
                $"[1] SKELETON: {bones.Count} skinning bones, {allTransforms} transforms total, "
                + $"{skins.Count} skinned renderer(s), "
                + $"root scale {instance.transform.lossyScale.x:F3}");

            if (bones.Count == 0)
            {
                report.AppendLine("  (no skinned renderer — this body is drawn as static meshes)");
                return;
            }

            // The skinning root: the bone with no skinning parent above it.
            var roots = new List<Transform>();
            foreach (Transform b in bones)
            {
                Transform p = b.parent;
                bool hasBoneParent = false;
                while (p != null)
                {
                    if (bones.Contains(p)) { hasBoneParent = true; break; }
                    p = p.parent;
                }
                if (!hasBoneParent) roots.Add(b);
            }

            foreach (Transform root in roots)
                PrintBoneTree(report, root, bones, 0, instance.transform);
        }

        static void PrintBoneTree(System.Text.StringBuilder report, Transform bone,
            HashSet<Transform> bones, int depth, Transform origin)
        {
            Vector3 local = origin.InverseTransformPoint(bone.position);
            float segment = 0f;
            if (bone.parent != null && bones.Contains(bone.parent))
                segment = Vector3.Distance(bone.position, bone.parent.position);

            report.AppendLine(
                $"  {new string(' ', depth * 2)}{bone.name}"
                + $"  [d{depth}]  pos({local.x:F2}, {local.y:F2}, {local.z:F2})"
                + (segment > 0f ? $"  seg {segment:F3} m" : "  (root)"));

            for (int i = 0; i < bone.childCount; i++)
            {
                Transform c = bone.GetChild(i);
                if (bones.Contains(c)) PrintBoneTree(report, c, bones, depth + 1, origin);
                else DescendToBones(report, c, bones, depth + 1, origin);
            }
        }

        /// A skinning bone can sit under a transform that skins nothing (a
        /// re-parenting node from the import). Walking through those keeps the
        /// printed tree the SKELETON rather than the prefab hierarchy.
        static void DescendToBones(System.Text.StringBuilder report, Transform t,
            HashSet<Transform> bones, int depth, Transform origin)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                if (bones.Contains(c)) PrintBoneTree(report, c, bones, depth, origin);
                else DescendToBones(report, c, bones, depth, origin);
            }
        }

        // ---- block 2: the clips ----------------------------------------------------

        /// What the archetype's controller actually plays. The controller is
        /// the filter that matters: an FBX carries every clip its author shipped
        /// (George alone carries twenty), and a table baked from all of them
        /// would be mostly dances.
        static List<AnimationClip> ReportClips(System.Text.StringBuilder report, string name,
            Animator animator)
        {
            var clips = new List<AnimationClip>();
            if (animator == null)
            {
                report.AppendLine("[2] CLIPS: no Animator on this prefab");
                return clips;
            }

            RuntimeAnimatorController rac = animator.runtimeAnimatorController;
            report.AppendLine(
                $"[2] CLIPS: controller '{(rac != null ? rac.name : "NONE")}', "
                + $"avatar '{(animator.avatar != null ? animator.avatar.name : "NONE")}', "
                + $"humanoid={(animator.avatar != null && animator.avatar.isHuman)}, "
                + $"applyRootMotion={animator.applyRootMotion}");

            if (rac == null) return clips;

            var seen = new HashSet<AnimationClip>();
            foreach (AnimationClip c in rac.animationClips)
                if (c != null && seen.Add(c)) clips.Add(c);

            var asController = rac as AnimatorController;
            if (asController != null)
            {
                report.AppendLine($"  layers: {asController.layers.Length}");
                foreach (AnimatorControllerLayer layer in asController.layers)
                    report.AppendLine(
                        $"    layer '{layer.name}' weight {layer.defaultWeight:F2} "
                        + $"mask={(layer.avatarMask != null ? layer.avatarMask.name : "none")} "
                        + $"states {layer.stateMachine.states.Length}");
            }

            foreach (AnimationClip c in clips)
            {
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(c);
                var paths = new HashSet<string>();
                foreach (EditorCurveBinding b in bindings) paths.Add(b.path);

                report.AppendLine(
                    $"  {c.name}: {c.length:F3} s, {c.frameRate:F0} fps, "
                    + $"{Mathf.RoundToInt(c.length * c.frameRate)} frames, "
                    + $"loop={c.isLooping}, curves {bindings.Length}, "
                    + $"animated transforms {paths.Count}");
            }
            return clips;
        }

        // ---- block 3: how far the pose actually moves a bone ------------------------

        /// The block that decides whether following the pose buys anything for
        /// this archetype, and the one that proves the sampler ran at all.
        ///
        /// TRAVEL IS MEASURED AS THE SPREAD OF ONE BONE'S POSITION over a cycle
        /// — the diagonal of the box its position sweeps, in the body's own
        /// space with the root pinned. That is the quantity a hit volume cares
        /// about: a bone whose spread is smaller than its own capsule radius is
        /// a bone a static pose describes just as well.
        ///
        /// ⛔ A ZERO SPREAD ON A RUN CYCLE IS AN INSTRUMENT FAULT, NOT A
        /// FINDING, and that is why it is labeled SUSPECT here rather than
        /// reported as a measurement. Legs move in a run; if the column says
        /// they do not, the sampler did not sample.
        static void ReportPoseTravel(System.Text.StringBuilder report, string name,
            GameObject instance, Animator animator, List<SkinnedMeshRenderer> skins,
            List<AnimationClip> clips)
        {
            if (clips.Count == 0)
            {
                report.AppendLine("[3] POSE TRAVEL: no clips to sample");
                return;
            }

            var bones = new List<Transform>();
            var seen = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer smr in skins)
            {
                if (smr == null || smr.bones == null) continue;
                foreach (Transform b in smr.bones)
                    if (b != null && seen.Add(b)) bones.Add(b);
            }
            if (bones.Count == 0)
            {
                report.AppendLine("[3] POSE TRAVEL: no skinning bones to follow");
                return;
            }

            // ⛔ THE SAMPLE TARGET IS THE ANIMATOR'S OWN GAME OBJECT, NOT THE
            // PREFAB ROOT, and the first run of this tool is what proved it:
            // every archetype reported a travel of exactly zero, including run
            // cycles, which is the impossible answer this block's SUSPECT label
            // exists to catch. Curve bindings are stored as paths RELATIVE TO
            // the object the clip is played on — `Torso/Chest/Neck/Head`, not
            // `Visual/George/Torso/...` — so sampling the prefab root resolves
            // every path against a child that does not exist and silently
            // applies nothing. A humanoid clip is worse still: its curves are
            // muscle values, and they mean nothing at all without the Avatar,
            // which lives on that same Animator.
            GameObject sampleTarget = animator != null ? animator.gameObject : instance;
            report.AppendLine($"[3] POSE TRAVEL over {TravelSamples} phases per clip "
                + "(bone position spread in body space, root pinned), "
                + $"sampling '{sampleTarget.name}'");

            AnimationMode.StartAnimationMode();
            try
            {
                foreach (AnimationClip clip in clips)
                {
                    var min = new Vector3[bones.Count];
                    var max = new Vector3[bones.Count];
                    for (int i = 0; i < bones.Count; i++)
                    {
                        min[i] = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                        max[i] = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    }

                    for (int s = 0; s < TravelSamples; s++)
                    {
                        float t = clip.length * s / TravelSamples;
                        AnimationMode.BeginSampling();
                        AnimationMode.SampleAnimationClip(sampleTarget, clip, t);
                        AnimationMode.EndSampling();

                        for (int i = 0; i < bones.Count; i++)
                        {
                            Vector3 p = instance.transform.InverseTransformPoint(bones[i].position);
                            min[i] = Vector3.Min(min[i], p);
                            max[i] = Vector3.Max(max[i], p);
                        }
                    }

                    float widest = 0f;
                    string widestName = "-";
                    float total = 0f;
                    int moving = 0;
                    for (int i = 0; i < bones.Count; i++)
                    {
                        float spread = (max[i] - min[i]).magnitude;
                        total += spread;
                        if (spread > 0.01f) moving++;
                        if (spread > widest) { widest = spread; widestName = bones[i].name; }
                    }

                    string verdict = widest < 1e-4f ? "  ⛔ SUSPECT: nothing moved" : "";
                    report.AppendLine(
                        $"  {clip.name}: widest {widest:F3} m ({widestName}), "
                        + $"mean {(total / bones.Count):F3} m, "
                        + $"bones moving >1 cm: {moving}/{bones.Count}{verdict}");
                }
            }
            finally
            {
                AnimationMode.StopAnimationMode();
            }
        }

        // ---- block 4: what a baked table would cost --------------------------------

        /// The table size in bytes, from this archetype's own counts. Quoted
        /// for every bone and for an eleven-bone subset, because the spec's
        /// open question is exactly how many bones a hit volume needs — the
        /// two columns bracket the answer instead of guessing it.
        static void ReportBakeBudget(System.Text.StringBuilder report, string name,
            List<SkinnedMeshRenderer> skins, List<AnimationClip> clips)
        {
            var bones = new HashSet<Transform>();
            foreach (SkinnedMeshRenderer smr in skins)
            {
                if (smr == null || smr.bones == null) continue;
                foreach (Transform b in smr.bones)
                    if (b != null) bones.Add(b);
            }
            if (bones.Count == 0 || clips.Count == 0)
            {
                report.AppendLine("[4] BAKE BUDGET: nothing to bake");
                return;
            }

            float seconds = 0f;
            foreach (AnimationClip c in clips) seconds += c.length;

            report.AppendLine($"[4] BAKE BUDGET: {bones.Count} bones, {clips.Count} clips, "
                + $"{seconds:F2} s of animation total, {BytesPerBonePerPhase} B per bone-phase");
            foreach (int rate in BudgetRates)
            {
                int phases = Mathf.CeilToInt(seconds * rate);
                long all = (long)phases * bones.Count * BytesPerBonePerPhase;
                long eleven = (long)phases * Mathf.Min(11, bones.Count) * BytesPerBonePerPhase;
                report.AppendLine(
                    $"    {rate} Hz: {phases} phases -> all bones {all / 1024f:F1} KB, "
                    + $"11 bones {eleven / 1024f:F1} KB");
            }
        }
    }
}
