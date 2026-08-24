using System;
using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Simulation.Core;
using Ring.Simulation.Visibility;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т33d (bd `app-tut2`): the table that gives EVERY visibility
    /// class its fade bookkeeping, and the reflective witness that says so.
    ///
    /// THIS FILE IS THE MISSING WITNESS THE DEFECT NAMED. Т32б gave mobs an
    /// `EntityStaleTracker` and stopped there, so when the same task started
    /// drawing pickups and containers those two popped at the edge of sight
    /// while mobs faded — a class of entity with no tracker and nothing
    /// anywhere to say one was owed. A single field per class cannot be
    /// checked for completeness; a table keyed by the enum can, and
    /// `CoversEveryVisibilityClass` below is that check. A fourth class added
    /// later gets the same treatment for free: the test names it before the
    /// picture does.
    public class EntityStaleTrackersTests
    {
        const int StaleTicks = 3;
        const int FadeTicks = 4;

        static EntityStaleTrackers Fresh()
        {
            SimConfig cfg = TestConfigs.Default();
            return new EntityStaleTrackers(in cfg.Arena, StaleTicks, FadeTicks);
        }

        static Array AllClasses() => Enum.GetValues(typeof(VisibilityClass));

        [Test]
        public void CoversEveryVisibilityClass()
        {
            EntityStaleTrackers trackers = Fresh();

            foreach (VisibilityClass cls in AllClasses())
            {
                EntityStaleTracker tracker = trackers.For(cls);
                Assert.IsNotNull(tracker, $"{cls} has no fade bookkeeping at all");
                // Not merely "a tracker" — the RIGHT one. A table that handed
                // every class the same instance would answer one class's
                // question with another's memory.
                Assert.AreEqual(cls, tracker.Class,
                    $"{cls} was handed the tracker of {tracker.Class}");
            }
        }

        [Test]
        public void EveryClassSizesItselfFromTheOneHomeOfCaps()
        {
            SimConfig cfg = TestConfigs.Default();
            var trackers = new EntityStaleTrackers(in cfg.Arena, StaleTicks, FadeTicks);

            // MEASURED FROM BOTH SIDES, and the second side is the load-bearing
            // one. The cap home is `VisibilitySet.CapacityFor` and there is no
            // second one — but with this fixture the mob cap (291) is the
            // LARGEST of the three, so a table that sized every class by the
            // mobs' number would satisfy a test that only checked "big enough"
            // while quietly giving cells and boxes a table four times the size
            // they are entitled to. So each class is filled to its own cap and
            // then asked for one more.
            foreach (VisibilityClass cls in AllClasses())
            {
                int capacity = VisibilitySet.CapacityFor(in cfg.Arena, cls);
                EntityStaleTracker tracker = trackers.For(cls);
                for (int id = 1; id <= capacity; id++) tracker.OnSeen(id, 1u);
                for (int id = 1; id <= capacity; id++)
                {
                    Assert.IsTrue(tracker.ShouldKeep(id),
                        $"{cls}: id {id} of {capacity} found no slot — the table is undersized");
                }

                // `EntitySlotMap.Claim` refuses rather than evicts when full,
                // and an id it refused is one nothing remembers.
                tracker.OnSeen(capacity + 1, 1u);
                Assert.IsFalse(tracker.ShouldKeep(capacity + 1),
                    $"{cls}: a {capacity + 1}st entity found room — this class was sized by "
                    + "somebody else's cap");
            }
        }

        [Test]
        public void AdvanceAll_AgesEveryClass()
        {
            EntityStaleTrackers trackers = Fresh();
            foreach (VisibilityClass cls in AllClasses()) trackers.For(cls).OnSeen(7, 1u);

            // Past stale AND past the fade: everything must have finished
            // leaving, in every class, off ONE pair of calls.
            //
            // BOTH HALVES, IN THE ORDER THE BACKEND USES THEM. `StalePolicy`
            // holds every entry at `Stale` while `GlobalStarvation` is up — a
            // quiet CONNECTION freezes the picture instead of fading it — so
            // ageing without telling the table that frames keep arriving would
            // measure nothing at all.
            for (uint tick = 1; tick <= 1 + StaleTicks + FadeTicks + 2; tick++)
            {
                trackers.OnFrameAppliedAll(tick, truncated: false);
                trackers.AdvanceAll((int)tick + 1);
            }

            foreach (VisibilityClass cls in AllClasses())
            {
                Assert.IsFalse(trackers.For(cls).ShouldKeep(7),
                    $"{cls}: AdvanceAll left this class un-aged");
            }
        }

        [Test]
        public void ResetAll_ForgetsEveryClass()
        {
            EntityStaleTrackers trackers = Fresh();
            foreach (VisibilityClass cls in AllClasses()) trackers.For(cls).OnSeen(7, 1u);

            trackers.ResetAll();

            // The epoch witness: a restart mints ids from 1 again, so a class
            // the reset skipped would answer the NEW match with the OLD one's
            // memory.
            foreach (VisibilityClass cls in AllClasses())
            {
                Assert.IsFalse(trackers.For(cls).ShouldKeep(7),
                    $"{cls}: ResetAll skipped this class");
            }
        }

        [Test]
        public void UnknownClass_Throws()
        {
            EntityStaleTrackers trackers = Fresh();

            // The catalog home's own rule (385/R-237): a switch over a domain
            // answers or throws, it never returns a default tenant.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => trackers.For((VisibilityClass)99),
                "a class the table does not know must say so, not hand back a neighbor");
        }
    }
}
