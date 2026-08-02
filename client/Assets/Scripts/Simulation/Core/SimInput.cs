using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Raw per-tick input from a client, before sanitization inside the world.
    public struct SimInput
    {
        public float2 MoveDir, AimPoint;
        public bool FireHeld, DashRequested;
    }

    /// Distributes one frame sample over N sub-ticks: held values copy to every
    /// tick, the dash edge-latch fires on tick 0 only (spec §3.2).
    public static class SimInputFrame
    {
        public static SimInput ForTick(in SimInput frame, int tickIndex)
        {
            SimInput si = frame;
            si.DashRequested = frame.DashRequested && tickIndex == 0;
            return si;
        }
    }
}
