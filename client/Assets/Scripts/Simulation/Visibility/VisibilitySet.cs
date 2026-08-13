namespace Ring.Simulation.Visibility
{
    /// Id-keyed set with per-id linger counters (Р19/Р20): _mobs uses swap-remove,
    /// so slot indices are unstable and would transfer state to a different mob.
    /// Flat array + linear scan (Task 19, spec §3.5) — no HashSet: allocations
    /// and unordered iteration are both unwanted on this per-connection, once-
    /// per-observer-per-tick path. Capacity is fixed at construction time (the
    /// caller's job to size — Arena.MaxMobs + Arena.MaxPlayers covers every
    /// live mob plus every player VisibilitySystem.Compute can ever visit in a
    /// single call) and never grown; Clear() only resets Count, so a
    /// Compute() call that Clear()s then re-Add()s every tick allocates
    /// nothing beyond the two arrays built here once, in the constructor.
    public sealed class VisibilitySet
    {
        readonly int[] _ids;
        readonly int[] _lingerTicks;
        int _count;

        public VisibilitySet(int capacity)
        {
            _ids = new int[capacity];
            _lingerTicks = new int[capacity];
        }

        public int Count => _count;

        public bool Contains(int entityId)
        {
            for (int i = 0; i < _count; i++)
                if (_ids[i] == entityId) return true;
            return false;
        }

        /// 0 for an id currently visible (linger counter untouched) as well
        /// as for one that is not tracked in this set at all — callers that
        /// need to tell "absent" apart from "visible now" check Contains
        /// first (VisibilitySystem's own per-entity evaluation does exactly
        /// that before ever reading this).
        public int LingerOf(int entityId)
        {
            for (int i = 0; i < _count; i++)
                if (_ids[i] == entityId) return _lingerTicks[i];
            return 0;
        }

        /// Entry `index` in INSERTION order (Stage 2 Task 28, carryover-t28.md
        /// §8а). Together with LingerAt below this is the whole enumeration
        /// contract: `for (int i = 0; i &lt; set.Count; i++) set.IdAt(i)` visits
        /// every tracked id exactly once, in the order Add was called — which
        /// for a VisibilitySystem.Compute result is players by index and then
        /// mobs by world slot.
        ///
        /// THE ORDER IS THE CONTRACT, not an implementation detail. Task 28's
        /// SnapshotAssembler writes its Players and Mobs blocks in exactly this
        /// order, so the same world state has to produce the same byte sequence
        /// twice; the truncation branch's "surviving entities keep insertion
        /// order" rule has nothing to keep otherwise. Pinned by
        /// VisibilityTests.SetEnumerator_IdAtAndLingerAt_FollowInsertionOrder_
        /// AndClearResetsCount.
        ///
        /// Bounds behave exactly like the underlying array's: an index outside
        /// [0, Count) is a CALLER bug (it can only come from ignoring Count),
        /// not untrusted input, so IndexOutOfRangeException from the array
        /// itself is the right answer and no guard is spent restating it. Note
        /// that Clear() only resets Count — the array keeps its stale contents —
        /// so an index at or above Count may well return a plausible-looking
        /// id rather than throwing. Count is the only bound a caller may trust.
        ///
        /// NO ReadOnlySpan&lt;int&gt; ACCESSOR (task-28-brief §2.10): the two
        /// methods cover the one consumer that exists, and a span would hand
        /// out the internal arrays themselves — a wider contract than anything
        /// asked for (AGENT.md rule 3).
        public int IdAt(int index) => _ids[index];

        /// The linger counter of entry `index`, under the same index as IdAt
        /// above — `LingerAt(i)` always equals `LingerOf(IdAt(i))`, without the
        /// linear scan. Same 0-means-"visible now" convention as LingerOf.
        public int LingerAt(int index) => _lingerTicks[index];

        public void Add(int entityId, int lingerTicks = 0)
        {
            _ids[_count] = entityId;
            _lingerTicks[_count] = lingerTicks;
            _count++;
        }

        public void Clear() => _count = 0;
    }

    /// Synthetic id space for players inside a VisibilitySet (Р20 context):
    /// PlayerState carries no Id field of its own, and MobState.Id is drawn
    /// from SimulationWorld's private _nextEntityId counter, which starts at
    /// 1 and only grows (SimulationWorld.cs:74, :544, :839) — every REAL
    /// entity id is therefore >= 1, leaving the negative integers free for a
    /// synthetic, disjoint player id space. This is the single seam that
    /// writes "-(index + 1)" (the "+1" keeps player 0 from mapping to 0,
    /// which would otherwise collide with an unrelated "no id" sentinel) —
    /// no second call site re-derives this by hand.
    public static class VisibilityIds
    {
        public static int ForPlayer(int index) => -(index + 1);
    }
}
