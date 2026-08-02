using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ring.Presentation
{
    /// Maps the mouse cursor to a point on the arena's y=0 ground plane (spec §3.8).
    /// This file and any view code that turns sim `(x, y)` back into a world position
    /// are the only places allowed to know the sim-to-world axis mapping:
    /// world = (sim.x, 0, sim.y).
    public sealed class AimProvider : MonoBehaviour
    {
        [SerializeField] Camera _camera;

        float2 _lastValid;

        /// Current aim point in sim space. Falls back to the last valid sample whenever
        /// the mouse device is missing (focus loss) or the camera ray is (near-)parallel
        /// to the plane / points away from it — never NaN, never stale-uninitialized
        /// beyond the very first frame (spec §3.8 invariant).
        public float2 CurrentAimSimPos
        {
            get
            {
                Mouse mouse = Mouse.current;
                if (mouse == null || _camera == null) return _lastValid;

                Vector2 screenPos = mouse.position.ReadValue();
                Ray ray = _camera.ScreenPointToRay(screenPos);
                if (math.abs(ray.direction.y) <= 1e-4f) return _lastValid;

                float t = -ray.origin.y / ray.direction.y;
                if (t <= 0f) return _lastValid;

                Vector3 world = ray.origin + ray.direction * t;
                _lastValid = new float2(world.x, world.z);
                return _lastValid;
            }
        }
    }
}
