using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Raw per-tick input from a client, before sanitization inside the world.
    public struct SimInput
    {
        public float2 MoveDir, AimPoint;
        public bool FireHeld, DashRequested;

        /// Aim-down-sights height controls (Task 8 Interfaces): AimHeld is a
        /// level (true while the player holds the aim button), AimHeight is the
        /// requested vertical aim offset it carries while held. The consumer
        /// that reads these to drive the raycast aim system arrives in Task 15
        /// — here they only travel through SimInputFrame.ForTick and Sanitize.
        public float AimHeight;
        public bool AimHeld, SlideRequested;
    }

    /// Distributes one frame sample over N sub-ticks: held values copy to every
    /// tick, the dash and slide edge-latches fire on tick 0 only (spec §3.2).
    public static class SimInputFrame
    {
        public static SimInput ForTick(in SimInput frame, int tickIndex)
        {
            SimInput si = frame;
            si.DashRequested = frame.DashRequested && tickIndex == 0;
            si.SlideRequested = frame.SlideRequested && tickIndex == 0;
            return si;
        }
    }
}
