using FishNet.Broadcast;

namespace Ring.Networking.Protocol
{
    /// Client -> server, Channel.Reliable (spec §3.7 table Р27 — lifecycle).
    ///
    /// A dead player asking the server to change who it watches (Stage 2 Task
    /// 42a, spec §3.10 :673-678, Р70). `MatchServer.OnSpectateRequest` is the
    /// only handler; `SpectatePolicy` decides whether the switch is accepted.
    ///
    /// `struct`, NOT `class` — the same constraint every FishNet broadcast
    /// obeys (`where T : struct, IBroadcast`, ServerManager.Broadcast/
    /// RegisterBroadcast). `IBroadcast` is an empty marker, so a `class` here
    /// would compile and only fail at the generic call site —
    /// `SpectateTests.SpectateRequestNet_IsAStructImplementingIBroadcast`
    /// pins the shape here instead, the same pattern
    /// `HandshakeTests.HandshakeStructs_AreStructsImplementingIBroadcast` and
    /// `SnapshotCodecTests.SnapshotBroadcast_IsAStructImplementingIBroadcast`
    /// already use.
    ///
    /// `byte` — the same width as `MatchWelcomeNet.PlayerIndex`
    /// (HandshakeNet.cs): one player-slot concept, one width, through the
    /// whole protocol.
    ///
    /// THERE IS NO REPLY MESSAGE. A refusal is logged server-side
    /// (`MatchServer.OnSpectateRequest`'s own doc) and nothing more —
    /// neither an explicit refused-broadcast nor a refusal counter has a
    /// consumer today (AGENT.md rule 3; the same choice `MatchHandshake`
    /// records as "NO PER-REASON REFUSAL COUNTER"). The accepted viewpoint
    /// itself reaches the client already, riding the ordinary snapshot the
    /// next tick sends it (`SnapshotAssembler.BuildFor`'s `viewpointIndex`) —
    /// an explicit `ObservedIndex` echo is Т47/Ф9, spec §3.12 :815-820, not
    /// this task's job.
    public struct SpectateRequestNet : IBroadcast
    {
        /// The player slot the sender wants to watch. Validated entirely
        /// server-side by `SpectatePolicy.Evaluate` — this struct carries no
        /// opinion about whether the index is even in range.
        public byte TargetIndex;
    }
}
