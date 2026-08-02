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
    /// `GameFeelDirector.ShakeOffset` (Task 26, trauma-driven camera shake) is added
    /// on top of the damped position every `LateUpdate`, read directly off a
    /// bootstrap-wired reference — never fed back into the `SmoothDamp` state, so
    /// shake reads as instantaneous jitter instead of being smoothed away, and never
    /// gated by hitstop (`GameFeelDirector` decays/animates it on `Time.unscaledTime`
    /// regardless of any freeze — see that class's own doc).
    public sealed class CameraRig : MonoBehaviour
    {
        [SerializeField] CameraConfig _config;
        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;
        [SerializeField] GameFeelDirector _gameFeelDirector;

        Vector3 _dampedPos;
        Vector3 _dampVelocity;
        bool _initialized;

        void LateUpdate()
        {
            // Task 25 (Приложение П-7): reads ONLY `SimulationRunner.RenderPrev`/
            // `RenderCurr`/`RenderAlpha` — a `FullFrame` hitstop freeze holds the
            // camera still along with everything else without this class knowing
            // hitstop exists.
            Vector3 prevW = SimSpace.ToWorld(_runner.RenderPrev.Player.Pos);
            Vector3 currW = SimSpace.ToWorld(_runner.RenderCurr.Player.Pos);
            Vector3 playerW = Vector3.Lerp(prevW, currW, _runner.RenderAlpha);
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

            transform.position = _dampedPos + _gameFeelDirector.ShakeOffset;
            transform.rotation = Quaternion.Euler(_config.PitchDeg, 0f, 0f);
        }
    }
}
