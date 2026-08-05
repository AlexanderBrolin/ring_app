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

        // Task 1 (spec hit-zone geometry): vertical hit-zone bounds (metres above
        // ground) and per-zone damage multipliers used by the raycast aim system
        // (Task 4+) to resolve which body zone a shot lands in.
        [Range(0.05f, 5f)] public float LegsTop = 0.55f;
        [Range(0.05f, 5f)] public float BodyTop = 1.35f;
        [Range(0.05f, 5f)] public float HeadTop = 1.75f;
        [Range(0f, 5f)] public float LegsDamageMult = 0.75f;
        [Range(0f, 5f)] public float BodyDamageMult = 1.0f;
        [Range(0f, 5f)] public float HeadDamageMult = 1.7f;

        // Task 1: slide stamina-movement profile height and the hero's own weapon
        // muzzle heights (standing / mid-slide), consumed by the aim-ray system (Task 4+).
        [Range(0.05f, 5f)] public float SlideProfileTop = 0.55f;
        [Range(0f, 5f)] public float MuzzleHeight = 1.0f;
        [Range(0f, 5f)] public float SlideMuzzleHeight = 0.45f;
        [Range(1f, 6f)] public float MaxAimHeight = 3.8f;

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

        // Stage 2 Task 8 (spec Interfaces): minimum tick gap the eventual
        // edge-request gate (Stage 2 Task 10 — decision F1a moved the gate
        // itself out of this task, see task-8-brief.md's header) will require
        // between two DashRequested/SlideRequested edges from the same
        // player. Declared here, data-only, no behaviour yet — SimConfig is
        // not part of StateHash (SimConfigHash arrives in Task 23), so this
        // field is hash-neutral by construction. LinkRefund above was the
        // sync-marker key until this field superseded it — see its own doc
        // for the historical chain before it.
        [Range(0, 15)] public int EdgeRequestMinTicks = 3; // sync-marker key — keep LAST

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
