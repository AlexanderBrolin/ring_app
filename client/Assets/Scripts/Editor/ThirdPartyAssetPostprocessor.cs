using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ring.Editor
{
    /// Import rules for Assets/ThirdParty/** (assets-mvp spec §4, revision R-I).
    /// First import lands correct — no default-then-reimport double pass.
    /// Hard failures live in ThirdPartyImportBootstrap: exceptions thrown from a
    /// postprocessor are swallowed by the import pipeline (batch still exits 0),
    /// so this class only reports via context.LogImportError.
    public sealed class ThirdPartyAssetPostprocessor : AssetPostprocessor
    {
        public const string Root = "Assets/ThirdParty/";
        public const string RingRoot = Root + "_Ring/";
        public const string UalRoot = Root + "UniversalAnimationLibrary/";
        public const string Ual2Root = Root + "UniversalAnimationLibrary2/";
        public const string MechRoot = Root + "AnimatedMechPack/";
        public const string SciFiRoot = Root + "SciFiEssentialsKit/";
        /// T24-2 (owner-approved Blender split, supersedes app-1zf's "no
        /// separable sub-mesh" finding — `GibView`'s class doc): capped-cut,
        /// static mech PART meshes (George_Parts.fbx/Leela_Parts.fbx), same
        /// material slots as the source mechs. Lives under `_Ring/` (our own
        /// content) rather than `MechRoot` (the vendored pack itself) — these
        /// files are OUR derivative, not pack content — so `IsRobotPath`
        /// deliberately does NOT cover this root; see the dedicated branch in
        /// `OnPreprocessModel` below.
        public const string GibsRoot = RingRoot + "Gibs/";
        public const string ScenePath = "Assets/Scenes/AssetPreview.unity";
        /// Single FBX carrying both the doll mesh and its 43 takes (INSPECTION.md).
        public const string DollPath = UalRoot + "UAL1_Standard.fbx";
        /// UAL2 clip container (same rig as the doll, 43 takes).
        public const string Ual2StandardPath = Ual2Root + "UAL2_Standard.fbx";
        /// Female mannequin (mesh-only, same bone set): gets its OWN avatar —
        /// humanoid retarget plays shared clips on it without avatar sharing.
        public const string FemaleDollPath = Ual2Root + "Mannequin_F.fbx";

        /// Robot FBX files with embedded clips → Generic rig; every other model
        /// under Mech/SciFi roots is a static prop/gun → no avatar at all.
        static readonly HashSet<string> AnimatedRobotFiles = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "George.fbx", "Leela.fbx", "Mike.fbx", "Stan.fbx",
            "Enemy_EyeDrone.fbx", "Enemy_QuadShell.fbx", "Enemy_Trilobite.fbx",
        };

        /// Bump to force mass reimport when rules/constants change.
        public override uint GetVersion() => 3;

        /// В3 fix-wave 1 (app-n6g item 1, PROVEN root cause of the "gib parts
        /// vanish" bug — see `OnPreprocessModel`'s `GibsRoot` branch below for
        /// the fix site, `GibScaleDiagnostics` (deleted before commit, see
        /// `v3-wave1-report.md`) for the measurement method/evidence). This
        /// pack's rig (`George.fbx`/`Leela.fbx`, and — because
        /// `George_Parts.fbx`/`Leela_Parts.fbx` were Blender-split FROM the
        /// same rig — their derivative too) carries a ~100× object-level
        /// Transform scale on every mesh node that was never "Applied" before
        /// export (a common Blender authoring gotcha: geometry modeled tiny,
        /// compensated with an un-applied Object > Scale instead of baking it
        /// into the vertex data). The LIVE mech/corpse visuals get this
        /// factor for free because they instantiate the WHOLE FBX prefab
        /// hierarchy (`EditorBootstrapUtils.EnsureVisual` →
        /// `PrefabUtility.InstantiatePrefab`) — the ~100× sits on that
        /// hierarchy's own node Transforms, cascading into the render
        /// automatically. `PersistentPropsDirector`/`GibView`, however, only
        /// ever consume the bare `Mesh` sub-asset (`StageOneSceneBootstrap.
        /// LoadGibParts` → `AssetDatabase.LoadAllAssetsAtPath(path).
        /// OfType&lt;Mesh&gt;()`) — raw vertex data, with NO hierarchy and
        /// therefore none of that node-level correction — so every gib chunk
        /// rendered ~100× too small (sub-centimeter, invisible at any
        /// playable camera distance) on the `SpawnFullExplodeGibs` code path
        /// (the only place `LoadGibParts`' meshes are ever spawned),
        /// matching the owner's observed "~35% of deaths vanish" 1:1 with
        /// `PersistentPropsDirector.GibFullExplodeChance = 0.35f`.
        /// Empirically verified (not guessed): reimporting `_Ring/Gibs/*.fbx`
        /// with `ModelImporter.globalScale` set to this value bakes the
        /// factor DIRECTLY into the imported `Mesh` geometry (confirmed via
        /// before/after `mesh.bounds` — grows by exactly this factor,
        /// uniformly on all 3 axes) — unlike the pack's OWN node-level scale,
        /// which stays exactly where it is regardless (a separate,
        /// independent mechanism; the two are not the same knob). This is the
        /// ONLY meta touched — `George.fbx`/`Leela.fbx` (outside `_Ring/`,
        /// global constraint) are untouched, and `GibView.Spawn`'s own
        /// `transform.localScale = Vector3.one * scale` formula (its class
        /// doc's documented "mirrors the archetype's own visualScale"
        /// contract) needed no runtime compensation once the geometry itself
        /// carries the correct real-world unit scale.
        const float GibPartsGlobalScale = 100f;

        public static bool IsHumanoidPath(string path) =>
            path.StartsWith(UalRoot, StringComparison.Ordinal)
            || path.StartsWith(Ual2Root, StringComparison.Ordinal);

        public static bool IsRobotPath(string path) =>
            path.StartsWith(MechRoot, StringComparison.Ordinal)
            || path.StartsWith(SciFiRoot, StringComparison.Ordinal);

        /// Total classification rule (no lists to maintain — INSPECTION.md facts):
        /// humanoid clips loop iff the take name ends with "_Loop";
        /// robot clips loop iff the name after "Armature|"/"Rig|" starts with
        /// Idle/Walk/Run. Everything else is one-shot.
        public static bool ShouldLoop(string assetPath, string clipName)
        {
            int bar = clipName.LastIndexOf('|');
            string name = bar >= 0 ? clipName.Substring(bar + 1) : clipName;
            if (IsHumanoidPath(assetPath))
                return name.EndsWith("_Loop", StringComparison.OrdinalIgnoreCase);
            return name.StartsWith("Idle", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Walk", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Run", StringComparison.OrdinalIgnoreCase);
        }

        /// Deterministic model enumeration for validator/bootstraps (single owner).
        public static string[] FindModels(string root)
        {
            string folder = root.TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(folder)) return Array.Empty<string>();
            return AssetDatabase.FindAssets("t:Model", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
        }

        /// Doll avatar accessor for validator/scene bootstraps; throws — callers
        /// run inside -executeMethod where an exception yields exit code 1.
        public static Avatar LoadDollAvatar()
        {
            Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(DollPath)
                .OfType<Avatar>().FirstOrDefault();
            if (avatar == null)
                throw new InvalidOperationException(
                    "ThirdPartyAssetPostprocessor: doll avatar not found at " + DollPath);
            return avatar;
        }

        /// Applies the loop/root-motion rules to every take of the importer.
        /// Shared verbatim by the validator — the single source of clip truth.
        public static void ApplyClipRules(ModelImporter importer)
        {
            ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
            if (clips.Length == 0) return;
            foreach (ModelImporterClipAnimation clip in clips)
            {
                bool loop = ShouldLoop(importer.assetPath, clip.name);
                clip.loopTime = loop;
                clip.loopPose = loop;
                // Bake Into Pose, Based Upon = Original (no root drift; motion
                // comes from simulation, never from animation — Critical Rule 3).
                clip.lockRootRotation = true;
                clip.lockRootHeightY = true;
                clip.lockRootPositionXZ = true;
                clip.keepOriginalOrientation = true;
                clip.keepOriginalPositionY = true;
                clip.keepOriginalPositionXZ = true;
            }
            importer.clipAnimations = clips; // defaultClipAnimations returns a copy
        }

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Root, StringComparison.Ordinal)) return;
            var importer = (ModelImporter)assetImporter;
            importer.materialImportMode =
                ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            if (IsHumanoidPath(assetPath))
            {
                importer.animationType = ModelImporterAnimationType.Human;
                if (assetPath == DollPath || assetPath == FemaleDollPath)
                {
                    importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                    return;
                }
                context.DependsOnArtifact(DollPath);
                Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(DollPath)
                    .OfType<Avatar>().FirstOrDefault();
                if (avatar == null)
                {
                    // Doll not imported yet; the validator heals via ForceUpdate.
                    context.LogImportError(
                        "Doll avatar missing — import order violated: " + assetPath);
                    return;
                }
                importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                importer.sourceAvatar = avatar;
            }
            else if (IsRobotPath(assetPath))
            {
                importer.animationType =
                    AnimatedRobotFiles.Contains(Path.GetFileName(assetPath))
                        ? ModelImporterAnimationType.Generic
                        : ModelImporterAnimationType.None; // props/guns: no avatar
            }
            else if (assetPath.StartsWith(GibsRoot, StringComparison.Ordinal))
            {
                // T24-2: capped-cut static part meshes — explicitly no rig,
                // no animation (task brief). Distinct branch rather than
                // folding into IsRobotPath above: these never carry embedded
                // takes (importedTakeInfos always empty), so this is
                // unconditional, unlike the robot branch's per-file check.
                importer.animationType = ModelImporterAnimationType.None;
                // В3 fix-wave 1 (app-n6g item 1, PROVEN root cause — see
                // GibPartsGlobalScale's own doc above): bakes the pack's
                // missing ~100× unit-scale correction directly into the
                // imported Mesh geometry so GibView's bare-Mesh consumption
                // renders parts at the same real-world size the live mech
                // hierarchy already gets for free.
                importer.globalScale = GibPartsGlobalScale;
            }
        }

        void OnPreprocessAnimation()
        {
            // Clip settings must live here: during OnPreprocessModel the take
            // list (defaultClipAnimations) is still empty on a first import.
            if (!assetPath.StartsWith(Root, StringComparison.Ordinal)) return;
            ApplyClipRules((ModelImporter)assetImporter);
        }
    }
}
