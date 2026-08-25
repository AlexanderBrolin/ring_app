namespace Ring.Presentation
{
    /// The bookkeeping behind ImmediateMuzzleFeedback (Task 28), extracted into
    /// one place in Stage 2 Task 45b and corrected in its fix-round 1: a
    /// component shows the feedback of an act in the frame the player performs
    /// it, ahead of the tick event that confirms the act, and then has to
    /// swallow that event so the act is not shown twice. This class is that
    /// "show it once" rule, and nothing else — no Unity types, no clock of its
    /// own, no knowledge of what is being shown.
    ///
    /// THREE COMPONENTS AND TWO PREDICTED THINGS CALL IT, one instance each —
    /// bd `app-g21` added the DASH beside Task 28's SHOT:
    ///  * the shot — `MuzzleFlashView`'s burst and `AudioDirector`'s report,
    ///    both gated on `SimulationRunner.WouldFireThisFrame`, both confirmed
    ///    by `ProjectileFired`;
    ///  * the dash — `PersistentPropsDirector`'s floor mark and
    ///    `AudioDirector`'s dash sound, both gated on
    ///    `SimulationRunner.DashingThisFrame`, both confirmed by
    ///    `PlayerDashed`.
    /// An instance per predicted THING and not merely per component, which is
    /// why `AudioDirector` holds two of them: one shot's outstanding prediction
    /// has nothing to say to a dash's, and a shared counter would let either
    /// one silence the other.
    ///
    /// THE RULE IS THREE FACTS, ALL OF THEM THIS CLASS'S OWN.
    ///  * ONE PREDICTION PER GATE PULSE. Both gates are LEVELS:
    ///    `WouldFireThisFrame` stays true for every frame between the tick that
    ///    opens the fire window and the tick that fires, `DashingThisFrame` for
    ///    every frame of a 90 ms dash, so arming on the level would arm once
    ///    per FRAME. The rising edge is what counts acts instead of frames.
    ///  * ONE UNCONFIRMED PREDICTION AT A TIME. An edge is only acted on while
    ///    nothing is already waiting for its event.
    ///  * AN EVENT THAT CAME FIRST SWALLOWS THE NEXT EDGE
    ///    (`NoteShownFromEvent`, bd `app-g21`) — the opposite order to the two
    ///    facts above, which only the dash's gate can produce.
    /// None of the three reads a number that belongs to somebody else (lesson
    /// 155): not the weapon's cadence, not a network timing, not a tick
    /// counter — only the caller's own clock and this class's own two windows.
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
    /// the reconcile does not). The dash has the identical shape one gate over
    /// — a replay can put `PlayerState.DashTimer` back on a tick the client had
    /// already run down, positive → 0 → positive with no second dash anywhere —
    /// and it is closed by the identical fact
    /// (`PersistentPropsDirector.PredictDashGlow`'s own doc).
    ///
    /// WHY THE THIRD FACT EXISTS — A GATE CAN RISE ON THE TICK THAT EMITS ITS
    /// OWN EVENT (bd `app-g21`, fix-round). `WouldFireThisFrame` goes DOWN on
    /// the tick that fires, so a shot's event can only ever reach a view after
    /// that shot's edge, and the two facts above order the pair by themselves.
    /// `DashingThisFrame` goes UP on the tick that emits `PlayerDashed` and
    /// stays up for the whole dash, so its readers can be handed the event and
    /// the edge in EITHER order — and on the local backend the event is always
    /// first, because `SimulationRunner` (pinned -50) advances the tick and fans
    /// its events out inside its own `Update`, before any view's `Update` or
    /// `LateUpdate` runs. Each reader has already shown its own cosmetic by the
    /// time the edge reaches it, and `TryConsume` has nothing to consume
    /// because nothing was predicted. Without this fact solo would show every
    /// one of this client's own dashes twice — a regression where there was no
    /// defect.
    ///
    /// AND THAT CREDIT LIVES BY A WINDOW, NOT BY THE GATE'S LEVEL, WHICH IS THE
    /// WHOLE POINT OF PUTTING IT HERE. The obvious shape — a bit in each reader,
    /// set by the event and cleared when the gate goes false — was written first
    /// and was wrong: an on-hit render pin `SimulationRunner` used to own (Task
    /// Т10, app-88jb, removed it whole) pinned `RenderCurr` at a COPY while the
    /// simulation kept ticking underneath it, so a dash that started under that
    /// pin emitted its event while the gate still read the pinned `DashTimer`
    /// 0 — clearing the bit that had just been set — and then raised its edge
    /// when the pin let go, with the dash still running. A second mark, a
    /// meter up the dash line, on about as ordinary a sequence as this game
    /// has (hit something, dash away). A window does not care what a stalled
    /// gate did to the level, and the local backend's own number covers this by
    /// construction: an edge can only rise while the dash is still running, a
    /// dash is `HeroConfig.DashDuration` 0.09 s, and `SameFrameWindowSeconds` is
    /// 0.1 s — sized against that now-removed mechanism, and left unchanged
    /// since a window-based credit costs nothing extra to keep once armed. On
    /// the networked backend the credit is only ever taken out when a
    /// prediction did NOT happen (an arriving event that finds nothing armed),
    /// and `BufferedWindowSeconds` 0.5 s expires inside the shortest gap two
    /// dashes can have — 0.61 s, a 0.09 s dash into a 0.52 s `SlideDuration` on
    /// the `LinkWindowSeconds` path that bypasses `DashCooldown` 0.9 s
    /// altogether.
    ///
    /// EXACTLY ONE EDGE IS SWALLOWED, and then the credit is spent — the edge
    /// it was taken out for is the edge it pays for, so nothing about a dash
    /// already shown can reach the dash after it. The window is the backstop
    /// for the one case where that edge never comes at all (a frame longer than
    /// the dash — "WHERE THE EDGE IS MISSED" below); a credit that neither the
    /// edge nor the clock could clear would cost the NEXT dash its prediction,
    /// which is the very lateness this class was extended to fix.
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
    /// THE DASH NEVER PAYS IT: two dashes are 0.61 s apart at the very closest
    /// (the arithmetic in "AND THAT CREDIT LIVES BY A WINDOW" above), against a
    /// 0.5 s window on the slower backend, so a dash's prediction is always
    /// either confirmed or expired before the next dash can ask for one.
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
    /// `CanFireWhileDash` false; for the dash, a reconciliation that rolls the
    /// dash away before the server ever saw one) — an accepted rare artifact
    /// since Task 28. Such a prediction is forgotten once its window has passed,
    /// so it cannot suppress the next act's feedback forever, and a credit taken
    /// out by `NoteShownFromEvent` is forgotten on the same terms. The window
    /// comes from the BACKEND (`ISimBackend.ImmediatePredictionWindowSeconds`)
    /// because the two backends confirm at wildly different speeds — see the two
    /// constants below.
    ///
    /// WHERE THE EDGE IS MISSED, AND WHY THAT IS THE HARMLESS DIRECTION. A gate
    /// that is up for a short time can be stepped over by a frame longer than
    /// it — a rate below the tick rate, a hitch, a multi-tick catch-up flush:
    /// the gate reads false both before and after, no edge is seen, and no
    /// prediction is made. That act's feedback then comes with its event,
    /// exactly as if the prediction had been refused. Nothing is shown twice;
    /// something is shown late. The two gates are very differently exposed to
    /// it: the fire window is one tick wide (~33 ms), while a dash holds its
    /// gate up for 90 ms, so only a frame longer than that can step over the
    /// dash's.
    ///
    /// A MATCH RESTART CLEARS NOTHING HERE. `AudioDirector` does subscribe to
    /// `WorldRestarted` (for `StopAll`) and so does `PersistentPropsDirector`
    /// (for `Clear`), while `MuzzleFlashView` does not, but not one of them
    /// resets this state, and that is a decision: an unconfirmed prediction —
    /// or an unspent credit — is forgotten by its own window within a fraction
    /// of a second of the restart, and `_gateWasSatisfied` survives it, so a
    /// player who holds Fire across a restart gets no prediction for the first
    /// round of the new match — one shot shown with its event instead of ahead
    /// of it. (A dash cannot survive a restart the same way: the restart zeroes
    /// every `PlayerState`, so its gate is false on the first frame of the new
    /// match and the first dash of it gets a genuine rising edge.) A `Clear`
    /// that no caller has a reason to call would be a member kept for its own
    /// sake.
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
        ///
        /// IT ALSO HAS TO OUTLIVE A DASH, since bd `app-g21` (the third fact of
        /// the class doc). This is the backend on which a dash's event always
        /// precedes its edge; an on-hit render pin `SimulationRunner` used to
        /// own (Task Т10, app-88jb, removed it whole) could put those two in
        /// different frames, and the credit had to survive from the one to the
        /// other — the widest that gap could be was the dash itself,
        /// `HeroConfig.DashDuration` 0.09 s, which is why this constant is
        /// sized past it rather than past the honest one-tick-plus-a-frame
        /// bound the doc above derives. Left unchanged since a window-based
        /// credit costs nothing extra to keep once armed (class doc, "THE
        /// WINDOW IS INSURANCE, NOT THE MATCH").
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
        ///
        /// AND IT HAS A CEILING NOW, since bd `app-g21`: an unspent credit
        /// (`NoteShownFromEvent`) lives this long too, so a number above the
        /// shortest gap between two dashes — 0.61 s, the class doc has the
        /// arithmetic — would let a dash shown from its event swallow the NEXT
        /// dash's prediction. Between that 0.61 s ceiling and the 0.35 s bound
        /// computed above, this constant has roughly a tenth of a second of room
        /// in either direction; whoever needs more than that splits the two uses
        /// into two numbers rather than stretching this one.
        public const float BufferedWindowSeconds = 0.5f;

        bool _armed;
        float _expireAt;
        bool _gateWasSatisfied;
        bool _shownFromEvent;
        float _shownExpireAt;

        /// Whether THIS frame should show a predicted act — the three facts of
        /// the class doc, asked as one question. It MUST be called on every
        /// frame the caller predicts at all, edge or not, because the edge is a
        /// function of the previous frame's gate.
        ///
        /// It does not arm anything. A caller that ends up showing nothing (no
        /// doll to fire from; a voice the SFX gates refused) leaves this class
        /// with no prediction outstanding, which is exactly right — the event
        /// that follows then finds nothing to consume and shows the feedback
        /// itself.
        ///
        /// AN EDGE IS SPENT WHETHER OR NOT IT IS GRANTED, and that includes the
        /// edge a credit from `NoteShownFromEvent` refuses: the credit is
        /// consumed here and now, so the act AFTER this one is predicted
        /// normally.
        public bool ShouldPredict(bool gateSatisfied, float now)
        {
            bool rising = gateSatisfied && !_gateWasSatisfied;
            _gateWasSatisfied = gateSatisfied;
            if (!rising) return false;
            Expire(now);
            if (_shownFromEvent)
            {
                _shownFromEvent = false;
                return false;
            }
            return !_armed;
        }

        /// Records that the caller actually SHOWED a predicted act, and for how
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

        /// The mirror image of `Arm`: the caller has just shown this act from
        /// its own authoritative EVENT, with no prediction of its own behind it,
        /// so the rising edge that is still to come for the same act must be
        /// refused. The third fact of the class doc, and the whole reason a dash
        /// needs one — a shot's event can never precede its edge, a dash's
        /// routinely does.
        ///
        /// `windowSeconds` IS A LIFETIME AND DELIBERATELY NOT "UNTIL THE GATE
        /// GOES FALSE": the level is what the now-removed on-hit render pin
        /// used to take away in the middle of the dash it belongs to (class
        /// doc, in full). It is the same
        /// number `Arm` is given, from the same place, and it works out on both
        /// backends: the local one's window outlives a whole dash, and the
        /// networked one's expires well inside the shortest gap two dashes can
        /// have (both constants carry their own half of that arithmetic).
        ///
        /// CALL IT ONLY WHERE THE COSMETIC WAS REALLY SHOWN — below the
        /// `TryConsume` that would have suppressed it, and for a sound only when
        /// a voice actually started (the same G-4 rule `Arm` follows). A credit
        /// taken out by an event that showed nothing would refuse the prediction
        /// of a dash nobody has seen yet.
        public void NoteShownFromEvent(float now, float windowSeconds)
        {
            _shownFromEvent = true;
            _shownExpireAt = now + windowSeconds;
        }

        void Expire(float now)
        {
            if (_armed && now > _expireAt) _armed = false;
            if (_shownFromEvent && now > _shownExpireAt) _shownFromEvent = false;
        }
    }
}
