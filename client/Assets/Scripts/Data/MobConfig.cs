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
        [Range(0.1f, 2f)] public float Radius = 0.5f;
        [Range(1f, 500f)] public float MaxHp = 30f;
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
    }
}
