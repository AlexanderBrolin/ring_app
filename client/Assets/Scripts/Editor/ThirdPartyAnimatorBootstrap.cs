using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using TP = Ring.Editor.ThirdPartyAssetPostprocessor;

namespace Ring.Editor
{
    /// Generates Animator assets for the ThirdParty packs (assets-mvp plan T12,
    /// revision R-I / owner decision 2a). Idempotent via the guard pattern:
    /// an asset that already exists is left untouched (recreating would change
    /// its GUID and break scene references) — a second run produces no diff.
    /// Not wired to gameplay: preview-scene consumers only.
    public static class ThirdPartyAnimatorBootstrap
    {
        public const string AnimatorsRoot = TP.RingRoot + "Animators/";
        public const string MasksRoot = TP.RingRoot + "Masks/";
        public const string MaterialsRoot = TP.RingRoot + "Materials/";
        public const string PlayerControllerPath = AnimatorsRoot + "PlayerAnimator.controller";
        public const string Ual2CheckControllerPath = AnimatorsRoot + "Ual2CheckAnimator.controller";
        public const string UpperBodyMaskPath = MasksRoot + "UpperBody.mask";

        /// Controller path for a pack model: file name sanitized to PascalCase
        /// alphanumerics (pack file names are never edited — only OUR asset
        /// names are derived; ADR-003 §9 dictionary holds: George/Leela/Mike/
        /// Stan/EnemyEyeDrone/EnemyQuadShell/EnemyTrilobite are neutral).
        public static string ControllerPathFor(string modelPath)
        {
            string raw = Path.GetFileNameWithoutExtension(modelPath);
            var sb = new StringBuilder(raw.Length);
            bool upper = true;
            foreach (char c in raw)
            {
                if (!char.IsLetterOrDigit(c)) { upper = true; continue; }
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            return AnimatorsRoot + sb + "Animator.controller";
        }

        [MenuItem("Ring/Bootstrap/ThirdParty Animators")]
        public static void Apply()
        {
            EnsureFolders();
            AvatarMask mask = GetOrCreateUpperBodyMask();
            CreatePlayerController(mask);
            CreateUal2CheckController();
            foreach (string path in TP.FindModels(TP.MechRoot)
                         .Concat(TP.FindModels(TP.SciFiRoot)))
                CreateRobotController(path);
            AssetDatabase.SaveAssets();
            Debug.Log("[ThirdPartyAnimators] done");
        }

        static void EnsureFolders()
        {
            EnsureFolder(TP.RingRoot);
            EnsureFolder(AnimatorsRoot);
            EnsureFolder(MasksRoot);
            EnsureFolder(MaterialsRoot); // used by AssetPreviewSceneBootstrap (T14)
        }

        static void EnsureFolder(string path)
        {
            string trimmed = path.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(trimmed)) return;
            AssetDatabase.CreateFolder(
                Path.GetDirectoryName(trimmed).Replace('\\', '/'),
                Path.GetFileName(trimmed));
        }

        static AvatarMask GetOrCreateUpperBodyMask()
        {
            var existing = AssetDatabase.LoadAssetAtPath<AvatarMask>(UpperBodyMaskPath);
            if (existing != null) return existing;
            var mask = new AvatarMask();
            // Explicit for EVERY part: Root/legs/IK default to active and would
            // leak the "upper body" layer onto locomotion otherwise.
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, false);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg, false);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg, false);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFootIK, false);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFootIK, false);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftHandIK, false);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightHandIK, false);
            AssetDatabase.CreateAsset(mask, UpperBodyMaskPath);
            return mask;
        }

        /// Contract for Phase B (spec §5, owner decision 2a — free Standard has
        /// no strafe clips, so locomotion is 1D):
        ///   - "Speed" is the ONLY parameter: |simulation velocity| normalized
        ///     to [0;1] against MaxSpeed, fed via SetFloat with dampTime;
        ///   - the Visual child rotates toward the MOVEMENT direction; aiming
        ///     is carried by the "Aim" upper-body layer toward the cursor;
        ///   - the entity root belongs to the view interpolation, animation
        ///     never moves it (Critical Rule 3; applyRootMotion stays false);
        ///   - dash visual candidate is the Roll state; its timing is cut to
        ///     the server dash duration (HeroConfig SO) at wiring time, never
        ///     the other way around.
        static void CreatePlayerController(AvatarMask mask)
        {
            var existingController =
                AssetDatabase.LoadAssetAtPath<AnimatorController>(PlayerControllerPath);
            if (existingController != null)
            {
                ReconcileSpeedDefault(existingController);
                Debug.Log("[ThirdPartyAnimators] exists, reconciled: " + PlayerControllerPath);
                return;
            }
            Dictionary<string, AnimationClip> clips = ClipsOf(TP.DollPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(PlayerControllerPath);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ReconcileSpeedDefault(controller);

            AnimatorState locomotion = controller.CreateBlendTreeInController(
                "Locomotion", out BlendTree tree, 0);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(Require(clips, "Idle_Loop"), 0f);
            tree.AddChild(Require(clips, "Walk_Loop"), 0.33f);
            tree.AddChild(Require(clips, "Jog_Fwd_Loop"), 0.66f);
            tree.AddChild(Require(clips, "Sprint_Loop"), 1f);
            controller.layers[0].stateMachine.defaultState = locomotion;

            AddOptionalState(controller, clips, "Hit_Chest", "HitReact");
            AddOptionalState(controller, clips, "Hit_Head", "HitReactHead");
            controller.AddMotion(Require(clips, "Death01"), 0).name = "Death";
            AddOptionalState(controller, clips, "Roll", "Dash");

            controller.AddLayer("Aim");
            AnimatorControllerLayer[] layers = controller.layers; // returns a copy
            layers[1].avatarMask = mask;
            layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
            layers[1].defaultWeight = 1f;
            controller.layers = layers;
            AnimatorStateMachine aim = controller.layers[1].stateMachine;
            AnimatorState aimIdle = AddAimState(controller, aim, clips, "Pistol_Aim_Neutral");
            aim.defaultState = aimIdle;
            AddAimState(controller, aim, clips, "Pistol_Aim_Up");
            AddAimState(controller, aim, clips, "Pistol_Aim_Down");
            AddAimState(controller, aim, clips, "Pistol_Shoot");
            AddAimState(controller, aim, clips, "Pistol_Reload");
            Debug.Log("[ThirdPartyAnimators] created " + PlayerControllerPath);
        }

        /// Preview shows the collector RUNNING by default (milestone-3 feedback:
        /// «он же бегать должен») — Speed defaults to 1 → Sprint end of the
        /// blend tree. Phase B drives the parameter from simulation velocity.
        static void ReconcileSpeedDefault(AnimatorController controller)
        {
            AnimatorControllerParameter[] parameters = controller.parameters; // copy
            bool changed = false;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name != "Speed") continue;
                if (!Mathf.Approximately(parameters[i].defaultFloat, 1f))
                {
                    parameters[i].defaultFloat = 1f;
                    changed = true;
                }
            }
            if (changed) controller.parameters = parameters;
        }

        /// UAL2 rig check (spec §9 risk): a second doll instance plays UAL2
        /// clips in the preview scene — broken retarget shows up immediately.
        static void CreateUal2CheckController()
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(Ual2CheckControllerPath) != null)
            {
                Debug.Log("[ThirdPartyAnimators] exists, skipped: " + Ual2CheckControllerPath);
                return;
            }
            string ual2 = TP.Ual2StandardPath;
            Dictionary<string, AnimationClip> clips = ClipsOf(ual2);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Ual2CheckControllerPath);
            AnimatorState def = controller.AddMotion(Require(clips, "Zombie_Walk_Fwd_Loop"), 0);
            controller.AddMotion(Require(clips, "Sword_Dash"), 0);
            controller.layers[0].stateMachine.defaultState = def;
            Debug.Log("[ThirdPartyAnimators] created " + Ual2CheckControllerPath);
        }

        static void CreateRobotController(string modelPath)
        {
            Dictionary<string, AnimationClip> clips = ClipsOf(modelPath);
            if (clips.Count == 0) return; // props/guns carry no takes
            string path = ControllerPathFor(modelPath);
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
            {
                Debug.Log("[ThirdPartyAnimators] exists, skipped: " + path);
                return;
            }
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            AnimatorState first = null, idle = null;
            foreach (KeyValuePair<string, AnimationClip> pair in clips.OrderBy(
                         p => p.Key, StringComparer.Ordinal))
            {
                AnimatorState state = controller.AddMotion(pair.Value, 0);
                state.name = pair.Key;
                if (first == null) first = state;
                if (idle == null && pair.Key.IndexOf(
                        "idle", StringComparison.OrdinalIgnoreCase) >= 0)
                    idle = state;
            }
            controller.layers[0].stateMachine.defaultState = idle ?? first;
            Debug.Log($"[ThirdPartyAnimators] created {path} ({clips.Count} states)");
        }

        /// Clips of an FBX keyed by short name (take name after the rig prefix,
        /// e.g. "Armature|Idle_Loop" → "Idle_Loop"). Unity's implicit preview
        /// clips (double underscore prefix) are skipped.
        static Dictionary<string, AnimationClip> ClipsOf(string modelPath)
        {
            var result = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
            foreach (AnimationClip clip in AssetDatabase
                         .LoadAllAssetRepresentationsAtPath(modelPath)
                         .OfType<AnimationClip>())
            {
                if (clip.name.StartsWith("__preview__", StringComparison.Ordinal)) continue;
                int bar = clip.name.LastIndexOf('|');
                string key = bar >= 0 ? clip.name.Substring(bar + 1) : clip.name;
                result[key] = clip;
            }
            return result;
        }

        static AnimationClip Require(Dictionary<string, AnimationClip> clips, string name)
        {
            if (!clips.TryGetValue(name, out AnimationClip clip))
                throw new InvalidOperationException(
                    "ThirdPartyAnimatorBootstrap: mandatory clip not found: " + name);
            return clip;
        }

        static void AddOptionalState(AnimatorController controller,
            Dictionary<string, AnimationClip> clips, string clipName, string stateName)
        {
            if (!clips.TryGetValue(clipName, out AnimationClip clip))
            {
                Debug.LogWarning("[ThirdPartyAnimators] optional clip missing, " +
                                 $"state skipped: {clipName}");
                return;
            }
            controller.AddMotion(clip, 0).name = stateName;
        }

        static AnimatorState AddAimState(AnimatorController controller,
            AnimatorStateMachine aim, Dictionary<string, AnimationClip> clips, string name)
        {
            AnimationClip clip = Require(clips, name);
            AnimatorState state = aim.AddState(name);
            state.motion = clip;
            return state;
        }
    }
}
