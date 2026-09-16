using System;
using System.IO;
using Ring.Data;
using Ring.Simulation.Core;
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
        /// the aim ray's two notch children would have wanted it a third time.
        /// ⚠ AND THE COUNT IS NOT FINISHED, WHICH IS SAID HERE RATHER THAN
        /// IMPLIED AWAY (fix round 1): two more copies of the same idiom, in
        /// ternary form, are still alive in `AssetPreviewSceneBootstrap` in
        /// this same assembly. Converting them is a task of its own and not
        /// this one's — so this helper has two callers today and is owed two
        /// more.
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
        /// ⛔ `startDisabled` SAYS THE COMMITTED SCENE HOLDS THIS RENDERER
        /// SWITCHED OFF — the runtime view turns it on the frame it has
        /// something to draw. All three of today's renderers want that: a
        /// `LineRenderer` saved enabled shows its default (0,0,0)→(0,0,1)
        /// segment to anyone who opens the scene without entering Play, and
        /// the aim ray has been committed dark since Task 20 for exactly that
        /// reason. The parameter stays rather than becoming a constant because
        /// nothing about this helper says a world line must be born dark, and
        /// the next caller may well want it lit.
        ///
        /// ⛔ AND IT IS SELF-HEALED, NOT ONE-TIME (app-461s T3 fix round 1) —
        /// the same treatment the material gets, for the same reason: a
        /// one-time write only ever reaches renderers this helper CREATES, so
        /// a re-`Apply` could never repair a scene whose renderers were saved
        /// in the wrong state, and the first version of this task committed
        /// exactly such a scene. `positionCount` is reconciled on the same
        /// argument: an Inspector edit that shrank it would make the caller's
        /// own `SetPosition` write out of bounds every frame, and nothing
        /// would put it back.
        /// ⚠ `useWorldSpace` and `shadowCastingMode` stay one-time on purpose:
        /// they are module settings an owner may legitimately want to override
        /// in the Inspector, the same line `GetOrCreateMaterial`'s `configure`
        /// draws between creation and reconciliation.
        public static LineRenderer EnsureWorldLine(GameObject host, Material material,
            int positionCount, bool startDisabled, ref bool changed)
        {
            LineRenderer line = host.GetComponent<LineRenderer>();
            if (line == null)
            {
                line = host.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                changed = true;
            }
            if (line.positionCount != positionCount)
            {
                line.positionCount = positionCount;
                changed = true;
            }
            if (startDisabled && line.enabled)
            {
                line.enabled = false;
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
        /// ⛔⛔ A LIST OF MARKER NAMES, NOT ONE (app-w4ca T4). With a single
        /// marker the check only worked while that field was the LAST one in
        /// its class, and nothing in the signature said so: a field added AFTER
        /// the marker never dirtied the asset, and no gate went red for it —
        /// R-IDEM stays green because a no-op is idempotent, and the EditMode
        /// set stays green because the tests read C# defaults. The numbers
        /// simply never reached the `.asset`, which is deviation 11a. Taking
        /// every name a task adds removes the "keep LAST" requirement entirely:
        /// each task APPENDS its new field here instead of relocating a marker.
        ///
        /// ⚠ AND THE HALF THIS DOES NOT FIX IS SAID OUT LOUD: the check sees a
        /// MISSING name and never sees a SURPLUS one, so a field REMOVED from a
        /// class (the shape T5b has) still needs an explicit `SetDirty` — this
        /// would happily report "all present" on an asset carrying a key no
        /// class has any more.
        public static void EnsureAssetHasKey(Object so, string assetPath,
            params string[] markerFields)
        {
            string text = File.ReadAllText(assetPath);
            foreach (string field in markerFields)
                if (!text.Contains(field)) { EditorUtility.SetDirty(so); return; }
        }

        /// THE OTHER HALF OF THE SAME GUARD (app-w4ca T5b) — the one the doc
        /// above names as missing, in the shape it named: a field REMOVED from
        /// a class. `EnsureAssetHasKey` sees a MISSING name and never a SURPLUS
        /// one, so a removed field's key stays in the committed YAML forever:
        /// nothing dirties the SO, `SaveAssets` writes nothing, and no gate
        /// goes red for it (R-IDEM stays green because a no-op is idempotent,
        /// and EditMode stays green because the tests read C# defaults). What
        /// the owner is left with is a number in the Inspector's YAML that no
        /// class has any more — the traceability defect its twin exists to
        /// prevent, mirrored.
        ///
        /// Same technique, inverted: dirty the SO while ANY of the named keys
        /// is STILL in the file, so the `AssetDatabase.SaveAssets()` the caller
        /// already makes rewrites the asset with the class's CURRENT field set
        /// and the surplus keys drop out with it.
        /// ⚠ ONE-TIME BY CONSTRUCTION, exactly like its twin: after that
        /// rewrite the names are gone from the text, so a second Apply dirties
        /// nothing and R-IDEM holds.
        /// ⛔ NAME THE DEPARTED FIELDS, NOT A MARKER: there is no "last field"
        /// convention on this side — the names are simply what the task took
        /// away. They stop being needed once every committed asset has been
        /// rewritten past them, and a later task that finds the list stale is
        /// meant to shorten it rather than grow it forever.
        /// ⛔ AND THE MATCH IS KEY-BOUNDED (`"Name:"`), unlike its twin's bare
        /// `Contains`, because the two directions fail differently. A surplus
        /// name matched by a LONGER key (`MobTurnDegPerSecScale` answering for
        /// `MobTurnDegPerSec`) would dirty the asset on EVERY run forever —
        /// a re-write of identical text, so no gate would ever go red for it.
        /// ⚠ The twin's own bare `Contains` has the mirrored hole (a longer key
        /// answers "present" and suppresses the backfill); it is untouched here
        /// because its fourteen call sites are not this task's to re-verify —
        /// recorded as a side-quest instead of fixed silently.
        public static void EnsureAssetLacksKey(Object so, string assetPath,
            params string[] removedFields)
        {
            string text = File.ReadAllText(assetPath);
            foreach (string field in removedFields)
                if (text.Contains(field + ":")) { EditorUtility.SetDirty(so); return; }
        }

        /// bd `app-saqr` (T4b): THE TWELVE SHEETS THE GAME LOADS, ASSEMBLED ONCE.
        ///
        /// ⛔ ONE HOME, because there are two callers and their disagreement
        /// would be silent: `LongRunHarness` MEASURES this configuration and
        /// `ShippedConfigVerify` asserts that it ASSEMBLES. A second spelling of
        /// the list means a thirteenth sheet needs two edits, and the run where
        /// one of them is missed is the run where the gate stops testing what the
        /// harness measures.
        ///
        /// ⚠ NOTHING IS SUBSTITUTED OR REPAIRED HERE: the point of both callers
        /// is to meet the game's own numbers, so this fails exactly where the
        /// game would — `SimConfigBuilder.Build` throws with every broken rule
        /// named, and that message is the whole value of the gate.
        public static SimConfig BuildShippedConfig()
        {
            return SimConfigBuilder.Build(
                LoadBattleAsset<HeroConfig>("HeroConfig"),
                LoadBattleAsset<WeaponConfig>("WeaponConfig"),
                LoadBattleAsset<MobConfig>("MobChaserConfig"),
                LoadBattleAsset<MobConfig>("MobGunnerConfig"),
                LoadBattleAsset<WaveConfig>("WaveConfig"),
                LoadBattleAsset<ArenaConfig>("ArenaConfig"),
                LoadBattleAsset<VisibilityConfig>("VisibilityConfig"),
                // Stage 3 Task 12 (owner decision R-73): the two archetype
                // sheets and the match-flow one ride along — an Elite-free
                // configuration is not the arena the game ships.
                LoadBattleAsset<MobConfig>("MobEliteConfig"),
                LoadBattleAsset<MobConfig>("MobDirectorConfig"),
                LoadBattleAsset<MatchFlowConfig>("MatchFlowConfig"),
                // Stage 3 Task 13: the catalog and the loot balance sheet, same
                // reasoning.
                LoadBattleAsset<ItemCatalog>("ItemCatalog"),
                LoadBattleAsset<LootConfig>("LootConfig"));
        }

        /// One battle sheet, refused by name when it is missing: a null here
        /// would surface as a `NullReferenceException` inside the builder,
        /// naming nothing.
        public static T LoadBattleAsset<T>(string name) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>($"{BattleDataDir}/{name}.asset");
            if (asset == null)
                throw new InvalidOperationException(
                    $"missing battle asset '{BattleDataDir}/{name}.asset'.");
            return asset;
        }

        public const string BattleDataDir = "Assets/Data";
    }
}
