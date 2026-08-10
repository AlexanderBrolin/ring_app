namespace Ring.Presentation
{
    /// The bookkeeping behind ImmediateMuzzleFeedback (Task 28), extracted into
    /// one place in Stage 2 Task 45b and corrected in its fix-round 1:
    /// `MuzzleFlashView` and `AudioDirector` both show a shot's feedback in the
    /// frame the player presses Fire, ahead of the tick event that confirms it,
    /// and both then have to swallow that event so the shot is not shown twice.
    /// This class is that "show it once" rule, and nothing else — no Unity
    /// types, no clock of its own, no knowledge of what is being shown. The two
    /// views call it.
    ///
    /// THE RULE IS TWO FACTS, BOTH OF THEM THIS CLASS'S OWN.
    ///  * ONE PREDICTION PER GATE PULSE. `SimulationRunner.WouldFireThisFrame`
    ///    stays true for every frame between the tick that opens the fire window
    ///    and the tick that fires, so arming on the level would arm once per
    ///    FRAME. The rising edge is what counts rounds instead of frames.
    ///  * ONE UNCONFIRMED PREDICTION AT A TIME. An edge is only acted on while
    ///    nothing is already waiting for its event.
    /// Neither test reads a number that belongs to somebody else (lesson 155):
    /// not the weapon's cadence, not a network timing, not a tick counter.
    ///
    /// WHY THE SECOND FACT EXISTS — RECONCILIATION HANDS OUT A SECOND EDGE FOR
    /// ONE ROUND (fix-round 1, G-2). On a networked client the local player's
    /// own state is predicted and then corrected:
    /// `PlayerPredictionCore.BeginReconcile` assigns the authoritative state
    /// whole ("timers, stamina and the gate counters", its own doc), and the
    /// replay that follows can put the shot on a different tick than the client
    /// guessed. `WeaponSystem.WouldFireThisTick` reads `FireCooldown`, so a
    /// correction that moves that number back re-opens the gate for a round that
    /// has already been shown: false → true again, a second rising edge, a
    /// second flash, and only ONE event ever arrives to swallow. Refusing to act
    /// on an edge while a prediction is still unconfirmed closes that by
    /// construction, because the correction always arrives while the round's own
    /// event is still on its way (the event waits out the interpolation buffer;
    /// the reconcile does not).
    ///
    /// WHAT IT COSTS, SAID PLAINLY. While one prediction is unconfirmed, the
    /// next round gets no predicted feedback — its flash comes with its event
    /// instead. On the local backend that costs nothing: the event lands in the
    /// very next frame, so a burst at `FireInterval` 0.12 s never has a
    /// prediction outstanding when the next round is fired. On a networked
    /// client, where confirmation takes an interpolation buffer plus the round
    /// trip, some rounds of a held burst are shown late rather than early. That
    /// is the trade the owner's own complaint asks for: a shot shown late is a
    /// shot shown once, and `app-id9` was opened about seeing one shot twice.
    ///
    /// MATCHING IS BY ORDER, NOT BY TICK. The owner's instruction was to match a
    /// prediction to its event by TICK, and the measurement that decided against
    /// it is in Task 45b's report: on the networked backend the tick a
    /// prediction is made against (FishNet's `LocalTick`, which is what the
    /// client's own predicted state advances on) and the tick an event carries
    /// (the server's world tick, which the picture only reaches
    /// `InterpBufferTicks` later) are two counters with no fixed offset —
    /// `NetworkSimBackend.CurrentTick`'s own doc says so in as many words, and
    /// nothing in the facade exposes a mapping. With one prediction outstanding
    /// at a time, "the event that arrives confirms the prediction that is
    /// waiting" needs no tick at all.
    ///
    /// THE WINDOW IS INSURANCE, NOT THE MATCH. A prediction can legitimately
    /// never be confirmed (the player releases Fire between the predicting frame
    /// and the confirming tick, or a dash starts the same frame with
    /// `CanFireWhileDash` false) — an accepted rare artifact since Task 28.
    /// Such a prediction is forgotten once its window has passed, so it cannot
    /// suppress the next round's feedback forever. The window comes from the
    /// BACKEND (`ISimBackend.ImmediatePredictionWindowSeconds`) because the two
    /// backends confirm at wildly different speeds — see the two constants
    /// below.
    ///
    /// WHERE THE EDGE IS MISSED, AND WHY THAT IS THE HARMLESS DIRECTION. The
    /// gate's window is one tick wide (~33 ms), so a frame longer than a tick —
    /// a rate below the tick rate, a hitch, a multi-tick catch-up flush — can
    /// step over it: the gate reads false both before and after, no edge is
    /// seen, and no prediction is made. That round's feedback then comes with
    /// its event, exactly as if the prediction had been refused. Nothing is
    /// shown twice; something is shown late.
    ///
    /// A MATCH RESTART CLEARS NOTHING HERE. `AudioDirector` does subscribe to
    /// `WorldRestarted` (for `StopAll`) and `MuzzleFlashView` does not, but
    /// neither one resets this state, and that is a decision: an unconfirmed
    /// prediction is forgotten by its own window within a fraction of a second
    /// of the restart, and `_gateWasSatisfied` survives it, so a player who
    /// holds Fire across a restart gets no prediction for the first round of the
    /// new match — one shot shown with its event instead of ahead of it. A
    /// `Clear` that no caller has a reason to call would be a member kept for
    /// its own sake.
    public sealed class ImmediatePredictionLatch
    {
        /// For a backend that confirms a prediction in the frame after it was
        /// made: the local one, where `SimulationRunner.Update` advances the
        /// tick and flushes its events before any view runs (the facade is
        /// pinned at `[DefaultExecutionOrder(-50)]`). The honest bound is one
        /// tick of accumulator (33 ms at 30 Hz — the shot may land on the tick
        /// after the frame that predicted it) plus a frame; 0.1 s is that
        /// doubled. It is deliberately NOT the networked number: a prediction
        /// that will never be confirmed blocks the next round's prediction for
        /// as long as this window lasts, and `app-id9` named that cost when it
        /// warned that raising the window "удлиняет окно ложного предсказания".
        public const float SameFrameWindowSeconds = 0.1f;

        /// For a backend whose confirmation crosses the wire: the client
        /// predicts ahead of the server (~RTT/2), the server simulates and sends
        /// the tick back (~RTT/2 plus a tick), the render clock waits out
        /// `NetConfig.InterpBufferTicks` and, on a lost packet, the redundant
        /// re-send arrives up to `NetConfig.EventRedundancyTicks` later. At the
        /// 80 ms RTT + 5% loss every playtest build must survive, and at those
        /// two fields' shipped values (3 and 4 ticks = 0.1 s and 0.13 s), that
        /// is about 0.35 s; 0.5 s is that with margin.
        ///
        /// IT IS A BOUND WITH A MARGIN, NOT A FUNCTION OF THOSE FIELDS, and it
        /// is a constant on purpose: the round-trip term is in no config at all,
        /// so computing this from `NetConfig` would give a precision the number
        /// does not have and would drag a `NetConfig` reference into classes
        /// that need nothing else from it. THE COROLLARY IS THE MAINTENANCE
        /// RULE: whoever raises `InterpBufferTicks` or `EventRedundancyTicks`
        /// re-does the arithmetic above and moves this number with them.
        public const float BufferedWindowSeconds = 0.5f;

        bool _armed;
        float _expireAt;
        bool _gateWasSatisfied;

        /// Whether THIS frame should show a predicted shot — the two facts of
        /// the class doc, asked as one question. It MUST be called on every
        /// frame the caller predicts at all, edge or not, because the edge is a
        /// function of the previous frame's gate.
        ///
        /// It does not arm anything. A caller that ends up showing nothing (no
        /// doll to fire from; a voice the SFX gates refused) leaves this class
        /// with no prediction outstanding, which is exactly right — the event
        /// that follows then finds nothing to consume and shows the feedback
        /// itself.
        public bool ShouldPredict(bool gateSatisfied, float now)
        {
            bool rising = gateSatisfied && !_gateWasSatisfied;
            _gateWasSatisfied = gateSatisfied;
            if (!rising) return false;
            Expire(now);
            return !_armed;
        }

        /// Records that the caller actually SHOWED a predicted shot, and for how
        /// long it is willing to wait for the confirming event (class doc — the
        /// backend decides the window).
        public void Arm(float now, float windowSeconds)
        {
            _armed = true;
            _expireAt = now + windowSeconds;
        }

        /// Whether the authoritative event that just arrived was already shown
        /// ahead of time, in which case its own feedback must be suppressed.
        /// Consumes the outstanding prediction when it answers true.
        public bool TryConsume(float now)
        {
            Expire(now);
            if (!_armed) return false;
            _armed = false;
            return true;
        }

        void Expire(float now)
        {
            if (_armed && now > _expireAt) _armed = false;
        }
    }
}
