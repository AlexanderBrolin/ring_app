using System.Collections.Generic;
using FishNet.Broadcast;
using NUnit.Framework;
using Ring.Data;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Networking.Server;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below are
// required by the file, not just convenience imports (InputCodecTests.cs and
// QuantizeTests.cs carry the same pair for the same reason).
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    // Stage 2 Task 26 (spec §3.8, Р29/Р60/Р82/Р101): the snapshot FRAME —
    // header (version, epoch, tick, flags) plus tagged, length-prefixed
    // blocks, read through a cursor that never throws on any byte sequence.
    // The state blocks themselves are Task 27; nothing here writes a game
    // field.
    //
    // WHY SO MANY LITERAL-BYTE ASSERTIONS. Task 25's fix rounds established
    // that round-trip tests are BLIND to any mutation applied symmetrically
    // to writer and reader — byte order, field offsets, swapped tags all
    // survive `Assert.AreEqual(original, roundTripped)` untouched. This
    // frame is the substrate of Tasks 27/28/29/32, so its layout is pinned
    // against INDEPENDENTLY spelled-out bytes, not against the reader.
    //
    // FIXTURE NUMBERS. Every DATA fixture below (epoch, tick, flags, block
    // kinds, payload bytes, payload lengths) was checked by grep against
    // client/Assets/Data/*.asset and appears in none of them — the trap that
    // caught three fixtures earlier in this phase (3.8 = MaxAimHeight,
    // 100 = MaxHp, 1.35 = BodyTop). VERIFIED STATEMENT, third attempt — the
    // history is the lesson. The first note said "19, 22 and 14 all occur in
    // ArenaConfig.asset"; a fix-round "corrected" it to "none of the three
    // lives in ArenaConfig", which was WORSE: that grep matched only
    // `key: value` at end of line and could not see inline values, and
    // ArenaConfig.asset does hold 14 and 22 as obstacle coordinates
    // (`- Pos: {x: 14, y: -9}` :25 and `{x: 30, y: 22}` :29). Re-checked with
    // a token-boundary grep across every .asset: 14 also lives in
    // MobGunnerConfig.ProjectileSpeed and GameFeelConfig.SlideDustBurstCount,
    // 22 also in GameFeelConfig.ShakeFrequency, and 19 appears nowhere as a
    // value. Only the claim about 19 was ever wrong. Moral, and the reason
    // this paragraph is long: a universal negative ("appears in none of
    // them") is only as good as the pattern that searched for it — state
    // where you looked, not just what you concluded. The truncation
    // boundaries below (8, 14, 21) come from SnapshotWriter's constants
    // anyway, which is right for a structural reason, not this one: they are
    // dictated by the layout, so spelling them out would freeze a number the
    // constants already own.
    // Structural numbers (byte offsets, buffer padding, sentinel tails) are
    // not fixtures: they are dictated by the wire layout or are pure test
    // scaffolding, and colliding with a balance number carries no meaning
    // for them.
    public class SnapshotCodecTests
    {
        const ushort Epoch = 0x1234;        // 4660
        const uint Tick = 0xDEADBEEFu;      // 3735928559 — all four bytes differ
        const byte Flags = 0xA5;            // 165 — reserved byte, still round-trips
        const byte KindA = 0x2A;            // 42
        const byte KindB = 0x5C;            // 92
        const byte UnknownKind = 0x7B;      // 123 — a kind no reader below knows
        const byte Sentinel = 0xCC;         // 204 — untouched-memory marker

        // A payload whose FIRST byte (0) differs from its LENGTH (3). That
        // gap is what makes the "skip an unknown block by its record count
        // instead of by payloadBytes" mutation observable: a reader that
        // treats the first payload byte as a count advances 0 bytes and then
        // reads that same 0 as the next block kind.
        static readonly byte[] PayloadA = { 0x00, 0x11, 0x27 };          // 0, 17, 39
        static readonly byte[] PayloadB = { 0x3D, 0x5C, 0x71, 0x4B };    // 61, 92, 113, 75

        // 291 bytes: the only payload length whose u16 encoding has BOTH
        // bytes non-zero AND different from each other (0x0123 -> 0x23 0x01
        // little-endian, 0x01 0x23 big-endian), so the length field's byte
        // order is pinned by inspection rather than by round-trip.
        const int LargePayloadBytes = 291;

        static readonly byte[] KnownAB = { KindA, KindB };
        static readonly byte[] KnownA = { KindA };
        static readonly byte[] KnownNone = new byte[0];

        static byte[] MakeLargePayload()
        {
            var payload = new byte[LargePayloadBytes];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)((i * 7 + 0x11) & 0xFF);
            return payload;
        }

        static byte[] Filled(int length)
        {
            var buffer = new byte[length];
            for (int i = 0; i < buffer.Length; i++) buffer[i] = Sentinel;
            return buffer;
        }

        /// Header + PayloadA under KindA + PayloadB under KindB, written into
        /// a fresh array sized exactly to the frame. Returns the frame.
        static byte[] BuildTwoBlockFrame()
        {
            int size = SnapshotWriter.HeaderBytes
                       + SnapshotWriter.BlockHeaderBytes + PayloadA.Length
                       + SnapshotWriter.BlockHeaderBytes + PayloadB.Length;
            var buffer = new byte[size];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteBlock(KindA, PayloadA);
            writer.WriteBlock(KindB, PayloadB);
            Assert.AreEqual(size, writer.BytesWritten,
                "fixture premise: the frame must fill the buffer exactly");
            return buffer;
        }

        static void AssertPayloadEquals(byte[] expected, System.ReadOnlySpan<byte> actual, string what)
        {
            Assert.AreEqual(expected.Length, actual.Length, $"{what}: payload length");
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], actual[i], $"{what}: payload byte {i}");
        }

        // ---- 1. Empty snapshot: header alone round-trips and ends cleanly ----

        [Test]
        public void EmptySnapshot_HeaderRoundTrips_AndBlockStreamEndsCleanly()
        {
            var buffer = Filled(SnapshotWriter.HeaderBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            // Fix-round 2 finding F-D: FreeBytes shipped with no test, so a
            // mutation of its formula would have gone unnoticed — in a round
            // whose whole point was closing mutation gaps. Task 28 asks this
            // on every entity to decide whether the next block still fits.
            Assert.AreEqual(buffer.Length - SnapshotWriter.HeaderBytes, writer.FreeBytes,
                "FreeBytes must report the room left after the header");
            Assert.AreEqual(SnapshotWriter.HeaderBytes, writer.BytesWritten,
                "a header-only snapshot occupies exactly HeaderBytes");

            var reader = new SnapshotReader(buffer);
            Assert.IsTrue(reader.TryReadHeader(out ushort epoch, out uint tick, out byte flags),
                "a well-formed header must be accepted");
            Assert.AreEqual(Epoch, epoch, "epoch must round-trip");
            Assert.AreEqual(Tick, tick, "tick must round-trip");
            Assert.AreEqual(Flags, flags,
                "the reserved flags byte must round-trip verbatim even with no bit assigned yet");

            Assert.IsFalse(reader.TryReadBlock(KnownAB, out _, out _),
                "a header-only snapshot has no blocks");
            Assert.IsFalse(reader.Failed, "running out of blocks cleanly is not a failure");
            Assert.IsFalse(reader.Truncated, "a snapshot ending on a block boundary is not truncated");
            Assert.AreEqual(0, reader.SkippedBlockCount, "nothing was skipped");
        }

        // ---- 2. Header byte layout, spelled out ----

        [Test]
        public void Header_ByteLayout_IsVersionEpochTickFlags_LittleEndian()
        {
            const int tailBytes = 4;
            var buffer = Filled(SnapshotWriter.HeaderBytes + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);

            // Literal 3, not ProtocolVersion.Current: comparing the writer
            // against the very constant it wrote would pass under a version
            // bump that silently broke every peer. The literal is therefore
            // MEANT to be edited by hand on a bump — it moved 1 → 2 with
            // Task 44a's ProjectileEndKind growth, then 2 → 3 with Stage 3
            // Task 10's MobType growth, alongside the pin in
            // ProtocolVersion_Current_IsPinnedToThree below.
            Assert.AreEqual((byte)3, buffer[0], "byte 0: protocol version");
            Assert.AreEqual((byte)0x34, buffer[1], "byte 1: epoch low byte (little-endian)");
            Assert.AreEqual((byte)0x12, buffer[2], "byte 2: epoch high byte (little-endian)");
            Assert.AreEqual((byte)0xEF, buffer[3], "byte 3: tick byte 0 (little-endian)");
            Assert.AreEqual((byte)0xBE, buffer[4], "byte 4: tick byte 1 (little-endian)");
            Assert.AreEqual((byte)0xAD, buffer[5], "byte 5: tick byte 2 (little-endian)");
            Assert.AreEqual((byte)0xDE, buffer[6], "byte 6: tick byte 3 (little-endian)");
            Assert.AreEqual(Flags, buffer[7], "byte 7: reserved flags");

            Assert.AreEqual(8, SnapshotWriter.HeaderBytes,
                "the header is 8 bytes: version(1) + epoch(2) + tick(4) + flags(1)");
            for (int i = SnapshotWriter.HeaderBytes; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the header");
        }

        // ---- 3. Block byte layout, spelled out ----

        [Test]
        public void Block_ByteLayout_IsKindThenLengthLittleEndian_ThenPayload()
        {
            byte[] large = MakeLargePayload();
            const int tailBytes = 4;
            int frameSize = SnapshotWriter.HeaderBytes
                            + SnapshotWriter.BlockHeaderBytes + PayloadA.Length
                            + SnapshotWriter.BlockHeaderBytes + large.Length;
            var buffer = Filled(frameSize + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteBlock(KindA, PayloadA);
            writer.WriteBlock(KindB, large);

            Assert.AreEqual(3, SnapshotWriter.BlockHeaderBytes,
                "a block header is 3 bytes: kind(1) + payloadBytes(2). Task 26 decision: the "
                + "length is in BYTES, not a record COUNT, so an unknown kind can be skipped "
                + "without knowing its record size");
            Assert.AreEqual(frameSize, writer.BytesWritten, "two blocks plus header, no padding");

            int first = SnapshotWriter.HeaderBytes;
            Assert.AreEqual(KindA, buffer[first], "block 1 byte 0: kind");
            Assert.AreEqual((byte)0x03, buffer[first + 1], "block 1 byte 1: payloadBytes low (little-endian)");
            Assert.AreEqual((byte)0x00, buffer[first + 2], "block 1 byte 2: payloadBytes high (little-endian)");
            for (int i = 0; i < PayloadA.Length; i++)
                Assert.AreEqual(PayloadA[i], buffer[first + SnapshotWriter.BlockHeaderBytes + i],
                    $"block 1 payload byte {i}");

            int second = first + SnapshotWriter.BlockHeaderBytes + PayloadA.Length;
            Assert.AreEqual(KindB, buffer[second], "block 2 byte 0: kind — blocks are contiguous, no padding");
            Assert.AreEqual((byte)0x23, buffer[second + 1], "block 2 byte 1: payloadBytes low (0x0123 -> 0x23)");
            Assert.AreEqual((byte)0x01, buffer[second + 2], "block 2 byte 2: payloadBytes high (0x0123 -> 0x01)");
            Assert.AreEqual(large[0], buffer[second + SnapshotWriter.BlockHeaderBytes], "block 2 payload byte 0");
            Assert.AreEqual(large[large.Length - 1], buffer[second + SnapshotWriter.BlockHeaderBytes + large.Length - 1],
                "block 2 payload last byte");

            for (int i = frameSize; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the frame");
        }

        // ---- 4. Blocks round-trip in write order, payloads intact ----

        [Test]
        public void Blocks_RoundTrip_InWriteOrder_WithPayloadsIntact()
        {
            byte[] frame = BuildTwoBlockFrame();

            var reader = new SnapshotReader(frame);
            Assert.IsTrue(reader.TryReadHeader(out ushort epoch, out uint tick, out byte flags));
            Assert.AreEqual(Epoch, epoch);
            Assert.AreEqual(Tick, tick);
            Assert.AreEqual(Flags, flags);

            Assert.IsTrue(reader.TryReadBlock(KnownAB, out byte kind1, out System.ReadOnlySpan<byte> payload1),
                "the first block must be delivered");
            Assert.AreEqual(KindA, kind1, "blocks arrive in write order — KindA was written first");
            AssertPayloadEquals(PayloadA, payload1, "block 1");

            Assert.IsTrue(reader.TryReadBlock(KnownAB, out byte kind2, out System.ReadOnlySpan<byte> payload2),
                "the second block must be delivered");
            Assert.AreEqual(KindB, kind2);
            AssertPayloadEquals(PayloadB, payload2, "block 2");

            Assert.IsFalse(reader.TryReadBlock(KnownAB, out _, out _), "the stream is exhausted");
            Assert.IsFalse(reader.Failed, "clean exhaustion is not a failure");
            Assert.IsFalse(reader.Truncated);
            Assert.AreEqual(0, reader.SkippedBlockCount, "both kinds were known — nothing to skip");
        }

        // ---- 5. Unknown kinds are skipped by payloadBytes and counted ----

        [Test]
        public void UnknownBlockKind_IsSkippedByPayloadBytes_AndCounted()
        {
            int size = SnapshotWriter.HeaderBytes
                       + SnapshotWriter.BlockHeaderBytes + PayloadA.Length
                       + SnapshotWriter.BlockHeaderBytes + PayloadB.Length;
            var buffer = new byte[size];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteBlock(UnknownKind, PayloadA);   // a kind from some later stage
            writer.WriteBlock(KindB, PayloadB);         // ...followed by one we do know

            var reader = new SnapshotReader(buffer);
            Assert.IsTrue(reader.TryReadHeader(out _, out _, out _));
            Assert.IsTrue(reader.TryReadBlock(KnownAB, out byte kind, out System.ReadOnlySpan<byte> payload),
                "the reader must step OVER the unknown block and reach the known one (Р29)");
            Assert.AreEqual(KindB, kind, "the delivered block must be the known one, not the unknown one");
            AssertPayloadEquals(PayloadB, payload, "known block after a skip");
            Assert.AreEqual(1, reader.SkippedBlockCount, "the skipped block must be counted (Р29)");
            Assert.IsFalse(reader.Failed, "an unknown kind is forward compatibility, not a failure");
            Assert.IsFalse(reader.Truncated);

            Assert.IsFalse(reader.TryReadBlock(KnownAB, out _, out _), "the stream is exhausted");
            Assert.AreEqual(1, reader.SkippedBlockCount, "the counter must not move at end of stream");
        }

        [Test]
        public void AllKindsUnknown_EverythingSkipped_StreamStillEndsCleanly()
        {
            byte[] frame = BuildTwoBlockFrame();

            var reader = new SnapshotReader(frame);
            Assert.IsTrue(reader.TryReadHeader(out _, out _, out _));
            Assert.IsFalse(reader.TryReadBlock(KnownNone, out _, out _),
                "a reader that knows no kind at all delivers nothing");
            Assert.AreEqual(2, reader.SkippedBlockCount, "both blocks must be counted as skipped");
            Assert.IsFalse(reader.Failed,
                "walking off the end of a fully-unknown stream is forward compatibility, not corruption");
            Assert.IsFalse(reader.Truncated);
        }

        [Test]
        public void KnownKindsAreConsulted_NotAssumed()
        {
            // Two readers over the SAME bytes with DIFFERENT known sets must
            // disagree — otherwise `knownKinds` is decorative and the skip
            // logic is really "deliver everything" or "deliver the first".
            byte[] frame = BuildTwoBlockFrame();

            var readerA = new SnapshotReader(frame);
            Assert.IsTrue(readerA.TryReadHeader(out _, out _, out _));
            Assert.IsTrue(readerA.TryReadBlock(KnownA, out byte kindA, out _));
            Assert.AreEqual(KindA, kindA);
            Assert.IsFalse(readerA.TryReadBlock(KnownA, out _, out _),
                "KindB is unknown to this reader and must be skipped, not delivered");
            Assert.AreEqual(1, readerA.SkippedBlockCount);

            var readerB = new SnapshotReader(frame);
            Assert.IsTrue(readerB.TryReadHeader(out _, out _, out _));
            Assert.IsTrue(readerB.TryReadBlock(KnownAB, out _, out _));
            Assert.IsTrue(readerB.TryReadBlock(KnownAB, out byte kindB, out _),
                "the same bytes must yield KindB to a reader that knows it");
            Assert.AreEqual(KindB, kindB);
            Assert.AreEqual(0, readerB.SkippedBlockCount);
        }

        // ---- 6. Truncation at EVERY length: never throws, always reported ----

        [Test]
        public void TruncatedFrame_AtEveryLength_NeverThrows_AndReportsTruncation()
        {
            byte[] frame = BuildTwoBlockFrame();

            // Cutting a frame exactly on a block boundary yields a SHORTER
            // WELL-FORMED frame, not a truncated one — those three lengths
            // must be reported clean. Every other length must be reported
            // truncated. Computed from the writer's own constants because
            // they are structural, not fixture, numbers — see the fixture
            // note at the top of this file for why "14 and 22 collide with
            // ArenaConfig" is true but is NOT the reason.
            int boundary0 = SnapshotWriter.HeaderBytes;
            int boundary1 = boundary0 + SnapshotWriter.BlockHeaderBytes + PayloadA.Length;
            int boundary2 = boundary1 + SnapshotWriter.BlockHeaderBytes + PayloadB.Length;
            Assert.AreEqual(frame.Length, boundary2, "fixture premise: boundary2 is the whole frame");

            for (int length = frame.Length; length >= 0; length--)
            {
                bool clean = length == boundary0 || length == boundary1 || length == boundary2;

                bool headerOk = false;
                bool failed = false;
                bool truncated = false;
                bool versionMismatch = false;
                ushort epoch = 0xFFFF;
                uint tick = 0xFFFFFFFFu;
                int cut = length;

                Assert.DoesNotThrow(() =>
                {
                    var reader = new SnapshotReader(new System.ReadOnlySpan<byte>(frame, 0, cut));
                    headerOk = reader.TryReadHeader(out epoch, out tick, out _);
                    while (reader.TryReadBlock(KnownAB, out _, out _)) { }
                    failed = reader.Failed;
                    truncated = reader.Truncated;
                    versionMismatch = reader.VersionMismatch;
                },
                    $"length {cut}: a truncated datagram is ordinary input (loss, MTU, a hostile "
                    + "client) — the reader must never throw (Р82)");

                Assert.IsFalse(versionMismatch, $"length {cut}: the version byte is intact in every prefix");

                if (clean)
                {
                    Assert.IsTrue(headerOk, $"length {cut}: a prefix ending on a block boundary is well formed");
                    Assert.IsFalse(failed, $"length {cut}: a prefix ending on a block boundary must not fail");
                    Assert.IsFalse(truncated, $"length {cut}: a prefix ending on a block boundary is not truncated");
                }
                else
                {
                    Assert.IsTrue(truncated, $"length {cut}: a mid-field cut must be reported as truncated");
                    Assert.IsTrue(failed, $"length {cut}: a truncated frame must leave the cursor failed");
                    if (cut < SnapshotWriter.HeaderBytes)
                    {
                        Assert.IsFalse(headerOk, $"length {cut}: an incomplete header must be refused");
                        Assert.AreEqual((ushort)0, epoch,
                            $"length {cut}: a refused header must not hand back a half-parsed epoch");
                        Assert.AreEqual(0u, tick,
                            $"length {cut}: a refused header must not hand back a half-parsed tick");
                    }
                }
            }
        }

        [Test]
        public void EmptyAndSingleByteInput_AreRefusedWithoutThrowing()
        {
            bool headerOk = true;
            bool truncated = false;
            bool blockOk = true;

            Assert.DoesNotThrow(() =>
            {
                var reader = new SnapshotReader(System.ReadOnlySpan<byte>.Empty);
                headerOk = reader.TryReadHeader(out _, out _, out _);
                blockOk = reader.TryReadBlock(KnownAB, out _, out _);
                truncated = reader.Truncated;
            }, "an empty payload must not throw (Р82)");
            Assert.IsFalse(headerOk, "an empty payload cannot carry a header");
            Assert.IsFalse(blockOk, "an empty payload cannot carry a block either");
            Assert.IsTrue(truncated, "an empty payload is a truncated one");

            var single = new byte[] { ProtocolVersion.Current };
            headerOk = true;
            truncated = false;
            Assert.DoesNotThrow(() =>
            {
                var reader = new SnapshotReader(single);
                headerOk = reader.TryReadHeader(out _, out _, out _);
                truncated = reader.Truncated;
            }, "a one-byte payload must not throw (Р82)");
            Assert.IsFalse(headerOk, "a one-byte payload cannot carry a header");
            Assert.IsTrue(truncated, "a one-byte payload is a truncated header");
        }

        // ---- 7. Version mismatch: refused BEFORE anything else is parsed ----

        [Test]
        public void VersionMismatch_IsRefusedBeforeAnyOtherFieldIsParsed()
        {
            byte[] frame = BuildTwoBlockFrame();
            frame[0] = (byte)(ProtocolVersion.Current + 1);

            var reader = new SnapshotReader(frame);
            Assert.IsFalse(reader.TryReadHeader(out ushort epoch, out uint tick, out byte flags),
                "a snapshot from another protocol version must be refused");
            Assert.IsTrue(reader.VersionMismatch, "the refusal reason must be reported as a version mismatch");
            Assert.IsFalse(reader.Truncated, "a full-length frame is not truncated");
            Assert.IsTrue(reader.Failed, "a refused header leaves the cursor failed");
            Assert.AreEqual((ushort)0, epoch, "a refused header must not hand back a decoded epoch");
            Assert.AreEqual(0u, tick, "a refused header must not hand back a decoded tick");
            Assert.AreEqual((byte)0, flags, "a refused header must not hand back decoded flags");

            // THE ORDERING PROBE. The assertions above hold just as well if
            // the version check runs AFTER epoch/tick/flags are parsed — the
            // out-parameters are zeroed on failure either way. A payload that
            // holds NOTHING BUT a wrong version byte separates the two: a
            // reader that checks first reports a version mismatch and no
            // truncation; a reader that parses first runs out of bytes at
            // the epoch and reports truncation instead.
            var versionOnly = new byte[] { (byte)(ProtocolVersion.Current + 1) };
            var probe = new SnapshotReader(versionOnly);
            Assert.IsFalse(probe.TryReadHeader(out _, out _, out _));
            Assert.IsTrue(probe.VersionMismatch,
                "the version must be checked BEFORE the rest of the header is parsed");
            Assert.IsFalse(probe.Truncated,
                "reporting truncation here means the version check happens too late");
        }

        [Test]
        public void ProtocolVersion_Current_IsPinnedToThree()
        {
            // A silent bump would part client and server with no red test
            // anywhere: the version is compared in the handshake (Task 39)
            // and on every snapshot, and both sides read the same constant.
            //
            // Stage 3 Task 10 (R-6, errata E-6 B-I7): renamed from
            // …IsPinnedToTwo — the old name was already lying about which
            // literal it pinned the moment MobType grew Elite/Director; a
            // test named after a stale value is worse than an unnamed one.
            Assert.AreEqual((byte)3, ProtocolVersion.Current,
                "protocol version 3 is the wire contract from Stage 3 Task 10 on — changing it "
                + "is a compatibility break that must be a deliberate, reviewed edit. It became "
                + "3 when MobType grew Elite = 2 and Director = 3: a version-2 reader validates "
                + "a Mobs record's type nibble against its own MaxMobTypeValue bound of 1 "
                + "(Gunner) and throws the whole record out as MalformedContent, and "
                + "SimConfigHash does not cover Elite's/Director's MobSimConfig sections yet "
                + "(R-17, Т13 wires them), so nothing but this version byte separates the two "
                + "builds. It became 2 in Task 44a, when ProjectileEndKind grew HitPlayer = 4 — "
                + "see ProtocolVersion's own HISTORY doc for that entry.");
        }

        // ---- 8. Failure is sticky ----

        [Test]
        public void Reader_AfterTruncation_StaysFailedAndDeliversNothing()
        {
            byte[] frame = BuildTwoBlockFrame();
            var cut = new System.ReadOnlySpan<byte>(frame, 0, frame.Length - 1);

            var reader = new SnapshotReader(cut);
            Assert.IsTrue(reader.TryReadHeader(out _, out _, out _));
            Assert.IsTrue(reader.TryReadBlock(KnownAB, out byte kind, out _), "the intact first block is delivered");
            Assert.AreEqual(KindA, kind);
            Assert.IsFalse(reader.TryReadBlock(KnownAB, out _, out _), "the cut second block is not");
            Assert.IsTrue(reader.Truncated);
            Assert.IsTrue(reader.Failed);

            for (int i = 0; i < 3; i++)
            {
                Assert.IsFalse(reader.TryReadBlock(KnownAB, out _, out _),
                    "every read after a failure must return false");
                Assert.IsTrue(reader.Failed, "the failed state is sticky");
            }
            Assert.IsFalse(reader.TryReadHeader(out _, out _, out _),
                "even a header re-read must be refused once the cursor has failed");
        }

        [Test]
        public void Reader_BlockBeforeHeader_FailsInsteadOfParsingHeaderBytesAsABlock()
        {
            // Not a hostile-input case but a caller-sequencing one. It is
            // guarded all the same because the alternative to a guard is
            // undefined behavior in a parser of untrusted bytes: without it
            // the header's own bytes would be handed back as a block whose
            // "kind" is the protocol version.
            byte[] frame = BuildTwoBlockFrame();

            var reader = new SnapshotReader(frame);
            Assert.IsFalse(reader.TryReadBlock(KnownAB, out byte kind, out _),
                "no block may be read before the header");
            Assert.AreEqual((byte)0, kind, "a refused block read must not hand back a kind");
            Assert.IsTrue(reader.Failed, "reading out of order poisons the cursor");
            Assert.IsFalse(reader.TryReadHeader(out _, out _, out _),
                "and the poisoned cursor refuses the header too");

            // Fix-round 2 finding F-C: the fixture below is built so the
            // first block's kind byte EQUALS ProtocolVersion.Current. With
            // the re-read guard removed, the second TryReadHeader therefore
            // parses a phantom but perfectly VALID header out of the block
            // stream and returns true — so this assertion fails directly on
            // the mutation, instead of passing because the refusal happened
            // to come from a version mismatch. Earlier versions of this test
            // depended on that accident, which Task 27 is free to break the
            // moment it assigns a kind equal to 1.
            byte[] phantom = BuildFrameWhoseFirstBlockKindIs(ProtocolVersion.Current);
            var twice = new SnapshotReader(phantom);
            Assert.IsTrue(twice.TryReadHeader(out _, out _, out _));
            Assert.IsFalse(twice.TryReadHeader(out _, out _, out _),
                "a second header read is a sequencing error, not a rewind — "
                + "even when the bytes at the cursor would parse as a valid header");
            Assert.IsTrue(twice.Failed);
            // Fix-round finding F7: without these two the test passed with
            // the guard REMOVED — the second call would have parsed the
            // first block's kind byte as a version, mismatched, and refused
            // for the wrong reason. Both assertions below distinguish "I
            // refuse because you called me out of order" from "I refuse
            // because the bytes are wrong", and the accident that saved the
            // old test (block kind != 1) is a fixture detail Task 27 is free
            // to break when it assigns real kind values.
            Assert.IsFalse(twice.VersionMismatch,
                "a sequencing refusal must not masquerade as a version mismatch");
            Assert.IsFalse(twice.Truncated,
                "a sequencing refusal must not masquerade as truncation");
        }

        [Test]
        public void Reader_AfterMalformedLength_DoesNotResumeOnAttackerChosenBytes()
        {
            // Fix-round finding F6: removing the sticky `if (_failed)` guard
            // from TryReadBlock survived the whole suite, because the only
            // failure fixture left nothing parseable behind the cut. This is
            // the case that matters — a hostile client, which is exactly who
            // Р82 is about: an unknown block DECLARES a length far past the
            // end of the frame, and a well-formed known block follows it. A
            // reader that forgets it already failed hands the attacker the
            // block of their choosing after the refusal.
            var frame = new System.Collections.Generic.List<byte>();
            frame.Add(ProtocolVersion.Current);
            frame.Add(0x34); frame.Add(0x12);                    // epoch, LE
            frame.Add(0xEF); frame.Add(0xBE); frame.Add(0xAD); frame.Add(0xDE); // tick, LE
            frame.Add(0);                                        // flags
            frame.Add(UnknownKind);
            frame.Add(0xFF); frame.Add(0xFF);                    // payloadBytes: a lie
            frame.Add(KindA);
            frame.Add(0x03); frame.Add(0x00);                    // a well-formed follower
            frame.AddRange(PayloadA);

            var reader = new SnapshotReader(frame.ToArray());
            Assert.IsTrue(reader.TryReadHeader(out _, out _, out _), "the header itself is intact");
            Assert.IsFalse(reader.TryReadBlock(KnownAB, out _, out _),
                "a declared length past the end of the frame must be refused");
            Assert.IsTrue(reader.Failed);

            Assert.IsFalse(reader.TryReadBlock(KnownAB, out byte kind, out _),
                "the attacker's chosen block must NOT be delivered after a refusal");
            Assert.AreEqual((byte)0, kind, "a refused read hands back no kind");
        }

        /// A frame whose FIRST BLOCK's kind byte is chosen by the caller —
        /// used to make a phantom re-read of the header parse cleanly (F-C).
        static byte[] BuildFrameWhoseFirstBlockKindIs(byte kind)
        {
            var f = new System.Collections.Generic.List<byte>();
            f.Add(ProtocolVersion.Current);
            f.Add(0x34); f.Add(0x12);                                       // epoch, LE
            f.Add(0xEF); f.Add(0xBE); f.Add(0xAD); f.Add(0xDE);             // tick, LE
            f.Add(0);                                                       // flags
            f.Add(kind);
            f.Add((byte)PayloadA.Length); f.Add(0);                         // payloadBytes, LE
            f.AddRange(PayloadA);
            // Enough bytes follow the block header for a phantom 8-byte
            // header to be read in full, so the mutation cannot be caught by
            // truncation instead of by the guard.
            f.AddRange(PayloadB);
            f.AddRange(PayloadB);
            return f.ToArray();
        }

        // ---- 9. The write side throws — and never writes partially ----

        [Test]
        public void Writer_HeaderDoesNotFit_ThrowsAndLeavesBufferUntouched()
        {
            var buffer = Filled(SnapshotWriter.HeaderBytes - 1);
            Assert.Throws<System.InvalidOperationException>(() =>
            {
                var writer = new SnapshotWriter(buffer);
                writer.WriteHeader(Epoch, Tick, Flags);
            }, "the write side owns its budget (Task 28) — running out of room is a caller bug, "
               + "the mirror image of the read side's never-throw contract");

            foreach (byte b in buffer)
                Assert.AreEqual(Sentinel, b, "a rejected header must not write a single byte");
        }

        [Test]
        public void Writer_BlockDoesNotFit_ThrowsAndLeavesTheRestOfTheBufferUntouched()
        {
            // One byte short of the second block's payload.
            int firstBlockEnd = SnapshotWriter.HeaderBytes + SnapshotWriter.BlockHeaderBytes + PayloadA.Length;
            int size = firstBlockEnd + SnapshotWriter.BlockHeaderBytes + PayloadB.Length - 1;
            var buffer = Filled(size);

            // The writer is a ref struct, so it cannot be captured by the
            // lambda — the whole sequence runs inside, and what is inspected
            // afterwards is the buffer it wrote into.
            Assert.Throws<System.InvalidOperationException>(() =>
            {
                var writer = new SnapshotWriter(buffer);
                writer.WriteHeader(Epoch, Tick, Flags);
                writer.WriteBlock(KindA, PayloadA);
                writer.WriteBlock(KindB, PayloadB);
            });

            Assert.AreEqual(KindA, buffer[SnapshotWriter.HeaderBytes],
                "the first block must still be there — the rejection happened at the SECOND one");
            for (int i = firstBlockEnd; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i],
                    $"byte {i}: a rejected block must not write its kind or length either");
        }

        [Test]
        public void Writer_PayloadLongerThanTheLengthField_Throws()
        {
            var payload = new byte[SnapshotWriter.MaxBlockPayloadBytes + 1];
            var buffer = new byte[SnapshotWriter.HeaderBytes
                                  + SnapshotWriter.BlockHeaderBytes + payload.Length];
            Assert.AreEqual(65535, SnapshotWriter.MaxBlockPayloadBytes,
                "the length field is u16, so a block payload tops out at 65535 bytes");

            Assert.Throws<System.ArgumentException>(() =>
            {
                var writer = new SnapshotWriter(buffer);
                writer.WriteHeader(Epoch, Tick, Flags);
                writer.WriteBlock(KindA, payload);
            }, "a payload that cannot be described by the length field must be refused loudly, "
               + "not silently truncated to its low 16 bits");
        }

        [Test]
        public void Block_EmptyPayload_RoundTrips()
        {
            int size = SnapshotWriter.HeaderBytes + SnapshotWriter.BlockHeaderBytes;
            var buffer = new byte[size];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteBlock(KindA, System.ReadOnlySpan<byte>.Empty);
            Assert.AreEqual(size, writer.BytesWritten, "an empty block is just its 3-byte header");

            var reader = new SnapshotReader(buffer);
            Assert.IsTrue(reader.TryReadHeader(out _, out _, out _));
            Assert.IsTrue(reader.TryReadBlock(KnownAB, out byte kind, out System.ReadOnlySpan<byte> payload),
                "a block with an empty payload is still a block (Task 27 will have kinds whose "
                + "count can legitimately be zero)");
            Assert.AreEqual(KindA, kind);
            Assert.AreEqual(0, payload.Length);
            Assert.IsFalse(reader.Failed);
            Assert.IsFalse(reader.Truncated);
        }

        // ---- 10. Zero allocations on the write+read pair ----

        [Test]
        public void WriteThenRead_DoesNotAllocateGCMemory()
        {
            var buffer = new byte[SnapshotWriter.HeaderBytes
                                  + SnapshotWriter.BlockHeaderBytes + PayloadA.Length
                                  + SnapshotWriter.BlockHeaderBytes + PayloadB.Length];
            byte[] payloadA = PayloadA;
            byte[] payloadB = PayloadB;
            byte[] known = KnownAB;

            // Warm-up OUTSIDE the measured lambda (Task 24 finding F9): the
            // first call of each method is JIT-compiled otherwise, and the
            // test would be measuring the JIT.
            //
            // STRENGTHENING (task-26-brief §4): this test passed on the
            // constant stubs, because a writer that writes nothing and a
            // reader that returns false allocate nothing either. The warm-up
            // is therefore ALSO the fixture premise — it asserts that the
            // measured body really does write a full frame and read both
            // blocks back, so "allocates nothing" cannot be satisfied by
            // "does nothing".
            {
                var warmWriter = new SnapshotWriter(buffer);
                warmWriter.WriteHeader(Epoch, Tick, Flags);
                warmWriter.WriteBlock(KindA, payloadA);
                warmWriter.WriteBlock(KindB, payloadB);
                Assert.AreEqual(buffer.Length, warmWriter.BytesWritten,
                    "fixture premise (stub-defeating): the measured body must write the whole frame");

                var warmReader = new SnapshotReader(buffer);
                Assert.IsTrue(warmReader.TryReadHeader(out ushort epoch, out uint tick, out byte flags),
                    "fixture premise (stub-defeating): the measured body must parse a header");
                Assert.AreEqual(Epoch, epoch);
                Assert.AreEqual(Tick, tick);
                Assert.AreEqual(Flags, flags);

                int delivered = 0;
                while (warmReader.TryReadBlock(known, out _, out System.ReadOnlySpan<byte> body))
                {
                    Assert.AreNotEqual(0, body.Length, "fixture premise: both fixture blocks carry bytes");
                    delivered++;
                }
                Assert.AreEqual(2, delivered,
                    "fixture premise (stub-defeating): the measured body must deliver both blocks");
                Assert.IsFalse(warmReader.Failed);
            }

            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var writer = new SnapshotWriter(buffer);
                    writer.WriteHeader(Epoch, Tick, Flags);
                    writer.WriteBlock(KindA, payloadA);
                    writer.WriteBlock(KindB, payloadB);

                    var reader = new SnapshotReader(new System.ReadOnlySpan<byte>(buffer, 0, writer.BytesWritten));
                    reader.TryReadHeader(out _, out _, out _);
                    while (reader.TryReadBlock(known, out _, out _)) { }
                }
            }, Is.Not.AllocatingGCMemory());
        }

        // ---- 11. The broadcast envelope ----

        [Test]
        public void SnapshotBroadcast_IsAStructImplementingIBroadcast()
        {
            // FishNet constrains every broadcast to `where T : struct,
            // IBroadcast` (Task 2 notes §1, Runtime/Broadcast/IBroadcast.cs).
            // Turning this into a class compiles fine on its own — the
            // interface is a marker with no members — and only breaks at the
            // Broadcast<T> call site — Task 36 (server send) and Task 32
            // (client receive), NOT Task 34, which owns prediction only.
            // Pinned here so the break
            // lands in Task 26's own suite instead.
            Assert.IsTrue(typeof(SnapshotBroadcast).IsValueType,
                "SnapshotBroadcast must be a struct — FishNet's Broadcast<T> is constrained to structs");
            Assert.IsTrue(typeof(IBroadcast).IsAssignableFrom(typeof(SnapshotBroadcast)),
                "SnapshotBroadcast must implement IBroadcast");
        }

        [Test]
        public void SnapshotBroadcast_CarriesExactlyTickEpochAndAnArraySegmentPayload()
        {
            // `byte[]` would allocate on EVERY deserialization, thirty times a
            // second per client; ArraySegment<byte> is serialized by FishNet's
            // own default writer/reader with no allocation at all (Task 2
            // notes §2). Same guard shape as InputCodecTests.
            // EveryFieldOfSimInput_IsCarriedOnTheWire: it does not prove a new
            // field is carried, it forces whoever adds one to come here.
            var fields = new System.Collections.Generic.List<string>();
            foreach (System.Reflection.FieldInfo f in typeof(SnapshotBroadcast).GetFields())
                fields.Add($"{f.FieldType.Name} {f.Name}");

            CollectionAssert.AreEquivalent(
                new[] { "UInt32 Tick", "UInt16 MatchEpoch", "ArraySegment`1 Payload" },
                fields,
                "SnapshotBroadcast gained, lost or retyped a field — the payload must stay "
                + "ArraySegment<byte> (byte[] allocates per message) and Tick/MatchEpoch stay "
                + "duplicated in the envelope on purpose: the transport layer reads them before "
                + "the payload is parsed at all");
        }

        // ==================================================================
        // Stage 2 Task 27 (spec §3.8, §3.12 Р68, task-27-brief): the five
        // state blocks — players, liveness, mobs, wave, events. Task 26's
        // lesson applies again: every byte layout below is pinned against
        // LITERAL bytes computed by this file's OWN double-precision
        // arithmetic (task-27-brief §3 footer), never by calling Quantize —
        // a round-trip-only test is blind to any mutation symmetric across
        // writer and reader (byte order, offsets, swapped fields, which
        // Quantize method feeds which field).
        //
        // FIXTURE CONFIG (SnapRadius/SnapHeroMaxHp/SnapChaserMaxHp/
        // SnapGunnerMaxHp) deliberately avoids every number tracked by a
        // client/Assets/Data/*.asset — same discipline as InputCodecTests'
        // RadiusA/HeightA (ArenaConfig.asset's Radius is 65, HeroConfig.
        // asset's MaxHp is 100, MobChaserConfig.asset's MaxHp is 30,
        // MobGunnerConfig.asset's MaxHp is 20). Every DATA fixture below
        // (positions, hp, ids, seq, tickDelta, waveIndex, aliveCount,
        // payload bytes) was checked with the token-boundary grep the brief
        // specifies:
        //   grep -nP "(?<![\d.])<N>(?![\d.])" client/Assets/Data/*.asset
        // RESULT, STATED AS WHAT WAS SEARCHED (fix-round M4, list completed in
        // fix-round 2). Pattern `(?<![\d.])N(?![\d.])` over all ten
        // `client/Assets/Data/*.asset`, for each of the 32 numbers this region
        // uses as DATA — 52, 118, 47, 33, 11, 23, 37, 41, 19, 67, 29, 733,
        // 61234, 26, 48, 27, 30.5, 14.5, 9001, 71, 191, 53, 31337, 4113, 200,
        // 250, 65543, 87, 126, 145, and the two event KIND bytes 210 (0xD2)
        // and 211 (0xD3), which the first version of this list omitted even
        // though the brief names "kinds" as data (both checked, both clean).
        // Exactly two produce a hit, 19 and 23, and both are the pattern
        // landing inside a GUID hex string on an `m_Script: {..., guid: ...}`
        // line (ArenaConfig.asset:12 and NetConfig.asset:12) — the same false
        // positive this file's Task 26 header note documents for "14 and 22."
        // No fixture matches a VALUE.
        //
        // TWO FIXTURES WERE CHANGED TO GET THERE, and saying so is the point
        // of this note. The fix-round's own first draft asserted the sentence
        // above without running it, and the run then found `5` (proposed for
        // SnapMaxPlayers) living in VisibilityConfig.LingerTicks,
        // GameFeelConfig.HeadHoverPulseHz and NetConfig.LatencySimLossPercent,
        // and `0x5A` = 90 (an event payload byte) living in HeroConfig.Accel.
        // They became 11 and 0x57. The earlier draft of this note had also
        // excused imagined collisions as unavoidable "single-digit counters
        // (tickDelta, aliveCount)" — untrue of this file, whose tickDelta is
        // 191 and 53 and whose aliveCount is 71. Both mistakes are the same
        // one: a claim about a search, written without the search. Structural
        // numbers (byte offsets, record sizes, sentinel tails) are not
        // fixtures, same rule as the rest of this file.
        const float SnapRadius = 52f;
        const float SnapHeroMaxHp = 118f;
        const float SnapChaserMaxHp = 47f;
        const float SnapGunnerMaxHp = 33f;

        /// The Players decoder refuses a slot index at or above
        /// `cfg.Arena.MaxPlayers` (fix-round I1). This fixture is
        /// DELIBERATELY NOT the shipped 3: with 3, the mutation "hardcode
        /// `index >= 3` instead of reading cfg" would be indistinguishable
        /// from the real implementation and would survive the whole suite.
        /// At 11, index 10 must be ACCEPTED and index 11 REFUSED, which no
        /// hardcoded 3 can satisfy — the test below exercises both ends.
        /// (11 rather than 5: 5 is a live value in three balance assets, see
        /// the fixture note above.)
        const int SnapMaxPlayers = 11;

        // Stage 3 Task 25: the Self and ContainerSlots decoders validate item
        // ids against the catalog (an id the catalog does not hold makes
        // ItemCatalogLookup.Find THROW, which is what Р82 forbids reaching
        // from the wire), so the fixture needs one. Ids 23/47/91 are NOT
        // catalog ids in any .asset — the shipped ItemCatalog runs 1..5 —
        // and are far from every wire-domain bound this file pins.
        const byte SnapItemA = 23;
        const byte SnapItemB = 47;
        const byte SnapItemC = 91;
        const byte SnapItemNotInCatalog = 199;

        static readonly ItemDef[] SnapCatalog =
        {
            new ItemDef { Id = SnapItemA, Tier = 1, SlotCost = 1, CreditValue = 10, Kind = ItemKind.Trophy },
            new ItemDef { Id = SnapItemB, Tier = 2, SlotCost = 2, CreditValue = 20, Kind = ItemKind.Trophy },
            new ItemDef { Id = SnapItemC, Tier = 0, SlotCost = 1, CreditValue = 0, Kind = ItemKind.RepairKit },
        };

        static readonly SimConfig SnapCfg = new SimConfig
        {
            Arena = new ArenaSimConfig { Radius = SnapRadius, MaxPlayers = SnapMaxPlayers },
            Hero = new HeroSimConfig { MaxHp = SnapHeroMaxHp },
            Chaser = new MobSimConfig { MaxHp = SnapChaserMaxHp },
            Gunner = new MobSimConfig { MaxHp = SnapGunnerMaxHp },
            Items = SnapCatalog,
        };

        // HALF A QUANTIZATION STEP, DERIVED HERE FROM THE WIRE WIDTHS and
        // never from Quantize (task-27-brief §3 item 9) — asserting against
        // Quantize's own half-step would compare two constants and prove
        // nothing, the exact defect Task 24 F1/F2 named. `Pos` spans
        // [-r, +r] over 65536 codes, so its step is 2r/65535 and half of that
        // is r/65535; `Unit` spans [0, max] over 256 codes; `Dir` spans 360
        // degrees over 256 codes.
        const float HalfStepPosMeters = SnapRadius / 65535f;          // 7.93e-4 m
        const float HalfStepHeroHp = SnapHeroMaxHp / 255f / 2f;       // 0.231
        const float HalfStepChaserHp = SnapChaserMaxHp / 255f / 2f;   // 0.092
        const float HalfStepDirDegrees = 360f / 256f / 2f;            // 0.703 deg

        // Slack for float32 noise on the decode path, and NOTHING MORE
        // (fix-round M7). The first version added 1e-3 m to the position
        // tolerance — larger than the 7.93e-4 half-step itself, so the
        // assertion actually admitted 2.26 half-steps and stopped being the
        // "within half a step" claim it is named for. float32 eps at |v| = 52
        // is about 6.2e-6, so 5e-5 is eight times the worst plausible noise
        // and six per cent of the half-step: comfortably loose against
        // rounding, comfortably tight against a real defect. (Task 24's
        // opposite mistake — slack THINNER than float noise — is why this
        // number is reasoned about rather than guessed.)
        const float PosNoiseMeters = 5e-5f;
        const float HpNoise = 1e-3f;
        const float DirNoiseDegrees = 0.05f;

        // Player fixtures. Wire codes (computed independently, see each
        // test's inline comments): P1 pos (23,-37) -> posX 47261 (0x9D,0xB8),
        // posY 9452 (0xEC,0x24), dir +X -> 128 (0x80), hp 67/118 -> 145
        // (0x91). P2 pos (-41,19) -> posX 6932 (0x14,0x1B), posY 44740
        // (0xC4,0xAE), dir -Y -> 64 (0x40), hp 29/118 -> 63 (0x3F).
        static readonly SnapshotBlocks.PlayerRecord PlayerP1 = new SnapshotBlocks.PlayerRecord
        {
            Index = 0, Pos = new float2(23f, -37f), Dir = new float2(1f, 0f), Hp = 67f,
            Flags = PlayerWireFlags.Alive | PlayerWireFlags.AimHeld, // 0x09
        };
        static readonly SnapshotBlocks.PlayerRecord PlayerP2 = new SnapshotBlocks.PlayerRecord
        {
            Index = 2, Pos = new float2(-41f, 19f), Dir = new float2(0f, -1f), Hp = 29f,
            Flags = PlayerWireFlags.Dashing | PlayerWireFlags.LinkWindow, // 0x12
        };

        // Mob fixtures. M1 (Chaser/Chase): id 733 -> (0xDD,0x02), typeAndAi
        // 0x01, pos (-19,30.5) -> posX 20795 (0x3B,0x51), posY 51987
        // (0x13,0xCB), dir +Y -> 192 (0xC0), hp 26/47 -> 141 (0x8D).
        // M2 (Gunner/Reposition, the pinned pair from task-27-brief §3 item
        // 5): id 61234 -> (0x32,0xEF), typeAndAi 0x14, pos (48,-27) -> posX
        // 63014 (0x26,0xF6), posY 15754 (0x8A,0x3D), dir -Y -> 64 (0x40), hp
        // 14.5/33 -> 112 (0x70).
        static readonly SnapshotBlocks.MobRecord MobM1 = new SnapshotBlocks.MobRecord
        {
            Id = 733, Type = MobType.Chaser, Ai = MobAiState.Chase,
            Pos = new float2(-19f, 30.5f), Dir = new float2(0f, 1f), Hp = 26f,
        };
        static readonly SnapshotBlocks.MobRecord MobM2 = new SnapshotBlocks.MobRecord
        {
            Id = 61234, Type = MobType.Gunner, Ai = MobAiState.Reposition,
            Pos = new float2(48f, -27f), Dir = new float2(0f, -1f), Hp = 14.5f,
        };

        const WavePhase WaveFixturePhase = WavePhase.Active;
        const ushort WaveFixtureIndex = 9001;   // 0x2329 — both bytes nonzero and different
        const byte WaveFixtureAliveCount = 71;

        // task-27-brief §3 item 4: the canonical "three players" example —
        // bit i means player i, mask 0b101 is players 0 and 2 alive, 1 dead.
        const byte LivenessFixtureMask = 0b101;

        // Stage 3 Task 25 (Р257): the second mask. Disjoint from the alive
        // mask by construction — a player is never both alive and extracted
        // (NetInvariants' own pair invariant) — and DIFFERENT from it byte
        // for byte, so a writer or reader that swapped the two would be
        // caught rather than round-tripped.
        const byte LivenessFixtureExtractedMask = 0b010;

        // Event fixtures — synthetic kinds/payloads, per task-27-brief §2.5
        // Task 27 draws no catalog. E1 pos (17,-49) -> posX 43480
        // (0xD8,0xA9), posY 1890 (0x62,0x07). E2 pos (-33,26) -> posX 11973
        // (0xC5,0x2E), posY 49151 (0xFF,0xBF), zero payload — the "0 B"
        // boundary from task-27-brief §3 item 7.
        static readonly byte[] EventPayloadPool = { 0x57, 0x7E, 0x91 };
        static readonly SnapshotBlocks.EventRecord EventE1 = new SnapshotBlocks.EventRecord
        {
            Kind = 0xD2, Seq = 31337, TickDelta = 191, Pos = new float2(17f, -49f),
            PayloadOffset = 0, PayloadLength = 3,
        };
        static readonly SnapshotBlocks.EventRecord EventE2 = new SnapshotBlocks.EventRecord
        {
            Kind = 0xD3, Seq = 4113, TickDelta = 53, Pos = new float2(-33f, 26f),
            PayloadOffset = 3, PayloadLength = 0,
        };

        static readonly byte[] AllBlockKinds =
        {
            (byte)SnapshotBlockKind.Players, (byte)SnapshotBlockKind.Liveness,
            (byte)SnapshotBlockKind.Mobs, (byte)SnapshotBlockKind.Wave, (byte)SnapshotBlockKind.Events,
        };

        /// The canonical Players -> Liveness -> Mobs -> Wave -> Events frame
        /// (task-27-brief §2.11), sized exactly via the writer's own public
        /// calculators so the fixture premise below is itself a check on
        /// those calculators.
        static byte[] BuildCanonicalFiveBlockFrame()
        {
            int size = SnapshotWriter.HeaderBytes
                       + SnapshotWriter.PlayersBlockBytes(2)
                       + SnapshotWriter.LivenessBlockBytes()
                       + SnapshotWriter.MobsBlockBytes(2)
                       + SnapshotWriter.WaveBlockBytes()
                       + SnapshotWriter.EventsBlockBytes(2, EventE1.PayloadLength + EventE2.PayloadLength);
            var buffer = new byte[size];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WritePlayersBlock(new[] { PlayerP1, PlayerP2 }, SnapCfg);
            writer.WriteLivenessBlock(LivenessFixtureMask, LivenessFixtureExtractedMask);
            writer.WriteMobsBlock(new[] { MobM1, MobM2 }, SnapCfg);
            writer.WriteWaveBlock(WaveFixturePhase, WaveFixtureIndex, WaveFixtureAliveCount);
            writer.WriteEventsBlock(new[] { EventE1, EventE2 }, EventPayloadPool, SnapCfg);
            Assert.AreEqual(size, writer.BytesWritten,
                "fixture premise: the canonical frame must fill the buffer exactly");
            return buffer;
        }

        /// Wrap-aware angular distance in degrees — duplicated test-only
        /// helper, same shape as InputCodecTests.AngularDifferenceDegrees
        /// (that file's own precedent: small helpers are copied rather than
        /// shared across closed tasks).
        static float AngularDifferenceDegrees(float2 a, float2 b)
        {
            float cross = a.x * b.y - a.y * b.x;
            float dot = a.x * b.x + a.y * b.y;
            return math.abs(math.degrees(math.atan2(cross, dot)));
        }

        /// Asserts a DECODED heading, and does it in two parts because the
        /// angular check alone has a blind spot big enough to drive the very
        /// regression it is meant to catch through (fix-round 2, found by the
        /// scoped re-review of fix-round 1).
        ///
        /// `AngularDifferenceDegrees(float2.zero, anything)` is EXACTLY ZERO:
        /// both `cross` and `dot` vanish, and `atan2(0, 0)` is 0 by
        /// convention — a fact this repo already documents in Quantize.cs's
        /// own doc for `Dir(float2.zero)`. So the single most likely decoder
        /// regression, "the field is simply never written and keeps the
        /// struct's default", passes an angular assertion against EVERY
        /// expected direction. Fix-round 1 added exactly such an assertion to
        /// close a coverage hole and, for that mutation, closed nothing; the
        /// mutation it did verify (a constant +X) has non-zero cross/dot and
        /// therefore looked like proof.
        ///
        /// The magnitude check is what actually pins it: `Quantize.DirBack`
        /// returns `(cos, sin)` of the cell angle, whose length is 1 to float
        /// precision, and no unwritten field can be unit-length. It is also
        /// platform-safe in the way a literal component would not be (lesson
        /// of Task 24/25: the exact bytes of a cosine near an axis are a
        /// precision detail, the length is not).
        static void AssertDecodedHeading(float2 actual, float2 expected, string what)
        {
            Assert.That(math.length(actual), Is.EqualTo(1f).Within(1e-4f),
                $"{what}: a decoded heading is a unit vector — this is the assertion that "
                + "fails when the field is never written, which the angular check below "
                + "cannot see (atan2(0,0) == 0 against any direction)");
            Assert.That(AngularDifferenceDegrees(actual, expected),
                Is.LessThanOrEqualTo(HalfStepDirDegrees + DirNoiseDegrees),
                $"{what}: decoded heading must come back within half a Dir step");
        }

        // ---- T27.1/2. Structural: enum and record-size constants ----

        [Test]
        public void SnapshotBlockKind_ValuesArePinned_AndNoneIsZero()
        {
            // task-27-brief §2.1: None=0 is a contract inherited from Task
            // 26's TryReadBlock refusal sentinel — Reader_BlockBeforeHeader_
            // FailsInsteadOfParsingHeaderBytesAsABlock and Reader_
            // AfterMalformedLength_DoesNotResumeOnAttackerChosenBytes both
            // pin `kind == 0` on a refusal. Assigning 0 to a real block kind
            // would make "the reader refused" indistinguishable from "a
            // Players block arrived."
            Assert.AreEqual((byte)0, (byte)SnapshotBlockKind.None,
                "None must stay 0 — Task 26's TryReadBlock refusal sentinel");
            Assert.AreEqual((byte)1, (byte)SnapshotBlockKind.Players);
            Assert.AreEqual((byte)2, (byte)SnapshotBlockKind.Liveness);
            Assert.AreEqual((byte)3, (byte)SnapshotBlockKind.Mobs);
            Assert.AreEqual((byte)4, (byte)SnapshotBlockKind.Wave);
            Assert.AreEqual((byte)5, (byte)SnapshotBlockKind.Events);

            // Stage 3 Task 25 (spec §3.12, plan errata E-7: ELEVEN values
            // counting None, not the plan body's "ten"). Appended, never
            // renumbered — a tag is the one field a reader of another build
            // must still agree on, and Р282 records that a new block kind is
            // precisely the case that does NOT bump ProtocolVersion.
            Assert.AreEqual((byte)6, (byte)SnapshotBlockKind.Match);
            Assert.AreEqual((byte)7, (byte)SnapshotBlockKind.Self);
            Assert.AreEqual((byte)8, (byte)SnapshotBlockKind.Pickups);
            Assert.AreEqual((byte)9, (byte)SnapshotBlockKind.Containers);
            Assert.AreEqual((byte)10, (byte)SnapshotBlockKind.ContainerSlots);
            Assert.AreEqual(11, System.Enum.GetValues(typeof(SnapshotBlockKind)).Length,
                "eleven values counting None — a twelfth tag is a wire change and reddens here first");
        }

        [Test]
        public void RecordSizeConstants_ArePinned()
        {
            Assert.AreEqual(8, SnapshotBlocks.PlayerRecordBytes);
            Assert.AreEqual(9, SnapshotBlocks.MobRecordBytes);
            Assert.AreEqual(9, SnapshotBlocks.EventHeaderBytes);
            Assert.AreEqual(2, SnapshotBlocks.LivenessBlockPayloadBytes,
                "Stage 3 Task 25 (Р257): TWO masks — alive and extracted");
            Assert.AreEqual(4, SnapshotBlocks.WaveBlockPayloadBytes);

            // Stage 3 Task 25 (spec §3.12's table, byte for byte).
            Assert.AreEqual(4, SnapshotBlocks.MatchBlockPayloadBytes);
            Assert.AreEqual(2, SnapshotBlocks.SelfBlockHeaderBytes);
            Assert.AreEqual(7, SnapshotBlocks.PickupRecordBytes);
            Assert.AreEqual(7, SnapshotBlocks.ContainerRecordBytes);
            Assert.AreEqual(3, SnapshotBlocks.ContainerSlotsRecordHeaderBytes);
        }

        // ---- T27.3-7. Byte layout, one block kind at a time ----

        [Test]
        public void Players_ByteLayout_TwoRecords_IsIndexPosDirHpFlags_LittleEndian()
        {
            const int tailBytes = 4;
            int blockBytes = SnapshotWriter.PlayersBlockBytes(2);
            var buffer = Filled(SnapshotWriter.HeaderBytes + blockBytes + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WritePlayersBlock(new[] { PlayerP1, PlayerP2 }, SnapCfg);
            Assert.AreEqual(SnapshotWriter.HeaderBytes + blockBytes, writer.BytesWritten);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Players, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)0x10, buffer[b + 1], "block byte 1: payloadBytes low (16 = 2 records * 8)");
            Assert.AreEqual((byte)0x00, buffer[b + 2], "block byte 2: payloadBytes high");

            int r0 = b + SnapshotWriter.BlockHeaderBytes;
            Assert.AreEqual((byte)0, buffer[r0 + 0], "record 1 byte 0: index");
            Assert.AreEqual((byte)0x9D, buffer[r0 + 1], "record 1 byte 1: posX low");
            Assert.AreEqual((byte)0xB8, buffer[r0 + 2], "record 1 byte 2: posX high");
            Assert.AreEqual((byte)0xEC, buffer[r0 + 3], "record 1 byte 3: posY low");
            Assert.AreEqual((byte)0x24, buffer[r0 + 4], "record 1 byte 4: posY high");
            Assert.AreEqual((byte)0x80, buffer[r0 + 5], "record 1 byte 5: dir (+X -> code 128)");
            Assert.AreEqual((byte)0x91, buffer[r0 + 6], "record 1 byte 6: hp (67/118 -> code 145)");
            Assert.AreEqual((byte)0x09, buffer[r0 + 7], "record 1 byte 7: flags (Alive|AimHeld)");

            int r1 = r0 + SnapshotBlocks.PlayerRecordBytes;
            Assert.AreEqual((byte)2, buffer[r1 + 0], "record 2 byte 0: index");
            Assert.AreEqual((byte)0x14, buffer[r1 + 1], "record 2 byte 1: posX low");
            Assert.AreEqual((byte)0x1B, buffer[r1 + 2], "record 2 byte 2: posX high");
            Assert.AreEqual((byte)0xC4, buffer[r1 + 3], "record 2 byte 3: posY low");
            Assert.AreEqual((byte)0xAE, buffer[r1 + 4], "record 2 byte 4: posY high");
            Assert.AreEqual((byte)0x40, buffer[r1 + 5], "record 2 byte 5: dir (-Y -> code 64)");
            Assert.AreEqual((byte)0x3F, buffer[r1 + 6], "record 2 byte 6: hp (29/118 -> code 63)");
            Assert.AreEqual((byte)0x12, buffer[r1 + 7], "record 2 byte 7: flags (Dashing|LinkWindow)");

            for (int i = SnapshotWriter.HeaderBytes + blockBytes; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the block");
        }

        [Test]
        public void Liveness_ByteLayout_MaskLiteral_AndBitIMeansPlayerI()
        {
            var buffer = Filled(SnapshotWriter.HeaderBytes + SnapshotWriter.LivenessBlockBytes());
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteLivenessBlock(LivenessFixtureMask, LivenessFixtureExtractedMask);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Liveness, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)2, buffer[b + 1], "block byte 1: payloadBytes low = 2 (Task 25, Р257)");
            Assert.AreEqual((byte)0, buffer[b + 2], "block byte 2: payloadBytes high");
            Assert.AreEqual((byte)0b101, buffer[b + SnapshotWriter.BlockHeaderBytes],
                "payload byte 0: ALIVE mask, literal 0b101");
            Assert.AreEqual((byte)0b010, buffer[b + SnapshotWriter.BlockHeaderBytes + 1],
                "payload byte 1: EXTRACTED mask, literal 0b010 — alive first, extracted second");

            Assert.IsTrue(SnapshotBlocks.TryReadLivenessBlock(
                new System.ReadOnlySpan<byte>(buffer, b + SnapshotWriter.BlockHeaderBytes,
                    SnapshotBlocks.LivenessBlockPayloadBytes),
                out byte decodedMask, out byte decodedExtracted, out SnapshotBlockError error));
            Assert.AreEqual(LivenessFixtureMask, decodedMask);
            Assert.AreEqual(LivenessFixtureExtractedMask, decodedExtracted,
                "the second mask must come back as itself, not as a copy of the first");
            Assert.AreEqual(SnapshotBlockError.None, error);

            // Fix-round I4: these three ran against the CONSTANT
            // `LivenessFixtureMask` and touched no production code at all —
            // `(0b101 & 1) != 0` is a fact about the literal, true on an
            // empty implementation and on any other. No mutation could redden
            // them, so they were pure decoration over the real assertions
            // above. Re-pointed at `decodedMask`, they now ride the full
            // write-then-decode path, and bit i still means player i.
            Assert.IsTrue((decodedMask & (1 << 0)) != 0, "player 0 alive");
            Assert.IsFalse((decodedMask & (1 << 1)) != 0, "player 1 dead");
            Assert.IsTrue((decodedMask & (1 << 2)) != 0, "player 2 alive");

            // Stage 3 Task 25 (Р257): bit i of the SECOND mask means player i
            // walked out. The two masks are disjoint by the invariant that a
            // player is never both — so player 1, dead in the alive mask
            // above, is the one this fixture marks extracted, and reading
            // either mask alone can no longer tell "lost" from "got out".
            Assert.IsFalse((decodedExtracted & (1 << 0)) != 0, "player 0 has not extracted");
            Assert.IsTrue((decodedExtracted & (1 << 1)) != 0, "player 1 extracted");
            Assert.IsFalse((decodedExtracted & (1 << 2)) != 0, "player 2 has not extracted");
        }

        [Test]
        public void Mobs_ByteLayout_TwoRecords_IncludingTypeAndAiPacking()
        {
            const int tailBytes = 4;
            int blockBytes = SnapshotWriter.MobsBlockBytes(2);
            var buffer = Filled(SnapshotWriter.HeaderBytes + blockBytes + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteMobsBlock(new[] { MobM1, MobM2 }, SnapCfg);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Mobs, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)0x12, buffer[b + 1], "block byte 1: payloadBytes low (18 = 2 records * 9)");
            Assert.AreEqual((byte)0x00, buffer[b + 2], "block byte 2: payloadBytes high");

            int r0 = b + SnapshotWriter.BlockHeaderBytes;
            Assert.AreEqual((byte)0xDD, buffer[r0 + 0], "record 1 byte 0: id low (733 = 0x02DD)");
            Assert.AreEqual((byte)0x02, buffer[r0 + 1], "record 1 byte 1: id high");
            Assert.AreEqual((byte)0x01, buffer[r0 + 2], "record 1 byte 2: typeAndAi (Chaser<<4 | Chase = 0x01)");
            Assert.AreEqual((byte)0x3B, buffer[r0 + 3], "record 1 byte 3: posX low");
            Assert.AreEqual((byte)0x51, buffer[r0 + 4], "record 1 byte 4: posX high");
            Assert.AreEqual((byte)0x13, buffer[r0 + 5], "record 1 byte 5: posY low");
            Assert.AreEqual((byte)0xCB, buffer[r0 + 6], "record 1 byte 6: posY high");
            Assert.AreEqual((byte)0xC0, buffer[r0 + 7], "record 1 byte 7: dir (+Y -> code 192)");
            Assert.AreEqual((byte)0x8D, buffer[r0 + 8], "record 1 byte 8: hp (26/47 -> code 141)");

            int r1 = r0 + SnapshotBlocks.MobRecordBytes;
            // Gunner/Reposition — the pinned pair from task-27-brief §3 item 5.
            Assert.AreEqual((byte)0x32, buffer[r1 + 0], "record 2 byte 0: id low (61234 = 0xEF32)");
            Assert.AreEqual((byte)0xEF, buffer[r1 + 1], "record 2 byte 1: id high");
            Assert.AreEqual((byte)0x14, buffer[r1 + 2], "record 2 byte 2: typeAndAi (Gunner<<4 | Reposition = 0x14)");
            Assert.AreEqual((byte)0x26, buffer[r1 + 3], "record 2 byte 3: posX low");
            Assert.AreEqual((byte)0xF6, buffer[r1 + 4], "record 2 byte 4: posX high");
            Assert.AreEqual((byte)0x8A, buffer[r1 + 5], "record 2 byte 5: posY low");
            Assert.AreEqual((byte)0x3D, buffer[r1 + 6], "record 2 byte 6: posY high");
            Assert.AreEqual((byte)0x40, buffer[r1 + 7], "record 2 byte 7: dir (-Y -> code 64)");
            Assert.AreEqual((byte)0x70, buffer[r1 + 8], "record 2 byte 8: hp (14.5/33 -> code 112)");

            for (int i = SnapshotWriter.HeaderBytes + blockBytes; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the block");
        }

        [Test]
        public void Wave_ByteLayout_PhaseIndexAliveCount()
        {
            var buffer = Filled(SnapshotWriter.HeaderBytes + SnapshotWriter.WaveBlockBytes());
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteWaveBlock(WaveFixturePhase, WaveFixtureIndex, WaveFixtureAliveCount);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Wave, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)4, buffer[b + 1], "block byte 1: payloadBytes low = 4");
            Assert.AreEqual((byte)0, buffer[b + 2], "block byte 2: payloadBytes high");

            int p = b + SnapshotWriter.BlockHeaderBytes;
            Assert.AreEqual((byte)WavePhase.Active, buffer[p + 0], "payload byte 0: phase");
            Assert.AreEqual((byte)0x29, buffer[p + 1], "payload byte 1: waveIndex low (9001 = 0x2329)");
            Assert.AreEqual((byte)0x23, buffer[p + 2], "payload byte 2: waveIndex high");
            Assert.AreEqual((byte)71, buffer[p + 3], "payload byte 3: aliveCount");
        }

        [Test]
        public void Events_ByteLayout_TwoRecords_SecondHasZeroPayload()
        {
            const int tailBytes = 4;
            int blockBytes = SnapshotWriter.EventsBlockBytes(2, EventE1.PayloadLength + EventE2.PayloadLength);
            var buffer = Filled(SnapshotWriter.HeaderBytes + blockBytes + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteEventsBlock(new[] { EventE1, EventE2 }, EventPayloadPool, SnapCfg);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Events, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)0x15, buffer[b + 1], "block byte 1: payloadBytes low (21 = (9+3) + (9+0))");
            Assert.AreEqual((byte)0x00, buffer[b + 2], "block byte 2: payloadBytes high");

            int r0 = b + SnapshotWriter.BlockHeaderBytes;
            Assert.AreEqual((byte)0xD2, buffer[r0 + 0], "record 1 byte 0: kind (opaque to Task 27)");
            Assert.AreEqual((byte)0x69, buffer[r0 + 1], "record 1 byte 1: seq low (31337 = 0x7A69)");
            Assert.AreEqual((byte)0x7A, buffer[r0 + 2], "record 1 byte 2: seq high");
            Assert.AreEqual((byte)191, buffer[r0 + 3], "record 1 byte 3: tickDelta");
            Assert.AreEqual((byte)0xD8, buffer[r0 + 4], "record 1 byte 4: posX low");
            Assert.AreEqual((byte)0xA9, buffer[r0 + 5], "record 1 byte 5: posX high");
            Assert.AreEqual((byte)0x62, buffer[r0 + 6], "record 1 byte 6: posY low");
            Assert.AreEqual((byte)0x07, buffer[r0 + 7], "record 1 byte 7: posY high");
            Assert.AreEqual((byte)3, buffer[r0 + 8], "record 1 byte 8: payloadBytes");
            Assert.AreEqual((byte)0x57, buffer[r0 + 9], "record 1 payload byte 0");
            Assert.AreEqual((byte)0x7E, buffer[r0 + 10], "record 1 payload byte 1");
            Assert.AreEqual((byte)0x91, buffer[r0 + 11], "record 1 payload byte 2");

            int r1 = r0 + SnapshotBlocks.EventHeaderBytes + 3;
            Assert.AreEqual((byte)0xD3, buffer[r1 + 0], "record 2 byte 0: kind");
            Assert.AreEqual((byte)0x11, buffer[r1 + 1], "record 2 byte 1: seq low (4113 = 0x1011)");
            Assert.AreEqual((byte)0x10, buffer[r1 + 2], "record 2 byte 2: seq high");
            Assert.AreEqual((byte)53, buffer[r1 + 3], "record 2 byte 3: tickDelta");
            Assert.AreEqual((byte)0xC5, buffer[r1 + 4], "record 2 byte 4: posX low");
            Assert.AreEqual((byte)0x2E, buffer[r1 + 5], "record 2 byte 5: posX high");
            Assert.AreEqual((byte)0xFF, buffer[r1 + 6], "record 2 byte 6: posY low");
            Assert.AreEqual((byte)0xBF, buffer[r1 + 7], "record 2 byte 7: posY high");
            Assert.AreEqual((byte)0, buffer[r1 + 8], "record 2 byte 8: payloadBytes = 0 — the zero-payload boundary");

            for (int i = SnapshotWriter.HeaderBytes + blockBytes; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the block");
        }

        // ---- T27.8. Full snapshot round-trip, canonical order ----

        [Test]
        public void FullSnapshot_AllFiveBlocks_RoundTrip_InCanonicalOrder()
        {
            byte[] frame = BuildCanonicalFiveBlockFrame();

            var reader = new SnapshotReader(frame);
            Assert.IsTrue(reader.TryReadHeader(out ushort epoch, out uint tick, out byte flags));
            Assert.AreEqual(Epoch, epoch); Assert.AreEqual(Tick, tick); Assert.AreEqual(Flags, flags);

            Assert.IsTrue(reader.TryReadBlock(AllBlockKinds, out byte kind1, out System.ReadOnlySpan<byte> payload1));
            Assert.AreEqual((byte)SnapshotBlockKind.Players, kind1, "canonical order: Players first");
            var playerDest = new SnapshotBlocks.PlayerRecord[4];
            Assert.IsTrue(SnapshotBlocks.TryReadPlayersBlock(payload1, SnapCfg, playerDest, out int playerCount, out SnapshotBlockError playerErr));
            Assert.AreEqual(2, playerCount);
            Assert.AreEqual(SnapshotBlockError.None, playerErr);
            Assert.AreEqual(PlayerP1.Index, playerDest[0].Index);
            Assert.AreEqual(PlayerP2.Index, playerDest[1].Index);
            Assert.AreEqual(PlayerP1.Flags, playerDest[0].Flags);
            Assert.AreEqual(PlayerP2.Flags, playerDest[1].Flags);

            Assert.IsTrue(reader.TryReadBlock(AllBlockKinds, out byte kind2, out System.ReadOnlySpan<byte> payload2));
            Assert.AreEqual((byte)SnapshotBlockKind.Liveness, kind2, "canonical order: Liveness second");
            Assert.IsTrue(SnapshotBlocks.TryReadLivenessBlock(payload2, out byte aliveMask,
                out byte extractedMask, out SnapshotBlockError liveErr));
            Assert.AreEqual(LivenessFixtureMask, aliveMask);
            Assert.AreEqual(LivenessFixtureExtractedMask, extractedMask);
            Assert.AreEqual(SnapshotBlockError.None, liveErr);

            Assert.IsTrue(reader.TryReadBlock(AllBlockKinds, out byte kind3, out System.ReadOnlySpan<byte> payload3));
            Assert.AreEqual((byte)SnapshotBlockKind.Mobs, kind3, "canonical order: Mobs third");
            var mobDest = new SnapshotBlocks.MobRecord[4];
            Assert.IsTrue(SnapshotBlocks.TryReadMobsBlock(payload3, SnapCfg, mobDest, out int mobCount, out SnapshotBlockError mobErr));
            Assert.AreEqual(2, mobCount);
            Assert.AreEqual(SnapshotBlockError.None, mobErr);
            Assert.AreEqual(MobM1.Id, mobDest[0].Id);
            Assert.AreEqual(MobM1.Type, mobDest[0].Type);
            Assert.AreEqual(MobM1.Ai, mobDest[0].Ai);
            Assert.AreEqual(MobM2.Id, mobDest[1].Id);
            Assert.AreEqual(MobM2.Type, mobDest[1].Type);
            Assert.AreEqual(MobM2.Ai, mobDest[1].Ai);
            // Fix-round C1: the DECODED mob heading was asserted by nothing
            // at all — not here, not in the precision test, not anywhere —
            // so `Dir = float2.zero` (or reading the typeAndAi byte as the
            // direction code) survived all 41 tests. The writer's half was
            // pinned by Mobs_ByteLayout_*; the reader's half was not, and
            // there was not even a round-trip to be blind. Symptom it would
            // have shipped: every mob on every client faces one fixed
            // direction regardless of what the server sent.
            AssertDecodedHeading(mobDest[0].Dir, MobM1.Dir, "mob 1");
            // A SECOND heading, opposite to mob 1's, so no constant satisfies
            // both; and AssertDecodedHeading's magnitude half also refuses the
            // unwritten-field case, which the angular half cannot see.
            AssertDecodedHeading(mobDest[1].Dir, MobM2.Dir, "mob 2");

            Assert.IsTrue(reader.TryReadBlock(AllBlockKinds, out byte kind4, out System.ReadOnlySpan<byte> payload4));
            Assert.AreEqual((byte)SnapshotBlockKind.Wave, kind4, "canonical order: Wave fourth");
            Assert.IsTrue(SnapshotBlocks.TryReadWaveBlock(payload4, out WavePhase phase, out ushort waveIndex, out byte aliveCount, out SnapshotBlockError waveErr));
            Assert.AreEqual(WaveFixturePhase, phase);
            Assert.AreEqual(WaveFixtureIndex, waveIndex);
            Assert.AreEqual(WaveFixtureAliveCount, aliveCount);
            Assert.AreEqual(SnapshotBlockError.None, waveErr);

            Assert.IsTrue(reader.TryReadBlock(AllBlockKinds, out byte kind5, out System.ReadOnlySpan<byte> payload5));
            Assert.AreEqual((byte)SnapshotBlockKind.Events, kind5, "canonical order: Events fifth");
            var eventDest = new SnapshotBlocks.EventRecord[4];
            Assert.IsTrue(SnapshotBlocks.TryReadEventsBlock(payload5, SnapCfg, eventDest, out int eventCount, out SnapshotBlockError eventErr));
            Assert.AreEqual(2, eventCount);
            Assert.AreEqual(SnapshotBlockError.None, eventErr);
            Assert.AreEqual(EventE1.Kind, eventDest[0].Kind);
            Assert.AreEqual(EventE1.Seq, eventDest[0].Seq);
            Assert.AreEqual(EventE1.TickDelta, eventDest[0].TickDelta);
            Assert.AreEqual(EventE1.PayloadLength, eventDest[0].PayloadLength);
            AssertPayloadEquals(EventPayloadPool, payload5.Slice(eventDest[0].PayloadOffset, eventDest[0].PayloadLength), "event 1 payload");
            Assert.AreEqual(EventE2.Kind, eventDest[1].Kind);
            Assert.AreEqual(EventE2.Seq, eventDest[1].Seq);
            Assert.AreEqual(EventE2.TickDelta, eventDest[1].TickDelta);
            Assert.AreEqual(EventE2.PayloadLength, eventDest[1].PayloadLength);

            Assert.IsFalse(reader.TryReadBlock(AllBlockKinds, out _, out _), "the stream is exhausted after five blocks");
            Assert.IsFalse(reader.Failed);
            Assert.IsFalse(reader.Truncated);
            Assert.AreEqual(0, reader.SkippedBlockCount);
        }

        // ---- T27.9. Numeric round-trip precision, tolerance computed here ----

        [Test]
        public void QuantizedFields_RoundTrip_WithinHalfStep_ToleranceComputedInTest()
        {
            // Tolerances are the file-level HalfStep* constants, derived from
            // the wire widths rather than from Quantize (see their doc) and
            // widened only by measured float noise (fix-round M7).
            byte[] frame = BuildCanonicalFiveBlockFrame();
            var reader = new SnapshotReader(frame);
            reader.TryReadHeader(out _, out _, out _);

            reader.TryReadBlock(AllBlockKinds, out _, out System.ReadOnlySpan<byte> playersPayload);
            var playerDest = new SnapshotBlocks.PlayerRecord[4];
            SnapshotBlocks.TryReadPlayersBlock(playersPayload, SnapCfg, playerDest, out _, out _);
            Assert.That(playerDest[0].Pos.x, Is.EqualTo(PlayerP1.Pos.x).Within(HalfStepPosMeters + PosNoiseMeters));
            Assert.That(playerDest[0].Pos.y, Is.EqualTo(PlayerP1.Pos.y).Within(HalfStepPosMeters + PosNoiseMeters));
            Assert.That(playerDest[0].Hp, Is.EqualTo(PlayerP1.Hp).Within(HalfStepHeroHp + HpNoise));
            AssertDecodedHeading(playerDest[0].Dir, PlayerP1.Dir, "player 1");
            // The SECOND player too: with only record 0 checked, every decoded
            // field of every record after the first was unverified.
            Assert.That(playerDest[1].Pos.x, Is.EqualTo(PlayerP2.Pos.x).Within(HalfStepPosMeters + PosNoiseMeters));
            Assert.That(playerDest[1].Pos.y, Is.EqualTo(PlayerP2.Pos.y).Within(HalfStepPosMeters + PosNoiseMeters));
            Assert.That(playerDest[1].Hp, Is.EqualTo(PlayerP2.Hp).Within(HalfStepHeroHp + HpNoise));
            AssertDecodedHeading(playerDest[1].Dir, PlayerP2.Dir, "player 2");

            reader.TryReadBlock(AllBlockKinds, out _, out _); // liveness, not under test here
            reader.TryReadBlock(AllBlockKinds, out _, out System.ReadOnlySpan<byte> mobsPayload);
            var mobDest = new SnapshotBlocks.MobRecord[4];
            SnapshotBlocks.TryReadMobsBlock(mobsPayload, SnapCfg, mobDest, out _, out _);
            Assert.That(mobDest[0].Pos.x, Is.EqualTo(MobM1.Pos.x).Within(HalfStepPosMeters + PosNoiseMeters));
            Assert.That(mobDest[0].Pos.y, Is.EqualTo(MobM1.Pos.y).Within(HalfStepPosMeters + PosNoiseMeters));
            Assert.That(mobDest[0].Hp, Is.EqualTo(MobM1.Hp).Within(HalfStepChaserHp + HpNoise));
            Assert.That(mobDest[1].Pos.x, Is.EqualTo(MobM2.Pos.x).Within(HalfStepPosMeters + PosNoiseMeters));
            Assert.That(mobDest[1].Pos.y, Is.EqualTo(MobM2.Pos.y).Within(HalfStepPosMeters + PosNoiseMeters));

            // Fix-round C2: the decoded EVENT position was asserted nowhere,
            // so `Pos = float2.zero`, a swap of the x/y read offsets, and the
            // `Aim`-instead-of-`Pos` mutation §2.2 of the brief explicitly
            // required to redden all survived the reader's half of the codec
            // (the writer's half was pinned by Events_ByteLayout_*).
            reader.TryReadBlock(AllBlockKinds, out _, out _); // wave
            reader.TryReadBlock(AllBlockKinds, out _, out System.ReadOnlySpan<byte> eventsPayload);
            var eventDest = new SnapshotBlocks.EventRecord[4];
            SnapshotBlocks.TryReadEventsBlock(eventsPayload, SnapCfg, eventDest, out _, out _);
            Assert.That(eventDest[0].Pos.x, Is.EqualTo(EventE1.Pos.x).Within(HalfStepPosMeters + PosNoiseMeters));
            Assert.That(eventDest[0].Pos.y, Is.EqualTo(EventE1.Pos.y).Within(HalfStepPosMeters + PosNoiseMeters));
            // E1 and E2 differ in BOTH axes and in sign, so neither a constant
            // nor an x/y swap can satisfy both records.
            Assert.That(eventDest[1].Pos.x, Is.EqualTo(EventE2.Pos.x).Within(HalfStepPosMeters + PosNoiseMeters));
            Assert.That(eventDest[1].Pos.y, Is.EqualTo(EventE2.Pos.y).Within(HalfStepPosMeters + PosNoiseMeters));
        }

        // ---- T27.10. Empty blocks ----

        [Test]
        public void EmptyBlocks_Players_Mobs_Events_RoundTripAsZeroRecords()
        {
            int size = SnapshotWriter.HeaderBytes
                       + SnapshotWriter.PlayersBlockBytes(0)
                       + SnapshotWriter.MobsBlockBytes(0)
                       + SnapshotWriter.EventsBlockBytes(0, 0);
            var buffer = new byte[size];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WritePlayersBlock(System.ReadOnlySpan<SnapshotBlocks.PlayerRecord>.Empty, SnapCfg);
            writer.WriteMobsBlock(System.ReadOnlySpan<SnapshotBlocks.MobRecord>.Empty, SnapCfg);
            writer.WriteEventsBlock(System.ReadOnlySpan<SnapshotBlocks.EventRecord>.Empty, System.ReadOnlySpan<byte>.Empty, SnapCfg);
            Assert.AreEqual(size, writer.BytesWritten);

            var reader = new SnapshotReader(buffer);
            reader.TryReadHeader(out _, out _, out _);

            Assert.IsTrue(reader.TryReadBlock(AllBlockKinds, out byte kindP, out System.ReadOnlySpan<byte> payloadP));
            Assert.AreEqual((byte)SnapshotBlockKind.Players, kindP);
            Assert.AreEqual(0, payloadP.Length);
            var playerDest = new SnapshotBlocks.PlayerRecord[1];
            Assert.IsTrue(SnapshotBlocks.TryReadPlayersBlock(payloadP, SnapCfg, playerDest, out int pCount, out SnapshotBlockError pErr));
            Assert.AreEqual(0, pCount);
            Assert.AreEqual(SnapshotBlockError.None, pErr);

            Assert.IsTrue(reader.TryReadBlock(AllBlockKinds, out byte kindM, out System.ReadOnlySpan<byte> payloadM));
            Assert.AreEqual((byte)SnapshotBlockKind.Mobs, kindM);
            var mobDest = new SnapshotBlocks.MobRecord[1];
            Assert.IsTrue(SnapshotBlocks.TryReadMobsBlock(payloadM, SnapCfg, mobDest, out int mCount, out SnapshotBlockError mErr));
            Assert.AreEqual(0, mCount);
            Assert.AreEqual(SnapshotBlockError.None, mErr);

            Assert.IsTrue(reader.TryReadBlock(AllBlockKinds, out byte kindE, out System.ReadOnlySpan<byte> payloadE));
            Assert.AreEqual((byte)SnapshotBlockKind.Events, kindE);
            var eventDest = new SnapshotBlocks.EventRecord[1];
            Assert.IsTrue(SnapshotBlocks.TryReadEventsBlock(payloadE, SnapCfg, eventDest, out int eCount, out SnapshotBlockError eErr));
            Assert.AreEqual(0, eCount);
            Assert.AreEqual(SnapshotBlockError.None, eErr);

            Assert.IsFalse(reader.Failed);
            Assert.IsFalse(reader.Truncated);
        }

        // ---- T27.11-14. Hostile input, one per SnapshotBlockError value ----

        [Test]
        public void MalformedLength_PlayersAndMobs_Rejected_NoRecordsYielded_NoException()
        {
            var badPlayers = new byte[SnapshotBlocks.PlayerRecordBytes + 3]; // 8 + 3, not a multiple of 8
            var playerDest = new SnapshotBlocks.PlayerRecord[4];
            bool okP = true;
            SnapshotBlockError errP = SnapshotBlockError.None;
            int countP = -1;
            Assert.DoesNotThrow(() => okP = SnapshotBlocks.TryReadPlayersBlock(badPlayers, SnapCfg, playerDest, out countP, out errP));
            Assert.IsFalse(okP);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, errP);
            Assert.AreEqual(0, countP);

            var badMobs = new byte[SnapshotBlocks.MobRecordBytes + 5]; // 9 + 5, not a multiple of 9
            var mobDest = new SnapshotBlocks.MobRecord[4];
            bool okM = true;
            SnapshotBlockError errM = SnapshotBlockError.None;
            int countM = -1;
            Assert.DoesNotThrow(() => okM = SnapshotBlocks.TryReadMobsBlock(badMobs, SnapCfg, mobDest, out countM, out errM));
            Assert.IsFalse(okM);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, errM);
            Assert.AreEqual(0, countM);
        }

        [Test]
        public void DestinationTooSmall_FiveMobsIntoThreeSlotDestination_Rejected_NoException()
        {
            var fiveMobs = new[] { MobM1, MobM2, MobM1, MobM2, MobM1 };
            var buffer = new byte[SnapshotWriter.HeaderBytes + SnapshotWriter.MobsBlockBytes(5)];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteMobsBlock(fiveMobs, SnapCfg);

            var destination = new SnapshotBlocks.MobRecord[3];
            bool ok = true;
            SnapshotBlockError error = SnapshotBlockError.None;
            int count = -1;
            // `payload` is a ReadOnlySpan<byte> (ref struct) — it must be
            // obtained and consumed entirely INSIDE the lambda, since a ref
            // struct cannot be captured across a closure boundary (CS8175).
            Assert.DoesNotThrow(() =>
            {
                var reader = new SnapshotReader(buffer);
                reader.TryReadHeader(out _, out _, out _);
                reader.TryReadBlock(AllBlockKinds, out _, out System.ReadOnlySpan<byte> payload);
                ok = SnapshotBlocks.TryReadMobsBlock(payload, SnapCfg, destination, out count, out error);
            });
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.DestinationTooSmall, error);
        }

        [Test]
        public void LivenessAndWave_WrongLength_Rejected_NoException()
        {
            // Stage 3 Task 25: the two blocks no longer share a legal length,
            // so the sweep is split. Liveness is TWO bytes now (Р257) — the
            // 1 below is the OLD format's length, and a decoder that still
            // accepted it would read a frame from a stale peer as a valid
            // one with an all-clear extracted mask.
            foreach (int len in new[] { 0, 1, 3, 5 })
            {
                var badLiveness = new byte[len];
                bool okL = true;
                SnapshotBlockError errL = SnapshotBlockError.None;
                Assert.DoesNotThrow(() => okL = SnapshotBlocks.TryReadLivenessBlock(badLiveness, out _, out _, out errL));
                Assert.IsFalse(okL, $"Liveness length {len} must be refused");
                Assert.AreEqual(SnapshotBlockError.MalformedLength, errL, $"Liveness length {len}");
            }

            foreach (int len in new[] { 0, 2, 3, 5 })
            {
                var badWave = new byte[len];
                bool okW = true;
                SnapshotBlockError errW = SnapshotBlockError.None;
                Assert.DoesNotThrow(() => okW = SnapshotBlocks.TryReadWaveBlock(badWave, out _, out _, out _, out errW));
                Assert.IsFalse(okW, $"Wave length {len} must be refused");
                Assert.AreEqual(SnapshotBlockError.MalformedLength, errW, $"Wave length {len}");
            }
        }

        [Test]
        public void EventsBlock_PayloadLongerThanTheOffsetField_IsRefused_NoException()
        {
            // Fix-round 2 (scoped re-review finding): the `payload.Length >
            // ushort.MaxValue` guard added by fix-round 1 had no test, so
            // both "use >= instead of >" and "report the wrong error" were
            // free mutations. The guard exists because `EventRecord.
            // PayloadOffset` is a ushort: past 65535 an offset wraps and
            // points a consumer at the wrong bytes, which is exactly the
            // silent corruption Р82 rules out. Unreachable through
            // SnapshotReader (its block lengths are u16 by construction) —
            // this method is public and its doc invites direct calls.
            var tooLong = new byte[ushort.MaxValue + 1];
            var destination = new SnapshotBlocks.EventRecord[4];
            bool ok = true;
            SnapshotBlockError error = SnapshotBlockError.None;
            int count = -1;
            Assert.DoesNotThrow(
                () => ok = SnapshotBlocks.TryReadEventsBlock(tooLong, SnapCfg, destination, out count, out error),
                "an over-long payload is refused, not thrown on (Р82)");
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error);
            Assert.AreEqual(0, count);

            // Exactly 65535 is the largest ADDRESSABLE payload and must be
            // accepted, so the guard is `>` and not `>=`. Its records are all
            // zero bytes: kind 0, seq 0, tickDelta 0, pos 0, payloadBytes 0 —
            // 7281 well-formed 9-byte headers and 6 bytes left over, which is
            // shorter than a header and therefore MalformedLength. Both facts
            // are asserted: the guard let it through, and the walk then
            // failed for its own, different reason.
            var largest = new byte[ushort.MaxValue];
            var bigDestination = new SnapshotBlocks.EventRecord[ushort.MaxValue / SnapshotBlocks.EventHeaderBytes + 1];
            SnapshotBlockError largestError = SnapshotBlockError.None;
            int largestCount = -1;
            Assert.DoesNotThrow(
                () => SnapshotBlocks.TryReadEventsBlock(largest, SnapCfg, bigDestination, out largestCount, out largestError));
            Assert.AreEqual(SnapshotBlockError.MalformedLength, largestError,
                "65535 is addressable, so it passes the length guard and fails only on its trailing partial record");
            Assert.AreEqual(ushort.MaxValue / SnapshotBlocks.EventHeaderBytes, largestCount,
                "every whole record before the trailing remainder was decoded");
        }

        [Test]
        public void EnumDomainBounds_MatchTheSimulationEnums()
        {
            // Fix-round I1. These three constants are what every decoder
            // validates against, so they are pinned twice over: literally,
            // and against the enum's own member count. The count half is the
            // tripwire — adding a MobAiState in Stage 3 reddens THIS test,
            // which says in words that the wire domain moved and needs a
            // ProtocolVersion bump, instead of silently making legal Stage 3
            // traffic unparseable by a decoder nobody thought to update.
            //
            // Stage 3 Task 10 (spec Р213/Р251) is exactly that: MobType
            // gained Elite AND Director, so the two MobType-shaped
            // assertions below move from Gunner/2 to Director/4 — this IS
            // the tripwire firing, on schedule, not a defect to silence.
            // SnapshotBlocks.MaxMobTypeValue now reads
            // `(byte)MobType.Director`, alongside the ProtocolVersion bump
            // its own HISTORY entry records (ProtocolVersion_Current_
            // IsPinnedToThree, this file, updates in the same commit — see
            // that test's own doc). MobAiState/WavePhase are UNCHANGED
            // (Р214: Elite/Director reuse the existing six-state FSM, no
            // new state) — only the MobType pair below moved.
            Assert.AreEqual((byte)3, SnapshotBlocks.MaxMobTypeValue, "MobType tops out at Director");
            Assert.AreEqual((byte)5, SnapshotBlocks.MaxMobAiStateValue, "MobAiState tops out at Fire");
            Assert.AreEqual((byte)1, SnapshotBlocks.MaxWavePhaseValue, "WavePhase tops out at Active");

            Assert.AreEqual(4, System.Enum.GetValues(typeof(MobType)).Length,
                "MobType gained or lost a member — the wire domain moved");
            Assert.AreEqual(6, System.Enum.GetValues(typeof(MobAiState)).Length,
                "MobAiState gained or lost a member — the wire domain moved");
            Assert.AreEqual(2, System.Enum.GetValues(typeof(WavePhase)).Length,
                "WavePhase gained or lost a member — the wire domain moved");

            // Stage 3 Task 25: three more enums reach the wire, and Tasks
            // 30-32 index a tint/prefab/icon table by every one of them.
            Assert.AreEqual((byte)3, SnapshotBlocks.MaxMatchPhaseValue, "MatchPhase tops out at Ended");
            Assert.AreEqual((byte)0, SnapshotBlocks.MaxPickupKindValue, "PickupKind tops out at EnergyCell");
            Assert.AreEqual((byte)4, SnapshotBlocks.MaxContainerKindValue,
                "ContainerKind tops out at PlayerCorpse");

            Assert.AreEqual(4, System.Enum.GetValues(typeof(MatchPhase)).Length,
                "MatchPhase gained or lost a member — the wire domain moved");
            Assert.AreEqual(1, System.Enum.GetValues(typeof(PickupKind)).Length,
                "PickupKind gained or lost a member — the wire domain moved");
            Assert.AreEqual(5, System.Enum.GetValues(typeof(ContainerKind)).Length,
                "ContainerKind gained or lost a member — the wire domain moved");
        }

        [Test]
        public void MalformedContent_MobTypeOrAiOutsideItsDomain_Rejected_NoException()
        {
            // Fix-round I1. `(MobType)15` and `(MobAiState)7` are legal casts
            // and illegal values; before this the decoder returned true and
            // handed them straight to a consumer that indexes prefab and
            // animator tables by exactly these. One hostile byte, one
            // IndexOutOfRange on the client's render path.
            void AssertPackedByteRefused(byte packed, string what)
            {
                var block = new byte[SnapshotBlocks.MobRecordBytes];
                block[2] = packed;
                var destination = new SnapshotBlocks.MobRecord[4];
                bool ok = true;
                SnapshotBlockError error = SnapshotBlockError.None;
                int count = -1;
                Assert.DoesNotThrow(
                    () => ok = SnapshotBlocks.TryReadMobsBlock(block, SnapCfg, destination, out count, out error),
                    $"{what}: hostile content is ordinary input, never an exception (Р82)");
                Assert.IsFalse(ok, what);
                Assert.AreEqual(SnapshotBlockError.MalformedContent, error, what);
                Assert.AreEqual(0, count, $"{what}: the whole block is rejected, no record is yielded");
            }

            // Stage 3 Task 10 coordinator finding: this fixture used to be
            // the hardcoded literal 0x20 ("type nibble 2, one past
            // Gunner") — MobType growing Elite/Director on to that exact
            // value would have turned a refusal fixture into a silent
            // false negative (the domain widened, so nibble 2 decodes
            // clean now). Built off MaxMobTypeValue itself so the NEXT
            // MobType growth cannot repeat this without the fixture moving
            // with it.
            AssertPackedByteRefused((byte)((SnapshotBlocks.MaxMobTypeValue + 1) << 4),
                "type nibble one past MaxMobTypeValue");
            AssertPackedByteRefused(0xF0, "type nibble 15");
            AssertPackedByteRefused(0x06, "ai nibble 6, one past Fire");
            AssertPackedByteRefused(0x0F, "ai nibble 15");
            AssertPackedByteRefused(0xF7, "both nibbles out of domain");

            // ...and the highest LEGAL packing still decodes, so the guard
            // rejects the domain rather than everything above some smaller
            // number it happened to be written with.
            var legal = new byte[SnapshotBlocks.MobRecordBytes];
            legal[2] = (byte)((SnapshotBlocks.MaxMobTypeValue << 4) | SnapshotBlocks.MaxMobAiStateValue);
            var dest = new SnapshotBlocks.MobRecord[1];
            Assert.IsTrue(SnapshotBlocks.TryReadMobsBlock(legal, SnapCfg, dest, out int okCount, out SnapshotBlockError okErr),
                "Director/Fire is the top of both domains and must be accepted");
            Assert.AreEqual(1, okCount);
            Assert.AreEqual(SnapshotBlockError.None, okErr);
            Assert.AreEqual(MobType.Director, dest[0].Type);
            Assert.AreEqual(MobAiState.Fire, dest[0].Ai);
        }

        [Test]
        public void MalformedContent_WavePhaseOutsideItsDomain_Rejected_NoException()
        {
            foreach (byte phaseByte in new byte[] { 2, 200, 255 })
            {
                var block = new byte[SnapshotBlocks.WaveBlockPayloadBytes];
                block[0] = phaseByte;
                bool ok = true;
                SnapshotBlockError error = SnapshotBlockError.None;
                Assert.DoesNotThrow(
                    () => ok = SnapshotBlocks.TryReadWaveBlock(block, out _, out _, out _, out error),
                    $"phase byte {phaseByte}: must not throw (Р82)");
                Assert.IsFalse(ok, $"phase byte {phaseByte} names no WavePhase");
                Assert.AreEqual(SnapshotBlockError.MalformedContent, error, $"phase byte {phaseByte}");
            }

            var legalBlock = new byte[SnapshotBlocks.WaveBlockPayloadBytes];
            legalBlock[0] = SnapshotBlocks.MaxWavePhaseValue;
            Assert.IsTrue(SnapshotBlocks.TryReadWaveBlock(legalBlock, out WavePhase phase, out _, out _, out _),
                "the top of the domain must still be accepted");
            Assert.AreEqual(WavePhase.Active, phase);
        }

        [Test]
        public void MalformedContent_PlayerIndexAtOrAboveMaxPlayers_Rejected_AndBelowAccepted()
        {
            // Fix-round I1, and the reason SnapMaxPlayers is 11 rather than
            // the shipped 3: index 10 must pass and index 11 must not, which
            // a hardcoded bound cannot do. The refusal must also reject the
            // WHOLE block — the second record here is perfectly well formed.
            void AssertIndexRefused(byte index)
            {
                var records = new[]
                {
                    new SnapshotBlocks.PlayerRecord
                    {
                        Index = index, Pos = float2.zero, Dir = new float2(1f, 0f),
                        Hp = 0f, Flags = PlayerWireFlags.Alive,
                    },
                    PlayerP1,
                };
                var buffer = new byte[SnapshotWriter.HeaderBytes + SnapshotWriter.PlayersBlockBytes(2)];
                var writer = new SnapshotWriter(buffer);
                writer.WriteHeader(Epoch, Tick, Flags);
                writer.WritePlayersBlock(records, SnapCfg);

                var destination = new SnapshotBlocks.PlayerRecord[4];
                bool ok = true;
                SnapshotBlockError error = SnapshotBlockError.None;
                int count = -1;
                Assert.DoesNotThrow(() =>
                {
                    var reader = new SnapshotReader(buffer);
                    reader.TryReadHeader(out _, out _, out _);
                    reader.TryReadBlock(AllBlockKinds, out _, out System.ReadOnlySpan<byte> payload);
                    ok = SnapshotBlocks.TryReadPlayersBlock(payload, SnapCfg, destination, out count, out error);
                }, $"index {index}: hostile content must not throw (Р82)");
                Assert.IsFalse(ok, $"index {index} is at or above MaxPlayers {SnapMaxPlayers}");
                Assert.AreEqual(SnapshotBlockError.MalformedContent, error, $"index {index}");
                Assert.AreEqual(0, count, $"index {index}: the whole block is rejected, well-formed records included");
            }

            AssertIndexRefused((byte)SnapMaxPlayers);
            AssertIndexRefused(200);
            AssertIndexRefused(byte.MaxValue);

            // The slot one below the cap is legitimate and must survive.
            var highest = new SnapshotBlocks.PlayerRecord
            {
                Index = (byte)(SnapMaxPlayers - 1), Pos = float2.zero, Dir = new float2(1f, 0f),
                Hp = 0f, Flags = PlayerWireFlags.Alive,
            };
            var okBuffer = new byte[SnapshotWriter.HeaderBytes + SnapshotWriter.PlayersBlockBytes(1)];
            var okWriter = new SnapshotWriter(okBuffer);
            okWriter.WriteHeader(Epoch, Tick, Flags);
            okWriter.WritePlayersBlock(new[] { highest }, SnapCfg);
            var okDest = new SnapshotBlocks.PlayerRecord[1];
            var okReader = new SnapshotReader(okBuffer);
            okReader.TryReadHeader(out _, out _, out _);
            okReader.TryReadBlock(AllBlockKinds, out _, out System.ReadOnlySpan<byte> okPayload);
            Assert.IsTrue(SnapshotBlocks.TryReadPlayersBlock(okPayload, SnapCfg, okDest, out int okCount, out SnapshotBlockError okErr),
                "the highest legal slot index must be accepted");
            Assert.AreEqual(1, okCount);
            Assert.AreEqual(SnapshotBlockError.None, okErr);
            Assert.AreEqual((byte)(SnapMaxPlayers - 1), okDest[0].Index);
        }

        [Test]
        public void Writer_MobTypeOrAiOutsideItsDomain_Throws()
        {
            // The write side's mirror of the read side's refusal (fix-round
            // M6/I1): a nibble cannot carry a value above 15, and masking one
            // would put a DIFFERENT, perfectly legal-looking mob type on the
            // wire. That is a caller bug, so it throws — the same asymmetry
            // Task 26 introduced and this file keeps.
            var buffer = new byte[SnapshotWriter.HeaderBytes + SnapshotWriter.MobsBlockBytes(1)];

            Assert.Throws<System.ArgumentException>(() =>
            {
                var writer = new SnapshotWriter(buffer);
                writer.WriteHeader(Epoch, Tick, Flags);
                writer.WriteMobsBlock(
                    new[] { new SnapshotBlocks.MobRecord { Id = 1, Type = (MobType)9, Ai = MobAiState.Idle, Dir = new float2(1f, 0f) } },
                    SnapCfg);
            }, "a MobType outside its domain cannot be packed into a nibble");

            Assert.Throws<System.ArgumentException>(() =>
            {
                var writer = new SnapshotWriter(buffer);
                writer.WriteHeader(Epoch, Tick, Flags);
                writer.WriteMobsBlock(
                    new[] { new SnapshotBlocks.MobRecord { Id = 1, Type = MobType.Chaser, Ai = (MobAiState)12, Dir = new float2(1f, 0f) } },
                    SnapCfg);
            }, "a MobAiState outside its domain cannot be packed into a nibble");
        }

        [Test]
        public void EventPayloadOverrun_PriorRecordsRemainDelivered_SubsequentDoNot_NoException()
        {
            // Record A: well-formed, zero payload (E2's own header bytes).
            var block = new System.Collections.Generic.List<byte>();
            block.Add(0xD3);
            block.Add(0x11); block.Add(0x10);
            block.Add(53);
            block.Add(0xC5); block.Add(0x2E);
            block.Add(0xFF); block.Add(0xBF);
            block.Add(0); // payloadBytes = 0 — well formed

            // Record B: declares payloadBytes = 250 with only 2 bytes left — a lie.
            block.Add(0xD2);
            block.Add(0x01); block.Add(0x02);
            block.Add(1);
            block.Add(0x00); block.Add(0x00);
            block.Add(0x00); block.Add(0x00);
            block.Add(250);
            block.Add(0xAA); block.Add(0xBB);

            var destination = new SnapshotBlocks.EventRecord[4];
            bool ok = true;
            SnapshotBlockError error = SnapshotBlockError.None;
            int count = -1;
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadEventsBlock(block.ToArray(), SnapCfg, destination, out count, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.EventPayloadOverrun, error);
            Assert.AreEqual(1, count, "record A must have been delivered before the lie was discovered");
            Assert.AreEqual((byte)0xD3, destination[0].Kind, "the already-decoded record A must remain in the destination");
            Assert.AreEqual((ushort)4113, destination[0].Seq);
        }

        // ---- T27.15. Fuzz: truncate the full frame at every length ----

        [Test]
        public void TruncatedFiveBlockFrame_AtEveryLength_BlockPayloadsNeverThrow()
        {
            byte[] frame = BuildCanonicalFiveBlockFrame();

            for (int length = frame.Length; length >= 0; length--)
            {
                int cut = length;
                Assert.DoesNotThrow(() =>
                {
                    var reader = new SnapshotReader(new System.ReadOnlySpan<byte>(frame, 0, cut));
                    reader.TryReadHeader(out _, out _, out _);
                    while (reader.TryReadBlock(AllBlockKinds, out byte kind, out System.ReadOnlySpan<byte> payload))
                    {
                        switch ((SnapshotBlockKind)kind)
                        {
                            case SnapshotBlockKind.Players:
                                SnapshotBlocks.TryReadPlayersBlock(payload, SnapCfg, new SnapshotBlocks.PlayerRecord[8], out _, out _);
                                break;
                            case SnapshotBlockKind.Liveness:
                                SnapshotBlocks.TryReadLivenessBlock(payload, out _, out _, out _);
                                break;
                            case SnapshotBlockKind.Mobs:
                                SnapshotBlocks.TryReadMobsBlock(payload, SnapCfg, new SnapshotBlocks.MobRecord[8], out _, out _);
                                break;
                            case SnapshotBlockKind.Wave:
                                SnapshotBlocks.TryReadWaveBlock(payload, out _, out _, out _, out _);
                                break;
                            case SnapshotBlockKind.Events:
                                SnapshotBlocks.TryReadEventsBlock(payload, SnapCfg, new SnapshotBlocks.EventRecord[8], out _, out _);
                                break;
                        }
                    }
                }, $"length {cut}: no block decoder may ever throw (Р82)");
            }
        }

        // ---- T27.16. u16 id truncation: pinned literals + collision ----

        [Test]
        public void MobId_U16Truncation_PinnedLiterals_AndCollisionAcrossTheWraparound()
        {
            void AssertIdBytes(int id, byte expectedLow, byte expectedHigh)
            {
                var record = new SnapshotBlocks.MobRecord
                {
                    Id = id, Type = MobType.Chaser, Ai = MobAiState.Idle,
                    Pos = float2.zero, Dir = new float2(1f, 0f), Hp = 0f,
                };
                var buffer = new byte[SnapshotWriter.HeaderBytes + SnapshotWriter.MobsBlockBytes(1)];
                var writer = new SnapshotWriter(buffer);
                writer.WriteHeader(Epoch, Tick, Flags);
                writer.WriteMobsBlock(new[] { record }, SnapCfg);
                int r0 = SnapshotWriter.HeaderBytes + SnapshotWriter.BlockHeaderBytes;
                Assert.AreEqual(expectedLow, buffer[r0 + 0], $"id {id}: low byte");
                Assert.AreEqual(expectedHigh, buffer[r0 + 1], $"id {id}: high byte");
            }

            AssertIdBytes(65535, 0xFF, 0xFF);
            AssertIdBytes(65536, 0x00, 0x00);
            AssertIdBytes(65537, 0x01, 0x00);

            // Two ids 65536 apart collide to the identical wire code
            // (task-27-brief §2.8) — a lossy, documented property, not a
            // proof that collisions cannot occur.
            var records = new[]
            {
                new SnapshotBlocks.MobRecord { Id = 7, Type = MobType.Chaser, Ai = MobAiState.Idle, Pos = float2.zero, Dir = new float2(1f, 0f), Hp = 0f },
                new SnapshotBlocks.MobRecord { Id = 65543, Type = MobType.Chaser, Ai = MobAiState.Idle, Pos = float2.zero, Dir = new float2(1f, 0f), Hp = 0f },
            };
            var buf2 = new byte[SnapshotWriter.HeaderBytes + SnapshotWriter.MobsBlockBytes(2)];
            var w2 = new SnapshotWriter(buf2);
            w2.WriteHeader(Epoch, Tick, Flags);
            w2.WriteMobsBlock(records, SnapCfg);
            int rec0 = SnapshotWriter.HeaderBytes + SnapshotWriter.BlockHeaderBytes;
            int rec1 = rec0 + SnapshotBlocks.MobRecordBytes;
            Assert.AreEqual(buf2[rec0 + 0], buf2[rec1 + 0], "id 7 and id 65543 must collide to the same wire code low byte");
            Assert.AreEqual(buf2[rec0 + 1], buf2[rec1 + 1], "id 7 and id 65543 must collide to the same wire code high byte");
            Assert.AreEqual((byte)7, buf2[rec0 + 0], "the shared code is 7 (65543 & 0xFFFF == 7)");
            Assert.AreEqual((byte)0, buf2[rec0 + 1]);
        }

        // ---- T27.17. HP decoded by the record's OWN type ----

        [Test]
        public void MobHp_DecodedByOwnType_NotAlwaysChaserMaxHp()
        {
            // Hand-built raw block: two records share the SAME hp byte (200)
            // but declare different types — the decoder must read
            // `typeAndAi` FIRST and pick cfg.Chaser.MaxHp / cfg.Gunner.MaxHp
            // accordingly (task-27-brief §2.7). SnapCfg's Chaser.MaxHp
            // (47) != Gunner.MaxHp (33), so a mutation hardcoding either
            // must fail on at least one of the two decoded values.
            var block = new System.Collections.Generic.List<byte>();
            block.Add(0x00); block.Add(0x00);
            block.Add(0x00); // typeAndAi = Chaser(0)<<4 | Idle(0)
            block.Add(0x00); block.Add(0x00);
            block.Add(0x00); block.Add(0x00);
            block.Add(0x00);
            block.Add(200);
            block.Add(0x00); block.Add(0x00);
            block.Add(0x10); // typeAndAi = Gunner(1)<<4 | Idle(0)
            block.Add(0x00); block.Add(0x00);
            block.Add(0x00); block.Add(0x00);
            block.Add(0x00);
            block.Add(200);

            var destination = new SnapshotBlocks.MobRecord[2];
            Assert.IsTrue(SnapshotBlocks.TryReadMobsBlock(block.ToArray(), SnapCfg, destination, out int count, out SnapshotBlockError error));
            Assert.AreEqual(2, count);
            Assert.AreEqual(SnapshotBlockError.None, error);
            Assert.AreEqual(MobType.Chaser, destination[0].Type);
            Assert.AreEqual(MobType.Gunner, destination[1].Type);
            Assert.AreNotEqual(destination[0].Hp, destination[1].Hp,
                "the same hp BYTE must decode to different VALUES because the two records carry different MaxHp");

            float expectedChaserHp = 200f / 255f * SnapChaserMaxHp;
            float expectedGunnerHp = 200f / 255f * SnapGunnerMaxHp;
            Assert.That(destination[0].Hp, Is.EqualTo(expectedChaserHp).Within(SnapChaserMaxHp / 255f / 2f + 1e-4f));
            Assert.That(destination[1].Hp, Is.EqualTo(expectedGunnerHp).Within(SnapGunnerMaxHp / 255f / 2f + 1e-4f));
        }

        // Stage 3 Task 10 fixture numbers, same "checked against every
        // .asset, appears in none" discipline as SnapChaserMaxHp/
        // SnapGunnerMaxHp above — distinct from both of those AND from each
        // other, so a mutation that quietly reused Gunner's own cap for
        // either new archetype cannot hide behind an accidental match.
        const float SnapEliteMaxHp = 58f;
        const float SnapDirectorMaxHp = 91f;

        [Test]
        public void MaxHpFor_DecodesAgainstOwnArchetypeCap()
        {
            // MaxHpFor's own doc (SnapshotBlocks.cs) predicted this exact
            // moment: "a third MobType in Stage 3 would have needed both
            // edits with neither a compile error nor a red test to demand
            // the second". Elite and Director are the third AND fourth
            // archetype in one task — each needs its OWN cap read out, not
            // a shared Gunner fallback.
            //
            // Called DIRECTLY rather than through TryReadMobsBlock (unlike
            // MobHp_DecodedByOwnType_NotAlwaysChaserMaxHp above): MaxHpFor
            // is the unit under test, and isolating it needs no detour
            // through the decoder's own domain gate (`MaxMobTypeValue`,
            // now Director-wide) or the packed-byte record layout — a
            // direct call pins the exact function this task's fourteen-
            // branch table names, nothing upstream of it.
            var cfg = SnapCfg;
            cfg.Elite = new MobSimConfig { MaxHp = SnapEliteMaxHp };
            cfg.Director = new MobSimConfig { MaxHp = SnapDirectorMaxHp };
            Assert.AreEqual(SnapEliteMaxHp, SnapshotBlocks.MaxHpFor(MobType.Elite, in cfg));
            Assert.AreEqual(SnapDirectorMaxHp, SnapshotBlocks.MaxHpFor(MobType.Director, in cfg));
        }

        // ---- T27.18. Player flag bit positions, each pinned individually ----

        [Test]
        public void PlayerFlags_BitPositionsArePinned_Individually()
        {
            Assert.AreEqual((byte)0x01, PlayerWireFlags.Alive);
            Assert.AreEqual((byte)0x02, PlayerWireFlags.Dashing);
            Assert.AreEqual((byte)0x04, PlayerWireFlags.Sliding);
            Assert.AreEqual((byte)0x08, PlayerWireFlags.AimHeld);
            Assert.AreEqual((byte)0x10, PlayerWireFlags.LinkWindow);

            void AssertFlagRoundTrips(byte flag, string what)
            {
                var record = new SnapshotBlocks.PlayerRecord
                {
                    Index = 0, Pos = float2.zero, Dir = new float2(1f, 0f), Hp = 0f, Flags = flag,
                };
                var buffer = new byte[SnapshotWriter.HeaderBytes + SnapshotWriter.PlayersBlockBytes(1)];
                var writer = new SnapshotWriter(buffer);
                writer.WriteHeader(Epoch, Tick, Flags);
                writer.WritePlayersBlock(new[] { record }, SnapCfg);
                int flagsByteOffset = SnapshotWriter.HeaderBytes + SnapshotWriter.BlockHeaderBytes + 7;
                Assert.AreEqual(flag, buffer[flagsByteOffset], $"{what}: wire byte must be exactly this single bit");

                var reader = new SnapshotReader(buffer);
                reader.TryReadHeader(out _, out _, out _);
                reader.TryReadBlock(AllBlockKinds, out _, out System.ReadOnlySpan<byte> payload);
                var dest = new SnapshotBlocks.PlayerRecord[1];
                SnapshotBlocks.TryReadPlayersBlock(payload, SnapCfg, dest, out _, out _);
                Assert.AreEqual(flag, dest[0].Flags, $"{what}: decoded flags must match exactly");
            }

            AssertFlagRoundTrips(PlayerWireFlags.Alive, "Alive -> bit0");
            AssertFlagRoundTrips(PlayerWireFlags.Dashing, "Dashing -> bit1");
            AssertFlagRoundTrips(PlayerWireFlags.Sliding, "Sliding -> bit2");
            AssertFlagRoundTrips(PlayerWireFlags.AimHeld, "AimHeld -> bit3");
            AssertFlagRoundTrips(PlayerWireFlags.LinkWindow, "LinkWindow -> bit4");
        }

        // ---- T27.19. Frame header's reserved flags byte is untouched ----

        [Test]
        public void FrameHeaderFlagsByte_UntouchedByAnyBlockMethod()
        {
            byte[] frame = BuildCanonicalFiveBlockFrame();
            // task-27-brief §2.12: Task 27 assigns no bit in the header's
            // reserved byte 7. None of the five block writers may touch it
            // — they all write starting at `_pos`, past the header.
            Assert.AreEqual(Flags, frame[7], "byte 7 (header flags) must be untouched by every Task 27 block writer");
        }

        // ---- T27.20 (§2.15). Zero allocations across all five blocks ----

        [Test]
        public void WriteThenReadAllBlocks_DoesNotAllocateGCMemory()
        {
            int size = SnapshotWriter.HeaderBytes
                       + SnapshotWriter.PlayersBlockBytes(2)
                       + SnapshotWriter.LivenessBlockBytes()
                       + SnapshotWriter.MobsBlockBytes(2)
                       + SnapshotWriter.WaveBlockBytes()
                       + SnapshotWriter.EventsBlockBytes(2, EventE1.PayloadLength + EventE2.PayloadLength);
            var buffer = new byte[size];
            var players = new[] { PlayerP1, PlayerP2 };
            var mobs = new[] { MobM1, MobM2 };
            var events = new[] { EventE1, EventE2 };
            var known = AllBlockKinds;
            var playerDest = new SnapshotBlocks.PlayerRecord[4];
            var mobDest = new SnapshotBlocks.MobRecord[4];
            var eventDest = new SnapshotBlocks.EventRecord[4];

            // Warm-up OUTSIDE the measured lambda, and the fixture premise
            // that defeats a do-nothing stub (Task 26 finding F-D, task-27-
            // brief §2.15/§4): the measured body must actually write and
            // decode all five blocks, not merely fail fast.
            {
                var w = new SnapshotWriter(buffer);
                w.WriteHeader(Epoch, Tick, Flags);
                w.WritePlayersBlock(players, SnapCfg);
                w.WriteLivenessBlock(LivenessFixtureMask, LivenessFixtureExtractedMask);
                w.WriteMobsBlock(mobs, SnapCfg);
                w.WriteWaveBlock(WaveFixturePhase, WaveFixtureIndex, WaveFixtureAliveCount);
                w.WriteEventsBlock(events, EventPayloadPool, SnapCfg);
                Assert.AreEqual(size, w.BytesWritten, "fixture premise (stub-defeating): the full frame must be written");

                var r = new SnapshotReader(buffer);
                Assert.IsTrue(r.TryReadHeader(out _, out _, out _));
                int delivered = 0;
                while (r.TryReadBlock(known, out byte kind, out System.ReadOnlySpan<byte> payload))
                {
                    delivered++;
                    switch ((SnapshotBlockKind)kind)
                    {
                        case SnapshotBlockKind.Players:
                            Assert.IsTrue(SnapshotBlocks.TryReadPlayersBlock(payload, SnapCfg, playerDest, out int pc, out _));
                            Assert.AreEqual(2, pc, "fixture premise (stub-defeating): both players must decode");
                            break;
                        case SnapshotBlockKind.Mobs:
                            Assert.IsTrue(SnapshotBlocks.TryReadMobsBlock(payload, SnapCfg, mobDest, out int mc, out _));
                            Assert.AreEqual(2, mc, "fixture premise (stub-defeating): both mobs must decode");
                            break;
                        case SnapshotBlockKind.Events:
                            Assert.IsTrue(SnapshotBlocks.TryReadEventsBlock(payload, SnapCfg, eventDest, out int ec, out _));
                            Assert.AreEqual(2, ec, "fixture premise (stub-defeating): both events must decode");
                            break;
                    }
                }
                Assert.AreEqual(5, delivered, "fixture premise (stub-defeating): all five blocks must be delivered");
                Assert.IsFalse(r.Failed);
            }

            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var w = new SnapshotWriter(buffer);
                    w.WriteHeader(Epoch, Tick, Flags);
                    w.WritePlayersBlock(players, SnapCfg);
                    w.WriteLivenessBlock(LivenessFixtureMask, LivenessFixtureExtractedMask);
                    w.WriteMobsBlock(mobs, SnapCfg);
                    w.WriteWaveBlock(WaveFixturePhase, WaveFixtureIndex, WaveFixtureAliveCount);
                    w.WriteEventsBlock(events, EventPayloadPool, SnapCfg);

                    var r = new SnapshotReader(buffer);
                    r.TryReadHeader(out _, out _, out _);
                    while (r.TryReadBlock(known, out byte kind, out System.ReadOnlySpan<byte> payload))
                    {
                        switch ((SnapshotBlockKind)kind)
                        {
                            case SnapshotBlockKind.Players:
                                SnapshotBlocks.TryReadPlayersBlock(payload, SnapCfg, playerDest, out _, out _);
                                break;
                            case SnapshotBlockKind.Liveness:
                                SnapshotBlocks.TryReadLivenessBlock(payload, out _, out _, out _);
                                break;
                            case SnapshotBlockKind.Mobs:
                                SnapshotBlocks.TryReadMobsBlock(payload, SnapCfg, mobDest, out _, out _);
                                break;
                            case SnapshotBlockKind.Wave:
                                SnapshotBlocks.TryReadWaveBlock(payload, out _, out _, out _, out _);
                                break;
                            case SnapshotBlockKind.Events:
                                SnapshotBlocks.TryReadEventsBlock(payload, SnapCfg, eventDest, out _, out _);
                                break;
                        }
                    }
                }
            }, Is.Not.AllocatingGCMemory());
        }

        // ================= Stage 2 Task 28: the wire-event CATALOG =================
        //
        // Task 27 left the record's `kind` byte and its payload opaque. This is
        // the catalog that fills them (task-28-brief §2.2) plus the frame
        // budget the assembler spends (§2.8).
        //
        // FIXTURE NUMBERS, STATED AS WHAT WAS SEARCHED (same discipline and
        // same pattern as the Task 26/27 note above):
        //   grep -nP "(?<![\d.])<N>(?![\d.])" client/Assets/Data/*.asset
        // run over all ten balance assets for every DATA number this region
        // introduces — 210, 6.5, 61, 17, 4919, 51001, 7, 39, 7.25, 2.75, 1.25,
        // 91, 57, 40507, 0.28, 0.96. Every one of them produces ZERO matches
        // except `7`, whose only three hits are the pattern landing inside GUID
        // hex on `m_Script: {..., guid: ...}` lines — the same false positive
        // this file already documents for 14, 19, 22 and 23. No fixture matches
        // a VALUE. Four numbers were CHANGED to get there and saying so is the
        // point: 44 (proposed for the weapon speed) lives in ArenaConfig's wall
        // `B: {x: 2, y: 44}`, 13 (velZ) and 9 (an index) in obstacle
        // coordinates and Gunner.PreferredRange, 8 (an index) in
        // MobGunnerConfig.ProjectileDamage. They became 61, -2.75 and 7.
        //   Slot INDICES are the one place where a collision is unavoidable and
        // also meaningless: they range over [0, MaxPlayers) — small integers
        // that every balance sheet is full of — and no code path could
        // substitute a damage multiplier for a slot number. 7 was picked
        // because it happens to be clean anyway; the boundary pair 10/11 below
        // is structural (it is MaxPlayers itself, minus one).
        //   Structural numbers (payload sizes, byte offsets, block overhead)
        // are not fixtures, same rule as the rest of this file.

        const float EvtStaminaMax = 210f;
        const float EvtMaxAimHeight = 6.5f;
        const float EvtWeaponSpeed = 61f;
        const float EvtGunnerSpeed = 17f;

        /// Extends the Task 27 fixture config with the four scales the event
        /// payloads quantize against. Radius/MaxPlayers/MaxHp are REUSED from
        /// SnapCfg's own constants rather than restated (rule 2), so the
        /// boundary tests below inherit the same deliberately-not-3 MaxPlayers.
        static readonly SimConfig EvtCfg = new SimConfig
        {
            Arena = new ArenaSimConfig { Radius = SnapRadius, MaxPlayers = SnapMaxPlayers },
            Hero = new HeroSimConfig
            {
                MaxHp = SnapHeroMaxHp, StaminaMax = EvtStaminaMax, MaxAimHeight = EvtMaxAimHeight,
            },
            Weapon = new WeaponSimConfig { ProjectileSpeed = EvtWeaponSpeed },
            Chaser = new MobSimConfig { MaxHp = SnapChaserMaxHp },
            Gunner = new MobSimConfig { MaxHp = SnapGunnerMaxHp, ProjectileSpeed = EvtGunnerSpeed },
        };

        const int EvtRoundId = 4919;        // 0x1337 -> 0x37, 0x13
        const int EvtMobId = 51001;         // 0xC739 -> 0x39, 0xC7
        const byte EvtSlot = 7;             // a real slot: below SnapMaxPlayers (11)
        const float EvtHorizSpeedPlayer = 39f;   // /61  -> 163
        const float EvtHorizSpeedMob = 7.25f;    // /17  -> 109
        const float EvtVelZ = -2.75f;            // /61  -> 31290 (0x3A,0x7A); /17 -> 27467 (0x4B,0x6B)
        const float EvtHeightHigh = 2.75f;       // /6.5 -> 108
        const float EvtHeightLow = 1.25f;        // /6.5 -> 49
        const float EvtDamage = 91f;             // /118 -> 197
        const float EvtStaminaMissing = 57f;     // /210 -> 69
        const int EvtWaveStartedIndex = 40507;   // 0x9E3B -> 0x3B, 0x9E

        // Non-palindromic headings whose Dir codes differ from each other and
        // from every rail value above (204 and 140).
        static readonly float2 EvtDirA = new float2(-0.28f, 0.96f);   // -> 204
        static readonly float2 EvtDirB = new float2(0.96f, 0.28f);    // -> 140

        // Half a quantization step, derived from the wire widths HERE, never
        // from Quantize (same rule as the Task 27 region): `Unit` spans
        // [0, max] over 256 codes, `Pos` spans [-r, +r] over 65536.
        static float HalfStepUnit(float max) => max / 255f / 2f;
        static float HalfStepPos(float radius) => radius / 65535f;

        static System.ReadOnlySpan<byte> Payload(byte[] buffer, SnapshotEventKind kind)
            => new System.ReadOnlySpan<byte>(buffer, 0, SnapshotEvents.PayloadBytesFor(kind));

        static SnapshotEventPayload Decoded(byte[] buffer, SnapshotEventKind kind)
        {
            Assert.IsTrue(SnapshotEvents.TryReadPayload(kind, Payload(buffer, kind), EvtCfg,
                out SnapshotEventPayload value, out SnapshotBlockError error),
                $"{kind}: a payload this codec just wrote must decode");
            Assert.AreEqual(SnapshotBlockError.None, error);
            Assert.AreEqual(kind, value.Kind, "the decoded payload must remember which kind it is");
            return value;
        }

        /// Writes one payload into a sentinel-filled buffer with room to spare,
        /// asserting the returned length and that nothing was written past it.
        static byte[] WritePayload(SnapshotEventKind kind, System.Func<byte[], int> write)
        {
            const int tailBytes = 4;
            var buffer = Filled(SnapshotEvents.MaxPayloadBytes + tailBytes);
            int written = write(buffer);
            int expected = SnapshotEvents.PayloadBytesFor(kind);
            Assert.AreEqual(expected, written, $"{kind}: a write must report its own declared payload size");
            for (int i = expected; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"{kind}: byte {i} is past the payload and must be untouched");
            return buffer;
        }

        // ---- T28.1/2. Structural: the two enums ----

        [Test]
        public void SnapshotEventKind_ValuesArePinned_AndNoneIsZero()
        {
            Assert.AreEqual((byte)0, (byte)SnapshotEventKind.None,
                "None must stay 0 — the same refusal sentinel contract SnapshotBlockKind.None carries");
            Assert.AreEqual((byte)1, (byte)SnapshotEventKind.ProjectileSpawned);
            Assert.AreEqual((byte)2, (byte)SnapshotEventKind.ProjectileEnded);
            Assert.AreEqual((byte)3, (byte)SnapshotEventKind.ShotHeard);
            Assert.AreEqual((byte)4, (byte)SnapshotEventKind.MobSpawned);
            Assert.AreEqual((byte)5, (byte)SnapshotEventKind.MobDied);
            Assert.AreEqual((byte)6, (byte)SnapshotEventKind.PlayerDamaged);
            Assert.AreEqual((byte)7, (byte)SnapshotEventKind.PlayerDied);
            Assert.AreEqual((byte)8, (byte)SnapshotEventKind.PlayerDashed);
            Assert.AreEqual((byte)9, (byte)SnapshotEventKind.PlayerSlideStarted);
            Assert.AreEqual((byte)10, (byte)SnapshotEventKind.DashRicocheted);
            Assert.AreEqual((byte)11, (byte)SnapshotEventKind.StaminaDenied);
            Assert.AreEqual((byte)12, (byte)SnapshotEventKind.WaveStarted);
            Assert.AreEqual((byte)13, (byte)SnapshotEventKind.WaveCleared);

            Assert.AreEqual((byte)14, (byte)SnapshotEventKind.DirectorActivated);
            Assert.AreEqual((byte)15, (byte)SnapshotEventKind.DirectorDied);
            Assert.AreEqual((byte)16, (byte)SnapshotEventKind.PlayerExtracted);
            Assert.AreEqual((byte)17, (byte)SnapshotEventKind.PickupTaken);
            Assert.AreEqual((byte)18, (byte)SnapshotEventKind.ContainerEmptied);

            // The catalog is DENSE and ContainerEmptied really is its top — the
            // decoder's own "is this kind known" test is a range check, so a
            // kind added above WaveCleared without moving that bound would be
            // silently unreadable, and a gap in the middle would make a
            // never-declared value decode as legal. (Strengthening recorded in
            // the report: without these two the test passed on a stubbed
            // catalog, because an enum declaration has no body to stub.)
            var declared = new HashSet<byte>();
            foreach (SnapshotEventKind kind in System.Enum.GetValues(typeof(SnapshotEventKind)))
                declared.Add((byte)kind);
            // Stage 3 Т29 moved the top from WaveCleared to ContainerEmptied
            // and the count from 14 to 19 — the five raid kinds. The
            // range check below is exactly the tripwire this comment
            // predicted: SnapshotEvents.IsKnown bounds against the top
            // member, so appending a kind without moving that bound leaves it
            // silently unreadable on the receiver.
            Assert.AreEqual(19, declared.Count, "None plus eighteen kinds — no duplicate values");
            for (byte v = 0; v <= (byte)SnapshotEventKind.ContainerEmptied; v++)
                Assert.IsTrue(declared.Contains(v), $"value {v} must be declared — the catalog has no gaps");
            Assert.AreEqual((byte)(declared.Count - 1), (byte)SnapshotEventKind.ContainerEmptied,
                "and ContainerEmptied is the top of the range every decoder bounds against");
        }

        [Test]
        public void ProjectileEndKind_ValuesArePinned_AndNoneIsZero()
        {
            Assert.AreEqual((byte)0, (byte)ProjectileEndKind.None,
                "None must stay 0 and is never written — a zero here would be indistinguishable from an "
                + "uninitialized payload byte");
            Assert.AreEqual((byte)1, (byte)ProjectileEndKind.Blocked);
            Assert.AreEqual((byte)2, (byte)ProjectileEndKind.Expired);
            Assert.AreEqual((byte)3, (byte)ProjectileEndKind.HitMob);
            // Stage 2 Task 44a: the fourth ending. This test's own strengthening
            // note below predicted it and asked to be told HERE when the wire
            // domain moved — so the numbers move together, deliberately, rather
            // than the bound being widened while the pin quietly kept claiming
            // HitMob was the top.
            Assert.AreEqual((byte)4, (byte)ProjectileEndKind.HitPlayer);
            Assert.AreEqual((byte)ProjectileEndKind.HitPlayer, SnapshotEvents.MaxProjectileEndKindValue,
                "the domain bound must track the enum's own top value");
            Assert.AreEqual((byte)HitZone.Head, SnapshotEvents.MaxHitZoneValue);

            // Strengthening (recorded in the report): the three assertions
            // above are about declarations and passed on a stubbed catalog.
            // These tie the bounds to the enums they claim to bound, so a
            // fourth ProjectileEndKind or a fourth HitZone in a later stage
            // fails HERE, saying the wire domain moved, instead of quietly
            // making legal traffic unparseable.
            Assert.AreEqual(5, System.Enum.GetValues(typeof(ProjectileEndKind)).Length,
                "None + four endings — growing this enum is a ProtocolVersion question");
            Assert.AreEqual(4, System.Enum.GetValues(typeof(HitZone)).Length,
                "None/Legs/Body/Head — the wire carries this as a raw byte");
            foreach (ProjectileEndKind k in System.Enum.GetValues(typeof(ProjectileEndKind)))
                Assert.LessOrEqual((byte)k, SnapshotEvents.MaxProjectileEndKindValue,
                    $"{k} must be inside the bound every decoder checks against");
            foreach (HitZone z in System.Enum.GetValues(typeof(HitZone)))
                Assert.LessOrEqual((byte)z, SnapshotEvents.MaxHitZoneValue, $"{z} must be inside the bound");
        }

        [Test]
        public void EventPriority_RanksDeathsAboveImpactsAboveStateAboveCosmetics_ForEveryKind()
        {
            // Р61 in one table, spelled out independently of PriorityOf itself —
            // a `foreach + DoesNotThrow` loop would agree with a uniformly-wrong
            // implementation, exactly the trap EventDeliveryTests.
            // ChannelFor_HandlesEveryKind documents for the channel table.
            var expected = new Dictionary<SnapshotEventKind, byte>
            {
                [SnapshotEventKind.PlayerDied] = SnapshotEvents.PriorityDeath,
                [SnapshotEventKind.MobDied] = SnapshotEvents.PriorityDeath,
                [SnapshotEventKind.PlayerDamaged] = SnapshotEvents.PriorityImpact,
                [SnapshotEventKind.ProjectileEnded] = SnapshotEvents.PriorityImpact,
                [SnapshotEventKind.ProjectileSpawned] = SnapshotEvents.PriorityState,
                [SnapshotEventKind.MobSpawned] = SnapshotEvents.PriorityState,
                [SnapshotEventKind.StaminaDenied] = SnapshotEvents.PriorityState,
                [SnapshotEventKind.WaveStarted] = SnapshotEvents.PriorityState,
                [SnapshotEventKind.WaveCleared] = SnapshotEvents.PriorityState,
                [SnapshotEventKind.ShotHeard] = SnapshotEvents.PriorityCosmetic,
                [SnapshotEventKind.PlayerDashed] = SnapshotEvents.PriorityCosmetic,
                [SnapshotEventKind.PlayerSlideStarted] = SnapshotEvents.PriorityCosmetic,
                [SnapshotEventKind.DashRicocheted] = SnapshotEvents.PriorityCosmetic,
                // Stage 3 Т29 (R-233). The Director's fall is BOTH a death and
                // the gate opening — nothing in a frame outranks it. His
                // arrival and a collector walking out destroy nothing and sit
                // with the wave pair. The last two announce a MOMENT whose
                // state already rides every frame (the Pickups block stops
                // carrying a collected cell; the Containers block carries the
                // "already looted" flag), so a frame's delay costs nothing.
                [SnapshotEventKind.DirectorDied] = SnapshotEvents.PriorityDeath,
                [SnapshotEventKind.DirectorActivated] = SnapshotEvents.PriorityState,
                [SnapshotEventKind.PlayerExtracted] = SnapshotEvents.PriorityState,
                [SnapshotEventKind.PickupTaken] = SnapshotEvents.PriorityCosmetic,
                [SnapshotEventKind.ContainerEmptied] = SnapshotEvents.PriorityCosmetic,
            };

            foreach (SnapshotEventKind kind in System.Enum.GetValues(typeof(SnapshotEventKind)))
            {
                if (kind == SnapshotEventKind.None) continue;
                Assert.IsTrue(expected.ContainsKey(kind),
                    $"{kind} has no entry in this test's own Р61 table — a new kind must be ranked HERE "
                    + "(and in PriorityOf), not left to inherit a rank nobody chose");
                Assert.AreEqual(expected[kind], SnapshotEvents.PriorityOf(kind), $"{kind}'s rank");
            }

            // Lower is more important, and the four bands are distinct — a
            // scale where every value happened to be equal would satisfy the
            // table above only if the table were also flat, which it is not.
            Assert.Less(SnapshotEvents.PriorityDeath, SnapshotEvents.PriorityImpact);
            Assert.Less(SnapshotEvents.PriorityImpact, SnapshotEvents.PriorityState);
            Assert.Less(SnapshotEvents.PriorityState, SnapshotEvents.PriorityCosmetic);
            Assert.Throws<System.ArgumentException>(() => SnapshotEvents.PriorityOf(SnapshotEventKind.None),
                "the never-written sentinel has no rank");
        }

        [Test]
        public void EventPayloadSizes_ArePinned_AndNoneExceedsMaxPayloadBytes()
        {
            Assert.AreEqual(8, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.ProjectileSpawned));
            Assert.AreEqual(5, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.ProjectileEnded));
            Assert.AreEqual(1, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.ShotHeard));
            Assert.AreEqual(3, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.MobSpawned));
            Assert.AreEqual(4, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.MobDied));
            Assert.AreEqual(4, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.PlayerDamaged));
            Assert.AreEqual(2, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.PlayerDied));
            Assert.AreEqual(1, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.PlayerDashed));
            Assert.AreEqual(1, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.PlayerSlideStarted));
            Assert.AreEqual(2, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.DashRicocheted));
            Assert.AreEqual(1, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.StaminaDenied));
            // Stage 3 Т29 (R-232): the two Director kinds are the catalog's
            // FIRST zero-length payloads — pinned rather than left implicit,
            // because "0" is also what a size table returns for a kind
            // somebody forgot, and the two must not be indistinguishable.
            Assert.AreEqual(0, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.DirectorActivated));
            Assert.AreEqual(0, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.DirectorDied));
            Assert.AreEqual(1, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.PlayerExtracted));
            Assert.AreEqual(2, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.PickupTaken));
            Assert.AreEqual(2, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.ContainerEmptied));
            Assert.AreEqual(2, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.WaveStarted));
            Assert.AreEqual(2, SnapshotEvents.PayloadBytesFor(SnapshotEventKind.WaveCleared));

            Assert.AreEqual(8, SnapshotEvents.MaxPayloadBytes,
                "MaxPayloadBytes sizes the assembler's carry-queue slots — it must be the real maximum, "
                + "not a round number chosen next to it");
            foreach (SnapshotEventKind kind in System.Enum.GetValues(typeof(SnapshotEventKind)))
            {
                if (kind == SnapshotEventKind.None) continue;
                Assert.LessOrEqual(SnapshotEvents.PayloadBytesFor(kind), SnapshotEvents.MaxPayloadBytes,
                    $"{kind} must fit a fixed carry-queue slot");
            }
            Assert.Throws<System.ArgumentException>(
                () => SnapshotEvents.PayloadBytesFor(SnapshotEventKind.None));
        }

        // ---- T28.3-7. Payload byte layouts, kind by kind ----

        [Test]
        public void EventPayload_ProjectileSpawned_ByteLayout_OnBothOwnerRails()
        {
            const SnapshotEventKind kind = SnapshotEventKind.ProjectileSpawned;

            byte[] player = WritePayload(kind, b => SnapshotEvents.WriteProjectileSpawned(
                new System.Span<byte>(b), EvtRoundId, EvtSlot, EvtDirA,
                EvtHorizSpeedPlayer, EvtVelZ, EvtHeightHigh, EvtCfg));

            Assert.AreEqual((byte)0x37, player[0], "byte 0: id low (4919 = 0x1337)");
            Assert.AreEqual((byte)0x13, player[1], "byte 1: id high");
            Assert.AreEqual(EvtSlot, player[2], "byte 2: ownerIndex");
            Assert.AreEqual((byte)204, player[3], "byte 3: dir (-0.28, 0.96) -> code 204");
            Assert.AreEqual((byte)163, player[4], "byte 4: horizSpeed 39/61 -> code 163");
            Assert.AreEqual((byte)0x3A, player[5], "byte 5: velZ low (-2.75 over +/-61 -> 31290 = 0x7A3A)");
            Assert.AreEqual((byte)0x7A, player[6], "byte 6: velZ high");
            Assert.AreEqual((byte)108, player[7], "byte 7: height 2.75/6.5 -> code 108");

            byte[] mob = WritePayload(kind, b => SnapshotEvents.WriteProjectileSpawned(
                new System.Span<byte>(b), EvtMobId, ProjectileIds.NoOwner, EvtDirB,
                EvtHorizSpeedMob, EvtVelZ, EvtHeightLow, EvtCfg));

            Assert.AreEqual((byte)0x39, mob[0], "byte 0: id low (51001 = 0xC739)");
            Assert.AreEqual((byte)0xC7, mob[1], "byte 1: id high");
            Assert.AreEqual(ProjectileIds.NoOwner, mob[2], "byte 2: 255 marks a mob's round");
            Assert.AreEqual((byte)140, mob[3], "byte 3: dir (0.96, 0.28) -> code 140");
            Assert.AreEqual((byte)109, mob[4], "byte 4: horizSpeed 7.25/17 -> code 109");
            Assert.AreEqual((byte)0x4B, mob[5], "byte 5: velZ low (-2.75 over +/-17 -> 27467 = 0x6B4B)");
            Assert.AreEqual((byte)0x6B, mob[6], "byte 6: velZ high");
            Assert.AreEqual((byte)49, mob[7], "byte 7: height 1.25/6.5 -> code 49");

            // The rails' whole point: the SAME velZ produces DIFFERENT bytes,
            // because ownerIndex selects the scale (the Task 27 precedent of a
            // mob's HP quantized against its own archetype's MaxHp).
            Assert.AreNotEqual(player[5], mob[5],
                "one velZ, two scales — a single hardcoded speedCap would make these agree");
            Assert.AreNotEqual(player[6], mob[6]);

            // Decoded values, asserted separately from the bytes (урок 108).
            SnapshotEventPayload dp = Decoded(player, kind);
            Assert.AreEqual(EvtRoundId, dp.Id);
            Assert.AreEqual(EvtSlot, dp.PlayerIndex);
            AssertDecodedHeading(dp.Dir, EvtDirA, "player rail dir");
            Assert.That(dp.HorizSpeed, Is.EqualTo(EvtHorizSpeedPlayer)
                .Within(HalfStepUnit(EvtWeaponSpeed) + 1e-3f));
            Assert.That(dp.VelZ, Is.EqualTo(EvtVelZ).Within(HalfStepPos(EvtWeaponSpeed) + PosNoiseMeters));
            Assert.That(dp.Height, Is.EqualTo(EvtHeightHigh).Within(HalfStepUnit(EvtMaxAimHeight) + 1e-3f));

            SnapshotEventPayload dm = Decoded(mob, kind);
            Assert.AreEqual(EvtMobId, dm.Id);
            Assert.AreEqual(ProjectileIds.NoOwner, dm.PlayerIndex);
            AssertDecodedHeading(dm.Dir, EvtDirB, "mob rail dir");
            Assert.That(dm.HorizSpeed, Is.EqualTo(EvtHorizSpeedMob)
                .Within(HalfStepUnit(EvtGunnerSpeed) + 1e-3f));
            Assert.That(dm.VelZ, Is.EqualTo(EvtVelZ).Within(HalfStepPos(EvtGunnerSpeed) + PosNoiseMeters));
            Assert.That(dm.Height, Is.EqualTo(EvtHeightLow).Within(HalfStepUnit(EvtMaxAimHeight) + 1e-3f));
        }

        [Test]
        public void EventPayload_ProjectileEnded_ByteLayout_AndDecodedValues()
        {
            const SnapshotEventKind kind = SnapshotEventKind.ProjectileEnded;

            byte[] blocked = WritePayload(kind, b => SnapshotEvents.WriteProjectileEnded(
                new System.Span<byte>(b), EvtRoundId, ProjectileEndKind.Blocked, HitZone.None,
                EvtHeightLow, EvtCfg));
            Assert.AreEqual((byte)0x37, blocked[0], "byte 0: id low");
            Assert.AreEqual((byte)0x13, blocked[1], "byte 1: id high");
            Assert.AreEqual((byte)1, blocked[2], "byte 2: endKind Blocked");
            Assert.AreEqual((byte)0, blocked[3], "byte 3: zone None — a wall has no hit zone");
            Assert.AreEqual((byte)49, blocked[4], "byte 4: contact height 1.25/6.5 -> code 49");

            byte[] onMob = WritePayload(kind, b => SnapshotEvents.WriteProjectileEnded(
                new System.Span<byte>(b), EvtMobId, ProjectileEndKind.HitMob, HitZone.Head, 0f, EvtCfg));
            Assert.AreEqual((byte)0x39, onMob[0]);
            Assert.AreEqual((byte)0xC7, onMob[1]);
            Assert.AreEqual((byte)3, onMob[2], "byte 2: endKind HitMob");
            Assert.AreEqual((byte)HitZone.Head, onMob[3], "byte 3: the zone the shooter's hitmarker needs");
            Assert.AreEqual((byte)0, onMob[4], "byte 4: height is 0 for every ending but Blocked");

            SnapshotEventPayload db = Decoded(blocked, kind);
            Assert.AreEqual(EvtRoundId, db.Id);
            Assert.AreEqual(ProjectileEndKind.Blocked, db.EndKind);
            Assert.AreEqual(HitZone.None, db.Zone);
            Assert.That(db.Height, Is.EqualTo(EvtHeightLow).Within(HalfStepUnit(EvtMaxAimHeight) + 1e-3f));

            SnapshotEventPayload dh = Decoded(onMob, kind);
            Assert.AreEqual(EvtMobId, dh.Id);
            Assert.AreEqual(ProjectileEndKind.HitMob, dh.EndKind);
            Assert.AreEqual(HitZone.Head, dh.Zone);
        }

        [Test]
        public void EventPayload_MobAndWaveKinds_ByteLayout_AndDecodedValues()
        {
            byte[] spawned = WritePayload(SnapshotEventKind.MobSpawned,
                b => SnapshotEvents.WriteMobSpawned(new System.Span<byte>(b), EvtMobId, MobType.Gunner));
            Assert.AreEqual((byte)0x39, spawned[0]);
            Assert.AreEqual((byte)0xC7, spawned[1]);
            Assert.AreEqual((byte)MobType.Gunner, spawned[2], "byte 2: mob type");
            SnapshotEventPayload ds = Decoded(spawned, SnapshotEventKind.MobSpawned);
            Assert.AreEqual(EvtMobId, ds.Id);
            Assert.AreEqual(MobType.Gunner, ds.MobType);

            byte[] died = WritePayload(SnapshotEventKind.MobDied,
                b => SnapshotEvents.WriteMobDied(new System.Span<byte>(b), EvtRoundId, EvtSlot,
                    HitZone.Head, EvtCfg));
            Assert.AreEqual((byte)0x37, died[0]);
            Assert.AreEqual((byte)0x13, died[1]);
            Assert.AreEqual(EvtSlot, died[2], "byte 2: the killer's slot (ATTACKER convention)");
            Assert.AreEqual((byte)HitZone.Head, died[3]);
            SnapshotEventPayload dd = Decoded(died, SnapshotEventKind.MobDied);
            Assert.AreEqual(EvtRoundId, dd.Id);
            Assert.AreEqual(EvtSlot, dd.PlayerIndex);
            Assert.AreEqual(HitZone.Head, dd.Zone);

            // A kill nobody owns is legal here and must not be mistaken for a
            // hostile index (SimulationWorld.DamageMob's own NoOwner guard).
            byte[] unowned = WritePayload(SnapshotEventKind.MobDied,
                b => SnapshotEvents.WriteMobDied(new System.Span<byte>(b), EvtRoundId,
                    ProjectileIds.NoOwner, HitZone.Body, EvtCfg));
            Assert.AreEqual(ProjectileIds.NoOwner, Decoded(unowned, SnapshotEventKind.MobDied).PlayerIndex);

            byte[] started = WritePayload(SnapshotEventKind.WaveStarted,
                b => SnapshotEvents.WriteWaveStarted(new System.Span<byte>(b), EvtWaveStartedIndex));
            Assert.AreEqual((byte)0x3B, started[0], "byte 0: waveIndex low (40507 = 0x9E3B)");
            Assert.AreEqual((byte)0x9E, started[1], "byte 1: waveIndex high");
            Assert.AreEqual((ushort)EvtWaveStartedIndex,
                Decoded(started, SnapshotEventKind.WaveStarted).WaveIndex);

            byte[] cleared = WritePayload(SnapshotEventKind.WaveCleared,
                b => SnapshotEvents.WriteWaveCleared(new System.Span<byte>(b), WaveFixtureIndex));
            Assert.AreEqual((byte)0x29, cleared[0], "byte 0: waveIndex low (9001 = 0x2329)");
            Assert.AreEqual((byte)0x23, cleared[1], "byte 1: waveIndex high");
            Assert.AreEqual(WaveFixtureIndex, Decoded(cleared, SnapshotEventKind.WaveCleared).WaveIndex);
        }

        [Test]
        public void EventPayload_PlayerAndShotKinds_ByteLayout_AndDecodedValues()
        {
            byte[] damaged = WritePayload(SnapshotEventKind.PlayerDamaged,
                b => SnapshotEvents.WritePlayerDamaged(new System.Span<byte>(b), EvtSlot, HitZone.Legs,
                    EvtDamage, EvtDirA, EvtCfg));
            Assert.AreEqual(EvtSlot, damaged[0], "byte 0: victim slot");
            Assert.AreEqual((byte)HitZone.Legs, damaged[1], "byte 1: zone");
            Assert.AreEqual((byte)197, damaged[2], "byte 2: amount 91/118 -> code 197");
            Assert.AreEqual((byte)204, damaged[3], "byte 3: hitDir -> code 204");
            SnapshotEventPayload dd = Decoded(damaged, SnapshotEventKind.PlayerDamaged);
            Assert.AreEqual(EvtSlot, dd.PlayerIndex);
            Assert.AreEqual(HitZone.Legs, dd.Zone);
            Assert.That(dd.Amount, Is.EqualTo(EvtDamage).Within(HalfStepUnit(SnapHeroMaxHp) + HpNoise));
            AssertDecodedHeading(dd.Dir, EvtDirA, "hit direction");

            byte[] died = WritePayload(SnapshotEventKind.PlayerDied,
                b => SnapshotEvents.WritePlayerDied(new System.Span<byte>(b), EvtSlot, HitZone.Body, EvtCfg));
            Assert.AreEqual(EvtSlot, died[0]);
            Assert.AreEqual((byte)HitZone.Body, died[1]);
            Assert.AreEqual(HitZone.Body, Decoded(died, SnapshotEventKind.PlayerDied).Zone);

            byte[] dashed = WritePayload(SnapshotEventKind.PlayerDashed,
                b => SnapshotEvents.WritePlayerDashed(new System.Span<byte>(b), EvtSlot, EvtCfg));
            Assert.AreEqual(EvtSlot, dashed[0]);
            Assert.AreEqual(EvtSlot, Decoded(dashed, SnapshotEventKind.PlayerDashed).PlayerIndex);

            byte[] slid = WritePayload(SnapshotEventKind.PlayerSlideStarted,
                b => SnapshotEvents.WritePlayerSlideStarted(new System.Span<byte>(b), EvtSlot, EvtCfg));
            Assert.AreEqual(EvtSlot, slid[0]);
            Assert.AreEqual(EvtSlot, Decoded(slid, SnapshotEventKind.PlayerSlideStarted).PlayerIndex);

            byte[] ricochet = WritePayload(SnapshotEventKind.DashRicocheted,
                b => SnapshotEvents.WriteDashRicocheted(new System.Span<byte>(b), EvtSlot, EvtDirB, EvtCfg));
            Assert.AreEqual(EvtSlot, ricochet[0]);
            Assert.AreEqual((byte)140, ricochet[1], "byte 1: the wall normal -> code 140");
            AssertDecodedHeading(Decoded(ricochet, SnapshotEventKind.DashRicocheted).Dir, EvtDirB,
                "ricochet normal");

            // No slot byte at all: this kind reaches its owner and nobody else,
            // so who it is about is already known to the receiver.
            byte[] denied = WritePayload(SnapshotEventKind.StaminaDenied,
                b => SnapshotEvents.WriteStaminaDenied(new System.Span<byte>(b), EvtStaminaMissing, EvtCfg));
            Assert.AreEqual((byte)69, denied[0], "byte 0: missing stamina 57/210 -> code 69");
            Assert.That(Decoded(denied, SnapshotEventKind.StaminaDenied).Amount,
                Is.EqualTo(EvtStaminaMissing).Within(HalfStepUnit(EvtStaminaMax) + HpNoise));

            byte[] heardPlayer = WritePayload(SnapshotEventKind.ShotHeard,
                b => SnapshotEvents.WriteShotHeard(new System.Span<byte>(b), EvtSlot, EvtCfg));
            Assert.AreEqual(EvtSlot, heardPlayer[0]);
            byte[] heardMob = WritePayload(SnapshotEventKind.ShotHeard,
                b => SnapshotEvents.WriteShotHeard(new System.Span<byte>(b), ProjectileIds.NoOwner, EvtCfg));
            Assert.AreEqual(ProjectileIds.NoOwner, heardMob[0], "255 marks a shot no player fired");
            Assert.AreEqual(ProjectileIds.NoOwner, Decoded(heardMob, SnapshotEventKind.ShotHeard).PlayerIndex);
        }

        // ---- Т29. The raid's own five kinds, through their own codec ----

        /// Every Stage 3 kind, written and read back. This is also the ONLY
        /// witness `SnapshotEvents.IsKnown` has: that bound is the one home in
        /// the file that does NOT throw on a kind it has no entry for — it
        /// range-checks against the enum's top member — so a kind appended
        /// without moving it decodes as `MalformedContent` here and nowhere
        /// else in the suite.
        [Test]
        public void EveryStage3Kind_RoundTripsItsOwnPayload()
        {
            // The two zero-length kinds. Their whole message is the kind and
            // the tick the record header carries; `Decoded` still has to
            // ACCEPT them, which is what a forgotten IsKnown bound would
            // break.
            byte[] activated = WritePayload(SnapshotEventKind.DirectorActivated,
                b => SnapshotEvents.WriteDirectorActivated(new System.Span<byte>(b)));
            // `WritePayload` has already asserted the two halves that matter:
            // the write REPORTED zero, and not one byte of the sentinel-filled
            // buffer was touched. Restating byte 0 here names the claim.
            Assert.AreEqual(Sentinel, activated[0], "DirectorActivated writes no byte at all");
            Assert.AreEqual(SnapshotEventKind.DirectorActivated,
                Decoded(activated, SnapshotEventKind.DirectorActivated).Kind);

            byte[] died = WritePayload(SnapshotEventKind.DirectorDied,
                b => SnapshotEvents.WriteDirectorDied(new System.Span<byte>(b)));
            Assert.AreEqual(Sentinel, died[0], "DirectorDied writes none either");
            Assert.AreEqual(SnapshotEventKind.DirectorDied,
                Decoded(died, SnapshotEventKind.DirectorDied).Kind);

            // The slot that walked out. EvtSlot, not 0 — a writer that stored
            // a constant would pass on slot zero (lesson 227).
            byte[] extracted = WritePayload(SnapshotEventKind.PlayerExtracted,
                b => SnapshotEvents.WritePlayerExtracted(new System.Span<byte>(b), EvtSlot, EvtCfg));
            Assert.AreEqual(EvtSlot, extracted[0]);
            Assert.AreEqual(EvtSlot, Decoded(extracted, SnapshotEventKind.PlayerExtracted).PlayerIndex);

            // The two id-carrying kinds. The id is deliberately ABOVE 255, so
            // a writer that stored one byte instead of a u16 loses the high
            // half and the round trip says so.
            const int WideId = 0x1234;
            byte[] taken = WritePayload(SnapshotEventKind.PickupTaken,
                b => SnapshotEvents.WritePickupTaken(new System.Span<byte>(b), WideId));
            Assert.AreEqual(WideId, Decoded(taken, SnapshotEventKind.PickupTaken).Id);

            byte[] emptied = WritePayload(SnapshotEventKind.ContainerEmptied,
                b => SnapshotEvents.WriteContainerEmptied(new System.Span<byte>(b), WideId));
            Assert.AreEqual(WideId, Decoded(emptied, SnapshotEventKind.ContainerEmptied).Id);
        }

        /// The write side throws on a CALLER bug (Р82's other half), and a
        /// seat this match does not have is exactly that — the same guard
        /// every other slot-carrying writer in the catalog applies.
        [Test]
        public void PlayerExtracted_RefusesASeatThisMatchDoesNotHave()
        {
            var buffer = new byte[SnapshotEvents.MaxPayloadBytes];
            Assert.Throws<System.ArgumentException>(() => SnapshotEvents.WritePlayerExtracted(
                new System.Span<byte>(buffer), (byte)EvtCfg.Arena.MaxPlayers, EvtCfg));
            Assert.DoesNotThrow(() => SnapshotEvents.WritePlayerExtracted(
                new System.Span<byte>(buffer), (byte)(EvtCfg.Arena.MaxPlayers - 1), EvtCfg),
                "the last real seat is legal — the bound is exclusive, not off by one");
        }

        // ---- T28.8-11. Hostile payloads: refused, never thrown ----

        [Test]
        public void EventPayload_WrongLength_IsRefusedWithoutThrowing()
        {
            var pool = new byte[SnapshotEvents.MaxPayloadBytes + 2];
            foreach (SnapshotEventKind kind in System.Enum.GetValues(typeof(SnapshotEventKind)))
            {
                if (kind == SnapshotEventKind.None) continue;
                int size = SnapshotEvents.PayloadBytesFor(kind);

                Assert.IsFalse(SnapshotEvents.TryReadPayload(kind,
                        new System.ReadOnlySpan<byte>(pool, 0, size + 1), EvtCfg,
                        out SnapshotEventPayload longer, out SnapshotBlockError le),
                    $"{kind}: a payload one byte too LONG must be refused — accepting it would leave the "
                    + "extra byte to be read as the next record's kind");
                Assert.AreEqual(SnapshotBlockError.MalformedLength, le);
                Assert.AreEqual(SnapshotEventKind.None, longer.Kind, "a refusal must leave `value` default");

                if (size > 0)
                {
                    Assert.IsFalse(SnapshotEvents.TryReadPayload(kind,
                            new System.ReadOnlySpan<byte>(pool, 0, size - 1), EvtCfg,
                            out _, out SnapshotBlockError se),
                        $"{kind}: a payload one byte too SHORT must be refused");
                    Assert.AreEqual(SnapshotBlockError.MalformedLength, se);
                }
            }
        }

        [Test]
        public void EventPayload_SlotIndexAtOrAboveMaxPlayers_IsRefused_AndBelowIsAccepted()
        {
            // The boundary is read from `cfg`, never hardcoded — the fixture's
            // MaxPlayers is deliberately 11 rather than the shipped 3, so a
            // `>= 3` hardcode cannot pass both halves (the Task 27 precedent).
            byte legal = (byte)(SnapMaxPlayers - 1);       // 10
            byte hostile = (byte)SnapMaxPlayers;           // 11

            var payload = new byte[SnapshotEvents.MaxPayloadBytes];
            payload[1] = (byte)HitZone.Body;

            payload[0] = legal;
            Assert.IsTrue(SnapshotEvents.TryReadPayload(SnapshotEventKind.PlayerDied,
                    new System.ReadOnlySpan<byte>(payload, 0, 2), EvtCfg, out _, out SnapshotBlockError ok));
            Assert.AreEqual(SnapshotBlockError.None, ok, "the last real slot must be accepted");

            payload[0] = hostile;
            Assert.IsFalse(SnapshotEvents.TryReadPayload(SnapshotEventKind.PlayerDied,
                    new System.ReadOnlySpan<byte>(payload, 0, 2), EvtCfg, out _, out SnapshotBlockError bad));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, bad,
                "a slot this match does not have must be refused — Tasks 32/45 index per-slot pools by it");

            payload[0] = ProjectileIds.NoOwner;
            Assert.IsFalse(SnapshotEvents.TryReadPayload(SnapshotEventKind.PlayerDied,
                    new System.ReadOnlySpan<byte>(payload, 0, 2), EvtCfg, out _, out SnapshotBlockError none));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, none,
                "a victim must be a real player: NoOwner is legal for a SHOOTER, never for a victim");

            // ... while it IS legal on the kinds whose slot can be "no player".
            var heard = new byte[] { ProjectileIds.NoOwner };
            Assert.IsTrue(SnapshotEvents.TryReadPayload(SnapshotEventKind.ShotHeard, heard, EvtCfg,
                out _, out SnapshotBlockError heardOk));
            Assert.AreEqual(SnapshotBlockError.None, heardOk, "a mob's shot names no player, by design");
            heard[0] = hostile;
            Assert.IsFalse(SnapshotEvents.TryReadPayload(SnapshotEventKind.ShotHeard, heard, EvtCfg,
                out _, out SnapshotBlockError heardBad));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, heardBad,
                "but an index that is neither a slot nor the sentinel is still hostile");
        }

        [Test]
        public void EventPayload_EnumeratorsOutsideTheirDomain_AreRefusedWithoutThrowing()
        {
            // Shape-legal, content-illegal: the one hostile class a length
            // check can never catch, and the one that reaches furthest
            // downstream (Р82 is "refuse bad input", not merely "never throw").
            var ended = new byte[] { 0x37, 0x13, (byte)ProjectileEndKind.Blocked, (byte)HitZone.Body, 0 };
            Assert.IsTrue(SnapshotEvents.TryReadPayload(SnapshotEventKind.ProjectileEnded, ended, EvtCfg,
                out _, out SnapshotBlockError ok), "witness: the same shape with legal values is accepted");
            Assert.AreEqual(SnapshotBlockError.None, ok);

            ended[2] = (byte)ProjectileEndKind.None;
            Assert.IsFalse(SnapshotEvents.TryReadPayload(SnapshotEventKind.ProjectileEnded, ended, EvtCfg,
                out _, out SnapshotBlockError zeroEnd));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, zeroEnd,
                "endKind 0 is never written, so receiving it means the bytes are not ours");

            ended[2] = (byte)(SnapshotEvents.MaxProjectileEndKindValue + 1);
            Assert.IsFalse(SnapshotEvents.TryReadPayload(SnapshotEventKind.ProjectileEnded, ended, EvtCfg,
                out _, out SnapshotBlockError bigEnd));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, bigEnd);

            ended[2] = (byte)ProjectileEndKind.HitMob;
            ended[3] = (byte)(SnapshotEvents.MaxHitZoneValue + 1);
            Assert.IsFalse(SnapshotEvents.TryReadPayload(SnapshotEventKind.ProjectileEnded, ended, EvtCfg,
                out _, out SnapshotBlockError badZone));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, badZone,
                "(HitZone)200 casts perfectly happily in C# and would index a feedback table on the client");

            var spawned = new byte[] { 0x39, 0xC7, (byte)(SnapshotBlocks.MaxMobTypeValue + 1) };
            Assert.IsFalse(SnapshotEvents.TryReadPayload(SnapshotEventKind.MobSpawned, spawned, EvtCfg,
                out _, out SnapshotBlockError badType));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, badType,
                "the mob-type bound is reused from SnapshotBlocks, not restated");
        }

        [Test]
        public void EventPayload_UnknownKind_IsRefusedByTheCatalog_ButSkippedByTheBlockWalker()
        {
            // Two different levels, two different answers, and both are right.
            // The BLOCK walker (Task 27) steps over an unknown record by its
            // declared length and reports no error — that is what lets a Stage
            // 3 event kind ride an old client's wire (Р29). The CATALOG, asked
            // to decode one anyway, can only say no.
            const byte unknownKind = 0x7B;   // 123 — not in the catalog
            Assert.Greater(unknownKind, (byte)SnapshotEventKind.WaveCleared,
                "fixture premise: this kind must genuinely be outside the catalog");

            var payload = new byte[] { 0x11, 0x22, 0x33 };
            Assert.IsFalse(SnapshotEvents.TryReadPayload((SnapshotEventKind)unknownKind, payload, EvtCfg,
                out SnapshotEventPayload value, out SnapshotBlockError error));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
            Assert.AreEqual(SnapshotEventKind.None, value.Kind);

            // The same record inside a real block: walked over, counted as a
            // record, and the block itself parses cleanly.
            var known = new SnapshotBlocks.EventRecord
            {
                Kind = unknownKind, Seq = 31337, TickDelta = 3, Pos = new float2(17f, -49f),
                PayloadOffset = 0, PayloadLength = (byte)payload.Length,
            };
            var buffer = new byte[SnapshotWriter.HeaderBytes
                                  + SnapshotWriter.EventsBlockBytes(1, payload.Length)];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteEventsBlock(new[] { known }, payload, EvtCfg);

            var reader = new SnapshotReader(buffer);
            Assert.IsTrue(reader.TryReadHeader(out _, out _, out _));
            Assert.IsTrue(reader.TryReadBlock(new[] { (byte)SnapshotBlockKind.Events },
                out _, out System.ReadOnlySpan<byte> body));
            var dest = new SnapshotBlocks.EventRecord[2];
            Assert.IsTrue(SnapshotBlocks.TryReadEventsBlock(body, EvtCfg, dest, out int count,
                out SnapshotBlockError blockError),
                "an unknown event kind must NOT make the block itself unreadable");
            Assert.AreEqual(SnapshotBlockError.None, blockError);
            Assert.AreEqual(1, count);
            Assert.AreEqual(unknownKind, dest[0].Kind);
        }

        [Test]
        public void EventPayload_WriteSide_ThrowsOnEveryCallerDomainError()
        {
            // The mirror of the read side, and the asymmetry Tasks 26/27
            // established: a value outside its domain handed to a WRITER is a
            // bug in the assembler, not hostile traffic.
            var buffer = new byte[SnapshotEvents.MaxPayloadBytes];

            Assert.Throws<System.ArgumentException>(() => SnapshotEvents.WritePlayerDied(
                new System.Span<byte>(buffer), (byte)SnapMaxPlayers, HitZone.Body, EvtCfg),
                "a victim slot this match does not have");
            Assert.Throws<System.ArgumentException>(() => SnapshotEvents.WritePlayerDied(
                new System.Span<byte>(buffer), ProjectileIds.NoOwner, HitZone.Body, EvtCfg),
                "and the shooter sentinel is not a victim either");
            Assert.Throws<System.ArgumentException>(() => SnapshotEvents.WritePlayerDied(
                new System.Span<byte>(buffer), EvtSlot, (HitZone)(SnapshotEvents.MaxHitZoneValue + 1), EvtCfg),
                "a zone outside the enum");
            Assert.Throws<System.ArgumentException>(() => SnapshotEvents.WriteProjectileEnded(
                new System.Span<byte>(buffer), EvtRoundId, ProjectileEndKind.None, HitZone.None, 0f, EvtCfg),
                "the never-written end kind");
            Assert.Throws<System.ArgumentException>(() => SnapshotEvents.WriteMobSpawned(
                new System.Span<byte>(buffer), EvtMobId, (MobType)(SnapshotBlocks.MaxMobTypeValue + 1)),
                "a mob type outside the enum");
            Assert.Throws<System.ArgumentException>(() => SnapshotEvents.WriteShotHeard(
                new System.Span<byte>(buffer), (byte)SnapMaxPlayers, EvtCfg),
                "a shooter slot this match does not have");

            // Too small a destination is a caller bug too — the assembler owns
            // the pool, so a short slot means its own arithmetic is wrong.
            Assert.Throws<System.ArgumentException>(() => SnapshotEvents.WriteProjectileSpawned(
                    new System.Span<byte>(buffer, 0, SnapshotEvents.MaxPayloadBytes - 1),
                    EvtRoundId, EvtSlot, EvtDirA, EvtHorizSpeedPlayer, EvtVelZ, EvtHeightHigh, EvtCfg),
                "a payload slot one byte short of the kind's own size");

            // Witness: the legal call the four above are variations of really
            // does succeed, so these are refusals rather than a broken writer.
            Assert.DoesNotThrow(() => SnapshotEvents.WritePlayerDied(
                new System.Span<byte>(buffer), EvtSlot, HitZone.Body, EvtCfg));
        }

        // ---- T28.12. The header's truncation bit ----

        [Test]
        public void SnapshotHeaderFlags_TruncatedIsBitZero_AndNoOtherBitIsAssigned()
        {
            Assert.AreEqual((byte)0x01, SnapshotHeaderFlags.Truncated,
                "bit 0 — the first tenant of the byte Tasks 26/27 deliberately left reserved");
            Assert.AreEqual(0, SnapshotHeaderFlags.Truncated & 0xFE,
                "and it is a SINGLE bit: bits 1-7 stay free for Tasks 29/32 onwards");

            // Strengthening (recorded in the report): the two assertions above
            // are about a `const` and passed on a stubbed assembler. This one
            // runs the bit through the real frame codec, so "the writer puts
            // flags in header byte 7 and the reader hands the same byte back"
            // is pinned for the bit that now MEANS something, not only for the
            // reserved byte Task 26 pinned.
            var buffer = new byte[SnapshotWriter.HeaderBytes];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, SnapshotHeaderFlags.Truncated);
            Assert.AreEqual(SnapshotHeaderFlags.Truncated, buffer[7],
                "the truncation bit lands in header byte 7, where SnapshotWriter's layout puts flags");
            var reader = new SnapshotReader(buffer);
            Assert.IsTrue(reader.TryReadHeader(out _, out _, out byte flags));
            Assert.AreNotEqual(0, flags & SnapshotHeaderFlags.Truncated,
                "and comes back set — this is the bit Tasks 32/37 branch on");
        }

        // ---- T28.13. The worst-case frame, recomputed ----

        [Test]
        public void WorstCaseFrame_RecomputedFromTheCalculators_WithTheRealCatalog()
        {
            // Урок 103: the number is RECOMPUTED here, not quoted. Every
            // earlier figure — spec §3.8's 1043, Task 26's 1052, Task 27's 1116
            // — predates the event catalog it depends on, and Task 27's comment
            // says so in as many words: 4 B of payload was an assumption. The
            // real maximum is ProjectileSpawned's 8.
            SimConfig shipped = TestConfigs.Default();
            var net = ScriptableObject.CreateInstance<NetConfig>();   // the shipped C# defaults

            int others = shipped.Arena.MaxPlayers - 1;
            int events = net.SnapshotEventBudget;
            int worstEventPayload = SnapshotEvents.MaxPayloadBytes;

            // Strengthening (recorded in the report): this test passed on a
            // stubbed catalog because it only read the `const`
            // MaxPayloadBytes, which no stub touched. The worst case is only
            // meaningful if the payload table it summarises is REAL — i.e. if
            // ProjectileSpawned actually is the widest kind and other kinds
            // actually are narrower. A catalog that answered "8" for
            // everything would make the budget below arithmetically fine and
            // completely wrong.
            Assert.AreEqual(worstEventPayload,
                SnapshotEvents.PayloadBytesFor(SnapshotEventKind.ProjectileSpawned),
                "the widest payload must be a real kind's, not a round number beside the table");
            Assert.Less(SnapshotEvents.PayloadBytesFor(SnapshotEventKind.ShotHeard), worstEventPayload,
                "and the table must actually vary — a flat catalog would over-reserve every frame");

            int header = SnapshotWriter.HeaderBytes;
            int players = SnapshotWriter.PlayersBlockBytes(others);
            int liveness = SnapshotWriter.LivenessBlockBytes();
            int mobs = SnapshotWriter.MobsBlockBytes(shipped.Arena.MaxMobs);
            int wave = SnapshotWriter.WaveBlockBytes();
            int eventsBytes = SnapshotWriter.EventsBlockBytes(events, events * worstEventPayload);

            // The arithmetic, spelled out rather than trusted to the
            // calculators alone (they are what this is checking).
            Assert.AreEqual(8, header, "8");
            Assert.AreEqual(3 + 2 * 8, players, "3 + 2 records * 8 B = 19");
            Assert.AreEqual(3 + 2, liveness,
                "3 + TWO mask bytes = 5 — Stage 3 Task 25 (Р257) added the extracted mask beside the "
                + "alive one, and the worst case grew with it");
            // Stage 3 Task 12: 96 was a literal of the Stage 2 cap. The whole
            // point of this test is that the calculators agree with the
            // arithmetic, so the arithmetic reads the same cap the calculator
            // was handed — at MaxMobs 288 that is 3 + 288 * 9 = 2595 B.
            Assert.AreEqual(3 + shipped.Arena.MaxMobs * 9, mobs,
                $"3 + {shipped.Arena.MaxMobs} records * 9 B = {3 + shipped.Arena.MaxMobs * 9}");
            Assert.AreEqual(3 + 4, wave, "3 + 4 = 7");
            Assert.AreEqual(3 + 16 * 9 + 16 * 8, eventsBytes, "3 + 16 * (9 header + 8 payload) = 275");

            int total = header + players + liveness + mobs + wave + eventsBytes;
            // Stage 3 Task 12: 1180 was that same Stage 2 cap carried into the
            // sum (8 + 19 + 4 + 867 + 7 + 275). At MaxMobs 288 the live
            // worst-case frame is 8 + 19 + 4 + 2595 + 7 + 275 = 2908 B — and
            // spec Р217 named this consequence before the numbers landed:
            // "блок мобов худшего случая — 288 x 9 = 2592 Б против
            // SnapshotMaxBytes 1000", i.e. entity truncation stops being
            // unreachable and becomes the ordinary shape of a saturated frame.
            Assert.AreEqual(8 + 19 + 5 + (3 + shipped.Arena.MaxMobs * 9) + 7 + 275, total,
                $"8 + 19 + 5 + {3 + shipped.Arena.MaxMobs * 9} + 7 + 275 — the live worst-case frame at "
                + "the shipped caps. Task 27's 1116 assumed 4 B of event payload; the real catalog's "
                + "widest is 8. The liveness term is 5 rather than 4 since Task 25 (Р257). This sum "
                + "is the FIVE Stage 2 blocks and stays that way on purpose — the five Stage 3 ones "
                + "are the sibling below (WorstCaseFrame_RecomputedWithNewBlocks), so the two halves "
                + "of the history stay separately checkable");
            Assert.Greater(total, net.SnapshotMaxBytes,
                "and it still exceeds our own cap, which is why the budget exists at all");

            // The fixed part always fits, by an enormous margin — asserted, not
            // asserted in prose (task-28-brief §2.8 item 2).
            int fixedPart = header + players + liveness + wave;
            Assert.AreEqual(39, fixedPart, "8 + 19 + 5 + 7 — the liveness term grew in Task 25");
            Assert.Less(fixedPart, net.SnapshotMaxBytes);

            // THE STAGE 2 FINDING, TURNED OVER BY STAGE 3 TASK 12 AND RE-PINNED
            // RATHER THAN DELETED — the arithmetic is the same, the verdict is
            // the opposite, and both halves are worth keeping on the record.
            //
            // Stage 2 pinned here that spec §3.8's argument for the truncation
            // branch ("the worst case exceeds SnapshotMaxBytes, therefore
            // truncation is reachable") did not hold at the shipped numbers:
            // the frame is not built worst-case first — mobs are budgeted
            // BEFORE events (task-28-brief §2.8 items 3-4) — so with MaxMobs 96
            // every mob fitted (864 B of a 956 B record budget) and what the
            // cap actually squeezed out was EVENTS, never entities.
            //
            // At MaxMobs 288 the same three numbers say the opposite, and spec
            // Р217 said in advance that they would: the record budget is
            // 1000 - 45 = 955 B (44 before Task 25 widened the liveness
            // block), a full crowd now needs 288 * 9 = 2592 B, so 106 mobs
            // ride and 182 are dropped. Entity truncation IS reachable at the
            // shipped defaults now.
            //
            // The precedence the Stage 2 finding named does not soften that —
            // it sharpens it. Mobs overrun the whole record budget, so a
            // saturated frame has nothing left for events AT ALL, which is a
            // STRONGER statement than the "cannot carry a full budget" this
            // block used to close on, and the assertion below says the stronger
            // thing rather than the surviving weaker one.
            //
            // Cross-check, deliberately stated: the sibling
            // WorstCase_ByCaps_TriggersTruncation decodes a real frame at this
            // same shipped cap and counts 106 mobs. That is this block's
            // 956 / 9 computed from the other end — from an assembled frame
            // rather than from the calculators. If the two ever disagree, one
            // of them is lying about the cap and neither may be believed.
            int fixedWithEmptyBlocks = fixedPart
                + SnapshotWriter.MobsBlockBytes(0) + SnapshotWriter.EventsBlockBytes(0, 0);
            Assert.AreEqual(45, fixedWithEmptyBlocks, "39 + 3 + 3 — all five blocks always ride");
            int roomForRecords = net.SnapshotMaxBytes - fixedWithEmptyBlocks;
            int roomMobsNeed = shipped.Arena.MaxMobs * SnapshotBlocks.MobRecordBytes;
            Assert.Less(roomForRecords, roomMobsNeed,
                $"at the shipped defaults a saturated frame no longer fits — {roomForRecords} B of "
                + $"record room against {shipped.Arena.MaxMobs} * {SnapshotBlocks.MobRecordBytes} = "
                + $"{roomMobsNeed} B of mobs (spec Р217) — so entity truncation fires there, which is "
                + "the Stage 2 finding above turned over rather than restated");
            int mobsThatFit = roomForRecords / SnapshotBlocks.MobRecordBytes;
            Assert.Less(mobsThatFit, shipped.Arena.MaxMobs,
                "the cut is real: fewer mobs ride than the world holds");
            Assert.Greater(mobsThatFit, 0,
                "and it is a CUT, not a collapse — the frame still carries mobs, which is what makes "
                + "the drop order (nearest survive) a question worth asking at all");
            int leftoverForEvents = roomForRecords - roomMobsNeed;
            Assert.Less(leftoverForEvents, 0,
                $"and the overrun ({leftoverForEvents} B) leaves NOTHING for events: entities outrank "
                + "them, so a saturated frame carries no event at all — not merely an incomplete "
                + "budget. This is why SnapshotAssemblerTests' own allocation fixtures have to buy a "
                + "roomier cap to keep measuring a frame with both blocks in it");
        }

        // ---- Т27. The same arithmetic, with the five Stage 3 blocks ----

        [Test]
        public void WorstCaseFrame_RecomputedWithNewBlocks()
        {
            // Урок 103 again, one stage later: the sibling above was written
            // when nothing wrote the Task 25 blocks into a frame, and its own
            // text named Т27 as the task that would recompute it with them.
            // This is that recomputation — and it is a SECOND test rather than
            // an edit of the first, because the two answer different
            // questions: what the five Stage 2 blocks cost, and what the
            // frame costs now that ten ride.
            SimConfig shipped = TestConfigs.Default();
            var net = ScriptableObject.CreateInstance<NetConfig>();   // the shipped C# defaults

            int live = shipped.Arena.MaxPlayers - 1;                  // a living recipient
            int dead = shipped.Arena.MaxPlayers;                      // …and one who is not

            // The fixed part, term by term, at an EMPTY backpack and then at
            // the fullest one the hero can carry — the two ends of the only
            // term that varies per frame.
            int FixedPart(int players, int items) =>
                SnapshotWriter.HeaderBytes
                + SnapshotWriter.SelfBlockBytes(items)
                + SnapshotWriter.MatchBlockBytes()
                + SnapshotWriter.PlayersBlockBytes(players)
                + SnapshotWriter.LivenessBlockBytes()
                + SnapshotWriter.WaveBlockBytes()
                + SnapshotWriter.ContainerSlotsBlockBytes(0, 0)
                + SnapshotWriter.MobsBlockBytes(0)
                + SnapshotWriter.ContainersBlockBytes(0)
                + SnapshotWriter.PickupsBlockBytes(0)
                + SnapshotWriter.EventsBlockBytes(0, 0);

            Assert.AreEqual(3 + 4, SnapshotWriter.MatchBlockBytes(), "3 + phase, seconds, flags = 7");
            Assert.AreEqual(3 + 2 + shipped.Hero.MaxInventoryItems,
                SnapshotWriter.SelfBlockBytes(shipped.Hero.MaxInventoryItems),
                "3 + slotPoints + count + one byte per item id");
            Assert.AreEqual(3, SnapshotWriter.PickupsBlockBytes(0), "an empty block is its own header");

            int widest = FixedPart(dead, shipped.Hero.MaxInventoryItems);
            Assert.AreEqual(8 + 21 + 7 + 27 + 5 + 7 + 3 + 3 + 3 + 3 + 3, widest,
                "the widest fixed part at the shipped caps — this is the number the assembler's "
                + "constructor refuses a SnapshotMaxBytes below (spec Р279), and it is 90 B against "
                + "the 53 of the five-block frame");
            Assert.AreEqual(widest, SnapshotAssembler.FixedFrameBytes(dead,
                shipped.Hero.MaxInventoryItems),
                "and the assembler's one home answers the same number");
            Assert.Less(widest, net.SnapshotMaxBytes,
                "it still fits the shipped cap by a wide margin, which is what makes the throw a "
                + "start-up guard rather than a limit anyone meets");

            // What the growth costs the entity budget, which is the point of
            // the whole exercise: 37 B off the record room of a living
            // recipient, and four mobs off what a saturated frame carries.
            int roomBefore = net.SnapshotMaxBytes
                             - (SnapshotWriter.HeaderBytes
                                + SnapshotWriter.PlayersBlockBytes(live)
                                + SnapshotWriter.LivenessBlockBytes()
                                + SnapshotWriter.WaveBlockBytes()
                                + SnapshotWriter.MobsBlockBytes(0)
                                + SnapshotWriter.EventsBlockBytes(0, 0));
            int roomAfter = net.SnapshotMaxBytes - FixedPart(live, shipped.Hero.MaxInventoryItems);
            Assert.AreEqual(37, roomBefore - roomAfter,
                "Match 7 + Self 21 at the full backpack + three empty headers 9 = 37 B, the price "
                + "of the five new blocks on a frame that carries nothing in three of them");
            Assert.AreEqual(4,
                roomBefore / SnapshotBlocks.MobRecordBytes - roomAfter / SnapshotBlocks.MobRecordBytes,
                "which is four mob records — the saturated frame carries that many fewer, and Р243's "
                + "own ordering decision is what makes those four mobs rather than four crates");
            Assert.Greater(roomAfter / SnapshotBlocks.MobRecordBytes, 0,
                "and a frame still carries a crowd, so the cut is a budget rather than a collapse");
        }

        // ---- T28.14. Truncation, by the caps (plan) ----

        [Test]
        public void WorstCase_ByCaps_TriggersTruncation()
        {
            // MaxPlayers - 1 other players, MaxMobs mobs, all in sight, against
            // a byte cap that cannot hold them. What is asserted is the
            // MECHANISM: the far ones go first, deterministically, the header
            // says so, the counter agrees, and the writer never throws.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: cfg.Arena.MaxPlayers);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            for (int i = 1; i < cfg.Arena.MaxPlayers; i++)
                TestWorlds.RelocatePlayerForTest(w, i, new float2(2f * i, 1f));
            TestWorlds.SpawnMobsToCap(w);
            Assert.AreEqual(cfg.Arena.MaxMobs, w.MobCount, "test setup: every mob slot is filled");

            // A full budget of the widest events, so the frame is genuinely the
            // worst case and not merely a crowd of mobs.
            for (int i = 0; i < 16; i++)
                w.Emit(SimEventKind.MobSpawned, w.Mobs[i].Pos, w.Mobs[i].Id, w.Mobs[i].Type, 0f);

            const int cap = 500;
            var net = ScriptableObject.CreateInstance<NetConfig>();
            net.SnapshotMaxBytes = cap;
            net.SnapshotEventBudget = 16;

            var asm = new SnapshotAssembler(cfg, net, connectionCount: 1);
            asm.BeginTick(w);
            int bytes = asm.BuildFor(0, 0, 0, Epoch);
            AssembledFrame f = AssembledFrame.Decode(asm.BufferFor(0), bytes, cfg);

            Assert.LessOrEqual(bytes, cap, "the whole point: the frame stays inside the cap");
            Assert.IsTrue(f.Truncated, "and says so in header bit 0, so the receiver can tell 'cut for room' "
                + "from 'left my view' (Tasks 32/37)");

            // Stage 3 Task 27 widened the fixed part from five blocks to ten
            // (spec §3.12): Self — empty here, this fixture's collectors carry
            // nothing — Match, and the three ground-entity blocks, all of which
            // ride whether or not they have content. Spelled out from the
            // calculators rather than taken from SnapshotAssembler.
            // FixedFrameBytes on purpose: a guard that read the production
            // home would agree with a wrong home too (lesson 324).
            int fixedWithEmptyBlocks = SnapshotWriter.HeaderBytes
                + SnapshotWriter.SelfBlockBytes(0)
                + SnapshotWriter.MatchBlockBytes()
                + SnapshotWriter.PlayersBlockBytes(cfg.Arena.MaxPlayers - 1)
                + SnapshotWriter.LivenessBlockBytes()
                + SnapshotWriter.WaveBlockBytes()
                + SnapshotWriter.ContainerSlotsBlockBytes(0, 0)
                + SnapshotWriter.MobsBlockBytes(0)
                + SnapshotWriter.ContainersBlockBytes(0)
                + SnapshotWriter.PickupsBlockBytes(0)
                + SnapshotWriter.EventsBlockBytes(0, 0);
            int expectedMobs = (cap - fixedWithEmptyBlocks) / SnapshotBlocks.MobRecordBytes;
            Assert.AreEqual(expectedMobs, f.MobCount, "exactly as many mobs as the remaining bytes hold");
            Assert.Less(f.MobCount, cfg.Arena.MaxMobs, "test setup: this must really be a truncation");
            Assert.AreEqual(cfg.Arena.MaxMobs - expectedMobs, asm.StatsFor(0).DroppedEntities,
                "DroppedEntities must agree with what actually went missing");

            // Deterministic order: the survivors are exactly the nearest ones,
            // with the smaller id winning a tie. Computed here from the world,
            // independently of the assembler.
            var ranked = new List<(float dist, int id)>();
            for (int i = 0; i < w.MobCount; i++)
                ranked.Add((math.distance(w.Mobs[i].Pos, float2.zero), w.Mobs[i].Id));
            ranked.Sort((a, b) => a.dist != b.dist ? a.dist.CompareTo(b.dist) : a.id.CompareTo(b.id));

            for (int i = 0; i < expectedMobs; i++)
                Assert.IsTrue(f.ContainsMob(ranked[i].id),
                    $"the {i}-th nearest mob (id {ranked[i].id}) must have survived — far ones go first");
            for (int i = expectedMobs; i < ranked.Count; i++)
                Assert.IsFalse(f.ContainsMob(ranked[i].id),
                    $"the {i}-th nearest mob (id {ranked[i].id}) is past the cut and must be gone");

            // This arm exists to prove the cut above came from the CAP and not
            // from something else in the assembler. Until Stage 3 Task 12 it
            // said so by showing the shipped 1000 B held everything — "a
            // bigger arena" was named as the hypothetical that would change
            // that, and Т12 is that arena: at MaxMobs 288 a full crowd is
            // 3 + 288 * 9 = 2595 B, so the shipped cap truncates too, exactly
            // as spec Р217 predicted. The contrast is therefore restated
            // against a cap that really is roomy, and the shipped cap's own
            // new behavior is asserted rather than dropped — a claim that
            // became false is replaced by the true one it turned into, not by
            // a weaker one.
            var shippedCap = ScriptableObject.CreateInstance<NetConfig>();
            var asmShipped = new SnapshotAssembler(cfg, shippedCap, connectionCount: 1);
            asmShipped.BeginTick(w);
            AssembledFrame shippedFrame = AssembledFrame.Decode(asmShipped.BufferFor(0),
                asmShipped.BuildFor(0, 0, 0, Epoch), cfg);
            Assert.Less(shippedFrame.MobCount, cfg.Arena.MaxMobs,
                "spec Р217: at MaxMobs 288 a saturated frame no longer fits the shipped "
                + "SnapshotMaxBytes, so entity truncation is now the ordinary shape of one");
            Assert.IsTrue(shippedFrame.Truncated);
            Assert.Greater(asmShipped.StatsFor(0).DroppedEntities, 0);

            var roomy = ScriptableObject.CreateInstance<NetConfig>();
            // Provably enough: the shipped 1000 B already held the fixed part
            // plus 106 records, so the same cap plus one full record per mob
            // cannot be short.
            roomy.SnapshotMaxBytes = shippedCap.SnapshotMaxBytes
                + SnapshotBlocks.MobRecordBytes * cfg.Arena.MaxMobs;
            var asm2 = new SnapshotAssembler(cfg, roomy, connectionCount: 1);
            asm2.BeginTick(w);
            AssembledFrame full = AssembledFrame.Decode(asm2.BufferFor(0),
                asm2.BuildFor(0, 0, 0, Epoch), cfg);
            Assert.AreEqual(cfg.Arena.MaxMobs, full.MobCount,
                "given room for every record, every mob still rides — the cut above is the cap's "
                + "doing and nothing else's");
            Assert.IsFalse(full.Truncated);
            Assert.AreEqual(0, asm2.StatsFor(0).DroppedEntities);
        }

        // ---- T28.15. The event budget prefers deaths (plan) ----

        [Test]
        public void EventBudget_PrioritizesDeaths()
        {
            // Р61. With room for three of five, the three that ride are the
            // three most important — and the two that do not are DEFERRED, not
            // dropped, which is the difference the counters have to show.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, 7f));
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(4f, 4f));
            // The spawn seam emits a MobSpawned of its own — cleared, so the
            // five events below are exactly the five this fixture states.
            w.ClearEvents();

            // Emitted WORST-FIRST on purpose: an implementation that simply
            // took the first three in buffer order would deliver the cosmetics.
            w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            w.Emit(SimEventKind.PlayerSlideStarted, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            w.Emit(SimEventKind.MobSpawned, new float2(4f, 4f), mobId, MobType.Chaser, 0f);
            w.Emit(SimEventKind.PlayerDamaged, new float2(6f, 0f), 1, default, 12f,
                zone: HitZone.Body, hitDir: new float2(1f, 0f), playerIndex: 1);
            w.Emit(SimEventKind.PlayerDied, new float2(0f, 7f), 2, default, 0f,
                zone: HitZone.Head, playerIndex: 2);

            var net = ScriptableObject.CreateInstance<NetConfig>();
            net.SnapshotEventBudget = 3;
            var asm = new SnapshotAssembler(cfg, net, connectionCount: 1);
            asm.BeginTick(w);
            AssembledFrame f = AssembledFrame.Decode(asm.BufferFor(0), asm.BuildFor(0, 0, 0, Epoch), cfg);

            Assert.AreEqual(3, f.EventCount, "the budget admits three");
            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.PlayerDied), "a death rides first (rank 0)");
            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.PlayerDamaged), "then the impact (rank 1)");
            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.MobSpawned), "then the state change (rank 2)");
            Assert.AreEqual(0, f.CountOf(SnapshotEventKind.PlayerDashed), "cosmetics wait");
            Assert.AreEqual(0, f.CountOf(SnapshotEventKind.PlayerSlideStarted));
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEvents,
                "and they WAIT — a deferral is not a drop, and conflating the two would hide real loss");

            // Rank order is also the order they are written in, which is what
            // makes a truncated read on the client still meaningful.
            Assert.AreEqual((byte)SnapshotEventKind.PlayerDied, f.Events[0].Kind);
            Assert.AreEqual((byte)SnapshotEventKind.PlayerDamaged, f.Events[1].Kind);
            Assert.AreEqual((byte)SnapshotEventKind.MobSpawned, f.Events[2].Kind);

            // Next frame, with nothing new: the deferred pair arrives.
            var idle = new SimInput[3];
            w.TickAll(idle);
            w.ClearEvents();
            asm.BeginTick(w);
            AssembledFrame f2 = AssembledFrame.Decode(asm.BufferFor(0), asm.BuildFor(0, 0, 0, Epoch), cfg);
            // TASK 29 AMENDED THE COUNT, NOT THE CLAIM. The two deferred
            // cosmetics still arrive here, exactly as Task 28 asserted; the
            // third record is the RESEND of the death this frame's budget has
            // room for (Р58 — fresh first, then resends by rank, all inside the
            // one budget). Expecting two would now be asserting the ABSENCE of
            // redundancy.
            Assert.AreEqual(3, f2.EventCount,
                "the two deferred cosmetics, plus one resend in the budget's remaining slot");
            Assert.AreEqual(1, f2.CountOf(SnapshotEventKind.PlayerDashed));
            Assert.AreEqual(1, f2.CountOf(SnapshotEventKind.PlayerSlideStarted));
            Assert.AreEqual((byte)1, f2.Events[0].TickDelta, "carried from exactly one tick back");
            Assert.AreEqual((byte)SnapshotEventKind.PlayerDied, f2.Events[2].Kind,
                "and the resend is the best-ranked one, written after every fresh record");
        }

        // ================= Stage 2 Task 29 — redundancy and dedup ===========
        //
        // The plan's three scenarios run the WHOLE pipe: SnapshotAssembler ->
        // BufferFor -> SnapshotReader/TryReadEventsBlock -> EventDedup. The
        // EventDedup unit tests below them poke the client half directly, where
        // a scenario would need a whole match to reach one branch.

        const ushort OtherEpoch = 0x5678;   // 22136 — both bytes differ from Epoch's

        static SnapshotBlocks.EventRecord DedupRecord(ushort seq, byte tickDelta)
            => new SnapshotBlocks.EventRecord
            {
                Kind = (byte)SnapshotEventKind.PlayerDied,
                Seq = seq,
                TickDelta = tickDelta,
                PayloadLength = (byte)SnapshotEvents.PayloadBytesFor(SnapshotEventKind.PlayerDied),
            };

        // ---- T29.C1. The plan's loss scenario ----

        [Test]
        public void LostSnapshot_EventsRecoveredByRedundancy()
        {
            // Р58's whole reason to exist. Events ride the UNRELIABLE payload of
            // a snapshot (Р27), and at the mandatory 5% loss every twentieth
            // death would otherwise vanish for good. Here every SECOND datagram
            // is lost — a far harsher rail than 5%, and one that N = 4 still
            // covers with three transmissions to spare — and the client must end
            // up having handled each event EXACTLY once: not zero (the loss got
            // through) and not twice (the dedup did not).
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));

            var net = ScriptableObject.CreateInstance<NetConfig>();
            Assert.AreEqual(4, net.EventRedundancyTicks, "fixture premise: the shipped default");
            var asm = new SnapshotAssembler(cfg, net, connectionCount: 1);
            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            const int emittingTicks = 6;
            const int totalTicks = 10;
            var idle = new SimInput[2];
            var handled = new Dictionary<(uint Tick, ushort Seq), int>();
            var handledAtDelta = new Dictionary<(uint Tick, ushort Seq), byte>();
            int recordsOffered = 0;

            for (int t = 0; t < totalTicks; t++)
            {
                w.ClearEvents();
                if (t < emittingTicks)
                    w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
                asm.BeginTick(w);
                int bytes = asm.BuildFor(0, 0, 0, Epoch);

                // The odd frames never leave the wire. Nothing is decoded for
                // them at all — that is what a lost datagram is.
                if ((t & 1) == 0)
                {
                    AssembledFrame f = AssembledFrame.Decode(asm.BufferFor(0), bytes, cfg);
                    for (int i = 0; i < f.EventCount; i++)
                    {
                        recordsOffered++;
                        var key = (f.Tick - f.Events[i].TickDelta, f.Events[i].Seq);
                        if (!dedup.TryAcceptEvent(f.Epoch, f.Tick, in f.Events[i], out _)) continue;
                        handled[key] = handled.TryGetValue(key, out int n) ? n + 1 : 1;
                        handledAtDelta[key] = f.Events[i].TickDelta;
                    }
                }
                w.TickAll(idle);
            }

            Assert.AreEqual(emittingTicks, handled.Count,
                "every emitted event must have reached the client despite half the datagrams being lost");
            foreach (KeyValuePair<(uint Tick, ushort Seq), int> pair in handled)
                Assert.AreEqual(1, pair.Value,
                    $"event (tick {pair.Key.Tick}, seq {pair.Key.Seq}) must be handled exactly once — "
                    + "a second pass would play a death animation twice");
            Assert.Greater(recordsOffered, handled.Count,
                "witness: duplicates really were OFFERED, or the dedup was never exercised at all");

            // And the half of the events that were recovered rather than merely
            // received: their first transmission went into a frame that never
            // arrived, so the copy the client handled is a RESEND.
            int recovered = 0;
            foreach (KeyValuePair<(uint Tick, ushort Seq), byte> pair in handledAtDelta)
            {
                if ((pair.Key.Tick & 1) == 0) continue;
                Assert.Greater(pair.Value, (byte)0,
                    $"the event of tick {pair.Key.Tick} was first sent in a LOST frame, so the copy the "
                    + "client saw must carry a nonzero tick delta — this is redundancy doing the work");
                recovered++;
            }
            Assert.AreEqual(emittingTicks / 2, recovered,
                "ticks 1, 3 and 5 emitted into frames that never arrived — three genuine recoveries");
        }

        // ---- T29.C2. The plan's reordering scenario ----

        [Test]
        public void ReorderedSnapshot_StateDropped_EventsKept()
        {
            // Р31's refinement (spec §3.7): the anti-stale rule is about STATE,
            // not about events. A reordered datagram must not apply its stale
            // world, but its events have never been seen and must be handled —
            // otherwise a packet that merely overtook another would swallow a
            // death outright.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));

            var net = ScriptableObject.CreateInstance<NetConfig>();
            net.SnapshotEventBudget = 1;   // so the newer frame carries only its OWN event
            var asm = new SnapshotAssembler(cfg, net, connectionCount: 1);
            var idle = new SimInput[2];

            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            asm.BeginTick(w);
            AssembledFrame older = AssembledFrame.Decode(asm.BufferFor(0),
                asm.BuildFor(0, 0, 0, Epoch), cfg);

            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerSlideStarted, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            asm.BeginTick(w);
            AssembledFrame newer = AssembledFrame.Decode(asm.BufferFor(0),
                asm.BuildFor(0, 0, 0, Epoch), cfg);

            Assert.AreEqual(1, older.EventCount, "fixture premise: one event per frame");
            Assert.AreEqual(1, newer.EventCount,
                "fixture premise: the budget of one leaves no room for the older event's resend, so the "
                + "reordering below really does deliver something the client has never seen");
            Assert.Greater(newer.Tick, older.Tick, "fixture premise: two different ticks");

            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            // The NEWER datagram overtakes the older one.
            Assert.IsTrue(dedup.TryAcceptState(newer.Epoch, newer.Tick), "the newer state applies");
            Assert.IsTrue(dedup.TryAcceptEvent(newer.Epoch, newer.Tick, in newer.Events[0], out _));

            Assert.IsFalse(dedup.TryAcceptState(older.Epoch, older.Tick),
                "the overtaken frame's STATE is stale and must not be applied over the newer one (Р31)");
            Assert.IsTrue(dedup.TryAcceptEvent(older.Epoch, older.Tick, in older.Events[0], out _),
                "but its event has never been seen and must still be handled — dropping it with the "
                + "state is exactly the hole the Р31 refinement closes");
        }

        // ---- T29.C3. The plan's replay scenario ----

        [Test]
        public void Dedup_DoesNotReplayEvents()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));

            var net = ScriptableObject.CreateInstance<NetConfig>();
            var asm = new SnapshotAssembler(cfg, net, connectionCount: 1);

            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            asm.BeginTick(w);
            AssembledFrame f = AssembledFrame.Decode(asm.BufferFor(0), asm.BuildFor(0, 0, 0, Epoch), cfg);
            Assert.AreEqual(2, f.EventCount, "fixture premise: two distinct events in one frame");
            Assert.AreNotEqual(f.Events[0].Seq, f.Events[1].Seq,
                "fixture premise: two events, two seq values — one number for both would make the "
                + "second assertion below pass for the wrong reason");

            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            Assert.IsTrue(dedup.TryAcceptState(f.Epoch, f.Tick));
            for (int i = 0; i < f.EventCount; i++)
                Assert.IsTrue(dedup.TryAcceptEvent(f.Epoch, f.Tick, in f.Events[i], out _),
                    $"first pass: event {i} has never been seen");

            // The identical datagram arrives a second time — a duplicated packet
            // on the wire, or the same resend read twice.
            Assert.IsFalse(dedup.TryAcceptState(f.Epoch, f.Tick),
                "the same tick's state is not applied twice (the gate is `<=`, not `<`)");
            for (int i = 0; i < f.EventCount; i++)
                Assert.IsFalse(dedup.TryAcceptEvent(f.Epoch, f.Tick, in f.Events[i], out _),
                    $"second pass: event {i} must not be handled again");
        }

        // ---- T29.C4. The key is built from the ORIGINAL tick ----

        [Test]
        public void Dedup_KeyIsBuiltFromTheOriginalTick_NotTheFrameTick()
        {
            // task-29-brief §2.5. A resend rides a LATER frame with a LARGER
            // tick delta, and the pair those two produce — `header.Tick -
            // record.TickDelta` — is invariant across every copy. Keying on the
            // frame's own tick would give each copy its own key, and every
            // resend would be handled as a new event: redundancy would double
            // every death instead of insuring it.
            var cfg = TestConfigs.Open();
            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            const uint frame = 1000;
            SnapshotBlocks.EventRecord first = DedupRecord(seq: 7, tickDelta: 2);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, frame, in first, out uint firstOrigin),
                "first sight of the event that happened on tick 998");
            Assert.AreEqual(998u, firstOrigin,
                "and the tick it names is the ORIGINAL one, not the frame's own 1000 — the caller "
                + "files the event under this number and must not re-derive it (fix round 1, F-2)");

            SnapshotBlocks.EventRecord resend = DedupRecord(seq: 7, tickDelta: 3);
            Assert.IsFalse(dedup.TryAcceptEvent(Epoch, frame + 1, in resend, out _),
                "one frame later, one delta larger — the SAME event (tick 998, seq 7), and keying on "
                + "the frame tick instead would have called it a new one");

            SnapshotBlocks.EventRecord other = DedupRecord(seq: 7, tickDelta: 2);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, frame + 1, in other, out uint otherOrigin),
                "witness: the same seq from a DIFFERENT origin tick (999) is a different event — "
                + "seq is unique per tick, not per match");
            Assert.AreEqual(999u, otherOrigin,
                "and it says so: the two accepted events differ by exactly the one tick their "
                + "origins differ by, which is what makes them two keys and not one");
        }

        // ---- T29.C4b (Task 44b fix round 1, F-2). The origin tick comes back ----

        [Test]
        public void Dedup_AcceptedEvent_HandsBackTheOriginTick_AndCannotUnderflow()
        {
            // Fix round 1, reviewer finding F-2. `ClientEventQueue` files an
            // accepted event under its ABSOLUTE tick, and until this round the
            // dedup derived that number, kept it in a local and returned a bare
            // bool — so the receiver had to perform the same subtraction a
            // second time. Two derivations of one key are two chances to
            // disagree, over a value that must match byte for byte or the event
            // surfaces on the wrong frame. The number now leaves through
            // `out originTick`, and this test is what holds that contract up.
            var cfg = TestConfigs.Open();
            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            // 1. The ordinary case: an event several ticks back inside a frame.
            SnapshotBlocks.EventRecord ordinary = DedupRecord(seq: 1, tickDelta: 7);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, 500, in ordinary, out uint ordinaryOrigin));
            Assert.AreEqual(493u, ordinaryOrigin, "500 - 7, and nothing else");

            // 2. Delta 0 — the event of the frame's own tick.
            SnapshotBlocks.EventRecord fresh = DedupRecord(seq: 2, tickDelta: 0);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, 500, in fresh, out uint freshOrigin));
            Assert.AreEqual(500u, freshOrigin, "delta 0 rides its own tick");

            // 3. THE UNDERFLOW BOUNDARY, both sides of it. `TickDelta ==
            // frameTick` names tick 0 — the first tick of the match, a legal
            // origin — and is admitted; one further back names a tick from
            // before the match began and is refused BEFORE the subtraction, so
            // the enormous wrapped uint that `frameTick - TickDelta` would
            // otherwise produce never exists. The guard is what the out
            // parameter's whole contract now rests on.
            var edge = new EventDedup(cfg);
            edge.Reset(Epoch);
            SnapshotBlocks.EventRecord atZero = DedupRecord(seq: 3, tickDelta: 9);
            Assert.IsTrue(edge.TryAcceptEvent(Epoch, 9, in atZero, out uint zeroOrigin),
                "delta exactly equal to the frame's tick is admitted — it names tick 0");
            Assert.AreEqual(0u, zeroOrigin, "and tick 0 is a real answer here, not an absence");

            SnapshotBlocks.EventRecord beforeTheMatch = DedupRecord(seq: 4, tickDelta: 10);
            Assert.IsFalse(edge.TryAcceptEvent(Epoch, 9, in beforeTheMatch, out uint refusedOrigin),
                "one tick further back is a record from before the match began (Р82)");
            Assert.AreEqual(0u, refusedOrigin,
                "and the refusal leaves `default`, not the ~4.29-billion wrap the subtraction would "
                + "have produced — which is exactly the number that would have been filed as a tick");

            // 4. Every OTHER refusal leaves `default` too, so a caller that
            // reads the number without checking the bool at least reads
            // something harmless. `default` is NOT a sentinel — case 3 above
            // returns 0 on success — the bool is.
            SnapshotBlocks.EventRecord foreign = DedupRecord(seq: 5, tickDelta: 0);
            Assert.IsFalse(dedup.TryAcceptEvent(OtherEpoch, 500, in foreign, out uint foreignOrigin),
                "a foreign epoch is refused");
            Assert.AreEqual(0u, foreignOrigin, "and hands back nothing that looks like a tick");

            SnapshotBlocks.EventRecord hostileSeq = DedupRecord((ushort)dedup.SeqCapacity, 0);
            Assert.IsFalse(dedup.TryAcceptEvent(Epoch, 500, in hostileSeq, out uint hostileOrigin),
                "a seq at capacity is refused");
            Assert.AreEqual(0u, hostileOrigin);

            Assert.IsFalse(dedup.TryAcceptEvent(Epoch, 500, in ordinary, out uint duplicateOrigin),
                "and the second copy of case 1's event is a duplicate");
            Assert.AreEqual(0u, duplicateOrigin,
                "which is the refusal that matters most: the record IS well-formed and its origin "
                + "tick WAS computed on the way to the verdict, so this is the one place the number "
                + "could have leaked out attached to a 'no'");
        }

        // ---- T29.C5. One epoch, and Reset clears everything ----

        [Test]
        public void Dedup_ForeignEpoch_RefusesBoth_AndResetClearsEverything()
        {
            // task-29-brief §2.5. Exactly ONE epoch is tracked, and the owner
            // (Task 32) names it over the Reliable lifecycle channel (Р60). A
            // stray datagram from another epoch must neither apply its state nor
            // have its events handled — and, above all, must not switch the
            // tracked epoch itself, which would let one wandering packet erase
            // the dedup state of the match in progress.
            var cfg = TestConfigs.Open();
            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            Assert.IsTrue(dedup.TryAcceptState(Epoch, 10));
            Assert.IsFalse(dedup.TryAcceptState(OtherEpoch, 20), "a foreign epoch's state is refused");
            Assert.IsTrue(dedup.TryAcceptState(Epoch, 11),
                "witness: the tracked epoch is still the one Reset named — the stray packet did not "
                + "take the match over");

            SnapshotBlocks.EventRecord record = DedupRecord(seq: 3, tickDelta: 0);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, 500, in record, out _));
            Assert.IsFalse(dedup.TryAcceptEvent(OtherEpoch, 500, in record, out _),
                "a foreign epoch's events are refused too — an id in one match means nothing in another");

            // A restart (Р60): the owner names the new epoch and everything the
            // old one taught is forgotten.
            dedup.Reset(OtherEpoch);
            Assert.IsTrue(dedup.TryAcceptEvent(OtherEpoch, 500, in record, out _),
                "after Reset the very same key is unseen again — a new match replays its own history");
            Assert.IsTrue(dedup.TryAcceptState(OtherEpoch, 0),
                "and state applies from tick zero again, because a restarted match starts at zero");
            Assert.IsFalse(dedup.TryAcceptState(Epoch, 999),
                "while the epoch that was tracked a moment ago is now the foreign one");
        }

        // ---- T29.C6. The anti-stale gate's boundary is `<=` ----

        [Test]
        public void Dedup_StateGate_IsLessOrEqual_NotStrictlyLess()
        {
            // Spec §3.7: "a snapshot with Tick <= _lastAppliedTick of the SAME
            // epoch does not apply its state". The boundary case is the one that
            // matters: a duplicated datagram carries the tick that was just
            // applied, and `<` would re-apply the whole world from it.
            var cfg = TestConfigs.Open();
            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            Assert.IsTrue(dedup.TryAcceptState(Epoch, 5), "the first frame applies");
            Assert.IsFalse(dedup.TryAcceptState(Epoch, 5),
                "the SAME tick again does not — this is the `<=` in the rule, and `<` would let a "
                + "duplicated packet re-apply it");
            Assert.IsFalse(dedup.TryAcceptState(Epoch, 4), "and an older one certainly does not");
            Assert.IsTrue(dedup.TryAcceptState(Epoch, 6), "witness: a newer one still does");
        }

        // ---- T29.C7. A hostile seq is refused, never thrown ----

        [Test]
        public void Dedup_HostileSeqAtOrAboveCapacity_IsRefusedWithoutThrowing()
        {
            // Р82: the wire is untrusted. `seq` is a raw u16 and the server's own
            // hard cap is 2 * MaxEventsPerFrame, so anything at or above that is
            // hostile or from another build. It must be REFUSED — never an
            // IndexOutOfRange out of a bitmask indexed by an attacker's number,
            // and never a "handle it anyway" either.
            var cfg = TestConfigs.Open();
            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            Assert.AreEqual(2 * cfg.Arena.MaxEventsPerFrame, dedup.SeqCapacity,
                "the client's per-tick seq capacity is the server's own hard cap — one wire event per "
                + "SimEvent plus the ProjectileFired split (SnapshotAssembler's own doc)");

            SnapshotBlocks.EventRecord justOver = DedupRecord((ushort)dedup.SeqCapacity, 0);
            bool atCapacity = true;
            Assert.DoesNotThrow(() => atCapacity = dedup.TryAcceptEvent(Epoch, 100, in justOver, out _),
                "a hostile seq must not throw — Р82 is 'do not throw' AND 'do not hand back rubbish'");
            Assert.IsFalse(atCapacity, "and it must be refused, not handled");

            bool maxed = true;
            SnapshotBlocks.EventRecord huge = DedupRecord(ushort.MaxValue, 0);
            Assert.DoesNotThrow(() => maxed = dedup.TryAcceptEvent(Epoch, 100, in huge, out _));
            Assert.IsFalse(maxed);

            // Nothing was corrupted on the way past: an ordinary record still
            // works, and still deduplicates.
            SnapshotBlocks.EventRecord legal = DedupRecord(seq: 3, tickDelta: 0);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, 100, in legal, out _),
                "witness: the refusals above left the structure usable");
            Assert.IsFalse(dedup.TryAcceptEvent(Epoch, 100, in legal, out _),
                "and still deduplicating");
        }

        // ---- T29.C8. The tick window ----

        [Test]
        public void Dedup_KeyOlderThanTheWindow_IsRefused_AndABigTickJumpLeavesNoStaleMasks()
        {
            // task-29-brief §2.5. The memory is a ring of per-tick seq masks, so
            // it has a horizon — and both of its edges have to behave.
            //
            // FALLING OUT OF THE WINDOW is a CONSERVATIVE refusal ("assume
            // seen"): a frame that old has been in flight for more than eight
            // seconds and Task 32/37's interpolation discards it anyway
            // (InterpMaxStaleTicks 3), so a false refusal costs nothing while a
            // false acceptance would replay a death.
            //
            // A BIG JUMP FORWARD must leave no dirty mask behind: a ring slot
            // reused by a much newer tick that still carried the old tick's bits
            // would silently swallow real events (урок 110 — this is the second
            // invariant, and the one a naive implementation fails).
            var cfg = TestConfigs.Open();
            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            const uint newest = 100000;
            SnapshotBlocks.EventRecord newestRecord = DedupRecord(seq: 1, tickDelta: 0);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, newest, in newestRecord, out _));

            SnapshotBlocks.EventRecord justInside = DedupRecord(seq: 1, tickDelta: 0);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, newest - (EventDedup.WindowTicks - 1), in justInside, out _),
                "one tick inside the window is still remembered, so it is answered on its own merits");

            SnapshotBlocks.EventRecord justOutside = DedupRecord(seq: 2, tickDelta: 0);
            Assert.IsFalse(dedup.TryAcceptEvent(Epoch, newest - EventDedup.WindowTicks, in justOutside, out _),
                "exactly one tick further back is outside the window, and an unknown answer is "
                + "conservatively 'already seen'");

            // The ring, wrapped: the same slot, a whole window later.
            var fresh = new EventDedup(cfg);
            fresh.Reset(Epoch);
            const uint baseTick = 1000;
            SnapshotBlocks.EventRecord marker = DedupRecord(seq: 5, tickDelta: 0);
            Assert.IsTrue(fresh.TryAcceptEvent(Epoch, baseTick, in marker, out _));
            Assert.IsTrue(fresh.TryAcceptEvent(Epoch, baseTick + 10, in marker, out _),
                "a different tick, so a different mask");
            SnapshotBlocks.EventRecord walker = DedupRecord(seq: 9, tickDelta: 0);
            Assert.IsTrue(fresh.TryAcceptEvent(Epoch, baseTick + 100, in walker, out _));
            Assert.IsTrue(fresh.TryAcceptEvent(Epoch, baseTick + 200, in walker, out _));
            Assert.IsTrue(fresh.TryAcceptEvent(Epoch, baseTick + 10 + EventDedup.WindowTicks, in marker, out _),
                "the slot that held tick 1010's mask has come round again — a ring that did not wipe "
                + "it as the window advanced would call this brand-new event a duplicate and eat it");
            Assert.IsFalse(fresh.TryAcceptEvent(Epoch, baseTick + 10 + EventDedup.WindowTicks, in marker, out _),
                "witness: the slot is working — the second copy of THAT event is refused");
        }

        // ---- T29.C8b (fix round 1). The window walk terminates at the u32 edge ----

        [Test]
        public void Dedup_WindowAdvance_AtTheU32Edge_TerminatesAndAnswers()
        {
            // Fix round 1, reviewer finding F1. The incremental wipe used to
            // walk ABSOLUTE tick values — `for (t = newest + 1; t <= tick; t++)`
            // — and at tick == uint.MaxValue that condition holds for EVERY
            // uint: the increment wraps to zero and the loop never leaves. Two
            // well-formed frames of the tracked epoch reach it, so this is
            // hostile-input territory (Р82), and the failure mode is the worst
            // of the three — not a throw, not a wrong answer, a hang. RED for
            // this test is therefore a HANG killed by the harness timeout, not
            // an assert. The fix walks the STEP COUNT, bounded by the jump,
            // itself under WindowTicks here — which no input can stretch.
            var cfg = TestConfigs.Open();
            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            // A jump of exactly the tick-delta byte's ceiling: comfortably
            // inside the window, so the incremental branch (the one that used
            // to walk absolute ticks) is the branch that runs.
            Assert.Less((int)byte.MaxValue, EventDedup.WindowTicks,
                "premise: a 255-tick jump takes the incremental branch, not the full wipe");
            const uint nearTop = uint.MaxValue - byte.MaxValue;
            SnapshotBlocks.EventRecord plant = DedupRecord(seq: 1, tickDelta: 0);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, nearTop, in plant, out _),
                "the frame that plants the window's edge near the top of u32 is ordinary");

            SnapshotBlocks.EventRecord atTheEdge = DedupRecord(seq: 2, tickDelta: 0);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, uint.MaxValue, in atTheEdge, out _),
                "a fresh key at tick uint.MaxValue itself is answered on its merits — the "
                + "incremental wipe must terminate for control to even get here");
            Assert.IsFalse(dedup.TryAcceptEvent(Epoch, uint.MaxValue, in atTheEdge, out _),
                "witness that the masks survived the walk: the second copy is a duplicate");
        }

        // ---- T29.C9. The client half allocates nothing either ----

        [Test]
        public void Dedup_DoesNotAllocateGCMemory()
        {
            var cfg = TestConfigs.Open();
            var dedup = new EventDedup(cfg);
            dedup.Reset(Epoch);

            // Stub-defeating premise before anything is measured (Task 26
            // finding F-D): a class that answers a constant allocates nothing
            // either, so the measurement only means something once the thing
            // being measured is shown to work.
            SnapshotBlocks.EventRecord warm = DedupRecord(seq: 1, tickDelta: 0);
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, 500, in warm, out _));
            Assert.IsFalse(dedup.TryAcceptEvent(Epoch, 500, in warm, out _),
                "fixture premise: the dedup really is deduplicating");
            Assert.IsTrue(dedup.TryAcceptState(Epoch, 500));
            Assert.IsFalse(dedup.TryAcceptState(Epoch, 500), "fixture premise: the gate really gates");

            Assert.That(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    uint frameTick = 600u + (uint)i;
                    dedup.TryAcceptState(Epoch, frameTick);
                    for (ushort s = 0; s < 8; s++)
                    {
                        var record = new SnapshotBlocks.EventRecord
                        {
                            Kind = (byte)SnapshotEventKind.PlayerDied,
                            Seq = s,
                            TickDelta = (byte)(i & 3),
                            PayloadLength = 2,
                        };
                        dedup.TryAcceptEvent(Epoch, frameTick, in record, out _);
                    }
                }
            }, Is.Not.AllocatingGCMemory());
        }

        // ================= Stage 3 Task 25: the five new blocks =============
        //
        // Spec §3.12's table, byte for byte. Same discipline as Task 27's
        // five above: every layout is pinned against INDEPENDENTLY spelled
        // out bytes, never against the reader — a round trip is blind to any
        // mutation applied symmetrically to both sides.
        //
        // FIXTURE NUMBERS. Ids 4211/58317, positions (11,-43)/(-29,38),
        // item ids 23/47/91/199 and the Match block's 743 were checked with a
        // token-boundary grep across client/Assets/Data/*.asset. The only
        // hits — 43, 23, 47 — are all inside `m_Script` GUID strings, not
        // values, so none of these is a balance number. (An earlier draft of
        // this line said (11,-53)/(-67,38) and 200: the first two were
        // positions OUTSIDE the fixture arena, which Quantize.Pos saturates,
        // and 200 is a live `CreditValue` in ItemCatalog.asset — both found
        // by the Task 25 review, both corrected here. State where you looked,
        // not just what you concluded.) Byte offsets and mask literals are
        // structural, not fixtures.

        // Pickup fixtures, positions INSIDE the fixture arena (SnapRadius 52
        // — Quantize.Pos saturates, so a coordinate outside it would pin a
        // clamp rather than a layout). K1: id 4211 -> (0x73,0x10), pos
        // (11,-43) -> posX 39699 (0x13,0x9B), posY 5671 (0x27,0x16), kind
        // EnergyCell -> 0. K2: id 58317 -> (0xCD,0xE3), pos (-29,38) -> posX
        // 14493 (0x9D,0x38), posY 56713 (0x89,0xDD).
        //
        // Fixture check, stated the way this file's own convention asks:
        // 4211, 58317, 11, 43, 29, 38, 23, 47, 91 and 743 were grepped across
        // every client/Assets/Data/*.asset with a token-boundary pattern. The
        // only three hits — 43, 23, 47 — are all inside `m_Script` GUID
        // strings, not values, so none of these numbers is a balance number.
        static readonly SnapshotBlocks.PickupRecord PickupK1 = new SnapshotBlocks.PickupRecord
        {
            Id = 4211, Kind = PickupKind.EnergyCell, Pos = new float2(11f, -43f),
        };
        static readonly SnapshotBlocks.PickupRecord PickupK2 = new SnapshotBlocks.PickupRecord
        {
            Id = 58317, Kind = PickupKind.EnergyCell, Pos = new float2(-29f, 38f),
        };

        // Container fixtures, same positions as the pickups above so the two
        // layouts differ ONLY in their tail byte — which is the whole point
        // of the pair: the kind/empty nibbles are what a mutation of either
        // block's packing has to disturb.
        static readonly SnapshotBlocks.ContainerRecord ContainerC1 = new SnapshotBlocks.ContainerRecord
        {
            Id = 4211, Kind = ContainerKind.Crate, IsEmpty = false, Pos = new float2(11f, -43f),
        };
        static readonly SnapshotBlocks.ContainerRecord ContainerC2 = new SnapshotBlocks.ContainerRecord
        {
            Id = 58317, Kind = ContainerKind.PlayerCorpse, IsEmpty = true, Pos = new float2(-29f, 38f),
        };

        const MatchPhase MatchFixturePhase = MatchPhase.DirectorActive;
        const ushort MatchFixtureSeconds = 743;     // 0x02E7 — both bytes nonzero and different
        const byte MatchFixtureFlags = MatchWireFlags.DirectorAlive; // 0x01

        static float2 DecodedPos(float2 raw)
            => new float2(
                Quantize.PosBack(Quantize.Pos(raw.x, SnapRadius), SnapRadius),
                Quantize.PosBack(Quantize.Pos(raw.y, SnapRadius), SnapRadius));

        [Test]
        public void Match_ByteLayout_PhaseSecondsFlags_LittleEndian()
        {
            const int tailBytes = 4;
            int blockBytes = SnapshotWriter.MatchBlockBytes();
            var buffer = Filled(SnapshotWriter.HeaderBytes + blockBytes + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteMatchBlock(MatchFixturePhase, MatchFixtureSeconds, MatchFixtureFlags);
            Assert.AreEqual(SnapshotWriter.HeaderBytes + blockBytes, writer.BytesWritten);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Match, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)4, buffer[b + 1], "block byte 1: payloadBytes low = 4");
            Assert.AreEqual((byte)0, buffer[b + 2], "block byte 2: payloadBytes high");

            int r = b + SnapshotWriter.BlockHeaderBytes;
            Assert.AreEqual((byte)1, buffer[r + 0], "payload byte 0: phase (DirectorActive = 1)");
            Assert.AreEqual((byte)0xE7, buffer[r + 1], "payload byte 1: seconds low (743 = 0x02E7)");
            Assert.AreEqual((byte)0x02, buffer[r + 2], "payload byte 2: seconds high");
            Assert.AreEqual((byte)0x01, buffer[r + 3], "payload byte 3: flags (DirectorAlive)");

            Assert.IsTrue(SnapshotBlocks.TryReadMatchBlock(
                new System.ReadOnlySpan<byte>(buffer, r, SnapshotBlocks.MatchBlockPayloadBytes),
                out MatchPhase phase, out ushort seconds, out byte flags, out SnapshotBlockError error));
            Assert.AreEqual(MatchFixturePhase, phase);
            Assert.AreEqual(MatchFixtureSeconds, seconds);
            Assert.AreEqual(MatchFixtureFlags, flags);
            Assert.AreEqual(SnapshotBlockError.None, error);
            Assert.IsTrue((flags & MatchWireFlags.DirectorAlive) != 0,
                "the Director is alive in this fixture");
            Assert.IsFalse((flags & MatchWireFlags.GateOpen) != 0,
                "and the gate is not open — the two bits are separate facts");

            for (int i = SnapshotWriter.HeaderBytes + blockBytes; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the block");
        }

        [Test]
        public void MatchBlock_MalformedLength_AndPhaseOutsideItsDomain_Refused_NoException()
        {
            var tooShort = new byte[] { 1, 0xE7, 0x02 };
            bool ok = true;
            var error = SnapshotBlockError.None;
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadMatchBlock(
                tooShort, out _, out _, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error, "3 bytes is not the Match block's shape");

            // (MatchPhase)9 is a legal cast and an illegal value — Т30's HUD
            // switches on this byte and Т32's overlay branches on it.
            var badPhase = new byte[] { 9, 0xE7, 0x02, 0x01 };
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadMatchBlock(
                badPhase, out _, out _, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
        }

        [Test]
        public void Self_ByteLayout_SlotPointsCountAndItemIds()
        {
            const byte slotPoints = 5;
            var items = new byte[] { SnapItemA, SnapItemC, SnapItemB };
            const int tailBytes = 3;
            int blockBytes = SnapshotWriter.SelfBlockBytes(items.Length);
            var buffer = Filled(SnapshotWriter.HeaderBytes + blockBytes + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteSelfBlock(slotPoints, items);
            Assert.AreEqual(SnapshotWriter.HeaderBytes + blockBytes, writer.BytesWritten);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Self, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)5, buffer[b + 1], "block byte 1: payloadBytes low = 2 + 3 ids");
            Assert.AreEqual((byte)0, buffer[b + 2], "block byte 2: payloadBytes high");

            int r = b + SnapshotWriter.BlockHeaderBytes;
            Assert.AreEqual((byte)5, buffer[r + 0], "payload byte 0: slot points");
            Assert.AreEqual((byte)3, buffer[r + 1], "payload byte 1: item count");
            Assert.AreEqual(SnapItemA, buffer[r + 2], "payload byte 2: first item id");
            Assert.AreEqual(SnapItemC, buffer[r + 3], "payload byte 3: second item id, IN ORDER");
            Assert.AreEqual(SnapItemB, buffer[r + 4], "payload byte 4: third item id");

            var decoded = new byte[8];
            Assert.IsTrue(SnapshotBlocks.TryReadSelfBlock(
                new System.ReadOnlySpan<byte>(buffer, r, 5), SnapCfg, decoded,
                out byte decodedPoints, out int decodedCount, out SnapshotBlockError error));
            Assert.AreEqual(slotPoints, decodedPoints);
            Assert.AreEqual(3, decodedCount);
            Assert.AreEqual(SnapItemA, decoded[0]);
            Assert.AreEqual(SnapItemC, decoded[1]);
            Assert.AreEqual(SnapItemB, decoded[2]);
            Assert.AreEqual(SnapshotBlockError.None, error);

            for (int i = SnapshotWriter.HeaderBytes + blockBytes; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the block");
        }

        [Test]
        public void SelfBlock_Refusals_LengthDestinationAndUnknownItemId()
        {
            var destination = new byte[8];
            bool ok = true;
            int count = -1;
            var error = SnapshotBlockError.None;

            // Shorter than its own fixed head.
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadSelfBlock(
                new byte[] { 5 }, SnapCfg, destination, out _, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error, "one byte cannot hold the 2-byte head");

            // The count field and the payload length disagree — the count is
            // the SENDER's, so this is hostile input, not a caller bug.
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadSelfBlock(
                new byte[] { 5, 4, SnapItemA, SnapItemB }, SnapCfg, destination, out _, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error,
                "a count of 4 with 2 ids present is a length that lies");

            // More items than the caller's buffer holds.
            var tiny = new byte[1];
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadSelfBlock(
                new byte[] { 5, 2, SnapItemA, SnapItemB }, SnapCfg, tiny, out _, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.DestinationTooSmall, error);

            // An id the catalog does not hold. ItemCatalogLookup.Find THROWS
            // on one, so letting it through would turn a hostile packet into
            // an exception inside whichever consumer resolved it first.
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadSelfBlock(
                new byte[] { 5, 2, SnapItemA, SnapItemNotInCatalog }, SnapCfg, destination,
                out _, out count, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
            Assert.AreEqual(0, count, "content validation refuses the WHOLE block, nothing is handed back");
        }

        [Test]
        public void Pickups_ByteLayout_TwoRecords_IdPosKind()
        {
            const int tailBytes = 4;
            int blockBytes = SnapshotWriter.PickupsBlockBytes(2);
            var buffer = Filled(SnapshotWriter.HeaderBytes + blockBytes + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WritePickupsBlock(new[] { PickupK1, PickupK2 }, SnapCfg);
            Assert.AreEqual(SnapshotWriter.HeaderBytes + blockBytes, writer.BytesWritten);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Pickups, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)14, buffer[b + 1], "block byte 1: payloadBytes low (14 = 2 records * 7)");
            Assert.AreEqual((byte)0, buffer[b + 2], "block byte 2: payloadBytes high");

            int r0 = b + SnapshotWriter.BlockHeaderBytes;
            Assert.AreEqual((byte)0x73, buffer[r0 + 0], "record 1 byte 0: id low (4211 = 0x1073)");
            Assert.AreEqual((byte)0x10, buffer[r0 + 1], "record 1 byte 1: id high");
            Assert.AreEqual((byte)0x13, buffer[r0 + 2], "record 1 byte 2: posX low (39699 = 0x9B13)");
            Assert.AreEqual((byte)0x9B, buffer[r0 + 3], "record 1 byte 3: posX high");
            Assert.AreEqual((byte)0x27, buffer[r0 + 4], "record 1 byte 4: posY low (5671 = 0x1627)");
            Assert.AreEqual((byte)0x16, buffer[r0 + 5], "record 1 byte 5: posY high");
            Assert.AreEqual((byte)0x00, buffer[r0 + 6], "record 1 byte 6: kind (EnergyCell = 0)");

            int r1 = r0 + SnapshotBlocks.PickupRecordBytes;
            Assert.AreEqual((byte)0xCD, buffer[r1 + 0], "record 2 byte 0: id low (58317 = 0xE3CD)");
            Assert.AreEqual((byte)0xE3, buffer[r1 + 1], "record 2 byte 1: id high");

            var decoded = new SnapshotBlocks.PickupRecord[4];
            Assert.IsTrue(SnapshotBlocks.TryReadPickupsBlock(
                new System.ReadOnlySpan<byte>(buffer, r0, 14), SnapCfg, decoded,
                out int count, out SnapshotBlockError error));
            Assert.AreEqual(2, count);
            Assert.AreEqual(SnapshotBlockError.None, error);
            Assert.AreEqual(PickupK1.Id, decoded[0].Id);
            Assert.AreEqual(PickupK2.Id, decoded[1].Id);
            Assert.AreEqual(PickupKind.EnergyCell, decoded[0].Kind);
            Assert.AreEqual(DecodedPos(PickupK1.Pos).x, decoded[0].Pos.x, PosNoiseMeters);
            Assert.AreEqual(DecodedPos(PickupK1.Pos).y, decoded[0].Pos.y, PosNoiseMeters);
            Assert.AreEqual(DecodedPos(PickupK2.Pos).x, decoded[1].Pos.x, PosNoiseMeters);
            Assert.AreEqual(DecodedPos(PickupK2.Pos).y, decoded[1].Pos.y, PosNoiseMeters);

            for (int i = SnapshotWriter.HeaderBytes + blockBytes; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the block");
        }

        [Test]
        public void PickupsBlock_Refusals_LengthDestinationAndKindOutsideItsDomain()
        {
            var destination = new SnapshotBlocks.PickupRecord[4];
            bool ok = true;
            int count = -1;
            var error = SnapshotBlockError.None;

            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadPickupsBlock(
                new byte[SnapshotBlocks.PickupRecordBytes + 1], SnapCfg, destination, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error, "8 B is not a multiple of 7");

            var tiny = new SnapshotBlocks.PickupRecord[1];
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadPickupsBlock(
                new byte[2 * SnapshotBlocks.PickupRecordBytes], SnapCfg, tiny, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.DestinationTooSmall, error);

            // PickupKind has exactly one member, so 1 is already outside it.
            var badKind = new byte[SnapshotBlocks.PickupRecordBytes];
            badKind[6] = 1;
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadPickupsBlock(
                badKind, SnapCfg, destination, out count, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
            Assert.AreEqual(0, count, "the whole block is refused, nothing is written into destination");
        }

        [Test]
        public void Containers_ByteLayout_TwoRecords_KindAndEmptyNibbles()
        {
            const int tailBytes = 4;
            int blockBytes = SnapshotWriter.ContainersBlockBytes(2);
            var buffer = Filled(SnapshotWriter.HeaderBytes + blockBytes + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteContainersBlock(new[] { ContainerC1, ContainerC2 }, SnapCfg);
            Assert.AreEqual(SnapshotWriter.HeaderBytes + blockBytes, writer.BytesWritten);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Containers, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)14, buffer[b + 1], "block byte 1: payloadBytes low (14 = 2 records * 7)");

            int r0 = b + SnapshotWriter.BlockHeaderBytes;
            Assert.AreEqual((byte)0x73, buffer[r0 + 0], "record 1 byte 0: id low");
            Assert.AreEqual((byte)0x10, buffer[r0 + 1], "record 1 byte 1: id high");
            Assert.AreEqual((byte)0x10, buffer[r0 + 6],
                "record 1 byte 6: kind Crate (1) in the HIGH nibble, not empty (0) in the low");

            int r1 = r0 + SnapshotBlocks.ContainerRecordBytes;
            Assert.AreEqual((byte)0x41, buffer[r1 + 6],
                "record 2 byte 6: kind PlayerCorpse (4) high, empty (1) low");

            var decoded = new SnapshotBlocks.ContainerRecord[4];
            Assert.IsTrue(SnapshotBlocks.TryReadContainersBlock(
                new System.ReadOnlySpan<byte>(buffer, r0, 14), SnapCfg, decoded,
                out int count, out SnapshotBlockError error));
            Assert.AreEqual(2, count);
            Assert.AreEqual(SnapshotBlockError.None, error);
            Assert.AreEqual(ContainerKind.Crate, decoded[0].Kind);
            Assert.IsFalse(decoded[0].IsEmpty);
            Assert.AreEqual(ContainerKind.PlayerCorpse, decoded[1].Kind);
            Assert.IsTrue(decoded[1].IsEmpty, "an already-looted box must read as empty at a distance");
            Assert.AreEqual(DecodedPos(ContainerC2.Pos).x, decoded[1].Pos.x, PosNoiseMeters);
            Assert.AreEqual(DecodedPos(ContainerC2.Pos).y, decoded[1].Pos.y, PosNoiseMeters);

            for (int i = SnapshotWriter.HeaderBytes + blockBytes; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the block");
        }

        [Test]
        public void ContainersBlock_Refusals_LengthDestinationAndKindOutsideItsDomain()
        {
            var destination = new SnapshotBlocks.ContainerRecord[4];
            bool ok = true;
            int count = -1;
            var error = SnapshotBlockError.None;

            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainersBlock(
                new byte[SnapshotBlocks.ContainerRecordBytes + 1], SnapCfg, destination, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error);

            var tiny = new SnapshotBlocks.ContainerRecord[1];
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainersBlock(
                new byte[2 * SnapshotBlocks.ContainerRecordBytes], SnapCfg, tiny, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.DestinationTooSmall, error);

            // (ContainerKind)7 in the high nibble — a legal cast, an illegal
            // value, and Т31 indexes a prefab table by exactly this.
            var badKind = new byte[SnapshotBlocks.ContainerRecordBytes];
            badKind[6] = 0x70;
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainersBlock(
                badKind, SnapCfg, destination, out count, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
            Assert.AreEqual(0, count);

            // The low nibble is a FLAG, so anything but 0 or 1 is content the
            // format cannot mean — refused rather than truncated to a bool.
            var badFlag = new byte[SnapshotBlocks.ContainerRecordBytes];
            badFlag[6] = 0x05;
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainersBlock(
                badFlag, SnapCfg, destination, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
        }

        [Test]
        public void ContainerSlots_ByteLayout_IdMaskAndOnlyOccupiedItems()
        {
            // Slots 0 and 3 occupied, 1/2 empty — the case Р277 exists for:
            // a compact list would renumber the second item to index 1 and
            // every Take against it would be refused by construction.
            const byte mask = 0b1001;
            var itemPool = new byte[] { SnapItemA, SnapItemB };
            var records = new[]
            {
                new SnapshotBlocks.ContainerSlotsRecord { Id = 4211, OccupancyMask = mask, ItemOffset = 0 },
            };

            const int tailBytes = 3;
            int blockBytes = SnapshotWriter.ContainerSlotsBlockBytes(1, 2);
            var buffer = Filled(SnapshotWriter.HeaderBytes + blockBytes + tailBytes);
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteContainerSlotsBlock(records, itemPool);
            Assert.AreEqual(SnapshotWriter.HeaderBytes + blockBytes, writer.BytesWritten);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.ContainerSlots, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)5, buffer[b + 1], "block byte 1: payloadBytes low (3 head + 2 ids)");

            int r = b + SnapshotWriter.BlockHeaderBytes;
            Assert.AreEqual((byte)0x73, buffer[r + 0], "record byte 0: id low");
            Assert.AreEqual((byte)0x10, buffer[r + 1], "record byte 1: id high");
            Assert.AreEqual((byte)0b1001, buffer[r + 2], "record byte 2: occupancy mask, slots 0 and 3");
            Assert.AreEqual(SnapItemA, buffer[r + 3], "record byte 3: the item in slot 0");
            Assert.AreEqual(SnapItemB, buffer[r + 4], "record byte 4: the item in slot 3, ascending slot order");

            var decoded = new SnapshotBlocks.ContainerSlotsRecord[4];
            var payload = new System.ReadOnlySpan<byte>(buffer, r, 5);
            Assert.IsTrue(SnapshotBlocks.TryReadContainerSlotsBlock(
                payload, SnapCfg, decoded, out int count, out SnapshotBlockError error));
            Assert.AreEqual(1, count);
            Assert.AreEqual(SnapshotBlockError.None, error);
            Assert.AreEqual(4211, decoded[0].Id);
            Assert.AreEqual(mask, decoded[0].OccupancyMask);
            Assert.AreEqual(2, SnapshotBlocks.OccupiedSlotCount(decoded[0].OccupancyMask),
                "how many ids follow is the mask's popcount — derived, never a second field");
            Assert.AreEqual(SnapItemA, payload[decoded[0].ItemOffset],
                "ItemOffset indexes the payload on the read side, like EventRecord.PayloadOffset");
            Assert.AreEqual(SnapItemB, payload[decoded[0].ItemOffset + 1]);

            for (int i = SnapshotWriter.HeaderBytes + blockBytes; i < buffer.Length; i++)
                Assert.AreEqual(Sentinel, buffer[i], $"byte {i}: nothing may be written past the block");
        }

        [Test]
        public void ContainerSlotsBlock_Refusals_MaskPromisingMoreThanRemains_DestinationAndUnknownItemId()
        {
            var destination = new SnapshotBlocks.ContainerSlotsRecord[4];
            bool ok = true;
            int taken = -1;
            var error = SnapshotBlockError.None;

            // A mask promising three ids with one byte left.
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainerSlotsBlock(
                new byte[] { 0x73, 0x10, 0b111, SnapItemA }, SnapCfg, destination, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error);

            // Shorter than the record's own 3-byte head.
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainerSlotsBlock(
                new byte[] { 0x73, 0x10 }, SnapCfg, destination, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error);

            var tiny = new SnapshotBlocks.ContainerSlotsRecord[1];
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainerSlotsBlock(
                new byte[] { 0x73, 0x10, 0b1, SnapItemA, 0x74, 0x10, 0b1, SnapItemB },
                SnapCfg, tiny, out taken, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.DestinationTooSmall, error);
            Assert.AreEqual(1, taken,
                "a walker discovers the overflow mid-walk, so what it already decoded stays — the same "
                + "contract TryReadEventsBlock documents");

            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainerSlotsBlock(
                new byte[] { 0x73, 0x10, 0b1, SnapItemNotInCatalog }, SnapCfg, destination, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
        }

        [Test]
        public void ContainerSlotsBlock_PayloadLongerThanTheOffsetField_IsRefused_NoException()
        {
            // The same guard, and the same reason, as
            // EventsBlock_PayloadLongerThanTheOffsetField_IsRefused_
            // NoException above: ContainerSlotsRecord.ItemOffset is a ushort,
            // so past 65535 an offset wraps and points a consumer at the
            // wrong item ids — silent corruption, which Р82 rules out.
            // Unreachable through SnapshotReader (its block lengths are u16
            // by construction) and enforced anyway, because the method is
            // public and its own doc invites direct calls. Without this test
            // both "use >= instead of >" and "report the wrong error" would
            // be free mutations.
            var huge = new byte[ushort.MaxValue + 1];
            var destination = new SnapshotBlocks.ContainerSlotsRecord[4];
            bool ok = true;
            int taken = -1;
            var error = SnapshotBlockError.None;
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainerSlotsBlock(
                huge, SnapCfg, destination, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error);

            // …and exactly 65535 must still REACH the walk, or the "use >=
            // instead of >" mutation this test names would be free (Task 25
            // review, Important — the first version handed the decoder a
            // three-byte SLICE of a big array, which passes the guard under
            // either comparison and proved nothing). So the payload really is
            // 65535 B, and it is well-formed: 13107 records of 5 B each (a
            // 3-byte head plus a mask of two occupied slots). The destination
            // is deliberately smaller, so the walk stops on
            // DestinationTooSmall — a refusal from FURTHER IN than the cap
            // guard, which is exactly what proves the cap guard let it past.
            var atTheLimit = new byte[ushort.MaxValue];
            for (int i = 0; i + 5 <= atTheLimit.Length; i += 5)
            {
                atTheLimit[i] = 0x73;
                atTheLimit[i + 1] = 0x10;
                atTheLimit[i + 2] = 0b11;
                atTheLimit[i + 3] = SnapItemA;
                atTheLimit[i + 4] = SnapItemB;
            }
            Assert.AreEqual(0, atTheLimit.Length % 5,
                "fixture premise: 65535 is 13107 whole records, so nothing is malformed by length");
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadContainerSlotsBlock(
                atTheLimit, SnapCfg, destination, out taken, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.DestinationTooSmall, error,
                "a 65535 B payload must pass the ushort cap and be refused by the DESTINATION instead — "
                + "if this reads MalformedLength, the cap guard has become >= and eats a legal payload");
            Assert.AreEqual(destination.Length, taken,
                "and it must have filled the destination before running out of room");
        }

        /// The canonical Match -> Self -> Pickups -> Containers ->
        /// ContainerSlots frame, sized through the writer's own calculators —
        /// the Task 25 twin of BuildCanonicalFiveBlockFrame, and the fixture
        /// the two house sweeps below run on. It exists because those sweeps
        /// (truncate at every length; allocate nothing) are the DOMESTIC
        /// witnesses of "a decoder never throws on hostile bytes" (Р82) and
        /// "the codec allocates nothing", and until Task 25's review they
        /// covered only the five blocks of Task 27 — leaving the most
        /// intricate new decoder, the ContainerSlots walker, outside both.
        static byte[] BuildCanonicalNewBlockFrame()
        {
            var items = new byte[] { SnapItemA, SnapItemB };
            int size = SnapshotWriter.HeaderBytes
                       + SnapshotWriter.MatchBlockBytes()
                       + SnapshotWriter.SelfBlockBytes(items.Length)
                       + SnapshotWriter.PickupsBlockBytes(2)
                       + SnapshotWriter.ContainersBlockBytes(2)
                       + SnapshotWriter.ContainerSlotsBlockBytes(1, 2);
            var buffer = new byte[size];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteMatchBlock(MatchFixturePhase, MatchFixtureSeconds, MatchFixtureFlags);
            writer.WriteSelfBlock(4, items);
            writer.WritePickupsBlock(new[] { PickupK1, PickupK2 }, SnapCfg);
            writer.WriteContainersBlock(new[] { ContainerC1, ContainerC2 }, SnapCfg);
            writer.WriteContainerSlotsBlock(
                new[] { new SnapshotBlocks.ContainerSlotsRecord { Id = 4211, OccupancyMask = 0b11, ItemOffset = 0 } },
                items);
            Assert.AreEqual(size, writer.BytesWritten,
                "fixture premise: the canonical new-block frame must fill the buffer exactly");
            return buffer;
        }

        /// Decodes every block of `frame` the Task 25 kinds cover, into
        /// caller-owned scratch. Shared by the two sweeps below so the switch
        /// over the five new kinds exists once.
        static void DecodeNewBlocks(byte[] frame, int length,
            SnapshotBlocks.PickupRecord[] pickups, SnapshotBlocks.ContainerRecord[] containers,
            SnapshotBlocks.ContainerSlotsRecord[] slots, byte[] selfItems, byte[] knownKinds)
        {
            var reader = new SnapshotReader(new System.ReadOnlySpan<byte>(frame, 0, length));
            reader.TryReadHeader(out _, out _, out _);
            while (reader.TryReadBlock(knownKinds, out byte kind, out System.ReadOnlySpan<byte> payload))
            {
                switch ((SnapshotBlockKind)kind)
                {
                    case SnapshotBlockKind.Match:
                        SnapshotBlocks.TryReadMatchBlock(payload, out _, out _, out _, out _);
                        break;
                    case SnapshotBlockKind.Self:
                        SnapshotBlocks.TryReadSelfBlock(payload, SnapCfg, selfItems, out _, out _, out _);
                        break;
                    case SnapshotBlockKind.Pickups:
                        SnapshotBlocks.TryReadPickupsBlock(payload, SnapCfg, pickups, out _, out _);
                        break;
                    case SnapshotBlockKind.Containers:
                        SnapshotBlocks.TryReadContainersBlock(payload, SnapCfg, containers, out _, out _);
                        break;
                    case SnapshotBlockKind.ContainerSlots:
                        SnapshotBlocks.TryReadContainerSlotsBlock(payload, SnapCfg, slots, out _, out _);
                        break;
                }
            }
        }

        [Test]
        public void TruncatedNewBlockFrame_AtEveryLength_BlockPayloadsNeverThrow()
        {
            byte[] frame = BuildCanonicalNewBlockFrame();
            var knownKinds = new byte[]
            {
                (byte)SnapshotBlockKind.Match, (byte)SnapshotBlockKind.Self,
                (byte)SnapshotBlockKind.Pickups, (byte)SnapshotBlockKind.Containers,
                (byte)SnapshotBlockKind.ContainerSlots,
            };
            var pickups = new SnapshotBlocks.PickupRecord[8];
            var containers = new SnapshotBlocks.ContainerRecord[8];
            var slots = new SnapshotBlocks.ContainerSlotsRecord[8];
            var selfItems = new byte[8];

            for (int length = frame.Length; length >= 0; length--)
            {
                int cut = length;
                Assert.DoesNotThrow(
                    () => DecodeNewBlocks(frame, cut, pickups, containers, slots, selfItems, knownKinds),
                    $"length {cut}: no Task 25 block decoder may ever throw (Р82)");
            }
        }

        [Test]
        public void CorruptedNewBlockFrame_EveryByteFlipped_NeverThrows()
        {
            // The truncation sweep above cuts the frame; this one keeps its
            // LENGTH and lies about its CONTENT, which is the other half of
            // Р82 and the half that reaches the walker's mask, the kind
            // nibbles and the item ids. Every byte past the header is set to
            // two hostile values in turn, one at a time, so each failure is
            // attributable to one byte.
            byte[] pristine = BuildCanonicalNewBlockFrame();
            var knownKinds = new byte[]
            {
                (byte)SnapshotBlockKind.Match, (byte)SnapshotBlockKind.Self,
                (byte)SnapshotBlockKind.Pickups, (byte)SnapshotBlockKind.Containers,
                (byte)SnapshotBlockKind.ContainerSlots,
            };
            var pickups = new SnapshotBlocks.PickupRecord[8];
            var containers = new SnapshotBlocks.ContainerRecord[8];
            var slots = new SnapshotBlocks.ContainerSlotsRecord[8];
            var selfItems = new byte[8];
            var frame = new byte[pristine.Length];

            foreach (byte hostile in new byte[] { 0x00, 0xFF })
                for (int i = SnapshotWriter.HeaderBytes; i < pristine.Length; i++)
                {
                    System.Array.Copy(pristine, frame, pristine.Length);
                    frame[i] = hostile;
                    int index = i;
                    byte value = hostile;
                    Assert.DoesNotThrow(
                        () => DecodeNewBlocks(frame, frame.Length, pickups, containers, slots,
                            selfItems, knownKinds),
                        $"byte {index} set to 0x{value:X2}: no Task 25 block decoder may ever throw (Р82)");
                }
        }

        [Test]
        public void WriteThenReadAllNewBlocks_DoesNotAllocateGCMemory()
        {
            var items = new byte[] { SnapItemA, SnapItemB };
            var slotRecords = new[]
            {
                new SnapshotBlocks.ContainerSlotsRecord { Id = 4211, OccupancyMask = 0b11, ItemOffset = 0 },
            };
            var pickupRecords = new[] { PickupK1, PickupK2 };
            var containerRecords = new[] { ContainerC1, ContainerC2 };
            int size = SnapshotWriter.HeaderBytes
                       + SnapshotWriter.MatchBlockBytes()
                       + SnapshotWriter.SelfBlockBytes(items.Length)
                       + SnapshotWriter.PickupsBlockBytes(2)
                       + SnapshotWriter.ContainersBlockBytes(2)
                       + SnapshotWriter.ContainerSlotsBlockBytes(1, 2);
            var buffer = new byte[size];
            var knownKinds = new byte[]
            {
                (byte)SnapshotBlockKind.Match, (byte)SnapshotBlockKind.Self,
                (byte)SnapshotBlockKind.Pickups, (byte)SnapshotBlockKind.Containers,
                (byte)SnapshotBlockKind.ContainerSlots,
            };
            var pickups = new SnapshotBlocks.PickupRecord[4];
            var containers = new SnapshotBlocks.ContainerRecord[4];
            var slots = new SnapshotBlocks.ContainerSlotsRecord[4];
            var selfItems = new byte[8];

            // Warm-up OUTSIDE the measured lambda, plus the stub-defeating
            // premise: the measured body must really write and decode all
            // five, not fail fast on the first one.
            {
                var w = new SnapshotWriter(buffer);
                w.WriteHeader(Epoch, Tick, Flags);
                w.WriteMatchBlock(MatchFixturePhase, MatchFixtureSeconds, MatchFixtureFlags);
                w.WriteSelfBlock(4, items);
                w.WritePickupsBlock(pickupRecords, SnapCfg);
                w.WriteContainersBlock(containerRecords, SnapCfg);
                w.WriteContainerSlotsBlock(slotRecords, items);
                Assert.AreEqual(size, w.BytesWritten, "fixture premise (stub-defeating): the frame must be written");

                var r = new SnapshotReader(buffer);
                Assert.IsTrue(r.TryReadHeader(out _, out _, out _));
                int delivered = 0;
                while (r.TryReadBlock(knownKinds, out byte kind, out System.ReadOnlySpan<byte> payload))
                {
                    delivered++;
                    if ((SnapshotBlockKind)kind == SnapshotBlockKind.ContainerSlots)
                    {
                        Assert.IsTrue(SnapshotBlocks.TryReadContainerSlotsBlock(payload, SnapCfg, slots,
                            out int sc, out _));
                        Assert.AreEqual(1, sc, "fixture premise (stub-defeating): the slots record must decode");
                    }
                }
                Assert.AreEqual(5, delivered, "fixture premise (stub-defeating): all five blocks must be delivered");
                Assert.IsFalse(r.Failed);
            }

            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var w = new SnapshotWriter(buffer);
                    w.WriteHeader(Epoch, Tick, Flags);
                    w.WriteMatchBlock(MatchFixturePhase, MatchFixtureSeconds, MatchFixtureFlags);
                    w.WriteSelfBlock(4, items);
                    w.WritePickupsBlock(pickupRecords, SnapCfg);
                    w.WriteContainersBlock(containerRecords, SnapCfg);
                    w.WriteContainerSlotsBlock(slotRecords, items);
                    DecodeNewBlocks(buffer, buffer.Length, pickups, containers, slots, selfItems, knownKinds);
                }
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void NewFixedBlocks_RefuseEveryWrongLength_FromBothSides()
        {
            // Task 25 review, Important: the first refusal tests fed each
            // fixed-size block ONE wrong length — a payload SHORTER than its
            // shape — so "use < instead of !=" survived on every one of them.
            // The house form is the both-sides sweep the Liveness/Wave test
            // above already runs.
            bool ok = true;
            var error = SnapshotBlockError.None;

            foreach (int len in new[] { 0, 1, 3, 5, 8 })
            {
                var bad = new byte[len];
                Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadMatchBlock(
                    bad, out _, out _, out _, out error));
                Assert.IsFalse(ok, $"Match length {len} must be refused — its payload is exactly 4");
                Assert.AreEqual(SnapshotBlockError.MalformedLength, error, $"Match length {len}");
            }

            // Self is variable-length, so "wrong" means its own count byte
            // disagreeing with the payload — in EITHER direction. The first
            // version tested only "the count claims more than is here"; a
            // trailing-garbage payload (the count claims FEWER) is the other
            // half, and the same mutation eats it.
            var destination = new byte[8];
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadSelfBlock(
                new byte[] { 5, 1, SnapItemA, SnapItemB }, SnapCfg, destination, out _, out _, out error));
            Assert.IsFalse(ok, "a count of 1 with 2 ids present is a length that lies the other way");
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error);

            byte points = 0;
            int count = -1;
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadSelfBlock(
                new byte[] { 5, 0 }, SnapCfg, destination, out points, out count, out error));
            Assert.IsTrue(ok, "an EMPTY backpack is legal and must decode — the boundary from the legal side");
            Assert.AreEqual((byte)5, points);
            Assert.AreEqual(0, count);
            Assert.AreEqual(SnapshotBlockError.None, error);
        }

        [Test]
        public void SelfBlock_WithNoCatalogToCheckAgainst_AcceptsAnyItemId()
        {
            // Task 25 review, Important: the "empty catalog skips validation"
            // branch had no witness at all, so flipping it to "refuse
            // everything" was free. It is a real branch with a real reason —
            // a hand-built fixture may carry no catalog, and a decoder must
            // not invent a domain it was handed nothing to check against —
            // and the only place that reason can be checked is here.
            var noCatalog = new SimConfig
            {
                Arena = new ArenaSimConfig { Radius = SnapRadius, MaxPlayers = SnapMaxPlayers },
                Hero = new HeroSimConfig { MaxHp = SnapHeroMaxHp },
            };
            Assert.IsNull(noCatalog.Items, "fixture premise: this config really has no catalog");

            var destination = new byte[8];
            bool ok = false;
            int count = -1;
            var error = SnapshotBlockError.None;
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadSelfBlock(
                new byte[] { 3, 1, SnapItemNotInCatalog }, noCatalog, destination,
                out _, out count, out error));
            Assert.IsTrue(ok, "with no catalog there is no domain to refuse against");
            Assert.AreEqual(SnapshotBlockError.None, error);
            Assert.AreEqual(1, count);

            // …and the same id against a config that DOES have one is refused,
            // so this test cannot pass by the check being gone entirely.
            Assert.DoesNotThrow(() => ok = SnapshotBlocks.TryReadSelfBlock(
                new byte[] { 3, 1, SnapItemNotInCatalog }, SnapCfg, destination, out _, out _, out error));
            Assert.IsFalse(ok);
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
        }

        [Test]
        public void WriteSelfBlock_ThrowsWhenTheBackpackOutgrowsItsOwnCountByte()
        {
            // Task 25 review, Important: the guard had no test, so deleting it
            // silently wrapped `(byte)itemIds.Length` — 256 items would have
            // written a count of 0 and a payload of 256, i.e. a frame whose
            // own length field disagrees with its count, built by us.
            // Unreachable at Hero.MaxInventoryItems 16 and guarded anyway,
            // because this is the WRITE side: an argument the format cannot
            // carry is a caller bug.
            var buffer = new byte[512];
            var tooMany = new byte[256];
            for (int i = 0; i < tooMany.Length; i++) tooMany[i] = SnapItemA;

            var refused = Assert.Throws<System.ArgumentException>(() =>
            {
                var w = new SnapshotWriter(buffer);
                w.WriteHeader(Epoch, Tick, Flags);
                w.WriteSelfBlock(8, tooMany);
            });
            StringAssert.Contains("255", refused.Message, "the refusal must name the ceiling it hit");

            // 255 exactly is still legal, so the boundary is pinned from both
            // sides — `>` must not become `>=`.
            var atTheLimit = new byte[255];
            for (int i = 0; i < atTheLimit.Length; i++) atTheLimit[i] = SnapItemA;
            Assert.DoesNotThrow(() =>
            {
                var w = new SnapshotWriter(buffer);
                w.WriteHeader(Epoch, Tick, Flags);
                w.WriteSelfBlock(8, atTheLimit);
            });
        }

        [Test]
        public void BlockCalculators_ArePinned_ForTheFiveNewBlocks()
        {
            Assert.AreEqual(3 + 4, SnapshotWriter.MatchBlockBytes(), "3 + 4");
            Assert.AreEqual(3 + 2 + 0, SnapshotWriter.SelfBlockBytes(0), "3 + 2 head + no ids");
            Assert.AreEqual(3 + 2 + 16, SnapshotWriter.SelfBlockBytes(16),
                "3 + 2 head + 16 ids — Hero.MaxInventoryItems is the widest a backpack can be");
            Assert.AreEqual(3 + 5 * 7, SnapshotWriter.PickupsBlockBytes(5));
            Assert.AreEqual(3 + 5 * 7, SnapshotWriter.ContainersBlockBytes(5));
            Assert.AreEqual(3 + 2 * 3 + 5, SnapshotWriter.ContainerSlotsBlockBytes(2, 5),
                "3 + two 3-byte heads + five ids between them");
            Assert.AreEqual(3 + 2, SnapshotWriter.LivenessBlockBytes(),
                "the Liveness block grew a byte in Task 25 and its calculator has to know");
        }

        [Test]
        public void WriteSide_ThrowsOnValuesOutsideTheirWireDomain()
        {
            // The mirror of the read side's MalformedContent: a CALLER that
            // hands the writer a value the nibble cannot carry has a bug of
            // its own, and Task 27's own WriteMobsBlock throws for exactly
            // this reason (see SnapshotWriter's class doc on the asymmetry).
            var buffer = new byte[64];

            Assert.Throws<System.ArgumentException>(() =>
            {
                var w = new SnapshotWriter(buffer);
                w.WriteHeader(Epoch, Tick, Flags);
                w.WriteContainersBlock(
                    new[] { new SnapshotBlocks.ContainerRecord { Id = 1, Kind = (ContainerKind)9 } }, SnapCfg);
            });

            Assert.Throws<System.ArgumentException>(() =>
            {
                var w = new SnapshotWriter(buffer);
                w.WriteHeader(Epoch, Tick, Flags);
                w.WritePickupsBlock(
                    new[] { new SnapshotBlocks.PickupRecord { Id = 1, Kind = (PickupKind)3 } }, SnapCfg);
            });

            Assert.Throws<System.ArgumentException>(() =>
            {
                var w = new SnapshotWriter(buffer);
                w.WriteHeader(Epoch, Tick, Flags);
                w.WriteMatchBlock((MatchPhase)9, MatchFixtureSeconds, MatchFixtureFlags);
            });
        }

        [Test]
        public void AllFiveNewBlocks_RideOneFrame_AndComeBackInOrder()
        {
            var knownKinds = new byte[]
            {
                (byte)SnapshotBlockKind.Match, (byte)SnapshotBlockKind.Self,
                (byte)SnapshotBlockKind.Pickups, (byte)SnapshotBlockKind.Containers,
                (byte)SnapshotBlockKind.ContainerSlots,
            };
            var items = new byte[] { SnapItemA, SnapItemB };
            var slotRecords = new[]
            {
                new SnapshotBlocks.ContainerSlotsRecord { Id = 4211, OccupancyMask = 0b11, ItemOffset = 0 },
            };

            int size = SnapshotWriter.HeaderBytes
                       + SnapshotWriter.MatchBlockBytes()
                       + SnapshotWriter.SelfBlockBytes(items.Length)
                       + SnapshotWriter.PickupsBlockBytes(2)
                       + SnapshotWriter.ContainersBlockBytes(2)
                       + SnapshotWriter.ContainerSlotsBlockBytes(1, 2);
            var buffer = new byte[size];
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, Flags);
            writer.WriteMatchBlock(MatchFixturePhase, MatchFixtureSeconds, MatchFixtureFlags);
            writer.WriteSelfBlock(4, items);
            writer.WritePickupsBlock(new[] { PickupK1, PickupK2 }, SnapCfg);
            writer.WriteContainersBlock(new[] { ContainerC1, ContainerC2 }, SnapCfg);
            writer.WriteContainerSlotsBlock(slotRecords, items);
            Assert.AreEqual(size, writer.BytesWritten, "the five calculators must add up to what was written");

            var reader = new SnapshotReader(buffer);
            Assert.IsTrue(reader.TryReadHeader(out ushort epoch, out uint tick, out byte flags));
            Assert.AreEqual(Epoch, epoch);
            Assert.AreEqual(Tick, tick);
            Assert.AreEqual(Flags, flags);

            Assert.IsTrue(reader.TryReadBlock(knownKinds, out byte k1, out System.ReadOnlySpan<byte> p1));
            Assert.AreEqual((byte)SnapshotBlockKind.Match, k1);
            Assert.IsTrue(SnapshotBlocks.TryReadMatchBlock(p1, out MatchPhase phase, out ushort seconds,
                out byte matchFlags, out _));
            Assert.AreEqual(MatchFixturePhase, phase);
            Assert.AreEqual(MatchFixtureSeconds, seconds);
            Assert.AreEqual(MatchFixtureFlags, matchFlags);

            Assert.IsTrue(reader.TryReadBlock(knownKinds, out byte k2, out System.ReadOnlySpan<byte> p2));
            Assert.AreEqual((byte)SnapshotBlockKind.Self, k2);
            var selfItems = new byte[8];
            Assert.IsTrue(SnapshotBlocks.TryReadSelfBlock(p2, SnapCfg, selfItems, out byte points,
                out int itemCount, out _));
            Assert.AreEqual((byte)4, points);
            Assert.AreEqual(2, itemCount);

            Assert.IsTrue(reader.TryReadBlock(knownKinds, out byte k3, out System.ReadOnlySpan<byte> p3));
            Assert.AreEqual((byte)SnapshotBlockKind.Pickups, k3);
            var pickups = new SnapshotBlocks.PickupRecord[4];
            Assert.IsTrue(SnapshotBlocks.TryReadPickupsBlock(p3, SnapCfg, pickups, out int pickupCount, out _));
            Assert.AreEqual(2, pickupCount);

            Assert.IsTrue(reader.TryReadBlock(knownKinds, out byte k4, out System.ReadOnlySpan<byte> p4));
            Assert.AreEqual((byte)SnapshotBlockKind.Containers, k4);
            var containers = new SnapshotBlocks.ContainerRecord[4];
            Assert.IsTrue(SnapshotBlocks.TryReadContainersBlock(p4, SnapCfg, containers, out int containerCount, out _));
            Assert.AreEqual(2, containerCount);

            Assert.IsTrue(reader.TryReadBlock(knownKinds, out byte k5, out System.ReadOnlySpan<byte> p5));
            Assert.AreEqual((byte)SnapshotBlockKind.ContainerSlots, k5);
            var slots = new SnapshotBlocks.ContainerSlotsRecord[4];
            Assert.IsTrue(SnapshotBlocks.TryReadContainerSlotsBlock(p5, SnapCfg, slots, out int slotCount, out _));
            Assert.AreEqual(1, slotCount);

            Assert.IsFalse(reader.TryReadBlock(knownKinds, out _, out _), "the frame is exhausted");
            Assert.IsFalse(reader.Failed, "and exhausted cleanly — no refusal, no truncation");
            Assert.AreEqual(0, reader.SkippedBlockCount);
        }
    }
}
