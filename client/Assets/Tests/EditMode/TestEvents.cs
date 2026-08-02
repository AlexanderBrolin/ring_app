using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Small event-inspection helper for tests — counts currently-buffered
    /// events (since the world's last ClearEvents()) of a given kind. Task 29:
    /// only new tests consume this, existing call-sites are untouched.
    public static class TestEvents
    {
        public static int CountOf(SimulationWorld w, SimEventKind kind)
        {
            int count = 0;
            for (int i = 0; i < w.EventCount; i++)
                if (w.GetEvent(i).Kind == kind) count++;
            return count;
        }
    }
}
