using UnityEngine;

namespace Ring.Data
{
    /// Presentation-only camera numbers (top-down ¾ framing). Never consumed by
    /// SimConfigBuilder / Ring.Simulation.
    [CreateAssetMenu(menuName = "Ring/Camera Config", fileName = "CameraConfig")]
    public sealed class CameraConfig : ScriptableObject
    {
        [Range(50f, 60f)] public float PitchDeg = 55f;
        [Range(1f, 50f)] public float Distance = 18f;
        [Range(0f, 2f)] public float LookAhead = 0.25f;
        [Range(0f, 1f)] public float Damp = 0.15f;

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
        // Like GameFeelConfig, never consumed by SimConfigBuilder (class doc
        // above); CameraRig already reads these fields fresh every frame, so
        // this Raise() only causes SimulationRunner to rebuild an unchanged
        // SimConfig — harmless, see GameFeelConfig.OnValidate's doc for why that
        // is not worth special-casing away.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
