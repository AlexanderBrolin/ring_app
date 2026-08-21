using Ring.Simulation.Core;

namespace Ring.Simulation.Visibility
{
    /// The three entity classes a connection keeps a SEPARATE visibility set for
    /// (Stage 3 Task 26, spec §3.9 Р268 item 2). Not a tag inside one shared
    /// set: the sign of an id is already spoken for — negative means "player"
    /// (VisibilityIds.ForPlayer below) — and SnapshotAssembler.WriteFrame
    /// dispatches an entry's class off exactly that sign, reading every
    /// positive id as a mob's. A pickup or container sharing that set would be
    /// looked up as a mob, found missing, and dropped by the branch that exists
    /// for a mob still LINGERING after its death: no exception, no counter,
    /// nothing in the frame. A second layer of tags in the same integer would
    /// have made MobSlotOf unreadable instead of fixing that.
    ///
    /// PLAYERS RIDE WITH THE MOBS, and that is not an oversight: they share one
    /// set because VisibilitySystem.Compute produces both in one pass and the
    /// frame writes both from one candidate list, told apart by the sign trick
    /// that already works. The split is between the three classes whose ids
    /// come from the SAME positive counter and can therefore only be told
    /// apart by which set they are in.
    public enum VisibilityClass { Mobs = 0, Pickups = 1, Containers = 2 }

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
        int _refused;

        public VisibilitySet(int capacity)
        {
            _ids = new int[capacity];
            _lingerTicks = new int[capacity];
        }

        /// How large a set has to be to hold everything ONE Compute call of
        /// that class can ever put in it (plan errata E-6 C-I3, Stage 3 Task
        /// 26) — the ONE home of all three numbers.
        ///
        /// WHY IT IS A HOME RATHER THAN THREE EXPRESSIONS. The mob sum lived
        /// in two places before this task (SnapshotAssembler.Connection and
        /// TestWorlds.Capacity), spelled out identically and with nothing to
        /// make the second follow the first. A set sized by the wrong class's
        /// cap does not fail loudly: too large merely wastes an array, and too
        /// small now REFUSES entities (see Add below) — a fog of war that
        /// quietly stops reporting the entities past the cap, which is the
        /// exact shape of bug CRITICAL RULE 4 makes expensive to find.
        ///
        /// PLAYERS RIDE ONLY IN THE MOB SUM, and that is the whole difference
        /// between the three: Compute visits every player and every mob in one
        /// pass, while ComputePickups and ComputeContainers each visit one
        /// store and nothing else. Adding MaxPlayers to their caps would not
        /// be conservative, it would be wrong about what the function does.
        public static int CapacityFor(in ArenaSimConfig arena, VisibilityClass cls)
        {
            switch (cls)
            {
                case VisibilityClass.Mobs: return arena.MaxMobs + arena.MaxPlayers;
                case VisibilityClass.Pickups: return arena.MaxPickups;
                case VisibilityClass.Containers: return arena.MaxContainers;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(cls), cls,
                        "VisibilitySet.CapacityFor: every VisibilityClass states its own cap "
                        + "here — falling back to another class's would size a set by a number "
                        + "that means something else.");
            }
        }

        public int Count => _count;

        /// How many Add calls this set has REFUSED for want of room, since it
        /// was constructed (Stage 3 Task 26, spec §3.9 item 1). Deliberately
        /// NOT reset by Clear: this is a health number of the same kind as
        /// `NetStats.DroppedEntities`, and one that reset every tick could
        /// never be read by anything slower than a tick. A nonzero value means
        /// some entity was left out of somebody's fog of war — bounded and
        /// counted, rather than thrown or silent.
        public int RefusedCount => _refused;

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

        /// Appends an entry, or REFUSES it and counts the refusal when the set
        /// is full (Stage 3 Task 26, spec §3.9 item 1 — until this task the
        /// line below wrote `_ids[_count]` with no bounds check at all, so an
        /// undersized set threw IndexOutOfRangeException from inside the
        /// per-tick snapshot assembly, on the server, mid-match).
        ///
        /// THE NEWCOMER IS WHAT GOES, never an incumbent. Evicting an entry
        /// already written would make the set's contents depend on the order
        /// Add happened to be called in, which the insertion-order contract
        /// IdAt/LingerAt above forbids outright — the frame is written in that
        /// order and has to be reproducible.
        public void Add(int entityId, int lingerTicks = 0)
        {
            if (_count >= _ids.Length)
            {
                _refused++;
                return;
            }
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
