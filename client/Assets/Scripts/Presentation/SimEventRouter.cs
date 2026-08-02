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
    ///   ViewRegistry (retire only) → DeathOverlayController.
    /// Only `AudioDirector`, `MuzzleFlashView` and `ViewRegistry` exist as of Task
    /// 17 — the other three slots are Phase 7/8 work; their place in the order is
    /// marked below instead of being stubbed out with empty classes.
    public sealed class SimEventRouter : MonoBehaviour
    {
        [SerializeField] SimulationRunner _runner;
        [SerializeField] AudioDirector _audioDirector;
        [SerializeField] MuzzleFlashView _muzzleFlash;
        [SerializeField] ViewRegistry _viewRegistry;

        void OnEnable() => _runner.TicksFlushed += OnTicksFlushed;

        void OnDisable() => _runner.TicksFlushed -= OnTicksFlushed;

        void OnTicksFlushed()
        {
            SimulationWorld world = _runner.World;
            int count = world.EventCount;
            for (int i = 0; i < count; i++)
            {
                SimEvent e = world.GetEvent(i);

                // GameFeelDirector (hitstop, screen shake) — Phase 8: slots in here first.
                // PersistentPropsDirector (casings, decals, corpses) — Phase 8: second.
                _audioDirector.HandleEvent(in e);
                _muzzleFlash.HandleEvent(in e); // shot feedback, same pass (П-2)
                _viewRegistry.HandleEvent(in e); // retire only — mapping/lerp is ViewRegistry's own LateUpdate
                // DeathOverlayController (death-screen fade) — Phase 7: slots in here last.
            }
        }
    }
}
