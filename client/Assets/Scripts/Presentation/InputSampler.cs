using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ring.Presentation
{
    /// Frame-level sampler: held values are read fresh every frame, while the Dash
    /// and Slide edges are captured via subscribed `performed` callbacks so a tap
    /// shorter than a frame is never missed. `SimulationRunner` samples once per
    /// render frame — from its own `Update` in solo, and from FishNet's pre-tick
    /// on a networked client, whichever asks first (Stage 2 app-b3z:
    /// `SimulationRunner.SampleFrameInputOnce` is the sole caller of `SampleFrame`
    /// below either way) — and clears latches only after any pending ticks
    /// consumed them (spec §3.2/§3.8).
    public sealed class InputSampler
    {
        readonly InputAction _move, _aim, _fire, _dash, _slide, _aimHold, _inventory;
        readonly AimProvider _aimProvider;
        readonly System.Action<InputAction.CallbackContext> _onDashPerformed;
        readonly System.Action<InputAction.CallbackContext> _onSlidePerformed;
        readonly System.Action<InputAction.CallbackContext> _onInventoryPerformed;
        bool _dashLatch, _slideLatch;

        /// Whether the loot window is open, held HERE because the key is an
        /// EDGE and the flag is a LEVEL (Stage 3 Т32б).
        ///
        /// `SimInput.InventoryOpen` has existed since Т20 and is carried in
        /// full — wire bit, movement slowdown, the fifth term of
        /// `WeaponSystem.CanFire`, and a server-side sanitizer that forces it
        /// back down for a dead, extracted, dashing or sliding player. What
        /// was missing was the client half, and the whole of it is this: Tab
        /// toggles, the flag reports.
        ///
        /// THE STATE CANNOT LIVE IN THE ACTION. `IsPressed()` would make the
        /// window a HOLD — open only while the key is down — which is neither
        /// what the spec asks for nor usable while both hands are on movement.
        /// So one bool, and the sampler owns it because the sampler is what
        /// `SimInput` is built from.
        bool _inventoryOpen;

        public InputSampler(InputActionAsset asset, AimProvider aimProvider)
        {
            _move = asset.FindAction("Gameplay/Move", true);
            _aim = asset.FindAction("Gameplay/Aim", true);
            _fire = asset.FindAction("Gameplay/Fire", true);
            _dash = asset.FindAction("Gameplay/Dash", true);
            _slide = asset.FindAction("Gameplay/Slide", true);
            _aimHold = asset.FindAction("Gameplay/AimHold", true);
            _inventory = asset.FindAction("Gameplay/Inventory", true);
            _aimProvider = aimProvider;

            // Stored so Enable()/Disable() can (re)subscribe the exact same delegate
            // instance.
            _onDashPerformed = _ => _dashLatch = true;
            _onSlidePerformed = _ => _slideLatch = true;
            _onInventoryPerformed = _ => _inventoryOpen = !_inventoryOpen;
            _dash.performed += _onDashPerformed;
            _slide.performed += _onSlidePerformed;
            _inventory.performed += _onInventoryPerformed;
        }

        public void Enable()
        {
            _move.Enable();
            _aim.Enable();
            _fire.Enable();
            _dash.Enable();
            _slide.Enable();
            _aimHold.Enable();
            _inventory.Enable();
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
            _inventory.performed -= _onInventoryPerformed;
            _inventory.performed += _onInventoryPerformed;
        }

        public void Disable()
        {
            _move.Disable();
            _aim.Disable();
            _fire.Disable();
            _dash.Disable();
            _slide.Disable();
            _aimHold.Disable();
            _inventory.Disable();
            _dash.performed -= _onDashPerformed;
            _slide.performed -= _onSlidePerformed;
            _inventory.performed -= _onInventoryPerformed;
            // A DISABLED SAMPLER REPORTS A CLOSED WINDOW. Leaving the flag up
            // across a disable would hand the next `SampleFrame` a window
            // nobody opened — and that flag slows the step and forbids the
            // shot, so the player would spend the first frames after a pause
            // walking slowly and unable to fire.
            _inventoryOpen = false;
        }

        /// Shuts the window from OUTSIDE the sampler, for the closing
        /// conditions this class cannot see (spec §3.11: Esc, and walking out
        /// of `LootRadius`).
        ///
        /// IT IS A SEAM RATHER THAN A SETTER, and the asymmetry is the point:
        /// nothing outside may OPEN the window, because opening is the
        /// player's own act and there is exactly one way to perform it. The
        /// sampler still has no config access of its own (QD11) and still needs
        /// none — "how far is the box" is a question the window controller
        /// already asks the backend, and the answer arrives here as a verb.
        public void CloseInventory() => _inventoryOpen = false;

        /// Whether the window is open right now — what the window controller
        /// draws and what `SampleFrame` reports.
        public bool InventoryOpen => _inventoryOpen;

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
                AimHeight = _aimProvider.CurrentAimHeight,
                // Level, like AimHeld above and unlike the two edge latches:
                // `SimInputFrame.ForTick` copies it unchanged to every sub-tick
                // because "the window is open" is a state the player holds.
                InventoryOpen = _inventoryOpen,
            };
        }

        public void ClearLatches()
        {
            // A DASH OR SLIDE SHUTS THE WINDOW, and it is done HERE because
            // this is the one place that sees the edge before it is spent. The
            // server closes it too — `SimInputSanitizer` forces the flag down
            // for a dashing or sliding player — but a client that kept drawing
            // an open window while the server had already closed it would show
            // a panel whose every request came back refused.
            if (_dashLatch || _slideLatch) _inventoryOpen = false;
            _dashLatch = false;
            _slideLatch = false;
        }
    }
}
