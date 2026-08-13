using FishNet.Broadcast;

namespace Ring.Networking.Protocol
{
    /// The envelope one server-to-client snapshot travels in (Stage 2 Task
    /// 26, spec §3.8, Р60). The bytes themselves are produced by
    /// SnapshotWriter and consumed by SnapshotReader; this type only carries
    /// them across FishNet.
    ///
    /// STRUCT IS MANDATORY, NOT STYLISTIC. Every FishNet broadcast API is
    /// constrained to `where T : struct, IBroadcast` (ServerManager.
    /// Broadcast.cs:118 and its overloads, ClientManager.Broadcast.cs:105 —
    /// Task 2's API notes §1). `IBroadcast` itself is an empty marker
    /// (Runtime/Broadcast/IBroadcast.cs:6), so declaring this a class
    /// compiles perfectly well and fails only at the generic call site,
    /// which is Task 36 (MatchServer's per-connection send) for the server
    /// and Task 32 for the client's registration — NOT Task 34, which owns
    /// prediction's ReplicateData/ReconcileData and never touches this type. SnapshotCodecTests.SnapshotBroadcast_IsAStructImplementing-
    /// IBroadcast moves that failure back here.
    ///
    /// `ArraySegment<byte>`, NEVER `byte[]`. Both are serialized by FishNet
    /// out of the box, but `byte[]` deserializes into a FRESH array on every
    /// message — thirty allocations per second per client. `ArraySegment
    /// <byte>` has `[DefaultWriter]`/`[DefaultReader]` pairs (Writer.cs:550,
    /// Reader.cs:618) that copy into, and slice straight out of, FishNet's
    /// own serializer buffers — `Writer._buffer` (Writer.cs:275-281) and
    /// `Reader._buffer` (Reader.cs:359), not the transport's (fix-round
    /// precision): no allocation on either side (Task 2 notes §2). The sending side (Task 28) keeps one preallocated buffer
    /// per connection and hands out `new ArraySegment<byte>(buffer, 0,
    /// writer.BytesWritten)`.
    ///
    /// TICK AND EPOCH ARE DUPLICATED ON PURPOSE. Both also sit in the first
    /// bytes of `Payload` (SnapshotWriter's header), and that redundancy is
    /// deliberate, not an oversight:
    ///   * the transport layer needs them BEFORE the payload is parsed —
    ///     stale-snapshot and duplicate-snapshot rejection (Р60, spec §3.7)
    ///     runs on arrival, ahead of any block walking, and must not pay for
    ///     a parse it may be about to throw away;
    ///   * the payload has to stand on its own for offline analysis: a
    ///     captured datagram, a recorded traffic dump or a Task 29 redundant
    ///     resend must be interpretable without the envelope that carried it.
    /// Six bytes per snapshot at 30 Hz is ~180 B/s against a ~34 KB/s budget
    /// (spec §3.8).
    public struct SnapshotBroadcast : IBroadcast
    {
        public uint Tick;
        public ushort MatchEpoch;
        public System.ArraySegment<byte> Payload;
    }
}
