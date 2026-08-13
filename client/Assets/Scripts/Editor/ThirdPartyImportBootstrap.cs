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
    /// The postprocessor is the single writer of import SETTINGS; on mismatch
    /// this class re-triggers it via a ForceUpdate reimport and re-checks.
    /// Material remaps (RemapPackMaterials) are a separate channel: they can
    /// only exist post-import, when artifact materials are enumerable.
    /// Failures throw: thrown from an
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
            // T24-2: gib part meshes (George_Parts.fbx/Leela_Parts.fbx, `_Ring/
            // Gibs/`) validate and remap exactly like the robot/prop models —
            // no clips, no avatar, materials remapped by NAME to the SAME
            // `_Ring/Materials/` assets the live mechs already use (verified:
            // both parts FBXs carry a single material slot each, named
            // "George_Texture"/"Leela_Texture" — identical to the source
            // mechs' own slot names, RemapPackMaterials' doc below).
            string[] robots = TP.FindModels(TP.MechRoot)
                .Concat(TP.FindModels(TP.SciFiRoot))
                .Concat(TP.FindModels(TP.GibsRoot)).ToArray();

            foreach (string path in humanoids)
                ValidateModel(path, doll, errors, ref healed);
            foreach (string path in robots)
                ValidateModel(path, null, errors, ref healed);
            if (doll != null && AssetImporter.GetAtPath(TP.DollPath) is ModelImporter dollImporter)
                ValidateClips(TP.DollPath, dollImporter, errors, ref healed);
            int remapped = RemapPackMaterials(robots);
            // В1/В2 fix-wave 2 (app-n6g item 1, PROVEN root cause): the player
            // doll (TP.DollPath) is excluded from `humanoids` above and never
            // reaches `RemapPackMaterials(robots)` — "UAL mannequins are
            // authored textureless and are not touched" (RemapPackMaterials'
            // own doc). Its embedded FBX materials (`materialLocation:
            // InPrefab`, confirmed in UAL1_Standard.fbx.meta — no external
            // .mat asset exists for this pack anywhere on disk) therefore
            // never get `_EMISSION` enabled by ANY step in this pipeline.
            // The doll's `MaterialPropertyBlock.SetColor("_EmissionColor",
            // ...)` writes (`PlayerView.Sync` since Stage 2 Task 45a, which
            // moved emission off `PlayerVisual` onto the root view) were
            // consequently dead:
            // with the keyword off, the shader variant compiled for the
            // material has no emission sampling path at all, so no MPB value
            // can ever show, regardless of the pulse math driving it (which
            // was already correct). Fix below, right after RemapPackMaterials
            // — same run.
            if (doll != null) RemapDollMaterialsForEmission();

            Debug.Log($"[ThirdParty] validation done: {humanoids.Length} humanoid, " +
                      $"{robots.Length} robot/prop models, healed {healed}, " +
                      $"remapped {remapped}, errors {errors.Count}");
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "ThirdPartyImportBootstrap: validation failed:\n  " +
                    string.Join("\n  ", errors));

            ReconcileRemapEmission();
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
        /// T24-2: `robotModels` now also carries the gib part FBXs (`_Ring/
        /// Gibs/`) — their material names ("George_Texture"/"Leela_Texture")
        /// are IDENTICAL to the live mechs' own slot names, so
        /// `GetOrCreateRemapMaterial`'s existence-guarded `path` lookup
        /// resolves to the exact SAME `_Ring/Materials/*.mat` asset the mechs
        /// already remap to — no duplicate material is ever created, gib
        /// parts and live mechs share one material per archetype.
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
        /// T24-2: gib part models live under `_Ring/Gibs/`, not `MechRoot`
        /// itself (they're OUR derivative, not vendored pack content — see
        /// `ThirdPartyAssetPostprocessor.GibsRoot`'s doc) but their single
        /// material slot is cut from the SAME mech, so the lookup still
        /// belongs in `MechRoot`'s own `Textures/` folder.
        static Texture FindPackTexture(string modelPath, string matName, string suffix)
        {
            string root = modelPath.StartsWith(TP.MechRoot, StringComparison.Ordinal)
                || modelPath.StartsWith(TP.GibsRoot, StringComparison.Ordinal)
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
            return EditorBootstrapUtils.GetOrCreateMaterial(
                path, EditorBootstrapUtils.UrpLitShader, mat =>
            {
                // was: emission enabled only when an *_Emissive.png exists — MechPack has
                // none, so MPB _EmissionColor writes were silently dead (spec §3.4, Б1).
                mat.SetTexture("_BaseMap", baseMap);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                // White keeps authored emissive zones (Sci-Fi atlases) alive; black base for
                // mask-less packs — the MPB accents are additive either way (ПБ2).
                mat.SetColor("_EmissionColor", emissive != null ? Color.white : Color.black);
                if (emissive != null) mat.SetTexture("_EmissionMap", emissive);
            });
        }

        /// В1/В2 fix-wave 2 (app-n6g item 1): same `AddRemap`/external-material
        /// idiom `RemapPackMaterials` above already uses for the robot packs,
        /// applied to the player doll instead — the ONE model this pipeline
        /// never routed through it (class doc at the `Apply` call site). No
        /// texture lookup here (unlike `RemapPackMaterials`): the doll pack
        /// ships no textures at all ("authored textureless"), so this only
        /// needs to preserve each artifact material's own `_BaseColor` tint
        /// and turn emission on — never touches meshes/textures/anything else
        /// in the pack itself (global constraint: content outside `_Ring/`
        /// stays untouched). Landing the remap materials in `_Ring/Materials/`
        /// means `ReconcileRemapEmission` below heals these too on any future
        /// drift, same as the robot remaps already get.
        static void RemapDollMaterialsForEmission()
        {
            if (!(AssetImporter.GetAtPath(TP.DollPath) is ModelImporter importer)) return;
            bool changed = false;
            foreach (Material artifact in AssetDatabase.LoadAllAssetsAtPath(TP.DollPath)
                         .OfType<Material>())
            {
                Color baseColor = artifact.HasProperty("_BaseColor")
                    ? artifact.GetColor("_BaseColor") : Color.white;
                Material remap = GetOrCreateDollRemapMaterial(artifact.name, baseColor);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(
                    typeof(Material), artifact.name), remap);
                changed = true;
            }
            if (changed)
            {
                importer.SaveAndReimport();
                Debug.Log("[ThirdParty] doll materials remapped for emission: " + TP.DollPath);
            }
        }

        /// `_Doll` suffix keeps the file distinct from any robot-pack remap
        /// that happens to share a material name (`GetOrCreateRemapMaterial`
        /// above uses the bare name) — two different packs, two different
        /// assets, never a collision. `configure` only runs at creation
        /// (`EditorBootstrapUtils.GetOrCreateMaterial`'s own contract), so an
        /// owner's in-Editor tweak of the remap material survives a re-run.
        static Material GetOrCreateDollRemapMaterial(string matName, Color baseColor)
        {
            string safe = new string(matName
                .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            string path = TP.RingRoot + "Materials/" + safe + "_Doll.mat";
            return EditorBootstrapUtils.GetOrCreateMaterial(
                path, EditorBootstrapUtils.UrpLitShader, mat =>
            {
                mat.SetColor("_BaseColor", baseColor);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                // Black at rest — the doll view's MPB writes are the only thing
                // that ever lights this up (`PlayerView.Sync`: the combo-window
                // pulse `LinkWindowFlashAccent * wave` plus, since Stage 2 Task
                // 45a, a remote player's `RemotePlayerEmission` rim), same
                // "textureless-pack" black-base convention GetOrCreateRemapMaterial
                // above already follows.
                mat.SetColor("_EmissionColor", Color.black);
            });
        }

        /// Б1 reconcile: already-committed remap materials predate the unconditional
        /// emission rule above — heal them in place (recreating would break the
        /// externalObjects GUID link in the pack .fbx.meta). DirectorSkin/PreviewFloor
        /// are NOT remaps and keep their own authored emission setup (names derived
        /// from the preview bootstrap's own path constants — no literal copies, Р9).
        static readonly string[] NonRemapMaterials =
        {
            System.IO.Path.GetFileNameWithoutExtension(AssetPreviewSceneBootstrap.DirectorSkinPath),
            System.IO.Path.GetFileNameWithoutExtension(AssetPreviewSceneBootstrap.FloorMatPath),
        };

        static void ReconcileRemapEmission()
        {
            string folder = ThirdPartyAnimatorBootstrap.MaterialsRoot.TrimEnd('/');
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (System.Array.IndexOf(NonRemapMaterials, name) >= 0) continue;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.IsKeywordEnabled("_EMISSION")) continue;
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssetIfDirty(mat);
                Debug.Log("[ThirdParty] emission reconciled: " + path);
            }
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
