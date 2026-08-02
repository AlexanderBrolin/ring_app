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
