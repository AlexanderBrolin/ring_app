using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using TA = Ring.Editor.ThirdPartyAnimatorBootstrap;

namespace Ring.Editor
{
    /// Shared guard primitives of the idempotent bootstraps (assets phase B
    /// plan T1, Phase A sanction P9). Contract: behavior matches the original
    /// call sites bit-for-bit — a second Apply() of any bootstrap must still
    /// produce an empty git diff.
    public static class EditorBootstrapUtils
    {
        public const string UrpLitShader = "Universal Render Pipeline/Lit";
        public const string UrpUnlitShader = "Universal Render Pipeline/Unlit";

        public static void EnsureFolder(string path)
        {
            string trimmed = path.TrimEnd('/');
            if (string.IsNullOrEmpty(trimmed))
                throw new InvalidOperationException(
                    "EditorBootstrapUtils: folder path escaped the project root.");
            if (AssetDatabase.IsValidFolder(trimmed)) return;
            string parent = Path.GetDirectoryName(trimmed).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(trimmed));
        }

        public static GameObject FindRootObject(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
            }
            return null;
        }

        public static bool SetRef(SerializedObject so, string fieldName, Object value)
        {
            SerializedProperty prop = so.FindProperty(fieldName);
            if (prop == null)
                throw new InvalidOperationException(
                    $"{so.targetObject.GetType().Name} has no serialized field '{fieldName}'.");
            if (prop.objectReferenceValue == value) return false;
            prop.objectReferenceValue = value;
            return true;
        }

        public static void RemoveCollider(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
        }

        /// Existence-guarded material factory: `configure` runs ONLY at
        /// creation, so an owner's in-Editor tweak survives a re-run.
        public static Material GetOrCreateMaterial(
            string path, string shaderName, Action<Material> configure)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
                throw new InvalidOperationException(
                    $"EditorBootstrapUtils: shader '{shaderName}' not found — is URP installed?");
            var mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            configure?.Invoke(mat);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// Existence-guarded prefab factory (the shared shape of the six E1
        /// GetOrCreate*Prefab helpers): `build` returns a scene object that is
        /// saved as the prefab and destroyed. Caller-specific self-heal blocks
        /// (casing layer, spark params) stay at the call sites.
        public static T BuildPrefab<T>(string path, Func<GameObject> build)
            where T : Component
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;
            GameObject go = build();
            try
            {
                GameObject asset = PrefabUtility.SaveAsPrefabAsset(go, path);
                return asset.GetComponent<T>();
            }
            finally
            {
                Object.DestroyImmediate(go); // never leak the staging object into the scene
            }
        }

        /// The Phase B hierarchy convention (spec §3.2/§3.3, preview's
        /// EnsureVisual promoted): a named child = pack FBX instance with an
        /// Animator (applyRootMotion=false, Normal, AlwaysAnimate — Б8). A
        /// visual instantiated from a DIFFERENT model is torn down and rebuilt
        /// — idempotent otherwise. `controllerPath == null` → no Animator.
        public static GameObject EnsureVisual(GameObject root, string modelPath,
            string controllerPath, float visualScale, ref bool changed,
            string childName = "Visual")
        {
            Transform visualTf = root.transform.Find(childName);
            if (visualTf != null)
            {
                Object source =
                    PrefabUtility.GetCorrespondingObjectFromSource(visualTf.gameObject);
                string sourcePath = source != null
                    ? AssetDatabase.GetAssetPath(source) : null;
                if (sourcePath != modelPath)
                {
                    Object.DestroyImmediate(visualTf.gameObject);
                    visualTf = null;
                    changed = true;
                }
            }
            GameObject visual;
            if (visualTf == null)
            {
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (model == null)
                    throw new InvalidOperationException(
                        "EditorBootstrapUtils: model not found at " + modelPath);
                visual = (GameObject)PrefabUtility.InstantiatePrefab(model);
                visual.name = childName;
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero;
                changed = true;
            }
            else
            {
                visual = visualTf.gameObject;
            }
            if (visual.transform.localScale != Vector3.one * visualScale)
            {
                visual.transform.localScale = Vector3.one * visualScale;
                changed = true;
            }
            if (controllerPath == null) return visual;
            var controller = AssetDatabase.LoadAssetAtPath<
                UnityEditor.Animations.AnimatorController>(controllerPath);
            if (controller == null)
                throw new InvalidOperationException(
                    "EditorBootstrapUtils: controller not found at " + controllerPath);
            Animator animator = visual.GetComponent<Animator>();
            if (animator == null)
            {
                animator = visual.AddComponent<Animator>();
                changed = true;
            }
            if (animator.runtimeAnimatorController != controller)
            {
                animator.runtimeAnimatorController = controller;
                changed = true;
            }
            if (animator.applyRootMotion)
            {
                animator.applyRootMotion = false; // motion is never animation-driven
                changed = true;
            }
            if (animator.updateMode != AnimatorUpdateMode.Normal)
            {
                animator.updateMode = AnimatorUpdateMode.Normal; // pose before LateUpdate (Б8)
                changed = true;
            }
            if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                changed = true;
            }
            return visual;
        }

        /// Source-path guard for prefab factories (Б11): true when every named
        /// child of the prefab is an instance of the expected model.
        public static bool PrefabVisualsMatch(string prefabPath,
            params (string child, string model)[] expected)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach ((string child, string model) pair in expected)
                {
                    Transform tf = contents.transform.Find(pair.child);
                    Object source = tf != null
                        ? PrefabUtility.GetCorrespondingObjectFromSource(tf.gameObject)
                        : null;
                    string sourcePath = source != null
                        ? AssetDatabase.GetAssetPath(source) : null;
                    if (sourcePath != pair.model) return false;
                }
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// Controller path for a pack model IF the controller asset exists
        /// (preview's DefaultControllerFor promoted) — null for static props.
        public static string DefaultControllerFor(string modelPath)
        {
            string path = TA.ControllerPathFor(modelPath);
            return AssetDatabase.LoadAssetAtPath<
                UnityEditor.Animations.AnimatorController>(path) != null ? path : null;
        }
    }
}
