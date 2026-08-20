using System;
using NUnit.Framework;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 11 (spec §3.3 Р211/Р212/Р250/Р298, coordinator R-50..R-63):
    /// the zone wave-spawn budget — WaveSystem.SplitByZones (the pure
    /// largest-remainder split), WaveSystem.PendingRef (the one (zone,
    /// archetype) -> WaveState field mapping), the elite/chaser/gunner mix
    /// within a zone, and Geometry.ZoneSpawnRingRadius (each zone's own wave
    /// spawn ring).
    ///
    /// Mutation discipline (coordinator notes, lesson 244/245/227): every
    /// zone-mix fixture below ISOLATES the zone under test via a ZoneWeights
    /// array like {1,0,0}/{0,1,0}/{0,0,1} — the OTHER two zones get zero
    /// budget, so their (irrelevant, potentially rounding-ambiguous) mix
    /// never has to be reasoned about, and the fixture's own numbers are
    /// picked so every expected count is exact integer arithmetic (no
    /// float .5 rounding-mode ambiguity anywhere).
    public class WaveZoneTests
    {
        // ------------------------------------------------------------------
        // WaveSystem.SplitByZones — pure largest-remainder split, no world.
        // ------------------------------------------------------------------

        [Test]
        public void SplitByZones_SumEqualsTotal_ForEveryTotalFromOneToFifty()
        {
            // Mutation M1 (largest remainder -> simple truncation): for
            // ANY of these fifty totals, floor(total*w) summed across the
            // three zones under-counts by the leftover the remainder step
            // exists to redistribute — the very first total (1) already
            // floors to [0,0,0], sum 0 != 1.
            ReadOnlySpan<float> weights = stackalloc float[] { 0.45f, 0.45f, 0.10f };
            for (int total = 1; total <= 50; total++)
            {
                Span<int> perZone = stackalloc int[3];
                WaveSystem.SplitByZones(total, weights, perZone);
                Assert.AreEqual(total, perZone[0] + perZone[1] + perZone[2],
                    $"total={total}: the three zone parts must sum back to the whole");
            }
        }

        [Test]
        public void SplitByZones_ZeroTotal_GivesThreeZeros()
        {
            // Errata E-7 ("SplitByZones(0) покрывается тестом"), kept
            // SEPARATE from the loop above (coordinator: widening that
            // test's own range to include 0 would leave its name
            // "...FromOneToFifty" describing something its body no longer
            // does — lesson 277).
            ReadOnlySpan<float> weights = stackalloc float[] { 0.45f, 0.45f, 0.10f };
            Span<int> perZone = stackalloc int[3];
            WaveSystem.SplitByZones(0, weights, perZone);
            CollectionAssert.AreEqual(new[] { 0, 0, 0 }, perZone.ToArray());
        }

        [Test]
        public void Debt_IsNeverLostOnRounding()
        {
            // Mutation M1's second victim: an adversarial single case where
            // floor-only truncation visibly loses a unit. weights
            // {0.2,0.3,0.5} against total 7 give exact shares [1.4,2.1,3.5]
            // -- floor sums to 1+2+3=6, one short. The largest remainder
            // (zone 2's 0.5) must claim the seventh unit, so the correct
            // split is [1,2,4].
            ReadOnlySpan<float> weights = stackalloc float[] { 0.2f, 0.3f, 0.5f };
            Span<int> perZone = stackalloc int[3];
            WaveSystem.SplitByZones(7, weights, perZone);
            Assert.AreEqual(7, perZone[0] + perZone[1] + perZone[2],
                "7 split by {0.2,0.3,0.5}: floor-only truncation gives 1+2+3=6, losing one " +
                "unit of debt that the largest-remainder step (zone 2, remainder 0.5) must " +
                "claim instead");
        }

        [Test]
        public void SplitByZones_IsDeterministic_ForEqualRemainders()
        {
            // Mutation M2 (fixed tie-break order -> a different order): a
            // dead tie between zone 0 and zone 1 (both floor to 0 with
            // remainder 0.5) must always resolve to the LOWER index (Zone's
            // own declared order, Outer first) -- flipping the scan's
            // comparison (`>` -> `>=`) would hand the one leftover unit to
            // zone 1 instead.
            ReadOnlySpan<float> weights = stackalloc float[] { 0.5f, 0.5f, 0f };
            Span<int> perZone = stackalloc int[3];
            WaveSystem.SplitByZones(1, weights, perZone);
            // Coordinator F1: one CollectionAssert over both zones instead
            // of two sequential Asserts -- a mutation that instead hands
            // the unit to zone 1 shows up in the SAME run, not masked by
            // zone 0 having already failed (or, under a DIFFERENT
            // mutation, having already passed and hidden zone 1's own
            // defect).
            CollectionAssert.AreEqual(new[] { 1, 0 }, new[] { perZone[0], perZone[1] },
                "a tied remainder between zones 0 and 1 must go to the LOWER index (fixed order)");
        }

        // ------------------------------------------------------------------
        // WaveSystem.PendingRef — the one (zone, archetype) -> field home.
        // ------------------------------------------------------------------

        [Test]
        public void PendingRef_EveryZoneArchetypePair_AddressesDistinctStorage()
        {
            // Coordinator R-51's mandatory sentinel, T10's
            // MobRadiusFor_AgreesWith_MobConfigFor... precedent: write a
            // DISTINCT value (1..9) through each of the nine pairs, then
            // read the raw fields back and confirm none collided. Mutation
            // M6 (two pairs swapped in PendingRef's switch) misplaces two of
            // the nine values, breaking the CollectionAssert below.
            (Zone zone, MobType type)[] pairs =
            {
                (Zone.Outer, MobType.Chaser), (Zone.Outer, MobType.Gunner), (Zone.Outer, MobType.Elite),
                (Zone.Middle, MobType.Chaser), (Zone.Middle, MobType.Gunner), (Zone.Middle, MobType.Elite),
                (Zone.Core, MobType.Chaser), (Zone.Core, MobType.Gunner), (Zone.Core, MobType.Elite),
            };
            WaveState w = default;
            for (int i = 0; i < pairs.Length; i++)
                WaveSystem.PendingRef(ref w, pairs[i].zone, pairs[i].type) = i + 1;

            int[] actual =
            {
                w.PendingOuterChaser, w.PendingOuterGunner, w.PendingOuterElite,
                w.PendingMiddleChaser, w.PendingMiddleGunner, w.PendingMiddleElite,
                w.PendingCoreChaser, w.PendingCoreGunner, w.PendingCoreElite,
            };
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, actual,
                "each (zone, type) pair through PendingRef must address its OWN field -- a " +
                "collision here means two pairs write the same storage");
        }

        [Test]
        public void PendingRef_UnknownTypeForZone_Throws()
        {
            // Spec Р251's own warning, applied to the debt matrix itself:
            // MobType.Director never spawns through a wave and has no
            // Pending field of its own -- PendingRef must refuse it loudly
            // rather than silently fall back onto a real archetype's field
            // (the RED stub does exactly that silent-fallback thing, which
            // is why this test is red at Step 2 too).
            WaveState w = default;
            Assert.Throws<ArgumentOutOfRangeException>(
                () => WaveSystem.PendingRef(ref w, Zone.Outer, MobType.Director));
        }

        // ------------------------------------------------------------------
        // Zone mix: elite share peeled off first, existing GunnerShare
        // splits what is left. Every fixture isolates ONE zone via
        // ZoneWeights so the OTHER two never spawn and never need reasoning
        // about.
        // ------------------------------------------------------------------

        [Test]
        public void OuterZone_GetsNoElite_OnFirstWave()
        {
            // Mutation M4 ((WaveIndex-1) -> WaveIndex in the growth term):
            // wave 1 is WaveIndex 1, so (WaveIndex-1)=0 and the real formula
            // gives exactly zero elite regardless of BaseCount. The mutant
            // reads WaveIndex directly (=1), giving
            // round(100*EliteShareOuterGrowth) = round(100*0.02) = 2 --
            // BaseCount is picked large enough (100) that this 2-vs-0 gap is
            // visible; a smaller zone budget could round the mutant back
            // down to 0 and hide the defect.
            var c = TestConfigs.Default();
            c.Arena.ZoneRadius = new[] { 20f, 40f };
            c.Wave.ZoneWeights = new[] { 1f, 0f, 0f };
            c.Wave.BaseCount = 100;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 1000;
            c.Wave.FirstWaveDelay = 0.1f;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f; // block every spawn -- debt freezes

            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);

            Assert.AreEqual(0, w.WaveRef.PendingOuterElite,
                "wave 1 (WaveIndex-1=0) must carry zero outer elite debt -- mutating " +
                "(WaveIndex-1) to WaveIndex would give round(100*0.02)=2");
        }

        [Test]
        public void OuterZone_EliteShareGrows_WithWaveIndex_UpToCap()
        {
            // Mutation M3 (EliteShareOuterCap dropped from the min()):
            // jumping straight to WaveIndex 20 (SetWaveForTest injects
            // WaveIndex=19, Phase=Waiting, PhaseTimer=0 -- StartWave
            // increments to 20 on the very next tick, same test seam
            // WorldLifecycleTests already uses) gives a real, capped share
            // of min(0.02*19, 0.25) = 0.25 -> round(100*0.25) = 25. The
            // uncapped mutant gives 0.38 -> round(100*0.38) = 38.
            var c = TestConfigs.Default();
            c.Arena.ZoneRadius = new[] { 20f, 40f };
            c.Wave.ZoneWeights = new[] { 1f, 0f, 0f };
            c.Wave.BaseCount = 100;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 1000;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f; // block every spawn -- debt freezes

            var w = new SimulationWorld(11, c);
            WaveState wv = w.WaveRef;
            wv.WaveIndex = 19;
            wv.Phase = WavePhase.Waiting;
            wv.PhaseTimer = 0f;
            w.SetWaveForTest(wv);
            w.Tick(default);

            Assert.AreEqual(25, w.WaveRef.PendingOuterElite,
                "wave 20: min(EliteShareOuterGrowth*19, EliteShareOuterCap) = min(0.38,0.25) " +
                "= 0.25 -> round(100*0.25) = 25 -- an uncapped share would give round(100*0.38) = 38");
        }

        [Test]
        public void OuterZone_EliteShareGrows_AtIntermediateWave_BelowCap()
        {
            // Coordinator F3: OuterZone_GetsNoElite_OnFirstWave (wave 1 ->
            // 0) and the cap test above (wave 20 -> capped at 25) only pin
            // the two ENDS of the growth curve -- mutation M12
            // (EliteShareOuterGrowth's own multiplier scaled, e.g. *2)
            // leaves BOTH of them green: wave 1 is still 0 no matter the
            // rate, wave 20 is still capped at 0.25 no matter the rate
            // (0.02*2*19=0.76 is capped exactly the same as 0.02*19=0.38).
            // This test pins a wave where the share has grown but has NOT
            // yet reached the cap: wave 6, min(0.02*5, 0.25) = 0.10 ->
            // round(100*0.10) = 10; the doubled-rate mutant gives
            // min(0.04*5, 0.25) = 0.20 -> round(100*0.20) = 20.
            var c = TestConfigs.Default();
            c.Arena.ZoneRadius = new[] { 20f, 40f };
            c.Wave.ZoneWeights = new[] { 1f, 0f, 0f };
            c.Wave.BaseCount = 100;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 1000;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f; // block every spawn -- debt freezes

            var w = new SimulationWorld(11, c);
            WaveState wv = w.WaveRef;
            wv.WaveIndex = 5;
            wv.Phase = WavePhase.Waiting;
            wv.PhaseTimer = 0f;
            w.SetWaveForTest(wv);
            w.Tick(default);

            Assert.AreEqual(10, w.WaveRef.PendingOuterElite,
                "wave 6: min(EliteShareOuterGrowth*5, EliteShareOuterCap) = min(0.10,0.25) = " +
                "0.10 -> round(100*0.10) = 10 -- a doubled growth rate would give " +
                "round(100*0.20) = 20");
        }

        [Test]
        public void MiddleZone_MixSumsToOne()
        {
            // Isolated Middle budget (10), EliteShareMiddle=0.4 (fixture,
            // not the .asset's 0.35) -> elites=round(10*0.4)=4 exactly;
            // remainder 6 split by the EXISTING GunnerShareBase=0.2 (Task
            // 16's own TestConfigs mirror, wave 1 so GunnerShareGrowth's
            // term is zero) -> gunners=round(6*0.2)=1, chasers=5.
            var c = TestConfigs.Default();
            c.Arena.ZoneRadius = new[] { 20f, 40f };
            c.Wave.ZoneWeights = new[] { 0f, 1f, 0f };
            c.Wave.EliteShareMiddle = 0.4f;
            c.Wave.BaseCount = 10;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 1000;
            c.Wave.FirstWaveDelay = 0.1f;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f; // block every spawn -- debt freezes

            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);

            int elite = w.WaveRef.PendingMiddleElite;
            int gunner = w.WaveRef.PendingMiddleGunner;
            int chaser = w.WaveRef.PendingMiddleChaser;
            // Coordinator F2: the name's own claim, asserted directly --
            // this holds for ANY elite/gunner share values as long as the
            // peel-then-subtract STRUCTURE holds (chaser = rest - gunner,
            // rest = budget - elite), a distinct risk from the exact share
            // VALUES below, so it can never mask the CollectionAssert that
            // follows (it is reached on every run, real or mutant, that
            // does not break the subtraction structure itself).
            Assert.AreEqual(10, elite + gunner + chaser,
                "the three-way mix must sum to the zone's own budget (10)");
            // Coordinator F1: one CollectionAssert over all three
            // components instead of three sequential Asserts -- exact
            // counts, not just their sum, so a wrong EliteShareMiddle
            // read-through cannot hide behind the sum invariant above.
            CollectionAssert.AreEqual(new[] { 4, 1, 5 }, new[] { elite, gunner, chaser },
                "elite=round(10*0.4)=4, gunner=round((10-4)*0.2)=1, chaser=remainder=5");
        }

        [Test]
        public void CoreZone_SpawnsOnlyElite()
        {
            // Isolated Core budget (7). Spec's own table: Core's elite share
            // is always 1, so the whole budget becomes elite regardless of
            // GunnerShare -- rest is 0, so chaser and gunner are 0 no matter
            // what GunnerShareBase/Growth happen to be.
            var c = TestConfigs.Default();
            c.Arena.ZoneRadius = new[] { 20f, 40f };
            c.Wave.ZoneWeights = new[] { 0f, 0f, 1f };
            c.Wave.BaseCount = 7;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 1000;
            c.Wave.FirstWaveDelay = 0.1f;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f; // block every spawn -- debt freezes

            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);

            // Coordinator F1: one CollectionAssert over all three
            // archetypes -- "only elite" is a claim about ALL THREE
            // fields at once, and a sequential Assert.AreEqual(7,
            // ...Elite) followed by Assert.AreEqual(0, ...Chaser +
            // ...Gunner) would leave the chaser-vs-gunner split itself
            // unobserved if the elite assert ever failed first.
            CollectionAssert.AreEqual(new[] { 7, 0, 0 },
                new[] { w.WaveRef.PendingCoreElite, w.WaveRef.PendingCoreChaser, w.WaveRef.PendingCoreGunner },
                "Core spawns ONLY elite -- the whole budget (7) as elite, zero chaser, zero gunner");
        }

        // ------------------------------------------------------------------
        // Stage 3 Т22 (spec §3.4 Р253/Р254, coordinator R-182/R-185): what the
        // Director's arrival does to the wave director itself — the slot
        // reserve held for the WHOLE raid, and the core leaving the wave
        // budget for good.
        // ------------------------------------------------------------------

        /// A wave fixture that will genuinely try to overfill the world: a
        /// small cap, one huge wave, and no spawn-distance rule to block it.
        /// The wave is aimed at the OUTER ring only, so nothing it spawns can
        /// be mistaken for the retinue standing in the core.
        static SimConfig ReserveFixture()
        {
            SimConfig c = TestConfigs.Open();
            c.Arena.MaxMobs = 12;
            c.Wave.FirstWaveDelay = 0.1f;
            c.Wave.BaseCount = 40;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 40;
            c.Wave.ZoneWeights = new[] { 1f, 0f, 0f };
            c.Wave.MinSpawnDistanceToPlayer = 0f;
            return c;
        }

        [Test]
        public void WaveSpawnStopsAtTheReserveCeiling_AndKeepsItsDebt()
        {
            SimConfig c = ReserveFixture();
            var w = new SimulationWorld(1, c, playerCount: 3);

            TestWorlds.IdleTicks(w, 120);

            int ceiling = c.Arena.MaxMobs - c.Flow.DirectorReserveSlots;
            Assert.AreEqual(ceiling, w.MobCount,
                "the wave stops at MaxMobs - DirectorReserveSlots and holds there for the whole " +
                "raid (Р254): the activation cannot be predicted, so the slots must be free ALWAYS");
            Assert.Greater(w.WaveRef.PendingTotal, 0,
                "the units it could not place stay as debt, exactly like the existing cap branch");
            Assert.AreEqual(0, w.WorldStats.MobSpawnsSkipped,
                "MobSpawnsSkipped counts the world hitting its PHYSICAL cap (SpawnMob's own " +
                "contract) — the reserve is the wave director's own policy, not an arena refusal");
        }

        [Test]
        public void CoreLosesItsWaveBudget_AfterActivation()
        {
            SimConfig c = TestConfigs.Open();
            c.Wave.ZoneWeights = new[] { 0f, 0f, 1f }; // every unit would go to the core...
            c.Wave.BaseCount = 8;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 100;
            c.Wave.FirstWaveDelay = 1e6f;             // ...but no wave starts until we allow it
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f; // block every spawn -- debt freezes

            var w = new SimulationWorld(11, c, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(c.Arena.ZoneRadius[0] * 0.5f, 0f));
            TestWorlds.IdleTicks(w);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase, "premise: activated");

            WaveState wave = w.WaveRef;
            wave.PhaseTimer = SimulationWorld.TickDt; // let the next tick start the wave
            w.SetWaveForTest(in wave);
            TestWorlds.IdleTicks(w);

            Assert.AreEqual(0, w.WaveRef.PendingCoreElite + w.WaveRef.PendingCoreChaser
                + w.WaveRef.PendingCoreGunner,
                "with the Director standing there the core stops receiving wave budget (spec §3.4): " +
                "a boss fight plus a live wave in the same room is a mess MVP balance cannot win");
        }

        [Test]
        public void CoreBudgetMovesToMiddle_TotalUnchanged()
        {
            SimConfig c = TestConfigs.Open();
            c.Wave.ZoneWeights = new[] { 0f, 0.5f, 0.5f }; // half the wave would be the core's
            c.Wave.BaseCount = 8;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 100;
            c.Wave.FirstWaveDelay = 1e6f;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f;

            var w = new SimulationWorld(11, c, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(c.Arena.ZoneRadius[0] * 0.5f, 0f));
            TestWorlds.IdleTicks(w);

            WaveState wave = w.WaveRef;
            wave.PhaseTimer = SimulationWorld.TickDt;
            w.SetWaveForTest(in wave);
            TestWorlds.IdleTicks(w);

            int middle = w.WaveRef.PendingMiddleElite + w.WaveRef.PendingMiddleChaser
                + w.WaveRef.PendingMiddleGunner;
            // The whole wave, stated the way the wave director states it — the
            // per-player scale is part of the size (three players here), so
            // BaseCount alone would be a different number wearing the same name.
            int waveSize = WaveSystem.CountForTest(in c.Wave, 0, w.PlayerCount);
            Assert.AreEqual(waveSize, middle,
                "the core's share MOVES to the middle zone (spec §3.4), it is not lost — a wave " +
                "that quietly shrank would be a silent break of Р211's own closing-debt rule");
        }

        [Test]
        public void CoreDoesNotRegainBudget_AfterTheDirectorDies()
        {
            SimConfig c = TestConfigs.Open();
            c.Wave.ZoneWeights = new[] { 0f, 0f, 1f };
            c.Wave.BaseCount = 8;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 100;
            c.Wave.FirstWaveDelay = 1e6f;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f;
            c.Flow.GateDelaySeconds = 2f * SimulationWorld.TickDt;

            var w = new SimulationWorld(11, c, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(c.Arena.ZoneRadius[0] * 0.5f, 0f));
            TestWorlds.IdleTicks(w);

            for (int i = 0; i < w.MobCount; i++)
            {
                if (w.Mobs[i].Type != MobType.Director) continue;
                w.DamageMob(i, 1e9f, w.Mobs[i].Pos, HitZone.Body, float2.zero, ownerIndex: 1);
                break;
            }
            TestWorlds.IdleTicks(w, 5);
            Assert.AreEqual(MatchPhase.GateOpen, w.Match.Phase, "premise: the gate has opened");

            WaveState wave = w.WaveRef;
            wave.PhaseTimer = SimulationWorld.TickDt;
            w.SetWaveForTest(in wave);
            TestWorlds.IdleTicks(w);

            Assert.AreEqual(0, w.WaveRef.PendingCoreElite + w.WaveRef.PendingCoreChaser
                + w.WaveRef.PendingCoreGunner,
                "the budget does NOT come back after his death (Р253): the sharing window over his " +
                "body has to pass without fresh elites, or it stops being a window");
        }

        // ------------------------------------------------------------------
        // Geometry.ZoneSpawnRingRadius — each zone's own wave spawn ring.
        // Coordinator F1 (T8's ZoneOf_On*Boundary precedent): three
        // SEPARATE tests, one per switch branch -- under mutation M5 the
        // Middle assert alone used to fail and stop the method, leaving
        // the Core branch unexercised in that very run. Split, each branch
        // now has its own pass/fail independent of its siblings'.
        // ------------------------------------------------------------------

        static readonly ArenaSimConfig SpawnRingArena =
            new ArenaSimConfig { Radius = 80f, ZoneRadius = new[] { 20f, 40f } };
        const float SpawnRingInset = 2f;

        [Test]
        public void OuterZone_SpawnRing_IsInsideOuterZone()
        {
            // Mutation M5 (spawn ring formula always Radius-Inset): Outer's
            // own formula already IS Radius-Inset, so this branch alone
            // cannot distinguish real from mutant -- kept as a real branch
            // test anyway (a wrong zone-to-boundary mapping elsewhere, not
            // just M5, would still show up here).
            float outerRing = Geometry.ZoneSpawnRingRadius(Zone.Outer, in SpawnRingArena, SpawnRingInset);
            Assert.AreEqual(Zone.Outer, Geometry.ZoneOf(new float2(outerRing, 0f), in SpawnRingArena),
                $"Outer zone's own spawn ring (radius={outerRing}) must lie in the Outer zone");
        }

        [Test]
        public void MiddleZone_SpawnRing_IsInsideMiddleZone()
        {
            // Mutation M5's visible branch: the mutant's Radius-Inset
            // (78) reads as Outer (78 > 40), not Middle.
            float middleRing = Geometry.ZoneSpawnRingRadius(Zone.Middle, in SpawnRingArena, SpawnRingInset);
            Assert.AreEqual(Zone.Middle, Geometry.ZoneOf(new float2(middleRing, 0f), in SpawnRingArena),
                $"Middle zone's own spawn ring (radius={middleRing}) must lie in the Middle zone");
        }

        [Test]
        public void CoreZone_SpawnRing_IsInsideCoreZone()
        {
            // Mutation M5's other visible branch: the mutant's
            // Radius-Inset (78) reads as Outer (78 > 40), not Core.
            float coreRing = Geometry.ZoneSpawnRingRadius(Zone.Core, in SpawnRingArena, SpawnRingInset);
            Assert.AreEqual(Zone.Core, Geometry.ZoneOf(new float2(coreRing, 0f), in SpawnRingArena),
                $"Core zone's own spawn ring (radius={coreRing}) must lie in the Core zone");
        }

        [Test]
        public void ZoneSpawnRingRadius_MiddleOrCoreOnZonelessArena_ThrowsNamedInvariantViolation()
        {
            // Coordinator R-64: the zoneless-routing invariant
            // (WaveSystem.StartWave's ZonelessWeights + SplitByZones,
            // R-53) is NONLOCAL -- held by two call sites, paid for by a
            // third. Batch 2's rejected mutation run (M2+M11 together)
            // demonstrated exactly this failure mode live: a bare
            // IndexOutOfRangeException four frames deep, naming no broken
            // rule. This is that guard's own witness, not a mutation
            // target (coordinator: "мутация на этот гард не нужна -- его
            // свидетель и есть тест") -- defensive-only, documented in
            // ZoneSpawnRingRadius's own doc as an ASSUMPTION + ADDRESSEE
            // pair, same precedent as PushOutOfArc's "start inside" guard
            // (R-36).
            var zonelessArena = new ArenaSimConfig { Radius = 80f, ZoneRadius = System.Array.Empty<float>() };
            Assert.Throws<System.InvalidOperationException>(
                () => Geometry.ZoneSpawnRingRadius(Zone.Middle, in zonelessArena, 2f),
                "Middle on a zoneless arena must throw a NAMED invariant violation, not a bare " +
                "IndexOutOfRangeException");
            Assert.Throws<System.InvalidOperationException>(
                () => Geometry.ZoneSpawnRingRadius(Zone.Core, in zonelessArena, 2f),
                "Core on a zoneless arena must throw a NAMED invariant violation, not a bare " +
                "IndexOutOfRangeException");
        }
    }
}
