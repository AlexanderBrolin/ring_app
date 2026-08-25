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
    /// paused by anything (`GameFeelDirector` decays/animates it on
    /// `Time.unscaledTime` unconditionally — see that class's own doc).
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
            // Task 25 (Приложение П-7): the position comes from the runner's own
            // render pair, so a paused frame holds the camera still along with
            // everything else without this class needing to know why.
            //
            // Stage 2 Task 47b: the seat followed is the OBSERVED one, not this
            // client's own. They are the same seat for the whole of solo and for
            // as long as this player is standing (`SimulationRunner.
            // ObservedIndex`); once that player is down they part company, and
            // the old read — `RenderCurr.Player.Pos`, i.e.
            // `Players[LocalPlayerIndex]` — was what sent the camera to the
            // arena origin on a networked client, because nothing wrote that
            // seat any more and an unwritten seat reads `default(PlayerState)`.
            Vector3 playerW = _runner.RenderObservedWorldPos;

            // THE LOOK-AHEAD IS THIS CLIENT'S OWN CURSOR, so it is not applied
            // to somebody else's body: `AimProvider.CurrentAimSimPos` is where
            // THIS player's mouse points, and biasing a spectated camera toward
            // it would drag a stranger's frame around by a cursor that stopped
            // meaning anything the moment its owner died. A watched player's own
            // aim IS on the wire while they hold it (`PlayerFlags.
            // ToSyntheticState`'s synthetic aim point), so a look-ahead for a
            // spectator could be built later — from that, measured, not from
            // this.
            Vector3 focus = playerW;
            if (!_runner.IsSpectating)
            {
                Vector3 aimW = SimSpace.ToWorld(_aimProvider.CurrentAimSimPos);
                focus = playerW + (aimW - playerW) * _config.LookAhead;
            }
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
