using Ring.Data;
using UnityEngine;

namespace Ring.Presentation
{
    /// Top-down ¾ camera rig (spec §3.11). Follows the interpolated player position
    /// with a fixed pitch, biased toward the current aim point by `CameraConfig.
    /// LookAhead`, and smoothed with `Vector3.SmoothDamp`. Every tunable number comes
    /// from `CameraConfig` (SO) — none of them are literals here.
    ///
    /// This component sits on the rig's own transform; `Main Camera` is its child at
    /// local zero, so the rig carries all position/rotation and the camera itself
    /// never needs to know about look-ahead or damping.
    ///
    /// `ExternalOffset` is a reserved additive hook for camera shake (Task 26 /
    /// GameFeelDirector). It is applied on top of the damped position — not fed back
    /// into the SmoothDamp state — so shake reads as instantaneous instead of being
    /// smoothed away. Defaults to zero and nothing here writes to it.
    public sealed class CameraRig : MonoBehaviour
    {
        [SerializeField] CameraConfig _config;
        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;

        Vector3 _dampedPos;
        Vector3 _dampVelocity;
        bool _initialized;

        public Vector3 ExternalOffset;

        void LateUpdate()
        {
            Vector3 prevW = SimSpace.ToWorld(_runner.Prev.Player.Pos);
            Vector3 currW = SimSpace.ToWorld(_runner.Curr.Player.Pos);
            Vector3 playerW = Vector3.Lerp(prevW, currW, _runner.Alpha);
            Vector3 aimW = SimSpace.ToWorld(_aimProvider.CurrentAimSimPos);

            Vector3 focus = playerW + (aimW - playerW) * _config.LookAhead;
            Vector3 offset = Quaternion.Euler(_config.PitchDeg, 0f, 0f) * Vector3.back * _config.Distance;
            Vector3 desired = focus + offset;

            if (_initialized)
            {
                _dampedPos = Vector3.SmoothDamp(_dampedPos, desired, ref _dampVelocity, _config.Damp);
            }
            else
            {
                // First frame: snap straight to the desired position instead of
                // damping from a Vector3.zero default, which would visibly swoop the
                // rig in from the origin on scene start.
                _dampedPos = desired;
                _initialized = true;
            }

            transform.position = _dampedPos + ExternalOffset;
            transform.rotation = Quaternion.Euler(_config.PitchDeg, 0f, 0f);
        }
    }
}
