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
        [Range(0f, 300f)] public float ProjectileSpeed = 0f;
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
        /// ⚠ app-94sk T2: same migration as HeroConfig.Parts -- the heights kept
        /// their numbers and changed their names, and BoneA/BoneB are the
        /// placeholder column the baker (T4) overwrites from the real skeleton.
        /// ⛔ app-94sk T4: THE EXTENTS NOW OBEY VALIDATION RULE 13 — the
        /// capsule's reach in the REST POSE off `TestConfigs.ChaserRestPose()`
        /// (bones 0 / 0.88 / 2.12 / 2.70). Same numbers `TestConfigs` has
        /// carried since T2; the class defaults were the half that still read
        /// as bands.
        /// ⛔⛔ app-saqr (T4b): THIRTEEN VOLUMES ON REAL BONES — see
        /// `HeroConfig.Parts` for why a placeholder column had to go and what
        /// it cost. The layout is spec §3.2 / COMBAT-001 §2.2 for the CHASER
        /// (this class's shape): torso, chest, head, upper and lower arm x2,
        /// leg of three segments x2. Indices are the baked table's columns off
        /// `Ring/Audit/Pose Candidates` against George's rig (47 skinning
        /// bones, 21 body bones):
        ///   1 Torso · 2 Chest · 3 Neck · 4 Head · 5/8 Shoulder l/r
        ///   6/9 UpperArm l/r · 7/10 LowerArm l/r · 11/15 UpperLeg l/r
        ///   12/16 MidLeg l/r · 13/17 LowerLeg l/r · 14/18 FootBack l/r
        /// ⛔ RADII ARE MEASURED by that same tool — the furthest the mesh gets
        /// from each pair's segment — never taken off a decile profile
        /// (spec §3.2's own refusal, and the reason is a T-posed prefab).
        /// ⚠ Arms carry `HitZone.Body` until plan 2 introduces `HitZone.Arms`.
        /// ⚠ Extents are derived off `TestConfigs.ChaserRestPose()`'s row 0
        /// (validation rule 13); the `.asset` gets them recomputed from the
        /// BAKED table by the bootstrap.
        public HitPart[] Parts =
        {
            new HitPart { BoneA = 1, BoneB = 2, Radius = 0.2902f, RestBottom = 0.6488f, RestTop = 1.6469f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 0 },   // Torso→Chest
            new HitPart { BoneA = 2, BoneB = 3, Radius = 0.8347f, RestBottom = 0.5220f, RestTop = 2.6224f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 1 },   // Chest→Neck
            new HitPart { BoneA = 4, BoneB = 4, Radius = 0.2545f, RestBottom = 1.7124f, RestTop = 2.2214f,
                Zone = HitZone.Head, DamageMult = 1.70f, PartId = 2 },   // Head→Head
            new HitPart { BoneA = 5, BoneB = 6, Radius = 0.1271f, RestBottom = 1.7035f, RestTop = 2.0418f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 3 },   // Shoulder.L→UpperArm.L
            new HitPart { BoneA = 6, BoneB = 7, Radius = 0.0998f, RestBottom = 1.4144f, RestTop = 2.0145f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 4 },   // UpperArm.L→LowerArm.L  ⛔ claw uncovered: containment 0.5587 would make his forearm wider than his torso
            new HitPart { BoneA = 8, BoneB = 9, Radius = 0.1271f, RestBottom = 1.7095f, RestTop = 2.0535f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 5 },   // Shoulder.R→UpperArm.R
            new HitPart { BoneA = 9, BoneB = 10, Radius = 0.0998f, RestBottom = 1.4362f, RestTop = 2.0262f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 6 },   // UpperArm.R→LowerArm.R  ⛔ same, right side
            new HitPart { BoneA = 11, BoneB = 12, Radius = 0.2757f, RestBottom = 0.4363f, RestTop = 1.3382f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 7 },   // UpperLeg.L→MidLeg.L
            new HitPart { BoneA = 12, BoneB = 13, Radius = 0.1265f, RestBottom = 0.3648f, RestTop = 0.8385f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 8 },   // MidLeg.L→LowerLeg.L
            new HitPart { BoneA = 13, BoneB = 14, Radius = 0.2019f, RestBottom = -0.1638f, RestTop = 0.6932f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 9 },   // LowerLeg.L→FootBack.L
            new HitPart { BoneA = 15, BoneB = 16, Radius = 0.2757f, RestBottom = 0.4641f, RestTop = 1.3541f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 10 },   // UpperLeg.R→MidLeg.R
            new HitPart { BoneA = 16, BoneB = 17, Radius = 0.1265f, RestBottom = 0.3526f, RestTop = 0.8663f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 11 },   // MidLeg.R→LowerLeg.R
            new HitPart { BoneA = 17, BoneB = 18, Radius = 0.2019f, RestBottom = -0.1638f, RestTop = 0.6810f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 12 },   // LowerLeg.R→FootBack.R
        }; // Was the sync-marker key until app-88jb Т19.

        /// app-94sk T2 (spec §3.3, Р507): the projectile broad phase's radius —
        /// see HeroConfig.GatherRadius for the whole argument. Default is the
        /// body radius until the baker writes the real one.
        // app-94sk T4: the chaser's own reach off ChaserRestPose — his foot is
        // swung 0.9 m out of his circle, so rule 9's table half bites here.
        // ⛔ app-saqr (T4b): 1.25 -> 0.9243 — rule 9 over the chaser's THIRTEEN
        // real volumes. It goes DOWN, and that is the measurement rather than
        // a saving: the old number came off a fixture whose foot was swung
        // 0.9 m aside by hand, and his real rest pose keeps its legs under him.
        [Range(0.05f, 12f)] public float GatherRadius = 0.9243f;

        /// app-94sk T2 (spec §3.9): WHICH volume this archetype strikes with.
        /// ⛔ -1 IS THE SENTINEL "this archetype does not strike at all", and it
        /// has to be one: 0 is a valid PartId, and the gunner's AttackRange is
        /// 0. Validation rule 10 is what pairs the two.
        /// ⚠ THE DEFAULT IS THE CHASER'S, like every number in this class: he
        /// strikes (AttackRange 1.1), so he names a volume. The Gunner's own
        /// .asset carries the sentinel instead, because his AttackRange is 0.
        [Range(-1, 255)] public int SwingPartId = 1;

        /// app-88jb Т19 (spec §3.4): this archetype's own ricochet numbers,
        /// the mob-side twin of WeaponConfig's three — same fields, same
        /// ranges, same reasoning (see WeaponConfig's own doc). Defaults below
        /// are the CHASER's, this class's shape; the Gunner/Elite/Director
        /// .assets take their own through the bootstrap.
        [Range(0, 8)] public int MaxRicochets = 2;
        [Range(0.05f, 1f)] public float RicochetRetention = 0.8f;
        [Range(0.1f, 100f)] public float RicochetMinSpeed = 6f; // Was the sync-marker key until app-88jb Т20.

        /// app-88jb Т20 (spec §3.4): this archetype's own piercing numbers, the
        /// mob-side twin of WeaponConfig's pair — same fields, same ranges,
        /// same reasoning (see WeaponConfig's own doc). The two are the same on
        /// all four archetypes today, exactly as the ricochet three above are,
        /// so the C# defaults here ARE every archetype's numbers and the
        /// bootstrap adds none of its own — what it does deliver is the key
        /// itself, through the sync marker below.
        [Range(0.001f, 1f)] public float PierceMassRatio = 0.06f;
        [Range(0f, 0.95f)] public float PierceDamageLoss = 0.5f; // Was the sync-marker key until app-88jb Т22.

        /// app-88jb Т22 (spec §3.5, owner decision Р442): this archetype's share
        /// of a collision's reaction — the mob-side twin of HeroConfig's own
        /// field, same range, same meaning (see that class's doc for the model).
        /// ONE for every archetype, and that is not a placeholder: a mob has no
        /// planted footing to shed reaction into, so mob-vs-mob conserves
        /// momentum EXACTLY, and the collector's 0.25 stays the single named
        /// deviation in the whole law rather than a special case in the caller.
        /// It is also the ready handle for a future anchor-like archetype that
        /// should be harder to shove than its mass alone would say.
        [Range(0f, 1f)] public float PushRecoilFraction = 1f; // sync-marker key — keep LAST (was PierceDamageLoss, app-88jb Т20)

        /// app-w4ca T4 (spec §3.5а): the BAKED POSE TABLE this body's volumes
        /// stand in. ⛔ NOT A HOT KNOB — the owner turns capsule radii and
        /// reaction thresholds; clips and phases are generated, and this field
        /// only says WHICH artifact a body reads. Without it
        /// `SimConfigBuilder.Build` would have nowhere to take the table from.
        /// ⚠ THE SYNC MARKER DOES NOT MOVE for this field: `EnsureAssetHasKey`
        /// now takes a LIST of marker names, so a new field is one more name in
        /// that list rather than a marker relocation (Runbook R-ASSET).
        public PoseTableAsset Poses;

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
