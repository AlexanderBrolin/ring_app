using Ring.Data;
using Ring.Presentation;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

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
    /// Task 13 (spec §3.13) extends it once more: an `Arena` object at scene root
    /// carries `GreyboxBuilder`, wired to `ArenaConfig` and the three hand-authored
    /// greybox materials (`Floor`/`Wall`/`Obstacle.mat`, created directly on disk,
    /// not by this bootstrap — unlike the emissive materials above, these already
    /// exist by the time `Apply()` runs, so they're loaded here, not created). The
    /// actual geometry is built at runtime by `GreyboxBuilder.Awake()`, not here —
    /// this method only wires the component references, same as everywhere else in
    /// this file.
    /// Task 14 (spec §3.10) adds the HUD skeleton: an `EventSystem` object carrying
    /// `InputSystemUIInputModule` (never `StandaloneInputModule` — the project's
    /// `activeInputHandler` is Input System Package only), and a `HUD` Canvas
    /// (Screen Space Overlay, `CanvasScaler` at 1920x1080) with an HP bar, a dash
    /// cooldown bar and a wave-number `TextMeshProUGUI`, wired to a `HudController`
    /// on the `HUD` object itself. TMP Essential Resources are not available via
    /// `AssetDatabase.ImportPackage` in `-batchmode` (verified empirically: the
    /// import job never completes synchronously, even across a full process
    /// lifetime — see task-14 report), so they're vendored ahead of time at
    /// `Assets/TextMesh Pro/` (same guids as a real interactive import, extracted
    /// from the ugui package's own `.unitypackage`) instead of generated here.
    /// Task 17 (spec §3.6/§3.7, П-1/П-2) adds the last pieces for the milestone-2
    /// playtest: `MobView`/`ProjectileView` prefabs (built here via
    /// `PrefabUtility.SaveAsPrefabAsset`, existence-guarded like the SO assets and
    /// materials above — never overwritten once created), a `Views` object
    /// carrying `ViewRegistry`, an `AudioDirector` object wired to five
    /// hand-placed placeholder clips under `Assets/Audio/Placeholders`, a
    /// `MuzzleFlash` object carrying a `ParticleSystem` + `MuzzleFlashView`, an
    /// `EventRouter` object carrying `SimEventRouter` (the sole `TicksFlushed`
    /// subscriber, П-1 — everything else above is driven through its per-event
    /// fan-out instead), and — editor/dev-build only — a `PracticeTargets` object
    /// that spawns milestone-2 target dummies via `SimulationWorld.DevSpawnMob`.
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
        const string ArenaObjectName = "Arena";
        const string EventSystemObjectName = "EventSystem";
        const string HudObjectName = "HUD";
        const string HpBarObjectName = "HpBar";
        const string DashBarObjectName = "DashBar";
        const string WaveTextObjectName = "WaveText";
        const string BackgroundObjectName = "Background";
        const string FillObjectName = "Fill";

        // Task 17.
        const string PrefabsDir = "Assets/Prefabs";
        const string AudioDir = "Assets/Audio/Placeholders";
        const string ProjectilePrefabPath = PrefabsDir + "/ProjectileView.prefab";
        const string MobPrefabPath = PrefabsDir + "/MobView.prefab";
        const string ViewsObjectName = "Views";
        const string AudioDirectorObjectName = "AudioDirector";
        const string MuzzleFlashObjectName = "MuzzleFlash";
        const string EventRouterObjectName = "EventRouter";
        const string PracticeTargetsObjectName = "PracticeTargets";
        const float ProjectileDiameter = 0.24f;

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

            // Task 13 (spec §3.13): greybox arena. Floor/Wall/Obstacle materials are
            // hand-authored on disk (unlike the emissive placeholders above), so
            // they're loaded, not created — a missing file is a setup error, same
            // treatment as the InputActionAsset/MainCamera checks above.
            Material floorMat = LoadMaterial("Floor");
            Material wallMat = LoadMaterial("Wall");
            Material obstacleMat = LoadMaterial("Obstacle");

            GameObject arenaGo = FindRootObject(scene, ArenaObjectName);
            if (arenaGo == null)
            {
                arenaGo = new GameObject(ArenaObjectName);
                sceneDirty = true;
            }
            GreyboxBuilder greyboxBuilder = arenaGo.GetComponent<GreyboxBuilder>();
            if (greyboxBuilder == null)
            {
                greyboxBuilder = arenaGo.AddComponent<GreyboxBuilder>();
                sceneDirty = true;
            }
            var greyboxSo = new SerializedObject(greyboxBuilder);
            bool greyboxRefsChanged = false;
            greyboxRefsChanged |= SetRef(greyboxSo, "_arena", arena);
            greyboxRefsChanged |= SetRef(greyboxSo, "_floor", floorMat);
            greyboxRefsChanged |= SetRef(greyboxSo, "_wall", wallMat);
            greyboxRefsChanged |= SetRef(greyboxSo, "_obstacle", obstacleMat);
            if (greyboxRefsChanged)
            {
                greyboxSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 14 (spec §3.10): HUD skeleton. EventSystem first — its
            // InputSystemUIInputModule needs no wiring beyond AddComponent: it
            // self-assigns the package's default UI action map in OnEnable when
            // no actionsAsset is set (InputSystemUIInputModule.AssignDefaultActions,
            // called from HasNoActions()), same as the "GameObject > UI > Event
            // System" menu item produces.
            GameObject eventSystemGo = FindRootObject(scene, EventSystemObjectName);
            if (eventSystemGo == null)
            {
                eventSystemGo = new GameObject(EventSystemObjectName);
                sceneDirty = true;
            }
            if (eventSystemGo.GetComponent<EventSystem>() == null)
            {
                eventSystemGo.AddComponent<EventSystem>();
                sceneDirty = true;
            }
            if (eventSystemGo.GetComponent<InputSystemUIInputModule>() == null)
            {
                eventSystemGo.AddComponent<InputSystemUIInputModule>();
                sceneDirty = true;
            }

            GameObject hudGo = FindRootObject(scene, HudObjectName);
            if (hudGo == null)
            {
                hudGo = new GameObject(HudObjectName, typeof(RectTransform));
                sceneDirty = true;
            }
            Canvas hudCanvas = hudGo.GetComponent<Canvas>();
            if (hudCanvas == null)
            {
                hudCanvas = hudGo.AddComponent<Canvas>();
                hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                sceneDirty = true;
            }
            CanvasScaler hudScaler = hudGo.GetComponent<CanvasScaler>();
            if (hudScaler == null)
            {
                hudScaler = hudGo.AddComponent<CanvasScaler>();
                hudScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                hudScaler.referenceResolution = new Vector2(1920f, 1080f);
                sceneDirty = true;
            }
            if (hudGo.GetComponent<GraphicRaycaster>() == null)
            {
                hudGo.AddComponent<GraphicRaycaster>();
                sceneDirty = true;
            }

            // HP top-left, dash bar directly beneath it, both left-anchored so the
            // fill grows rightward from a fixed origin (spec resolution П-2:
            // fillOrigin Left, 1 = ready/full).
            Image hpFill = GetOrCreateBar(hudGo.transform, HpBarObjectName,
                anchoredPos: new Vector2(24f, -24f), size: new Vector2(320f, 28f),
                backgroundColor: new Color(0.05f, 0.05f, 0.05f, 0.85f),
                fillColor: new Color(0.85f, 0.2f, 0.2f), ref sceneDirty);
            Image dashFill = GetOrCreateBar(hudGo.transform, DashBarObjectName,
                anchoredPos: new Vector2(24f, -60f), size: new Vector2(320f, 14f),
                backgroundColor: new Color(0.05f, 0.05f, 0.05f, 0.85f),
                fillColor: new Color(0.3f, 0.7f, 0.9f), ref sceneDirty);
            TMP_Text waveText = GetOrCreateWaveText(hudGo.transform, ref sceneDirty);

            HudController hud = hudGo.GetComponent<HudController>();
            if (hud == null)
            {
                hud = hudGo.AddComponent<HudController>();
                sceneDirty = true;
            }
            var hudSo = new SerializedObject(hud);
            bool hudRefsChanged = false;
            hudRefsChanged |= SetRef(hudSo, "_runner", runner);
            hudRefsChanged |= SetRef(hudSo, "_hpFill", hpFill);
            hudRefsChanged |= SetRef(hudSo, "_dashFill", dashFill);
            hudRefsChanged |= SetRef(hudSo, "_waveText", waveText);
            if (hudRefsChanged)
            {
                hudSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 17 (spec §3.6/§3.7, П-1/П-2): pooled mob/projectile views matched
            // by Id, placeholder SFX + muzzle flash fanned out from a single
            // TicksFlushed subscriber, and milestone-2 practice targets to shoot at.
            Material mobMat = GetOrCreateMaterial(
                "MobEmissive",
                baseColor: new Color(0.06f, 0.06f, 0.06f),
                emissionColor: new Color(0.15f, 0.15f, 0.15f));
            Material projectileMat = GetOrCreateMaterial(
                "ProjectileEmissive",
                baseColor: new Color(0.02f, 0.03f, 0.04f),
                emissionColor: new Color(2.5f, 3f, 3.5f));
            Material tracerMat = GetOrCreateUnlitMaterial("TracerTrail", new Color(2.5f, 3f, 3.5f));
            Material muzzleMat = GetOrCreateUnlitMaterial("MuzzleFlash", new Color(4f, 2.2f, 0.6f));

            MobView mobPrefab = GetOrCreateMobPrefab(mobMat);
            ProjectileView projectilePrefab =
                GetOrCreateProjectilePrefab(projectileMat, tracerMat, gameFeel.TracerFadeSeconds);

            GameObject viewsGo = FindRootObject(scene, ViewsObjectName);
            if (viewsGo == null)
            {
                viewsGo = new GameObject(ViewsObjectName);
                sceneDirty = true;
            }
            ViewRegistry viewRegistry = viewsGo.GetComponent<ViewRegistry>();
            if (viewRegistry == null)
            {
                viewRegistry = viewsGo.AddComponent<ViewRegistry>();
                sceneDirty = true;
            }
            var viewsSo = new SerializedObject(viewRegistry);
            bool viewsRefsChanged = false;
            viewsRefsChanged |= SetRef(viewsSo, "_runner", runner);
            viewsRefsChanged |= SetRef(viewsSo, "_gameFeel", gameFeel);
            viewsRefsChanged |= SetRef(viewsSo, "_arena", arena);
            viewsRefsChanged |= SetRef(viewsSo, "_mobPrefab", mobPrefab);
            viewsRefsChanged |= SetRef(viewsSo, "_projectilePrefab", projectilePrefab);
            if (viewsRefsChanged)
            {
                viewsSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            AudioClip shotClip = LoadAudioClip("shot.wav");
            AudioClip hitClip = LoadAudioClip("hit.wav");
            AudioClip mobDeathClip = LoadAudioClip("mob_death.wav");
            AudioClip dashClip = LoadAudioClip("dash.wav");
            AudioClip playerHitClip = LoadAudioClip("player_hit.wav");

            GameObject audioGo = FindRootObject(scene, AudioDirectorObjectName);
            if (audioGo == null)
            {
                audioGo = new GameObject(AudioDirectorObjectName);
                sceneDirty = true;
            }
            AudioDirector audioDirector = audioGo.GetComponent<AudioDirector>();
            if (audioDirector == null)
            {
                audioDirector = audioGo.AddComponent<AudioDirector>();
                sceneDirty = true;
            }
            var audioSo = new SerializedObject(audioDirector);
            bool audioRefsChanged = false;
            audioRefsChanged |= SetRef(audioSo, "_gameFeel", gameFeel);
            audioRefsChanged |= SetRef(audioSo, "_shotClip", shotClip);
            audioRefsChanged |= SetRef(audioSo, "_hitClip", hitClip);
            audioRefsChanged |= SetRef(audioSo, "_mobDeathClip", mobDeathClip);
            audioRefsChanged |= SetRef(audioSo, "_dashClip", dashClip);
            audioRefsChanged |= SetRef(audioSo, "_playerHitClip", playerHitClip);
            if (audioRefsChanged)
            {
                audioSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            GameObject muzzleGo = FindRootObject(scene, MuzzleFlashObjectName);
            if (muzzleGo == null)
            {
                muzzleGo = new GameObject(MuzzleFlashObjectName);
                ParticleSystem particles = muzzleGo.AddComponent<ParticleSystem>();
                ConfigureMuzzleParticles(particles);
                muzzleGo.AddComponent<MuzzleFlashView>();
                sceneDirty = true;
            }
            // Checked unconditionally (not just on first creation), same as the
            // Player/Crosshair renderer checks above: `AddComponent<ParticleSystem>()`
            // does not assign its `ParticleSystemRenderer` a material on its own, so
            // a self-heal here is what fixes an already-committed object that was
            // created before this check existed, not just future re-runs.
            ParticleSystemRenderer muzzleRenderer = muzzleGo.GetComponent<ParticleSystemRenderer>();
            if (muzzleRenderer.sharedMaterial != muzzleMat)
            {
                muzzleRenderer.sharedMaterial = muzzleMat;
                sceneDirty = true;
            }
            // MuzzleFlashView takes no serialized references (fix-round app-2pl
            // round 2 — direction now comes from the event's own Amount field,
            // not a SimulationRunner snapshot), so no SerializedObject wiring here.
            // The already-committed scene still carries the removed `_runner`
            // field's data as an orphaned YAML key, though — `SerializedObject`
            // only ever reflects the CURRENT class layout, so there is no
            // supported API to diff against the stale value; `HasStaleField`
            // below reads the scene text directly to detect it (existence-guarded
            // like everything else here: once the field is gone, this is false
            // forever, so re-running `Apply()` stays a no-op again after this
            // one migration run drops it via the ordinary SaveScene path).
            MuzzleFlashView muzzleFlash = muzzleGo.GetComponent<MuzzleFlashView>();
            if (HasStaleSerializedField("Ring.Presentation.MuzzleFlashView", "_runner"))
            {
                EditorUtility.SetDirty(muzzleFlash);
                sceneDirty = true;
            }

            GameObject routerGo = FindRootObject(scene, EventRouterObjectName);
            if (routerGo == null)
            {
                routerGo = new GameObject(EventRouterObjectName);
                sceneDirty = true;
            }
            SimEventRouter router = routerGo.GetComponent<SimEventRouter>();
            if (router == null)
            {
                router = routerGo.AddComponent<SimEventRouter>();
                sceneDirty = true;
            }
            var routerSo = new SerializedObject(router);
            bool routerRefsChanged = false;
            routerRefsChanged |= SetRef(routerSo, "_runner", runner);
            routerRefsChanged |= SetRef(routerSo, "_audioDirector", audioDirector);
            routerRefsChanged |= SetRef(routerSo, "_muzzleFlash", muzzleFlash);
            routerRefsChanged |= SetRef(routerSo, "_viewRegistry", viewRegistry);
            if (routerRefsChanged)
            {
                routerSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Milestone-2 target dummies (Task 17) — dev/editor only, same
            // compile guard as `PracticeTargets` itself; deleted outright in
            // Phase 7 once the real WaveSystem exists.
            GameObject practiceGo = FindRootObject(scene, PracticeTargetsObjectName);
            if (practiceGo == null)
            {
                practiceGo = new GameObject(PracticeTargetsObjectName);
                sceneDirty = true;
            }
            PracticeTargets practiceTargets = practiceGo.GetComponent<PracticeTargets>();
            if (practiceTargets == null)
            {
                practiceTargets = practiceGo.AddComponent<PracticeTargets>();
                sceneDirty = true;
            }
            var practiceSo = new SerializedObject(practiceTargets);
            if (SetRef(practiceSo, "_runner", runner))
            {
                practiceSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }
#endif

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

        /// Task 17's tracer material: URP Unlit rather than Lit (like
        /// `GetOrCreateMaterial` above) so the trail reads as a flat glowing streak
        /// regardless of scene lighting — a `TrailRenderer`'s strip geometry isn't
        /// meant to be shaded. Existence-guarded the same way.
        static Material GetOrCreateUnlitMaterial(string assetName, Color color)
        {
            string path = $"{MaterialsDir}/{assetName}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            if (!AssetDatabase.IsValidFolder(MaterialsDir))
                AssetDatabase.CreateFolder(ArtDir, "Materials");

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                throw new System.InvalidOperationException(
                    "StageOneSceneBootstrap: URP Unlit shader not found — is URP installed?");

            var mat = new Material(shader) { name = assetName };
            mat.SetColor("_BaseColor", color);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// Task 17: the shared `MobView` prefab — a bare capsule, no per-type
        /// scale/color baked in (`MobView.Bind` sets the accent color at runtime
        /// via `MaterialPropertyBlock`, spec/П-2). Existence-guarded like the SO
        /// assets/materials above: once the `.prefab` exists on disk, this is
        /// never re-authored, so an owner's in-Editor tweak (e.g. hand-adjusted
        /// scale) survives a re-run.
        static MobView GetOrCreateMobPrefab(Material mobMat)
        {
            var existing = AssetDatabase.LoadAssetAtPath<MobView>(MobPrefabPath);
            if (existing != null) return existing;

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "MobView";
            RemoveCollider(go);
            go.GetComponent<MeshRenderer>().sharedMaterial = mobMat;
            go.AddComponent<MobView>();

            GameObject asset = PrefabUtility.SaveAsPrefabAsset(go, MobPrefabPath);
            Object.DestroyImmediate(go);
            return asset.GetComponent<MobView>();
        }

        /// Task 17: the shared `ProjectileView` prefab — a small emissive sphere
        /// (~0.24 diameter) plus a `TrailRenderer` tracer. `TrailRenderer.time` is
        /// only seeded here from the `GameFeelConfig` value at bootstrap time;
        /// `ProjectileView.Bind` re-applies it live every spawn so PlayMode
        /// hot-tweaking `TracerFadeSeconds` (spec §3.9) still takes effect.
        /// Existence-guarded the same way as `GetOrCreateMobPrefab`.
        static ProjectileView GetOrCreateProjectilePrefab(Material sphereMat, Material trailMat,
            float tracerFadeSeconds)
        {
            var existing = AssetDatabase.LoadAssetAtPath<ProjectileView>(ProjectilePrefabPath);
            if (existing != null) return existing;

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "ProjectileView";
            RemoveCollider(go);
            go.transform.localScale = Vector3.one * ProjectileDiameter;
            go.GetComponent<MeshRenderer>().sharedMaterial = sphereMat;

            TrailRenderer trail = go.AddComponent<TrailRenderer>();
            trail.time = tracerFadeSeconds;
            trail.startWidth = 0.06f;
            trail.endWidth = 0f;
            trail.minVertexDistance = 0.05f;
            trail.sharedMaterial = trailMat;

            go.AddComponent<ProjectileView>();

            GameObject asset = PrefabUtility.SaveAsPrefabAsset(go, ProjectilePrefabPath);
            Object.DestroyImmediate(go);
            return asset.GetComponent<ProjectileView>();
        }

        /// Task 17: one-time module setup for the `MuzzleFlash` object's
        /// `ParticleSystem` — a short warm-colored burst, triggered manually via
        /// `Emit` from `MuzzleFlashView.HandleEvent` (no continuous emission, no
        /// auto-play). Only ever called once, at creation (existence-guarded by
        /// the caller), so an owner's in-Editor tweak of these modules survives a
        /// re-run. The renderer's material is deliberately NOT set here — unlike
        /// these module settings, it is self-healing (checked unconditionally by
        /// the caller every run, see the `muzzleRenderer.sharedMaterial` check
        /// above) because `AddComponent<ParticleSystem>()` leaves it empty and
        /// that needs to fix an already-committed object too, not just a fresh one.
        /// Fix-round (app-2pl): the owner's milestone-2 playtest read the burst as
        /// unreadable chaos — a narrow forward cone (`ShapeModule`, ~18°) replaces
        /// the previous unconfigured (effectively omnidirectional-reading) shape,
        /// paired with a smaller size/shorter lifetime and `MuzzleFlashView` now
        /// orienting `transform` to the shot's actual direction before `Emit`
        /// (this module setup only shapes the cone in the emitter's own local
        /// space — it says nothing about which way that local space points).
        static void ConfigureMuzzleParticles(ParticleSystem particles)
        {
            ParticleSystem.MainModule main = particles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = 0.12f;
            main.startSpeed = 3f;
            main.startSize = 0.08f;
            main.startColor = new Color(1f, 0.6f, 0.15f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.rateOverTime = 0f;

            // Narrow forward cone (local +Z, i.e. `transform.forward`) instead of
            // the wide/undefined default shape — this is what makes the burst
            // read as "a directed spark", not a scatter, once MuzzleFlashView
            // orients the transform to the shot direction before emitting.
            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f;
            shape.radius = 0.05f;
        }

        /// Loads a hand-placed placeholder `.wav` from `Assets/Audio/Placeholders`
        /// (Task 17) — a missing file is a hard setup error, same treatment as
        /// `LoadMaterial` below for the greybox materials.
        static AudioClip LoadAudioClip(string fileName)
        {
            string path = $"{AudioDir}/{fileName}";
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null)
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: no audio clip at '{path}'.");
            return clip;
        }

        /// A HUD bar (Task 14): a container `RectTransform` (top-left anchored) with
        /// a dark `Background` Image behind a `Fill` Image (`Image.Type.Filled`,
        /// `FillMethod.Horizontal`, origin `Left`) on top. Colors/anchors/sizing are
        /// set only at creation — existence-guarded like everything else in this
        /// file, so an owner's in-Editor tweak survives a re-run. Returns the `Fill`
        /// Image, which is what `HudController` drives every frame.
        static Image GetOrCreateBar(Transform parent, string name, Vector2 anchoredPos, Vector2 size,
            Color backgroundColor, Color fillColor, ref bool sceneDirty)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing.Find(FillObjectName).GetComponent<Image>();

            var barGo = new GameObject(name, typeof(RectTransform));
            barGo.transform.SetParent(parent, false);
            var barRect = (RectTransform)barGo.transform;
            barRect.anchorMin = new Vector2(0f, 1f);
            barRect.anchorMax = new Vector2(0f, 1f);
            barRect.pivot = new Vector2(0f, 1f);
            barRect.anchoredPosition = anchoredPos;
            barRect.sizeDelta = size;

            var bgGo = new GameObject(BackgroundObjectName, typeof(RectTransform), typeof(Image));
            bgGo.transform.SetParent(barGo.transform, false);
            StretchToFillParent((RectTransform)bgGo.transform);
            bgGo.GetComponent<Image>().color = backgroundColor;

            var fillGo = new GameObject(FillObjectName, typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(barGo.transform, false);
            StretchToFillParent((RectTransform)fillGo.transform);
            Image fillImage = fillGo.GetComponent<Image>();
            fillImage.color = fillColor;
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImage.fillAmount = 1f;

            sceneDirty = true;
            return fillImage;
        }

        /// The wave-number label (Task 14), top-right anchored. TMP Essential
        /// Resources are vendored at `Assets/TextMesh Pro/` (see class doc), so a
        /// plain `AddComponent<TextMeshProUGUI>()` resolves `TMP_Settings.
        /// defaultFontAsset` on its own — no explicit font wiring needed here.
        static TMP_Text GetOrCreateWaveText(Transform parent, ref bool sceneDirty)
        {
            Transform existing = parent.Find(WaveTextObjectName);
            if (existing != null) return existing.GetComponent<TMP_Text>();

            var go = new GameObject(WaveTextObjectName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-24f, -24f);
            rect.sizeDelta = new Vector2(240f, 40f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "WAVE 0";
            tmp.fontSize = 28f;
            tmp.alignment = TextAlignmentOptions.TopRight;
            tmp.color = Color.white;

            sceneDirty = true;
            return tmp;
        }

        static void StretchToFillParent(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// Loads a hand-authored `.mat` from `Assets/Art/Materials` — Task 13's
        /// greybox materials are created directly on disk (unlike the emissive
        /// placeholders above), so this never creates one; a missing file means the
        /// asset wasn't checked in and is a hard setup error.
        static Material LoadMaterial(string assetName)
        {
            string path = $"{MaterialsDir}/{assetName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: no material at '{path}'.");
            return mat;
        }

        /// One-off migration helper (fix-round app-2pl round 2): detects a
        /// serialized field's leftover YAML key for a given `MonoBehaviour` type
        /// after the field itself was removed from the C# class. `SerializedObject`
        /// cannot see this — it only ever reflects the type's CURRENT field
        /// layout — so this reads `Main.unity`'s text directly instead, scoped to
        /// a small window right after the type's `m_EditorClassIdentifier` line so
        /// an unrelated component that happens to share the same field name (e.g.
        /// `CameraRig._runner`) never false-positives.
        static bool HasStaleSerializedField(string classIdentifier, string fieldName)
        {
            string text = System.IO.File.ReadAllText(ScenePath);
            int idx = text.IndexOf(classIdentifier, System.StringComparison.Ordinal);
            if (idx < 0) return false;
            int windowLen = System.Math.Min(200, text.Length - idx);
            return text.Substring(idx, windowLen).Contains(fieldName + ":");
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
