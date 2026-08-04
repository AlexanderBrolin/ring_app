using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ring.Presentation
{
    /// Frame-level sampler: held values are read fresh every frame, while the Dash
    /// and Slide edges are captured via subscribed `performed` callbacks so a tap
    /// shorter than a frame is never missed. `SimulationRunner.Update` samples once
    /// per frame and clears latches only after any pending ticks consumed them
    /// (spec §3.2/§3.8).
    public sealed class InputSampler
    {
        readonly InputAction _move, _aim, _fire, _dash, _slide, _aimHold;
        readonly AimProvider _aimProvider;
        readonly System.Action<InputAction.CallbackContext> _onDashPerformed;
        readonly System.Action<InputAction.CallbackContext> _onSlidePerformed;
        bool _dashLatch, _slideLatch;

        public InputSampler(InputActionAsset asset, AimProvider aimProvider)
        {
            _move = asset.FindAction("Gameplay/Move", true);
            _aim = asset.FindAction("Gameplay/Aim", true);
            _fire = asset.FindAction("Gameplay/Fire", true);
            _dash = asset.FindAction("Gameplay/Dash", true);
            _slide = asset.FindAction("Gameplay/Slide", true);
            _aimHold = asset.FindAction("Gameplay/AimHold", true);
            _aimProvider = aimProvider;

            // Stored so Enable()/Disable() can (re)subscribe the exact same delegate
            // instance.
            _onDashPerformed = _ => _dashLatch = true;
            _onSlidePerformed = _ => _slideLatch = true;
            _dash.performed += _onDashPerformed;
            _slide.performed += _onSlidePerformed;
        }

        public void Enable()
        {
            _move.Enable();
            _aim.Enable();
            _fire.Enable();
            _dash.Enable();
            _slide.Enable();
            _aimHold.Enable();
            // F-2 fix: Disable() below unsubscribes the edge callbacks but Enable()
            // never resubscribed them — after one disable->enable cycle the dash (and
            // now slide) edge latch was dead for the rest of the session (SampleFrame's
            // DashRequested/SlideRequested stayed permanently false; held-value reads
            // like MoveDir kept working since those don't depend on a subscription).
            // `-=` first guards against a double subscription if Enable() is ever
            // called twice without an intervening Disable().
            _dash.performed -= _onDashPerformed;
            _dash.performed += _onDashPerformed;
            _slide.performed -= _onSlidePerformed;
            _slide.performed += _onSlidePerformed;
        }

        public void Disable()
        {
            _move.Disable();
            _aim.Disable();
            _fire.Disable();
            _dash.Disable();
            _slide.Disable();
            _aimHold.Disable();
            _dash.performed -= _onDashPerformed;
            _slide.performed -= _onSlidePerformed;
        }

        public SimInput SampleFrame()
        {
            Vector2 move = _move.ReadValue<Vector2>();
            return new SimInput
            {
                MoveDir = new float2(move.x, move.y),
                AimPoint = _aimProvider.CurrentAimSimPos,
                // Tap shorter than a frame: WasPressedThisFrame also catches a
                // press-then-release that both landed between two samples.
                FireHeld = _fire.IsPressed() || _fire.WasPressedThisFrame(),
                // No extra `|| _dash.WasPressedThisFrame()`/`|| _slide.
                // WasPressedThisFrame()` here: the `performed` subscriptions above
                // already latch any within-frame press.
                DashRequested = _dashLatch,
                SlideRequested = _slideLatch,
                // Level, not edge (C16): a WasPressedThisFrame OR here would latch a
                // phantom extra aim frame the way the dash/slide edges intentionally
                // do — AimHeld must track exactly whether the button is down right now.
                AimHeld = _aimHold.IsPressed(),
                // Task 19: height now comes from AimProvider — meaningful only
                // while AimHeld (WeaponSystem's hip-fire branch never reads it,
                // AimProvider's own class doc); the sampler still has no config
                // access of its own (QD11) and never needs one, since the height
                // arrives fully formed from the provider's cached proxy cast.
                AimHeight = _aimProvider.CurrentAimHeight
            };
        }

        public void ClearLatches()
        {
            _dashLatch = false;
            _slideLatch = false;
        }
    }
}
