using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Sole mapping between sim-space `float2 (x, y)` and Unity world-space
    /// `Vector3 (x, 0, z)` (spec §3.7/§3.11). Every other Presentation type that
    /// needs to convert between the two routes through here — no inline
    /// `new Vector3(p.x, 0f, p.y)` / `new float2(w.x, w.z)` elsewhere.
    public static class SimSpace
    {
        public static Vector3 ToWorld(float2 simPos) => new Vector3(simPos.x, 0f, simPos.y);

        public static float2 ToSim(Vector3 world) => new float2(world.x, world.z);
    }
}
