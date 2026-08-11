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
