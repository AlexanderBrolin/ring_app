using Ring.Simulation.Core;

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
    /// by 9. The spec's 14 left 6 B for all tags and counts of five blocks:
    /// enough for five bare 1-byte tags with a byte to spare, but not for
    /// tags PLUS any per-block count or length — the plan's own kind+count
    /// needs 10 B, this format needs 15. So that line was never realizable
    /// in any encoding that can actually delimit blocks (precision fixed in
    /// a later round: the first wording claimed five tags alone did not
    /// fit, which is arithmetically false). The
    /// consequence that matters is the worst case, and the live figure for it
    /// is the one right below, not the 1052 B this paragraph used to end on.
    /// The spec line goes to the Task 57 amendments.
    ///
    /// WORST-CASE FRAME SIZE — TASK 28'S BUDGET INPUT, 1116 B. Recomputed in
    /// Task 27 from the five calculators below, not quoted: Task 27 is the
    /// first place where a real event RECORD exists, so every earlier figure
    /// predates the thing being measured. Spec §3.8 said 1043 B (header 14 B,
    /// event ~9 B); Task 26 corrected the format overhead and got 1052 B while
    /// leaving the spec's event estimate untouched. Neither was dishonest —
    /// both were computed before the record they depend on existed. At the
    /// shipped caps (`MaxPlayers` 3, so 2 OTHER players; `MaxMobs` 96; a
    /// 16-event `SnapshotEventBudget` at an assumed 4 B of payload each):
    ///
    ///   HeaderBytes                                    8
    ///   PlayersBlockBytes(2)                          19
    ///   LivenessBlockBytes()                           4
    ///   MobsBlockBytes(96)                           867
    ///   WaveBlockBytes()                               7
    ///   EventsBlockBytes(16, 16*4)                   211
    ///   -----------------------------------------------
    ///   total                                       1116
    ///
    /// against `SnapshotMaxBytes` 1000 (Р101, NetConfig) — 116 B OVER the cap,
    /// so Task 28's truncation branch is not an edge case but the ordinary
    /// shape of a full frame. Mobs alone is 867 B; with the 8 B header that
    /// leaves 125 B for everything else, of which Players+Liveness+Wave take
    /// 30 B — about 95 B for events, roughly SEVEN records at 13 B each, not
    /// the sixteen the raw `SnapshotEventBudget` config field suggests.
    ///
    /// THE 4 B/EVENT PAYLOAD IS AN ASSUMPTION, NOT A MEASUREMENT — the event
    /// catalog is Task 28's (Task 27 leaves the payload opaque), so a
    /// different payload size changes the seven. RECOMPUTE FROM THE
    /// CALCULATORS, DO NOT RE-QUOTE THIS COMMENT: the mistake this paragraph
    /// exists to correct is exactly the one it will itself commit the moment
    /// anything below changes.
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
    /// NOTHING IS WRITTEN UNTIL EVERYTHING FITS. Every write method — the
    /// two of Task 26 and the five block methods of Task 27 — validates its
    /// arguments and then reserves the room it needs before touching a single
    /// byte, so a rejected call leaves the buffer bit-for-bit as it was and
    /// the caller may keep and send the shorter valid frame it had already
    /// built.
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
        /// tested, NOT as a budgeting reference: the frame's budget owner —
        /// Task 28, not Task 27, which turned out to write blocks and budget
        /// nothing — must budget against the frame cap minus this frame's
        /// other blocks, never against this constant, whose rejection branch
        /// is unreachable in production.
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

        // ---- Stage 2 Task 27: the five state blocks (spec §3.8, §3.12
        // Р68, task-27-brief §2.2-§2.15). Each method appends ONE tagged
        // block built from caller-owned records, quantizing through
        // Quantize with `cfg` as a parameter (no formula lives twice — rule
        // 2). Reading is the mirror-image static side on SnapshotBlocks
        // (task-27-brief §2.10: these use `_pos`, the read side does not).
        //
        // NOTHING IS WRITTEN UNTIL EVERYTHING FITS, same discipline as
        // WriteHeader/WriteBlock: any argument-shape problem (too many
        // records for the u16 length field, an event payload outside its
        // pool) is checked and thrown BEFORE `Reserve` is even called, and
        // `Reserve` itself throws before a single byte moves.

        /// Appends a Players block (task-27-brief §2.2 layout: index, posX,
        /// posY, dir, hp, flags — 8 bytes per record).
        public void WritePlayersBlock(System.ReadOnlySpan<SnapshotBlocks.PlayerRecord> records, in SimConfig cfg)
        {
            int payloadBytes = records.Length * SnapshotBlocks.PlayerRecordBytes;
            if (payloadBytes > MaxBlockPayloadBytes)
                throw new System.ArgumentException(
                    $"SnapshotWriter.WritePlayersBlock: {records.Length} records ({payloadBytes} bytes) "
                    + $"exceed MaxBlockPayloadBytes ({MaxBlockPayloadBytes}); the length field is u16.",
                    nameof(records));

            Reserve(BlockHeaderBytes + payloadBytes);

            _dst[_pos] = (byte)SnapshotBlockKind.Players;
            WriteU16(_pos + 1, (ushort)payloadBytes);
            int cursor = _pos + BlockHeaderBytes;
            for (int i = 0; i < records.Length; i++)
            {
                SnapshotBlocks.PlayerRecord r = records[i];
                _dst[cursor] = r.Index;
                WriteU16(cursor + 1, Quantize.Pos(r.Pos.x, cfg.Arena.Radius));
                WriteU16(cursor + 3, Quantize.Pos(r.Pos.y, cfg.Arena.Radius));
                _dst[cursor + 5] = Quantize.Dir(r.Dir);
                _dst[cursor + 6] = Quantize.Unit(r.Hp, cfg.Hero.MaxHp);
                _dst[cursor + 7] = r.Flags;
                cursor += SnapshotBlocks.PlayerRecordBytes;
            }
            _pos += BlockHeaderBytes + payloadBytes;
        }

        /// Appends the Liveness block — exactly one mask byte, bit `i` =
        /// "player `i` is alive" (task-27-brief §2.2, §2.4, Р70).
        public void WriteLivenessBlock(byte aliveMask)
        {
            Reserve(BlockHeaderBytes + SnapshotBlocks.LivenessBlockPayloadBytes);

            _dst[_pos] = (byte)SnapshotBlockKind.Liveness;
            WriteU16(_pos + 1, SnapshotBlocks.LivenessBlockPayloadBytes);
            _dst[_pos + BlockHeaderBytes] = aliveMask;
            _pos += BlockHeaderBytes + SnapshotBlocks.LivenessBlockPayloadBytes;
        }

        /// Appends a Mobs block (task-27-brief §2.2 layout: id, typeAndAi,
        /// posX, posY, dir, hp — 9 bytes per record). `id` is truncated to
        /// its low 16 bits (task-27-brief §2.8) — lossy by construction, see
        /// SnapshotBlocks.MobRecord's doc. HP is quantized against the
        /// record's OWN type's MaxHp (task-27-brief §2.7) — Chaser and
        /// Gunner do not share a cap.
        public void WriteMobsBlock(System.ReadOnlySpan<SnapshotBlocks.MobRecord> records, in SimConfig cfg)
        {
            int payloadBytes = records.Length * SnapshotBlocks.MobRecordBytes;
            if (payloadBytes > MaxBlockPayloadBytes)
                throw new System.ArgumentException(
                    $"SnapshotWriter.WriteMobsBlock: {records.Length} records ({payloadBytes} bytes) "
                    + $"exceed MaxBlockPayloadBytes ({MaxBlockPayloadBytes}); the length field is u16.",
                    nameof(records));

            Reserve(BlockHeaderBytes + payloadBytes);

            _dst[_pos] = (byte)SnapshotBlockKind.Mobs;
            WriteU16(_pos + 1, (ushort)payloadBytes);
            int cursor = _pos + BlockHeaderBytes;
            for (int i = 0; i < records.Length; i++)
            {
                SnapshotBlocks.MobRecord r = records[i];

                // Both halves of the packed byte are checked, not just
                // masked (fix-round M6). A `MobType`/`MobAiState` outside its
                // declared domain cannot survive a nibble: masking would ship
                // a DIFFERENT mob type on the wire and the receiver would
                // decode it as a legal one. That is a caller bug, so it
                // throws, exactly like the record-count check above — the
                // read side's mirror of this check refuses instead (Р82), see
                // SnapshotBlocks.TryReadMobsBlock.
                if ((byte)r.Type > SnapshotBlocks.MaxMobTypeValue
                    || (byte)r.Ai > SnapshotBlocks.MaxMobAiStateValue)
                    throw new System.ArgumentException(
                        $"SnapshotWriter.WriteMobsBlock: record {i} carries Type={(byte)r.Type}/"
                        + $"Ai={(byte)r.Ai}, outside the declared domains "
                        + $"(<= {SnapshotBlocks.MaxMobTypeValue} / <= {SnapshotBlocks.MaxMobAiStateValue}); "
                        + "the packed byte has one nibble each and cannot carry them.",
                        nameof(records));

                WriteU16(cursor, (ushort)(r.Id & 0xFFFF));
                byte typeAndAi = (byte)(((((byte)r.Type) & 0x0F) << 4) | ((byte)r.Ai & 0x0F));
                _dst[cursor + 2] = typeAndAi;
                WriteU16(cursor + 3, Quantize.Pos(r.Pos.x, cfg.Arena.Radius));
                WriteU16(cursor + 5, Quantize.Pos(r.Pos.y, cfg.Arena.Radius));
                _dst[cursor + 7] = Quantize.Dir(r.Dir);
                _dst[cursor + 8] = Quantize.Unit(r.Hp, SnapshotBlocks.MaxHpFor(r.Type, cfg));
                cursor += SnapshotBlocks.MobRecordBytes;
            }
            _pos += BlockHeaderBytes + payloadBytes;
        }

        /// Appends the Wave block — exactly 4 bytes: phase, waveIndex (LE),
        /// aliveCount (task-27-brief §2.2).
        public void WriteWaveBlock(WavePhase phase, ushort waveIndex, byte aliveCount)
        {
            Reserve(BlockHeaderBytes + SnapshotBlocks.WaveBlockPayloadBytes);

            _dst[_pos] = (byte)SnapshotBlockKind.Wave;
            WriteU16(_pos + 1, SnapshotBlocks.WaveBlockPayloadBytes);
            int cursor = _pos + BlockHeaderBytes;
            _dst[cursor] = (byte)phase;
            WriteU16(cursor + 1, waveIndex);
            _dst[cursor + 3] = aliveCount;
            _pos += BlockHeaderBytes + SnapshotBlocks.WaveBlockPayloadBytes;
        }

        /// Appends an Events block: N variable-length records, each 9 +
        /// `PayloadLength` bytes (task-27-brief §2.5). `payloadPool` backs
        /// every record's opaque payload bytes; a record whose
        /// `PayloadOffset + PayloadLength` runs past `payloadPool` is a
        /// CALLER bug (Task 28 owns the pool) and throws, unlike everything
        /// on the read side (Р82 does not apply to the write path — see
        /// SnapshotWriter's class doc). Positions quantize through `Pos`,
        /// never `Aim` (task-27-brief §2.2): an event's position is a point
        /// inside the arena, not the wider domain `AimPoint`'s sanitizer
        /// clamp needs.
        public void WriteEventsBlock(
            System.ReadOnlySpan<SnapshotBlocks.EventRecord> records,
            System.ReadOnlySpan<byte> payloadPool,
            in SimConfig cfg)
        {
            // Validate BEFORE touching the destination — mirrors WriteBlock's
            // "nothing is written until everything fits" (Task 26).
            int payloadBytes = 0;
            for (int i = 0; i < records.Length; i++)
            {
                SnapshotBlocks.EventRecord r = records[i];
                if (r.PayloadOffset + r.PayloadLength > payloadPool.Length)
                    throw new System.ArgumentException(
                        $"SnapshotWriter.WriteEventsBlock: record {i}'s payload "
                        + $"[{r.PayloadOffset}, {r.PayloadOffset + r.PayloadLength}) "
                        + $"runs past payloadPool.Length ({payloadPool.Length}) — a caller bug (Task 28 owns the pool).",
                        nameof(records));
                payloadBytes += SnapshotBlocks.EventHeaderBytes + r.PayloadLength;
            }
            if (payloadBytes > MaxBlockPayloadBytes)
                throw new System.ArgumentException(
                    $"SnapshotWriter.WriteEventsBlock: {records.Length} records ({payloadBytes} bytes) "
                    + $"exceed MaxBlockPayloadBytes ({MaxBlockPayloadBytes}); the length field is u16.",
                    nameof(records));

            Reserve(BlockHeaderBytes + payloadBytes);

            _dst[_pos] = (byte)SnapshotBlockKind.Events;
            WriteU16(_pos + 1, (ushort)payloadBytes);
            int cursor = _pos + BlockHeaderBytes;
            for (int i = 0; i < records.Length; i++)
            {
                SnapshotBlocks.EventRecord r = records[i];
                _dst[cursor] = r.Kind;
                WriteU16(cursor + 1, r.Seq);
                _dst[cursor + 3] = r.TickDelta;
                WriteU16(cursor + 4, Quantize.Pos(r.Pos.x, cfg.Arena.Radius));
                WriteU16(cursor + 6, Quantize.Pos(r.Pos.y, cfg.Arena.Radius));
                _dst[cursor + 8] = r.PayloadLength;
                payloadPool.Slice(r.PayloadOffset, r.PayloadLength)
                    .CopyTo(_dst.Slice(cursor + SnapshotBlocks.EventHeaderBytes, r.PayloadLength));
                cursor += SnapshotBlocks.EventHeaderBytes + r.PayloadLength;
            }
            _pos += BlockHeaderBytes + payloadBytes;
        }

        /// Bytes a Players block of `playerCount` records would occupy,
        /// including its own 3-byte block header — the number Task 28's
        /// truncation branch budgets against (task-27-brief §2.13), never
        /// `MaxBlockPayloadBytes`.
        public static int PlayersBlockBytes(int playerCount)
            => BlockHeaderBytes + playerCount * SnapshotBlocks.PlayerRecordBytes;

        /// Bytes the Liveness block occupies, header included — always the
        /// same regardless of player count (task-27-brief §2.3).
        public static int LivenessBlockBytes()
            => BlockHeaderBytes + SnapshotBlocks.LivenessBlockPayloadBytes;

        /// Bytes a Mobs block of `mobCount` records would occupy, header
        /// included.
        public static int MobsBlockBytes(int mobCount)
            => BlockHeaderBytes + mobCount * SnapshotBlocks.MobRecordBytes;

        /// Bytes the Wave block occupies, header included — fixed size.
        public static int WaveBlockBytes()
            => BlockHeaderBytes + SnapshotBlocks.WaveBlockPayloadBytes;

        /// Bytes an Events block of `eventCount` records totalling
        /// `totalPayloadBytes` of opaque payload would occupy, header
        /// included.
        public static int EventsBlockBytes(int eventCount, int totalPayloadBytes)
            => BlockHeaderBytes + eventCount * SnapshotBlocks.EventHeaderBytes + totalPayloadBytes;

        /// Throws unless `bytes` more bytes fit — BEFORE anything is written,
        /// so a rejected call leaves the buffer untouched. `InvalidOperation-
        /// Exception` rather than `ArgumentException`: what is at fault is
        /// the writer's remaining room, not any argument of the call that
        /// discovers it (`WriteBlock`'s oversized-payload check, and every
        /// Task 27 block method's own record-count/pool check, ARE argument
        /// faults and throw accordingly).
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
