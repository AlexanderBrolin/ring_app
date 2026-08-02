using Ring.Data;
using Ring.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ring.Editor
{
    /// One-shot bootstrap for Stage 1 Task 7 (spec §3.9/§3.10): materializes the 8
    /// balance SO assets in `Assets/Data` and wires a `Simulation` GameObject with
    /// `SimulationRunner` into `Main.unity`. Every step is guarded by an existence
    /// check, so re-running `Apply()` is a no-op — safe to invoke again after the
    /// owner hand-tweaks asset numbers in the Editor.
    public static class StageOneSceneBootstrap
    {
        const string DataDir = "Assets/Data";
        const string ScenePath = "Assets/Scenes/Main.unity";

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
            MobConfig gunner = GetOrCreate<MobConfig>("MobGunnerConfig", ApplyGunnerDefaults);
            WaveConfig wave = GetOrCreate<WaveConfig>("WaveConfig");
            ArenaConfig arena = GetOrCreate<ArenaConfig>("ArenaConfig");
            GameFeelConfig gameFeel = GetOrCreate<GameFeelConfig>("GameFeelConfig");
            CameraConfig camera = GetOrCreate<CameraConfig>("CameraConfig");
            AssetDatabase.SaveAssets();

            SimulationRunner runner = FindRunner(scene);
            bool sceneDirty = false;
            if (runner == null)
            {
                var go = new GameObject("Simulation");
                runner = go.AddComponent<SimulationRunner>();
                sceneDirty = true;
            }

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
            if (refsChanged)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            if (sceneDirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log($"StageOneSceneBootstrap: assets ok, scene {(sceneDirty ? "updated" : "already up to date")}.");
        }

        static void ApplyGunnerDefaults(MobConfig m)
        {
            // Ranged archetype numbers (spec §3.9 Task 7); everything not listed here
            // (ContactDamage, AttackRange, TelegraphSeconds, AttackCooldown, Radius,
            // SeparationRadius/Strength, AvoidLookahead) keeps MobConfig's chaser-mirrored
            // field defaults — orchestrator resolution: "остальное как chaser-дефолты".
            m.MaxSpeed = 4f;
            m.Accel = 25f;
            m.Radius = 0.5f;
            m.MaxHp = 20f;
            m.PreferredRange = 9f;
            m.RangeTolerance = 1.5f;
            m.StrafeSpeed = 3f;
            m.FireInterval = 1.6f;
            m.ProjectileSpeed = 14f;
            m.ProjectileRadius = 0.15f;
            m.ProjectileLifetime = 3f;
            m.ProjectileDamage = 8f;
            m.LeadFactor = 0.8f;
            m.SeparationRadius = 1.2f;
            m.SeparationStrength = 6f;
            m.AvoidLookahead = 3f;
        }

        static T GetOrCreate<T>(string assetName, System.Action<T> configure = null) where T : ScriptableObject
        {
            string path = $"{DataDir}/{assetName}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;

            var asset = ScriptableObject.CreateInstance<T>();
            configure?.Invoke(asset);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
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

        static bool SetRef(SerializedObject so, string fieldName, Object value)
        {
            SerializedProperty prop = so.FindProperty(fieldName);
            if (prop == null)
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: SimulationRunner has no serialized field '{fieldName}'.");
            if (prop.objectReferenceValue == value) return false;
            prop.objectReferenceValue = value;
            return true;
        }
    }
}
