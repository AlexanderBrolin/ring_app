using FishNet.Broadcast;

namespace Ring.Networking.Protocol
{
    /// Client -> server, Channel.Reliable (spec §3.7 table Р27).
    ///
    /// The connection handshake's wire contract (Stage 2 Task 39, spec §3.10
    /// :642-651). Two structs, not one: the client cannot know MatchEpoch/
    /// Seed/PlayerIndex before the server answers, and the server never
    /// produces the client's own SimConfigHash — a single shared struct
    /// would force each side to fill the other's fields with zeros, and
    /// default(T) would then be indistinguishable from a real message.
    ///
    /// STRUCT IS MANDATORY, NOT STYLISTIC, for all three types below — same
    /// reasoning as `SnapshotBroadcast`'s own doc comment (Task 26): every
    /// FishNet broadcast API is constrained to `where T : struct, IBroadcast`
    /// (`ServerManager.Broadcast<T>`/`ClientManager.Broadcast<T>` and their
    /// overloads). `IBroadcast` is an empty marker, so a `class` here
    /// compiles fine and only breaks at the generic `Broadcast<T>`/
    /// `RegisterBroadcast<T>` call sites —
    /// `HandshakeTests.HandshakeStructs_AreStructsImplementingIBroadcast`
    /// pins the shape here instead, by the same pattern as
    /// `SnapshotCodecTests.SnapshotBroadcast_IsAStructImplementingIBroadcast`.
    ///
    /// Channel.Reliable for all three (spec §3.7 table Р27 — "Lifecycle"
    /// class messages ride Reliable). The channel is not encoded on the
    /// struct itself — `Channel` is a send-time argument on `Broadcast<T>`,
    /// not a wire field — so it is documented here, at the type, and
    /// enforced at the one call site that sends each type (`MatchHandshake`).
    ///
    /// `HandshakeRefusal` IS THE ONE VOCABULARY FOR EVERY REFUSAL REASON —
    /// both the version/balance checks this task owns AND MatchRoster's own
    /// `JoinRejection` (Task 38), mapped onto this enum by
    /// `HandshakeDecision.FromJoinRejection` so a client only ever has to
    /// understand ONE set of codes. It is deliberately NOT FishNet's own
    /// `KickReason` (`ServerManager.Kick`): that enum's members —
    /// Unset/ExploitAttempt/MalformedData/ExploitExcessiveData/
    /// ExcessiveData/UnexpectedProblem/UnusualActivity — are the package's
    /// own exploit/abuse vocabulary and have no member meaning "the two
    /// balance configs disagree" or "you are on the wrong protocol
    /// version" — reusing it would either misreport an ordinary balance
    /// drift as an exploit attempt or silently drop the real reason.
    ///
    /// `PlayerIndex` is `byte` — the arena never seats more players than
    /// `Arena.MaxPlayers` (3 today), so a wider type would only waste bytes
    /// on every future match. `MatchEpoch` is `ushort`, matching
    /// `SnapshotBroadcast.MatchEpoch` — one width for one concept across
    /// the wire protocol.
    public struct ClientHelloNet : IBroadcast
    {
        public byte ProtocolVersion;
        public ulong SimConfigHash;
        public string PlayerId;

        /// Compared against the roster's own token with a plain string
        /// equality (`MatchRoster.TryJoin`, Task 38). NOT constant-time: a
        /// timing side-channel on a token comparison is a real weakness in
        /// general, but closing it is explicitly deferred to milestone Э5
        /// (the spec's own security-hardening pass, brief §2.5) — recorded
        /// here so a future reader does not mistake the plain comparison
        /// for an oversight rather than a scoped decision.
        public string JoinToken;
    }

    /// Server -> client, accepted.
    public struct MatchWelcomeNet : IBroadcast
    {
        public ushort MatchEpoch;
        public long Seed;
        public byte PlayerIndex;
    }

    /// Server -> client, refused — sent BEFORE the disconnect (see
    /// `MatchHandshake`'s own doc for why the ordering and the
    /// `Disconnect(false)` argument both matter).
    public struct MatchRefusedNet : IBroadcast
    {
        public byte Reason;
    }

    /// THE SINGLE REFUSAL VOCABULARY (see `ClientHelloNet`'s own doc
    /// comment above for the two-struct/vocabulary reasoning — there is no
    /// separate file-level doc in C#, this type's doc carries it). Values
    /// are PINNED — this rides the wire as a single byte
    /// (`HandshakeRefusal_ValuesAreStableOnTheWire`), and reordering the
    /// members would silently change the meaning of a refusal code already
    /// in flight between a client build and a server build compiled from
    /// different sources.
    ///
    /// THIS IS A DIAGNOSTIC, NOT AN ANTI-CHEAT CHECK (spec §3.10 :643-645 —
    /// repeated here because it is the single most important fact about this
    /// whole handshake). A modified client can report ANY
    /// `SimConfigHash`/`ProtocolVersion` it likes; `SimConfigMismatch` only
    /// ever catches an HONEST client whose build disagrees with the
    /// server's balance data. Do not describe this enum, or any refusal it
    /// produces, with words like "verify/validate the client is
    /// legitimate", "anti-tamper" or "security check" — that framing is
    /// simply false and misleads whoever reads it next.
    ///
    /// `UnrecognizedRejection` IS APPENDED, NOT INSERTED (fix-round 1,
    /// N-2/N-3) — every member above it keeps its original numeric value;
    /// only this one is new. It is the fallback `HandshakeDecision.
    /// FromJoinRejection` reports for a `JoinRejection` byte it does not
    /// recognize, AND the value `MatchHandshake` substitutes when a join
    /// delegate reports "refused" but supplies a rejection code that
    /// decodes to `None`. Both are internal contract violations, not a
    /// legitimate refusal reason, and both are logged loudly
    /// (`MatchHandshake.Refuse`) rather than silently passed through as
    /// `None` — which would read to a human, and to a client build, as
    /// "not refused at all".
    public enum HandshakeRefusal : byte
    {
        None = 0,
        ProtocolVersionMismatch = 1,
        SimConfigMismatch = 2,
        UnknownPlayer = 3,
        BadToken = 4,
        DuplicatePlayer = 5,
        MatchFull = 6,
        MatchAlreadyStarted = 7,
        InvalidPlayerId = 8,
        UnrecognizedRejection = 9,
    }
}
