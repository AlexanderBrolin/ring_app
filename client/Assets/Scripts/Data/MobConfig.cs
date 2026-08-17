using UnityEngine;

namespace Ring.Data
{
    /// Balance numbers shared by all mob archetypes (chaser/gunner use the same shape,
    /// one asset per archetype). Field defaults mirror
    /// Ring.Simulation.Tests.TestConfigs.Default().Chaser — the melee-only archetype,
    /// so the ranged-only fields default to 0 (unused by chaser); the Gunner .asset
    /// overrides them (Task 7).
    [CreateAssetMenu(menuName = "Ring/Mob Config", fileName = "MobConfig")]
    public sealed class MobConfig : ScriptableObject
    {
        [Range(0.1f, 20f)] public float MaxSpeed = 5.2f;
        [Range(1f, 200f)] public float Accel = 30f;
        // Stage 3 Task 8 (spec §3.13, Р284, errata E-6 D-I6): ceilings
        // widened 2 -> 4 (Radius) and 500 -> 5000 (MaxHp) so Т10's Elite
        // (Radius 0.8) and Director (MaxHp 2500, Radius 2.2) assets fit the
        // Inspector slider without the owner's first touch silently
        // clamping them back down.
        [Range(0.1f, 4f)] public float Radius = 0.5f;
        [Range(1f, 5000f)] public float MaxHp = 30f;
        [Range(0f, 200f)] public float ContactDamage = 15f;
        [Range(0f, 20f)] public float AttackRange = 1.1f;
        [Range(0f, 5f)] public float TelegraphSeconds = 0.35f;
        [Range(0f, 10f)] public float AttackCooldown = 0.9f;
        [Range(0f, 30f)] public float PreferredRange = 0f;
        [Range(0f, 10f)] public float RangeTolerance = 0f;
        [Range(0f, 20f)] public float StrafeSpeed = 0f;
        [Range(0f, 5f)] public float FireInterval = 0f;
        [Range(0f, 100f)] public float ProjectileSpeed = 0f;
        [Range(0f, 2f)] public float ProjectileRadius = 0f;
        [Range(0f, 10f)] public float ProjectileLifetime = 0f;
        [Range(0f, 200f)] public float ProjectileDamage = 0f;
        [Range(0f, 2f)] public float LeadFactor = 0f;
        [Range(0f, 10f)] public float SeparationRadius = 1.2f;
        [Range(0f, 50f)] public float SeparationStrength = 6f;
        [Range(0f, 10f)] public float AvoidLookahead = 3f;
        [Range(0f, 5f)] public float AvoidMargin = 1f;

        // Task 1 (spec hit-zone geometry): vertical hit-zone bounds and per-zone
        // damage multipliers, same shape as HeroConfig — consumed by the raycast aim
        // system (Task 4+). Defaults below are the chaser archetype (this class's shape,
        // see class doc); the Gunner .asset overrides LegsTop/BodyTop/HeadTop to the
        // taller ranged-mech silhouette (Task 7/17).
        [Range(0.05f, 5f)] public float LegsTop = 0.60f;
        [Range(0.05f, 5f)] public float BodyTop = 1.45f;
        [Range(0.05f, 5f)] public float HeadTop = 1.85f;
        [Range(0f, 5f)] public float LegsDamageMult = 0.75f;
        [Range(0f, 5f)] public float BodyDamageMult = 1.0f;
        [Range(0f, 5f)] public float HeadDamageMult = 1.7f;

        // Task 1: muzzle height for ranged mobs (Gunner); the chaser (this class's
        // default shape) never reads it, but it must stay a plausible in-zone value
        // (not 0) — the Gunner slot's SimConfigBuilder.Validate rule D5 checks
        // Hero.SlideProfileTop + Gunner.ProjectileRadius < Gunner.MuzzleHeight even
        // when the Gunner .asset has not been authored yet (Task 17) and a freshly
        // created MobConfig instance is standing in for it in tests.
        [Range(0f, 5f)] public float MuzzleHeight = 0.95f;

        // Task 1: melee swing-attack target lead — how far ahead of a moving target's
        // position a Chaser's swing aims (Task 15+); capped in metres so a fast-fleeing
        // target does not pull the swing absurdly far off its own body.
        [Range(0f, 2f)] public float SwingLeadFactor = 1.0f;
        [Range(0f, 6f)] public float SwingLeadMaxMeters = 2.0f; // sync-marker key — keep LAST

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
