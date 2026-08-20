using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Protocol
{
    /// Stage 2 Task 27 (spec §3.8, §3.12 Р68, Р29/Р60/Р70/Р82/Р101): the FIVE
    /// state blocks that ride inside the SnapshotWriter/SnapshotReader frame
    /// (Task 26) — players, liveness, mobs, wave and events. Task 26 gave the
    /// frame; this file gives the frame's content. Task 27 does not decide
    /// WHO is in a snapshot (Task 28's filter/budget) nor WHAT event kinds
    /// exist (Task 28's catalog, since it owns the producer) — only how a
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
        /// two mask bytes, no record framing (task-27-brief §2.3; Stage 3
        /// Task 25 grew it from one, spec Р257: alive first, extracted
        /// second).
        public const int LivenessBlockPayloadBytes = 2;

        /// The Wave block's payload is always exactly this many bytes —
        /// phase(1) + waveIndex(2) + aliveCount(1) = 4.
        public const int WaveBlockPayloadBytes = 4;

        /// Stage 3 Task 25 (spec §3.12's own table): the Match block's
        /// payload is always exactly this many bytes — phase(1) +
        /// secondsRemaining(2) + flags(1) = 4.
        public const int MatchBlockPayloadBytes = 4;

        /// Bytes of the Self block's FIXED head, before the variable run of
        /// item ids that follows it: slotPoints(1) + itemCount(1) = 2.
        public const int SelfBlockHeaderBytes = 2;

        /// Bytes of one Pickups record: id(2) + posX(2) + posY(2) + kind(1).
        public const int PickupRecordBytes = 7;

        /// Bytes of one Containers record: id(2) + posX(2) + posY(2) +
        /// kindAndEmpty(1) — the last byte packs two nibbles, exactly as the
        /// Mobs record packs MobType and MobAiState.
        public const int ContainerRecordBytes = 7;

        /// Bytes of one ContainerSlots record's FIXED head, before the item
        /// ids of its occupied slots: id(2) + occupancyMask(1) = 3. The
        /// number of ids that follows is the mask's POPCOUNT — derived,
        /// never a second field that could disagree with it (this file's own
        /// "no record count" doctrine, applied to a mask).
        public const int ContainerSlotsRecordHeaderBytes = 3;

        /// Highest legal wire value of `MobType` / `MobAiState` / `WavePhase`
        /// (fix-round I1). The wire carries these as raw bits, so a hostile
        /// or corrupted byte can name a value the enum does not define —
        /// `(MobType)15` casts perfectly happily in C#. Every decoder below
        /// refuses such a record (`SnapshotBlockError.MalformedContent`)
        /// instead of handing the consumer an undefined enumerator: Task 32
        /// and Tasks 43-45 index prefab/animator tables by these very
        /// values, so a pass-through would turn one bad byte into an
        /// IndexOutOfRange on the client's render path — refusing bad input
        /// is what Р82 asks for, and "never throws" was only half of it.
        ///
        /// THESE THREE CONSTANTS ARE THE TRIPWIRE for the enums growing. They
        /// are pinned literally by SnapshotCodecTests.
        /// EnumDomainBounds_MatchTheSimulationEnums, which also counts the
        /// enumerators — so adding a MobAiState in Stage 3 fails there, in a
        /// test that says in words that the wire domain moved, rather than
        /// silently making legal traffic unparseable. A wire domain change is
        /// also a ProtocolVersion bump (see ProtocolVersion's own doc).
        ///
        /// Stage 3 Task 10 (spec Р213/Р251): `MobType` grew Elite/Director,
        /// so this moves from `Gunner` to `Director` — exactly the
        /// ProtocolVersion-bump case the doc above already named. MobAiState/
        /// WavePhase are unchanged (Р214: Elite/Director reuse the existing
        /// six-state FSM, no new state).
        public const byte MaxMobTypeValue = (byte)MobType.Director;
        public const byte MaxMobAiStateValue = (byte)MobAiState.Fire;
        public const byte MaxWavePhaseValue = (byte)WavePhase.Active;

        /// Stage 3 Task 25: the same tripwire, extended to the three enums
        /// the new blocks put on the wire. `MatchPhase` rides the Match
        /// block, `PickupKind` the Pickups record's tail byte and
        /// `ContainerKind` the high nibble of the Containers record's — and
        /// every one of them is read by Tasks 30-32 as an index into a
        /// prefab/tint/icon table, which is precisely the pass-through this
        /// project already paid for once with MobType.
        ///
        /// `MaxPickupKindValue` IS ZERO TODAY, and that is a fact about the
        /// catalog rather than a disabled check: `PickupKind` has exactly one
        /// member (EnergyCell), so every byte above 0 is out of domain. The
        /// day a second pickup kind lands, EnumDomainBounds_
        /// MatchTheSimulationEnums reddens here first.
        public const byte MaxMatchPhaseValue = (byte)MatchPhase.Ended;
        public const byte MaxPickupKindValue = (byte)PickupKind.EnergyCell;
        public const byte MaxContainerKindValue = (byte)ContainerKind.PlayerCorpse;

        /// The `MaxHp` a mob record's HP byte is quantized against — no two
        /// archetypes share a cap, so the record's own type decides
        /// (task-27-brief §2.7). One home for the rule, called by both sides
        /// (fix-round M1): it was written out twice, and a third `MobType` in
        /// Stage 3 would have needed both edits with neither a compile error
        /// nor a red test to demand the second — Stage 3 Task 10 is that
        /// third (and fourth) `MobType`, exactly as predicted.
        ///
        /// The four-way switch is safe because both call sites reject
        /// anything above `MaxMobTypeValue` first, and that constant is
        /// pinned by the test named above — the `_` arm below is
        /// unreachable in practice, not a real fallback, same reasoning as
        /// ProjectileSystem.Update's own radius switch.
        public static float MaxHpFor(MobType type, in SimConfig cfg) => type switch
        {
            MobType.Chaser => cfg.Chaser.MaxHp,
            MobType.Gunner => cfg.Gunner.MaxHp,
            MobType.Elite => cfg.Elite.MaxHp,
            MobType.Director => cfg.Director.MaxHp,
            _ => cfg.Gunner.MaxHp,
        };

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
        /// `Id` IS A LOSSY u16 CODE ON THE WIRE, AND THE FIELD MEANS
        /// DIFFERENT THINGS ON EACH SIDE (task-27-brief §2.8; fix-round I5 —
        /// the first wording claimed this field "never holds a value outside
        /// [0, 65535]", which is false for half of its own contract and is
        /// exactly the kind of universal negative this track keeps paying
        /// for). `MobRecord` is shared by both sides, so:
        ///   * ON WRITE it carries the FULL `MobState.Id`, an `int` — which
        ///     is why SnapshotWriter.WriteMobsBlock narrows it with
        ///     `(ushort)(r.Id & 0xFFFF)`, and why this file's own test feeds
        ///     it `Id = 65543`;
        ///   * ON READ it carries the decoded u16 code, so a record produced
        ///     by the decoder below is always in [0, 65535].
        /// The ORIGINAL id can never be recovered from the code — two source
        /// ids 65536 apart produce the identical one (pinned by
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

        /// Decoded Pickups record (Stage 3 Task 25, spec §3.12). `Pos` is the
        /// DECODED position, same convention as PlayerRecord/MobRecord above;
        /// `Id` is the same lossy u16 code MobRecord's own doc describes, and
        /// spec Р278 adds the part that is new here — a container or a cell
        /// can outlive a mob by the whole raid, so the receiver may map the
        /// code to a live entity ONLY within the current epoch and the
        /// current frame, never remember it between frames.
        ///
        /// `Amount` IS DELIBERATELY NOT ON THE WIRE (plan errata E-7, written
        /// out rather than left implicit). A cell's charge decides what the
        /// SERVER adds to the picker's ammo (PickupSystem.Collect); the
        /// client draws the same blue sphere either way, and CRITICAL RULE 3
        /// puts the arithmetic on the server regardless. Two bytes per record
        /// bought nothing and would have been a second authority on a number
        /// the client cannot act on.
        public struct PickupRecord
        {
            public int Id;
            public PickupKind Kind;
            public float2 Pos;
        }

        /// Decoded Containers record (Stage 3 Task 25, spec §3.12). The wire
        /// packs `Kind` and `IsEmpty` into one byte's two nibbles — the same
        /// packing MobRecord uses for type and AI state, and for the same
        /// reason: two small domains, one byte, no third field.
        ///
        /// `IsEmpty` rides even though the ContainerSlots block would answer
        /// the same question, because the two blocks travel on DIFFERENT
        /// terms: slots are sent only to an observer inside LootRadius (spec
        /// §3.8 Р238), while this record is sent to everyone who can see the
        /// box. "Already looted" is what a player reads at a distance to
        /// decide whether the walk is worth it, so it cannot depend on being
        /// close enough to already know.
        public struct ContainerRecord
        {
            public int Id;
            public ContainerKind Kind;
            public bool IsEmpty;
            public float2 Pos;
        }

        /// Decoded ContainerSlots record (Stage 3 Task 25, spec §3.12 Р277).
        /// One record per container whose interior is being sent.
        ///
        /// THE MASK IS THE POINT, NOT A COMPACTION (Р277, finding D-14).
        /// `LootOps.Take` addresses a slot BY INDEX, so a compact "here are
        /// the three items it still holds" list would systematically
        /// disagree with the server's own numbering after any partial
        /// looting — every second Take would be refused as "slot empty" by
        /// construction, not by a race. Bit `i` of `OccupancyMask` means slot
        /// `i` is occupied, and the ids that follow are those slots' items in
        /// ascending slot order. At MaxContainerSlots 8 one byte covers a
        /// whole container.
        ///
        /// `ItemOffset` INDEXES DIFFERENT SPANS ON THE TWO SIDES, exactly
        /// like EventRecord.PayloadOffset below: on WRITE it points into the
        /// caller's own item pool, on READ into the block payload that was
        /// handed to TryReadContainerSlotsBlock. There is no length field
        /// beside it — the count is the mask's popcount, derived, so the wire
        /// cannot contradict itself.
        public struct ContainerSlotsRecord
        {
            public int Id;
            public byte OccupancyMask;
            public ushort ItemOffset;
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
        /// byte instead of four: an absolute tick would need the full `u32`
        /// the header already carries. The byte's 255-tick reach is what the
        /// server budgets against — the redundancy window
        /// (`EventRedundancyTicks`, default 4) is tiny, and the carry
        /// queue / resend history evict, with a counter or silently per
        /// their own docs, anything whose delta would no longer fit
        /// (phase gate fix wave: the horizon guards in SnapshotAssembler
        /// are this field's real binding consumer, not the redundancy
        /// window alone).
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

            // CONTENT VALIDATION IS ITS OWN PASS, BEFORE A SINGLE RECORD IS
            // WRITTEN (fix-round I1), so this method's "the whole block is
            // rejected, nothing is written into `destination`" contract stays
            // literally true rather than nearly true. The slot index is a raw
            // wire byte and can name a slot this match does not have; both
            // peers agree on MaxPlayers by construction (it is part of
            // SimConfig, hence of the SimConfigHash compared in the handshake,
            // Task 39), so an index at or above it is hostile or stale, never
            // legitimate — and letting it through would hand Task 32/45 an
            // out-of-range index into their per-slot view pools.
            for (int i = 0; i < recordCount; i++)
                if (payload[i * PlayerRecordBytes] >= cfg.Arena.MaxPlayers)
                {
                    error = SnapshotBlockError.MalformedContent;
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
            out byte extractedMask,
            out SnapshotBlockError error)
        {
            aliveMask = 0;
            extractedMask = 0;
            if (payload.Length != LivenessBlockPayloadBytes)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            aliveMask = payload[0];
            extractedMask = payload[1];
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

            // Content validation as its own pass, before anything is written
            // (fix-round I1) — same reasoning as TryReadPlayersBlock. A
            // hostile `typeAndAi` nibble casts to an undefined enumerator
            // without complaint in C#: `(MobType)15` and `(MobAiState)7` are
            // legal casts and illegal values, and Tasks 43-45 index their
            // prefab/animator tables by exactly these.
            for (int i = 0; i < recordCount; i++)
            {
                byte packed = payload[i * MobRecordBytes + 2];
                if ((packed >> 4) > MaxMobTypeValue || (packed & 0x0F) > MaxMobAiStateValue)
                {
                    error = SnapshotBlockError.MalformedContent;
                    return false;
                }
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
                float maxHp = MaxHpFor(type, cfg);
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

            // Fix-round I1: `WavePhase` has two enumerators, the wire byte has
            // 256 values. Refuse the block rather than hand a consumer a
            // `(WavePhase)200` that no `switch` accounts for.
            if (payload[0] > MaxWavePhaseValue)
            {
                error = SnapshotBlockError.MalformedContent;
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

            // Fix-round M3: `EventRecord.PayloadOffset` is a `ushort`, so an
            // offset in a payload longer than 65535 B would wrap silently and
            // point the consumer at the wrong bytes. Through
            // SnapshotReader.TryReadBlock this is unreachable — a block's own
            // length field is a u16 — but this method is public and its doc
            // invites direct calls, so the precondition is enforced rather
            // than assumed. A silent wrap inside a decoder of untrusted bytes
            // is precisely what Р82 exists to rule out.
            if (payload.Length > ushort.MaxValue)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

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

        /// Decodes the Match block payload — exactly 4 bytes (spec §3.12).
        /// Never throws. `secondsRemaining` is the raid's own countdown, not
        /// an elapsed time: elapsed is already on the wire in the frame
        /// header's tick, and the remaining half depends on
        /// NetConfig.MatchMaxDurationSeconds, which lives on the server's
        /// side of the authority line (CRITICAL RULE 3 names the match timer
        /// among the things the server decides). Producing it is the
        /// assembler's job (Task 27); this decoder only refuses a payload
        /// whose shape or phase byte is not legal.
        public static bool TryReadMatchBlock(
            System.ReadOnlySpan<byte> payload,
            out MatchPhase phase,
            out ushort secondsRemaining,
            out byte flags,
            out SnapshotBlockError error)
        {
            phase = default;
            secondsRemaining = 0;
            flags = 0;
            if (payload.Length != MatchBlockPayloadBytes)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            // Same content pass every other decoder here runs, and for the
            // same reason: `(MatchPhase)9` is a legal cast and an illegal
            // value, and the HUD switches on it.
            if (payload[0] > MaxMatchPhaseValue)
            {
                error = SnapshotBlockError.MalformedContent;
                return false;
            }

            phase = (MatchPhase)payload[0];
            secondsRemaining = ReadU16(payload, 1);
            flags = payload[3];
            error = SnapshotBlockError.None;
            return true;
        }

        /// Decodes the Self block payload — the owner's own backpack (spec
        /// §3.12 Р276: everything that is already a PlayerState field rides
        /// reconciliation instead, so only the item ids and their slot-point
        /// total are here). Never throws.
        ///
        /// The item ids are validated against the catalog, not passed
        /// through: Loot.ItemCatalogLookup.Find THROWS on an unknown id, so
        /// an unchecked byte off the wire would turn one hostile packet into
        /// an exception inside whichever consumer resolved it first — the
        /// exact shape Р82 rules out, and the same argument that already
        /// makes a Players record's slot index a MalformedContent case.
        public static bool TryReadSelfBlock(
            System.ReadOnlySpan<byte> payload,
            in SimConfig cfg,
            System.Span<byte> itemDestination,
            out byte slotPoints,
            out int itemCount,
            out SnapshotBlockError error)
        {
            slotPoints = 0;
            itemCount = 0;
            if (payload.Length < SelfBlockHeaderBytes)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            int declared = payload[1];
            if (payload.Length != SelfBlockHeaderBytes + declared)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }
            if (declared > itemDestination.Length)
            {
                error = SnapshotBlockError.DestinationTooSmall;
                return false;
            }

            // Content pass before a single byte is handed back, so "the whole
            // block is refused" stays literally true.
            for (int i = 0; i < declared; i++)
                if (!InCatalog(payload[SelfBlockHeaderBytes + i], in cfg))
                {
                    error = SnapshotBlockError.MalformedContent;
                    return false;
                }

            for (int i = 0; i < declared; i++)
                itemDestination[i] = payload[SelfBlockHeaderBytes + i];

            slotPoints = payload[0];
            itemCount = declared;
            error = SnapshotBlockError.None;
            return true;
        }

        /// Decodes a Pickups block payload (spec §3.12). Same derived-count
        /// discipline as Players/Mobs: a length that is not a multiple of
        /// PickupRecordBytes is MalformedLength, an implied count past
        /// `destination` is DestinationTooSmall, and both refuse the WHOLE
        /// block without writing anything. Never throws.
        public static bool TryReadPickupsBlock(
            System.ReadOnlySpan<byte> payload,
            in SimConfig cfg,
            System.Span<PickupRecord> destination,
            out int count,
            out SnapshotBlockError error)
        {
            count = 0;
            if (payload.Length % PickupRecordBytes != 0)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            int recordCount = payload.Length / PickupRecordBytes;
            if (recordCount > destination.Length)
            {
                error = SnapshotBlockError.DestinationTooSmall;
                return false;
            }

            for (int i = 0; i < recordCount; i++)
                if (payload[i * PickupRecordBytes + 6] > MaxPickupKindValue)
                {
                    error = SnapshotBlockError.MalformedContent;
                    return false;
                }

            for (int i = 0; i < recordCount; i++)
            {
                int off = i * PickupRecordBytes;
                ReadEntityRecord(payload, off, in cfg, out int id, out float2 pos, out byte tail);
                destination[i] = new PickupRecord
                {
                    Id = id,
                    Kind = (PickupKind)tail,
                    Pos = pos,
                };
            }

            count = recordCount;
            error = SnapshotBlockError.None;
            return true;
        }

        /// Decodes a Containers block payload (spec §3.12). The tail byte's
        /// HIGH nibble is the kind and its LOW nibble is the "already empty"
        /// flag; a kind outside ContainerKind's domain, or an empty nibble
        /// that is neither 0 nor 1, is MalformedContent — a client indexes
        /// prefab tables by the first and branches on the second.
        /// Never throws.
        public static bool TryReadContainersBlock(
            System.ReadOnlySpan<byte> payload,
            in SimConfig cfg,
            System.Span<ContainerRecord> destination,
            out int count,
            out SnapshotBlockError error)
        {
            count = 0;
            if (payload.Length % ContainerRecordBytes != 0)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            int recordCount = payload.Length / ContainerRecordBytes;
            if (recordCount > destination.Length)
            {
                error = SnapshotBlockError.DestinationTooSmall;
                return false;
            }

            for (int i = 0; i < recordCount; i++)
            {
                byte packed = payload[i * ContainerRecordBytes + 6];
                // The low nibble is a FLAG, so 0 and 1 are the only values it
                // can mean. Anything else is content the format has no
                // reading for, and truncating it to a bool would invent one.
                if ((packed >> 4) > MaxContainerKindValue || (packed & 0x0F) > 1)
                {
                    error = SnapshotBlockError.MalformedContent;
                    return false;
                }
            }

            for (int i = 0; i < recordCount; i++)
            {
                int off = i * ContainerRecordBytes;
                ReadEntityRecord(payload, off, in cfg, out int id, out float2 pos, out byte tail);
                destination[i] = new ContainerRecord
                {
                    Id = id,
                    Kind = (ContainerKind)((tail >> 4) & 0x0F),
                    IsEmpty = (tail & 0x0F) != 0,
                    Pos = pos,
                };
            }

            count = recordCount;
            error = SnapshotBlockError.None;
            return true;
        }

        /// Decodes a ContainerSlots block payload by WALKING it, exactly as
        /// TryReadEventsBlock does and for the same reason: its records vary
        /// in size, so no count can be derived from the payload length alone.
        /// Each record's own size is its 3-byte head plus the popcount of its
        /// occupancy mask. Records decoded before a refusal REMAIN in
        /// `destination` and are counted in `count`, the same contract the
        /// Events walker documents. Never throws.
        public static bool TryReadContainerSlotsBlock(
            System.ReadOnlySpan<byte> payload,
            in SimConfig cfg,
            System.Span<ContainerSlotsRecord> destination,
            out int count,
            out SnapshotBlockError error)
        {
            count = 0;

            // `ItemOffset` is a ushort, so an offset inside a payload longer
            // than 65535 B would wrap silently and point a consumer at the
            // wrong bytes — the same precondition TryReadEventsBlock enforces
            // for the same field width, unreachable through SnapshotReader
            // (block lengths are u16) and enforced anyway because this method
            // is public.
            if (payload.Length > ushort.MaxValue)
            {
                error = SnapshotBlockError.MalformedLength;
                return false;
            }

            int pos = 0;
            while (pos < payload.Length)
            {
                if (payload.Length - pos < ContainerSlotsRecordHeaderBytes)
                {
                    error = SnapshotBlockError.MalformedLength;
                    return false;
                }

                int id = ReadU16(payload, pos);
                byte mask = payload[pos + 2];
                int occupied = OccupiedSlotCount(mask);
                int itemStart = pos + ContainerSlotsRecordHeaderBytes;
                if (payload.Length - itemStart < occupied)
                {
                    error = SnapshotBlockError.MalformedLength;
                    return false;
                }
                if (count >= destination.Length)
                {
                    error = SnapshotBlockError.DestinationTooSmall;
                    return false;
                }
                for (int i = 0; i < occupied; i++)
                    if (!InCatalog(payload[itemStart + i], in cfg))
                    {
                        error = SnapshotBlockError.MalformedContent;
                        return false;
                    }

                destination[count] = new ContainerSlotsRecord
                {
                    Id = id,
                    OccupancyMask = mask,
                    ItemOffset = (ushort)itemStart,
                };
                count++;
                pos = itemStart + occupied;
            }

            error = SnapshotBlockError.None;
            return true;
        }

        /// Number of occupied slots a ContainerSlots mask promises — the ONE
        /// home of "how many item ids follow this record", called by the
        /// decoder above and by SnapshotWriter's own writer and calculator so
        /// the three cannot drift apart.
        public static int OccupiedSlotCount(byte occupancyMask)
        {
            int n = 0;
            for (int bit = 0; bit < 8; bit++)
                if ((occupancyMask & (1 << bit)) != 0) n++;
            return n;
        }

        /// The head shared by the Pickups and Containers records — id, then
        /// a quantized position, then one tail byte whose MEANING is each
        /// block's own business (plan errata E-6 C-I5's second half: one
        /// home for the ground entities' wire record, not two copies six
        /// bytes long apiece). The tail is handed back raw precisely because
        /// the two blocks read it differently: a plain kind for a pickup, two
        /// nibbles for a container.
        static void ReadEntityRecord(System.ReadOnlySpan<byte> payload, int offset,
            in SimConfig cfg, out int id, out float2 pos, out byte tail)
        {
            id = ReadU16(payload, offset);
            pos = new float2(
                Quantize.PosBack(ReadU16(payload, offset + 2), cfg.Arena.Radius),
                Quantize.PosBack(ReadU16(payload, offset + 4), cfg.Arena.Radius));
            tail = payload[offset + 6];
        }

        /// Whether the catalog holds this item id (Stage 3 Task 25). An id it
        /// does NOT hold makes Loot.ItemCatalogLookup.Find throw, so the two
        /// decoders that carry ids off the wire refuse it here instead —
        /// same argument as the Players record's slot index: both peers agree
        /// on the catalog by construction (it is part of SimConfig and hence
        /// of the SimConfigHash the handshake compares), so an id outside it
        /// is hostile or stale, never legitimate.
        ///
        /// A config with NO catalog at all skips the check rather than
        /// refusing everything: an empty catalog is a hand-built fixture, not
        /// a live match (SimConfigBuilder.Validate refuses one for a real
        /// config), and a decoder must not invent a domain it was handed
        /// nothing to check against.
        static bool InCatalog(byte itemId, in SimConfig cfg)
        {
            ItemDef[] catalog = cfg.Items;
            if (catalog == null || catalog.Length == 0) return true;
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i].Id == itemId) return true;
            return false;
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

        // Stage 3 Task 25 (spec §3.12). Appended, never renumbered: a tag is
        // the one field of this format a reader of ANOTHER build still has to
        // agree on, and Р282 is explicit that a new block kind does NOT bump
        // ProtocolVersion — the tagged, length-prefixed frame exists so an
        // older reader can walk past a tag it has never heard of.
        Match = 6,
        Self = 7,
        Pickups = 8,
        Containers = 9,
        ContainerSlots = 10,
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

    /// Bit positions of the Match block's `flags` byte (Stage 3 Task 25,
    /// spec §3.12: "флаги u8 (Директор жив, створ открыт)"). Bits 2-7 are
    /// free and UNASSIGNED.
    ///
    /// THE TWO BITS ARE NOT THE SAME KIND OF FACT, and reading them as one
    /// would be a mistake this comment exists to prevent.
    ///   * `DirectorAlive` is NOT derivable from the phase. The Director can
    ///     be dead while MatchState.Phase is still DirectorActive — the gate
    ///     opens GateDelaySeconds AFTER his death (MatchFlowSystem stamps
    ///     DirectorDeathTick and waits), and that whole window is exactly
    ///     when a client most needs to know he is gone.
    ///   * `GateOpen` IS derivable — it is `Phase == MatchPhase.GateOpen`,
    ///     and it rides anyway for the same reason PlayerWireFlags.Alive
    ///     duplicates the Liveness block: a consumer reading this byte should
    ///     not have to re-derive the state machine's own verdict. THE PHASE
    ///     IS THE SOURCE OF TRUTH; this bit is a convenience view of it, and
    ///     a consumer that finds the two disagreeing believes the phase.
    public static class MatchWireFlags
    {
        public const byte DirectorAlive = 1 << 0;
        public const byte GateOpen = 1 << 1;
        // bits 2..7 are free and NOT assigned.
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
        /// record's 9-byte header to fit at all. Since fix-round 1 it also
        /// covers the opposite end — an Events payload LONGER than 65535 B,
        /// which no `EventRecord.PayloadOffset` could address (that guard is
        /// unreachable through SnapshotReader, whose block lengths are u16,
        /// and exists for direct callers). "Length is not a legal shape",
        /// then, in both directions, not merely "too short".
        MalformedLength,

        /// The block declares more records than the caller's destination
        /// buffer holds. The record count comes from the SENDER's declared
        /// length, so this is hostile/stale input, not a caller bug — see
        /// the class doc's Р82 note.
        ///
        /// WHAT `destination` HOLDS AFTERWARDS DIFFERS BY BLOCK (fix-round
        /// M2). Players and Mobs know their count up front, so they refuse
        /// before writing anything and report `count` 0. Events cannot —
        /// its records vary in size, so the overflow is only discovered
        /// while walking, and the records decoded before it REMAIN in
        /// `destination` with `count` naming how many. Read `count` in both
        /// cases; never assume the refusal means the buffer is untouched.
        DestinationTooSmall,

        /// An Events record's own `payloadBytes` claims more bytes than
        /// remain in the block.
        EventPayloadOverrun,

        /// A field's VALUE is outside its declared domain even though the
        /// block's shape is legal (fix-round I1): a `MobType`/`MobAiState`
        /// nibble or a `WavePhase` byte naming an enumerator that does not
        /// exist, or a Players record whose slot index is not below
        /// `cfg.Arena.MaxPlayers`. Shape-legal, content-illegal — the one
        /// hostile case the block's own length can never catch, and the one
        /// that reaches furthest downstream if it is let through (Task 32 and
        /// Tasks 43-45 index tables by exactly these values).
        MalformedContent,
    }
}
