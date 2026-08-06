using FishNet.Broadcast;
using NUnit.Framework;
using Ring.Networking.Protocol;
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
    }
}
