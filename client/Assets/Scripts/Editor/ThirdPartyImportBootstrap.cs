using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using TP = Ring.Editor.ThirdPartyAssetPostprocessor;

namespace Ring.Editor
{
    /// Idempotent validator of Assets/ThirdParty imports (assets-mvp plan T5).
    /// The postprocessor is the single writer of import settings; on mismatch
    /// this class re-triggers it via a ForceUpdate reimport and re-checks —
    /// it never assigns importer fields itself. Failures throw: thrown from an
    /// -executeMethod, that yields a non-zero batch exit code (unlike exceptions
    /// inside the import pipeline, which Unity swallows).
    public static class ThirdPartyImportBootstrap
    {
        [MenuItem("Ring/Bootstrap/Validate ThirdParty Import")]
        public static void Apply()
        {
            var errors = new List<string>();
            int healed = 0;
            CheckJunk(errors);

            Avatar doll = null;
            if (AssetDatabase.IsValidFolder(TP.UalRoot.TrimEnd('/')))
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(TP.DollPath) == null)
                    throw new InvalidOperationException(
                        "ThirdPartyImportBootstrap: doll FBX missing at " + TP.DollPath);
                AssetDatabase.ImportAsset(TP.DollPath,
                    ImportAssetOptions.ForceSynchronousImport);
                doll = TP.LoadDollAvatar();
                if (!doll.isValid || !doll.isHuman)
                    throw new InvalidOperationException(
                        "ThirdPartyImportBootstrap: doll avatar is not a valid " +
                        "humanoid (bone auto-mapping failed?) at " + TP.DollPath);
                Debug.Log("[ThirdParty] doll avatar OK: isValid && isHuman");
            }

            string[] humanoids = TP.FindModels(TP.UalRoot)
                .Concat(TP.FindModels(TP.Ual2Root))
                .Where(p => p != TP.DollPath).ToArray();
            string[] robots = TP.FindModels(TP.MechRoot)
                .Concat(TP.FindModels(TP.SciFiRoot)).ToArray();

            foreach (string path in humanoids)
                ValidateModel(path, doll, errors, ref healed);
            foreach (string path in robots)
                ValidateModel(path, null, errors, ref healed);
            if (doll != null && AssetImporter.GetAtPath(TP.DollPath) is ModelImporter dollImporter)
                ValidateClips(TP.DollPath, dollImporter, errors, ref healed);
            int remapped = RemapPackMaterials(robots);

            Debug.Log($"[ThirdParty] validation done: {humanoids.Length} humanoid, " +
                      $"{robots.Length} robot/prop models, healed {healed}, " +
                      $"remapped {remapped}, errors {errors.Count}");
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "ThirdPartyImportBootstrap: validation failed:\n  " +
                    string.Join("\n  ", errors));
        }

        static void ValidateModel(string path, Avatar doll,
            List<string> errors, ref int healed)
        {
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer))
            {
                errors.Add("no ModelImporter: " + path);
                return;
            }
            if (!Check(path, importer, doll, out string why))
            {
                // Heal through the single writer: reimport re-runs the postprocessor.
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                healed++;
                importer = (ModelImporter)AssetImporter.GetAtPath(path);
                if (!Check(path, importer, doll, out why))
                {
                    errors.Add(why + ": " + path);
                    return;
                }
            }
            ValidateClips(path, importer, errors, ref healed);
            ReportEvents(path);
        }

        static bool Check(string path, ModelImporter importer, Avatar doll, out string why)
        {
            why = null;
            if (TP.IsHumanoidPath(path))
            {
                if (importer.animationType != ModelImporterAnimationType.Human)
                { why = "animationType != Human"; return false; }
                if (path == TP.FemaleDollPath)
                {
                    Avatar own = AssetDatabase.LoadAllAssetsAtPath(path)
                        .OfType<Avatar>().FirstOrDefault();
                    if (own == null || !own.isValid || !own.isHuman)
                    { why = "female mannequin avatar invalid"; return false; }
                    return true;
                }
                if (doll != null && importer.sourceAvatar != doll)
                { why = "sourceAvatar != doll avatar"; return false; }
                return true;
            }
            bool robot = importer.importedTakeInfos.Length > 0;
            ModelImporterAnimationType expected = robot
                ? ModelImporterAnimationType.Generic
                : ModelImporterAnimationType.None;
            if (importer.animationType != expected)
            { why = $"animationType != {expected}"; return false; }
            return true;
        }

        static void ValidateClips(string path, ModelImporter importer,
            List<string> errors, ref int healed)
        {
            if (importer.defaultClipAnimations.Length == 0) return;
            if (!ClipsMatchRules(importer))
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                healed++;
                importer = (ModelImporter)AssetImporter.GetAtPath(path);
                if (!ClipsMatchRules(importer))
                {
                    errors.Add("clip flags diverge from ApplyClipRules: " + path);
                    return;
                }
            }
            int loops = importer.clipAnimations.Count(c => c.loopTime);
            Debug.Log($"[ThirdParty] {path}: {importer.clipAnimations.Length} clips, " +
                      $"{loops} looping");
        }

        static bool ClipsMatchRules(ModelImporter importer)
        {
            ModelImporterClipAnimation[] configured = importer.clipAnimations;
            if (configured.Length != importer.defaultClipAnimations.Length)
                return false; // postprocessor never ran on this asset
            foreach (ModelImporterClipAnimation clip in configured)
            {
                bool loop = TP.ShouldLoop(importer.assetPath, clip.name);
                if (clip.loopTime != loop || clip.loopPose != loop) return false;
                if (!clip.lockRootRotation || !clip.lockRootHeightY
                    || !clip.lockRootPositionXZ || !clip.keepOriginalOrientation
                    || !clip.keepOriginalPositionY || !clip.keepOriginalPositionXZ)
                    return false;
            }
            return true;
        }

        static void ReportEvents(string path)
        {
            int events = AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                .OfType<AnimationClip>().Sum(c => c.events.Length);
            if (events > 0)
                Debug.Log($"[ThirdParty] {path}: {events} AnimationEvents " +
                          "(never wired to gameplay — visual-only policy)");
        }

        /// Milestone-2 fix: the packs' FBX material descriptions reference
        /// external textures that Unity does not bind (mech pack, some prop
        /// atlases) — models render flat grey. For every artifact material
        /// without a base map we create a URP Lit material in _Ring/Materials/
        /// wired to the pack texture found by naming convention, and remap the
        /// importer to it (persistent — Phase B instantiations get it for free).
        /// UAL mannequins are authored textureless and are not touched.
        static int RemapPackMaterials(string[] robotModels)
        {
            int remapped = 0;
            foreach (string path in robotModels)
            {
                if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) continue;
                bool changed = false;
                foreach (Material artifact in AssetDatabase.LoadAllAssetsAtPath(path)
                             .OfType<Material>())
                {
                    if (artifact.HasProperty("_BaseMap")
                        && artifact.GetTexture("_BaseMap") != null) continue;
                    Texture baseMap = FindPackTexture(path, artifact.name, "_BaseColor")
                        ?? FindPackTexture(path, artifact.name, "");
                    if (baseMap == null)
                    {
                        Debug.LogWarning("[ThirdParty] no conventional texture for " +
                            $"material '{artifact.name}' of {path} — left as is");
                        continue;
                    }
                    Texture emissive = FindPackTexture(path, artifact.name, "_Emissive");
                    Material remap = GetOrCreateRemapMaterial(
                        artifact.name, baseMap, emissive);
                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(
                        typeof(Material), artifact.name), remap);
                    changed = true;
                }
                if (changed)
                {
                    importer.SaveAndReimport();
                    remapped++;
                    Debug.Log("[ThirdParty] materials remapped: " + path);
                }
            }
            return remapped;
        }

        /// Convention lookup in the pack's Textures/ folder:
        /// "George_Texture" (+"") → George_Texture.png;
        /// "MI_Trim_02" (+"_BaseColor") → T_Trim_02_BaseColor.png.
        static Texture FindPackTexture(string modelPath, string matName, string suffix)
        {
            string root = modelPath.StartsWith(TP.MechRoot, StringComparison.Ordinal)
                ? TP.MechRoot : TP.SciFiRoot;
            string dir = root + "Textures/";
            string stripped = matName;
            foreach (string prefix in new[] { "MI_", "M_", "T_" })
                if (stripped.StartsWith(prefix, StringComparison.Ordinal))
                { stripped = stripped.Substring(prefix.Length); break; }
            string[] candidates =
            {
                dir + matName + suffix + ".png",
                dir + "T_" + stripped + suffix + ".png",
            };
            foreach (string candidate in candidates)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture>(candidate);
                if (tex != null) return tex;
            }
            return null;
        }

        static Material GetOrCreateRemapMaterial(string matName,
            Texture baseMap, Texture emissive)
        {
            string safe = new string(matName
                .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            string path = TP.RingRoot + "Materials/" + safe + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            EnsureFolder(TP.RingRoot);
            EnsureFolder(TP.RingRoot + "Materials/");
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetTexture("_BaseMap", baseMap);
            if (emissive != null)
            {
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags =
                    MaterialGlobalIlluminationFlags.RealtimeEmissive;
                mat.SetTexture("_EmissionMap", emissive);
                mat.SetColor("_EmissionColor", Color.white);
            }
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void EnsureFolder(string path)
        {
            string trimmed = path.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(trimmed)) return;
            AssetDatabase.CreateFolder(
                Path.GetDirectoryName(trimmed).Replace('\\', '/'),
                Path.GetFileName(trimmed));
        }

        /// Spec §7.4 junk gate, single implementation: no Resources/,
        /// StreamingAssets/, scripts, plugins, scenes or foreign .mat files may
        /// enter the repo with the packs (outside our _Ring folder).
        static void CheckJunk(List<string> errors)
        {
            string rootFs = TP.Root.TrimEnd('/');
            if (!Directory.Exists(rootFs)) return;
            foreach (string dir in Directory.GetDirectories(rootFs, "*",
                SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dir);
                if (name == "Resources" || name == "StreamingAssets")
                    errors.Add("forbidden folder in pack: " + dir);
            }
            string[] forbidden = { ".cs", ".dll", ".unity", ".mat" };
            foreach (string file in Directory.GetFiles(rootFs, "*",
                SearchOption.AllDirectories))
            {
                string norm = file.Replace('\\', '/');
                if (norm.StartsWith(TP.RingRoot, StringComparison.Ordinal)) continue;
                if (forbidden.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    errors.Add("forbidden file in pack: " + norm);
            }
        }
    }
}
