using Ring.Simulation.Core;
using Ring.Simulation.Visibility;

namespace Ring.Networking.Client
{
    /// Every visibility class's fade bookkeeping, in one table (Stage 3 Т33d,
    /// bd `app-tut2`).
    ///
    /// WHY A TABLE AND NOT THREE FIELDS. Т32б gave mobs an
    /// `EntityStaleTracker` and drew pickups and containers in the same task —
    /// and those two popped at the edge of sight while mobs faded, because a
    /// field per class states nothing about the classes that have none. There
    /// was no place for a test to ask "is every class covered", so nothing
    /// asked. Keyed by the enum, the question is one loop
    /// (`EntityStaleTrackersTests.CoversEveryVisibilityClass`), and a fourth
    /// class added later is named by that test before it is noticed in the
    /// picture.
    ///
    /// THE LIFECYCLE CALLS COME IN ALL-FORMS FOR THE SAME REASON. Advancing,
    /// hearing a frame and resetting are per-CONNECTION facts — no class ages
    /// on its own clock — so the backend says them once and cannot say them to
    /// two classes out of three. `ClientMatchReset` holds this object rather
    /// than one tracker, which is what makes the epoch seam cover every class
    /// too.
    ///
    /// CAPS COME FROM `VisibilitySet.CapacityFor`, the one home of all three
    /// (Task 26). The mob entry is therefore sized `MaxMobs + MaxPlayers`
    /// rather than the bare `MaxMobs` Т32б passed: a superset, so nothing it
    /// used to hold stops fitting, and one number instead of a second opinion.
    public sealed class EntityStaleTrackers
    {
        readonly EntityStaleTracker[] _byClass;

        public EntityStaleTrackers(in ArenaSimConfig arena, int staleTicks, int fadeTicks)
        {
            var classes = (VisibilityClass[])System.Enum.GetValues(typeof(VisibilityClass));
            _byClass = new EntityStaleTracker[classes.Length];
            for (int i = 0; i < classes.Length; i++)
            {
                VisibilityClass cls = classes[i];
                // Built through the same indexer the readers use, so a domain
                // whose values ever stop being 0..N-1 fails HERE, at startup,
                // rather than by handing out a neighbor's tracker at runtime.
                _byClass[IndexOf(cls)] = new EntityStaleTracker(cls,
                    VisibilitySet.CapacityFor(in arena, cls), staleTicks, fadeTicks);
            }
        }

        /// This class's tracker. Throws on a class the table does not know —
        /// the catalog rule (R-237): a domain lookup answers or says it cannot,
        /// it never returns a default tenant, because a default tenant here is
        /// one class's memory answering another class's question.
        public EntityStaleTracker For(VisibilityClass cls) => _byClass[IndexOf(cls)];

        /// Ages every class. One render tick, one sweep per class — see
        /// `EntityStaleTracker.Advance` for why the sweep cannot be
        /// event-driven.
        public void AdvanceAll(int renderTick)
        {
            for (int i = 0; i < _byClass.Length; i++) _byClass[i].Advance(renderTick);
        }

        /// A frame landed. The two starvation clocks it feeds are properties of
        /// the CONNECTION, so every class hears the same thing.
        public void OnFrameAppliedAll(uint frameTick, bool truncated)
        {
            for (int i = 0; i < _byClass.Length; i++) _byClass[i].OnFrameApplied(frameTick, truncated);
        }

        /// The epoch seam: a new match mints ids from 1 and restarts the tick
        /// counter, so a class left un-reset would answer the new match with
        /// the old one's memory.
        public void ResetAll()
        {
            for (int i = 0; i < _byClass.Length; i++) _byClass[i].Reset();
        }

        static int IndexOf(VisibilityClass cls)
        {
            switch (cls)
            {
                case VisibilityClass.Mobs: return 0;
                case VisibilityClass.Pickups: return 1;
                case VisibilityClass.Containers: return 2;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(cls), cls,
                        "EntityStaleTrackers: every VisibilityClass owns a row of this table "
                        + "and a new one must be given its own here.");
            }
        }
    }
}
