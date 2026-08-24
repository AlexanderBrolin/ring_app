using System;
using NUnit.Framework;
using Ring.Data;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 11 (spec §3.3 Р212/Р250/Р298, coordinator R-50..R-63):
    /// what a ring's own wave is made of — WaveSystem.PendingRef (the one
    /// archetype -> WaveState field mapping), the elite/chaser/gunner mix
    /// inside a ring, and Geometry.ZoneSpawnRingRadius (each ring's own wave
    /// spawn ring).
    ///
    /// ⚠ REWRITTEN IN Т4 (bd app-ggvz, owner decision К3). The zone BUDGET is
    /// gone: there is no single wave to apportion any more, every ring draws
    /// a whole one, and with it went WaveSystem.SplitByZones and
    /// Wave.ZoneWeights. Three largest-remainder tests and
    /// CoreBudgetMovesToMiddle_TotalUnchanged (Р253's own witness, deleted
    /// with Р312) went with them, because the mechanisms they described no
    /// longer exist; Debt_IsNeverLostOnRounding stayed, retargeted at the
    /// split that does.
    ///
    /// Mutation discipline (coordinator notes, lesson 244/245/227): the mix
    /// fixtures below no longer need to isolate a ring — each ring's wave is
    /// independent by construction, so a fixture simply asserts on the ring
    /// it names and never has to reason about its neighbors. Their numbers
    /// are still picked so every expected count is exact integer arithmetic
    /// (no float .5 rounding-mode ambiguity anywhere), and every one of them
    /// blocks placement outright (MinSpawnDistanceToPlayer far past the
    /// arena) so the DEBT stands still to be read.
    ///
    /// ⚠ WaveState.WaveIndex IS THE RAID'S DIFFICULTY STEP from Т4 on, not a
    /// ring's wave ordinal (spec Р315). It can no longer be injected through
    /// SetWaveForTest — StartWave ASSIGNS it from the world's tick — so the
    /// growth-curve tests below drive the CLOCK instead, with a fixture whose
    /// pause and difficulty step are the same two ticks: wave number k then
    /// lands on tick FirstDelay + 2*(k-1) carrying step k exactly.
    public class WaveZoneTests
    {
        /// The shape every mix fixture below shares (rule 2 — before Т4 the
        /// same six lines were copied into five of them): a three-ring arena
        /// small enough to state, a first wave a few ticks in, and placement
        /// blocked outright, so the DEBT stands still to be read instead of
        /// turning into mobs.
        ///
        /// THE PAUSE AND THE DIFFICULTY STEP ARE BOTH TWO TICKS, which is what
        /// makes the clock legible here: wave k starts on tick
        /// FirstWaveTick + 2*(k - 1) and carries difficulty step k exactly.
        /// Two ticks is also SimConfigBuilder.Validate's own floor for both
        /// numbers (Р336), so this is a configuration the game would accept
        /// rather than one only a hand-built fixture can reach.
        static SimConfig MixFixture()
        {
            SimConfig c = TestConfigs.Default();
            c.Arena.ZoneRadius = new[] { 20f, 40f };
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 1000;
            c.Wave.FirstWaveDelay = 0.1f;
            float twoTicks = 2f * SimulationWorld.TickDt;
            c.Wave.WavePauseByZone = new[] { twoTicks, twoTicks, twoTicks };
            c.Wave.DifficultyStepSeconds = twoTicks;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f; // block every spawn -- debt freezes
            return c;
        }

        /// The tick every ring's FIRST wave lands on.
        static int FirstWaveTick(in SimConfig c) =>
            SimulationWorld.TicksFromSeconds(c.Wave.FirstWaveDelay);

        /// The tick a wave carrying difficulty `step` starts on, for
        /// MixFixture's own two-tick pause and two-tick step. Stated as
        /// arithmetic over the fixture rather than by calling
        /// WaveSystem.DifficultyStepFor — a test must not ask the function
        /// under test what to expect (lesson 428).
        static int TickOfStep(in SimConfig c, int step) =>
            FirstWaveTick(in c) + 2 * (step - 1);

        // ------------------------------------------------------------------
        // The three-way split inside ONE ring — no world needed beyond the
        // debt it leaves standing.
        // ------------------------------------------------------------------

        [Test]
        public void Debt_IsNeverLostOnRounding()
        {
            // ⚠ REWRITTEN IN Т4 against the split that still exists. Until Т4
            // this test guarded SplitByZones' largest-remainder step, where
            // floor-only truncation visibly lost units ({0.2,0.3,0.5} of 7
            // floors to 1+2+3=6). That apportionment is gone with the shared
            // budget, but the CLAIM survives one level down, where it is now
            // the only place a wave can lose a mob to rounding: StartWave
            // peels the elite share off with round(), then splits the
            // REMAINDER with round() again.
            //
            // The structure that makes it exact is `rest = count - elites`
            // and `chasers = rest - gunners` — subtraction, never a third
            // round(). A mutant that computed chasers as
            // `round(rest * (1 - gunnerShare))` instead would lose or invent
            // a mob on exactly the half-integers this sweep walks over.
            SimConfig c = MixFixture();
            c.Wave.EliteShareMiddle = 0.5f;   // half of an odd count is a half-integer
            for (int count = 1; count <= 40; count++)
            {
                SimConfig one = c;
                one.Wave.BaseCount = count;
                var w = new SimulationWorld(11, one);
                TestWorlds.IdleTicks(w, FirstWaveTick(in one));

                WaveState mid = w.WaveRef(Zone.Middle);
                Assert.AreEqual(count, mid.PendingTotal,
                    $"count={count}: the three archetype debts must sum back to the ring's " +
                    "whole wave — a mob lost to rounding is a debt that can never close");
            }
        }

        // ------------------------------------------------------------------
        // WaveSystem.PendingRef — the one (zone, archetype) -> field home.
        // ------------------------------------------------------------------

        [Test]
        public void PendingRef_EveryArchetypeOfEveryRingInstance_AddressesDistinctStorage()
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
            // Wave-cadence-per-zone (bd app-ggvz Т3): the zone half of the
            // pair moved out of the field NAMES and into the index of the
            // WaveState instance, so the nine distinct storages are three
            // fields in each of three instances -- the claim under test is
            // unchanged.
            var waves = new WaveState[Zones.Count];
            for (int i = 0; i < pairs.Length; i++)
                WaveSystem.PendingRef(ref waves[(int)pairs[i].zone], pairs[i].type) = i + 1;

            int[] actual =
            {
                waves[(int)Zone.Outer].PendingChaser, waves[(int)Zone.Outer].PendingGunner,
                waves[(int)Zone.Outer].PendingElite,
                waves[(int)Zone.Middle].PendingChaser, waves[(int)Zone.Middle].PendingGunner,
                waves[(int)Zone.Middle].PendingElite,
                waves[(int)Zone.Core].PendingChaser, waves[(int)Zone.Core].PendingGunner,
                waves[(int)Zone.Core].PendingElite,
            };
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, actual,
                "each (zone, type) pair through PendingRef must address its OWN field -- a " +
                "collision here means two pairs write the same storage");
        }

        [Test]
        public void PendingRef_UnknownArchetype_Throws()
        {
            // Spec Р251's own warning, applied to the debt matrix itself:
            // MobType.Director never spawns through a wave and has no
            // Pending field of its own -- PendingRef must refuse it loudly
            // rather than silently fall back onto a real archetype's field
            // (the RED stub does exactly that silent-fallback thing, which
            // is why this test is red at Step 2 too).
            WaveState w = default;
            Assert.Throws<ArgumentOutOfRangeException>(
                () => WaveSystem.PendingRef(ref w, MobType.Director));
        }

        // ------------------------------------------------------------------
        // Ring mix: elite share peeled off first, the existing GunnerShare
        // splits what is left. Each fixture asserts on the ring it names;
        // since Т4 the neighbors run their own independent waves and never
        // need reasoning about.
        // ------------------------------------------------------------------

        [Test]
        public void OuterZone_GetsNoElite_OnFirstWave()
        {
            // Mutation M4 ((WaveIndex-1) -> WaveIndex in the growth term):
            // the first wave is difficulty step 1, so (WaveIndex-1)=0 and the real formula
            // gives exactly zero elite regardless of BaseCount. The mutant
            // reads WaveIndex directly (=1), giving
            // round(100*EliteShareOuterGrowth) = round(100*0.02) = 2 --
            // BaseCount is picked large enough (100) that this 2-vs-0 gap is
            // visible; a smaller zone budget could round the mutant back
            // down to 0 and hide the defect.
            SimConfig c = MixFixture();
            c.Wave.BaseCount = 100;

            var w = new SimulationWorld(11, c);
            TestWorlds.IdleTicks(w, FirstWaveTick(in c));

            Assert.AreEqual(0, w.WaveRef(Zone.Outer).PendingElite,
                "step 1 (WaveIndex-1=0) must carry zero outer elite debt -- mutating " +
                "(WaveIndex-1) to WaveIndex would give round(100*0.02)=2");
        }

        [Test]
        public void OuterZone_EliteShareGrows_WithWaveIndex_UpToCap()
        {
            // Mutation M3 (EliteShareOuterCap dropped from the min()):
            // difficulty step 20 gives a real, capped share of
            // min(0.02*19, 0.25) = 0.25 -> round(100*0.25) = 25. The uncapped
            // mutant gives 0.38 -> round(100*0.38) = 38.
            //
            // ⚠ THE STEP IS REACHED BY TICKING, not by injecting it: since Т4
            // StartWave ASSIGNS WaveIndex from the world's own tick (Р315), so
            // the old seam — SetWaveForTest with WaveIndex=19 and a spent
            // timer — would be silently overwritten and this test would have
            // measured step 1 while claiming to measure step 20.
            SimConfig c = MixFixture();
            c.Wave.BaseCount = 100;

            var w = new SimulationWorld(11, c);
            TestWorlds.IdleTicks(w, TickOfStep(in c, 20));

            Assert.AreEqual(25, w.WaveRef(Zone.Outer).PendingElite,
                "step 20: min(EliteShareOuterGrowth*19, EliteShareOuterCap) = min(0.38,0.25) " +
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
            // This test pins a step where the share has grown but has NOT
            // yet reached the cap: step 6, min(0.02*5, 0.25) = 0.10 ->
            // round(100*0.10) = 10; the doubled-rate mutant gives
            // min(0.04*5, 0.25) = 0.20 -> round(100*0.20) = 20.
            SimConfig c = MixFixture();
            c.Wave.BaseCount = 100;

            var w = new SimulationWorld(11, c);
            TestWorlds.IdleTicks(w, TickOfStep(in c, 6));

            Assert.AreEqual(10, w.WaveRef(Zone.Outer).PendingElite,
                "step 6: min(EliteShareOuterGrowth*5, EliteShareOuterCap) = min(0.10,0.25) = " +
                "0.10 -> round(100*0.10) = 10 -- a doubled growth rate would give " +
                "round(100*0.20) = 20");
        }

        [Test]
        public void OuterZone_StillSpawnsChasers_AtTheStepTheOldGunnerCurveSaturated()
        {
            // Task app-jmb2 (owner decision Р347). THE TWO SHARE NUMBERS COME
            // FROM THE SHIPPED SO, NOT FROM THE FIXTURE, and that is the whole
            // point of this test: TestConfigs deliberately stays at the OLD
            // GunnerShareGrowth (0.05) so the golden digests never move
            // (ConfigTests' own sixth divergence note), so a fixture-valued
            // test here would be green on both states of the tree and witness
            // nothing (lesson 447/451).
            //
            // STEP 17 IS THE OLD NUMBER'S OWN SATURATION POINT:
            // 0.2 + 0.05 * (17 - 1) = 1.0 exactly, which is 5.4 minutes into a
            // raid at the shipped 20-second difficulty step. From there the
            // saturated share handed the WHOLE non-elite remainder to gunners
            // and chasers stopped spawning on every ring for the rest of the
            // raid. The retuned number leaves 0.2 + 0.0135 * 16 = 0.416, so
            // the remainder still splits.
            //
            // The assertion is "chasers exist", not their exact count: the
            // shipped share is a balance number the owner tunes (spec §0),
            // and what must survive a retune is that both kinds of pressure
            // reach the player, not one particular split.
            var shipped = ScriptableObject.CreateInstance<WaveConfig>();
            try
            {
                SimConfig c = MixFixture();
                c.Wave.BaseCount = 100;
                c.Wave.GunnerShareBase = shipped.GunnerShareBase;
                c.Wave.GunnerShareGrowth = shipped.GunnerShareGrowth;

                var w = new SimulationWorld(11, c);
                TestWorlds.IdleTicks(w, TickOfStep(in c, 17));

                int elite = w.WaveRef(Zone.Outer).PendingElite;
                int gunner = w.WaveRef(Zone.Outer).PendingGunner;
                int chaser = w.WaveRef(Zone.Outer).PendingChaser;
                // The structural half, same role as MiddleZone_MixSumsToOne's
                // own sum assertion: it holds for any share values as long as
                // chaser = rest - gunner survives, so it can never mask the
                // claim below.
                Assert.AreEqual(100, elite + gunner + chaser,
                    "the three-way mix must sum to the ring's own wave (100)");
                Assert.Greater(chaser, 0,
                    $"step 17 gave elite={elite}, gunner={gunner}, chaser={chaser} — a "
                    + "gunner share that has reached 1 by this step takes the entire "
                    + "non-elite remainder and the arena stops producing chasers for "
                    + "the rest of the raid");
            }
            finally
            {
                // Fully qualified: this file's own `using System;` makes a
                // bare `Object` ambiguous against `object`.
                UnityEngine.Object.DestroyImmediate(shipped);
            }
        }

        [Test]
        public void MiddleZone_MixSumsToOne()
        {
            // The Middle ring's own wave (10), EliteShareMiddle=0.4 (fixture,
            // not the .asset's 0.35) -> elites=round(10*0.4)=4 exactly;
            // remainder 6 split by the EXISTING GunnerShareBase=0.2 (Task
            // 16's own TestConfigs mirror, step 1 so GunnerShareGrowth's
            // term is zero) -> gunners=round(6*0.2)=1, chasers=5.
            SimConfig c = MixFixture();
            c.Wave.EliteShareMiddle = 0.4f;
            c.Wave.BaseCount = 10;

            var w = new SimulationWorld(11, c);
            TestWorlds.IdleTicks(w, FirstWaveTick(in c));

            int elite = w.WaveRef(Zone.Middle).PendingElite;
            int gunner = w.WaveRef(Zone.Middle).PendingGunner;
            int chaser = w.WaveRef(Zone.Middle).PendingChaser;
            // Coordinator F2: the name's own claim, asserted directly --
            // this holds for ANY elite/gunner share values as long as the
            // peel-then-subtract STRUCTURE holds (chaser = rest - gunner,
            // rest = budget - elite), a distinct risk from the exact share
            // VALUES below, so it can never mask the CollectionAssert that
            // follows (it is reached on every run, real or mutant, that
            // does not break the subtraction structure itself).
            Assert.AreEqual(10, elite + gunner + chaser,
                "the three-way mix must sum to the ring's own wave (10)");
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
            // The Core ring's own wave (7). Spec's own table: Core's elite
            // share is always 1, so the whole wave becomes elite regardless of
            // GunnerShare -- rest is 0, so chaser and gunner are 0 no matter
            // what GunnerShareBase/Growth happen to be. The core is running at
            // all here because the match is still in Farm (§3.6).
            SimConfig c = MixFixture();
            c.Wave.BaseCount = 7;

            var w = new SimulationWorld(11, c);
            TestWorlds.IdleTicks(w, FirstWaveTick(in c));

            // Coordinator F1: one CollectionAssert over all three
            // archetypes -- "only elite" is a claim about ALL THREE
            // fields at once, and a sequential Assert.AreEqual(7,
            // ...Elite) followed by Assert.AreEqual(0, ...Chaser +
            // ...Gunner) would leave the chaser-vs-gunner split itself
            // unobserved if the elite assert ever failed first.
            CollectionAssert.AreEqual(new[] { 7, 0, 0 },
                new[] { w.WaveRef(Zone.Core).PendingElite, w.WaveRef(Zone.Core).PendingChaser,
                    w.WaveRef(Zone.Core).PendingGunner },
                "Core spawns ONLY elite -- its whole wave (7) as elite, zero chaser, zero gunner");
        }

        // ------------------------------------------------------------------
        // Stage 3 Т22 (spec §3.4 Р253/Р254, coordinator R-182/R-185): what the
        // Director's arrival does to the wave director itself — the slot
        // reserve held for the WHOLE raid, and the core leaving the wave
        // budget for good.
        // ------------------------------------------------------------------

        /// A wave fixture that will genuinely try to overfill the world: a
        /// small cap, one huge wave per ring, and no spawn-distance rule to
        /// block it.
        ///
        /// ⚠ Т4: the ZoneWeights = {1,0,0} line that used to aim the wave at
        /// the OUTER ring is gone with the weights themselves, and it is not
        /// missed. Update walks the rings in the fixed order Outer -> Middle
        /// -> Core, and the outer ring alone owes forty mobs against a
        /// ceiling of nine, so the reserve is already spent before the middle
        /// ring is reached and the core still spawns nothing at all — the very
        /// property the old line bought, now a consequence of the cadence
        /// rather than of a fixture number.
        static SimConfig ReserveFixture()
        {
            SimConfig c = TestConfigs.Open();
            c.Arena.MaxMobs = 12;
            c.Wave.FirstWaveDelay = 0.1f;
            c.Wave.BaseCount = 40;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 40;
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
            Assert.Greater(w.WaveRef(Zone.Outer).PendingTotal, 0,
                "the units it could not place stay as debt, exactly like the existing cap branch");
            Assert.AreEqual(0, w.WorldStats.MobSpawnsSkipped,
                "MobSpawnsSkipped counts the world hitting its PHYSICAL cap (SpawnMob's own " +
                "contract) — the reserve is the wave director's own policy, not an arena refusal");
        }

        [Test]
        public void CoreLosesItsWaveBudget_AfterActivation()
        {
            // ⚠ REWRITTEN IN Т4 to the FREEZE semantics (spec §3.3/§3.6). The
            // core does not "stop receiving budget" any more — there is no
            // budget to receive; the ring is switched off outright, and the
            // claim is now three-fold: phase back to Waiting, timer at zero,
            // no debt. All three, because a ring left Active with a spent
            // timer would fire a wave the instant the phase changed back, and
            // a ring left holding debt would seat it the same tick.
            SimConfig c = TestConfigs.Open();
            c.Wave.BaseCount = 8;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 100;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f; // block every spawn -- debt freezes
            // The core has to have been RUNNING before the Director woke, or
            // "it fell silent" is indistinguishable from "it never started":
            // one tick of Farm seats a full core wave (8 elites of debt), and
            // the activation on the next tick is what has to clear it.
            c.Wave.FirstWaveDelay = SimulationWorld.TickDt;

            var w = new SimulationWorld(11, c, playerCount: 3);
            TestWorlds.IdleTicks(w);
            Assert.Greater(w.WaveRef(Zone.Core).PendingTotal, 0,
                "premise: the core ran a wave of its own while the match was still Farm");

            TestWorlds.RelocatePlayerForTest(w, 1, TestWorlds.InsideCore(in c));
            TestWorlds.IdleTicks(w);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase, "premise: activated");

            // ONE MORE TICK, and the reason is the system order, not padding:
            // WaveSystem.Update runs next-to-last and MatchFlowSystem.Update
            // last, so the tick that ACTIVATES the Director had already asked
            // the wave director its question while the match was still Farm.
            // The core is frozen on the first tick that sees the new phase,
            // which is this one.
            TestWorlds.IdleTicks(w);

            WaveState core = w.WaveRef(Zone.Core);
            CollectionAssert.AreEqual(
                new[] { (int)WavePhase.Waiting, 0, 0 },
                new[] { (int)core.Phase, core.PhaseTicks, core.PendingTotal },
                "with the Director standing there the core is frozen outright (spec §3.6): " +
                "phase Waiting, timer zero, no debt — a boss fight plus a live wave in the " +
                "same room is a mess MVP balance cannot win");
        }

        [Test]
        public void CoreDoesNotRegainBudget_AfterTheDirectorDies()
        {
            // ⚠ REWRITTEN IN Т4 to the freeze semantics, same as
            // CoreLosesItsWaveBudget_AfterActivation above. The half this test
            // owns is the ONE-WAY LATCH: `!= Farm` rather than
            // `== DirectorActive`, so GateOpen keeps the core switched off.
            // The timer is forced spent before the last tick precisely so a
            // mutant that only froze DirectorActive would have to start a
            // wave here and be caught.
            SimConfig c = TestConfigs.Open();
            c.Wave.BaseCount = 8;
            c.Wave.CountGrowth = 0;
            c.Wave.MaxMobsPerWave = 100;
            c.Wave.FirstWaveDelay = 1e6f;
            c.Wave.MinSpawnDistanceToPlayer = 1_000_000f;
            c.Flow.GateDelaySeconds = 2f * SimulationWorld.TickDt;

            var w = new SimulationWorld(11, c, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 1, TestWorlds.InsideCore(in c));
            TestWorlds.IdleTicks(w);

            for (int i = 0; i < w.MobCount; i++)
            {
                if (w.Mobs[i].Type != MobType.Director) continue;
                w.DamageMob(i, 1e9f, w.Mobs[i].Pos, HitZone.Body, float2.zero, ownerIndex: 1);
                break;
            }
            TestWorlds.IdleTicks(w, 5);
            Assert.AreEqual(MatchPhase.GateOpen, w.Match.Phase, "premise: the gate has opened");

            WaveState armed = w.WaveRef(Zone.Core);
            armed.PhaseTicks = 1; // a spent timer: an unfrozen core WOULD start a wave now
            w.SetWaveForTest(Zone.Core, in armed);
            TestWorlds.IdleTicks(w);

            WaveState core = w.WaveRef(Zone.Core);
            CollectionAssert.AreEqual(
                new[] { (int)WavePhase.Waiting, 0, 0 },
                new[] { (int)core.Phase, core.PhaseTicks, core.PendingTotal },
                "the core does NOT come back after his death (Р253): the sharing window over his " +
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
            // Coordinator R-64: the zoneless-routing invariant is NONLOCAL --
            // held elsewhere (WaveSystem.RingIsFrozen since Т4; the
            // ZonelessWeights + SplitByZones pair before it), paid for here.
            // Batch 2's rejected mutation run (M2+M11 together)
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
