using FishNet.Component.Transforming.Beta;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Predicting;
using FishNet.Managing.Timing;
using FishNet.Managing.Transporting;
using FishNet.Object;
using FishNet.Transporting.Tugboat;
using Ring.Data;
using Ring.Networking;
using Ring.Presentation;
using Ring.Presentation.Net;
using Ring.Server;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ring.Editor
{
    /// Builds the Stage 2 server scene (`Assets/Scenes/Server.unity`), the
    /// first networked prefab and the project's build-scene list (Stage 2 Task
    /// 41, spec §3.11). Idempotent like every other bootstrap in this folder:
    /// every step is existence-guarded and each artifact is written only when
    /// it actually changed, so a second `Apply()` leaves an empty `git diff`.
    /// Scenes and prefabs in this project are never hand-authored — this file
    /// is the only definition of `Server.unity` and of `NetworkPlayer.prefab`.
    ///
    /// TWO ENTRY POINTS AS OF TASK 44e. `Apply` is the server scene above;
    /// `ApplyClientNetworking` puts the SAME network stack — one shared
    /// helper, not a copy — plus the client's own bootstrap object onto
    /// `Assets/Scenes/Main.unity`, whose every other object belongs to
    /// `StageOneSceneBootstrap`. Ownership of that scene is therefore split
    /// by OBJECT rather than by file, and the split holds only because
    /// neither bootstrap removes a root it did not create; see
    /// `ApplyClientNetworking`'s own doc.
    ///
    /// WHAT THE SCENE MAY CONTAIN, AND WHY SO LITTLE. Spec §3.11 asks for a
    /// server scene "with no camera, HUD, views or particles": the headless
    /// process renders nothing, and anything visual in here would be loaded,
    /// initialized and paid for on every match container. That is also why the
    /// scene is created from `NewSceneSetup.EmptyScene` rather than the default
    /// setup — the default one ships a `Main Camera` and a `Directional Light`,
    /// and deleting them afterwards would be a step that has to keep working
    /// rather than a shape that cannot go wrong. A camera, a light or a HUD
    /// object appearing here later is a review finding, not an oversight.
    ///
    /// THE TICK RATE COMES FROM `NetConfig`, NOT A SEPARATE CONSTANT (Ф8
    /// gate W-1, correcting this paragraph's own earlier claim).
    /// `TimeManager`'s own default happens to be 30, but the number is
    /// spec-pinned (§3.7), and this class writes `net.TickRate` — the very
    /// same `NetConfig` asset it already loads below — into the scene's
    /// `TimeManager._tickRate`, rather than carrying a second literal that
    /// could drift from it. That closes only ONE of the two remaining
    /// agreements, though, and the paragraph this replaces overstated what
    /// it bought: `NetInvariants` connects the scene and `NetConfig` to each
    /// other with its own invariant #8 (Ф8 gate), and separately connects
    /// `NetConfig.TickRate` to `SimulationWorld.TickDt` — the WORLD's own
    /// fixed step — with invariant #6. `SimulationWorld.TickDt` itself stays
    /// a plain `const float` of the simulation assembly: it cannot read a
    /// `ScriptableObject` at all (Critical Rule 1 — no `UnityEngine` in
    /// `Simulation`), so there is no way to fold it into `NetConfig` and make
    /// this "one fact with one owner" in the strong sense. What exists
    /// instead is two facts — `TickDt`, and `NetConfig.TickRate == this
    /// scene's TimeManager.TickRate` — kept in step by two independently
    /// tested invariants rather than by a single shared owner.
    ///
    /// THE BUILD-SCENE LIST IS WRITTEN HERE AND NOWHERE ELSE. `BuildCommands`
    /// stopped reading `EditorBuildSettings` in Task 41a — it takes a per-target
    /// scene array from its caller — so this list serves the Editor (File >
    /// Build, "Build And Run", scene build indices) rather than our own build
    /// entry points. `AssetPreview.unity` is deliberately left OUT of it: it is
    /// an asset-inspection scene, and listing it would ship it inside the
    /// client builds.
    public static class StageTwoSceneBootstrap
    {
        const string ScenePath = "Assets/Scenes/Server.unity";
        const string MainScenePath = "Assets/Scenes/Main.unity";
        const string ScenesDir = "Assets/Scenes";
        const string DataDir = "Assets/Data";
        const string PrefabsDir = "Assets/Prefabs";
        const string PlayerPrefabPath = PrefabsDir + "/NetworkPlayer.prefab";

        /// FishNet's spawnable-prefab registry. The path is the package's own
        /// default (`ConfigurationData.DefaultPrefabObjectsPath` =
        /// `Assets/DefaultPrefabObjects.asset`) and this project carries no
        /// `FishNet.Config.XML` to move it.
        const string SpawnablePrefabsPath = "Assets/DefaultPrefabObjects.asset";

        const string NetworkManagerObjectName = "NetworkManager";
        const string BootstrapObjectName = "ServerBootstrap";
        const string ClientBootstrapObjectName = "ClientNetworkBootstrap";
        const string PlayerPrefabName = "NetworkPlayer";
        const string GraphicalChildName = "Visual";

        [MenuItem("Ring/Bootstrap/Stage 2 Server Scene")]
        public static void Apply()
        {
            // Open/create the target scene FIRST (the same reason
            // StageOneSceneBootstrap states at its own first line):
            // OpenScene/NewScene in Single mode unloads the previously active
            // scene's context and invalidates any asset reference fetched
            // beforehand — they come back as Unity "fake-null" objects, which
            // is silently wrong rather than a NullReferenceException.
            Scene scene = OpenOrCreateScene(out bool sceneDirty);

            HeroConfig hero = Load<HeroConfig>("HeroConfig");
            WeaponConfig weapon = Load<WeaponConfig>("WeaponConfig");
            MobConfig chaser = Load<MobConfig>("MobChaserConfig");
            MobConfig gunner = Load<MobConfig>("MobGunnerConfig");
            WaveConfig wave = Load<WaveConfig>("WaveConfig");
            ArenaConfig arena = Load<ArenaConfig>("ArenaConfig");
            VisibilityConfig visibility = Load<VisibilityConfig>("VisibilityConfig");
            NetConfig net = Load<NetConfig>("NetConfig");

            NetworkObject playerPrefab = GetOrCreatePlayerPrefab();

            // Loaded, never created: the registry is FishNet's own asset and
            // FishNet's own generator owns its contents (see
            // GetOrCreatePlayerPrefab for how the new prefab gets into it).
            var spawnablePrefabs =
                AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>(SpawnablePrefabsPath);
            if (spawnablePrefabs == null)
                throw new System.InvalidOperationException(
                    $"StageTwoSceneBootstrap: no DefaultPrefabObjects at '{SpawnablePrefabsPath}'.");

            EnsureNetworkStack(scene, net, spawnablePrefabs, ref sceneDirty);

            GameObject bootstrapGo = GetOrCreateRoot(scene, BootstrapObjectName, ref sceneDirty);
            ServerBootstrap bootstrap = EnsureComponent<ServerBootstrap>(bootstrapGo, ref sceneDirty);
            var bootstrapSo = new SerializedObject(bootstrap);
            bool bootstrapChanged = false;
            bootstrapChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_hero", hero);
            bootstrapChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_weapon", weapon);
            bootstrapChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_chaser", chaser);
            bootstrapChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_gunner", gunner);
            bootstrapChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_wave", wave);
            bootstrapChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_arena", arena);
            bootstrapChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_visibility", visibility);
            bootstrapChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_net", net);
            bootstrapChanged |= EditorBootstrapUtils.SetRef(bootstrapSo, "_playerPrefab",
                playerPrefab.gameObject);
            if (bootstrapChanged)
            {
                bootstrapSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            if (sceneDirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            bool buildScenesChanged = EnsureBuildScenes();
            if (buildScenesChanged) AssetDatabase.SaveAssets();

            Debug.Log($"StageTwoSceneBootstrap: scene {(sceneDirty ? "updated" : "already up to date")} " +
                $"at {ScenePath}, build scenes {(buildScenesChanged ? "updated" : "already up to date")}.");
        }

        /// Adds the client half of Stage 2 to `Assets/Scenes/Main.unity`
        /// (Task 44e): the same network stack `Apply` puts on the server
        /// scene, plus the `ClientNetworkBootstrap` object that installs the
        /// networked backend and dials the server when the launch asks for
        /// it. Idempotent by the same existence guards, so a second run
        /// leaves an empty `git diff`.
        ///
        /// A SECOND ENTRY POINT RATHER THAN A BRANCH OF `Apply`. The two
        /// scenes have nothing in common past the stack itself — no player
        /// prefab, no `ServerBootstrap`, no build-scene list on this side —
        /// and the batch runbook drives bootstraps by method name, so a
        /// caller that wants the client scene should not have to rebuild the
        /// server one to get it.
        ///
        /// IT LIVES HERE, NOT IN `StageOneSceneBootstrap`, THOUGH THAT FILE
        /// OWNS THE SCENE. The code that places a `NetworkManager` is already
        /// written in this file and is now shared by both methods rather than
        /// copied; the objects it writes are Stage 2's, not Stage 1's; and
        /// this file already knew the path. The two bootstraps therefore
        /// write into ONE scene, which is safe only because their objects are
        /// disjoint: each one creates what it does not find and neither
        /// removes a root it does not own, so re-running either leaves the
        /// other's work standing. A future step that starts deleting unknown
        /// roots from `Main.unity` breaks that, and this paragraph is where
        /// it should be found out.
        ///
        /// WHY THE STACK IS ADDED AT ALL WHEN SOLO IS THE DEFAULT. A client
        /// cannot dial a server without a transport, and the transport cannot
        /// be added at run time in a form the Inspector can be trusted to
        /// show. `ClientNetworkBootstrap` is inert without its command-line
        /// switch — see its own doc — and the manager it leaves idle changes
        /// nothing a solo session reads: FishNet's headless auto-start is
        /// compiled out of a non-server build, and `TimeManager`'s physics
        /// mode stays the package default, which is Unity's own.
        [MenuItem("Ring/Bootstrap/Stage 2 Client Networking")]
        public static void ApplyClientNetworking()
        {
            // The scene first, for the fake-null reason `Apply` states above.
            Scene scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);

            NetConfig net = Load<NetConfig>("NetConfig");
            var spawnablePrefabs =
                AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>(SpawnablePrefabsPath);
            if (spawnablePrefabs == null)
                throw new System.InvalidOperationException(
                    $"StageTwoSceneBootstrap: no DefaultPrefabObjects at '{SpawnablePrefabsPath}'.");

            // The client resolves a spawn by prefab id against this very
            // registry, so it is as load-bearing here as on the server: an
            // unassigned field would mean the player objects the server
            // spawns never appear on this side.
            bool sceneDirty = false;
            EnsureNetworkStack(scene, net, spawnablePrefabs, ref sceneDirty);

            SimulationRunner runner =
                EditorBootstrapUtils.FindComponentInScene<SimulationRunner>(scene);
            if (runner == null)
                throw new System.InvalidOperationException(
                    "StageTwoSceneBootstrap: no SimulationRunner in " + MainScenePath +
                    " — run Ring/Bootstrap/Stage 1 Scene first.");

            // Its own root, the way `ServerBootstrap` has one: the manager
            // object is marked DontDestroyOnLoad by FishNet and outlives the
            // scene, while this component's life is meant to BE the scene's —
            // that is what makes its OnDestroy the right place to drop the
            // subscriptions the manager would otherwise keep.
            GameObject bootstrapGo = GetOrCreateRoot(scene, ClientBootstrapObjectName, ref sceneDirty);
            ClientNetworkBootstrap clientBootstrap =
                EnsureComponent<ClientNetworkBootstrap>(bootstrapGo, ref sceneDirty);

            var clientSo = new SerializedObject(clientBootstrap);
            bool clientChanged = false;
            clientChanged |= EditorBootstrapUtils.SetRef(clientSo, "_runner", runner);
            clientChanged |= EditorBootstrapUtils.SetRef(clientSo, "_net", net);
            if (clientChanged)
            {
                clientSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            if (sceneDirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log($"StageTwoSceneBootstrap: client networking " +
                $"{(sceneDirty ? "updated" : "already up to date")} at {MainScenePath}.");
        }

        /// The network stack itself — `NetworkManager` and the four managers
        /// beside it — on a root object of `scene`. Shared by both entry
        /// points rather than written twice: the client and the server need
        /// the same components, the same prefab registry and the same tick
        /// rate, and two copies of that list would be two places for the next
        /// component to be added to and one place for it to be forgotten.
        ///
        /// The components are added explicitly even though `NetworkManager`
        /// would create the missing ones itself at run time (its own
        /// `GetOrCreateComponent` pass): a manager that exists only after
        /// `Awake` cannot be inspected, cannot be pre-configured in the scene
        /// and — for `TimeManager` — cannot carry a scene-authored tick rate.
        ///
        /// THE TRANSPORT'S ADDRESS AND PORT ARE LEFT AT THE PACKAGE DEFAULTS
        /// ON BOTH SCENES. The server's real port arrives per match from
        /// `MatchConfig` and is applied by the `StartConnection` overload that
        /// takes one (Task 41c); the client's arrives from the command line
        /// and is applied by the client overload of the same shape. A value
        /// authored here would be a second source of that number, and the
        /// effective one would silently be whichever of the two ran last.
        static void EnsureNetworkStack(Scene scene, NetConfig net,
            DefaultPrefabObjects spawnablePrefabs, ref bool sceneDirty)
        {
            GameObject managerGo = GetOrCreateRoot(scene, NetworkManagerObjectName, ref sceneDirty);
            NetworkManager manager = EnsureComponent<NetworkManager>(managerGo, ref sceneDirty);
            EnsureComponent<TransportManager>(managerGo, ref sceneDirty);
            EnsureComponent<Tugboat>(managerGo, ref sceneDirty);
            TimeManager timeManager = EnsureComponent<TimeManager>(managerGo, ref sceneDirty);
            EnsureComponent<PredictionManager>(managerGo, ref sceneDirty);

            var managerSo = new SerializedObject(manager);
            // Wired explicitly rather than left to the Editor-only auto-fill in
            // NetworkManager.ValidateSpawnablePrefabs: that path exists only
            // under UNITY_EDITOR, so a built player with an unassigned field
            // would log "SpawnablePrefabs is null" and fail to spawn.
            if (EditorBootstrapUtils.SetRef(managerSo, "_spawnablePrefabs", spawnablePrefabs))
            {
                managerSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            var timeSo = new SerializedObject(timeManager);
            if (SetInt(timeSo, "_tickRate", net.TickRate))
            {
                timeSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }
        }

        static Scene OpenOrCreateScene(out bool created)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                created = false;
                return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            EditorBootstrapUtils.EnsureFolder(ScenesDir);
            // EmptyScene, not DefaultGameObjects: see the class doc — the
            // default setup would seed a camera and a light, and spec §3.11
            // wants neither in a headless scene.
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
                    $"StageTwoSceneBootstrap: no {typeof(T).Name} at '{path}' — " +
                    "run Ring/Bootstrap/Stage 1 Scene first.");
            return asset;
        }

        /// The player's networked object: `NetworkObject` +
        /// `PlayerNetworkController` on the root, a `Visual` child carrying the
        /// tick smoother. Existence-guarded like every other prefab factory in
        /// this folder — built once, never overwritten, so a later hand-tweak
        /// survives a re-Apply.
        ///
        /// NO MODEL, NO MESH, NO RENDERER. The visual body is Presentation's
        /// zone and Task 44's wiring; this prefab is the network seat and
        /// nothing else. `Visual` exists as a bare transform because the
        /// smoother needs a graphical transform that is NOT the one being
        /// moved every tick — see below.
        ///
        /// WHY THE SMOOTHER SITS ON THE CHILD AND NOT ON THE ROOT. The package
        /// decides this, not taste. `NetworkTickSmoother.OnStartClient` passes
        /// its OWN transform as the graphical one
        /// (`Runtime/Generated/Component/TickSmoothing/NetworkTickSmoother.cs`:
        /// `SetNetworkedRuntimeValues(..., graphicalTransform: transform, ...)`),
        /// and `UniversalTickSmoother.TransformsAreValid` refuses to initialize
        /// when `targetTransform == graphicalTransform` ("Target transform
        /// cannot be the same as graphical transform on {graphicalTransform}").
        /// A smoother on the root would therefore log an error and smooth
        /// nothing. `InitializationSettings.TargetTransform` — the object that
        /// actually moves every tick — is the prefab root, wired below; leaving
        /// it null is the other refusal branch ("Target transform on {…} cannot
        /// be null").
        ///
        /// THE OBSOLETE SMOOTHING PATH IS LEFT UNWIRED ON PURPOSE (Р106).
        /// `NetworkObject` still carries `_graphicalObject` and its own
        /// `_ownerInterpolation`/`_enableTeleport`/`_teleportThreshold` block,
        /// but `NetworkObject.PredictionSmoother` is marked
        /// `[Obsolete("This field will be removed in v5. Instead reference
        /// NetworkTickSmoother on each graphical object used.")]`, and
        /// `NetworkObject.InitializeSmoothers` skips that whole path while
        /// `_graphicalObject` is null. Assigning it would run TWO smoothers
        /// over one transform.
        ///
        /// `MovementSettings.TeleportThreshold` IS NOT SET, IN EITHER SET
        /// (Р160). It is not the reconciliation-snap knob it looks like:
        /// `UniversalTickSmoother` caches it SQUARED and compares it against a
        /// plain metre distance, and what it gates is the transform delta
        /// BETWEEN TWO TICKS — a quantity legitimate movement and a correction
        /// enter indistinguishably. The snap is driven from
        /// `NetConfig.ReconcileSnapMeters` through
        /// `PlayerPredictionCore.ShouldSnapCorrection` instead (Task 44). The
        /// struct's own initializer already leaves `EnableTeleport` false and
        /// the threshold at 0, so this is a decision to write nothing rather
        /// than a value to write.
        ///
        /// `_favorPredictionNetworkTransform` KEEPS THE PACKAGE DEFAULT
        /// (`true`), and the reason is read off the code rather than off its
        /// tooltip. `UniversalTickSmoother.Initialize` only reaches for
        /// `NetworkObject.PredictionNetworkTransform` when the flag is set, and
        /// that property returns `NetworkObject`'s serialized `_networkTransform`
        /// — which this prefab does not have and does not assign. Both branches
        /// therefore end at `_predictionNetworkTransform = null`, and the flag
        /// is behaviorally inert here today: `CanSmooth`'s early-out on it can
        /// never fire. `true` is kept because it is the value that stays
        /// correct if a `NetworkTransform` is ever added — the smoother would
        /// then step aside instead of smoothing the same transform twice.
        static NetworkObject GetOrCreatePlayerPrefab()
            => EditorBootstrapUtils.BuildPrefab<NetworkObject>(PlayerPrefabPath, () =>
            {
                var root = new GameObject(PlayerPrefabName);
                NetworkObject nob = root.AddComponent<NetworkObject>();
                root.AddComponent<PlayerNetworkController>();

                var graphical = new GameObject(GraphicalChildName);
                graphical.transform.SetParent(root.transform, false);
                NetworkTickSmoother smoother = graphical.AddComponent<NetworkTickSmoother>();

                // Unconditional writes: this callback runs only when the
                // prefab is being created, so there is no previous value to
                // preserve and no re-Apply to keep no-op.
                var nobSo = new SerializedObject(nob);
                nobSo.FindProperty("_enablePrediction").boolValue = true;
                nobSo.ApplyModifiedPropertiesWithoutUndo();

                var smootherSo = new SerializedObject(smoother);
                EditorBootstrapUtils.SetRef(smootherSo,
                    "_initializationSettings.TargetTransform", root.transform);
                smootherSo.ApplyModifiedPropertiesWithoutUndo();

                return root;
            });

        /// Registers `Main.unity` and `Server.unity` in the Editor's build-scene
        /// list, in that fixed order, both enabled — and writes only when the
        /// list already differs, so a re-Apply leaves
        /// `ProjectSettings/EditorBuildSettings.asset` byte-identical. The
        /// order is not cosmetic: index 0 is the scene a player build opens,
        /// and `Main.unity` has held it since Stage 1.
        ///
        /// THIS LIST IS OWNED HERE, AND OWNERSHIP MEANS REPLACEMENT, NOT MERGE.
        /// A scene added to the list by hand is dropped by the next `Apply`
        /// without being named in the log: the comparison below only runs when
        /// the lengths already match, so any third entry falls through to a
        /// wholesale rewrite. That is the intended enforcement — it is what
        /// keeps `AssetPreview.unity` out of player builds — but it is worth
        /// knowing before wondering where a hand-added scene went.
        static bool EnsureBuildScenes()
        {
            var desired = new[]
            {
                new EditorBuildSettingsScene(MainScenePath, true),
                new EditorBuildSettingsScene(ScenePath, true),
            };

            EditorBuildSettingsScene[] current = EditorBuildSettings.scenes;
            if (current.Length == desired.Length)
            {
                bool same = true;
                for (int i = 0; i < desired.Length; i++)
                {
                    if (current[i].path == desired[i].path && current[i].enabled == desired[i].enabled)
                        continue;
                    same = false;
                    break;
                }
                if (same) return false;
            }

            EditorBuildSettings.scenes = desired;
            return true;
        }

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

        /// Integer sibling of `EditorBootstrapUtils.SetRef` — write-if-different
        /// so a re-Apply stays a no-op like every other wiring call here. Kept
        /// private rather than promoted to `EditorBootstrapUtils`: it has one
        /// call site, and that file describes itself as holding the guard
        /// primitives the idempotent bootstraps SHARE — a single-caller helper
        /// does not qualify yet.
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
    }
}
