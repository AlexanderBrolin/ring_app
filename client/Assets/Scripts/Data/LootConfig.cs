using UnityEngine;

namespace Ring.Data
{
    /// Loot-system balance numbers (Stage 3 Task 13, spec §3.7/§3.8):
    /// per-archetype/zone drop chances, container counts, the repair kit's
    /// own numbers, per-tier transfer time, and the two entity TTLs. Field
    /// defaults mirror Ring.Simulation.Tests.TestConfigs.Default().Loot
    /// (two-sources-of-numbers discipline, spec §0) except where the golden
    /// scenarios require otherwise — see TestConfigs' own comments for each
    /// such divergence.
    [CreateAssetMenu(menuName = "Ring/Loot Config", fileName = "LootConfig")]
    public sealed class LootConfig : ScriptableObject
    {
        // Stage 3 Task 13 (spec §3.7 table): [archetype * 3 + zone] -> chance,
        // archetype indexed like MobType (Chaser/Gunner/Elite/Director), zone
        // like Zone (Outer/Middle/Core) — see LootSimConfig.DropChance's own
        // doc for why the Director's row stays unread. Array element ranges
        // are not expressible via [Range] (same WaveConfig.ZoneWeights
        // precedent) — SimConfigBuilder.Validate is the real gate, where one
        // exists (owner decision R-92: only a validation with a named
        // consequence is added).
        //          Outer  Middle  Core
        // Chaser:  0.10   0.10    0.00
        // Gunner:  0.10   0.10    0.00
        // Elite:   0.00   0.35    0.50
        // Director:0.00   0.00    0.00 (fixed drop rule, not a chance roll)
        public float[] DropChance =
        {
            0.10f, 0.10f, 0.00f,
            0.10f, 0.10f, 0.00f,
            0.00f, 0.35f, 0.50f,
            0.00f, 0.00f, 0.00f,
        };

        [Range(0, 64)] public int CrateCount = 8;
        [Range(0, 64)] public int CacheCountMiddle = 5;
        [Range(0, 64)] public int CacheCountCore = 2;
        [Range(0f, 1f)] public float RepairKitChance = 0.25f;

        // Stage 3 Task 13 (spec §3.7): indexed by MobType, same archetype
        // axis as DropChance above — replaces the four TEMPORARY per-archetype
        // MobSimConfig.CellsOnDeath copies (R-3) with one flat array.
        public int[] CellsPerMob = { 1, 1, 4, 20 };

        [Range(0f, 1f)] public float CorpseCellFraction = 0.5f;
        [Range(1f, 500f)] public float RepairKitHealAmount = 40f;
        [Range(0.1f, 30f)] public float RepairKitChannelSeconds = 2f;

        // Stage 3 Task 13 (spec §3.8, Р235): indexed by tier - 1 (tier 1..4).
        public float[] TransferSeconds = { 0.3f, 0.6f, 0.9f, 1.2f };

        [Range(1, 100)] public int LootSpawnAttempts = 16;
        [Range(0, 100)] public int LootFallbackSlots = 24;
        [Range(1f, 600f)] public float PickupTtlSeconds = 120f;
        [Range(1f, 600f)] public float ContainerTtlSeconds = 180f;

        // Stage 3 Task 13 (owner decision R-9, errata E-6 A-I8): spec §3.13's
        // table puts this on HeroConfig; the errata/ledger override — one
        // home next to every other loot number. Consumer: Т17.
        [Range(0.1f, 20f)] public float LootRadius = 3f; // sync-marker key — keep LAST

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
