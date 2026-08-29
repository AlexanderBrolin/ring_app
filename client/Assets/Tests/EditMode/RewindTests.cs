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
        public void HistorySlot_SurvivesASwapRemoveOfANeighbor()
        {
            // ⭐ TEST 33 -- the witness of Р406. A mob dies in the MIDDLE of
            // the window, and the SURVIVOR's history must not shift: over six
            // ticks one array index has time to be three different mobs, a
            // slot does not.
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
            // AND THE NEIGHBOUR ABOVE CANNOT CARRY THIS WEIGHT, which is why
            // this is a fifth test rather than two more asserts on the fourth:
            // in DeadBodysSlot_IsReused_ButNotItsPast exactly ONE mob is alive
            // when the kill lands, so `_mobs[index] = _mobs[--_mobCount]`
            // degenerates into `_mobs[0] = _mobs[0]`. After a self-assignment
            // the slot reads the same before and after the swap, and the two
            // orderings are indistinguishable. It takes a SECOND live body for
            // the readings to differ at all, and that is the whole of what
            // this fixture adds.
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
        }
    }
}
