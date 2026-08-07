using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Protocol
{
    /// Stage 2 Task 27 (spec §3.8, §3.12 Р68, Р29/Р60/Р70/Р82/Р101): the FIVE
    /// state blocks that ride inside the SnapshotWriter/SnapshotReader frame
    /// (Task 26) — players, liveness, mobs, wave and events. Task 26 gave the
    /// frame; this file gives the frame's content. Task 27 does not decide
    /// WHO is in a snapshot (Task 28's filter/budget) nor WHAT event kinds
    /// exist (Task 28's catalogue, since it owns the producer) — only how a
    /// record of each kind is laid out on the wire.
    ///
    /// WHY THE READ SIDE LIVES HERE AND THE WRITE SIDE LIVES ON
    /// SnapshotWriter (task-27-brief §2.10). The write methods
    /// (SnapshotWriter.WritePlayersBlock etc.) move `_pos`, i.e. they use the
    /// writer's own cursor state — the same reason WriteHeader/WriteBlock
    /// live there. The read methods below take an already-delivered
    /// `ReadOnlySpan<byte>` (the payload SnapshotReader.TryReadBlock handed
    /// back) and touch no cursor at all; an instance method that ignores
    /// instance state would be a false claim about the type's contract, so
    /// they are static here instead. The two sides are therefore
    /// deliberately asymmetric, not an oversight.
    ///
    /// EVERY MULTI-BYTE FIELD IS LITTLE-ENDIAN, same convention as the frame
    /// header and InputCodec. Records are packed back to back inside a
    /// block's payload, no padding.
    ///
    /// NO RECORD COUNT FIELD (task-27-brief §2.3). For a block whose records
    /// are all the same size (Players, Mobs), a count would be redundant
    /// with `payloadBytes / RecordBytes` — and redundant with the wire is
    /// exactly a new vector for hostile input to make disagree with itself.
    /// `count` is DERIVED, and a payload whose length is not an exact
    /// multiple of the record size is rejected outright
    /// (`SnapshotBlockError.MalformedLength`), never floor-divided. Liveness
    /// and Wave are fixed-size single "records" (1 and 4 bytes respectively)
    /// with the same rule: any other length is malformed. Events cannot be
    /// counted this way at all — its records vary in size — so it is walked
    /// length-by-length instead (see EventRecord below).
    ///
    /// ERROR TAXONOMY IS ITS OWN, SEPARATE FROM SnapshotReader'S
    /// Failed/Truncated/VersionMismatch (task-27-brief §2.9). A block that
    /// SnapshotReader.TryReadBlock handed back was already a well-formed,
    /// fully-present slice of the frame — its own length did not lie, or
    /// SnapshotReader itself would have raised `Truncated`. What happens
    /// INSIDE that payload is this file's business, and is reported through
    /// `SnapshotBlockError`, never by setting `Failed` on the reader that
    /// delivered it: conflating the two levels would blur the
    /// already-documented meaning of `Failed` ("a caller sequencing error"
    /// with no `Truncated`/`VersionMismatch`) with "this block's content
    /// happens to be hostile," which is ordinary traffic on this path (Р82).
    /// `DestinationTooSmall` is not a caller bug either, for the same
    /// Р82 reason as everything else on this read path: the record COUNT is
    /// derived from a length the SENDER controls, so an attacker can declare
    /// a length that implies far more records than any real caller's fixed
    /// destination buffer holds (e.g. a `payloadBytes` near 65535 implies
    /// over 7000 Mobs records against a 96-entry receiver) — the decoder
    /// reports it and refuses, it does not throw.
    ///
    /// THE WRITE SIDE STILL THROWS (task-27-brief §2.9, consistent with all
    /// of Task 26): a caller that hands the writer more records than the
    /// destination buffer/pool can support, or than fit the block's u16
    /// length field, has a bug of its own, not a hostile-input problem — see
    /// each SnapshotWriter.WriteXBlock method for the specific exception.
    ///
    /// QUANTIZATION IS ALWAYS THROUGH Quantize, WITH cfg AS A PARAMETER
    /// (task-27-brief §2.14; rule 2, reuse-not-duplication). No formula is
    /// re-implemented here. `in SimConfig cfg` mirrors InputCodec's own
    /// signature shape — `in`, not by value, because SimConfig is copied
    /// whole otherwise.
    ///
    /// ZERO ALLOCATIONS (task-27-brief §2.15): every read method takes a
    /// caller-owned `Span<TRecord>` destination and writes into it in place;
    /// nothing here is `new`d on the heap.
    public static class SnapshotBlocks
    {
        /// Bytes of one Players record (task-27-brief §2.2/§2.3).
        public const int PlayerRecordBytes = 8;

        /// Bytes of one Mobs record.
        public const int MobRecordBytes = 9;

        /// Bytes of one Events record's FIXED header, before its variable
        /// payload (task-27-brief §2.5): kind(1) + seq(2) + tickDelta(1) +
        /// posX(2) + posY(2) + payloadBytes(1) = 9.
        public const int EventHeaderBytes = 9;

        /// The Liveness block's payload is always exactly this many bytes —
        /// one mask byte, no record framing (task-27-brief §2.3).
        public const int LivenessBlockPayloadBytes = 1;

        /// The Wave block's payload is always exactly this many bytes —
        /// phase(1) + waveIndex(2) + aliveCount(1) = 4.
        public const int WaveBlockPayloadBytes = 4;

        /// Decoded Players record (task-27-brief §2.2, §2.4). `Pos`/`Dir`/
        /// `Hp` are the DECODED values (Quantize's *Back methods), not wire
        /// codes — a caller compares them against simulation state directly.
        public struct PlayerRecord
        {
            public byte Index;
            public float2 Pos;
            public float2 Dir;
            public float Hp;
            public byte Flags;
        }

        /// Decoded Mobs record (task-27-brief §2.2, §2.7, §2.8).
        ///
        /// `Id` IS A LOSSY u16 CODE, WIDENED TO int (task-27-brief §2.8):
        /// the wire only ever carries `(ushort)(sourceId & 0xFFFF)`, so this
        /// field never holds a value outside [0, 65535] regardless of how
        /// large the original `MobState.Id` was, and the ORIGINAL id can
        /// never be recovered from it — two source ids 65536 apart produce
        /// the identical code (pinned by
        /// SnapshotCodecTests.MobId_U16Truncation_PinnedLiterals_AndCollisionAcrossTheWraparound).
        /// That collision needs an entity to outlive 65536 spawns of its
        /// kind, which the current caps (MaxMobs 96 + MaxProjectiles 384,
        /// so <= 480 concurrently live) do not bound in TIME — a
        /// practical-insufficiency claim, not a proof of impossibility.
        /// MAPPING THE CODE BACK TO A LIVE ENTITY IS THE RECEIVER'S JOB
        /// (Task 32), not this decoder's — if a future stage introduces
        /// long-lived entities the question has to be revisited there.
        public struct MobRecord
        {
            public int Id;
            public MobType Type;
            public MobAiState Ai;
            public float2 Pos;
            public float2 Dir;
            public float Hp;
        }

        /// One event record, shared by BOTH sides (task-27-brief §2.5) —
        /// the write side (SnapshotWriter.WriteEventsBlock) and the read
        /// side (TryReadEventsBlock below). The struct never allocates (no
        /// Span field), so `Span<EventRecord>`/`ReadOnlySpan<EventRecord>`
        /// are legal on both sides (task-27-brief §2.15).
        ///
        /// `PayloadOffset`/`PayloadLength` MEAN DIFFERENT SPANS ON EACH
        /// SIDE, documented here since the type itself cannot say which: on
        /// WRITE they index into the CALLER's `payloadPool` argument passed
        /// alongside; on READ they index into the `payload` span that was
        /// passed to `TryReadEventsBlock` (i.e. into the block's own bytes,
        /// starting right after this record's 9-byte header). Both are
        /// "some buffer the caller also has a handle on" — the field names
        /// stay the same because the shape of the problem (a variable slice
        /// described by offset+length instead of an inline array) is
        /// identical either way.
        ///
        /// `Seq` IS `ushort`, NOT THE `u8` spec §3.8 states (task-27-brief
        /// §2.6, correcting spec §3.8 — recorded as a fact correction, not a
        /// silent deviation): at `MaxEventsPerFrame` 512, a one-byte `seq`
        /// wraps inside a single frame's worth of events and Task 29's
        /// dedup key `(epoch, tick, seq)` stops being unique. The plan
        /// already overrode the spec on this point; this is where the
        /// override becomes code.
        ///
        /// `TickDelta` counts ticks BACK FROM THE FRAME HEADER's tick, not
        /// an absolute tick — that is the entire reason the field is one
        /// byte instead of four: `EventRedundancyTicks` (NetConfig, default
        /// 4) never needs more than a tiny window, while an absolute tick
        /// would need the full `u32` the header already carries.
        public struct EventRecord
        {
            public byte Kind;
            public ushort Seq;
            public byte TickDelta;
            public float2 Pos;
            public ushort PayloadOffset;
            public byte PayloadLength;
        }

        /// Decodes a Players block payload (task-27-brief §2.2 layout).
        /// `false` means refused — see `error` for which
        /// `SnapshotBlockError`. Never throws (Р82). The record count is
        /// derived from `payload.Length`, never trusted as a separate field
        /// (task-27-brief §2.3): a length not a multiple of
        /// `PlayerRecordBytes` is `MalformedLength`, and a legally-shaped
        /// payload whose implied count exceeds `destination.Length` is
        /// `DestinationTooSmall` — both refusals reject the WHOLE block,
        /// nothing is written into `destination`.
        public static bool TryReadPlayersBlock(
            System.ReadOnlySpan<byte> payload,
            in SimConfig cfg,
            System.Span<PlayerRecord> destination,
            out int count,
            out SnapshotBlockError error)
        {
            count = 0;
            if (payload.Length % PlayerRecordBytes != 0)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            int recordCount = payload.Length / PlayerRecordBytes;
            if (recordCount > destination.Length)
            {
                error = SnapshotBlockError.DestinationTooSmall;
                return false;
            }

            for (int i = 0; i < recordCount; i++)
            {
                int off = i * PlayerRecordBytes;
                byte index = payload[off];
                ushort xCode = ReadU16(payload, off + 1);
                ushort yCode = ReadU16(payload, off + 3);
                byte dirCode = payload[off + 5];
                byte hpCode = payload[off + 6];
                byte flags = payload[off + 7];
                destination[i] = new PlayerRecord
                {
                    Index = index,
                    Pos = new float2(
                        Quantize.PosBack(xCode, cfg.Arena.Radius),
                        Quantize.PosBack(yCode, cfg.Arena.Radius)),
                    Dir = Quantize.DirBack(dirCode),
                    Hp = Quantize.UnitBack(hpCode, cfg.Hero.MaxHp),
                    Flags = flags,
                };
            }

            count = recordCount;
            error = SnapshotBlockError.None;
            return true;
        }

        /// Decodes a Liveness block payload — exactly one mask byte
        /// (task-27-brief §2.2, §2.4). Never throws.
        public static bool TryReadLivenessBlock(
            System.ReadOnlySpan<byte> payload,
            out byte aliveMask,
            out SnapshotBlockError error)
        {
            aliveMask = 0;
            if (payload.Length != LivenessBlockPayloadBytes)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            aliveMask = payload[0];
            error = SnapshotBlockError.None;
            return true;
        }

        /// Decodes a Mobs block payload (task-27-brief §2.2, §2.7, §2.8).
        /// `typeAndAi` is read BEFORE `hp` is decoded, because the correct
        /// `MaxHp` to decode against depends on the record's OWN type
        /// (Chaser and Gunner do not share a cap) — reading them in the
        /// other order would be a decode-time guess. `Id` is handed back
        /// as-is, a lossy u16 code (see MobRecord's own doc). Never throws.
        public static bool TryReadMobsBlock(
            System.ReadOnlySpan<byte> payload,
            in SimConfig cfg,
            System.Span<MobRecord> destination,
            out int count,
            out SnapshotBlockError error)
        {
            count = 0;
            if (payload.Length % MobRecordBytes != 0)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            int recordCount = payload.Length / MobRecordBytes;
            if (recordCount > destination.Length)
            {
                error = SnapshotBlockError.DestinationTooSmall;
                return false;
            }

            for (int i = 0; i < recordCount; i++)
            {
                int off = i * MobRecordBytes;
                ushort idCode = ReadU16(payload, off);
                byte typeAndAi = payload[off + 2];
                var type = (MobType)((typeAndAi >> 4) & 0x0F);
                var ai = (MobAiState)(typeAndAi & 0x0F);
                ushort xCode = ReadU16(payload, off + 3);
                ushort yCode = ReadU16(payload, off + 5);
                byte dirCode = payload[off + 7];
                byte hpCode = payload[off + 8];
                float maxHp = type == MobType.Chaser ? cfg.Chaser.MaxHp : cfg.Gunner.MaxHp;
                destination[i] = new MobRecord
                {
                    Id = idCode,
                    Type = type,
                    Ai = ai,
                    Pos = new float2(
                        Quantize.PosBack(xCode, cfg.Arena.Radius),
                        Quantize.PosBack(yCode, cfg.Arena.Radius)),
                    Dir = Quantize.DirBack(dirCode),
                    Hp = Quantize.UnitBack(hpCode, maxHp),
                };
            }

            count = recordCount;
            error = SnapshotBlockError.None;
            return true;
        }

        /// Decodes the Wave block payload — exactly 4 bytes
        /// (task-27-brief §2.2). Never throws.
        public static bool TryReadWaveBlock(
            System.ReadOnlySpan<byte> payload,
            out WavePhase phase,
            out ushort waveIndex,
            out byte aliveCount,
            out SnapshotBlockError error)
        {
            phase = default;
            waveIndex = 0;
            aliveCount = 0;
            if (payload.Length != WaveBlockPayloadBytes)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            phase = (WavePhase)payload[0];
            waveIndex = ReadU16(payload, 1);
            aliveCount = payload[3];
            error = SnapshotBlockError.None;
            return true;
        }

        /// Decodes an Events block payload by walking record lengths
        /// (task-27-brief §2.5) rather than a record count — the payload is
        /// not a multiple of any fixed record size. Records already
        /// successfully decoded before a hostile record is hit REMAIN in
        /// `destination` and are counted in `count`; only the refusal itself
        /// stops the walk (task-27-brief §3 item 14). Positions decode
        /// through `PosBack`, matching the writer's `Pos` (task-27-brief
        /// §2.2). Never throws.
        public static bool TryReadEventsBlock(
            System.ReadOnlySpan<byte> payload,
            in SimConfig cfg,
            System.Span<EventRecord> destination,
            out int count,
            out SnapshotBlockError error)
        {
            count = 0;
            int pos = 0;
            while (pos < payload.Length)
            {
                if (payload.Length - pos < EventHeaderBytes)
                {
                    error = SnapshotBlockError.MalformedLength;
                    return false;
                }

                byte kind = payload[pos];
                ushort seq = ReadU16(payload, pos + 1);
                byte tickDelta = payload[pos + 3];
                ushort xCode = ReadU16(payload, pos + 4);
                ushort yCode = ReadU16(payload, pos + 6);
                byte payloadLength = payload[pos + 8];
                int recordPayloadStart = pos + EventHeaderBytes;

                if (payload.Length - recordPayloadStart < payloadLength)
                {
                    error = SnapshotBlockError.EventPayloadOverrun;
                    return false;
                }
                if (count >= destination.Length)
                {
                    error = SnapshotBlockError.DestinationTooSmall;
                    return false;
                }

                destination[count] = new EventRecord
                {
                    Kind = kind,
                    Seq = seq,
                    TickDelta = tickDelta,
                    Pos = new float2(
                        Quantize.PosBack(xCode, cfg.Arena.Radius),
                        Quantize.PosBack(yCode, cfg.Arena.Radius)),
                    PayloadOffset = (ushort)recordPayloadStart,
                    PayloadLength = payloadLength,
                };
                count++;
                pos = recordPayloadStart + payloadLength;
            }

            error = SnapshotBlockError.None;
            return true;
        }

        static ushort ReadU16(System.ReadOnlySpan<byte> src, int offset)
            => (ushort)(src[offset] | (src[offset + 1] << 8));
    }

    /// Block kind tags carried by SnapshotWriter.WriteBlock/SnapshotReader.
    /// TryReadBlock's `kind` byte (task-27-brief §2.1).
    ///
    /// `None = 0` MUST NEVER BE WRITTEN — this is a contract inherited from
    /// Task 26, not an aesthetic choice. `SnapshotReader.TryReadBlock`
    /// returns `kind = 0` on every refusal (see its `out byte kind` default),
    /// and two existing Task 26 tests pin that zero literally
    /// (`Reader_BlockBeforeHeader_FailsInsteadOfParsingHeaderBytesAsABlock`,
    /// `Reader_AfterMalformedLength_DoesNotResumeOnAttackerChosenBytes`).
    /// Assigning 0 to a real block kind would make "the reader refused" and
    /// "a Players block arrived" indistinguishable to any caller that only
    /// checks the returned `kind`. Pinned by
    /// SnapshotCodecTests.SnapshotBlockKind_ValuesArePinned_AndNoneIsZero.
    public enum SnapshotBlockKind : byte
    {
        None = 0,
        Players = 1,
        Liveness = 2,
        Mobs = 3,
        Wave = 4,
        Events = 5,
    }

    /// Bit positions of the Players record's `flags` byte (task-27-brief
    /// §2.4, spec §3.12 Р68). Bits 5-7 are free and UNASSIGNED.
    ///
    /// `Alive` DUPLICATES THE Liveness BLOCK ON PURPOSE. The Liveness block
    /// is the registry of every slot in the match — a dead player still
    /// needs to appear there so other dead players have a full roster of
    /// spectate candidates (Р70) — while a player's OWN record in the
    /// Players block still gets written for a dead player too (spec §3.5:
    /// "the corpse is visible under ordinary visibility rules";
    /// `carryover-t28.md` §8в explicitly forbids Task 28 from adding a
    /// liveness guard on the Players block). A hostile or simply
    /// out-of-sync sender can therefore make the two disagree — bit `Alive`
    /// clear on a record while the Liveness block still marks that slot
    /// alive, or vice versa. THIS DECODER DOES NOT RECONCILE THEM AND NEVER
    /// THROWS ON A MISMATCH (Р82): the Liveness block is the source of
    /// truth for "who is in the match and alive"; the record's own `Alive`
    /// bit is the source of truth for "what to draw for this specific
    /// record." Any cross-check belongs to the consumer (Task 32), not the
    /// codec.
    public static class PlayerWireFlags
    {
        public const byte Alive = 1 << 0;
        public const byte Dashing = 1 << 1;
        public const byte Sliding = 1 << 2;
        public const byte AimHeld = 1 << 3;
        public const byte LinkWindow = 1 << 4;
        // bits 5..7 are free and NOT assigned.
    }

    /// Refusal reasons a block DECODER can report (task-27-brief §2.9) —
    /// deliberately separate from SnapshotReader's Failed/Truncated/
    /// VersionMismatch, which describe the OUTER frame, not a block's
    /// content. See SnapshotBlocks's class doc for the full reasoning.
    public enum SnapshotBlockError : byte
    {
        None = 0,

        /// The payload length is not a legal shape for this block kind: not
        /// a multiple of its fixed record size (Players/Mobs), not equal to
        /// its fixed size (Liveness/Wave), or too short for the next Events
        /// record's 9-byte header to fit at all.
        MalformedLength,

        /// The block declares more records than the caller's destination
        /// buffer holds. The record count comes from the SENDER's declared
        /// length, so this is hostile/stale input, not a caller bug — see
        /// the class doc's Р82 note.
        DestinationTooSmall,

        /// An Events record's own `payloadBytes` claims more bytes than
        /// remain in the block.
        EventPayloadOverrun,
    }
}
