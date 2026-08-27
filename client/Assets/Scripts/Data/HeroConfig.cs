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
        [Range(1f, 6f)] public float MaxAimHeight = 4.9f;

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
        /// ⚠ Parts[0].Top IS SlideProfileTop: validation rule 5 requires the
        /// slide profile to land exactly on a part boundary, which is what
        /// keeps the slide equivalent to what it was before parts existed.
        public HitPart[] Parts =
        {
            new HitPart { Radius = 0.32f, Bottom = 0f, Top = 0.55f,
                Zone = HitZone.Legs, DamageMult = 0.75f },
            new HitPart { Radius = 0.45f, Bottom = 0.55f, Top = 1.35f,
                Zone = HitZone.Body, DamageMult = 1.0f },
            new HitPart { Radius = 0.16f, Bottom = 1.35f, Top = 1.75f,
                Zone = HitZone.Head, DamageMult = 1.7f },
        }; // sync-marker key — keep LAST (was TiltGain, app-88jb)

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
