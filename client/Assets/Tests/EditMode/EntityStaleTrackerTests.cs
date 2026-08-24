using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Simulation.Visibility;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т32б (bd `app-dut`): the pair that lets `StalePolicy` — written
    /// for dense player seats — speak about entities carrying sparse wire ids.
    ///
    /// THE CADENCE IS `StalePolicyTests`', because the policy inside is that
    /// class's and its rules are pinned there: `OnFrameApplied` is fed for
    /// every render tick so the GLOBAL starvation gate (keyed off the same
    /// `staleTicks`) does not trip first and mask the per-entity fade these
    /// tests are about.
    public class EntityStaleTrackerTests
    {
        const int StaleTicks = 3;
        const int FadeTicks = 2;

        static EntityStaleTracker Tracker(int capacity)
            => new EntityStaleTracker(VisibilityClass.Mobs, capacity, StaleTicks, FadeTicks);

        [Test]
        public void ASeenEntity_IsKept_AndNotYetFading()
        {
            EntityStaleTracker tracker = Tracker(4);

            tracker.OnSeen(9001, frameTick: 10);
            tracker.OnFrameApplied(10, truncated: false);
            tracker.Advance(10);

            Assert.IsTrue(tracker.ShouldKeep(9001), "a mob the frame just carried is kept");
            Assert.AreEqual(0f, tracker.FadeProgress(9001), 1e-6f);
        }

        [Test]
        public void AnUnseenEntity_FadesOutAndThenStopsBeingKept()
        {
            EntityStaleTracker tracker = Tracker(4);
            tracker.OnSeen(9001, frameTick: 10);

            // Past the freeze boundary and one tick into the fade budget.
            tracker.OnFrameApplied(13, truncated: false);
            tracker.Advance(14);
            Assert.IsTrue(tracker.ShouldKeep(9001), "still on screen — dimming, not gone");
            Assert.AreEqual(0.5f, tracker.FadeProgress(9001), 1e-6f,
                "one of two fade ticks spent");

            tracker.OnFrameApplied(14, truncated: false);
            tracker.Advance(15);
            Assert.IsFalse(tracker.ShouldKeep(9001),
                "the budget is spent: what is left is an unlit shape, and the view is released");
        }

        /// The slot comes back when its tenant is gone, which is what keeps a
        /// table sized to the arena's cap from filling up over a match that
        /// mints thousands of ids.
        ///
        /// A ONE-SLOT TABLE IS THE FIXTURE, so a tracker that never recycled
        /// would have nowhere to put the second mob and would answer `false`
        /// for it — the pop this whole issue is about, arriving through the
        /// table instead of through the policy.
        [Test]
        public void AGoneEntitysSlotIsRecycled()
        {
            EntityStaleTracker tracker = Tracker(1);
            tracker.OnSeen(9001, frameTick: 10);
            tracker.OnFrameApplied(13, truncated: false);
            tracker.Advance(14);
            tracker.OnFrameApplied(14, truncated: false);
            tracker.Advance(15);
            Assert.IsFalse(tracker.ShouldKeep(9001), "premise: the first tenant finished fading");

            tracker.OnSeen(9002, frameTick: 16);
            tracker.OnFrameApplied(16, truncated: false);
            tracker.Advance(16);

            Assert.IsTrue(tracker.ShouldKeep(9002),
                "the freed slot took the new mob — without recycling the table would be full");
            Assert.AreEqual(0f, tracker.FadeProgress(9002),
                "and it starts unfaded: the first sighting clears what the previous tenant left");
        }

        /// An id nothing remembers is not kept and has nothing to fade — the
        /// answer a caller asking about a mob that died before this tracker
        /// existed must get.
        [Test]
        public void AnUnknownId_IsNotKeptAndHasNoFade()
        {
            EntityStaleTracker tracker = Tracker(4);
            Assert.IsFalse(tracker.ShouldKeep(4242));
            Assert.AreEqual(0f, tracker.FadeProgress(4242), 1e-6f);
        }

        /// A new epoch mints ids from 1 again and restarts the tick counter, so
        /// a survivor would answer for an entity from the match before — a
        /// wrong answer rather than a missing one.
        [Test]
        public void Reset_ForgetsEveryEntity()
        {
            EntityStaleTracker tracker = Tracker(4);
            tracker.OnSeen(9001, frameTick: 10);
            tracker.OnFrameApplied(10, truncated: false);
            tracker.Advance(10);
            Assert.IsTrue(tracker.ShouldKeep(9001), "premise: there is something to forget");

            tracker.Reset();

            Assert.IsFalse(tracker.ShouldKeep(9001));
            Assert.AreEqual(0f, tracker.FadeProgress(9001), 1e-6f);
        }
    }
}
