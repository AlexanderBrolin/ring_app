using UnityEngine;

namespace Ring.Data
{
    /// Balance numbers for the player's weapon (fire rate, spread/recoil, projectiles).
    /// Field defaults mirror Ring.Simulation.Tests.TestConfigs.Default().Weapon.
    [CreateAssetMenu(menuName = "Ring/Weapon Config", fileName = "WeaponConfig")]
    public sealed class WeaponConfig : ScriptableObject
    {
        [Range(0.01f, 5f)] public float FireInterval = 0.12f;
        [Range(1f, 100f)] public float ProjectileSpeed = 35f;
        [Range(0.01f, 2f)] public float ProjectileRadius = 0.12f;
        [Range(0.1f, 10f)] public float ProjectileLifetime = 1.5f;
        [Range(0.1f, 1000f)] public float Damage = 12f;
        [Range(0f, 1f)] public float SpreadRad = 0.026f;
        [Range(0f, 1f)] public float RecoilPerShotRad = 0.006f;
        [Range(0f, 5f)] public float RecoilRecoveryRadPerSec = 0.03f;
        [Range(0f, 1f)] public float RecoilMaxRad = 0.07f;
        [Range(0f, 5f)] public float MuzzleOffset = 0.6f;
        public bool CanFireWhileDash = false;
    }
}
