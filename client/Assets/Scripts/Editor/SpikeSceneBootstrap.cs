// TEMPORARY (T3 -> T30): this bootstrap, the Assets/Scenes/NetSpike.unity scene
// it builds and the Assets/Prefabs/SpikePlayer.prefab it authors are deleted in
// T30 together with Assets/Scripts/Networking/Spike/**.
using FishNet.Component.Spawning;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Predicting;
using FishNet.Managing.Timing;
using FishNet.Managing.Transporting;
using FishNet.Object;
using FishNet.Transporting.Tugboat;
using Ring.Data;
using Ring.Networking.Spike;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ring.Editor
{
    /// Builds the T3 network-spike scene (`Assets/Scenes/NetSpike.unity`): a
    /// `NetworkManager` root carrying the managers whose inspector the owner
    /// actually touches on plan step 3 (`TransportManager` — the latency
    /// simulator; `TimeManager` — the spec-pinned 30 Hz tick; `PredictionManager`
    /// — state interpolation, i.e. input redundancy per Т2 note §5), Tugboat as
    /// the transport, FishNet's own `PlayerSpawner` wired to a generated
    /// `SpikePlayer` prefab, and a `SpikeBootstrap` object wired to the six
    /// balance assets `Main.unity` already feeds `SimulationRunner`.
    ///
    /// Idempotent like every other bootstrap here: every step is existence-guarded
    /// and the scene is saved only when something actually changed, so a second
    /// `Apply()` leaves an empty `git diff`. Scenes in this project are never
    /// hand-authored — this file is the only definition of NetSpike.unity.
    ///
    /// NOT registered in `EditorBuildSettings` (plan T3): the spike is opened by
    /// hand in the Editor and never ships in a build. `BuildCommands` builds
    /// whatever `EditorBuildSettings.scenes` lists (today: `Main.unity` alone), so
    /// leaving that file untouched is exactly what keeps the spike out.
    ///
    /// Side effect to expect on the first run: FishNet's own asset postprocessor
    /// (`Runtime/Editor/PrefabCollectionGenerator/Generator.cs:587`) appends the
    /// new `SpikePlayer.prefab` to the committed
    /// `Assets/DefaultPrefabObjects.asset` — that diff is FishNet's spawnable
    /// prefab registry doing its job, and it reverts by itself when T30 deletes
    /// the prefab.
    public static class SpikeSceneBootstrap
    {
        const string ScenePath = "Assets/Scenes/NetSpike.unity";
        const string PlayerPrefabPath = "Assets/Prefabs/SpikePlayer.prefab";
        const string DataDir = "Assets/Data";
        const string SpawnablePrefabsPath = "Assets/DefaultPrefabObjects.asset";

        const string NetworkManagerObjectName = "NetworkManager";
        const string BootstrapObjectName = "SpikeBootstrap";
        const string CameraObjectName = "Main Camera";
        const string LightObjectName = "KeyLight";
        const string FloorObjectName = "Floor";
        const string PlayerPrefabName = "SpikePlayer";
        const string GraphicalChildName = "Visual";
        const string BodyChildName = "Body";

        /// Spec §3.7: the server simulates 30 times per second. TimeManager's own
        /// default happens to match, but the number is pinned by the spec, so the
        /// scene states it instead of inheriting it.
        const int TickRate = 30;

        [MenuItem("Ring/Bootstrap/Net Spike Scene")]
        public static void Apply()
        {
            // Scene first (lesson 15, same as every bootstrap here): OpenScene
            // invalidates asset references fetched beforehand.
            Scene scene = OpenOrCreateScene(out bool sceneDirty);

            HeroConfig hero = Load<HeroConfig>("HeroConfig");
            WeaponConfig weapon = Load<WeaponConfig>("WeaponConfig");
            MobConfig chaser = Load<MobConfig>("MobChaserConfig");
            MobConfig gunner = Load<MobConfig>("MobGunnerConfig");
            WaveConfig wave = Load<WaveConfig>("WaveConfig");
            ArenaConfig arena = Load<ArenaConfig>("ArenaConfig");

            NetworkObject playerPrefab = GetOrCreatePlayerPrefab();
            var spawnablePrefabs =
                AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>(SpawnablePrefabsPath);
            if (spawnablePrefabs == null)
                throw new System.InvalidOperationException(
                    $"SpikeSceneBootstrap: no DefaultPrefabObjects at '{SpawnablePrefabsPath}' — " +
                    "let FishNet generate it by reimporting any NetworkObject prefab first.");

            GameObject managerGo = GetOrCreateRoot(scene, NetworkManagerObjectName, ref sceneDirty);
            NetworkManager manager = EnsureComponent<NetworkManager>(managerGo, ref sceneDirty);
            // Added explicitly even though NetworkManager.Awake would create them
            // at runtime (NetworkManager.cs:320-333): the owner has to reach the
            // latency simulator, the tick rate and state interpolation in the
            // inspector BEFORE pressing Play.
            EnsureComponent<TransportManager>(managerGo, ref sceneDirty);
            EnsureComponent<Tugboat>(managerGo, ref sceneDirty);
            TimeManager timeManager = EnsureComponent<TimeManager>(managerGo, ref sceneDirty);
            EnsureComponent<PredictionManager>(managerGo, ref sceneDirty);
            PlayerSpawner spawner = EnsureComponent<PlayerSpawner>(managerGo, ref sceneDirty);

            var managerSo = new SerializedObject(manager);
            if (EditorBootstrapUtils.SetRef(managerSo, "_spawnablePrefabs", spawnablePrefabs))
            {
                managerSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            var timeSo = new SerializedObject(timeManager);
            if (SetInt(timeSo, "_tickRate", TickRate))
            {
                timeSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            var spawnerSo = new SerializedObject(spawner);
            if (EditorBootstrapUtils.SetRef(spawnerSo, "_playerPrefab", playerPrefab))
            {
                spawnerSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            GameObject bootstrapGo = GetOrCreateRoot(scene, BootstrapObjectName, ref sceneDirty);
            SpikeBootstrap bootstrap = EnsureComponent<SpikeBootstrap>(bootstrapGo, ref sceneDirty);
            var bootstrapSo = new SerializedObject(bootstrap);
            bool bootstrapRefsChanged = false;
            bootstrapRefsChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_networkManager", manager);
            bootstrapRefsChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_hero", hero);
            bootstrapRefsChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_weapon", weapon);
            bootstrapRefsChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_chaser", chaser);
            bootstrapRefsChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_gunner", gunner);
            bootstrapRefsChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_wave", wave);
            bootstrapRefsChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_arena", arena);
            if (bootstrapRefsChanged)
            {
                bootstrapSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            BuildFloor(scene, arena, ref sceneDirty);
            BuildLight(scene, ref sceneDirty);
            BuildCamera(scene, arena, ref sceneDirty);

            if (sceneDirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log($"SpikeSceneBootstrap: scene {(sceneDirty ? "updated" : "already up to date")} " +
                $"at {ScenePath} (deliberately NOT added to EditorBuildSettings).");
        }

        static Scene OpenOrCreateScene(out bool created)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                created = false;
                return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            EditorBootstrapUtils.EnsureFolder("Assets/Scenes");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            created = true;
            return scene;
        }

        static T Load<T>(string assetName) where T : ScriptableObject
        {
            string path = $"{DataDir}/{assetName}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new System.InvalidOperationException(
                    $"SpikeSceneBootstrap: no {typeof(T).Name} at '{path}' — " +
                    "run Ring/Bootstrap/Stage 1 Scene first.");
            return asset;
        }

        /// The owned player: a `NetworkObject` with prediction enabled plus the
        /// spike controller on the root, and an empty `Visual` child as the
        /// graphical object FishNet's tick smoother interpolates (spec §3.9 Р78 —
        /// the smoother owns that transform, so the capsule hangs one level below
        /// it and keeps its own standing offset).
        static NetworkObject GetOrCreatePlayerPrefab()
            => EditorBootstrapUtils.BuildPrefab<NetworkObject>(PlayerPrefabPath, () =>
            {
                var root = new GameObject(PlayerPrefabName);
                NetworkObject nob = root.AddComponent<NetworkObject>();
                root.AddComponent<SpikePlayerController>();

                var graphical = new GameObject(GraphicalChildName);
                graphical.transform.SetParent(root.transform, false);

                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = BodyChildName;
                // Cosmetics only: collisions are the simulation's business
                // (Geometry/PlayerMovementSystem), never Unity physics.
                EditorBootstrapUtils.RemoveCollider(body);
                body.transform.SetParent(graphical.transform, false);
                body.transform.localPosition = new Vector3(0f, 1f, 0f); // capsule pivot is its centre

                var so = new SerializedObject(nob);
                so.FindProperty("_enablePrediction").boolValue = true;
                so.FindProperty("_graphicalObject").objectReferenceValue = graphical.transform;
                so.ApplyModifiedPropertiesWithoutUndo();
                return root;
            });

        static GameObject GetOrCreateRoot(Scene scene, string name, ref bool changed)
        {
            GameObject go = EditorBootstrapUtils.FindRootObject(scene, name);
            if (go != null) return go;
            changed = true;
            return new GameObject(name);
        }

        static T EnsureComponent<T>(GameObject go, ref bool changed) where T : Component
        {
            T component = go.GetComponent<T>();
            if (component != null) return component;
            changed = true;
            return go.AddComponent<T>();
        }

        /// Integer sibling of `EditorBootstrapUtils.SetRef` — write-if-different so
        /// a re-Apply stays a no-op like every other wiring call here.
        static bool SetInt(SerializedObject so, string fieldName, int value)
        {
            SerializedProperty prop = so.FindProperty(fieldName);
            if (prop == null)
                throw new System.InvalidOperationException(
                    $"{so.targetObject.GetType().Name} has no serialized field '{fieldName}'.");
            if (prop.intValue == value) return false;
            prop.intValue = value;
            return true;
        }

        /// Ground reference: without something under the capsule a correction of a
        /// few centimetres is invisible. Sized off ArenaConfig so the spike's
        /// playfield matches the arena the movement tick actually collides with.
        static void BuildFloor(Scene scene, ArenaConfig arena, ref bool changed)
        {
            GameObject floor = EditorBootstrapUtils.FindRootObject(scene, FloorObjectName);
            if (floor == null)
            {
                floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.name = FloorObjectName;
                EditorBootstrapUtils.RemoveCollider(floor);
                changed = true;
            }
            var scale = new Vector3(arena.Radius / 5f, 1f, arena.Radius / 5f); // plane is 10 x 10
            if (floor.transform.localScale != scale)
            {
                floor.transform.localScale = scale;
                changed = true;
            }
            if (floor.transform.position != Vector3.zero)
            {
                floor.transform.position = Vector3.zero;
                changed = true;
            }
        }

        static void BuildLight(Scene scene, ref bool changed)
        {
            GameObject go = EditorBootstrapUtils.FindRootObject(scene, LightObjectName);
            if (go == null)
            {
                go = new GameObject(LightObjectName);
                changed = true;
            }
            Light light = EnsureComponent<Light>(go, ref changed);
            if (light.type != LightType.Directional)
            {
                light.type = LightType.Directional;
                changed = true;
            }
            var rotation = Quaternion.Euler(50f, -30f, 0f);
            if (go.transform.rotation != rotation)
            {
                go.transform.rotation = rotation;
                changed = true;
            }
        }

        /// Top-down 3/4 view (ADR-001 §9), pulled back far enough to hold the whole
        /// scripted path in frame.
        static void BuildCamera(Scene scene, ArenaConfig arena, ref bool changed)
        {
            GameObject go = EditorBootstrapUtils.FindRootObject(scene, CameraObjectName);
            if (go == null)
            {
                go = new GameObject(CameraObjectName) { tag = "MainCamera" };
                changed = true;
            }
            EnsureComponent<Camera>(go, ref changed);
            EnsureComponent<AudioListener>(go, ref changed);

            float height = Mathf.Max(12f, arena.Radius * 0.8f);
            var position = new Vector3(0f, height, -height * 0.6f);
            if (go.transform.position != position)
            {
                go.transform.position = position;
                changed = true;
            }
            var rotation = Quaternion.Euler(55f, 0f, 0f);
            if (go.transform.rotation != rotation)
            {
                go.transform.rotation = rotation;
                changed = true;
            }
        }
    }
}
