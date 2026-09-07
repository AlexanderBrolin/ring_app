using UnityEngine;

namespace Ring.Data
{
    /// Balance numbers for the player's weapon (fire rate, spread/recoil, projectiles).
    /// Field defaults mirror Ring.Simulation.Tests.TestConfigs.Default().Weapon.
    [CreateAssetMenu(menuName = "Ring/Weapon Config", fileName = "WeaponConfig")]
    public sealed class WeaponConfig : ScriptableObject
    {
        [Range(0.01f, 5f)] public float FireInterval = 0.12f;
        [Range(1f, 300f)] public float ProjectileSpeed = 35f;
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
        [Range(0.01f, 10f)] public float EmergencyFireInterval = 1.25f; // Was the sync-marker key until app-88jb.

        /// app-88jb Т1 (spec §3.2): impact physics — a GAME quantity
        /// calibrated backwards from the desired delta-v, NOT a physical
        /// bullet mass (see SimConfig.HeroSimConfig's own doc for why).
        [Range(0.01f, 100f)] public float ProjectileMass = 2.6f; // Was the sync-marker key until app-88jb Т19.

        /// app-88jb Т19 (spec §3.4, owner decision Н19): the ricochet — the
        /// round repeats the dash's rule off a wall one for one. See
        /// WeaponSimConfig's own doc for the rule and for why there is no
        /// angle threshold: `MaxRicochets` and `RicochetMinSpeed` are what
        /// bound a chain of weak ricochets, an angle threshold is not.
        /// `RicochetRetention` is declared over (0, 1] by validation rule 9
        /// (spec §3.10): at 1 a ricochet is lossless, above 1 it would ACCELERATE
        /// the round and no counter could stop it, so the attribute's ceiling
        /// and the rule agree on the same number rather than only nearly.
        [Range(0, 8)] public int MaxRicochets = 2;
        [Range(0.05f, 1f)] public float RicochetRetention = 0.8f;
        [Range(0.1f, 100f)] public float RicochetMinSpeed = 6f; // Was the sync-marker key until app-88jb Т20.

        /// app-88jb Т20 (spec §3.4, owner decision Н13): piercing a light body
        /// — a blow that would STRICTLY OVERKILL a light enough target kills it
        /// and the round flies on with part of its damage spent. Strictly, and
        /// the word is load-bearing (coordinator Ruling 102): a blow that kills
        /// EXACTLY consumes the round like any other, because the rule asks for
        /// `dmg > Hp` while death asks only for `dmg >= Hp`. See WeaponSimConfig's own
        /// doc for the rule, for why the mass ratio is DIRECT rather than its
        /// reciprocal (v1's inversion made 0 pierce everything, the Director
        /// included), and for why nobody is pierced at the shipped numbers.
        /// `PierceMassRatio` is required STRICTLY POSITIVE by validation rule
        /// 10 (spec §3.10), so the attribute's floor sits just above zero the
        /// same way `RicochetMinSpeed`'s does — zero is the one value the rule
        /// refuses outright, and an attribute that offered it would invite the
        /// setting the rule then rejects. The ceiling of 1 is an EDITOR limit
        /// and not a rule: a ratio of 1 already reads "a round must be as heavy
        /// as its target", i.e. the mechanic is off, so nothing above it is a
        /// different setting.
        /// `PierceDamageLoss` is declared over [0, 1) by the same rule, so the
        /// attribute stays strictly INSIDE the open end — the same shape
        /// MobConfig.TiltDampingRatio already uses for an open bound — while
        /// keeping zero, which the rule allows and which means "piercing costs
        /// the round nothing".
        [Range(0.001f, 1f)] public float PierceMassRatio = 0.06f;
        [Range(0f, 0.95f)] public float PierceDamageLoss = 0.5f; // Was the sync-marker key until app-8dv.

        /// app-8dv (spec §3.2/§3.8, owner decisions Н27/Н29/Н32/Н33): the spray
        /// PATTERN that replaced the world RNG draw — see WeaponSimConfig's own
        /// doc for what the pattern is and for why the angle had to become a
        /// function of state both sides already hold.
        ///
        /// `SprayPatternShots` is the burst length the pattern is drawn over and
        /// is required STRICTLY POSITIVE by validation rule 1 (it is a divisor),
        /// so the attribute's floor is 1 rather than 0.
        /// `SprayYawTurns` is the only one of the five with no upper bound in
        /// its rule (rule 5 asks non-negative): the attribute's 8 is an EDITOR
        /// limit, since past a few turns per burst the horizontal simply reads
        /// as noise and nothing above it is a different setting.
        /// ⚠ THE PATTERN MAY NOT BE OFF ON BOTH AXES AT ONCE (rule 6) —
        /// together that is a weapon with no pattern at all. The vertical has
        /// ONE off switch, `SprayPitchAmplitude = 0`; the horizontal has TWO,
        /// because `yawBase` is a product of both of its numbers — EITHER
        /// `SprayYawTurns = 0` (the phase freezes) OR `SprayYawAmplitude = 0`
        /// (the whole term scales away). So the refused settings are
        /// `SprayPitchAmplitude = 0` together with either of those two, and
        /// naming only the first pair here would promise a freedom the builder
        /// does not grant: authoring `SprayYawAmplitude = 0` beside a zero
        /// vertical fails the build with an ArgumentException.
        ///
        /// EITHER AXIS ALONE IS LEGAL, and deliberately so:
        /// `SprayPitchAmplitude = 0` is half of the owner-facing rollback,
        /// `SprayVariance = 1` the other half, and a rule that refused either
        /// would break the rollback. A zero horizontal beside a LIVE vertical
        /// is legal for the same reason.
        [Range(1, 60)] public int SprayPatternShots = 12;
        [Range(0f, 1f)] public float SprayYawAmplitude = 1.0f;
        [Range(0f, 8f)] public float SprayYawTurns = 0.7f;
        [Range(0f, 1f)] public float SprayPitchAmplitude = 0.35f;
        [Range(0f, 1f)] public float SprayVariance = 0.35f; // sync-marker key — keep LAST (was PierceDamageLoss, app-8dv)

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
