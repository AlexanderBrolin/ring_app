using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// The spray pattern as a pure function (app-8dv T1, spec §4.4 tests 1, 3,
    /// 4a-c, 7-9). Tests 2, 5, 6, 10-12 live in WeaponTests instead: they
    /// examine Advance and need a world and an event rather than the formula
    /// (finding I4-3).
    public class SprayPatternTests
    {
        const float Eps = 1e-6f;

        /// The fixture's numbers are named HERE rather than taken from
        /// TestConfigs: this test is about the SHAPE of the curve, and the
        /// curve has to be visible next to the expectation.
        static WeaponSimConfig Pattern(float variance = 0f) => new WeaponSimConfig
        {
            SprayPatternShots = 12,
            SprayYawAmplitude = 1f,
            SprayYawTurns = 0.7f,
            SprayPitchAmplitude = 0.35f,
            SprayVariance = variance,
        };

        [Test]
        public void SameArguments_GiveTheSameAngle()   // test 1
        {
            var w = Pattern(0.35f);
            float2 a = SprayPattern.Draw(3, 41, new float2(7.5f, -2.25f), 0.0959f, in w);
            float2 b = SprayPattern.Draw(3, 41, new float2(7.5f, -2.25f), 0.0959f, in w);
            Assert.AreEqual(a.x, b.x, Eps, "горизонталь не воспроизводится");
            Assert.AreEqual(a.y, b.y, Eps, "вертикаль не воспроизводится");
        }

        [Test]
        public void SecondHalfOfTheBurst_DeviatesMoreOnAverage()   // test 3, M233
        {
            // The MEAN, not the maximum: measured by the maximum, the "no amp"
            // mutant survived (0.97 against 1.00). On the mean the sign of the
            // comparison flips: the correct code gives 0.250 against 0.442, the
            // mutant 0.781 against 0.523.
            var w = Pattern();
            double first = 0d, second = 0d;
            for (int k = 0; k < 6; k++) first += math.abs(Yaw(k, in w));
            for (int k = 6; k < 12; k++) second += math.abs(Yaw(k, in w));
            Assert.Greater(second / 6d, first / 6d,
                "конец очереди отклоняется не сильнее начала — амплитуда не растёт");
            Assert.AreEqual(0.250d, first / 6d, 0.005d, "премисса: средняя первой половины");
            Assert.AreEqual(0.442d, second / 6d, 0.005d, "премисса: средняя второй половины");
        }

        [Test]
        public void Amplitude_SaturatesAtThePatternLength()   // test 4a, M264
        {
            var w = Pattern();
            // The vertical is the monotone half of the pattern, so saturation
            // is visible on it without picking apart the sign of the sine.
            float atLength = Pitch(11, in w);
            float beyond = Pitch(47, in w);
            Assert.AreEqual(atLength, beyond, Eps,
                "амплитуда не насыщается — рисунок продолжает расти за своей длиной");
        }

        [Test]
        public void Yaw_KeepsChangingBeyondThePatternLength()   // test 4b, M234
        {
            // ⚠ THE WORDING WAS REWRITTEN BY ROUND 2 OF THE SPEC: the previous
            // one ("zero on both axes") did NOT kill the mutant -- with the
            // phase saturated the sine freezes on a constant, and pitch is
            // always positive. What is measured here is the SPAN: the correct
            // code gives 1.988, the mutant exactly zero.
            var w = Pattern();
            float min = float.MaxValue, max = float.MinValue;
            for (int k = 11; k <= 35; k++)
            {
                float y = Yaw(k, in w);
                min = math.min(min, y); max = math.max(max, y);
            }
            Assert.Greater(max - min, 1.5f,
                "yaw замер за длиной рисунка — насыщена фаза, а не только амплитуда");
        }

        [Test]
        public void TheFirstShotIsAlreadyOffCenter()   // test 4c, M232/M235
        {
            // k being 1-based is a DECISION, not a detail: 0-based, both axes
            // give zero on the first shot, and tapping single rounds would be
            // perfectly accurate at any range.
            var w = Pattern();
            float2 first = SprayPattern.Draw(0, 0, float2.zero, 1f, in w);
            Assert.Greater(math.length(first), 0.02f,
                "первый выстрел идеально точен — k стал 0-based или рисунок константа");
            Assert.AreEqual(0.042d, math.length(first), 0.002d,
                "премисса: суммарное отклонение первого выстрела — 0.042 конуса");
        }

        [Test]
        public void VarianceZeroIsPurePattern_AndOneIsAUniformDraw()   // test 7, M238
        {
            var pure = Pattern(0f);
            var random = Pattern(1f);
            float2 a = SprayPattern.Draw(4, 4, new float2(3f, 1f), 1f, in pure);
            float2 b = SprayPattern.Draw(4, 4, new float2(3f, 1f), 1f, in random);
            Assert.AreEqual(Yaw(4, in pure), a.x, Eps, "при variance 0 остаётся чистый рисунок");
            Assert.AreNotEqual(a.x, b.x, "при variance 1 горизонталь обязана уехать в бросок");
            // ⭐ AND ONLY TOGETHER WITH SprayPitchAmplitude = 0 is this today's
            // behavior: at variance 1 the vertical REMAINS, while today there
            // is no vertical at all.
            var todays = Pattern(1f); todays.SprayPitchAmplitude = 0f;
            Assert.AreEqual(0f, SprayPattern.Draw(4, 4, new float2(3f, 1f), 1f, in todays).y, Eps,
                "откат — ДВА числа: одна variance вертикаль не убирает");
        }

        [Test]
        public void TheTwoAxesAreUncorrelated()   // test 8, M239
        {
            // One salt for both axes would collapse the random area into a
            // segment of a straight line -- the same class of defect round 1
            // found in the pattern itself.
            var w = Pattern(1f);   // a pure draw: the pattern masks no correlation
            double sx = 0d, sy = 0d, sxy = 0d, sxx = 0d, syy = 0d;
            const int N = 2048;
            for (int i = 0; i < N; i++)
            {
                float2 d = SprayPattern.Draw(i % 12, i, new float2(i * 0.37f, -i * 0.11f), 1f, in w);
                sx += d.x; sy += d.y; sxy += (double)d.x * d.y;
                sxx += (double)d.x * d.x; syy += (double)d.y * d.y;
            }
            double varX = sxx / N - (sx / N) * (sx / N);
            double varY = syy / N - (sy / N) * (sy / N);
            // ⛔ THE PREMISE IS MANDATORY, AND IT STANDS AGAINST A DEGENERATE
            // Hash01 RATHER THAN AGAINST M239: on zero variances r = 0/0 = NaN,
            // and NUnit reduces Assert.Less to Double.CompareTo, where NaN is
            // less than any number -- that is, the test would pass on a
            // constant stub and would witness nothing. (On M239 itself both
            // axes are non-zero, r is about 1, and it survives without this
            // line.)
            Assert.Greater(varX, 1e-6d, "премисса: горизонталь обязана разбрасываться");
            Assert.Greater(varY, 1e-6d, "премисса: вертикаль обязана разбрасываться");
            double cov = sxy / N - (sx / N) * (sy / N);
            double r = cov / math.sqrt(varX * varY);
            Assert.Less(math.abs(r), 0.1d, "оси коррелируют — соль у них одна");
        }

        [Test]
        public void TheSeedComesFromTheShotOrdinal_NotFromTheBurstCounter()   // test 9, M240
        {
            // Two bursts carrying the SAME number within the burst and a
            // different number within the match must get a different random
            // addend.
            var w = Pattern(1f);
            float2 a = SprayPattern.Draw(3, 3, new float2(5f, 5f), 1f, in w);
            float2 b = SprayPattern.Draw(3, 99, new float2(5f, 5f), 1f, in w);
            Assert.AreNotEqual(a.x, b.x, "посев не зависит от номера выстрела за матч");
        }

        static float Yaw(int burstShots, in WeaponSimConfig w)
            => SprayPattern.Draw(burstShots, 0, float2.zero, 1f, in w).x;
        static float Pitch(int burstShots, in WeaponSimConfig w)
            => SprayPattern.Draw(burstShots, 0, float2.zero, 1f, in w).y;
    }
}
