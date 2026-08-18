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
        // Stage 3 Task 12 (spec §3.13): 36 -> 72. The arena is three zones
        // now and a wave is split across all three by ZoneWeights — at 36 the
        // core's 10% share rounds to three or four mobs and the periphery
        // never fills at all, so a wave could not populate the arena it is
        // spread over.
        [Range(1, 100)] public int MaxMobsPerWave = 72;
        [Range(1, 100)] public int MaxSpawnAttempts = 16;
        [Range(0, 100)] public int FallbackSlots = 24;
        [Range(0f, 1f)] public float GunnerShareBase = 0.2f;
        [Range(0f, 1f)] public float GunnerShareGrowth = 0.05f;

        // Stage 2 Task 16 (spec §3.4): per-extra-player wave scale — the wave
        // size is multiplied by (1 + (playerCount - 1) * PerPlayerCountFrac)
        // before the MaxMobsPerWave cap (WaveSystem.CountForTest). 0 keeps the
        // Stage 1 solo-sized waves at any player count.
        [Range(0f, 2f)] public float PerPlayerCountFrac = 0.7f;

        // Stage 3 Task 11 (spec §3.3 Р211/Р212/Р298, coordinator R-58): the
        // zone budget and elite-composition numbers. Array element ranges
        // are not expressible via [Range] (Unity's attribute clamps the
        // whole field, not per-element) — SimConfigBuilder.Validate is the
        // real gate for ZoneWeights (sums to 1, exactly three elements,
        // coordinator R-56).
        public float[] ZoneWeights = { 0.45f, 0.45f, 0.10f };
        [Range(0f, 1f)] public float EliteShareMiddle = 0.35f;
        [Range(0f, 1f)] public float EliteShareOuterGrowth = 0.02f;
        // Coordinator R-60: the fourth wave field, not a code constant —
        // CRITICAL RULE 6 (ADR-002 §4) puts every wave balance number in a
        // ScriptableObject, this one included, so the owner can retune the
        // periphery's difficulty ceiling on milestone В1 without a
        // recompile.
        [Range(0f, 1f)] public float EliteShareOuterCap = 0.25f; // sync-marker key — keep LAST

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
