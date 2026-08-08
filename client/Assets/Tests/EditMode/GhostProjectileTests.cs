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
    /// own predicted-shot tracers — five plan tests plus five coordinator
    /// tests (task-35-brief §2.3), covering the whole GhostProjectiles
    /// contract without touching Simulation.Combat.WeaponSystem's private
    /// Advance/SpawnShot or any network runtime.
    ///
    /// FIXTURES ARE HAND-BUILT, NOT TestConfigs.Default() (brief §2.3):
    /// capacity/ghostConfirmTicks are small literals chosen per test to keep
    /// FIFO order and expiry boundaries easy to read; only the weapon config
    /// (`CanFireWhileDash`/`CanFireWhileSlide`) is borrowed from
    /// TestConfigs.Default().Weapon so the shared CanFire predicate has a
    /// realistic balance sheet to read Task 35 doesn't own.
    public class GhostProjectileTests
    {
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
            var ghosts = new GhostProjectiles(capacity: 4, ghostConfirmTicks: 5, stats);
            var weapon = Weapon();
            var p = Alive();
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
            var ghosts = new GhostProjectiles(4, 5, stats);
            var weapon = Weapon();
            var p = Alive();
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
            var ghosts = new GhostProjectiles(4, confirmTicks, stats);
            var weapon = Weapon();
            var p = Alive();
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
            var ghosts = new GhostProjectiles(8, 5, stats);
            var weapon = Weapon();
            var p = Alive();
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
            var ghosts = new GhostProjectiles(4, 5, stats);
            var weapon = Weapon();
            var p = Alive();
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
        public void Ghost_SpawnGateIsTheSharedCanFirePredicate()
        {
            // Coordinator test 6 (brief §2.3 #6) — first behavioral witness
            // of decision 0a: the spawn gate is EXACTLY WeaponSystem.CanFire,
            // now public for this reason. task-35-brief's own prose names
            // "FireCooldown > 0" as the hot/cooled discriminator, but
            // WeaponSystem.CanFire structurally never reads FireCooldown
            // (confirmed against the source — FireCooldown only gates the
            // SEPARATE fire-loop inside the private Advance, not CanFire
            // itself; SimulationRunner.WouldFireThisFrame's own doc
            // corroborates this, ANDing `FireCooldown <= 0f` on top of a
            // gate that otherwise mirrors CanFire). DashTimer is used here
            // instead — a field CanFire actually reads — and the test cross
            // -checks TrySpawnFromPrediction's answer against a live call to
            // WeaponSystem.CanFire itself, which pins "gate == CanFire"
            // regardless of which field varies. Reported as a documented
            // clarification of the brief's prose, not a scope change (report
            // §9).
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(4, 5, stats);
            var weapon = Weapon();
            weapon.CanFireWhileDash = false;
            var input = Firing();

            var dashing = Alive(dashTimer: 0.2f);
            Assert.IsFalse(WeaponSystem.CanFire(in dashing, in input, in weapon),
                "fixture premise: this state must actually be a CanFire refusal.");
            Assert.IsFalse(ghosts.TrySpawnFromPrediction(in dashing, in input, in weapon,
                predictedTick: 1, out int noGhostId));
            Assert.AreEqual(0, noGhostId);

            var settled = Alive(dashTimer: 0f);
            Assert.IsTrue(WeaponSystem.CanFire(in settled, in input, in weapon),
                "fixture premise: this state must actually be a CanFire acceptance.");
            Assert.IsTrue(ghosts.TrySpawnFromPrediction(in settled, in input, in weapon,
                predictedTick: 2, out int ghostId));
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
            var ghosts = new GhostProjectiles(4, 5, stats);
            var weapon = Weapon();
            var p = Alive();
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
            var ghosts = new GhostProjectiles(4, confirmTicks, stats);
            var weapon = Weapon();
            var p = Alive();
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
            var ghosts = new GhostProjectiles(4, 5, stats);
            var weapon = Weapon();
            var p = Alive();
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
            // Coordinator test 10 (GC). Exercises all four hot-path members —
            // TrySpawnFromPrediction, Confirm, Advance, TryTranslateEnd — in
            // one loop, warmed up outside the measured window (same
            // discipline as AllocationTests.cs).
            var stats = new NetStats();
            var ghosts = new GhostProjectiles(16, 4, stats);
            var weapon = Weapon();
            var p = Alive();
            var input = Firing();

            uint tick = 0;
            for (int i = 0; i < 32; i++)
            {
                tick++;
                if (ghosts.TrySpawnFromPrediction(in p, in input, in weapon, tick, out _))
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
                    if (ghosts.TrySpawnFromPrediction(in p, in input, in weapon, tick, out _))
                    {
                        ghosts.Confirm(serverId: i, tick);
                        ghosts.TryTranslateEnd(i, out _);
                    }
                    ghosts.Advance(tick);
                }
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
