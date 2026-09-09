using NUnit.Framework;
using Ring.Networking;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// The client's own record of "I fired" (app-8dv T4, spec §3.4, Р451,
    /// ruling 319) -- written INSIDE the predicted tick and drained by the
    /// frame.
    ///
    /// TWO GROUPS OF TESTS, AND THEY WITNESS DIFFERENT THINGS. The first five
    /// examine the log AS A TYPE: capacity, refusal, draining, stamping, reset.
    /// The last two examine the SINK -- that a predicted tick actually leaves a
    /// record, and that the record carries the geometry of THAT MOMENT rather
    /// than of the state the tick ended in. The second group is the one the
    /// whole task exists for, and it is also the witness for the DoD line
    /// "client and server agree on the angle": the record is born inside the
    /// same tick the world fires in, so it carries the same cone, the same
    /// BurstShots and the same overshoot.
    public class PredictedShotLogTests
    {
        /// A solution with recognizable numbers -- the log stores it whole and
        /// is not supposed to look inside, so the values only have to be
        /// distinguishable from `default`.
        static ShotSolution Marker(float x) => new ShotSolution(
            spawnPos: new float2(x, 2f), height: 1.25f, vel: new float2(35f, 0f),
            dir: new float2(1f, 0f), velZ: 0.5f, pictureTicks: 3, inputTicks: 2);

        [Test]
        public void RecordsUpToCapacity()
        {
            var log = new PredictedShotLog(capacity: 4);
            log.BeginTick(10u);
            for (int i = 0; i < 4; i++) log.Record(i + 1, Marker(i));

            var shots = log.Drain();
            Assert.AreEqual(4, shots.Length, "журнал не удержал ёмкость записей");
            Assert.AreEqual(0, log.OverflowDroppedShots, "отказа не было, а счётчик вырос");
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(i + 1, shots[i].Key, "записи вышли не в том порядке");
        }

        /// Р82, and the neighbor is `ClientEventQueue`: overflow REFUSES the
        /// newcomer and counts it, rather than throwing or quietly evicting a
        /// resident. A refused shot costs one predicted trail, which the
        /// authoritative `ProjectileSpawned` will draw a moment later anyway.
        [Test]
        public void OverflowRefusesTheNewcomerAndCountsIt()
        {
            var log = new PredictedShotLog(capacity: 2);
            log.BeginTick(10u);
            log.Record(1, Marker(1f));
            log.Record(2, Marker(2f));
            log.Record(3, Marker(3f));
            log.Record(4, Marker(4f));

            var shots = log.Drain();
            Assert.AreEqual(2, shots.Length, "переполнение вытеснило старожила вместо отказа новичку");
            Assert.AreEqual(1, shots[0].Key, "вытеснён старожил, а отказать полагалось новичку");
            Assert.AreEqual(2, log.OverflowDroppedShots, "отказы не сосчитаны");
        }

        /// The default capacity is the number the production caller gets, and
        /// nothing else in the tree pins it: every fixture above asks for a
        /// capacity of its own, so a mutant that halved this constant would pass
        /// all of them (review finding). Sixteen is a CHOSEN number -- the
        /// constant's own doc says so and says why -- and this test pins the
        /// choice rather than deriving it.
        [Test]
        public void DefaultCapacity_HoldsSixteenAndRefusesTheSeventeenth()
        {
            var log = new PredictedShotLog();
            log.BeginTick(1u);
            for (int i = 0; i < PredictedShotLog.DefaultCapacity + 1; i++) log.Record(i + 1, Marker(i));

            Assert.AreEqual(16, PredictedShotLog.DefaultCapacity, "ёмкость по умолчанию сменилась");
            Assert.AreEqual(16, log.Drain().Length, "журнал удержал не свою ёмкость");
            Assert.AreEqual(1, log.OverflowDroppedShots, "семнадцатая запись прошла без отказа");
        }

        /// The floor under the constructor's argument, which no production call
        /// reaches (the backend takes the default) and which therefore has no
        /// other witness. The neighbor `OwnDamageLane` carries the same line for
        /// the same reason.
        [Test]
        public void AnImpossibleCapacityStillHoldsOneRecord()
        {
            var log = new PredictedShotLog(capacity: 0);
            log.BeginTick(1u);
            log.Record(1, Marker(1f));

            Assert.AreEqual(1, log.Drain().Length, "журнал нулевой ёмкости потерял запись");
            Assert.AreEqual(0, log.OverflowDroppedShots, "отказа не было, а счётчик вырос");
        }

        [Test]
        public void Drain_EmptiesTheLog()
        {
            var log = new PredictedShotLog();
            log.BeginTick(10u);
            log.Record(1, Marker(1f));
            Assert.AreEqual(1, log.Drain().Length, "премисса: запись обязана быть");
            Assert.AreEqual(0, log.Drain().Length, "вычерпанное вернулось во втором Drain");
        }

        /// The tick is stamped by whoever HAS one -- `PlayerPredictionCore.
        /// Predict`, which reads it off `PerformReplicate`. `Advance` is the
        /// shared body of server and client, and a FishNet tick means nothing
        /// on the server's path, which is why the stamp does not travel as a
        /// parameter of the write.
        [Test]
        public void BeginTick_StampsTheRecordsThatFollowIt()
        {
            var log = new PredictedShotLog();
            log.BeginTick(7u);
            log.Record(1, Marker(1f));
            log.BeginTick(9u);
            log.Record(2, Marker(2f));

            var shots = log.Drain();
            Assert.AreEqual(2, shots.Length, "премисса: обе записи обязаны быть");
            Assert.AreEqual(7u, shots[0].LocalTick, "первая запись взяла чужой тик");
            Assert.AreEqual(9u, shots[1].LocalTick, "вторая запись не увидела нового тика");
        }

        /// ⛔ THE COUNTER SURVIVES `Reset`, AND THAT IS THE PRECEDENT WORD FOR
        /// WORD: "a per-connection health counter that cleared itself on every
        /// restart would hide precisely the pattern it exists to surface"
        /// (`ClientEventQueue`, whose `Reset` leaves `OverflowDroppedEvents`
        /// alone for the same reason `SnapshotQueue.Reset` leaves its own).
        /// The records themselves DO go: a shot predicted in the previous match
        /// must never be drawn in the next one.
        [Test]
        public void Reset_ForgetsRecords_ButKeepsTheOverflowCounter()
        {
            var log = new PredictedShotLog(capacity: 1);
            log.BeginTick(10u);
            log.Record(1, Marker(1f));
            log.Record(2, Marker(2f));
            Assert.AreEqual(1, log.OverflowDroppedShots, "премисса: отказ обязан был случиться");

            log.Reset();
            Assert.AreEqual(0, log.Drain().Length, "записи прошлого матча пережили Reset");
            Assert.AreEqual(1, log.OverflowDroppedShots,
                "счётчик здоровья соединения обнулился на рестарте матча");
        }

        /// ⭐⭐ THE SINK ITSELF (M244): a predicted tick that fires must leave a
        /// record. Today's code leaves none, which is what makes this a direct
        /// RED rather than a guard.
        ///
        /// The key is the POST-increment ordinal, so the first shot of a match
        /// is 1 -- `Advance` raises both counters AFTER the shot is worked out,
        /// and zero has to stay free as the "no key" sentinel.
        [Test]
        public void APredictedShotWritesARecord()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            var predicted = w.Player;
            var log = new PredictedShotLog();
            log.BeginTick(100u);

            PlayerPrediction.Step(ref predicted, TestWorlds.HipFire(), in cfg, in ImpactPulse.None,
                System.ReadOnlySpan<PushableBody>.Empty, log);

            var shots = log.Drain();
            Assert.AreEqual(1, shots.Length, "предсказанный выстрел не оставил записи");
            Assert.AreEqual(1, shots[0].Key, "первый выстрел матча обязан получить ключ 1");
            Assert.AreEqual(100u, shots[0].LocalTick, "запись взяла не тот тик");
        }

        /// ⭐⭐ THE WITNESS FOR THE DoD LINE "CLIENT AND SERVER AGREE ON THE
        /// ANGLE" (M245), and it is here rather than in T1 because only here
        /// are both halves observable at once. The record is written INSIDE the
        /// predicted tick, so it must carry the cone, the BurstShots and the
        /// overshoot of THAT MOMENT -- not of the state the tick ended in. A
        /// journal filled after the tick would read a cone already widened by
        /// this very shot's recoil and a burst counter already stepped on.
        ///
        /// ⚠ THE TWO SIDES START FROM ONE STATE AND ARE COMPARED THROUGH THE
        /// EVENT, not through a second call to the geometry: the world's
        /// `ProjectileFired` is what the server really fired.
        [Test]
        public void TheRecordCarriesThePreShotConeAndOvershoot()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            var predicted = w.Player;
            SimInput fire = TestWorlds.HipFire();
            var log = new PredictedShotLog();
            log.BeginTick(100u);
            w.ClearEvents();

            w.Tick(fire);
            PlayerPrediction.Step(ref predicted, fire, in cfg, in ImpactPulse.None,
                System.ReadOnlySpan<PushableBody>.Empty, log);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileFired, out SimEvent serverShot),
                "премисса: авторитетный тик обязан выстрелить");
            var shots = log.Drain();
            Assert.AreEqual(1, shots.Length, "премисса: предсказанный тик обязан записать выстрел");
            PredictedShot record = shots[0];

            Assert.AreEqual(serverShot.Pos.x, record.Solution.SpawnPos.x, 1e-5f,
                "точка вылета клиента разошлась с серверной по X");
            Assert.AreEqual(serverShot.Pos.y, record.Solution.SpawnPos.y, 1e-5f,
                "точка вылета клиента разошлась с серверной по Y");
            Assert.AreEqual(serverShot.Amount,
                math.atan2(record.Solution.Dir.y, record.Solution.Dir.x), 1e-5f,
                "клиент и сервер разошлись в угле — посев, конус или overshoot считаются по-разному");
            Assert.AreEqual(serverShot.BirthSteps, record.Solution.BirthSteps,
                "клиент и сервер разошлись в числе догоняющих шагов");
        }

        /// ⭐ THE PRODUCTION WIRING, WHICH NOTHING ELSE WITNESSES (review
        /// finding). Everything above drives `PlayerPrediction.Step` directly and
        /// hands it a journal by hand; in a live match the journal arrives by a
        /// different route entirely -- the backend builds it, `AttachShotLog`
        /// hands it to the core, and the core stamps it with the FishNet tick
        /// `PerformReplicate` was given. Three separate links, none of them
        /// covered: a mutant that emptied `AttachShotLog`, or passed the journal
        /// no tick, or stamped a constant, passed every other test in this tree.
        ///
        /// ⚠ THE JOURNAL IS PRE-STAMPED WITH A FOREIGN TICK ON PURPOSE, so the
        /// assertion below cannot pass by accident: if `Predict` failed to stamp
        /// its own, the record would carry 7 instead of 100.
        [Test]
        public void TheCoreStampsTheJournalWithTheTickItWasGiven()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var alive = new PlayerState
            {
                Hp = cfg.Hero.MaxHp,
                Stamina = cfg.Hero.StaminaMax,
                Alive = true,
            };
            var core = new PlayerPredictionCore();
            core.BeginReconcile(4242u, in alive);
            core.FinishReconcile();

            var log = new PredictedShotLog();
            core.AttachShotLog(log);
            log.BeginTick(7u);

            core.Predict(TestWorlds.HipFire(), in cfg, 100u);

            var shots = log.Drain();
            Assert.AreEqual(1, shots.Length,
                "журнал не доехал до ядра — предсказанный выстрел не записан");
            Assert.AreEqual(100u, shots[0].LocalTick,
                "ядро не проставило тик, который получило от PerformReplicate");
            Assert.AreEqual(1, shots[0].Key, "первый выстрел матча обязан получить ключ 1");
        }
    }
}
