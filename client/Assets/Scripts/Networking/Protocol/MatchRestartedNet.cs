using FishNet.Broadcast;

namespace Ring.Networking.Protocol
{
    /// Server -> client, Channel.Reliable (spec §3.7 table Р27, "Lifecycle"):
    /// a new match is starting under a new epoch (Stage 2 Task 40, spec §3.10
    /// Р44/Р60, §6k Р164).
    ///
    /// THIS MESSAGE IS THE ONLY THING THAT CARRIES THE RIGHT TO RESET. The
    /// client's three epoch-aware seams — `EventDedup`, `SnapshotQueue` and
    /// `RenderClock` — each track exactly one epoch and REFUSE a frame naming
    /// any other (`SnapshotQueue.Admit` answers `ForeignEpoch` and changes
    /// nothing). The other three per-match seams `ClientMatchReset` clears
    /// (`GhostProjectiles`, `StalePolicy` and, since Task 44b,
    /// `ClientEventQueue`) track no epoch at all and are cleared outright.
    /// Only `Reset`/`ResetForEpoch` switches the tracked
    /// epoch, and this is the message that calls for it
    /// (`ClientMatchReset`). That is why the server sends this BEFORE its
    /// first snapshot of the new epoch: a frame that arrives ahead of the
    /// right to reset is discarded, and the few opening frames a `Reliable`
    /// message can still lose the race to are absorbed by the interpolation
    /// buffer — the accepted cost §6k Р164 names.
    ///
    /// NO `PlayerIndex`, AND THAT IS A DECISION, NOT AN OMISSION. This is
    /// `MatchWelcomeNet` minus that field: §6k Р164 keeps the roster and the
    /// handshake untouched across a restart, so slots survive it and there is
    /// nothing to reassign. A client already knows which index it is; being
    /// told again would only invite the question of what to do when the two
    /// answers differ.
    ///
    /// STRUCT IS MANDATORY, NOT STYLISTIC — see `MatchEndedNet`'s own doc for
    /// the full reasoning and the test that pins it.
    public struct MatchRestartedNet : IBroadcast
    {
        /// The NEW epoch, freshly minted (`MatchEpochCounter`) — `ushort`,
        /// the same width the rest of the protocol uses for this one concept
        /// (Р163а).
        public ushort MatchEpoch;

        /// The new match's seed, the same value the server hands
        /// `SimulationWorld` — carried for the same reason `MatchWelcomeNet`
        /// carries it: a client that logs or reproduces a match needs it, and
        /// there is no second channel that would deliver it.
        public long Seed;
    }
}
