using System;
using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 44b (spec §3.10's "full reset of the client's network
    /// state" and its connection/life-cycle half; plan Т44b): the CLIENT end
    /// of the wire — the decision core `ClientLinkState` and the sixth
    /// per-match seam `ClientEventQueue`.
    ///
    /// `ClientMatchLink`'s FISHNET WIRING IS NOT UNIT-TESTED HERE, ON PURPOSE
    /// — the same deliberate split `MatchHandshake`/`HandshakeTests`,
    /// `MatchServer`/`MatchLifecycleTests` and `SnapshotAssembler` already
    /// carry in their own class docs: registering a broadcast, sending one,
    /// and reading `TransportManager.LatencySimulator` all need a live
    /// `NetworkManager`, which EditMode cannot raise. R-COMPILE and,
    /// eventually, the two-process milestone В1 are that proof instead. What
    /// the wiring DECIDES is not in the wiring at all — every branch it has
    /// is a question to `ClientLinkState`, and the answers are pinned below.
    ///
    /// EVERY NEGATIVE CASE CARRIES A POSITIVE WITNESS BESIDE IT. A core that
    /// refused everything, or one that accepted everything, would otherwise
    /// pass half this file; a stub returning a constant would pass more.
    ///
    /// FIXTURES ARE HAND-BUILT (Р56 — the asset owns the game's numbers, the
    /// fixture owns the test's). `EventBudget` below is deliberately NOT
    /// `NetConfig`'s shipped 16: a number that happens to match the asset
    /// cannot show whether the code read the argument or the asset.
    public class ClientLinkTests
    {
        const ushort FirstEpoch = 7;
        const ushort SecondEpoch = 8;
        const long FirstSeed = 111L;
        const long SecondSeed = 222L;
        const byte Slot = 2;

        /// The test's own event budget — see the class doc for why it is not
        /// the asset's 16.
        const int EventBudget = 7;

        /// The test's own roster cap, the number `OnWelcome` validates the
        /// welcome's seat against. Deliberately NOT `ArenaConfig`'s shipped 3,
        /// for the same reason `EventBudget` above is not the asset's 16: a
        /// fixture that happens to match the asset cannot show whether the code
        /// read the argument or reached for a number of its own.
        const int RosterCap = 5;

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

        /// A queue sized off the same `NetTimings` the snapshot ring uses,
        /// so the two stay in step by construction rather than by luck.
        static ClientEventQueue NewQueue(int eventBudget = EventBudget)
        {
            var timings = Timings();
            return new ClientEventQueue(in timings, eventBudget);
        }

        static SnapshotBlocks.EventRecord Record(ushort seq) => new SnapshotBlocks.EventRecord
        {
            Kind = 1,
            Seq = seq,
            TickDelta = 0,
            Pos = new float2(1f, 2f),
            PayloadOffset = 0,
            PayloadLength = 0,
        };

        static MatchWelcomeNet Welcome(ushort epoch, long seed = FirstSeed, byte playerIndex = Slot)
            => new MatchWelcomeNet { MatchEpoch = epoch, Seed = seed, PlayerIndex = playerIndex };

        static MatchRestartedNet Restarted(ushort epoch, long seed = SecondSeed)
            => new MatchRestartedNet { MatchEpoch = epoch, Seed = seed };

        static MatchEndedNet Ended(ushort epoch, uint finalTick = 500u)
            => new MatchEndedNet { Reason = 1, MatchEpoch = epoch, FinalTick = finalTick, Kills = 3 };

        /// A state that has already been admitted to `FirstEpoch` — the
        /// starting point of every life-cycle test below.
        static ClientLinkState Joined()
        {
            var state = new ClientLinkState();
            Assert.IsTrue(state.TryBeginHello(), "fixture premise: the first hello is allowed");
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied,
                state.OnWelcome(Welcome(FirstEpoch), RosterCap).Verdict,
                "fixture premise: the opening welcome is accepted");
            return state;
        }

        // ------------------------------------------------------------------
        // Rule 1 (brief §2.5): the hello goes out exactly once per
        // connection. A second one is refused by MatchRoster as
        // DuplicatePlayer (MatchHandshake's own I-4 doc) — the seat is
        // already taken, so the repeat buys nothing and only muddies the
        // server's log.
        // ------------------------------------------------------------------

        [Test]
        public void Hello_IsAllowedExactlyOnce()
        {
            var state = new ClientLinkState();

            Assert.IsTrue(state.TryBeginHello(),
                "ClientLinkState.TryBeginHello must allow the FIRST hello — a client that never "
                + "greets the server waits out JoinTimeoutSeconds with nothing to show for it");
            Assert.IsTrue(state.HelloSent,
                "ClientLinkState.HelloSent must record that the hello was allowed out");

            Assert.IsFalse(state.TryBeginHello(),
                "ClientLinkState.TryBeginHello must refuse every hello after the first — the "
                + "server answers a repeat with DuplicatePlayer and the seat is already ours");
            Assert.IsFalse(state.TryBeginHello(),
                "the refusal is permanent, not alternating");
        }

        // ------------------------------------------------------------------
        // Rule 2: the welcome is accepted ONCE. A second one can reach a
        // client from a stale MatchHandshake instance that was never
        // unregistered (MatchHandshake.Unregister's own doc: both handlers
        // stay subscribed and the stale one answers with its OWN epoch).
        // ------------------------------------------------------------------

        [Test]
        public void Welcome_IsAcceptedOnceAndCarriesTheMatchIdentity()
        {
            var state = new ClientLinkState();
            state.TryBeginHello();

            ClientLinkState.LinkAction action = state.OnWelcome(Welcome(FirstEpoch), RosterCap);

            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied, action.Verdict,
                "the opening welcome must be applied");
            Assert.IsTrue(action.ResetSeams,
                "the welcome that opens the first match is one of the two messages that carry the "
                + "right to reset (ClientMatchReset's own doc names it beside MatchRestartedNet)");
            Assert.AreEqual(FirstEpoch, action.Epoch,
                "LinkAction.Epoch must be the epoch the seams are to be reset for");

            Assert.AreEqual(ClientLinkState.LinkPhase.Joined, state.Phase, "ClientLinkState.Phase");
            Assert.AreEqual(FirstEpoch, state.MatchEpoch, "ClientLinkState.MatchEpoch");
            Assert.AreEqual(FirstSeed, state.Seed, "ClientLinkState.Seed");
            Assert.AreEqual(Slot, state.PlayerIndex, "ClientLinkState.PlayerIndex");
        }

        [Test]
        public void Welcome_SecondOneIsRefusedByValueAndChangesNothing()
        {
            ClientLinkState state = Joined();

            ClientLinkState.LinkAction second =
                state.OnWelcome(Welcome(SecondEpoch, seed: SecondSeed, playerIndex: 1), RosterCap);

            Assert.AreEqual(ClientLinkState.LinkVerdict.AlreadyJoined, second.Verdict,
                "a second welcome must be refused by VALUE — a stale handshake instance answering "
                + "with its own epoch must not rewrite the epoch of the match in progress");
            Assert.IsFalse(second.ResetSeams,
                "a refused welcome must not carry the right to reset the seams");

            Assert.AreEqual(FirstEpoch, state.MatchEpoch,
                "ClientLinkState.MatchEpoch must still name the match this client was admitted to");
            Assert.AreEqual(FirstSeed, state.Seed, "ClientLinkState.Seed must be unchanged");
            Assert.AreEqual(Slot, state.PlayerIndex, "ClientLinkState.PlayerIndex must be unchanged");
        }

        [Test]
        public void Welcome_BeforeAnyHelloIsUnexpected()
        {
            var state = new ClientLinkState();

            Assert.AreEqual(ClientLinkState.LinkVerdict.Unexpected,
                state.OnWelcome(Welcome(FirstEpoch), RosterCap).Verdict,
                "the server only ever welcomes an answer to a hello — a welcome to a client that "
                + "never greeted anyone belongs to no exchange this object took part in");
            Assert.AreEqual(ClientLinkState.LinkPhase.Connecting, state.Phase,
                "ClientLinkState.Phase must not advance on a message that was refused");

            // Positive witness: the very same welcome IS applied once the
            // hello has gone out, so the refusal above is about the ORDER
            // and not about the message.
            var witness = new ClientLinkState();
            witness.TryBeginHello();
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied,
                witness.OnWelcome(Welcome(FirstEpoch), RosterCap).Verdict,
                "witness: after the hello, that same welcome is applied");
        }

        [Test]
        public void Welcome_WithTheReservedZeroEpochIsRefused()
        {
            var state = new ClientLinkState();
            state.TryBeginHello();

            Assert.AreEqual(ClientLinkState.LinkVerdict.ReservedEpoch,
                state.OnWelcome(Welcome(0), RosterCap).Verdict,
                "epoch 0 is reserved for 'there is no epoch' (MatchEpochCounter never mints it), "
                + "and adopting it would leave every seam tracking an epoch the server never sends");
            Assert.AreEqual(ClientLinkState.LinkPhase.HelloSent, state.Phase,
                "ClientLinkState.Phase must not advance to Joined on a refused welcome");

            // Positive witness: the smallest epoch the counter CAN mint is
            // accepted, so the refusal above is about zero and not about
            // small numbers.
            var witness = new ClientLinkState();
            witness.TryBeginHello();
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied,
                witness.OnWelcome(Welcome(1), RosterCap).Verdict,
                "witness: epoch 1, the first the counter ever mints, is accepted");
        }

        // ------------------------------------------------------------------
        // Rule 2b (Stage 2 Task 44c fix-round 1, F-5): the seat the welcome
        // assigns is validated against the roster it has to index. The byte is
        // the only field of the handshake nothing downstream can survive being
        // wrong about — `RenderSnapshot.Player` indexes `Players` by it with no
        // guard of its own — so it is refused where it ENTERS rather than
        // patched where it hurts.
        // ------------------------------------------------------------------

        [Test]
        public void Welcome_WithASeatOutsideTheRosterIsRefused()
        {
            var state = new ClientLinkState();
            state.TryBeginHello();

            ClientLinkState.LinkAction action =
                state.OnWelcome(Welcome(FirstEpoch, playerIndex: RosterCap), RosterCap);

            Assert.AreEqual(ClientLinkState.LinkVerdict.SlotOutOfRange, action.Verdict,
                "a welcome naming seat " + RosterCap + " in a roster of " + RosterCap + " seats "
                + "must be refused by VALUE — the seat is the index every reader of the render "
                + "pair uses, and no reader of it carries a range check of its own");
            Assert.IsFalse(action.ResetSeams,
                "a refused welcome must not carry the right to reset the seams");
            Assert.AreEqual(ClientLinkState.LinkPhase.HelloSent, state.Phase,
                "ClientLinkState.Phase must not advance to Joined on a refused welcome");
            Assert.AreEqual(0, state.MatchEpoch,
                "ClientLinkState.MatchEpoch must not adopt the epoch of a welcome that was refused");
            Assert.AreEqual((byte)0, state.PlayerIndex,
                "ClientLinkState.PlayerIndex must stay at its unassigned zero — storing the "
                + "out-of-range byte and refusing the verdict would leave the very number the "
                + "refusal is about readable by everyone");

            // Positive witness: the LARGEST legal seat is accepted, so the
            // refusal above is about the boundary and not about big numbers —
            // and it is what makes an off-by-one in the comparison observable.
            var witness = new ClientLinkState();
            witness.TryBeginHello();
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied,
                witness.OnWelcome(Welcome(FirstEpoch, playerIndex: RosterCap - 1), RosterCap).Verdict,
                "witness: seat " + (RosterCap - 1) + ", the last one a roster of " + RosterCap
                + " seats has, is accepted");
            Assert.AreEqual((byte)(RosterCap - 1), witness.PlayerIndex,
                "ClientLinkState.PlayerIndex must be the seat the accepted welcome named");
        }

        // ------------------------------------------------------------------
        // Rule 3: the refusal is terminal, and its reason is remembered
        // BEFORE the disconnect that follows it (MatchHandshake.Refuse
        // broadcasts, then calls Disconnect(false)). A client that lost the
        // reason shows "connection lost" where the truth was "your balance
        // data disagrees with the server's".
        // ------------------------------------------------------------------

        [Test]
        public void Refusal_IsTerminalAndRemembersTheReason()
        {
            var state = new ClientLinkState();
            state.TryBeginHello();

            var refused = new MatchRefusedNet { Reason = (byte)HandshakeRefusal.SimConfigMismatch };
            ClientLinkState.LinkAction action = state.OnRefused(in refused);

            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied, action.Verdict,
                "the refusal itself is applied — it is the one thing this client will ever learn "
                + "about why it is not playing");
            Assert.IsFalse(action.ResetSeams,
                "a refusal opens no match and must not carry the right to reset the seams");
            Assert.AreEqual(ClientLinkState.LinkPhase.Refused, state.Phase, "ClientLinkState.Phase");
            Assert.AreEqual(HandshakeRefusal.SimConfigMismatch, state.RefusalReason,
                "ClientLinkState.RefusalReason must survive the disconnect that follows it");
            Assert.AreEqual((byte)HandshakeRefusal.SimConfigMismatch, state.RefusalReasonRaw,
                "ClientLinkState.RefusalReasonRaw must keep the byte exactly as it rode the wire");

            // Terminal: nothing that arrives afterwards is applied.
            Assert.AreEqual(ClientLinkState.LinkVerdict.LinkRefused,
                state.OnWelcome(Welcome(SecondEpoch), RosterCap).Verdict,
                "a welcome arriving after a refusal must be refused by value");
            Assert.AreEqual(ClientLinkState.LinkVerdict.LinkRefused,
                state.OnRestarted(Restarted(SecondEpoch)).Verdict,
                "a restart arriving after a refusal must be refused by value");
            Assert.AreEqual(ClientLinkState.LinkVerdict.LinkRefused,
                state.OnEnded(Ended(SecondEpoch)).Verdict,
                "an end arriving after a refusal must be refused by value");
            Assert.AreEqual(0, state.MatchEpoch,
                "ClientLinkState.MatchEpoch must still be the 'no epoch' zero — a refused client "
                + "was never admitted to any match");
        }

        [Test]
        public void Refusal_DecodesEveryCodeAndNeverReportsNone()
        {
            // Completeness by enumeration over the ONE refusal vocabulary
            // (HandshakeNet.cs). `None` is the deliberate exception: a
            // refusal message carrying 0 is the same internal contract
            // violation MatchHandshake.Refuse already maps to
            // UnrecognizedRejection rather than let a human — or a UI —
            // read a refusal as "not refused at all".
            foreach (HandshakeRefusal value in Enum.GetValues(typeof(HandshakeRefusal)))
            {
                var state = new ClientLinkState();
                state.TryBeginHello();
                var refused = new MatchRefusedNet { Reason = (byte)value };

                Assert.AreEqual(ClientLinkState.LinkVerdict.Applied, state.OnRefused(in refused).Verdict,
                    $"a refusal carrying {value} must be applied");

                HandshakeRefusal expected = value == HandshakeRefusal.None
                    ? HandshakeRefusal.UnrecognizedRejection
                    : value;
                Assert.AreEqual(expected, state.RefusalReason,
                    $"ClientLinkState.RefusalReason for the wire byte {(byte)value}");
                Assert.AreEqual((byte)value, state.RefusalReasonRaw,
                    $"ClientLinkState.RefusalReasonRaw for the wire byte {(byte)value}");
            }

            // A code from a NEWER server build than this client: still a
            // refusal, still not None, and the raw byte is kept so a log
            // line can name the number nobody here understands.
            var newer = new ClientLinkState();
            newer.TryBeginHello();
            var unknown = new MatchRefusedNet { Reason = 200 };
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied, newer.OnRefused(in unknown).Verdict);
            Assert.AreEqual(HandshakeRefusal.UnrecognizedRejection, newer.RefusalReason,
                "ClientLinkState.RefusalReason must fall back to UnrecognizedRejection, never None");
            Assert.AreEqual(200, newer.RefusalReasonRaw,
                "ClientLinkState.RefusalReasonRaw must keep the unrecognized byte itself");
        }

        [Test]
        public void Refusal_AfterAWelcomeIsIgnored()
        {
            // DuplicatePlayer is the ONE refusal an already-seated, entirely
            // legitimate connection can trigger, and MatchHandshake.Refuse
            // deliberately does NOT disconnect on it (fix-round 1, I-4) —
            // precisely so a real player's seat is not burned by a harmless
            // repeat. Treating it as terminal here would throw the match
            // away for the same harmless repeat, from the other end.
            ClientLinkState state = Joined();
            var refused = new MatchRefusedNet { Reason = (byte)HandshakeRefusal.DuplicatePlayer };

            Assert.AreEqual(ClientLinkState.LinkVerdict.AlreadyJoined, state.OnRefused(in refused).Verdict,
                "a refusal arriving after this client was admitted must be refused by value");
            Assert.AreEqual(ClientLinkState.LinkPhase.Joined, state.Phase,
                "ClientLinkState.Phase must stay Joined — the match is running");
            Assert.AreEqual(HandshakeRefusal.None, state.RefusalReason,
                "ClientLinkState.RefusalReason must stay empty: this client was not refused");

            // Positive witness: the very same message IS applied to a state
            // that never got a welcome, so the refusal above is about the
            // PHASE and not about the message.
            var witness = new ClientLinkState();
            witness.TryBeginHello();
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied, witness.OnRefused(in refused).Verdict,
                "witness: the same refusal is applied to a client that was never admitted");
        }

        // ------------------------------------------------------------------
        // Rule 4: MatchRestartedNet — and ONLY it, beside the opening
        // welcome — carries the right to reset the seams (MatchRestartedNet's
        // own doc, ClientMatchReset's "never on a snapshot").
        // ------------------------------------------------------------------

        [Test]
        public void Restart_CarriesTheRightToResetAndTheNewMatchIdentity()
        {
            ClientLinkState state = Joined();

            ClientLinkState.LinkAction action = state.OnRestarted(Restarted(SecondEpoch));

            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied, action.Verdict,
                "a restart naming a fresh epoch must be applied");
            Assert.IsTrue(action.ResetSeams,
                "the restart is the message that carries the right to reset — a client that does "
                + "not reset here starts the new match with the old match's tick numbers and is "
                + "silently dead for its whole length");
            Assert.AreEqual(SecondEpoch, action.Epoch,
                "LinkAction.Epoch must be the NEW epoch, the one the seams are to track");

            Assert.AreEqual(ClientLinkState.LinkPhase.Joined, state.Phase, "ClientLinkState.Phase");
            Assert.AreEqual(SecondEpoch, state.MatchEpoch, "ClientLinkState.MatchEpoch");
            Assert.AreEqual(SecondSeed, state.Seed,
                "ClientLinkState.Seed must be the restart's own seed — it is the authoritative one");
            Assert.AreEqual(Slot, state.PlayerIndex,
                "ClientLinkState.PlayerIndex must survive a restart: §6k Р164 keeps the roster "
                + "untouched, which is why MatchRestartedNet carries no PlayerIndex to re-read");
        }

        [Test]
        public void Restart_NamingTheEpochAlreadyTrackedIsRefusedAsADuplicate()
        {
            // SnapshotQueue.Reset and ClientMatchReset both state that
            // deduplicating a repeated life-cycle message belongs to the
            // CALLER, because only the caller can tell a repeat from a
            // deliberate same-epoch restart. This object is that caller.
            ClientLinkState state = Joined();

            ClientLinkState.LinkAction repeat = state.OnRestarted(Restarted(FirstEpoch, seed: SecondSeed));

            Assert.AreEqual(ClientLinkState.LinkVerdict.DuplicateEpoch, repeat.Verdict,
                "a restart naming the epoch already tracked is a repeat of a message this client "
                + "has acted on");
            Assert.IsFalse(repeat.ResetSeams,
                "a repeated restart must not reset the seams a second time — that would throw away "
                + "the frames of the match already in progress");
            Assert.AreEqual(FirstSeed, state.Seed,
                "ClientLinkState.Seed must not be rewritten by a repeated restart");

            // Positive witness: a DIFFERENT epoch on the same state is
            // applied, so the refusal above is about the repeat.
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied,
                state.OnRestarted(Restarted(SecondEpoch)).Verdict,
                "witness: a restart naming a fresh epoch is applied");
        }

        [Test]
        public void Restart_WithTheReservedZeroEpochIsRefused()
        {
            ClientLinkState state = Joined();

            Assert.AreEqual(ClientLinkState.LinkVerdict.ReservedEpoch,
                state.OnRestarted(Restarted(0)).Verdict,
                "epoch 0 is reserved for 'there is no epoch' and must never become the tracked one");
            Assert.AreEqual(FirstEpoch, state.MatchEpoch,
                "ClientLinkState.MatchEpoch must still name the match in progress");
        }

        [Test]
        public void Restart_BeforeAnyWelcomeIsUnexpected()
        {
            var state = new ClientLinkState();
            state.TryBeginHello();

            ClientLinkState.LinkAction action = state.OnRestarted(Restarted(SecondEpoch));

            Assert.AreEqual(ClientLinkState.LinkVerdict.Unexpected, action.Verdict,
                "there is no match to restart before this client has been admitted to one");
            Assert.IsFalse(action.ResetSeams,
                "an unexpected restart must not hand the seams an epoch this client was never "
                + "admitted to — a snapshot of that epoch would then be applied unadmitted");
            Assert.AreEqual(0, state.MatchEpoch, "ClientLinkState.MatchEpoch");
        }

        // ------------------------------------------------------------------
        // Rule 5: MatchEndedNet ends the match and clears NOTHING. The
        // restart, if it comes, comes as its own message and brings its own
        // epoch with it.
        // ------------------------------------------------------------------

        [Test]
        public void MatchEnded_DoesNotResetTheSeams()
        {
            ClientLinkState state = Joined();

            ClientLinkState.LinkAction action = state.OnEnded(Ended(FirstEpoch));

            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied, action.Verdict,
                "the end of the match is applied — it carries the summary this client will show");
            Assert.IsFalse(action.ResetSeams,
                "an ended match must NOT reset the seams: the epoch has not changed, and resetting "
                + "here would throw away the very frames the end screen interpolates over");
            Assert.AreEqual(ClientLinkState.LinkPhase.MatchEnded, state.Phase, "ClientLinkState.Phase");
            Assert.AreEqual(FirstEpoch, state.MatchEpoch,
                "ClientLinkState.MatchEpoch must still name the match that ended");
            Assert.AreEqual(3, state.EndedNet.Kills,
                "ClientLinkState.EndedNet must hold the summary the message carried");

            // Positive witness, on the SAME state: the restart that follows
            // an end DOES carry the right to reset. Without it, "ResetSeams
            // is false" would also pass on a core that never resets at all.
            ClientLinkState.LinkAction restart = state.OnRestarted(Restarted(SecondEpoch));
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied, restart.Verdict,
                "witness: a restart after an end is applied");
            Assert.IsTrue(restart.ResetSeams,
                "witness: the restart, unlike the end, does carry the right to reset");
        }

        [Test]
        public void MatchEnded_OfAForeignEpochIsRefusedByValue()
        {
            // Rule 6. The end of a match this client is not in must not put
            // it into MatchEnded — a client frozen on someone else's end
            // screen sees its own match keep playing behind it.
            ClientLinkState state = Joined();

            ClientLinkState.LinkAction foreign = state.OnEnded(Ended(SecondEpoch, finalTick: 900u));

            Assert.AreEqual(ClientLinkState.LinkVerdict.ForeignEpoch, foreign.Verdict,
                "an end naming an epoch this client is not in must be refused by value");
            Assert.AreEqual(ClientLinkState.LinkPhase.Joined, state.Phase,
                "ClientLinkState.Phase must stay Joined — this client's own match is still running");
            Assert.AreEqual(0u, state.EndedNet.FinalTick,
                "ClientLinkState.EndedNet must not take the summary of a foreign match");

            // Positive witness: the SAME message shaped for THIS epoch is
            // applied, so the refusal is about the epoch and nothing else.
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied,
                state.OnEnded(Ended(FirstEpoch, finalTick: 900u)).Verdict,
                "witness: the same end, naming this client's own epoch, is applied");
            Assert.AreEqual(900u, state.EndedNet.FinalTick,
                "witness: and its summary is the one that lands");
        }

        [Test]
        public void MatchEnded_RepeatedForTheSameMatchIsRefusedByValue()
        {
            ClientLinkState state = Joined();
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied,
                state.OnEnded(Ended(FirstEpoch, finalTick: 500u)).Verdict,
                "fixture premise: the first end is applied");

            Assert.AreEqual(ClientLinkState.LinkVerdict.AlreadyEnded,
                state.OnEnded(Ended(FirstEpoch, finalTick: 900u)).Verdict,
                "the same match cannot end twice — a repeated MatchEndedNet is a repeat, not news");
            Assert.AreEqual(500u, state.EndedNet.FinalTick,
                "ClientLinkState.EndedNet must keep the summary of the end that was applied");
        }

        [Test]
        public void MatchEnded_BeforeAnyWelcomeIsUnexpected()
        {
            var state = new ClientLinkState();
            state.TryBeginHello();

            Assert.AreEqual(ClientLinkState.LinkVerdict.Unexpected,
                state.OnEnded(Ended(FirstEpoch)).Verdict,
                "no match has been joined, so none of them can have ended for this client");
            Assert.AreEqual(ClientLinkState.LinkPhase.HelloSent, state.Phase,
                "ClientLinkState.Phase must not advance on a message that was refused");
        }

        // ------------------------------------------------------------------
        // The sixth seam: ClientEventQueue (spec §3.10 names "the receive
        // queue of events" among what a full client reset must clear).
        // EventDedup answers "is this event new" and stores nothing;
        // RenderSnapshot carries state and no events; between "accepted" and
        // "shown" there was nowhere for an event to wait.
        // ------------------------------------------------------------------

        [Test]
        public void Queue_CapacityIsTheEventBudgetTimesTheSnapshotRingDepth()
        {
            // The number is DERIVED, not chosen: at most `SnapshotEventBudget`
            // events ride one frame, and at most `SnapshotQueue.Depth` frames'
            // worth of ticks can sit between the render clock and the newest
            // tick buffered. Asserting against `SnapshotQueue.Depth` itself —
            // rather than restating `InterpBufferTicks + 2` here — is what
            // keeps the two from drifting apart silently.
            var arena = TestConfigs.DefaultArena();
            var timings = Timings();
            var ring = new SnapshotQueue(in arena, in timings);

            Assert.AreEqual(EventBudget * ring.Depth, NewQueue().Capacity,
                "ClientEventQueue.Capacity must be the per-frame event budget times the number of "
                + "frames the snapshot ring can hold undischarged");
        }

        [Test]
        public void Queue_DeliversInTickOrderAndWithholdsTheFuture()
        {
            var queue = NewQueue();

            // Deliberately out of tick order on the way in: a reordered
            // datagram is an everyday event at 5% loss, and the queue is
            // fed in arrival order, not in tick order.
            Assert.IsTrue(queue.Enqueue(12u, Record(seq: 30)), "fixture premise: enqueued");
            Assert.IsTrue(queue.Enqueue(10u, Record(seq: 10)), "fixture premise: enqueued");
            Assert.IsTrue(queue.Enqueue(11u, Record(seq: 20)), "fixture premise: enqueued");

            Assert.IsTrue(queue.TryDequeue(renderTick: 11, out ClientEventQueue.PendingEvent first));
            Assert.AreEqual(10u, first.Tick, "PendingEvent.Tick of the first delivery");
            Assert.AreEqual(10, first.Record.Seq,
                "PendingEvent.Record.Seq of the first delivery — the record delivered first must "
                + "be the one born on the earliest tick, not the one that arrived first");

            Assert.IsTrue(queue.TryDequeue(renderTick: 11, out ClientEventQueue.PendingEvent second));
            Assert.AreEqual(11u, second.Tick, "PendingEvent.Tick of the second delivery");
            Assert.AreEqual(20, second.Record.Seq, "PendingEvent.Record.Seq of the second delivery");

            Assert.IsFalse(queue.TryDequeue(renderTick: 11, out _),
                "the event born on tick 12 must NOT be handed out while the render clock is on "
                + "tick 11 — showing an event before its moment is the whole failure this queue "
                + "exists to prevent");

            // Positive witness: it is withheld, not lost. One more tick of
            // render time and it comes out.
            Assert.IsTrue(queue.TryDequeue(renderTick: 12, out ClientEventQueue.PendingEvent third),
                "witness: the withheld event is delivered once its tick is reached");
            Assert.AreEqual(12u, third.Tick, "PendingEvent.Tick of the third delivery");
            Assert.AreEqual(30, third.Record.Seq, "PendingEvent.Record.Seq of the third delivery");
        }

        [Test]
        public void Queue_KeepsArrivalOrderWithinOneTick()
        {
            // Two events of the SAME tick: the server assigned their seq in
            // BeginTick, in order, and nothing downstream may shuffle them.
            var queue = NewQueue();
            queue.Enqueue(5u, Record(seq: 1));
            queue.Enqueue(5u, Record(seq: 2));
            queue.Enqueue(5u, Record(seq: 3));

            Assert.IsTrue(queue.TryDequeue(renderTick: 5, out ClientEventQueue.PendingEvent a));
            Assert.IsTrue(queue.TryDequeue(renderTick: 5, out ClientEventQueue.PendingEvent b));
            Assert.IsTrue(queue.TryDequeue(renderTick: 5, out ClientEventQueue.PendingEvent c));

            Assert.AreEqual(1, a.Record.Seq, "PendingEvent.Record.Seq, first out");
            Assert.AreEqual(2, b.Record.Seq, "PendingEvent.Record.Seq, second out");
            Assert.AreEqual(3, c.Record.Seq, "PendingEvent.Record.Seq, third out");
        }

        [Test]
        public void Queue_CountsTheOverflowItRefusesAndKeepsItsResidents()
        {
            // A one-event budget and the smallest interpolation buffer make
            // a queue of exactly SnapshotQueue's own depth — small enough to
            // fill in a test, sized by the same expression as the real one.
            var timings = new NetTimings { InterpBufferTicks = 1 };
            var queue = new ClientEventQueue(in timings, snapshotEventBudget: 1);
            int capacity = queue.Capacity;
            Assert.Greater(capacity, 0, "fixture premise: the queue holds something");

            for (int i = 0; i < capacity; i++)
            {
                Assert.IsTrue(queue.Enqueue(1u, Record(seq: (ushort)i)),
                    $"fixture premise: event {i} fits inside the capacity");
            }
            Assert.AreEqual(0, queue.OverflowDroppedEvents,
                "witness: filling the queue exactly to capacity drops nothing");

            Assert.IsFalse(queue.Enqueue(1u, Record(seq: 999)),
                "an event past the capacity must be refused BY VALUE — the dedup has already "
                + "marked it seen, so the redundant resends will never bring it back");
            Assert.AreEqual(1, queue.OverflowDroppedEvents,
                "ClientEventQueue.OverflowDroppedEvents must count the refusal: a silently lost "
                + "event is exactly what spec §3.7 forbids");

            // The residents are untouched: the newcomer was refused, not
            // swapped in for one of them.
            Assert.AreEqual(capacity, queue.Count, "ClientEventQueue.Count after the refusal");
            for (int i = 0; i < capacity; i++)
            {
                Assert.IsTrue(queue.TryDequeue(renderTick: 1, out ClientEventQueue.PendingEvent e),
                    $"resident {i} must still be deliverable");
                Assert.AreEqual(i, e.Record.Seq,
                    "PendingEvent.Record.Seq — the residents keep their arrival order");
            }
        }

        [Test]
        public void Queue_ResetEmptiesItAndKeepsTheOverflowCounter()
        {
            var timings = new NetTimings { InterpBufferTicks = 1 };
            var queue = new ClientEventQueue(in timings, snapshotEventBudget: 1);
            int capacity = queue.Capacity;

            for (int i = 0; i < capacity; i++) queue.Enqueue(1u, Record(seq: (ushort)i));
            queue.Enqueue(1u, Record(seq: 999));
            Assert.AreEqual(1, queue.OverflowDroppedEvents, "fixture premise: one refusal counted");

            queue.Reset();

            Assert.IsFalse(queue.TryDequeue(renderTick: int.MaxValue, out _),
                "a reset queue must hand out nothing at all — the previous match's events must "
                + "not be shown over the new match's opening seconds");
            Assert.AreEqual(0, queue.Count, "ClientEventQueue.Count after Reset");

            Assert.AreEqual(1, queue.OverflowDroppedEvents,
                "ClientEventQueue.OverflowDroppedEvents must SURVIVE the reset — the same reason "
                + "SnapshotQueue.OverflowDroppedSnapshots does: a per-connection health counter "
                + "that cleared itself every restart would hide the pattern it exists to surface");

            // Positive witness: the queue still works after the reset.
            Assert.IsTrue(queue.Enqueue(2u, Record(seq: 4)),
                "witness: a reset queue accepts the new match's events");
            Assert.IsTrue(queue.TryDequeue(renderTick: 2, out ClientEventQueue.PendingEvent fresh),
                "witness: and hands them out");
            Assert.AreEqual(4, fresh.Record.Seq, "PendingEvent.Record.Seq after the reset");
        }

        [Test]
        public void Queue_HoldsNothingBeforeAnythingIsEnqueued()
        {
            // The boundary the delivery tests are measured against: an empty
            // queue answers `false` for every render tick, including a
            // hostile one.
            var queue = NewQueue();

            Assert.IsFalse(queue.TryDequeue(renderTick: 0, out _), "an empty queue delivers nothing");
            Assert.IsFalse(queue.TryDequeue(renderTick: int.MaxValue, out _),
                "and no render tick makes an empty queue produce an event");

            // A render tick before the match began cannot be produced by
            // RenderClock (its own SetTime floors at zero), but this class
            // takes the number from a caller and refuses rather than throws
            // (Р82).
            queue.Enqueue(0u, Record(seq: 1));
            Assert.IsFalse(queue.TryDequeue(renderTick: -1, out _),
                "a negative render tick names no moment and must deliver nothing");
            Assert.IsTrue(queue.TryDequeue(renderTick: 0, out _),
                "witness: the same event at render tick 0 is delivered");
        }
    }
}
