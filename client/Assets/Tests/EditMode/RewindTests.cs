using NUnit.Framework;
using Ring.Simulation.Combat;
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

        // ---- app-88jb Т27: the split itself, called directly ---------------
        //
        // ⭐ THE ARITHMETIC OF THE DIVISION, AND IT IS ASKED OF RewindSplit
        // ITSELF rather than of a world (coordinator RULING 181). The three
        // tests below take no SimulationWorld at all — they stand with the
        // config-rule units at the head of this file for that reason — and they
        // witness ONLY the arithmetic: that the number reaches a round, that it
        // is spent once, and that it comes from the round's own shooter are
        // witnessed by the Т27 fixtures at the foot of this file, which drive a
        // real Tick.
        //
        // ⛔ THE SHALLOW CASE CANNOT BE REACHED THROUGH A CONSUMER AT ALL, and
        // that is why a direct call is not a convenience here. Replace
        // PictureTicks' min by its right operand and a `k` under the picture
        // depth yields a NEGATIVE step count; ProjectileSystem.CatchUp's `for`
        // declines a negative bound exactly the way it declines zero, so
        // through WeaponSystem that mutant produces the identical world. Only
        // the call below can tell the two apart.
        //
        // The fixture is TestConfigs.Default() and every expectation is written
        // out of its own Arena — the tests follow the baseline's cap and
        // picture depth instead of restating them as 6 and 3.

        [Test]
        public void PictureHalf_SaturatesAtTheArenaDepth_AndTheRestGoesToTheRound()
        {
            // Above the arena's picture depth the question stops getting
            // deeper: a shooter lagging worse than the interpolation buffer is
            // shown no further back than anybody else, and every tick past that
            // point belongs to the round.
            ArenaSimConfig arena = TestConfigs.Default().Arena;
            Assert.Greater(arena.RewindCapTicks, arena.RewindPictureTicks,
                "премисса фикстуры: у базовой конфигурации нет глубины сверх картинки, " +
                "насыщать нечем");

            for (int k = arena.RewindPictureTicks + 1; k <= arena.RewindCapTicks; k++)
            {
                Assert.AreEqual(arena.RewindPictureTicks, RewindSplit.PictureTicks(k, in arena),
                    $"картинка не насытилась на глубине {k} — вопрос забирает больше, " +
                    "чем ему отпущено ареной");
                Assert.AreEqual(k - arena.RewindPictureTicks, RewindSplit.InputTicks(k, in arena),
                    $"на глубине {k} снаряду досталась не вся оставшаяся половина");
            }
        }

        [Test]
        public void DepthShallowerThanThePicture_LeavesTheRoundNoCatchUpAtAll()
        {
            // ⭐⭐ THE AXIS A MUTANT KILLS AND A WORLD CANNOT, for the reason
            // this section's opening note sets out: under the picture depth the
            // question takes the WHOLE of `k` and the round is owed nothing. This is also the ordinary case
            // of a healthy connection — most shooters spend their entire depth
            // here and take no catch-up step at all.
            ArenaSimConfig arena = TestConfigs.Default().Arena;
            Assert.Greater(arena.RewindPictureTicks, 0,
                "премисса фикстуры: при нулевой картинке мелкой глубины не существует");

            for (int k = 0; k < arena.RewindPictureTicks; k++)
            {
                Assert.AreEqual(k, RewindSplit.PictureTicks(k, in arena),
                    $"мелкая глубина {k} не ушла в картинку целиком");
                Assert.AreEqual(0, RewindSplit.InputTicks(k, in arena),
                    $"на глубине {k} снаряду отпущены догоняющие шаги, которых нет: " +
                    "деление отдаёт вопросу не min, а саму глубину картинки");
            }
        }

        [Test]
        public void TwoHalves_AddBackUpToTheDepth_AcrossTheWholeDomain()
        {
            // The conservation law of the split, over the whole domain a
            // sanitized input can carry: nothing of the depth is invented and
            // nothing is lost between the two halves. Neither half may go
            // negative either — a negative input half is exactly the shape
            // DepthShallowerThanThePicture_LeavesTheRoundNoCatchUpAtAll
            // refuses, and this sweep says it holds nowhere in the domain
            // rather than only under the picture depth.
            ArenaSimConfig arena = TestConfigs.Default().Arena;

            for (int k = 0; k <= arena.RewindCapTicks; k++)
            {
                int picture = RewindSplit.PictureTicks(k, in arena);
                int input = RewindSplit.InputTicks(k, in arena);
                Assert.AreEqual(k, picture + input,
                    $"половины глубины {k} не сходятся обратно в неё: {picture} + {input}");
                Assert.GreaterOrEqual(picture, 0, $"картинка ушла в минус на глубине {k}");
                Assert.GreaterOrEqual(input, 0, $"половина снаряда ушла в минус на глубине {k}");
            }
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
            // Test 39: the history is part of the save, and a restore brings
            // it back.
            // ⚠ NEITHER THIS TEST NOR ITS NAME INVOLVES A SHOT (review finding
            // B-3). The line here used to promise "the same outcome for a shot
            // with k = 6", copied verbatim from a plan edition whose version of
            // this test called PosAt; the body fires nothing, rewinds nothing
            // and asserts on one digest. It is a witness of the SAVE, and of
            // the deep copy inside it (mutation M34) -- not of a rewound
            // outcome, which is Т27/Т28's test to write.
            // ⛔ AND IT IS NOT A WITNESS OF THE FOLD EITHER: remove the fold
            // step from StateHash and both digests collapse to equal, so this
            // assertion passes trivially. TwoWorlds... above is the fold's
            // witness. The NAME still over-promises; renaming it is not this
            // round's call and is recorded for the coordinator.
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
            // ⭐ THE FLAG HALF OF THE DEGENERATE RECORD (coordinator RULING
            // 153). The branch returns `true` BEFORE the flags are ever read,
            // so nothing above can tell FlagAlive from a zero byte -- these two
            // assertions are the whole of its witness, and without them the
            // `FlagAlive` in `new Record(currentPos, FlagAlive)` is production
            // code no mutation could kill.
            Assert.AreNotEqual(0, rec.Flags & PositionHistory.FlagAlive,
                "вырожденная ветка отдала запись без бита жизни — вызывающий прочтёт её как труп");
            // AND THE OTHER TWO BITS MUST BE CLEAR, which is a contract rather
            // than an accident (ruling 145): the signature takes a position and
            // not a body, so this branch does not KNOW whether the target is
            // sliding or invulnerable, and a raised bit here would be an
            // invention. The caller reads both off the live body instead.
            Assert.AreEqual(0, rec.Flags & (PositionHistory.FlagSliding | PositionHistory.FlagInvulnerable),
                "вырожденная ветка соврала про подкат или неуязвимость — она их не знает");
        }

        [Test]
        public void PosAtATickTheCollectorDidNotSurvive_ReportsAMiss()
        {
            // ⭐ THE WITNESS OF THE THIRD LINE OF PosAt's TABLE (ruling 145): a
            // record that IS there, under a stamp that DOES match, with the
            // Alive bit clear -- "the target was dead at that moment".
            //
            // ⛔ THE POSITIVE ASSERTION COMES FIRST, AND IT IS NOT DECORATION.
            // ⚠ "Today's stub" is what this paragraph used to say, and the stub
            // was deleted by the very commit that made the test pass (review
            // finding B-4). The reasoning survives the stub, restated for the
            // tree as it is: a test made of the IsFalse alone would have been
            // green against the Т24 body -- `record = default; return false;`
            // -- which answered false to every question ever asked, so it would
            // have been a guard pointing the wrong way rather than a witness.
            // The PAIR is what states the contract, and it still does: the same
            // slot, inside the same window, answers differently on two ticks,
            // so the answer is read off the record and not off the caller.
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
            // `0 % 7` is 0, a perfectly legal index. The guard only runs on a
            // NEGATIVE tick, so only a negative tick can observe it -- without
            // this test the line is production code no mutation could kill,
            // which is exactly the defect ruling 145 wrote the two fixtures
            // above to avoid.
            //
            // ⚠ NAMED, NOT NUMBERED, and deliberately: ruling 149 struck an
            // ordinal out of this very file one round ago because a count goes
            // stale the moment a test is inserted above it. "The thirteenth
            // test" would have re-introduced the same defect in the same file.
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

        [Test]
        public void HistoryRow_CarriesTheSlideAndInvulnerabilityOfTheTickItRecords()
        {
            // ⭐⭐ THE WITNESS OF THE FLAG AXIS (coordinator RULING 153, review
            // finding B-1, and it is the axis the field exists for at all).
            // Until this fixture no test in the tree asserted on Record.Flags
            // even once, and no fixture here ever slid or dashed -- input was
            // `default` or a walk -- so both bit lines were production code
            // nothing could kill. Spec §4.3 had ALREADY named the witness
            // (its entry 35 is the Sliding bit); ruling 145 pulled its
            // neighbors 34 and 36 forward and left 35 behind.
            //
            // WHAT IT COSTS TO BE WRONG, from spec finding C-I5 read backwards:
            // a collector who was mid-slide `k` ticks ago is tested against a
            // STANDING profile, so a round that visibly went over his head
            // lands; and DashIframes is 0.2 s, which is EXACTLY the six-tick
            // rewind cap, so a whole dodge fits inside the deepest rewind and a
            // lost invulnerability bit awards a hit the victim had already
            // earned away.
            //
            // ⛔ THE TIMERS ARE SET DIRECTLY, NOT DRIVEN THROUGH INPUT, and that
            // is the safer of the two. A real slide is gated on a run-up and a
            // real dash on stamina and cooldown, so an input-driven fixture that
            // silently failed its gate would assert on a collector who never
            // slid -- a green test witnessing nothing, which is the exact defect
            // class this test exists to close. Setting the timer is the tree's
            // own convention for the same reason (PlayerState.SlideTimer's doc
            // counts seventeen fixtures doing it), and the two sanity
            // assertions below make the fixture prove it worked before the
            // record is asked anything.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            PlayerState p = w.Player;
            p.SlideTimer = cfg.Hero.SlideDuration;
            p.SlideDir = new float2(1f, 0f);
            p.IframeTimer = cfg.Hero.DashIframes;
            w.SetPlayerForTest(0, p);

            w.Tick(default);

            Assert.Greater(w.Player.SlideTimer, 0f, "фикстура не подкатывается — тест ничего не меряет");
            Assert.Greater(w.Player.IframeTimer, 0f, "фикстура не неуязвима — тест ничего не меряет");

            Assert.IsTrue(w.HistoryForTest.PosAt(w.PlayerAt(0).HistorySlot, w.CurrentTick,
                w.Player.Pos, out PositionHistory.Record rec),
                "строка только что закрытого тика обязана существовать");
            Assert.AreNotEqual(0, rec.Flags & PositionHistory.FlagSliding,
                "подкат не записан в строку — отмотанный выстрел проверит подкатывающегося по стоячему профилю");
            Assert.AreNotEqual(0, rec.Flags & PositionHistory.FlagInvulnerable,
                "неуязвимость не записана в строку — отмотанный выстрел засчитает попадание по уклонившемуся");
        }

        [Test]
        public void HistoryRowOfAMob_ReportsItAliveAtItsOwnSlot()
        {
            // ⭐ THE ONLY FIXTURE THAT ASKS PosAt ABOUT A MOB (RULING 153).
            // Every other call in this file passes the COLLECTOR's slot, so the
            // mob arm of Write -- the `FlagAlive` it hands every body in
            // `_mobs` -- had no witness at all. Its failure mode is not subtle:
            // with the bit gone PosAt reports a miss for every mob in every past
            // tick, and PvE lag compensation is silently dead while every other
            // test in the suite stays green.
            //
            // ⚠ `currentPos` IS A PLACE THE MOB HAS NEVER BEEN, deliberately. If
            // the row were missing, the degenerate branch would answer `true`
            // with whatever was passed in, and a fixture that passed the mob's
            // real position could not tell that apart from a correct read. The
            // sentinel makes the two answers differ by tens of meters.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            var m = w.Mobs[0]; m.Hp = 1e6f; w.SetMobForTest(0, m);
            int mobSlot = w.Mobs[0].HistorySlot;

            w.Tick(default);

            Assert.AreNotEqual(w.PlayerAt(0).HistorySlot, mobSlot,
                "фикстура спрашивает слот сборщика, а не моба — тест не про ту ветку");
            var neverVisited = new float2(-500f, -500f);
            Assert.IsTrue(w.HistoryForTest.PosAt(mobSlot, w.CurrentTick, neverVisited,
                out PositionHistory.Record rec),
                "строка моба отвечает промахом — PvE-отмотка мертва по построению");
            Assert.AreNotEqual(0, rec.Flags & PositionHistory.FlagAlive,
                "у моба не записан бит жизни");
            Assert.AreEqual(w.Mobs[0].Pos.x, rec.Pos.x, 1e-5f,
                "вместо строки моба вернулась вырожденная запись");
        }

        [Test]
        public void TwoWorldsDifferingOnlyInARecordedFlag_DisagreeOnTheHash()
        {
            // ⭐ THE WITNESS OF THE FLAG BYTE INSIDE THE FOLD (RULING 153).
            // TwoWorldsWithEqualPresentAndDifferentPast above pins the fold as a
            // whole, but it differs in POSITIONS: drop the Flags step from
            // FoldRecord and it stays green. This fixture differs in NOTHING BUT
            // one recorded bit.
            //
            // HOW THE PRESENT IS KEPT IDENTICAL: invulnerability is the one of
            // the three bits that moves no body. The collector of `a` carries
            // i-frames through the recorded tick and `b`'s does not, so the two
            // rows differ by exactly one byte while the two positions are equal;
            // then the present is aligned field by field, exactly as the older
            // fixture does it, which also erases the IframeTimer difference from
            // HashPlayer. What is left to tell the two digests apart is the flag
            // byte of one historical record, and nothing else.
            SimConfig cfg = TestConfigs.Open();
            var a = new SimulationWorld(7, cfg);
            var b = new SimulationWorld(7, cfg);
            PlayerState dodging = a.Player;
            dodging.IframeTimer = cfg.Hero.DashIframes;
            a.SetPlayerForTest(0, dodging);

            a.Tick(default);
            b.Tick(default);
            Assert.AreEqual(b.Player.Pos, a.Player.Pos,
                "неуязвимость сдвинула тело — фикстура различает не только флаг");
            a.SetPlayerForTest(0, b.Player);

            Assert.AreNotEqual(b.StateHash(), a.StateHash(),
                "флаги записи не входят в хеш — два мира с разным прошлым подката дали ОДИН хеш");
        }

        [Test]
        public void ShotWithInputLag_IsBornAtTheMuzzle_AndCatchesUp()
        {
            // ⭐ THE POSITION AXIS OF THE CATCH-UP (app-88jb Т27, spec §3.6).
            // The input half of the rewind depth moves the round FORWARD from
            // the muzzle; it never births it in the past. The witness is a pair
            // of worlds on ONE seed whose only difference is the depth the
            // shooter's own input claims -- the spread is drawn from the same
            // stream at the same point in both, so the two muzzle angles are
            // equal and only the crank can separate the two positions.
            //
            // ⚠ THE SECOND ASSERTION IS A RESTATEMENT AND NOT A SECOND
            // WITNESS, and saying so is the whole point of this paragraph
            // (coordinator RULING 190). The canceled design it names -- Р381,
            // which had the round born at tick T - k and fast-forwarded, i.e.
            // starting BEHIND the shooter -- is refused by the FIRST line
            // already: at a tolerance of 0.05 against a gap of two whole steps
            // that line admits nothing standing less than (2 * step - 0.05) m
            // ahead of the still round, and NUnit reaches the line after it
            // only when it passed. It therefore cannot fail on its own, and
            // whatever kills it kills the first assertion first.
            //   IT STAYS ALL THE SAME, as a decision rather than an oversight
            // (the same ruling): it costs one comparison, and it is the one
            // line in this file that tells the next reader WHICH scheme was
            // canceled and which way its mistake pointed. Spec §3.6 carries the
            // full account of what that edition got wrong.
            //
            // OpenField(), not Open(): Open() spawns the collector 159 m out on
            // the ring, so an aim point at (30, 0) lies in -X from him and both
            // assertions below -- written for +X -- would be red against
            // CORRECT code. OpenField puts him at the origin and drops the zone
            // boundaries with it, so no Director is born on top of what this
            // fixture measures (lesson 590).
            SimConfig cfg = TestConfigs.OpenField();
            var slow = new SimulationWorld(7, cfg);
            var fast = new SimulationWorld(7, cfg);
            var noLag = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f),
                AimHeight = cfg.Hero.MuzzleHeight, RewindTicks = 0 };
            // Two ticks past the picture depth, so the picture half saturates
            // and exactly two ticks are left for the input half.
            var lagged = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f),
                AimHeight = cfg.Hero.MuzzleHeight,
                RewindTicks = (byte)(cfg.Arena.RewindPictureTicks + 2) };

            slow.Tick(noLag); fast.Tick(lagged);
            Assert.AreEqual(1, slow.ProjectileCount);
            Assert.AreEqual(1, fast.ProjectileCount);

            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            Assert.AreEqual(slow.Projectiles[0].Pos.x + 2f * step,
                fast.Projectiles[0].Pos.x, 0.05f,
                "снаряд не прокручен на k_ввод шагов");
            Assert.Greater(fast.Projectiles[0].Pos.x, slow.Projectiles[0].Pos.x,
                "снаряд отброшен НАЗАД — это отменённая схема Р381");
        }

        [Test]
        public void CatchUpSteps_AgeTheRound_ByDistanceNotByTicks()
        {
            // ⭐ THE Ttl AXIS, and it is the degenerate case spec §3.6 states
            // for the lifetime: catch-up steps AGE the round. Without them a
            // lagging shooter would be handed a longer-ranged weapon than
            // everybody else -- the extra meters would be free, paid for by
            // nobody's lifetime.
            //
            // FOUR SUBTRACTIONS ON THE BIRTH TICK, NOT ONE. At the arena cap
            // the depth splits into a saturated picture half and three ticks of
            // input half, and the birth tick spends those three catch-up steps
            // PLUS the ordinary ProjectileSystem step every round gets. The
            // expectation is written as that expression rather than as a
            // number, so it follows the fixture's own cap and picture depth
            // instead of restating them.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg);
            var lagged = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f),
                AimHeight = cfg.Hero.MuzzleHeight,
                RewindTicks = (byte)cfg.Arena.RewindCapTicks };
            w.Tick(lagged);
            float expected = cfg.Weapon.ProjectileLifetime
                - (cfg.Arena.RewindCapTicks - cfg.Arena.RewindPictureTicks + 1) * SimulationWorld.TickDt;
            Assert.AreEqual(expected, w.Projectiles[0].Ttl, 1e-3f,
                "Ttl не вычтен на догоняющих шагах");
        }

        /// THE PREMISE OF EVERY "MET ON A CATCH-UP STEP" FIXTURE IN THIS FILE,
        /// ASSERTED RATHER THAN ASSUMED (coordinator RULING 185). Each of them
        /// stands a collector short of something round -- an obstacle, or
        /// another collector -- and needs the contact to land on a catch-up
        /// step that is NEITHER the round's ordinary step NOR the LAST of the
        /// catch-up. Both halves are load-bearing and both rest on balance
        /// numbers that have already moved once inside this epic (Т23 raised
        /// the ProjectileSpeed ceiling to 300, and the window these fixtures
        /// are correct in is narrow):
        ///   * nearer than the ordinary step and the round meets the obstacle
        ///     with no catch-up at all -- the fixture drops back to the
        ///     sentinel RULING 174 moved it away from, and it would have been
        ///     green through the whole red phase;
        ///   * on the LAST catch-up step and the loop ends by itself on the
        ///     step that resolves the contact -- dropping the RULING 172 break
        ///     would then change nothing observable, and a guard would read as
        ///     witnessed while no assertion could tell it from its own absence.
        /// Left implicit, both go blind IN SILENCE: the tests stay green and
        /// stop witnessing. Hence an assertion, and a message that says which
        /// half went.
        ///
        /// EVERY NUMBER IS DERIVED, NONE IS READ OFF A RUN. The muzzle is
        /// MuzzleOffset plus the first shot's whole tick of fire-cooldown
        /// overshoot (WeaponSystem.SpawnShot); the contact is where the
        /// solver's padded circle -- the target's own radius plus the round's
        /// -- meets the line; and the step is ProjectileSpeed * TickDt because
        /// these fixtures fire from the HIP (no AimHeld), which leaves VelZ at
        /// zero and makes the shot's whole speed horizontal. `toTargetCenter`
        /// is measured off the world by every caller, so a fixture is checked
        /// where it actually put its shooter rather than where it meant to.
        ///
        /// Answers the 1-based catch-up step the contact falls on, which the
        /// ricochet fixture spends on arithmetic of its own.
        static int AssertContactLandsOnAMiddleCatchUpStep(in SimConfig cfg,
            float toTargetCenter, float targetRadius)
        {
            int steps = cfg.Arena.RewindCapTicks - cfg.Arena.RewindPictureTicks;
            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            float muzzle = cfg.Weapon.MuzzleOffset
                + SimulationWorld.TickDt * cfg.Weapon.ProjectileSpeed;
            float contact = toTargetCenter - muzzle
                - (targetRadius + cfg.Weapon.ProjectileRadius);
            int contactStep = (int)math.ceil(contact / step);

            Assert.Greater(contactStep, 1,
                $"фикстура перестала свидетельствовать RULING 172: баланс уехал, контакт лежит " +
                $"в {contact} м от дула при шаге {step} м, и его достаёт ОБЫЧНЫЙ шаг — " +
                "препятствие встречается до догона, а тест снова сторожит порядок эмитов, " +
                "который существовал и до Т27");
            Assert.Less(contactStep, steps,
                $"фикстура перестала свидетельствовать RULING 172: баланс уехал, контакт лежит " +
                $"в {contact} м от дула при шаге {step} м, то есть на ПОСЛЕДНЕМ из {steps} " +
                "догоняющих шагов — цикл догона кончился бы сам, и снятие break не дало бы " +
                "ни одного лишнего шага, который эти ассерты могли бы увидеть");
            return contactStep;
        }

        [Test]
        public void WallOnACatchUpStep_EndsTheRoundInThePast_SpawnBeforeEnd()
        {
            // ⭐⭐ THE DEGENERATE CASE THE WHOLE TASK IS MOST EXPOSED TO: the
            // wall met on a CATCH-UP step, not on the ordinary one. Two things
            // are witnessed here and the second is why the fixture had to move.
            //
            // ORDER OF EMITS (spec §3.6): the spawn is emitted BEFORE the
            // catch-up runs, because the snapshot assembler opens its
            // per-viewer subscription on the spawn and closes it on the ending.
            // An ending that arrived first would address a set nobody is in.
            //
            // AND THE SWAP-REMOVE (coordinator RULING 172). A round that dies
            // on a catch-up step is the LAST slot of the live set, so
            // RemoveProjectileAt puts its own index past ProjectileCount; a
            // catch-up loop that did not stop there would step a copy outside
            // the live set, emit a SECOND ending for one round and, on its
            // second removal, walk the count below zero. The two trailing
            // assertions are that witness: nothing left in flight, exactly one
            // ending on the wire.
            //
            // ⛔ AND THAT WITNESS ONLY EXISTS BECAUSE A CATCH-UP STEP IS LEFT
            // UNEXECUTED (coordinator RULING 179, executor finding of the
            // green round). The contact has to land BEFORE the last of the
            // three catch-up steps, or the loop would end of its own accord on
            // the very step that kills the round and dropping the `break`
            // would change nothing observable -- the guard would read as
            // witnessed while no assertion here could tell it from its own
            // absence. With the contact on the SECOND step the third is the
            // one that must not run: it would re-step a copy whose Pos the
            // barrier arm never advanced, meet the same circle again, put a
            // second ProjectileBlocked on the wire, and then call
            // RemoveProjectileAt against an already empty set.
            //
            // ⛔ THE FIXTURE DEPARTS FROM THE PLAN'S, AND THE PLAN'S OWN NUMBER
            // IS WHY (coordinator RULING 174). It stood the collector
            // ObstacleRadius + 1.5 m from the obstacle center on the strength
            // of a 0.78 m muzzle-to-wall gap, i.e. of a muzzle sitting at
            // Weapon.MuzzleOffset. It does not: WeaponSystem.SpawnShot walks
            // the round out by the fire cooldown's overshoot as well, and on
            // the first shot of a match that overshoot is a whole tick, so the
            // muzzle stands at 0.6 + 35/30 = 1.7666667 m. At the plan's spacing
            // the round would have been born INSIDE the obstacle circle.
            //
            // THE SPACING IS ObstacleRadius + 3.6, AND BOTH HALVES OF THAT
            // NUMBER ARE LOAD-BEARING (coordinator RULING 179). The collector
            // stands 5.8 m from the obstacle's center, so the muzzle sits
            // 1.8333333 m short of its edge and 1.7133333 m short of the
            // CONTACT -- the solver adds the round's own 0.12 m radius to the
            // circle. Against catch-up steps that end at 1.1666667 / 2.3333333
            // / 3.5 m that puts the contact a little under halfway through the
            // SECOND of the three.
            //   * further out than the ordinary step (1.1666667 m), or the
            //     round would meet the wall with no catch-up at all and this
            //     test would have been green on the red phase -- the sentinel
            //     the plan wrote and the reason RULING 174 moved it;
            //   * nearer than the THIRD step, or the loop would end by itself
            //     on the step that kills the round and the two trailing
            //     assertions would witness nothing (see the RULING 172
            //     paragraph above).
            // An earlier spacing of ObstacleRadius + 4.5 satisfied only the
            // first, and ObstacleRadius + 2.8 only the second. ⚠ Every number
            // here is derived from the solver's own radius sum, not read off a
            // run.
            //   ⛔ AND THE SPACING NO LONGER CARRIES THAT REASONING ALONE
            // (coordinator RULING 185). Both halves above are true only inside
            // a narrow band of ProjectileSpeed, and this test used to lose them
            // in complete silence if the balance left it — so the fixture now
            // asserts its own premise through
            // AssertContactLandsOnAMiddleCatchUpStep, which derives the contact
            // and the step it falls on from the config and names which half
            // went when it goes.
            //
            // ⛔ AND MaxRicochets IS STATED, WHICH THE PLAN DID NOT DO
            // (coordinator Ruling 94's own rule, already obeyed by three
            // fixtures elsewhere in the suite): the shared baseline ships ONE
            // ricochet, and TryRicochet returns BEFORE the ProjectileBlocked
            // this test waits for. A round reflecting off the obstacle would
            // leave endAt at -1 forever, on correct code as much as on the
            // stub. A test whose subject is a round DYING on a barrier states
            // the zero itself.
            //
            // Quiet(), not Default(): the same twenty obstacles and the same
            // walls, with waves pushed out of reach so no gunner wanders into
            // the firing line (RULING 175). OpenField() cannot serve here -- it
            // inherits Open(), which has no obstacles at all.
            //
            // ⚠ AND THE COLLECTOR STANDS IN THE CORE, so this tick also
            // activates the Director (lesson 590's own hazard). It cannot reach
            // what is measured here: the phase machine is the LAST step of
            // TickAll, so the boss and his retinue are born after the round has
            // already met the wall, and neither of them emits a projectile
            // event. The fixture is single-tick precisely so that stays true.
            SimConfig cfg = TestConfigs.Quiet();
            cfg.Weapon.MaxRicochets = 0;
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            TestWorlds.RelocatePlayerForTest(w, 0,
                obstacle - new float2(cfg.Arena.ObstacleRadius[0] + 3.6f, 0f));
            AssertContactLandsOnAMiddleCatchUpStep(in cfg,
                math.distance(w.PlayerAt(0).Pos, obstacle), cfg.Arena.ObstacleRadius[0]);
            var lagged = new SimInput { FireHeld = true, AimPoint = obstacle,
                RewindTicks = (byte)cfg.Arena.RewindCapTicks };
            w.Tick(lagged);

            int spawnAt = -1, endAt = -1;
            for (int i = 0; i < w.EventCount; i++)
            {
                // ⚠ The SIM kind is named ProjectileFired; ProjectileSpawned is
                // a WIRE kind (SnapshotEventKind) and would not compile here.
                SimEventKind k = w.GetEvent(i).Kind;
                if (k == SimEventKind.ProjectileFired && spawnAt < 0) spawnAt = i;
                if (k == SimEventKind.ProjectileBlocked && endAt < 0) endAt = i;
            }
            Assert.GreaterOrEqual(spawnAt, 0, "события спавна нет вовсе");
            Assert.GreaterOrEqual(endAt, 0, "снаряд не встретил стену на догоне");
            Assert.Less(spawnAt, endAt, "конец эмитится РАНЬШЕ спавна — подписка не откроется");
            Assert.AreEqual(0, w.ProjectileCount,
                "погибший на догоне снаряд остался на доске — догон шагает по снятой памяти");
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked),
                "у одного раунда два конца — догон не прервался на снятом снаряде");
        }

        [Test]
        public void MobFiredRound_GetsNoRewindAtAll()
        {
            // ⛔ A SENTINEL, SAID OUT LOUD (lesson 427): this test is green on
            // the day it is written and stays green after the feature lands.
            // A mob has no client and no one-way delay, so its depth is zero --
            // but nothing CHECKS that. Mobs spawn their rounds by calling
            // SimulationWorld.SpawnProjectile straight from MobAiSystem,
            // bypassing WeaponSystem entirely, and the catch-up lives in the
            // collector's weapon phase; the zero is produced by the path being
            // different, not by a rule (coordinator RULING 177). What can kill
            // this test is therefore a STRUCTURAL mutation -- moving the
            // catch-up into the mob path (M73) -- and not a damaged line.
            //
            // The witness itself is a distance: on its birth tick a gunner's
            // round stands exactly ONE ordinary step out from its own muzzle,
            // and the muzzle sits on the shooter's collision circle.
            //
            // ⚠ TWO THINGS THE FIXTURE HAS TO STATE. OpenField(), because
            // Open() puts the collector on the spawn ring 159 m away and the
            // gunner would never see him. And the gunner stands EXACTLY at
            // PreferredRange: outside the tolerance band around it, UpdateGunner
            // drives the mob into Reposition and returns, erasing the Fire state
            // the seam just set, so a gunner placed further out would never
            // fire at all.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Gunner, new float2(cfg.Gunner.PreferredRange, 0f)));
            var g = w.Mobs[0]; g.Ai = MobAiState.Fire; g.FireCooldown = 0f; w.SetMobForTest(0, g);

            // The collector claims the FULL depth -- it must not touch the
            // mob's round.
            var deepInput = new SimInput { RewindTicks = (byte)cfg.Arena.RewindCapTicks,
                AimHeight = cfg.Hero.MuzzleHeight };
            // The budget is a fixture EXPRESSION, not a magic number: the FSM
            // may spend a tick or two before the shot goes out.
            int budget = SimulationWorld.TicksFromSeconds(cfg.Gunner.FireInterval);
            for (int i = 0; i < budget && w.ProjectileCount == 0; i++) w.Tick(deepInput);

            Assert.AreEqual(1, w.ProjectileCount, "ганнер не выстрелил — фикстура не о том");
            float step = cfg.Gunner.ProjectileSpeed * SimulationWorld.TickDt;
            float traveled = math.distance(w.Projectiles[0].Pos, w.Mobs[0].Pos);
            Assert.Less(traveled, cfg.Gunner.Radius + 2f * step,
                "мобий снаряд прокручен догоняющими шагами — k не обнулён для мобов");
        }

        [Test]
        public void TargetThatLeavesThreeTicksAfterTheShot_IsNotHit()
        {
            // ⛔ A SENTINEL, AND ITS ONLY WITNESS IS A MUTATION (lesson 427,
            // the same shape the Т25 and Т26 rounds each ended up carrying).
            // It is green against this task's stub -- a round with no catch-up
            // at all reaches the target even later -- and green against the
            // finished feature, because the rewind is spent ONCE, on the birth
            // tick. What kills it is mutation M74: crank the round by the full
            // input depth on EVERY tick of its flight, which is the canceled
            // Р381 design, and the round arrives while the target is still on
            // the line.
            //
            // ⚠ ON THIS TASK "THE TARGET LEFT" MEANS ITS BODY PHYSICALLY LEFT
            // THE FIRING LINE. Asking where a body STOOD k ticks ago is the
            // other half of the compensation and does not exist yet, so the
            // fixture moves the body instead of rewinding it. The assertion
            // core is unaffected by that; only the fixture is.
            //
            // THE ARITHMETIC IT IS BUILT ON. After N ticks a correct round has
            // taken (N + k) steps from the muzzle, where k is the input half of
            // the depth; the mutant has taken N * (1 + k). At the arena cap k is
            // 3 and a step is 1.167 m, so with the target 13 m out and the
            // muzzle at 1.767 m the correct round stands 9.93 m downrange when
            // the target leaves, 2.5 m short of the 12.43 m contact circle --
            // while the mutant crossed that circle during the THIRD tick, two
            // ticks before the target moved.
            //
            // A SECOND COLLECTOR IS THE TARGET, not a mob, and that is what
            // makes "the target left" a decision of the fixture rather than of
            // an FSM: a collector handed no input does not move at all, and
            // relocating him is one call.
            SimConfig cfg = TestConfigs.OpenField();
            // No cone: every number above is a straight line down +X, and a
            // randomized muzzle angle would move the contact those numbers are
            // built from -- the same statement, for the same reason, that the
            // PvP damage fixtures make about themselves.
            cfg.Weapon.SpreadRad = 0f;
            cfg.Weapon.RecoilPerShotRad = 0f;
            const float targetX = 13f;
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(targetX, 0f));

            var idle = default(SimInput);
            var inputs = new SimInput[2];
            inputs[0] = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f),
                AimHeight = cfg.Hero.MuzzleHeight,
                RewindTicks = (byte)cfg.Arena.RewindCapTicks };
            inputs[1] = idle;
            w.TickAll(inputs);
            Assert.AreEqual(1, w.ProjectileCount, "выстрела не было — фикстура ничего не мерит");

            inputs[0] = idle;
            for (int i = 0; i < 3; i++) w.TickAll(inputs);

            // Three ticks on, and the round must still be SHORT of the target:
            // that IS "the rewind is spent once". Under M74 it is not merely
            // further along, it is already gone -- the count below is what says
            // so first.
            float contactX = targetX - cfg.Hero.Radius - cfg.Weapon.ProjectileRadius;
            Assert.AreEqual(1, w.ProjectileCount,
                "снаряд уже израсходован — отмотка потрачена не один раз, а каждый тик");
            Assert.Less(w.Projectiles[0].Pos.x, contactX,
                "снаряд дошёл до цели за три тика — отмотка тратится каждый тик");

            // The target leaves the line of fire, and the round flies out its
            // lifetime past the place he stood.
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(targetX, 50f));
            for (int i = 0; i < 10; i++) w.TickAll(inputs);

            Assert.AreEqual(cfg.Hero.MaxHp, w.PlayerAt(1).Hp, 1e-4f,
                "ушедшая с линии цель поражена — отмотка потрачена больше одного раза");
        }

        [Test]
        public void TwoCollectorsWithDifferentLag_EachGetTheirOwnCatchUp()
        {
            // ⭐ THE DEGENERATE CASE spec §3.6 NAMES BY NAME AND NOTHING IN THE
            // TREE WITNESSED (coordinator RULING 176): two collectors firing on
            // ONE tick with DIFFERENT depths. Two claims, not one -- the depth a
            // round is cranked by comes from ITS OWN shooter's input, and the
            // order the weapon phase and the backwards projectile pass run in
            // does not swap the two rounds.
            //
            // THE ROUNDS ARE TOLD APART BY OwnerIndex, AND THE y-PAIR BELOW IS
            // THE PREMISE OF THE MEASUREMENT rather than a witness of its own
            // (coordinator RULING 190). The two shooters stand 80 m apart
            // across the X axis, so that pair says the round the OwnerIndex
            // scan matched to a shooter really is traveling along THAT
            // shooter's line -- which is exactly what makes the two travels
            // below distances from the right origins.
            //   ⚠ IT DOES NOT CATCH A CRANK SENT TO THE WRONG SLOT, and the
            // claim that it did was wrong. Both shots run down +X, so cranking
            // either round by anybody's depth moves x and leaves y where it
            // was; under a swapped depth both y checks stay green. What catches
            // that is the LAST assertion, the difference of the two travels --
            // and mutation M72, which takes the depth from the neighboring
            // shooter, killed this test through that line and no other.
            //
            // ⚠ THE TWO DEPTHS ARE THE EXTREMES OF THE DOMAIN: nothing at all
            // for one shooter, the arena cap for the other, so the expected gap
            // is the whole input half.
            //
            // NEITHER COLLECTOR STANDS AT THE ORIGIN (lesson 590), and
            // OpenField() carries no zone boundaries at all, so no Director is
            // born in the middle of what this fixture measures.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(0f, -40f));
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, 40f));

            var inputs = new SimInput[2];
            inputs[0] = new SimInput { FireHeld = true, AimPoint = new float2(30f, -40f),
                AimHeight = cfg.Hero.MuzzleHeight, RewindTicks = 0 };
            inputs[1] = new SimInput { FireHeld = true, AimPoint = new float2(30f, 40f),
                AimHeight = cfg.Hero.MuzzleHeight,
                RewindTicks = (byte)cfg.Arena.RewindCapTicks };
            w.TickAll(inputs);

            Assert.AreEqual(2, w.ProjectileCount, "оба сборщика обязаны выстрелить");
            int still = -1, lagging = -1;
            for (int i = 0; i < w.ProjectileCount; i++)
            {
                if (w.Projectiles[i].OwnerIndex == 0) still = i;
                if (w.Projectiles[i].OwnerIndex == 1) lagging = i;
            }
            Assert.GreaterOrEqual(still, 0, "снаряда сборщика без задержки нет вовсе");
            Assert.GreaterOrEqual(lagging, 0, "снаряда лагающего сборщика нет вовсе");
            Assert.AreEqual(w.PlayerAt(0).Pos.y, w.Projectiles[still].Pos.y, 1f,
                "снаряд приписан не тому стрелку — он летит по чужой линии");
            Assert.AreEqual(w.PlayerAt(1).Pos.y, w.Projectiles[lagging].Pos.y, 1f,
                "снаряд приписан не тому стрелку — он летит по чужой линии");

            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            float stillTravel = math.distance(w.Projectiles[still].Pos, w.PlayerAt(0).Pos);
            float laggingTravel = math.distance(w.Projectiles[lagging].Pos, w.PlayerAt(1).Pos);
            float inputHalf = cfg.Arena.RewindCapTicks - cfg.Arena.RewindPictureTicks;
            Assert.AreEqual(inputHalf * step, laggingTravel - stillTravel, 0.05f,
                "глубина отмотки взята не из ввода СВОЕГО стрелка");
        }

        [Test]
        public void SpawnRefusedByTheCap_DoesNotCrankSomebodyElsesRound()
        {
            // ⭐⭐ THE AXIS THE GUARD IN WeaponSystem.SpawnShot EXISTS FOR, and
            // nothing in the tree had it (coordinator RULING 180).
            //
            // ⛔ A SENTINEL, SAID OUT LOUD (lesson 427), the same shape tests
            // 44b and the once-only test above already carry: it is green
            // against this task's stub -- which called no catch-up at all, so
            // nothing could be cranked by anybody's depth -- and green against
            // the finished feature. Its ONLY witness is a mutation, and the
            // mutation is a named one: drop `if (projectileId >= 0)` in
            // WeaponSystem.SpawnShot and crank ProjectileCount - 1
            // unconditionally.
            // SimulationWorld.SpawnProjectile answers an ID, not a slot, and on
            // a full array it answers -1 having spawned nothing. The catch-up
            // addresses the fresh round as ProjectileCount - 1, so a caller
            // that did not read that answer would hand the LAST LIVE round --
            // somebody else's -- the depth of a shot that was never born.
            //
            // ⚠ THIS IS NOT A SECOND COPY OF WeaponTests.
            // ProjectileCap_SkipsDeterministically, which already covers the
            // refusal itself. That fixture floods the cap with ONE shooter at
            // depth zero, so the catch-up it triggers is zero steps long and
            // never reads the index at all -- dropping the guard there changes
            // nothing whatever. What is witnessed here is the PAIR: a refusal
            // AND a nonzero depth standing behind it, which is the only shape
            // in which a missing guard corrupts anything.
            //
            // THE FIXTURE IS THE SMALLEST THING THAT MAKES THAT PAIR -- one
            // projectile slot and two collectors firing on ONE tick. The weapon
            // phase walks players by increasing index (SimulationWorld.TickAll,
            // and the fixture above turns on the same ordering), so collector 0
            // takes the only slot and collector 1 is refused; collector 1 is
            // the one claiming the arena cap, so under a dropped guard his
            // three catch-up steps would be spent on collector 0's round.
            //
            // THE NUMBER THAT SEPARATES THE TWO OUTCOMES. Collector 0 fires at
            // depth zero, so his round owes no catch-up and ends the tick one
            // ORDINARY step out from his own muzzle: 0.6 m of MuzzleOffset plus
            // a whole tick of overshoot -- the first shot of a match leaves
            // FireCooldown at -TickDt, so the pre-advance is a full 35/30 m --
            // plus the projectile pass's own 35/30 m, i.e. 2.9333333 m. Under a
            // dropped guard that same round would also have taken collector 1's
            // three catch-up steps and stood at 6.4333333 m. The gap is 3.5 m
            // against a tolerance of 0.05.
            //
            // OpenField(), and neither collector at the origin, for the two
            // reasons the fixtures above state: Open() spawns them 159 m out on
            // the ring, and a body at the origin activates the Director in the
            // middle of the measurement (lesson 590).
            SimConfig cfg = TestConfigs.OpenField();
            // ONE slot, so the second shot of the tick is refused. Sized in the
            // fixture rather than reached by a flood (the way WeaponTests gets
            // to the same branch) because the refusal has to land on a SPECIFIC
            // shooter -- the deep one -- and a flood cannot say which.
            cfg.Arena.MaxProjectiles = 1;
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(0f, -40f));
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, 40f));

            var inputs = new SimInput[2];
            inputs[0] = new SimInput { FireHeld = true, AimPoint = new float2(30f, -40f),
                AimHeight = cfg.Hero.MuzzleHeight, RewindTicks = 0 };
            inputs[1] = new SimInput { FireHeld = true, AimPoint = new float2(30f, 40f),
                AimHeight = cfg.Hero.MuzzleHeight,
                RewindTicks = (byte)cfg.Arena.RewindCapTicks };
            w.TickAll(inputs);

            Assert.AreEqual(1, w.ProjectileCount, "слот один — снаряд обязан быть ровно один");
            Assert.Greater(w.WorldStats.ProjectileSpawnsSkipped, 0,
                "спавн никому не отказали — фикстура не о том");
            // The premise of the measurement below: it reads the distance from
            // collector 0, so the surviving round has to be his.
            Assert.AreEqual((byte)0, w.Projectiles[0].OwnerIndex,
                "слот занял не тот сборщик — оружейная фаза пошла не по индексу");

            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            float muzzle = cfg.Weapon.MuzzleOffset
                + SimulationWorld.TickDt * cfg.Weapon.ProjectileSpeed;
            float traveled = math.distance(w.Projectiles[0].Pos, w.PlayerAt(0).Pos);
            Assert.AreEqual(muzzle + step, traveled, 0.05f,
                "снаряд прокручен чужой глубиной — снята охрана if (projectileId >= 0)");
        }

        [Test]
        public void BodyOnACatchUpStep_EndsTheRound_SpawnBeforeEnd()
        {
            // ⭐⭐ THE MOST COMMON ENDING A CATCH-UP HAS IN A REAL MATCH, and
            // until now the only ending witnessed on one was a BARRIER
            // (coordinator RULING 186). Five sites in StepProjectile lower
            // `stillInSlot`; the wall fixture covers one of them, and this
            // covers the arm the whole rewind exists for -- a round meeting a
            // BODY. The three things witnessed are the wall fixture's three,
            // for the same reasons its doc gives at length: the round leaves
            // the board, the round reports exactly ONE ending, and the spawn is
            // emitted before that ending so the snapshot assembler's per-viewer
            // subscription is open when the ending addresses it.
            //
            // A SECOND COLLECTOR IS THE BODY, not a mob, and that choice is
            // the one TargetThatLeavesThreeTicksAfterTheShot_IsNotHit already
            // makes: a collector handed no input does not move at all, so "the
            // target stands exactly there" is a decision of the fixture instead
            // of an outcome of an FSM. It also keeps the tick single -- MobAiSystem
            // runs AFTER the weapon phase, so a mob would be measured from
            // where it stood last tick and the fixture would have to say so.
            //
            // ⚠ THE PIERCE CANNOT INTERFERE AND GETS NO FIXTURE OF ITS OWN
            // HERE, which is a decision rather than a gap: at the shipped
            // numbers the rule refuses every body in the game (2.6 against the
            // lightest 70 kg is 0.037 under a threshold of 0.06), it needs a
            // STRICT overkill besides -- 12 damage against 100 Hp is not one --
            // and app-vb5u is the epic that turns the knob. A fixture raising
            // that threshold would be witnessing Т20's rule, not Т27's.
            //
            // OpenField(), and the shooter is at the origin only because this
            // fixture carries no zones at all -- Open() would put both
            // collectors 159 m out on the ring, and a zoned arena would
            // activate the Director in the middle of the measurement (lesson
            // 590).
            SimConfig cfg = TestConfigs.OpenField();
            // No cone: the premise below is a straight line down +X, and a
            // randomized muzzle angle would move the contact it is computed
            // from -- the same statement, for the same reason, that
            // TargetThatLeavesThreeTicksAfterTheShot_IsNotHit makes about
            // itself.
            cfg.Weapon.SpreadRad = 0f;
            cfg.Weapon.RecoilPerShotRad = 0f;
            // Close enough that the contact falls on the SECOND of the three
            // catch-up steps -- asserted below, not trusted.
            const float targetX = 4.05f;
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(targetX, 0f));
            AssertContactLandsOnAMiddleCatchUpStep(in cfg,
                math.distance(w.PlayerAt(0).Pos, w.PlayerAt(1).Pos), cfg.Hero.Radius);

            var inputs = new SimInput[2];
            inputs[0] = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f),
                AimHeight = cfg.Hero.MuzzleHeight,
                RewindTicks = (byte)cfg.Arena.RewindCapTicks };
            inputs[1] = default(SimInput);
            w.TickAll(inputs);

            int spawnAt = -1, endAt = -1;
            for (int i = 0; i < w.EventCount; i++)
            {
                SimEventKind k = w.GetEvent(i).Kind;
                if (k == SimEventKind.ProjectileFired && spawnAt < 0) spawnAt = i;
                if (k == SimEventKind.ProjectileHitPlayer && endAt < 0) endAt = i;
            }
            Assert.GreaterOrEqual(spawnAt, 0, "события спавна нет вовсе");
            Assert.GreaterOrEqual(endAt, 0, "снаряд не встретил тело на догоне");
            Assert.Less(spawnAt, endAt, "конец эмитится РАНЬШЕ спавна — подписка не откроется");
            Assert.AreEqual(0, w.ProjectileCount,
                "погибший на теле снаряд остался на доске — догон шагает по снятой памяти");
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileHitPlayer),
                "у одного раунда два конца — догон не прервался на снятом снаряде");
            Assert.Less(w.PlayerAt(1).Hp, cfg.Hero.MaxHp,
                "цель не получила урона — конец пришёлся не на неё");
        }

        [Test]
        public void RicochetOnACatchUpStep_KeepsTheRoundAndTheRestOfItsSteps()
        {
            // ⭐⭐ THE OTHER HALF OF StepProjectile's CONTRACT, and nothing in
            // the tree asserted it (coordinator RULING 186): `true` does NOT
            // mean "nothing was hit". A ricochet resolves a contact and leaves
            // the round in its slot on purpose, so the catch-up owes it the
            // REST of its steps -- a loop that stopped on any contact would
            // quietly hand a lagging shooter a shorter-ranged bounce than
            // everybody else, which is different physics rather than a smaller
            // bug.
            //
            // ⛔ AND THIS FIXTURE DOES NOT STATE MaxRicochets, WHERE THE WALL
            // FIXTURE STATES ZERO. It runs on the shared baseline's own value,
            // which is ONE -- a documented deviation from the game number of 2
            // that TestConfigs states and ConfigTests guards, not the shipped
            // number itself. That is the point: the branch under test is the
            // one a real match takes.
            //
            // A SECOND CONTACT AT t = 0 IS IMPOSSIBLE, checked in TryRicochet's
            // body before this fixture was built: it seats the round at the
            // contact plus one Geometry.Skin ALONG THE NORMAL, i.e. just
            // outside the padded circle, precisely so the next step does not
            // answer t = 0 with the outward normal and extinguish the round on
            // its own touchdown point.
            //
            // ⚠ THE PIERCE GETS NO WITNESS ON THE CATCH-UP, AND THAT IS A
            // DECISION: at the shipped numbers it refuses every body in the
            // game (2.6 against the lightest 70 kg is 0.037 under a threshold
            // of 0.06), app-vb5u is the epic that turns that knob, and a
            // fixture raising the threshold would be witnessing Т20's rule
            // rather than Т27's.
            //
            // THE ARITHMETIC. The contact lands on the SECOND of three catch-up
            // steps (asserted, not assumed), the reflection is head-on off a
            // circle the round flies straight at, so the round leaves at
            // ProjectileSpeed * RicochetRetention back down -X, and TWO steps of
            // that damped speed are still owed to it on this tick: the third
            // catch-up step and the ordinary ProjectileSystem one. A catch-up
            // that broke on the ricochet would spend only the ordinary one and
            // leave the round a whole damped step short of where the last
            // assertion looks.
            //
            // Quiet(), not Default(), and Т19's own reason: the same twenty
            // obstacles and the same walls with the waves pushed out of reach,
            // so no gunner wanders into the firing line. The collector stands
            // in the core and this tick therefore activates the Director; it
            // cannot reach what is measured here, because the phase machine is
            // the LAST step of TickAll and the fixture is single-tick.
            SimConfig cfg = TestConfigs.Quiet();
            // No cone, so the shot meets the circle head-on and the reflection
            // is exactly -X: the expected position below is an arithmetic
            // consequence of the fixture rather than a number off a run.
            cfg.Weapon.SpreadRad = 0f;
            cfg.Weapon.RecoilPerShotRad = 0f;
            var w = new SimulationWorld(7, cfg);
            float2 obstacle = cfg.Arena.ObstaclePos[0];
            TestWorlds.RelocatePlayerForTest(w, 0,
                obstacle - new float2(cfg.Arena.ObstacleRadius[0] + 3.6f, 0f));

            // The two TryRicochet gates this fixture does not otherwise state.
            // The counter is the whole subject; the speed floor would silently
            // turn this into the wall fixture if the balance ever crossed it.
            Assert.Greater(cfg.Weapon.MaxRicochets, 0,
                "премисса фикстуры: базовая конфигурация запрещает рикошет, и контакт " +
                "погасил бы раунд — это фикстура стены, а не эта");
            Assert.GreaterOrEqual(cfg.Weapon.ProjectileSpeed * cfg.Weapon.RicochetRetention,
                cfg.Weapon.RicochetMinSpeed,
                "премисса фикстуры: гашёная скорость упала ниже порога рикошета, и контакт " +
                "погасил бы раунд");
            int contactStep = AssertContactLandsOnAMiddleCatchUpStep(in cfg,
                math.distance(w.PlayerAt(0).Pos, obstacle), cfg.Arena.ObstacleRadius[0]);

            var lagged = new SimInput { FireHeld = true, AimPoint = obstacle,
                RewindTicks = (byte)cfg.Arena.RewindCapTicks };
            w.Tick(lagged);

            Assert.AreEqual(1, w.ProjectileCount,
                "отскочивший раунд снят с доски — догон прервался там, где снаряд остался в слоте");
            Assert.AreEqual(1, w.Projectiles[0].Ricochets,
                "отскока не случилось — фикстура мерит не то");
            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked),
                "отскок эмитил конец раунда — рикошет обязан молчать до Т30");
            Assert.Less(w.Projectiles[0].Vel.x, 0f, "снаряд не развернулся");

            int steps = cfg.Arena.RewindCapTicks - cfg.Arena.RewindPictureTicks;
            float back = cfg.Weapon.ProjectileSpeed * cfg.Weapon.RicochetRetention
                * SimulationWorld.TickDt;
            float contactX = obstacle.x - cfg.Arena.ObstacleRadius[0] - cfg.Weapon.ProjectileRadius;
            float expectedX = contactX - Geometry.Skin - (steps - contactStep + 1) * back;
            Assert.AreEqual(expectedX, w.Projectiles[0].Pos.x, 0.05f,
                "остаток догоняющих шагов раунду не сохранён — догон прервался на рикошете, " +
                "и отскочивший снаряд лагающего стрелка отлетел на целый шаг меньше");
        }
    }
}
