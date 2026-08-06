using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below
// are required by the file, not just convenience imports (QuantizeTests.cs and
// AllocationTests.cs carry the same pair for the same reason).
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    // Stage 2 Task 25 (spec §3.8, Р30/Р34/Р84): InputCodec packs the four
    // SimInput fields that actually cross the wire (MoveDir, AimPoint,
    // AimHeight, four flags — recounted by grep against SimInput.cs, not
    // quoted from the brief, per task-25-brief §2's own warning) into 8
    // bytes and back, using Quantize (Task 24) for every scalar mapping.
    // Task 30's prediction-parity test depends on Decode(Encode(...))
    // reproducing the exact value the client is about to send (Р34), so the
    // contracts below are exercised directly, not assumed from Quantize's
    // own (already-tested) idempotency.
    public class InputCodecTests
    {
        // Fixture configs deliberately avoid every number tracked by an
        // .asset: ArenaConfig.asset's Radius is 65, HeroConfig.asset's
        // MaxAimHeight is 3.8 (T24 fix-round finding F7 hit exactly this
        // trap with 3.8f). Two DIFFERENT non-default pairs, not one, so a
        // mutation hardcoding either real number fails on at least one of
        // them (task-25-brief §3 item 9, урок 93) — see
        // DifferentConfigs_ReadRadiusAndMaxAimHeightFromCfg_NotHardcoded.
        const float RadiusA = 40f;
        const float HeightA = 4.25f;
        const float RadiusB = 15f;
        const float HeightB = 2f;

        static readonly SimConfig ConfigA = new SimConfig
        {
            Arena = new ArenaSimConfig { Radius = RadiusA },
            Hero = new HeroSimConfig { MaxAimHeight = HeightA }
        };

        static readonly SimConfig ConfigB = new SimConfig
        {
            Arena = new ArenaSimConfig { Radius = RadiusB },
            Hero = new HeroSimConfig { MaxAimHeight = HeightB }
        };

        static SimInput RoundTrip(in SimInput input, in SimConfig cfg)
        {
            System.Span<byte> buf = stackalloc byte[InputCodec.SizeBytes];
            InputCodec.Encode(input, cfg, buf);
            return InputCodec.Decode(buf, cfg);
        }

        /// Wrap-aware angular distance in degrees between two DIRECTION
        /// vectors — mirrors QuantizeTests.AngularDifferenceDegrees (a
        /// private helper of that class, unreachable from here without
        /// editing a file task-25-brief §1 forbids touching), duplicated as
        /// a small test-only helper rather than shared.
        static float AngularDifferenceDegrees(float2 a, float2 b)
        {
            float cross = a.x * b.y - a.y * b.x;
            float dot = a.x * b.x + a.y * b.y;
            return math.abs(math.degrees(math.atan2(cross, dot)));
        }

        // ---- 1. Round-trip each field separately ----

        [Test]
        public void MoveDir_RoundTrip_ArbitraryDeflection()
        {
            var input = new SimInput { MoveDir = new float2(0.6f, -0.8f) }; // magnitude 1, off-axis
            SimInput decoded = RoundTrip(input, ConfigA);

            float originalLen = math.length(input.MoveDir);
            float decodedLen = math.length(decoded.MoveDir);
            float lenHalfStep = 1f / 255f / 2f;
            Assert.That(decodedLen, Is.EqualTo(originalLen).Within(lenHalfStep + 1e-4f),
                "MoveDir magnitude must round-trip within Unit's half-step");

            float angleErrorDeg = AngularDifferenceDegrees(input.MoveDir, decoded.MoveDir);
            float angleHalfStepDeg = 360f / 256f / 2f;
            Assert.That(angleErrorDeg, Is.LessThanOrEqualTo(angleHalfStepDeg + 0.05f),
                "MoveDir angle must round-trip within Dir's half-step");
        }

        [Test]
        public void AimPoint_RoundTrip_ArenaAbsoluteCoordinates()
        {
            // x != y (catches an x/y swap) and neither axis is zero.
            var input = new SimInput { AimPoint = new float2(23f, -37f) };
            SimInput decoded = RoundTrip(input, ConfigA);

            float halfStep = (3f * RadiusA) / 65535f;
            Assert.That(decoded.AimPoint.x, Is.EqualTo(input.AimPoint.x).Within(halfStep + 1e-3f),
                "AimPoint.x must round-trip within Aim's half-step");
            Assert.That(decoded.AimPoint.y, Is.EqualTo(input.AimPoint.y).Within(halfStep + 1e-3f),
                "AimPoint.y must round-trip within Aim's half-step");
        }

        [Test]
        public void AimHeight_RoundTrip_WithinTwoCentimeters()
        {
            // task-25-brief §3 item 5: round-trip error <= 2 cm at the
            // fixture's MaxAimHeight. ConfigA's MaxAimHeight (4.25 m, not
            // tracked by any .asset) gives a step of 4.25/255 =~ 1.67 cm,
            // under the 2 cm bound with margin — same shape as the brief's
            // own worked example (3.8 m/255 =~ 1.5 cm) but off the real
            // HeroConfig.asset number.
            var input = new SimInput { AimHeight = 2.73f };
            SimInput decoded = RoundTrip(input, ConfigA);

            Assert.That(math.abs(decoded.AimHeight - input.AimHeight), Is.LessThanOrEqualTo(0.02f),
                "AimHeight round-trip error must be <= 2 cm (task-25-brief §3 item 5)");
        }

        [Test]
        public void Flags_RoundTrip_EachIndividually()
        {
            var baseline = new SimInput
            {
                MoveDir = new float2(0.3f, -0.4f),
                AimPoint = new float2(11f, -7f),
                AimHeight = 1.5f
            };

            SimInput fireOnly = baseline; fireOnly.FireHeld = true;
            SimInput d = RoundTrip(fireOnly, ConfigA);
            Assert.IsTrue(d.FireHeld, "FireHeld alone must decode true");
            Assert.IsFalse(d.DashRequested); Assert.IsFalse(d.AimHeld); Assert.IsFalse(d.SlideRequested);

            SimInput dashOnly = baseline; dashOnly.DashRequested = true;
            d = RoundTrip(dashOnly, ConfigA);
            Assert.IsFalse(d.FireHeld); Assert.IsTrue(d.DashRequested, "DashRequested alone must decode true");
            Assert.IsFalse(d.AimHeld); Assert.IsFalse(d.SlideRequested);

            SimInput aimOnly = baseline; aimOnly.AimHeld = true;
            d = RoundTrip(aimOnly, ConfigA);
            Assert.IsFalse(d.FireHeld); Assert.IsFalse(d.DashRequested);
            Assert.IsTrue(d.AimHeld, "AimHeld alone must decode true"); Assert.IsFalse(d.SlideRequested);

            SimInput slideOnly = baseline; slideOnly.SlideRequested = true;
            d = RoundTrip(slideOnly, ConfigA);
            Assert.IsFalse(d.FireHeld); Assert.IsFalse(d.DashRequested); Assert.IsFalse(d.AimHeld);
            Assert.IsTrue(d.SlideRequested, "SlideRequested alone must decode true");
        }

        // ---- 2. Partial stick deflection is preserved, not clamped to 1 ----

        [Test]
        public void MoveDir_PartialDeflection_NotClampedToFullSpeed()
        {
            // SimInputSanitizer.Sanitize only normalizes when |MoveDir| > 1
            // (SimInputSanitizer.cs), so an analog stick at half deflection
            // must survive the codec as ~0.5, not be promoted to 1.
            var input = new SimInput { MoveDir = new float2(0.5f, 0f) };
            SimInput decoded = RoundTrip(input, ConfigA);

            float decodedLen = math.length(decoded.MoveDir);
            float halfStep = 1f / 255f / 2f;
            Assert.That(decodedLen, Is.EqualTo(0.5f).Within(halfStep + 1e-4f),
                "half deflection must decode back near 0.5 (allowing only Unit's own step error)");
            Assert.Less(decodedLen, 0.9f,
                "half deflection must NOT be promoted toward full speed — magnitude must survive the codec");
        }

        // ---- 3. Idempotency (Р34): second pass gives same bytes AND same decoded value ----

        [Test]
        public void Idempotency_SecondPass_SameBytesAndSameDecodedValue()
        {
            var original = new SimInput
            {
                MoveDir = new float2(0.42f, -0.31f),
                AimPoint = new float2(19f, -53f),
                AimHeight = 3.05f,
                FireHeld = true,
                DashRequested = false,
                AimHeld = true,
                SlideRequested = false
            };

            System.Span<byte> buf1 = stackalloc byte[InputCodec.SizeBytes];
            InputCodec.Encode(original, ConfigA, buf1);
            SimInput decoded1 = InputCodec.Decode(buf1, ConfigA);

            // STRENGTHENING (task-25-brief §4): a constant stub (Encode
            // always writes zero bytes ignoring `input`, Decode always
            // returns `default` ignoring `src`) passes the byte-for-byte
            // and decoded-for-decoded checks below TRIVIALLY — encode2
            // would be the same constant zero bytes as encode1 regardless
            // of what decoded1 holds, and decode2 would be the same
            // `default` as decoded1. This assertion block, run BEFORE the
            // pure-idempotency checks, forces decoded1 to actually reflect
            // `original` (nonzero on every field) — a stub fails HERE, not
            // just further down.
            float halfStepAim = (3f * RadiusA) / 65535f;
            float halfStepUnit = 1f / 255f / 2f;
            Assert.That(math.length(decoded1.MoveDir), Is.EqualTo(math.length(original.MoveDir)).Within(halfStepUnit + 1e-4f),
                "fixture premise (stub-defeating): decoded1's MoveDir magnitude must reflect the original input");
            Assert.That(decoded1.AimPoint.x, Is.EqualTo(original.AimPoint.x).Within(halfStepAim + 1e-3f),
                "fixture premise (stub-defeating): decoded1.AimPoint.x must reflect the original input");
            Assert.AreEqual(original.FireHeld, decoded1.FireHeld, "fixture premise (stub-defeating): flags must reflect the original input");
            Assert.AreEqual(original.AimHeld, decoded1.AimHeld);

            // Pure idempotency (Р34): re-encoding the DECODED value must
            // reproduce byte-identical wire data and therefore a
            // byte-identical (hence value-identical) second decode. This is
            // the property Task 30 relies on: the client predicts from
            // Decode(Encode(raw)), and that value must be stable under a
            // second pass through the same codec.
            System.Span<byte> buf2 = stackalloc byte[InputCodec.SizeBytes];
            InputCodec.Encode(decoded1, ConfigA, buf2);
            SimInput decoded2 = InputCodec.Decode(buf2, ConfigA);

            for (int i = 0; i < InputCodec.SizeBytes; i++)
                Assert.AreEqual(buf1[i], buf2[i], $"byte {i} must be identical on the second pass (Р34)");

            Assert.AreEqual(decoded1.MoveDir.x, decoded2.MoveDir.x, 0f, "MoveDir.x must be bit-identical on the second decode");
            Assert.AreEqual(decoded1.MoveDir.y, decoded2.MoveDir.y, 0f, "MoveDir.y must be bit-identical on the second decode");
            Assert.AreEqual(decoded1.AimPoint.x, decoded2.AimPoint.x, 0f, "AimPoint.x must be bit-identical on the second decode");
            Assert.AreEqual(decoded1.AimPoint.y, decoded2.AimPoint.y, 0f, "AimPoint.y must be bit-identical on the second decode");
            Assert.AreEqual(decoded1.AimHeight, decoded2.AimHeight, 0f, "AimHeight must be bit-identical on the second decode");
            Assert.AreEqual(decoded1.FireHeld, decoded2.FireHeld);
            Assert.AreEqual(decoded1.DashRequested, decoded2.DashRequested);
            Assert.AreEqual(decoded1.AimHeld, decoded2.AimHeld);
            Assert.AreEqual(decoded1.SlideRequested, decoded2.SlideRequested);
        }

        // ---- 4. AimPoint at Sanitize's clamp boundary must not sit on the rail ----

        [Test]
        public void AimPoint_NearSanitizeClampBoundary_DoesNotSitOnRail()
        {
            // task-25-brief §3 item 4 / Р30: Sanitize allows AimPoint up to
            // 2*Radius from the PLAYER, i.e. up to 3*Radius from the arena
            // CENTRE — Aim's own domain (Quantize.Aim's doc). 2.5*RadiusA
            // and 1.2*RadiusA are both OUTSIDE +-RadiusA (Pos's domain) but
            // well INSIDE +-3*RadiusA (Aim's domain): a codec that
            // mistakenly used Quantize.Pos here (mutation, task-25-brief §4)
            // would clamp both components onto the top rail.
            var input = new SimInput { AimPoint = new float2(2.5f * RadiusA, 1.2f * RadiusA) };
            System.Span<byte> buf = stackalloc byte[InputCodec.SizeBytes];
            InputCodec.Encode(input, ConfigA, buf);

            ushort codeX = (ushort)(buf[2] | (buf[3] << 8));
            ushort codeY = (ushort)(buf[4] | (buf[5] << 8));
            Assert.AreNotEqual(ushort.MaxValue, codeX, "AimPoint.x inside the 3*Radius domain must not sit on the top rail (Р30)");
            Assert.AreNotEqual(ushort.MaxValue, codeY, "AimPoint.y inside the 3*Radius domain must not sit on the top rail (Р30)");

            SimInput decoded = InputCodec.Decode(buf, ConfigA);
            float halfStep = (3f * RadiusA) / 65535f;
            Assert.That(decoded.AimPoint.x, Is.EqualTo(input.AimPoint.x).Within(halfStep + 1e-3f));
            Assert.That(decoded.AimPoint.y, Is.EqualTo(input.AimPoint.y).Within(halfStep + 1e-3f));
        }

        // ---- 5. Every flag combination round-trips exactly ----

        [Test]
        public void FlagCombinations_AllSixteen_RoundTripExactly()
        {
            // task-25-brief §3 item 6: a mutation swapping DashRequested's
            // and SlideRequested's bits only shows up when the two differ —
            // all sixteen combinations of the four flags are required, not
            // a handful of hand-picked ones.
            var baseline = new SimInput
            {
                MoveDir = new float2(0.3f, -0.4f),
                AimPoint = new float2(11f, -7f),
                AimHeight = 1.5f
            };

            for (int mask = 0; mask < 16; mask++)
            {
                SimInput input = baseline;
                input.FireHeld = (mask & 1) != 0;
                input.DashRequested = (mask & 2) != 0;
                input.AimHeld = (mask & 4) != 0;
                input.SlideRequested = (mask & 8) != 0;

                SimInput decoded = RoundTrip(input, ConfigA);

                Assert.AreEqual(input.FireHeld, decoded.FireHeld, $"mask {mask}: FireHeld");
                Assert.AreEqual(input.DashRequested, decoded.DashRequested, $"mask {mask}: DashRequested");
                Assert.AreEqual(input.AimHeld, decoded.AimHeld, $"mask {mask}: AimHeld");
                Assert.AreEqual(input.SlideRequested, decoded.SlideRequested, $"mask {mask}: SlideRequested");
            }
        }

        // ---- 6. Zero MoveDir decodes to EXACT zero ----

        [Test]
        public void MoveDir_Zero_DecodesToExactZero_NotUnitVectorTimesZero()
        {
            // task-25-brief §2 item 2 (decision): Quantize.Dir(float2.zero)
            // encodes the same structural code as +X (Quantize.cs's own
            // doc) — nothing here may read that angle back as if it meant
            // something. Decode must return EXACTLY float2.zero; the only
            // thing that guarantees this is the magnitude multiplication
            // itself (DirBack(code) * 0f == (0f, 0f) for any finite
            // DirBack), pinned here rather than assumed.
            var input = new SimInput { MoveDir = float2.zero };
            SimInput decoded = RoundTrip(input, ConfigA);

            Assert.AreEqual(0f, decoded.MoveDir.x, "zero MoveDir must decode to EXACTLY 0 on X, not a near-zero remainder");
            Assert.AreEqual(0f, decoded.MoveDir.y, "zero MoveDir must decode to EXACTLY 0 on Y, not a near-zero remainder");
        }

        // ---- 7. Non-default Radius/MaxAimHeight are actually read from cfg ----

        [Test]
        public void DifferentConfigs_ReadRadiusAndMaxAimHeightFromCfg_NotHardcoded()
        {
            // task-25-brief §3 item 9 (урок 93): a parameter no test varies
            // is untested. ConfigA/ConfigB use two DIFFERENT non-default
            // Radius/MaxAimHeight pairs, so a mutation hardcoding either
            // real number (e.g. "65 instead of cfg.Arena.Radius") fails on
            // at least one of the two configs below.
            var input = new SimInput { AimPoint = new float2(11f, -6f), AimHeight = 1.5f };
            System.Span<byte> bufA = stackalloc byte[InputCodec.SizeBytes];
            System.Span<byte> bufB = stackalloc byte[InputCodec.SizeBytes];
            InputCodec.Encode(input, ConfigA, bufA);
            InputCodec.Encode(input, ConfigB, bufB);

            bool aimXDiffers = bufA[2] != bufB[2] || bufA[3] != bufB[3];
            bool aimYDiffers = bufA[4] != bufB[4] || bufA[5] != bufB[5];
            Assert.IsTrue(aimXDiffers, "AimPoint.x wire code must depend on cfg.Arena.Radius");
            Assert.IsTrue(aimYDiffers, "AimPoint.y wire code must depend on cfg.Arena.Radius");
            Assert.AreNotEqual(bufA[6], bufB[6], "AimHeight wire code must depend on cfg.Hero.MaxAimHeight");

            SimInput decodedA = InputCodec.Decode(bufA, ConfigA);
            SimInput decodedB = InputCodec.Decode(bufB, ConfigB);
            float aimHalfStepA = (3f * RadiusA) / 65535f;
            float aimHalfStepB = (3f * RadiusB) / 65535f;
            Assert.That(decodedA.AimPoint.x, Is.EqualTo(input.AimPoint.x).Within(aimHalfStepA + 1e-3f));
            Assert.That(decodedB.AimPoint.x, Is.EqualTo(input.AimPoint.x).Within(aimHalfStepB + 1e-3f));

            float heightHalfStepA = HeightA / 255f / 2f;
            float heightHalfStepB = HeightB / 255f / 2f;
            Assert.That(decodedA.AimHeight, Is.EqualTo(input.AimHeight).Within(heightHalfStepA + 1e-4f));
            Assert.That(decodedB.AimHeight, Is.EqualTo(input.AimHeight).Within(heightHalfStepB + 1e-4f));
        }

        // ---- 8. Exact wire layout: offsets, little-endian, flag bits (size + tail included) ----

        [Test]
        public void Encode_MatchesDocumentedByteLayout_LittleEndian()
        {
            // Single "known vector" test that pins the layout documented on
            // InputCodec itself (offsets, byte order, flag bits) against
            // INDEPENDENTLY computed expected bytes — not against Decode.
            // This is deliberate: a big-endian mutation applied
            // symmetrically to one field's Encode AND Decode would still
            // round-trip correctly through Decode(Encode(...)), so no
            // round-trip test above can catch it (task-25-brief §4). Only a
            // raw-byte comparison against a manually computed expectation
            // can.
            var input = new SimInput
            {
                MoveDir = new float2(0.6f, 0.8f),
                AimPoint = new float2(37f, -91f), // x != y, catches a swap too
                AimHeight = 3.1f,
                FireHeld = true,
                DashRequested = false,
                AimHeld = false,
                SlideRequested = true
            };

            byte expectedAngle = Quantize.Dir(input.MoveDir);
            byte expectedMag = Quantize.Unit(math.length(input.MoveDir), 1f);
            ushort expectedAimX = Quantize.Aim(input.AimPoint.x, ConfigA.Arena.Radius);
            ushort expectedAimY = Quantize.Aim(input.AimPoint.y, ConfigA.Arena.Radius);
            byte expectedHeight = Quantize.Unit(input.AimHeight, ConfigA.Hero.MaxAimHeight);
            const byte expectedFlags = 0b0000_1001; // bit0 FireHeld + bit3 SlideRequested

            var dst = new byte[InputCodec.SizeBytes + 4];
            for (int i = 0; i < dst.Length; i++) dst[i] = 0xAA; // sentinel tail
            InputCodec.Encode(input, ConfigA, dst);

            Assert.AreEqual(expectedAngle, dst[0], "byte 0: MoveDir angle");
            Assert.AreEqual(expectedMag, dst[1], "byte 1: MoveDir magnitude");
            Assert.AreEqual((byte)(expectedAimX & 0xFF), dst[2], "byte 2: AimPoint.x low byte (little-endian)");
            Assert.AreEqual((byte)(expectedAimX >> 8), dst[3], "byte 3: AimPoint.x high byte (little-endian)");
            Assert.AreEqual((byte)(expectedAimY & 0xFF), dst[4], "byte 4: AimPoint.y low byte (little-endian)");
            Assert.AreEqual((byte)(expectedAimY >> 8), dst[5], "byte 5: AimPoint.y high byte (little-endian)");
            Assert.AreEqual(expectedHeight, dst[6], "byte 6: AimHeight");
            Assert.AreEqual(expectedFlags, dst[7], "byte 7: flags (bit0 FireHeld, bit1 DashRequested, bit2 AimHeld, bit3 SlideRequested)");

            for (int i = InputCodec.SizeBytes; i < dst.Length; i++)
                Assert.AreEqual((byte)0xAA, dst[i], $"byte {i}: must be untouched beyond SizeBytes");
        }

        // ---- 9. Buffers shorter than SizeBytes throw, never corrupt memory ----

        [Test]
        public void Encode_BufferTooShort_ThrowsWithoutWritingPartialData()
        {
            var input = new SimInput { FireHeld = true, MoveDir = new float2(1f, 0f) };
            var dst = new byte[InputCodec.SizeBytes - 1];
            for (int i = 0; i < dst.Length; i++) dst[i] = 0xCC;

            Assert.Throws<System.ArgumentException>(() => InputCodec.Encode(input, ConfigA, dst));

            foreach (byte b in dst)
                Assert.AreEqual((byte)0xCC, b, "buffer must be left untouched when Encode rejects it as too short");
        }

        [Test]
        public void Decode_BufferTooShort_Throws()
        {
            var src = new byte[InputCodec.SizeBytes - 1];
            Assert.Throws<System.ArgumentException>(() => InputCodec.Decode(src, ConfigA));
        }

        // ---- 10. Zero allocations on a mixed call sequence ----

        [Test]
        public void EncodeDecode_MixedSequence_DoesNotAllocateGC()
        {
            var input = new SimInput
            {
                MoveDir = new float2(0.3f, -0.4f),
                AimPoint = new float2(11f, -7f),
                AimHeight = 1.5f,
                FireHeld = true,
                SlideRequested = true
            };
            var buf = new byte[InputCodec.SizeBytes];

            // Warm-up OUTSIDE the measured lambda (QuantizeTests.cs /
            // AllocationTests.cs discipline — T24 fix-round finding F9): the
            // first call of each method is JIT-compiled inside the
            // measurement otherwise, and the test would measure the JIT.
            InputCodec.Encode(input, ConfigA, buf);
            InputCodec.Decode(buf, ConfigA);

            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    InputCodec.Encode(input, ConfigA, buf);
                    SimInput decoded = InputCodec.Decode(buf, ConfigA);
                    InputCodec.Encode(decoded, ConfigA, buf);
                }
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
