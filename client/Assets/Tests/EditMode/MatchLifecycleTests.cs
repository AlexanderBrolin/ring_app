using Unity.Mathematics;
using System;
using FishNet.Broadcast;
using FishNet.Connection;
using NUnit.Framework;
using Ring.Networking;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Networking.Server;
using Ring.Server;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 40 (spec §3.10, §3.11, §6k Р163/Р164; plan Т40): the
    /// match's own life cycle — the epoch it is minted under, the two ways it
    /// can end, and the SEVEN client-side seams a restart has to clear (five in
    /// Task 40; `ClientEventQueue` joined them in Task 44b, by the two-places
    /// rule `ClientMatchReset`'s own doc states).
    ///
    /// FIVE SUBJECTS, ONE FILE, BECAUSE THEY ARE ONE CONTRACT.
    /// `MatchEpochCounter` (`Ring.Server`), `MatchEndPolicy` and
    /// `MatchServer.EndedNetFor` (`Ring.Networking.Server`), the two wire
    /// structs (`Ring.Networking.Protocol`) and `ClientMatchReset`
    /// (`Ring.Networking.Client`) live in three namespaces and two assemblies,
    /// but a match end, a restart and an epoch are one story: the epoch names
    /// the match, the policy decides when it is over, the structs carry both
    /// facts to the client, the copy fills them in, and the reset is what
    /// makes the client able to believe the next one.
    ///
    /// `MatchServer`'s FISHNET WIRING IS NOT UNIT-TESTED HERE, ON PURPOSE —
    /// the same deliberate split `SnapshotAssembler`, `PlayerNetworkController`
    /// and `MatchHandshake` already carry in their own class docs (and the
    /// same one `HandshakeTests` records for Task 39): the end-of-match send,
    /// the restart broadcast and the disconnect-kill pass need a live
    /// `NetworkManager`, which EditMode cannot raise. R-COMPILE and,
    /// eventually, the two-process milestone В1 are that proof instead.
    ///
    /// WHAT IS LIFTED OUT OF THAT WIRING IS TESTED HERE, THOUGH — the split is
    /// about the network, not about the file a method happens to sit in.
    /// Everything those code paths DECIDE lives in `MatchEndPolicy`, and
    /// `MatchServer.EndedNetFor` — a pure summary-to-message copy with no
    /// FishNet in it — is `internal` precisely so a test can reach it
    /// (fix-round 1, I-5; `EffectiveInputBatch` in that same file set the
    /// precedent).
    ///
    /// EVERY RESET TEST OBSERVES ITS OWN SEAM THROUGH THAT SEAM'S OWN PUBLIC
    /// BEHAVIOR, and carries a positive witness that the behavior it asserts
    /// away was really there to begin with. "Nothing threw" and "the thing is
    /// not empty" are not accepted as evidence that a seam ran: a forgotten
    /// `Reset` does not crash, it silently kills one guarantee for the whole
    /// next match, so a test that cannot tell the two apart pins nothing.
    ///
    /// A NEW PER-MATCH SEAM MUST BE ADDED IN TWO PLACES — `ClientMatchReset`
    /// and this file. The completeness of the set is contractual, not
    /// something the type system holds up: nothing in C# can say "these are
    /// all the objects a restart must clear". The mutation wave of each task
    /// (one removed call per seam) is what pins the current seven
    /// mechanically; a seventh inherits the same obligation.
    ///
    /// FIXTURES ARE HAND-BUILT (Р56 — the asset owns the game's numbers, the
    /// fixture owns the test's), with the two existing Simulation-side
    /// fixtures (`TestConfigs.Default`/`TestConfigs.DefaultArena`) reused
    /// wherever a realistic balance sheet is needed rather than restated as
    /// literals.
    public class MatchLifecycleTests
    {
        const ushort OldEpoch = 7;
        const ushort NewEpoch = 8;

        // ------------------------------------------------------------------
        // Fixtures.
        // ------------------------------------------------------------------

        static NetTimings Timings() => new NetTimings
        {
            InterpBufferTicks = 3,
            InterpMaxStaleTicks = 3,
            RenderClockSnapTicks = 10,
            SlewFraction = 0.08f,
        };

        static EventDedup NewDedup()
        {
            var cfg = TestConfigs.Default();
            return new EventDedup(in cfg);
        }

        static SnapshotQueue NewQueue()
        {
            var arena = TestConfigs.DefaultArena();
            var timings = Timings();
            return new SnapshotQueue(in arena, in timings);
        }

        /// Small, readable ceilings — this file never exercises ghost expiry
        /// boundaries themselves (`GhostProjectileTests` owns those), only
        /// whether the registry survived a restart.
        static GhostProjectiles NewGhosts() => new GhostProjectiles(
            capacity: 4, ghostConfirmTicks: 5, maxTrackTicks: 20, new NetStats());

        static StalePolicy NewStalePolicy() =>
            new StalePolicy(capacity: 4, staleTicks: 3, fadeTicks: 4);

        /// The sixth seam (Task 44b). A one-event budget keeps the queue
        /// small and readable — `ClientLinkTests` owns the capacity
        /// arithmetic itself; this file only asks whether the seam was
        /// cleared.
        static ClientEventQueue NewEventQueue()
        {
            var timings = Timings();
            return new ClientEventQueue(in timings, snapshotEventBudget: 1);
        }

        /// One accepted, already-decoded event on `tick`, identified by its
        /// `EntityId`. The queue holds finished `SimEvent`s as of Task 44d —
        /// the wire record it used to hold pointed into a receive buffer that
        /// is gone by delivery time. This file never inspects the payload; it
        /// only watches whether events survive a restart.
        static SimEvent EventAt(uint tick, int id) => new SimEvent
        {
            Kind = SimEventKind.MobDied,
            Tick = (int)tick,
            EntityId = id,
        };

        /// Spawns `count` unconfirmed ghosts born on tick 0. The gate is
        /// `WeaponSystem.WouldFireThisTick`, so the fixture has to satisfy it:
        /// alive, trigger held, and a cooldown that expires this tick.
        static void SpawnGhosts(GhostProjectiles ghosts, int count)
        {
            var weapon = TestConfigs.Default().Weapon;
            var player = new PlayerState { Alive = true };
            var input = new SimInput { FireHeld = true };
            for (int i = 0; i < count; i++)
            {
                Assert.IsTrue(
                    ghosts.TrySpawnFromPrediction(in player, in input, in weapon,
                        predictedTick: 0, out _),
                    "fixture premise: the ghost fixture must actually spawn a ghost");
            }
        }

        // ------------------------------------------------------------------
        // The epoch counter (§6k Р163).
        // ------------------------------------------------------------------

        [Test]
        public void FirstMintIsOne()
        {
            // Р163: zero is reserved for "there is no epoch", so the first
            // live epoch a process ever hands out is 1 — never 0, which is
            // what `default(ushort)` reads as everywhere on the wire.
            var counter = new MatchEpochCounter();
            Assert.AreEqual(1, counter.Mint(),
                "the first minted epoch must be 1 — 0 is reserved for 'no epoch yet'");
        }

        [Test]
        public void MintIncrements()
        {
            var counter = new MatchEpochCounter();
            Assert.AreEqual(1, counter.Mint(), "first mint");
            Assert.AreEqual(2, counter.Mint(), "second mint");
            Assert.AreEqual(3, counter.Mint(), "third mint");
        }

        [Test]
        public void CurrentIsZeroBeforeFirstMint()
        {
            var counter = new MatchEpochCounter();
            Assert.AreEqual(0, counter.Current,
                "before the first mint there is no epoch, and 0 is how that is said");

            // Positive witness: `Current` is not simply a constant — it
            // follows the value the last mint handed out.
            ushort minted = counter.Mint();
            Assert.AreEqual(minted, counter.Current,
                "after a mint, Current must report exactly the epoch just handed out");
        }

        [Test]
        public void MintWrapsFrom65535ToOne()
        {
            var counter = new MatchEpochCounter();
            ushort last = 0;
            for (int i = 0; i < ushort.MaxValue; i++) last = counter.Mint();

            // Positive witness: the walk really did reach the top of the
            // range, so the assertion below is about the wrap and not about
            // some earlier value.
            Assert.AreEqual(ushort.MaxValue, last,
                "witness: minting 65535 times must land on 65535");

            Assert.AreEqual(1, counter.Mint(),
                "the wrap goes 65535 -> 1, never 65535 -> 0: zero stays reserved");
        }

        [Test]
        public void MintNeverReturnsZero()
        {
            // A full revolution plus a few, checked at EVERY step rather than
            // at the two interesting ones — "0 is never minted" is a promise
            // about the whole sequence.
            var counter = new MatchEpochCounter();
            for (int i = 0; i < ushort.MaxValue + 5; i++)
            {
                ushort minted = counter.Mint();
                Assert.AreNotEqual(0, minted,
                    $"mint #{i + 1} handed out 0, which is reserved for 'no epoch'");
            }
        }

        // ------------------------------------------------------------------
        // End conditions (§2.3, spec §3.10/§3.11).
        // ------------------------------------------------------------------

        [Test]
        public void NoEndWhileSomeoneAliveAndTimeLeft()
        {
            // The positive witness the three end tests below are measured
            // against: an ordinary tick of an ordinary match ends nothing.
            var policy = new MatchEndPolicy(maxDurationTicks: 100);
            Assert.AreEqual(MatchEndReason.None, policy.Evaluate(worldTick: 10, alivePlayers: 1),
                "a match with a living player and time left on the clock is not over");
        }

        [Test]
        public void AllDeadEndsMatch()
        {
            var policy = new MatchEndPolicy(maxDurationTicks: 100);
            Assert.AreEqual(MatchEndReason.AllPlayersDead,
                policy.Evaluate(worldTick: 10, alivePlayers: 0),
                "no living players is the end of the match, well before the timer");
        }

        [Test]
        public void MaxDurationEndsMatch()
        {
            var policy = new MatchEndPolicy(maxDurationTicks: 100);

            // The boundary is `>=`, and the tick before it is the witness
            // that the limit is not simply always true. Two survivors rather
            // than one, deliberately: this test is about the CLOCK, and a
            // fixture sitting on the edge of the "everyone is dead" condition
            // would answer for both at once.
            Assert.AreEqual(MatchEndReason.None, policy.Evaluate(worldTick: 99, alivePlayers: 2),
                "witness: one tick short of the limit the match is still running");
            Assert.AreEqual(MatchEndReason.MaxDurationReached,
                policy.Evaluate(worldTick: 100, alivePlayers: 2),
                "AT the limit the match is over — the boundary is >=, not >");
        }

        [Test]
        public void AllDeadWinsOverMaxDuration()
        {
            // Both conditions true on the same tick. The assertion is on the
            // REASON, not on "it ended": the two reasons carry different
            // process exit codes (0 against 4), so which one wins is
            // observable outside the process and must be pinned.
            var policy = new MatchEndPolicy(maxDurationTicks: 100);
            Assert.AreEqual(MatchEndReason.AllPlayersDead,
                policy.Evaluate(worldTick: 100, alivePlayers: 0),
                "a match whose players are all dead ended in substance, not on the timer — "
                + "AllPlayersDead is checked first and its exit code (0) is the one reported");
        }

        [Test]
        public void ExitCodeForMapsEveryReason()
        {
            // Completeness by enumeration, with an assertion on the VALUE of
            // every member: a reason added later without an exit code lands
            // on `Assert.Fail` below instead of quietly inheriting someone
            // else's number.
            var reasons = (MatchEndReason[])Enum.GetValues(typeof(MatchEndReason));
            int covered = 0;

            foreach (MatchEndReason reason in reasons)
            {
                switch (reason)
                {
                    case MatchEndReason.None:
                        // Asking a RUNNING match for its exit code is a bug in
                        // the caller, not a fourth exit code.
                        Assert.Throws<ArgumentOutOfRangeException>(
                            () => MatchEndPolicy.ExitCodeFor(MatchEndReason.None),
                            "None is not an outcome — asking it for an exit code must throw");
                        covered++;
                        break;
                    case MatchEndReason.AllPlayersDead:
                        Assert.AreEqual(0, MatchEndPolicy.ExitCodeFor(reason),
                            "spec §3.11: a match that was played out exits 0");
                        covered++;
                        break;
                    case MatchEndReason.MaxDurationReached:
                        Assert.AreEqual(4, MatchEndPolicy.ExitCodeFor(reason),
                            "spec §3.11: an exhausted MatchMaxDurationSeconds exits 4");
                        covered++;
                        break;
                    default:
                        Assert.Fail($"MatchEndReason.{reason} has no pinned exit code — the "
                            + "table in spec §3.11 must grow together with the enum");
                        break;
                }
            }

            Assert.AreEqual(reasons.Length, covered,
                "every member of MatchEndReason must be answered for by this test");
        }

        [Test]
        public void ShouldKillOnDisconnect_TruthTable()
        {
            // Spec §3.10: "player disconnect -> KillPlayerNoDamage". All four
            // combinations, because the one that matters is a conjunction and
            // a predicate that dropped either half would still answer three
            // of them correctly.
            Assert.IsFalse(MatchEndPolicy.ShouldKillOnDisconnect(connectionActive: true, playerAlive: true),
                "a live connection owning a live player is the ordinary case — nobody dies");
            Assert.IsFalse(MatchEndPolicy.ShouldKillOnDisconnect(connectionActive: true, playerAlive: false),
                "a live connection whose player is already dead has nothing left to kill");
            Assert.IsTrue(MatchEndPolicy.ShouldKillOnDisconnect(connectionActive: false, playerAlive: true),
                "the one case that acts: the connection is gone and the player is still alive");
            Assert.IsFalse(MatchEndPolicy.ShouldKillOnDisconnect(connectionActive: false, playerAlive: false),
                "a dead connection whose player is already dead is idempotent, not a second kill");
        }

        [Test]
        public void MatchEndPolicy_RejectsNonPositiveMaxDuration()
        {
            // A duration limit of zero ticks would end every match on its
            // opening tick; a negative one has no meaning at all. Both are
            // caller bugs — the conversion from seconds is the bootstrap's
            // own arithmetic (Т41), and a bad result must fail at the seam.
            Assert.Throws<ArgumentOutOfRangeException>(() => new MatchEndPolicy(maxDurationTicks: 0),
                "a match cannot be limited to zero ticks");
            Assert.Throws<ArgumentOutOfRangeException>(() => new MatchEndPolicy(maxDurationTicks: -1),
                "a negative tick limit is a caller bug, not a disabled limit");

            // Positive witness: the smallest legal limit constructs.
            Assert.DoesNotThrow(() => new MatchEndPolicy(maxDurationTicks: 1),
                "witness: one tick is a legal, if extreme, limit");
        }

        // ------------------------------------------------------------------
        // The five restart seams (§2.5, §6k Р164).
        // ------------------------------------------------------------------

        [Test]
        public void ResetForEpoch_ResetsDedup()
        {
            var dedup = NewDedup();
            dedup.Reset(OldEpoch);
            Assert.IsTrue(dedup.TryAcceptState(OldEpoch, 100),
                "fixture premise: the old match applied a frame at tick 100");

            // Positive witness: the anti-stale gate really is closed on a
            // lower tick before the reset — which is exactly what would eat
            // the whole opening of the next match if the seam were forgotten.
            Assert.IsFalse(dedup.TryAcceptState(OldEpoch, 50),
                "witness: a lower tick of the SAME epoch is refused as stale");

            var reset = NewReset(dedup, NewQueue(), new RenderClock(), NewGhosts(), NewStalePolicy());
            reset.ResetForEpoch(NewEpoch);

            Assert.IsTrue(dedup.TryAcceptState(NewEpoch, 50),
                "after the restart the new epoch's own opening ticks must apply — a dedup "
                + "still holding the previous match's epoch and lastAppliedTick refuses every one");
        }

        [Test]
        public void ResetForEpoch_ResetsQueue()
        {
            // Also the plan's own `AfterEpochChange_LowerTickAccepted`: the
            // new match starts back at a low tick, and the queue must admit
            // it instead of judging it against the previous match's newest.
            var queue = NewQueue();
            queue.Reset(OldEpoch);
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(OldEpoch, 500, out _),
                "fixture premise: the old match's frame at tick 500 is admitted");
            queue.Commit(500);

            // Positive witness: the low tick is genuinely refused while the
            // old match's bookkeeping stands.
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Stale, queue.Admit(OldEpoch, 100, out _),
                "witness: tick 100 is far behind the newest committed tick and is refused");

            var reset = NewReset(NewDedup(), queue, new RenderClock(), NewGhosts(), NewStalePolicy());
            reset.ResetForEpoch(NewEpoch);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(NewEpoch, 100, out RenderSnapshot slot),
                "a frame of the NEW epoch carrying a LOWER tick must be accepted after the reset — "
                + "this is the resynchronization rule of spec §3.10");
            Assert.IsNotNull(slot, "an Accepted verdict must hand back a slot to decode into");
        }

        [Test]
        public void ResetForEpoch_ResetsRenderClock()
        {
            var clock = new RenderClock();
            var timings = Timings();
            clock.ResetForEpoch(OldEpoch);
            clock.OnSnapshot(100, OldEpoch);
            clock.OnSnapshot(101, OldEpoch);
            clock.Advance(SimulationWorld.TickDt, in timings);

            // Positive witness: the clock really is running, and on the old
            // match's own tick numbers.
            Assert.IsTrue(clock.Started, "witness: two distinct ticks started the clock");
            Assert.AreEqual(101 - timings.InterpBufferTicks, clock.RenderTick,
                "witness: the clock sits on the old match's target");

            var reset = NewReset(NewDedup(), NewQueue(), clock, NewGhosts(), NewStalePolicy());
            reset.ResetForEpoch(NewEpoch);

            Assert.IsFalse(clock.Started,
                "a restarted clock has no pair of snapshots yet and must not run on the "
                + "previous match's ones");
            Assert.AreEqual(0, clock.RenderTick,
                "the new match starts at tick 0, so the clock must not still read the old "
                + "match's render tick — this is the 'snap on MatchEpoch change' of spec §3.9");

            // Clearing is only half the seam: the clock must now TRACK THE
            // EPOCH IT WAS GIVEN. A reset that forwarded the wrong epoch (a
            // zero, or the epoch that just ended) clears exactly the same
            // state and passes every assertion above — and then the clock
            // ignores every frame of the new match forever, because
            // `OnSnapshot` refuses anything that is not the tracked epoch.
            // Both halves are checked below: the old epoch must not start it,
            // the new one must.
            clock.OnSnapshot(500, OldEpoch);
            clock.OnSnapshot(501, OldEpoch);
            Assert.IsFalse(clock.Started,
                "frames of the FINISHED match's epoch must be ignored after the reset");

            clock.OnSnapshot(10, NewEpoch);
            clock.OnSnapshot(11, NewEpoch);
            Assert.IsTrue(clock.Started,
                "two distinct frames of the NEW epoch must start the clock — if they do not, "
                + "the reset handed the clock an epoch nobody is sending");

            clock.Advance(SimulationWorld.TickDt, in timings);
            Assert.AreEqual(11 - timings.InterpBufferTicks, clock.RenderTick,
                "and the first frame after that start places the clock on the NEW match's "
                + "own target");
        }

        /// bd `app-s0u`, the seventh seam. Without this the mutation "delete
        /// `_tracers.Reset()` from `ResetForEpoch`" leaves the whole suite
        /// green while the rounds of the match that ended keep flying through
        /// the new one.
        [Test]
        public void ResetForEpoch_ClearsTracers()
        {
            var tracers = new TracerProjectiles(4);
            tracers.TrySpawn(1, 0, float2.zero, 1f, new float2(1f, 0f), 10f, 0f, 0.1f, 2f);
            var scratch = new ProjectileState[4];
            Assert.AreEqual(1, tracers.WriteInto(scratch, 0), "witness: a round is being drawn");

            NewReset(NewDedup(), NewQueue(), new RenderClock(), NewGhosts(), NewStalePolicy(),
                NewEventQueue(), tracers).ResetForEpoch(2);

            Assert.AreEqual(0, tracers.Count, "the new epoch starts with no tracer at all");
            Assert.AreEqual(0, tracers.WriteInto(scratch, 0));
        }


        [Test]
        public void ResetForEpoch_ClearsGhosts()
        {
            const uint FarLaterTick = 1000;

            // Positive witness, on an untouched instance: without a reset the
            // previous match's ghosts are still there to be aged and expired.
            var witness = NewGhosts();
            SpawnGhosts(witness, 2);
            Assert.AreEqual(2, witness.Advance(FarLaterTick).Length,
                "witness: unreset ghosts of the old match survive into the next one");

            var ghosts = NewGhosts();
            SpawnGhosts(ghosts, 2);

            var reset = NewReset(NewDedup(), NewQueue(), new RenderClock(), ghosts, NewStalePolicy());
            reset.ResetForEpoch(NewEpoch);

            Assert.AreEqual(0, ghosts.Advance(FarLaterTick).Length,
                "the previous match's tracers must not outlive it — nothing may expire in the "
                + "new match that was born in the old one");
        }

        [Test]
        public void ResetForEpoch_ResetsStalePolicy()
        {
            const uint OldMatchTick = 500;
            const uint NewMatchTick = 2;
            const int NewMatchRenderTick = 10;

            // Positive witness: the applied-frame clock of this class is
            // monotonic, so the finished match's high tick swallows the new
            // match's opening frame and the starvation indicator stays dark
            // for the whole next match — the exact silent failure the seam
            // exists to prevent.
            var witness = NewStalePolicy();
            witness.OnFrameApplied(OldMatchTick, truncated: false);
            witness.OnFrameApplied(NewMatchTick, truncated: false);
            witness.Advance(NewMatchRenderTick);
            Assert.IsFalse(witness.GlobalStarvation,
                "witness: an unreset policy never reports starvation in the next match");

            var policy = NewStalePolicy();
            policy.OnFrameApplied(OldMatchTick, truncated: false);

            var reset = NewReset(NewDedup(), NewQueue(), new RenderClock(), NewGhosts(), policy);
            reset.ResetForEpoch(NewEpoch);

            policy.OnFrameApplied(NewMatchTick, truncated: false);
            policy.Advance(NewMatchRenderTick);

            Assert.IsTrue(policy.GlobalStarvation,
                "after the reset the policy measures the NEW match's own clock: eight render "
                + "ticks past the last applied frame is starvation, and the indicator must arm");
        }

        [Test]
        public void ResetForEpoch_ClearsEventQueue()
        {
            // The sixth seam (Task 44b; spec §3.10 lists "the receive queue of
            // events" among what a full client reset must clear, as its own
            // item beside the set of seen (epoch, tick, seq)).
            //
            // This seam fails in the OPPOSITE direction from the other five: a
            // forgotten Reset here does not refuse anything, it PRODUCES.
            // Records are keyed by ABSOLUTE tick and the restarted match
            // replays its ticks from zero, so a leftover record is handed out
            // again the moment the NEW match's render clock reaches that same
            // tick number — the finished match's death surfacing in the middle
            // of the next one — while occupying capacity until then.
            const uint OldMatchTick = 400;

            // Positive witness, on an untouched instance: the leftover record
            // really is still deliverable. Without it the assertion below
            // would also pass on a queue that never delivers anything at all.
            var witness = NewEventQueue();
            Assert.IsTrue(witness.Enqueue(EventAt(OldMatchTick, id: 1)),
                "fixture premise: the old match's event is queued");
            Assert.IsTrue(witness.TryDequeue((int)OldMatchTick, out SimEvent stale),
                "witness: an unreset queue still hands the previous match's event out when the "
                + "next match's clock reaches that tick number");
            Assert.AreEqual((int)OldMatchTick, stale.Tick,
                "witness: and it is the OLD match's event, on the old match's own tick");

            var events = NewEventQueue();
            events.Enqueue(EventAt(OldMatchTick, id: 1));

            var reset = NewReset(NewDedup(), NewQueue(), new RenderClock(), NewGhosts(),
                NewStalePolicy(), events);
            reset.ResetForEpoch(NewEpoch);

            Assert.IsFalse(events.TryDequeue(int.MaxValue, out _),
                "after the restart the previous match's events must be gone at EVERY render tick — "
                + "an event replayed into the new match is a death or a hit shown for something "
                + "that never happened in the world the player is looking at");
            Assert.AreEqual(0, events.Count,
                "ClientEventQueue.Count must be zero after the seam is cleared");
        }

        // ------------------------------------------------------------------
        // Guards and wire shape.
        // ------------------------------------------------------------------

        [Test]
        public void ValidateRoster_RejectsPlayerCountChangedFromThePreviousMatch()
        {
            // Ф8 gate W-13. `ValidateRoster` never dereferences an element of
            // either array (only `.Length` and the two nulls above) — the
            // same reason this class's own doc gives for NOT unit-testing
            // MatchServer's FishNet wiring does not apply here, and arrays of
            // null references are enough to drive every branch.
            var connections = new NetworkConnection[2];
            var controllers = new PlayerNetworkController[2];

            // -1: no match has ever started on this instance — a first start
            // has no previous roster to disagree with, so any count is legal.
            Assert.DoesNotThrow(
                () => MatchServer.ValidateRoster(connections, controllers, lastPlayerCount: -1),
                "a first start (lastPlayerCount == -1, the 'never started' sentinel) has nothing "
                + "to compare against and must not be rejected");

            // Witness: a restart naming the SAME player count as the
            // previous match is legal — proves the check below is about the
            // MISMATCH, not about restarting at all.
            Assert.DoesNotThrow(
                () => MatchServer.ValidateRoster(connections, controllers, lastPlayerCount: 2),
                "a restart fielding the SAME player count as the previous match must be accepted");

            // The defect this closes: a restart naming a DIFFERENT player
            // count. A compacted or padded roster would silently rename
            // players against the slot indices MatchWelcomeNet.PlayerIndex
            // already promised those clients in the join-phase handshake.
            var ex = Assert.Throws<ArgumentException>(
                () => MatchServer.ValidateRoster(connections, controllers, lastPlayerCount: 3));
            Assert.AreEqual("controllers", ex.ParamName);
            StringAssert.Contains("previous match", ex.Message);

            // The mirror direction — the new roster is LONGER than the
            // previous match's — must be refused too, not merely a shrink.
            Assert.Throws<ArgumentException>(
                () => MatchServer.ValidateRoster(connections, controllers, lastPlayerCount: 1));
        }

        [Test]
        public void ClientMatchReset_RejectsNullSeams()
        {
            var dedup = NewDedup();
            var queue = NewQueue();
            var clock = new RenderClock();
            var ghosts = NewGhosts();
            var policy = NewStalePolicy();
            var events = NewEventQueue();

            // Seven separate guards, seven separate assertions on the
            // PARAMETER NAME: "something threw" would pass even if one seam's
            // guard covered another's argument, which is precisely the mistake
            // a seven-argument constructor invites.
            var tracers = new TracerProjectiles(8);
            Assert.AreEqual("dedup",
                Assert.Throws<ArgumentNullException>(
                    () => new ClientMatchReset(null, queue, clock, ghosts, policy, events, tracers)).ParamName);
            Assert.AreEqual("snapshotQueue",
                Assert.Throws<ArgumentNullException>(
                    () => new ClientMatchReset(dedup, null, clock, ghosts, policy, events, tracers)).ParamName);
            Assert.AreEqual("renderClock",
                Assert.Throws<ArgumentNullException>(
                    () => new ClientMatchReset(dedup, queue, null, ghosts, policy, events, tracers)).ParamName);
            Assert.AreEqual("ghosts",
                Assert.Throws<ArgumentNullException>(
                    () => new ClientMatchReset(dedup, queue, clock, null, policy, events, tracers)).ParamName);
            Assert.AreEqual("stalePolicy",
                Assert.Throws<ArgumentNullException>(
                    () => new ClientMatchReset(dedup, queue, clock, ghosts, null, events, tracers)).ParamName);
            // The sixth seam (Task 44b), by the same rule as the five above.
            Assert.AreEqual("eventQueue",
                Assert.Throws<ArgumentNullException>(
                    () => new ClientMatchReset(dedup, queue, clock, ghosts, policy, null, tracers)).ParamName);

            // The seventh seam (bd `app-s0u`), by the same rule as the six above.
            Assert.AreEqual("tracers",
                Assert.Throws<ArgumentNullException>(
                    () => new ClientMatchReset(dedup, queue, clock, ghosts, policy, events, null))
                    .ParamName);

            // Positive witness: a fully wired set constructs.
            Assert.DoesNotThrow(
                () => new ClientMatchReset(dedup, queue, clock, ghosts, policy, events, tracers),
                "witness: all seven seams present is the legal construction");
        }

        [Test]
        public void LifecycleStructs_AreStructsImplementingIBroadcast()
        {
            // Same pin as `HandshakeStructs_AreStructsImplementingIBroadcast`
            // and `SnapshotBroadcast_IsAStructImplementingIBroadcast`:
            // `IBroadcast` is an empty marker, so a `class` here compiles and
            // breaks only at the generic `Broadcast<T>` call site inside
            // `MatchServer`. This moves that failure back to the type.
            AssertIsBroadcastStruct(typeof(MatchEndedNet));
            AssertIsBroadcastStruct(typeof(MatchRestartedNet));
        }

        static void AssertIsBroadcastStruct(Type t)
        {
            Assert.IsTrue(t.IsValueType,
                $"{t.Name} must be a struct — FishNet's Broadcast<T> is constrained to structs.");
            Assert.IsTrue(typeof(IBroadcast).IsAssignableFrom(t), $"{t.Name} must implement IBroadcast.");
        }

        [Test]
        public void EndedNetFor_CopiesEveryStat()
        {
            // `MatchServer.EndedNetFor` is a pure function of a summary and a
            // slot — no FishNet, no world, no clock — and eleven same-typed
            // assignments in a row are exactly the shape where a swapped pair
            // compiles, runs, and misreports a number for the rest of the
            // project's life. Every value below is DISTINCT for that reason: a
            // fixture with repeated numbers cannot tell a swap from a copy.
            var mine = new MatchStats
            {
                Kills = 11,
                HeadshotKills = 12,
                ShotsFired = 13,
                ShotsHit = 14,
                DashesUsed = 15,
                SlidesUsed = 16,
                DeathTick = 17,
                DamageTaken = 18.5f,
            };
            // The other slot carries different numbers again, so a message
            // built for the wrong player is as visible as a swapped field.
            var other = new MatchStats { Kills = 91, HeadshotKills = 92, ShotsFired = 93 };
            var world = new WorldStats
            {
                WavesCleared = 21,
                MobSpawnsSkipped = 22,
                ProjectileSpawnsSkipped = 23,
            };

            var summary = new MatchSummary(MatchEndReason.MaxDurationReached, NewEpoch,
                finalTick: 4242, in world, droppedEvents: 31,
                new[] { other, mine }, new[] { new NetStats(), new NetStats() });

            MatchEndedNet net = MatchServer.EndedNetFor(in summary, slot: 1);

            Assert.AreEqual((byte)MatchEndReason.MaxDurationReached, net.Reason, "Reason");
            Assert.AreEqual(NewEpoch, net.MatchEpoch, "MatchEpoch");
            Assert.AreEqual(4242u, net.FinalTick, "FinalTick");

            Assert.AreEqual(11, net.Kills, "Kills");
            Assert.AreEqual(12, net.HeadshotKills, "HeadshotKills");
            Assert.AreEqual(13, net.ShotsFired, "ShotsFired");
            Assert.AreEqual(14, net.ShotsHit, "ShotsHit");
            Assert.AreEqual(15, net.DashesUsed, "DashesUsed");
            Assert.AreEqual(16, net.SlidesUsed, "SlidesUsed");
            Assert.AreEqual(17, net.DeathTick, "DeathTick");
            Assert.AreEqual(18.5f, net.DamageTaken, "DamageTaken");

            Assert.AreEqual(21, net.WavesCleared, "WavesCleared");
            Assert.AreEqual(22, net.MobSpawnsSkipped, "MobSpawnsSkipped");
            Assert.AreEqual(23, net.ProjectileSpawnsSkipped, "ProjectileSpawnsSkipped");
        }

        [Test]
        public void MatchEndReason_ValuesAreStableOnTheWire()
        {
            // Pinned literals, not a re-derivation of the enum:
            // `MatchEndedNet.Reason` rides the wire as a single byte, and
            // reordering the members would silently change the meaning of a
            // reason already in flight between builds compiled from different
            // sources (the same discipline `HandshakeRefusal` carries).
            Assert.AreEqual(0, (byte)MatchEndReason.None);
            Assert.AreEqual(1, (byte)MatchEndReason.AllPlayersDead);
            Assert.AreEqual(2, (byte)MatchEndReason.MaxDurationReached);
        }

        /// `eventQueue` (Task 44b) and `tracers` (bd `app-s0u`) are last and
        /// defaulted: every existing call site keeps reading exactly as it did,
        /// and only the test that is ABOUT one of those seams has to name it.
        static ClientMatchReset NewReset(EventDedup dedup, SnapshotQueue queue, RenderClock clock,
            GhostProjectiles ghosts, StalePolicy stalePolicy, ClientEventQueue eventQueue = null,
            TracerProjectiles tracers = null)
            => new ClientMatchReset(dedup, queue, clock, ghosts, stalePolicy,
                eventQueue ?? NewEventQueue(), tracers ?? new TracerProjectiles(8));
    }
}
