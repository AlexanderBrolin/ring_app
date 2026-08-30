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
    // AimHeight, five flags as of Stage 3 Task 20 — recounted by grep
    // against SimInput.cs, not quoted from the brief, per task-25-brief
    // §2's own warning) into 8 bytes and back, using Quantize (Task 24)
    // for every scalar mapping.
    // Task 30's prediction-parity test depends on Decode(Encode(...))
    // reproducing the exact value the client is about to send (Р34), so the
    // contracts below are exercised directly, not assumed from Quantize's
    // own (already-tested) idempotency.
    public class InputCodecTests
    {
        // Fixture configs deliberately avoid every number tracked by an
        // .asset: ArenaConfig.asset's Radius is 65, and HeroConfig.asset's
        // MaxAimHeight is whatever the bootstrap has most recently delivered
        // there (T24 fix-round finding F7 sprang exactly this trap by taking
        // the value that asset carried at the time). ⚠ THE FIELD IS NAMED
        // HERE AND THE NUMBER IS NOT, ON PURPOSE: app-88jb Т16 delivered a new
        // ceiling into HeroConfig.asset, so the literal this note used to
        // quote stopped being the number to avoid the moment it landed — and
        // any replacement literal would go stale at the next tune just as
        // quietly. The fixtures below are unaffected and still legal: neither
        // 4.25 nor 2.0 has ever been the shipped ceiling, before Т16 or after
        // it. Two DIFFERENT non-default pairs, not one, so a
        // mutation hardcoding either real number fails on at least one of
        // them (task-25-brief §3 item 9, lesson 93) — see
        // DifferentConfigs_ReadRadiusAndMaxAimHeightFromCfg_NotHardcoded.
        const float RadiusA = 40f;
        const float HeightA = 4.25f;
        // MuzzleHeight fixture: the sanitizer's non-finite AimHeight
        // fallback (SimInputSanitizer.cs:31). Checked by grep against every
        // client/Assets/Data/*.asset — 1.37 appears in none of them. (The
        // first draft used 1.35, which is the top of the collector's own torso
        // part in HeroConfig.asset — the third fixture in a row this trap has
        // caught, so the value is now checked by command, not by eye.)
        const float MuzzleA = 1.37f;
        const float RadiusB = 15f;
        const float HeightB = 2f;

        static readonly SimConfig ConfigA = new SimConfig
        {
            Arena = new ArenaSimConfig { Radius = RadiusA },
            Hero = new HeroSimConfig { MaxAimHeight = HeightA, MuzzleHeight = MuzzleA }
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
            // task-25-brief §3 item 9 (lesson 93): a parameter no test varies
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
            // raw-byte comparison can. Precision note (fix-round finding
            // F8): the expectations below are computed by calling Quantize
            // directly, NOT by hand — this test pins the LAYOUT (offsets,
            // byte order, which Quantize method feeds which field), while
            // Quantize's own numeric mapping is Task 24's business and is
            // pinned by QuantizeTests.
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
            // bit0 FireHeld + bit3 SlideRequested; bit4 down, and bits 5-7
            // (RewindTicks, app-88jb Т26) zero because this fixture leaves the
            // depth at its default — which is what makes this vector still a
            // pure FLAG vector after the byte grew a number in it.
            const byte expectedFlags = 0b0000_1001;

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
            Assert.AreEqual(expectedFlags, dst[7],
                "byte 7: bit0 FireHeld, bit1 DashRequested, bit2 AimHeld, "
                + "bit3 SlideRequested, bit4 InventoryOpen, bits 5-7 RewindTicks");

            for (int i = InputCodec.SizeBytes; i < dst.Length; i++)
                Assert.AreEqual((byte)0xAA, dst[i], $"byte {i}: must be untouched beyond SizeBytes");
        }

        [Test]
        public void SizeBytes_IsEightAndEveryByteIsWritten()
        {
            // Fix-round finding F2: the layout test above pinned "nothing is
            // written PAST SizeBytes" but never "SizeBytes is 8" nor "every
            // byte up to it IS written" — so the mutation `SizeBytes = 9`
            // survived all fourteen tests (the tail loop starts AT SizeBytes,
            // and the short-buffer tests pass SizeBytes-1, which stays short
            // either way). That is exactly the number this task exists to
            // correct: spec §3.8 rounds the payload up to "9 Б", and Task 34
            // budgets uplink traffic from it, so a silent extra byte per tick
            // per player would ride into the В2 measurement unnoticed.
            Assert.AreEqual(8, InputCodec.SizeBytes,
                "wire payload is 8 bytes; spec §3.8's '9 Б' is a round-up with margin, not the contract");

            var input = new SimInput
            {
                MoveDir = new float2(0.6f, 0.8f),
                AimPoint = new float2(37f, -91f),
                AimHeight = 3.1f,
                FireHeld = true
            };
            var dst = new byte[InputCodec.SizeBytes];
            for (int i = 0; i < dst.Length; i++) dst[i] = 0xAA;
            InputCodec.Encode(input, ConfigA, dst);

            // Every byte of the declared payload must have been overwritten.
            // The fixture above is chosen so that no field legitimately
            // encodes to the 0xAA sentinel: byte 7 (flags) is 0b0000_0001
            // and byte 6 (AimHeight at 3.1 of 4.25) is 186 — both checked
            // explicitly so this premise cannot rot silently.
            Assert.AreNotEqual((byte)0xAA, dst[7], "byte 7 (flags) was not written");
            Assert.AreNotEqual((byte)0xAA, dst[6], "byte 6 (AimHeight) was not written");
            for (int i = 0; i < InputCodec.SizeBytes; i++)
                Assert.AreNotEqual((byte)0xAA, dst[i], $"byte {i} inside SizeBytes was left unwritten");
        }

        [Test]
        public void FlagBits_EachOccupiesItsDocumentedPosition()
        {
            // Fix-round finding F1: the known vector of the layout test has
            // FireHeld+SlideRequested set and the other two clear, so its
            // flags byte is 0b0000_1001 — a PALINDROME to which DashRequested
            // and AimHeld contribute nothing. Swapping bit1 with bit2 (or
            // reversing all four) leaves that byte identical, and round-trip
            // tests are blind to it because such a mutation is symmetric
            // across Encode and Decode. Of the 1679 non-identity ways to
            // place four flags across eight bits, 59 survived the whole
            // suite. The doc comment on InputCodec is the only source of
            // truth about these positions for Task 34's ReplicateData and for
            // anyone reading a traffic dump, so each bit is pinned alone.
            AssertSingleFlagByte(new SimInput { FireHeld = true }, 0b0000_0001, "FireHeld -> bit0");
            AssertSingleFlagByte(new SimInput { DashRequested = true }, 0b0000_0010, "DashRequested -> bit1");
            AssertSingleFlagByte(new SimInput { AimHeld = true }, 0b0000_0100, "AimHeld -> bit2");
            AssertSingleFlagByte(new SimInput { SlideRequested = true }, 0b0000_1000, "SlideRequested -> bit3");
            // Stage 3 Task 20 (coordinator D-6): a fifth, independent case —
            // same reasoning as the four above, not re-derived against the
            // fresh combinatorics of five-in-eight (the "1679/59" figures
            // above are the four-bit measurement and are not restated here).
            // A round-trip test alone cannot catch Encode/Decode agreeing on
            // the WRONG bit for InventoryOpen; only a raw-byte pin like this
            // one can.
            AssertSingleFlagByte(new SimInput { InventoryOpen = true }, 0b0001_0000, "InventoryOpen -> bit4");
            // app-88jb Т26: the last three bits of the same byte stop being
            // spare and start carrying RewindTicks — a 3-bit NUMBER rather
            // than a flag, which is why each of its three bits is raised
            // ALONE below (1, 2, 4) instead of a depth being spelled out.
            // The pin is not decoration: both round-trip tests for the depth
            // set it while every flag is down, so a writer and a reader that
            // agree on the WRONG offset — the depth shifted to bit 4 instead
            // of bit 5, say — survive them both, bit 4 being zero in those
            // fixtures. Only a raw-byte case can tell the offsets apart, the
            // same argument this test's own opening paragraph makes for the
            // flags and Т20 made once more for bit 4.
            AssertSingleFlagByte(new SimInput { RewindTicks = 1 }, 0b0010_0000, "RewindTicks = 1 -> bit5");
            AssertSingleFlagByte(new SimInput { RewindTicks = 2 }, 0b0100_0000, "RewindTicks = 2 -> bit6");
            AssertSingleFlagByte(new SimInput { RewindTicks = 4 }, 0b1000_0000, "RewindTicks = 4 -> bit7");
            // The fourth case pins SATURATION, not a position — the one branch
            // of the writer the three cases above cannot reach. A depth wider
            // than the field is not a wire value at all, and Encode is required
            // to hand over the maximum rather than mask: 200 is 0b1100_1000, so
            // `RewindTicks & 0b111` would keep its low three bits and send 0 —
            // "no rewind", the single answer indistinguishable from an honest
            // zero. Every other fixture in this file and in ReconcileCodecTests
            // stays inside 0..6, so without this line that branch has no
            // witness and a masking writer survives the whole suite. (The arena
            // cap is a different rule with a different home and its own test in
            // DeterminismTests; what is pinned here is only the wire's own
            // three bits.)
            AssertSingleFlagByte(new SimInput { RewindTicks = 200 }, 0b1110_0000,
                "RewindTicks = 200 saturates -> bits 5-7 set");
            AssertSingleFlagByte(new SimInput(), 0b0000_0000, "no flags -> zero byte");
        }

        [Test]
        public void InventoryOpenFlag_RoundTrips()
        {
            // Stage 3 Task 20 (spec §3.8/§3.11): the fifth flag bit — the loot
            // window is now carried on the wire like the other four, in bits
            // FlagCombinations_AllSixteen_RoundTripExactly does not touch:
            // that sweep predates this bit and is deliberately NOT re-scoped
            // to it (sixteen combinations of the original four). FlagBits_
            // EachOccupiesItsDocumentedPosition, by contrast, WAS extended to
            // bit 4 in this same task (decision D-6) and carries the direct
            // byte-level pin.
            var baseline = new SimInput
            {
                MoveDir = new float2(0.3f, -0.4f),
                AimPoint = new float2(11f, -7f),
                AimHeight = 1.5f
            };

            SimInput open = baseline; open.InventoryOpen = true;
            SimInput decodedOpen = RoundTrip(open, ConfigA);
            Assert.IsTrue(decodedOpen.InventoryOpen, "InventoryOpen=true must round-trip true");

            SimInput closed = baseline; closed.InventoryOpen = false;
            SimInput decodedClosed = RoundTrip(closed, ConfigA);
            Assert.IsFalse(decodedClosed.InventoryOpen, "InventoryOpen=false must round-trip false");

            // Independence from the other four flags (mirrors Flags_RoundTrip_
            // EachIndividually's own "alone" premise): InventoryOpen true must
            // not perturb the existing four, and vice versa.
            SimInput mixed = baseline;
            mixed.FireHeld = true; mixed.SlideRequested = true; mixed.InventoryOpen = true;
            SimInput decodedMixed = RoundTrip(mixed, ConfigA);
            Assert.IsTrue(decodedMixed.FireHeld, "InventoryOpen must not perturb FireHeld");
            Assert.IsTrue(decodedMixed.SlideRequested, "InventoryOpen must not perturb SlideRequested");
            Assert.IsTrue(decodedMixed.InventoryOpen);
            Assert.IsFalse(decodedMixed.DashRequested, "InventoryOpen must not perturb DashRequested");
            Assert.IsFalse(decodedMixed.AimHeld, "InventoryOpen must not perturb AimHeld");
        }

        [Test]
        public void SizeBytes_IsStillEight()
        {
            // Stage 3 Task 20 (Global Constraints, hard prohibition #5): the
            // fifth flag bit rides inside the EXISTING byte 7, not a ninth
            // byte — SizeBytes_IsEightAndEveryByteIsWritten already pins this
            // number for the pre-Task-20 layout; this is the same pin
            // restated at the exact point a reader would otherwise expect the
            // payload to grow, so a future editor reaching for "just add one
            // more byte" finds a red test immediately instead of only
            // discovering the constraint two tests away.
            //
            // app-88jb Т26 gives the same promise its SECOND reason: the
            // rewind depth rides in the three spare bits of that same byte 7,
            // and the whole point of the owner's decision on it is that the
            // input payload does NOT grow to carry it. A third pin of the
            // same constant would only be a copy of this one, so the reason
            // is recorded here instead of in a test of its own.
            Assert.AreEqual(8, InputCodec.SizeBytes,
                "InventoryOpen must fit inside byte 7's existing flags, not grow the payload");
        }

        [Test]
        public void RewindTicks_RoundTripsZeroThroughSix()
        {
            // Test 42: all seven legal values travel and arrive intact.
            SimConfig cfg = ConfigA;
            System.Span<byte> buf = stackalloc byte[InputCodec.SizeBytes];
            for (int k = 0; k <= 6; k++)
            {
                var input = new SimInput { RewindTicks = (byte)k, AimHeight = cfg.Hero.MuzzleHeight };
                InputCodec.Encode(in input, in cfg, buf);
                Assert.IsTrue(InputCodec.TryDecode(buf, in cfg, out SimInput back));
                Assert.AreEqual((byte)k, back.RewindTicks, $"глубина {k} не пережила провод");
            }
        }

        [Test]
        public void RewindTicksSeven_ReadsAsSix_AndDoesNotThrow()
        {
            // Test 43 (Р82): three bits offer eight values and only seven are
            // legal. The eighth is NOT a wire error — it reads as the cap and
            // the decoder stays silent.
            SimConfig cfg = ConfigA;
            System.Span<byte> buf = stackalloc byte[InputCodec.SizeBytes];
            var input = new SimInput { AimHeight = cfg.Hero.MuzzleHeight };
            InputCodec.Encode(in input, in cfg, buf);
            buf[7] |= 0b1110_0000;                       // all three bits set = 7

            Assert.IsTrue(InputCodec.TryDecode(buf, in cfg, out SimInput back),
                "декодер бросил на легальном байте");
            Assert.AreEqual((byte)6, back.RewindTicks, "значение 7 прочитано не как кап");
        }

        [Test]
        public void RewindTicks_AndFlags_DoNotPerturbEachOther()
        {
            // app-88jb Т26, mirroring InventoryOpenFlag_RoundTrips: the depth
            // and the five flags share byte 7, so each side needs a witness
            // that the other one does not eat it. Both round-trip tests above
            // raise the depth with every flag DOWN, and every flag test in
            // this file raises flags with the depth at ZERO — neither shape
            // can see a writer that ORs the depth over the flag nibble or a
            // reader that masks the flags out of the depth.
            //
            // Three cases and not one. The first catches a depth that wipes
            // the flags, the third catches flags that wipe the depth, and the
            // second is the premise without which the other two cannot tell
            // real carriage from "both fields always read back at maximum".
            var baseline = new SimInput
            {
                MoveDir = new float2(0.3f, -0.4f),
                AimPoint = new float2(11f, -7f),
                AimHeight = 1.5f
            };

            SimInput deep = baseline;
            deep.FireHeld = true; deep.DashRequested = true; deep.AimHeld = true;
            deep.SlideRequested = true; deep.InventoryOpen = true;
            deep.RewindTicks = 6;
            SimInput decodedDeep = RoundTrip(deep, ConfigA);
            Assert.IsTrue(decodedDeep.FireHeld, "the maximum depth must not perturb FireHeld");
            Assert.IsTrue(decodedDeep.DashRequested, "the maximum depth must not perturb DashRequested");
            Assert.IsTrue(decodedDeep.AimHeld, "the maximum depth must not perturb AimHeld");
            Assert.IsTrue(decodedDeep.SlideRequested, "the maximum depth must not perturb SlideRequested");
            Assert.IsTrue(decodedDeep.InventoryOpen, "the maximum depth must not perturb InventoryOpen");
            Assert.AreEqual((byte)6, decodedDeep.RewindTicks,
                "the maximum legal depth must survive the wire with all five flags raised");

            SimInput shallow = deep;
            shallow.RewindTicks = 0;
            SimInput decodedShallow = RoundTrip(shallow, ConfigA);
            Assert.AreEqual((byte)0, decodedShallow.RewindTicks,
                "premise: five raised flags must not read back as a nonzero depth");
            Assert.IsTrue(decodedShallow.FireHeld, "a zero depth must not perturb FireHeld");
            Assert.IsTrue(decodedShallow.DashRequested, "a zero depth must not perturb DashRequested");
            Assert.IsTrue(decodedShallow.AimHeld, "a zero depth must not perturb AimHeld");
            Assert.IsTrue(decodedShallow.SlideRequested, "a zero depth must not perturb SlideRequested");
            Assert.IsTrue(decodedShallow.InventoryOpen, "a zero depth must not perturb InventoryOpen");

            SimInput bare = baseline;
            bare.RewindTicks = 6;
            SimInput decodedBare = RoundTrip(bare, ConfigA);
            Assert.IsFalse(decodedBare.FireHeld, "the depth must not raise FireHeld");
            Assert.IsFalse(decodedBare.DashRequested, "the depth must not raise DashRequested");
            Assert.IsFalse(decodedBare.AimHeld, "the depth must not raise AimHeld");
            Assert.IsFalse(decodedBare.SlideRequested, "the depth must not raise SlideRequested");
            Assert.IsFalse(decodedBare.InventoryOpen, "the depth must not raise InventoryOpen");
            Assert.AreEqual((byte)6, decodedBare.RewindTicks,
                "the maximum legal depth must survive the wire with every flag down");
        }

        static void AssertSingleFlagByte(in SimInput input, byte expected, string what)
        {
            System.Span<byte> buf = stackalloc byte[InputCodec.SizeBytes];
            InputCodec.Encode(input, ConfigA, buf);
            Assert.AreEqual(expected, buf[7], $"flags byte for {what}");
        }

        [Test]
        public void NonFiniteInput_ProducesLegalCodes_AndMoveDirMirrorsSanitize()
        {
            // Fix-round finding F4. A wire codec must turn ANY input into a
            // legal code — that part held. What did not: MoveDir's non-finite
            // reading was the OPPOSITE of SimInputSanitizer's. Quantize.Unit's
            // NaN-safe saturate lifts NaN to the upper rail, so a glitching
            // client used to decode to FULL SPEED, while the sanitizer maps a
            // non-finite MoveDir to zero (stand still). After Task 34 the
            // server never sees raw input, so that guard would have been dead
            // code on the network path and the rule silently inverted.
            var nan = new SimInput
            {
                MoveDir = new float2(float.NaN, 0f),
                AimPoint = new float2(float.NaN, float.PositiveInfinity),
                AimHeight = float.NaN
            };
            System.Span<byte> buf = stackalloc byte[InputCodec.SizeBytes];
            Assert.DoesNotThrow(() =>
            {
                System.Span<byte> local = stackalloc byte[InputCodec.SizeBytes];
                InputCodec.Encode(nan, ConfigA, local);
            }, "non-finite input must never throw on the write path of a tick");

            InputCodec.Encode(nan, ConfigA, buf);
            Assert.AreEqual((byte)0, buf[1], "non-finite MoveDir must encode magnitude 0, mirroring SimInputSanitizer");

            SimInput decoded = InputCodec.Decode(buf, ConfigA);
            Assert.AreEqual(0f, math.length(decoded.MoveDir), 1e-6f,
                "non-finite MoveDir must decode to a standstill, not to full speed");
            Assert.IsTrue(math.all(math.isfinite(decoded.AimPoint)), "AimPoint must decode finite");
            Assert.IsTrue(math.isfinite(decoded.AimHeight), "AimHeight must decode finite");

            // AimHeight mirrors the sanitizer too (fix-round 2): its
            // fallback is cfg.Hero.MuzzleHeight, a plain balance number this
            // codec already has — the first version of this test wrongly
            // pinned the rail, repeating a false claim from the production
            // comment. AimPoint is the only field that genuinely cannot be
            // mirrored (its fallback is the player's own AimPoint), so it
            // still saturates, pinned here as deliberate.
            Assert.AreEqual(3f * RadiusA, decoded.AimPoint.x, 3f * RadiusA / 65535f + 1e-3f,
                "non-finite AimPoint.x saturates to the upper rail (+3*Radius) — its fallback needs player state");
            Assert.AreEqual(MuzzleA, decoded.AimHeight, HeightA / 255f + 1e-4f,
                "non-finite AimHeight must decode to MuzzleHeight, mirroring SimInputSanitizer.cs:31");
            Assert.AreNotEqual(HeightA, decoded.AimHeight,
                "non-finite AimHeight must NOT saturate to MaxAimHeight (a fully raised aim)");
        }

        [Test]
        public void SubDeadzoneMagnitude_ValueIsStable_EvenThoughBytesAreNot()
        {
            // Fix-round finding F3: the class doc used to claim byte-level
            // idempotency without qualification. Below 1/510 the magnitude
            // byte quantizes to 0, Decode returns a zero vector, and
            // re-encoding that zero writes Quantize.Dir's zero-vector angle
            // instead of the original heading — measured: (0, 0.001) writes
            // angle 192 on the first pass and 128 on the second. What Р34
            // actually needs is that the decoded VALUE be stable, and it is.
            var tiny = new SimInput { MoveDir = new float2(0f, 0.001f) };

            System.Span<byte> first = stackalloc byte[InputCodec.SizeBytes];
            InputCodec.Encode(tiny, ConfigA, first);
            Assert.AreEqual((byte)0, first[1], "fixture premise: this magnitude must quantize to code 0");
            // Stub-defeating (fix-round 2): without these two the whole test
            // passed on a constant stub writing zero bytes and decoding to
            // default — every remaining assertion is about zeros. They also
            // pin the very numbers the class doc quotes, so the doc cannot
            // drift away from the code unnoticed.
            Assert.AreEqual((byte)192, first[0],
                "angle byte of (0, 0.001) is +90 degrees -> code 192 on the FIRST pass");

            SimInput decoded1 = InputCodec.Decode(first, ConfigA);
            System.Span<byte> second = stackalloc byte[InputCodec.SizeBytes];
            InputCodec.Encode(decoded1, ConfigA, second);
            SimInput decoded2 = InputCodec.Decode(second, ConfigA);

            // NOT pinned to a literal: re-encoding a zero vector runs
            // atan2 on the SIGNED zeros produced by `DirBack(192) * 0`, and
            // the sign of the x component follows math.cos's rounding at
            // pi/2 (Mono returns a small NEGATIVE cosine, giving code 0; a
            // library rounding the other way would give 128). Pinning the
            // literal would turn a precision detail into a false invariant —
            // the same trap Task 24 avoided for Dir(NaN). What IS invariant,
            // and what the class doc actually claims, is that the byte
            // CHANGES while the decoded value does not.
            Assert.AreNotEqual(first[0], second[0],
                "re-encoding the decoded zero must write Dir's zero-vector code, not the original heading");
            Assert.AreEqual(0f, math.length(decoded1.MoveDir), 1e-6f, "sub-deadzone input decodes to a standstill");
            Assert.AreEqual(decoded1.MoveDir.x, decoded2.MoveDir.x, 0f, "decoded value must be stable across passes");
            Assert.AreEqual(decoded1.MoveDir.y, decoded2.MoveDir.y, 0f, "decoded value must be stable across passes");
        }

        [Test]
        public void EveryFieldOfSimInput_IsCarriedOnTheWire()
        {
            // Fix-round finding F5: Decode builds a fresh SimInput with an
            // object initializer, so a field added in a later stage would
            // come back as default and no test would notice. Same guard shape
            // as SimConfigHashTests.SimConfig_CarriesExactlyTwelveFields
            // (coordinator fix-round Ф3 review m4: renamed from
            // …ExactlyEightSections since Т13 grew the section count to
            // twelve) — it does not prove a new field is carried, it forces
            // whoever adds one to come here and decide.
            // Stage 3 Task 17: InventoryOpen joins the struct — the decision
            // this sentinel exists to force, made and recorded here rather
            // than defaulted into. Stage 3 Task 20 carries it the rest of
            // the way: the bit lives in InputCodec.InventoryOpenBit (byte 7,
            // bit 4) and ReplicateData's own Flags byte, with a dedicated
            // round-trip case in InventoryOpenFlag_RoundTrips below — this
            // sentinel's own job stays narrow (a field was added, not
            // whether the wire carries it).
            // app-88jb Т26 adds RewindTicks and makes the same decision in the
            // same place: the rewind depth IS carried, in bits 5-7 of that
            // same byte 7, with the arena clamp in SimInputSanitizer and the
            // wire cases in RewindTicks_RoundTripsZeroThroughSix,
            // RewindTicksSeven_ReadsAsSix_AndDoesNotThrow and
            // RewindTicks_AndFlags_DoNotPerturbEachOther above. The name is
            // added here only after those exist, which is the whole point of
            // this sentinel refusing to be a formality.
            string[] expected =
            {
                "MoveDir", "AimPoint", "FireHeld", "DashRequested",
                "AimHeight", "AimHeld", "SlideRequested", "InventoryOpen",
                "RewindTicks"
            };
            var actual = new System.Collections.Generic.List<string>();
            foreach (System.Reflection.FieldInfo f in typeof(SimInput).GetFields()) actual.Add(f.Name);
            CollectionAssert.AreEquivalent(expected, actual,
                "SimInput gained or lost a field — InputCodec's layout and SizeBytes must be revisited");
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
