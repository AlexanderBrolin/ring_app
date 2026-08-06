using UnityEngine;

namespace Ring.Data
{
    /// Server-side visibility filter balance numbers (Stage 2 Task 19, spec
    /// §3.5): sight/hearing radii, exit hysteresis, linger grace period, and
    /// the audible-position quantization grid. Field defaults mirror
    /// Ring.Simulation.Tests.TestConfigs.Default().Visibility. Wiring this SO
    /// into SimConfigBuilder (the seventh Build() parameter), its validation
    /// and the shipped .asset are Task 22's scope, not this one's — see
    /// task-19-brief.md's own documented deviation for why.
    [CreateAssetMenu(menuName = "Ring/Visibility Config", fileName = "VisibilityConfig")]
    public sealed class VisibilityConfig : ScriptableObject
    {
        [Range(5f, 150f)] public float SightRadius = 45f;
        [Range(5f, 200f)] public float HearRadius = 60f;
        [Range(0f, 20f)] public float ExitHysteresis = 3f;
        [Range(0, 30)] public int LingerTicks = 5;

        // Stage 2 Task 20 (spec §3.5, Р21): grid size, in metres, that events
        // from an invisible source snap their reported position onto — see
        // VisibilitySystem.QuantizeAudiblePos (Task 20). 0 disables
        // quantization (exact position always reported).
        [Range(0f, 10f)] public float HearPositionGridMeters = 3f; // sync-marker key — keep LAST

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
