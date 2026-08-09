using FishNet.Broadcast;

namespace Ring.Networking.Protocol
{
    /// The connection handshake's wire contract (Stage 2 Task 39, spec §3.10
    /// :642-651, §3.7 table Р27). Two structs, not one: the client cannot
    /// know MatchEpoch/Seed/PlayerIndex before the server answers, and the
    /// server never produces the client's own SimConfigHash — a single
    /// shared struct would force each side to fill the other's fields with
    /// zeros, and default(T) would then be indistinguishable from a real
    /// message.
    ///
    /// STRUCT IS MANDATORY, NOT STYLISTIC, for all three types below — same
    /// reasoning as SnapshotBroadcast.cs:10-19 (Task 26): every FishNet
    /// broadcast API is constrained to `where T : struct, IBroadcast`
    /// (ServerManager.Broadcast.cs:118 and its overloads,
    /// ClientManager.Broadcast.cs:105 — Task 2 notes §1, re-verified Task
    /// 39). `IBroadcast` is an empty marker (Runtime/Broadcast/IBroadcast.
    /// cs:6), so a `class` here compiles fine and only breaks at the
    /// generic Broadcast&lt;T&gt;/RegisterBroadcast&lt;T&gt; call sites —
    /// HandshakeTests.HandshakeStructs_AreStructsImplementingIBroadcast
    /// pins the shape here instead, by the same pattern as
    /// SnapshotCodecTests.SnapshotBroadcast_IsAStructImplementingIBroadcast.
    ///
    /// Channel.Reliable for all three (spec §3.7 table Р27 — "Lifecycle"
    /// class messages ride Reliable). The channel is not encoded on the
    /// struct itself — FishNet.Transporting.Channel is a send-time argument
    /// on Broadcast&lt;T&gt;, not a wire field — so it is documented here,
    /// at the type, and enforced at the one call site that sends each type
    /// (MatchHandshake).
    ///
    /// `HandshakeRefusal` IS THE ONE VOCABULARY FOR EVERY REFUSAL REASON —
    /// both the version/balance checks this task owns AND MatchRoster's own
    /// `JoinRejection` (Task 38), mapped onto this enum by
    /// HandshakeDecision.FromJoinRejection so a client only ever has to
    /// understand ONE set of codes. It is deliberately NOT FishNet's own
    /// `KickReason` (ServerManager.QOL.cs:181): that enum's members —
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
    /// SnapshotBroadcast.cs:47 — one width for one concept across the wire
    /// protocol.
    public struct ClientHelloNet : IBroadcast
    {
        public byte ProtocolVersion;
        public ulong SimConfigHash;
        public string PlayerId;
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
    /// MatchHandshake's own doc for why the ordering and the
    /// `Disconnect(false)` argument both matter).
    public struct MatchRefusedNet : IBroadcast
    {
        public byte Reason;
    }

    /// THE SINGLE REFUSAL VOCABULARY (see the file doc above). Values are
    /// PINNED — this rides the wire as a single byte
    /// (HandshakeRefusal_ValuesAreStableOnTheWire), and reordering the
    /// members would silently change the meaning of a refusal code already
    /// in flight between a client build and a server build compiled from
    /// different sources.
    ///
    /// THIS IS A DIAGNOSTIC, NOT AN ANTI-CHEAT CHECK (spec §3.10 :643-645,
    /// brief §2.5 — repeated here because it is the single most important
    /// fact about this whole handshake). A modified client can report
    /// ANY `SimConfigHash`/`ProtocolVersion` it likes; `SimConfigMismatch`
    /// only ever catches an HONEST client whose build disagrees with the
    /// server's balance data. Do not describe this enum, or any refusal it
    /// produces, with words like "verify/validate the client is
    /// legitimate", "anti-tamper" or "security check" — that framing is
    /// simply false and misleads whoever reads it next.
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
    }
}
