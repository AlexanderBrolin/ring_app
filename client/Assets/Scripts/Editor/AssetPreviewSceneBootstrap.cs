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
        public const string DirectorMatPath = TA.MaterialsRoot + "DirectorDark.mat";
        public const string FloorMatPath = TA.MaterialsRoot + "PreviewFloor.mat";

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

            Material directorMat = GetOrCreateMaterial(DirectorMatPath,
                new Color(0.08f, 0.08f, 0.1f), new Color(2f, 0.1f, 0.1f));
            Material floorMat = GetOrCreateMaterial(FloorMatPath,
                new Color(0.12f, 0.12f, 0.14f), Color.black);

            BuildEntity("Player", new Vector3(0f, 0f, 0f),
                TP.DollPath, TA.PlayerControllerPath);
            BuildEntity("Ual2Check", new Vector3(2f, 0f, 0f),
                TP.Ual2Root + "Mannequin_F.fbx", TA.Ual2CheckControllerPath);

            GameObject mechs = GetOrCreateRoot("Mechs", new Vector3(0f, 0f, 4f));
            BuildEntityUnder(mechs, "George", new Vector3(-3f, 0f, 0f),
                TP.MechRoot + "Models/George.fbx");
            BuildEntityUnder(mechs, "Leela", new Vector3(-1f, 0f, 0f),
                TP.MechRoot + "Models/Leela.fbx");
            BuildEntityUnder(mechs, "Mike", new Vector3(1f, 0f, 0f),
                TP.MechRoot + "Models/Mike.fbx");
            BuildEntityUnder(mechs, "Stan", new Vector3(3f, 0f, 0f),
                TP.MechRoot + "Models/Stan.fbx");

            GameObject elites = GetOrCreateRoot("EliteRobots", new Vector3(0f, 0f, 8f));
            BuildEntityUnder(elites, "EyeDrone", new Vector3(-2f, 0f, 0f),
                TP.SciFiRoot + "Models/Enemy_EyeDrone.fbx");
            BuildEntityUnder(elites, "QuadShell", new Vector3(0f, 0f, 0f),
                TP.SciFiRoot + "Models/Enemy_QuadShell.fbx");
            BuildEntityUnder(elites, "Trilobite", new Vector3(2f, 0f, 0f),
                TP.SciFiRoot + "Models/Enemy_Trilobite.fbx");

            GameObject director = BuildEntity("DirectorStub", new Vector3(0f, 0f, 12f),
                TP.SciFiRoot + "Models/Enemy_QuadShell.fbx",
                TA.ControllerPathFor("Enemy_QuadShell.fbx"));
            Transform directorVisual = director.transform.Find("Visual");
            directorVisual.localScale = Vector3.one * 1.75f;
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
            ReportEmission(scene);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[AssetPreview] scene saved: " + TP.ScenePath);
        }

        static GameObject GetOrCreateRoot(string name, Vector3 position)
        {
            GameObject go = GameObject.Find("/" + name);
            if (go == null) go = new GameObject(name);
            go.transform.position = position;
            return go;
        }

        static GameObject BuildEntity(string name, Vector3 position,
            string modelPath, string controllerPath)
        {
            GameObject root = GetOrCreateRoot(name, position);
            EnsureVisual(root, modelPath, controllerPath);
            return root;
        }

        static void BuildEntityUnder(GameObject parent, string name,
            Vector3 localPosition, string modelPath, string controllerPath = null)
        {
            Transform existing = parent.transform.Find(name);
            GameObject root = existing != null ? existing.gameObject : new GameObject(name);
            root.transform.SetParent(parent.transform, false);
            root.transform.localPosition = localPosition;
            EnsureVisual(root, modelPath,
                controllerPath ?? DefaultControllerFor(modelPath));
        }

        static string DefaultControllerFor(string modelPath)
        {
            string path = TA.ControllerPathFor(modelPath);
            return AssetDatabase.LoadAssetAtPath<UnityEditor.Animations
                .AnimatorController>(path) != null ? path : null;
        }

        static void EnsureVisual(GameObject root, string modelPath, string controllerPath)
        {
            Transform visualTf = root.transform.Find("Visual");
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
                    }
                }
            }
            Debug.Log("[AssetPreview] emission check done (warnings above, if any)");
        }
    }
}
