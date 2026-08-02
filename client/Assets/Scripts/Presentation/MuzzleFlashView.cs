using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Minimal muzzle-flash feedback (П-2, Task 17 milestone): bursts a
    /// `ParticleSystem` at the fire position on every `ProjectileFired` event.
    /// Driven exclusively by `SimEventRouter`'s `HandleEvent` fan-out (П-1) — never
    /// subscribes to `TicksFlushed` itself. Full game-feel treatment (screen shake,
    /// hitstop-synced timing, persistent casings) is Phase 8 — this is only the
    /// base "something happened" feedback the milestone-2 playtest needs.
    public sealed class MuzzleFlashView : MonoBehaviour
    {
        const int BurstCount = 10;

        ParticleSystem _particles;

        void Awake() => _particles = GetComponent<ParticleSystem>();

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out).
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind != SimEventKind.ProjectileFired) return;

            transform.position = SimSpace.ToWorld(e.Pos);
            _particles.Emit(BurstCount);
        }
    }
}
