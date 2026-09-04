using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// The instrument behind bd `app-03et`: how long after a blow the player is
    /// TOLD about it, and how far his own body travelled meanwhile.
    ///
    /// WHY AN INSTRUMENT BEFORE A FIX. The owner's report is "they hit me after
    /// I had already rounded the corner", and three different causes produce
    /// exactly that picture: a wrong hit test, a mispredicted body, and a
    /// correct hit reported late. The В4 run of 2026-09-04 ruled out the second
    /// (the reconciliation median was 0.000 m over ~1977 corrections) and the
    /// solo roster ruled out lag compensation entirely (no PvP, and RULING 177
    /// denies mobs both rewind and catch-up). What is left is the third, and it
    /// has never been measured.
    ///
    /// ONE DOMAIN, WHICH IS THE WHOLE REASON THE KEY IS A RENDER TICK.
    /// `ImpactPulseLog`'s own doc records the trap this class had to avoid:
    /// the world's tick (what stamps every snapshot header and therefore every
    /// event) and FishNet's `TimeManager.LocalTick` are unrelated, so
    /// subtracting one from the other computes garbage, and a ring addressed by
    /// the wrong one answers `false` forever in silence. The position stored
    /// here is the client's own PREDICTED position — a value from the other
    /// clock — but it is STAMPED with the NEWEST world tick this client held
    /// when the sample was taken, and every tick this class ever compares is a
    /// world tick. Nothing below subtracts one domain from the other.
    ///
    /// ⚠ THE KEY IS THE NEWEST TICK AND NOT THE RENDER TICK, which is a
    /// correction made before the wiring landed rather than a preference. The
    /// event queue hands an event over exactly when the render clock REACHES
    /// its tick, so a trail keyed by the render tick would measure
    /// `renderTick - eventTick` — zero by construction, an instrument that
    /// reports success whatever the connection does. Keyed by the newest tick,
    /// the same subtraction is the real question: how far the server's present
    /// had run past the blow by the time the player was shown it.
    ///
    /// A PREALLOCATED RING, NOT A DICTIONARY, for the reason every per-frame
    /// object on this client keeps: `AllocationTests` forbids per-tick
    /// allocation. The parallel `_tickOf` is `ImpactPulseLog`'s answer rather
    /// than `EventDedup`'s, and for its reason: this class is asked about an
    /// arbitrary past tick and must answer without moving anything, which
    /// leaves it nothing to check a reused slot against except the tick
    /// recorded beside the sample.
    public sealed class HitFeedbackTrail
    {
        /// A slot no tick has claimed. `int.MinValue` rather than 0 or -1:
        /// tick 0 is legal, and a render tick is free to be negative before the
        /// clock is placed.
        const int NoTick = int.MinValue;

        readonly float2[] _positions;
        readonly int[] _tickOf;

        public HitFeedbackTrail(int capacityTicks)
        {
            if (capacityTicks < 1) capacityTicks = 1;
            _positions = new float2[capacityTicks];
            _tickOf = new int[capacityTicks];
            for (int i = 0; i < _tickOf.Length; i++) _tickOf[i] = NoTick;
        }

        public int Capacity => _positions.Length;

        /// Where this client's own body stood when `renderTick` was the moment
        /// on screen. Called once per frame; a repeated tick overwrites, which
        /// is what a frame that ran twice on one tick should do.
        public void NotePosition(int renderTick, float2 pos)
        {
            int slot = SlotOf(renderTick);
            _tickOf[slot] = renderTick;
            _positions[slot] = pos;
        }

        /// The two numbers `app-03et` needs: how many ticks passed between the
        /// blow being resolved and the player being shown it, and how far his
        /// body carried him over that gap.
        ///
        /// Refuses rather than guesses when either end has fallen out of the
        /// ring (Р82: a refusal is a value, never an exception) — a measurement
        /// nobody can stand behind is worse than no measurement.
        public bool TryMeasure(int eventTick, int shownTick,
            out int lagTicks, out float movedMeters)
        {
            lagTicks = 0;
            movedMeters = 0f;

            int fromSlot = SlotOf(eventTick);
            int toSlot = SlotOf(shownTick);

            // THE TICK BESIDE THE SAMPLE IS THE WHOLE GUARD, and it is what
            // `_tickOf` exists for: at a capacity of 32 the ticks 8 and 40
            // share a slot, so a ring that answered from the slot alone would
            // hand back a position the body left a second ago and call it a
            // measurement.
            if (_tickOf[fromSlot] != eventTick) return false;
            if (_tickOf[toSlot] != shownTick) return false;

            lagTicks = shownTick - eventTick;
            movedMeters = math.distance(_positions[fromSlot], _positions[toSlot]);
            return true;
        }

        int SlotOf(int tick)
        {
            int slot = tick % _positions.Length;
            return slot < 0 ? slot + _positions.Length : slot;
        }
    }
}
