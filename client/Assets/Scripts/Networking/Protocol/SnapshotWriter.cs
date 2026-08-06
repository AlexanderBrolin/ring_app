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
    /// byte per block. FACT CORRECTION (fix-round, re-measured rather than
    /// quoted from the brief, which got this wrong): the real format overhead
    /// is `HeaderBytes + 3 * blocks` = 8 + 15 = 23 B at the five blocks of
    /// spec §3.8, NOT "inside" that section's 14 B line item — it exceeds it
    /// by 9. The spec's 14 allowed only 6 B for all tags and counts, which is
    /// not enough for five one-byte tags either, so that line was never
    /// realizable in ANY encoding, including the plan's own kind+count. The
    /// consequence that matters is the worst case: 1043 - 14 + 23 = 1052 B
    /// against a `SnapshotMaxBytes` cap of 1000, so Task 28's truncation
    /// branch stays reachable and must be budgeted from THIS number, not from
    /// the spec's. The spec line goes to the Task 57 amendments.
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
    /// why SnapshotReader reports rather than throws (Р82). This asymmetry
    /// is INTRODUCED HERE, not inherited: `InputCodec` (Task 25) throws on
    /// BOTH sides (InputCodec.cs:104 and :158), which is right for it — its
    /// input is a fixed layout handed over by FishNet's codegen, not a
    /// datagram off the wire. (The brief claimed InputCodec as the precedent;
    /// it is not, and that was checked only in review.) Once Task 34 puts
    /// InputCodec on the receive path, its Decode-side throw becomes an
    /// untrusted-input throw and has to be revisited — tracked as its own bd
    /// issue rather than fixed from here, that task being closed.
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
    /// once on entry. PRECISION (fix-round): that difference is real only on
    /// the READ side, where `TryReadU8/U16/U32` check the remainder on every
    /// call. This writer's own `WriteU16`/`WriteU32` check nothing either —
    /// `Reserve` validates once per public method, exactly InputCodec's
    /// pattern, and the two bodies are byte-identical apart from the buffer
    /// being a field instead of a parameter. So the honest reason they are
    /// not shared is not a difference of contract but the rule against
    /// editing a closed task. The threshold at
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
        /// production, roughly 65x lower. Public so the guard below can be
        /// tested, NOT as a budgeting reference: Task 27 must budget against
        /// the frame cap minus this frame's other blocks, never against this
        /// constant, whose rejection branch is unreachable in production.
        public const int MaxBlockPayloadBytes = 65535;

        readonly System.Span<byte> _dst;
        int _pos;

        /// Room left, in bytes. Task 28's truncation branch has to decide
        /// "does the next block still fit" on every entity, and the only
        /// alternative to this accessor is catching the writer's own
        /// exception as control flow — which would make an ordinary,
        /// expected outcome (the frame is full, drop the far entities) look
        /// like the caller bug the throw is meant to signal.
        public int FreeBytes => _dst.Length - _pos;

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
