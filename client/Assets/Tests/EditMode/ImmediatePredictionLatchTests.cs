using NUnit.Framework;
using Ring.Presentation;

namespace Ring.Simulation.Tests
{
    /// The three facts of `ImmediatePredictionLatch`, pinned by tests for the
    /// first time — before bd `app-8dv` opened `Simulation.Tests` onto
    /// `Ring.Presentation`, nothing but a playtest covered this class at all.
    ///
    /// AND, SINCE app-8dv T6, THE QUEUE — built the second time and on a
    /// different principle, which is why the first attempt is still described
    /// here. That one told a reconciliation echo from a genuine next round by
    /// TIME: a floor of four fifths of `FireInterval` = 96 ms, smaller than the
    /// echo's own measured delay (~167 ms: 267 ms of confirmation less the
    /// 100 ms interpolation buffer the reconcile does not wait out), so it did
    /// not close `app-id9` — one shot shown twice — while `WeaponSystem`'s own
    /// cadence quantizes to 100/133 ms and left that floor a 4 ms margin. It
    /// was reverted after review.
    /// ⇒ T6 keys the records by `ShotOrdinal` instead, so an echo is refused by
    /// IDENTITY and no number has to separate it from the next round. The eight
    /// fixtures written for the single-latch rule are kept untouched: they now
    /// pin the KEYLESS half of the class, which is the dash's, and a mutation
    /// that routes a dash through the queue reddens six of them.
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
        /// A grant leaves a record whether or not the caller manages to SHOW
        /// anything -- a voice the SFX gates refused, a frame with no doll to
        /// fire from. What such a record must NOT do is shift the
        /// correspondence between events and rounds: events arrive in the order
        /// their rounds were fired (`ClientEventQueue` delivers by tick, and by
        /// the server's own `seq` within a tick), so the k-th event belongs to
        /// the k-th record. `TryConsume` therefore TAKES the head record and
        /// answers with its `Shown` flag.
        ///
        /// ⛔⛔ THE FIRST VERSION OF THIS TEST ASSERTED THE OPPOSITE AND WAS
        /// GREEN, WHICH IS WHY THE WORDING BELOW IS SO EXPLICIT ABOUT WHICH
        /// EVENT IS WHICH. It let `TryConsume` SKIP the unmarked record, so
        /// round 1's own event ate round 2's record: round 1 fell silent and
        /// round 2 was heard twice -- a lost shot and `app-id9` in one input,
        /// pinned as if it were correct.
        [Test]
        public void AnUnmarkedRecordAnswersItsOwnEvent_WithoutStealingTheNextRounds()
        {
            var latch = new ImmediatePredictionLatch();

            Assert.IsTrue(Pulse(latch, 0f, key: 1),
                "fixture premise: round 1 is granted...");
            // ...and deliberately NOT armed: PlayClip returned false, the voice
            // was gated out, nothing was shown.

            Assert.IsTrue(Pulse(latch, FireGap, key: 2), "round 2 is granted...");
            latch.Arm(FireGap, Window, key: 2);   // ...and this one really was shown.

            Assert.IsFalse(latch.TryConsume(FireGap + 0.01f),
                "ROUND 1's OWN EVENT: its record says nothing was shown, so the caller must "
                + "play the sound now -- suppressing it here is how a shot goes silent");
            Assert.IsTrue(latch.TryConsume(FireGap + 0.02f),
                "ROUND 2's OWN EVENT: that one WAS shown ahead of time, so its duplicate is "
                + "suppressed -- answering false here is how a shot is heard twice");
            Assert.IsFalse(latch.TryConsume(FireGap + 0.03f),
                "and nothing is left over");
        }

        /// `Arm` PINS THE RECORD'S DEADLINE, and the mutant that drops that one
        /// line survives every fixture above: they all arm in the same frame
        /// they were granted and with the same window the provisional deadline
        /// already used. The two backends differ by exactly this -- the local
        /// one confirms within 0.1 s, so it SHORTENS the wait from the
        /// provisional 0.5 s, four whole `FireInterval`s of difference.
        [Test]
        public void ArmShortensTheRecordToItsOwnWindow_NotTheProvisionalOne()
        {
            var latch = new ImmediatePredictionLatch();
            const float shortWindow = ImmediatePredictionLatch.SameFrameWindowSeconds;

            Assert.IsTrue(Pulse(latch, 0f, key: 1));
            latch.Arm(0f, shortWindow, key: 1);

            Assert.IsFalse(latch.TryConsume(shortWindow + 0.01f),
                "a record armed on the same-frame window is gone once that window passes -- "
                + "not held for the provisional buffered one");
        }

        /// A GRANT NOBODY MARKED IS FORGOTTEN BY ITS OWN WINDOW. Without this,
        /// a voice the SFX gates refused would hold its slot until the next
        /// restart, and after five such rounds the queue is full and prediction
        /// stops altogether -- the feature switching itself off silently.
        [Test]
        public void AnUnmarkedRecordExpiresOnItsOwn_SoAGatedOutVoiceCannotBlockTheQueue()
        {
            var latch = new ImmediatePredictionLatch();

            for (int shot = 1; shot <= 5; shot++)
                Assert.IsTrue(Pulse(latch, shot * 0.001f, key: shot),
                    "fixture premise: the queue fills with granted-but-unshown records");

            Assert.IsFalse(Pulse(latch, 0.01f, key: 6),
                "fixture premise: a sixth grant has nowhere to go while they are alive");
            Assert.AreEqual(1, latch.OverflowDroppedPredictions,
                "and the refusal is counted rather than silent (Р82)");

            Assert.IsTrue(Pulse(latch, Window + 0.02f, key: 6),
                "once their own window passes, the slots come back");
        }

        /// `Reset` CLEARS THE QUEUE, not only the ring of shown keys. The
        /// fixture that pins the ring cannot see this: its record is already
        /// consumed by the time the restart happens.
        [Test]
        public void ARestartDropsRecordsStillWaiting_SoNoEventOfTheNewMatchEatsAnOldOne()
        {
            var latch = new ImmediatePredictionLatch();

            Assert.IsTrue(Pulse(latch, 0f, key: 1));
            latch.Arm(0f, Window, key: 1);

            latch.Reset();

            Assert.IsFalse(latch.TryConsume(0.01f),
                "the first event of the NEW match must find nothing: consuming the previous "
                + "match's record would suppress a shot nobody predicted");
        }

        /// THE ECHO OF A ROUND THAT WAS NEVER SHOWN. The ring of shown keys
        /// cannot refuse this one -- the round never reached `Arm`, so its key
        /// was never written there -- and only the queue's own duplicate guard
        /// can. Without it a reconciliation echo would put a SECOND record for
        /// one round where a single event is coming, and every later event
        /// would answer the wrong round.
        [Test]
        public void AnEchoOfAnUnshownRound_DoesNotQueueASecondRecordForIt()
        {
            var latch = new ImmediatePredictionLatch();

            Assert.IsTrue(Pulse(latch, 0f, key: 1),
                "fixture premise: round 1 is granted and never armed");

            Assert.IsFalse(Pulse(latch, 0.05f, key: 1),
                "the replayed edge for the same round must not be granted again");

            Assert.IsFalse(latch.TryConsume(0.06f),
                "one round, one record: its event reports the round was not shown...");
            Assert.IsFalse(latch.TryConsume(0.07f),
                "...and there is no second record behind it to answer somebody else's event");
        }

        /// THE RING FORGETS ITS OLDEST KEY AND KEEPS WRITING. A counter that
        /// never wrapped would throw on the sixth shown round -- two seconds
        /// into an ordinary burst -- and the wrap is what makes the ring a
        /// ring rather than a one-shot buffer.
        [Test]
        public void TheShownRingWrapsAndForgetsItsOldestKey()
        {
            var latch = new ImmediatePredictionLatch();

            // Six rounds, each granted, shown and confirmed in turn: more than
            // the ring can hold, so the first key must fall out of it.
            for (int shot = 1; shot <= 6; shot++)
            {
                float now = (shot - 1) * FireGap;
                Assert.IsTrue(Pulse(latch, now, key: shot),
                    $"fixture premise: round {shot} is predicted");
                latch.Arm(now, Window, key: shot);
                Assert.IsTrue(latch.TryConsume(now + 0.01f),
                    $"fixture premise: round {shot} is confirmed, so the queue never fills");
            }

            Assert.IsTrue(Pulse(latch, 6f * FireGap, key: 1),
                "key 1 has been pushed out of the ring by five later ones, so it is free "
                + "again -- which is exactly what makes the ring safe to clear on a restart");
            Assert.IsFalse(Pulse(latch, 6f * FireGap + 0.01f, key: 6),
                "while the five most recent keys are still remembered");
        }

        /// THE OVERFLOW COUNTER OUTLIVES A RESTART, the rule its neighbors
        /// keep word for word (`PredictedShotLog.OverflowDroppedShots`,
        /// `ClientEventQueue.OverflowDroppedEvents`): a per-session health
        /// counter that zeroed itself on every restart would hide precisely
        /// the pattern it exists to surface.
        [Test]
        public void TheOverflowCounterSurvivesAReset()
        {
            var latch = new ImmediatePredictionLatch();

            for (int shot = 1; shot <= 5; shot++)
                Assert.IsTrue(Pulse(latch, shot * 0.001f, key: shot));
            Assert.IsFalse(Pulse(latch, 0.01f, key: 6));
            Assert.AreEqual(1, latch.OverflowDroppedPredictions,
                "fixture premise: one grant was refused for want of a slot");

            latch.Reset();

            Assert.AreEqual(1, latch.OverflowDroppedPredictions,
                "a new match does not make the previous one's losses un-happen");
        }

    }
}
