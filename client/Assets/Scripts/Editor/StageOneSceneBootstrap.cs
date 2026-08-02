using Ring.Data;
using Ring.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Ring.Editor
{
    /// One-shot bootstrap for Stage 1 Task 7 (spec §3.9/§3.10): materializes the 8
    /// balance SO assets in `Assets/Data` and wires a `Simulation` GameObject with
    /// `SimulationRunner` into `Main.unity`. Every step is guarded by an existence
    /// check, so re-running `Apply()` is a no-op — safe to invoke again after the
    /// owner hand-tweaks asset numbers in the Editor. Task 11 (spec §3.8) extends it
    /// to also add `AimProvider` to the same `Simulation` object and wire the scene's
    /// Main Camera plus the project-wide `InputSystem_Actions` asset into the runner.
    /// Task 12 (spec §3.7/§3.11) extends it further: a `Player` capsule (`PlayerView`)
    /// and a `Crosshair` marker (`CrosshairView`) are added at scene root, and
    /// `Main Camera` is reparented under a new `CameraRig` object that carries the
    /// `CameraRig` component — the camera itself stays at local zero. Two placeholder
    /// emissive materials (`PlayerEmissive`, `CrosshairEmissive`) are created under
    /// `Assets/Art/Materials` the same way the SO assets are: existence-guarded,
    /// never overwritten once created, so an owner's in-Editor color tweak survives
    /// a re-run.
    public static class StageOneSceneBootstrap
    {
        const string DataDir = "Assets/Data";
        const string ArtDir = "Assets/Art";
        const string MaterialsDir = "Assets/Art/Materials";
        const string ScenePath = "Assets/Scenes/Main.unity";
        const string ActionsAssetPath = "Assets/InputSystem_Actions.inputactions";
        const string PlayerObjectName = "Player";
        const string CameraRigObjectName = "CameraRig";
        const string CrosshairObjectName = "Crosshair";
        const string MarkerObjectName = "Marker";

        [MenuItem("Ring/Bootstrap/Stage 1 Scene")]
        public static void Apply()
        {
            // Open the target scene FIRST: EditorSceneManager.OpenScene(Single) unloads
            // the previously active scene's context, which invalidates any SO references
            // fetched beforehand (they come back as Unity "fake-null" objects — silently
            // wrong, not a NullReferenceException). Loading/creating the assets only
            // after the scene swap keeps the returned references live for SerializedObject.
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            HeroConfig hero = GetOrCreate<HeroConfig>("HeroConfig");
            WeaponConfig weapon = GetOrCreate<WeaponConfig>("WeaponConfig");
            MobConfig chaser = GetOrCreate<MobConfig>("MobChaserConfig");
            MobConfig gunner = GetOrCreate<MobConfig>("MobGunnerConfig");
            WaveConfig wave = GetOrCreate<WaveConfig>("WaveConfig");
            ArenaConfig arena = GetOrCreate<ArenaConfig>("ArenaConfig");
            GameFeelConfig gameFeel = GetOrCreate<GameFeelConfig>("GameFeelConfig");
            CameraConfig camera = GetOrCreate<CameraConfig>("CameraConfig");

            // Reapplied unconditionally (not just on first creation) so a stale
            // existing asset self-heals — fix-round 1: melee fields must read 0,
            // not chaser's contact-combat numbers (gunner never melees, spec §3.9
            // baseline is TestConfigs.Default().Gunner, where they're unset -> 0).
            bool gunnerChanged = ApplyGunnerDefaults(gunner);
            if (gunnerChanged) EditorUtility.SetDirty(gunner);
            AssetDatabase.SaveAssets();

            SimulationRunner runner = FindRunner(scene);
            bool sceneDirty = false;
            if (runner == null)
            {
                var go = new GameObject("Simulation");
                runner = go.AddComponent<SimulationRunner>();
                sceneDirty = true;
            }

            // AimProvider lives on the same "Simulation" object (Task 11/П-5 §3):
            // no separate GameObject needed, and existence-guarded like everything
            // else here so re-running Apply() stays a no-op.
            AimProvider aimProvider = runner.GetComponent<AimProvider>();
            if (aimProvider == null)
            {
                aimProvider = runner.gameObject.AddComponent<AimProvider>();
                sceneDirty = true;
            }

            Camera mainCamera = FindMainCamera(scene);
            if (mainCamera == null)
                throw new System.InvalidOperationException(
                    "StageOneSceneBootstrap: no Camera tagged MainCamera found in the scene.");

            var aimSo = new SerializedObject(aimProvider);
            bool aimRefsChanged = SetRef(aimSo, "_camera", mainCamera);
            if (aimRefsChanged)
            {
                aimSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            InputActionAsset actionsAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ActionsAssetPath);
            if (actionsAsset == null)
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: no InputActionAsset at '{ActionsAssetPath}'.");

            var so = new SerializedObject(runner);
            bool refsChanged = false;
            refsChanged |= SetRef(so, "_hero", hero);
            refsChanged |= SetRef(so, "_weapon", weapon);
            refsChanged |= SetRef(so, "_chaser", chaser);
            refsChanged |= SetRef(so, "_gunner", gunner);
            refsChanged |= SetRef(so, "_wave", wave);
            refsChanged |= SetRef(so, "_arena", arena);
            refsChanged |= SetRef(so, "_gameFeel", gameFeel);
            refsChanged |= SetRef(so, "_camera", camera);
            refsChanged |= SetRef(so, "_actionsAsset", actionsAsset);
            refsChanged |= SetRef(so, "_aimProvider", aimProvider);
            if (refsChanged)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 12 (spec §3.7/§3.11): placeholder emissive materials, then the
            // Player capsule, the CameraRig (parent of Main Camera) and the Crosshair
            // marker. Colors are greybox placeholders — Task 13+ owns the real art
            // pass; only the emissive channel matters here (dark-neon readability).
            Material playerMat = GetOrCreateMaterial(
                "PlayerEmissive",
                baseColor: new Color(0.03f, 0.03f, 0.04f),
                emissionColor: new Color(0f, 2.5f, 3f));
            Material crosshairMat = GetOrCreateMaterial(
                "CrosshairEmissive",
                baseColor: new Color(0.04f, 0.02f, 0f),
                emissionColor: new Color(3.5f, 1.2f, 0f));

            GameObject playerGo = FindRootObject(scene, PlayerObjectName);
            if (playerGo == null)
            {
                playerGo = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                playerGo.name = PlayerObjectName;
                RemoveCollider(playerGo);
                sceneDirty = true;
            }
            MeshRenderer playerRenderer = playerGo.GetComponent<MeshRenderer>();
            if (playerRenderer.sharedMaterial != playerMat)
            {
                playerRenderer.sharedMaterial = playerMat;
                sceneDirty = true;
            }
            PlayerView playerView = playerGo.GetComponent<PlayerView>();
            if (playerView == null)
            {
                playerView = playerGo.AddComponent<PlayerView>();
                sceneDirty = true;
            }
            var playerSo = new SerializedObject(playerView);
            bool playerRefsChanged = false;
            playerRefsChanged |= SetRef(playerSo, "_runner", runner);
            playerRefsChanged |= SetRef(playerSo, "_aimProvider", aimProvider);
            if (playerRefsChanged)
            {
                playerSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // CameraRig is the parent: it carries position/rotation, Main Camera
            // stays a child at local zero (П-3 resolution). Reparenting an existing
            // camera is itself guarded so a second run is a no-op.
            GameObject cameraRigGo = FindRootObject(scene, CameraRigObjectName);
            if (cameraRigGo == null)
            {
                cameraRigGo = new GameObject(CameraRigObjectName);
                sceneDirty = true;
            }
            if (mainCamera.transform.parent != cameraRigGo.transform)
            {
                mainCamera.transform.SetParent(cameraRigGo.transform, worldPositionStays: false);
                mainCamera.transform.localPosition = Vector3.zero;
                mainCamera.transform.localRotation = Quaternion.identity;
                sceneDirty = true;
            }
            CameraRig cameraRig = cameraRigGo.GetComponent<CameraRig>();
            if (cameraRig == null)
            {
                cameraRig = cameraRigGo.AddComponent<CameraRig>();
                sceneDirty = true;
            }
            var cameraRigSo = new SerializedObject(cameraRig);
            bool cameraRigRefsChanged = false;
            cameraRigRefsChanged |= SetRef(cameraRigSo, "_config", camera);
            cameraRigRefsChanged |= SetRef(cameraRigSo, "_runner", runner);
            cameraRigRefsChanged |= SetRef(cameraRigSo, "_aimProvider", aimProvider);
            if (cameraRigRefsChanged)
            {
                cameraRigSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            GameObject crosshairGo = FindRootObject(scene, CrosshairObjectName);
            if (crosshairGo == null)
            {
                crosshairGo = new GameObject(CrosshairObjectName);
                sceneDirty = true;
            }
            CrosshairView crosshairView = crosshairGo.GetComponent<CrosshairView>();
            if (crosshairView == null)
            {
                crosshairView = crosshairGo.AddComponent<CrosshairView>();
                sceneDirty = true;
            }
            Transform markerTf = crosshairGo.transform.Find(MarkerObjectName);
            GameObject markerGo;
            if (markerTf == null)
            {
                markerGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                markerGo.name = MarkerObjectName;
                RemoveCollider(markerGo);
                markerGo.transform.SetParent(crosshairGo.transform, false);
                // Quad's default normal faces -Z; lay it flat, normal up, for a
                // top-down ¾ camera.
                markerGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                markerGo.transform.localScale = Vector3.one * 0.5f;
                sceneDirty = true;
            }
            else
            {
                markerGo = markerTf.gameObject;
            }
            MeshRenderer markerRenderer = markerGo.GetComponent<MeshRenderer>();
            if (markerRenderer.sharedMaterial != crosshairMat)
            {
                markerRenderer.sharedMaterial = crosshairMat;
                sceneDirty = true;
            }
            var crosshairSo = new SerializedObject(crosshairView);
            bool crosshairRefsChanged = false;
            crosshairRefsChanged |= SetRef(crosshairSo, "_marker", markerGo.transform);
            crosshairRefsChanged |= SetRef(crosshairSo, "_aimProvider", aimProvider);
            if (crosshairRefsChanged)
            {
                crosshairSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            if (sceneDirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log($"StageOneSceneBootstrap: gunner {(gunnerChanged ? "updated" : "ok")}, " +
                $"scene {(sceneDirty ? "updated" : "already up to date")}.");
        }

        /// Full field set of the ranged archetype — mirrors TestConfigs.Default().Gunner
        /// exactly (spec §3.9 baseline). Fix-round 1: TestConfigs.Gunner is a struct
        /// literal that only sets the ranged-combat fields; everything it doesn't list
        /// (ContactDamage, AttackRange, TelegraphSeconds, AttackCooldown) defaults to 0
        /// for a struct, NOT to MobConfig's chaser-mirrored class field defaults — the
        /// gunner never melees, so those must read 0 here too. Reapplied every run
        /// (idempotent via per-field diff) so a stale asset self-heals.
        static bool ApplyGunnerDefaults(MobConfig m)
        {
            bool changed = false;
            changed |= SetIfDifferent(ref m.MaxSpeed, 4f);
            changed |= SetIfDifferent(ref m.Accel, 25f);
            changed |= SetIfDifferent(ref m.Radius, 0.5f);
            changed |= SetIfDifferent(ref m.MaxHp, 20f);
            changed |= SetIfDifferent(ref m.ContactDamage, 0f);
            changed |= SetIfDifferent(ref m.AttackRange, 0f);
            changed |= SetIfDifferent(ref m.TelegraphSeconds, 0f);
            changed |= SetIfDifferent(ref m.AttackCooldown, 0f);
            changed |= SetIfDifferent(ref m.PreferredRange, 9f);
            changed |= SetIfDifferent(ref m.RangeTolerance, 1.5f);
            changed |= SetIfDifferent(ref m.StrafeSpeed, 3f);
            changed |= SetIfDifferent(ref m.FireInterval, 1.6f);
            changed |= SetIfDifferent(ref m.ProjectileSpeed, 14f);
            changed |= SetIfDifferent(ref m.ProjectileRadius, 0.15f);
            changed |= SetIfDifferent(ref m.ProjectileLifetime, 3f);
            changed |= SetIfDifferent(ref m.ProjectileDamage, 8f);
            changed |= SetIfDifferent(ref m.LeadFactor, 0.8f);
            changed |= SetIfDifferent(ref m.SeparationRadius, 1.2f);
            changed |= SetIfDifferent(ref m.SeparationStrength, 6f);
            changed |= SetIfDifferent(ref m.AvoidLookahead, 3f);
            return changed;
        }

        static bool SetIfDifferent(ref float field, float value)
        {
            if (field == value) return false;
            field = value;
            return true;
        }

        static T GetOrCreate<T>(string assetName) where T : ScriptableObject
        {
            string path = $"{DataDir}/{assetName}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;

            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        /// Existence-guarded like `GetOrCreate<T>`: once the `.mat` exists on disk,
        /// its colors are never reapplied — an owner's in-Editor hand-tweak survives
        /// a re-run, same contract as the SO assets above.
        static Material GetOrCreateMaterial(string assetName, Color baseColor, Color emissionColor)
        {
            string path = $"{MaterialsDir}/{assetName}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            if (!AssetDatabase.IsValidFolder(MaterialsDir))
                AssetDatabase.CreateFolder(ArtDir, "Materials");

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                throw new System.InvalidOperationException(
                    "StageOneSceneBootstrap: URP Lit shader not found — is URP installed?");

            var mat = new Material(shader) { name = assetName };
            mat.SetColor("_BaseColor", baseColor);
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.SetColor("_EmissionColor", emissionColor);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static SimulationRunner FindRunner(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var runner = root.GetComponentInChildren<SimulationRunner>(true);
                if (runner != null) return runner;
            }
            return null;
        }

        static Camera FindMainCamera(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Camera cam in root.GetComponentsInChildren<Camera>(true))
                {
                    if (cam.CompareTag("MainCamera")) return cam;
                }
            }
            return null;
        }

        static GameObject FindRootObject(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
            }
            return null;
        }

        static void RemoveCollider(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
        }

        static bool SetRef(SerializedObject so, string fieldName, Object value)
        {
            SerializedProperty prop = so.FindProperty(fieldName);
            if (prop == null)
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: {so.targetObject.GetType().Name} has no serialized field '{fieldName}'.");
            if (prop.objectReferenceValue == value) return false;
            prop.objectReferenceValue = value;
            return true;
        }
    }
}
