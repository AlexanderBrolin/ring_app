using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// Stage 2 Task 31 (spec §3.9, Р57/Р38/Р83): the client's interpolation
    /// clock — the one thing that decides WHICH MOMENT of the world is on
    /// screen this frame.
    ///
    /// NAMESPACE, NOT ASSEMBLY. `Ring.Networking.Client` is a folder inside the
    /// `Ring.Networking` assembly, next to `EventDedup`. Nothing here touches
    /// UnityEngine or FishNet: this is a pure decision object, driven entirely
    /// by its three entry points, and testable in EditMode with no scene. The
    /// component that subscribes to `TimeManager` and feeds it real frames is
    /// Task 36; the receiver that owns it and calls `ResetForEpoch` on the
    /// Reliable lifecycle message is Task 32; the runner that hands it
    /// `Time.unscaledDeltaTime` is Task 44.
    ///
    /// THE DEFECT THIS CLASS EXISTS TO KILL (Р57, the critical correction of
    /// spec v3). Version 2 ran the clock as an ANCHOR:
    /// `RenderTick = LastAppliedSnapshotTick - InterpBufferTicks`, recomputed
    /// only when a snapshot landed. Time then moved in the arrival pattern of
    /// the network instead of in local time — a lost packet froze the picture
    /// for a whole tick and the next packet advanced it by two. The
    /// interpolation buffer therefore smoothed away precisely nothing at the
    /// 5% loss every playtest build is required to survive (Critical Rule 7),
    /// which is the entire reason the buffer exists.
    ///
    /// SO `OnSnapshot` NEVER MOVES TIME, AND THAT IS STRUCTURAL, NOT
    /// INCIDENTAL. It only records what has been seen. `Advance` is the sole
    /// writer of `_renderTime`, including the very first placement of it. Any
    /// path that let an arriving packet set the clock would BE the anchored
    /// clock again, in miniature, and would be one careless edit away from
    /// being the whole thing.
    ///
    /// TIME IS COUNTED IN WORLD TICKS, NOT SECONDS. `RenderTime` is a double
    /// whose unit is one simulation tick, so `RenderTick = floor(RenderTime)`
    /// and `Phase = frac(RenderTime)` are a decomposition rather than a
    /// conversion, and the Р38 invariant `RenderTime == RenderTick + Phase`
    /// holds by construction. `Advance` converts its seconds through
    /// `SimulationWorld.TickDt` — the single source of the 30 Hz rate
    /// (ADR-002 T5) — so this file holds no second copy of the tick rate.
    ///
    /// THE TARGET IS `newestBufferedTick - InterpBufferTicks`, AND
    /// `newestBufferedTick` IS A MAXIMUM. A frame that lost a race with the one
    /// behind it must not pull the target back: the moment it names has already
    /// been shown, and chasing it would make every reordered datagram a visible
    /// hitch. The target is floored at zero, because the first ticks of a match
    /// name a moment before the match began — see the start-up note below.
    ///
    /// CORRECTION IS A CHANGE OF PACE, NOT A JUMP — WITH ONE ASYMMETRIC
    /// EXCEPTION.
    ///   * The error the controller acts on is measured at the END of the
    ///     frame: where a free-running step would land the clock, against the
    ///     target it has now. Measuring it at the start of the frame instead
    ///     would settle the clock a whole frame past its target and quietly
    ///     spend one tick of the interpolation buffer for the life of the
    ///     match.
    ///   * Inside the snap threshold the rate becomes `1 ± SlewFraction` and
    ///     the error is walked out over many frames.
    ///   * Beyond `RenderClockSnapTicks` of error the clock JUMPS onto the
    ///     target — but only FORWARD, and only when that threshold is a
    ///     positive number of ticks; anything else disables the jump rather
    ///     than reshaping it (see the hostile-input note below). A clock that
    ///     has run AHEAD of its target
    ///     (the buffer starved) is never snapped back, however far ahead it
    ///     has run, because `renderTime` is monotonic inside an epoch (spec
    ///     §3.9) and a backward jump would replay moments the player has
    ///     already seen. It is slewed down instead, and what to do about a
    ///     render time that has outrun the newest snapshot is the stale
    ///     policy's problem (Task 37), not the clock's.
    ///
    /// HYSTERESIS, AND WHY IT HAS THE SHAPE IT HAS. A bang-bang rate needs a
    /// band or it chatters, and the band needs two edges or it flaps on the
    /// single one. `SlewEnterTicks` is half a world tick: below that the error
    /// cannot change which pair of snapshots the consumer interpolates between
    /// for as much as half a frame, so correcting it would be chasing sub-frame
    /// phase noise. `SlewExitTicks` is a quarter of that, which makes the
    /// shortest possible correction burst about five frames long — long enough
    /// that the rate cannot toggle frame to frame. On top of the two edges the
    /// state machine NEVER REVERSES IN ONE FRAME: going from slowing to
    /// speeding up (or back) always spends a frame at the neutral rate. That
    /// rule is what makes the anchored clock's signature — a frame that moves
    /// no ticks immediately followed by a frame that moves two — impossible by
    /// construction rather than merely unlikely, because a neutral frame at
    /// `dt == TickDt` moves `RenderTime` by exactly one and so moves
    /// `RenderTick` by exactly one.
    ///
    /// ONE EPOCH IS TRACKED, AND ONLY `ResetForEpoch` CHANGES IT — the same
    /// discipline as `EventDedup.Reset`, for the same reason. The epoch arrives
    /// over the Reliable lifecycle channel (Р60) and the owner passes it in; a
    /// restarted match replays its ticks from zero, so everything the clock
    /// knew must go with it. A snapshot of any other epoch changes nothing at
    /// all: letting one wandering datagram switch the tracked epoch would hand
    /// the clock to a match this client is not in. Before the first
    /// `ResetForEpoch` there is nothing to compare against and every snapshot
    /// is ignored. `ResetForEpoch` is also, by construction, the "snap on
    /// MatchEpoch change" the spec asks for: monotonicity is a within-epoch
    /// promise, and slewing from the old match's tick count down to the new
    /// one would take minutes.
    ///
    /// START-UP: TWO SNAPSHOTS, AND THEY MUST BE DISTINCT. Interpolation needs
    /// a pair of moments, and two copies of one datagram are one moment; a
    /// duplicate therefore does not start the clock. Until it starts, `Advance`
    /// does nothing at all. On the first `Advance` after the start the clock is
    /// PLACED on its target outright — that is a primary placement, not a slew
    /// and not the snap above, and it consumes the frame. At the very start of
    /// a match the target can be floored: with the shipped buffer of 3, a
    /// client armed on ticks 1 and 2 is placed at tick 0 instead of the -1 the
    /// formula names. The slew walks that last fraction of a tick off over the
    /// following frames, which is exactly what it is for.
    ///
    /// HOSTILE AND ABSURD INPUT IS REFUSED, NEVER THROWN (Р82, both halves —
    /// do not throw AND do not hand back rubbish). `tick` is a raw wire value:
    /// one beyond `int.MaxValue` has no representation in the `int` that
    /// `RenderTick` is by contract, and an out-of-range double→int cast in C#
    /// produces an unspecified number rather than an error, so such a tick is
    /// refused outright — it is 828 days of match away and can only be
    /// corruption. `unscaledDeltaTime` that is not a positive finite number
    /// buys a frame of nothing. `SlewFraction` is clamped into a range that
    /// keeps the rate positive, and a `RenderClockSnapTicks` that is not a
    /// positive number of ticks disables the snap outright — because
    /// "renderTime is monotonic" is a promise of this class and not a
    /// consequence of someone having tuned an asset correctly. Both knobs
    /// arrive through `NetTimings`, which is a plain struct any caller can
    /// build by hand or leave at `default`, so neither the asset's `[Range]`
    /// attributes nor `NetInvariants` stand between a caller bug and this code
    /// (fix round 1: the int knob was unguarded while the float one was not).
    ///
    /// NOTHING HERE ALLOCATES. The whole state is a handful of scalars; there
    /// is no buffer to size and the constructor is the implicit one.
    public sealed class RenderClock
    {
        /// Error, in world ticks, at which the slew engages from rest. Half a
        /// tick — see the hysteresis note in the class doc.
        const double SlewEnterTicks = 0.5d;

        /// Error the slew runs down to before disengaging. A quarter of the
        /// entry edge, so the shortest correction burst spans several frames.
        const double SlewExitTicks = 0.125d;

        /// Structural ceiling on `NetTimings.SlewFraction`. The field is
        /// documented 0.05..0.10; this is not a second opinion about tuning but
        /// the bound that keeps `1 - SlewFraction` a positive rate no matter
        /// what reaches it.
        const float MaxSlewFraction = 0.5f;

        /// Highest wire tick the clock can represent, because `RenderTick` is
        /// an `int` by contract. See the Р82 note in the class doc.
        const uint MaxRepresentableTick = int.MaxValue;

        /// Largest float strictly below one (1 - 2^-24). `Phase` is a float
        /// while `RenderTime` is a double, so a fractional part near the top of
        /// its range can round UP to exactly 1f on the narrowing conversion and
        /// break the "[0, 1)" half of the Р38 invariant. This is the value it
        /// is pinned to instead; the resulting error is under 1e-7 of a tick.
        const float MaxPhase = 0.99999994f;

        /// Which way the clock is currently correcting. `Neutral` is not
        /// "no error" — it is "not correcting", which is also where the state
        /// machine parks for one frame when the error changes sign.
        enum SlewState : byte
        {
            Neutral,
            Slower,
            Faster,
        }

        double _renderTime;
        int _renderTick;
        float _phase;

        bool _hasEpoch;
        ushort _epoch;

        /// The first tick seen since the reset, and the yardstick for
        /// "distinct": the second DIFFERENT tick is what starts the clock.
        bool _hasFirstTick;
        uint _firstTick;

        bool _hasNewestTick;
        uint _newestBufferedTick;

        bool _started;

        /// Whether `Advance` has already placed `_renderTime` for this epoch.
        /// Separate from `_started` because the two happen in different calls:
        /// the start is decided by an arriving snapshot, the placement is done
        /// by the frame that follows it, and `OnSnapshot` never writes time.
        bool _placed;

        SlewState _slew;

        /// How many times `Advance` has JUMPED onto its target since the last
        /// `ResetForEpoch`. See `Snaps`.
        int _snaps;

        /// Render time in world ticks — a continuous quantity that advances
        /// with local delta time, not with packet arrivals.
        public double RenderTime => _renderTime;

        /// `floor(RenderTime)`: the older of the two snapshots the consumer
        /// interpolates between.
        public int RenderTick => _renderTick;

        /// `frac(RenderTime)` in [0, 1) — the Р38 blend factor between
        /// `RenderTick` and the tick after it.
        public float Phase => _phase;

        /// Whether the clock has the two distinct snapshots it needs to run.
        /// Until it does, `Advance` is a no-op and the consumer has nothing to
        /// interpolate between.
        public bool Started => _started;

        /// Whether `Advance` has already placed render time for this epoch —
        /// the state `_placed` records, made readable for the same reason
        /// `Started` is (app-88jb Т26 fix-round A, ruling 166; review finding
        /// A-REV-2).
        ///
        /// A CONSUMER THAT ASKS "WHICH TICK IS ON SCREEN" MUST GATE ON THIS
        /// AND NOT ON `Started`. The two are decided in different calls: the
        /// start by an arriving snapshot in `OnSnapshot`, the placement by the
        /// first `Advance` that follows it. In between, `Started` already
        /// reads true while `RenderTick` still reads 0 — a tick that has never
        /// been on any screen. Reading it there is harmless for a consumer
        /// that only interpolates (there is nothing to draw yet either way),
        /// and wrong for one that reports the moment as a measurement.
        ///
        /// It implies `Started`, so a caller that needs both wants only this
        /// one: `Advance` returns before the placement branch while the clock
        /// is not started.
        public bool Placed => _placed;

        /// WHICH WAY THE CLOCK IS CORRECTING RIGHT NOW, as a sign: `+1` while
        /// it is running fast to catch a target ahead of it, `-1` while it is
        /// running slow, `0` while it is not correcting (Stage 2 Task 48 — the
        /// dev overlay's "state of the render clock" line, plan Ф9
        /// :2100-2107).
        ///
        /// A SIGN AND NOT THE STATE OBJECT, so that `SlewState` stays private:
        /// the state machine's third value is "not correcting" and not "no
        /// error" (see the enum), and a sign carries exactly that distinction
        /// with nothing else attached. It also crosses the assembly border the
        /// overlay's own seam is made of primitives for, without a second
        /// enum being declared on the far side of it.
        ///
        /// IT IS A DIAGNOSTIC, NOT A CONTROL. Nothing may branch on it: what
        /// the clock does about its error is decided in `Advance` and in
        /// `NextSlewState`, and a reader that acted on this would be a second
        /// copy of a rule with one home.
        public int SlewSign => _slew == SlewState.Faster ? 1 : _slew == SlewState.Slower ? -1 : 0;

        /// How many times the clock has JUMPED onto its target since the last
        /// `ResetForEpoch` — the OTHER half of "the state of the render clock",
        /// and it has to be a count rather than a state because a snap is an
        /// instant and not a condition: `Advance` jumps and returns, and by the
        /// time anything could read a flag it would be false again.
        ///
        /// NONZERO IS UNHEALTHY, WHICH IS WHY IT IS WORTH A LINE ON THE PANEL.
        /// A snap is a visible discontinuity in the moment being shown — the
        /// picture skips forward — and the slew exists precisely so that
        /// ordinary error never needs one. The first frame of an epoch is NOT
        /// counted here: primary placement returns before this branch, and it
        /// is not a jump in anything the player has already seen.
        public int Snaps => _snaps;

        /// Starts tracking `epoch` and forgets everything else — the buffered
        /// ticks, the start, the placement, the correction in progress and the
        /// time itself. Called by the owner (Task 32) on the Reliable lifecycle
        /// message that names the match's epoch, a restart included (Р60).
        /// This is the spec's "snap on MatchEpoch change": the new match starts
        /// at its own target on its own first frame, because monotonicity is a
        /// within-epoch promise and crawling from the old tick count to the new
        /// one would take minutes.
        public void ResetForEpoch(ushort epoch)
        {
            _epoch = epoch;
            _hasEpoch = true;

            _hasFirstTick = false;
            _firstTick = 0;
            _hasNewestTick = false;
            _newestBufferedTick = 0;

            _started = false;
            _placed = false;
            _slew = SlewState.Neutral;
            _snaps = 0;

            _renderTime = 0d;
            _renderTick = 0;
            _phase = 0f;
        }

        /// Records that a snapshot of `(epoch, tick)` has been seen. Moves no
        /// time whatsoever — see the class doc on why that is the whole point.
        /// Everything unusable is refused in silence (Р82): a foreign epoch,
        /// anything at all before the first `ResetForEpoch`, and a tick past
        /// the range `RenderTick` can represent.
        public void OnSnapshot(uint tick, ushort epoch)
        {
            if (!_hasEpoch || epoch != _epoch) return;
            if (tick > MaxRepresentableTick) return;

            // A MAXIMUM, never an assignment: a reordered frame must not pull
            // the target back to a moment already shown.
            if (!_hasNewestTick)
            {
                _hasNewestTick = true;
                _newestBufferedTick = tick;
            }
            else if (tick > _newestBufferedTick)
            {
                _newestBufferedTick = tick;
            }

            if (!_hasFirstTick)
            {
                _hasFirstTick = true;
                _firstTick = tick;
            }
            else if (tick != _firstTick)
            {
                // The second DISTINCT tick. A duplicated datagram repeats the
                // first one and gives interpolation no pair to work with.
                _started = true;
            }
        }

        /// Advances the clock by one rendered frame. The only writer of render
        /// time in the class: it integrates `unscaledDeltaTime`, corrects
        /// towards the buffered target by changing pace, and jumps only
        /// forward and only past the configured threshold.
        public void Advance(float unscaledDeltaTime, in NetTimings cfg)
        {
            if (!_started) return;
            // A frame length that is not a positive finite number is not a
            // frame. Refuse it rather than propagate a NaN into the clock.
            if (!math.isfinite(unscaledDeltaTime) || unscaledDeltaTime <= 0f) return;

            double target = TargetTicks(in cfg);

            if (!_placed)
            {
                // Primary placement: the clock takes its target outright and
                // that is the whole frame. Not a slew, and not the snap below.
                _placed = true;
                SetTime(target);
                return;
            }

            double dtTicks = unscaledDeltaTime / (double)SimulationWorld.TickDt;

            // The error at the END of the frame: where a free-running step
            // would land, measured against the target as it stands now.
            double drift = target - (_renderTime + dtTicks);

            if (cfg.RenderClockSnapTicks > 0 && drift > cfg.RenderClockSnapTicks)
            {
                // Forward only, and only past the threshold. BOTH halves of the
                // condition are load-bearing (fix round 1):
                //   * the threshold has to be a POSITIVE number of ticks before
                //     it names a gap at all. A negative one — a caller bug, not
                //     something the asset's own [Range(1, 60)] can produce —
                //     would be exceeded by a NEGATIVE drift, i.e. by a target
                //     sitting BEHIND the clock, and this branch would then
                //     rewind time in the name of the forward-only snap. A
                //     threshold of zero would be worse in the other direction:
                //     every sub-tick error becomes a per-frame teleport and the
                //     slew is silently dead. So a non-positive threshold
                //     DISABLES the jump and leaves the whole correction to the
                //     slew, which cannot break monotonicity — the same shape as
                //     a non-positive `SlewFraction` disabling the slew in
                //     `SlewFractionOf`. A hardening path degrades a mechanism;
                //     it never invents a bound the caller did not ask for.
                //   * `drift > threshold` is strict: a gap of exactly
                //     RenderClockSnapTicks is still the slew's to walk out.
                _slew = SlewState.Neutral;
                _snaps++; // Task 48: counted here and only here — see `Snaps`.
                SetTime(target);
                return;
            }

            _slew = NextSlewState(_slew, drift);

            double rate = 1d;
            if (_slew != SlewState.Neutral)
            {
                double fraction = SlewFractionOf(in cfg);
                rate = _slew == SlewState.Faster ? 1d + fraction : 1d - fraction;
            }

            SetTime(_renderTime + dtTicks * rate);
        }

        /// The moment the clock is aiming at: `InterpBufferTicks` behind the
        /// newest tick seen for this epoch, floored at zero because the opening
        /// ticks of a match name a moment before it began.
        double TargetTicks(in NetTimings cfg)
        {
            if (!_hasNewestTick) return 0d;
            double target = (double)_newestBufferedTick - cfg.InterpBufferTicks;
            return target > 0d ? target : 0d;
        }

        /// The two-edged band, plus the rule that the correction never reverses
        /// inside a single frame. See the hysteresis note in the class doc for
        /// what each edge is worth and why the neutral frame in the middle is
        /// what keeps a freeze-then-leap pair impossible.
        static SlewState NextSlewState(SlewState current, double drift)
        {
            if (current == SlewState.Neutral)
            {
                if (drift > SlewEnterTicks) return SlewState.Faster;
                if (drift < -SlewEnterTicks) return SlewState.Slower;
                return SlewState.Neutral;
            }

            if (math.abs(drift) <= SlewExitTicks) return SlewState.Neutral;

            // The error overshot to the other side while a correction was
            // running: stand down for this frame instead of reversing the rate
            // between two consecutive frames.
            if (current == SlewState.Faster && drift < 0d) return SlewState.Neutral;
            if (current == SlewState.Slower && drift > 0d) return SlewState.Neutral;

            return current;
        }

        /// `SlewFraction` with the structural clamp of the class doc: NaN and
        /// anything at or below zero mean "do not correct", and the ceiling
        /// keeps `1 - fraction` a positive rate.
        static double SlewFractionOf(in NetTimings cfg)
        {
            float fraction = cfg.SlewFraction;
            // Written as a negated `>` so a NaN takes this branch too.
            if (!(fraction > 0f)) return 0d;
            return fraction < MaxSlewFraction ? fraction : MaxSlewFraction;
        }

        /// The single place render time is written, and therefore the single
        /// place the Р38 decomposition is maintained. The bounds are the ones
        /// the contract needs rather than opinions about gameplay: time never
        /// goes below zero, and never past what `RenderTick`'s `int` can hold.
        void SetTime(double renderTime)
        {
            // Negated comparison so a NaN — which no path above can produce,
            // but which one future path might — lands on the floor rather than
            // poisoning every subsequent frame.
            if (!(renderTime > 0d)) renderTime = 0d;
            else if (renderTime > MaxRepresentableTick) renderTime = MaxRepresentableTick;

            _renderTime = renderTime;

            double whole = math.floor(renderTime);
            _renderTick = (int)whole;

            float phase = (float)(renderTime - whole);
            _phase = phase < 1f ? phase : MaxPhase;
        }
    }
}
