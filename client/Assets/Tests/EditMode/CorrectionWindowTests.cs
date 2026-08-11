using NUnit.Framework;
using Ring.Networking.Client;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 48 (plan Ф9 :2100-2107, spec §3.14 item 7): the rolling
    /// median of reconciliation corrections the lag gate В3 is written in.
    ///
    /// THE MEDIAN IS THE POINT, AND A MEAN IS THE DEFECT THESE TESTS EXIST
    /// FOR. The gate reads one number off the dev overlay and compares it
    /// against 0.25 m. A single teleport — a dash mispredicted through a wall,
    /// one state packet arriving after a long stall — is a correction of
    /// several metres, and an average over a window would let that one sample
    /// hold the reported figure above the threshold for the whole window while
    /// the connection it is supposed to describe is healthy.
    /// `Median_IsNotTheMean` is the test that tells the two apart.
    ///
    /// NUMBERS ARE THE FIXTURES' OWN. Nothing here reads an `.asset` or the
    /// production capacity — every test names the window size it wants, so a
    /// retune of `PlayerPredictionCore.CorrectionWindowSamples` cannot make a
    /// green test go red for a reason that has nothing to do with the median.
    public class CorrectionWindowTests
    {
        [Test]
        public void EmptyWindow_HasNothingToShow()
        {
            var window = new CorrectionWindow(8);

            // `Count == 0` IS the "nothing to show" test — the panel prints a
            // dash off it — so there is no third member and no sentinel
            // median. Zero is a legitimate median (a perfect prediction
            // reconciles to exactly zero, `PlayerPredictionCore.
            // FinishReconcile`), which is precisely why it cannot double as
            // "no samples".
            Assert.AreEqual(0, window.Count);
            Assert.AreEqual(8, window.Capacity);
        }

        [Test]
        public void OddSampleCount_MedianIsTheMiddleSample()
        {
            var window = new CorrectionWindow(8);
            window.Record(5f);
            window.Record(1f);
            window.Record(3f);

            Assert.AreEqual(3, window.Count);
            Assert.AreEqual(3f, window.MedianMeters, 1e-5f);
        }

        [Test]
        public void EvenSampleCount_MedianIsTheMeanOfTheTwoMiddleSamples()
        {
            var window = new CorrectionWindow(8);
            window.Record(1f);
            window.Record(2f);
            window.Record(3f);
            window.Record(4f);

            Assert.AreEqual(4, window.Count);
            Assert.AreEqual(2.5f, window.MedianMeters, 1e-5f);
        }

        [Test]
        public void Median_IsNotTheMean()
        {
            var window = new CorrectionWindow(8);
            // Four ordinary corrections and one teleport. The mean is 2.02 —
            // eight times the lag gate's 0.25 m threshold — while the median
            // is 0.1, which is what the connection actually looks like.
            window.Record(0.1f);
            window.Record(0.1f);
            window.Record(0.1f);
            window.Record(10f);
            window.Record(0.1f);

            Assert.AreEqual(0.1f, window.MedianMeters, 1e-5f);
        }

        [Test]
        public void WindowOverflow_OldestSamplesAreEvicted()
        {
            var window = new CorrectionWindow(3);
            window.Record(100f);
            window.Record(100f);
            window.Record(100f);
            window.Record(1f);
            window.Record(2f);
            window.Record(3f);

            // The median describes the last `Capacity` corrections; `Count`
            // describes the whole match, which is why it keeps climbing past
            // the window size.
            Assert.AreEqual(6, window.Count);
            Assert.AreEqual(2f, window.MedianMeters, 1e-5f);
        }

        [Test]
        public void ArrivalOrder_DoesNotChangeTheMedian()
        {
            var ascending = new CorrectionWindow(8);
            ascending.Record(1f);
            ascending.Record(2f);
            ascending.Record(3f);

            var shuffled = new CorrectionWindow(8);
            shuffled.Record(3f);
            shuffled.Record(1f);
            shuffled.Record(2f);

            Assert.AreEqual(2f, ascending.MedianMeters, 1e-5f);
            Assert.AreEqual(2f, shuffled.MedianMeters, 1e-5f);
        }

        /// Stage 2 Task 48 fix-round 1 (axis A, finding I-3): the class doc
        /// declares "HOSTILE CAPACITY IS REFUSED, NEVER THROWN (Р82)" and the
        /// constructor floors a non-positive capacity at one — an absolute
        /// that no test held. Every fixture above hands the constructor a
        /// legal size (8, 8, 8, 8, 3, 8, 4), so the floor could be deleted and
        /// all seven would stay green. The precedent for pinning it is next
        /// door and arrived the same way, as a review finding:
        /// `StalePolicyTests.Constructor_ClampsNonPositiveTunings`.
        ///
        /// A FLOOR AND NOT A THROW, because of who the readers are: a dev
        /// overlay and milestone В3's lag gate. `GhostProjectiles` and
        /// `StalePolicy` clamp their own capacities the same way for the same
        /// reason.
        [Test]
        public void Constructor_FloorsNonPositiveCapacityAtOne()
        {
            Assert.AreEqual(1, new CorrectionWindow(0).Capacity,
                "a capacity of zero must floor at one slot — a window that can "
                + "hold no sample has no median to define");

            var window = new CorrectionWindow(-3);
            Assert.AreEqual(1, window.Capacity,
                "a negative capacity must floor the same way; unfloored it "
                + "reaches `new float[-3]`, which is the exception Р82 refuses "
                + "to raise at a caller");

            // And the floored window WORKS rather than merely constructs: one
            // slot that records, evicts and reports the sample it holds.
            window.Record(5f);
            Assert.AreEqual(1, window.Count);
            Assert.AreEqual(5f, window.MedianMeters, 1e-5f);

            window.Record(7f);
            Assert.AreEqual(2, window.Count,
                "`Count` is the whole run and climbs past the window size, "
                + "exactly as in WindowOverflow_OldestSamplesAreEvicted");
            Assert.AreEqual(7f, window.MedianMeters, 1e-5f,
                "the single slot now holds the newer sample — the ring cursor "
                + "evicted the older one, which is what a capacity of one means");
        }

        [Test]
        public void Reset_ForgetsEverySample()
        {
            var window = new CorrectionWindow(4);
            window.Record(7f);
            window.Record(9f);
            window.Reset();

            Assert.AreEqual(0, window.Count);

            window.Record(1f);
            Assert.AreEqual(1, window.Count);
            Assert.AreEqual(1f, window.MedianMeters, 1e-5f);
        }
    }
}
