using NUnit.Framework;
using Ring.Presentation;

namespace Ring.Simulation.Tests
{
    /// bd `app-8dv` — the latch may hold MORE than one unconfirmed prediction
    /// for the shot, so a held burst on a networked client is drawn at its own
    /// cadence instead of waiting out a confirmation per round.
    ///
    /// THE DANGER THIS SUITE EXISTS TO PIN. "One unconfirmed prediction at a
    /// time" was not a simplification: it closes the SECOND rising edge that
    /// reconciliation hands out for a round already shown (`app-id9`, one shot
    /// shown twice). Lifting it wholesale brings that back. So the new rule
    /// keeps a floor on how CLOSE two predictions may be — a real burst is
    /// `FireInterval` apart, a reconciliation echo is not — and the caller
    /// supplies that floor, because a latch that read the weapon's cadence
    /// itself would be a gate built on somebody else's number (lesson 155).
    ///
    /// The dash keeps the old behaviour untouched: it calls the overload
    /// without a floor, and one unconfirmed dash prediction is still all it
    /// will hold (bd `app-g21`).
    public class ImmediatePredictionLatchTests
    {
        const float Window = ImmediatePredictionLatch.BufferedWindowSeconds;
        const float FireGap = 0.12f;      // WeaponConfig.FireInterval at the shipped balance

        /// What a caller supplies as the floor: BELOW the cadence, never equal
        /// to it. Frames do not land on tick boundaries and the accumulated
        /// float of a real clock drifts either way, so a floor AT the cadence
        /// refuses legitimate rounds — measured here as 0.11999997 against
        /// 0.12f. The margin is the caller's to choose; `SimulationRunner`
        /// ships four fifths of the interval.
        const float Floor = FireGap * 0.8f;

        static bool Pulse(ImmediatePredictionLatch latch, float now, float minGap)
        {
            // A gate pulse is a fall and a rise: the fire gate is one tick wide,
            // so every round arrives as its own edge.
            latch.ShouldPredict(false, now, minGap);
            return latch.ShouldPredict(true, now, minGap);
        }

        [Test]
        public void ABurstIsPredictedEveryRound_EvenWithNothingConfirmedYet()
        {
            var latch = new ImmediatePredictionLatch();
            float t = 0f;

            Assert.IsTrue(Pulse(latch, t, Floor), "the first round of the burst");
            latch.Arm(t, Window);

            // Nothing is confirmed in between — on a networked client the first
            // ProjectileFired is still ~0.27 s away (measured, bd app-a4k).
            for (int round = 2; round <= 4; round++)
            {
                t += FireGap;
                Assert.IsTrue(Pulse(latch, t, Floor),
                    $"round {round} must be predicted too — waiting for a confirmation that "
                    + "has not crossed the wire yet is the delay this task removes");
                latch.Arm(t, Window);
            }
        }

        [Test]
        public void AReconciliationEchoIsStillRefused()
        {
            var latch = new ImmediatePredictionLatch();

            Assert.IsTrue(Pulse(latch, 0f, Floor));
            latch.Arm(0f, Window);

            // The correction re-opens the gate for the round already shown, well
            // inside one FireInterval — that is what tells an echo from a round.
            Assert.IsFalse(Pulse(latch, 0.03f, Floor),
                "a second edge for the SAME round must not be shown a second time (app-id9)");
            Assert.IsFalse(Pulse(latch, 0.09f, Floor),
                "and it stays refused right up to the floor, however many edges arrive");
        }

        [Test]
        public void EachConfirmationConsumesExactlyOnePrediction()
        {
            var latch = new ImmediatePredictionLatch();

            Pulse(latch, 0f, Floor);
            latch.Arm(0f, Window);
            Pulse(latch, FireGap, Floor);
            latch.Arm(FireGap, Window);

            Assert.IsTrue(latch.TryConsume(FireGap), "the first round's event");
            Assert.IsTrue(latch.TryConsume(FireGap), "the second round's event");
            Assert.IsFalse(latch.TryConsume(FireGap),
                "a third event has no prediction behind it and must show itself");
        }

        [Test]
        public void AnUnconfirmedPredictionIsForgottenByItsOwnWindow()
        {
            var latch = new ImmediatePredictionLatch();

            Pulse(latch, 0f, Floor);
            latch.Arm(0f, Window);

            Assert.IsFalse(latch.TryConsume(Window + 0.01f),
                "a prediction nobody ever confirmed must not suppress a later round's event");
        }

        [Test]
        public void PredictionsBeyondTheTableAreRefusedRatherThanForgotten()
        {
            var latch = new ImmediatePredictionLatch();
            float t = 0f;

            // Far more rounds than any confirmation delay can leave outstanding
            // (0.27 s measured against a 0.12 s cadence is two or three).
            int predicted = 0;
            for (int i = 0; i < 32; i++)
            {
                if (Pulse(latch, t, Floor))
                {
                    latch.Arm(t, Window);
                    predicted++;
                }
                t += FireGap;
            }

            Assert.AreEqual(32, predicted,
                "the window expires each prediction long before the table can fill, so a "
                + "held trigger never runs out of predictions");
        }

        [Test]
        public void TheDashKeepsOneUnconfirmedPredictionAtATime()
        {
            var latch = new ImmediatePredictionLatch();

            latch.ShouldPredict(false, 0f);
            Assert.IsTrue(latch.ShouldPredict(true, 0f), "the dash's own edge");
            latch.Arm(0f, Window);

            latch.ShouldPredict(false, 0.1f);
            Assert.IsFalse(latch.ShouldPredict(true, 0.1f),
                "bd app-g21's rule is untouched by this task: with no floor supplied, one "
                + "unconfirmed prediction is still all the latch holds");
        }

        [Test]
        public void AnEventShownFirstStillSwallowsExactlyOneEdge()
        {
            var latch = new ImmediatePredictionLatch();

            latch.NoteShownFromEvent(0f, Window);
            Assert.IsFalse(Pulse(latch, 0f, Floor),
                "the third fact of the class doc survives the new counting");
            Assert.IsTrue(Pulse(latch, FireGap, Floor),
                "and the credit is spent by that one edge, not by the next act too");
        }
    }
}
