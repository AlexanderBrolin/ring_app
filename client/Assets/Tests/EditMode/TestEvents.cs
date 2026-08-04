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

        /// First buffered event of `kind` (Task 6) — the "what did that blow
        /// report" lookup zone/amount fixtures need, next to CountOf rather than
        /// copied into every test class that inspects a single event.
        public static bool TryFirstOf(SimulationWorld w, SimEventKind kind, out SimEvent found)
        {
            for (int i = 0; i < w.EventCount; i++)
            {
                if (w.GetEvent(i).Kind != kind) continue;
                found = w.GetEvent(i);
                return true;
            }
            found = default;
            return false;
        }
    }
}
