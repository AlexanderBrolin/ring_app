using System.Collections.Generic;
using System.IO;
using Ring.Simulation.Core;
using UnityEditor;
using UnityEngine;

namespace Ring.Editor
{
    /// bd `app-w4ca` (spec §3.5, integrity level 3): THE "REBAKE AND COMPARE"
    /// GATE. It bakes all five bodies afresh and holds each result beside the
    /// committed artifact; a disagreement is a NON-ZERO EXIT CODE, and so is an
    /// exception.
    ///
    /// ⛔⛔ IT IS MANDATORY RATHER THAN DESIRABLE, AND THE REASON IS A PROPERTY
    /// OF THE ARTIFACT, NOT A PREFERENCE. `.posetable` is BINARY and lives
    /// under LFS, so a change to it is INVISIBLE IN A PULL REQUEST — the diff
    /// shows a pointer. Level 1 (the `ScriptedImporter`'s version field and its
    /// dependency on the source `.fbx`) cannot stand in: that importer does not
    /// bake at all, it only re-reads the committed bytes, so a table that no
    /// longer matches its clips re-imports just as happily as one that does.
    /// This is the only reader a reviewer has.
    ///
    /// ⚠ WHAT IT COMPARES IS THE TABLE, NOT THE FILE. Two bakes of one skeleton
    /// must agree number for number; a difference in how the bytes are laid out
    /// on disk is the serializer's business and would be caught by its own
    /// round trip. Comparing tables also means the failure can NAME what moved
    /// — a body, a row, a bone — instead of saying "the files differ".
    ///
    /// Run it from the menu, or in batchmode:
    ///   Unity -batchmode -nographics -quit -projectPath client \
    ///         -executeMethod Ring.Editor.PoseBakeVerify.Verify
    public static class PoseBakeVerify
    {
        const string PosesDir = "Assets/Data/Poses";

        [MenuItem("Ring/Audit/Verify Pose Tables")]
        public static void Verify()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== POSE BAKE VERIFY (bd app-w4ca, spec 3.5 level 3) ===");

            List<string> failures;
            try
            {
                failures = Compare(report);
            }
            catch (System.Exception e)
            {
                // ⛔ AN EXCEPTION IS A FAILURE, NOT A CRASH TO BE READ IN THE
                // LOG — spec §3.5 says it in as many words: an exception is a
                // non-zero exit code. A gate that throws and exits zero is a
                // gate that passes on a broken baker.
                Debug.LogError(report + "\nVERIFY THREW: " + e);
                Exit(2);
                return;
            }

            if (failures.Count == 0)
            {
                report.AppendLine("✅ all five tables reproduce byte for byte");
                Debug.Log(report.ToString());
                return;
            }

            foreach (string f in failures) report.AppendLine("⛔ " + f);
            report.AppendLine(
                $"⛔⛔ {failures.Count} of {AnimatorCatalog.Bodies.Count} tables DISAGREE with the "
                + "committed artifact — the clips moved since the bake, or the baker did. Run "
                + "Ring/Bootstrap/Pose Table and commit the result.");
            Debug.LogError(report.ToString());
            Exit(1);
        }

        /// Every body, so one failure does not hide the others.
        static List<string> Compare(System.Text.StringBuilder report)
        {
            var failures = new List<string>();
            foreach (AnimatorCatalog.BodyEntry body in AnimatorCatalog.Bodies)
            {
                string path = $"{PosesDir}/{body.Kind}.posetable";
                if (!File.Exists(path))
                {
                    failures.Add($"{body.Kind}: no committed artifact at {path}");
                    continue;
                }

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(body.PrefabPath);
                if (prefab == null)
                {
                    failures.Add($"{body.Kind}: prefab missing at {body.PrefabPath}");
                    continue;
                }

                PoseTable committed = PoseTableImporter.Read(File.ReadAllBytes(path));
                PoseTable fresh = PoseBaker.BakeOne(prefab);

                string diff = FirstDifference(in committed, in fresh);
                if (diff != null) { failures.Add($"{body.Kind}: {diff}"); continue; }

                report.AppendLine(
                    $"  {body.Kind}: {fresh.BoneCount} bones x "
                    + $"{fresh.Bones.Length / fresh.BoneCount} rows, "
                    + $"checksum 0x{fresh.Checksum:X16} — matches");
            }
            return failures;
        }

        /// The FIRST number that moved, named by body part rather than counted.
        /// ⛔ It reports one difference and stops on purpose: a table whose
        /// every row moved would otherwise print a wall nobody reads, and the
        /// first disagreement is the one worth looking at.
        static string FirstDifference(in PoseTable a, in PoseTable b)
        {
            if (a.BoneCount != b.BoneCount)
                return $"bone count {a.BoneCount} committed against {b.BoneCount} fresh";
            if (a.ClipFirstRow.Length != b.ClipFirstRow.Length)
                return $"clip count {a.ClipFirstRow.Length - 1} committed against "
                    + $"{b.ClipFirstRow.Length - 1} fresh";
            for (int i = 0; i < a.ClipFirstRow.Length; i++)
                if (a.ClipFirstRow[i] != b.ClipFirstRow[i])
                    return $"clip {i} starts at row {a.ClipFirstRow[i]} committed against "
                        + $"{b.ClipFirstRow[i]} fresh";

            if (a.Bones.Length != b.Bones.Length)
                return $"{a.Bones.Length / a.BoneCount} rows committed against "
                    + $"{b.Bones.Length / b.BoneCount} fresh";
            for (int i = 0; i < a.Bones.Length; i++)
            {
                if (a.Bones[i].Equals(b.Bones[i])) continue;
                return $"row {i / a.BoneCount}, bone {i % a.BoneCount}: "
                    + $"{a.Bones[i]} committed against {b.Bones[i]} fresh";
            }

            if (a.BlendThresholds.Length != b.BlendThresholds.Length)
                return $"{a.BlendThresholds.Length} blend thresholds committed against "
                    + $"{b.BlendThresholds.Length} fresh";
            for (int i = 0; i < a.BlendThresholds.Length; i++)
                if (a.BlendThresholds[i] != b.BlendThresholds[i])
                    return $"blend threshold {i}: {a.BlendThresholds[i]} committed against "
                        + $"{b.BlendThresholds[i]} fresh";

            // app-94sk T6c: the rates, per clip.
            if (a.ClipRate.Length != b.ClipRate.Length)
                return $"{a.ClipRate.Length} clip rates committed against {b.ClipRate.Length} fresh";
            for (int i = 0; i < a.ClipRate.Length; i++)
                if (a.ClipRate[i] != b.ClipRate[i])
                    return $"clip {i} rate: {a.ClipRate[i]} committed against {b.ClipRate[i]} fresh";

            if (a.UpperLayerMask.Length != b.UpperLayerMask.Length)
                return $"{a.UpperLayerMask.Length} mask words committed against "
                    + $"{b.UpperLayerMask.Length} fresh";
            for (int i = 0; i < a.UpperLayerMask.Length; i++)
                if (a.UpperLayerMask[i] != b.UpperLayerMask[i])
                    return $"aim mask word {i}: 0x{a.UpperLayerMask[i]:X16} committed against "
                        + $"0x{b.UpperLayerMask[i]:X16} fresh";

            // ⚠ LAST, NOT FIRST: the checksum says only THAT something moved.
            // Reaching it means every number above agreed, so a mismatch here is
            // a stale seal rather than a stale table — a different defect, and
            // it deserves its own sentence.
            if (a.Checksum != b.Checksum)
                return $"checksum 0x{a.Checksum:X16} committed against 0x{b.Checksum:X16} fresh, "
                    + "while every number agrees — the committed seal is stale";
            return null;
        }

        /// ⚠ `EditorApplication.Exit` rather than a thrown exception: `-quit`
        /// exits zero even after an unhandled exception in `-executeMethod`, so
        /// a gate that only throws is a gate that always passes.
        static void Exit(int code) => EditorApplication.Exit(code);
    }
}
