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

        /// The same pulse, carrying the shot's identity (app-8dv T6). The dash
        /// callers deliberately have no such helper: they pass no key at all,
        /// which is what the eight fixtures above go on exercising.
        static bool Pulse(ImmediatePredictionLatch latch, float now, int key)
        {
            latch.ShouldPredict(false, now, key);
            return latch.ShouldPredict(true, now, key);
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
        // ---- app-8dv T6: a queue keyed by the shot, not a single latch ----

        /// M265's witness, and the whole point of the task. THE BURST IS SHOWN
        /// WHOLE. Before this, the second round of a held burst found the first
        /// one still waiting for its event and was refused, so it was heard and
        /// seen only when its own event arrived -- 267 ms late, which is the
        /// complaint the owner filed at milestone В4 word for word ("after the
        /// first shot there is a delay during the spray").
        [Test]
        public void ThreeRoundsOfOneBurst_AreAllPredicted_AndEachIsConsumedOnce()
        {
            var latch = new ImmediatePredictionLatch();

            for (int shot = 1; shot <= 3; shot++)
            {
                float now = (shot - 1) * FireGap;
                Assert.IsTrue(Pulse(latch, now, key: shot),
                    $"round {shot} of the burst must be predicted, not made to wait for the "
                    + "round before it to be confirmed");
                latch.Arm(now, Window, key: shot);
            }

            for (int shot = 1; shot <= 3; shot++)
            {
                Assert.IsTrue(latch.TryConsume(3f * FireGap),
                    $"the event of round {shot} finds its own record and suppresses the "
                    + "duplicate feedback");
            }

            Assert.IsFalse(latch.TryConsume(3f * FireGap),
                "and nothing is left over: three rounds shown, three records spent");
        }

        /// M254's witness, and the reason the queue is KEYED rather than plain.
        /// Reconciliation hands out a SECOND rising edge for a round already
        /// shown -- `BeginReconcile` assigns the authoritative state whole, the
        /// cooldown steps back, and the gate goes false->true again -- while
        /// only ONE event ever arrives. A bare queue would show that round
        /// twice, which is exactly the defect `app-id9` recorded.
        ///
        /// BOTH ORDERS, because the echo can arrive on either side of the
        /// event and only one of them is covered by "something is still
        /// waiting".
        [Test]
        public void AReconciliationEcho_ShowsNothingTwice_InEitherOrder()
        {
            // (a) the echo arrives BEFORE the event.
            var beforeEvent = new ImmediatePredictionLatch();
            Assert.IsTrue(Pulse(beforeEvent, 0f, key: 1));
            beforeEvent.Arm(0f, Window, key: 1);

            Assert.IsFalse(Pulse(beforeEvent, 0.05f, key: 1),
                "the same ShotOrdinal is the same act: a replayed edge for a round already "
                + "shown must not show it again");

            Assert.IsTrue(beforeEvent.TryConsume(0.06f), "and its one event still lands");

            // (b) the echo arrives AFTER the event has already consumed the
            // record -- the order "still waiting" cannot possibly catch.
            var afterEvent = new ImmediatePredictionLatch();
            Assert.IsTrue(Pulse(afterEvent, 0f, key: 1));
            afterEvent.Arm(0f, Window, key: 1);
            Assert.IsTrue(afterEvent.TryConsume(0.02f));

            Assert.IsFalse(Pulse(afterEvent, 0.05f, key: 1),
                "the queue is empty by now, so only a memory of SHOWN keys can refuse this "
                + "-- which is why the ring exists beside the queue");

            // The next genuine round is not collateral damage.
            Assert.IsTrue(Pulse(afterEvent, FireGap, key: 2),
                "a different ShotOrdinal is a different act and is predicted normally");
        }

        /// M255's witness. A match restart hands the new match a fresh ordinal
        /// counter that starts at 1 again -- so a latch still remembering the
        /// previous match's keys would swallow the new match's first rounds
        /// silently.
        ///
        /// AND `_gateWasSatisfied` GOES WITH THEM, which is not a formality:
        /// the class header states outright that it survives a restart and that
        /// "a player who holds Fire across a restart gets no prediction for the
        /// first round of the new match". This test promises the opposite, so
        /// the field is named in `Reset` and the header says so.
        [Test]
        public void ARestartForgetsTheShownKeys_SoTheNewMatchsFirstRoundIsPredicted()
        {
            var latch = new ImmediatePredictionLatch();

            Assert.IsTrue(Pulse(latch, 0f, key: 1));
            latch.Arm(0f, Window, key: 1);
            Assert.IsTrue(latch.TryConsume(0.02f));
            Assert.IsFalse(Pulse(latch, 0.05f, key: 1),
                "fixture premise: key 1 is remembered as shown");

            latch.Reset();

            Assert.IsTrue(latch.ShouldPredict(true, 0.06f, key: 1),
                "a held trigger across the restart still gets the new match's first round "
                + "predicted: the gate memory is cleared with everything else, so this frame "
                + "IS a rising edge");
        }

        /// M257's witness, and the defect the G-4 comment in `AudioDirector`
        /// predicted in writing: "one shot's event could consume a record
        /// another shot had left behind".
        ///
        /// A grant now leaves a record whether or not the caller manages to
        /// SHOW anything -- a voice the SFX gates refused, a frame with no doll
        /// to fire from. Such a record must be invisible to `TryConsume`: an
        /// event that swallowed it would suppress feedback for an act nobody
        /// ever saw, and the shot would be lost entirely.
        [Test]
        public void AnUnmarkedRecordIsNotConsumed_SoNoShotLosesItsFeedback()
        {
            var latch = new ImmediatePredictionLatch();

            Assert.IsTrue(Pulse(latch, 0f, key: 1),
                "fixture premise: round 1 is granted...");
            // ...and deliberately NOT armed: PlayClip returned false, the voice
            // was gated out, nothing was shown.

            Assert.IsTrue(Pulse(latch, FireGap, key: 2), "round 2 is granted...");
            latch.Arm(FireGap, Window, key: 2);   // ...and this one really was shown.

            Assert.IsTrue(latch.TryConsume(FireGap + 0.01f),
                "the first event finds the SHOWN record of round 2 and suppresses its "
                + "duplicate");
            Assert.IsFalse(latch.TryConsume(FireGap + 0.02f),
                "and round 1's unshown record is not there to be eaten: its own event must "
                + "play the sound normally, or the shot is silent");
        }

    }
}
