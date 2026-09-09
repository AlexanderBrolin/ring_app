using NUnit.Framework;
using Ring.Networking;
using Ring.Networking.Client;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below
// are required by the file, not just convenience imports (AllocationTests.cs
// carries the same pair for the same reason).
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 35 (spec §3.9 Р40/Р67, task-35-brief §2.3): the client's
    /// own predicted-shot tracers. Ten plan/coordinator tests from the
    /// original round, plus seven fix-round 1 tests (findings I-1..I-5,
    /// M-3/M-4) — none touching Simulation.Combat.WeaponSystem's private
    /// Advance/SpawnShot or any network runtime.
    ///
    /// FIXTURES ARE HAND-BUILT, NOT TestConfigs.Default() (brief §2.3):
    /// capacity/ghostConfirmTicks/maxTrackTicks are small literals chosen
    /// per test to keep FIFO order and expiry boundaries easy to read; only
    /// the weapon config (`CanFireWhileDash`/`CanFireWhileSlide`/
    /// `FireInterval`) is borrowed from TestConfigs.Default().Weapon so the
    /// shared gate has a realistic balance sheet to read Task 35 doesn't own.
    ///
    /// `maxTrackTicks` DEFAULTS TO 20 IN TESTS THAT DON'T EXERCISE IT
    /// (fix-round 1, finding I-3) — comfortably above every `ghostConfirmTicks`/
    /// tick-range this file uses elsewhere, so the new confirmed-record
    /// ceiling never interferes with a test written before it existed.
    public class GhostProjectileTests
    {
        const int RoomyMaxTrackTicks = 20;

        static WeaponSimConfig Weapon() => TestConfigs.Default().Weapon;

        static PlayerState Alive(float dashTimer = 0f, float slideTimer = 0f) =>
            new PlayerState { Alive = true, DashTimer = dashTimer, SlideTimer = slideTimer };

        static SimInput Firing() => new SimInput { FireHeld = true, AimPoint = new float2(5f, 0f) };

        [Test]
        public void Ghost_KeepsStableIdAfterConfirmation()
        {
            // Plan test 1 (task-35-brief §2.3 #1, mutation table: id remapped
            // to the server's on Confirm). The id the caller was handed at
            // spawn is the ONLY id ever used to route the end event — never
            // retired-and-rented under the server's id, which would teleport
            // the client's own tracer back 5-7 m and cut its trail (finding
            // C-2, plan :1664-1666).
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 100, out int ghostId));
            Assert.Less(ghostId, 0, "a ghost id handed to the caller must be negative by construction.");

            ghosts.Confirm(serverId: 42, tick: 101);

            Assert.IsTrue(ghosts.TryTranslateEnd(serverId: 42, out int translatedId));
            Assert.AreEqual(ghostId, translatedId,
                "the id handed to the caller at spawn must be the SAME id used to route the end "
                + "event — Confirm must never remap it to the server's id (Р67).");
        }

        [Test]
        public void Ghost_TrajectoryUnchangedOnConfirm()
        {
            // Plan test 2. "Trajectory" for Task 35's own scope is the opaque
            // birth tick (brief §2.2: opaque storage, at minimum the birth
            // tick — no flight math lives here, Ф9's job). Confirm must
            // leave it exactly as spawned; the internal accessor exists
            // solely so this premise is observable from a test (task-35-brief
            // §2.2 leaves the exact form of "opaque storage" to the implementer).
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 200, out int ghostId));
            Assert.IsTrue(ghosts.TryGetBirthTick(ghostId, out uint birthBefore));
            Assert.AreEqual(200u, birthBefore);

            ghosts.Confirm(serverId: 7, tick: 205);

            Assert.IsTrue(ghosts.TryGetBirthTick(ghostId, out uint birthAfter),
                "the opaque birth-tick parameter must still be readable after Confirm.");
            Assert.AreEqual(birthBefore, birthAfter,
                "Confirm must not rewrite the ghost's opaque birth parameters — confirmation "
                + "never touches trajectory (Р67).");
        }

        [Test]
        public void Ghost_ExpiresWithoutConfirmation()
        {
            // Plan test 3. Boundary named explicitly (brief §2.3 #3): EXACTLY
            // at GhostConfirmTicks the ghost is still alive; one tick past it,
            // it gasps and NetStats.UnconfirmedGhosts counts it once. Witness:
            // a ghost confirmed before the threshold neither expires nor
            // grows the counter.
            var stats = new NetStats();
            const int confirmTicks = 5;
            var ghosts = new GhostProjectiles(4, confirmTicks, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            // Witness spawned and confirmed FIRST (FIFO matches oldest
            // unconfirmed, so it must be born and confirmed before ghost A
            // exists, or Confirm below would match A instead — matching is
            // positional, not by identity).
            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 1000, out int ghostB));
            ghosts.Confirm(serverId: 99, tick: 1000);
            Assert.AreEqual(0, stats.UnconfirmedGhosts);

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 1000, out int ghostA));

            // AT the threshold (age == confirmTicks): not expired yet.
            var atThreshold = ghosts.Advance(predictedTick: 1000 + confirmTicks);
            Assert.AreEqual(0, atThreshold.Length,
                "a ghost exactly AT GhostConfirmTicks old must not expire yet.");
            Assert.AreEqual(0, stats.UnconfirmedGhosts);

            // One tick PAST the threshold: ghost A gasps, counted once.
            var pastThreshold = ghosts.Advance(predictedTick: 1000 + confirmTicks + 1);
            Assert.AreEqual(1, pastThreshold.Length);
            Assert.AreEqual(ghostA, pastThreshold[0]);
            Assert.AreEqual(1, stats.UnconfirmedGhosts);

            // The confirmed witness never appears in an expired list and
            // still translates normally.
            Assert.IsTrue(ghosts.TryTranslateEnd(99, out int translatedB));
            Assert.AreEqual(ghostB, translatedB);
        }

        [Test]
        public void Ghost_IdSpaceDoesNotCollide()
        {
            // Plan test 4. Every id handed out is negative; server ids live
            // on the wire as u16 (0..65535) — exercised at BOTH boundaries
            // inclusive — so the two spaces can never collide by
            // construction (plan :1664-1666).
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(8, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            var ids = new int[5];
            for (int i = 0; i < ids.Length; i++)
            {
                Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                    predictedTick: (uint)i, out ids[i]));
                Assert.Less(ids[i], 0,
                    "every ghost id handed to the caller must be negative.");
            }

            ghosts.Confirm(serverId: 0, tick: 10);
            ghosts.Confirm(serverId: 65535, tick: 11);

            Assert.IsTrue(ghosts.TryTranslateEnd(0, out int idAtLowBoundary));
            Assert.Less(idAtLowBoundary, 0);
            Assert.IsTrue(ghosts.TryTranslateEnd(65535, out int idAtHighBoundary));
            Assert.Less(idAtHighBoundary, 0);
        }

        [Test]
        public void Ghost_EndEventTranslatedToGhostId()
        {
            // Plan test 5. A confirmed serverId's end translates to the
            // ghost's own id and consumes the record; an unknown serverId
            // refuses without throwing (Р82).
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 50, out int ghostId));
            ghosts.Confirm(serverId: 17, tick: 51);

            Assert.IsTrue(ghosts.TryTranslateEnd(17, out int translated));
            Assert.AreEqual(ghostId, translated);

            Assert.IsFalse(ghosts.TryTranslateEnd(9999, out int unknown),
                "an unknown serverId must refuse, never throw.");
            Assert.AreEqual(0, unknown);

            Assert.IsFalse(ghosts.TryTranslateEnd(17, out _),
                "TryTranslateEnd must consume the record it just translated — a second call "
                + "for the same serverId finds nothing.");
        }

        [Test]
        public void Ghost_SpawnGateIsWouldFireThisTick()
        {
            // Coordinator test 6 (brief §2.3 #6), STRENGTHENED fix-round 1
            // (finding I-1/decision "Б"), RENAMED fix-round 2 (W13) to name
            // the gate the decision actually landed on: the spawn gate is
            // `WeaponSystem.WouldFireThisTick`, not bare `CanFire` — `CanFire`
            // alone never reads `FireCooldown` and would fire every tick the
            // trigger stays held (measured 3.6x over-spawn, see the class
            // doc). Two branches:
            //
            //   * DASH (CanFire's own contribution, unchanged premise from
            //     the original round): mid-dash with CanFireWhileDash false
            //     refuses regardless of cooldown.
            //   * COOLDOWN (fix-round 1: the brief's ORIGINAL fixture,
            //     "FireCooldown > 0 -> no ghost", was invalid against bare
            //     CanFire — which never reads FireCooldown — and is valid
            //     again now that the gate does). Hot (> TickDt) refuses;
            //     cooled (<= TickDt, the loop's own inclusive boundary)
            //     admits.
            //
            // Both branches cross-check the LIVE `WeaponSystem` methods
            // directly, not just a hand-derived expectation — the strongest
            // form of "first consumer of the public predicate" witness.
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            weapon.CanFireWhileDash = false;
            var input = Firing();

            // Dash branch.
            var dashing = Alive(dashTimer: 0.2f);
            dashing.FireCooldown = 0f;
            Assert.IsFalse(WeaponSystem.WouldFireThisTick(in dashing, in input, in weapon),
                "fixture premise: this state must actually be a WouldFireThisTick refusal.");
            Assert.IsFalse(ghosts.TrySpawnFromPrediction(in dashing, in input, in weapon,
                predictedTick: 1, out int noGhostFromDash));
            Assert.AreEqual(0, noGhostFromDash);

            // Cooldown branch, hot: dash/slide settled (CanFire open), but
            // FireCooldown sits STRICTLY ABOVE TickDt — Advance's own loop
            // would not fire this tick.
            var hot = Alive();
            hot.FireCooldown = SimulationWorld.TickDt + 0.02f;
            Assert.IsTrue(WeaponSystem.CanFire(in hot, in input, in weapon),
                "fixture premise: dash/slide settled — only the cooldown term should refuse.");
            Assert.IsFalse(WeaponSystem.WouldFireThisTick(in hot, in input, in weapon),
                "fixture premise: this state must actually be a WouldFireThisTick refusal.");
            Assert.IsFalse(ghosts.TrySpawnFromPrediction(in hot, in input, in weapon,
                predictedTick: 2, out int noGhostFromCooldown));
            Assert.AreEqual(0, noGhostFromCooldown);

            // Cooldown branch, cooled — AT the boundary (FireCooldown ==
            // TickDt exactly, the inclusive edge `Advance`'s own `<= 0f`
            // test lands on after `-= TickDt`): the ghost exists.
            var cooled = Alive();
            cooled.FireCooldown = SimulationWorld.TickDt;
            Assert.IsTrue(WeaponSystem.WouldFireThisTick(in cooled, in input, in weapon),
                "fixture premise: this state must actually be a WouldFireThisTick acceptance.");
            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in cooled, in input, in weapon,
                predictedTick: 3, out int ghostId));
            Assert.Less(ghostId, 0);
        }

        [Test]
        public void Confirm_FifoMatchesOldestUnconfirmed()
        {
            // Coordinator test 7. Two ghosts, two Confirm calls with
            // different serverIds — matching must land in BIRTH order, not
            // arrival order of the Confirm calls (both arrive in the "right"
            // order here; the mutation this pins is FIFO vs LIFO storage).
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 10, out int first));
            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 11, out int second));

            ghosts.Confirm(serverId: 501, tick: 12);
            ghosts.Confirm(serverId: 502, tick: 13);

            Assert.IsTrue(ghosts.TryTranslateEnd(501, out int matchedFirst));
            Assert.AreEqual(first, matchedFirst,
                "the FIRST Confirm call must match the OLDEST (first-born) unconfirmed ghost.");
            Assert.IsTrue(ghosts.TryTranslateEnd(502, out int matchedSecond));
            Assert.AreEqual(second, matchedSecond);
        }

        [Test]
        public void Confirm_AfterExpiryIsSilentNoOp()
        {
            // Coordinator test 8. A Confirm call that arrives after its
            // ghost already gasped from lack of confirmation is a silent
            // no-op (Р82) — it must not resurrect the record. Witness: a
            // genuinely live ghost spawned afterwards still confirms
            // normally, proving the no-op didn't corrupt bookkeeping.
            var stats = new NetStats();
            const int confirmTicks = 3;
            var ghosts = new GhostProjectiles(4, confirmTicks, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 100, out int ghostA));
            var expired = ghosts.Advance(predictedTick: 100 + confirmTicks + 1);
            Assert.AreEqual(1, expired.Length);
            Assert.AreEqual(ghostA, expired[0]);

            Assert.DoesNotThrow(() => ghosts.Confirm(serverId: 55, tick: 999));
            Assert.IsFalse(ghosts.TryTranslateEnd(55, out _),
                "a late Confirm for an already-expired ghost must not resurrect a record.");

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 200, out int ghostB));
            ghosts.Confirm(serverId: 56, tick: 201);
            Assert.IsTrue(ghosts.TryTranslateEnd(56, out int translatedB));
            Assert.AreEqual(ghostB, translatedB);
        }

        [Test]
        public void Reset_ForgetsEverything()
        {
            // Coordinator test 9. Reset forgets every record AND restarts the
            // id counter from its own first value — a stale serverId no
            // longer translates, and the id space starts over rather than
            // continuing where the previous match left off.
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 10, out int firstEverId));
            ghosts.Confirm(serverId: 77, tick: 11);
            Assert.AreEqual(-1, firstEverId,
                "fixture premise: the very first ghost of a fresh instance is id -1.");

            ghosts.Reset();

            Assert.IsFalse(ghosts.TryTranslateEnd(77, out _),
                "a serverId confirmed before Reset must not translate after it.");

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 20, out int afterResetId));
            Assert.AreEqual(-1, afterResetId,
                "the id counter must restart at -1 after Reset, not continue at -2.");
        }

        [Test]
        public void GhostProjectiles_HotPathDoesNotAllocateGC()
        {
            // Coordinator test 10 (GC), EXPANDED fix-round 1 (finding M-3).
            // Small capacity/ghostConfirmTicks so the measured loop actually
            // exercises all three branches the original version's "spawn,
            // confirm, translate every iteration" shape never reached:
            //   * odd iterations spawn and leave the ghost UNCONFIRMED, so
            //     `Advance`'s real expiry branch runs (FreeSlot + the
            //     NetStats increment) a few iterations later instead of
            //     always finding an empty queue;
            //   * even iterations still spawn-confirm-translate in the same
            //     tick, exercising the FIFO/duplicate-scan/translate paths.
            //
            // Fix-round 2, W14 (both phase reviewers traced this): an
            // earlier draft of this comment ALSO claimed the loop exercises
            // the "no free slot" capacity-refusal branch. That is false —
            // in this loop's steady state a free slot is ALWAYS available
            // (a confirmed ghost frees its slot the SAME iteration it is
            // confirmed, and an unconfirmed one expires within
            // `ghostConfirmTicks`), so that branch is never actually taken
            // here. This loop covers spawn/confirm/translate/expire only;
            // the capacity-refusal branch is pinned separately by
            // `TrySpawnFromPrediction_CapacityExhaustedRefusesSilently`.
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 2, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            uint tick = 0;
            for (int i = 0; i < 32; i++)
            {
                tick++;
                bool spawned = ghosts.TrySpawnFromPrediction(in p, in input, in weapon, tick, out _);
                if (spawned && i % 2 == 0)
                {
                    ghosts.Confirm(serverId: i, tick);
                    ghosts.TryTranslateEnd(i, out _);
                }
                ghosts.Advance(tick);
            }

            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    tick++;
                    bool spawned = ghosts.TrySpawnFromPrediction(in p, in input, in weapon, tick, out _);
                    if (spawned && i % 2 == 0)
                    {
                        ghosts.Confirm(serverId: i, tick);
                        ghosts.TryTranslateEnd(i, out _);
                    }
                    ghosts.Advance(tick);
                }
            }, Is.Not.AllocatingGCMemory());

            // Fixture-liveness check AFTER the measured window (Урок 87):
            // prove the loop genuinely exercised expiry and capacity refusal,
            // not merely the confirm/translate churn path.
            Assert.Greater(stats.UnconfirmedGhosts, 0,
                "fixture premise: the measured loop must have actually expired unconfirmed "
                + "ghosts through Advance's real branch, not just an empty-queue no-op.");

            // app-8dv T5: THE UNGATED ENTRANCE GETS ITS OWN MEASURED WINDOW
            // (spec §3.4, finding M1₃). The loop above pins the GATED member,
            // and the entrance the journal actually drives is a different
            // method -- an allocation added to it would pass unnoticed here.
            // A second registry rather than a second arm of the loop above:
            // that one's arithmetic (`i % 2`, capacity 4, confirmTicks 2) is
            // balanced so its expiry branch runs, and adding a third spawn per
            // iteration would exhaust the table instead.
            var ownStats = new NetStats();
            var ownGhosts = new GhostProjectiles(4, 2, RoomyMaxTrackTicks, ownStats);
            uint ownTick = 0;
            for (int i = 0; i < 32; i++)
            {
                ownTick++;
                bool spawned = ownGhosts.TrySpawnPredictedShot(ownTick, out _);
                if (spawned && i % 2 == 0)
                {
                    ownGhosts.TryConfirm(serverId: i, tick: ownTick, out _);
                    ownGhosts.TryTranslateEnd(i, out _);
                }
                ownGhosts.Advance(ownTick);
            }

            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    ownTick++;
                    bool spawned = ownGhosts.TrySpawnPredictedShot(ownTick, out _);
                    if (spawned && i % 2 == 0)
                    {
                        ownGhosts.TryConfirm(serverId: i, tick: ownTick, out _);
                        ownGhosts.TryTranslateEnd(i, out _);
                    }
                    ownGhosts.Advance(ownTick);
                }
            }, Is.Not.AllocatingGCMemory());

            Assert.Greater(ownStats.UnconfirmedGhosts, 0,
                "fixture premise: the ungated loop must have expired unconfirmed ghosts too, "
                + "not merely spawned and translated them in the same tick.");
        }

        [Test]
        public void Advance_BackwardTickDoesNotExpireAnyGhost()
        {
            // Fix-round 1, finding I-2. FishNet replays the [Replicate]
            // queue after every state packet (~30x/s the predicted tick
            // runs BACKWARD relative to the previous Advance call, well
            // within a live match) — not a hypothetical. An UNSIGNED age
            // computation would read a backward tick as ~4.29e9 ticks old
            // and mass-expire every unconfirmed ghost on the very first
            // replayed tick. A negative SIGNED age — "a ghost from the
            // future" — must not expire anything.
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 100, out int ghostId));

            // Replay: the tick runs BACKWARD relative to the ghost's own
            // birth tick.
            var backward = ghosts.Advance(predictedTick: 50);
            Assert.AreEqual(0, backward.Length,
                "a predicted tick running backward (FishNet replay) must not expire any ghost.");
            Assert.AreEqual(0, stats.UnconfirmedGhosts);

            // Witness: normal forward aging still works after a backward call.
            var forward = ghosts.Advance(predictedTick: 100 + 5 + 1);
            Assert.AreEqual(1, forward.Length);
            Assert.AreEqual(ghostId, forward[0]);
            Assert.AreEqual(1, stats.UnconfirmedGhosts);
        }

        [Test]
        public void ConfirmedGhost_WithoutEndEvent_FreesAtMaxTrackTicks()
        {
            // Fix-round 1, finding I-3. A confirmed record with no end event
            // (lost ProjectileEndedNet, Р58) must not occupy its slot
            // forever — that silently exhausts capacity by the end of a
            // long match. The ceiling frees it WITHOUT counting it as
            // unconfirmed (it WAS confirmed — registry hygiene, not a
            // broken prediction) and WITHOUT appearing in the expired view.
            // Capacity 1 makes the reclaim proof airtight: the final spawn
            // below can only succeed if the ceiling genuinely freed the slot.
            var stats = new NetStats();
            const int confirmTicks = 3;
            const int maxTrackTicks = 10;
            var ghosts = new GhostProjectiles(1, confirmTicks, maxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 100, out int ghostId));
            ghosts.Confirm(serverId: 5, tick: 101);

            // Witness: alive well before the ceiling, translation still works.
            var beforeCeiling = ghosts.Advance(predictedTick: 100 + maxTrackTicks);
            Assert.AreEqual(0, beforeCeiling.Length);
            Assert.AreEqual(0, stats.UnconfirmedGhosts);

            // Past the ceiling: silently freed.
            var pastCeiling = ghosts.Advance(predictedTick: 100 + maxTrackTicks + 1);
            Assert.AreEqual(0, pastCeiling.Length,
                "a confirmed ghost past maxTrackTicks must not appear in the expired view.");
            Assert.AreEqual(0, stats.UnconfirmedGhosts,
                "a confirmed ghost's registry-hygiene cleanup must never count as UnconfirmedGhosts.");

            Assert.IsFalse(ghosts.TryTranslateEnd(5, out _),
                "the record must genuinely be gone — a late end event finds nothing.");

            // Capacity is 1 — this can only succeed if the old slot was
            // truly reclaimed by the ceiling.
            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 200, out int newGhostId));
            Assert.AreNotEqual(ghostId, newGhostId);
        }

        [Test]
        public void Confirm_DuplicateServerIdIsSilentNoOp()
        {
            // Fix-round 1, finding I-5. The duplicate guard (an occupied
            // slot already carrying this serverId short-circuits Confirm)
            // was reachable but never pinned by a dedicated test — removing
            // it left all ten original tests green. Two live ghosts,
            // Confirm(7) called TWICE: the second call must be a silent
            // no-op, not consume the SECOND unconfirmed ghost.
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 10, out int first));
            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 11, out int second));

            ghosts.Confirm(serverId: 7, tick: 12);
            ghosts.Confirm(serverId: 7, tick: 13); // duplicate — must not touch `second`

            Assert.IsTrue(ghosts.TryTranslateEnd(7, out int translatedFirst));
            Assert.AreEqual(first, translatedFirst);

            // `second` must STILL be unconfirmed — the duplicate call must
            // not have matched it. Confirming it now with a genuinely new
            // serverId succeeds.
            ghosts.Confirm(serverId: 8, tick: 14);
            Assert.IsTrue(ghosts.TryTranslateEnd(8, out int translatedSecond));
            Assert.AreEqual(second, translatedSecond);
        }

        [Test]
        public void Confirm_NegativeServerIdIsSilentNoOp()
        {
            // Fix-round 1, finding M-4 — pins the `serverId < 0` guard (Р82:
            // a negative value can never be a legal wire serverId), reachable
            // but unpinned before. Deliberately uses -5, NOT -1: -1 is the
            // internal `NoServerId` sentinel every unconfirmed slot already
            // carries, and querying exactly -1 would be silently absorbed by
            // the (unrelated, still-intact) duplicate-serverId scan ahead of
            // this guard instead of exercising it — -5 cannot collide with
            // anything and isolates the guard under test. Witness: a
            // genuinely valid serverId still confirms the SAME (untouched)
            // ghost afterward — the discriminating check, since a mutant
            // that dropped the guard would have already misspent the ghost
            // on -5, leaving nothing for 9 to match.
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 10, out int ghostId));

            Assert.DoesNotThrow(() => ghosts.Confirm(serverId: -5, tick: 11));
            Assert.IsFalse(ghosts.TryTranslateEnd(-5, out _));

            ghosts.Confirm(serverId: 9, tick: 12);
            Assert.IsTrue(ghosts.TryTranslateEnd(9, out int translated));
            Assert.AreEqual(ghostId, translated);
        }

        [Test]
        public void TryTranslateEnd_NegativeServerIdRefusesWithoutThrow()
        {
            // Fix-round 1, finding M-4 — pins TryTranslateEnd's own
            // `serverId < 0` guard. Deliberately queries -1, the SAME value
            // as the internal `NoServerId` sentinel an UNCONFIRMED slot
            // carries (the ghost below is spawned but never confirmed) —
            // this is the exact collision the guard exists to prevent (class
            // doc): without it, querying -1 would accidentally MATCH the
            // unconfirmed ghost's sentinel and wrongly "translate" it away.
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 10, out int unconfirmedId));

            Assert.IsFalse(ghosts.TryTranslateEnd(-1, out int noId),
                "a negative serverId must refuse, and must not accidentally match the "
                + "internal NoServerId sentinel an unconfirmed slot carries.");
            Assert.AreEqual(0, noId);

            // Witness: the still-unconfirmed ghost was untouched by the
            // refused query above — it confirms and translates normally.
            ghosts.Confirm(serverId: 3, tick: 11);
            Assert.IsTrue(ghosts.TryTranslateEnd(3, out int translated));
            Assert.AreEqual(unconfirmedId, translated);
        }

        [Test]
        public void TrySpawnFromPrediction_CapacityExhaustedRefusesSilently()
        {
            // Fix-round 1, finding M-4 — the "no free slot" branch (Р82),
            // distinct from the gate refusal, pinned with its own witness
            // (capacity freed by a translate, the next spawn succeeds again).
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(1, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 10, out int firstId));

            Assert.IsFalse(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 11, out int refusedId),
                "capacity is exhausted — a second spawn must refuse, not throw or evict.");
            Assert.AreEqual(0, refusedId);

            ghosts.Confirm(serverId: 1, tick: 12);
            Assert.IsTrue(ghosts.TryTranslateEnd(1, out int translated));
            Assert.AreEqual(firstId, translated);

            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 20, out int secondId),
                "the reclaimed slot must be usable again.");
            Assert.AreNotEqual(firstId, secondId);
        }

        [Test]
        public void Constructor_ClampsTuningParameters()
        {
            // Fix-round 1, finding M-4 — the constructor's defensive floors,
            // described by doc, pinned here as measured facts.
            var stats = new NetStats();
            var weapon = Weapon();
            var p = Alive();
            p.FireCooldown = SimulationWorld.TickDt;
            var input = Firing();

            // (a) ghostConfirmTicks clamps to >= 0; at exactly 0 the ghost
            // still gets the grace of its own birth tick ("life of one
            // tick") — it does NOT gasp on the tick it was born, only on
            // the next one.
            var zeroConfirm = new GhostProjectiles(4, ghostConfirmTicks: -3, maxTrackTicks: 20, stats);
            Assert.IsTrue(zeroConfirm.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 100, out int shortLivedId));
            var atBirthTick = zeroConfirm.Advance(predictedTick: 100);
            Assert.AreEqual(0, atBirthTick.Length,
                "a ghost must survive the tick it was born on, even at ghostConfirmTicks 0.");
            var oneTickLater = zeroConfirm.Advance(predictedTick: 101);
            Assert.AreEqual(1, oneTickLater.Length,
                "with ghostConfirmTicks clamped to 0, the ghost gasps on the very next tick.");
            Assert.AreEqual(shortLivedId, oneTickLater[0]);

            // (b) maxTrackTicks clamps UP to at least ghostConfirmTicks — a
            // confirmed ghost must not be killed by the ceiling before an
            // unconfirmed one would even expire.
            var clampedCeiling = new GhostProjectiles(4, ghostConfirmTicks: 5, maxTrackTicks: 1, stats);
            Assert.IsTrue(clampedCeiling.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 200, out int confirmedId));
            clampedCeiling.Confirm(serverId: 77, tick: 201);
            var beforeGhostConfirmTicks = clampedCeiling.Advance(predictedTick: 200 + 5);
            Assert.AreEqual(0, beforeGhostConfirmTicks.Length,
                "maxTrackTicks below ghostConfirmTicks must clamp UP — a confirmed ghost "
                + "survives at least as long as ghostConfirmTicks, not the smaller raw ceiling.");
            Assert.IsTrue(clampedCeiling.TryTranslateEnd(77, out int translated));
            Assert.AreEqual(confirmedId, translated);

            // (c) capacity clamps to >= 1.
            var zeroCapacity = new GhostProjectiles(capacity: 0, ghostConfirmTicks: 5,
                maxTrackTicks: 20, stats);
            Assert.IsTrue(zeroCapacity.TrySpawnFromPrediction(in p, in input, in weapon,
                predictedTick: 300, out int firstEverInZeroCap));
            Assert.Less(firstEverInZeroCap, 0);
        }

        // ---- app-8dv T5: the entrance the journal uses, and the pairing it reads ----

        /// THE GATE IS THE WHOLE DIFFERENCE, and it is why the entrance is a
        /// new member rather than a relaxed old one (spec §3.4, plan Step 3).
        /// `TrySpawnFromPrediction` asks `WeaponSystem.WouldFireThisTick`,
        /// which reads `FireCooldown` -- and the journal writes its record
        /// INSIDE the predicted tick, AFTER `Advance` has already charged that
        /// cooldown. So on the state a drained record actually describes, the
        /// gate is shut and the old entrance answers `false` forever: the
        /// ghost would never be born at all, which is the first of the four
        /// reasons the spec's v2 approach was abandoned.
        ///
        /// ⚠ THE FIXTURE PROVES THE PREMISE RATHER THAN ASSUMING IT: the same
        /// `PlayerState` is offered to both entrances in the same fixture, so
        /// a reader can see that what changed is the gate and not the state.
        [Test]
        public void TrySpawnPredictedShot_NeedsNoGate_BecauseTheShotAlreadyHappened()
        {
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);
            var weapon = Weapon();
            var input = Firing();

            // The state a journal record is written from: the shot has just
            // been fired this tick, so the cooldown is charged a full
            // FireInterval forward.
            var afterTheShot = Alive();
            afterTheShot.FireCooldown = weapon.FireInterval;

            Assert.IsFalse(ghosts.TrySpawnFromPrediction(in afterTheShot, in input, in weapon,
                    predictedTick: 500, out _),
                "fixture premise: on the post-shot state the gated entrance refuses -- which "
                + "is exactly why polling the gate from the frame could never work");

            Assert.IsTrue(ghosts.TrySpawnPredictedShot(predictedTick: 500, out int ghostId),
                "the ungated entrance is told a shot HAPPENED, so it has nothing to decide");
            Assert.AreEqual(-1, ghostId,
                "and it mints from the same counter as the gated one -- FirstGhostId, "
                + "counting down");

            // It is the same registry, not a parallel one: the ghost just
            // born is the one Confirm pairs and Advance ages.
            Assert.IsTrue(ghosts.TryConfirm(serverId: 7, tick: 501, out int pairedId));
            Assert.AreEqual(ghostId, pairedId);
        }

        /// M249's witness, and the shape the whole route hangs on: `TryConfirm`
        /// hands back WHICH ghost it paired, because the caller has to adopt
        /// that ghost's trail by id. `Confirm` keeps its old signature and
        /// becomes a wrapper over this -- `.Confirm(` is called 22 times in
        /// this file with named arguments, and an `out` parameter would break
        /// every one of them, while a compile error is not a RED (332/498/630).
        ///
        /// BOTH REFUSALS ARE HERE, because the caller's fallback depends on
        /// telling them apart from a success: an empty queue (nothing was
        /// predicted, or it already gasped) and a duplicate server id. On
        /// either, the shot must still get an ordinary trail, so the answer
        /// has to be a value rather than a silent no-op.
        [Test]
        public void TryConfirm_HandsBackThePairedGhost_AndRefusesByValue()
        {
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, RoomyMaxTrackTicks, stats);

            Assert.IsFalse(ghosts.TryConfirm(serverId: 11, tick: 600, out int onEmpty),
                "an empty queue confirms nothing -- the shot falls back to the ordinary path");
            Assert.AreEqual(0, onEmpty, "and the id is left at 0 on a refusal");

            Assert.IsTrue(ghosts.TrySpawnPredictedShot(predictedTick: 600, out int oldest));
            Assert.IsTrue(ghosts.TrySpawnPredictedShot(predictedTick: 601, out int younger));

            Assert.IsTrue(ghosts.TryConfirm(serverId: 11, tick: 602, out int paired));
            Assert.AreEqual(oldest, paired,
                "matching is positional FIFO: the OLDEST unconfirmed ghost is the one paired");

            Assert.IsFalse(ghosts.TryConfirm(serverId: 11, tick: 603, out int onDuplicate),
                "a duplicate server id is refused rather than eating the next ghost in line");
            Assert.AreEqual(0, onDuplicate);

            // And the wrapper still routes into the same machinery: the
            // younger ghost is what the plain Confirm pairs next.
            ghosts.Confirm(serverId: 12, tick: 604);
            Assert.IsTrue(ghosts.TryTranslateEnd(12, out int translated));
            Assert.AreEqual(younger, translated,
                "Confirm is a thin wrapper over TryConfirm, not a second mechanism");
        }
    }
}
