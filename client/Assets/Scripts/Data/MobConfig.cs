using Ring.Simulation.Core;
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

        // Task 1: muzzle height for ranged mobs (Gunner); the chaser (this class's
        // default shape) never reads it, but it must stay a plausible in-zone value
        // (not 0) — the Gunner slot's SimConfigBuilder.Validate rule D5 checks
        // Hero.SlideProfileTop + Gunner.ProjectileRadius < Gunner.MuzzleHeight even
        // when the Gunner .asset has not been authored yet (Task 17) and a freshly
        // created MobConfig instance is standing in for it in tests.
        [Range(0f, 5f)] public float MuzzleHeight = 0.95f;

        // Task 1: melee swing-attack target lead — how far ahead of a moving target's
        // position a Chaser's swing aims (Task 15+); capped in meters so a fast-fleeing
        // target does not pull the swing absurdly far off its own body.
        [Range(0f, 2f)] public float SwingLeadFactor = 1.0f;
        [Range(0f, 6f)] public float SwingLeadMaxMeters = 2.0f; // Was the sync-marker key until app-88jb.

        /// app-88jb Т1 (spec §3.2): impact physics — same shape as
        /// HeroConfig's Mass/ImpactSpeedCap/tilt-spring block (no
        /// CocoonDamping here, mobs carry no cocoon), plus knockdown.
        /// Defaults below are the CHASER archetype (this class's shape, see
        /// class doc) — the Gunner/Elite/Director .assets override
        /// Mass/CenterOfMassHeight to their own numbers in Т11a (owner
        /// decision Р432 — not Т16, which stays part geometry/MaxAimHeight).
        [Range(1f, 10000f)] public float Mass = 90f;
        [Range(0.1f, 50f)] public float ImpactSpeedCap = 6f;
        [Range(0.01f, 100f)] public float ProjectileMass = 3.0f;
        [Range(0f, 6f)] public float CenterOfMassHeight = 1.17f;
        [Range(0.05f, 0.95f)] public float TiltDampingRatio = 0.55f;
        [Range(0.15f, 5f)] public float TiltSettleSeconds = 0.9f;
        [Range(0f, 50f)] public float TiltGain = 10.5f;
        /// Knockdown (owner decision Н23, variant 3a): above TiltFallAngle
        /// the mob goes down for DownedSeconds and neither shoots nor strikes.
        [Range(0.1f, 3.14f)] public float TiltFallAngle = 0.9f;
        [Range(0.1f, 10f)] public float DownedSeconds = 1.2f; // Was the sync-marker key until app-88jb Т13.

        /// app-88jb Т13 (spec §3.3, owner decision Н8): this archetype's body as
        /// an ORDERED stack of parts, bottom to top — same field and same
        /// contract as HeroConfig.Parts, whose doc carries the reasoning
        /// (Inspector array held directly, no [Range] on an array, gated by
        /// SimConfigBuilder.Validate). Defaults below are the CHASER's, this
        /// class's shape (see the class doc); the Gunner/Elite/Director .assets
        /// get their own arrays from the bootstrap in Т16, and the test-side
        /// archetype fixtures from ConfigTests.SeedMob.
        /// THE HEIGHTS ARE THE MODEL'S, NOT THE OLD COLUMN'S (spec §3.3,
        /// evidence Т12): the chaser's crown measures 2.6996 m against a
        /// column of 1.85, i.e. a scale factor of 1.46, and every height here
        /// is the old one multiplied by it. That is the whole point of the
        /// change — the old column ended 0.85 m below the visible head, so a
        /// shot at what the player SAW as the head passed over an empty
        /// number. Radii are 0.7 / 1.0 / 0.35 of Radius, the same humanoid
        /// proportion all five bodies use.
        public HitPart[] Parts =
        {
            new HitPart { Radius = 0.35f, Bottom = 0f, Top = 0.88f,
                Zone = HitZone.Legs, DamageMult = 0.75f },
            new HitPart { Radius = 0.50f, Bottom = 0.88f, Top = 2.12f,
                Zone = HitZone.Body, DamageMult = 1.0f },
            new HitPart { Radius = 0.17f, Bottom = 2.12f, Top = 2.70f,
                Zone = HitZone.Head, DamageMult = 1.7f },
        }; // Was the sync-marker key until app-88jb Т19.

        /// app-88jb Т19 (spec §3.4): this archetype's own ricochet numbers,
        /// the mob-side twin of WeaponConfig's three — same fields, same
        /// ranges, same reasoning (see WeaponConfig's own doc). Defaults below
        /// are the CHASER's, this class's shape; the Gunner/Elite/Director
        /// .assets take their own through the bootstrap.
        [Range(0, 8)] public int MaxRicochets = 2;
        [Range(0.05f, 1f)] public float RicochetRetention = 0.8f;
        [Range(0.1f, 100f)] public float RicochetMinSpeed = 6f; // sync-marker key — keep LAST (was Parts, app-88jb)

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
