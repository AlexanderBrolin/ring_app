namespace Ring.Networking.Protocol
{
    /// Wire protocol revision of everything under Ring.Networking.Protocol
    /// (Stage 2 Task 26, spec §3.8, Р29).
    ///
    /// It lives in its own file, alone, because it has TWO independent
    /// readers: the first byte of every snapshot payload (SnapshotWriter /
    /// SnapshotReader below) and the connection handshake, where it is
    /// compared next to SimConfigHash (Task 39). Parking it inside either
    /// one would make the other's dependency look accidental.
    ///
    /// BUMP THIS ONLY DELIBERATELY. Client and server read the same constant
    /// from the same build, so a bump is invisible in a single-build test run
    /// and only shows up as "every snapshot refused" against an older peer.
    /// SnapshotCodecTests.ProtocolVersion_Current_IsPinnedToOne pins the
    /// literal for exactly that reason: the value cannot drift without a
    /// human editing a test that says, in words, that this is a
    /// compatibility break.
    ///
    /// A version bump is required whenever the MEANING of existing bytes
    /// changes (field order, field width, the meaning of a block kind or of
    /// a flags bit). ADDING a new block kind does NOT need one — that is the
    /// entire point of the tagged, length-prefixed block format documented on
    /// SnapshotWriter: an older reader skips a kind it does not know and
    /// counts it (Р29).
    public static class ProtocolVersion
    {
        public const byte Current = 1;
    }
}
