using NUnit.Framework;
using Ring.Networking.Protocol;
using Unity.Mathematics;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below
// are required by the file, not just convenience imports (AllocationTests.cs
// carries the same pair for the same reason).
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    // Stage 2 Task 24 (spec §3.8, Р30/Р34/Р84/Р134): Quantize's own contract
    // is idempotency (Q(D(q)) == q for every code) plus a tolerance bound on
    // the ORIGINAL value's round trip — both are exercised exhaustively
    // below, not on a single spot value, because Task 30's prediction-parity
    // test depends on EVERY code round-tripping, not a lucky one.
    public class QuantizeTests
    {
        // ---- 1. Round-trip tolerance on a GRID of values (not one number) ----

        [Test]
        public void Pos_RoundTrip_WithinToleranceAcrossGrid()
        {
            const float Radius = 65f; // fixture: arbitrary arena-scale radius, not read from any .asset
            const int Samples = 1201; // > 1000 required by task-24-brief §3.1
            const float PlanMaxErrorMeters = 0.004f; // plan Task 24: position round-trip error <= 4 mm
            float halfStep = Radius / 65535f; // = (2*radius/65535) / 2
            Assert.LessOrEqual(halfStep, PlanMaxErrorMeters,
                "the codec's own half-step must already satisfy the plan's <=4mm requirement");

            for (int i = 0; i < Samples; i++)
            {
                float v = -Radius + i * (2f * Radius) / (Samples - 1);
                float decoded = Quantize.PosBack(Quantize.Pos(v, Radius), Radius);
                float error = math.abs(decoded - v);
                if (error > halfStep * 1.001f) // 0.1% float-arithmetic slack, not a tolerance widening
                    Assert.Fail($"v={v}: round-trip error {error} exceeds half-step {halfStep}");
            }
        }

        [Test]
        public void Aim_RoundTrip_WithinToleranceAcrossGrid()
        {
            const float Radius = 65f;
            const int Samples = 1201;
            const float PlanMaxErrorMeters = 0.008f; // plan Task 24: aim round-trip error <= 8 mm
            float aimRange = 3f * Radius; // Р30 — Aim's domain is 3x Pos's
            float halfStep = aimRange / 65535f;
            Assert.LessOrEqual(halfStep, PlanMaxErrorMeters,
                "the codec's own half-step must already satisfy the plan's <=8mm requirement");

            for (int i = 0; i < Samples; i++)
            {
                float v = -aimRange + i * (2f * aimRange) / (Samples - 1);
                float decoded = Quantize.AimBack(Quantize.Aim(v, Radius), Radius);
                float error = math.abs(decoded - v);
                if (error > halfStep * 1.001f)
                    Assert.Fail($"v={v}: round-trip error {error} exceeds half-step {halfStep}");
            }
        }

        [Test]
        public void Dir_RoundTrip_AngleWithinToleranceAcrossGrid()
        {
            const int Samples = 1201;
            const float PlanMaxErrorDegrees = 1.5f; // plan Task 24: direction round-trip error <= 1.5 deg
            float halfStepDeg = 360f / 256f / 2f;
            Assert.LessOrEqual(halfStepDeg, PlanMaxErrorDegrees,
                "the codec's own half-step must already satisfy the plan's <=1.5deg requirement");

            for (int i = 0; i < Samples; i++)
            {
                float angleDeg = -180f + i * 360f / (Samples - 1);
                float rad = math.radians(angleDeg);
                var v = new float2(math.cos(rad), math.sin(rad));
                float2 decoded = Quantize.DirBack(Quantize.Dir(v));
                float error = AngularDifferenceDegrees(v, decoded);
                if (error > halfStepDeg * 1.001f)
                    Assert.Fail($"angle={angleDeg}deg: round-trip error {error}deg exceeds half-step {halfStepDeg}deg");
            }
        }

        [Test]
        public void Unit_RoundTrip_WithinTolerancePercentAcrossGrid()
        {
            const float Max = 100f; // fixture: arbitrary one-sided max, not read from any .asset
            const int Samples = 1201;
            const float PlanMaxErrorFraction = 0.005f; // plan Task 24: Unit round-trip error <= 0.5%
            float halfStepFraction = 1f / 255f / 2f;
            Assert.LessOrEqual(halfStepFraction, PlanMaxErrorFraction,
                "the codec's own half-step must already satisfy the plan's <=0.5% requirement");

            for (int i = 0; i < Samples; i++)
            {
                float v = i * Max / (Samples - 1);
                float decoded = Quantize.UnitBack(Quantize.Unit(v, Max), Max);
                float errorFraction = math.abs(decoded - v) / Max;
                if (errorFraction > halfStepFraction * 1.001f)
                    Assert.Fail($"v={v}: round-trip error fraction {errorFraction} exceeds half-step {halfStepFraction}");
            }
        }

        // ---- 2. Idempotency on every representable code (Р34) ----

        [Test]
        public void Pos_Idempotent_AllCodes_AcrossRadii()
        {
            foreach (float radius in new[] { 65f, 22.5f }) // second radius is the non-default fixture required by §3.7
            {
                for (int q = 0; q <= ushort.MaxValue; q++)
                {
                    ushort code = (ushort)q;
                    ushort reencoded = Quantize.Pos(Quantize.PosBack(code, radius), radius);
                    if (reencoded != code)
                        Assert.Fail($"radius={radius}: Pos(PosBack({code})) = {reencoded}, expected {code} (Р34 idempotency)");
                }
            }
        }

        [Test]
        public void Aim_Idempotent_AllCodes_AcrossRadii()
        {
            foreach (float radius in new[] { 65f, 22.5f })
            {
                for (int q = 0; q <= ushort.MaxValue; q++)
                {
                    ushort code = (ushort)q;
                    ushort reencoded = Quantize.Aim(Quantize.AimBack(code, radius), radius);
                    if (reencoded != code)
                        Assert.Fail($"radius={radius}: Aim(AimBack({code})) = {reencoded}, expected {code} (Р34 idempotency)");
                }
            }
        }

        [Test]
        public void Dir_Idempotent_AllCodes()
        {
            for (int q = 0; q <= byte.MaxValue; q++)
            {
                byte code = (byte)q;
                byte reencoded = Quantize.Dir(Quantize.DirBack(code));
                if (reencoded != code)
                    Assert.Fail($"Dir(DirBack({code})) = {reencoded}, expected {code} (Р34 idempotency)");
            }
        }

        [Test]
        public void Unit_Idempotent_AllCodes_AcrossMax()
        {
            foreach (float max in new[] { 100f, 3.8f }) // second max is the non-default fixture required by §3.7 (HeroConfig.MaxAimHeight shape)
            {
                for (int q = 0; q <= byte.MaxValue; q++)
                {
                    byte code = (byte)q;
                    byte reencoded = Quantize.Unit(Quantize.UnitBack(code, max), max);
                    if (reencoded != code)
                        Assert.Fail($"max={max}: Unit(UnitBack({code})) = {reencoded}, expected {code} (Р34 idempotency)");
                }
            }
        }

        // ---- 3. Boundaries ----

        [Test]
        public void Pos_Boundaries_MapToExpectedCodes()
        {
            const float Radius = 65f;
            const ushort MidCode = 32768; // round-to-even(65535/2 == 32767.5) — Р134: half-integer rounds to the EVEN neighbour
            Assert.AreEqual((ushort)0, Quantize.Pos(-Radius, Radius), "-radius must map to code 0 (lower rail)");
            Assert.AreEqual(MidCode, Quantize.Pos(0f, Radius), "v=0 sits exactly on a round-to-even midpoint (Р134)");
            Assert.AreEqual(ushort.MaxValue, Quantize.Pos(Radius, Radius), "+radius must map to the top code (upper rail)");
        }

        [Test]
        public void Aim_Boundaries_MapToExpectedCodes()
        {
            const float Radius = 65f;
            const ushort MidCode = 32768;
            Assert.AreEqual((ushort)0, Quantize.Aim(-3f * Radius, Radius), "-3*radius must map to code 0 (lower rail, Р30)");
            Assert.AreEqual(MidCode, Quantize.Aim(0f, Radius), "v=0 sits exactly on a round-to-even midpoint (Р134)");
            Assert.AreEqual(ushort.MaxValue, Quantize.Aim(3f * Radius, Radius), "+3*radius must map to the top code (upper rail, Р30)");
        }

        [Test]
        public void Unit_Boundaries_MapToExpectedCodes()
        {
            const float Max = 3.8f; // fixture: shape of HeroConfig.MaxAimHeight (Р84), not read from any .asset
            Assert.AreEqual((byte)0, Quantize.Unit(0f, Max), "v=0 must map to code 0");
            Assert.AreEqual(byte.MaxValue, Quantize.Unit(Max, Max), "v=max must map to code 255");
        }

        // ---- 4. Clamps beyond range: never throw, always a legal boundary code ----

        [Test]
        public void Pos_OutOfRangeAndNonFinite_ClampToBoundary_NoThrow()
        {
            const float Radius = 65f;
            ushort high = 1, low = 1, nanCode = 1, posInf = 1, negInf = 1;
            Assert.DoesNotThrow(() => high = Quantize.Pos(Radius * 10f, Radius), "10x radius must not throw");
            Assert.DoesNotThrow(() => low = Quantize.Pos(-Radius * 10f, Radius), "-10x radius must not throw");
            Assert.DoesNotThrow(() => nanCode = Quantize.Pos(float.NaN, Radius), "NaN must not throw");
            Assert.DoesNotThrow(() => posInf = Quantize.Pos(float.PositiveInfinity, Radius), "+Infinity must not throw");
            Assert.DoesNotThrow(() => negInf = Quantize.Pos(float.NegativeInfinity, Radius), "-Infinity must not throw");

            Assert.AreEqual(ushort.MaxValue, high, "10x radius must clamp to the top rail, not wrap");
            Assert.AreEqual((ushort)0, low, "-10x radius must clamp to the bottom rail");
            // Unity.Mathematics' scalar min/max are NaN-safe (Quantize.cs's own
            // class doc, math.cs:929/1061): saturate((NaN+r)/(2r)) resolves to
            // the UPPER bound, not to NaN.
            Assert.AreEqual(ushort.MaxValue, nanCode, "NaN must clamp to the top rail (Unity.Mathematics NaN-safe saturate)");
            Assert.AreEqual(ushort.MaxValue, posInf, "+Infinity must clamp to the top rail");
            Assert.AreEqual((ushort)0, negInf, "-Infinity must clamp to the bottom rail");
        }

        [Test]
        public void Aim_OutOfRangeAndNonFinite_ClampToBoundary_NoThrow()
        {
            const float Radius = 65f;
            ushort high = 1, low = 1, nanCode = 1, posInf = 1, negInf = 1;
            Assert.DoesNotThrow(() => high = Quantize.Aim(Radius * 30f, Radius), "far beyond 3x radius must not throw");
            Assert.DoesNotThrow(() => low = Quantize.Aim(-Radius * 30f, Radius), "far below -3x radius must not throw");
            Assert.DoesNotThrow(() => nanCode = Quantize.Aim(float.NaN, Radius), "NaN must not throw");
            Assert.DoesNotThrow(() => posInf = Quantize.Aim(float.PositiveInfinity, Radius), "+Infinity must not throw");
            Assert.DoesNotThrow(() => negInf = Quantize.Aim(float.NegativeInfinity, Radius), "-Infinity must not throw");

            Assert.AreEqual(ushort.MaxValue, high, "far beyond 3x radius must clamp to the top rail");
            Assert.AreEqual((ushort)0, low, "far below -3x radius must clamp to the bottom rail");
            Assert.AreEqual(ushort.MaxValue, nanCode, "NaN must clamp to the top rail");
            Assert.AreEqual(ushort.MaxValue, posInf, "+Infinity must clamp to the top rail");
            Assert.AreEqual((ushort)0, negInf, "-Infinity must clamp to the bottom rail");
        }

        [Test]
        public void Unit_OutOfRangeAndNonFinite_ClampToBoundary_NoThrow()
        {
            const float Max = 3.8f;
            byte high = 1, low = 1, nanCode = 1, posInf = 1, negInf = 1;
            Assert.DoesNotThrow(() => high = Quantize.Unit(Max * 10f, Max), "10x max must not throw");
            Assert.DoesNotThrow(() => low = Quantize.Unit(-Max * 10f, Max), "-10x max must not throw");
            Assert.DoesNotThrow(() => nanCode = Quantize.Unit(float.NaN, Max), "NaN must not throw");
            Assert.DoesNotThrow(() => posInf = Quantize.Unit(float.PositiveInfinity, Max), "+Infinity must not throw");
            Assert.DoesNotThrow(() => negInf = Quantize.Unit(float.NegativeInfinity, Max), "-Infinity must not throw");

            Assert.AreEqual(byte.MaxValue, high, "10x max must clamp to the top rail");
            Assert.AreEqual((byte)0, low, "-10x max must clamp to the bottom rail (one-sided domain, Р84)");
            Assert.AreEqual(byte.MaxValue, nanCode, "NaN must clamp to the top rail");
            Assert.AreEqual(byte.MaxValue, posInf, "+Infinity must clamp to the top rail");
            Assert.AreEqual((byte)0, negInf, "-Infinity must clamp to the bottom rail");
        }

        // ---- 5. Symmetry on a concrete pair (not a tautology) ----

        [Test]
        public void Pos_SymmetricAroundZero_ConcretePair()
        {
            const float Radius = 65f;
            const float X = Radius / 3f; // concrete, non-edge, non-zero probe point
            float decodedPlus = Quantize.PosBack(Quantize.Pos(X, Radius), Radius);
            float decodedMinus = Quantize.PosBack(Quantize.Pos(-X, Radius), Radius);

            // Fixture-premise guard: on an all-zero stub (Q=>0, D=>0f) BOTH
            // sides decode to 0f and "0f == -0f" would pass this check for
            // free (Task 23's "0 trivially equals 0" trap, same shape) —
            // this line forces the round trip to have actually moved off
            // the origin before the mirror check below means anything.
            Assert.Greater(math.abs(decodedPlus), Radius / 4f,
                "fixture premise: PosBack(Pos(+x)) must decode near +x, not collapse to 0 on a stub");

            float halfStep = Radius / 65535f;
            Assert.That(decodedPlus, Is.EqualTo(-decodedMinus).Within(halfStep * 1.001f),
                "PosBack(Pos(+x)) and PosBack(Pos(-x)) must be exact mirror images around zero (Р134 — ToEven is an odd function)");
        }

        [Test]
        public void Unit_ReflectionAroundMidpoint_ConcretePair()
        {
            // Unit's domain is ONE-SIDED [0, max] (Р84 — HP/stick magnitude/
            // AimHeight are all non-negative, Quantize.cs's own class doc),
            // so there is no negative half to mirror the way Pos does. The
            // domain-correct analogue of "symmetry around zero" is symmetry
            // around the domain's OWN centre, max/2: a probe point v and its
            // mirror (max - v) must decode back to values summing to max.
            const float Max = 100f;
            const float V = Max / 3f;
            float decodedLow = Quantize.UnitBack(Quantize.Unit(V, Max), Max);
            float decodedHigh = Quantize.UnitBack(Quantize.Unit(Max - V, Max), Max);

            Assert.Greater(decodedLow, Max / 10f,
                "fixture premise: UnitBack(Unit(v)) must decode near v, not collapse to 0 on a stub");

            float halfStep = Max / 255f / 2f;
            Assert.That(decodedLow + decodedHigh, Is.EqualTo(Max).Within(halfStep * 2.002f),
                "UnitBack(Unit(v)) + UnitBack(Unit(max-v)) must sum to max — Unit's own mirror symmetry (Р134 ToEven)");
        }

        // ---- 6. Dir covers the full circle ----

        static readonly (float deg, byte code)[] EightDirections =
        {
            (0f, 128), (45f, 160), (90f, 192), (135f, 224),
            (180f, 0), (225f, 32), (270f, 64), (315f, 96)
        };

        [Test]
        public void Dir_EightDirections_RoundTrip()
        {
            float halfStepDeg = 360f / 256f / 2f;
            foreach ((float deg, byte expectedCode) in EightDirections)
            {
                float rad = math.radians(deg);
                var v = new float2(math.cos(rad), math.sin(rad));
                byte code = Quantize.Dir(v);
                Assert.AreEqual(expectedCode, code, $"{deg}deg must encode to structural code {expectedCode}");

                float2 back = Quantize.DirBack(code);
                float error = AngularDifferenceDegrees(v, back);
                Assert.That(error, Is.LessThanOrEqualTo(halfStepDeg * 1.001f),
                    $"DirBack({code}) must decode back within half a step ({halfStepDeg}deg) of {deg}deg");
            }
        }

        [Test]
        public void Dir_ZeroVector_GivesSameCodeAsPlusX()
        {
            // Decision (task-24-brief §2, "Обязательные решения" item 3):
            // atan2(0,0) == 0 radians, i.e. Dir treats a ZERO-magnitude
            // direction the same as pointing along +X. Not a codec defect:
            // on the wire MoveDir is angle + magnitude (Task 25), and at
            // magnitude 0 nothing ever reads the angle back — pinned here
            // so the next reader does not mistake this for a bug.
            byte plusXCode = Quantize.Dir(new float2(1f, 0f));
            Assert.AreEqual((byte)128, plusXCode, "ground truth: +X must encode to structural code 128");
            Assert.AreEqual(plusXCode, Quantize.Dir(float2.zero),
                "Dir(zero) must encode identically to +X (atan2(0,0) == 0 rad)");
        }

        [Test]
        public void Dir_NearPiSeam_GivesConsistentCode_DistinctFromPlusX()
        {
            // Approaching -X (angle = +-pi) from BOTH sides of the seam must
            // fold to the SAME code — exactly what the `& 0xFF` mask buys
            // (Quantize.cs's own class doc): without it, the +pi side's raw
            // code is 256, one past what a byte holds.
            byte fromPositiveSide = Quantize.Dir(new float2(-1f, 1e-4f));
            byte fromNegativeSide = Quantize.Dir(new float2(-1f, -1e-4f));
            byte exactPlusPi = Quantize.Dir(new float2(-1f, 0f));
            byte plusXCode = Quantize.Dir(new float2(1f, 0f)); // independent second call, not the literal 128

            Assert.AreEqual((byte)0, exactPlusPi, "ground truth: angle=+pi (atan2(0,-1)) must fold to structural code 0");
            Assert.AreEqual(exactPlusPi, fromPositiveSide, "approaching the seam from the +pi side must fold to the same code as the exact seam");
            Assert.AreEqual(exactPlusPi, fromNegativeSide, "approaching the seam from the -pi side must land on the same code");

            // Defeats a "return one constant for every input" stub (RED-stage
            // fix-round finding: comparing against the LITERAL 128 instead of
            // this second call left the whole test green on the all-zero
            // stub, because ground truth for exactPlusPi is ALSO 0 — same
            // trap as Task 23's "0 trivially equals 0"). Comparing against an
            // INDEPENDENT call instead means a stub returning one constant
            // for every input fails this line too.
            Assert.AreNotEqual(plusXCode, exactPlusPi, "the seam code must differ from +X's code, not be a fixed constant shared by every direction");
        }

        // ---- 8. Zero allocations ----

        [Test]
        public void Quantize_MixedCallSequence_DoesNotAllocateGC()
        {
            const float Radius = 65f;
            const float Max = 3.8f;
            var v = new float2(12f, -7f);
            Assert.That(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    ushort pos = Quantize.Pos(3f, Radius);
                    Quantize.PosBack(pos, Radius);
                    ushort aim = Quantize.Aim(-40f, Radius);
                    Quantize.AimBack(aim, Radius);
                    byte dir = Quantize.Dir(v);
                    Quantize.DirBack(dir);
                    byte unit = Quantize.Unit(2.1f, Max);
                    Quantize.UnitBack(unit, Max);
                }
            }, Is.Not.AllocatingGCMemory());
        }

        // ---- helpers ----

        /// Wrap-aware angular distance in degrees between two DIRECTION
        /// vectors (not necessarily unit length) — a naive |a-b| on raw
        /// angles breaks at the +-pi seam (179deg vs -179deg would read as
        /// ~358deg apart instead of ~2deg); atan2(cross, dot) reports the
        /// true signed angle between them in (-180, 180].
        static float AngularDifferenceDegrees(float2 a, float2 b)
        {
            float cross = a.x * b.y - a.y * b.x;
            float dot = a.x * b.x + a.y * b.y;
            return math.abs(math.degrees(math.atan2(cross, dot)));
        }
    }
}
