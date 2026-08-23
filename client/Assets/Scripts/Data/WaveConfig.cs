using UnityEngine;

namespace Ring.Data
{
    /// Wave-spawning balance numbers (pacing, counts, spawn placement).
    ///
    /// ⚠ THE DEFAULTS HERE ARE NO LONGER A MIRROR OF
    /// Ring.Simulation.Tests.TestConfigs.Default().Wave, and saying they were
    /// stopped being true in Т2 (bd app-ggvz, spec §0/Р325). The cadence
    /// numbers below are the numbers OF THE GAME; the fixture deliberately
    /// ships its own cadence, because the golden scenarios in
    /// DeterminismTests run 3000-18000 ticks and the shipped ceilings would
    /// turn a determinism check into a load test.
    ///
    /// THE DIVERGENCE HAS TWO CAUSES AND TWO DIRECTIONS, so "the fixture is
    /// scaled down" is only half of it (amended in Т6). Т2's three cadence
    /// fields are indeed scaled down (the fixture is the smaller side); Т6's
    /// two are the opposite kind -- the OWNER RAISED the shipped number and
    /// the fixture stayed put, upward for BaseCount (16 shipped against the
    /// fixture's 4, decision К5) and DOWNWARD for EliteShareOuterGrowth
    /// (0.007 shipped against the fixture's 0.02, decision Р311), so on that
    /// last field the fixture is the LARGER of the two.
    ///
    /// Where the two sources agree they agree ON PURPOSE and
    /// ConfigTests.AssertWaveEqual pins them with plain equality. Where they
    /// differ, AssertWaveEqual EXCLUDES the field by name and the difference
    /// is pinned in three-part form (the BarrierTop precedent) at
    /// AssertWaveEqual's caller,
    /// ConfigTests.Build_DefaultAssets_MatchesTestConfigsBaseline -- so
    /// neither side can drift unnoticed.
    [CreateAssetMenu(menuName = "Ring/Wave Config", fileName = "WaveConfig")]
    public sealed class WaveConfig : ScriptableObject
    {
        [Range(0f, 60f)] public float FirstWaveDelay = 2.5f;
        [Range(0f, 20f)] public float SpawnRingInset = 2f;
        [Range(0f, 50f)] public float MinSpawnDistanceToPlayer = 8f;
        // Task Т6 (app-ggvz, owner decision К5): base wave size x4, 4 -> 16. At
        // three players the per-player wave scale multiplies BaseCount by
        // 1 + (playerCount - 1) * PerPlayerCountFrac = 1 + 2 * 0.7 = 2.4
        // (WaveSystem.CountForTest), so the first ring wave becomes
        // round(16 * 2.4) = 38 mobs instead of round(4 * 2.4) = 10.
        [Range(1, 50)] public int BaseCount = 16;
        [Range(0, 20)] public int CountGrowth = 2;
        // Stage 2 Task 16 (spec §3.4): 24 -> 36, headroom for the x2.4 three-player scale.
        // Stage 3 Task 12 (spec §3.13): 36 -> 72, because a single wave was
        // then split across all three rings and at 36 the core's share
        // rounded to three or four mobs while the periphery never filled at
        // all. bd app-ggvz Т4 removed the split — every ring now draws a
        // WHOLE wave of this size — so the number is a per-RING ceiling from
        // here on, and 72 is what one ring may hold from one wave.
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

        // Stage 3 Task 11 (spec §3.3 Р212/Р298, coordinator R-58): the
        // elite-composition numbers. ZoneWeights stood here until bd
        // app-ggvz Т4 (owner decision К3): with an independent wave per ring
        // there is no single budget left to apportion, so the weights had
        // nothing to weigh.
        //
        // Both are indexed by the raid's DIFFICULTY STEP from Т4 on, not by
        // a ring's own wave counter (spec Р315) — see
        // WaveSystem.DifficultyStepFor.
        //
        // Task Т6 (app-ggvz, decision Р311): EliteShareOuterGrowth 0.02 -> 0.007.
        // The outer ring's elite share is EliteShareOuterGrowth * (step - 1),
        // capped at EliteShareOuterCap (0.25), so it saturates at
        // step = 1 + EliteShareOuterCap / EliteShareOuterGrowth. At 0.02 that
        // was step 13.5 -> 14, i.e. FirstWaveDelay + 13 * DifficultyStepSeconds
        // = 2.5 + 260 = 262.5s (~4.5 min) into the raid. At 0.007 the cap
        // moves to step 37 (0.007 * 36 = 0.252 >= 0.25), i.e.
        // 2.5 + 36 * 20 = 722.5s (~12.0 min), matching the canonical ramp in
        // ADR-001 §3.1.
        [Range(0f, 1f)] public float EliteShareMiddle = 0.35f;
        [Range(0f, 1f)] public float EliteShareOuterGrowth = 0.007f;
        // Coordinator R-60: the fourth wave field, not a code constant —
        // CRITICAL RULE 6 (ADR-002 §4) puts every wave balance number in a
        // ScriptableObject, this one included, so the owner can retune the
        // periphery's difficulty ceiling on milestone В1 without a
        // recompile. Was the sync-marker key until app-ggvz's
        // DifficultyStepSeconds field below superseded it.
        [Range(0f, 1f)] public float EliteShareOuterCap = 0.25f;

        // Task Т2 (app-ggvz, spec §3.4/§3.8): four per-zone wave cadence
        // numbers — the pause between a ring's waves and its living-mob
        // ceiling are per RING (Zones.Count entries, Outer/Middle/Core, the
        // Zone enum's own declared order); the spawn-per-tick cap smooths a
        // wave's arrival across several ticks instead of seating it all at
        // once (spec Р317); the difficulty step is the divisor of the
        // clock-based difficulty curve (spec §3.3 Р315).
        //
        // WavePauseByZone and DifficultyStepSeconds are consumed by
        // WaveSystem as of Т4 (the cadence itself); MaxAliveByZone and
        // MaxSpawnsPerZonePerTick are still gated by SimConfigBuilder.Validate
        // alone and get their consumer in Т5. Ranges are not expressible per
        // element via [Range] (Unity clamps the whole field) --
        // SimConfigBuilder.Validate is the real gate, the same convention
        // ArenaConfig.ZoneRadius already follows.
        public float[] WavePauseByZone = { 20f, 30f, 30f };
        public int[] MaxAliveByZone = { 150, 110, 10 };
        [Range(1, 20)] public int MaxSpawnsPerZonePerTick = 2;
        // EliteShareOuterCap above was the sync-marker key until this field
        // superseded it -- see its own doc for the historical chain before
        // it.
        [Range(1f, 120f)] public float DifficultyStepSeconds = 20f; // sync-marker key — keep LAST

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
