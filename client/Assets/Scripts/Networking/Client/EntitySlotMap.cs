using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// A sparse wire entity id turned into a DENSE slot, and kept there for as
    /// long as anyone still cares about it (Stage 3 Т32б, bd `app-dut`).
    ///
    /// WHY IT HAS TO EXIST. `StalePolicy` is an array-per-fact structure: it
    /// remembers a last-seen tick and a fade counter PER INDEX, and an index is
    /// what it takes. Player slots are already dense — the array index IS the
    /// seat — so Task 47c could hand it seat numbers and be done. Mobs,
    /// pickups and containers carry `SimulationWorld`'s own entity counter,
    /// which climbs past every cap within a match and is sparse by
    /// construction. Without a mapping the policy has nowhere to write, which
    /// is precisely why `app-dut` stayed open: players faded at the edge of
    /// sight and everything else vanished with a pop, and the difference is
    /// visible to the eye.
    ///
    /// A LINEAR SCAN, ON PURPOSE, and the shape is `MobTypeMemory`'s (errata
    /// E-6 I13 names it as the model). The table is at most one capacity long,
    /// it is walked once per record rather than once per pixel, and a
    /// dictionary would allocate as it grew on the one path in this project
    /// that must not allocate.
    ///
    /// A CLAIM CAN FAIL, AND FAILING IS THE RIGHT ANSWER. Capacity is the
    /// arena's own cap for the class, so a full table means every slot is
    /// held by an entity something still remembers — and the honest response
    /// to "no room to remember this one" is to not remember it, i.e. to let it
    /// pop the way it used to, rather than to evict a live entry and make a
    /// DIFFERENT entity pop instead.
    public sealed class EntitySlotMap
    {
        /// The id living in each slot; `NoEntity` marks a free one. Entity ids
        /// start at 1 (`SimulationWorld`'s own counter), so zero is free for
        /// this and cannot collide with a real id.
        public const int NoEntity = 0;

        readonly int[] _ids;

        public EntitySlotMap(int capacity)
        {
            // Floored at one rather than refused, the shape `MobTypeMemory`
            // and `ClientEventQueue` both keep: a zero-width table remembers
            // nothing, which is a worse answer than a small one.
            _ids = new int[math.max(1, capacity)];
        }

        public int Capacity => _ids.Length;

        /// The slot `id` already holds, or -1. Never claims.
        public int Find(int id)
        {
            if (id == NoEntity) return -1;
            for (int i = 0; i < _ids.Length; i++)
                if (_ids[i] == id) return i;
            return -1;
        }

        /// The slot `id` holds, claiming a free one if it has none; -1 when the
        /// table is full or the id is the free marker.
        ///
        /// THE SEARCH FOR AN EXISTING ENTRY COMES FIRST AND IS COMPLETE. A
        /// claim that stopped at the first free slot without finishing the scan
        /// could give one id two slots — and two slots means two fade timers
        /// for one mob, one of which is always about to expire.
        public int Claim(int id)
        {
            if (id == NoEntity) return -1;

            int free = -1;
            for (int i = 0; i < _ids.Length; i++)
            {
                if (_ids[i] == id) return i;
                if (free < 0 && _ids[i] == NoEntity) free = i;
            }

            if (free < 0) return -1;
            _ids[free] = id;
            return free;
        }

        /// Hands a slot back. Idempotent: releasing a slot nobody holds is what
        /// a caller does when it sweeps for expired entries and finds one it
        /// already swept.
        public void Release(int slot)
        {
            if (slot < 0 || slot >= _ids.Length) return;
            _ids[slot] = NoEntity;
        }

        /// Forgets everything — called on the epoch change, because a new match
        /// mints entity ids from 1 again and a surviving entry would answer for
        /// an entity from the match before. The same reason `MobTypeMemory`
        /// resets, and it is a wrong answer rather than a missing one.
        public void Reset() => System.Array.Clear(_ids, 0, _ids.Length);
    }
}
