using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ring.Presentation
{
    /// Frame-level sampler: held values are read fresh every frame, while the Dash
    /// edge is captured via a subscribed `performed` callback so a tap shorter than a
    /// frame is never missed. `SimulationRunner.Update` samples once per frame and
    /// clears latches only after any pending ticks consumed them (spec §3.2/§3.8).
    public sealed class InputSampler
    {
        readonly InputAction _move, _aim, _fire, _dash;
        readonly AimProvider _aimProvider;
        readonly System.Action<InputAction.CallbackContext> _onDashPerformed;
        bool _dashLatch;

        public InputSampler(InputActionAsset asset, AimProvider aimProvider)
        {
            _move = asset.FindAction("Gameplay/Move", true);
            _aim = asset.FindAction("Gameplay/Aim", true);
            _fire = asset.FindAction("Gameplay/Fire", true);
            _dash = asset.FindAction("Gameplay/Dash", true);
            _aimProvider = aimProvider;

            // Stored so Enable()/Disable() can (re)subscribe the exact same delegate
            // instance.
            _onDashPerformed = _ => _dashLatch = true;
            _dash.performed += _onDashPerformed;
        }

        public void Enable()
        {
            _move.Enable();
            _aim.Enable();
            _fire.Enable();
            _dash.Enable();
            // F-2 fix: Disable() below unsubscribes _onDashPerformed but Enable()
            // never resubscribed it — after one disable->enable cycle the dash edge
            // latch was dead for the rest of the session (SampleFrame's DashRequested
            // stayed permanently false; held-value reads like MoveDir kept working
            // since those don't depend on a subscription). `-=` first guards against a
            // double subscription if Enable() is ever called twice without an
            // intervening Disable().
            _dash.performed -= _onDashPerformed;
            _dash.performed += _onDashPerformed;
        }

        public void Disable()
        {
            _move.Disable();
            _aim.Disable();
            _fire.Disable();
            _dash.Disable();
            _dash.performed -= _onDashPerformed;
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
                // No extra `|| _dash.WasPressedThisFrame()` here: the `performed`
                // subscription above already latches any within-frame press.
                DashRequested = _dashLatch
            };
        }

        public void ClearLatches() => _dashLatch = false;
    }
}
