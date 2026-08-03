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
        // (Т4+) to resolve which body zone a shot lands in.
        [Range(0.05f, 5f)] public float LegsTop = 0.55f;
        [Range(0.05f, 5f)] public float BodyTop = 1.35f;
        [Range(0.05f, 5f)] public float HeadTop = 1.75f;
        [Range(0f, 5f)] public float LegsDamageMult = 0.75f;
        [Range(0f, 5f)] public float BodyDamageMult = 1.0f;
        [Range(0f, 5f)] public float HeadDamageMult = 1.7f;

        // Task 1: slide stamina-movement profile height and the hero's own weapon
        // muzzle heights (standing / mid-slide), consumed by the aim-ray system (Т4+).
        [Range(0.05f, 5f)] public float SlideProfileTop = 0.55f;
        [Range(0f, 5f)] public float MuzzleHeight = 1.0f;
        [Range(0f, 5f)] public float SlideMuzzleHeight = 0.45f;
        [Range(1f, 6f)] public float MaxAimHeight = 3.8f;

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
