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

        // Task 2 (spec stamina/slide/aim): movement-driven spread widening while
        // running/sliding, and whether the weapon can fire at all mid-slide.
        public bool CanFireWhileSlide = true;
        [Range(1f, 5f)] public float SpreadRunMult = 1.5f;
        [Range(1f, 5f)] public float SpreadSlideMult = 2.0f;
        [Range(0f, 1f)] public float RunSpreadSpeedFrac = 0.5f;

        // Stage 3 Task 2 (spec Р261/Р225): the ammo economy — energy cells
        // convert to shots, and the magazine fires slower once it runs dry
        // rather than going silent (see WeaponSimConfig's own doc for the
        // full mechanism). AmmoStart deliberately does NOT mirror into
        // Ring.Simulation.Tests.TestConfigs.Default() — see that fixture's
        // own comment (errata E-4/A-C3).
        [Range(1, 100)] public int ShotsPerCell = 10;
        [Range(0, 2000)] public int AmmoStart = 120;
        [Range(1, 2000)] public int AmmoMax = 400;
        [Range(0.01f, 10f)] public float EmergencyFireInterval = 1.25f; // sync-marker key — keep LAST

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
