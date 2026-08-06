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

        // Fix-round finding F1/F2: the first draft asserted `error <=
        // halfStep * 1.001f` and, separately, `halfStep <=
        // PlanMaxError` — a constant-vs-constant comparison that never
        // touched the codec at all, so the plan's ACTUAL requirement
        // (<=4mm / <=8mm / <=1.5deg / <=0.5%) was never tested. Worse, the
        // sharp half-step proxy carried a 0.1% slack thinner than the float
        // noise of the decode path itself. Numbers, re-measured for this
        // note in a strict float32 emulation rather than quoted from a
        // review (fix-round 2 — the first version of this comment cited a
        // figure the coordinator had not reproduced): on THIS test's own
        // 1201-point grid the worst Aim error is 2.9907 mm against a
        // half-step of 2.9755 mm (ratio 1.0051, at v = 78 m), i.e. already
        // past the old slack; Pos happens to stay just under it on this
        // grid (ratio 0.99998) but goes to 1.0077 on a two-million-point
        // sweep. So the old test was green by the accident of which points
        // it sampled and of Mono's intermediate precision, not by the
        // codec's merit. Both asserts below now run against a MEASURED
        // maximum: the plan bound is the contract, the half-step bound is
        // the sharp proxy, and the noise term is explicit instead of a
        // magic percentage.
        const float FloatEps = 1.1920929e-7f; // 2^-23, single-precision epsilon

        /// Worst-case float rounding the decode path can add on top of the
        /// ideal half-step: the decode multiplies by the full range, so the
        /// error scales with it. Four eps is a deliberately generous bound —
        /// measured overshoot is 15 um for Aim and 8 um for Pos at radius
        /// 65, against an allowance of 186 um and 62 um respectively, i.e.
        /// an order of magnitude of headroom.
        static float FloatNoise(float range) => 4f * math.abs(range) * FloatEps;

        [Test]
        public void Pos_RoundTrip_WithinToleranceAcrossGrid()
        {
            // Radius 65 is the plan's own acceptance number for this bound
            // ("round-trip позиции <= 4 мм при radius = 65"), quoted from the
            // plan — NOT read from, and not tracking, ArenaConfig.asset,
            // whose Radius happens to hold the same value today. Idempotency
            // across non-default radii is covered separately below.
            const float Radius = 65f;
            const int Samples = 1201; // > 1000 required by task-24-brief §3.1
            const float PlanMaxErrorMeters = 0.004f; // plan Task 24: position round-trip error <= 4 mm
            float halfStep = Radius / 65535f; // = (2*radius/65535) / 2

            float maxError = 0f, worstV = 0f;
            for (int i = 0; i < Samples; i++)
            {
                float v = -Radius + i * (2f * Radius) / (Samples - 1);
                float error = math.abs(Quantize.PosBack(Quantize.Pos(v, Radius), Radius) - v);
                if (error > maxError) { maxError = error; worstV = v; }
            }

            Assert.LessOrEqual(maxError, PlanMaxErrorMeters,
                $"plan contract: worst round-trip error {maxError} m at v={worstV} exceeds 4 mm");
            Assert.LessOrEqual(maxError, halfStep + FloatNoise(2f * Radius),
                $"sharp bound: worst error {maxError} m at v={worstV} exceeds half-step {halfStep} plus float noise");
        }

        [Test]
        public void Aim_RoundTrip_WithinToleranceAcrossGrid()
        {
            const float Radius = 65f;
            const int Samples = 1201;
            const float PlanMaxErrorMeters = 0.008f; // plan Task 24: aim round-trip error <= 8 mm
            float aimRange = 3f * Radius; // Р30 — Aim's domain is 3x Pos's
            float halfStep = aimRange / 65535f;

            float maxError = 0f, worstV = 0f;
            for (int i = 0; i < Samples; i++)
            {
                float v = -aimRange + i * (2f * aimRange) / (Samples - 1);
                float error = math.abs(Quantize.AimBack(Quantize.Aim(v, Radius), Radius) - v);
                if (error > maxError) { maxError = error; worstV = v; }
            }

            Assert.LessOrEqual(maxError, PlanMaxErrorMeters,
                $"plan contract: worst round-trip error {maxError} m at v={worstV} exceeds 8 mm");
            Assert.LessOrEqual(maxError, halfStep + FloatNoise(2f * aimRange),
                $"sharp bound: worst error {maxError} m at v={worstV} exceeds half-step {halfStep} plus float noise");
        }

        [Test]
        public void Dir_RoundTrip_AngleWithinToleranceAcrossGrid()
        {
            const int Samples = 1201;
            const float PlanMaxErrorDegrees = 1.5f; // plan Task 24: direction round-trip error <= 1.5 deg
            float halfStepDeg = 360f / 256f / 2f;

            float maxError = 0f, worstDeg = 0f;
            for (int i = 0; i < Samples; i++)
            {
                float angleDeg = -180f + i * 360f / (Samples - 1);
                float rad = math.radians(angleDeg);
                var v = new float2(math.cos(rad), math.sin(rad));
                float error = AngularDifferenceDegrees(v, Quantize.DirBack(Quantize.Dir(v)));
                if (error > maxError) { maxError = error; worstDeg = angleDeg; }
            }

            Assert.LessOrEqual(maxError, PlanMaxErrorDegrees,
                $"plan contract: worst angular error {maxError} deg at {worstDeg} deg exceeds 1.5 deg");
            Assert.LessOrEqual(maxError, halfStepDeg + FloatNoise(360f),
                $"sharp bound: worst angular error {maxError} deg at {worstDeg} deg exceeds half-step {halfStepDeg} deg plus float noise");
        }

        [Test]
        public void Unit_RoundTrip_WithinTolerancePercentAcrossGrid()
        {
            // 137 is deliberately a number no .asset holds — fix-round 2
            // finding: the previous fixture, 100, is both HeroConfig.MaxHp
            // and HeroConfig.StaminaMax, under a comment claiming no
            // balance number held it. Same defect this round closed as F7.
            const float Max = 137f;
            const int Samples = 1201;
            const float PlanMaxErrorFraction = 0.005f; // plan Task 24: Unit round-trip error <= 0.5%
            float halfStepFraction = 1f / 255f / 2f;

            float maxErrorFraction = 0f, worstV = 0f;
            for (int i = 0; i < Samples; i++)
            {
                float v = i * Max / (Samples - 1);
                float errorFraction = math.abs(Quantize.UnitBack(Quantize.Unit(v, Max), Max) - v) / Max;
                if (errorFraction > maxErrorFraction) { maxErrorFraction = errorFraction; worstV = v; }
            }

            Assert.LessOrEqual(maxErrorFraction, PlanMaxErrorFraction,
                $"plan contract: worst relative error {maxErrorFraction} at v={worstV} exceeds 0.5%");
            Assert.LessOrEqual(maxErrorFraction, halfStepFraction + FloatNoise(1f),
                $"sharp bound: worst relative error {maxErrorFraction} at v={worstV} exceeds half-step {halfStepFraction} plus float noise");
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
                float2 decoded = Quantize.DirBack(code);
                byte reencoded = Quantize.Dir(decoded);
                if (reencoded != code)
                    Assert.Fail($"Dir(DirBack({code})) = {reencoded}, expected {code} (Р34 idempotency)");

                // Fix-round finding F6: DirBack's contract is a UNIT vector,
                // and nothing pinned it — `DirBack => (cos, sin) * 2f`
                // survived every Dir test, because both Dir (atan2) and the
                // angular-difference helper are invariant to positive
                // scaling. Task 25 encodes MoveDir as angle + magnitude and
                // multiplies the decoded direction BY that magnitude, so a
                // non-unit DirBack would silently double movement speed and
                // break Task 30's prediction parity.
                float length = math.length(decoded);
                Assert.That(length, Is.EqualTo(1f).Within(1e-5f),
                    $"DirBack({code}) must return a UNIT vector, got length {length}");
            }
        }

        [Test]
        public void Unit_Idempotent_AllCodes_AcrossMax()
        {
            // Fix-round finding F7: the second fixture used to be 3.8f,
            // which is exactly HeroConfig.MaxAimHeight's shipped value, under
            // a comment claiming it was not a balance number. Replaced with a
            // value no .asset holds — the point of a second max is only that
            // it differs from the first (урок 93: a parameter no fixture
            // varies is an untested parameter).
            foreach (float max in new[] { 137f, 4.25f })
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

            // Fix-round finding F5: the three rails above do NOT pin the 3x
            // multiplier — with a 2*radius domain, -3*radius and +3*radius
            // simply CLAMP to the same two rails and all three asserts stay
            // green, so the mutation "Aim uses 2f*radius" survived the whole
            // suite. Р30 exists precisely because Sanitize allows AimPoint at
            // 2*Radius from the player while AimProvider casts to Radius*2,
            // i.e. up to 3*Radius from the centre; a silently narrowed domain
            // would clamp far aim points and only surface in Task 27/Task 30.
            // These two probe INSIDE the declared domain, where clamping
            // cannot hide the difference.
            Assert.Greater(Quantize.Aim(-2f * Radius, Radius), (ushort)0,
                "-2*radius lies INSIDE Aim's 3*radius domain and must not sit on the bottom rail (Р30)");
            Assert.That(Quantize.AimBack(ushort.MaxValue, Radius), Is.EqualTo(3f * Radius)
                    .Within(3f * Radius / 65535f + FloatNoise(6f * Radius)),
                "the top code must decode to +3*radius, not to some narrower range's edge (Р30)");
        }

        [Test]
        public void Unit_Boundaries_MapToExpectedCodes()
        {
            const float Max = 4.25f; // fixture: one-sided max, no balance number holds this value (F7)
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
            const float Max = 4.25f; // fixture, not a balance number (F7)
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

        [Test]
        public void Dir_NonFinite_DoesNotThrow_AndStaysALegalCode()
        {
            // Fix-round finding F8: Pos/Aim/Unit all had a non-finite test,
            // Dir had none, while Quantize's class doc promises "including
            // NaN/Infinity" for every codec here. Dir's path differs from the
            // others: there is no saturate to absorb NaN, so the value
            // reaches `(int)math.round(NaN)`, whose result C# leaves
            // UNSPECIFIED. What the mask `& 0xFF` guarantees — and all this
            // test may assert — is that the outcome is still a legal byte and
            // never an exception. The concrete code is deliberately NOT
            // pinned: it is platform-dependent (x86 and ARM64 happen to agree
            // on 0 today, IL2CPP need not), and pinning it would turn a
            // portability caveat into a false invariant.
            byte nanCode = 1, infCode = 1, mixedCode = 1;
            Assert.DoesNotThrow(() => nanCode = Quantize.Dir(new float2(float.NaN, 0f)), "NaN component must not throw");
            Assert.DoesNotThrow(() => infCode = Quantize.Dir(new float2(float.PositiveInfinity, float.PositiveInfinity)),
                "infinite components must not throw");
            Assert.DoesNotThrow(() => mixedCode = Quantize.Dir(new float2(float.NegativeInfinity, 1f)),
                "mixed infinite/finite components must not throw");

            // Every byte is a legal direction code, so the only assertable
            // contract is decodability: the code must round-trip through
            // DirBack into a finite unit vector.
            foreach (byte code in new[] { nanCode, infCode, mixedCode })
            {
                float2 decoded = Quantize.DirBack(code);
                Assert.IsTrue(math.all(math.isfinite(decoded)), $"DirBack({code}) must decode to a finite vector");
                Assert.That(math.length(decoded), Is.EqualTo(1f).Within(1e-5f), $"DirBack({code}) must stay a unit vector");
            }
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
            Assert.That(decodedPlus, Is.EqualTo(-decodedMinus).Within(halfStep + FloatNoise(2f * Radius)),
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
            const float Max = 137f; // no .asset holds this value (fix-round 2)
            const float V = Max / 3f;
            float decodedLow = Quantize.UnitBack(Quantize.Unit(V, Max), Max);
            float decodedHigh = Quantize.UnitBack(Quantize.Unit(Max - V, Max), Max);

            Assert.Greater(decodedLow, Max / 10f,
                "fixture premise: UnitBack(Unit(v)) must decode near v, not collapse to 0 on a stub");

            float halfStep = Max / 255f / 2f;
            Assert.That(decodedLow + decodedHigh, Is.EqualTo(Max).Within(2f * halfStep + FloatNoise(Max)),
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
                Assert.That(error, Is.LessThanOrEqualTo(halfStepDeg + FloatNoise(360f)),
                    $"DirBack({code}) must decode back within half a step ({halfStepDeg}deg) of {deg}deg");
            }
        }

        [Test]
        public void Dir_ZeroVector_GivesSameCodeAsPlusX()
        {
            // Decision (task-24-brief §2, mandatory-decisions item 3):
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
            const float Max = 4.25f;
            var v = new float2(12f, -7f);

            // Fix-round finding F9: warm up OUTSIDE the measured lambda, the
            // way AllocationTests.cs does — otherwise the first call of each
            // method is JIT-compiled inside the measurement and the test
            // measures the JIT, not the codec.
            Quantize.PosBack(Quantize.Pos(3f, Radius), Radius);
            Quantize.AimBack(Quantize.Aim(-40f, Radius), Radius);
            Quantize.DirBack(Quantize.Dir(v));
            Quantize.UnitBack(Quantize.Unit(2.1f, Max), Max);

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
