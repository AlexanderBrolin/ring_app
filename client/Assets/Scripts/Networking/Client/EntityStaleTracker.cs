using Ring.Simulation.Visibility;

namespace Ring.Networking.Client
{
    /// One visibility class's fade bookkeeping (Stage 3 Т32б, bd `app-dut`):
    /// a `StalePolicy` and the `EntitySlotMap` that gives it indices to write
    /// into.
    ///
    /// WHY THE TWO TRAVEL TOGETHER. `StalePolicy` was written for player
    /// SEATS, where the index is the identity; every other entity on the wire
    /// carries `SimulationWorld`'s sparse id. Pairing the policy with a map is
    /// the whole of the adaptation, and pairing them HERE rather than at each
    /// call site is what stops the two from being wired differently for two
    /// classes — the shape errata E-6 I13 asks for ("one shared mapping,
    /// instances per class").
    ///
    /// A SLOT IS RECYCLED THE TICK ITS TENANT IS `Gone`, which is what keeps a
    /// table sized to the arena's cap from filling up over a match that mints
    /// thousands of ids. Reuse is safe without clearing the policy's own arrays
    /// because `StalePolicy.OnEntitySeen` zeroes the fade and restamps the
    /// last-seen tick on the FIRST sighting of the new tenant — and ticks only
    /// grow inside an epoch, so its "not older than what I have" guard cannot
    /// swallow that first sighting. Across an epoch both halves are reset
    /// wholesale.
    public sealed class EntityStaleTracker
    {
        readonly EntitySlotMap _slots;
        readonly StalePolicy _policy;

        public EntityStaleTracker(VisibilityClass visibilityClass, int capacity, int staleTicks,
            int fadeTicks)
        {
            Class = visibilityClass;
            _slots = new EntitySlotMap(capacity);
            _policy = new StalePolicy(capacity, staleTicks, fadeTicks);
        }

        /// Which class this instance speaks for — carried so a caller holding
        /// several of them can say which one refused a claim, and so the field
        /// is not a comment.
        public VisibilityClass Class { get; }

        /// This frame saw `id`. A table with no room left says nothing and the
        /// entity keeps its old behavior — see `EntitySlotMap.Claim` for why
        /// refusing beats evicting.
        public void OnSeen(int id, uint frameTick)
        {
            int slot = _slots.Claim(id);
            if (slot >= 0) _policy.OnEntitySeen(slot, frameTick);
        }

        /// Mirrors `StalePolicy.OnFrameApplied` — the two starvation clocks are
        /// per-connection facts and every class measures them the same way.
        public void OnFrameApplied(uint frameTick, bool truncated)
            => _policy.OnFrameApplied(frameTick, truncated);

        /// Ages every entry and hands back the slots whose tenants finished
        /// fading.
        ///
        /// THE SWEEP IS O(capacity) AND ONCE PER FRAME, which is the price the
        /// issue's own direction note budgeted for ("the size of the policy and
        /// one O(capacity) pass per frame"). It cannot be event-driven: the
        /// policy has no callback and nothing else knows the moment an entry
        /// crosses into `Gone`.
        public void Advance(int renderTick)
        {
            _policy.Advance(renderTick);
            for (int slot = 0; slot < _policy.Capacity; slot++)
                if (_policy.StateOf(slot) == StalePolicy.StaleState.Gone) _slots.Release(slot);
        }

        /// How much of `id`'s fade-out is already spent, in `[0, 1]`. An id
        /// nothing remembers answers 0 — "nothing to fade", the same safe
        /// default `StalePolicy.FadeProgress` gives an unknown index.
        public float FadeProgress(int id)
        {
            int slot = _slots.Find(id);
            return slot < 0 ? 0f : _policy.FadeProgress(slot);
        }

        /// Whether the view for `id` still has something to show. False for an
        /// id this tracker never saw, which is what a caller asking about an
        /// entity that vanished before the tracker existed must get.
        public bool ShouldKeep(int id)
        {
            int slot = _slots.Find(id);
            return slot >= 0 && _policy.StateOf(slot) != StalePolicy.StaleState.Gone;
        }

        /// Forgets everything, on the epoch change — both halves, because a new
        /// match mints ids from 1 and restarts the tick counter, and either one
        /// surviving alone would answer for the match before.
        public void Reset()
        {
            _slots.Reset();
            _policy.Reset();
        }
    }
}
