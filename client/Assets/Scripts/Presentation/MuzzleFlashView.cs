using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Minimal muzzle-flash feedback (П-2, Task 17 milestone): bursts a
    /// `ParticleSystem` at the fire position on every `ProjectileFired` event,
    /// oriented to face the shot's actual direction (fix-round app-2pl round 1 —
    /// the burst previously stayed at a fixed world rotation, so its narrow-cone
    /// shape, `StageOneSceneBootstrap.ConfigureMuzzleParticles`, read as random
    /// scatter instead of "this way"). Round 1 derived that direction from
    /// `SimulationRunner.Curr.Player.Pos`, which is wrong during a multi-tick
    /// catch-up flush (`Curr` reflects only the batch's last tick, not
    /// necessarily the tick a given buffered event fired on — round 2 fix); the
    /// direction now comes entirely from the event's own `Amount` field
    /// (`SimulationWorld.SpawnProjectile`), which is tick-exact by
    /// construction, so this component needs no `SimulationRunner` reference at
    /// all. Driven exclusively by `SimEventRouter`'s `HandleEvent` fan-out
    /// (П-1) — never subscribes to `TicksFlushed` itself. Full game-feel
    /// treatment (screen shake, hitstop-synced timing, persistent casings) is
    /// Phase 8 — this is only the base "something happened" feedback the
    /// milestone-2 playtest needs.
    public sealed class MuzzleFlashView : MonoBehaviour
    {
        const int BurstCount = 8;

        ParticleSystem _particles;

        void Awake() => _particles = GetComponent<ParticleSystem>();

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out).
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind != SimEventKind.ProjectileFired) return;

            transform.position = SimSpace.ToWorld(e.Pos);
            // `e.Amount` is the shot's sim-plane velocity angle in radians
            // (`atan2(vel.y, vel.x)`, tick-exact). `SimSpace.ToWorld` maps sim
            // (x, y) to world (x, 0, z=y) linearly, so a sim-plane direction
            // vector maps through the same (x, y) -> (x, 0, y) convention as a
            // position — always unit-length, never the degenerate zero-vector
            // case a position difference could hit.
            Vector3 dir = new Vector3(Mathf.Cos(e.Amount), 0f, Mathf.Sin(e.Amount));
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

            _particles.Emit(BurstCount);
        }
    }
}
