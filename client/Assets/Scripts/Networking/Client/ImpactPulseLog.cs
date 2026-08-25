using Ring.Simulation.Combat;

namespace Ring.Networking.Client
{
    /// Tick -> impulse table over the whole correction window (app-88jb Т9,
    /// decision Р417). PREALLOCATED ring, no dictionary: AllocationTests
    /// forbids per-tick allocation, and this project has refused hash
    /// structures five times in writing.
    ///
    /// THE PARALLEL `_tickOf` IS THIS CLASS'S OWN ANSWER, NOT `EventDedup`'S
    /// TRICK, AND THE DIFFERENCE IS THE CONTRACT — worth stating because the
    /// two look alike from a distance. `EventDedup.SlotOf` (`EventDedup.cs:282`)
    /// is the precedent for the ADDRESSING and for nothing else: it stores no
    /// tick in a slot at all. It handles staleness from a monotone edge
    /// instead — `AdvanceWindow` (`EventDedup.cs:260-279`) wipes every slot the
    /// newest edge of the ring passes, "so a reused one cannot answer with a
    /// previous occupant's bits". That works there because the edge only ever
    /// moves forward and every question it is asked is about the window it has
    /// already advanced to.
    ///
    /// `For` here has no such edge. It is a PURE function, asked about an
    /// arbitrary tick as many times as FishNet chooses to replay it, and it
    /// must answer without moving anything — which leaves it nothing to check
    /// a slot against except the tick recorded beside the impulse. Borrowing
    /// the wipe-on-advance trick instead would break the very test this ring
    /// is built for (`ReplayingTheSameTick_GivesTheSameAnswer`) and would
    /// leave the mutation "`For` does not check `_tickOf[slot]`" with no
    /// victim at all.
    ///
    /// ⚠ WHOSE TICK GOES IN AND OUT OF THIS TABLE IS THE CALLER'S PROBLEM, AND
    /// THIS CLIENT HAS TWO CLOCKS. The world's own `SimulationWorld.CurrentTick`
    /// stamps every snapshot header (`SnapshotAssembler.cs:621`, `:1713`) and so
    /// every event tick `EventDedup` hands back, while FishNet's prediction runs
    /// on `TimeManager.LocalTick`. `EffectiveInputBatch`'s own doc records what
    /// happens when the two are mixed — "UNRELATED ... so subtracting one from
    /// the other directly computes GARBAGE" — and `NetworkSimBackend.
    /// SampleOwnPlayer` keeps the same rule ("nothing here subtracts one tick
    /// number from the other"). This table cannot detect the mistake: two ticks
    /// of different clocks compare unequal, so a mismatched pair simply answers
    /// `None` forever, silently. Both callers MUST stand in one domain.
    internal sealed class ImpactPulseLog
    {
        /// A slot no tick has claimed. `0` cannot serve here: tick 0 is a
        /// legal tick, so a zero-filled table would answer it with whatever
        /// slot 0 happens to hold.
        ///
        /// It doubles as the value `Prune` leaves behind, and being the LARGEST
        /// `uint` is what makes that free: `NoTick < oldestKeptTick` is false
        /// for every tick a match can reach, so an emptied slot is never
        /// re-examined and needs no second test of its own.
        const uint NoTick = uint.MaxValue;

        /// The summed impulse of the tick `_tickOf[slot]` holds, addressed by
        /// `tick % Length`.
        readonly ImpactPulse[] _pulses;

        /// Which tick each slot currently holds, `NoTick` for none. Parallel
        /// to `_pulses` rather than a field inside it, because `ImpactPulse`
        /// belongs to Ring.Simulation.Combat and is the value the prediction
        /// step consumes — this ring's bookkeeping is this class's business
        /// alone and has no place in it.
        readonly uint[] _tickOf;

        /// A HOSTILE CAPACITY IS REFUSED, NEVER THROWN (Р82) — the shape
        /// `CorrectionWindow` already has one file over (`CorrectionWindow.cs:89-90`,
        /// witnessed by `CorrectionWindowTests.cs:131`). A ring of zero slots
        /// has no representation: it could hold no tick, and `tick % 0` raises
        /// `DivideByZeroException` on the first `Add` or `For` — the
        /// constructor itself would NOT throw, since a zero-length array is
        /// legal, so the failure would surface far from its cause. The caller
        /// is production code holding a structural constant, so this guards a
        /// future caller rather than today's one.
        public ImpactPulseLog(int capacityTicks)
        {
            if (capacityTicks < 1) capacityTicks = 1;
            _pulses = new ImpactPulse[capacityTicks];
            _tickOf = new uint[_pulses.Length];
            // Through `Reset` rather than a second fill loop written out here:
            // "every slot holds no tick" is one statement, and a fresh table
            // and a cleared one are the same table (rule 2).
            Reset();
        }

        /// Adds one authoritative blow to the tick it was resolved on. Called
        /// once per PlayerDamaged the decoder hands over; SUMS, because two
        /// hits in one tick are the norm (D2-C4). Applying it more than once
        /// is what EventDedup already prevents (EventRedundancyTicks 4 means
        /// up to four deliveries of the SAME event).
        ///
        /// A SLOT WHOSE TICK HAS CHANGED IS OVERWRITTEN, NOT ADDED INTO. The
        /// ring reuses a slot every `Length` ticks, so the occupant found there
        /// is a stranger far more often than it is this tick's own running sum;
        /// adding into it would carry an impulse from `Length` ticks ago into
        /// a blow that has nothing to do with it.
        public void Add(uint tick, in ImpactPulse pulse)
        {
            int slot = SlotOf(tick);
            if (_tickOf[slot] != tick)
            {
                _tickOf[slot] = tick;
                _pulses[slot] = pulse;
                return;
            }

            _pulses[slot] = new ImpactPulse(_pulses[slot].Delta + pulse.Delta,
                _pulses[slot].TiltImpulse + pulse.TiltImpulse);
        }

        /// The summed impulse of that tick, or ImpactPulse.None. Pure — a
        /// replay asks the same tick as many times as FishNet replays it.
        public ImpactPulse For(uint tick)
        {
            int slot = SlotOf(tick);
            return _tickOf[slot] == tick ? _pulses[slot] : ImpactPulse.None;
        }

        /// Drops everything older than `oldestKeptTick`.
        ///
        /// A WALK OF THE WHOLE RING, NOT OF THE GAP SINCE LAST TIME, and the
        /// bound is the point: the ring has `Length` slots and no input can
        /// make this loop longer than that — the same reasoning
        /// `EventDedup.AdvanceWindow` gives for clamping its own walk. There is
        /// no cursor to keep in step either, which is what lets `For` stay pure.
        ///
        /// `<` OVER RAW `uint`, WITH NO WRAP ARITHMETIC. Both clocks a caller
        /// could hand this counter up from zero and neither comes near
        /// `uint.MaxValue` — a match's world tick count is in the tens of
        /// thousands, and FishNet's process-lifetime tick would need years at
        /// 30 Hz — so the ordering is total over every value this ever sees.
        /// The sentinel rides along for free: `NoTick` is the largest `uint`,
        /// so an empty slot compares as newer than anything and is left alone.
        public void Prune(uint oldestKeptTick)
        {
            for (int i = 0; i < _tickOf.Length; i++)
            {
                if (_tickOf[i] < oldestKeptTick) _tickOf[i] = NoTick;
            }
        }

        /// Forgets every tick, so that a match epoch cannot be answered with a
        /// blow from the one before it. The client's reset seam
        /// (`ClientMatchReset.ResetForEpoch`) is where that switch happens.
        ///
        /// `_pulses` IS DELIBERATELY LEFT DIRTY. `For` answers off `_tickOf`
        /// alone, so a stale impulse behind a sentinel is unreachable — and
        /// clearing it as well would make the SENTINEL untestable: a table that
        /// forgot by zeroing `_tickOf` would then also answer tick 0 with a
        /// zeroed impulse and read as correct.
        public void Reset()
        {
            for (int i = 0; i < _tickOf.Length; i++) _tickOf[i] = NoTick;
        }

        /// The one home of this ring's addressing — the precedent, verbatim in
        /// shape, is `EventDedup.SlotOf` (`EventDedup.cs:282`).
        int SlotOf(uint tick) => (int)(tick % (uint)_pulses.Length);
    }
}
