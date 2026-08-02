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
        // same split the rest of this file already makes (e.g. `MobOffset`/
        // `ProjectileOffset` in ViewRegistry are also never SO fields).
        [Range(0f, 10f)] public float CasingImpulseUpMin = 1.2f;
        [Range(0f, 10f)] public float CasingImpulseUpMax = 2.2f;
        [Range(0f, 10f)] public float CasingImpulseSideMax = 1.2f;
        [Range(0f, 1f)] public float CasingTorqueScale = 0.02f;
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
