using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// Which archetype a mob id was, remembered for exactly as long as an
    /// event can still name it (Stage 2 Task 44d fix-round 1).
    ///
    /// WHY THE RECEIVER HAS TO REMEMBER IT AT ALL. The simulation puts
    /// `mob.Type` into the `MobDied` event it emits; the wire does not carry
    /// it — the payload of that kind is the mob's id, the killer's slot and
    /// the hit zone, and by the owner's decision of 2026-08-10 the protocol is
    /// not being widened. So the decoded event reaches Presentation with
    /// `MobType` at the enum's zero, which is `Chaser`: a REAL archetype, not
    /// a missing one, and the consumers act on it — the corpse mesh, its
    /// scale and the gib parts are all picked by that field. Every gunner
    /// would die as a chaser. What this class restores is the one term of
    /// that loss the client already has: the Mobs block of every frame names
    /// each visible mob's archetype, and this remembers the pairing.
    ///
    /// TWO GENERATIONS, AND THAT NUMBER IS THE WHOLE DESIGN. A mob dies on
    /// the tick the server drops it from the world, so the frame whose Events
    /// block reports the death is the frame whose Mobs block no longer carries
    /// it: one generation would answer "unknown" for every death there is. Two
    /// answer for every mob this client could see one frame earlier, which is
    /// every mob it watched die. It is two frames DEEP rather than two ticks
    /// WIDE — a frame that arrived out of order and filled a hole in the ring
    /// retires the newer generation just like any other — and that is the
    /// property the case above needs. It is also indifferent to where the
    /// Events block sits relative to the Mobs block in a frame, which matters
    /// because the receiver walks whatever order arrives rather than the one
    /// the assembler writes.
    ///
    /// WHAT IT DOES NOT COVER, NAMED RATHER THAN LEFT TO BE FOUND: a mob this
    /// client has not seen in either of the two retained frames. Fog of war
    /// makes that reachable — a mob killed the moment it came into view, by
    /// somebody else, in a frame that never listed it. `TryGetType` answers
    /// false there and the caller leaves the decoded event exactly as the
    /// decoder built it, zero included. A guess would be indistinguishable
    /// from an answer.
    ///
    /// THE MOBS BLOCK IS THE ONLY SOURCE. `MobSpawned` carries the archetype
    /// as well and is deliberately not fed in here: one fact with two homes is
    /// two chances to disagree, and these two would — a client that sees a mob
    /// but never saw it spawn is the ordinary case under fog of war, not the
    /// exception.
    ///
    /// NOTHING HERE ALLOCATES AFTER THE CONSTRUCTOR, and nothing throws on any
    /// input (Р82): this is fed from inside FishNet's batched parsing loop,
    /// where a throw abandons every message batched behind it in the same
    /// datagram. A record list longer than the capacity is clipped rather than
    /// refused — the caller's own scratch is sized from the same `MaxMobs`, so
    /// the shipped path cannot produce one.
    ///
    /// NOT A SEVENTH SEAM. `ClientMatchReset` owns the six per-match objects
    /// and its own doc argues for having one call site rather than six; this
    /// is a memory the SNAPSHOT DECODER keeps, so its owner resets it where it
    /// observes the epoch change, beside the two other things it clears there.
    public sealed class MobTypeMemory
    {
        // Two generations of "which ids were in a frame, and what they were".
        // Parallel arrays rather than an array of pairs: the search reads ids
        // only, and the types are touched once a hit is found.
        int[] _newerIds;
        MobType[] _newerTypes;
        int _newerCount;
        int[] _olderIds;
        MobType[] _olderTypes;
        int _olderCount;

        /// `maxMobs` is `ArenaSimConfig.MaxMobs` — the same cap the caller's
        /// decode scratch is sized from, so a frame can never carry more
        /// records than one generation holds. Floored at 1 rather than
        /// refused, the shape `ClientEventQueue`'s own constructor keeps: a
        /// zero-width memory remembers nothing, which is a worse answer than a
        /// small one.
        public MobTypeMemory(int maxMobs)
        {
            int capacity = math.max(1, maxMobs);
            _newerIds = new int[capacity];
            _newerTypes = new MobType[capacity];
            _olderIds = new int[capacity];
            _olderTypes = new MobType[capacity];
        }

        /// One frame's Mobs block, after its decoder accepted it. The previous
        /// call's list becomes the older generation and the one before that is
        /// forgotten; the arrays are swapped rather than copied, so the whole
        /// call costs one pass over the records.
        ///
        /// CALL IT FOR FRAMES WHOSE STATE IS BEING PUBLISHED AND NO OTHERS. A
        /// frame the ring refused a slot to — a duplicate of one already
        /// decoded, or one older than the window — says nothing newer than
        /// what is already here, and letting it retire the newer generation
        /// would cost exactly the lookup this class exists for.
        public void OnMobsDecoded(System.ReadOnlySpan<SnapshotBlocks.MobRecord> records)
        {
            int[] ids = _olderIds;
            MobType[] types = _olderTypes;
            _olderIds = _newerIds;
            _olderTypes = _newerTypes;
            _olderCount = _newerCount;

            int count = math.min(records.Length, ids.Length);
            for (int i = 0; i < count; i++)
            {
                ids[i] = records[i].Id;
                types[i] = records[i].Type;
            }

            _newerIds = ids;
            _newerTypes = types;
            _newerCount = count;
        }

        /// The archetype this client last saw `id` wearing. `false` means it
        /// has not seen it in either retained frame — see the class doc for
        /// why that is answered rather than guessed.
        ///
        /// A LINEAR SCAN, DELIBERATELY. Both generations together hold at most
        /// `2 * MaxMobs` entries, this is asked once per `MobDied` record
        /// rather than once per mob, and a dictionary would allocate as it
        /// grew on the one path in this project that must not allocate.
        public bool TryGetType(int id, out MobType type)
        {
            if (Find(_newerIds, _newerTypes, _newerCount, id, out type)) return true;
            return Find(_olderIds, _olderTypes, _olderCount, id, out type);
        }

        /// Forgets both generations — called on the epoch change, because a
        /// new match mints its entity ids from 1 again (`SimulationWorld`'s own
        /// counter). An id that survived would answer with the archetype of a
        /// mob from the match before, which is a wrong answer rather than a
        /// missing one.
        public void Reset()
        {
            System.Array.Clear(_newerIds, 0, _newerIds.Length);
            System.Array.Clear(_olderIds, 0, _olderIds.Length);
            _newerCount = 0;
            _olderCount = 0;
        }

        static bool Find(int[] ids, MobType[] types, int count, int id, out MobType type)
        {
            for (int i = 0; i < count; i++)
            {
                if (ids[i] != id) continue;
                type = types[i];
                return true;
            }

            type = default;
            return false;
        }
    }
}
