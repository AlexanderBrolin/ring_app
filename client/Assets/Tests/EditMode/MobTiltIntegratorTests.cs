using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// app-88jb Т31 (coordinator Ruling 258): the client-side rebuild of a
    /// mob's tilt, measured against THE AUTHORITY rather than against the
    /// formula it is built from.
    ///
    /// WHY THE WORLD AND NOT `Impact.SpringStep`. A test that stepped the same
    /// public spring the integrator steps and compared the two would be a
    /// tautology — it would agree with a wrong step as happily as with a right
    /// one, and every mutation that matters here is about the WIRING: the
    /// order of the two calls the moment is built from, whether the step is a
    /// TICK or a frame, whether an impulse lands before or after the first
    /// step. So the witness is `SimulationWorld` itself: a mob is hit through
    /// `DamageMob`, the world is ticked, and the integrator is asked for the
    /// same pair on the same tick. The shape is `TracerProjectilesTests`', for
    /// its stated reason.
    ///
    /// THE FIXTURE IS FROZEN AND OFF THE ORIGIN. `TestWorlds.FreezeArchetype`
    /// runs BEFORE `new SimulationWorld` — zeroing the archetype's MaxSpeed and
    /// Accel is the whole of the freeze, because `MobAiState.Idle` is rewritten
    /// to Chase on the very next tick. And the mob stands at (10, 0) rather
    /// than at the collector's own spot, so that nothing about the fixture
    /// depends on the two occupying one point.
    ///
    /// NOTHING BUT `TiltSystem` MOVES `Tilt` INSIDE A TICK, and that is a fact
    /// about `TickAll` rather than an assumption this file makes: the pair is
    /// written in exactly two places in the whole simulation — the impulse in
    /// `SimulationWorld.DamageMob` and the step in `TiltSystem.Apply` — and the
    /// third writer (the tilt clamp in `ApplyConfig`) runs only when the config
    /// is replaced, which no test here does. `TiltSystem.Apply` runs once per
    /// `TickAll`, after `ProjectileSystem` and before `WaveSystem`. So the
    /// world's order is "impulse, then N steps", which is the order the
    /// integrator is driven in below.
    public class MobTiltIntegratorTests
    {
        /// Above the chaser's own center of mass, so the arm is positive and
        /// the moment is not a zero that any implementation would produce.
        const float HitHeight = 2.0f;

        /// The shooter's seat. A real one rather than `ProjectileIds.NoOwner`,
        /// because the two select DIFFERENT projectile masses and speed caps —
        /// `Impact.ProjectileMassFor` and `SnapshotEvents.SpeedCapFor` both fork
        /// on exactly this byte, and the whole reason the owner is dug back out
        /// of the tracer is that the fork is worth several times the blow.
        const byte ShooterSlot = 0;

        /// A frozen single chaser standing at (10, 0), plus the config it was
        /// built from. The freeze happens before construction, which is what
        /// `TestWorlds.FreezeArchetype`'s own doc requires.
        static SimulationWorld FrozenChaser(out SimConfig cfg)
        {
            cfg = TestConfigs.OpenField();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(10f, 0f)));
            Assert.AreEqual(1, w.MobCount, "fixture premise: exactly one body to measure");
            return w;
        }

        /// The blow the whole file is about, dealt through the authority's own
        /// seam. `projectileMass`/`projectileSpeed3D` are stated from the two
        /// homes the integrator will read them from, so the two sides are
        /// comparable by construction rather than by coincidence: a collector's
        /// round carries `Weapon.ProjectileMass` and flies at the cap
        /// `SpeedCapFor` answers for that same seat.
        static void HitTheChaser(SimulationWorld w, in SimConfig cfg)
        {
            w.DamageMob(0, 1f, new float2(10f, 0f), HitZone.Head, new float2(1f, 0f),
                ownerIndex: ShooterSlot, hitHeight: HitHeight,
                projectileMass: cfg.Weapon.ProjectileMass,
                projectileSpeed3D: SnapshotEvents.SpeedCapFor(ShooterSlot, in cfg));
        }

        /// Test 12. THE MOMENT, and it is not a restatement of the formula: the
        /// left-hand side is whatever `DamageMob` actually put into `TiltVel`,
        /// read off the world one statement after the blow. A mutation in
        /// either of the two calls the moment is built from — the wrong mass
        /// fork, the wrong speed cap, the arm measured from the ground instead
        /// of the center of mass, the damping divisor a mob does not have —
        /// moves one side and not the other.
        [Test]
        public void AngularImpulseFor_EqualsWhatDamageMobPutsIntoTiltVel()
        {
            var w = FrozenChaser(out SimConfig cfg);

            Assert.AreEqual(0f, w.Mobs[0].TiltVel, 0f,
                "fixture premise: the body carries no angular velocity before the blow, so what "
                + "is read after it IS the impulse and not a sum with something older");

            HitTheChaser(w, in cfg);
            float fromWorld = w.Mobs[0].TiltVel;

            Assert.AreNotEqual(0f, fromWorld,
                "fixture premise: the blow really did put a moment into the body — otherwise the "
                + "comparison below is two zeros agreeing");

            Assert.AreEqual(fromWorld,
                MobTiltIntegrator.AngularImpulseFor(ShooterSlot, in cfg.Chaser, HitHeight, in cfg),
                0f,
                "момент клиента разошёлся с моментом авторитетного мира — крен по сети будет "
                + "другой силы, чем оффлайн");
        }

        /// Test 13. THE CURVE, tick for tick, and the two halves are driven in
        /// the same order the world drives them: the impulse lands outside a
        /// tick, every step is one `TickAll`.
        ///
        /// THE IMPULSE IS TAKEN FROM THE WORLD RATHER THAN FROM
        /// `AngularImpulseFor`, deliberately. Feeding the integrator its own
        /// moment would tie this test to the previous one — a single mutation
        /// in that method would kill both, and neither would say which claim
        /// broke. Here the moment is a given and the STEP is the subject.
        [Test]
        public void Curve_MatchesTheAuthoritativeWorld_TickForTick()
        {
            const int ticks = 45;

            var w = FrozenChaser(out SimConfig cfg);
            int mobId = w.Mobs[0].Id;

            Assert.AreEqual(0f, w.Mobs[0].Tilt, 0f, "fixture premise: the body starts upright");
            HitTheChaser(w, in cfg);
            float impulse = w.Mobs[0].TiltVel;
            Assert.AreNotEqual(0f, impulse, "fixture premise: there is a moment to integrate");

            var integrator = new MobTiltIntegrator(in cfg);
            Assert.IsTrue(integrator.Apply(mobId, MobType.Chaser, impulse),
                "интегратор не принял импульс — сравнивать кривую не с чем");

            for (int t = 1; t <= ticks; t++)
            {
                TestWorlds.IdleTicks(w, 1);
                integrator.StepTicks(1, in cfg);

                Assert.IsTrue(integrator.TryGetTilt(mobId, out float tilt, out float tiltVel),
                    $"тик {t}: интегратор потерял моба, который ещё качается в мире");
                Assert.AreEqual(w.Mobs[0].Tilt, tilt, 1e-6f,
                    $"тик {t}: крен разошёлся с авторитетным миром");
                Assert.AreEqual(w.Mobs[0].TiltVel, tiltVel, 1e-6f,
                    $"тик {t}: угловая скорость разошлась с авторитетным миром");
            }

            Assert.AreNotEqual(0f, w.Mobs[0].Tilt,
                "witness: the body is still swinging at the end of the window, so the run above "
                + "compared a real curve rather than a pair of settled zeros");
        }

        /// Test 14. Two blows inside one tick SUM, exactly as `DamageMob` sums
        /// them (`TiltVel +=`). An implementation that overwrote the slot would
        /// make the second hit erase the first, so a body shot twice in a tick
        /// would rock as though it had been shot once.
        [Test]
        public void TwoHitsInOneTick_SumIntoOneAngularVelocity()
        {
            // Two DIFFERENT moments, so a sum is distinguishable from either
            // one doubled and from either one alone.
            const float first = 3f;
            const float second = -1.25f;
            const int mobId = 7;

            SimConfig cfg = TestConfigs.OpenField();
            var integrator = new MobTiltIntegrator(in cfg);

            Assert.IsTrue(integrator.Apply(mobId, MobType.Chaser, first),
                "первый удар не принят");
            Assert.IsTrue(integrator.Apply(mobId, MobType.Chaser, second),
                "второй удар по тому же телу не принят — слот занят, а не закрыт");
            Assert.AreEqual(1, integrator.Count,
                "два удара по одному телу — один слот, а не два: список наклонённых ищется линейно "
                + "и второй слот с тем же id разошёлся бы с первым");

            Assert.IsTrue(integrator.TryGetTilt(mobId, out _, out float tiltVel));
            Assert.AreEqual(first + second, tiltVel, 1e-6f,
                "два удара в одном тике не сложились: DamageMob складывает через +=, и тело, "
                + "получившее два попадания, обязано качнуться сильнее");
        }

        /// Test 15. THE SLOT COMES BACK. `Impact.SpringStep` snaps the pair to
        /// exact zeros through its own `RestEpsilon`, and that snap is what
        /// frees the slot — without it the table fills with bodies that stopped
        /// moving a match ago.
        ///
        /// THE WINDOW IS ASSERTED, NOT THE SNAP TICK. Three settle times is the
        /// bound `Impact.PeakTilt` states for itself, and it is an order of
        /// magnitude wider than the answer needs; the exact tick the snap lands
        /// on is a MEASUREMENT that belongs in a report, not a number a test
        /// should stand on.
        [Test]
        public void SettledSlot_IsFreed_AndCountReturnsToZero()
        {
            const int mobId = 7;

            SimConfig cfg = TestConfigs.OpenField();
            int window = (int)math.ceil(3f * cfg.Chaser.TiltSettleSeconds / SimulationWorld.TickDt);

            var integrator = new MobTiltIntegrator(in cfg);
            float impulse = MobTiltIntegrator.AngularImpulseFor(ShooterSlot, in cfg.Chaser,
                HitHeight, in cfg);

            Assert.IsTrue(integrator.Apply(mobId, MobType.Chaser, impulse),
                "удар не принят — освобождать нечего");
            // WITHOUT THIS LINE THE TEST IS SATISFIED BY A TABLE THAT NEVER
            // TOOK THE SLOT AT ALL: an empty integrator answers 0 and false to
            // everything below, which is exactly what a settled one answers.
            Assert.AreEqual(1, integrator.Count, "слот не занят — тест ниже проверял бы пустоту");

            integrator.StepTicks(window, in cfg);

            Assert.AreEqual(0, integrator.Count,
                "слот не освободился за окно затухания — таблица наполнится телами, которые давно "
                + "стоят прямо");
            Assert.IsFalse(integrator.TryGetTilt(mobId, out _, out _),
                "и защёлкнувшееся тело больше не наклонено");
        }

        /// Test 16. WHOSE tilt is patched, and what a reset forgets. The array
        /// holds three bodies and only the middle one was hit, so an
        /// implementation that wrote every slot — or the wrong one — is caught
        /// by the two that must stay upright.
        [Test]
        public void WriteInto_PatchesOnlyItsOwnIds_AndResetForgets()
        {
            const int quietLow = 5;
            const int tiltedId = 7;
            const int quietHigh = 9;

            SimConfig cfg = TestConfigs.OpenField();
            var integrator = new MobTiltIntegrator(in cfg);
            float impulse = MobTiltIntegrator.AngularImpulseFor(ShooterSlot, in cfg.Chaser,
                HitHeight, in cfg);

            Assert.IsTrue(integrator.Apply(tiltedId, MobType.Chaser, impulse), "удар не принят");
            integrator.StepTicks(1, in cfg);

            var mobs = new[]
            {
                new MobState { Id = quietLow, Type = MobType.Chaser },
                new MobState { Id = tiltedId, Type = MobType.Chaser },
                new MobState { Id = quietHigh, Type = MobType.Chaser },
            };
            integrator.WriteInto(mobs, mobs.Length);

            // The array arrives zeroed, so "the neighbors are zero" is true of
            // an integrator that wrote nothing at all — this is the assertion
            // that tells the two apart, and it has to come first.
            Assert.AreNotEqual(0f, mobs[1].Tilt,
                "крен не попал в опубликованную пару — по сети моб останется стоять прямо");
            Assert.AreEqual(0f, mobs[0].Tilt, 0f,
                "сосед с меньшим id накренился — патч идёт по id, а не по индексу");
            Assert.AreEqual(0f, mobs[2].Tilt, 0f, "сосед с большим id накренился");
            Assert.AreEqual(0f, mobs[0].TiltVel, 0f, "и угловая скорость соседа осталась нулевой");

            integrator.Reset();
            Assert.AreEqual(0, integrator.Count,
                "эпоха сменилась, а наклонённые тела прежнего матча остались");

            var again = new[]
            {
                new MobState { Id = tiltedId, Type = MobType.Chaser },
            };
            integrator.WriteInto(again, again.Length);
            Assert.AreEqual(0f, again[0].Tilt, 0f,
                "после сброса патчить нечего: новый матч раздаёт id заново, и старый крен на "
                + "чужом теле — неверный ответ, а не отсутствующий");
        }

        /// Test 17, AND IT IS NOT ONE OF THE SIXTEEN THE PLAN LISTED. The Т31
        /// mutation cycle gave the swap-remove back the `i++` its own comment
        /// forbids and ran this whole file: all five tests stayed green
        /// (M176, `task-31-mutation-evidence.md`). The hole was in the
        /// FIXTURES rather than in the claim — tests 13, 14 and 15 tilt a
        /// single body, and test 16's three `MobState` entries are the
        /// published pair's rather than this table's slots — so the table
        /// never held two occupied slots at once. With one, `RemoveAt` drops
        /// `_count` to zero and the extra `i++` walks the loop off the end
        /// exactly as `continue` does: the defect had no input that could show
        /// it. Ruling 264 closes that here, inside the task.
        ///
        /// WHAT THE WRONG BOOKKEEPING COSTS. Two bodies under fire and the
        /// lighter one settles first: `RemoveAt` moves the LAST slot into the
        /// freed index, so the body that stood behind it now sits at an index
        /// this pass has already walked past. Stepping on instead of staying
        /// put skips it for that tick, and from then on its whole curve trails
        /// the authoritative one by a tick — the client showing a different
        /// blow from the one the server resolved, which is the single thing
        /// this class exists not to do.
        ///
        /// THE REFERENCE IS THE SAME INTEGRATOR WITH A TABLE OF ONE, and that
        /// is not the tautology this file's own doc refuses: the public spring
        /// is never stepped by hand here, and the arithmetic is not the
        /// subject at all — test 13 already measures that against the
        /// authoritative world, tick for tick. The claim here belongs to the
        /// TABLE: a body's curve does not depend on who else is standing in
        /// it, and the two runs differ in exactly that and in nothing else.
        /// A step mutation moves both sides together and leaves this test
        /// green, which is the property that keeps one decision to one
        /// witness.
        ///
        /// THE ORDER OF THE TWO `Apply` CALLS IS LOAD-BEARING. `Apply`
        /// appends, so the graze takes the LOWER index and the hard blow sits
        /// behind it; freeing the lower index is what moves the hard blow
        /// BACKWARDS, into a slot the pass has already visited. Applied the
        /// other way round the freed index would be the last one, the swap
        /// would move that body onto itself, and both loop forms would end the
        /// pass identically — a fixture unable to tell them apart, which is
        /// the fixture the file had.
        ///
        /// THE TWO MOMENTS ARE TWO ORDERS OF MAGNITUDE APART BECAUSE THE
        /// SPRING IS ONE. All four archetypes carry identical tilt numbers in
        /// `TestConfigs`, so two equal blows would snap on the same tick and
        /// nothing would ever be swapped mid-pass; the RATIO is what puts the
        /// graze's rest inside the run and the hard blow's outside it. Neither
        /// is taken on trust: the run asserts that a slot really came back
        /// while the other body was still swinging, and the tick it happens on
        /// is a measurement for the report rather than a number this test
        /// stands on (test 15 states that rule for itself).
        [Test]
        public void NeighborThatSettlesFirst_DoesNotCostTheBodyBehindItATick()
        {
            const int grazedId = 5;
            const int hardHitId = 9;
            // About the moment test 12 measures for a chaser headshot on
            // this file's own fixture (8.81 rad/s), and a graze two orders of
            // magnitude under it. Neither number is read from the config: the
            // subject here is the table, and a moment taken from
            // `AngularImpulseFor` would make every mutation of THAT method
            // move this fixture's settle tick as well.
            const float hardBlow = 8f;
            const float graze = 0.05f;
            // Test 13's window, and for its reason: the hard blow is still
            // swinging at the end of it, which the last assertion pins.
            const int ticks = 45;

            SimConfig cfg = TestConfigs.OpenField();

            var crowded = new MobTiltIntegrator(in cfg);
            Assert.IsTrue(crowded.Apply(grazedId, MobType.Chaser, graze),
                "скользящий удар не принят — соседа, который освободит слот, не будет");
            Assert.IsTrue(crowded.Apply(hardHitId, MobType.Chaser, hardBlow),
                "второй удар по ДРУГОМУ телу не принят — второго слота не будет");
            Assert.AreEqual(2, crowded.Count,
                "премисса: в таблице два занятых слота — при одном своп-ремуву нечего было бы "
                + "переставлять, и тест остался бы зелёным на любой форме цикла");

            var alone = new MobTiltIntegrator(in cfg);
            Assert.IsTrue(alone.Apply(hardHitId, MobType.Chaser, hardBlow),
                "эталонное тело не принято — сравнивать не с чем");
            Assert.AreEqual(1, alone.Count, "премисса: в эталонной таблице ровно одно тело");

            int freedOnTick = 0;
            for (int t = 1; t <= ticks; t++)
            {
                crowded.StepTicks(1, in cfg);
                alone.StepTicks(1, in cfg);
                if (freedOnTick == 0 && crowded.Count == 1) freedOnTick = t;

                Assert.IsTrue(
                    alone.TryGetTilt(hardHitId, out float lonelyTilt, out float lonelyTiltVel),
                    $"тик {t}: эталонное тело выпало из своей таблицы раньше срока");
                Assert.IsTrue(crowded.TryGetTilt(hardHitId, out float tilt, out float tiltVel),
                    $"тик {t}: тело потеряно таблицей, в которой сосед освободил слот");
                Assert.AreEqual(lonelyTilt, tilt, 1e-6f,
                    $"тик {t}: крен тела зависит от того, кто ещё стоял в таблице — своп-ремув "
                    + "пропустил переехавшее тело, и его кривая отстала на тик от авторитетной");
                Assert.AreEqual(lonelyTiltVel, tiltVel, 1e-6f,
                    $"тик {t}: угловая скорость тела зависит от соседей по таблице");
            }

            Assert.AreNotEqual(0, freedOnTick,
                "премисса: сосед так и не защёлкнулся за окно — своп-ремув ни разу не исполнился, "
                + "и сравнение выше не проверило ничего");
            Assert.AreEqual(1, crowded.Count,
                "и освободился ровно один слот: второе тело обязано было пережить окно");
            Assert.IsFalse(crowded.TryGetTilt(grazedId, out _, out _),
                "защёлкнувшийся сосед остался в таблице");
            Assert.IsTrue(crowded.TryGetTilt(hardHitId, out float finalTilt, out _));
            Assert.AreNotEqual(0f, finalTilt,
                "witness: the hard-hit body is still swinging at the end of the window, so the "
                + "run above compared two live curves rather than two settled zeros");
        }

        /// Test 18, from the review round (finding B-8). A FULL TABLE REFUSES
        /// BY VALUE, and the refusal costs the bodies already in it nothing.
        /// `Apply`'s capacity guard had no witness at all: no fixture in this
        /// file ever filled the table, so the classic off-by-one there — `>`
        /// where `>=` is meant — would have written one slot PAST four arrays
        /// on the first blow the table could not hold, which is an exception
        /// out of a guard whose whole purpose is not to throw one.
        ///
        /// THE CAPACITY COMES FROM THE CONFIG, NEVER STATED AS A NUMBER. The
        /// constructor sizes the arrays by `Arena.MaxMobs`, so a fixture
        /// carrying its own literal would keep passing a table that had
        /// silently been sized by something else. It is also why the fill loop
        /// mints DIFFERENT ids: same-id blows sum into one slot, and a table
        /// filled with one id is not a full table.
        ///
        /// AND AN ID THE TABLE ALREADY HOLDS IS STILL SERVED WHEN IT IS FULL.
        /// That is not a second rule but the ORDER of the method's body: the
        /// scan over occupied slots runs BEFORE the capacity check, because
        /// summing into a slot that already exists needs no new one. A body
        /// under sustained fire therefore keeps rocking harder even in a match
        /// crowded enough to refuse the next NEW body.
        [Test]
        public void AFullTable_RefusesANewBody_ButStillSumsIntoOneItAlreadyHolds()
        {
            const int firstId = 1;
            const int watchedId = firstId;
            // Three moments no two of which can be mistaken for each other: the
            // watched slot's, its neighbors' and the one the refusal carries.
            const float watchedImpulse = 3f;
            const float fillerImpulse = 1f;
            const float refusedImpulse = 9f;

            SimConfig cfg = TestConfigs.OpenField();
            int capacity = cfg.Arena.MaxMobs;
            Assert.Greater(capacity, 1,
                "премисса: ёмкость таблицы — Arena.MaxMobs, и слотов обязано быть больше одного, "
                + "иначе «полная таблица» и «первый удар» — одно событие");

            var integrator = new MobTiltIntegrator(in cfg);
            for (int id = firstId; id < firstId + capacity; id++)
                integrator.Apply(id, MobType.Chaser,
                    id == watchedId ? watchedImpulse : fillerImpulse);

            Assert.AreEqual(capacity, integrator.Count,
                "премисса: таблица заполнена ровно до ёмкости — каждый из MaxMobs РАЗНЫХ id занял "
                + "свой слот, иначе отказ ниже спрашивали бы у неполной таблицы");

            int strangerId = firstId + capacity;
            Assert.IsFalse(integrator.Apply(strangerId, MobType.Chaser, refusedImpulse),
                "полная таблица обязана отказать ЗНАЧЕНИЕМ — не броском и не записью за край "
                + "массива: цена отказа тут одно некачнувшееся тело");
            Assert.AreEqual(capacity, integrator.Count,
                "и отказ ничего не занял: счётчик остался на ёмкости");
            Assert.IsFalse(integrator.TryGetTilt(strangerId, out _, out _),
                "отказанное тело не наклонено — иначе отказ был бы враньём");
            Assert.IsTrue(integrator.TryGetTilt(watchedId, out _, out float watchedVel));
            Assert.AreEqual(watchedImpulse, watchedVel, 0f,
                "тела, уже стоявшие в таблице, отказом не тронуты");

            Assert.IsTrue(integrator.Apply(watchedId, MobType.Chaser, watchedImpulse),
                "удар по УЖЕ занятому слоту принимается и при полной таблице: поиск занятого слота "
                + "идёт до проверки ёмкости, потому что суммирование слота не требует");
            Assert.AreEqual(capacity, integrator.Count,
                "и он не занял второго слота под тем же id");
            Assert.IsTrue(integrator.TryGetTilt(watchedId, out _, out float summedVel));
            Assert.AreEqual(watchedImpulse * 2f, summedVel, 1e-6f,
                "второй удар не сложился с первым: тело под огнём обязано качаться сильнее");
        }

        /// Test 19, from the same finding. `WriteInto`'s TWO REFUSALS — a null
        /// destination and a `count` wider than the array it names — neither of
        /// which had a witness either. The shape is the neighbor's
        /// (`TracerProjectilesTests.WriteIntoNeverOverrunsTheDestination`),
        /// which pins the same property for the table next door.
        ///
        /// WHY A BODY THE FRAME DOES NOT LIST IS PART OF THE FIXTURE. The
        /// clamp can only be reached when the inner scan runs to the END, and
        /// that scan breaks the moment it finds the id it is looking for — so a
        /// table holding nothing but bodies the array lists never walks past
        /// `mobs.Length`, and an unclamped `count` would go unnoticed there.
        /// The unseen body is not a contrivance: it is the ordinary case the
        /// class doc names, a mob that left this client's view while it was
        /// still rocking and keeps its slot until the spring snaps.
        ///
        /// BOTH ARE REFUSALS RATHER THAN THROWS FOR THE PATH'S OWN REASON: this
        /// is the tail of the render frame, and a frame that names no mobs at
        /// all is an ordinary frame, not a defect worth an exception.
        [Test]
        public void WriteInto_TakesANullArrayAndAnOverstatedCount_WithoutThrowing()
        {
            const int listedId = 7;
            const int unseenId = 11;

            SimConfig cfg = TestConfigs.OpenField();
            var integrator = new MobTiltIntegrator(in cfg);
            float impulse = MobTiltIntegrator.AngularImpulseFor(ShooterSlot, in cfg.Chaser,
                HitHeight, in cfg);

            Assert.IsTrue(integrator.Apply(listedId, MobType.Chaser, impulse), "удар не принят");
            Assert.IsTrue(integrator.Apply(unseenId, MobType.Chaser, impulse),
                "второй удар по ДРУГОМУ телу не принят — искать за краем массива будет нечего");
            integrator.StepTicks(1, in cfg);
            Assert.AreEqual(2, integrator.Count,
                "премисса: качаются оба тела, и одного из них в массиве ниже НЕТ — только на нём "
                + "внутренний обход доходит до конца");

            Assert.DoesNotThrow(() => integrator.WriteInto(null, 0),
                "кадр может не принести массива вовсе: WriteInto(null) — отказ значением, а не "
                + "бросок в хвосте рендер-кадра");

            var mobs = new[] { new MobState { Id = listedId, Type = MobType.Chaser } };
            Assert.DoesNotThrow(() => integrator.WriteInto(mobs, mobs.Length + 5),
                "count шире массива обязан клэмпиться его длиной: обход за краем — исключение "
                + "там, где ценой ошибки должно быть одно некачнувшееся тело");
            Assert.AreNotEqual(0f, mobs[0].Tilt,
                "и тела ВНУТРИ массива всё-таки пропатчены: клэмп сужает обход, а не отменяет его");
        }
    }
}
