using FishNet.Broadcast;
using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;
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

            // Literal 1, not ProtocolVersion.Current: comparing the writer
            // against the very constant it wrote would pass under a version
            // bump that silently broke every peer.
            Assert.AreEqual((byte)1, buffer[0], "byte 0: protocol version");
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
        public void ProtocolVersion_Current_IsPinnedToOne()
        {
            // A silent bump would part client and server with no red test
            // anywhere: the version is compared in the handshake (Task 39)
            // and on every snapshot, and both sides read the same constant.
            Assert.AreEqual((byte)1, ProtocolVersion.Current,
                "protocol version 1 is the wire contract of Stage 2 — changing it is a "
                + "compatibility break that must be a deliberate, reviewed edit");
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
            // undefined behaviour in a parser of untrusted bytes: without it
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
        // A handful of small integers DO turn up elsewhere in those files
        // (e.g. "3", "6", "8" appear as unrelated tuning numbers in several
        // configs) — searched and accepted, not filtered out by eye: they
        // are single-digit counters (tickDelta, aliveCount) for which no
        // collision-free choice exists across ten balance files, and none of
        // them equals the SAME-DOMAIN balance number a mutation could
        // plausibly confuse them with. Some grep hits are pure noise from
        // the pattern matching inside a GUID hex string (e.g. "19" and "23"
        // inside `m_Script: {..., guid: ...}` lines) — not a real value
        // collision, the same class of false positive this file's Task 26
        // header note already documents for "14 and 22." Structural numbers
        // (byte offsets, record sizes, sentinel tails) are not fixtures,
        // same rule as the rest of this file.
        const float SnapRadius = 52f;
        const float SnapHeroMaxHp = 118f;
        const float SnapChaserMaxHp = 47f;
        const float SnapGunnerMaxHp = 33f;

        static readonly SimConfig SnapCfg = new SimConfig
        {
            Arena = new ArenaSimConfig { Radius = SnapRadius },
            Hero = new HeroSimConfig { MaxHp = SnapHeroMaxHp },
            Chaser = new MobSimConfig { MaxHp = SnapChaserMaxHp },
            Gunner = new MobSimConfig { MaxHp = SnapGunnerMaxHp },
        };

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

        // Event fixtures — synthetic kinds/payloads, per task-27-brief §2.5
        // Task 27 draws no catalogue. E1 pos (17,-49) -> posX 43480
        // (0xD8,0xA9), posY 1890 (0x62,0x07). E2 pos (-33,26) -> posX 11973
        // (0xC5,0x2E), posY 49151 (0xFF,0xBF), zero payload — the "0 B"
        // boundary from task-27-brief §3 item 7.
        static readonly byte[] EventPayloadPool = { 0x5A, 0x7E, 0x91 };
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
            writer.WriteLivenessBlock(LivenessFixtureMask);
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
        }

        [Test]
        public void RecordSizeConstants_ArePinned()
        {
            Assert.AreEqual(8, SnapshotBlocks.PlayerRecordBytes);
            Assert.AreEqual(9, SnapshotBlocks.MobRecordBytes);
            Assert.AreEqual(9, SnapshotBlocks.EventHeaderBytes);
            Assert.AreEqual(1, SnapshotBlocks.LivenessBlockPayloadBytes);
            Assert.AreEqual(4, SnapshotBlocks.WaveBlockPayloadBytes);
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
            writer.WriteLivenessBlock(LivenessFixtureMask);

            int b = SnapshotWriter.HeaderBytes;
            Assert.AreEqual((byte)SnapshotBlockKind.Liveness, buffer[b], "block byte 0: kind");
            Assert.AreEqual((byte)1, buffer[b + 1], "block byte 1: payloadBytes low = 1");
            Assert.AreEqual((byte)0, buffer[b + 2], "block byte 2: payloadBytes high");
            Assert.AreEqual((byte)0b101, buffer[b + SnapshotWriter.BlockHeaderBytes], "mask byte, literal 0b101");

            Assert.IsTrue((LivenessFixtureMask & (1 << 0)) != 0, "player 0 alive");
            Assert.IsFalse((LivenessFixtureMask & (1 << 1)) != 0, "player 1 dead");
            Assert.IsTrue((LivenessFixtureMask & (1 << 2)) != 0, "player 2 alive");

            Assert.IsTrue(SnapshotBlocks.TryReadLivenessBlock(
                new System.ReadOnlySpan<byte>(buffer, b + SnapshotWriter.BlockHeaderBytes, 1),
                out byte decodedMask, out SnapshotBlockError error));
            Assert.AreEqual(LivenessFixtureMask, decodedMask);
            Assert.AreEqual(SnapshotBlockError.None, error);
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
            Assert.AreEqual((byte)0x5A, buffer[r0 + 9], "record 1 payload byte 0");
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
            Assert.IsTrue(SnapshotBlocks.TryReadLivenessBlock(payload2, out byte aliveMask, out SnapshotBlockError liveErr));
            Assert.AreEqual(LivenessFixtureMask, aliveMask);
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
            // Tolerances computed HERE, independently of Quantize
            // (task-27-brief §3 item 9) — comparing against Quantize's own
            // half-step would compare two constants against each other and
            // prove nothing (the exact defect Task 24 F1/F2 named).
            float halfStepPos = SnapRadius / 65535f;
            float halfStepHeroHp = SnapHeroMaxHp / 255f / 2f;
            float halfStepChaserHp = SnapChaserMaxHp / 255f / 2f;
            float halfStepDirDeg = 360f / 256f / 2f;

            byte[] frame = BuildCanonicalFiveBlockFrame();
            var reader = new SnapshotReader(frame);
            reader.TryReadHeader(out _, out _, out _);

            reader.TryReadBlock(AllBlockKinds, out _, out System.ReadOnlySpan<byte> playersPayload);
            var playerDest = new SnapshotBlocks.PlayerRecord[4];
            SnapshotBlocks.TryReadPlayersBlock(playersPayload, SnapCfg, playerDest, out _, out _);
            Assert.That(playerDest[0].Pos.x, Is.EqualTo(PlayerP1.Pos.x).Within(halfStepPos + 1e-3f));
            Assert.That(playerDest[0].Pos.y, Is.EqualTo(PlayerP1.Pos.y).Within(halfStepPos + 1e-3f));
            Assert.That(playerDest[0].Hp, Is.EqualTo(PlayerP1.Hp).Within(halfStepHeroHp + 1e-3f));
            float dirErrDeg = AngularDifferenceDegrees(playerDest[0].Dir, PlayerP1.Dir);
            Assert.That(dirErrDeg, Is.LessThanOrEqualTo(halfStepDirDeg + 0.05f));

            reader.TryReadBlock(AllBlockKinds, out _, out _); // liveness, not under test here
            reader.TryReadBlock(AllBlockKinds, out _, out System.ReadOnlySpan<byte> mobsPayload);
            var mobDest = new SnapshotBlocks.MobRecord[4];
            SnapshotBlocks.TryReadMobsBlock(mobsPayload, SnapCfg, mobDest, out _, out _);
            Assert.That(mobDest[0].Pos.x, Is.EqualTo(MobM1.Pos.x).Within(halfStepPos + 1e-3f));
            Assert.That(mobDest[0].Pos.y, Is.EqualTo(MobM1.Pos.y).Within(halfStepPos + 1e-3f));
            Assert.That(mobDest[0].Hp, Is.EqualTo(MobM1.Hp).Within(halfStepChaserHp + 1e-3f));
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
            foreach (int len in new[] { 0, 2, 3, 5 })
            {
                var badLiveness = new byte[len];
                bool okL = true;
                SnapshotBlockError errL = SnapshotBlockError.None;
                Assert.DoesNotThrow(() => okL = SnapshotBlocks.TryReadLivenessBlock(badLiveness, out _, out errL));
                Assert.IsFalse(okL, $"Liveness length {len} must be refused");
                Assert.AreEqual(SnapshotBlockError.MalformedLength, errL, $"Liveness length {len}");

                var badWave = new byte[len];
                bool okW = true;
                SnapshotBlockError errW = SnapshotBlockError.None;
                Assert.DoesNotThrow(() => okW = SnapshotBlocks.TryReadWaveBlock(badWave, out _, out _, out _, out errW));
                Assert.IsFalse(okW, $"Wave length {len} must be refused");
                Assert.AreEqual(SnapshotBlockError.MalformedLength, errW, $"Wave length {len}");
            }
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
                                SnapshotBlocks.TryReadLivenessBlock(payload, out _, out _);
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
                w.WriteLivenessBlock(LivenessFixtureMask);
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
                    w.WriteLivenessBlock(LivenessFixtureMask);
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
                                SnapshotBlocks.TryReadLivenessBlock(payload, out _, out _);
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
    }
}
