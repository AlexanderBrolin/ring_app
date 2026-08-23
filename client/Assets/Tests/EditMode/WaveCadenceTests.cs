using NUnit.Framework;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WaveCadenceTests
    {
        /// The two fixture numbers every cadence test below counts in, stated
        /// ONCE and in TICKS. A test that spells the conversion out at each
        /// call site reads as arithmetic and hides which ring it is talking
        /// about; and the numbers themselves stay where §0's two-sources rule
        /// puts them — in TestConfigs, never as a literal here.
        static int OuterPause(in SimConfig cfg) =>
            SimulationWorld.TicksFromSeconds(cfg.Wave.WavePauseByZone[(int)Zone.Outer]);

        static int FirstDelay(in SimConfig cfg) =>
            SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay);

        [Test]
        public void SpawnZone_IsSetByTheSpawner_NotByPosition()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            // A MIDDLE-ring mob is placed at a point that geometrically lies
            // in the outer ring: the attribution must follow the spawner,
            // not the coordinate.
            int id = w.SpawnMobForTest(MobType.Chaser,
                new float2(cfg.Arena.Radius - 1f, 0f), Zone.Middle);
            Assert.GreaterOrEqual(id, 0, "моб не заспавнился");
            Assert.AreEqual(Zone.Middle, w.Mobs[w.MobCount - 1].SpawnZone);
        }

        [Test]
        public void ProductionSpawn_HasNoDefaultForZone()
        {
            // Guard Р324: the test seam's convenience must not leak into
            // production.
            var m = typeof(SimulationWorld).GetMethod("SpawnMob",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(m, "SpawnMob не найден");
            var ps = m.GetParameters();
            Assert.AreEqual(3, ps.Length, "у производственного SpawnMob должно быть три параметра");
            Assert.IsFalse(ps[2].HasDefaultValue,
                "зона в производственном SpawnMob обязана быть обязательной");
        }

        [Test]
        public void SaveState_DoesNotAliasTheLiveWaveArray()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, 100);
            WorldSave save = w.SaveState();
            int before = save.Waves[(int)Zone.Outer].PendingTotal;

            WaveState outer = w.WaveRef(Zone.Outer);
            outer.PendingChaser += 99;
            w.SetWaveForTest(Zone.Outer, outer);

            Assert.AreEqual(before, save.Waves[(int)Zone.Outer].PendingTotal,
                "сохранённое состояние алиасит живой массив волн");
        }

        /// The seam Т4's cadence tests need: an emptied ring, so a wave can be
        /// watched clearing. It is deliberately NOT TestWorlds.ClearFirstWave,
        /// which empties the arena by DAMAGING every mob to death — that path
        /// spawns corpses, rolls loot and credits kills, all of which would be
        /// noise in a test about a timer. Hence the container assertion: it is
        /// what tells "taken off the arena" apart from "killed".
        [Test]
        public void ClearMobsForTest_TakesEveryMobOffTheArena_WithoutKillingThem()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, 100);
            int containersBefore = w.ContainerCount;

            Assert.Greater(w.MobCount, 0,
                "премиса: на арене обязаны быть мобы, иначе шов проверяется на пустоте");

            w.ClearMobsForTest();

            Assert.AreEqual(0, w.MobCount, "шов обязан снять с арены КАЖДОГО моба");
            Assert.AreEqual(containersBefore, w.ContainerCount,
                "шов снимает мобов, а не убивает их: ни трупа, ни выпавшего лута");
        }

        [Test]
        public void Snapshot_CarriesTheWorldAggregate_NotTheFirstRing()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            var frame = new RenderSnapshot(in cfg);
            TestWorlds.IdleTicks(w, 100);

            // The rings are DELIBERATELY given different steps and timers: on
            // tick 100 the world itself holds {1,1,1} and something like
            // {58,88,88}, where "the maximum" is indistinguishable from both
            // "the first ring" and "the minimum", and half of the assertions
            // below would be true under any implementation at all.
            WaveState mid = w.WaveRef(Zone.Middle);
            mid.WaveIndex = 5;                    // strictly above outer and core
            mid.PhaseTicks = 3;                   // strictly below the neighboring timers
            w.SetWaveForTest(Zone.Middle, mid);
            w.CaptureSnapshot(frame);

            int sum = w.WaveRef(Zone.Outer).AliveCount + w.WaveRef(Zone.Middle).AliveCount
                + w.WaveRef(Zone.Core).AliveCount;
            Assert.AreEqual(sum, frame.Wave.AliveCount, "агрегат не суммирует живых");
            Assert.AreEqual(5, frame.Wave.WaveIndex,
                "агрегат обязан брать МАКСИМУМ шага по кольцам, а не первое кольцо");
            Assert.AreEqual(3, frame.Wave.PhaseTicks,
                "агрегат обязан брать МИНИМУМ таймера среди незамороженных колец");
            Assert.AreEqual(WavePhase.Active, frame.Wave.Phase,
                "агрегат обязан быть Active, пока активно хоть одно кольцо");
        }

        /// THE TARGET OF THE WHOLE TASK, written first (spec §4 test 1): the
        /// ring's timer runs ALWAYS, so the next wave lands on its own
        /// schedule even though nothing has been killed. Today the queue is
        /// moved by a full wipe of the WHOLE arena and by nothing else, which
        /// is why a live 252-second raid three-handed saw exactly one wave of
        /// ten mobs.
        [Test]
        public void SecondWaveArrives_WithoutASingleKill()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + OuterPause(in cfg) / 2);
            Assert.AreEqual(WavePhase.Active, w.WaveRef(Zone.Outer).Phase);
            int aliveAfterFirst = w.WaveRef(Zone.Outer).AliveCount;
            Assert.Greater(aliveAfterFirst, 0, "первая волна не родила ни одного моба");

            TestWorlds.IdleTicks(w, OuterPause(in cfg) + 2);
            Assert.Greater(w.WaveRef(Zone.Outer).AliveCount, aliveAfterFirst,
                "вторая волна не пришла: сегодня очередь двигает только полный вайп арены");
        }

        [Test]
        public void Rings_TickIndependently()
        {
            SimConfig cfg = TestConfigs.Default();   // fixture pauses {2, 3, 3} s
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);
            Assert.AreNotEqual(w.WaveRef(Zone.Outer).PhaseTicks,
                w.WaveRef(Zone.Middle).PhaseTicks, "кольца тикают одним таймером");
        }

        [Test]
        public void ClearingARing_RestartsItsOwnTimer_AndLeavesNeighborsAlone()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);

            // The neighbors are left holding a debt, so that EXACTLY the outer
            // ring comes out clear.
            foreach (Zone z in new[] { Zone.Middle, Zone.Core })
            {
                WaveState s = w.WaveRef(z);
                s.PendingChaser = 99;
                w.SetWaveForTest(z, s);
            }
            WaveState outer = w.WaveRef(Zone.Outer);
            outer.PendingChaser = outer.PendingGunner = outer.PendingElite = 0;
            w.SetWaveForTest(Zone.Outer, outer);
            w.ClearMobsForTest();

            int clearedBefore = w.WorldStatsRef.WavesCleared;
            int middleBefore = w.WaveRef(Zone.Middle).PhaseTicks;
            int coreBefore = w.WaveRef(Zone.Core).PhaseTicks;
            w.Tick(default);

            Assert.AreEqual(clearedBefore + 1, w.WorldStatsRef.WavesCleared);
            Assert.AreEqual(WavePhase.Waiting, w.WaveRef(Zone.Outer).Phase);
            Assert.AreEqual(OuterPause(in cfg), w.WaveRef(Zone.Outer).PhaseTicks,
                "окно тишины должно быть ПОЛНЫМ, а не остатком");
            Assert.AreEqual(middleBefore - 1, w.WaveRef(Zone.Middle).PhaseTicks,
                "зачистка чужого кольца сдвинула таймер среднего");
            Assert.AreEqual(coreBefore - 1, w.WaveRef(Zone.Core).PhaseTicks);
        }

        [Test]
        public void ClearIsNotCounted_WhileAnyMobOfThatRingLives()
        {
            // The negative half: mutation M2's victim.
            // ⚠ THE ASSERTION IS ABSOLUTE, not a one-tick delta (re-review
            // finding К7): a mutant without the `alive == 0` check counts the
            // clear on the very tick the debt closed (75-76), that is BEFORE a
            // delta would begin to be measured — and `before == after` then
            // holds on BOTH branches (lesson 432).
            // On correct code no ring is clear at all: the debt is closed, the
            // mobs are alive.
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);
            WaveState outer = w.WaveRef(Zone.Outer);
            outer.PendingChaser = outer.PendingGunner = outer.PendingElite = 0;
            w.SetWaveForTest(Zone.Outer, outer);          // debt closed, mobs ALIVE
            w.Tick(default);
            Assert.AreEqual(0, w.WorldStatsRef.WavesCleared,
                "кольцо засчитано вычищенным при живых мобах (мутант дал бы 3 — по кольцу на каждое)");
        }

        [Test]
        public void WaveIndex_FollowsTheClock_NotTheNumberOfWavesStarted()
        {
            // Spec tests 2а and 15: mutation M9's victim.
            // ⚠ THE RING'S PAUSE IS DELIBERATELY MADE LARGER THAN THE
            // DIFFICULTY STEP (re-review finding К8). On the fixture numbers
            // the pause (2 s = 60 ticks) EQUALS the step (2 s = 60), and then
            // the clock and the wave counter give one and the same number
            // whatever the history of clears: one clear pushes the next start
            // back by less than a whole step. The test would be green on the
            // mutant — exactly the tautology lesson 428 warns about. A pause of
            // 4 s = 120 ticks = TWO steps separates them for good.
            SimConfig cfg = TestConfigs.Default();
            cfg.Wave.WavePauseByZone = new[] { 4f, 3f, 3f };
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);   // tick 77; first wave on 75, step 1

            WaveState outer = w.WaveRef(Zone.Outer);
            outer.PendingChaser = outer.PendingGunner = outer.PendingElite = 0;
            w.SetWaveForTest(Zone.Outer, outer);
            w.ClearMobsForTest();
            w.Tick(default);                                  // tick 78: cleared, timer = 120

            // Tick on to the outer ring's next start: it falls on tick 198.
            for (int i = 0; i < OuterPause(in cfg) + 4; i++)
            {
                w.Tick(default);
                if (w.WaveRef(Zone.Outer).Phase == WavePhase.Active) break;
            }
            Assert.AreEqual(WavePhase.Active, w.WaveRef(Zone.Outer).Phase, "волна не пришла");
            // The expectation is arithmetic, not a second call to the very
            // function under test (lesson 428): the start is on tick 198,
            // FirstWaveDelay = 75 ticks, the step = 60 ticks,
            // 1 + (198 - 75) / 60 = 1 + 2 = 3. A wave counter would give 2 —
            // this is the ring's SECOND wave, and that is the number mutation
            // M9 gives itself away by.
            Assert.AreEqual(3, w.WaveRef(Zone.Outer).WaveIndex,
                "номер волны отстал от часов — значит он всё ещё счётчик волн кольца");
        }

        [Test]
        public void ZonelessArena_RunsOnlyTheOuterRing()
        {
            // The zoneless fixture is built FROM Default(): OpenField()
            // descends from Quiet(), and that one pushes the first wave out to
            // 1e6 seconds (TestConfigs.cs:387).
            SimConfig cfg = TestConfigs.Default();
            cfg.Arena.ZoneRadius = System.Array.Empty<float>();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 4);
            Assert.AreEqual(WavePhase.Waiting, w.WaveRef(Zone.Middle).Phase);
            Assert.AreEqual(0, w.WaveRef(Zone.Middle).PendingTotal);
            Assert.AreEqual(0, w.WaveRef(Zone.Core).PendingTotal);
            Assert.Greater(w.WaveRef(Zone.Outer).AliveCount, 0);
        }

        [Test]
        public void CoreFreezes_WhenTheDirectorIsAwake()
        {
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 4);
            w.MatchRef.Phase = MatchPhase.DirectorActive;   // the existing ref seam
            w.Tick(default);
            Assert.AreEqual(WavePhase.Waiting, w.WaveRef(Zone.Core).Phase);
            Assert.AreEqual(0, w.WaveRef(Zone.Core).PhaseTicks);
            Assert.AreEqual(0, w.WaveRef(Zone.Core).PendingTotal);
        }

        // ------------------------------------------------------------------
        // Т5: the ring's living-mob CEILING and the SMOOTHING of a wave's
        // arrival. Both live in WaveSystem.SpawnPendingOfType, inside the
        // attempt loop and beside the Director's existing slot reserve — the
        // ceiling caps how many of a ring's mobs may stand at once, the
        // smoothing caps how many of them may be seated in one tick.
        //
        // ⚠ EVERY CEILING BELOW IS STRICTLY LOWER THAN THE WAVE (finding
        // A-Critical). On the fixture numbers with one player a wave is
        // CountForTest = round((4 + 2*0) * 1) = 4, so a ceiling of 4 would let
        // the debt close and the test would be asserting nothing at all.
        // ------------------------------------------------------------------

        [Test]
        public void RingAtItsCeiling_DoesNotSpawn_AndKeepsItsDebt()
        {
            SimConfig cfg = TestConfigs.Default();
            cfg.Wave.MaxAliveByZone = new[] { 2, 16, 8 };     // strictly below the wave (4)
            var w = new SimulationWorld(7, cfg);
            int skippedBefore = w.WorldStatsRef.MobSpawnsSkipped;
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 30);

            // EXACT equality rather than LessOrEqual: `0 <= 2` is true of a
            // completely dead spawner too (re-review finding M-2). Placement on
            // the 171 m ring is deterministically successful — 11.84 m to the
            // player against a threshold of 8.
            Assert.AreEqual(2, w.WaveRef(Zone.Outer).AliveCount, "потолок кольца перееден или спавн мёртв");
            Assert.Greater(w.WaveRef(Zone.Outer).PendingTotal, 0, "долг обязан сохраниться");
            Assert.AreEqual(skippedBefore, w.WorldStatsRef.MobSpawnsSkipped,
                "потолок кольца — не отказ арены, MobSpawnsSkipped расти не должен");
        }

        [Test]
        public void CeilingIsPerRing_NotPerArena()
        {
            SimConfig cfg = TestConfigs.Default();
            cfg.Wave.MaxAliveByZone = new[] { 1, 16, 8 };
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 30);
            Assert.AreEqual(1, w.WaveRef(Zone.Outer).AliveCount);
            Assert.Greater(w.WaveRef(Zone.Middle).AliveCount, 1,
                "среднее кольцо остановилось из-за чужого потолка");
        }

        [Test]
        public void WaveDoesNotOvershootTheCeiling_WithinASingleTick()
        {
            SimConfig cfg = TestConfigs.Default();
            cfg.Wave.MaxAliveByZone = new[] { 3, 16, 8 };
            cfg.Wave.MaxSpawnsPerZonePerTick = 64;            // smoothing deliberately switched off
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 4);
            Assert.LessOrEqual(w.WaveRef(Zone.Outer).AliveCount, 3,
                "инкремент alive внутри тика потерян — волна перелетела потолок");
        }

        [Test]
        public void WaveArrivesGradually_NotInASingleTick()
        {
            SimConfig cfg = TestConfigs.Default();
            cfg.Wave.MaxSpawnsPerZonePerTick = 1;
            var w = new SimulationWorld(7, cfg);
            // EXACTLY up to the starting tick: a wave works off its own debt on
            // that same tick, so one extra iteration would buy a second spawn
            // (finding A-Critical).
            TestWorlds.IdleTicks(w, FirstDelay(in cfg));
            Assert.AreEqual(1, w.WaveRef(Zone.Outer).AliveCount);
            Assert.Greater(w.WaveRef(Zone.Outer).PendingTotal, 0);
        }

        [Test]
        public void RingWhoseCeilingIsBelowItsWave_NeitherHangsNorClears()
        {
            // Spec test 12 — the core's own invariant, and it is RED here
            // rather than only under mutation.
            SimConfig cfg = TestConfigs.Default();
            cfg.Wave.MaxAliveByZone = new[] { 24, 16, 1 };
            var w = new SimulationWorld(7, cfg);
            // ⚠ 195 ticks is 6.5 s under contact damage: three chasers that
            // converge take 100 HP off, the collector dies, WaveSystem goes to
            // its early exit, the world freezes — and ALL FOUR assertions below
            // stay true VACUOUSLY (re-review finding В5). The HP budget is
            // handed out through the same seam TrioSaturated uses, and the
            // collector's being alive is asserted explicitly.
            TestWorlds.RelocatePlayerForTest(w, 0, w.PlayerAt(0).Pos, hp: 1e6f);
            int cleared = w.WorldStatsRef.WavesCleared;
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 120);
            Assert.IsTrue(w.PlayerAt(0).Alive, "носитель погиб — прогон заморожен, тест ничего не проверил");
            Assert.AreEqual(WavePhase.Active, w.WaveRef(Zone.Core).Phase);
            Assert.Greater(w.WaveRef(Zone.Core).PendingTotal, 0, "долг обязан сохраняться");
            Assert.AreEqual(1, w.WaveRef(Zone.Core).AliveCount);
            Assert.AreEqual(cleared, w.WorldStatsRef.WavesCleared,
                "кольцо с потолком ниже волны не может быть вычищено — это инвариант ядра");
        }

        [Test]
        public void UnspawnedDebt_IsOverwrittenByTheNextWave_NotAccumulated()
        {
            // Spec test 6: mutation M11's victim. ⚠ It MOVED here out of Т4
            // (re-review finding К6): its premise is the CEILING GUARD, which
            // arrives in this task and no earlier — in Т4 the whole debt would
            // sit down on the starting tick and `debtAfterFirst` would be zero.
            SimConfig cfg = TestConfigs.Default();
            cfg.Wave.MaxAliveByZone = new[] { 1, 16, 8 };   // the outer ring is at its ceiling at once
            var w = new SimulationWorld(7, cfg);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 2);
            int debtAfterFirst = w.WaveRef(Zone.Outer).PendingTotal;
            Assert.AreEqual(3, debtAfterFirst, "волна 4, потолок 1 — незакрытым обязан остаться долг 3");

            TestWorlds.IdleTicks(w, OuterPause(in cfg) + 2);
            // ⚠ EXACT equality against the size of ONE wave, not `<=
            // MaxMobsPerWave` (72): the old bound was wider than both outcomes,
            // so accumulation (3 + 6 = 9) cleared it exactly as overwriting (6)
            // did and mutation M11 survived (re-review finding C-2). The second
            // wave runs at difficulty step 2: 4 + 2 * 1 = 6.
            Assert.AreEqual(WaveSystem.CountForTest(in cfg.Wave, 1, w.PlayerCount),
                w.WaveRef(Zone.Outer).PendingTotal,
                "долг копится вместо перезаписи: он обязан быть РОВНО одной волной");
        }

        [Test]
        public void DirectorAndRetinue_AreFiledUnderTheCore_AndTopUpIsANoOp()
        {
            // Spec test 14 — it had no task of its own at all (re-review
            // finding В1). The behavioral change spec §3.4 names outright: once
            // the core's ceiling is filled by WAVE elites, LiveRetinueCount is
            // already >= RetinueCount and TopUpRetinue becomes a no-op — the
            // "retinue" turns out to be the remainder of the core's last wave.
            // That agrees with Р215 (the retinue is a POSITIONAL notion, not a
            // mark on a mob), but it has to be a decision carrying an assertion
            // rather than a drift.
            SimConfig cfg = TestConfigs.Default();
            cfg.Wave.MaxAliveByZone = new[] { 24, 16, 4 };   // the core fills its ceiling from a wave
            var w = new SimulationWorld(7, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, w.PlayerAt(0).Pos, hp: 1e6f);
            TestWorlds.IdleTicks(w, FirstDelay(in cfg) + 10);
            int elitesBefore = CoreElites(w);
            Assert.GreaterOrEqual(elitesBefore, cfg.Flow.RetinueCount,
                "фикстура не набрала ядро волной — тест не о том");

            // ⚠ The activation is done by the TRANSITION, never by assigning
            // the phase: the Director is born inside MatchFlowSystem.Activate
            // (an unconditional spawn plus TopUpRetinue), and a world whose
            // phase was set by hand never runs the latch at all.
            TestWorlds.RelocatePlayerForTest(w, 0, TestWorlds.InsideCore(in cfg), hp: 1e6f);
            w.Tick(default);
            Assert.AreEqual(MatchPhase.DirectorActive, w.MatchRef.Phase, "защёлка не сработала");

            // The Director is filed under the core BY THE SPAWNER, not by geometry.
            int director = -1;
            for (int i = 0; i < w.MobCount; i++)
                if (w.Mobs[i].Type == MobType.Director) director = i;
            Assert.GreaterOrEqual(director, 0, "Директор не родился");
            Assert.AreEqual(Zone.Core, w.Mobs[director].SpawnZone);
            // The retinue is not topped up: the core already holds more elites
            // than RetinueCount. ELITES are counted, not the ring's AliveCount:
            // the Director himself is filed under the core now and would make
            // an AliveCount equality false.
            Assert.AreEqual(elitesBefore, CoreElites(w),
                "TopUpRetinue досыпал свиту, хотя ядро уже держит её остатком волны");
        }

        /// Elites FILED UNDER the core — deliberately not "standing in" it,
        /// which is a separate, positional notion (Р215) this task has no right
        /// to touch: MatchFlowSystem.LiveRetinueCount reads the position, this
        /// reads the attribution, and the two answering differently is exactly
        /// what the test above is about.
        static int CoreElites(SimulationWorld w)
        {
            int n = 0;
            for (int i = 0; i < w.MobCount; i++)
                if (w.Mobs[i].Type == MobType.Elite && w.Mobs[i].SpawnZone == Zone.Core) n++;
            return n;
        }
    }
}
