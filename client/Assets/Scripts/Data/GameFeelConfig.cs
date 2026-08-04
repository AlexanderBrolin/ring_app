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
        [Range(1, 4096)] public int MaxCasings = 1024;
        [Range(1, 4096)] public int MaxDecals = 512;
        [Range(1, 512)] public int MaxCorpses = 64;
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
        [Range(1, 64)] public int HitSparkBurstCount = 15;
        [Range(1, 64)] public int BlockSparkBurstCount = 18;

        public bool ImmediateMuzzleFeedback = true;
        public bool ExtrapolateLocalPlayer = false;

        // Assets phase B (spec §3.7): character-visual numbers. Scale fields are
        // bind-time (re-run the bootstrap / rebuild prefabs to apply); the rest are
        // read per frame — live hot-tweak. GunLocal* are reconciled write-if-different
        // by the bootstrap on every Apply. GunLocalEuler was the sync-marker key
        // (bootstrap:245) until the Б1 fix-wave-2 block below superseded it, and
        // `DashGlowSize` in turn until the Б1 fix-wave-3 field below superseded
        // THAT, and `CasingEjectSpeedMax` after that, then `AimDotScale`
        // (Task 17), then `SlideDustSize` (Task 22, spec Г6) — `LinkWindowFlashBoost`
        // is the current marker (В1 fix-wave 1, owner playtest feedback), see
        // its own doc.
        [Range(0.1f, 3f)] public float PlayerVisualScale = 1f;
        [Range(0.05f, 2f)] public float ChaserVisualScale = 0.4f;
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
        [Range(0f, 2f)] public float MuzzleLiftY = 1.1f;
        public Vector3 GunLocalPosition = Vector3.zero;
        public Vector3 GunLocalEuler = Vector3.zero;

        // Б1 milestone owner request (app-9av): a glowing floor mark at the dash
        // start point, fading out over a few seconds. MaxDashGlows is a pool cap
        // (dash cooldown 1.2s vs ~2.5s life → 3 alive typ.). DashGlowSize was the
        // bootstrap sync-marker key until the Б1 fix-wave-3 field below
        // superseded it, then `CasingEjectSpeedMax`, then `AimDotScale`
        // (Task 17), then `SlideDustSize` (Task 22, spec Г6) — `LinkWindowFlashBoost`
        // is the current marker (В1 fix-wave 1, owner playtest feedback), see
        // its own doc.
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
        // vector (PersistentPropsDirector.SpawnCasing ejects to the
        // shooter's right of the shot, like a real pistol's ejection port)
        // rather than an undirected impulse.
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
        // (`PlayerVisual.UpdateLinkWindowFlash` reads `PlayerState.
        // PostDashSlideTimer`/`LinkWindowTimer`, either > 0f — the same two
        // timers `PlayerMovementSystem` already uses to gate the link itself).
        // `LinkWindowFlashHz` is the pulse's oscillation rate; `LinkWindowFlashBoost`
        // scales its peak intensity on top of the fixed accent color
        // `PlayerVisual` reuses from `PlayerEmissive`/`DashGlowView` (Э1) —
        // same accent-constant-vs-SO-number split `MobView`'s Gunner glint
        // already makes. `LinkWindowFlashBoost` is the new sync-marker key,
        // superseding `SlideDustSize` (Task 22, spec Г6) — see that field's
        // own chain history in the doc paragraphs above (PlayerVisualScale /
        // MaxDashGlows).
        [Range(0.5f, 20f)] public float LinkWindowFlashHz = 6f;
        [Range(1f, 4f)] public float LinkWindowFlashBoost = 1.6f; // sync-marker key — keep LAST

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
