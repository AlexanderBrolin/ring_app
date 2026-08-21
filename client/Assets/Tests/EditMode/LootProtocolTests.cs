using FishNet.Broadcast;
using FishNet.Serializing;
using FishNet.Serializing.Helping;
using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Ring.Simulation.Loot;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т28 (spec §3.8 С17/Р237, table Р27; plan Т28): the reliable
    /// loot channel — the two wire structs, the epoch gate and the reply
    /// builder both ends share (`LootNet`), the world seam a request enters
    /// the simulation through (`SimulationWorld.TryBeginLoot`), and the
    /// client's memory of the request it is waiting on
    /// (`LootRequestTracker`).
    ///
    /// THE TWO HANDLERS THEMSELVES ARE NOT COVERED HERE, by the same split
    /// every other broadcast handler in this project carries
    /// (`SpectateTests`'s own class doc states the mechanism):
    /// `MatchServer.OnLootRequest` and `NetworkSimBackend.OnLootResult` need
    /// a live `NetworkManager` EditMode cannot raise, so R-COMPILE and
    /// milestone В1 stand in for their wiring — and everything that DECIDES
    /// anything was kept out of them for exactly that reason. What is left
    /// inside a handler is a slot lookup, a call and a send.
    ///
    /// THE VALIDATION CHECKS ARE NOT RE-TESTED HERE either: they are
    /// `LootOps.Validate`'s, they were tested by `LootOpsTests` at Т17-Т19,
    /// and Т28 adds none of its own. What this file witnesses about them is the
    /// only thing that is new — that a request arriving from the wire
    /// reaches them at all, and that the code they answer with comes back out
    /// the same way it went in.
    ///
    /// EVERY NEGATIVE CASE CARRIES A POSITIVE WITNESS RIGHT NEXT TO IT — the
    /// discipline `SpectateTests`/`MatchLifecycleTests`/`HandshakeTests`
    /// already use: a witness proves the fixture could have produced the
    /// other answer, so the refusal above it is about the ONE fact under
    /// test rather than an accident of some other field the fixture also
    /// gets wrong.
    public class LootProtocolTests
    {
        // ------------------------------------------------ 0. fixtures

        /// A one-player world whose collector stands at the origin with a
        /// stocked crate one metre away — well inside `Loot.LootRadius`.
        ///
        /// `TestConfigs.OpenField()`, NOT `Open()` (fixture rule R-173/355):
        /// every fixture in this file TICKS the world, because
        /// `TryBeginLoot` reads the SANITIZED input of the last completed
        /// tick and nothing else writes that array. A ticking fixture with a
        /// live collector standing in the core would wake the Director and
        /// its escort; the zone-free arena is the recorded way out.
        static SimulationWorld MakeWorld(out int containerId)
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            containerId = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f),
                new byte[] { 1, 2 });
            return w;
        }

        /// One tick with every window open, so `_sanitizedInputs` — the array
        /// `TryBeginLoot` validates against — carries a raised flag when the
        /// request arrives. This is the fixture expression of what happens on
        /// a real server: the flag rides the tick input (Р239) and the
        /// request rides a separate reliable message.
        static void TickWithWindowsOpen(SimulationWorld w)
        {
            var inputs = new SimInput[w.PlayerCount];
            for (int i = 0; i < inputs.Length; i++) inputs[i] = new SimInput { InventoryOpen = true };
            w.TickAll(inputs);
        }

        static LootRequestNet Request(ushort epoch, LootOp op, int containerId, byte slot)
            => new LootRequestNet
            {
                MatchEpoch = epoch,
                Op = (byte)op,
                ContainerId = containerId,
                Slot = slot,
            };

        // ------------------------------------------------ 1. the wire shape

        /// `struct`, not `class` — `IBroadcast` is an empty marker, so a
        /// `class` compiles here and only fails at FishNet's own
        /// `where T : struct` call sites. Same pin
        /// `SpectateTests.SpectateRequestNet_IsAStructImplementingIBroadcast`
        /// and `HandshakeTests.HandshakeStructs_AreStructsImplementingIBroadcast`
        /// already carry.
        [Test]
        public void LootStructs_AreStructsImplementingIBroadcast()
        {
            Assert.IsTrue(typeof(LootRequestNet).IsValueType, "LootRequestNet must be a struct");
            Assert.IsTrue(typeof(IBroadcast).IsAssignableFrom(typeof(LootRequestNet)),
                "LootRequestNet must implement IBroadcast");
            Assert.IsTrue(typeof(LootResultNet).IsValueType, "LootResultNet must be a struct");
            Assert.IsTrue(typeof(IBroadcast).IsAssignableFrom(typeof(LootResultNet)),
                "LootResultNet must implement IBroadcast");
        }

        /// `LootOp` rides `LootRequestNet.Op` as a byte from this task on, so
        /// its numbering became a wire contract here — the same pinning
        /// discipline `SpectateRefusal`, `HandshakeRefusal`, `MatchEndReason`
        /// and `MatchOutcome` already carry, and paid in the task that puts
        /// the domain on the wire rather than in a later one.
        [Test]
        public void LootOp_ValuesAreStableOnTheWire()
        {
            Assert.AreEqual(0, (byte)LootOp.Take);
            Assert.AreEqual(1, (byte)LootOp.Drop);
            Assert.AreEqual(2, (byte)LootOp.Use);
        }

        /// `LootRefusal` rides `LootResultNet.Code`, which its own doc has
        /// said since Т17. Appending is safe; renumbering is not — a client
        /// already in flight would light a different reason on the slot.
        [Test]
        public void LootRefusal_ValuesAreStableOnTheWire()
        {
            Assert.AreEqual(0, (byte)LootRefusal.None);
            Assert.AreEqual(1, (byte)LootRefusal.DeadOrExtracted);
            Assert.AreEqual(2, (byte)LootRefusal.WindowClosed);
            Assert.AreEqual(3, (byte)LootRefusal.UnknownOp);
            Assert.AreEqual(4, (byte)LootRefusal.NoSuchContainer);
            Assert.AreEqual(5, (byte)LootRefusal.SlotOutOfRange);
            Assert.AreEqual(6, (byte)LootRefusal.SlotEmpty);
            Assert.AreEqual(7, (byte)LootRefusal.InventoryIndexOutOfRange);
            Assert.AreEqual(8, (byte)LootRefusal.TooFar);
            Assert.AreEqual(9, (byte)LootRefusal.NotEnoughSlots);
            Assert.AreEqual(10, (byte)LootRefusal.Busy);
            Assert.AreEqual(11, (byte)LootRefusal.DashingOrSliding);
            Assert.AreEqual(12, (byte)LootRefusal.ItemNotUsable);
        }

        // ------------------------------------------- 2. the FishNet round trip

        /// The real wire, not a copy assignment: FishNet's IL post-processor
        /// has to have produced a writer and a reader for this struct, and
        /// every field has to survive them. Same shape and the same
        /// hand-driven serializer registration
        /// `ReconcileCodecTests.ReconcileData_SurvivesTheFishNetWireRoundTrip`
        /// uses (`TestSerializers.EnsureRegistered`, extracted for this second
        /// caller).
        ///
        /// EVERY FIELD IS NON-DEFAULT on the way in, deliberately: a writer
        /// that skipped a field entirely would round-trip a zero back into a
        /// zero and look correct.
        [Test]
        public void LootRequestNet_SurvivesTheFishNetWireRoundTrip()
        {
            TestSerializers.EnsureRegistered();

            var source = new LootRequestNet
            {
                MatchEpoch = 4919,
                Op = (byte)LootOp.Use,
                ContainerId = 123456,
                Slot = 7,
            };

            Assert.IsNotNull(GenericWriter<LootRequestNet>.Write,
                "FishNet's codegen must have produced a writer for LootRequestNet — without one "
                + "the request cannot leave the client at all");
            Assert.IsNotNull(GenericReader<LootRequestNet>.Read, "…and a matching reader");

            var writer = new Writer();
            writer.Write<LootRequestNet>(source);
            var reader = new Reader(writer.GetArraySegment(), null);
            LootRequestNet back = reader.Read<LootRequestNet>();

            Assert.AreEqual(source.MatchEpoch, back.MatchEpoch);
            Assert.AreEqual(source.Op, back.Op);
            Assert.AreEqual(source.ContainerId, back.ContainerId);
            Assert.AreEqual(source.Slot, back.Slot);
        }

        [Test]
        public void LootResultNet_SurvivesTheFishNetWireRoundTrip()
        {
            TestSerializers.EnsureRegistered();

            var source = new LootResultNet
            {
                MatchEpoch = 4919,
                Op = (byte)LootOp.Drop,
                ContainerId = 123456,
                Slot = 7,
                Code = (byte)LootRefusal.TooFar,
            };

            Assert.IsNotNull(GenericWriter<LootResultNet>.Write,
                "FishNet's codegen must have produced a writer for LootResultNet — without one "
                + "the refusal never reaches the slot the player pressed");
            Assert.IsNotNull(GenericReader<LootResultNet>.Read, "…and a matching reader");

            var writer = new Writer();
            writer.Write<LootResultNet>(source);
            var reader = new Reader(writer.GetArraySegment(), null);
            LootResultNet back = reader.Read<LootResultNet>();

            Assert.AreEqual(source.MatchEpoch, back.MatchEpoch);
            Assert.AreEqual(source.Op, back.Op);
            Assert.AreEqual(source.ContainerId, back.ContainerId);
            Assert.AreEqual(source.Slot, back.Slot);
            Assert.AreEqual(source.Code, back.Code);
        }

        // ------------------------------------------------- 3. the epoch gate

        /// Р237/Р292: a request left over from the match before must not be
        /// applied to this one. The gate answers with a VALUE — this wire
        /// never throws — and the positive witness beside it proves the
        /// fixture could have said yes.
        [Test]
        public void ForeignEpochRequest_IsIgnored()
        {
            LootRequestNet stale = Request(7, LootOp.Take, containerId: 11, slot: 0);
            LootRequestNet current = Request(9, LootOp.Take, containerId: 11, slot: 0);

            Assert.IsFalse(LootNet.IsCurrentEpoch(stale.MatchEpoch, matchEpoch: 9),
                "a request stamped with the previous match's epoch belongs to no live match");
            Assert.IsTrue(LootNet.IsCurrentEpoch(current.MatchEpoch, matchEpoch: 9),
                "the same gate must admit a request of the current epoch");
        }

        /// The same rule, the other direction (spec §3.8: "a request OR a
        /// reply"). One home, two callers — the server asks it of an arriving
        /// request, the client of an arriving reply.
        [Test]
        public void ForeignEpochReply_IsIgnoredByTheSameGate()
        {
            Assert.IsFalse(LootNet.IsCurrentEpoch(messageEpoch: 3, matchEpoch: 4));
            Assert.IsTrue(LootNet.IsCurrentEpoch(messageEpoch: 4, matchEpoch: 4));
        }

        /// Epoch 0 is reserved for "there is no epoch yet" — `ClientMatchLink`
        /// refuses a welcome carrying it, `MatchEpochCounter` mints 1 first
        /// and never returns 0. Without this term a reply that overtook the
        /// opening welcome would be admitted into a client that does not yet
        /// know which match it is in, exactly the case `SnapshotQueue.Admit`
        /// spells `!_hasEpoch`.
        [Test]
        public void ZeroCurrentEpoch_AdmitsNothing_NotEvenAZeroStampedMessage()
        {
            Assert.IsFalse(LootNet.IsCurrentEpoch(messageEpoch: 0, matchEpoch: 0),
                "no epoch tracked yet — a zero-stamped message must not be admitted by equality");
            Assert.IsFalse(LootNet.IsCurrentEpoch(messageEpoch: 1, matchEpoch: 0),
                "…and neither may a real epoch, before this end knows which match it is in");
            Assert.IsTrue(LootNet.IsCurrentEpoch(messageEpoch: 1, matchEpoch: 1),
                "the witness that the guard above is about the ZERO and not about equality");
        }

        // ---------------------------------------------- 4. the reply builder

        /// Spec §3.8/§3.11: without the echo two `Take`s on different slots
        /// give indistinguishable answers and "the refusal lights up on the
        /// slot the player pressed" is unimplementable. Non-default,
        /// mutually distinct values throughout, so a builder that copied the
        /// wrong field cannot pass by coincidence.
        [Test]
        public void ResultEchoesRequestAddress()
        {
            LootRequestNet request = Request(9, LootOp.Drop, containerId: 4242, slot: 5);

            LootResultNet result = LootNet.ResultFor(in request, LootRefusal.TooFar);

            Assert.AreEqual(request.MatchEpoch, result.MatchEpoch, "the epoch rides back too");
            Assert.AreEqual(request.Op, result.Op, "which of the three operations this answers");
            Assert.AreEqual(request.ContainerId, result.ContainerId, "the container half of the address");
            Assert.AreEqual(request.Slot, result.Slot, "the slot half — the one §3.11 lights up");
        }

        /// The other half of the reply: the verdict itself, on both sides of
        /// `None` so a builder that hard-coded either value is caught.
        [Test]
        public void ResultCarriesTheCodeItWasBuiltWith()
        {
            LootRequestNet request = Request(9, LootOp.Take, containerId: 4242, slot: 5);

            Assert.AreEqual((byte)LootRefusal.SlotEmpty,
                LootNet.ResultFor(in request, LootRefusal.SlotEmpty).Code,
                "a refusal travels as its own code");
            Assert.AreEqual((byte)LootRefusal.None,
                LootNet.ResultFor(in request, LootRefusal.None).Code,
                "and an accepted operation answers None — the client un-ghosts the slot on it");
        }

        // ------------------------------------ 4a. the outgoing request

        /// Р237/Р292 on the way OUT: the request carries the epoch of the
        /// match this link is actually in, and the link's own core is what
        /// stamps it (`ClientMatchLink.RequestLoot` only sends). Non-default,
        /// mutually distinct values so a builder that copied the wrong field
        /// cannot pass by coincidence.
        [Test]
        public void OutgoingRequestIsStampedWithTheLinksOwnEpoch()
        {
            var state = new ClientLinkState();
            Assert.IsTrue(state.TryBeginHello(), "premise: the first hello is allowed");
            Assert.AreEqual(ClientLinkState.LinkVerdict.Applied,
                state.OnWelcome(new MatchWelcomeNet { MatchEpoch = 41, Seed = 7L, PlayerIndex = 0 },
                    maxPlayers: 3).Verdict,
                "premise: the opening welcome is accepted");

            LootRequestNet request = state.LootRequestFor(LootOp.Use, containerId: 606, slot: 9);

            Assert.AreEqual(41, request.MatchEpoch,
                "the epoch is the link's own, not a caller's copy of it");
            Assert.AreEqual((byte)LootOp.Use, request.Op);
            Assert.AreEqual(606, request.ContainerId);
            Assert.AreEqual(9, request.Slot);
        }

        /// Before the welcome there is no match to name, and the stamp says
        /// so rather than the link inventing a phase test of its own: the far
        /// end's `LootNet.IsCurrentEpoch` refuses a zero-stamped request, and
        /// that is the one place the rule lives.
        [Test]
        public void OutgoingRequestBeforeTheWelcomeIsStampedZero()
        {
            var state = new ClientLinkState();

            Assert.AreEqual(0, state.LootRequestFor(LootOp.Take, containerId: 1, slot: 0).MatchEpoch);
            Assert.IsFalse(LootNet.IsCurrentEpoch(0, matchEpoch: 41),
                "…and a zero-stamped request is what the far end refuses");
        }

        // ------------------------------------------- 5. the world seam

        /// The positive witness for the whole seam: a legal request is
        /// accepted AND opens the transfer channel. It is also the witness
        /// that the input `TryBeginLoot` validates against is the world's own
        /// sanitized array — a seam handing `default(SimInput)` to Validate
        /// would answer `WindowClosed` here with everything else legal.
        [Test]
        public void LegalRequest_IsAcceptedAndOpensTheChannel()
        {
            var w = MakeWorld(out int containerId);
            TickWithWindowsOpen(w);

            LootRefusal code = w.TryBeginLoot(0, LootOp.Take, containerId, 0);

            Assert.AreEqual(LootRefusal.None, code,
                "a live collector in reach, window open in the last tick's input — a legal Take");
            Assert.Greater(w.PlayerAt(0).LootTimer, 0f,
                "an accepted request must actually open the channel, not merely answer None");
            Assert.AreEqual(containerId, w.PlayerAt(0).LootTargetContainerId,
                "…aimed at the container the request named, by id (Р266)");
        }

        /// Check 1 of `LootOps.Validate` (`LootOps.cs:99`), reached through
        /// the wire seam. The CODE is not new — `LootOpsTests.
        /// Validate_RefusesDeadPlayer` has owned it since Т17 — what is new,
        /// and what this test witnesses, is that a request arriving from the
        /// wire gets that code back instead of silence.
        [Test]
        public void RequestFromDeadPlayer_IsRefusedWithCode()
        {
            var w = MakeWorld(out int containerId);
            TickWithWindowsOpen(w);
            w.KillPlayerNoDamage(0);
            Assert.IsFalse(w.PlayerAt(0).Alive, "premise: the collector must actually be dead");

            LootRefusal code = w.TryBeginLoot(0, LootOp.Take, containerId, 0);

            Assert.AreEqual(LootRefusal.DeadOrExtracted, code);
            Assert.AreEqual(0f, w.PlayerAt(0).LootTimer, 0f,
                "a refused request must not open a channel a later tick would try to finish");
        }

        /// The OTHER half of check 1's `!Alive || Extracted`. `Alive` stays
        /// true on purpose: an extracted collector is not a corpse, and a
        /// seam that only looked at `Alive` would pass the test above and
        /// fail this one.
        [Test]
        public void RequestFromExtractedPlayer_IsRefusedWithCode()
        {
            var w = MakeWorld(out int containerId);
            TickWithWindowsOpen(w);
            PlayerState p = w.PlayerAt(0);
            p.Extracted = true;
            w.SetPlayerForTest(0, p);
            Assert.IsTrue(w.PlayerAt(0).Alive, "premise: extracted, but NOT dead");

            LootRefusal code = w.TryBeginLoot(0, LootOp.Take, containerId, 0);

            Assert.AreEqual(LootRefusal.DeadOrExtracted, code);
            Assert.AreEqual(0f, w.PlayerAt(0).LootTimer, 0f,
                "a refused request must not open a channel");
        }

        /// The seam validates against the REQUESTING player's own input, not
        /// against slot 0's (lesson 227 — the subject is the second element).
        /// Player 0 keeps the window shut, player 1 opens it; a seam reading
        /// index 0 would refuse player 1's perfectly legal request with
        /// `WindowClosed`.
        [Test]
        public void TheValidatedInputIsTheRequestingPlayersOwn()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg, 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(20f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 1, float2.zero);
            int containerId = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f),
                new byte[] { 1, 2 });

            w.TickAll(new[]
            {
                new SimInput(),                              // slot 0: window shut
                new SimInput { InventoryOpen = true },       // slot 1: window open
            });

            Assert.AreEqual(LootRefusal.None, w.TryBeginLoot(1, LootOp.Take, containerId, 0),
                "player 1 opened the window in the last tick — their own input is the one that counts");
            Assert.AreEqual(LootRefusal.WindowClosed, w.TryBeginLoot(0, LootOp.Take, containerId, 0),
                "player 0 did not — and reads their own input, not a neighbour's");
        }

        // ------------------------------------------ 6. the client's ghost

        static LootResultNet ReplyTo(LootOp op, int containerId, byte slot, LootRefusal code)
            => new LootResultNet
            {
                MatchEpoch = 9,
                Op = (byte)op,
                ContainerId = containerId,
                Slot = slot,
                Code = (byte)code,
            };

        [Test]
        public void Tracker_OpensOnARequestAndRemembersItsAddress()
        {
            var tracker = new LootRequestTracker();

            Assert.IsFalse(tracker.InFlight, "nothing is waited on before the first request");
            Assert.IsTrue(tracker.TryOpen(LootOp.Take, containerId: 77, slot: 3));

            Assert.IsTrue(tracker.InFlight, "the ghost is on while the answer is outstanding");
            Assert.AreEqual(77, tracker.ContainerId);
            Assert.AreEqual(3, tracker.Slot);
            Assert.AreEqual(LootOp.Take, tracker.Op);
        }

        /// The ghost names ONE slot. A second request while one is
        /// outstanding would either steal that name or need a second ghost
        /// the surface has no way to show.
        [Test]
        public void Tracker_RefusesASecondRequestWhileOneIsOutstanding()
        {
            var tracker = new LootRequestTracker();
            Assert.IsTrue(tracker.TryOpen(LootOp.Take, containerId: 77, slot: 3),
                "premise: the first request opens");

            Assert.IsFalse(tracker.TryOpen(LootOp.Drop, containerId: 0, slot: 1),
                "a second request while one is outstanding is refused");
            Assert.AreEqual(77, tracker.ContainerId, "…and the refused one changes nothing");
            Assert.AreEqual(3, tracker.Slot);
            Assert.AreEqual(LootOp.Take, tracker.Op);
        }

        /// Review Т28, I-2: the wire field is ONE byte, so a slot no byte can
        /// name must never open a wait. The concrete failure the rule
        /// prevents: `(byte)300` is 44, so the server would honestly operate
        /// on slot 44, echo 44, and `TryClose` — waiting on 300 — would refuse
        /// its own answer and latch the ghost over an operation that really
        /// happened.
        ///
        /// 255 IS THE POSITIVE WITNESS, and it is the boundary rather than a
        /// comfortable middle: a rule written `>= byte.MaxValue` would pass
        /// every other test in this file and fail only here.
        [Test]
        public void Tracker_RefusesASlotNoByteCanName()
        {
            var tracker = new LootRequestTracker();

            Assert.IsFalse(tracker.TryOpen(LootOp.Take, containerId: 77, slot: -1),
                "a negative index names nothing");
            Assert.IsFalse(tracker.TryOpen(LootOp.Take, containerId: 77, slot: 256),
                "…and one past what the wire byte can carry would be TRUNCATED into a stranger");
            Assert.IsFalse(tracker.InFlight, "neither refusal may leave a ghost behind");

            Assert.IsTrue(tracker.TryOpen(LootOp.Take, containerId: 77, slot: byte.MaxValue),
                "the last slot a byte CAN name is legal — the boundary is inclusive");
        }

        [Test]
        public void Tracker_ClosesOnTheMatchingAnswerAndKeepsTheAddress()
        {
            var tracker = new LootRequestTracker();
            tracker.TryOpen(LootOp.Take, containerId: 77, slot: 3);

            Assert.IsTrue(tracker.TryClose(ReplyTo(LootOp.Take, 77, 3, LootRefusal.SlotEmpty)));

            Assert.IsFalse(tracker.InFlight, "the ghost goes out when the answer arrives");
            Assert.AreEqual(LootRefusal.SlotEmpty, tracker.LastCode);
            Assert.AreEqual(77, tracker.ContainerId,
                "the address survives the answer — a refusal has to be shown on the slot it is about");
            Assert.AreEqual(3, tracker.Slot);
        }

        /// An accepted operation answers `None`, and the ghost goes out just
        /// the same — the positive witness that `TryClose` is not gated on
        /// the code being a refusal.
        [Test]
        public void Tracker_ClosesOnAnAcceptance()
        {
            var tracker = new LootRequestTracker();
            tracker.TryOpen(LootOp.Take, containerId: 77, slot: 3);

            Assert.IsTrue(tracker.TryClose(ReplyTo(LootOp.Take, 77, 3, LootRefusal.None)));
            Assert.IsFalse(tracker.InFlight);
            Assert.AreEqual(LootRefusal.None, tracker.LastCode);
        }

        [Test]
        public void Tracker_IgnoresAnAnswerAddressedElsewhere()
        {
            var tracker = new LootRequestTracker();
            tracker.TryOpen(LootOp.Take, containerId: 77, slot: 3);

            Assert.IsFalse(tracker.TryClose(ReplyTo(LootOp.Take, 78, 3, LootRefusal.SlotEmpty)),
                "another container — not this wait's answer");
            Assert.IsFalse(tracker.TryClose(ReplyTo(LootOp.Take, 77, 4, LootRefusal.SlotEmpty)),
                "another slot — not this wait's answer either");
            Assert.IsTrue(tracker.InFlight, "the ghost stays on: the real answer has not arrived");
            Assert.AreEqual(LootRefusal.None, tracker.LastCode,
                "and nothing was recorded from a reply that was not ours");
        }

        /// `Drop` and `Use` both carry container id 0 and a backpack index,
        /// so the operation is part of the address rather than decoration:
        /// without it a reply to an abandoned `Drop` would close a `Use`
        /// waiting on the same backpack slot.
        [Test]
        public void Tracker_IgnoresAnAnswerForADifferentOperation()
        {
            var tracker = new LootRequestTracker();
            tracker.TryOpen(LootOp.Use, containerId: 0, slot: 2);

            Assert.IsFalse(tracker.TryClose(ReplyTo(LootOp.Drop, 0, 2, LootRefusal.ItemNotUsable)),
                "same address, other operation — not this wait's answer");
            Assert.IsTrue(tracker.InFlight);

            Assert.IsTrue(tracker.TryClose(ReplyTo(LootOp.Use, 0, 2, LootRefusal.ItemNotUsable)),
                "the witness that the address itself was matching all along");
        }

        /// A reply arriving with nothing outstanding is refused, INCLUDING one
        /// addressed exactly like a tracker at rest.
        ///
        /// THE SECOND PROBE IS THE ONE THAT MATTERS, and it was written
        /// because a mutation asked for it (M17): a resting tracker holds
        /// `Take`, container 0, slot 0, so a probe on any other address is
        /// turned away by the address comparisons alone and says nothing at
        /// all about the `InFlight` guard. `Take`/0/0 is the one address that
        /// reaches it — the shape of a stale reply to a `Take` from the very
        /// first container of a match that has since been reset.
        [Test]
        public void Tracker_IgnoresAnAnswerWhenNothingIsOutstanding()
        {
            var tracker = new LootRequestTracker();

            Assert.IsFalse(tracker.TryClose(ReplyTo(LootOp.Take, 77, 3, LootRefusal.SlotEmpty)),
                "a duplicate reliable delivery, or one that outlived its request");
            Assert.IsFalse(tracker.TryClose(ReplyTo(LootOp.Take, 0, 0, LootRefusal.SlotEmpty)),
                "…and one whose address happens to equal what a resting tracker holds");
            Assert.AreEqual(LootRefusal.None, tracker.LastCode,
                "nothing outstanding means nothing to record — the verdict stays unset");

            tracker.TryOpen(LootOp.Take, containerId: 0, slot: 0);
            Assert.IsTrue(tracker.TryClose(ReplyTo(LootOp.Take, 0, 0, LootRefusal.SlotEmpty)),
                "the witness that the probe above is refused for being UNAWAITED, not for its address");
        }

        /// The one case the wire never answers: a request that reached no
        /// match at all. `NetworkSimBackend.SyncMatchEpoch` calls this on
        /// every epoch change, beside the spectate window and the mob-type
        /// memory it already drops there.
        [Test]
        public void Tracker_ResetForgetsTheGhost()
        {
            var tracker = new LootRequestTracker();
            tracker.TryOpen(LootOp.Take, containerId: 77, slot: 3);
            tracker.TryClose(ReplyTo(LootOp.Take, 77, 3, LootRefusal.TooFar));

            tracker.Reset();

            Assert.IsFalse(tracker.InFlight);
            Assert.AreEqual(0, tracker.ContainerId);
            Assert.AreEqual(0, tracker.Slot);
            Assert.AreEqual(LootRefusal.None, tracker.LastCode);
            Assert.IsTrue(tracker.TryOpen(LootOp.Drop, containerId: 0, slot: 1),
                "and a fresh request may open right after — Reset leaves nothing outstanding");
        }
    }
}
