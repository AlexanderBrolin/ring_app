namespace Ring.Simulation.Core
{
    /// Gaffer-style fixed timestep accumulator. Will also serve the Stage 2 headless server.
    public sealed class FixedStepAccumulator
    {
        public const float MaxFrameTime = 0.25f;

        float _acc;

        /// Whether the NEXT `Advance` reports its overrun. See
        /// `IgnoreNextFrameGap` for what raises it and why.
        bool _ignoreNextFrameGap;

        public float DroppedTime { get; private set; }
        public float Alpha => _acc / SimulationWorld.TickDt;

        public int Advance(float dt)
        {
            // THE CLAMP AND THE ACCOUNTING ARE TWO THINGS (Stage 2 Task 48,
            // bd `app-c3m`, the owner's decision of 2026-08-11, variant a).
            // They were one line until this task, and only the ACCOUNTING half
            // moved: `dt` is still capped at `MaxFrameTime` on exactly the same
            // frames, by exactly the same comparison, to exactly the same
            // value. That is not a stylistic point — the cap decides how many
            // ticks a frame produces, the tick count decides every state the
            // world reaches, and `StateHash` is a function of those states. A
            // flag that reached the cap would move the golden hashes;
            // `AccumulatorTests.IgnoreNextFrameGap_ChangesNoTickCountAndNoPhase`
            // is what pins that this one does not.
            bool ignoreGap = _ignoreNextFrameGap;
            _ignoreNextFrameGap = false;
            if (dt > MaxFrameTime)
            {
                if (!ignoreGap) DroppedTime += dt - MaxFrameTime;
                dt = MaxFrameTime;
            }
            if (dt < 0f) dt = 0f;
            _acc += dt;
            int ticks = (int)(_acc / SimulationWorld.TickDt);
            _acc -= ticks * SimulationWorld.TickDt;
            if (_acc < 0f) _acc = 0f; // float rounding on exact-boundary frames
            return ticks;
        }

        /// The next frame's overrun is NOT this game's hitch — leave it out of
        /// the `DroppedTime` diagnostic (Stage 2 Task 48, bd `app-c3m`).
        ///
        /// WHAT IT IS FOR. `DroppedTime` exists to say "the simulation could
        /// not keep up and real time was thrown away", and the dev overlay
        /// paints it red above zero. Two frames per session are guaranteed to
        /// trip it while the simulation is keeping up perfectly: the first
        /// frame after a match restart, which carries scene load and shader
        /// compilation, and the first frame after the window gets its focus
        /// back — Unity runs no frames at all while the window is out of focus
        /// (`ProjectSettings` carries `runInBackground: 0`), so the entire
        /// idle stretch arrives as ONE delta. The owner measured 79 seconds of
        /// it after minimizing. A counter that is red from the first minute of
        /// every session says nothing about any minute after it, which is the
        /// defect this method closes.
        ///
        /// THE DECISION LIVES HERE, THE CALLER ONLY REPORTS A FACT (lesson
        /// 130). The caller knows one thing — "the engine itself was not
        /// running frames until now" — and says exactly that; what that means
        /// for a diagnostic is this class's business. `SimulationRunner` is the
        /// caller, because it is the only home of the simulation's Unity
        /// lifecycle: it performs restarts itself, and `OnApplicationFocus`/
        /// `OnApplicationPause` are raised on it. Both callbacks, never one —
        /// losing focus and being suspended are different events on different
        /// platforms, and the pair is idempotent here, since raising this twice
        /// still excuses exactly one frame.
        ///
        /// EXACTLY ONE FRAME, AND ANY FRAME. `Advance` clears the flag before
        /// it looks at `dt`, so a short frame spends the excuse just as a long
        /// one does. The alternative — hold the excuse until a long frame
        /// actually turns up — sounds more forgiving and is strictly worse: the
        /// engine would then carry a licence to under-report the FIRST REAL
        /// hitch of the session, whenever it happened to come.
        ///
        /// RAISE IT AFTER THE FRAME'S LAST `Advance`, NEVER BEFORE IT (Stage 2
        /// Task 48 fix-round 1, F-7). The excuse is spent by the very next
        /// call, so a caller that raises it and then advances the same frame
        /// has handed it to the frame it meant to charge, and the long frame
        /// that follows — the one the restart or the resume actually caused —
        /// is charged in full instead. It is a real ordering and not a
        /// theoretical one: `SimulationRunner`'s hot-tweak recovery restarts
        /// from the middle of its own `Update`, which is why that method
        /// returns without advancing on the frame a restart happened in.
        ///
        /// PAUSE NEEDS NOTHING FROM THIS. A paused facade returns from `Update`
        /// before it reaches `Advance` at all, so a pause of any length adds
        /// nothing to `DroppedTime` and there is no gap to excuse on the way
        /// out (`SimulationRunner.Update`'s `if (_paused) return;`, and
        /// `ResetAccumulatorOnly` below for what pause DOES cost).
        public void IgnoreNextFrameGap() { _ignoreNextFrameGap = true; }

        /// A fresh match starts its own diagnostic — including its own excuses:
        /// a pending `IgnoreNextFrameGap` goes with everything else, and the
        /// caller that restarts raises it again afterwards if the restart is
        /// itself one of the two excused cases (`LocalSimBackend.Restart`
        /// does exactly that, in that order).
        public void Reset() { _acc = 0f; DroppedTime = 0f; _ignoreNextFrameGap = false; }

        /// Zeroes the phase accumulator only, leaving `DroppedTime` untouched
        /// (Presentation's pause gate, Task 24 review round): entering pause
        /// should discard any fractional-tick backlog of real time so resuming
        /// doesn't burst-catch-up, but it must not erase the dropped-time
        /// diagnostic DevOverlay surfaces (spec §3.7 — no silent loss). `Reset()`
        /// above stays the one used for a full match restart, where zeroing
        /// `DroppedTime` too is correct — a fresh run starts its own diagnostic
        /// count from zero.
        public void ResetAccumulatorOnly() { _acc = 0f; }
    }
}
