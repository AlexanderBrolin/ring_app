namespace Ring.Networking.Protocol
{
    /// Reads back what SnapshotWriter produced (Stage 2 Task 26, spec §3.8,
    /// Р29/Р60/Р82). The byte layout is documented once, on SnapshotWriter;
    /// this class is its mirror and deliberately does not restate it.
    ///
    /// IT NEVER THROWS — ON ANY BYTE SEQUENCE (Р82). A truncated, empty,
    /// one-byte or entirely arbitrary payload is ORDINARY input on this path:
    /// UDP loss, an MTU cut, a peer of another version, a hostile client. It
    /// is not a programming error, so it is not signalled with an exception.
    /// Every read returns `bool`; on any malformed input the cursor moves to
    /// a sticky FAILED state, after which every read returns false and the
    /// position does not advance. Nothing partially parsed is ever handed
    /// back: an `out` value is assigned only on the success path, so a
    /// refused header yields zero epoch/tick/flags, never half-decoded ones.
    ///
    /// WHY THE COUNTERS LIVE HERE AND NOT IN `NetStats`. `SkippedBlockCount`,
    /// `Truncated` and `VersionMismatch` describe ONE parse. NetStats (Task
    /// 23) is per-connection and per-match, and has no field for any of them
    /// — deliberately: adding one now would be a field with no consumer,
    /// since the code that would feed it is Task 28 (server side) and Task 32
    /// (client side). Whoever consumes a reader is responsible for folding
    /// these into NetStats.
    ///
    /// UNKNOWN BLOCK KINDS ARE SKIPPED, NOT FAILURES (Р29). `TryReadBlock`
    /// takes the set of kinds the CALLER understands and walks past
    /// everything else, counting it. That is what lets a Stage 3/4 entity be
    /// added to the snapshot without a protocol bump, and it works only
    /// because a block declares its length in BYTES (see SnapshotWriter's
    /// doc) — a record count would be unusable for a kind whose record size
    /// the reader does not know.
    ///
    /// HEADER FIRST, ALWAYS. A block read before the header fails instead of
    /// parsing the header's own bytes as a block whose "kind" is the protocol
    /// version. This is caller sequencing rather than hostile input, and it
    /// is guarded anyway because the only alternative to a guard is undefined
    /// behaviour inside a parser of untrusted bytes — precisely what Р82
    /// rules out. The write side carries no mirror-image guard, and that
    /// asymmetry is intentional: SnapshotWriter has no untrusted-input path
    /// at all, and a misordered write is caught by any round-trip its caller
    /// runs.
    ///
    /// ZERO ALLOCATIONS: a `ref struct` over a caller-supplied
    /// `ReadOnlySpan<byte>`. A delivered payload is a SLICE of the caller's
    /// buffer, not a copy — it stays valid exactly as long as that buffer
    /// does, which for Task 32 is the duration of the broadcast handler.
    ///
    /// WHY ITS OWN LITTLE-ENDIAN PRIMITIVES: see SnapshotWriter's doc — the
    /// same reasoning, and the same "a third consumer is the threshold for a
    /// shared helper" rule.
    /// KNOWN FORMAT LIMIT — END OF DATA IS NOT DISTINGUISHABLE FROM A CUT
    /// ON A BLOCK BOUNDARY (fix-round finding F8). The frame carries no
    /// block COUNT, so a datagram cut exactly at a block boundary parses as
    /// a complete, shorter snapshot: `Failed` and `Truncated` both stay
    /// false and the missing blocks simply never arrive. Inside a block the
    /// cut IS caught; only the boundary case is silent. That is a deliberate
    /// trade — a count field would have to be back-patched after the last
    /// block, and a wrong count is a worse failure mode than a short read —
    /// but it means the RECEIVER (Task 32) must not treat "no failure" as
    /// "nothing missing": it has to check that the kinds it requires are
    /// actually present, and count their absence itself.
    ///
    /// FLAG SEMANTICS, PRECISELY (findings F9, F10). `Truncated` means "a
    /// field or payload ran past the end of the buffer" and covers BOTH an
    /// honestly short datagram AND a block header that declares a length it
    /// does not have — the second is a hostile input, not a lossy network,
    /// and a consumer that folds both into one "packet loss" counter will
    /// not see an attack (Task 28/32 may want to split them). `Failed`
    /// without either `Truncated` or `VersionMismatch` means a CALLER
    /// sequencing error (a block read before the header, or a second header
    /// read) — not bad bytes, a bug in the calling code.
    public ref struct SnapshotReader
    {
        readonly System.ReadOnlySpan<byte> _src;
        int _pos;
        bool _headerRead;
        bool _failed;
        bool _truncated;
        bool _versionMismatch;
        int _skippedBlockCount;

        public SnapshotReader(System.ReadOnlySpan<byte> source)
        {
            _src = source;
            _pos = 0;
            _headerRead = false;
            _failed = false;
            _truncated = false;
            _versionMismatch = false;
            _skippedBlockCount = 0;
        }

        /// Sticky: once true, every read returns false and the position
        /// stands still.
        public bool Failed => _failed;

        /// The payload ended in the middle of a field. Note that a payload
        /// ending exactly on a block boundary is a SHORTER WELL-FORMED
        /// snapshot, not a truncated one.
        public bool Truncated => _truncated;

        /// The first byte was not `ProtocolVersion.Current`. Checked before
        /// any other header field is touched, so this and `Truncated` are
        /// never both set by a header refusal.
        public bool VersionMismatch => _versionMismatch;

        /// Blocks walked past because the caller did not list their kind
        /// (Р29). Forward compatibility, not an error — `Failed` stays false.
        public int SkippedBlockCount => _skippedBlockCount;

        /// Reads the 8-byte header. False means refused: inspect
        /// `VersionMismatch` and `Truncated` for which. Callable once.
        public bool TryReadHeader(out ushort epoch, out uint tick, out byte flags)
        {
            epoch = 0;
            tick = 0;
            flags = 0;

            if (_failed) return false;
            if (_headerRead)
            {
                // A second header read is a sequencing error, not a rewind.
                _failed = true;
                return false;
            }

            if (!TryReadU8(out byte version)) return false;

            // BEFORE anything else is parsed. A payload from another protocol
            // revision may lay its remaining bytes out however it likes, so
            // decoding them and only then comparing versions would mean
            // interpreting bytes whose meaning is unknown by definition.
            if (version != ProtocolVersion.Current)
            {
                _versionMismatch = true;
                _failed = true;
                return false;
            }

            if (!TryReadU16(out ushort readEpoch)) return false;
            if (!TryReadU32(out uint readTick)) return false;
            if (!TryReadU8(out byte readFlags)) return false;

            epoch = readEpoch;
            tick = readTick;
            flags = readFlags;
            _headerRead = true;
            return true;
        }

        /// Delivers the next block whose kind appears in `knownKinds`,
        /// skipping and counting every block whose kind does not (Р29).
        ///
        /// False means "nothing more to deliver" and has two shapes the
        /// caller can tell apart: a clean end of the payload (`Failed` false)
        /// and a malformed one (`Failed` true, and `Truncated` true when the
        /// payload was cut mid-field).
        ///
        /// `payload` is a slice of the source buffer — no copy is made.
        public bool TryReadBlock(
            System.ReadOnlySpan<byte> knownKinds,
            out byte kind,
            out System.ReadOnlySpan<byte> payload)
        {
            kind = 0;
            payload = default;

            if (_failed) return false;
            if (!_headerRead)
            {
                _failed = true;
                return false;
            }

            while (_pos < _src.Length)
            {
                if (!TryReadU8(out byte readKind)) return false;
                if (!TryReadU16(out ushort payloadBytes)) return false;
                if (_src.Length - _pos < payloadBytes)
                {
                    FailTruncated();
                    return false;
                }

                System.ReadOnlySpan<byte> body = _src.Slice(_pos, payloadBytes);
                _pos += payloadBytes;

                if (IsKnown(knownKinds, readKind))
                {
                    kind = readKind;
                    payload = body;
                    return true;
                }

                _skippedBlockCount++;
            }

            return false;
        }

        static bool IsKnown(System.ReadOnlySpan<byte> knownKinds, byte kind)
        {
            for (int i = 0; i < knownKinds.Length; i++)
                if (knownKinds[i] == kind)
                    return true;
            return false;
        }

        void FailTruncated()
        {
            _failed = true;
            _truncated = true;
        }

        bool TryReadU8(out byte value)
        {
            value = 0;
            if (_src.Length - _pos < 1)
            {
                FailTruncated();
                return false;
            }

            value = _src[_pos];
            _pos += 1;
            return true;
        }

        bool TryReadU16(out ushort value)
        {
            value = 0;
            if (_src.Length - _pos < 2)
            {
                FailTruncated();
                return false;
            }

            value = (ushort)(_src[_pos] | (_src[_pos + 1] << 8));
            _pos += 2;
            return true;
        }

        bool TryReadU32(out uint value)
        {
            value = 0;
            if (_src.Length - _pos < 4)
            {
                FailTruncated();
                return false;
            }

            value = (uint)(_src[_pos]
                           | (_src[_pos + 1] << 8)
                           | (_src[_pos + 2] << 16)
                           | (_src[_pos + 3] << 24));
            _pos += 4;
            return true;
        }
    }
}
