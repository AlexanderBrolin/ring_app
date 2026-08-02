using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    public class AccumulatorTests
    {
        [Test]
        public void AccumulatesFractionsAcrossFrames()
        {
            var acc = new FixedStepAccumulator();
            int total = 0;
            for (int i = 0; i < 30; i++) total += acc.Advance(1f / 60f); // 0.5 s
            Assert.AreEqual(15, total); // 0.5 / (1/30)
        }

        [Test]
        public void BigFrame_ManyTicks_NoLoss()
        {
            var acc = new FixedStepAccumulator();
            // 0.21 s / (1/30) = 6.3 — clear of the float boundary (0.2f sits exactly on it)
            Assert.AreEqual(6, acc.Advance(0.21f));
            Assert.That(acc.Alpha, Is.InRange(0f, 1f));
        }

        [Test]
        public void FrameInput_EdgeLatchConsumedByFirstTickOnly()
        {
            var frame = new SimInput { FireHeld = true, DashRequested = true };
            Assert.IsTrue(SimInputFrame.ForTick(frame, 0).DashRequested);
            Assert.IsFalse(SimInputFrame.ForTick(frame, 1).DashRequested); // one dash per frame
            Assert.IsTrue(SimInputFrame.ForTick(frame, 1).FireHeld); // held — into every tick
        }

        [Test]
        public void FrameSpike_CappedAndReported()
        {
            var acc = new FixedStepAccumulator();
            int n = acc.Advance(2f);
            Assert.AreEqual((int)(0.25f / SimulationWorld.TickDt), n); // 7
            Assert.AreEqual(1.75f, acc.DroppedTime, 1e-4f);
        }

        [Test]
        public void SameTotalTime_SameTickCount_RegardlessOfFraming()
        {
            var a = new FixedStepAccumulator(); var b = new FixedStepAccumulator();
            int na = 0, nb = 0;
            for (int i = 0; i < 100; i++) na += a.Advance(0.0177f);
            for (int i = 0; i < 59; i++) nb += b.Advance(0.03f);
            Assert.AreEqual(53, na); // 1.77 s
            Assert.AreEqual(53, nb); // 1.77 s
        }

        [Test]
        public void Reset_ClearsAccumulatorAndAlpha()
        {
            var acc = new FixedStepAccumulator();
            acc.Advance(0.02f);
            acc.Reset();
            Assert.AreEqual(0f, acc.Alpha);
        }

        [Test]
        public void ResetAccumulatorOnly_ZeroesAlpha_PreservesDroppedTime()
        {
            var acc = new FixedStepAccumulator();
            acc.Advance(2f); // same spike as FrameSpike_CappedAndReported — DroppedTime = 1.75
            acc.ResetAccumulatorOnly();

            Assert.AreEqual(0f, acc.Alpha);
            Assert.AreEqual(1.75f, acc.DroppedTime, 1e-4f); // NOT cleared — unlike Reset()
        }
    }
}
