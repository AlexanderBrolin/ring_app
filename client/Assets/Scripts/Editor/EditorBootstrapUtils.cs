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

        /// The one component of its type anywhere in `scene`, inactive
        /// objects included — the sibling of `FindRootObject` for the case
        /// where the TYPE is the identity and the object's name is not.
        /// Promoted out of `StageOneSceneBootstrap`'s private `FindRunner`
        /// when Stage 2 Task 44e needed the same `SimulationRunner` from the
        /// other bootstrap: the name of the object it sits on is a literal of
        /// the file that creates it, and copying that literal into a second
        /// file would tie the two together far harder than sharing this
        /// search does.
        public static T FindComponentInScene<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<T>(true);
                if (found != null) return found;
            }
            return null;
        }

        /// Find-or-create a named child under `parent`: handed back either way,
        /// with the answer of whether it had to be created — the shape every
        /// `Ensure*` of the bootstraps already speaks.
        ///
        /// PROMOTED OUT OF `StageOneSceneBootstrap.EnsureSocketChild` (app-461s
        /// T3), which stays as a thin wrapper over this one, the same way
        /// `EnsureCasingsLayer` became a wrapper over `EnsureUserLayer`. The
        /// identical block also opens `EnsureAimProxyCapsule` in that file, and
        /// the aim ray's two notch children would have wanted it a third time:
        /// three copies of "look it up, make it if it is missing, say which
        /// happened" is two copies too many.
        public static bool EnsureChild(Transform parent, string name, out Transform child)
        {
            child = parent.Find(name);
            if (child != null) return false;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            child = go.transform;
            return true;
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

        /// Array sibling of `SetRef` (T24-2: `PersistentPropsDirector`'s
        /// per-archetype `Mesh[]` gib-part fields) — `SetRef` only ever
        /// touches a single `objectReferenceValue`, arrays need their own
        /// element-by-element diff so a re-Apply with an unchanged FBX stays
        /// a no-op like every other wiring call in this file.
        public static bool SetObjectArray(SerializedObject so, string fieldName, Object[] values)
        {
            SerializedProperty prop = so.FindProperty(fieldName);
            if (prop == null)
                throw new InvalidOperationException(
                    $"{so.targetObject.GetType().Name} has no serialized field '{fieldName}'.");
            bool changed = false;
            if (prop.arraySize != values.Length)
            {
                prop.arraySize = values.Length;
                changed = true;
            }
            for (int i = 0; i < values.Length; i++)
            {
                SerializedProperty element = prop.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue == values[i]) continue;
                element.objectReferenceValue = values[i];
                changed = true;
            }
            return changed;
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

        /// A world-space `LineRenderer` on an ALREADY RESOLVED host object
        /// (app-461s T3): get-or-add the component, one-time module settings
        /// inside the creation guard, unconditional material self-heal outside
        /// it — the same existence-guard/self-heal split `GetOrCreateMaterial`
        /// and `EnsureVisual` above already keep.
        ///
        /// ⛔ IT TAKES THE HOST, NOT A PARENT PLUS A NAME. The block it was
        /// extracted from is the aim ray's, and the aim ray is a ROOT object of
        /// the scene (`FindRootObject`), not a child of anything; its two notch
        /// strokes, by contrast, are children (`EnsureChild`). Resolving the
        /// object therefore stays with the caller, which is what lets both
        /// kinds share this middle.
        ///
        /// ⛔ `startDisabled` IS A PARAMETER BECAUSE THE CALLERS DISAGREE ON IT.
        /// The ray's one-time setup switches the renderer off (`AimRayView`
        /// turns it back on the first frame it has something to draw), the
        /// notches' setup does not. Once `AddComponent` moved in here the
        /// caller lost sight of the "just created" branch — `ref bool changed`
        /// does not report it, because the material self-heal raises the very
        /// same flag — so without this parameter a fresh bootstrap would write
        /// a scene whose three renderers sit in states the committed one does
        /// not have.
        public static LineRenderer EnsureWorldLine(GameObject host, Material material,
            int positionCount, bool startDisabled, ref bool changed)
        {
            LineRenderer line = host.GetComponent<LineRenderer>();
            if (line == null)
            {
                line = host.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = positionCount;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (startDisabled) line.enabled = false;
                changed = true;
            }
            if (line.sharedMaterial != material)
            {
                line.sharedMaterial = material;
                changed = true;
            }
            return line;
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

        /// One-time sync guard (Task 17, extracted from the Task 27-review
        /// GameFeelConfig-only inline check in StageOneSceneBootstrap): an
        /// already-committed SO asset predates whichever field the caller
        /// passes as `markerField` (the class's LAST field, PB9's sync-marker
        /// convention) — Unity only writes an SO's CURRENT field set to disk
        /// when something marks it dirty, so a stale committed asset falls
        /// back to the C# field initializer at load time (not wrong, just
        /// invisible to the owner's Inspector/YAML hand-tweak). Detected via
        /// a direct text read of the asset file rather than SerializedObject
        /// — cheap, no reimport needed; `AssetDatabase.SaveAssets()` (called
        /// once by the caller, after every `EnsureAssetHasKey`) is what
        /// actually flushes the dirtied SO's full current field set to disk.
        public static void EnsureAssetHasKey(Object so, string assetPath, string markerField)
        {
            if (!File.ReadAllText(assetPath).Contains(markerField))
                EditorUtility.SetDirty(so);
        }
    }
}
