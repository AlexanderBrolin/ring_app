namespace Ring.Networking.Voice
{
    /// Stage 2 Task 55: "is the microphone open right now", as a pure function
    /// of the key state and elapsed time.
    ///
    /// WHY A GATE IS PART OF THE SPIKE AND NOT A LATER POLISH. MetaVoiceChat
    /// ships no voice-activity detection at all: `VcMic.CoRecord` raises a
    /// frame every 20 ms and never raises a null one, and `MetaVc.SendFrame`
    /// reads "is speaking" as "samples != null". Left alone, a joined client
    /// therefore encodes and transmits SILENCE fifty times a second for the
    /// whole match — which is exactly the traffic the Stage 2 DoD budget of
    /// 40 KB/s per client has to survive. Push-to-talk is the cheapest honest
    /// answer: nothing goes on the wire unless somebody is holding the key.
    ///
    /// THE TAIL IS THE WHOLE DESIGN. A gate that closed on the exact frame the
    /// key came up would clip the end of every word — the release is a human
    /// motion that lands mid-syllable, and 20 ms frames make the loss audible.
    /// Holding the gate open for a short tail after release costs a handful of
    /// frames of silence per transmission and keeps speech intact. The upstream
    /// example filter calls the same idea `debounceSeconds` and defaults it to
    /// 0.2 s.
    ///
    /// PURE ON PURPOSE. No `UnityEngine`, no `Time.deltaTime`, no key polling:
    /// the caller passes the key state and the frame's delta, so the rule can
    /// be tested without a scene, a keyboard or a microphone — the same split
    /// `PlayerPredictionCore` and `RenderClock` already use.
    public struct PushToTalkGate
    {
        readonly float _releaseTailSeconds;
        float _remainingTailSeconds;

        public PushToTalkGate(float releaseTailSeconds)
        {
            _releaseTailSeconds = releaseTailSeconds;
            _remainingTailSeconds = 0f;
        }

        /// Seconds the gate would still stay open if the key were never pressed
        /// again. Exposed for the dev overlay and for tests to read the tail
        /// without inferring it from `ShouldTransmit` alone.
        public float RemainingTailSeconds => _remainingTailSeconds;

        /// Anything at or below this much tail left counts as no tail at all.
        ///
        /// MEASURED, NOT DEFENSIVE-BY-HABIT. The remainder is walked down by
        /// subtracting one frame's delta at a time, and a tail spent in whole
        /// frames does not land on zero: the shipped 0.2 s tail minus ten
        /// 20 ms frames leaves **+2.98e-08** in `float`, and that dust is
        /// enough to hold the gate open for one extra frame every single time
        /// anybody stops talking. 58 of the tail/frame-rate combinations in the
        /// plausible range (50–500 ms tails at 10/16.7/20/40 ms frames) land in
        /// the same trap.
        const float TailDustSeconds = 1e-6f;

        /// Advances the gate by one frame and answers whether this frame's
        /// audio may go on the wire.
        public bool Tick(bool isKeyHeld, float deltaSeconds)
        {
            if (isKeyHeld)
            {
                // Refilled while held, not merely started on the press: the
                // tail has to be whole at the moment of release, whenever that
                // turns out to be.
                _remainingTailSeconds = _releaseTailSeconds;
                return true;
            }

            if (_remainingTailSeconds <= TailDustSeconds)
            {
                _remainingTailSeconds = 0f;
                return false;
            }

            _remainingTailSeconds -= deltaSeconds;

            // Only the sign is clamped here, and the dust above is deliberately
            // NOT clamped a second time: two guards that each catch the same
            // case are two rules in one counter, and neither can then be shown
            // to be load-bearing — a mutation of either survives because the
            // other covers for it. The dust is decided in one place, above.
            if (_remainingTailSeconds < 0f)
                _remainingTailSeconds = 0f;

            // This frame is still inside the tail — the key came up during it,
            // and the syllable it carries is the one the tail exists for.
            return true;
        }
    }
}
