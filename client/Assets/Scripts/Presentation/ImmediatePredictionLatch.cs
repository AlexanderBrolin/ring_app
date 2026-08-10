namespace Ring.Presentation
{
    /// The bookkeeping behind ImmediateMuzzleFeedback (Task 28), extracted into
    /// one place and repaired in Stage 2 Task 45b (bd `app-id9`): `MuzzleFlashView`
    /// and `AudioDirector` both show a shot's feedback in the frame the player
    /// presses Fire, ahead of the tick event that confirms it, and both then have
    /// to swallow that event so the shot is not shown twice. This class is that
    /// "show it once" rule, and nothing else — no Unity types, no clock of its
    /// own, no knowledge of what is being shown. The two views call it.
    ///
    /// WHAT WENT WRONG WITH THE ONE-BOOLEAN VERSION. It armed a single latch
    /// with a wall-clock deadline (`ImmediatePredictionTtlSeconds`, 0.05 s ≈ 1.5
    /// ticks) and consumed it only if the event landed inside that deadline. At
    /// `FireInterval` 0.12 s the owner saw a doubled flash on roughly every
    /// tenth to fifteenth round of a burst: the event arrived a few milliseconds
    /// late, the deadline had already dropped the latch, and the authoritative
    /// burst drew a second flash for a shot the player had already seen. The
    /// deadline was doing two incompatible jobs — matching a prediction to its
    /// event, and bounding how long a prediction that will never be confirmed
    /// stays armed.
    ///
    /// MATCHING IS BY ORDER, NOT BY CLOCK, AND NOT BY TICK. Predictions happen
    /// in the order the shots do, and the events arrive in that same order, so
    /// the n-th event confirms the n-th prediction however late it is: this
    /// class keeps a small FIFO of pending predictions instead of one flag, and
    /// an event consumes the oldest. The owner's own instruction was to match on
    /// the TICK, and the measurement that decided against it is in Task 45b's
    /// report: on the networked backend the tick a prediction is made against
    /// (FishNet's `LocalTick`, which is what the client's own predicted player
    /// state advances on) and the tick an event carries (the server's world
    /// tick, which the picture only reaches `InterpBufferTicks` later) are two
    /// counters with no fixed offset — `NetworkSimBackend.CurrentTick`'s own doc
    /// says so in as many words, and nothing in the facade exposes a mapping.
    /// A tick comparison would therefore have matched nothing at all there,
    /// while order matches on both backends without asking what a tick is.
    ///
    /// ONE PREDICTION PER SHOT IS THE CALLER'S EDGE, NOT A TIMER.
    /// `SimulationRunner.WouldFireThisFrame` stays true for every frame between
    /// the tick that opens the fire window and the tick that fires, so `Arm`
    /// alone would fire once per FRAME. `RisingEdge` is what makes it once per
    /// SHOT: the gate goes false again the moment the weapon's cooldown is
    /// reset, so its rising edge counts rounds. That is exact at any frame rate,
    /// and it does not care how late the confirmation is — which the old
    /// "already armed?" test did.
    ///
    /// THE TIMEOUT SURVIVES AS INSURANCE ONLY. A prediction can legitimately
    /// never be confirmed (the player releases Fire between the predicting frame
    /// and the confirming tick, or a dash starts the same frame with
    /// `CanFireWhileDash` false) — an accepted rare artifact since Task 28. Such
    /// an entry is dropped once `ImmediatePredictionTtlSeconds` has passed, so a
    /// stale one cannot swallow a real shot's feedback forever. What it costs is
    /// bounded and stated plainly: while a stale entry is pending, a real shot
    /// whose prediction was MISSED (a mispredicted own state, say) would have
    /// its feedback swallowed by that entry instead. Both halves of that are
    /// rare, and the alternative — a short deadline — is the defect this class
    /// exists to fix.
    ///
    /// `Capacity` is what a legitimate backlog can reach: at `FireInterval` 0.12
    /// s and a confirmation delay bounded by the timeout below, at most a
    /// handful of predictions are ever in flight. An overflow drops the OLDEST
    /// pending entry, which is the one closest to being stale anyway — the
    /// newcomer describes a shot the player just took, and refusing IT would
    /// double-show that shot.
    ///
    /// A MATCH RESTART IS NOT RESET HERE, and that is a decision rather than an
    /// omission: neither view subscribes to `WorldRestarted` for this state's
    /// sake, and the only thing a leftover entry can do is swallow one shot's
    /// feedback within the timeout of the restart — self-healing, where a
    /// `Clear` nobody calls would be a member kept for its own sake.
    public sealed class ImmediatePredictionLatch
    {
        public const int Capacity = 8;

        readonly float[] _expireAt = new float[Capacity];
        int _count;
        bool _gateWasSatisfied;

        /// Whether THIS frame is the one that should show a predicted shot: the
        /// rising edge of the caller's own gate (class doc). Must be called every
        /// frame the caller predicts at all, edge or not, because the answer is a
        /// function of the previous frame's gate.
        public bool RisingEdge(bool gateSatisfied)
        {
            bool rising = gateSatisfied && !_gateWasSatisfied;
            _gateWasSatisfied = gateSatisfied;
            return rising;
        }

        /// Records that the caller actually SHOWED a predicted shot. Separate
        /// from `RisingEdge` above on purpose: `AudioDirector`'s predicted voice
        /// can be gated out by `MinSfxInterval`/`VoicesPerSfx` after the edge is
        /// already spent, and arming for feedback that never played would make
        /// the real event swallow itself into silence.
        public void Arm(float now)
        {
            Expire(now);
            if (_count == Capacity) DropOldest();
            _expireAt[_count++] = now + SimulationRunner.ImmediatePredictionTtlSeconds;
        }

        /// Whether the authoritative event that just arrived was already shown
        /// ahead of time, in which case its own feedback must be suppressed.
        /// Consumes the oldest pending prediction when it answers true.
        public bool TryConsume(float now)
        {
            Expire(now);
            if (_count == 0) return false;
            DropOldest();
            return true;
        }

        /// Entries are armed in time order, so the expired ones are always a
        /// prefix — one walk from the front, no scan.
        void Expire(float now)
        {
            while (_count > 0 && _expireAt[0] <= now) DropOldest();
        }

        void DropOldest()
        {
            _count--;
            for (int i = 0; i < _count; i++) _expireAt[i] = _expireAt[i + 1];
        }
    }
}
