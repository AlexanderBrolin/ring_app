using Ring.Simulation.Core;

namespace Ring.Presentation
{
    /// Stub until Task 11 (spec §3.2/П-5): plain class, parameterless constructor so
    /// `SimulationRunner.Awake` can construct it without any Input System wiring yet.
    /// Task 11 swaps the constructor for `(InputActionAsset, AimProvider)` and gives
    /// `SampleFrame`/`ClearLatches` real bodies; `Awake` is edited to match.
    public class InputSampler
    {
        public SimInput SampleFrame() => default;

        public void ClearLatches() { }
    }
}
