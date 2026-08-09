using System;
using FishNet.Broadcast;
using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Networking.Server;
using Ring.Server;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 39 (spec §3.10 :642-651, §3.7 table Р27; plan Т39;
    /// task-39-brief.md §2.7). Tests the CORE decision logic only —
    /// `HandshakeDecision.Evaluate`/`FromJoinRejection` and the wire
    /// structs/enum in HandshakeNet.cs. `MatchHandshake` itself (the FishNet
    /// wiring — registration, sending, disconnect) is NOT covered here, by
    /// the same split as `MatchServer`/`SnapshotAssembler`/
    /// `PlayerNetworkController`: a live `NetworkManager` cannot be raised in
    /// EditMode, and R-COMPILE plus milestone В1 are that proof instead
    /// (brief §2.7's own paragraph, MatchHandshake's class doc).
    ///
    /// Every negative test carries a positive witness beside it (brief
    /// §2.8), so a mutation that always refuses (or always accepts) cannot
    /// survive unnoticed — see task-39-report.md's mutation table (M-1..M-13:
    /// M-8..M-11 added fix-round 1, M-12 added fix-round 2 (replacing M-9
    /// after the fix-round 2 K-1/N-1 ruling deleted the code M-9 targeted),
    /// M-13 added fix-round 3 (NNF-1, upper-bound negative witness)) for the
    /// full sweep this file was written against.
    public class HandshakeTests
    {
        // Two-source-of-numbers convention (Global Constraints): the
        // "expected" side of every fixture below is a REAL computed value —
        // ProtocolVersion.Current and SimConfigHash.Compute(TestConfigs.
        // Default()) — not a bare literal standing in for them, matching
        // MatchConfigTests.ArenaCap's own fixture-not-literal precedent.
        const byte ExpectedVersion = ProtocolVersion.Current;
        static readonly ulong ExpectedHash = SimConfigHash.Compute(TestConfigs.Default());

        static ClientHelloNet MatchingHello() => new ClientHelloNet
        {
            ProtocolVersion = ExpectedVersion,
            SimConfigHash = ExpectedHash,
            PlayerId = "p1",
            JoinToken = "t1",
        };

        // ------------------------------------------------------------------
        // HandshakeDecision.Evaluate — version/balance checks only (brief
        // §2.4's own doc: roster/token decisions belong to MatchRoster and
        // arrive through the join delegate, not through this method).
        // ------------------------------------------------------------------

        [Test]
        public void Evaluate_AcceptsMatchingVersionAndHash() // positive witness for 2-4 below
        {
            ClientHelloNet hello = MatchingHello();
            Assert.AreEqual(HandshakeRefusal.None,
                HandshakeDecision.Evaluate(in hello, ExpectedVersion, ExpectedHash));
        }

        [Test]
        public void Evaluate_RefusesProtocolVersionMismatch()
        {
            ClientHelloNet hello = MatchingHello();
            hello.ProtocolVersion = (byte)(ExpectedVersion + 1);
            Assert.AreEqual(HandshakeRefusal.ProtocolVersionMismatch,
                HandshakeDecision.Evaluate(in hello, ExpectedVersion, ExpectedHash));
            // Witness: the matching hello above is accepted. (No explicit
            // `in` here — MatchingHello() is an rvalue, and an explicit
            // `in` at the call site requires an addressable lvalue, CS8156;
            // the compiler still passes it by readonly reference under the
            // hood without the keyword.)
            ClientHelloNet witness = MatchingHello();
            Assert.AreEqual(HandshakeRefusal.None,
                HandshakeDecision.Evaluate(in witness, ExpectedVersion, ExpectedHash));
        }

        [Test]
        public void Evaluate_RefusesSimConfigMismatch()
        {
            ClientHelloNet hello = MatchingHello();
            hello.SimConfigHash = ExpectedHash ^ 1UL;
            Assert.AreEqual(HandshakeRefusal.SimConfigMismatch,
                HandshakeDecision.Evaluate(in hello, ExpectedVersion, ExpectedHash));
            // Witness: the matching hello above is accepted (see the
            // CS8156 note in Evaluate_RefusesProtocolVersionMismatch above).
            ClientHelloNet witness = MatchingHello();
            Assert.AreEqual(HandshakeRefusal.None,
                HandshakeDecision.Evaluate(in witness, ExpectedVersion, ExpectedHash));
        }

        [Test]
        public void Evaluate_ChecksVersionBeforeHash()
        {
            // Both wrong at once — a DISCRIMINATING assert on the CAUSE
            // (brief §2.4/§2.8), not merely "some refusal happened". Version
            // is checked first because a version mismatch means `hello`'s
            // OTHER fields may not even have been read the way they were
            // written, making a hash comparison meaningless (brief §2.4).
            ClientHelloNet hello = MatchingHello();
            hello.ProtocolVersion = (byte)(ExpectedVersion + 1);
            hello.SimConfigHash = ExpectedHash ^ 1UL;
            Assert.AreEqual(HandshakeRefusal.ProtocolVersionMismatch,
                HandshakeDecision.Evaluate(in hello, ExpectedVersion, ExpectedHash));
        }

        // ------------------------------------------------------------------
        // HandshakeDecision.FromJoinRejection — total mapping (brief §2.4's
        // own doc: "a JoinRejection with no case here is a bug, not a
        // pass").
        // ------------------------------------------------------------------

        [Test]
        public void FromJoinRejection_MapsEveryJoinRejectionValue()
        {
            // Every REAL current member (except None, covered separately
            // below) must map to its OWN, SAME-NAMED HandshakeRefusal — not
            // merely "some non-None value" (fix-round 1, I-2). AreNotEqual
            // (None) alone would pass a mapping that is a plain
            // renumbering of the two enums (e.g. JoinRejection.
            // MatchAlreadyStarted (1) -> HandshakeRefusal.SimConfigMismatch
            // (2)) or a straight permutation of two members with no count
            // change at all — both silently misreport WHY a join was
            // refused while still "not None". All six non-None
            // JoinRejection members are deliberately spelled identically in
            // HandshakeRefusal (HandshakeNet.cs), so a by-NAME assert is
            // exact, not approximate.
            foreach (JoinRejection value in Enum.GetValues(typeof(JoinRejection)))
            {
                if (value == JoinRejection.None) continue;
                HandshakeRefusal refusal = HandshakeDecision.FromJoinRejection((byte)value);
                Assert.AreNotEqual(HandshakeRefusal.None, refusal,
                    $"JoinRejection.{value} must not map to HandshakeRefusal.None.");
                Assert.AreEqual(value.ToString(), refusal.ToString(),
                    $"JoinRejection.{value} must map to HandshakeRefusal.{value} by name, "
                    + $"not to {refusal} — a mismatched-but-non-None mapping is still wrong.");
            }

            // Fix-round 1, N-2/N-3 (justification corrected fix-round 2,
            // NF-1): FromJoinRejection no longer THROWS on an unrecognized
            // code. A thrown exception inside a FishNet broadcast handler
            // (ServerManager.ParseReceived's dispatch) is caught two
            // DIFFERENT wrong ways depending on the build: in a DEVELOPMENT
            // build (Editor/BuildOptions.Development) it escapes with no
            // catch at all — silence, this handshake never gets to answer;
            // in the PRODUCTION build this project actually ships
            // (BuildCommands.BuildLinuxServer, which never sets
            // BuildOptions.Development) ParseReceived's try/catch turns ANY
            // exception into an immediate Kick(..., KickReason.
            // MalformedData, ...) — not silence, but a false accusation
            // against the client for a bug that is entirely server-side.
            // Neither is acceptable, so this maps to the dedicated
            // UnrecognizedRejection member instead — loud in EVERY build
            // (MatchHandshake.Refuse logs an error specifically for it)
            // without blaming the client. 255 is outside every current
            // JoinRejection value (0-6).
            Assert.AreEqual(HandshakeRefusal.UnrecognizedRejection,
                HandshakeDecision.FromJoinRejection(255),
                "an unrecognized rejection code must map to UnrecognizedRejection, not silently to None.");
        }

        [Test]
        public void FromJoinRejection_NoneMapsToNone() // boundary witness for the totality test above
        {
            Assert.AreEqual(HandshakeRefusal.None,
                HandshakeDecision.FromJoinRejection((byte)JoinRejection.None));
        }

        // ------------------------------------------------------------------
        // HandshakeDecision.SlotsFitOnTheWire (fix-round 2, K-1/N-1 ruling)
        // — the ONE-TIME construction-time precondition that replaced
        // fix-round 1's per-connection runtime guard in MatchHandshake.
        // OnClientHello. Testing this core function directly is what
        // closes mutation M-9 (task-39-report.md §4): fix-round 1's guard
        // lived entirely in untested wiring: this arithmetic does not.
        // ------------------------------------------------------------------

        [Test]
        public void SlotsFitOnTheWire_BoundaryValues()
        {
            // Negative: nonsensical regardless of wire width.
            Assert.IsFalse(HandshakeDecision.SlotsFitOnTheWire(-1),
                "a negative maxPlayers can never be a valid match shape.");
            // Zero: no seats at all — MatchRoster's own constructor already
            // refuses MaxPlayers < 1 for the same reason (Task 38); this
            // function cannot see that guard across the assembly boundary
            // (brief §2.2), so it restates the same bound independently.
            Assert.IsFalse(HandshakeDecision.SlotsFitOnTheWire(0),
                "zero seats is not a match this class should be constructed for.");
            // Witness: the smallest legitimate match — one seat, slot 0,
            // trivially a byte.
            Assert.IsTrue(HandshakeDecision.SlotsFitOnTheWire(1),
                "a one-seat match must be accepted.");
            // Comfortably inside range — highest slot is 254.
            Assert.IsTrue(HandshakeDecision.SlotsFitOnTheWire(255),
                "255 seats (highest slot 254) fits a byte comfortably.");
            // The exact edge: highest slot is 255, which IS byte.MaxValue —
            // still valid, this is the last maxPlayers that fits.
            Assert.IsTrue(HandshakeDecision.SlotsFitOnTheWire(256),
                "256 seats (highest slot 255 == byte.MaxValue) is the last value that still fits.");
            // Fix-round 3, NNF-1: the negative witness 256's own positive
            // result needs — the upper bound is the ONE reason this
            // function exists (narrowing to a byte), and without a value
            // just past it the bound itself is unpinned: neither raising
            // it by one nor deleting it changes any assertion above.
            Assert.IsFalse(HandshakeDecision.SlotsFitOnTheWire(257),
                "257 seats would need slot 256, one past byte.MaxValue.");
        }

        // ------------------------------------------------------------------
        // Wire shape (brief §2.3) — same pattern as SnapshotCodecTests.
        // SnapshotBroadcast_IsAStructImplementingIBroadcast (Task 26).
        // ------------------------------------------------------------------

        [Test]
        public void HandshakeStructs_AreStructsImplementingIBroadcast()
        {
            AssertIsBroadcastStruct(typeof(ClientHelloNet));
            AssertIsBroadcastStruct(typeof(MatchWelcomeNet));
            AssertIsBroadcastStruct(typeof(MatchRefusedNet));
        }

        static void AssertIsBroadcastStruct(Type t)
        {
            Assert.IsTrue(t.IsValueType, $"{t.Name} must be a struct — FishNet's Broadcast<T> is constrained to structs.");
            Assert.IsTrue(typeof(IBroadcast).IsAssignableFrom(t), $"{t.Name} must implement IBroadcast.");
        }

        [Test]
        public void HandshakeRefusal_ValuesAreStableOnTheWire()
        {
            // Pinned literals, not a re-derivation of the enum — a
            // reordering of the members would silently change the meaning
            // of a code already in flight between builds compiled from
            // different sources (HandshakeNet.cs's own doc).
            Assert.AreEqual(0, (byte)HandshakeRefusal.None);
            Assert.AreEqual(1, (byte)HandshakeRefusal.ProtocolVersionMismatch);
            Assert.AreEqual(2, (byte)HandshakeRefusal.SimConfigMismatch);
            Assert.AreEqual(3, (byte)HandshakeRefusal.UnknownPlayer);
            Assert.AreEqual(4, (byte)HandshakeRefusal.BadToken);
            Assert.AreEqual(5, (byte)HandshakeRefusal.DuplicatePlayer);
            Assert.AreEqual(6, (byte)HandshakeRefusal.MatchFull);
            Assert.AreEqual(7, (byte)HandshakeRefusal.MatchAlreadyStarted);
            Assert.AreEqual(8, (byte)HandshakeRefusal.InvalidPlayerId);
            // Fix-round 1, N-2/N-3: appended, not inserted — every member
            // above kept its original value (HandshakeNet.cs's own doc).
            Assert.AreEqual(9, (byte)HandshakeRefusal.UnrecognizedRejection);
        }
    }
}
