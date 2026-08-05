using UnityEngine;

namespace Ring.Data
{
    /// Wave-spawning balance numbers (pacing, counts, spawn placement).
    /// Field defaults mirror Ring.Simulation.Tests.TestConfigs.Default().Wave.
    [CreateAssetMenu(menuName = "Ring/Wave Config", fileName = "WaveConfig")]
    public sealed class WaveConfig : ScriptableObject
    {
        [Range(0f, 60f)] public float FirstWaveDelay = 2.5f;
        [Range(0.1f, 60f)] public float WavePause = 4f;
        [Range(0f, 20f)] public float SpawnRingInset = 2f;
        [Range(0f, 50f)] public float MinSpawnDistanceToPlayer = 8f;
        [Range(1, 50)] public int BaseCount = 4;
        [Range(0, 20)] public int CountGrowth = 2;
        // Stage 2 Task 16 (spec §3.4): 24 -> 36, headroom for the x2.4 three-player scale.
        [Range(1, 100)] public int MaxMobsPerWave = 36;
        [Range(1, 100)] public int MaxSpawnAttempts = 16;
        [Range(0, 100)] public int FallbackSlots = 24;
        [Range(0f, 1f)] public float GunnerShareBase = 0.2f;
        [Range(0f, 1f)] public float GunnerShareGrowth = 0.05f;

        // Stage 2 Task 16 (spec §3.4): per-extra-player wave scale — the wave
        // size is multiplied by (1 + (playerCount - 1) * PerPlayerCountFrac)
        // before the MaxMobsPerWave cap (WaveSystem.CountForTest). 0 keeps the
        // Stage 1 solo-sized waves at any player count.
        [Range(0f, 2f)] public float PerPlayerCountFrac = 0.7f; // sync-marker key — keep LAST

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
