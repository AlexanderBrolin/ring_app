using UnityEngine;

namespace Ring.Data
{
    /// Server-side visibility filter balance numbers (Stage 2 Task 19, spec
    /// §3.5): sight/hearing radii, exit hysteresis, linger grace period, and
    /// the audible-position quantization grid. Field defaults mirror
    /// Ring.Simulation.Tests.TestConfigs.Default().Visibility. Task 22 wired
    /// this SO into SimConfigBuilder (the seventh Build() parameter), added
    /// the per-field validation there and shipped the .asset — one task later
    /// than the fields themselves, see task-19-brief.md's own documented
    /// deviation for why the split was necessary. The [Range] bands below are
    /// Inspector hints only; the builder mirrors them with real checks
    /// (SimConfigBuilder's own Р115 note), and ConfigTests pins both ends of
    /// every band from the attributes themselves.
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
