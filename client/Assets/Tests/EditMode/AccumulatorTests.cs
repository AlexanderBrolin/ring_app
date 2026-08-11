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

        // ---- Stage 2 Task 48 (bd app-c3m): the accounting, not the clamp ----

        [Test]
        public void IgnoreNextFrameGap_LongFrame_IsNotCounted()
        {
            var acc = new FixedStepAccumulator();
            acc.IgnoreNextFrameGap();
            acc.Advance(2f); // the same spike FrameSpike_CappedAndReported charges 1.75 for

            Assert.AreEqual(0f, acc.DroppedTime, 1e-4f);
        }

        [Test]
        public void IgnoreNextFrameGap_LastsExactlyOneFrame()
        {
            var acc = new FixedStepAccumulator();
            acc.IgnoreNextFrameGap();
            acc.Advance(2f); // excused
            acc.Advance(2f); // charged in full — the flag is not "until somebody clears it"

            Assert.AreEqual(1.75f, acc.DroppedTime, 1e-4f);
        }

        [Test]
        public void IgnoreNextFrameGap_SpendsItselfOnAnOrdinaryFrame()
        {
            var acc = new FixedStepAccumulator();
            acc.IgnoreNextFrameGap();
            acc.Advance(1f / 60f); // the frame the engine actually came back on was short
            acc.Advance(2f); // a REAL hitch, one frame later — it must still be reported

            Assert.AreEqual(1.75f, acc.DroppedTime, 1e-4f);
        }

        /// THE HASH-NEUTRALITY PROOF (bd `app-c3m`, task 48 brief §2.6 item 4).
        /// `DroppedTime` is not in `StateHash`, but the clamp that feeds it
        /// decides HOW MANY TICKS a frame produces, and the tick count decides
        /// everything. This pins that the flag changes the accounting and
        /// nothing else: same frames, same tick counts, same interpolation
        /// phase — only the diagnostic differs.
        [Test]
        public void IgnoreNextFrameGap_ChangesNoTickCountAndNoPhase()
        {
            var charged = new FixedStepAccumulator();
            var excused = new FixedStepAccumulator();
            excused.IgnoreNextFrameGap();

            int chargedTicks = charged.Advance(2f);
            int excusedTicks = excused.Advance(2f);

            Assert.AreEqual(chargedTicks, excusedTicks);
            Assert.AreEqual(charged.Alpha, excused.Alpha, 0f);

            // And the frames after it, so the divergence cannot merely be
            // deferred by one frame: the phase carried forward is the same,
            // so every later tick boundary falls in the same place.
            for (int i = 0; i < 40; i++)
            {
                Assert.AreEqual(charged.Advance(0.0177f), excused.Advance(0.0177f));
                Assert.AreEqual(charged.Alpha, excused.Alpha, 0f);
            }

            Assert.AreEqual(1.75f, charged.DroppedTime, 1e-4f);
            Assert.AreEqual(0f, excused.DroppedTime, 1e-4f);
        }

        [Test]
        public void Reset_ClearsAPendingIgnore()
        {
            var acc = new FixedStepAccumulator();
            acc.IgnoreNextFrameGap();
            acc.Reset(); // a fresh match starts its own diagnostic — and its own excuses
            acc.Advance(2f);

            Assert.AreEqual(1.75f, acc.DroppedTime, 1e-4f);
        }
    }
}
