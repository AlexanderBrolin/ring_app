using FishNet.Broadcast;

namespace Ring.Networking.Protocol
{
    /// Server -> client, Channel.Reliable (spec §3.7 table Р27, "Lifecycle"):
    /// the match is over, here is how it went (Stage 2 Task 40, spec §3.10).
    ///
    /// ONE MESSAGE PER CONNECTION, NOT ONE FOR EVERYONE. Spec §3.10 asks for
    /// PERSONAL statistics alongside the world-scoped ones, so `MatchServer`
    /// builds a separate instance for every live connection out of that
    /// player's own slot in the match summary. A single broadcast to all would
    /// either have to carry every player's numbers (handing each client the
    /// others' shooting accuracy for nothing) or drop the personal half
    /// entirely.
    ///
    /// STRUCT IS MANDATORY, NOT STYLISTIC — same reasoning as
    /// `SnapshotBroadcast`/`HandshakeNet`: every FishNet broadcast API is
    /// constrained to `where T : struct, IBroadcast`, and `IBroadcast` is an
    /// empty marker, so a `class` here compiles fine and breaks only at the
    /// generic `Broadcast<T>` call site inside `MatchServer`.
    /// `MatchLifecycleTests.LifecycleStructs_AreStructsImplementingIBroadcast`
    /// moves that failure back here.
    ///
    /// THE STATISTICS ARE FLAT FIELDS, NOT AN EMBEDDED `MatchStats`/
    /// `WorldStats`. Those two are `Ring.Simulation.Core` types — simulation
    /// state, whose field list is owned by the simulation and is free to grow
    /// for reasons that have nothing to do with the wire. Embedding them would
    /// silently enlist FishNet's serializer codegen for a simulation type and
    /// make every future simulation field an unreviewed protocol change;
    /// copying the named numbers across at the one place they are read
    /// (`MatchServer`'s summary) keeps the protocol's own surface explicit.
    /// The numbers themselves are never recomputed or "improved" here — they
    /// are exactly what `SimulationWorld.StatsAt`/`WorldStats` report.
    ///
    /// NO QUANTIZATION, DELIBERATELY. This travels once per match on the
    /// Reliable channel; the snapshot byte budget (spec §3.8) is a per-tick
    /// concern and has nothing to say about a single lifecycle message, so
    /// packing these counters would buy nothing and cost the ability to read
    /// them straight off the wire.
    public struct MatchEndedNet : IBroadcast
    {
        /// `MatchEndReason` as a byte — see that enum's own doc for why the
        /// values are pinned.
        public byte Reason;

        /// The epoch of the match that just ended (`ushort`, the same width
        /// `MatchWelcomeNet`/`SnapshotBroadcast`/`MatchRestartedNet` all use —
        /// Р163а: one width for one concept across the whole protocol), so a
        /// client can discard a message belonging to a match it is no longer
        /// in.
        public ushort MatchEpoch;

        /// The world tick the match ended on.
        public uint FinalTick;

        // This connection's own player, straight out of its MatchStats slot.
        public int Kills;
        public int HeadshotKills;
        public int ShotsFired;
        public int ShotsHit;
        public int DashesUsed;
        public int SlidesUsed;
        public int DeathTick;
        public float DamageTaken;

        // World-scoped, identical in every copy of this message (WorldStats).
        public int WavesCleared;
        public int MobSpawnsSkipped;
        public int ProjectileSpawnsSkipped;
    }
}
