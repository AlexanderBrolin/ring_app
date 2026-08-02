using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Raw per-tick input from a client, before sanitization inside the world.
    public struct SimInput
    {
        public float2 MoveDir, AimPoint;
        public bool FireHeld, DashRequested;
    }
}
