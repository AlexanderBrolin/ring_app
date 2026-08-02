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
        [Range(1, 100)] public int MaxMobsPerWave = 24;
        [Range(1, 100)] public int MaxSpawnAttempts = 16;
        [Range(0, 100)] public int FallbackSlots = 24;
        [Range(0f, 1f)] public float GunnerShareBase = 0.2f;
        [Range(0f, 1f)] public float GunnerShareGrowth = 0.05f;
    }
}
