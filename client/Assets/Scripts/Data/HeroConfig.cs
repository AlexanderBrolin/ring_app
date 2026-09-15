using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Data
{
    /// Balance numbers for the player hero (movement, dash, HP).
    /// Field defaults mirror Ring.Simulation.Tests.TestConfigs.Default().Hero.
    [CreateAssetMenu(menuName = "Ring/Hero Config", fileName = "HeroConfig")]
    public sealed class HeroConfig : ScriptableObject
    {
        [Range(0.1f, 30f)] public float MaxSpeed = 7f;
        [Range(1f, 200f)] public float Accel = 40f;
        [Range(1f, 200f)] public float Friction = 30f;
        [Range(0.1f, 2f)] public float Radius = 0.45f;
        [Range(1f, 1000f)] public float MaxHp = 100f;
        [Range(1f, 60f)] public float DashSpeed = 22f;
        [Range(0.05f, 1f)] public float DashDuration = 0.15f;
        [Range(0.1f, 10f)] public float DashCooldown = 1.2f;
        [Range(0f, 1f)] public float DashIframes = 0.2f;
        [Range(0f, 0.5f)] public float DashBufferWindow = 0.15f;

        // Task 1: slide stamina-movement profile height and the hero's own weapon
        // muzzle heights (standing / mid-slide), consumed by the aim-ray system (Task 4+).
        [Range(0.05f, 5f)] public float SlideProfileTop = 0.55f;
        [Range(0f, 5f)] public float MuzzleHeight = 1.0f;
        [Range(0f, 5f)] public float SlideMuzzleHeight = 0.45f;
        // app-88jb Т13 (plan's own ordering rule 1, review findings A-C6/D-C1):
        // 3.8 -> 4.9 IN THIS TASK, not in Т16. Validation rule 14 grows to
        // "MaxAimHeight >= the top of every archetype's last part", and the
        // tallest of those is the Director's 4.80 — introduce the rule with the
        // number still at 3.8 and every BuildShipped in the suite throws. The
        // shipped .asset deliberately stays at its old value until Т16 delivers
        // it through the bootstrap, which is what Т16's gate-on-the-old-value
        // exists for.
        //
        // ⛔⛔ app-94sk T4: 4.9 -> 5.9, AND THE REASON IS A PLACEHOLDER FOLLOWED
        // TO ITS CONCLUSION. Validation rule 13 now makes every body's extents
        // agree with its pose table, so the Director's torso capsule reaches
        // 3.70 + 2.20 = 5.90 — his bone at 3.70 grown by a radius that IS HIS
        // WHOLE BODY CIRCLE, standing in for a torso nobody has measured yet.
        // Rule 14 measures the aim ceiling against that, so the ceiling has to
        // clear it or no configuration builds at all. T2 met this exact number,
        // reverted the derivation and named T4 as the task that would face it
        // (`TestConfigs`' own doc says so in as many words); this is that task.
        // ⚠ THE NUMBER IS EXPECTED TO COME BACK DOWN IN T4b, which lays the
        // Director's fifteen real volumes out — a 2.20 m torso capsule is not
        // one of them. The `[Range]` ceiling goes to 7 so the rule has headroom
        // rather than sitting on its own bound.
        [Range(1f, 7f)] public float MaxAimHeight = 5.9f;

        // Task 2 (spec stamina/slide/aim): stamina pool, per-action costs and regen —
        // stamina drains on Dash/Slide and regenerates after a delay once no action
        // is draining it.
        // В1 fix-wave 3 (owner economy rework, app-n6g): LinkedDashStaminaCost's
        // discounted-dash-in-window model is retired — dash/slide now always pay
        // their own full price; LinkRefund (below — was the class's sync-marker
        // field until Stage 2 Task 8's EdgeRequestMinTicks superseded it, see
        // LinkRefund's own doc) is what makes chaining net-cheaper instead.
        [Range(1f, 300f)] public float StaminaMax = 100f;
        [Range(0.1f, 300f)] public float DashStaminaCost = 40f;
        [Range(0.1f, 300f)] public float SlideStaminaCost = 30f;
        [Range(0.1f, 100f)] public float StaminaRegenPerSec = 20f;
        [Range(0f, 5f)] public float StaminaRegenDelay = 0.8f;

        // Task 2: slide kinematics (speed/duration/steering) and the buffered-input
        // windows that let a queued slide/dash chain into the next action instead of
        // being dropped.
        [Range(0.1f, 40f)] public float SlideSpeed = 13.5f;
        [Range(0.05f, 5f)] public float SlideDuration = 0.52f;
        [Range(0f, 10f)] public float SlideSteerRadPerSec = 1.2f;
        [Range(0.01f, 1f)] public float SlideMinSpeedFrac = 0.75f;
        [Range(0.05f, 5f)] public float RunUpSeconds = 1.18f;
        [Range(0f, 10f)] public float RunUpDecayMult = 3.0f;
        [Range(0f, 1f)] public float SlideBufferWindow = 0.15f;
        [Range(0f, 1f)] public float LinkWindowSeconds = 0.25f;
        [Range(0f, 1f)] public float PostDashSlideWindow = 0.32f;
        [Range(-1f, 1f)] public float SlideWallStopDot = 0.7f;
        [Range(0f, 1f)] public float RicochetRetention = 0.8f;

        // Task 2: aim-down-sights movement/settle profile. AimMoveSpeedFrac must stay
        // strictly above SlideMinSpeedFrac (D15) so aiming can never be mistaken for a
        // slide-speed state by downstream movement code.
        [Range(0.01f, 1f)] public float AimMoveSpeedFrac = 0.8f;
        [Range(0.01f, 1f)] public float AimSlideSpeedMult = 0.5f;
        [Range(0.05f, 2f)] public float AimSettleSeconds = 0.5f;

        // В1 fix-wave 3 (owner economy rework, app-n6g): stamina credited back
        // when a slide/dash executes inside its link window (PostDashSlideTimer
        // for a linked slide, LinkWindowTimer for a linked dash — see
        // PlayerMovementSystem.Update's two "linked" branches). Validated
        // strictly below min(DashStaminaCost, SlideStaminaCost) by
        // SimConfigBuilder — no perpetual motion, every linked move still nets
        // a stamina drain. Was the sync-marker key until Stage 2 Task 8's
        // EdgeRequestMinTicks field below superseded it.
        [Range(0f, 40f)] public float LinkRefund = 10f;

        // Stage 2 Task 8 (spec Interfaces): minimum tick gap the edge-request
        // gate requires between two ACCEPTED DashRequested/SlideRequested edges
        // of the same kind from the same player. Declared here in Task 8;
        // consumed since Stage 2 Task 10, where the gate itself landed
        // (PlayerMovementSystem.Update — decision F1a moved it out of Task 8,
        // see task-8-brief.md's header). SimConfig is not part of StateHash
        // (SimConfigHash arrives in Task 23), so the field itself stays
        // hash-neutral by construction even though what it gates is not.
        // LinkRefund above was the sync-marker key until this field superseded
        // it — see its own doc for the historical chain before it. Was itself
        // the sync-marker key until Stage 3 Task 3's PickupRadius field below
        // superseded it.
        [Range(0, 15)] public int EdgeRequestMinTicks = 3;

        /// Stage 3 Task 3 (spec §3.6 table, owner decision R-4): auto-pickup
        /// collection radius — Loot.PickupSystem.Update gathers energy cells
        /// within this distance of a live, un-extracted player. R-4 moved
        /// this class's sync-marker onto this field in Stage 3 Task 3
        /// (errata E-7 precedent, same as ArenaConfig.MaxPickups) —
        /// EdgeRequestMinTicks above was the marker until then.
        [Range(0.1f, 10f)] public float PickupRadius = 2f;

        /// Stage 3 Task 4 (spec §3.6 "Рюкзак", errata E-6 D-I8): the
        /// backpack's two capacity numbers — InventoryCapacity in SLOT
        /// POINTS (Loot.Inventory.TryAdd's own capacity check), MaxInventoryItems
        /// as the hard ceiling on item COUNT that sizes SimulationWorld's
        /// per-player Loot.Inventory backing array. R-4 moves this class's
        /// sync-marker onto MaxInventoryItems in THIS task (same errata E-7
        /// precedent PickupRadius followed one task ago) — PickupRadius
        /// above was the marker until now.
        [Range(1, 32)] public int InventoryCapacity = 8;
        [Range(1, 32)] public int MaxInventoryItems = 16; // Was the sync-marker key until app-88jb.

        /// app-88jb Т1 (spec §3.2): impact physics — mass (kilograms,
        /// plausible by RATIO to other bodies) and the impact-speed ceiling
        /// applied to the body being shoved, before CocoonDamping divides it
        /// down for the collector (SimConfig.HeroSimConfig carries the full
        /// rationale).
        [Range(1f, 10000f)] public float Mass = 120f;
        [Range(0.1f, 50f)] public float ImpactSpeedCap = 6f;
        [Range(1f, 20f)] public float CocoonDamping = 3f;
        /// Tilt spring (spec §3.2, owner decision Н10/Н23): parameterized
        /// through the damping RATIO and the settle TIME, never raw k/c —
        /// see Ring.Simulation.Combat.Impact.SpringFromSettle.
        [Range(0f, 6f)] public float CenterOfMassHeight = 0.95f;
        [Range(0.05f, 0.95f)] public float TiltDampingRatio = 0.55f;
        [Range(0.15f, 5f)] public float TiltSettleSeconds = 0.9f;
        [Range(0f, 50f)] public float TiltGain = 10.5f; // Was the sync-marker key until app-88jb Т13.

        /// app-88jb Т13 (spec §3.3, owner decision Н8): the collector's body as
        /// an ORDERED stack of parts, bottom to top. Held here as the Inspector
        /// array itself — no parallel Data-side DTO — exactly the way
        /// ItemCatalog holds `ItemDef[]` (HitPart's own doc in SimConfig.cs
        /// carries the [System.Serializable]/mutable-fields argument).
        /// NO [Range]: the attribute is not expressible per element of an
        /// array, so the gate is SimConfigBuilder.Validate's six rules, the
        /// same place ArenaConfig's Obstacle/Wall structs are gated.
        /// THE COLLECTOR IS THE ONE BODY WHOSE HEIGHTS DO NOT MOVE, and that
        /// is the SPEC's decision (§3.3), not a measurement: he is not from the
        /// mech pack the four archetypes come from, so the spec gives him k = 1
        /// and leaves his heights alone.
        /// ⚠ Т12 DID measure him, and the number is 1.0481, not 1.0 — his drawn
        /// crown is 1.8342 m against a 1.75 m column (evidence
        /// task-88jb-12-elite-measurement.md). The top ~8 cm of the model
        /// therefore stay outside his hit volume, exactly the mismatch this
        /// phase closes for the other four. Left as the spec has it, and
        /// written down rather than rounded away. So the three heights are the
        /// same 0.55/1.35/1.75 the collector has been hittable at since Task
        /// 1, and only the per-part RADII are new. They are 0.7 / 1.0 / 0.35
        /// of Radius — one named humanoid proportion, applied to all five
        /// bodies alike (0.315 and 0.1575 rounded to the centimeter the
        /// Inspector shows).
        /// ⚠ app-94sk T4: `Parts[0].RestTop` IS NO LONGER `SlideProfileTop`, and
        /// the rule that made them one number — validation rule 5 — is
        /// withdrawn. The slide is judged by rule 16 now, against the crown of
        /// the SLIDE CLIP BY THE TABLE; the scalar survives only as the height
        /// ceiling two combat readers still derive from it, until the pose
        /// reaches them in T6a/T6b.
        /// ⚠ app-94sk T2: the heights kept their numbers and changed their
        /// NAMES (Bottom/Top -> RestBottom/RestTop, the capsule's extent in the
        /// rest pose), and the bone indices below are PLACEHOLDERS -- a column
        /// of four bone ends, which is what a three-band body was. The baker
        /// (T4) overwrites all four of BoneA/BoneB/RestBottom/RestTop from the
        /// real skeleton; until it does, these are the numbers the game has
        /// always used and the answers do not move. PartId is the index, which
        /// is where an append-only local numbering starts.
        /// ⛔ app-94sk T4: THE EXTENTS NOW OBEY VALIDATION RULE 13 — each is the
        /// capsule's reach in the REST POSE, i.e. the bone end grown by the
        /// radius, computed off `TestConfigs.HeroRestAndSlidePose()`'s rest row
        /// (bones 0 / 0.55 / 1.35 / 1.75, which are his column since Task 1).
        /// They read differently from the band numbers they replace ONLY
        /// because a capsule has CAPS and a band did not: the legs reach 0.32
        /// below the ground and the head 0.16 above the crown.
        /// ⛔⛔ app-saqr (T4b): ELEVEN VOLUMES ON REAL BONES, WHICH IS THE WHOLE
        /// POINT OF THIS PASS. The three bands above hung on a placeholder
        /// column 0..3 — bone ends that stood in for a body while the table was
        /// being built — and a number derived from a placeholder is worse than
        /// the placeholder: T4 derived the extents from the real table over
        /// those fake pairs and the SHIPPED configuration stopped assembling
        /// (`Hero.MuzzleHeight 1.0` above a head bottom of 0.8536, bd app-saqr).
        ///
        /// The layout is spec §3.2 / COMBAT-001 §2.2 — pelvis, chest, head,
        /// upper and lower arm x2, thigh and shin x2 — and the BONE INDICES ARE
        /// THE BAKED TABLE'S COLUMNS, printed by `Ring/Audit/Pose Candidates`
        /// against the UAL2 rig (65 skinning bones, 25 of them body bones after
        /// the finger/IK filter):
        ///   1 pelvis · 3 spine_02 · 5 neck_01 · 6 Head · 8/12 upperarm l/r
        ///   9/13 lowerarm l/r · 10/14 hand l/r · 15/20 thigh l/r
        ///   16/21 calf l/r · 17/22 foot l/r
        /// ⛔ RADII ARE MEASURED, NOT CHOSEN: each is the furthest the mesh's
        /// own vertices get from that pair's segment, printed by the same tool.
        /// Spec §3.2 forbids taking them off COMBAT-001's decile profile and
        /// says why — the prefab holds a T-POSE, so its 8th and 9th deciles are
        /// ARM SPAN (0.95 / 1.19 m), and an arm sized from them would be twice
        /// as wide as the whole collector.
        /// ⚠ ARMS CARRY `HitZone.Body` FOR NOW: `HitZone.Arms` is plan 2's
        /// (spec §3.10), and a volume naming a member that does not exist would
        /// not compile. Their multiplier is the torso's until that lands.
        /// ⚠ `RestBottom`/`RestTop` are derived — the capsule's extent in the
        /// rest pose, off `TestConfigs.HeroRestAndSlidePose()`'s row 0, which
        /// validation rule 13 checks. The shipped `.asset` gets the same
        /// numbers recomputed from the BAKED table by the bootstrap.
        public HitPart[] Parts =
        {
            new HitPart { BoneA = 1, BoneB = 3, Radius = 0.1559f, RestBottom = 0.7213f, RestTop = 1.2935f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 0 },   // pelvis→spine_02
            new HitPart { BoneA = 3, BoneB = 5, Radius = 0.2107f, RestBottom = 0.9269f, RestTop = 1.6570f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 1 },   // spine_02→neck_01
            new HitPart { BoneA = 5, BoneB = 6, Radius = 0.2605f, RestBottom = 1.1858f, RestTop = 1.7861f,
                Zone = HitZone.Head, DamageMult = 1.70f, PartId = 2 },   // neck_01→Head
            new HitPart { BoneA = 8, BoneB = 9, Radius = 0.0835f, RestBottom = 1.0711f, RestTop = 1.4852f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 3 },   // upperarm_l→lowerarm_l
            new HitPart { BoneA = 9, BoneB = 10, Radius = 0.1284f, RestBottom = 0.7707f, RestTop = 1.2830f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 4 },   // lowerarm_l→hand_l
            new HitPart { BoneA = 12, BoneB = 13, Radius = 0.0832f, RestBottom = 1.0922f, RestTop = 1.4942f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 5 },   // upperarm_r→lowerarm_r
            new HitPart { BoneA = 13, BoneB = 14, Radius = 0.1284f, RestBottom = 0.7941f, RestTop = 1.3038f,
                Zone = HitZone.Body, DamageMult = 1.00f, PartId = 6 },   // lowerarm_r→hand_r
            new HitPart { BoneA = 15, BoneB = 16, Radius = 0.1022f, RestBottom = 0.4263f, RestTop = 0.9995f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 7 },   // thigh_l→calf_l
            new HitPart { BoneA = 16, BoneB = 17, Radius = 0.2604f, RestBottom = -0.1573f, RestTop = 0.7889f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 8 },   // calf_l→foot_l
            new HitPart { BoneA = 20, BoneB = 21, Radius = 0.1017f, RestBottom = 0.4088f, RestTop = 0.9990f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 9 },   // thigh_r→calf_r
            new HitPart { BoneA = 21, BoneB = 22, Radius = 0.2604f, RestBottom = -0.1567f, RestTop = 0.7709f,
                Zone = HitZone.Legs, DamageMult = 0.75f, PartId = 10 },   // calf_r→foot_r
        }; // Was the sync-marker key until app-88jb Т22's PushRecoilFraction below.

        /// app-94sk T2 (spec §3.3, Р507): the PROJECTILE BROAD PHASE's radius.
        /// `Radius` above stays physical — shoving, walls, visibility — and does
        /// not move; this one must cover the furthest bone in any phase of any
        /// clip plus the radius of the volume sitting on it.
        /// ⚠ THE DEFAULT IS THE BODY RADIUS ON PURPOSE: until the baker (T4)
        /// writes the real number, the broad phase must keep answering exactly
        /// what it answered before the split, so this task moves no outcome.
        // ⛔ app-saqr (T4b): 0.45 -> 1.1030772, AND IT IS RULE 9 ON THE ELEVEN REAL
        // VOLUMES rather than a stand-in. The old value was his body circle,
        // which the field's doc above called a placeholder "until the baker
        // writes the real one"; with arms on real bones his widest capsule
        // leaves that circle by 65 cm, and a gather that small would drop the
        // arm from the candidate set in silence. Off `TestConfigs`' fixture
        // table — the shipped number is recomputed from the BAKED table by
        // `StageOneSceneBootstrap.ApplyPoseNumbers`, as it has been since T4.
        [Range(0.05f, 12f)] public float GatherRadius = 1.1030772f;

        // app-88jb Т22 (spec §3.5, owner decisions Н15/Р442): the two numbers a
        // body collision needs from the collector.
        //
        // MaxDepenetrationPerTick caps how far ONE tick may push the collector
        // out of a body it is already inside. The case is real rather than
        // defensive: the dash covers 2.7 m and the Director is 4.4 m across, so
        // a dash can END inside that body, and an uncapped push would teleport
        // the collector 0.97 m in a single frame. Zero is NOT a legal "off"
        // switch — it walls the collector inside the body forever — which is
        // why SimConfigBuilder rejects it rather than accepting a silent no-op.
        //
        // PushRecoilFraction is how much of a collision's reaction the
        // collector's own footing FAILS to absorb. A self-propelled body pushes
        // against the ground and the ground takes the rest, so this is a
        // modelled term rather than a fudge: at 0.25 a dash keeps 89% of its
        // speed through a contact (Н15's cocoon falls out of the arithmetic
        // instead of needing a branch) and a slide through three chasers still
        // leaves the collector faster than running. A mob's own fraction is 1.0
        // — see MobConfig's twin — which makes mob-vs-mob conserve momentum
        // exactly and leaves the collector's deviation ONE named number.
        // Above 1 a body would gain more than it gives, which is why the range
        // and the validation rule both stop at one.
        [Range(0.01f, 5f)] public float MaxDepenetrationPerTick = 0.5f;
        [Range(0f, 1f)] public float PushRecoilFraction = 0.25f; // Was the sync-marker key until SlideThrustRecovery below.

        // app-88jb Т22 (owner decision Р443): the suit's impulse thrusters, as a
        // number. In the fiction they are WEAK units around the shoulder blades
        // whose vector cannot be aimed properly yet, which is why the slide is
        // the only move that uses them: prone, the thrust is at least
        // controllable, and it cannot lift the body off the ground.
        //
        // Mechanically this is how much slide speed the thrust WINS BACK per
        // second after a collision has taken some (PushRecoilFraction above is
        // what takes it). It is the difference between a slide that COASTS and
        // one that is DRIVEN, and it is the single number a meta-upgrade raises:
        // a stronger engine shrugs a chaser off and keeps going, a weak one
        // bogs down on the third. It is also where a future jump attaches —
        // thrust that can beat the recoil is thrust that can leave the ground.
        //
        // Linear, in m/s per second, the same MoveTowards-shaped decay this
        // project uses everywhere for a speed converging on a target. Zero is
        // legal and means an unpowered slide: the loss stands for the rest of
        // the move.
        [Range(0f, 60f)] public float SlideThrustRecovery = 18f; // sync-marker key — keep LAST (was PushRecoilFraction, app-88jb Т22)

        /// app-w4ca T4 (spec §3.5а): the BAKED POSE TABLE this body's volumes
        /// stand in. ⛔ NOT A HOT KNOB — the owner turns capsule radii and
        /// reaction thresholds; clips and phases are generated, and this field
        /// only says WHICH artifact a body reads. Without it
        /// `SimConfigBuilder.Build` would have nowhere to take the table from.
        /// ⚠ THE SYNC MARKER DOES NOT MOVE for this field: `EnsureAssetHasKey`
        /// now takes a LIST of marker names, so a new field is one more name in
        /// that list rather than a marker relocation (Runbook R-ASSET).
        public PoseTableAsset Poses;

        // Task 28 (spec §3.9): hot-tweak signal — every Inspector edit while in
        // PlayMode rebuilds SimConfig via SimulationRunner instead of requiring a
        // full match restart. Editor-only (OnValidate never runs in a player
        // build regardless of this guard); RingDataChanged.Raise() is a no-op
        // with zero subscribers outside Editor/dev builds either way.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
