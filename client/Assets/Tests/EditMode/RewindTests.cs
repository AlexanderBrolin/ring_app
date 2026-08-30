using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class RewindTests
    {
        [Test]
        public void CapRule_IsWrittenInTicks_NotInSeconds()
        {
            // ⚠ The direct witness of finding A-C5: 6 * TickDt = 0.20000002 >
            // 0.2f, so a rule written as a MULTIPLICATION would reject the
            // legal cap of 6. The project has already paid for this fact once
            // (SimulationWorld.cs:32).
            Assert.AreEqual(6, SimulationWorld.TicksFromSeconds(0.2f));
            Assert.Greater(6 * SimulationWorld.TickDt, 0.2f,
                "арифметика float изменилась — правило капа надо перечитать");
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.RewindCapTicks = 6;
            Assert.DoesNotThrow(() => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis),
                "правило капа отвергает кап, который сам же назначает");
        }

        [Test]
        public void Validate_CapAboveTwoHundredMilliseconds_Throws()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.RewindCapTicks = 7;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Arena.RewindCapTicks"));
        }

        [Test]
        public void Validate_CapOfZeroTicks_Throws()
        {
            // Fix-round (review M-10), and it overturns an earlier ruling of
            // this task: rule 12 shipped with an upper bound only, on the
            // grounds that a lower one had not been asked for. The convention
            // had asked for it. Zero passes `0 <= 6` cheerfully, builds a ring
            // of ONE row and switches lag compensation off ENTIRELY -- the
            // failure is silent, and the only place it would ever surface is a
            // playtest where dodging quietly stops mattering.
            // The precedent is Arena.RelaxIterations in the same validator,
            // whose own doc settles the shape: "zero disables the hard body
            // separation silently, so the builder rejects it (validation, not
            // a clamp)". Same reasoning, same remedy, one field over.
            // ⚠ Arena.RewindPictureTicks gets NO such rule, and that asymmetry
            // is deliberate: zero there is a meaningful setting -- no picture
            // time, the whole compensation goes to the projectile.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            a.RewindCapTicks = 0;
            // The picture half goes to zero WITH it, and not as decoration:
            // left at its default 3 it would violate the "picture <= cap" rule
            // as well, the builder would report two things at once, and the
            // assertion below would no longer be able to say WHICH rule
            // refused. Zero is a legal value for this field, so the fixture
            // stays on one rule without stepping outside the domain.
            a.RewindPictureTicks = 0;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("Arena.RewindCapTicks"));
            Assert.That(ex.Message, Does.Not.Contain("Arena.RewindPictureTicks"),
                "правило нижней границы обязано быть единственным нарушением этой фикстуры");
        }

        [Test]
        public void HistorySlot_SurvivesASwapRemoveOfANeighbor()
        {
            // ⭐ TEST 33 -- the witness of Р406. A mob dies in the MIDDLE of
            // the window, and the SURVIVOR's history must not shift: over six
            // ticks one array index has time to be three different mobs, a
            // slot does not.
            //
            // ⚠ THE TRAILING ASSERTION ALONE DOES NOT WITNESS THAT (fix-round,
            // review I-7). "The survivor's slot is unchanged after the swap"
            // is a statement about C# copying a value type: it holds for every
            // field of MobState, and it holds even under the design Р406
            // rejects, where the address is just the spawn-order index -- there
            // the survivor would carry 1 before the swap and 1 after it, and
            // the assert would pass while the address meant the wrong body.
            //
            // WHAT DISCRIMINATES IS THE PAIR BELOW, taken BEFORE the kill.
            // Slots come from ONE allocator shared with the collectors, who
            // rent first and never give a slot back, so every mob is numbered
            // ABOVE them and no mob's address can coincide with its own place
            // in `_mobs`. Under "address = array index" the first mob would
            // carry 0 -- its own index, and the collector's slot -- and the
            // second assertion fails on the spot. That is the claim Р406 is
            // about, and it is the one the original form was missing.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)),
                (MobType.Chaser, new float2(6f, 8f)));
            for (int i = 0; i < 2; i++)
            {
                var mi = w.Mobs[i]; mi.Ai = MobAiState.Idle; mi.Hp = 1e6f; w.SetMobForTest(i, mi);
            }
            int survivorId = w.Mobs[1].Id;
            int survivorSlot = w.Mobs[1].HistorySlot;

            Assert.AreNotEqual(w.Mobs[0].HistorySlot, survivorSlot,
                "два живых моба получили один адрес истории");
            for (int i = 0; i < 2; i++)
            {
                Assert.AreNotEqual(i, w.Mobs[i].HistorySlot,
                    $"адрес истории моба {i} совпал с его индексом в массиве — это и есть " +
                    "схема, отвергнутая Р406");
            }

            // Kill the FIRST one: the swap with the tail moves the survivor
            // into slot 0.
            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, new float2(1f, 0f),
                ownerIndex: 0, hitHeight: 1f, projectileMass: 0f, projectileSpeed3D: 0f);

            Assert.AreEqual(survivorId, w.Mobs[0].Id, "фикстура не воспроизвела своп с хвостом");
            Assert.AreEqual(survivorSlot, w.Mobs[0].HistorySlot,
                "слот истории уехал вместе с индексом — адрес нестабилен");
        }

        [Test]
        public void DeadBodysSlot_IsReused_ButNotItsPast()
        {
            // The second half of the same: a freed slot goes back into
            // circulation, but the new tenant does not inherit the dead
            // body's past.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            var m0 = w.Mobs[0]; m0.Ai = MobAiState.Idle; m0.Hp = 1e6f; w.SetMobForTest(0, m0);
            for (int i = 0; i < 8; i++) w.Tick(default);
            int freedSlot = w.Mobs[0].HistorySlot;

            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, new float2(1f, 0f),
                ownerIndex: 0, hitHeight: 1f, projectileMass: 0f, projectileSpeed3D: 0f);
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(20f, 20f)));

            Assert.AreEqual(freedSlot, w.Mobs[0].HistorySlot, "слот не переиспользован");
            Assert.AreEqual(new float2(20f, 20f), w.Mobs[0].Pos,
                "новый жилец слота встал в позицию покойника");
        }

        [Test]
        public void NoTwoLiveBodies_ShareAHistorySlot()
        {
            // ⭐ THE INVARIANT HistorySlot EXISTS TO HOLD, stated once and
            // guarded here: at no moment may two LIVING bodies address the
            // same row. Everything else the field does -- riding a swap-remove
            // inside its own struct, coming back into circulation after a
            // death -- is machinery in service of that one sentence, and until
            // this test nothing in the suite asserted the sentence itself.
            //
            // WRITTEN BECAUSE A MUTATION SURVIVED. Т24's mutation cycle moved
            // DamageMob's `_history.ReturnSlot(...)` from ABOVE the swap with
            // the tail to BELOW it (M-slot-b) and killed nothing: RewindTests
            // 4/4 and WorldLifecycleTests 10/10 stayed green. The mutant frees
            // the SURVIVOR's slot -- one line later that index already holds
            // the body that used to be at the tail -- and leaks the dead
            // body's, so two live bodies end up sharing a row. What hid it is
            // that the damage is DETERMINISTIC AND SYMMETRIC, which a
            // determinism test cannot see by construction:
            // SaveRestore_ReplaysToSameHash takes its save before any death,
            // so both of its 500-tick windows corrupt the occupancy the same
            // way and the two digests still agree. A wrong world that is
            // wrong identically twice passes every test that only asks
            // whether two runs match.
            //
            // AND THE NEIGHBOR ABOVE CANNOT CARRY THIS WEIGHT, which is why
            // this is a test of its own rather than two more asserts on
            // DeadBodysSlot_IsReused_ButNotItsPast: there exactly ONE mob is
            // alive when the kill lands, so `_mobs[index] = _mobs[--_mobCount]`
            // degenerates into `_mobs[0] = _mobs[0]`. After a self-assignment
            // the slot reads the same before and after the swap, and the two
            // orderings are indistinguishable. It takes a SECOND live body for
            // the readings to differ at all, and that is the whole of what
            // this fixture adds.
            // ⚠ NEITHER NEIGHBOR IS NAMED BY AN ORDINAL HERE, and that is the
            // repair rather than a matter of taste (coordinator ruling 149).
            // This paragraph used to read "a fifth test rather than two more
            // asserts on the fourth", and both numbers went stale the moment
            // the fix-round inserted Validate_CapOfZeroTicks_Throws third --
            // silently, because nothing recomputes prose. A name cannot go
            // stale when a test is inserted above it, so the ordinal is not
            // corrected, it is removed.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)),
                (MobType.Chaser, new float2(6f, 8f)));
            for (int i = 0; i < 2; i++)
            {
                var mi = w.Mobs[i]; mi.Ai = MobAiState.Idle; mi.Hp = 1e6f; w.SetMobForTest(i, mi);
            }
            // Read through the live structs, never hand-built ones: a
            // fixture-made MobState carries HistorySlot 0 and would claim the
            // collector's row (app-41wd).
            int deadSlot = w.Mobs[0].HistorySlot;
            int survivorSlot = w.Mobs[1].HistorySlot;

            w.DamageMob(0, 1e9f, w.Mobs[0].Pos, HitZone.Body, new float2(1f, 0f),
                ownerIndex: 0, hitHeight: 1f, projectileMass: 0f, projectileSpeed3D: 0f);
            // The third body is what turns a leaked slot into a SHARED one:
            // the mutant's freed number is the survivor's, so the next rent
            // hands it straight back out while the survivor still holds it.
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(20f, 20f)));

            Assert.AreEqual(survivorSlot, w.Mobs[0].HistorySlot,
                "выживший потерял свой слот истории при свопе с хвостом");
            Assert.AreNotEqual(survivorSlot, w.Mobs[1].HistorySlot,
                "два живых тела делят один слот истории");
            Assert.AreEqual(deadSlot, w.Mobs[1].HistorySlot,
                "слот покойника не переиспользован — новый жилец взял чужой номер");
            // A SECOND INVARIANT, NOT A RESTATEMENT OF THE FIRST. The three
            // asserts above are about MOBS sharing a row with each other; this
            // one is about a mob sharing one with the COLLECTOR, and nothing
            // else in the tree reads PlayerState.HistorySlot by value at all.
            // What it guards is a plausible tidy-up rather than a typo: adding
            // `_history.ReturnSlot(p.HistorySlot)` to KillPlayer "for
            // consistency with DamageMob" kills no other test, and its
            // consequence is that a dead collector frees slot 0, the next
            // SpawnMob rents it, and a live mob addresses the collector's row.
            // The collector's slot is issued once and never returned precisely
            // because `_players` is never compacted -- see PlayerState.HistorySlot.
            Assert.AreNotEqual(w.PlayerAt(0).HistorySlot, w.Mobs[1].HistorySlot,
                "моб делит слот истории со сборщиком");
        }

        [Test]
        public void DeadCollectorsSlot_IsNotReissued()
        {
            // Fix-round (review I-5), and the assertion above is NOT this
            // test. That one says a mob must not be handed the slot of a LIVE
            // collector, and it is satisfied by any allocator that keeps the
            // two populations apart at all. This one is about the one rule
            // that has no other guard in the tree: the collector's slot is
            // rented once and NEVER RETURNED, not even when he dies.
            //
            // MEASURED, NOT ASSUMED: writing `_history.ReturnSlot(p.HistorySlot)`
            // into KillPlayer "for consistency with DamageMob" leaves every
            // other test in this file green -- including the live-collector
            // assertion in the test above, because that fixture never kills
            // anybody's collector. So the mutant needs a fixture in which one
            // actually dies.
            //
            // AND A DEAD COLLECTOR IS STILL A BODY THE RING WRITES A ROW FOR.
            // `_players` is never compacted -- KillPlayer clears Alive and
            // leaves the body in place -- so from Т25 the writer walks him
            // every tick to record FlagAlive = 0. A mob handed his address
            // would share that row and the two would overwrite each other,
            // which is why "he is dead, the slot is free" is exactly the
            // wrong inference.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            int collectorSlot = w.PlayerAt(0).HistorySlot;

            w.KillPlayerNoDamage(0);
            Assert.IsFalse(w.PlayerAt(0).Alive, "фикстура не убила сборщика");
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));

            Assert.AreEqual(collectorSlot, w.PlayerAt(0).HistorySlot,
                "смерть отобрала у сборщика его слот истории");
            Assert.AreNotEqual(collectorSlot, w.Mobs[0].HistorySlot,
                "слот мёртвого сборщика вернулся в оборот — моб занял его строку");
        }

        [Test]
        public void TwoWorldsWithEqualPresentAndDifferentPast_DisagreeOnTheHash()
        {
            // ⭐ TEST 38 -- the witness of the CANCELED Н5 (the review's own
            // counterexample). The present is aligned FIELD BY FIELD; only the
            // past is left to differ.
            //
            // ⚠ `Ai = Idle` BELOW DOES NOT FREEZE THE MOB (ruling 17/104):
            // UpdateChaser writes Chase over it on the very next tick. It is
            // harmless here and no real freeze is needed, because this fixture
            // never reads the mob's present -- it OVERWRITES it, wholesale,
            // from the other world. What has to differ is the two mobs' PAST,
            // and a four-meter head start give that whether they walk or
            // stand: a frozen pair would sit at (2,0) and (6,0) for three ticks
            // and disagree just as flatly.
            SimConfig cfg = TestConfigs.Open();
            var a = new SimulationWorld(7, cfg);
            var b = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(a, (MobType.Chaser, new float2(6f, 0f)));
            TestWorlds.SpawnMobsAt(b, (MobType.Chaser, new float2(6f, 0f)));
            for (int i = 0; i < 2; i++)
            {
                var world = i == 0 ? a : b;
                var m = world.Mobs[0]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; world.SetMobForTest(0, m);
            }
            // A different past: in `a` the mob spent three ticks somewhere else.
            var moved = a.Mobs[0]; moved.Pos = new float2(2f, 0f); a.SetMobForTest(0, moved);
            for (int i = 0; i < 3; i++) { a.Tick(default); b.Tick(default); }
            // The present is aligned by hand -- the worlds now differ ONLY in the past.
            var same = b.Mobs[0]; a.SetMobForTest(0, same);

            Assert.AreNotEqual(b.StateHash(), a.StateHash(),
                "два мира с равным настоящим и разной историей дали ОДИН хеш");
        }

        [Test]
        public void HistoryRowOfTickT_HoldsThePositionAtTheEndOfTickT()
        {
            // ⭐ TEST 37 -- the moment of the write is a REAL fork (M32): a
            // write placed BEFORE movement would shift the whole rewind by
            // exactly one tick, and every other test would stay green.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            var input = new SimInput { MoveDir = new float2(1f, 0f) };
            w.Tick(input);
            float2 endOfTick = w.Player.Pos;
            w.Tick(input);

            // ⚠ THE PLAN'S OWN TEXT CALLS A `PlayerHistorySlotForTest(0)` SEAM
            // HERE, and this line is the one place these three tests part from
            // it (coordinator ruling 148). The seam was dropped before it
            // shipped: PlayerAt is public and PlayerState.HistorySlot is a
            // public field, so it added no capability, and this very file
            // already read the slot this way in two older fixtures -- a second
            // spelling of one path is what rule 2 forbids.
            Assert.IsTrue(w.HistoryForTest.PosAt(w.PlayerAt(0).HistorySlot,
                w.CurrentTick - 1, w.Player.Pos, out PositionHistory.Record rec));
            Assert.AreEqual(endOfTick.x, rec.Pos.x, 1e-5f,
                "запись тика T содержит позицию НАЧАЛА тика, а не конца");
        }

        [Test]
        public void SaveAndRestore_ReproduceTheSameRewoundOutcome()
        {
            // Test 39: the history is part of the save, and a restore
            // reproduces the same outcome for a shot with k = 6.
            // ⚠ THE FIXTURE MOVES -- correction from review round 3 (finding
            // D-I4). In v2 the ten ticks between SaveState and RestoreState ran
            // on ZERO input past an Idle mob, so the ring's rows for ticks
            // 14-20 were bit-for-bit equal to the rows for ticks 4-10: a copy
            // "by reference" produced the same hash and mutation M34 survived.
            // Walking the collector makes the past genuinely different.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            var walk = new SimInput { MoveDir = new float2(1f, 0f) };
            for (int i = 0; i < 10; i++) w.Tick(walk);
            WorldSave save = w.SaveState();
            ulong before = w.StateHash();

            for (int i = 0; i < 10; i++) w.Tick(walk);
            w.RestoreState(save);

            Assert.AreEqual(before, w.StateHash(), "восстановление не вернуло историю");
        }

        [Test]
        public void PosAtATickWithNoRow_DegradesToTheCurrentPosition()
        {
            // ⭐ THE WITNESS OF THE SECOND LINE OF PosAt's OWN TABLE
            // (coordinator ruling 145), and that line is a BATTLE branch rather
            // than a fallback. The row for tick T is written at the END of
            // TickAll, so a round fired in the weapon phase of tick T finds no
            // row stamped T and is answered right here -- which is also how the
            // table's "k == 0 -> live positions" line gets executed, instead of
            // by a branch of its own.
            //
            // TICK 0 IS THE QUESTION, and it is not an arbitrary number.
            // TickAll increments the counter before it runs, so the first row
            // the ring is ever handed is tick 1 and no row can ever carry tick
            // 0 -- this is the "first ticks of the match" case of the table,
            // stated at the one tick where it is unconditional. It is also the
            // fixture that discriminates against the sentinel being zero: with
            // NoTick == 0 a blank row would answer FOR tick 0, hand back a
            // `default` record and, its Alive bit clear, report a MISS instead.
            //
            // Open(), not OpenField(): the collector spawns 159.16 m out on the
            // ring, so `currentPos` is nowhere near the origin and a blank
            // record's zero position cannot pass for the right answer. He is far
            // outside the outer zone boundary, so no Director is born on top of
            // what this fixture measures (lesson 590).
            const int neverWrittenTick = 0;
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            w.Tick(default);
            w.Tick(default);
            float2 currentPos = w.Player.Pos;

            Assert.IsTrue(w.HistoryForTest.PosAt(w.PlayerAt(0).HistorySlot,
                neverWrittenTick, currentPos, out PositionHistory.Record rec),
                "отмотка к тику, строки которого в кольце нет, обязана вырождаться в поведение " +
                "без отмотки, а не в промах");
            Assert.AreEqual(currentPos, rec.Pos,
                "вырожденная ветка вернула не текущую позицию");
        }

        [Test]
        public void PosAtATickTheCollectorDidNotSurvive_ReportsAMiss()
        {
            // ⭐ THE WITNESS OF THE THIRD LINE OF PosAt's TABLE (ruling 145): a
            // record that IS there, under a stamp that DOES match, with the
            // Alive bit clear -- "the target was dead at that moment".
            //
            // ⛔ THE POSITIVE ASSERTION COMES FIRST, AND IT IS NOT DECORATION.
            // A test made of the IsFalse alone would be GREEN on today's stub,
            // which answers false to every question ever asked -- a guard
            // pointing the wrong way rather than a witness. The PAIR is what
            // states the contract: the same slot, inside the same window,
            // answers differently on two ticks, so the answer is read off the
            // record and not off the caller.
            //
            // BOTH TICKS ARE INSIDE THE WINDOW, one tick apart against a ring
            // of RewindCapTicks + 1 rows, so neither of them can fall through
            // to the degenerate branch the test above covers.
            //
            // The collector's slot is never returned (PlayerState.HistorySlot's
            // own doc), so the row he is killed out of stays his: `_players` is
            // never compacted, the writer keeps walking his body every tick, and
            // what it records from the kill on is FlagAlive clear -- which is
            // exactly what the second question reads.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            w.Tick(default);
            int aliveTick = w.CurrentTick;

            w.KillPlayerNoDamage(0);
            w.Tick(default);
            int deadTick = w.CurrentTick;
            Assert.IsFalse(w.PlayerAt(0).Alive, "фикстура не убила сборщика");

            int slot = w.PlayerAt(0).HistorySlot;
            float2 currentPos = w.Player.Pos;
            Assert.IsTrue(w.HistoryForTest.PosAt(slot, aliveTick, currentPos, out _),
                "тик, в котором сборщик был жив, обязан отдать историческую запись");
            Assert.IsFalse(w.HistoryForTest.PosAt(slot, deadTick, currentPos, out _),
                "тик, в котором сборщик был мёртв, обязан быть промахом");
        }

        [Test]
        public void PosAtANegativeTick_DegradesInsteadOfThrowing()
        {
            // ⭐ THE WITNESS OF THE GUARD ITSELF (coordinator ruling 152), and
            // it is a test of its own rather than two more asserts on
            // PosAtATickWithNoRow_DegradesToTheCurrentPosition because that
            // fixture cannot reach the guard at all: it asks about tick 0, and
            // `0 % 7` is 0, a perfectly legal index.
            // ⚠ NAMED, NOT NUMBERED, and deliberately: ruling 149 struck an
            // ordinal out of this very file one round ago because a count goes
            // stale the moment a test is inserted above it. "The thirteenth
            // test" would have re-introduced the same defect in the same file. The guard only runs on a NEGATIVE tick, so only a
            // negative tick can observe it -- without this test the line is
            // production code no mutation could kill, which is exactly the
            // defect ruling 145 wrote the two fixtures above to avoid.
            //
            // ⛔ -1 IS THE TICK THE CONTRACT NAMES. Spec §3.6 gives the
            // projectile step an explicit `int historyTick` whose `-1` means
            // "the present", and C# computes `-1 % 7` as -1 rather than 6, so
            // an index built before the guard reaches into the ring at a
            // negative offset. The caller is contracted never to send the
            // sentinel down here -- it branches on it and reads the live body
            // -- but Р378 ("PosAt NEVER THROWS: this is a combat path") is
            // unconditional, and an unconditional promise needs a test that
            // does the thing the contract forbids.
            //
            // ⚠ GREEN THE DAY IT IS WRITTEN, and that is stated rather than
            // hidden: the guard already ships, so this test cannot fail today.
            // What makes it a witness instead of decoration is mutation M35 --
            // drop the `tick < 0 ||` clause and the first assertion below turns
            // red with an IndexOutOfRangeException. Same shape and same
            // justification as ruling 137, where a test was likewise written
            // after the code and proved by repeating the mutation.
            const int SentinelTick = -1;
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            w.Tick(default);
            w.Tick(default);

            PositionHistory ring = w.HistoryForTest;
            int slot = w.PlayerAt(0).HistorySlot;
            float2 currentPos = w.Player.Pos;
            PositionHistory.Record rec = default;
            bool answered = false;

            Assert.DoesNotThrow(() => answered = ring.PosAt(slot, SentinelTick, currentPos, out rec),
                "PosAt бросила на отрицательном тике — боевой путь обязан отвечать, а не падать");
            Assert.IsTrue(answered,
                "тик до начала матча обязан вырождаться в поведение без отмотки, а не в промах");
            Assert.AreEqual(currentPos, rec.Pos,
                "вырожденная ветка вернула не текущую позицию");
        }
    }
}
