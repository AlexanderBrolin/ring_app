using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Minimal muzzle-flash feedback (П-2, Task 17 milestone): bursts a
    /// `ParticleSystem` at the fire position on every `ProjectileFired` event,
    /// oriented to face the shot's actual direction (fix-round, app-2pl — the
    /// burst previously stayed at a fixed world rotation, so its narrow-cone
    /// shape (`StageOneSceneBootstrap.ConfigureMuzzleParticles`) read as random
    /// scatter instead of "this way"). Driven exclusively by `SimEventRouter`'s
    /// `HandleEvent` fan-out (П-1) — never subscribes to `TicksFlushed` itself.
    /// Full game-feel treatment (screen shake, hitstop-synced timing, persistent
    /// casings) is Phase 8 — this is only the base "something happened" feedback
    /// the milestone-2 playtest needs.
    public sealed class MuzzleFlashView : MonoBehaviour
    {
        const int BurstCount = 8;

        [SerializeField] SimulationRunner _runner;

        ParticleSystem _particles;

        void Awake() => _particles = GetComponent<ParticleSystem>();

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out).
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind != SimEventKind.ProjectileFired) return;

            // `e.Pos` is the shot's spawn point (`p.Pos + dir * MuzzleOffset`,
            // `WeaponSystem.Update`) and `Curr.Player.Pos` is that same tick's
            // post-movement player position (captured right after the tick that
            // fired this shot) — their difference is exactly `dir` scaled by a
            // positive factor, so normalizing it recovers the true fire
            // direction, spread/recoil included, even for a multi-shot tick.
            Vector3 fireW = SimSpace.ToWorld(e.Pos);
            Vector3 playerW = SimSpace.ToWorld(_runner.Curr.Player.Pos);
            Vector3 dir = fireW - playerW;
            transform.position = fireW;
            // Zero (or near-zero) direction: leave the previous facing rather
            // than snapping to identity (same guard as PlayerView).
            if (dir.sqrMagnitude > 1e-8f)
                transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

            _particles.Emit(BurstCount);
        }
    }
}
