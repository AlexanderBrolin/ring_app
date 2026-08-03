using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Sole subscriber of `SimulationRunner.TicksFlushed` in the whole Presentation
    /// layer (П-1) — every class that needs per-event reactions is driven through
    /// this router's `HandleEvent` fan-out instead of subscribing directly. One
    /// pass over the tick-flush's event buffer (`World.EventCount`/`GetEvent`, read
    /// before `SimulationRunner` clears it), fanned out per event in a fixed
    /// relative order:
    ///   GameFeelDirector → PersistentPropsDirector → AudioDirector →
    ///   MuzzleFlashView → PlayerVisual (animation retrigger/death, phase B) →
    ///   ViewRegistry (retire only) → DeathOverlayController.
    /// `AudioDirector`, `MuzzleFlashView` and `ViewRegistry` exist as of Task 17;
    /// `DeathOverlayController` (Task 24) slots in last; `GameFeelDirector`
    /// (Task 25, Приложение П-1) slots in FIRST — hitstop/hit-flash/vignette must
    /// react before anything else in the same pass gets a chance to read view
    /// state for this frame. `PersistentPropsDirector` (Task 27, Приложение П-1)
    /// slots in right after it — casings/decals/corpses/sparks spawn purely from
    /// each event's own position, independent of anything the later slots do.
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

        void OnEnable() => _runner.TicksFlushed += OnTicksFlushed;

        void OnDisable() => _runner.TicksFlushed -= OnTicksFlushed;

        void OnTicksFlushed()
        {
            SimulationWorld world = _runner.World;
            int count = world.EventCount;
            for (int i = 0; i < count; i++)
            {
                SimEvent e = world.GetEvent(i);

                _gameFeelDirector.HandleEvent(in e); // hitstop/flash/vignette, first slot (Task 25, П-1)
                _persistentProps.HandleEvent(in e); // casings/decals/corpses/sparks (Task 27, П-1)
                _audioDirector.HandleEvent(in e);
                _muzzleFlash.HandleEvent(in e); // shot feedback, same pass (П-2)
                _playerVisual.HandleEvent(in e); // animation retrigger/death (phase B)
                _viewRegistry.HandleEvent(in e); // retire only — mapping/lerp is ViewRegistry's own LateUpdate
                _deathOverlay.HandleEvent(in e); // death-screen show, last slot (Task 24, П-1)
            }
        }
    }
}
