namespace Ring.Networking.Protocol
{
    /// Serializes the snapshot FRAME into a caller-owned buffer (Stage 2 Task
    /// 26, spec §3.8, Р29/Р60/Р101). Task 26 delivers the frame only — the
    /// header and the tagged block machinery; the state blocks that fill it
    /// (players, liveness bits, mobs, wave, events) are Task 27, the
    /// per-connection assembly is Task 28, redundancy is Task 29 and the
    /// receiving end is Task 32.
    ///
    /// WIRE LAYOUT (offsets in bytes; every multi-byte field is
    /// little-endian, written by hand rather than through
    /// `System.BitConverter` so the layout does not depend on host
    /// endianness — same reasoning as InputCodec, Task 25):
    ///
    ///   header, 8 bytes, exactly once, first:
    ///     [0]     version        u8   — ProtocolVersion.Current
    ///     [1..2]  epoch          u16  — MatchEpoch (Р60)
    ///     [3..6]  tick           u32  — server simulation tick
    ///     [7]     flags          u8   — RESERVED, see below
    ///   then zero or more blocks, back to back, no padding:
    ///     [0]     kind           u8   — block kind tag
    ///     [1..2]  payloadBytes   u16  — LENGTH IN BYTES of what follows
    ///     [3..]   payload        payloadBytes bytes, opaque here
    ///
    /// LENGTH IN BYTES, NOT A RECORD COUNT (decision, task-26-brief §2.1 —
    /// a deliberate departure from the plan's "blockKind u8 + count", which
    /// is recorded here and in the task's decision log). Forward
    /// compatibility (Р29) requires a reader to STEP OVER a block whose kind
    /// it has never heard of. From a record count that is only possible if
    /// the reader knows the record size — that is, only for kinds it already
    /// knows, which is precisely the case that needs no skipping. A byte
    /// length makes the skip pure arithmetic, independent of semantics. Where
    /// a kind does have a record count, that count is the FIRST FIELD INSIDE
    /// its payload (Task 27), not part of this header. The price is one extra
    /// byte per block — 5 bytes at the five blocks of spec §3.8, inside its
    /// own "header ~14 B" line item.
    ///
    /// `flags` IS RESERVED AND NO BIT IS ASSIGNED. It is written and read
    /// verbatim so the field exists on the wire from version 1 onward, but
    /// Tasks 27-29 own its meaning (a "snapshot truncated" bit is the
    /// expected first tenant). Assigning a bit here, with no producer and no
    /// consumer, would be a feature for its own sake (AGENT.md rule 3).
    /// Callers in Task 26 pass 0.
    ///
    /// THE WRITE SIDE THROWS; THE READ SIDE NEVER DOES. Running out of room
    /// is a bug in the CALLER — Task 28 owns the byte budget and must respect
    /// `SnapshotMaxBytes` (Р101), so a silent partial frame would be a
    /// corrupted snapshot nobody noticed. A short or malformed INPUT, by
    /// contrast, is ordinary traffic (loss, MTU, a hostile client), which is
    /// why SnapshotReader reports rather than throws (Р82). Exactly the
    /// asymmetry InputCodec established in Task 25.
    ///
    /// NOTHING IS WRITTEN UNTIL EVERYTHING FITS. Both write methods check
    /// the remaining room before touching a single byte, so a rejected call
    /// leaves the buffer bit-for-bit as it was and the caller may keep and
    /// send the shorter valid frame it had already built.
    ///
    /// ZERO ALLOCATIONS. A `ref struct` over a caller-supplied `Span<byte>`:
    /// no buffer of its own, no pooling, nothing on the heap. Task 28 keeps
    /// one preallocated `byte[SnapshotMaxBytes]` per connection (the ceiling
    /// is fixed, so no pool is needed) and wraps `BytesWritten` of it into
    /// `SnapshotBroadcast.Payload`.
    ///
    /// WHY ITS OWN LITTLE-ENDIAN PRIMITIVES (carryover from Task 25, finding
    /// F11). `InputCodec` has private `WriteU16`/`ReadU16` of its own, and
    /// they are NOT reused here — nor is InputCodec edited to share them,
    /// that task being closed. The contracts genuinely differ: InputCodec
    /// serializes a FIXED, fully known 8-byte layout and validates the length
    /// once on entry, so its primitives need no per-operation check; this
    /// class writes a VARIABLE-LENGTH stream where every operation must first
    /// establish that the room exists. Two small primitives with different
    /// preconditions are not duplication of a single one. The threshold at
    /// which they should be lifted into a shared helper is a THIRD consumer:
    /// with two, a shared helper would have to satisfy both contracts and
    /// would end up being the weaker of them.
    public ref struct SnapshotWriter
    {
        public const int HeaderBytes = 8;
        public const int BlockHeaderBytes = 3;

        /// A block payload is described by a u16, so this is the hard
        /// ceiling on one block — far above `SnapshotMaxBytes` (1000 by
        /// default, Р101), which is the ceiling that actually binds in
        /// production. Kept public because Task 27's per-kind caps are
        /// budgeted against it.
        public const int MaxBlockPayloadBytes = 65535;

        readonly System.Span<byte> _dst;
        int _pos;

        public SnapshotWriter(System.Span<byte> destination)
        {
            _dst = destination;
            _pos = 0;
        }

        /// Bytes of `destination` used so far — the length Task 28 wraps into
        /// `SnapshotBroadcast.Payload`.
        public int BytesWritten => _pos;

        /// Writes the 8-byte header. `flags` is reserved (see the class doc);
        /// pass 0 until a bit is assigned.
        public void WriteHeader(ushort epoch, uint tick, byte flags)
        {
            Reserve(HeaderBytes);

            _dst[_pos] = ProtocolVersion.Current;
            WriteU16(_pos + 1, epoch);
            WriteU32(_pos + 3, tick);
            _dst[_pos + 7] = flags;
            _pos += HeaderBytes;
        }

        /// Appends one tagged block. `payload` is opaque here: its shape is
        /// the business of whoever owns `kind` (Task 27).
        public void WriteBlock(byte kind, System.ReadOnlySpan<byte> payload)
        {
            if (payload.Length > MaxBlockPayloadBytes)
                throw new System.ArgumentException(
                    $"SnapshotWriter.WriteBlock: payload of {payload.Length} bytes exceeds "
                    + $"MaxBlockPayloadBytes ({MaxBlockPayloadBytes}); the length field is u16.",
                    nameof(payload));

            Reserve(BlockHeaderBytes + payload.Length);

            _dst[_pos] = kind;
            WriteU16(_pos + 1, (ushort)payload.Length);
            payload.CopyTo(_dst.Slice(_pos + BlockHeaderBytes, payload.Length));
            _pos += BlockHeaderBytes + payload.Length;
        }

        /// Throws unless `bytes` more bytes fit — BEFORE anything is written,
        /// so a rejected call leaves the buffer untouched. `InvalidOperation-
        /// Exception` rather than `ArgumentException`: what is at fault is
        /// the writer's remaining room, not any argument of the call that
        /// discovers it (`WriteBlock`'s oversized-payload check above IS an
        /// argument fault, and throws accordingly).
        void Reserve(int bytes)
        {
            int free = _dst.Length - _pos;
            if (free < bytes)
                throw new System.InvalidOperationException(
                    $"SnapshotWriter: {bytes} bytes needed, {free} left of {_dst.Length}. "
                    + "The caller owns the snapshot byte budget (Р101).");
        }

        void WriteU16(int offset, ushort value)
        {
            _dst[offset] = (byte)(value & 0xFF);
            _dst[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        void WriteU32(int offset, uint value)
        {
            _dst[offset] = (byte)(value & 0xFF);
            _dst[offset + 1] = (byte)((value >> 8) & 0xFF);
            _dst[offset + 2] = (byte)((value >> 16) & 0xFF);
            _dst[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
