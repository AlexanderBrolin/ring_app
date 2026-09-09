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
    ///  * ONE UNCONFIRMED PREDICTION AT A TIME — ⛔ FOR A CALLER WITH NO KEY
    ///    ONLY, since app-8dv T6. An edge is only acted on while nothing is
    ///    already waiting for its event. That is still the whole rule for the
    ///    DASH callers, who pass no key: a dash is one act at a time by nature.
    ///    For a caller that names its act (the shot, keyed by `ShotOrdinal`)
    ///    the rule is replaced rather than relaxed: every round of a burst is
    ///    predicted, and what stops the same round being shown twice is its
    ///    IDENTITY, not a lock. Why the lock existed at all is the paragraph
    ///    below — reconciliation hands out a second edge for one round — and
    ///    the key answers that question directly instead of by exclusion.
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
    /// WHAT IT COSTS, SAID PLAINLY — ⛔ AND app-8dv T6 IS WHAT STOPPED PAYING
    /// IT FOR THE SHOT. The paragraph below describes the price of the lock:
    /// some rounds of a held burst are shown late rather than early. That is
    /// the owner's own complaint at milestone В4, word for word — "after the
    /// first shot there is a delay during the spray" — and it is now paid only
    /// by the keyless callers, for whom it is not a cost at all. Kept because
    /// it explains why the lock was ever acceptable, and because the dash half
    /// still lives under it:
    /// While one prediction is unconfirmed, the
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
    /// something is shown late.
    /// ⚠ SINCE app-8dv T6 THAT GUARANTEE RESTS ON `TryConsume` TAKING THE HEAD
    /// RECORD, and it is worth saying because the first draft of the queue lost
    /// it: a missed edge leaves NO record, so the events that follow still line
    /// up with the records that remain. An implementation that SKIPPED records
    /// instead of taking them would answer each event with the next round's
    /// record — one shot silent, the next one twice.
    /// The two gates are very differently exposed to
    /// it: the fire window is one tick wide (~33 ms), while a dash holds its
    /// gate up for 90 ms, so only a frame longer than that can step over the
    /// dash's.
    ///
    /// ⛔⛔ A MATCH RESTART USED TO CLEAR NOTHING HERE, AND app-8dv T6 REVERSES
    /// THAT DECISION EXPLICITLY rather than quietly — the old text is kept
    /// below so the reversal can be read, not guessed at.
    ///
    /// WHAT IT SAID: "an unconfirmed prediction — or an unspent credit — is
    /// forgotten by its own window within a fraction of a second of the
    /// restart, and `_gateWasSatisfied` survives it, so a player who holds Fire
    /// across a restart gets no prediction for the first round of the new
    /// match — one shot shown with its event instead of ahead of it. A `Clear`
    /// that no caller has a reason to call would be a member kept for its own
    /// sake."
    ///
    /// WHY IT NO LONGER HOLDS. The windows still expire on their own, but the
    /// ring of SHOWN KEYS does not: a new match numbers its rounds from 1
    /// again, so keys the previous match already showed would refuse the new
    /// match's opening rounds outright — silently, and for as long as the ring
    /// is deep. And the one shot the old text was willing to lose is exactly
    /// the one that gets noticed: the first round after a restart. ⇒ `Reset`
    /// now has a production caller on each keyed instance
    /// (`AudioDirector.HandleWorldRestarted`, `MuzzleFlashView.
    /// HandleWorldRestarted`, both off `SimulationRunner.WorldRestarted` —
    /// `ClientMatchReset` cannot reach `Presentation`, Р180), and
    /// `_gateWasSatisfied` is cleared with everything else, so a held trigger
    /// across a restart DOES get its first round predicted.
    /// ⚠ THE DASH LATCHES ARE STILL NOT RESET, and the old argument holds for
    /// them unchanged: the restart zeroes every `PlayerState`, so a dash's gate
    /// is false on the first frame of the new match and its first dash gets a
    /// genuine rising edge.
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

        /// NO KEY AT ALL: the caller has no identity to give its act.
        /// It is a number rather
        /// than an overload because the dash callers must keep behaving exactly
        /// as they did (bd `app-g21`): with no key the identity check is off
        /// entirely, and the class falls back to the single-outstanding-
        /// prediction rule the eight original fixtures pin.
        /// ⛔ ZERO IS FREE BY CONSTRUCTION, not by luck: the shot key is the
        /// POST-increment `ShotOrdinal`, so the first round of a match is 1
        /// (`WeaponSystem` raises the counter after the shot). That is also
        /// what makes clearing the ring to `default` safe -- a zeroed slot
        /// cannot read as "shot 0 was already shown".
        public const int NoKey = 0;

        /// How many predicted acts can be outstanding at once, and how many
        /// shown keys are remembered.
        ///
        /// DERIVED, NOT CHOSEN, from the two numbers that actually bound the
        /// wait -- the precedent is `ClientEventQueue`'s own constructor
        /// ("capacity is DERIVED from the two numbers that bound the wait"):
        /// `ceil(BufferedWindowSeconds 0.5 / FireInterval 0.12)` = 5. Nothing
        /// slower than the fire cadence can put more rounds in the air than
        /// that inside one confirmation window.
        /// ⚠ THE RING IS NOT SHORTER THAN THE QUEUE, deliberately: a shown key
        /// forgotten while its own record is still queued would let the
        /// reconciliation echo of that very round through.
        /// ⛔ AND IT IS DERIVED FROM A BALANCE NUMBER, SO IT CARRIES THE SAME
        /// MAINTENANCE RULE `BufferedWindowSeconds` above carries: `FireInterval`
        /// lives in a `ScriptableObject` and is tunable without a recompile
        /// (CR 6), with a `[Range(0.01f, 5f)]` that refuses nothing. Take it
        /// below 0.1 s and `ceil(0.5 / FireInterval)` passes 5 — the queue then
        /// refuses grants during a sustained burst and counts them in
        /// `OverflowDroppedPredictions`. Whoever moves `FireInterval` or the
        /// window re-does this arithmetic and moves this number with them; the
        /// symptom of forgetting is a rising counter, not a wrong picture.
        const int DefaultCapacity = 5;

        bool _armed;
        float _expireAt;
        bool _gateWasSatisfied;
        bool _shownFromEvent;
        float _shownExpireAt;

        /// One granted prediction: which act it belongs to, when it stops
        /// waiting, and whether the caller actually SHOWED it.
        ///
        /// ⛔ `Shown` IS THE FIELD THE WHOLE QUEUE TURNS ON, and the reason is
        /// written where the defect was predicted -- `AudioDirector`'s G-4
        /// comment: "one shot's event could consume a record another shot had
        /// left behind". A grant is not a showing: the SFX gates can refuse a
        /// voice, a frame can have no doll to fire from.
        /// ⛔⛔ AND THE ANSWER IS NOT "MAKE SUCH A RECORD INVISIBLE" — that was
        /// the first attempt and a review took it apart. Skipping an unmarked
        /// record leaves it in the queue, so every later event is answered by
        /// the record of the round AFTER its own: one shot loses its feedback
        /// and the next gets it twice. The record stays VISIBLE and is taken in
        /// order; what `Shown` decides is only the ANSWER — `false` means "no
        /// prediction was made for this round, play it now".
        struct Granted
        {
            public int Key;
            public float ExpireAt;
            public bool Shown;
        }

        readonly Granted[] _queue = new Granted[DefaultCapacity];
        int _queueCount;

        /// The keys already SHOWN, newest overwriting oldest.
        /// ⛔ "OCCUPIED" MEANS "IN THIS RING", not "in the live queue" and not
        /// "ever handed out" (spec §3.5). The first does not survive the event
        /// that consumed the record -- and the reconciliation echo can arrive
        /// after it. The second breaks the moment `BeginReconcile` steps the
        /// ordinal BACKWARD, which it does on every correction.
        readonly int[] _shownKeys = new int[DefaultCapacity];
        int _shownWrite;

        /// Grants refused because every slot was outstanding -- refusal by
        /// value with a counter, the shape `ClientEventQueue` uses (Р82), not
        /// an exception and not a silent overwrite of somebody's record.
        public int OverflowDroppedPredictions;

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
        public bool ShouldPredict(bool gateSatisfied, float now, int key = NoKey)
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

            // NO KEY: the caller has no identity for its act (a dash), so the
            // original rule stands whole -- one unconfirmed prediction at a
            // time. Nothing below this line runs for those callers.
            if (key == NoKey) return !_armed;

            // THE ECHO, REFUSED BY IDENTITY RATHER THAN BY TIME (spec §3.5,
            // Р454). Reconciliation replays the tick that fired and hands out a
            // SECOND rising edge for a round already shown, while only one
            // event ever arrives -- showing it twice is `app-id9`.
            if (WasShown(key)) return false;

            // The same act already waiting: a repeat grant would put two
            // records where one event is coming.
            if (IndexOfQueued(key) >= 0) return false;

            if (_queueCount >= _queue.Length)
            {
                OverflowDroppedPredictions++;
                return false;
            }

            // ⚠ THE RECORD IS LAID DOWN BY THE GRANT, NOT BY THE SHOWING, and
            // its provisional deadline is this class's LONGEST window: the
            // caller has not told us which backend it is on yet -- `Arm` does
            // that -- and a record nobody marks has to expire on its own rather
            // than hold a slot forever.
            _queue[_queueCount++] = new Granted
            {
                Key = key,
                ExpireAt = now + BufferedWindowSeconds,
                Shown = false,
            };
            return true;
        }

        /// Records that the caller actually SHOWED a predicted act, and for how
        /// long it is willing to wait for the confirming event (class doc — the
        /// backend decides the window).
        public void Arm(float now, float windowSeconds, int key = NoKey)
        {
            if (key == NoKey)
            {
                _armed = true;
                _expireAt = now + windowSeconds;
                return;
            }

            int index = IndexOfQueued(key);
            // No record to mark: the grant expired, or this caller never had
            // one. Refusing by doing nothing is right -- inventing a record
            // here would let a showing with no grant behind it swallow the
            // next round's event.
            if (index < 0) return;

            _queue[index].Shown = true;
            _queue[index].ExpireAt = now + windowSeconds;
            NoteShownKey(key);
        }

        /// Whether the authoritative event that just arrived was already shown
        /// ahead of time, in which case its own feedback must be suppressed.
        /// Consumes the outstanding prediction when it answers true.
        public bool TryConsume(float now)
        {
            Expire(now);

            // ⛔⛔ THE HEAD RECORD, TAKEN WHETHER OR NOT IT WAS SHOWN, AND THE
            // ANSWER IS ITS `Shown` FLAG. This is the one line of the class a
            // review found wrong after it was written, so the reasoning is
            // recorded rather than trusted to be obvious.
            //
            // Events of one burst arrive in the order their rounds were fired
            // — `ClientEventQueue` orders delivery by tick and, within a tick,
            // by the `seq` the server assigned — so the k-th event belongs to
            // the k-th record, full stop. SKIPPING an unmarked record instead
            // of taking it breaks exactly that correspondence: the first
            // event would then be answered with the SECOND round's record.
            // Measured on the shipped numbers: round 1 is granted and its
            // voice is refused by the SFX gates (unmarked), round 2 is granted
            // and shown; round 1's event skips past its own record, eats round
            // 2's, and is suppressed — round 1 goes silent — while round 2's
            // own event finds nothing and plays a SECOND time. That is a lost
            // shot and `app-id9` in one input.
            //
            // Taking the head answers both: an unmarked head is removed and
            // reported as `false`, so the round's own event plays it (the
            // prediction never happened); a marked head is removed and
            // reported as `true`, so the duplicate is suppressed.
            if (_queueCount > 0)
            {
                bool shown = _queue[0].Shown;
                RemoveQueuedAt(0);
                return shown;
            }

            // The keyless path, untouched: a dash's single outstanding
            // prediction.
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

        /// A NEW MATCH: everything this class remembers about the previous one
        /// goes (app-8dv T6). Called from `Presentation`'s own
        /// `SimulationRunner.WorldRestarted`, because `ClientMatchReset` cannot
        /// reach here -- `Presentation.asmdef` does not reference
        /// `Ring.Networking` (Р180).
        ///
        /// ⛔ EVERY FIELD IS NAMED, INCLUDING `_gateWasSatisfied`, and that one
        /// is not a formality: the class header used to state that it survives
        /// a restart on purpose, so "a player who holds Fire across a restart
        /// gets no prediction for the first round of the new match". That
        /// decision is REVERSED here, deliberately and in writing -- the new
        /// match's first round is exactly the one the owner notices.
        /// ⛔ `OverflowDroppedPredictions` IS NOT CLEARED, the rule its
        /// neighbors keep word for word: a health counter that zeroed itself
        /// on every restart would hide the pattern it exists to surface.
        public void Reset()
        {
            _armed = false;
            _expireAt = 0f;
            _gateWasSatisfied = false;
            _shownFromEvent = false;
            _shownExpireAt = 0f;

            _queueCount = 0;
            for (int i = 0; i < _queue.Length; i++) _queue[i] = default;

            _shownWrite = 0;
            for (int i = 0; i < _shownKeys.Length; i++) _shownKeys[i] = NoKey;
        }

        bool WasShown(int key)
        {
            for (int i = 0; i < _shownKeys.Length; i++)
                if (_shownKeys[i] == key) return true;
            return false;
        }

        void NoteShownKey(int key)
        {
            _shownKeys[_shownWrite] = key;
            _shownWrite = (_shownWrite + 1) % _shownKeys.Length;
        }

        int IndexOfQueued(int key)
        {
            for (int i = 0; i < _queueCount; i++)
                if (_queue[i].Key == key) return i;
            return -1;
        }

        void RemoveQueuedAt(int index)
        {
            // Order carries meaning here -- the oldest record is the one the
            // next event belongs to -- so this shifts rather than swap-removes.
            for (int i = index; i < _queueCount - 1; i++) _queue[i] = _queue[i + 1];
            _queueCount--;
            _queue[_queueCount] = default;
        }

        void Expire(float now)
        {
            if (_armed && now > _expireAt) _armed = false;
            if (_shownFromEvent && now > _shownExpireAt) _shownFromEvent = false;

            // A record whose event never arrived is forgotten by its own
            // window, exactly as a single outstanding prediction always was.
            for (int i = _queueCount - 1; i >= 0; i--)
                if (now > _queue[i].ExpireAt) RemoveQueuedAt(i);
        }
    }
}
