using NUnit.Framework;
using Ring.Presentation;

namespace Ring.Simulation.Tests
{
    /// The three facts of `ImmediatePredictionLatch`, pinned by tests for the
    /// first time — before bd `app-8dv` opened `Simulation.Tests` onto
    /// `Ring.Presentation`, nothing but a playtest covered this class at all.
    ///
    /// WHAT THIS SUITE IS **NOT**. It is not the queue `app-8dv` asked for.
    /// That attempt was reverted after review: the floor it used to tell a
    /// reconciliation echo from a genuine next round (four fifths of
    /// `FireInterval` = 96 ms) is smaller than the echo's own measured delay
    /// (~167 ms: 267 ms of confirmation less the 100 ms interpolation buffer
    /// the reconcile does not wait out), so it did not close `app-id9` — one
    /// shot shown twice — while `WeaponSystem`'s own cadence quantizes to
    /// 100/133 ms and left that floor a 4 ms margin. The task stays open with
    /// the numbers written down; these tests keep the CURRENT rule honest in
    /// the meantime.
    public class ImmediatePredictionLatchTests
    {
        const float Window = ImmediatePredictionLatch.BufferedWindowSeconds;
        const float FireGap = 0.12f;

        static bool Pulse(ImmediatePredictionLatch latch, float now)
        {
            // A gate pulse is a fall and a rise: the fire gate is one tick wide,
            // so every round arrives as its own edge.
            latch.ShouldPredict(false, now);
            return latch.ShouldPredict(true, now);
        }

        [Test]
        public void OnlyARisingEdgeIsEverActedOn()
        {
            var latch = new ImmediatePredictionLatch();

            Assert.IsTrue(latch.ShouldPredict(true, 0f), "the first frame the gate is up");
            Assert.IsFalse(latch.ShouldPredict(true, 0.01f),
                "the gate is a LEVEL — acting on it every frame would predict once per frame "
                + "instead of once per act");
        }

        [Test]
        public void OneUnconfirmedPredictionAtATime()
        {
            var latch = new ImmediatePredictionLatch();

            Assert.IsTrue(Pulse(latch, 0f));
            latch.Arm(0f, Window);

            Assert.IsFalse(Pulse(latch, FireGap),
                "this is what closes app-id9: reconciliation hands out a second rising edge "
                + "for a round already shown, and only ONE event ever arrives to swallow it");
        }

        [Test]
        public void AConfirmationFreesTheNextPrediction()
        {
            var latch = new ImmediatePredictionLatch();

            Pulse(latch, 0f);
            latch.Arm(0f, Window);
            Assert.IsTrue(latch.TryConsume(0.05f), "the round's own event");

            Assert.IsTrue(Pulse(latch, FireGap), "and the next round is predicted normally");
        }

        [Test]
        public void AnEventWithNothingArmedShowsItself()
        {
            var latch = new ImmediatePredictionLatch();

            Assert.IsFalse(latch.TryConsume(0f),
                "nothing was predicted, so the event has nothing to swallow and the caller "
                + "must show the act itself");
        }

        [Test]
        public void AnUnconfirmedPredictionIsForgottenByItsOwnWindow()
        {
            var latch = new ImmediatePredictionLatch();

            Pulse(latch, 0f);
            latch.Arm(0f, Window);

            Assert.IsFalse(latch.TryConsume(Window + 0.01f),
                "a prediction nobody ever confirmed must not suppress a later round's event");
            Assert.IsTrue(Pulse(latch, Window + 0.02f),
                "nor block the next act's prediction forever");
        }

        [Test]
        public void AnEventShownFirstSwallowsExactlyOneEdge()
        {
            var latch = new ImmediatePredictionLatch();

            latch.NoteShownFromEvent(0f, Window);
            Assert.IsFalse(Pulse(latch, 0f),
                "the dash's own order: the event reaches the view before the edge does, and "
                + "the act must not be shown twice");
            Assert.IsTrue(Pulse(latch, FireGap),
                "and the credit is spent by that one edge, not by the next act too");
        }

        [Test]
        public void ACreditIsForgottenByItsOwnWindowToo()
        {
            var latch = new ImmediatePredictionLatch();

            latch.NoteShownFromEvent(0f, Window);
            Assert.IsTrue(Pulse(latch, Window + 0.01f),
                "an unspent credit must not cost the NEXT act its prediction — that is the "
                + "very lateness the window exists to bound");
        }

        [Test]
        public void AnEdgeIsSpentWhetherOrNotItIsGranted()
        {
            var latch = new ImmediatePredictionLatch();

            latch.NoteShownFromEvent(0f, Window);
            Assert.IsFalse(Pulse(latch, 0f), "the credit refuses this edge");

            // The gate is still up; a caller asking again on the next frame is
            // asking about the SAME act, and there is no new edge in it.
            Assert.IsFalse(latch.ShouldPredict(true, 0.01f),
                "no fall, no rise, no act");
        }
    }
}
