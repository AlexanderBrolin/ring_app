namespace Ring.Simulation.Core
{
    /// Gaffer-style fixed timestep accumulator. Will also serve the Stage 2 headless server.
    public sealed class FixedStepAccumulator
    {
        public const float MaxFrameTime = 0.25f;

        float _acc;

        public float DroppedTime { get; private set; }
        public float Alpha => _acc / SimulationWorld.TickDt;

        public int Advance(float dt)
        {
            if (dt > MaxFrameTime) { DroppedTime += dt - MaxFrameTime; dt = MaxFrameTime; }
            if (dt < 0f) dt = 0f;
            _acc += dt;
            int ticks = (int)(_acc / SimulationWorld.TickDt);
            _acc -= ticks * SimulationWorld.TickDt;
            if (_acc < 0f) _acc = 0f; // float rounding on exact-boundary frames
            return ticks;
        }

        public void Reset() { _acc = 0f; DroppedTime = 0f; }

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
