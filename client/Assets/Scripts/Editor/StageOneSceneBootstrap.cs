using System.Linq;
using Ring.Data;
using Ring.Presentation;
using Ring.Simulation.Core;
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
    /// `PersistentPropsDirector`, wired to every prefab above plus
    /// `_gameFeel` (Task 21 drops the `_arena` slot this director used to
    /// need — the decal/block-spark normal now comes straight off the
    /// triggering event's own `HitDir`, `PersistentPropsDirector`'s class
    /// doc); (4) `SimEventRouter`'s new `_persistentProps` slot.
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
    /// Task 12 (assets phase B plan, spec §3.7/§3.11, milestone Б2, owner
    /// decision 1b) replaces the placeholder capsule mob/corpse prefabs with
    /// real mech visuals: `MobChaserView.prefab`/`MobGunnerView.prefab`
    /// (`GetOrCreateMobArchetypePrefab`, a named `Visual` child + `MobView`/
    /// `MobVisual`, per T9/T10) bound to George/Leela respectively, and
    /// `CorpseMechView.prefab` (`GetOrCreateCorpseMechPrefab`, TWO named
    /// children — `VisualChaser`/`VisualGunner`, T12's own resolution) per
    /// `CorpseView`'s Б4 mech-corpse wiring. Both factories guard on
    /// `EditorBootstrapUtils.PrefabVisualsMatch` rather than plain existence
    /// (Б11) — the mapping constants above are the SOLE place the pair is
    /// chosen, so a re-Apply after an edit there rebuilds the affected
    /// prefab(s) instead of silently keeping the old model. `GetOrCreateMobPrefab`
    /// and its capsule `MobView.prefab` are retired outright (`ViewRegistry`'s
    /// old single `_mobPrefab` slot became two, `_chaserPrefab`/
    /// `_gunnerPrefab`, in the same T9/T11 change that made this task
    /// possible); `GetOrCreateCorpsePrefab`'s old capsule stays in the file
    /// and on disk (ПБ13, `CorpseView` doc) but is no longer wired to
    /// `PersistentPropsDirector` — `CorpseMechView` takes its `_corpsePrefab`
    /// slot instead.
    /// Task 24 (revised per app-1zf's investigation: primitives only, see
    /// `GibView`'s class doc) adds an eighth persistent-cosmetic prefab:
    /// `Gib` (`GetOrCreateGibPrefab`), reusing `PersistentPropsDirector.
    /// CasingsLayer` outright rather than claiming a new one —
    /// `PersistentPropsDirector`'s existing wiring block gains a
    /// `_gibPrefab` slot alongside `_casingPrefab`/`_decalPrefab`/
    /// `_corpsePrefab`/`_dashGlowPrefab`.
    /// T24-2 (owner-approved Blender split) keeps `Gib.prefab`'s slot but
    /// changes its own internal shape (`GetOrCreateGibPrefab`'s doc) and adds
    /// four more `PersistentPropsDirector` slots: `_chaserParts`/
    /// `_gunnerParts` (`Mesh[]`, `LoadGibParts` off `George_Parts.fbx`/
    /// `Leela_Parts.fbx`, `_Ring/Gibs/`) and `_chaserPartMaterial`/
    /// `_gunnerPartMaterial` (the live mechs' own `_Ring/Materials/
    /// *_Texture.mat` remaps, loaded not created — `ThirdPartyImportBootstrap`
    /// is what actually produces/reuses them).
    /// Task 20 (spec Г5, PC6/PC8/QA10) adds the aim-assist ray: a new `AimRay`
    /// root object carrying `LineRenderer` + `AimRayView`, wired to `_runner`/
    /// `_aimProvider`/`_gameFeel`/`_rayMaterial` — the last built here via a
    /// new `GetOrCreateUnlitMaterial("AimRayEmissive", ...)` call alongside
    /// `spreadConeMat`'s own. The existing `Crosshair/Marker` swaps its
    /// square Quad primitive for a flat round `Cylinder` disc (same shape
    /// idiom `GreyboxBuilder`'s floor/obstacles already use), self-healing an
    /// already-committed scene's stale Quad the same way the Player root's
    /// leftover capsule renderer self-heals above.
    /// В1 fix-wave 1 (owner playtest feedback, app-n6g): item 1 retires the
    /// HUD's dash-cooldown bar (two bars only, HP + Stamina) — the HUD bars
    /// section below self-heals an already-committed scene's leftover
    /// `DashBar` object out and slides `StaminaBar` up into its old slot;
    /// `GetOrCreateBar`'s own existence branch grows a matching
    /// anchoredPosition self-heal so that reposition actually reaches a
    /// scene the bar already exists in, not just a fresh one. Item 4 (owner's
    /// sanctioned milestone numbers, `GunnerVisualScale` 0.4→0.76) exposed a
    /// separate self-heal gap: `GetOrCreateMobArchetypePrefab`'s early-return
    /// path (prefab already on disk, model unchanged) called
    /// `SelfHealAimProxyOnPrefab` but never re-applied `EnsureVisual`'s own
    /// scale check, so a pure `ChaserVisualScale`/`GunnerVisualScale` retune
    /// never reached an already-committed prefab — `SelfHealVisualScaleOnPrefab`
    /// (new, called alongside `SelfHealAimProxyOnPrefab` in that same branch)
    /// closes it, generically for both archetypes.
    /// В1 fix-wave 3 (owner-decided economy rework, app-n6g): HeroConfig's
    /// `LinkedDashStaminaCost` field is retired outright (the old
    /// discounted-dash-in-window model) — `LinkRefund` (new, appended at the
    /// end of the class) replaces it as the class's sync-marker field, per
    /// the drill above: `EnsureAssetHasKey` now checks for `LinkRefund`
    /// instead of `AimSettleSeconds`, so an already-committed HeroConfig.asset
    /// predating this wave self-heals the new key on this Apply. No new
    /// scene objects/references — this wave is config-shape only.
    /// Stage 2 Task 45a (spec §3.12) SUPERSEDES THE PHASE-B TASK 8 PARAGRAPH
    /// ABOVE, which this history keeps verbatim as the record of what that task
    /// did rather than of what the file does now (append-only, same as every
    /// entry here): the scene's `Player` object is gone. Every doll in a match
    /// — this client's own included — is an instance of ONE pooled prefab,
    /// `GetOrCreatePlayerDollPrefab` below, wired into `ViewRegistry`'s new
    /// `_playerPrefab` slot; `Apply` now self-heals the scene by DESTROYING a
    /// leftover `Player` root, exactly the way the retired `PracticeTargets`
    /// object is destroyed. Consequences for the two paragraphs above: no
    /// `PlayerView`/`PlayerVisual` reference wiring happens in the scene at all
    /// (`PlayerVisual` keeps only `_animator`/`_visual`, both prefab-internal),
    /// `SimEventRouter`'s `_playerVisual` slot is gone with the class's fan-out
    /// entry (`ViewRegistry.HandlePlayerEvent` took its place), and the gun's
    /// write-if-different pose reconciliation moved from the scene block into
    /// `SelfHealGunPoseOnPrefab`, against the same `GameFeelConfig.GunLocalPosition`/
    /// `GunLocalEuler` numbers, so an owner's Б1 tweak still reaches a build.
    /// Stage 2 Task 45b (owner requirement 2026-08-10) gives that gun two empty
    /// children — `Muzzle` and `EjectPort`, `EnsureGunSockets` — posed from
    /// three new `GameFeelConfig` fields and reconciled on every `Apply` by
    /// `SelfHealGunSocketsOnPrefab`, which also wires them into `PlayerView`
    /// (the runtime read) and `PlayerGunTuner` (the owner's PlayMode gizmo
    /// loop). Three scene references follow them: `MuzzleFlashView` and
    /// `PersistentPropsDirector` gain a `_viewRegistry` slot in their existing
    /// wiring blocks, and `AimRayView` gains one in a second pass of its own,
    /// because its object is built long before `ViewRegistry` exists — the same
    /// shape, and the same reason, as `GameFeelDirector`'s second pass. The
    /// sync-marker key moves with the new fields, `RemotePlayerEmission` →
    /// `GunEjectLocalEuler`.
    /// Stage 2 Task 47b (spec §3.10, the owner's decisions 4a/4b of
    /// 2026-08-11) adds the two objects a spectator needs and nothing else.
    /// `HUD/SpectateLabel` is a `TMP_Text` on the HUD canvas, top-center,
    /// wired into `HudController._spectateLabel`, LEFT DISABLED IN THE SCENE
    /// (fix-round 1) and shown only while this client is watching somebody
    /// else — the object's shipped state has to be the state the game loads
    /// in, because a run-time rule cannot run before the game is running;
    /// the same block wires the STAMINA
    /// BAR'S ROOT into `_staminaBar`, because that bar is HIDDEN while
    /// spectating rather than drawn empty (no stamina of anyone else exists on
    /// the wire, and an empty bar claims one). `DeathPanel/SpectateButton` is a
    /// second `GetOrCreateOverlayButton` in the SAME slot as `RestartButton`,
    /// which is safe because `DeathOverlayController.Show` offers exactly one
    /// of the two — by `ISimBackend.CanRestartMatch` — and the panel's `Hint`
    /// line is now wired in as well so it can be hidden beside the restart
    /// button whose keys it advertises. `GetOrCreateWaveText`'s eleven lines of
    /// label construction moved into a shared `GetOrCreateHudLabel` that both
    /// labels call; its own self-heal branch is untouched. No new numbers, no
    /// new sync-marker key: this task adds no asset field.
    public static class StageOneSceneBootstrap
    {
        const string DataDir = "Assets/Data";
        const string MaterialsDir = "Assets/Art/Materials";
        const string ScenePath = "Assets/Scenes/Main.unity";
        const string ActionsAssetPath = "Assets/InputSystem_Actions.inputactions";
        const string PlayerObjectName = "Player";
        const string CameraRigObjectName = "CameraRig";
        const string CrosshairObjectName = "Crosshair";
        const string MarkerObjectName = "Marker";
        const string SpreadConeObjectName = "SpreadCone";
        const string AimRayObjectName = "AimRay"; // Task 20
        const string ArenaObjectName = "Arena";
        const string EventSystemObjectName = "EventSystem";
        const string HudObjectName = "HUD";
        const string HpBarObjectName = "HpBar";
        const string DashBarObjectName = "DashBar";
        const string StaminaBarObjectName = "StaminaBar"; // Task 22
        const string WaveTextObjectName = "WaveText";
        const string SpectateLabelObjectName = "SpectateLabel"; // Task 47b
        const string SpectateButtonObjectName = "SpectateButton"; // Task 47b
        const string BackgroundObjectName = "Background";
        const string FillObjectName = "Fill";

        // Task 17.
        const string PrefabsDir = "Assets/Prefabs";
        const string AudioDir = "Assets/Audio/Placeholders";
        const string ProjectilePrefabPath = PrefabsDir + "/ProjectileView.prefab";
        const string ViewsObjectName = "Views";
        const string AudioDirectorObjectName = "AudioDirector";
        const string MuzzleFlashObjectName = "MuzzleFlash";
        const string EventRouterObjectName = "EventRouter";
        const string PracticeTargetsObjectName = "PracticeTargets";
        // В3 fix-wave 2 (item 1): only the PREFAB ASSET's own cold Inspector/preview
        // scale now — `ProjectileView.Bind` overwrites `transform.localScale` with
        // this SHOT's own live sim-radius-derived diameter on every rent, before
        // the pooled view is ever re-enabled/rendered (ViewRegistry.SyncProjectiles),
        // so this constant no longer reaches the screen in play.
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
        const string SlideDustPrefabPath = PrefabsDir + "/SlideDust.prefab"; // Task 22
        const string PickupPopPrefabPath = PrefabsDir + "/PickupPop.prefab"; // Stage 3 Task 31
        const string PersistentPropsObjectName = "PersistentProps";
        const string TagManagerPath = "ProjectSettings/TagManager.asset";
        const string CasingsLayerName = "Casings";

        // Task 19 (spec QA7/QD1): the 3D aim-proxy raycast layer.
        const string AimProxyLayerName = "AimProxy";

        // Б1 fix-wave 2 (app-9av): dash-start floor mark.
        const string DashGlowPrefabPath = PrefabsDir + "/DashGlow.prefab";

        // Task 24 (revised per app-1zf: primitives only — see GibView's class doc);
        // T24-2 (owner-approved Blender split) swaps the prefab's SHAPE to a
        // single mesh-swappable object — same path, see GetOrCreateGibPrefab.
        const string GibPrefabPath = PrefabsDir + "/Gib.prefab";
        // T24-2: gib part meshes, cut from the SAME source mechs
        // (ChaserModelPath/GunnerModelPath below) — same material slot names
        // ("George_Texture"/"Leela_Texture"), so ThirdPartyImportBootstrap's
        // RemapPackMaterials reuses the live mechs' own remap materials
        // rather than creating duplicates (see that class's doc).
        const string ChaserGibPartsPath = ThirdPartyAssetPostprocessor.GibsRoot + "George_Parts.fbx";
        const string GunnerGibPartsPath = ThirdPartyAssetPostprocessor.GibsRoot + "Leela_Parts.fbx";

        // Task 8 (assets phase B plan, spec §3.2): the pistol in the doll's hand.
        const string GunObjectName = "Gun";
        // Stage 2 Task 45b: the pistol's own two anchor points — the mouth of
        // the barrel and the ejection port. Empty children of the gun, posed
        // from GameFeelConfig (EnsureGunSockets).
        const string MuzzleSocketObjectName = "Muzzle";
        const string EjectSocketObjectName = "EjectPort";
        // 8a: swapping the gun = this one id.
        const string GunModelPath = ThirdPartyAssetPostprocessor.SciFiRoot + "Models/Gun_Pistol.fbx";

        // Task 12 (assets phase B plan, spec §3.7/§3.11, milestone Б2): the
        // mech archetype + corpse prefab paths. Owner decision 1b: starting
        // pair; swapping a mech = edit ChaserModelPath/GunnerModelPath here
        // + re-Apply (the source-path guard rebuilds the prefab, Б11) —
        // this is the SOLE place the pair is chosen.
        const string ChaserModelPath = ThirdPartyAssetPostprocessor.MechRoot + "Models/George.fbx";
        const string GunnerModelPath = ThirdPartyAssetPostprocessor.MechRoot + "Models/Leela.fbx";
        // Stage 3 Task 31 (spec §3.11/Р251, owner decision R-192): the other
        // two archetypes, out of the OTHER pack — ASSETS-001 §2.2 reserves the
        // Sci-Fi Essentials robots for Elite and the Director's escort
        // precisely so they cannot be mistaken for wave meat, and §2.3 gives
        // the Director the kit's biggest robot at a scale of its own.
        //
        // WHY THESE TWO OF THE KIT'S THREE, measured against the generated
        // controllers rather than chosen by name: `Enemy_EyeDrone` carries
        // neither Walk nor Run nor any death take (it is a hovering drone),
        // so it cannot drive `MobVisual`'s locomotion at all and stays a
        // reserve. `Enemy_Trilobite` and `Enemy_QuadShell` both carry
        // Idle/Walk/Run/Attack/TurnOff, and the owner already looked at both
        // on the preview stage — Trilobite as the big elite (halved, asset
        // milestone 3), QuadShell as the Director's stub in `DirectorSkin`.
        // Same "SOLE place the pair is chosen" rule as the mech pair above.
        const string EliteModelPath = ThirdPartyAssetPostprocessor.SciFiRoot + "Models/Enemy_Trilobite.fbx";
        const string DirectorModelPath = ThirdPartyAssetPostprocessor.SciFiRoot + "Models/Enemy_QuadShell.fbx";
        // Stage 2 Task 45a (spec §3.12): the collector doll is a pooled prefab
        // now, one instance per player slot, rented by `ViewRegistry` — the
        // scene's own `Player` object is retired in `Apply`.
        const string PlayerDollPrefabPath = PrefabsDir + "/PlayerDollView.prefab";
        const string MobChaserPrefabPath = PrefabsDir + "/MobChaserView.prefab";
        const string MobGunnerPrefabPath = PrefabsDir + "/MobGunnerView.prefab";
        const string MobElitePrefabPath = PrefabsDir + "/MobEliteView.prefab";
        const string MobDirectorPrefabPath = PrefabsDir + "/MobDirectorView.prefab";
        const string CorpseMechPrefabPath = PrefabsDir + "/CorpseMechView.prefab";
        // Stage 3 Task 31 (spec §3.11): the raid's furniture. Four container
        // prefabs rather than five — the two corpse kinds share the marker,
        // because their body is drawn by somebody else already
        // (`ContainerView`'s own doc).
        const string PickupPrefabPath = PrefabsDir + "/PickupView.prefab";
        const string CrateContainerPrefabPath = PrefabsDir + "/ContainerCrateView.prefab";
        const string CacheContainerPrefabPath = PrefabsDir + "/ContainerCacheView.prefab";
        const string GroundContainerPrefabPath = PrefabsDir + "/ContainerGroundView.prefab";
        const string CorpseMarkerPrefabPath = PrefabsDir + "/ContainerCorpseMarkerView.prefab";
        // Models from the same Sci-Fi Essentials kit the Elite and the Director
        // come out of (ASSETS-001 §2.2 reserves that kit's crates for loot on
        // purpose), so the loot reads as belonging to the same world as the
        // robots guarding it.
        const string CrateModelPath = ThirdPartyAssetPostprocessor.SciFiRoot + "Models/Prop_Crate.fbx";
        const string CacheModelPath = ThirdPartyAssetPostprocessor.SciFiRoot + "Models/Prop_Chest.fbx";
        const string GroundModelPath = ThirdPartyAssetPostprocessor.SciFiRoot + "Models/Prop_Ammo.fbx";

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

            // Task 17 race-order guard (QD6): snapshot whether the already-
            // committed MobGunnerConfig.asset carries the MobConfig sync-marker
            // key BEFORE any EnsureAssetHasKey/SetDirty/SaveAssets call below
            // can touch it. ApplyGunnerZoneDefaults' gate needs "does this
            // asset predate the whole Task 1 zone-field block" — independent
            // of `gunnerCreated`, which only tells us "brand new asset this
            // run" and says nothing about an older asset that already existed
            // but was committed before those fields existed at all.
            bool gunnerMarkerPresent = System.IO.File
                .ReadAllText($"{DataDir}/MobGunnerConfig.asset")
                .Contains("SwingLeadMaxMeters");

            // Stage 3 Task 12 (spec §3.13, errata E-6 I5): the Elite and the
            // Director are new ASSETS of the existing MobConfig class, not a
            // new class — so they are seeded exactly the way the gunner is
            // (ApplyGunnerDefaults' own first-creation contract): a brand-new
            // asset gets the archetype numbers once, and an owner hand-tune at
            // milestone В1 survives every later re-run.
            MobConfig elite = GetOrCreate<MobConfig>("MobEliteConfig", out bool eliteCreated);
            MobConfig director = GetOrCreate<MobConfig>("MobDirectorConfig", out bool directorCreated);
            if (eliteCreated && ApplyEliteDefaults(elite)) EditorUtility.SetDirty(elite);
            if (directorCreated && ApplyDirectorDefaults(director)) EditorUtility.SetDirty(director);

            WaveConfig wave = GetOrCreate<WaveConfig>("WaveConfig");
            ArenaConfig arena = GetOrCreate<ArenaConfig>("ArenaConfig");
            // Stage 3 Task 12 (errata E-2): match-flow pacing — a brand-new SO
            // CLASS, so its C# field initializers ARE the shipped numbers and
            // no seeding method is needed (VisibilityConfig's own precedent).
            MatchFlowConfig flow = GetOrCreate<MatchFlowConfig>("MatchFlowConfig");
            // Stage 2 Task 22: seventh SimConfigBuilder.Build() parameter — a
            // brand-new asset, so its C# defaults ARE the shipped numbers
            // (VisibilityConfig.cs's own doc: they mirror
            // TestConfigs.Default().Visibility exactly), unlike
            // ApplyGunnerDefaults/ApplyStageTwoBalance below, which backfill an
            // OLDER asset that predates a field.
            VisibilityConfig visibility = GetOrCreate<VisibilityConfig>("VisibilityConfig");
            // Stage 3 Task 13 (spec §3.7/§3.8): the item catalog and the
            // loot balance sheet — both brand-new SO CLASSES, same
            // "C# defaults ARE the shipped numbers" precedent as
            // MatchFlowConfig above.
            ItemCatalog items = GetOrCreate<ItemCatalog>("ItemCatalog");
            LootConfig loot = GetOrCreate<LootConfig>("LootConfig");
            // Stage 2 Task 23 (spec §3.8/§3.15, Р52): NetConfig is NOT a
            // SimConfigBuilder.Build() parameter (see its own class doc for
            // why) and carries no scene reference below — until Task 33
            // (DevLatencySetup) and Task 41 (ServerBootstrap) nothing in the
            // scene consumes it, and a SimulationRunner field would wire a
            // network concern into Presentation, which spec §3.12 does not
            // provide for. This GetOrCreate call exists purely to deliver
            // the asset to disk, exactly like every other balance SO here.
            NetConfig net = GetOrCreate<NetConfig>("NetConfig");
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

            // Task 17: the Task 1 hit-zone-geometry block (LegsTop/BodyTop/
            // HeadTop/*DamageMult/MuzzleHeight) never got a gunner-archetype
            // override — ApplyGunnerDefaults above only covers the older
            // chaser-mirrored field block, so a committed MobGunnerConfig.asset
            // still carries the chaser's shorter silhouette. Same first-
            // creation contract as ApplyGunnerDefaults (F-5 regression guard —
            // PA4/PB2/PC3), PLUS a one-time backfill when the committed asset
            // predates the whole block (`gunnerMarkerPresent`, snapshotted
            // above before this run could change it). `SwingLead*` is
            // deliberately left untouched — the gunner archetype ignores melee
            // swing lead entirely (A15).
            gunnerChanged |= (gunnerCreated || !gunnerMarkerPresent) && ApplyGunnerZoneDefaults(gunner);
            if (gunnerChanged) EditorUtility.SetDirty(gunner);

            // Stage 2 Task 9 (owner decision F3a): call-gate scaffold for the
            // one-time Stage 2 balance delivery Task 16 populates. Sample
            // taken from ApplyGunnerZoneDefaults' BODY only (SetIfDifferent
            // per field, see ApplyStageTwoBalance below), not its call-site
            // gate — that gate is the EnsureAssetHasKey backfill marker
            // (gunnerMarkerPresent above), which only proves "does this field
            // EXIST", not "does it still hold the spec value", and which for
            // arena would already read true on this very Apply() (Arena's own
            // marker, PlayerSpawnRingFrac, is delivered by this same run a
            // few lines down) — copying it literally would gate on the wrong
            // signal on the one run that matters.
            //
            // Fix-round 1 (Explore/opus review, C-1): the first draft of this
            // gate read `arena.Walls == null || arena.Walls.Length == 0` on
            // the LOADED ArenaConfig instance. That is wrong by construction:
            // a missing YAML key silently falls back to the C# field
            // initializer (documented above, at EnsureAssetHasKey's own call
            // site) — so once Task 16 gives `Walls` a non-empty default array
            // (it must, or Build_DefaultAssets_MatchesTestConfigsBaseline
            // fails comparing CreateInstance defaults against
            // TestConfigs.DefaultArena()), `arena.Walls` would read populated
            // on EVERY run, including the very first one where the numbers
            // have never touched disk — ApplyStageTwoBalance would never
            // fire, and ArenaConfig.asset would silently keep pre-Stage-2
            // numbers forever while EditMode stays green (tests read C#
            // defaults, not the asset; the gap would only surface at a
            // playtest). The fix — mirroring gunnerMarkerPresent exactly — is
            // to measure the ON-DISK text instead, snapshotted here, BEFORE
            // the EnsureAssetHasKey/SaveAssets block below can change it.
            // "Walls:" cannot appear in ArenaConfig.asset before Task 16 adds
            // the field, so this reads true unconditionally through Tasks
            // 9-15 and turns meaningful only once Task 16's own Apply first
            // writes the key — no further edit needed at that point.
            //
            // AND IT IS NOW CLOSED FOR GOOD (spec Р120, phase F3 review I-3):
            // Task 16 committed "Walls:" into ArenaConfig.asset, so from here
            // on the predicate is false in every clone and
            // ApplyStageTwoBalance never runs again. New KEYS still arrive via
            // the EnsureAssetHasKey marker mechanism below, but any future
            // sanctioned edit of an EXISTING value on these three assets will
            // NOT be delivered by this bootstrap and must be re-gated
            // deliberately. Tasks 22/23/45 edit this file again — they must not
            // assume this block still fires.
            bool stageTwoPending = !System.IO.File
                .ReadAllText($"{DataDir}/ArenaConfig.asset")
                .Contains("Walls:");

            // Fix-round 1 (I-2): the eight Task 16 numbers do not all live on
            // `arena` — MaxMobsPerWave belongs to WaveConfig and
            // MaxCorpses/MaxCasings/MaxDecals to GameFeelConfig — so a single
            // `arenaChanged`/`SetDirty(arena)` pair would silently drop
            // whichever of those four numbers Task 16 changes. Three
            // independent flags/SetDirty calls instead, fed by two `out`
            // params from the shared call below.
            bool arenaChanged = false;
            bool waveChanged = false;
            bool feelChanged = false;
            if (stageTwoPending)
            {
                arenaChanged |= ApplyStageTwoBalance(arena, wave, gameFeel,
                    out bool waveDelta, out bool feelDelta);
                waveChanged |= waveDelta;
                feelChanged |= feelDelta;
            }

            // Stage 3 Task 12 (spec §3.13, plan Т12): the Stage 2 gate above is
            // CLOSED FOR GOOD (Р120) — it reads "Walls:", a key Task 16
            // committed — so the sanctioned Stage 3 edits of EXISTING values
            // need a gate of their own, measured the same way: on the
            // ArenaConfig.asset TEXT, snapshotted BEFORE the EnsureAssetHasKey
            // /SaveAssets block below can change it, and keyed on a field that
            // cannot appear in the file until this very task writes it.
            // "ZoneRadius:" is that field (Т8 declared it, deliberately empty,
            // and an empty array still serializes the key — so this reads true
            // exactly once, on the delivery run, and false in every clone
            // afterwards).
            //
            // NEW keys are NOT this block's business, then or now: they arrive
            // through the EnsureAssetHasKey marker mechanism (ArenaConfig's
            // marker is MaxContainerSlots, WaveConfig's EliteShareOuterCap —
            // both already moved by the tasks that appended those fields,
            // errata E-7/owner decision R-4), which is why this method touches
            // only values that already exist on disk.
            bool stageThreePending = !System.IO.File
                .ReadAllText($"{DataDir}/ArenaConfig.asset")
                .Contains("ZoneRadius:");

            bool netChanged = false;
            if (stageThreePending)
            {
                arenaChanged |= ApplyStageThreeBalance(arena, wave, net,
                    out bool waveThreeDelta, out bool netDelta);
                waveChanged |= waveThreeDelta;
                netChanged |= netDelta;
            }
            if (arenaChanged) EditorUtility.SetDirty(arena);
            if (waveChanged) EditorUtility.SetDirty(wave);
            if (feelChanged) EditorUtility.SetDirty(gameFeel);
            if (netChanged) EditorUtility.SetDirty(net);

            // Task 27 review fix-round (extended by the milestone-4 DoD
            // iteration, generalized to five assets by Task 17): an already-
            // committed SO asset predates whichever feel/balance field most
            // recently landed on its class — Unity only writes a
            // ScriptableObject's CURRENT field set to disk when something
            // marks it dirty (missing keys silently fall back to the C#
            // field initializer at load time either way, so this is a
            // traceability fix, not a correctness one: the owner should see
            // real numbers to hot-tweak in the Inspector/YAML, not an absent
            // key). `EditorBootstrapUtils.EnsureAssetHasKey` checks via a
            // direct text read (same technique the now-removed
            // HasStaleSerializedField migration helper used — Task 28 dropped
            // it once its one caller, MuzzleFlashView's `_runner` field, went
            // from stale-to-detect back to legitimately wired — inverted
            // here: detects a MISSING key instead of a stale one) so this is
            // a one-time sync per field addition, not an unconditional touch
            // every run. Each marker key is that class's MOST RECENTLY added
            // field (GameFeelConfig: `ContainerVisualScale` as of Stage 3 Task
            // 31 — the two archetype scales and the two furniture scales are
            // that class's new last fields and the committed asset predates
            // all four — was `MeshSagMeters`
            // (Stage 3 Task 30, the arc-segmentation tolerance shipped
            // alongside the three zone floor tints) before that,
            // `GunEjectLocalEuler` (Stage 2 Task 45b)
            // before that, `RemotePlayerEmission` (Stage 2 Task
            // 45a) before THAT — was `HeadHoverPulseAmp` (В3 fix-wave 2) before that,
            // `AimRayHeadAlphaBoost` (В3 fix-wave 1) before THAT,
            // `AimHoverGlowBoost` (В1/В2 fix-wave 2) before THAT, and
            // `LinkWindowFlashBoost` (В1 fix-wave 1) before THAT, see the
            // field's own doc for the fuller history; HeroConfig's marker is
            // `MaxInventoryItems` as of Stage 3 Task 4 (the backpack's two
            // capacity numbers, the class's new last field) — was
            // `PickupRadius` (Stage 3 Task 3, auto-pickup collection radius)
            // before that, `EdgeRequestMinTicks` (Stage 2 Task 8/9,
            // edge-request rate limiting) before THAT, `LinkRefund` (В1
            // fix-wave 3, owner economy rework) before THAT,
            // `AimSettleSeconds` (Task 17) before THAT;
            // WeaponConfig's marker moves to `EmergencyFireInterval` as of
            // Stage 3 Task 2 (spec Р261's ammo economy — the class's new last
            // field) — was `RunSpreadSpeedFrac` (Task 17) before that. Owner
            // decision R-4: the plan body assigned this relocation to Т12, but
            // the marker is always the class's LAST declared field, so it has
            // to move in the SAME task that appends the field or new fields
            // silently fail to reach a committed `.asset` even with every
            // test green (the errata E-7 precedent for Т3/Т4/Т8's own marker
            // moves) — Т12 stays a values-only delivery task. MobConfig's
            // marker field is unchanged since Task 17, so any asset committed
            // before that task predates it and self-heals on this Apply;
            // ArenaConfig's marker is `MaxContainerSlots` as of Stage 3 Task 8
            // (per-match container-slot cap, the class's new last field) —
            // was `MaxPickups` (Stage 3 Task 3, per-match pickup cap) before
            // that, `BarrierTop` (Stage 2 Task 46, the interior barriers'
            // modelled height) before THAT, `PlayerSpawnRingFrac` (Stage 2
            // Task 4) from Stage 2 Task 9, when ArenaConfig joined the
            // mechanism for the first time, before THAT — and the committed
            // asset carries each superseded key already, so leaving the
            // marker on any of them would have left the newer field unable
            // to reach the file at all).
            EditorBootstrapUtils.EnsureAssetHasKey(hero, $"{DataDir}/HeroConfig.asset", "MaxInventoryItems"); // Stage 3 Task 4 (was PickupRadius, Stage 3 Task 3)
            EditorBootstrapUtils.EnsureAssetHasKey(weapon, $"{DataDir}/WeaponConfig.asset", "EmergencyFireInterval"); // Stage 3 Task 2 (was RunSpreadSpeedFrac, Task 17)
            EditorBootstrapUtils.EnsureAssetHasKey(chaser, $"{DataDir}/MobChaserConfig.asset", "SwingLeadMaxMeters");
            EditorBootstrapUtils.EnsureAssetHasKey(gunner, $"{DataDir}/MobGunnerConfig.asset", "SwingLeadMaxMeters");
            EditorBootstrapUtils.EnsureAssetHasKey(gameFeel, $"{DataDir}/GameFeelConfig.asset", "ContainerVisualScale"); // Stage 3 Task 31 (was MeshSagMeters, Task 30)
            EditorBootstrapUtils.EnsureAssetHasKey(arena, $"{DataDir}/ArenaConfig.asset", "MaxContainerSlots"); // Stage 3 Task 8 (was MaxPickups, Stage 3 Task 3)
            // WaveConfig joined the marker mechanism in Stage 2 Task 16 with
            // PerPlayerCountFrac as its marker; Stage 3 Task 11 (coordinator
            // R-58) moves it to EliteShareOuterCap — the class's newest
            // field (zone budget + elite composition, spec §3.3) — same
            // migration pattern as ArenaConfig/HeroConfig/WeaponConfig's own
            // comments above: the committed asset already carries
            // PerPlayerCountFrac, so leaving the marker there would leave
            // the four new keys unable to reach the file at all.
            EditorBootstrapUtils.EnsureAssetHasKey(wave, $"{DataDir}/WaveConfig.asset", "EliteShareOuterCap"); // Stage 3 Task 11 (was PerPlayerCountFrac, Stage 2 Task 16)
            // VisibilityConfig joins the marker mechanism for the first time
            // here, in Stage 2 Task 22, with HearPositionGridMeters (the
            // class's own newest/last field) as its marker — the asset is
            // brand new on this run, so this call is a one-time onboarding
            // exactly like ArenaConfig/WaveConfig's own first-join comments
            // above, not a migration of an older asset.
            // Stage 3 Task 13 (owner decision R-88): the marker moves off
            // HearPositionGridMeters onto ContainerRadiusForVisibility —
            // this class's own new LAST field — same "append, don't
            // reshuffle" migration lesson 40 has already cost this
            // codebase four times (VisibilityConfig.cs's own doc has the
            // fuller account).
            EditorBootstrapUtils.EnsureAssetHasKey(visibility, $"{DataDir}/VisibilityConfig.asset",
                "ContainerRadiusForVisibility"); // Stage 3 Task 13 (was HearPositionGridMeters, Stage 2 Task 22)
            // Stage 3 Task 12: the two new MobConfig assets join the same
            // mechanism their two older siblings use, with MobConfig's own
            // marker field — brand-new assets on this run, so these are
            // one-time onboardings, not migrations. MatchFlowConfig joins for
            // the first time with DirectorReserveSlots, its class's own last
            // field.
            EditorBootstrapUtils.EnsureAssetHasKey(elite, $"{DataDir}/MobEliteConfig.asset",
                "SwingLeadMaxMeters"); // Stage 3 Task 12
            EditorBootstrapUtils.EnsureAssetHasKey(director, $"{DataDir}/MobDirectorConfig.asset",
                "SwingLeadMaxMeters"); // Stage 3 Task 12
            EditorBootstrapUtils.EnsureAssetHasKey(flow, $"{DataDir}/MatchFlowConfig.asset",
                "DirectorReserveSlots"); // Stage 3 Task 12
            // Stage 3 Task 13: the item catalog and loot balance sheet join
            // for the first time — brand-new assets, one-time onboardings
            // like MatchFlowConfig above. ItemCatalog's marker is its own
            // sole field (Items); LootConfig's is LootRadius, its class's
            // own last field.
            EditorBootstrapUtils.EnsureAssetHasKey(items, $"{DataDir}/ItemCatalog.asset",
                "Items"); // Stage 3 Task 13
            EditorBootstrapUtils.EnsureAssetHasKey(loot, $"{DataDir}/LootConfig.asset",
                "LootRadius"); // Stage 3 Task 13
            // Coordinator fix-round (Ф3 review C1): a VALUE rewrite, not a
            // missing key — EnsureAssetHasKey's own marker mechanism (just
            // above) only backfills a field the .asset text lacks entirely,
            // and every one of these five records was already on disk with
            // the wrong number. Same "text-read gate, one-time apply" shape
            // as ApplyStageThreeBalance's own `stageThreePending` (R-112),
            // sized to this ONE asset instead of three: the marker string
            // is "Id: 0" (YAML for the FIRST record's own field), present
            // only on a catalog this fix-round has not touched yet.
            bool itemIdsPending = System.IO.File
                .ReadAllText($"{DataDir}/ItemCatalog.asset")
                .Contains("Id: 0");
            if (itemIdsPending && ApplyItemCatalogIdShift(items)) EditorUtility.SetDirty(items);
            // NetConfig joins the marker mechanism for the first time here,
            // in Stage 2 Task 23, with MatchMaxDurationSeconds (the class's
            // own newest/last field) as its marker — brand-new asset on
            // this run, so this call is a one-time onboarding exactly like
            // ArenaConfig/WaveConfig/VisibilityConfig's own first-join
            // comments above, not a migration of an older asset. Stage 2
            // Task 41b moves the marker to MatchAbandonGraceSeconds, which
            // Task 41a appended as the class's new last field: the mechanism
            // is a text search for the marker's NAME in the committed YAML,
            // so a marker naming a key the asset already carries can never
            // dirty anything again — with MatchMaxDurationSeconds still named
            // here, neither SlewFraction (Task 41a) nor
            // MatchAbandonGraceSeconds would ever reach NetConfig.asset, and
            // both would silently fall back to their C# initializers while
            // EditMode stayed green (the tests read the C# defaults, not the
            // asset). Same drill as HeroConfig's own marker moves above
            // (AimSettleSeconds -> LinkRefund -> EdgeRequestMinTicks). Stage
            // 2 Task 42a moves the marker again, to
            // SpectatorSwitchCooldownSeconds, which it appended as the
            // class's new last field — same drill, same reason: with
            // MatchAbandonGraceSeconds still named here the new field would
            // never reach NetConfig.asset either. Stage 2 Task 47c moves it
            // once more, to EntityFadeTicks — the stranger-doll fade duration
            // that stopped being a NetworkSimBackend constant the moment
            // ViewRegistry became its reader. Same drill: with
            // SpectatorSwitchCooldownSeconds still named here the new key
            // would never reach NetConfig.asset, the owner's В1 fade knob
            // would silently be the C# initializer, and EditMode would stay
            // green throughout (the tests read C# defaults, not the asset).
            EditorBootstrapUtils.EnsureAssetHasKey(net, $"{DataDir}/NetConfig.asset",
                "EntityFadeTicks"); // Stage 2 Task 47c (was SpectatorSwitchCooldownSeconds, Task 42a; MatchAbandonGraceSeconds, Task 41b; MatchMaxDurationSeconds, Task 23)

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

            // Task 19 (spec QA7/QD1): the 3D aim-proxy raycast layer, user layer
            // 10 — same idempotent TagManager claim as casings above, now shared
            // via EnsureUserLayer (QC14).
            bool aimProxyLayerChanged = EnsureAimProxyLayer();

            SimulationRunner runner =
                EditorBootstrapUtils.FindComponentInScene<SimulationRunner>(scene);
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
            bool aimRefsChanged = false;
            aimRefsChanged |= EditorBootstrapUtils.SetRef(aimSo, "_camera", mainCamera);
            // Task 19 (PA8/PD16): AimProvider needs the runner for LastFrameInput.
            // AimHeld (gates the proxy cast) and World.Config.Arena.Radius (the
            // cast's maxDistance) — same "Simulation" GameObject, so this is
            // always just self-referencing the very runner AimProvider lives on.
            aimRefsChanged |= EditorBootstrapUtils.SetRef(aimSo, "_runner", runner);
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
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_visibility", visibility);
            // Stage 3 Task 12 (owner decision R-73): without these three the
            // PlayMode scene would build SimConfig with Elite/Director/Flow at
            // zero — and waves have spawned Elites since Т11.
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_elite", elite);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_director", director);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_flow", flow);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_items", items);
            refsChanged |= EditorBootstrapUtils.SetRef(so, "_loot", loot);
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
            // Task 20: a cool cyan tint for the aim-assist ray, distinct from
            // the cone's warm-neon orange above and the tracer's near-white
            // cyan below — AimRayView reads AimRayWidth/AimRayAlpha off
            // GameFeelConfig fresh every frame, this material just supplies
            // the base emissive color those numbers scale.
            Material aimRayMat = GetOrCreateUnlitMaterial("AimRayEmissive", new Color(0.6f, 2.4f, 3.2f));

            // Stage 2 Task 45a (spec §3.12): the scene's `Player` object is
            // retired outright — every doll in the match, this client's own
            // included, is now rented from ONE pooled prefab by `ViewRegistry`
            // (GetOrCreatePlayerDollPrefab below). Keeping a scene-resident doll
            // for slot 0 would mean two homes for one concept: a doll wired to
            // `SimulationRunner`/`AimProvider`/`GameFeelConfig` for the local
            // player and a reference-free clone for everybody else. The
            // existence-guard is inverted here, exactly like the retired
            // `PracticeTargets` object below: PRESENCE, not absence, is what
            // makes the scene dirty.
            GameObject stalePlayerGo = EditorBootstrapUtils.FindRootObject(scene, PlayerObjectName);
            if (stalePlayerGo != null)
            {
                Object.DestroyImmediate(stalePlayerGo);
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
            if (markerTf != null)
            {
                // Task 20 (PC8, round mini-disc): an already-committed scene may
                // still carry the old square Quad marker — self-heal it the same
                // way the Player root's stale MeshRenderer/MeshFilter above are
                // torn down, rather than leaving the retired shape in place
                // forever under the plain-existence guard below.
                MeshFilter staleMarkerFilter = markerTf.GetComponent<MeshFilter>();
                if (staleMarkerFilter == null || staleMarkerFilter.sharedMesh == null
                    || staleMarkerFilter.sharedMesh.name != "Cylinder")
                {
                    Object.DestroyImmediate(markerTf.gameObject);
                    markerTf = null;
                    sceneDirty = true;
                }
            }
            GameObject markerGo;
            if (markerTf == null)
            {
                markerGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                markerGo.name = MarkerObjectName;
                EditorBootstrapUtils.RemoveCollider(markerGo);
                markerGo.transform.SetParent(crosshairGo.transform, false);
                // Cylinder's own axis is already Y (flat circular caps facing
                // up/down) — no extra rotation needed, unlike the retired
                // Quad's -Z-facing default. Thin in Y, round in XZ: a genuine
                // disc for the top-down ¾ camera, the same flat-round-shape
                // idiom `GreyboxBuilder.BuildFloor`/`BuildObstacles` already
                // use (their own Cylinder floor/obstacle discs).
                markerGo.transform.localScale = new Vector3(0.5f, 0.03f, 0.5f);
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
            // Task 20: AimDotScale — the marker's own scale multiplier while
            // AimHeld (class doc, PC8).
            crosshairRefsChanged |= EditorBootstrapUtils.SetRef(crosshairSo, "_gameFeel", gameFeel);
            // В3 fix-wave 1 (app-n6g item 3a): billboards the marker toward
            // this SAME camera (AimProvider's own `_camera` above, `mainCamera`
            // local var still in scope).
            crosshairRefsChanged |= EditorBootstrapUtils.SetRef(crosshairSo, "_camera", mainCamera);
            if (crosshairRefsChanged)
            {
                crosshairSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 20 (spec Г5, PC6/PC8/QA10): the aim-assist ray — a two-point
            // world-space LineRenderer from the weapon's muzzle to the current
            // aim point, visible only while AimHeld (AimRayView.LateUpdate). Its
            // own root object, not a Crosshair child: CrosshairView never drives
            // it, and it carries no marker of its own (the Crosshair's existing
            // `_marker` doubles as the aim dot while AimHeld, PC8 above).
            GameObject aimRayGo = EditorBootstrapUtils.FindRootObject(scene, AimRayObjectName);
            if (aimRayGo == null)
            {
                aimRayGo = new GameObject(AimRayObjectName);
                sceneDirty = true;
            }
            // LineRenderer BEFORE AimRayView (MuzzleFlashView/ParticleSystem
            // precedent, F-style ordering): AimRayView carries
            // `[RequireComponent(typeof(LineRenderer))]`, which auto-adds a
            // bare default-configured LineRenderer the instant
            // `AddComponent<AimRayView>()` runs if one isn't already present
            // — creating the component here FIRST means that implicit add is
            // a no-op instead of silently pre-empting (and skipping) the
            // one-time module setup below.
            LineRenderer aimRayLine = aimRayGo.GetComponent<LineRenderer>();
            if (aimRayLine == null)
            {
                aimRayLine = aimRayGo.AddComponent<LineRenderer>();
                aimRayLine.useWorldSpace = true;
                aimRayLine.positionCount = 2;
                aimRayLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                aimRayLine.enabled = false; // AimRayView.LateUpdate only enables it while AimHeld
                sceneDirty = true;
            }
            if (aimRayLine.sharedMaterial != aimRayMat)
            {
                aimRayLine.sharedMaterial = aimRayMat;
                sceneDirty = true;
            }
            AimRayView aimRayView = aimRayGo.GetComponent<AimRayView>();
            if (aimRayView == null)
            {
                aimRayView = aimRayGo.AddComponent<AimRayView>();
                sceneDirty = true;
            }

            var aimRaySo = new SerializedObject(aimRayView);
            bool aimRayRefsChanged = false;
            aimRayRefsChanged |= EditorBootstrapUtils.SetRef(aimRaySo, "_runner", runner);
            aimRayRefsChanged |= EditorBootstrapUtils.SetRef(aimRaySo, "_aimProvider", aimProvider);
            aimRayRefsChanged |= EditorBootstrapUtils.SetRef(aimRaySo, "_gameFeel", gameFeel);
            aimRayRefsChanged |= EditorBootstrapUtils.SetRef(aimRaySo, "_rayMaterial", aimRayMat);
            if (aimRayRefsChanged)
            {
                aimRaySo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 13 (spec §3.13): greybox arena. Floor/Wall/Obstacle materials are
            // hand-authored on disk (unlike the emissive placeholders above), so
            // they're loaded, not created — a missing file is a setup error, same
            // treatment as the InputActionAsset/MainCamera checks above.
            Material floorMat = LoadMaterial("Floor");
            Material wallMat = LoadMaterial("Wall");
            Material obstacleMat = LoadMaterial("Obstacle");
            // Stage 3 Task 30 (spec §3.11, plan errata I13): the two painted
            // floor rings `GreyboxBuilder` draws around extraction points.
            // Unlit like every other emissive accent this bootstrap generates
            // (TracerTrail/MuzzleFlash/DashGlow) — a marker painted on the
            // ground must read the same under any scene light, and a Lit
            // material on a 6 cm slab would mostly read as its own shadow.
            // CYAN FOR THE EARLY PORTAL, AMBER FOR THE GATE, on the palette
            // this project already speaks (GameFeelConfig's own color docs):
            // cyan is the player's own signature — an early portal is the way
            // out you brought with you — while the gate belongs to the
            // Director's half of the arena and takes the warm end. Neither
            // sits at the red the headshot cue owns.
            Material portalRingMat = GetOrCreateUnlitMaterial("PortalRing", new Color(0f, 2.2f, 2.8f));
            Material gateRingMat = GetOrCreateUnlitMaterial("GateRing", new Color(3f, 1.4f, 0.2f));

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
            // Stage 3 Task 30: the builder reads GameFeelConfig for the three
            // zone tints and for `MeshSagMeters`, the tolerance its derived
            // segment counts come out of (spec Р273).
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_gameFeel", gameFeel);
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_floor", floorMat);
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_wall", wallMat);
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_obstacle", obstacleMat);
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_portalRing", portalRingMat);
            greyboxRefsChanged |= EditorBootstrapUtils.SetRef(greyboxSo, "_gateRing", gateRingMat);
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

            // HP top-left, both left-anchored so the fill grows rightward from
            // a fixed origin (spec resolution П-2: fillOrigin Left, 1 = ready/
            // full).
            Image hpFill = GetOrCreateBar(hudGo.transform, HpBarObjectName,
                anchoredPos: new Vector2(24f, -24f), size: new Vector2(320f, 28f),
                backgroundColor: new Color(0.05f, 0.05f, 0.05f, 0.85f),
                fillColor: new Color(0.85f, 0.2f, 0.2f), ref sceneDirty);
            // В1 fix-wave 1 (owner playtest feedback, item 1 "две полоски"):
            // the dash-cooldown bar is retired outright — self-heals an
            // already-committed scene's leftover "DashBar" object the same
            // way Task 24's PracticeTargets retirement above does (presence,
            // not absence, triggers sceneDirty here; the object is a child of
            // the HUD canvas, not a scene root, so this uses Transform.Find
            // rather than FindRootObject).
            Transform staleDashBar = hudGo.transform.Find(DashBarObjectName);
            if (staleDashBar != null)
            {
                Object.DestroyImmediate(staleDashBar.gameObject);
                sceneDirty = true;
            }
            // Task 22's stamina bar now sits directly beneath HP, in the slot
            // the retired dash bar used to occupy (same left origin/sizing,
            // same ~8px gap under the HP bar's own bottom edge) — mirrors the
            // dash bar's old layout math now that it's the second bar again.
            // fillColor here is only the static creation-time default —
            // HudController overwrites it every frame from GameFeelConfig
            // once the bar exists.
            Image staminaFill = GetOrCreateBar(hudGo.transform, StaminaBarObjectName,
                anchoredPos: new Vector2(24f, -60f), size: new Vector2(320f, 14f),
                backgroundColor: new Color(0.05f, 0.05f, 0.05f, 0.85f),
                fillColor: gameFeel.StaminaBarFullColor, ref sceneDirty);
            TMP_Text waveText = GetOrCreateWaveText(hudGo.transform, ref sceneDirty);
            // Stage 2 Task 47b: the spectator's own line, top-center between the
            // two corners the bars and the wave counter already hold. Hidden by
            // `HudController` on every frame this client is watching its own
            // player, which is the whole of solo — so a solo screen gains
            // nothing here, which is the requirement (the owner's decision 4a).
            TMP_Text spectateLabel = GetOrCreateHudLabel(hudGo.transform, SpectateLabelObjectName,
                "НАБЛЮДЕНИЕ", anchor: new Vector2(0.5f, 1f), anchoredPos: new Vector2(0f, -24f),
                size: new Vector2(640f, 40f), fontSize: 26f,
                alignment: TextAlignmentOptions.Top, ref sceneDirty);
            // AND IT SHIPS DISABLED (fix-round 1, Ф-2). `GetOrCreateHudLabel`
            // hands back a live object — right for the wave counter, which is
            // always on screen, wrong for a label that belongs to a state the
            // game is not in when it loads. `HudController` switching it off at
            // run time is not enough on its own: the committed scene is what a
            // build starts from, and this one was committed with
            // `m_IsActive: 1`, so the word hung over the connect screen of
            // every networked client. Checked unconditionally rather than only
            // on creation, exactly like `GetOrCreateWaveText`'s "WAVE" heal
            // below — a scene saved by the previous run of this bootstrap
            // picks the fix up, and being ALREADY disabled raises no flag, so
            // a second run of this method changes nothing (А6).
            if (spectateLabel != null && spectateLabel.gameObject.activeSelf)
            {
                spectateLabel.gameObject.SetActive(false);
                sceneDirty = true;
            }
            // The bar's ROOT, which is what gets hidden — see HudController's
            // `_staminaBar`. Found rather than returned by `GetOrCreateBar`,
            // whose one job is the Fill every caller wires to.
            Transform staminaBar = hudGo.transform.Find(StaminaBarObjectName);

            HudController hud = hudGo.GetComponent<HudController>();
            if (hud == null)
            {
                hud = hudGo.AddComponent<HudController>();
                sceneDirty = true;
            }
            var hudSo = new SerializedObject(hud);
            bool hudRefsChanged = false;
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_runner", runner);
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_gameFeel", gameFeel);
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_hpFill", hpFill);
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_staminaFill", staminaFill);
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_waveText", waveText);
            hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_spectateLabel", spectateLabel);
            if (staminaBar != null)
                hudRefsChanged |= EditorBootstrapUtils.SetRef(hudSo, "_staminaBar", staminaBar.gameObject);
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
            TMP_Text deathHint = GetOrCreateOverlayText(deathPanelGo.transform, "Hint",
                "R — заново · Shift+R — тот же seed",
                new Vector2(0f, -170f), new Vector2(700f, 30f), 18f, ref sceneDirty);
            Button deathRestartButton = GetOrCreateOverlayButton(deathPanelGo.transform, "RestartButton", "Заново",
                new Vector2(0f, -230f), new Vector2(220f, 50f), ref sceneDirty);
            // Stage 2 Task 47b (the owner's decision 4b): the same slot as the
            // restart button, because the two are never both offered —
            // `DeathOverlayController.Show` shows exactly one of them, by
            // `ISimBackend.CanRestartMatch`.
            Button deathSpectateButton = GetOrCreateOverlayButton(deathPanelGo.transform,
                SpectateButtonObjectName, "Наблюдать",
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
            deathOverlayRefsChanged |= EditorBootstrapUtils.SetRef(deathOverlaySo, "_hintText", deathHint);
            deathOverlayRefsChanged |= EditorBootstrapUtils.SetRef(deathOverlaySo, "_spectateButton",
                deathSpectateButton);
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
            // Task 12: MobEmissive is no longer consumed by any prefab (the
            // mech visuals carry their own pack materials) — kept as a
            // greybox fallback on disk, statement-only, no local (no scene
            // consumer, same treatment PlayerEmissive already got in T8).
            GetOrCreateMaterial(
                "MobEmissive",
                baseColor: new Color(0.06f, 0.06f, 0.06f),
                emissionColor: new Color(0.15f, 0.15f, 0.15f));
            Material projectileMat = GetOrCreateMaterial(
                "ProjectileEmissive",
                baseColor: new Color(0.02f, 0.03f, 0.04f),
                emissionColor: new Color(2.5f, 3f, 3.5f));
            Material tracerMat = GetOrCreateUnlitMaterial("TracerTrail", new Color(2.5f, 3f, 3.5f));
            Material muzzleMat = GetOrCreateUnlitMaterial("MuzzleFlash", new Color(4f, 2.2f, 0.6f));

            // Task 12 (owner decision 1b): the starting mech pair — George
            // (Chaser), Leela (Gunner). GetOrCreateMobArchetypePrefab's own
            // source-path guard (PrefabVisualsMatch) rebuilds the prefab if
            // the mapping above ever changes.
            MobView chaserPrefab = GetOrCreateMobArchetypePrefab(
                MobChaserPrefabPath, ChaserModelPath, gameFeel.ChaserVisualScale,
                chaser.LegsTop, chaser.BodyTop, chaser.HeadTop, chaser.Radius,
                gameFeel.AimProxyHeadRadiusFrac);
            MobView gunnerPrefab = GetOrCreateMobArchetypePrefab(
                MobGunnerPrefabPath, GunnerModelPath, gameFeel.GunnerVisualScale,
                gunner.LegsTop, gunner.BodyTop, gunner.HeadTop, gunner.Radius,
                gameFeel.AimProxyHeadRadiusFrac);
            // Stage 3 Task 31: the two archetypes that used to be rented out of
            // the Gunner's pool. Their belts come from their OWN MobConfigs, so
            // the Director's aim proxies are the Director's size — a boss you
            // could only headshot at a Gunner's head height would be worse than
            // no proxy at all.
            Material directorSkin = AssetPreviewSceneBootstrap.GetOrCreateDirectorSkin();
            MobView elitePrefab = GetOrCreateMobArchetypePrefab(
                MobElitePrefabPath, EliteModelPath, gameFeel.EliteVisualScale,
                elite.LegsTop, elite.BodyTop, elite.HeadTop, elite.Radius,
                gameFeel.AimProxyHeadRadiusFrac, AnimIds.MobClipFamily.SciFiEnemy);
            MobView directorPrefab = GetOrCreateMobArchetypePrefab(
                MobDirectorPrefabPath, DirectorModelPath, gameFeel.DirectorVisualScale,
                director.LegsTop, director.BodyTop, director.HeadTop, director.Radius,
                gameFeel.AimProxyHeadRadiusFrac, AnimIds.MobClipFamily.SciFiEnemy,
                directorSkin);
            ProjectileView projectilePrefab =
                GetOrCreateProjectilePrefab(projectileMat, tracerMat, gameFeel.TracerFadeSeconds);
            // Stage 3 Task 31 (spec §3.11): the cell and the four container
            // variants. CYAN for the cell — energy the collector is here to
            // carry, on the same signature color the player's own dash glow and
            // emissive already speak; a warmer AMBER for the corpse marker, so
            // "something to loot on a body" never reads as "a cell lying here".
            Material pickupMat = GetOrCreateUnlitMaterial("PickupCell", new Color(0.2f, 2.6f, 3.2f));
            Material lootMarkerMat = GetOrCreateUnlitMaterial("LootMarker", new Color(2.8f, 1.9f, 0.4f));
            PickupView pickupPrefab =
                GetOrCreatePickupPrefab(pickupMat, gameFeel.PickupVisualDiameter);
            ContainerView cratePrefab = GetOrCreateContainerPrefab(CrateContainerPrefabPath,
                CrateModelPath, null, gameFeel.ContainerVisualScale);
            ContainerView cachePrefab = GetOrCreateContainerPrefab(CacheContainerPrefabPath,
                CacheModelPath, null, gameFeel.ContainerVisualScale);
            ContainerView groundPrefab = GetOrCreateContainerPrefab(GroundContainerPrefabPath,
                GroundModelPath, null, gameFeel.ContainerVisualScale);
            // The marker is sized in its own right rather than at the container
            // scale: it is a tell on the ground, not a prop, and half a meter
            // is the same read the cell gets.
            ContainerView corpseMarkerPrefab = GetOrCreateContainerPrefab(CorpseMarkerPrefabPath,
                null, lootMarkerMat, gameFeel.PickupVisualDiameter);
            // Stage 2 Task 45a: the collector doll, same factory shape as the
            // two mech archetypes above — it is a POOLED prefab now, one
            // instance per player slot, and no longer a scene object.
            PlayerView playerDollPrefab = GetOrCreatePlayerDollPrefab(
                PlayerDollPrefabPath, gameFeel.PlayerVisualScale,
                hero.LegsTop, hero.BodyTop, hero.HeadTop, hero.Radius,
                gameFeel.AimProxyHeadRadiusFrac, gameFeel);

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
            // В1/В2 fix-wave 2 (app-n6g item 3b): ViewRegistry.SyncMobs reads
            // AimProvider.CurrentHoveredMob once per frame for the hover-glow
            // boost — same reference already wired into CrosshairView/AimRayView
            // above (`aimProvider` local var, still in scope).
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_aimProvider", aimProvider);
            // Stage 2 Task 45a: the doll pool's own prefab slot, alongside the
            // two mech archetypes and the projectile.
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_playerPrefab", playerDollPrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_chaserPrefab", chaserPrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_gunnerPrefab", gunnerPrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_elitePrefab", elitePrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_directorPrefab", directorPrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_projectilePrefab", projectilePrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_pickupPrefab", pickupPrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_crateContainerPrefab", cratePrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_cacheContainerPrefab", cachePrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_groundContainerPrefab", groundPrefab);
            viewsRefsChanged |= EditorBootstrapUtils.SetRef(viewsSo, "_corpseMarkerPrefab", corpseMarkerPrefab);
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

            // Stage 2 Task 45b: second `AimRayView` wiring pass, and it exists
            // for exactly the reason `GameFeelDirector`'s does — the ray's
            // object is built with the rest of the aiming UI, well before
            // `ViewRegistry`, and the ray now starts at the local doll's own
            // muzzle socket instead of at the hero's center + a muzzle height.
            var aimRaySo2 = new SerializedObject(aimRayView);
            if (EditorBootstrapUtils.SetRef(aimRaySo2, "_viewRegistry", viewRegistry))
            {
                aimRaySo2.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            AudioClip shotClip = LoadAudioClip("shot.wav");
            AudioClip hitClip = LoadAudioClip("hit.wav");
            AudioClip mobDeathClip = LoadAudioClip("mob_death.wav");
            AudioClip dashClip = LoadAudioClip("dash.wav");
            AudioClip playerHitClip = LoadAudioClip("player_hit.wav");
            AudioClip staminaDeniedClip = LoadAudioClip("denied.wav"); // Task 22
            AudioClip ricochetClip = LoadAudioClip("ricochet.wav"); // Task 22
            // В3 fix-wave 2 (item 3c): same synthesized-placeholder idiom as
            // denied.wav/ricochet.wav above (44.1kHz/16-bit PCM mono, LFS —
            // `client/**/*.wav` already covers this path, no `.gitattributes`
            // touch needed) — a short (~55ms) bright downward chirp (2200→1500Hz)
            // with a fast percussive attack/decay envelope, audibly distinct from
            // denied.wav's own low 200→130Hz buzz/sweep.
            AudioClip headHoverTickClip = LoadAudioClip("head_hover_tick.wav");

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
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_staminaDeniedClip", staminaDeniedClip); // Task 22
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_ricochetClip", ricochetClip); // Task 22
            audioRefsChanged |= EditorBootstrapUtils.SetRef(audioSo, "_headHoverTickClip", headHoverTickClip); // В3 fix-wave 2
            if (audioRefsChanged)
            {
                audioSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // В3 fix-wave 2 (item 3c): second `CrosshairView` wiring pass —
            // `audioDirector` now exists (just created above); `crosshairView`/
            // `crosshairSo` are the SAME local vars the first pass (near the
            // `Crosshair` object's own creation, earlier in this method) already
            // set up, still in scope — same "second wiring pass" idiom as
            // `gameFeelDirectorSo2` above (that block's own comment).
            var crosshairSo2 = new SerializedObject(crosshairView);
            bool crosshairRefsChanged2 = false;
            crosshairRefsChanged2 |= EditorBootstrapUtils.SetRef(crosshairSo2, "_audio", audioDirector);
            if (crosshairRefsChanged2)
            {
                crosshairSo2.ApplyModifiedPropertiesWithoutUndo();
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
            // Stage 2 Task 45b: the flash is anchored to the shooter's own doll
            // now (`ViewRegistry.TryGetPlayerView`), so this view asks the
            // registry per event/frame — the same `_viewRegistry` slot
            // `GameFeelDirector` already carries for `TryGetMobView`, not a
            // second way in.
            muzzleRefsChanged |= EditorBootstrapUtils.SetRef(muzzleSo, "_viewRegistry", viewRegistry);
            if (muzzleRefsChanged)
            {
                muzzleSo.ApplyModifiedPropertiesWithoutUndo();
                sceneDirty = true;
            }

            // Task 27 (spec §3.11, Приложение П): persistent cosmetics —
            // shell casings, impact decals, corpses, and the four pooled
            // spark/burst particle systems (muzzle flash itself is the
            // MuzzleFlashView object just above, not duplicated here; slide
            // dust is Task 22, spec Г6).
            Material casingMat = GetOrCreateMaterial(
                "CasingBrass", baseColor: new Color(0.25f, 0.16f, 0.05f), emissionColor: Color.black);
            Material corpseMat = GetOrCreateMaterial(
                "CorpseEmissive", baseColor: new Color(0.05f, 0.05f, 0.05f), emissionColor: new Color(0.1f, 0.1f, 0.1f));
            Material decalMat = GetOrCreateDecalMaterial("ScorchDecal", new Color(0.04f, 0.04f, 0.04f, 0.85f));
            Material hitSparkMat = GetOrCreateUnlitMaterial("HitSpark", new Color(3.5f, 3f, 1.6f));
            Material blockSparkMat = GetOrCreateUnlitMaterial("BlockSpark", new Color(2f, 2.3f, 3f));
            Material deathBurstMat = GetOrCreateUnlitMaterial("DeathBurst", new Color(4f, 1.3f, 0.3f));
            // Task 22: slide dust — a muted, non-HDR tan (unlike the sparks above,
            // this isn't a combat-feedback flash meant to bloom, just a
            // traversal-cosmetic puff), still an Unlit material like every
            // other burst here (same spark-pool precedent, GetOrCreateSparkPrefab
            // below).
            Material slideDustMat = GetOrCreateUnlitMaterial("SlideDust", new Color(0.55f, 0.5f, 0.4f));
            // Б1 fix-wave 2 review (app-9av): unlit quad, not a decal — see
            // GetOrCreateDecalMaterial's doc for why a decal material can't
            // glow at all. Color mirrors PlayerEmissive's accent (Э1) so the
            // mark reads as "this player's" trail, not a generic FX color.
            Material dashGlowMat = GetOrCreateUnlitMaterial("DashGlow", new Color(0f, 2.5f, 3f));
            // Task 24 (revised per app-1zf — primitives only, GibView's class
            // doc): a dark gunmetal Lit material, same non-HDR/black-emission
            // shape as CasingBrass above. T24-2 (real part meshes) demotes
            // this to a FALLBACK only — see chaserPartMat/gunnerPartMat below
            // and PersistentPropsDirector's class doc.
            Material gibMat = GetOrCreateMaterial(
                "GibMetal", baseColor: new Color(0.12f, 0.12f, 0.14f), emissionColor: Color.black);

            CasingView casingPrefab = GetOrCreateCasingPrefab(casingMat);
            DecalProjector decalPrefab = GetOrCreateDecalPrefab(decalMat, gameFeel.DecalSize);
            // Task 12: the old capsule CorpseView is no longer wired into the
            // scene (CorpseMechView replaces it) — the call is kept as a
            // bare statement so the capsule artifact stays on disk (ПБ13,
            // CorpseView class doc), no local since nothing reads it anymore.
            GetOrCreateCorpsePrefab(corpseMat);
            CorpseView corpseMechPrefab = GetOrCreateCorpseMechPrefab(
                CorpseMechPrefabPath, ChaserModelPath, GunnerModelPath,
                EliteModelPath, DirectorModelPath,
                gameFeel.ChaserVisualScale, gameFeel.GunnerVisualScale,
                gameFeel.EliteVisualScale, gameFeel.DirectorVisualScale,
                directorSkin);
            DashGlowView dashGlowPrefab = GetOrCreateDashGlowPrefab(dashGlowMat);
            GibView gibPrefab = GetOrCreateGibPrefab();

            // T24-2 (owner-approved Blender split): per-archetype gib part
            // meshes + the single material each archetype's parts all
            // share. The material is the SAME `_Ring/Materials/*_Texture.mat`
            // remap the live mechs already use (ThirdPartyImportBootstrap's
            // RemapPackMaterials, extended per its own doc to cover
            // `_Ring/Gibs/` too) — loaded here, not created, same "already
            // exists by the time StageOneSceneBootstrap runs" contract the
            // Floor/Wall/Obstacle materials above follow. `gibMat` (GibMetal)
            // is the fallback ONLY if a remap is somehow missing — should
            // never trigger in practice, since the parts FBXs share their
            // material NAME with the already-remapped live mechs.
            Mesh[] chaserParts = LoadGibParts(ChaserGibPartsPath);
            Mesh[] gunnerParts = LoadGibParts(GunnerGibPartsPath);
            Material chaserPartMat = AssetDatabase.LoadAssetAtPath<Material>(
                ThirdPartyAnimatorBootstrap.MaterialsRoot + "George_Texture.mat") ?? gibMat;
            Material gunnerPartMat = AssetDatabase.LoadAssetAtPath<Material>(
                ThirdPartyAnimatorBootstrap.MaterialsRoot + "Leela_Texture.mat") ?? gibMat;
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
            // Task 22 (spec Г6) fix-round: slide dust reuses GetOrCreateSparkPrefab
            // outright (following the same pattern as the other spark pools, per
            // the brief) — lifetime/speed/size/
            // burstCount are ALL config-sourced, same as HitSpark/BlockSpark/
            // DeathBurst's own lifetime/speed/size above (review caught an
            // earlier version of this comment misstating that split as
            // "lifetime/speed/size stay literals" — they don't, for any of the
            // four spark kinds; only coneAngle, and DeathBurst's burstCount
            // literal specifically, are the actual bootstrap-local exceptions).
            ParticleSystem slideDustPrefab = GetOrCreateSparkPrefab(SlideDustPrefabPath, "SlideDust", slideDustMat,
                lifetime: gameFeel.SlideDustLifetime, speed: gameFeel.SlideDustSpeed, size: gameFeel.SlideDustSize,
                burstCount: gameFeel.SlideDustBurstCount, coneAngle: 60f);
            // Stage 3 Task 31 (the FLASH half of what spec §3.11 asks a pickup
            // for): the cell's own pop, on the cell's own cyan.
            //
            // LITERALS RATHER THAN FOUR NEW `GameFeelConfig` FIELDS, and the
            // precedent is this file's own history: the hit/block/death spark
            // numbers were literals here too until Task 27, and they moved into
            // the SO at the exact moment the OWNER asked to retune them on a
            // playtest. A brand-new effect nobody has seen yet has nothing to
            // retune against; if the В1 playtest wants it bigger or longer,
            // these four take the same road the sparks did. Short and small on
            // purpose — a cell is a small thing being picked up, not a kill.
            ParticleSystem pickupPopPrefab = GetOrCreateSparkPrefab(PickupPopPrefabPath, "PickupPop",
                pickupMat, lifetime: 0.25f, speed: 2f, size: 0.07f, burstCount: 12, coneAngle: 90f);

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
            // Stage 2 Task 45b: the shell casing leaves the shooter's own
            // ejection port now — same registry seam the muzzle flash above
            // uses (`SpawnCasing`'s own doc).
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_viewRegistry", viewRegistry);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_casingPrefab", casingPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_decalPrefab", decalPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_corpsePrefab", corpseMechPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_dashGlowPrefab", dashGlowPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_gibPrefab", gibPrefab); // Task 24
            // T24-2.
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetObjectArray(persistentPropsSo, "_chaserParts", chaserParts);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetObjectArray(persistentPropsSo, "_gunnerParts", gunnerParts);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_chaserPartMaterial", chaserPartMat);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_gunnerPartMaterial", gunnerPartMat);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_hitSparkPrefab", hitSparkPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_blockSparkPrefab", blockSparkPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_deathBurstPrefab", deathBurstPrefab);
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_slideDustPrefab", slideDustPrefab); // Task 22
            persistentPropsRefsChanged |= EditorBootstrapUtils.SetRef(persistentPropsSo, "_pickupPopPrefab", pickupPopPrefab); // Stage 3 Task 31
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
            // Stage 2 Task 45a: the `_playerVisual` slot is gone — the doll's
            // own fan-out place is now `ViewRegistry.HandlePlayerEvent`, which
            // needs no second reference (`_viewRegistry` already wired below).
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_viewRegistry", viewRegistry);
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_deathOverlay", deathOverlay);
            routerRefsChanged |= EditorBootstrapUtils.SetRef(routerSo, "_hud", hud); // Task 22
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
                $"aim proxy layer {(aimProxyLayerChanged ? "added" : "ok")}, " +
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

        /// Task 17 (spec §3.6/§3.7, hit-zone geometry): the gunner archetype's
        /// silhouette is the taller ranged mech, not the chaser's — overrides
        /// the Task 1 zone-field block (LegsTop/BodyTop/HeadTop/*DamageMult/
        /// MuzzleHeight) that ApplyGunnerDefaults above never touches. Same
        /// first-creation/backfill-only contract as ApplyGunnerDefaults (see
        /// its own doc + the call site's gate) — never reapplied
        /// unconditionally, so an owner hand-tweak of these fields survives a
        /// re-run. `SwingLead*` is deliberately absent: the gunner archetype
        /// never melees, so it ignores swing lead entirely (A15).
        static bool ApplyGunnerZoneDefaults(MobConfig m)
        {
            bool changed = false;
            changed |= SetIfDifferent(ref m.LegsTop, 1.10f);
            changed |= SetIfDifferent(ref m.BodyTop, 2.70f);
            changed |= SetIfDifferent(ref m.HeadTop, 3.50f);
            changed |= SetIfDifferent(ref m.LegsDamageMult, 0.75f);
            changed |= SetIfDifferent(ref m.BodyDamageMult, 1.0f);
            changed |= SetIfDifferent(ref m.HeadDamageMult, 1.7f);
            changed |= SetIfDifferent(ref m.MuzzleHeight, 0.95f);
            return changed;
        }

        /// Stage 2 Task 9 scaffold (owner decision F3a), body filled by Task 16:
        /// the ONE-TIME delivery of Stage 2's sanctioned balance edits into the
        /// already-committed `.asset`s. Mirrors ApplyGunnerZoneDefaults'
        /// body — SetIfDifferent per field, so a post-delivery owner hand-tune at
        /// milestone В1 survives every later R-APPLY. Called behind the on-disk
        /// `stageTwoPending` gate above; see that call site's doc (fix-round 1,
        /// C-1) for why it reads ArenaConfig.asset's TEXT instead of the loaded
        /// object, and why that gate is the gate's PERMANENT form — and, since Task 16 committed the key, a
        /// permanently CLOSED one (spec Р120): this method has already run its
        /// one and only time which Task 16
        /// must not replace. Task 9's tripwire (a throw that fired the moment
        /// `ArenaConfig.Walls` was declared) has done its job and is gone.
        ///
        /// The VALUES are not restated here. Spec §0's two-sources discipline
        /// makes the C# field initializer the starting-balance source of truth,
        /// so a pristine `CreateInstance` of each class supplies them and this
        /// method only decides WHICH fields are sanctioned to move (spec §3.15):
        /// ArenaConfig's Radius/MaxMobs/MaxProjectiles/MaxEventsPerFrame plus the
        /// Obstacles and Walls arrays, WaveConfig's MaxMobsPerWave, and
        /// GameFeelConfig's MaxCorpses/MaxCasings/MaxDecals. Everything else on
        /// those three assets — SpawnClearance, wave pacing, every game-feel
        /// number the owner tuned across milestones Б/В — is deliberately left
        /// untouched. A literal copy of the numbers here would have been a THIRD
        /// place to keep them in sync, on top of the C# defaults and TestConfigs.
        ///
        /// `PerPlayerCountFrac` is absent on purpose: it is a NEW key, and new
        /// keys arrive through the marker mechanism (EnsureAssetHasKey on
        /// WaveConfig, added by this same task) — this method exists only for
        /// EXISTING values, which that mechanism never rewrites.
        ///
        /// The two `out` flags let the call site SetDirty all three touched
        /// assets independently (fix-round 1, I-2): MaxMobsPerWave lives on
        /// WaveConfig and the three FIFO limits on GameFeelConfig, so a single
        /// dirty flag on `arena` would silently drop them.
        static bool ApplyStageTwoBalance(ArenaConfig arena, WaveConfig wave, GameFeelConfig gameFeel,
            out bool waveChanged, out bool feelChanged)
        {
            var arenaDefaults = ScriptableObject.CreateInstance<ArenaConfig>();
            var waveDefaults = ScriptableObject.CreateInstance<WaveConfig>();
            var feelDefaults = ScriptableObject.CreateInstance<GameFeelConfig>();
            try
            {
                bool arenaChanged = false;
                arenaChanged |= SetIfDifferent(ref arena.Radius, arenaDefaults.Radius);
                arenaChanged |= SetIfDifferent(ref arena.MaxMobs, arenaDefaults.MaxMobs);
                arenaChanged |= SetIfDifferent(ref arena.MaxProjectiles, arenaDefaults.MaxProjectiles);
                arenaChanged |= SetIfDifferent(ref arena.MaxEventsPerFrame, arenaDefaults.MaxEventsPerFrame);
                arenaChanged |= SetIfDifferent(ref arena.Obstacles, arenaDefaults.Obstacles);
                arenaChanged |= SetIfDifferent(ref arena.Walls, arenaDefaults.Walls);

                waveChanged = SetIfDifferent(ref wave.MaxMobsPerWave, waveDefaults.MaxMobsPerWave);

                feelChanged = false;
                feelChanged |= SetIfDifferent(ref gameFeel.MaxCorpses, feelDefaults.MaxCorpses);
                feelChanged |= SetIfDifferent(ref gameFeel.MaxCasings, feelDefaults.MaxCasings);
                feelChanged |= SetIfDifferent(ref gameFeel.MaxDecals, feelDefaults.MaxDecals);

                return arenaChanged;
            }
            finally
            {
                Object.DestroyImmediate(arenaDefaults);
                Object.DestroyImmediate(waveDefaults);
                Object.DestroyImmediate(feelDefaults);
            }
        }

        /// Stage 3 Task 12 (spec §3.13/§3.15): the ONE-TIME delivery of Stage
        /// 3's sanctioned edits of EXISTING values into the already-committed
        /// `.asset`s — the exact role ApplyStageTwoBalance played for Stage 2,
        /// behind a gate of its own (see the call site for why the Stage 2
        /// gate could not be reused, spec Р120).
        ///
        /// The VALUES are not restated here either: a pristine
        /// `CreateInstance` of each class supplies them (spec §0 — the C#
        /// field initializer is the starting-balance source of truth), and
        /// this method only decides WHICH fields are sanctioned to move.
        /// The sanctioned list, spec §3.13's own table plus the layout of
        /// §3.15: ArenaConfig's Radius (65 -> 113), MaxMobs (96 -> 288),
        /// MaxProjectiles (384 -> 1024), MaxEventsPerFrame (512 -> 1024),
        /// PlayerSpawnRingFrac (0.8 -> 0.92) and the two layout arrays that
        /// grow with the arena (Obstacles 8 -> 20, Walls 6 -> 14);
        /// WaveConfig's MaxMobsPerWave (36 -> 72); NetConfig's
        /// MatchMaxDurationSeconds (1800 -> 900, spec Р255). NOTHING else on
        /// those three assets moves — SpawnClearance, MaxPlayers, BarrierTop,
        /// wave pacing and every network number the owner tuned across Stage
        /// 2 are deliberately left alone.
        ///
        /// The zone walls, doors, portals and container caps are absent on
        /// purpose: they are NEW keys, and new keys travel by the
        /// EnsureAssetHasKey marker mechanism, which this method never
        /// duplicates (ApplyStageTwoBalance's own PerPlayerCountFrac note).
        ///
        /// Two `out` flags for the same reason ApplyStageTwoBalance has them
        /// (fix-round 1, I-2): MaxMobsPerWave lives on WaveConfig and
        /// MatchMaxDurationSeconds on NetConfig, so one dirty flag on `arena`
        /// would silently drop both.
        static bool ApplyStageThreeBalance(ArenaConfig arena, WaveConfig wave, NetConfig net,
            out bool waveChanged, out bool netChanged)
        {
            var arenaDefaults = ScriptableObject.CreateInstance<ArenaConfig>();
            var waveDefaults = ScriptableObject.CreateInstance<WaveConfig>();
            var netDefaults = ScriptableObject.CreateInstance<NetConfig>();
            try
            {
                bool arenaChanged = false;
                arenaChanged |= SetIfDifferent(ref arena.Radius, arenaDefaults.Radius);
                arenaChanged |= SetIfDifferent(ref arena.MaxMobs, arenaDefaults.MaxMobs);
                arenaChanged |= SetIfDifferent(ref arena.MaxProjectiles, arenaDefaults.MaxProjectiles);
                arenaChanged |= SetIfDifferent(ref arena.MaxEventsPerFrame,
                    arenaDefaults.MaxEventsPerFrame);
                arenaChanged |= SetIfDifferent(ref arena.PlayerSpawnRingFrac,
                    arenaDefaults.PlayerSpawnRingFrac);
                arenaChanged |= SetIfDifferent(ref arena.Obstacles, arenaDefaults.Obstacles);
                arenaChanged |= SetIfDifferent(ref arena.Walls, arenaDefaults.Walls);

                waveChanged = SetIfDifferent(ref wave.MaxMobsPerWave, waveDefaults.MaxMobsPerWave);
                netChanged = SetIfDifferent(ref net.MatchMaxDurationSeconds,
                    netDefaults.MatchMaxDurationSeconds);

                return arenaChanged;
            }
            finally
            {
                Object.DestroyImmediate(arenaDefaults);
                Object.DestroyImmediate(waveDefaults);
                Object.DestroyImmediate(netDefaults);
            }
        }

        /// Coordinator fix-round (Ф3 review C1): rewrites all five
        /// ItemCatalog.asset records to the corrected Id numbering (1..5,
        /// not 0..4) — 0 is reserved as the container slot's own "empty"
        /// sentinel (SimulationWorld.TryTakeFromContainer), so the Tier-1
        /// record's own Id 0 made it permanently unreachable through the
        /// one take shim in the codebase. Same "own local defaults instance,
        /// SetIfDifferent, destroy in a finally" shape as
        /// ApplyStageThreeBalance above, but for a SINGLE struct array
        /// field: SetIfDifferent's own generic `T[]` overload already does
        /// element-wise comparison (EqualityComparer&lt;T&gt;.Default,
        /// ItemDef has no custom IEquatable, so this falls back to
        /// reflection-based value equality — fine for a five-record,
        /// once-per-editor-session call), so replacing the whole array in
        /// one call is simpler and no less precise than five separate
        /// per-field pokes.
        static bool ApplyItemCatalogIdShift(ItemCatalog items)
        {
            var corrected = new[]
            {
                new ItemDef { Id = 1, Tier = 1, SlotCost = 1, CreditValue = 15, Kind = ItemKind.Trophy },
                new ItemDef { Id = 2, Tier = 2, SlotCost = 2, CreditValue = 60, Kind = ItemKind.Trophy },
                new ItemDef { Id = 3, Tier = 3, SlotCost = 3, CreditValue = 200, Kind = ItemKind.Trophy },
                new ItemDef { Id = 4, Tier = 4, SlotCost = 4, CreditValue = 1000, Kind = ItemKind.Trophy },
                new ItemDef { Id = 5, Tier = 0, SlotCost = 1, CreditValue = 0, Kind = ItemKind.RepairKit },
            };
            return SetIfDifferent(ref items.Items, corrected);
        }

        /// Stage 3 Task 12 (spec §3.13/§3.3 Р214): the Elite archetype's own
        /// numbers, seeded into a brand-new MobEliteConfig.asset. Same
        /// first-creation-only contract as ApplyGunnerDefaults above, and the
        /// same reason: this is a balance sheet the owner tunes at milestone
        /// В1, not something the bootstrap keeps overwriting.
        ///
        /// Source of every number, since a reader will ask (owner decision
        /// R-75): MaxSpeed/Radius/MaxHp/ContactDamage are spec §3.13 verbatim;
        /// the hit-zone belts, the multipliers, the muzzle and the whole
        /// ranged block are the Gunner's ("по образцу ганнера", §3.13
        /// verbatim); Accel, the melee timings, separation and swing lead are
        /// the Chaser's (Р214 — "усиленный чейзер"). Two numbers are DERIVED
        /// rather than copied, and both because MobAiSystem measures
        /// center-to-center: AttackRange 1.4 keeps the Chaser's own 0.6 m of
        /// reach past its hull (1.1 - 0.5, now 0.8 + 0.6), and PreferredRange
        /// 2.5 puts the ranged hold band just OUTSIDE melee — at the Gunner's
        /// own 9 the distance dispatch (MobAiSystem: chaser inside
        /// AttackRange, gunner outside) would park the Elite at 9 m and it
        /// would never close, which is a Gunner with more HP rather than
        /// Р214's enhanced chaser.
        internal static bool ApplyEliteDefaults(MobConfig m)
        {
            bool changed = false;
            changed |= SetIfDifferent(ref m.MaxSpeed, 4.2f);
            changed |= SetIfDifferent(ref m.Accel, 30f);
            changed |= SetIfDifferent(ref m.Radius, 0.8f);
            changed |= SetIfDifferent(ref m.MaxHp, 120f);
            changed |= SetIfDifferent(ref m.ContactDamage, 25f);
            changed |= SetIfDifferent(ref m.AttackRange, 1.4f);
            changed |= SetIfDifferent(ref m.TelegraphSeconds, 0.35f);
            changed |= SetIfDifferent(ref m.AttackCooldown, 0.9f);
            changed |= SetIfDifferent(ref m.PreferredRange, 2.5f);
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
            changed |= SetIfDifferent(ref m.LegsTop, 1.10f);
            changed |= SetIfDifferent(ref m.BodyTop, 2.70f);
            changed |= SetIfDifferent(ref m.HeadTop, 3.50f);
            changed |= SetIfDifferent(ref m.LegsDamageMult, 0.75f);
            changed |= SetIfDifferent(ref m.BodyDamageMult, 1.0f);
            changed |= SetIfDifferent(ref m.HeadDamageMult, 1.7f);
            changed |= SetIfDifferent(ref m.MuzzleHeight, 0.95f);
            changed |= SetIfDifferent(ref m.SwingLeadFactor, 1.0f);
            changed |= SetIfDifferent(ref m.SwingLeadMaxMeters, 2.0f);
            return changed;
        }

        /// Stage 3 Task 12 (spec §3.13/§3.4): the Director's numbers. Spec
        /// §3.4 states the archetype outright — "Elite-профиль с числами
        /// Директора" — so this IS ApplyEliteDefaults with the five numbers
        /// §3.13 names overridden (MaxHp 2500, Radius 2.2, ContactDamage 45,
        /// MaxSpeed 3.0, TelegraphSeconds 1.1), plus the same two derived
        /// ones: AttackRange 2.8 (2.2 + the Chaser's 0.6 m of reach past its
        /// hull) and PreferredRange back at the Gunner's 9, because §3.4 gives
        /// the Director the ranged stance in as many words ("дистанционный
        /// залп на Reposition/Fire") and Р248 keeps it inside the core anyway.
        /// Calling ApplyEliteDefaults first is the point (rule 2): the shared
        /// profile has ONE home, and only the differences are written here.
        ///
        /// HeadTop stays at the Gunner's 3.50 deliberately, tall as this
        /// archetype is: Hero.MaxAimHeight is 3.8, so a taller silhouette
        /// would put the Director's head above anything a collector can aim
        /// at. Model scale (ASSETS-001 §2.3, x1.5-2) is Presentation's own
        /// number and does not touch this one.
        internal static bool ApplyDirectorDefaults(MobConfig m)
        {
            bool changed = ApplyEliteDefaults(m);
            changed |= SetIfDifferent(ref m.MaxSpeed, 3.0f);
            changed |= SetIfDifferent(ref m.Radius, 2.2f);
            changed |= SetIfDifferent(ref m.MaxHp, 2500f);
            changed |= SetIfDifferent(ref m.ContactDamage, 45f);
            changed |= SetIfDifferent(ref m.TelegraphSeconds, 1.1f);
            changed |= SetIfDifferent(ref m.AttackRange, 2.8f);
            changed |= SetIfDifferent(ref m.PreferredRange, 9f);
            return changed;
        }

        static bool SetIfDifferent(ref float field, float value)
        {
            if (field == value) return false;
            field = value;
            return true;
        }

        /// Stage 2 Task 16: five of the eight sanctioned numbers are ints — the
        /// `ref float` overload above silently would not bind to them.
        static bool SetIfDifferent(ref int field, int value)
        {
            if (field == value) return false;
            field = value;
            return true;
        }

        /// Stage 2 Task 16: the arena's Obstacles/Walls arrays. Length first,
        /// then element-wise via the struct's own value equality — an
        /// already-delivered asset must compare equal so a re-Apply stays a
        /// no-op (R-IDEM). Both failure modes are safe: a false "different"
        /// verdict assigns an equal array (no YAML delta), and a false "same"
        /// verdict cannot happen on the delivery run itself, where the lengths
        /// differ (5 -> 8 circles, 0 -> 6 walls).
        static bool SetIfDifferent<T>(ref T[] field, T[] value) where T : struct
        {
            if (field != null && field.Length == value.Length)
            {
                var comparer = System.Collections.Generic.EqualityComparer<T>.Default;
                bool same = true;
                for (int i = 0; i < value.Length && same; i++)
                    same = comparer.Equals(field[i], value[i]);
                if (same) return false;
            }
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

        /// Task 12 (assets phase B plan, spec §3.7/§3.11): the shared
        /// per-archetype mech prefab — a named `Visual` child carrying the
        /// pack model + generated Animator (`EditorBootstrapUtils.
        /// EnsureVisual`), `MobView`/`MobVisual` on the root. Guarded by
        /// `PrefabVisualsMatch` (Б11) rather than plain existence, like the
        /// SO assets/materials above once the `.prefab` exists on disk this
        /// is never re-authored UNLESS the mapping at the top of this file
        /// picks a different model — then the stale prefab is deleted and
        /// rebuilt so an owner's pair swap actually takes effect.
        /// Task 19 (spec QA7/QD1) adds `legsTop`/`bodyTop`/`headTop`/
        /// `bodyRadius`/`headRadiusFrac`: the archetype's zone-geometry SO
        /// fields, used to size the `AimProxy_*` belts (`EnsureAimProxyChildren`).
        /// The self-heal reaches BOTH paths — an already-committed prefab
        /// whose visuals already match (`SelfHealAimProxyOnPrefab`, UNDER the
        /// `PrefabVisualsMatch` early return, PC2) and a freshly-built one
        /// (inline in the `build()` closure below).
        /// Stage 3 Task 31 adds `clipFamily` and `skin`. The FAMILY is which
        /// pack's take names this model's controller carries — `MobVisual`
        /// reads it instead of the six loose mech constants it used to
        /// (`AnimIds.MobClipSet`), because the Sci-Fi kit calls its takes
        /// Attack/TurnOff where the mech pack says Punch/Shoot/Death. The SKIN
        /// is an optional material override for every renderer under `Visual`:
        /// the Director is the same `Enemy_QuadShell` mesh Elite's own kit
        /// ships, and what tells him apart at a glance — besides being three
        /// and a half times the size — is `DirectorSkin.mat`, the darkened,
        /// red-emissive clone of the pack material the owner accepted at asset
        /// milestone 3. `null` leaves the model's own materials alone, which is
        /// what all three of the other archetypes pass.
        static MobView GetOrCreateMobArchetypePrefab(string prefabPath, string modelPath,
            float visualScale, float legsTop, float bodyTop, float headTop,
            float bodyRadius, float headRadiusFrac,
            AnimIds.MobClipFamily clipFamily = AnimIds.MobClipFamily.Mech,
            Material skin = null)
        {
            if (AssetDatabase.LoadAssetAtPath<MobView>(prefabPath) != null)
            {
                if (EditorBootstrapUtils.PrefabVisualsMatch(prefabPath, ("Visual", modelPath)))
                {
                    SelfHealAimProxyOnPrefab(prefabPath, legsTop, bodyTop, headTop,
                        bodyRadius, headRadiusFrac);
                    SelfHealVisualScaleOnPrefab(prefabPath, "Visual", visualScale);
                    return AssetDatabase.LoadAssetAtPath<MobView>(prefabPath);
                }
                AssetDatabase.DeleteAsset(prefabPath); // pair swapped: rebuild; SetRef re-wires
            }
            return EditorBootstrapUtils.BuildPrefab<MobView>(prefabPath, () =>
            {
                var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(prefabPath));
                bool changed = false;
                GameObject visual = EditorBootstrapUtils.EnsureVisual(go, modelPath,
                    ThirdPartyAnimatorBootstrap.ControllerPathFor(modelPath),
                    visualScale, ref changed);
                if (skin != null) ApplySkin(visual, skin);
                go.AddComponent<MobView>();
                MobVisual mobVisual = go.AddComponent<MobVisual>();
                var so = new SerializedObject(mobVisual);
                EditorBootstrapUtils.SetRef(so, "_animator", visual.GetComponent<Animator>());
                EditorBootstrapUtils.SetRef(so, "_visual", visual.transform);
                so.FindProperty("_clipFamily").enumValueIndex = (int)clipFamily;
                so.ApplyModifiedPropertiesWithoutUndo();
                // Task 19: AimProxy_Legs/Body/Head siblings of Visual, at
                // prefab-root local space (EnsureAimProxyChildren's own doc).
                EnsureAimProxyChildren(go.transform, legsTop, bodyTop, headTop,
                    bodyRadius, headRadiusFrac);
                return go;
            });
        }

        /// Stage 2 Task 45a (spec §3.12): the collector doll prefab — the same
        /// hierarchy the scene's retired `Player` object carried
        /// (`PlayerView`/`PlayerVisual` on the root, a named `Visual` child off
        /// the UAL1 doll FBX with its generated controller, the
        /// `AimProxy_Legs/Body/Head` belts sized from `HeroConfig`'s own
        /// zone geometry, and the pistol parented into the doll's `RightHand`
        /// bone), plus the editor-only `PlayerGunTuner` that inherited the
        /// owner's PlayMode gun workflow (Р97).
        ///
        /// Guarded by `PrefabVisualsMatch` (Б11) and self-healed on the
        /// early-return path exactly like `GetOrCreateMobArchetypePrefab` above,
        /// with ONE extra self-heal of its own: the gun's local pose. That is
        /// the write-if-different reconciliation the scene block used to do on
        /// every `Apply` (audit anchor A14) — it has to keep happening, because
        /// what a BUILD ships is the pose baked into this prefab, while
        /// `PlayerGunTuner`'s live push only exists in the Editor.
        static PlayerView GetOrCreatePlayerDollPrefab(string prefabPath, float visualScale,
            float legsTop, float bodyTop, float headTop, float bodyRadius,
            float headRadiusFrac, GameFeelConfig gameFeel)
        {
            if (AssetDatabase.LoadAssetAtPath<PlayerView>(prefabPath) != null)
            {
                if (EditorBootstrapUtils.PrefabVisualsMatch(prefabPath,
                        ("Visual", ThirdPartyAssetPostprocessor.DollPath)))
                {
                    SelfHealAimProxyOnPrefab(prefabPath, legsTop, bodyTop, headTop,
                        bodyRadius, headRadiusFrac);
                    SelfHealVisualScaleOnPrefab(prefabPath, "Visual", visualScale);
                    SelfHealGunPoseOnPrefab(prefabPath, gameFeel);
                    SelfHealGunSocketsOnPrefab(prefabPath, gameFeel); // Stage 2 Task 45b
                    return AssetDatabase.LoadAssetAtPath<PlayerView>(prefabPath);
                }
                AssetDatabase.DeleteAsset(prefabPath); // doll swapped: rebuild; SetRef re-wires
            }
            return EditorBootstrapUtils.BuildPrefab<PlayerView>(prefabPath, () =>
            {
                var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(prefabPath));
                bool changed = false;
                GameObject visual = EditorBootstrapUtils.EnsureVisual(go,
                    ThirdPartyAssetPostprocessor.DollPath,
                    ThirdPartyAnimatorBootstrap.PlayerControllerPath, visualScale, ref changed);
                go.AddComponent<PlayerView>();
                PlayerVisual playerVisual = go.AddComponent<PlayerVisual>();
                var visualSo = new SerializedObject(playerVisual);
                EditorBootstrapUtils.SetRef(visualSo, "_animator", visual.GetComponent<Animator>());
                EditorBootstrapUtils.SetRef(visualSo, "_visual", visual.transform);
                visualSo.ApplyModifiedPropertiesWithoutUndo();
                // Task 19: AimProxy_Legs/Body/Head siblings of Visual, at
                // prefab-root local space (EnsureAimProxyChildren's own doc).
                // Every doll carries them now, which is what lets a player be
                // aimed AT — see AimProvider.TryAimProxy's own doc.
                EnsureAimProxyChildren(go.transform, legsTop, bodyTop, headTop,
                    bodyRadius, headRadiusFrac);

                Transform hand = visual.GetComponent<Animator>()
                    .GetBoneTransform(HumanBodyBones.RightHand);
                if (hand == null)
                    throw new System.InvalidOperationException(
                        "StageOneSceneBootstrap: doll has no RightHand bone.");
                GameObject gunModel = AssetDatabase.LoadAssetAtPath<GameObject>(GunModelPath);
                if (gunModel == null)
                    throw new System.InvalidOperationException(
                        "StageOneSceneBootstrap: no gun model at " + GunModelPath);
                var gun = (GameObject)PrefabUtility.InstantiatePrefab(gunModel);
                gun.name = GunObjectName;
                gun.transform.SetParent(hand, false);
                gun.transform.localPosition = gameFeel.GunLocalPosition;
                gun.transform.localEulerAngles = gameFeel.GunLocalEuler;
                // Stage 2 Task 45b: the barrel's mouth and the ejection port,
                // the two points every weapon cosmetic is anchored to from now
                // on. The model carries no sockets of its own (its only nodes
                // are the mesh), so they are ours to place.
                EnsureGunSockets(gun.transform, gameFeel,
                    out Transform muzzleSocket, out Transform ejectSocket);

                var viewSo = new SerializedObject(go.GetComponent<PlayerView>());
                EditorBootstrapUtils.SetRef(viewSo, "_muzzleSocket", muzzleSocket);
                EditorBootstrapUtils.SetRef(viewSo, "_ejectSocket", ejectSocket);
                viewSo.ApplyModifiedPropertiesWithoutUndo();

                PlayerGunTuner tuner = go.AddComponent<PlayerGunTuner>();
                var tunerSo = new SerializedObject(tuner);
                EditorBootstrapUtils.SetRef(tunerSo, "_gameFeel", gameFeel);
                EditorBootstrapUtils.SetRef(tunerSo, "_gun", gun.transform);
                EditorBootstrapUtils.SetRef(tunerSo, "_muzzleSocket", muzzleSocket);
                EditorBootstrapUtils.SetRef(tunerSo, "_ejectSocket", ejectSocket);
                tunerSo.ApplyModifiedPropertiesWithoutUndo();
                return go;
            });
        }

        /// Stage 2 Task 45a: the gun-pose half of the doll prefab's self-heal —
        /// the write-if-different reconciliation the scene block did on every
        /// `Apply` before the doll became a prefab, moved here unchanged in
        /// substance. Same `LoadPrefabContents`/`SaveAsPrefabAsset` shape as
        /// `SelfHealAimProxyOnPrefab`/`SelfHealVisualScaleOnPrefab` next door,
        /// and kept a separate method for the same reason those two are: one
        /// self-heal, one concern.
        static void SelfHealGunPoseOnPrefab(string prefabPath, GameFeelConfig gameFeel)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Transform gun = FindDescendant(contents.transform, GunObjectName);
                if (gun == null) return;
                bool changed = false;
                if (gun.localPosition != gameFeel.GunLocalPosition)
                {
                    gun.localPosition = gameFeel.GunLocalPosition;
                    changed = true;
                }
                // Compare ROTATIONS, not euler read-backs: localEulerAngles returns values
                // re-derived from the quaternion (normalized to [0;360)), so e.g. (0,-90,0)
                // reads back as (0,270,0) and a naive != would re-dirty the prefab on every
                // Apply (audit fix ПБ19). Writing via localEulerAngles keeps the serialized
                // euler hint consistent.
                if (Quaternion.Angle(gun.localRotation,
                        Quaternion.Euler(gameFeel.GunLocalEuler)) > 1e-3f)
                {
                    gun.localEulerAngles = gameFeel.GunLocalEuler;
                    changed = true;
                }
                if (changed) PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// Stage 2 Task 45b: the two empty sockets under the doll's `Gun` —
        /// created if missing, reconciled write-if-different otherwise, and
        /// handed back either way so the caller can wire the references that
        /// make them reachable at runtime. Same find-or-create + per-field diff
        /// shape as `EnsureAimProxyCapsule` above; returns whether anything
        /// changed, like every other `Ensure*` in this file.
        ///
        /// UNDER THE GUN, NOT UNDER THE HAND. The gun's own pose inside the hand
        /// is itself owner-tuned (`GameFeelConfig.GunLocalPosition/Euler`), so a
        /// socket parented to the hand would silently drift off the barrel the
        /// next time the grip is re-tuned. Parented to the gun, the barrel's
        /// mouth stays the barrel's mouth.
        ///
        /// ONLY THE PORT'S ROTATION IS RECONCILED. The muzzle socket is a POINT
        /// for the flash and the aim ray — nothing reads its forward axis — and
        /// writing a rotation nobody reads would give the owner a knob whose
        /// effect is invisible. `GameFeelConfig.GunEjectLocalEuler`'s own doc
        /// has the split.
        static bool EnsureGunSockets(Transform gun, GameFeelConfig gameFeel,
            out Transform muzzle, out Transform eject)
        {
            bool changed = EnsureSocketChild(gun, MuzzleSocketObjectName, out muzzle);
            changed |= EnsureSocketChild(gun, EjectSocketObjectName, out eject);
            if (muzzle.localPosition != gameFeel.GunMuzzleLocalPosition)
            {
                muzzle.localPosition = gameFeel.GunMuzzleLocalPosition;
                changed = true;
            }
            if (eject.localPosition != gameFeel.GunEjectLocalPosition)
            {
                eject.localPosition = gameFeel.GunEjectLocalPosition;
                changed = true;
            }
            // Rotations compared as rotations, never as euler read-backs — the
            // ПБ19 audit fix `SelfHealGunPoseOnPrefab` above documents in full.
            if (Quaternion.Angle(eject.localRotation,
                    Quaternion.Euler(gameFeel.GunEjectLocalEuler)) > 1e-3f)
            {
                eject.localEulerAngles = gameFeel.GunEjectLocalEuler;
                changed = true;
            }
            return changed;
        }

        static bool EnsureSocketChild(Transform gun, string childName, out Transform socket)
        {
            socket = gun.Find(childName);
            if (socket != null) return false;
            var go = new GameObject(childName);
            go.transform.SetParent(gun, false);
            socket = go.transform;
            return true;
        }

        /// Stage 2 Task 45b: the socket half of the doll prefab's self-heal.
        /// Separate from `SelfHealGunPoseOnPrefab` above for the reason this
        /// file's own convention gives — one self-heal, one concern — and it
        /// carries something that one does not: the two REFERENCES into the
        /// sockets. An already-committed prefab predates both the objects and
        /// the fields that point at them, so creating the children without
        /// rewiring `PlayerView`/`PlayerGunTuner` would leave a doll whose gun
        /// has a muzzle nothing can find.
        static void SelfHealGunSocketsOnPrefab(string prefabPath, GameFeelConfig gameFeel)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Transform gun = FindDescendant(contents.transform, GunObjectName);
                if (gun == null) return;
                bool changed = EnsureGunSockets(gun, gameFeel,
                    out Transform muzzle, out Transform eject);

                var viewSo = new SerializedObject(contents.GetComponent<PlayerView>());
                bool refsChanged = EditorBootstrapUtils.SetRef(viewSo, "_muzzleSocket", muzzle);
                refsChanged |= EditorBootstrapUtils.SetRef(viewSo, "_ejectSocket", eject);
                if (refsChanged) viewSo.ApplyModifiedPropertiesWithoutUndo();
                changed |= refsChanged;

                PlayerGunTuner tuner = contents.GetComponent<PlayerGunTuner>();
                if (tuner != null)
                {
                    var tunerSo = new SerializedObject(tuner);
                    bool tunerRefsChanged =
                        EditorBootstrapUtils.SetRef(tunerSo, "_muzzleSocket", muzzle);
                    tunerRefsChanged |= EditorBootstrapUtils.SetRef(tunerSo, "_ejectSocket", eject);
                    if (tunerRefsChanged) tunerSo.ApplyModifiedPropertiesWithoutUndo();
                    changed |= tunerRefsChanged;
                }

                if (changed) PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// Depth-first search by name — the gun hangs off a humanoid bone whose
        /// path inside the doll rig is the pack's business, not this file's, so
        /// it cannot be reached by a fixed `Transform.Find` path the way every
        /// other child in this file can.
        static Transform FindDescendant(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == name) return child;
                Transform found = FindDescendant(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// Task 12 (assets phase B plan, spec §3.7/§3.11): the shared mech
        /// corpse prefab — TWO named `Visual` children (`VisualChaser`/
        /// `VisualGunner`, each its own model/Animator via `EnsureVisual`),
        /// toggled by `CorpseView.Spawn` per the dying mob's `MobType`
        /// (`CorpseView` class doc, Б4). Same `PrefabVisualsMatch` guard as
        /// `GetOrCreateMobArchetypePrefab` above, checked against BOTH
        /// children so a swap of either half of the pair rebuilds this
        /// prefab too.
        static CorpseView GetOrCreateCorpseMechPrefab(string prefabPath,
            string chaserModelPath, string gunnerModelPath,
            string eliteModelPath, string directorModelPath,
            float chaserScale, float gunnerScale, float eliteScale, float directorScale,
            Material directorSkin)
        {
            if (AssetDatabase.LoadAssetAtPath<CorpseView>(prefabPath) != null)
            {
                if (EditorBootstrapUtils.PrefabVisualsMatch(prefabPath,
                        ("VisualChaser", chaserModelPath), ("VisualGunner", gunnerModelPath),
                        ("VisualElite", eliteModelPath), ("VisualDirector", directorModelPath)))
                    return AssetDatabase.LoadAssetAtPath<CorpseView>(prefabPath);
                AssetDatabase.DeleteAsset(prefabPath);
            }
            return EditorBootstrapUtils.BuildPrefab<CorpseView>(prefabPath, () =>
            {
                var go = new GameObject("CorpseMechView");
                bool changed = false;
                GameObject chaserVisual = EditorBootstrapUtils.EnsureVisual(go,
                    chaserModelPath, ThirdPartyAnimatorBootstrap.ControllerPathFor(chaserModelPath),
                    chaserScale, ref changed, "VisualChaser");
                GameObject gunnerVisual = EditorBootstrapUtils.EnsureVisual(go,
                    gunnerModelPath, ThirdPartyAnimatorBootstrap.ControllerPathFor(gunnerModelPath),
                    gunnerScale, ref changed, "VisualGunner");
                GameObject eliteVisual = EditorBootstrapUtils.EnsureVisual(go,
                    eliteModelPath, ThirdPartyAnimatorBootstrap.ControllerPathFor(eliteModelPath),
                    eliteScale, ref changed, "VisualElite");
                GameObject directorVisual = EditorBootstrapUtils.EnsureVisual(go,
                    directorModelPath, ThirdPartyAnimatorBootstrap.ControllerPathFor(directorModelPath),
                    directorScale, ref changed, "VisualDirector");
                // The corpse wears the same skin the live Director does — a boss
                // that changed color on death would read as a different body.
                if (directorSkin != null) ApplySkin(directorVisual, directorSkin);
                gunnerVisual.SetActive(false); // Spawn() flips per MobType
                eliteVisual.SetActive(false);
                directorVisual.SetActive(false);
                CorpseView view = go.AddComponent<CorpseView>();
                var so = new SerializedObject(view);
                EditorBootstrapUtils.SetRef(so, "_chaserVisual", chaserVisual);
                EditorBootstrapUtils.SetRef(so, "_gunnerVisual", gunnerVisual);
                EditorBootstrapUtils.SetRef(so, "_eliteVisual", eliteVisual);
                EditorBootstrapUtils.SetRef(so, "_directorVisual", directorVisual);
                EditorBootstrapUtils.SetRef(so, "_chaserAnimator", chaserVisual.GetComponent<Animator>());
                EditorBootstrapUtils.SetRef(so, "_gunnerAnimator", gunnerVisual.GetComponent<Animator>());
                EditorBootstrapUtils.SetRef(so, "_eliteAnimator", eliteVisual.GetComponent<Animator>());
                EditorBootstrapUtils.SetRef(so, "_directorAnimator", directorVisual.GetComponent<Animator>());
                so.FindProperty("_chaserClips").enumValueIndex = (int)AnimIds.MobClipFamily.Mech;
                so.FindProperty("_gunnerClips").enumValueIndex = (int)AnimIds.MobClipFamily.Mech;
                so.FindProperty("_eliteClips").enumValueIndex = (int)AnimIds.MobClipFamily.SciFiEnemy;
                so.FindProperty("_directorClips").enumValueIndex = (int)AnimIds.MobClipFamily.SciFiEnemy;
                so.ApplyModifiedPropertiesWithoutUndo();
                return go;
            });
        }

        /// Overwrites every material slot under a built `Visual` with one
        /// shared material (Stage 3 Task 31) — the Director's own skin. Mirrors
        /// what `AssetPreviewSceneBootstrap` does to its Director stub, which
        /// is where the owner accepted the look; `sharedMaterials` is rewritten
        /// slot for slot rather than assigned once, since a pack model may
        /// carry more than one slot even when every slot holds the same
        /// material.
        static void ApplySkin(GameObject visual, Material skin)
        {
            foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                var slots = new Material[renderer.sharedMaterials.Length];
                for (int i = 0; i < slots.Length; i++) slots[i] = skin;
                renderer.sharedMaterials = slots;
            }
        }

        /// Task 17: the shared `ProjectileView` prefab — a small emissive sphere
        /// (~0.24 diameter at rest, in the Inspector/preview only) plus a
        /// `TrailRenderer` tracer. `TrailRenderer.time` is only seeded here from
        /// the `GameFeelConfig` value at bootstrap time; `ProjectileView.Bind`
        /// re-applies it live every spawn so PlayMode hot-tweaking
        /// `TracerFadeSeconds` (spec §3.9) still takes effect — В3 fix-wave 2
        /// (item 1) extends that same live-rebind treatment to the sphere's own
        /// `transform.localScale`, off this shot's real `ProjectileState.Radius`
        /// (`ProjectileBallScale`'s own `GameFeelConfig` doc), so the bare
        /// `ProjectileDiameter` baked here is only ever a cold placeholder.
        /// Existence-guarded the same way as the mech/corpse prefabs above.
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
        /// Stage 3 Task 31 (spec §3.11): the cell on the floor — an emissive
        /// sphere, built the same way `GetOrCreateProjectilePrefab` builds its
        /// own ball, with the model on a `Visual` child so `PickupView.Bind`
        /// can scale the picture without touching the pooled root's own
        /// transform (the root is what `ViewRegistry` positions).
        static PickupView GetOrCreatePickupPrefab(Material cellMat, float diameter)
        {
            return EditorBootstrapUtils.BuildPrefab<PickupView>(PickupPrefabPath, () =>
            {
                var go = new GameObject("PickupView");
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "Visual";
                EditorBootstrapUtils.RemoveCollider(sphere);
                sphere.transform.SetParent(go.transform, false);
                sphere.transform.localScale = Vector3.one * diameter;
                sphere.GetComponent<MeshRenderer>().sharedMaterial = cellMat;

                PickupView view = go.AddComponent<PickupView>();
                var so = new SerializedObject(view);
                EditorBootstrapUtils.SetRef(so, "_visual", sphere.transform);
                so.ApplyModifiedPropertiesWithoutUndo();
                return go;
            });
        }

        /// One container prefab: a pack prop (or, for the corpse marker, a
        /// primitive) on a `Visual` child, `ContainerView` on the root. Same
        /// `PrefabVisualsMatch` guard the mob archetypes use, so swapping a
        /// model above rebuilds the prefab rather than leaving a stale one.
        /// `modelPath` null builds the MARKER variant: an emissive sphere, the
        /// "there is something on this body worth taking" tell spec §3.11 asks
        /// for over a corpse that is already on the floor.
        static ContainerView GetOrCreateContainerPrefab(string prefabPath, string modelPath,
            Material markerMat, float scale)
        {
            if (AssetDatabase.LoadAssetAtPath<ContainerView>(prefabPath) != null)
            {
                if (modelPath == null
                    || EditorBootstrapUtils.PrefabVisualsMatch(prefabPath, ("Visual", modelPath)))
                    return AssetDatabase.LoadAssetAtPath<ContainerView>(prefabPath);
                AssetDatabase.DeleteAsset(prefabPath);
            }
            return EditorBootstrapUtils.BuildPrefab<ContainerView>(prefabPath, () =>
            {
                var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(prefabPath));
                GameObject visual;
                if (modelPath == null)
                {
                    visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    visual.name = "Visual";
                    EditorBootstrapUtils.RemoveCollider(visual);
                    visual.transform.SetParent(go.transform, false);
                    visual.GetComponent<MeshRenderer>().sharedMaterial = markerMat;
                    visual.transform.localScale = Vector3.one * scale;
                }
                else
                {
                    bool changed = false;
                    // No controller: these props carry no takes at all (they are
                    // static furniture), and `DefaultControllerFor` answers null
                    // for exactly that case.
                    visual = EditorBootstrapUtils.EnsureVisual(go, modelPath,
                        EditorBootstrapUtils.DefaultControllerFor(modelPath), scale, ref changed);
                }

                ContainerView view = go.AddComponent<ContainerView>();
                var so = new SerializedObject(view);
                EditorBootstrapUtils.SetRef(so, "_visual", visual.transform);
                so.ApplyModifiedPropertiesWithoutUndo();
                return go;
            });
        }

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
        /// `ProjectSettings/TagManager.asset`. Task 19 (QC14) extracted the
        /// actual claim/refuse logic into the shared `EnsureUserLayer` below —
        /// this is now a thin wrapper so both call sites (this one, and
        /// `EnsureAimProxyLayer`) share one idempotent implementation instead
        /// of two near-identical copies.
        static bool EnsureCasingsLayer() =>
            EnsureUserLayer(PersistentPropsDirector.CasingsLayer, CasingsLayerName);

        /// Task 19 (spec QA7/QD1): claims user layer 10 as "AimProxy" —
        /// verified empty before this task claimed it (`grep` against the
        /// committed `TagManager.asset`; layer 9 already belongs to
        /// `CasingsLayerName` above). Second `EnsureUserLayer` call site.
        static bool EnsureAimProxyLayer() =>
            EnsureUserLayer(AimProvider.AimProxyLayer, AimProxyLayerName);

        /// Task 19 (QC14): the shared TagManager-layer-claim logic — was
        /// `EnsureCasingsLayer`'s entire body alone (Task 27 review
        /// fix-round), generalized so `EnsureAimProxyLayer` above reuses the
        /// exact same sequence instead of a second copy. Standard
        /// editor-script recipe for programmatically naming a layer
        /// (`SerializedObject` over the `layers` string array, no dedicated
        /// public API exists). Idempotent: a second run sees `name` already
        /// in the slot and no-ops. Defensively throws instead of silently
        /// overwriting if `slot` ever ends up holding some OTHER name (e.g. a
        /// teammate claims it for something else first) — same "hard error on
        /// unexpected setup state" policy as `LoadMaterial`/`LoadAudioClip`
        /// below. Returns whether it actually changed anything.
        static bool EnsureUserLayer(int slot, string name)
        {
            Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
            if (tagManagerAssets == null || tagManagerAssets.Length == 0)
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: no asset at '{TagManagerPath}'.");

            var so = new SerializedObject(tagManagerAssets[0]);
            SerializedProperty layers = so.FindProperty("layers");
            SerializedProperty slotProp = layers.GetArrayElementAtIndex(slot);

            if (slotProp.stringValue == name) return false;
            if (!string.IsNullOrEmpty(slotProp.stringValue))
                throw new System.InvalidOperationException(
                    $"StageOneSceneBootstrap: layer {slot} is already named " +
                    $"'{slotProp.stringValue}' — refusing to overwrite it with '{name}'.");

            slotProp.stringValue = name;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            return true;
        }

        /// Task 19 (spec QA7/QD1): three `CapsuleCollider` triggers on
        /// `AimProvider.AimProxyLayer` — Legs/Body/Head belts sized from the
        /// archetype's own zone-geometry SO fields (`LegsTop`/`BodyTop`/
        /// `HeadTop`, the exact numbers `SimulationWorld`'s zone-damage
        /// lookup already keys off — this proxy is a SEPARATE, purely-visual
        /// raycast target that Simulation never touches; "no sim changes").
        /// Head radius is scaled by `GameFeelConfig.AimProxyHeadRadiusFrac`
        /// for a narrower headshot volume than the body/legs. Attached at
        /// ROOT local space, unscaled — same convention the zone-geometry
        /// fields themselves assume (mirrors `Visual`'s own un-scaled
        /// parent). Idempotent per-field diff, like every other self-heal in
        /// this file. Returns whether anything changed.
        static bool EnsureAimProxyChildren(Transform root, float legsTop, float bodyTop,
            float headTop, float bodyRadius, float headRadiusFrac)
        {
            bool changed = false;
            changed |= EnsureAimProxyCapsule(root, "AimProxy_Legs", 0f, legsTop, bodyRadius);
            changed |= EnsureAimProxyCapsule(root, "AimProxy_Body", legsTop, bodyTop, bodyRadius);
            changed |= EnsureAimProxyCapsule(root, "AimProxy_Head", bodyTop, headTop,
                bodyRadius * headRadiusFrac);
            return changed;
        }

        static bool EnsureAimProxyCapsule(Transform root, string childName, float bottom,
            float top, float radius)
        {
            bool changed = false;
            Transform tf = root.Find(childName);
            GameObject go;
            if (tf == null)
            {
                go = new GameObject(childName);
                go.transform.SetParent(root, false);
                changed = true;
            }
            else
            {
                go = tf.gameObject;
            }
            if (go.layer != AimProvider.AimProxyLayer)
            {
                go.layer = AimProvider.AimProxyLayer;
                changed = true;
            }
            CapsuleCollider capsule = go.GetComponent<CapsuleCollider>();
            if (capsule == null)
            {
                capsule = go.AddComponent<CapsuleCollider>();
                changed = true;
            }
            if (!capsule.isTrigger)
            {
                capsule.isTrigger = true;
                changed = true;
            }
            float height = Mathf.Max(top - bottom, 0.01f);
            var center = new Vector3(0f, bottom + height * 0.5f, 0f);
            if (!Mathf.Approximately(capsule.height, height))
            {
                capsule.height = height;
                changed = true;
            }
            if (capsule.center != center)
            {
                capsule.center = center;
                changed = true;
            }
            if (!Mathf.Approximately(capsule.radius, radius))
            {
                capsule.radius = radius;
                changed = true;
            }
            return changed;
        }

        /// Task 19 (self-heal idiom PC2 — mirrors `GetOrCreateCasingPrefab`'s
        /// unconditional layer self-heal): reopens an ALREADY-COMMITTED
        /// prefab asset and patches its `AimProxy_*` children in place. This
        /// is what makes the self-heal reach a prefab built by an earlier
        /// task/commit — `GetOrCreateMobArchetypePrefab`'s own `build()`
        /// closure handles the fresh-build case inline instead. Same
        /// `LoadPrefabContents`/`SaveAsPrefabAsset`/`UnloadPrefabContents`
        /// shape as `EditorBootstrapUtils.PrefabVisualsMatch`.
        static void SelfHealAimProxyOnPrefab(string prefabPath, float legsTop, float bodyTop,
            float headTop, float bodyRadius, float headRadiusFrac)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                bool changed = EnsureAimProxyChildren(contents.transform,
                    legsTop, bodyTop, headTop, bodyRadius, headRadiusFrac);
                if (changed) PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// В1 fix-wave 1 chore (owner's sanctioned milestone number,
        /// `GunnerVisualScale` 0.4→0.76): `EditorBootstrapUtils.EnsureVisual`'s
        /// own scale self-heal (its `localScale != Vector3.one * visualScale`
        /// check) only ever runs when the "Visual" child branch inside it
        /// actually executes — but `GetOrCreateMobArchetypePrefab`'s
        /// early-return path (prefab already on disk, model path unchanged)
        /// never calls `EnsureVisual` at all, so a pure `ChaserVisualScale`/
        /// `GunnerVisualScale` retune alone never reached an already-committed
        /// prefab before this. Same `LoadPrefabContents`/`SaveAsPrefabAsset`
        /// shape as `SelfHealAimProxyOnPrefab` (called alongside it from the
        /// same early-return branch) — kept as its own method rather than
        /// folded into that one so each self-heal stays independently
        /// testable/readable, same split the rest of this file's per-concern
        /// self-heal helpers already follow.
        static void SelfHealVisualScaleOnPrefab(string prefabPath, string childName, float visualScale)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Transform child = contents.transform.Find(childName);
                Vector3 target = Vector3.one * visualScale;
                if (child != null && child.localScale != target)
                {
                    child.localScale = target;
                    PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
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

        /// Task 24 (revised per app-1zf's investigation — George/Leela were
        /// monolithic skinned meshes with no separable sub-mesh, so gibs
        /// shipped as PRIMITIVES ONLY: two colliderless children,
        /// `GibBox`/`GibCapsule`, toggled at random by `GibView.Spawn`).
        /// T24-2 (owner-approved Blender split) replaces that pair with a
        /// SINGLE object carrying `MeshFilter`/`MeshRenderer` — `GibView.
        /// Spawn` now swaps in the actual part mesh/material every call
        /// instead of picking a primitive shape (that class's doc has the
        /// full story). The ROOT still carries the actual physics — a
        /// `SphereCollider` (`GibView.Spawn` resizes/recenters it from the
        /// swapped-in mesh's own `bounds` every call — no `MeshCollider`,
        /// task brief) + `Rigidbody` — on `PersistentPropsDirector.
        /// CasingsLayer` (Task 27 review fix-round, self-collision already
        /// isolated in `PersistentPropsDirector.Awake`), unchanged from
        /// Task 24. Self-heals an already-committed Task 24 `Gib.prefab`:
        /// its old `GibBox`/`GibCapsule` shape has no `MeshFilter` on the
        /// root, which is what this factory checks for before deciding to
        /// delete-and-rebuild — same "structural shape changed" guard
        /// `GetOrCreateMobArchetypePrefab`'s `PrefabVisualsMatch` uses,
        /// simplified to a single component-presence check since there's no
        /// per-model source path to compare here.
        static GibView GetOrCreateGibPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GibView>(GibPrefabPath);
            if (existing != null && existing.GetComponent<MeshFilter>() == null)
            {
                AssetDatabase.DeleteAsset(GibPrefabPath);
                existing = null;
            }
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

            return EditorBootstrapUtils.BuildPrefab<GibView>(GibPrefabPath, () =>
            {
                var go = new GameObject("Gib");
                go.layer = PersistentPropsDirector.CasingsLayer;

                MeshFilter meshFilter = go.AddComponent<MeshFilter>();
                MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();

                SphereCollider collider = go.AddComponent<SphereCollider>();
                collider.radius = 0.08f; // resized per-spawn from the swapped mesh's own bounds (GibView.Spawn)
                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.mass = 0.35f;
                rb.linearDamping = 0.15f;
                rb.angularDamping = 0.3f;

                GibView view = go.AddComponent<GibView>();
                var so = new SerializedObject(view);
                EditorBootstrapUtils.SetRef(so, "_meshFilter", meshFilter);
                EditorBootstrapUtils.SetRef(so, "_meshRenderer", meshRenderer);
                so.ApplyModifiedPropertiesWithoutUndo();
                return go;
            });
        }

        /// T24-2: loads a gib parts FBX's own `Mesh` sub-assets — one per
        /// capped-cut body part (`George_Parts.fbx`: Head/ArmL/ArmR/LegL/
        /// LegR/Torso; `Leela_Parts.fbx`: Head/LegL/LegR/Torso). Order is
        /// whatever `LoadAllAssetsAtPath` returns — callers never assume a
        /// position, only `GibView.ClassifyPart(mesh.name)`'s own kind
        /// (`PersistentPropsDirector.PartHeight`/`SpawnFullExplodeGibs`).
        /// Throws (same "hard error on unexpected setup state" policy as
        /// `LoadMaterial`/`LoadAudioClip` below) if the FBX yields no
        /// meshes at all, or no `Head` part — `SpawnFullExplodeGibs`'s
        /// headshot directional-impulse special case (В3 fix-wave 1, item 2)
        /// depends on every archetype shipping exactly one.
        static Mesh[] LoadGibParts(string fbxPath)
        {
            Mesh[] parts = AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                .OfType<Mesh>().ToArray();
            if (parts.Length == 0)
                throw new System.InvalidOperationException(
                    "StageOneSceneBootstrap: no Mesh sub-assets at " + fbxPath);
            if (!parts.Any(m => GibView.ClassifyPart(m.name) == GibPartKind.Head))
                throw new System.InvalidOperationException(
                    "StageOneSceneBootstrap: no Head part among gib meshes at " + fbxPath);
            Debug.Log($"[StageOneSceneBootstrap] {fbxPath}: {parts.Length} gib parts");
            return parts;
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

        /// Task 27: existence-guarded factory for the four pooled spark/burst
        /// particle prefabs (hit-spark, block-spark, death-burst, and — Task 22,
        /// spec Г6 — slide-dust) —
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
                // В1 fix-wave 1: self-heals an already-committed bar's stale
                // anchoredPosition (StaminaBar sliding up into the retired
                // DashBar's old slot, item 1) the same unconditional way the
                // fillSprite check right below self-heals — a caller-side
                // layout constant change alone wouldn't otherwise reach a bar
                // object that already exists in the committed scene.
                var existingRect = (RectTransform)existing;
                if (existingRect.anchoredPosition != anchoredPos)
                {
                    existingRect.anchoredPosition = anchoredPos;
                    sceneDirty = true;
                }
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

        /// The wave-number label (Task 14), top-right anchored.
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

            return GetOrCreateHudLabel(parent, WaveTextObjectName, "ВОЛНА 0",
                anchor: new Vector2(1f, 1f), anchoredPos: new Vector2(-24f, -24f),
                size: new Vector2(240f, 40f), fontSize: 28f,
                alignment: TextAlignmentOptions.TopRight, ref sceneDirty);
        }

        /// A `TextMeshProUGUI` label directly on the HUD canvas, anchored to one
        /// of its corners or edges (Stage 2 Task 47b lifted this out of
        /// `GetOrCreateWaveText`, which was the only such label until the
        /// spectate line joined it — a second hand-written copy of the same
        /// eleven lines is exactly the duplication AGENT.md rule 2 forbids).
        /// Distinct from `GetOrCreateOverlayText` above, which anchors to the
        /// CENTER of a modal panel and stacks by pixel offset.
        ///
        /// `anchor` sets `anchorMin`, `anchorMax` and `pivot` together: a HUD
        /// label is a point on the screen with text hanging off it, so the three
        /// are never anything but equal here, and passing one vector is what
        /// keeps a caller from getting two of them right and one wrong.
        ///
        /// TMP Essential Resources are vendored at `Assets/TextMesh Pro/` (see
        /// class doc), so a plain `AddComponent<TextMeshProUGUI>()` resolves
        /// `TMP_Settings.defaultFontAsset` on its own — no explicit font wiring
        /// needed here. Existence-guarded like everything else in this file.
        static TMP_Text GetOrCreateHudLabel(Transform parent, string name, string defaultText,
            Vector2 anchor, Vector2 anchoredPos, Vector2 size, float fontSize,
            TextAlignmentOptions alignment, ref bool sceneDirty)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing.GetComponent<TMP_Text>();

            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = defaultText;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
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

        // FindRunner lived here until Stage 2 Task 44e; it is
        // `EditorBootstrapUtils.FindComponentInScene<SimulationRunner>` now,
        // because StageTwoSceneBootstrap needs the very same runner to wire
        // the client's network bootstrap to.

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
