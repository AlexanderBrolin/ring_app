using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using TP = Ring.Editor.ThirdPartyAssetPostprocessor;
using TA = Ring.Editor.ThirdPartyAnimatorBootstrap;

namespace Ring.Editor
{
    /// Builds the AssetPreview scene (assets-mvp plan T14): owner's milestone
    /// playtest target. NOT in the build (BuildCommands hardcodes Main.unity).
    /// Idempotent: existing roots are reused, materials are created once, the
    /// scene is always saved at the end (a -quit batch would otherwise drop
    /// changes silently). Hierarchy convention for Phase B: entity root is an
    /// empty holder (the view will own it), the model instance lives on the
    /// "Visual" child together with the Animator (applyRootMotion = false).
    public static class AssetPreviewSceneBootstrap
    {
        /// Clone of the pack's textured QuadShell material, darkened + red
        /// emission (owner feedback: a flat dark override lost the texture).
        public const string DirectorSkinPath = TA.MaterialsRoot + "DirectorSkin.mat";
        public const string ObsoleteDirectorMatPath = TA.MaterialsRoot + "DirectorDark.mat";
        public const string FloorMatPath = TA.MaterialsRoot + "PreviewFloor.mat";
        // Milestone feedback numbers (preview cosmetics, not gameplay SO).
        const float MechScale = 0.4f;    // мехи «в 2,5 раза меньше» (веха 1)
        const float EliteScale = 1.5f;   // элита «в 1,5 раза больше» (веха 1)
        const float TriloScale = EliteScale * 0.5f; // «самый большой справа — в 2 раза меньше» (веха 3)
        const float DirectorScale = 3.5f;           // «Директора в 2 раза больше» (веха 3)
        const float RobotYaw = 180f;     // «роботы стоят задом к Сборщикам» (веха 3)

        [MenuItem("Ring/Bootstrap/Asset Preview Scene")]
        public static void Apply()
        {
            // Open/create the scene BEFORE loading assets (lesson 15: OpenScene
            // invalidates previously loaded asset references).
            Scene scene;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TP.ScenePath) != null)
            {
                scene = EditorSceneManager.OpenScene(TP.ScenePath, OpenSceneMode.Single);
            }
            else
            {
                scene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, TP.ScenePath);
            }

            Material floorMat = GetOrCreateMaterial(FloorMatPath,
                new Color(0.12f, 0.12f, 0.14f), Color.black);
            Material directorMat = GetOrCreateDirectorSkin();

            BuildEntity("Player", new Vector3(0f, 0f, 0f),
                TP.DollPath, TA.PlayerControllerPath);
            // UAL2 rig check runs on the PROVEN male doll (the production case:
            // UAL2 clips on the collector rig). Mannequin_F stays imported with
            // its own avatar — its Unity export turned out invisible on stage
            // (milestone-1 feedback), needs a look before reuse.
            BuildEntity("Ual2Check", new Vector3(2f, 0f, 0f),
                TP.DollPath, TA.Ual2CheckControllerPath);

            GameObject mechs = GetOrCreateRoot("Mechs", new Vector3(0f, 0f, 4f), RobotYaw);
            BuildEntityUnder(mechs, "George", new Vector3(-3f, 0f, 0f),
                TP.MechRoot + "Models/George.fbx", null, MechScale);
            BuildEntityUnder(mechs, "Leela", new Vector3(-1f, 0f, 0f),
                TP.MechRoot + "Models/Leela.fbx", null, MechScale);
            BuildEntityUnder(mechs, "Mike", new Vector3(1f, 0f, 0f),
                TP.MechRoot + "Models/Mike.fbx", null, MechScale);
            BuildEntityUnder(mechs, "Stan", new Vector3(3f, 0f, 0f),
                TP.MechRoot + "Models/Stan.fbx", null, MechScale);

            GameObject elites = GetOrCreateRoot("EliteRobots", new Vector3(0f, 0f, 8f), RobotYaw);
            // Flying drone hovers (веха 3: спавнился наполовину в полу).
            // Phase B: hover height must come from the mob's view wiring.
            BuildEntityUnder(elites, "EyeDrone", new Vector3(-2.5f, 1.5f, 0f),
                TP.SciFiRoot + "Models/Enemy_EyeDrone.fbx", null, EliteScale);
            BuildEntityUnder(elites, "QuadShell", new Vector3(0f, 0f, 0f),
                TP.SciFiRoot + "Models/Enemy_QuadShell.fbx", null, EliteScale);
            BuildEntityUnder(elites, "Trilobite", new Vector3(2.5f, 0f, 0f),
                TP.SciFiRoot + "Models/Enemy_Trilobite.fbx", null, TriloScale);

            GameObject director = BuildEntity("DirectorStub", new Vector3(0f, 0f, 12f),
                TP.SciFiRoot + "Models/Enemy_QuadShell.fbx",
                TA.ControllerPathFor("Enemy_QuadShell.fbx"), DirectorScale);
            director.transform.rotation = Quaternion.Euler(0f, RobotYaw, 0f);
            Transform directorVisual = director.transform.Find("Visual");
            foreach (Renderer renderer in
                     directorVisual.GetComponentsInChildren<Renderer>())
                renderer.sharedMaterials = Enumerable.Repeat(
                    directorMat, renderer.sharedMaterials.Length).ToArray();

            GameObject crates = GetOrCreateRoot("LootCrates", new Vector3(0f, 0f, -3f));
            BuildEntityUnder(crates, "Crate", new Vector3(-3f, 0f, 0f),
                TP.SciFiRoot + "Models/Prop_Crate.fbx");
            BuildEntityUnder(crates, "CrateLarge", new Vector3(-1f, 0f, 0f),
                TP.SciFiRoot + "Models/Prop_Crate_Large.fbx");
            BuildEntityUnder(crates, "Chest", new Vector3(1f, 0f, 0f),
                TP.SciFiRoot + "Models/Prop_Chest.fbx");
            BuildEntityUnder(crates, "Locker", new Vector3(3f, 0f, 0f),
                TP.SciFiRoot + "Models/Prop_Locker.fbx");

            BuildFloor(floorMat);
            BuildLights();
            BuildCamera();
            ReportEmission(scene);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[AssetPreview] scene saved: " + TP.ScenePath);
        }

        static GameObject GetOrCreateRoot(string name, Vector3 position, float yaw = 0f)
        {
            GameObject go = GameObject.Find("/" + name);
            if (go == null) go = new GameObject(name);
            go.transform.position = position;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            return go;
        }

        static GameObject BuildEntity(string name, Vector3 position,
            string modelPath, string controllerPath, float visualScale = 1f)
        {
            GameObject root = GetOrCreateRoot(name, position);
            EnsureVisual(root, modelPath, controllerPath, visualScale);
            return root;
        }

        static void BuildEntityUnder(GameObject parent, string name,
            Vector3 localPosition, string modelPath, string controllerPath = null,
            float visualScale = 1f)
        {
            Transform existing = parent.transform.Find(name);
            GameObject root = existing != null ? existing.gameObject : new GameObject(name);
            root.transform.SetParent(parent.transform, false);
            root.transform.localPosition = localPosition;
            EnsureVisual(root, modelPath,
                controllerPath ?? DefaultControllerFor(modelPath), visualScale);
        }

        static string DefaultControllerFor(string modelPath)
        {
            string path = TA.ControllerPathFor(modelPath);
            return AssetDatabase.LoadAssetAtPath<UnityEditor.Animations
                .AnimatorController>(path) != null ? path : null;
        }

        static void EnsureVisual(GameObject root, string modelPath,
            string controllerPath, float visualScale = 1f)
        {
            Transform visualTf = root.transform.Find("Visual");
            // A visual instantiated from a DIFFERENT model (e.g. after feedback
            // swaps) is torn down and rebuilt — idempotent otherwise.
            if (visualTf != null)
            {
                UnityEngine.Object source =
                    PrefabUtility.GetCorrespondingObjectFromSource(visualTf.gameObject);
                string sourcePath = source != null
                    ? AssetDatabase.GetAssetPath(source) : null;
                if (sourcePath != modelPath)
                {
                    UnityEngine.Object.DestroyImmediate(visualTf.gameObject);
                    visualTf = null;
                }
            }
            GameObject visual;
            if (visualTf == null)
            {
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (model == null)
                    throw new InvalidOperationException(
                        "AssetPreviewSceneBootstrap: model not found at " + modelPath);
                visual = (GameObject)PrefabUtility.InstantiatePrefab(model);
                visual.name = "Visual";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero;
            }
            else
            {
                visual = visualTf.gameObject;
            }
            visual.transform.localScale = Vector3.one * visualScale;
            if (controllerPath == null) return; // static props carry no Animator
            var controller = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations
                .AnimatorController>(controllerPath);
            if (controller == null)
                throw new InvalidOperationException(
                    "AssetPreviewSceneBootstrap: controller not found at " + controllerPath);
            Animator animator = visual.GetComponent<Animator>();
            if (animator == null) animator = visual.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false; // motion is never animation-driven
        }

        /// DirectorSkin = FRESH URP Lit material: cloning the importer's artifact
        /// material both lost the texture and triggered Unity's "materials
        /// created in an older version" prompt (milestone-2 feedback). The
        /// pack's maps are wired explicitly — base color texture plus the
        /// EMISSIVE MASK, so the red glow sits in the authored zones instead of
        /// flooding the whole mesh.
        static Material GetOrCreateDirectorSkin()
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(ObsoleteDirectorMatPath) != null)
                AssetDatabase.DeleteAsset(ObsoleteDirectorMatPath);
            var existing = AssetDatabase.LoadAssetAtPath<Material>(DirectorSkinPath);
            bool healthy = existing != null
                && existing.GetTexture("_BaseMap") != null
                && existing.GetTexture("_EmissionMap") != null; // v1 flooded red: no mask
            if (healthy) return existing; // idempotent skip
            if (existing != null) AssetDatabase.DeleteAsset(DirectorSkinPath);

            Texture baseMap = LoadEnemyTexture("_BaseColor");
            Texture emissiveMap = LoadEnemyTexture("_Emissive");
            if (baseMap == null)
            {
                // Fall back to whatever the importer bound for QuadShell.
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(
                    TP.SciFiRoot + "Models/Enemy_QuadShell.fbx");
                Material source = model == null ? null
                    : model.GetComponentInChildren<Renderer>()?.sharedMaterial;
                if (source != null && source.HasProperty("_BaseMap"))
                    baseMap = source.GetTexture("_BaseMap");
            }
            if (baseMap == null)
                throw new InvalidOperationException(
                    "AssetPreviewSceneBootstrap: no base texture found for DirectorSkin");

            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetTexture("_BaseMap", baseMap);
            mat.SetColor("_BaseColor", new Color(0.55f, 0.55f, 0.6f)); // darkened tint
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (emissiveMap != null) mat.SetTexture("_EmissionMap", emissiveMap);
            mat.SetColor("_EmissionColor", new Color(2.2f, 0.12f, 0.12f));
            AssetDatabase.CreateAsset(mat, DirectorSkinPath);
            Debug.Log($"[AssetPreview] DirectorSkin rebuilt: baseMap={baseMap.name}, " +
                      $"emissiveMap={(emissiveMap ? emissiveMap.name : "none")}");
            return mat;
        }

        /// Enemy atlas lookup: prefers the Large variant (QuadShell is a large
        /// enemy), falls back to the regular atlas.
        static Texture LoadEnemyTexture(string suffix)
        {
            string dir = TP.SciFiRoot + "Textures/";
            return AssetDatabase.LoadAssetAtPath<Texture>(
                       dir + "T_Enemies_Large" + suffix + ".png")
                ?? AssetDatabase.LoadAssetAtPath<Texture>(
                       dir + "T_Enemies" + suffix + ".png");
        }

        static Material GetOrCreateMaterial(string path, Color baseColor, Color emission)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                throw new InvalidOperationException("URP Lit shader not found");
            mat = new Material(shader);
            mat.SetColor("_BaseColor", baseColor);
            if (emission.maxColorComponent > 0f)
            {
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                mat.SetColor("_EmissionColor", emission);
            }
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void BuildFloor(Material floorMat)
        {
            GameObject floor = GameObject.Find("/Floor");
            if (floor == null)
            {
                floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.name = "Floor";
                // Preview cosmetics only — no physics (pattern: E1 RemoveCollider).
                UnityEngine.Object.DestroyImmediate(floor.GetComponent<Collider>());
            }
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(6f, 1f, 6f);
            floor.GetComponent<MeshRenderer>().sharedMaterial = floorMat;
        }

        static void BuildLights()
        {
            GameObject key = GetOrCreateRoot("KeyLight", new Vector3(0f, 6f, 0f));
            Light keyLight = key.GetComponent<Light>();
            if (keyLight == null) keyLight = key.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.intensity = 0.35f;
            keyLight.color = new Color(0.75f, 0.8f, 1f);
            key.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            GameObject neon = GetOrCreateRoot("NeonLights", Vector3.zero);
            EnsurePointLight(neon, "NeonCyan", new Vector3(0f, 3f, 2f),
                new Color(0.2f, 0.9f, 1f));
            EnsurePointLight(neon, "NeonMagenta", new Vector3(-4f, 3f, 8f),
                new Color(1f, 0.2f, 0.8f));
            EnsurePointLight(neon, "NeonOrange", new Vector3(4f, 3f, -2f),
                new Color(1f, 0.55f, 0.15f));
            // No Lightmapping calls anywhere: realtime only, no LightingData.
        }

        /// Game-view camera for the milestone PlayMode look: ¾ top-down angle
        /// matching the game's perspective (ADR-001 §9), covers all rows.
        static void BuildCamera()
        {
            GameObject go = GameObject.Find("/PreviewCamera");
            if (go == null) go = new GameObject("PreviewCamera");
            Camera camera = go.GetComponent<Camera>();
            if (camera == null) camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.02f, 0.04f);
            if (go.GetComponent<AudioListener>() == null)
                go.AddComponent<AudioListener>();
            go.transform.position = new Vector3(0f, 16f, -8f);
            go.transform.rotation = Quaternion.Euler(55f, 0f, 0f);
        }

        static void EnsurePointLight(GameObject parent, string name,
            Vector3 position, Color color)
        {
            Transform tf = parent.transform.Find(name);
            GameObject go = tf != null ? tf.gameObject : new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = position;
            Light light = go.GetComponent<Light>();
            if (light == null) light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 14f;
            light.intensity = 4f;
            light.color = color;
        }

        /// Spec §6 emission criterion: every mob-candidate model must expose a
        /// URP Lit material with an _EmissionColor slot (Phase B accents/telegraph
        /// reuse the E1 MaterialPropertyBlock mechanic on top of it).
        static void ReportEmission(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
                {
                    foreach (Material mat in renderer.sharedMaterials)
                    {
                        if (mat == null) continue;
                        bool lit = mat.shader != null
                            && mat.shader.name == "Universal Render Pipeline/Lit";
                        bool emission = mat.HasProperty("_EmissionColor");
                        if (!lit || !emission)
                            Debug.LogWarning("[AssetPreview] emission check FAILED: " +
                                $"{root.name}/{renderer.name} mat '{mat.name}' " +
                                $"shader '{(mat.shader ? mat.shader.name : "null")}'");
                        // Texture-binding diagnostic (milestone-2): a model whose
                        // material lost its albedo map renders flat grey. The UAL
                        // mannequins are authored textureless (colored materials) —
                        // not a defect, skip them.
                        bool mannequin = root.name == "Player" || root.name == "Ual2Check";
                        bool hasBaseMap = mat.HasProperty("_BaseMap")
                            && mat.GetTexture("_BaseMap") != null;
                        if (!hasBaseMap && !mannequin && mat.name != "PreviewFloor")
                            Debug.LogWarning("[AssetPreview] NO BASE TEXTURE: " +
                                $"{root.name}/{renderer.name} mat '{mat.name}'");
                    }
                }
            }
            Debug.Log("[AssetPreview] emission check done (warnings above, if any)");
        }
    }
}
