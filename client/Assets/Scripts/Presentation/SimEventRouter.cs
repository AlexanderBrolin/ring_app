using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Sole subscriber of `SimulationRunner.TicksFlushed` in the whole Presentation
    /// layer (П-1) — every class that needs per-event reactions is driven through
    /// this router's `HandleEvent` fan-out instead of subscribing directly. One
    /// pass over the tick-flush's event buffer (`SimulationRunner.EventCount`/
    /// `GetEvent` — Task 43 moved the read off the world and onto the facade,
    /// which is what lets a backend without a world of its own feed this
    /// router; still read before the facade closes the frame and the buffer is
    /// dropped), fanned out per event in a fixed relative order:
    ///   GameFeelDirector → PersistentPropsDirector → AudioDirector →
    ///   MuzzleFlashView → PlayerVisual (animation retrigger/death, phase B) →
    ///   ViewRegistry (retire only) → DeathOverlayController → HudController.
    /// `AudioDirector`, `MuzzleFlashView` and `ViewRegistry` exist as of Task 17;
    /// `DeathOverlayController` (Task 24) slots in last; `GameFeelDirector`
    /// (Task 25, Приложение П-1) slots in FIRST — hitstop/hit-flash/vignette must
    /// react before anything else in the same pass gets a chance to read view
    /// state for this frame. `PersistentPropsDirector` (Task 27, Приложение П-1)
    /// slots in right after it — casings/decals/corpses/sparks spawn purely from
    /// each event's own position, independent of anything the later slots do.
    /// `HudController` (Task 22, spec Г6) slots in LAST — its one reaction
    /// (arming the stamina-bar's `StaminaDenied` pulse) reads/writes no state any
    /// other slot touches, so its position in the order is not load-bearing;
    /// appended after `DeathOverlayController` rather than interleaved, so this
    /// task's diff doesn't reshuffle the already-settled relative order above.
    public sealed class SimEventRouter : MonoBehaviour
    {
        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelDirector _gameFeelDirector;
        [SerializeField] PersistentPropsDirector _persistentProps;
        [SerializeField] AudioDirector _audioDirector;
        [SerializeField] MuzzleFlashView _muzzleFlash;
        [SerializeField] PlayerVisual _playerVisual;
        [SerializeField] ViewRegistry _viewRegistry;
        [SerializeField] DeathOverlayController _deathOverlay;
        [SerializeField] HudController _hud;

        void OnEnable() => _runner.TicksFlushed += OnTicksFlushed;

        void OnDisable() => _runner.TicksFlushed -= OnTicksFlushed;

        /// Readiness guard (Task 43): this class had none, because the only
        /// backend that existed could not raise `TicksFlushed` before its first
        /// `Restart` had built a world. That is an ordering contract, not a
        /// property of this class — a networked backend subscribes here at
        /// `OnEnable` and becomes ready only once its first snapshot lands, so
        /// the early exit is what keeps the gap between the two from being read
        /// as an event buffer.
        ///
        /// `count` is read once, before the loop: a fan-out slot may spawn
        /// objects, so re-reading it per iteration would be asking the backend a
        /// question about a buffer that is being consumed.
        void OnTicksFlushed()
        {
            if (!_runner.Ready) return;

            int count = _runner.EventCount;
            for (int i = 0; i < count; i++)
            {
                SimEvent e = _runner.GetEvent(i);

                _gameFeelDirector.HandleEvent(in e); // hitstop/flash/vignette, first slot (Task 25, П-1)
                _persistentProps.HandleEvent(in e); // casings/decals/corpses/sparks/dash-glows (Task 27, П-1; app-9av)
                _audioDirector.HandleEvent(in e);
                _muzzleFlash.HandleEvent(in e); // shot feedback, same pass (П-2)
                _playerVisual.HandleEvent(in e); // animation retrigger/death (phase B)
                _viewRegistry.HandleEvent(in e); // retire only — mapping/lerp is ViewRegistry's own LateUpdate
                _deathOverlay.HandleEvent(in e); // death-screen show, last slot (Task 24, П-1)
                _hud.HandleEvent(in e); // stamina-denied pulse, last slot (Task 22)
            }
        }
    }
}
