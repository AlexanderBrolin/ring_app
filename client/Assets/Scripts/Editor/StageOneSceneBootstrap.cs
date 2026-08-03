using Ring.Data;
using Ring.Presentation;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
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
    /// Task 21 (spec §3.6, П-11) adds a `DevOverlay` object — the dev-spawn-buttons
    /// stub Task 24 grows into the full overlay — wired to the same `runner`
    /// and `aimProvider` references as everything else above.
    /// Task 24 (spec Interfaces): a `DeathPanel`/`PausePanel` pair of modal
    /// overlays under the existing `HUD` canvas, carried by new `DeathOverlay`/
    /// `PauseMenu` root objects (`DeathOverlayController`/`PauseController`);
    /// `SimEventRouter` gains a wired `_deathOverlay` slot. The milestone-2
    /// `PracticeTargets` object is retired — self-healed out of the scene
    /// outright (its class no longer exists) now that real wave spawning
    /// (Task 22) makes the placeholder dummies redundant.
    /// Task 25 (spec Interfaces, Приложение П-1/П-7) adds a `GameFeelDirector`
    /// object, wired in TWO passes: an early one (right after `SimulationRunner`
    /// itself, `_runner`/`_gameFeel` only) so the component instance already
    /// exists by the time `DeathOverlayController`'s own `_gameFeelDirector`
    /// slot and `SimEventRouter`'s fan-out need to reference it, and a second
    /// pass in the Task 17 views section once `ViewRegistry` and the new
    /// full-screen `Vignette` `Image` (added to the `HUD` canvas, Task 14
    /// section) both exist.
    /// Task 26 (spec Interfaces, this task's resolution П-3) wires `CameraRig`'s
    /// new `_gameFeelDirector` slot (it reads `GameFeelDirector.ShakeOffset`
    /// directly every `LateUpdate` — no event/`SimulationRunner` indirection) in
    /// the existing CameraRig section, and adds a `SpreadCone` child under the
    /// `Crosshair` object: a `LineRenderer` ring (closed loop, world-space,
    /// `CrosshairView.ConeSegments` points), plus a new `SpreadConeEmissive`
    /// unlit material created the same existence-guarded way as every other
    /// placeholder material in this file. `CrosshairView` gains `_cone`/
    /// `_runner` reference slots alongside its existing `_marker`/`_aimProvider`.
    /// Task 27 (spec §3.11, Приложение П) adds persistent cosmetics: (1) an
    /// idempotent `AddDecalRendererFeatureIfMissing` pass over both
    /// `PC_Renderer.asset`/`Mobile_Renderer.asset` — the brief's "через Editor"
    /// step reproduced by hand (no Editor UI in batchmode) via the exact
    /// `SerializedObject`/`AssetDatabase` sequence the URP package's own
    /// `ScriptableRendererDataEditor.AddComponent` uses (read directly from
    /// package source, `Editor/ScriptableRendererDataEditor.cs`), guarded by
    /// the public `ScriptableRendererData.TryGetRendererFeature&lt;T&gt;` the
    /// same Inspector uses to reject a duplicate; (2) six new existence-guarded
    /// prefabs (`Casing`/`Decal`/`Corpse`/`HitSpark`/`BlockSpark`/`DeathBurst`,
    /// same `PrefabUtility.SaveAsPrefabAsset` pattern as `MobView`/
    /// `ProjectileView`) and their materials, including a decal material
    /// cloned from URP's own shipped `Runtime/Materials/Decal.mat` template
    /// (loaded via its `Packages/com.unity.render-pipelines.universal/...`
    /// virtual path) rather than hand-building a `Shader Graphs/Decal`
    /// material from scratch; (3) a `PersistentProps` object carrying
    /// `PersistentPropsDirector`, wired to every prefab above plus `_arena`
    /// (decal/block-spark normal computation) and `_gameFeel`; (4)
    /// `SimEventRouter`'s new `_persistentProps` slot.
    /// Task 27 review fix-round adds a fifth step: `EnsureCasingsLayer`
    /// claims user layer 9 ("Casings") in `ProjectSettings/TagManager.asset`
    /// — casings originally shared `GreyboxBuilder.CosmeticsLayer`/8 with the
    /// arena's own colliders, which made `PersistentPropsDirector.Awake`'s
    /// self-collision guard also disable casing-vs-arena collision (see that
    /// class's doc); `GetOrCreateCasingPrefab` self-heals an
    /// already-committed `Casing.prefab`'s layer unconditionally. The same
    /// round also moves several Presentation-only literals (casing impulse/
    /// torque, corpse glow fade, decal size, spark lifetime/speed/size) from
    /// bootstrap/`PersistentPropsDirector`/`CorpseView` constants into new
    /// `GameFeelConfig` fields (client/CLAUDE.md: "все числа game feel — в
    /// ScriptableObjects") — this file's spark-prefab calls now read
    /// `gameFeel.*` instead of literals for those three.
    /// Task 28 (spec §3.9/§3.11) rewires `MuzzleFlashView`'s `_runner`/
    /// `_gameFeel` slots (ImmediateMuzzleFeedback's per-frame prediction path
    /// needs them; the event-driven burst still doesn't) — the migration
    /// helper `HasStaleSerializedField` this section used to self-heal the
    /// object's leftover YAML key is removed along with it, since the field is
    /// no longer stale. Every other Task 28 change (`RingDataChanged`,
    /// `OnValidate` on the 7 SO classes, `SimulationRunner.LastFrameInput`/
    /// `WouldFireThisFrame`, `AudioDirector`'s predicted-play path) is pure C#
    /// with no new scene objects/references, so no other wiring section here
    /// changes.
    /// Assets phase B plan, Task 8 (spec §3.2): the `Player` root stops being
    /// a bare capsule — it self-heals an already-committed scene's leftover
    /// `MeshRenderer`/`MeshFilter` and instead carries a doll `Visual` child
    /// (`EditorBootstrapUtils.EnsureVisual`, the UAL1 doll FBX + generated
    /// `PlayerAnimator` controller) driven by a new `PlayerVisual` component
    /// (`_runner`/`_aimProvider`/`_gameFeel`/`_animator`/`_visual`, wired to
    /// `SimEventRouter`'s new `_playerVisual` fan-out slot). `PlayerView`
    /// loses its own `_aimProvider` slot in the same change (the field moved
    /// to `PlayerVisual`, T7) — only `_runner` is wired here now.
    /// `PlayerEmissive`'s `GetOrCreateMaterial` call is kept purely so the
    /// greybox material stays available on disk for a hand-tweak/fallback;
    /// nothing in the scene consumes it as of this task. A `Gun` object
    /// (SciFi kit pistol prefab) is instantiated once as a child of the
    /// doll's `RightHand` bone and then write-if-different reconciled every
    /// `Apply()` against `GameFeelConfig.GunLocalPosition`/`GunLocalEuler`,
    /// so an owner's number tweak on the milestone Б1 playtest applies
    /// without tearing the gun down and rebuilding it.
    /// Б1 fix-wave 2 (app-9av, owner request) adds a seventh persistent-cosmetic
    /// prefab: `DashGlow` (`GetOrCreateDashGlowPrefab`) — a flat unlit `Quad` +
    /// `DashGlowView` pair (`PersistentPropsDirector`'s existing wiring block
    /// gains a `_dashGlowPrefab` slot alongside `_casingPrefab`/`_decalPrefab`/
    /// `_corpsePrefab`). Review round: the first pass tried a `DecalProjector`
    /// (`GetOrCreateDecalMaterial` grew an `emissionColor` parameter for it) —
    /// URP's `Decal.shadergraph` turned out to have NO Emission block at all,
    /// so that emission was a provable no-op; `GetOrCreateDecalMaterial` is
    /// back to its original two-parameter signature (`ScorchDecal`'s call site
    /// unchanged), and `DashGlow` instead reuses `GetOrCreateUnlitMaterial` —
    /// same `Universal Render Pipeline/Unlit` HDR-`_BaseColor` family as
    /// `HitSpark`/`BlockSpark`/`DeathBurst` above, which DOES bloom.
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
        const string SpreadConeObjectName = "SpreadCone";
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

        // Task 21.
        const string DevOverlayObjectName = "DevOverlay";

        // Task 24.
        const string DeathPanelObjectName = "DeathPanel";
        const string DeathOverlayObjectName = "DeathOverlay";
        const string PausePanelObjectName = "PausePanel";
        const string PauseControllerObjectName = "PauseMenu";

        // Task 25.
        const string GameFeelDirectorObjectName = "GameFeelDirector";
        const string VignetteObjectName = "Vignette";

        // Task 27.
        const string PcRendererPath = "Assets/Settings/PC_Renderer.asset";
        const string MobileRendererPath = "Assets/Settings/Mobile_Renderer.asset";
        const string DecalTemplateMaterialPath =
            "Packages/com.unity.render-pipelines.universal/Runtime/Materials/Decal.mat";
        const string CasingPrefabPath = PrefabsDir + "/Casing.prefab";
        const string DecalPrefabPath = PrefabsDir + "/Decal.prefab";
        const string CorpsePrefabPath = PrefabsDir + "/Corpse.prefab";
        const string HitSparkPrefabPath = PrefabsDir + "/HitSpark.prefab";
        const string BlockSparkPrefabPath = PrefabsDir + "/BlockSpark.prefab";
        const string DeathBurstPrefabPath = PrefabsDir + "/DeathBurst.prefab";
        const string PersistentPropsObjectName = "PersistentProps";
        const string TagManagerPath = "ProjectSettings/TagManager.asset";
        const string CasingsLayerName = "Casings";

        // Б1 fix-wave 2 (app-9av): dash-start floor mark.
        const string DashGlowPrefabPath = PrefabsDir + "/DashGlow.prefab";

        // Task 8 (assets phase B plan, spec §3.2): the pistol in the doll's hand.
        const string GunObjectName = "Gun";
        // 8a: swapping the gun = this one id.
        const string GunModelPath = ThirdPartyAssetPostprocessor.SciFiRoot + "Models/Gun_Pistol.fbx";

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
            MobConfig gunner = GetOrCreate<MobConfig>("MobGunnerConfig", out bool gunnerCreated);
            WaveConfig wave = GetOrCreate<WaveConfig>("WaveConfig");
            ArenaConfig arena = GetOrCreate<ArenaConfig>("ArenaConfig");
            GameFeelConfig gameFeel = GetOrCreate<GameFeelConfig>("GameFeelConfig");
            CameraConfig camera = GetOrCreate<CameraConfig>("CameraConfig");

            // F-5 fix-round: applied ONLY on first creation, not on every Apply()
            // — this used to be reapplied unconditionally so a stale existing
            // asset would self-heal (fix-round 1: melee fields must read 0, not
            // chaser's contact-combat numbers — gunner never melees, spec §3.9
            // baseline is TestConfigs.Default().Gunner, where they're unset -> 0),
            // but "unconditionally" also meant it silently stomped any owner
            // hand-tweak of MobGunnerConfig.asset on the very next bootstrap run —
            // a "баланс в ScriptableObjects" violation (AGENT.md §6): the SO is
            // supposed to be the tunable source of truth, not something this
            // script keeps overwriting. `gunnerCreated` is only true for a
            // brand-new asset (e.g. a fresh clone with no committed asset at all),
            // where every field is still MobConfig's own chaser-mirrored class
            // default and genuinely needs the gunner numbers seeded once.
            bool gunnerChanged = gunnerCreated && ApplyGunnerDefaults(gunner);
            if (gunnerChanged) EditorUtility.SetDirty(gunner);

            // Task 27 review fix-round (extended by the milestone-4 DoD
            // iteration): an already-committed GameFeelConfig.asset predates
            // whichever feel fields most recently landed — Unity only writes
            // a ScriptableObject's CURRENT field set to disk when something
            // marks it dirty (missing keys silently fall back to the C#
            // field initializer at load time either way, so this is a
            // traceability fix, not a correctness one: the owner should see
            // real numbers to hot-tweak in the Inspector/YAML, not an absent
            // key). Checked via a direct text read (same technique the now-
            // removed HasStaleSerializedField migration helper used — Task 28
            // dropped it once its one caller, MuzzleFlashView's `_runner`
            // field, went from stale-to-detect back to legitimately wired —
            // inverted here: detects a MISSING key instead of a stale one) so
            // this is a one-time sync, not an unconditional touch every run.
            // The marker key is always the MOST RECENTLY added field
            // (currently `DashGlowSize`, Б1 fix-wave 2 (app-9av) — was
            // `GunLocalEuler` before that) so a fresh field addition
            // is what re-triggers the sync, regardless of which older fields
            // an already-committed asset already happens to carry.
            if (!System.IO.File.ReadAllText($"{DataDir}/GameFeelConfig.asset").Contains("DashGlowSize"))
                EditorUtility.SetDirty(gameFeel);

            AssetDatabase.SaveAssets();

            // Task 27 (Приложение П, Decal Renderer Feature): independent of
            // scene state, so it runs before any scene wiring below — see
            // AddDecalRendererFeatureIfMissing's own doc for why this
            // reproduces the URP Editor's "Add Renderer Feature" button by
            // hand instead of using it (batchmode has no Inspector to click).
            bool decalFeatureChanged = false;
            decalFeatureChanged |= AddDecalRendererFeatureIfMissing(PcRendererPath);
            decalFeatureChanged |= AddDecalRendererFeatureIfMissing(MobileRendererPath);

            // Task 27 review fix-round: casings need their OWN layer, distinct
            // from GreyboxBuilder.CosmeticsLayer (see PersistentPropsDirector's
            // class doc for the bug this fixes) — EnsureCasingsLayer claims
            // user layer 9 in TagManager.asset, idempotently.
            bool casingsLayerChanged = EnsureCasingsLayer();

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
            bool aimRefsChanged = EditorBootstrapUtils.SetRef(aimSo, "_camera", mainCamera);
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
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_hero", hero);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_weapon", weapon);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_chaser", chaser);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_gunner", gunner);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_wave", wave);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_arena", arena);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_gameFeel", gameFeel);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_camera", camera);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_actionsAsset", actionsAsset);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_aimProvider", aimProvider);
            if (refsChanged)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 25 (Приложение П-1/П-7): `GameFeelDirector` object, created
            // (first wiring pass) here rather than down in the Task 17 views
            // section — `DeathOverlayController`'s `_gameFeelDirector` slot and
            // `SimEventRouter`'s fan-out both need the component INSTANCE to
            // already exist well before `ViewRegistry`/the HUD's `Vignette`
            // `Image` are built, even though this pass only wires the two refs
            // (`_runner`, `_gameFeel`) already available this early. The second
            // pass (`_viewRegistry`, `_vignette`) runs later, once those exist.
            GameObject gameFeelDirectorGo = EditorBootstrapUtils.FindRootObject(scene, GameFeelDirectorObjectName);
            if (gameFeelDirectorGo == null)
            {
                gameFeelDirectorGo = new GameObject(GameFeelDirectorObjectName);
                sceneDirty = true;
            }
            GameFeelDirector gameFeelDirector = gameFeelDirectorGo.GetComponent<GameFeelDirector>();
            if (gameFeelDirector == null)
            {
                gameFeelDirector = gameFeelDirectorGo.AddComponent<GameFeelDirector>();
                sceneDirty = true;
            }
            var gameFeelDirectorSo = new SerializedObject(gameFeelDirector);
            bool gameFeelDirectorRefsChanged = false;
            gameFeelDirectorRefsChanged |= EditorBootstrapUtils.SetRef(gameFeelDirectorSo, "_runner", runner);
            gameFeelDirectorRefsChanged |= EditorBootstrapUtils.SetRef(gameFeelDirectorSo, "_gameFeel", gameFeel);
            if (gameFeelDirectorRefsChanged)
            {
                gameFeelDirectorSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 12 (spec §3.7/§3.11): placeholder emissive materials, then the
            // Player object, the CameraRig (parent of Main Camera) and the Crosshair
            // marker. Colors are greybox placeholders — Task 13+ owns the real art
            // pass; only the emissive channel matters here (dark-neon readability).
            // greybox fallback kept on disk; no scene consumer since phase B
            GetOrCreateMaterial(
                "PlayerEmissive",
                baseColor: new Color(0.03f, 0.03f, 0.04f),
                emissionColor: new Color(0f, 2.5f, 3f));
            Material crosshairMat = GetOrCreateMaterial(
                "CrosshairEmissive",
                baseColor: new Color(0.04f, 0.02f, 0f),
                emissionColor: new Color(3.5f, 1.2f, 0f));
            // Task 26: the spread-cone ring reuses the crosshair's own warm-neon
            // emissive tint (unlit, like the tracer/muzzle materials — a
            // `LineRenderer` strip isn't meant to be shaded).
            Material spreadConeMat = GetOrCreateUnlitMaterial("SpreadConeEmissive", new Color(3.5f, 1.2f, 0f));

            GameObject playerGo = EditorBootstrapUtils.FindRootObject(scene, PlayerObjectName);
            if (playerGo == null)
            {
                playerGo = new GameObject(PlayerObjectName);
                EditorBootstrapUtils.RemoveCollider(playerGo);
                sceneDirty = true;
            }
            // Self-heal an already-committed E1 capsule: since assets phase B
            // (spec §3.2) the root carries no renderer of its own — the doll
            // lives on the "Visual" child instead (EnsureVisual below).
            MeshRenderer playerMeshRenderer = playerGo.GetComponent<MeshRenderer>();
            if (playerMeshRenderer != null)
            {
                Object.DestroyImmediate(playerMeshRenderer);
                sceneDirty = true;
            }
            MeshFilter playerMeshFilter = playerGo.GetComponent<MeshFilter>();
            if (playerMeshFilter != null)
            {
                Object.DestroyImmediate(playerMeshFilter);
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
            playerRefsChanged |= EditorBootstrapUtils.SetRef(playerSo, "_runner", runner);
            if (playerRefsChanged)
            {
                playerSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Assets phase B (spec §3.2, task 8): the collector doll — a named
            // "Visual" child instantiated from the UAL1 doll FBX, driven by
            // PlayerVisual (facing/animation) instead of the root transform
            // itself (PlayerView doc).
            bool playerVisualChanged = false;
            GameObject playerVisualGo = EditorBootstrapUtils.EnsureVisual(playerGo,
                ThirdPartyAssetPostprocessor.DollPath,
                ThirdPartyAnimatorBootstrap.PlayerControllerPath,
                gameFeel.PlayerVisualScale, ref playerVisualChanged);
            sceneDirty |= playerVisualChanged;

            PlayerVisual playerVisual = playerGo.GetComponent<PlayerVisual>();
            if (playerVisual == null)
            {
                playerVisual = playerGo.AddComponent<PlayerVisual>();
                sceneDirty = true;
            }
            var playerVisualSo = new SerializedObject(playerVisual);
            bool playerVisualRefsChanged = false;
            playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_runner", runner);
            playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_aimProvider", aimProvider);
            playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_gameFeel", gameFeel);
            playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_animator",
                playerVisualGo.GetComponent<Animator>());
            playerVisualRefsChanged |= EditorBootstrapUtils.SetRef(playerVisualSo, "_visual",
                playerVisualGo.transform);
            if (playerVisualRefsChanged)
            {
                playerVisualSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // The gun: instantiated once as a child of the doll's RightHand
            // bone, then write-if-different reconciled against
            // GameFeelConfig's local transform every Apply — an owner's
            // number tweak on the milestone Б1 playtest applies without
            // tearing the object down and rebuilding it.
            Animator dollAnimator = playerVisualGo.GetComponent<Animator>();
            Transform hand = dollAnimator.GetBoneTransform(HumanBodyBones.RightHand);
            if (hand == null)
                throw new System.InvalidOperationException(
                    "StageOneSceneBootstrap: doll has no RightHand bone.");
            Transform gunTf = hand.Find(GunObjectName);
            if (gunTf == null)
            {
                GameObject gunModel = AssetDatabase.LoadAssetAtPath<GameObject>(GunModelPath);
                if (gunModel == null)
                    throw new System.InvalidOperationException(
                        "StageOneSceneBootstrap: no gun model at " + GunModelPath);
                var gun = (GameObject)PrefabUtility.InstantiatePrefab(gunModel);
                gun.name = GunObjectName;
                gun.transform.SetParent(hand, false);
                gunTf = gun.transform;
                sceneDirty = true;
            }
            if (gunTf.localPosition != gameFeel.GunLocalPosition)
            {
                gunTf.localPosition = gameFeel.GunLocalPosition;
                sceneDirty = true;
            }
            // Compare ROTATIONS, not euler read-backs: localEulerAngles returns values
            // re-derived from the quaternion (normalized to [0;360)), so e.g. (0,-90,0)
            // reads back as (0,270,0) and a naive != would re-dirty the scene on every
            // Apply (audit fix ПБ19). Writing via localEulerAngles keeps the serialized
            // euler hint consistent.
            Quaternion gunTargetRotation = Quaternion.Euler(gameFeel.GunLocalEuler);
            if (Quaternion.Angle(gunTf.localRotation, gunTargetRotation) > 1e-3f)
            {
                gunTf.localEulerAngles = gameFeel.GunLocalEuler;
                sceneDirty = true;
            }

            // CameraRig is the parent: it carries position/rotation, Main Camera
            // stays a child at local zero (П-3 resolution). Reparenting an existing
            // camera is itself guarded so a second run is a no-op.
            GameObject cameraRigGo = EditorBootstrapUtils.FindRootObject(scene, CameraRigObjectName);
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
            cameraRigRefsChanged |= EditorBootstrapUtils.SetRef(cameraRigSo, "_config", camera);
            cameraRigRefsChanged |= EditorBootstrapUtils.SetRef(cameraRigSo, "_runner", runner);
            cameraRigRefsChanged |= EditorBootstrapUtils.SetRef(cameraRigSo, "_aimProvider", aimProvider);
            cameraRigRefsChanged |= EditorBootstrapUtils.SetRef(cameraRigSo, "_gameFeelDirector", gameFeelDirector);
            if (cameraRigRefsChanged)
            {
                cameraRigSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            GameObject crosshairGo = EditorBootstrapUtils.FindRootObject(scene, CrosshairObjectName);
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
                EditorBootstrapUtils.RemoveCollider(markerGo);
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

            // Task 26 (spec §3.5/§3.11, resolution П-3): the honest spread-cone
            // ring — a world-space `LineRenderer` loop, sibling of `Marker` under
            // `Crosshair`. Module settings (`loop`/`useWorldSpace`/`positionCount`/
            // `widthMultiplier`) are one-time, existence-guarded like every other
            // module setup in this file (e.g. `ConfigureMuzzleParticles`) — only
            // the material is self-healed unconditionally, same treatment as the
            // marker's own `MeshRenderer.sharedMaterial` check just above, and
            // `CrosshairView.UpdateCone` overwrites every position every frame
            // regardless, so no positions need seeding here.
            Transform spreadConeTf = crosshairGo.transform.Find(SpreadConeObjectName);
            GameObject spreadConeGo;
            if (spreadConeTf == null)
            {
                spreadConeGo = new GameObject(SpreadConeObjectName);
                spreadConeGo.transform.SetParent(crosshairGo.transform, false);
                sceneDirty = true;
            }
            else
            {
                spreadConeGo = spreadConeTf.gameObject;
            }
            LineRenderer spreadCone = spreadConeGo.GetComponent<LineRenderer>();
            if (spreadCone == null)
            {
                spreadCone = spreadConeGo.AddComponent<LineRenderer>();
                spreadCone.loop = true;
                spreadCone.useWorldSpace = true;
                spreadCone.positionCount = CrosshairView.ConeSegments;
                spreadCone.widthMultiplier = 0.04f;
                spreadCone.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                sceneDirty = true;
            }
            if (spreadCone.sharedMaterial != spreadConeMat)
            {
                spreadCone.sharedMaterial = spreadConeMat;
                sceneDirty = true;
            }

            var crosshairSo = new SerializedObject(crosshairView);
            bool crosshairRefsChanged = false;
            crosshairRefsChanged |= EditorBootstrapUtils.SetRef(crosshairSo, "_marker", markerGo.transform);
            crosshairRefsChanged |= EditorBootstrapUtils.SetRef(crosshairSo, "_aimProvider", aimProvider);
            crosshairRefsChanged |= EditorBootstrapUtils.SetRef(crosshairSo, "_cone", spreadCone);
            crosshairRefsChanged |= EditorBootstrapUtils.SetRef(crosshairSo, "_runner", runner);
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

            GameObject arenaGo = EditorBootstrapUtils.FindRootObject(scene, ArenaObjectName);
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
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_runner", runner);
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_arena", arena);
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_floor", floorMat);
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_wall", wallMat);
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_obstacle", obstacleMat);
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
            GameObject eventSystemGo = EditorBootstrapUtils.FindRootObject(scene, EventSystemObjectName);
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

            GameObject hudGo = EditorBootstrapUtils.FindRootObject(scene, HudObjectName);
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
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_runner", runner);
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_hpFill", hpFill);
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_dashFill", dashFill);
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_waveText", waveText);
            if (hudRefsChanged)
            {
                hudSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 25 (spec Interfaces: "винетка (UI Image alpha-пульс)"): a
            // full-screen damage vignette, sibling-ordered right after the HUD
            // bars/wave-text (renders on top of them) and before the death/pause
            // panels below (renders underneath those — moot either way since it's
            // non-interactive and the panels are near-opaque, but keeps the
            // z-order intent explicit).
            Image vignetteImage = GetOrCreateVignette(hudGo.transform, ref sceneDirty);

            // Task 24 (spec Interfaces): death screen + pause menu panels, both
            // children of the same HUD canvas the bars/wave-text above live on
            // (later sibling order in the same Canvas renders them on top).
            // Both panels start hidden; DeathOverlayController/PauseController's
            // own Awake also enforces this defensively every play session.
            GameObject deathPanelGo = GetOrCreateOverlayPanel(hudGo.transform, DeathPanelObjectName, ref sceneDirty);
            GetOrCreateOverlayText(deathPanelGo.transform, "Title", "Носитель потерян",
                new Vector2(0f, 160f), new Vector2(700f, 70f), 42f, ref sceneDirty);
            TMP_Text deathMetrics = GetOrCreateOverlayText(deathPanelGo.transform, "Metrics", "",
                new Vector2(0f, -10f), new Vector2(700f, 260f), 24f, ref sceneDirty);
            GetOrCreateOverlayText(deathPanelGo.transform, "Hint", "R — заново · Shift+R — тот же seed",
                new Vector2(0f, -170f), new Vector2(700f, 30f), 18f, ref sceneDirty);
            Button deathRestartButton = GetOrCreateOverlayButton(deathPanelGo.transform, "RestartButton", "Заново",
                new Vector2(0f, -230f), new Vector2(220f, 50f), ref sceneDirty);

            GameObject deathOverlayGo = EditorBootstrapUtils.FindRootObject(scene, DeathOverlayObjectName);
            if (deathOverlayGo == null)
            {
                deathOverlayGo = new GameObject(DeathOverlayObjectName);
                sceneDirty = true;
            }
            DeathOverlayController deathOverlay = deathOverlayGo.GetComponent<DeathOverlayController>();
            if (deathOverlay == null)
            {
                deathOverlay = deathOverlayGo.AddComponent<DeathOverlayController>();
                sceneDirty = true;
            }
            var deathOverlaySo = new SerializedObject(deathOverlay);
            bool deathOverlayRefsChanged = false;
            deathOverlayRefsChanged |= EditorBootstrapUtils.SetRef(deathOverlaySo, "_runner", runner);
            deathOverlayRefsChanged |= EditorBootstrapUtils.SetRef(deathOverlaySo, "_gameFeelDirector", gameFeelDirector);
            deathOverlayRefsChanged |= EditorBootstrapUtils.SetRef(deathOverlaySo, "_panel", deathPanelGo);
            deathOverlayRefsChanged |= EditorBootstrapUtils.SetRef(deathOverlaySo, "_metricsText", deathMetrics);
            deathOverlayRefsChanged |= EditorBootstrapUtils.SetRef(deathOverlaySo, "_restartButton", deathRestartButton);
            if (deathOverlayRefsChanged)
            {
                deathOverlaySo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            GameObject pausePanelGo = GetOrCreateOverlayPanel(hudGo.transform, PausePanelObjectName, ref sceneDirty);
            GetOrCreateOverlayText(pausePanelGo.transform, "Title", "Пауза",
                new Vector2(0f, 160f), new Vector2(400f, 70f), 42f, ref sceneDirty);
            Button resumeButton = GetOrCreateOverlayButton(pausePanelGo.transform, "ResumeButton", "Продолжить",
                new Vector2(0f, 50f), new Vector2(240f, 50f), ref sceneDirty);
            Button pauseRestartButton = GetOrCreateOverlayButton(pausePanelGo.transform, "RestartButton",
                "Начать заново", new Vector2(0f, -20f), new Vector2(240f, 50f), ref sceneDirty);
            Button quitButton = GetOrCreateOverlayButton(pausePanelGo.transform, "QuitButton", "Выйти",
                new Vector2(0f, -90f), new Vector2(240f, 50f), ref sceneDirty);

            GameObject pauseControllerGo = EditorBootstrapUtils.FindRootObject(scene, PauseControllerObjectName);
            if (pauseControllerGo == null)
            {
                pauseControllerGo = new GameObject(PauseControllerObjectName);
                sceneDirty = true;
            }
            PauseController pauseController = pauseControllerGo.GetComponent<PauseController>();
            if (pauseController == null)
            {
                pauseController = pauseControllerGo.AddComponent<PauseController>();
                sceneDirty = true;
            }
            var pauseSo = new SerializedObject(pauseController);
            bool pauseRefsChanged = false;
            pauseRefsChanged |= EditorBootstrapUtils.SetRef(pauseSo, "_runner", runner);
            pauseRefsChanged |= EditorBootstrapUtils.SetRef(pauseSo, "_menu", pausePanelGo);
            pauseRefsChanged |= EditorBootstrapUtils.SetRef(pauseSo, "_resumeButton", resumeButton);
            pauseRefsChanged |= EditorBootstrapUtils.SetRef(pauseSo, "_restartButton", pauseRestartButton);
            pauseRefsChanged |= EditorBootstrapUtils.SetRef(pauseSo, "_quitButton", quitButton);
            if (pauseRefsChanged)
            {
                pauseSo.ApplyModifiedPropertiesWithoutUndo();
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

            GameObject viewsGo = EditorBootstrapUtils.FindRootObject(scene, ViewsObjectName);
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
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_runner", runner);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_gameFeel", gameFeel);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_arena", arena);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_mobPrefab", mobPrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_projectilePrefab", projectilePrefab);
            if (viewsRefsChanged)
            {
                viewsSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 25: second `GameFeelDirector` wiring pass — `_viewRegistry`
            // now exists (just created above) and `_vignette` was created
            // earlier in the Task 14 HUD section; `_runner`/`_gameFeel` were
            // already wired in the first pass near the top of this method.
            var gameFeelDirectorSo2 = new SerializedObject(gameFeelDirector);
            bool gameFeelDirectorRefsChanged2 = false;
            gameFeelDirectorRefsChanged2 |= EditorBootstrapUtils.SetRef(gameFeelDirectorSo2, "_viewRegistry", viewRegistry);
            gameFeelDirectorRefsChanged2 |= EditorBootstrapUtils.SetRef(gameFeelDirectorSo2, "_vignette", vignetteImage);
            if (gameFeelDirectorRefsChanged2)
            {
                gameFeelDirectorSo2.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            AudioClip shotClip = LoadAudioClip("shot.wav");
            AudioClip hitClip = LoadAudioClip("hit.wav");
            AudioClip mobDeathClip = LoadAudioClip("mob_death.wav");
            AudioClip dashClip = LoadAudioClip("dash.wav");
            AudioClip playerHitClip = LoadAudioClip("player_hit.wav");

            GameObject audioGo = EditorBootstrapUtils.FindRootObject(scene, AudioDirectorObjectName);
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
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_runner", runner);
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_gameFeel", gameFeel);
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_shotClip", shotClip);
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_hitClip", hitClip);
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_mobDeathClip", mobDeathClip);
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_dashClip", dashClip);
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_playerHitClip", playerHitClip);
            if (audioRefsChanged)
            {
                audioSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            GameObject muzzleGo = EditorBootstrapUtils.FindRootObject(scene, MuzzleFlashObjectName);
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
            // Task 28 (spec §3.11, ImmediateMuzzleFeedback): MuzzleFlashView
            // regains a SimulationRunner/GameFeelConfig reference — removed in
            // fix-round app-2pl round 2 (direction for the EVENT-driven burst
            // comes from the event's own Amount field, not a runner snapshot),
            // re-added here only for the SEPARATE per-frame prediction path
            // (RenderCurr.Player + WouldFireThisFrame); HandleEvent's own
            // tick-exact direction logic still needs neither.
            MuzzleFlashView muzzleFlash = muzzleGo.GetComponent<MuzzleFlashView>();
            var muzzleSo = new SerializedObject(muzzleFlash);
            bool muzzleRefsChanged = false;
            muzzleRefsChanged |= EditorBootstrapUtils.SetRef(muzzleSo, "_runner", runner);
            muzzleRefsChanged |= EditorBootstrapUtils.SetRef(muzzleSo, "_gameFeel", gameFeel);
            if (muzzleRefsChanged)
            {
                muzzleSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 27 (spec §3.11, Приложение П): persistent cosmetics —
            // shell casings, impact decals, corpses, and the three pooled
            // spark/burst particle systems (muzzle flash itself is the
            // MuzzleFlashView object just above, not duplicated here).
            Material casingMat = GetOrCreateMaterial(
                "CasingBrass", baseColor: new Color(0.25f, 0.16f, 0.05f), emissionColor: Color.black);
            Material corpseMat = GetOrCreateMaterial(
                "CorpseEmissive", baseColor: new Color(0.05f, 0.05f, 0.05f), emissionColor: new Color(0.1f, 0.1f, 0.1f));
            Material decalMat = GetOrCreateDecalMaterial("ScorchDecal", new Color(0.04f, 0.04f, 0.04f, 0.85f));
            Material hitSparkMat = GetOrCreateUnlitMaterial("HitSpark", new Color(3.5f, 3f, 1.6f));
            Material blockSparkMat = GetOrCreateUnlitMaterial("BlockSpark", new Color(2f, 2.3f, 3f));
            Material deathBurstMat = GetOrCreateUnlitMaterial("DeathBurst", new Color(4f, 1.3f, 0.3f));
            // Б1 fix-wave 2 review (app-9av): unlit quad, not a decal — see
            // GetOrCreateDecalMaterial's doc for why a decal material can't
            // glow at all. Color mirrors PlayerEmissive's accent (Э1) so the
            // mark reads as "this player's" trail, not a generic FX color.
            Material dashGlowMat = GetOrCreateUnlitMaterial("DashGlow", new Color(0f, 2.5f, 3f));

            CasingView casingPrefab = GetOrCreateCasingPrefab(casingMat);
            DecalProjector decalPrefab = GetOrCreateDecalPrefab(decalMat, gameFeel.DecalSize);
            CorpseView corpsePrefab = GetOrCreateCorpsePrefab(corpseMat);
            DashGlowView dashGlowPrefab = GetOrCreateDashGlowPrefab(dashGlowMat);
            // lifetime/speed/size/burstCount read from GameFeelConfig at
            // prefab-creation time (review fix-round — same "creation-time SO
            // read" contract as TracerFadeSeconds/GetOrCreateProjectilePrefab
            // above; milestone-4 DoD iteration moved HitSpark/BlockSpark
            // burstCount into the same bucket after the owner asked to
            // retune it on playtest). Cone angle stays a bootstrap-local
            // literal — pure shape, never asked to be tunable. DeathBurst's
            // burstCount (24) also stays a literal — no owner complaint about
            // it, so it wasn't promoted (see PersistentPropsDirector's
            // pool-capacity consts doc for the original "feel number vs
            // technical constant" line, now updated by this iteration for
            // the two counts the owner DID ask about).
            ParticleSystem hitSparkPrefab = GetOrCreateSparkPrefab(HitSparkPrefabPath, "HitSpark", hitSparkMat,
                lifetime: gameFeel.HitSparkLifetime, speed: gameFeel.HitSparkSpeed, size: gameFeel.HitSparkSize,
                burstCount: gameFeel.HitSparkBurstCount, coneAngle: 35f);
            ParticleSystem blockSparkPrefab = GetOrCreateSparkPrefab(BlockSparkPrefabPath, "BlockSpark", blockSparkMat,
                lifetime: gameFeel.BlockSparkLifetime, speed: gameFeel.BlockSparkSpeed, size: gameFeel.BlockSparkSize,
                burstCount: gameFeel.BlockSparkBurstCount, coneAngle: 25f);
            ParticleSystem deathBurstPrefab = GetOrCreateSparkPrefab(DeathBurstPrefabPath, "DeathBurst", deathBurstMat,
                lifetime: gameFeel.DeathBurstLifetime, speed: gameFeel.DeathBurstSpeed, size: gameFeel.DeathBurstSize,
                burstCount: 24, coneAngle: 90f);

            GameObject persistentPropsGo = EditorBootstrapUtils.FindRootObject(scene, PersistentPropsObjectName);
            if (persistentPropsGo == null)
            {
                persistentPropsGo = new GameObject(PersistentPropsObjectName);
                sceneDirty = true;
            }
            PersistentPropsDirector persistentProps = persistentPropsGo.GetComponent<PersistentPropsDirector>();
            if (persistentProps == null)
            {
                persistentProps = persistentPropsGo.AddComponent<PersistentPropsDirector>();
                sceneDirty = true;
            }
            var persistentPropsSo = new SerializedObject(persistentProps);
            bool persistentPropsRefsChanged = false;
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_runner", runner);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_gameFeel", gameFeel);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_arena", arena);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_casingPrefab", casingPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_decalPrefab", decalPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_corpsePrefab", corpsePrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_dashGlowPrefab", dashGlowPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_hitSparkPrefab", hitSparkPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_blockSparkPrefab", blockSparkPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_deathBurstPrefab", deathBurstPrefab);
            if (persistentPropsRefsChanged)
            {
                persistentPropsSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            GameObject routerGo = EditorBootstrapUtils.FindRootObject(scene, EventRouterObjectName);
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
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_runner", runner);
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_gameFeelDirector", gameFeelDirector);
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_persistentProps", persistentProps);
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_audioDirector", audioDirector);
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_muzzleFlash", muzzleFlash);
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_playerVisual", playerVisual);
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_viewRegistry", viewRegistry);
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_deathOverlay", deathOverlay);
            if (routerRefsChanged)
            {
                routerSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 24 (spec Interfaces): milestone-2 target dummies are retired
            // now that real wave spawning exists (Task 22) — self-heals a scene
            // saved by an older bootstrap run by removing any leftover
            // `PracticeTargets` object outright (existence-guard inverted:
            // PRESENCE, not absence, is what triggers a sceneDirty change here).
            // The `PracticeTargets` class/type itself is deleted from the
            // codebase, so this can only ever find a stale scene object, never
            // recreate the component.
            GameObject stalePracticeGo = EditorBootstrapUtils.FindRootObject(scene, PracticeTargetsObjectName);
            if (stalePracticeGo != null)
            {
                Object.DestroyImmediate(stalePracticeGo);
                sceneDirty = true;
            }

            // Task 21 (spec §3.6, resolution П-11): a `DevOverlay` object carrying
            // the dev-spawn-buttons stub, since grown into the full overlay
            // (Task 24). This wiring is NOT wrapped in a `#if` guard — `StageOneSceneBootstrap`
            // itself only ever compiles for the Editor (it lives under
            // `Assets/Scripts/Editor`, which Unity excludes from every player
            // build outright, guard or not), so re-guarding the reference here
            // would be redundant; only `DevOverlay`'s own file (in `Presentation/`,
            // which DOES ship into player builds) needs the compile guard, and it
            // has one.
            GameObject devOverlayGo = EditorBootstrapUtils.FindRootObject(scene, DevOverlayObjectName);
            if (devOverlayGo == null)
            {
                devOverlayGo = new GameObject(DevOverlayObjectName);
                sceneDirty = true;
            }
            DevOverlay devOverlay = devOverlayGo.GetComponent<DevOverlay>();
            if (devOverlay == null)
            {
                devOverlay = devOverlayGo.AddComponent<DevOverlay>();
                sceneDirty = true;
            }
            var devOverlaySo = new SerializedObject(devOverlay);
            bool devOverlayRefsChanged = false;
            devOverlayRefsChanged |= EditorBootstrapUtils.SetRef(devOverlaySo, "_runner", runner);
            devOverlayRefsChanged |= EditorBootstrapUtils.SetRef(devOverlaySo, "_aimProvider", aimProvider);
            if (devOverlayRefsChanged)
            {
                devOverlaySo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            if (sceneDirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log($"StageOneSceneBootstrap: gunner {(gunnerChanged ? "updated" : "ok")}, " +
                $"decal feature {(decalFeatureChanged ? "added" : "ok")}, " +
                $"casings layer {(casingsLayerChanged ? "added" : "ok")}, " +
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
            changed |= SetIfDifferent(ref m.AvoidMargin, 1f);
            return changed;
        }

        static bool SetIfDifferent(ref float field, float value)
        {
            if (field == value) return false;
            field = value;
            return true;
        }

        static T GetOrCreate<T>(string assetName) where T : ScriptableObject
            => GetOrCreate<T>(assetName, out _);

        /// F-5 fix-round overload: reports whether THIS call actually created the
        /// asset (as opposed to finding it already on disk) — see the `gunner`
        /// call site's doc for why that distinction matters. The bare overload
        /// above is unchanged for every caller that doesn't need it.
        static T GetOrCreate<T>(string assetName, out bool created) where T : ScriptableObject
        {
            string path = $"{DataDir}/{assetName}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) { created = false; return existing; }

            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            created = true;
            return asset;
        }

        /// Existence-guarded like `GetOrCreate<T>`: once the `.mat` exists on disk,
        /// its colors are never reapplied — an owner's in-Editor hand-tweak survives
        /// a re-run, same contract as the SO assets above.
        static Material GetOrCreateMaterial(string assetName, Color baseColor, Color emissionColor)
        {
            string path = $"{MaterialsDir}/{assetName}.mat";
            return EditorBootstrapUtils.GetOrCreateMaterial(path, EditorBootstrapUtils.UrpLitShader, mat =>
            {
                mat.SetColor("_BaseColor", baseColor);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                mat.SetColor("_EmissionColor", emissionColor);
            });
        }

        /// Task 17's tracer material: URP Unlit rather than Lit (like
        /// `GetOrCreateMaterial` above) so the trail reads as a flat glowing streak
        /// regardless of scene lighting — a `TrailRenderer`'s strip geometry isn't
        /// meant to be shaded. Existence-guarded the same way.
        static Material GetOrCreateUnlitMaterial(string assetName, Color color)
        {
            string path = $"{MaterialsDir}/{assetName}.mat";
            return EditorBootstrapUtils.GetOrCreateMaterial(path, EditorBootstrapUtils.UrpUnlitShader, mat =>
                mat.SetColor("_BaseColor", color));
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
            EditorBootstrapUtils.RemoveCollider(go);
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
            return EditorBootstrapUtils.BuildPrefab<ProjectileView>(ProjectilePrefabPath, () =>
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "ProjectileView";
                EditorBootstrapUtils.RemoveCollider(go);
                go.transform.localScale = Vector3.one * ProjectileDiameter;
                go.GetComponent<MeshRenderer>().sharedMaterial = sphereMat;

                TrailRenderer trail = go.AddComponent<TrailRenderer>();
                trail.time = tracerFadeSeconds;
                trail.startWidth = 0.06f;
                trail.endWidth = 0f;
                trail.minVertexDistance = 0.05f;
                trail.sharedMaterial = trailMat;

                go.AddComponent<ProjectileView>();
                return go;
            });
        }

        /// Task 27: idempotent replacement for clicking "Add Renderer Feature
        /// → Decal" in the Inspector (unavailable in batchmode) — reproduces
        /// `ScriptableRendererDataEditor.AddComponent`'s exact sequence (URP
        /// package source, `Editor/ScriptableRendererDataEditor.cs`): grow
        /// `m_RendererFeatures`/`m_RendererFeatureMap` by one element each,
        /// create the feature as a sub-asset of the renderer data asset
        /// (`AddObjectToAsset`, same as a real "Add Renderer Feature" click),
        /// force a save/reimport. Guarded by the public
        /// `ScriptableRendererData.TryGetRendererFeature&lt;T&gt;` — the same
        /// API the Inspector itself uses to detect
        /// `DecalRendererFeature`'s own `[DisallowMultipleRendererFeature]` —
        /// so a second `Apply()` run is a no-op, same contract as everything
        /// else in this file. Returns whether it actually added the feature.
        static bool AddDecalRendererFeatureIfMissing(string rendererDataPath)
        {
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(rendererDataPath);
            if (data == null)
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: no ScriptableRendererData at '{rendererDataPath}'.");

            if (data.TryGetRendererFeature<DecalRendererFeature>(out _)) return false;

            var so = new SerializedObject(data);
            SerializedProperty featuresProp = so.FindProperty("m_RendererFeatures");
            SerializedProperty mapProp = so.FindProperty("m_RendererFeatureMap");

            var feature = ScriptableObject.CreateInstance<DecalRendererFeature>();
            feature.name = nameof(DecalRendererFeature);
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            featuresProp.arraySize++;
            featuresProp.GetArrayElementAtIndex(featuresProp.arraySize - 1).objectReferenceValue = feature;
            mapProp.arraySize++;
            mapProp.GetArrayElementAtIndex(mapProp.arraySize - 1).longValue = localId;
            so.ApplyModifiedProperties();

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssetIfDirty(data);
            AssetDatabase.ImportAsset(rendererDataPath);
            return true;
        }

        /// Task 27 review fix-round: claims user layer 9 as "Casings" in
        /// `ProjectSettings/TagManager.asset` — the standard editor-script
        /// recipe for programmatically naming a layer (`SerializedObject`
        /// over the `layers` string array, no dedicated public API exists).
        /// Verified empty before this task claimed it (`grep` against the
        /// committed `TagManager.asset`, T13's `GreyboxBuilder.CosmeticsLayer`
        /// already owns layer 8). Idempotent: a second run sees `"Casings"`
        /// already in the slot and no-ops. Defensively throws instead of
        /// silently overwriting if slot 9 ever ends up holding some OTHER
        /// name (e.g. a teammate claims it for something else first) — same
        /// "hard error on unexpected setup state" policy as
        /// `LoadMaterial`/`LoadAudioClip` below. Returns whether it actually
        /// changed anything.
        static bool EnsureCasingsLayer()
        {
            Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
            if (tagManagerAssets == null || tagManagerAssets.Length == 0)
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: no asset at '{TagManagerPath}'.");

            var so = new SerializedObject(tagManagerAssets[0]);
            SerializedProperty layers = so.FindProperty("layers");
            SerializedProperty slot = layers.GetArrayElementAtIndex(PersistentPropsDirector.CasingsLayer);

            if (slot.stringValue == CasingsLayerName) return false;
            if (!string.IsNullOrEmpty(slot.stringValue))
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: layer {PersistentPropsDirector.CasingsLayer} is already " +
                    $"named '{slot.stringValue}' — refusing to overwrite it with '{CasingsLayerName}'.");

            slot.stringValue = CasingsLayerName;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            return true;
        }

        /// Task 27: the shared `CasingView` prefab — a small primitive
        /// Cylinder (default-primitive `CapsuleCollider` deliberately KEPT,
        /// unlike every other primitive in this file that calls
        /// `RemoveCollider` — this is the one prop that needs real PhysX to
        /// bounce off the arena's Cosmetics-layer geometry, T13) plus a
        /// `Rigidbody` and a `CasingView`. Existence-guarded like every other
        /// prefab helper here — EXCEPT the layer, which is self-healed
        /// unconditionally (review fix-round bug: an already-committed
        /// `Casing.prefab` may still carry the layer this task originally
        /// shipped with, `GreyboxBuilder.CosmeticsLayer`/8, shared with the
        /// arena's own colliders — see `PersistentPropsDirector`'s class doc
        /// for why that made casings fall through the floor; self-heal here
        /// is what fixes an already-committed prefab, not just a fresh one,
        /// same treatment as the muzzle-particle material check elsewhere in
        /// this file).
        static CasingView GetOrCreateCasingPrefab(Material casingMat)
        {
            var existing = AssetDatabase.LoadAssetAtPath<CasingView>(CasingPrefabPath);
            if (existing != null)
            {
                if (existing.gameObject.layer != PersistentPropsDirector.CasingsLayer)
                {
                    existing.gameObject.layer = PersistentPropsDirector.CasingsLayer;
                    EditorUtility.SetDirty(existing.gameObject);
                    AssetDatabase.SaveAssets();
                }
                return existing;
            }

            return EditorBootstrapUtils.BuildPrefab<CasingView>(CasingPrefabPath, () =>
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "Casing";
                go.layer = PersistentPropsDirector.CasingsLayer;
                go.transform.localScale = new Vector3(0.05f, 0.06f, 0.05f);
                go.GetComponent<MeshRenderer>().sharedMaterial = casingMat;

                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.mass = 0.01f;
                rb.linearDamping = 0.4f;
                rb.angularDamping = 0.6f;

                go.AddComponent<CasingView>();
                return go;
            });
        }

        /// Task 27: the shared `DecalProjector` prefab. `size`/`material` are
        /// one-time module config (existence-guarded like everything else in
        /// this file — `size` comes from `GameFeelConfig.DecalSize`, read
        /// once here at creation time, review fix-round); `pivot` stays the
        /// component's own default (`(0, 0, 0.5)`,
        /// `Runtime/Decal/DecalProjector.cs`) — it centers the projection box
        /// between the transform's own position (local Z = 0) and `size.z`
        /// ahead of it, which is exactly what
        /// `PersistentPropsDirector.HandleBlocked`'s own near-offset/rotation
        /// math assumes.
        static DecalProjector GetOrCreateDecalPrefab(Material decalMat, float size)
        {
            return EditorBootstrapUtils.BuildPrefab<DecalProjector>(DecalPrefabPath, () =>
            {
                var go = new GameObject("Decal");
                DecalProjector projector = go.AddComponent<DecalProjector>();
                projector.material = decalMat;
                projector.size = Vector3.one * size;
                return go;
            });
        }

        /// Б1 fix-wave 2 review (app-9av): the shared `DashGlowView` prefab —
        /// a flat unlit `Quad` (same primitive/orientation convention as
        /// `CrosshairView`'s `Marker` above — `RemoveCollider`, no default
        /// primitive collider kept, unlike `Casing`), NOT a `DecalProjector`
        /// (see `GetOrCreateDecalMaterial`'s doc for why a decal can't glow
        /// at all). `Spawn` fully resets position/rotation/scale/color every
        /// call (same "reset like fresh" contract as `CasingView.Spawn`), so
        /// this factory bakes no particular transform into the prefab —
        /// only the `DashGlowView`/`Renderer` sibling wiring, via `SetRef`
        /// on the staging object before it's saved, same technique the
        /// scene-wiring calls elsewhere in this file use.
        static DashGlowView GetOrCreateDashGlowPrefab(Material dashGlowMat)
        {
            return EditorBootstrapUtils.BuildPrefab<DashGlowView>(DashGlowPrefabPath, () =>
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "DashGlow";
                EditorBootstrapUtils.RemoveCollider(go);
                MeshRenderer renderer = go.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = dashGlowMat;

                DashGlowView view = go.AddComponent<DashGlowView>();
                var so = new SerializedObject(view);
                EditorBootstrapUtils.SetRef(so, "_renderer", renderer);
                so.ApplyModifiedPropertiesWithoutUndo();
                return go;
            });
        }

        /// Task 27: the shared `CorpseView` prefab — a bare, uncollided
        /// (`RemoveCollider`, same treatment as `MobView`/`ProjectileView`)
        /// Capsule; per-death tint comes from a `MaterialPropertyBlock`
        /// override in `CorpseView.Spawn`, never a material instance (same
        /// no-per-instance-materials rule `MobView` follows).
        static CorpseView GetOrCreateCorpsePrefab(Material corpseMat)
        {
            return EditorBootstrapUtils.BuildPrefab<CorpseView>(CorpsePrefabPath, () =>
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "Corpse";
                EditorBootstrapUtils.RemoveCollider(go);
                go.GetComponent<MeshRenderer>().sharedMaterial = corpseMat;
                go.AddComponent<CorpseView>();
                return go;
            });
        }

        /// Task 27: existence-guarded factory for the three pooled spark/burst
        /// particle prefabs (hit-spark, block-spark, death-burst) —
        /// `ConfigureBurstParticles` bakes an authored `Burst` at time 0 into
        /// the Emission module (unlike `ConfigureMuzzleParticles`'s manual
        /// `Emit()` call, `PersistentPropsDirector.PlayParticle` only ever
        /// calls `Play()`, so the burst has to be self-triggering) and sets
        /// `stopAction = Callback` so `ParticleReturnToPool.
        /// OnParticleSystemStopped` fires once the burst finishes.
        /// Milestone-4 DoD iteration: `lifetime`/`speed`/`size`/`burstCount`
        /// are all baked into the prefab ONCE at creation time, unlike
        /// `CasingView`/`CorpseView`'s runtime parameters — so once the owner
        /// asked to retune `burstCount` post-creation, "existence-guarded,
        /// never touched again" started conflicting with "GameFeelConfig is
        /// the source of truth for baked numbers" (fix-round precedent, same
        /// resolution applied here: `SparkParamsDiffer` below decides which
        /// wins). An already-committed prefab now self-heals whenever its
        /// baked values drift from the CURRENT `GameFeelConfig` call-site
        /// values — same "config wins, prefab catches up" policy the T27
        /// fix-round already established for `Casing.prefab`'s layer.
        static ParticleSystem GetOrCreateSparkPrefab(string path, string objectName, Material mat,
            float lifetime, float speed, float size, int burstCount, float coneAngle)
        {
            var existing = AssetDatabase.LoadAssetAtPath<ParticleSystem>(path);
            if (existing != null)
            {
                if (SparkParamsDiffer(existing, lifetime, speed, size, burstCount))
                {
                    ConfigureBurstParticles(existing, lifetime, speed, size, burstCount, coneAngle);
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();
                }
                return existing;
            }

            return EditorBootstrapUtils.BuildPrefab<ParticleSystem>(path, () =>
            {
                var go = new GameObject(objectName);
                ParticleSystem particles = go.AddComponent<ParticleSystem>();
                ConfigureBurstParticles(particles, lifetime, speed, size, burstCount, coneAngle);
                go.GetComponent<ParticleSystemRenderer>().sharedMaterial = mat;
                go.AddComponent<ParticleReturnToPool>();
                return go;
            });
        }

        /// True if an already-baked prefab's module values (lifetime/speed/
        /// size/burstCount — everything `ConfigureBurstParticles` writes
        /// that also has a `GameFeelConfig` source) no longer match the
        /// CURRENT config call-site values, so `GetOrCreateSparkPrefab`
        /// knows to re-bake. `coneAngle` is deliberately excluded (never
        /// config-sourced, see call-site doc) — comparing it would make an
        /// unrelated literal tweak here look like a "config drift" self-heal
        /// in the log/diff.
        static bool SparkParamsDiffer(ParticleSystem particles, float lifetime, float speed, float size,
            int burstCount)
        {
            ParticleSystem.MainModule main = particles.main;
            if (!Mathf.Approximately(main.startLifetime.constant, lifetime)) return true;
            if (!Mathf.Approximately(main.startSpeed.constant, speed)) return true;
            if (!Mathf.Approximately(main.startSize.constant, size)) return true;

            ParticleSystem.EmissionModule emission = particles.emission;
            int currentBurstCount = 0;
            if (emission.burstCount > 0)
            {
                var bursts = new ParticleSystem.Burst[emission.burstCount];
                emission.GetBursts(bursts);
                // F-6 fix: ConfigureBurstParticles below writes the burst as a
                // CONSTANT-mode MinMaxCurve (`new ParticleSystem.Burst(0f, (short)
                // burstCount)`), but `Burst.minCount` reads back the curve's
                // constantMIN, a distinct backing value that a hand-authored
                // min/max burst (e.g. a prefab built before this bake path
                // existed, or edited by hand in the Inspector) can leave sitting
                // at a stale value unrelated to what was actually written here —
                // `.count.constant` is what a constant-mode curve was actually SET
                // to, so it's the only read that round-trips this method's own
                // write. The old `minCount` read always disagreed with a
                // constant-mode `burstCount` (e.g. prefabs authored with
                // minScalar=30), so every Apply() saw "differs" and re-baked
                // (rebuilding the prefab, discarding an owner's Inspector tweak)
                // even when nothing had actually changed.
                currentBurstCount = (int)bursts[0].count.constant;
            }
            return currentBurstCount != burstCount;
        }

        static void ConfigureBurstParticles(ParticleSystem particles, float lifetime, float speed, float size,
            int burstCount, float coneAngle)
        {
            ParticleSystem.MainModule main = particles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.stopAction = ParticleSystemStopAction.Callback;
            main.startLifetime = lifetime;
            main.startSpeed = speed;
            main.startSize = size;
            main.startColor = Color.white;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)burstCount) });

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = coneAngle;
            shape.radius = 0.05f;
        }

        /// Task 27: existence-guarded like `GetOrCreateMaterial`/
        /// `GetOrCreateUnlitMaterial`, but cloned from URP's own shipped
        /// `Decal.mat` (loaded via its package virtual path) rather than
        /// built from `Shader.Find` — the Decal shader graph
        /// (`Shaders/Decal.shadergraph`) exposes the same `_BaseColor`
        /// convention as the project's `Universal Render Pipeline/Lit`
        /// materials (verified by reading the template's own `.mat` YAML),
        /// so only the tint needs overriding. Б1 fix-wave 2 review
        /// (app-9av): a since-removed `emissionColor` parameter here was
        /// dead on arrival — `Decal.shadergraph` has NO Emission block at
        /// all, so `EnableKeyword("_EMISSION")`/`_EmissionColor` on a decal
        /// material is a no-op (the keyword lands in `m_InvalidKeywords`).
        /// A glowing floor mark (`DashGlow`) needs a real emissive/unlit
        /// surface instead — see `GetOrCreateUnlitMaterial` and
        /// `DashGlowView`'s own doc for why it isn't a decal at all.
        static Material GetOrCreateDecalMaterial(string assetName, Color baseColor)
        {
            string path = $"{MaterialsDir}/{assetName}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Material template = AssetDatabase.LoadAssetAtPath<Material>(DecalTemplateMaterialPath);
            if (template == null)
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: no default Decal material at '{DecalTemplateMaterialPath}' — is URP installed?");

            var mat = new Material(template) { name = assetName };
            mat.SetColor("_BaseColor", baseColor);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
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
        ///
        /// bd app-yi9 (owner-found bug, milestone 3, diagnosis confirmed by
        /// reading the scene YAML directly): `Image.OnPopulateMesh` checks
        /// `overrideSprite == null` BEFORE it ever looks at `type` — with no
        /// sprite it falls straight through to `Graphic`'s own base mesh (a
        /// plain full-rect quad) for EVERY type, `Filled` included, so
        /// `fillAmount`/`fillMethod`/`fillOrigin` are silently ignored outright;
        /// the bar always rendered 100% full no matter what `HudController` set.
        /// `Image.Type.Simple` (the `Vignette` full-screen overlay from Task 25)
        /// is unaffected — its own null-sprite rendering already IS a plain
        /// full-rect quad, so the "fallback" path looks identical either way;
        /// only the fill TYPES actually need one. Fixed two ways: `fillSprite`
        /// is assigned at creation below for any FUTURE bar, and the early
        /// existence-guard return now self-heals an already-committed scene
        /// (checked unconditionally, same shape as this file's muzzle-particle
        /// material check) instead of trusting a stale `Fill` object as-is.
        static Image GetOrCreateBar(Transform parent, string name, Vector2 anchoredPos, Vector2 size,
            Color backgroundColor, Color fillColor, ref bool sceneDirty)
        {
            Sprite fillSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

            Transform existing = parent.Find(name);
            if (existing != null)
            {
                Image existingFill = existing.Find(FillObjectName).GetComponent<Image>();
                if (existingFill.sprite == null)
                {
                    existingFill.sprite = fillSprite;
                    sceneDirty = true;
                }
                return existingFill;
            }

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
            fillImage.sprite = fillSprite;
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
            if (existing != null)
            {
                TMP_Text existingText = existing.GetComponent<TMP_Text>();
                // F-8 fix: self-heals an already-committed scene's stale English
                // placeholder ("WAVE 0", predates the settled ADR-003 §9 word
                // list) the same way the HP/dash bars' fillSprite check above
                // self-heals — checked unconditionally so a scene saved by an
                // older bootstrap run picks up the fix, not just a fresh one.
                if (existingText.text.StartsWith("WAVE"))
                {
                    existingText.text = "ВОЛНА 0";
                    sceneDirty = true;
                }
                return existingText;
            }

            var go = new GameObject(WaveTextObjectName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-24f, -24f);
            rect.sizeDelta = new Vector2(240f, 40f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "ВОЛНА 0";
            tmp.fontSize = 28f;
            tmp.alignment = TextAlignmentOptions.TopRight;
            tmp.color = Color.white;

            sceneDirty = true;
            return tmp;
        }

        /// A full-screen, non-interactive damage vignette (Task 25 spec
        /// Interfaces: "винетка (UI Image alpha-пульс)") — a single `Image`
        /// stretched to fill the HUD canvas, starting fully transparent.
        /// `raycastTarget = false` so it never intercepts clicks meant for the
        /// HP/dash bars or the death/pause panel buttons layered around it.
        /// `GameFeelDirector` only ever writes this `Image`'s ALPHA channel
        /// (`UpdateVignette`) — the base RGB set here is a Presentation color
        /// choice, same as the HP/dash bar fill colors above, existence-guarded
        /// like everything else in this file so an owner's in-Editor tint tweak
        /// survives a re-run. No `GameFeelConfig.VignetteColor` field exists on
        /// that SO (checked, not assumed) — this task reuses `TraumaPlayerHit`/
        /// `TraumaDecayPerSec` for the pulse's peak/decay instead of growing the
        /// SO for a single new color.
        static Image GetOrCreateVignette(Transform parent, ref bool sceneDirty)
        {
            Transform existing = parent.Find(VignetteObjectName);
            if (existing != null) return existing.GetComponent<Image>();

            var go = new GameObject(VignetteObjectName, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            StretchToFillParent((RectTransform)go.transform);
            Image image = go.GetComponent<Image>();
            image.color = new Color(0.55f, 0.02f, 0.02f, 0f);
            image.raycastTarget = false;

            sceneDirty = true;
            return image;
        }

        /// A full-screen darkened background panel for a modal overlay (Task 24:
        /// death screen, pause menu) — a single `Image` stretched to fill its
        /// parent Canvas, starting hidden. Existence-guarded like everything
        /// else in this file: an owner's in-Editor tweak (e.g. background tint)
        /// survives a re-run.
        static GameObject GetOrCreateOverlayPanel(Transform parent, string name, ref bool sceneDirty)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing.gameObject;

            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            StretchToFillParent((RectTransform)go.transform);
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);
            go.SetActive(false);

            sceneDirty = true;
            return go;
        }

        /// A centered `TextMeshProUGUI` label inside a modal overlay panel (Task
        /// 24) — anchored to the panel's center with a pixel offset, so callers
        /// stack lines (title/metrics/hint) purely via `anchoredPos`.
        /// Existence-guarded like everything else in this file.
        static TMP_Text GetOrCreateOverlayText(Transform parent, string name, string defaultText,
            Vector2 anchoredPos, Vector2 size, float fontSize, ref bool sceneDirty)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing.GetComponent<TMP_Text>();

            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = defaultText;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;

            sceneDirty = true;
            return tmp;
        }

        /// A centered uGUI `Button` (background `Image` + `TextMeshProUGUI`
        /// label) inside a modal overlay panel (Task 24). Existence-guarded like
        /// everything else in this file.
        static Button GetOrCreateOverlayButton(Transform parent, string name, string label,
            Vector2 anchoredPos, Vector2 size, ref bool sceneDirty)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing.GetComponent<Button>();

            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;
            go.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.18f, 0.9f);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, false);
            StretchToFillParent((RectTransform)labelGo.transform);
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 22f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;

            sceneDirty = true;
            return go.GetComponent<Button>();
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

    }
}
