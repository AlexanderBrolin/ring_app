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
    /// records as "NO PER-REASON REFUSAL COUNTER"). WHAT THE CLIENT CAN AND
    /// CANNOT SEE, STATED EXACTLY (corrected at the Ф8 phase gate — an
    /// earlier wording here claimed the accepted viewpoint "reaches the
    /// client", which overstates it): no field of the snapshot carries the
    /// viewpoint slot, so the only thing that changes on the wire is WHICH
    /// entities the frame contains. An accepted switch is therefore
    /// inferable from the picture; a refusal is indistinguishable from a
    /// switch whose effects have not arrived yet. Closing that gap — an
    /// explicit `ObservedIndex` echo, or a reply — belongs to Т47/Ф9 (spec
    /// §3.12), together with the client-side state it feeds; this task
    /// deliberately ships neither, and the phase gate records the open end
    /// rather than leaving Т47 to discover it.
    public struct SpectateRequestNet : IBroadcast
    {
        /// The player slot the sender wants to watch. Validated entirely
        /// server-side by `SpectatePolicy.Evaluate` — this struct carries no
        /// opinion about whether the index is even in range.
        public byte TargetIndex;
    }
}
