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
    /// SnapshotCodecTests.ProtocolVersion_Current_IsPinnedToFour pins the
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
    ///
    /// HISTORY — one line per break, so the reason is here and not in a log:
    ///   1 → 2 (Stage 2 Task 44a): the DOMAIN of `ProjectileEndKind` grew by
    ///   `HitPlayer` = 4 inside the existing `ProjectileEnded` block. That is
    ///   the rule above, not its exception: no new block kind was added, so
    ///   an older reader does not skip and count anything — it validates the
    ///   payload byte against its own `MaxProjectileEndKindValue` of 3 and
    ///   rejects the WHOLE event as `MalformedContent` (SnapshotEvents'
    ///   ProjectileEnded decoder). The handshake could not catch that on its
    ///   own either: `ProjectileEndKind` is not part of `SimConfigHash`, so
    ///   an old client would pass the config check and then silently lose
    ///   every PvP ending. The bump is what turns that into an honest
    ///   `HandshakeRefusal.ProtocolVersionMismatch`.
    ///
    ///   2 → 3 (Stage 3 Task 10, spec Р213/Р251): the DOMAIN of `MobType`
    ///   grew by `Elite` = 2 and `Director` = 3 inside the existing Mobs
    ///   block. Same rule as the 1 → 2 entry, not its exception: no new
    ///   block kind was added, so an older reader does not skip and count
    ///   anything — it validates the Mobs record's type nibble against its
    ///   own `SnapshotBlocks.MaxMobTypeValue` (now `Director`, was `Gunner`)
    ///   and rejects the WHOLE record as `MalformedContent`
    ///   (SnapshotBlocks.TryReadMobsBlock). The handshake could not catch
    ///   that on its own either: Elite's and Director's MobSimConfig
    ///   sections are deliberately NOT part of `SimConfigHash.Compute` yet
    ///   (owner decision R-17 — see SimConfig.Elite/Director's own doc; Т13
    ///   wires them), so an old client would pass the config check and then
    ///   silently misparse every Elite/Director Mobs record. The bump is
    ///   what turns that into an honest
    ///   `HandshakeRefusal.ProtocolVersionMismatch`.
    ///
    ///   2 → 3, SECOND REASON (Stage 3 Task 25, spec Р257) — recorded here
    ///   rather than left to the commit log, because by the rule above it
    ///   would have earned a bump of its own: the Liveness block's payload
    ///   WIDTH changed, 1 byte to 2, and width is named in that rule
    ///   explicitly. It rides the same 3 because both breaks land inside
    ///   Stage 3 with no build released between them — a version is a
    ///   promise to PEERS, and there is no peer that ever spoke "3 with a
    ///   one-byte liveness block". Adding five new block KINDS in the same
    ///   task needed no bump at all (Р282, the rule's own exception); the
    ///   widening is what would have.
    ///
    ///   3 → 4 (app-88jb Т6, spec §3.2): the DOMAIN of `MobAiState` grew by
    ///   `Downed` = 6 inside the existing Mobs block — a body tipped past
    ///   MobSimConfig.TiltFallAngle stops steering, striking and firing until
    ///   DownedSeconds is up, and that state rides the record's ai nibble like
    ///   any other. Same rule as both entries above, not its exception: no new
    ///   block kind was added, so an older reader does not skip and count
    ///   anything — it validates the ai nibble against its own
    ///   `SnapshotBlocks.MaxMobAiStateValue` (now `Downed`, was `Fire`) and
    ///   rejects the WHOLE Mobs BLOCK, not one record
    ///   (SnapshotBlocks.TryReadMobsBlock returns `MalformedContent` for the
    ///   block). ⚠ THAT IS WHY THIS IS NOT A DEBT TO PAY LATER: to an old
    ///   client every mob in the arena would simply vanish the first time any
    ///   one of them fell over. The handshake could not catch it either — the
    ///   tilt numbers reach SimConfigHash only with Т11a, and MobAiState is
    ///   not hashed there at all, so an old client would pass the config check
    ///   and then lose the Mobs block. The bump is what turns that into an
    ///   honest `HandshakeRefusal.ProtocolVersionMismatch`.
    ///
    ///   3 → 4, SECOND REASON RESERVED (app-88jb Т16): the `MaxAimHeight`
    ///   scale is retuned there, which changes the MEANING of the aim-height
    ///   byte already on the wire — by the rule above that is a break of its
    ///   own. It will ride THIS same 4 and needs no second bump, for the
    ///   reason the 2 → 3 SECOND REASON entry states in full: both breaks land
    ///   inside one unreleased epic, and there is no peer that ever spoke "4
    ///   with the old MaxAimHeight scale". ⚠ When Т16 lands, it appends its
    ///   sentence to THIS entry rather than bumping — that is the whole point
    ///   of reserving the place here.
    public static class ProtocolVersion
    {
        public const byte Current = 4;
    }
}
