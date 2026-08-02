using Ring.Data;
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
    /// construction, so `HandleEvent` itself needs no `SimulationRunner`
    /// reference. Driven exclusively by `SimEventRouter`'s `HandleEvent` fan-out
    /// (П-1) for the authoritative burst — never subscribes to `TicksFlushed`
    /// itself.
    ///
    /// Task 28 (spec §3.11, ImmediateMuzzleFeedback) reintroduces a
    /// `SimulationRunner`/`GameFeelConfig` reference, but only for a SEPARATE,
    /// per-frame `Update` path: predicts the burst in the frame the player
    /// presses Fire, ahead of the authoritative tick event which can otherwise
    /// land up to one full 30 Hz tick (spec §3.2, ~33ms) later — most visible at
    /// high render framerates, where several render frames elapse per sim tick.
    /// `SimulationRunner.WouldFireThisFrame` is the single source of truth for
    /// the heuristic (shared with `AudioDirector`, so the two components' guesses
    /// can never disagree). `_predicted`/`_predictedExpireAt` latch the
    /// prediction so it fires ONCE per press (not every render frame while
    /// `RenderCurr` is stale between two ticks) and get consumed by the matching
    /// real `ProjectileFired` event in `HandleEvent` to avoid a visible double
    /// burst. If no matching event arrives within `PredictedTtlSeconds` (a false
    /// prediction — e.g. `CanFireWhileDash=false` and a dash starts the same
    /// frame, or the player releases Fire between the predicting frame and the
    /// confirming tick), the latch just times out: one extra burst with no
    /// matching shot, an accepted rare cosmetic artifact (spec-acknowledged,
    /// task-28-report.md). The suppression also cannot distinguish a player shot
    /// from a mob's (this component already shares one flash prop across both,
    /// pre-Task-28 — `HandleEvent` never filtered by owner) — a mob shot landing
    /// inside another shot's TTL window could theoretically get swallowed
    /// instead of bursting on its own; `PredictedTtlSeconds` is kept close to one
    /// tick's length specifically to keep that window small.
    public sealed class MuzzleFlashView : MonoBehaviour
    {
        const int BurstCount = 8;

        // ~1.5 tick periods (33ms/tick @ 30Hz, spec §3.2): long enough for the
        // matching real tick to land and flush even under a slightly-late
        // accumulator crossing, short enough to bound both how long a false
        // prediction lingers and how wide the (already rare) mob-shot mismatch
        // window above is.
        const float PredictedTtlSeconds = 0.05f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;

        ParticleSystem _particles;
        bool _predicted;
        float _predictedExpireAt;

        void Awake() => _particles = GetComponent<ParticleSystem>();

        /// Task 28: per-frame prediction — see the class doc above.
        void Update()
        {
            if (!_gameFeel.ImmediateMuzzleFeedback) return;
            if (_predicted && Time.unscaledTime > _predictedExpireAt) _predicted = false;
            if (_predicted) return;
            if (!_runner.WouldFireThisFrame) return;

            PlayerState player = _runner.RenderCurr.Player;
            Vector3 posW = SimSpace.ToWorld(player.Pos);
            Vector3 dir = SimSpace.ToWorld(player.AimPoint) - posW;
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward; // aim degenerately coincides with Pos

            EmitBurst(posW, dir);
            _predicted = true;
            _predictedExpireAt = Time.unscaledTime + PredictedTtlSeconds;
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out).
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind != SimEventKind.ProjectileFired) return;

            if (_predicted && Time.unscaledTime <= _predictedExpireAt)
            {
                // Already shown this shot's feedback ahead of time (Update above)
                // — consume the latch instead of bursting a visible duplicate
                // (Task 28).
                _predicted = false;
                return;
            }

            // `e.Amount` is the shot's sim-plane velocity angle in radians
            // (`atan2(vel.y, vel.x)`, tick-exact). `SimSpace.ToWorld` maps sim
            // (x, y) to world (x, 0, z=y) linearly, so a sim-plane direction
            // vector maps through the same (x, y) -> (x, 0, y) convention as a
            // position — always unit-length, never the degenerate zero-vector
            // case a position difference could hit.
            Vector3 dir = new Vector3(Mathf.Cos(e.Amount), 0f, Mathf.Sin(e.Amount));
            EmitBurst(SimSpace.ToWorld(e.Pos), dir);
        }

        void EmitBurst(Vector3 worldPos, Vector3 dir)
        {
            transform.position = worldPos;
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            _particles.Emit(BurstCount);
        }
    }
}
