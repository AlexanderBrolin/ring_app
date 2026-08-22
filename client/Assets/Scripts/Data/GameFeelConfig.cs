using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Data
{
    /// Presentation-only game-feel numbers (hitstop, screen shake, VFX/SFX pooling).
    /// Never consumed by SimConfigBuilder / Ring.Simulation — purely client feel,
    /// hot-tweakable in PlayMode.
    [CreateAssetMenu(menuName = "Ring/Game Feel Config", fileName = "GameFeelConfig")]
    public sealed class GameFeelConfig : ScriptableObject
    {
        public enum HitstopScopeMode { TargetOnly, FullFrame }

        [Range(0f, 0.2f)] public float HitstopSeconds = 0.04f;
        public HitstopScopeMode HitstopScope = HitstopScopeMode.FullFrame;
        [Range(0f, 1f)] public float MaxHitstopRatio = 0.35f;
        [Range(0f, 0.5f)] public float HitstopCatchUpSeconds = 0.05f;
        [Range(0f, 0.5f)] public float FlashDuration = 0.08f;
        [Range(0f, 1f)] public float TraumaHit = 0.2f;
        [Range(0f, 1f)] public float TraumaDeath = 0.35f;
        [Range(0f, 1f)] public float TraumaPlayerHit = 0.45f;
        [Range(0f, 5f)] public float TraumaDecayPerSec = 1.2f;
        [Range(0f, 2f)] public float ShakeAmplitude = 0.35f;
        [Range(1f, 60f)] public float ShakeFrequency = 22f;
        [Range(0f, 1f)] public float PitchRange = 0.12f;
        [Range(0f, 2f)] public float TracerFadeSeconds = 0.4f;
        [Range(0f, 5f)] public float CasingPhysicsSeconds = 1.5f;
        // Stage 2 Task 16 (spec §3.4, Р17/Р92): persistent-prop FIFO limits
        // rescaled for a 65 m arena with three players and MaxMobs 96 — at
        // MaxCorpses 64 the first wave alone would already start evicting.
        // These are the C# side of the two-sources discipline (§0): the same
        // three numbers ship into GameFeelConfig.asset via
        // StageOneSceneBootstrap.ApplyStageTwoBalance, so no new .asset-vs-C#
        // divergence is created.
        [Range(1, 4096)] public int MaxCasings = 3072;
        [Range(1, 4096)] public int MaxDecals = 1536;
        [Range(1, 512)] public int MaxCorpses = 128;
        [Range(1, 32)] public int VoicesPerSfx = 6;
        [Range(0f, 1f)] public float MinSfxInterval = 0.03f;

        // Task 27 fix-round (review): feel numbers the owner will hot-tweak on
        // the milestone-4 playtest, pulled out of PersistentPropsDirector/
        // CorpseView/StageOneSceneBootstrap literals per client/CLAUDE.md's
        // "все числа game feel — в ScriptableObjects" rule. Structural spawn
        // offsets (lateral/vertical spawn nudges, decal near-offset/height —
        // positioning epsilons, not feel) deliberately stay as code constants,
        // same split the rest of this file already makes (e.g. `MobOffset` in
        // ViewRegistry is also never an SO field; ViewRegistry's own former
        // `ProjectileOffset` constant was Task 21's approximate-height guess,
        // removed once the projectile's real simulated `Height`/`PrevHeight`
        // — Task 4 — became available to read instead).
        [Range(0f, 10f)] public float CasingImpulseUpMin = 1.2f;
        [Range(0f, 10f)] public float CasingImpulseUpMax = 2.2f;
        [Range(0f, 30f)] public float CasingTorqueScale = 12f; // rad/s via VelocityChange since app-xjz
        [Range(0.1f, 20f)] public float CorpseGlowFadeSeconds = 3f;
        [Range(0.05f, 3f)] public float DecalSize = 0.6f;
        [Range(0.01f, 2f)] public float HitSparkLifetime = 0.15f;
        [Range(0f, 20f)] public float HitSparkSpeed = 3.5f;
        [Range(0f, 2f)] public float HitSparkSize = 0.06f;
        [Range(0.01f, 2f)] public float BlockSparkLifetime = 0.18f;
        [Range(0f, 20f)] public float BlockSparkSpeed = 3f;
        [Range(0f, 2f)] public float BlockSparkSize = 0.07f;
        [Range(0.01f, 2f)] public float DeathBurstLifetime = 0.3f;
        [Range(0f, 20f)] public float DeathBurstSpeed = 4f;
        [Range(0f, 2f)] public float DeathBurstSize = 0.12f;

        // Milestone-4 DoD iteration (owner playtest feedback: "немного больше
        // искр при попадании снаряда по ботам/стенам") — burstCount was still
        // a bootstrap-local literal (fix-round doc explicitly called out
        // density/count as a deliberate non-SO technical constant); the owner
        // asking to retune it makes it a feel number after all, same
        // precedent as the fix-round's lifetime/speed/size migration.
        // Defaults are the previous literals × ~1.5 ("немного больше"):
        // HitSpark 10→15, BlockSpark 12→18. DeathBurst (24) untouched — no
        // complaint about it. Baked into the prefab at bootstrap-creation
        // time, same as HitSparkLifetime/etc. above — StageOneSceneBootstrap's
        // GetOrCreateSparkPrefab self-heals an already-committed prefab
        // whose baked burst count no longer matches this field.
        // В3 fix-wave 2 (item 2, sanctioned QB9 chore, owner playtest feedback:
        // "мало искр при попадании по роботам") — GameFeelConfig.asset's
        // HitSparkBurstCount hand-edited 15→30 (this C# initializer, like
        // CasingScale's own, is intentionally NOT kept in sync with the tuned
        // .asset — GameFeelConfig is never consumed by SimConfigBuilder, class
        // doc above, so there is no TestConfigs-mirroring reason to touch it;
        // the .asset is the sole source of truth an owner playtest actually
        // exercises). BlockSparkBurstCount/DeathBurst untouched — no complaint
        // about either.
        [Range(1, 64)] public int HitSparkBurstCount = 15;
        [Range(1, 64)] public int BlockSparkBurstCount = 18;

        public bool ImmediateMuzzleFeedback = true;

        // Assets phase B (spec §3.7): character-visual numbers. Scale fields are
        // bind-time (re-run the bootstrap / rebuild prefabs to apply); the rest are
        // read per frame — live hot-tweak. GunLocal* are reconciled write-if-different
        // by the bootstrap on every Apply. GunLocalEuler was the sync-marker key
        // (bootstrap:245) until the Б1 fix-wave-2 block below superseded it, and
        // `DashGlowSize` in turn until the Б1 fix-wave-3 field below superseded
        // THAT, and `CasingEjectSpeedMax` after that, then `AimDotScale`
        // (Task 17), then `SlideDustSize` (Task 22, spec Г6), then
        // `LinkWindowFlashBoost` (В1 fix-wave 1), then `AimHoverGlowBoost`
        // (В1/В2 fix-wave 2), then `AimRayHeadAlphaBoost` (В3 fix-wave 1), then
        // `HeadHoverPulseAmp` (В3 fix-wave 2), then `RemotePlayerEmission`
        // (Stage 2 Task 45a) — `GunEjectLocalEuler` is the current marker
        // (Stage 2 Task 45b), see its own doc.
        [Range(0.1f, 3f)] public float PlayerVisualScale = 1f;
        [Range(0.05f, 2f)] public float ChaserVisualScale = 0.4f;
        // I5 reviewer rec #5 (final review wave, app-n6g): this scale is NOT
        // auto-coupled to MobGunnerConfig's zone-top geometry (LegsTop/
        // BodyTop/HeadTop) or the AimProxy_Legs/Body/Head belts sized from
        // those tops (StageOneSceneBootstrap.EnsureAimProxyChildren) — a
        // retune of one alone silently desyncs the visible model from its
        // hittable silhouette. A GunnerVisualScale change must move both.
        [Range(0.05f, 2f)] public float GunnerVisualScale = 0.4f;
        [Range(0f, 0.5f)] public float SpeedDampTime = 0.1f;
        [Range(0f, 1f)] public float PlayerMoveThreshold01 = 0.05f;
        [Range(0f, 1440f)] public float VisualTurnDegPerSec = 720f;
        [Range(0f, 1440f)] public float IdleAimTurnDegPerSec = 180f;
        [Range(0f, 1440f)] public float MobTurnDegPerSec = 540f;
        [Range(-180f, 180f)] public float PlayerYawOffsetDeg = 180f;
        [Range(-180f, 180f)] public float MechYawOffsetDeg = 0f;
        [Range(0f, 5f)] public float MobWalkEnterSpeed = 0.4f;
        [Range(0f, 5f)] public float MobWalkExitSpeed = 0.2f;
        [Range(0f, 10f)] public float MobRunEnterSpeed = 2.6f;
        [Range(0f, 10f)] public float MobRunExitSpeed = 2.2f;
        [Range(0f, 1f)] public float LocomotionHoldSeconds = 0.15f;
        [Range(0f, 90f)] public float AimYawClampDeg = 80f;
        [Range(0f, 1f)] public float SpineYawShare = 0.4f;
        [Range(0f, 45f)] public float DashLeanDeg = 18f;
        [Range(0.01f, 0.5f)] public float DashLeanInOutSeconds = 0.08f;
        [Range(0f, 0.5f)] public float LocomotionCrossFadeSeconds = 0.12f;
        [Range(0f, 0.3f)] public float OneShotCrossFadeSeconds = 0.06f;
        // Deprecated, not read (Task 21, PB11): every former consumer
        // (MuzzleFlashView's prediction/player-branch burst,
        // PersistentPropsDirector.SpawnCasing) switched to
        // SimulationRunner.RenderMuzzleHeight — the sim's own slide-aware
        // muzzle height (PC7) — instead of this flat guessed lift. Field kept
        // rather than deleted so an already-authored .asset doesn't silently
        // lose a serialized value; no code path reads it anymore.
        //
        // AND THOSE CONSUMERS MOVED AGAIN, off RenderMuzzleHeight too (Stage 2
        // Task 45b): the flash and the brass now leave sockets on the doll's
        // gun, at whatever height the animated hand holds it, so neither this
        // field nor the property that replaced it describes them any more.
        // RenderMuzzleHeight itself sat with zero readers between that task and
        // Stage 2 Task 45c, which gave it one of a different kind — AimProvider
        // asks where the SIMULATION's round leaves from in order to work out
        // where it lands (app-bej). Recorded here because "this field's
        // successor" is the only thing this paragraph is for, and a successor
        // that had stopped being read would have made it a dead pointer to a
        // dead pointer.
        [Range(0f, 2f)] public float MuzzleLiftY = 1.1f;
        public Vector3 GunLocalPosition = Vector3.zero;
        public Vector3 GunLocalEuler = Vector3.zero;

        // Б1 milestone owner request (app-9av): a glowing floor mark at the dash
        // start point, fading out over a few seconds. MaxDashGlows is a pool cap
        // (dash cooldown 1.2s vs ~2.5s life → 3 alive typ.). DashGlowSize was the
        // bootstrap sync-marker key until the Б1 fix-wave-3 field below
        // superseded it, then `CasingEjectSpeedMax`, then `AimDotScale`
        // (Task 17), then `SlideDustSize` (Task 22, spec Г6), then
        // `LinkWindowFlashBoost` (В1 fix-wave 1), then `AimHoverGlowBoost`
        // (В1/В2 fix-wave 2), then `AimRayHeadAlphaBoost` (В3 fix-wave 1), then
        // `HeadHoverPulseAmp` (В3 fix-wave 2), then `RemotePlayerEmission`
        // (Stage 2 Task 45a) — `GunEjectLocalEuler` is the current marker
        // (Stage 2 Task 45b), see its own doc.
        [Range(1, 32)] public int MaxDashGlows = 8;
        [Range(0.1f, 10f)] public float DashGlowSeconds = 2.5f;
        [Range(0.1f, 3f)] public float DashGlowSize = 0.9f;

        // Б1 fix-wave 3 (owner playtest feedback: "гильзы не видно — мелкие и
        // тёмные") — the baked-prefab casing scale (0.05/0.06/0.05, Task 27)
        // is no longer the last word: `CasingView.Spawn` now takes a live
        // scale read from here every shot, same hot-tweak contract as
        // `CasingPhysicsSeconds`.
        [Range(0.02f, 0.4f)] public float CasingScale = 0.12f;

        // Б1 fix-wave 5 (app-xjz): replaces the old CasingImpulseSideMax
        // random left/right scatter — CasingView.Spawn now switched from
        // ForceMode.Impulse to ForceMode.VelocityChange (see its own doc),
        // so these are direct meters-per-second along a *directed* eject
        // vector rather than an undirected impulse.
        //
        // WHICH DIRECTION THAT IS CHANGED IN STAGE 2 TASK 45b, and this doc
        // named the old one until Task 45c: the shell no longer leaves "to the
        // shooter's right of the shot" (the shot direction rotated -90°, a
        // guess about a pistol derived from where the ROUND went). It leaves
        // along the ejection-port socket's own forward axis, flattened to
        // horizontal — GunEjectLocalEuler below is what aims it, and
        // CasingImpulseUpMin/Max above supply the vertical part these two
        // fields deliberately do not (PersistentPropsDirector.SpawnCasing's own
        // doc has the reason the axis is flattened rather than used whole).
        [Range(0f, 5f)] public float CasingEjectSpeedMin = 0.8f;
        [Range(0f, 5f)] public float CasingEjectSpeedMax = 1.4f;

        // Task 17 (combat-depth Г5, spec §3.5 as corrected — QC3/QC4/QD12):
        // game-feel numbers for the combat-depth Presentation work later in
        // this phase (tracer visuals, slide dust, stamina bar, headshot
        // hitstop/pitch, gib pooling, aim-proxy ray). Consumers wired
        // incrementally as Г5 progressed, not all at once: `AimProxyHeadRadiusFrac`
        // → `StageOneSceneBootstrap` (Task 19); `AimRayAlpha`/`AimRayWidth` →
        // `AimRayView` and `AimDotScale` → `CrosshairView` (both Task 20);
        // `TracerScale` → `ViewRegistry`/`ProjectileView` (Task 21). The rest
        // (`SlideDustBurstCount`, `StaminaBar*`, `HeadHitstopScale`,
        // `ZoneHitPitchOffset`, `Gib*`) still await their own later Г5 tasks.
        // `RicochetSparkCount` and
        // `SlideWallSparkBurstCount` are deliberately NOT added here: ricochet
        // sparks reuse the baked `BlockSparkBurstCount` prefab, and neither
        // has a consumer.
        [Range(0.1f, 3f)] public float TracerScale = 0.7f;
        [Range(1, 64)] public int SlideDustBurstCount = 14;
        public Color StaminaBarFullColor = new Color(0.15f, 0.95f, 0.85f);
        public Color StaminaBarLowColor = new Color(1f, 0.25f, 0.15f);
        [Range(0f, 1f)] public float StaminaBarLowThreshold = 0.25f;
        [Range(0f, 1f)] public float StaminaDeniedPulseSeconds = 0.2f;
        [Range(1f, 5f)] public float HeadHitstopScale = 1.4f;
        [Range(0f, 1f)] public float ZoneHitPitchOffset = 0.06f;
        [Range(0f, 20f)] public float GibHeadImpulseSpeed = 6f;
        [Range(0f, 20f)] public float GibExplosionSpeed = 4f;
        [Range(1, 512)] public int GibPartsFifoLimit = 24;
        [Range(0f, 10f)] public float GibPhysicsSeconds = 3f;
        [Range(0f, 1f)] public float AimProxyHeadRadiusFrac = 0.5f;
        [Range(0f, 1f)] public float AimRayAlpha = 0.35f;
        [Range(0f, 0.5f)] public float AimRayWidth = 0.03f;
        [Range(0f, 2f)] public float AimDotScale = 0.15f;

        // Task 22 (spec Г6) fix-round: slide-dust burst lifetime/speed/size —
        // review found these baked as `StageOneSceneBootstrap`-local literals,
        // misdocumented as following the HitSpark/BlockSpark/DeathBurst split
        // (those three DO config-source their own lifetime/speed/size; only
        // the burst COUNT literal for DeathBurst, and coneAngle for all four,
        // are the actual bootstrap-local exceptions). Moved here for the same
        // reason the other three sparks' numbers already are — owner
        // hot-tweak on playtest, GameFeelConfig class doc's "all game-feel
        // numbers in ScriptableObjects" rule (client/CLAUDE.md).
        [Range(0.01f, 2f)] public float SlideDustLifetime = 0.35f;
        [Range(0f, 20f)] public float SlideDustSpeed = 1.8f;
        [Range(0f, 2f)] public float SlideDustSize = 0.14f;

        // В1 fix-wave 1 (owner playtest feedback, item 3 "мерцание сборщика"):
        // the collector's doll pulses while a Dash↔Slide combo window is open
        // (`PlayerView.Sync` reads `PlayerState.PostDashSlideTimer`/
        // `LinkWindowTimer`, either > 0f — the same two timers
        // `PlayerMovementSystem` already uses to gate the link itself; the
        // pulse lived in `PlayerVisual.UpdateLinkWindowFlash` until Stage 2
        // Task 45a moved emission onto the root view, next to the renderers).
        // `LinkWindowFlashHz` is the pulse's oscillation rate; `LinkWindowFlashBoost`
        // scales its peak intensity on top of the fixed accent color
        // `PlayerView` reuses from `PlayerEmissive`/`DashGlowView` (Э1) —
        // same accent-constant-vs-SO-number split `MobView`'s Gunner glint
        // already makes. `LinkWindowFlashBoost` was the sync-marker key,
        // superseding `SlideDustSize` (Task 22, spec Г6) — see that field's
        // own chain history in the doc paragraphs above (PlayerVisualScale /
        // MaxDashGlows) — until `AimHoverGlowBoost` below superseded it in
        // turn (В1/В2 fix-wave 2).
        [Range(0.5f, 20f)] public float LinkWindowFlashHz = 6f;
        [Range(1f, 4f)] public float LinkWindowFlashBoost = 1.6f;

        // В1/В2 fix-wave 2 (app-n6g item 3a, owner playtest feedback:
        // headshot aiming was unreadable — "не видно куда точно целюсь").
        // `AimProvider.TryAimProxy` now exposes WHICH `AimProxy_Legs/Body/
        // Head` collider its raycast actually hit as `Ring.Simulation.Core.
        // HitZone` (the same enum `SimEvent.Zone` already uses — Presentation
        // reads it, never redefines a second one, Reuse > duplication). Both
        // `CrosshairView`'s marker and `AimRayView`'s ray tint by the hit zone
        // via the shared `AimZoneColors.Resolve` lookup: Legs/Body share the
        // neutral `AimZoneBodyColor`; Head gets the distinct `AimZoneHeadColor`
        // accent so a headshot lineup reads as visibly different before the
        // trigger is even pulled. Only meaningful while `AimHeld` (zone is
        // `None` otherwise — `AimProvider`'s own class doc); the marker/ray
        // fall back to their existing baked color when the zone is `None`.
        public Color AimZoneBodyColor = new Color(0.6f, 0.85f, 1f);
        public Color AimZoneHeadColor = new Color(1f, 0.25f, 0.15f);

        // Item 3b (same fix-wave, same owner request): while the aim-proxy
        // hit belongs to a live mob (`AimProvider.CurrentHoveredMob`),
        // `MobView.Sync` layers a soft extra glow (`MobView.HoverGlowAccent`)
        // on top of whatever accent is already active that frame — same
        // accent-constant-vs-SO-number-multiplier split `MobView`'s own
        // `GunnerGlintAccent`/`TelegraphAccent` pairs already use, mirrored
        // from `LinkWindowFlashAccent`/`LinkWindowFlashBoost` above. Range
        // floors at 1x (never DIMS an already-visible accent, only adds to
        // it) and caps at 3x; "keep subtle" per owner guidance, default 1.35.
        // Was the sync-marker key, superseding `LinkWindowFlashBoost` above,
        // until `AimRayHeadAlphaBoost` below superseded it (В3 fix-wave 1).
        [Range(1f, 3f)] public float AimHoverGlowBoost = 1.35f;

        // В3 fix-wave 1 (app-n6g item 3, owner playtest feedback: "не видно,
        // будет ли хедшот" — headshot aiming read as flat/2D). Two
        // readability boosts, both applied ONLY while the aim-proxy hit is
        // `HitZone.Head` (`AimProvider.CurrentAimZone`, `AimZoneColors`'s own
        // switch — Legs/Body stay at their existing neutral treatment,
        // unchanged by this fix-wave):
        // `AimMarkerHeadScaleBoost` — `CrosshairView`'s marker (now billboarded
        // to the camera and positioned at the proxy's own real 3D `hit.point`,
        // `AimProvider.CurrentAimWorldPoint`, instead of a floor projection)
        // grows by this multiplier on top of its existing `AimDotScale` shrink,
        // so a headshot lineup visibly enlarges the dot, not just recolors it.
        // `AimRayHeadAlphaBoost` — `AimRayView`'s existing whole-ray zone tint
        // (`AimZoneColors.Resolve`, already applied uniformly via
        // `MaterialPropertyBlock`, not just at the tip) gets an extra
        // brightness multiplier on top of `AimRayAlpha` for Head specifically,
        // so the color swap reads as unmistakable rather than a faint dim-red
        // tinge under Bloom (`AimRayAlpha`'s own class doc: a lower alpha
        // reads as a fainter glow, not literal translucency).
        [Range(1f, 3f)] public float AimMarkerHeadScaleBoost = 1.4f;
        [Range(1f, 4f)] public float AimRayHeadAlphaBoost = 2f;

        // В3 fix-wave 2 (app-n6g item 1, owner playtest feedback: "шар — большой,
        // хвост — маленький"). `ProjectileView`'s ball never read anything but a
        // flat `StageOneSceneBootstrap.ProjectileDiameter` constant baked into the
        // prefab once at bootstrap time — identical for every shot, regardless of
        // owner or sim radius, even after wave-1's chore shrank the PLAYER weapon's
        // sim `ProjectileRadius` 0.12→0.08 (×1.5). `ViewRegistry.SyncProjectiles`
        // now binds the ball's live diameter straight off the SAME per-shot
        // `ProjectileState.Radius` every projectile already carries
        // (`SimulationWorld.SpawnProjectile`'s own `radius` param — `Weapon.
        // ProjectileRadius` for the player, `Gunner.ProjectileRadius` for a mob
        // shot; both owners flow through this ONE multiplier, no per-owner branch
        // needed — Reuse > duplication). Default 1 already reads as sim-accurate:
        // a player shot's ball becomes 0.08*2*1=0.16m, exactly ×1.5 smaller than
        // the old flat 0.24m (matching the owner's own complaint 1:1 with no knob
        // touched); a Gunner shot's ball becomes 0.15*2*1=0.30m — slightly LARGER
        // than the old flat 0.24m, which is correct, not a regression: item 4d
        // (В3 fix-wave 2 investigation) wants mob projectiles reading their real,
        // more dangerous collision size rather than an undersized decorative ball
        // that could make a genuine hit look like it "passed over." The range
        // lets the owner push either direction further on playtest.
        [Range(0.2f, 3f)] public float ProjectileBallScale = 1f;

        // В3 fix-wave 2 (app-n6g item 3a, owner playtest feedback: "хочется больше
        // акцента на хедшоте" — the head-zone marker scale boost from В3 fix-wave 1
        // (`AimMarkerHeadScaleBoost` above) reads as a static size bump; the owner
        // asked for MORE emphasis without reviving `AimSnapScreenRadius` (explicitly
        // rejected). `CrosshairView.LateUpdate` now layers a breathing scale PULSE
        // on top of that boost while the aim-proxy hit is `HitZone.Head`: the same
        // `0.5+0.5*Mathf.Sin(Time.unscaledTime * Hz * 2π)`-shaped oscillation
        // `PlayerView.Sync`/`MobView`'s telegraph/glint pulses
        // already use (Reuse > duplication — this project has exactly one pulse
        // idiom, not a second one per feature), remapped from that method's [0,1]
        // emission-intensity range to a signed [-1,1] SCALE offset around 1 (a
        // marker should visibly grow AND shrink, not just fade in/out):
        // `1 + HeadHoverPulseAmp * Mathf.Sin(...)`. `HeadHoverPulseHz` is the
        // oscillation rate; `HeadHoverPulseAmp` is the peak deviation from 1 —
        // default 0.25 swings the marker between 0.75x and 1.25x of its existing
        // `AimMarkerHeadScaleBoost`-boosted size, never zero/negative (amp capped
        // at 1). Was the sync-marker key, superseding `AimRayHeadAlphaBoost`
        // above, until `RemotePlayerEmission` below superseded it in turn
        // (Stage 2 Task 45a).
        [Range(0.5f, 15f)] public float HeadHoverPulseHz = 5f;
        [Range(0f, 1f)] public float HeadHoverPulseAmp = 0.25f;

        // Stage 2 Task 45a (spec §3.12's "other players — pooled by index,
        // emission GameFeelConfig.RemotePlayerEmission" bullet; §3.15's
        // new-fields table): the
        // steady emissive rim every doll but this client's own wears, so a
        // stranger reads as a stranger the instant they come out of the fog.
        // Applied by `PlayerView.Sync` through the SAME MaterialPropertyBlock/
        // `_EmissionColor` write the combo-window pulse already goes through —
        // one emission mechanism per doll, not two — and composed with it
        // additively, so a remote player in a link window still pulses. The
        // local doll is handed `Color.black` by `ViewRegistry`, which is why
        // "own" needs no branch inside the view; a doll that dies is handed
        // black once and for good (`PlayerView.DetachAsCorpse`).
        //
        // HDR IS NOT DECORATION HERE, IT IS THE FIELD WORKING AT ALL
        // (fix-round 1). An unattributed `Color` draws with Unity's LDR picker,
        // which clamps every channel into [0,1] the first time the owner opens
        // it and presses OK — and this field IS the В1 tuning path for
        // "свой/чужой", with the `.asset` off-limits to hand editing. Emission
        // above 1 is what Bloom reacts to, so a clamped value would quietly
        // turn the rim off. This is the first HDR color the project keeps in an
        // SO: the existing accent pairs put the HDR part in a CODE constant
        // (`LinkWindowFlashAccent`, `MobView.TelegraphAccent`/`GunnerGlintAccent`)
        // and expose only an LDR multiplier, and the existing SO colors
        // (`AimZoneBodyColor`/`AimZoneHeadColor`, `StaminaBar*`) are LDR because
        // something else multiplies them. Neither shape fits: the spec asks for
        // one field, nothing multiplies this one, and a second SO float would be
        // a knob the owner did not ask for.
        //
        // DEFAULT IS A VIOLET, and the reason is the palette that already
        // exists rather than taste: the player signature is the cyan
        // `PlayerView`/`DashGlowView`/`PlayerEmissive` share (0, 2.5, 3), the
        // Chaser telegraph is amber, the Gunner glint cool-white, the headshot
        // cue red and the body cue pale blue — violet is the one direction left
        // that neither argues with the player's own cyan nor imitates a mob
        // tell. Intensity sits well under the pulse's own peak (the cyan accent
        // × LinkWindowFlashBoost reaches ~4.8 on its brightest channel), so it
        // reads as a rim under Bloom rather than a flash, and a remote player's
        // link window still visibly out-shines their resting tint.
        //
        // Was the sync-marker key, superseding `HeadHoverPulseAmp` above, until
        // `GunEjectLocalEuler` below superseded it in turn (Stage 2 Task 45b).
        [ColorUsage(false, true)]
        public Color RemotePlayerEmission = new Color(1.4f, 0.15f, 1.6f);

        // Stage 2 Task 45b (owner requirement 2026-08-10: the flash, the brass
        // and the aim ray all leave the BARREL OF THE MODEL, not a point in
        // front of the hero — bd app-fl3/app-e2n/app-60c): the local poses of
        // the two empty sockets `StageOneSceneBootstrap` parents under the
        // doll's `Gun`, reconciled write-if-different on every `Apply` exactly
        // like `GunLocalPosition`/`GunLocalEuler` above already are.
        //
        // SOCKETS RATHER THAN A COMPUTED OFFSET, because the owner's tuning
        // loop is "drag it with the scene gizmo in PlayMode → Capture → the
        // numbers land here" (`PlayerGunTuner`), and a GameObject is what a
        // gizmo can drag. Both are read at runtime through `PlayerView`'s own
        // serialized references, never by name lookup.
        //
        // WHY THE MUZZLE HAS NO EULER AND THE EJECTION PORT DOES. The flash and
        // the ray need a POINT: the shot's direction is already carried
        // tick-exactly by the event itself (`SimEvent.Amount`) and by the aim
        // for the predicted burst, and re-deriving it from an untuned socket's
        // forward axis would replace a measured number with a guess. The brass
        // needs a point AND a direction — bd app-e2n asks for an impulse
        // "вбок-назад от ориентации оружия" outright, and the only home for
        // that direction is the port's own rotation.
        //
        // DEFAULTS. Muzzle: 0.18 m along the pistol's own local forward, i.e.
        // about the length of the slide ahead of the model's pivot — the
        // barrel's mouth for a model authored facing +Z. Ejection port: 3 cm to
        // the right of the pistol's centerline and 4 cm above it (the top-right
        // of the slide, where a real pistol throws brass), 2 cm behind the
        // muzzle's own offset. Its euler yaws the port's forward 100° right of
        // the barrel, which is "sideways and slightly back". Each is a starting
        // pose for the owner's gizmo pass on the smoke test, not a measurement:
        // the model carries no sockets of its own to take them from.
        // Was the sync-marker key until Stage 3 Task 30's `MeshSagMeters`
        // below superseded it.
        public Vector3 GunMuzzleLocalPosition = new Vector3(0f, 0f, 0.18f);
        public Vector3 GunEjectLocalPosition = new Vector3(0.03f, 0.04f, 0.16f);
        public Vector3 GunEjectLocalEuler = new Vector3(0f, 100f, 0f);

        // Stage 3 Task 30 (spec §3.11): the greybox's zone floor tint — one
        // color per zone, read once by `GreyboxBuilder` when it builds the
        // arena. THREE FLAT COLORS RATHER THAN ONE PLUS A GRADIENT, because
        // the thing being communicated is a DISCRETE fact: `Geometry.ZoneOf`
        // answers Outer/Middle/Core off two radii, and a player who has to
        // judge a gradient to tell which zone their feet are in is being shown
        // something the simulation does not believe. LDR, not HDR: this is the
        // arena's own ground reading under the scene's lighting, not an accent
        // — every emissive accent in this project is a separate material
        // (`DashGlow`, `TracerTrail`, the extraction rings below), and a floor
        // that emits would wash out the mobs standing on it.
        //
        // DEFAULTS. Outer is the coolest and darkest (the spawn belt, where
        // the least happens), Middle warms slightly, Core is the warmest and
        // brightest — so "further in" reads as "hotter" at a glance without
        // any of the three competing with the player's own cyan or the
        // Chaser's amber telegraph. All three sit low enough in value to keep
        // the "dark environment + emissive VFX" look ADR-001 §10 asks for.
        //
        // BAKED AT BUILD TIME, NOT PER FRAME: `GreyboxBuilder` writes these
        // into a `MaterialPropertyBlock` while constructing the floor, so an
        // owner edit lands on the next arena build (a match restart, or a
        // fresh PlayMode enter) rather than live — the same documented
        // limitation the spark/decal numbers above already carry.
        public Color ZoneTintOuter = new Color(0.24f, 0.26f, 0.32f);
        public Color ZoneTintMiddle = new Color(0.34f, 0.30f, 0.28f);
        public Color ZoneTintCore = new Color(0.46f, 0.33f, 0.26f);

        // Stage 3 Task 30 (spec §3.11, Р273): how far a drawn arc is allowed
        // to depart from the circle Simulation actually collides against —
        // the SAGITTA of one mesh segment, in meters. `GreyboxBuilder` turns
        // it into a segment count with the same arithmetic Р208 used to reject
        // the "arc as a chain of stadiums" alternative: a chord of half-angle
        // θ on a circle of radius R sags by R(1 − cos θ), so a full ring needs
        // ⌈π / arccos(1 − sag/R)⌉ segments and every arc drawn on that ring
        // steps by that same angle. That is what keeps the number out of the
        // code: the ring wall's own 48 segments used to be a literal, and a
        // literal cannot follow a radius the owner retunes (113 today, 65
        // before Т12) — at a fixed count the picture silently drifts off the
        // collision the further the radius moves, which is exactly the class
        // of defect `app-1ru` was filed for.
        //
        // 0.05 IS THE SPEC'S NUMBER and it is a VISUAL tolerance, not a
        // physical one: 5 cm is under the width of a round and far under the
        // 1 m half-width of a zone wall, so no shot ever passes through a
        // gap the eye can see. Raising it coarsens the mesh (fewer objects,
        // visibly faceted rings); lowering it multiplies primitives with no
        // gain the camera can resolve at this arena's scale.
        [Range(0.005f, 0.5f)] public float MeshSagMeters = 0.05f;

        // Stage 3 Task 31 (spec §3.11/Р251, owner decision R-192 — the
        // acceptance condition of milestone В1): the two archetypes that had
        // no visual identity of their own. Until this task `ViewRegistry`
        // branched two ways on `MobType`, so Elite, the Director's escort and
        // the DIRECTOR HIMSELF were all drawn with the Gunner's prefab at the
        // Gunner's scale — a boss with 2500 HP that looked exactly like the
        // mob a player had already killed forty times.
        //
        // THE NUMBERS ARE THE OWNER'S OWN, not this task's invention. The
        // asset track put every candidate model on a stage and the owner tuned
        // them by eye across two milestones; the surviving numbers live in
        // `AssetPreviewSceneBootstrap` (`EliteScale` 1.5 on QuadShell,
        // `TriloScale` 0.75 — "the biggest elite, halved" — and `DirectorScale`
        // 3.5, "the director, doubled"). Those preview scales sit on the SAME
        // base as these fields: the preview's own mech scale is 0.4, which is
        // `ChaserVisualScale` exactly. Elite is drawn with `Enemy_Trilobite`,
        // hence 0.75; the Director with `Enemy_QuadShell` in `DirectorSkin.mat`,
        // hence 3.5 — a silhouette four and a half times the Gunner's.
        //
        // ⚠ ASSETS-001 §2.3 asks for x1.5-2 on the Director and this ships
        // 3.5. The ADR number was written before the model was ever on a
        // stage; 3.5 is what the owner asked for after looking at it (asset
        // milestone 3). Both are starting points for the В1 tuning pass —
        // this field is live-tweakable in PlayMode, which is exactly why it is
        // a field.
        // ⚠ ELITE WAS 0.75 AND IS 1.5 SINCE THE В1 PLAYTEST'S SECOND ROUND (bd
        // `app-oxyo`), on the owner's call — "the elite has to be at least twice
        // as big" — and the MEASUREMENT that call turned out to agree with. The
        // drawn crown of every archetype was measured against the `AimProxy_Head`
        // belt its own SO builds (renderer AABB of the prefab's `Visual`
        // subtree): Chaser 2.70 m against a 1.85 m column, Gunner 4.21 against
        // 3.5, Director 4.79 against 3.5 — and ELITE 1.79 against 3.5, the only
        // archetype drawn SHORTER than its own hittable column, at barely half
        // of it. The Head band is 2.7-3.5 m, so it stood entirely in the air
        // ABOVE the model: an elite could not be headshot at the head a player
        // could see, and the visible head read as body. 1.5 puts the crown at
        // 3.58 m against the column's 3.5 — the paragraph above's warning
        // ("a retune of one alone silently desyncs the visible model from its
        // hittable silhouette") answered rather than repeated.
        //
        // The other three keep their measured mismatch (1.46 / 1.20 / 1.37 — the
        // drawn model is TALLER than the column, so the top of it cannot be
        // shot). That is bd `app-1cst`, and it is not a retune: the owner's
        // decision is that hits should be resolved against the MODEL rather than
        // a capsule, together with the projectile/mob physics of `app-afaz`.
        [Range(0.05f, 6f)] public float EliteVisualScale = 1.5f;
        [Range(0.05f, 6f)] public float DirectorVisualScale = 3.5f;

        // Stage 3 Task 31 (spec §3.11): how big the raid's own furniture is
        // drawn. Both are read fresh on every bind (`PickupView.Bind`'s own
        // doc), so the owner can size them live on the В1 playtest — which is
        // the point, because what these two numbers really control is whether a
        // player can FIND loot on a 113 m arena from a three-quarter camera,
        // and no number derived at a desk answers that.
        //
        // 0.5 m for a cell is a hand's width — big enough to catch the eye at
        // the camera's distance, small enough not to read as a crate. The
        // container scale is 1 because the Sci-Fi kit's props are authored at
        // roughly human furniture size already; it exists as a field so the
        // owner can push crates up or down against the collector doll without
        // touching four prefabs.
        [Range(0.05f, 3f)] public float PickupVisualDiameter = 0.5f;
        [Range(0.05f, 5f)] public float ContainerVisualScale = 1f; // sync-marker key — keep LAST

        /// THE one place an archetype becomes a visual scale (fix-round, Ф7
        /// review B-M5). `ViewRegistry` sizes the LIVE mob with it and
        /// `PersistentPropsDirector` sizes that mob's corpse and its gib parts
        /// with it, and the two must never disagree — a body that changed size
        /// the instant it died reads as a bug (В1/В2 fix-wave 2, app-n6g item
        /// 2). Until this fix that rule was carried by two mirrored `switch`
        /// blocks and a comment asking each to remember the other; now there is
        /// nothing to remember. Throws on an unknown archetype rather than
        /// answering with the Gunner's size, for the reason spec Р251 spent a
        /// stage on.
        public float VisualScaleFor(MobType type) => type switch
        {
            MobType.Chaser => ChaserVisualScale,
            MobType.Gunner => GunnerVisualScale,
            MobType.Elite => EliteVisualScale,
            MobType.Director => DirectorVisualScale,
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type,
                "unknown archetype"),
        };

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
        // GameFeelConfig itself is never consumed by SimConfigBuilder (class doc
        // above), so SimulationRunner's ApplyConfig reaction to this Raise() just
        // rebuilds an unchanged SimConfig — harmless, not skipped, since telling
        // GameFeelConfig's OnValidate apart from the six balance SOs' would need
        // a second event/subscriber and buys nothing. The Presentation-only
        // fields here (hitstop/shake/pooling/ImmediateMuzzleFeedback) are read
        // fresh every frame by their own consumers regardless (class doc above),
        // so they hot-tweak with no reaction to this event at all — EXCEPT the
        // handful of numbers GameFeelDirector/PersistentPropsDirector/
        // CorpseView/StageOneSceneBootstrap bake into a PREFAB at bootstrap time
        // (spark lifetime/speed/size, decal size, T27) — those only pick up an
        // edit after a fresh bootstrap re-run, not live in PlayMode; documented
        // limitation, not a bug (task-28-report.md).
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
