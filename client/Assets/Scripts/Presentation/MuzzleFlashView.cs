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
    /// burst — the TTL itself is `SimulationRunner.ImmediatePredictionTtlSeconds`,
    /// a single shared constant (fix-round review #3) so this component and
    /// `AudioDirector` can never drift onto two different windows. If no
    /// matching event arrives within it (a false prediction — e.g.
    /// `CanFireWhileDash=false` and a dash starts the same frame, or the player
    /// releases Fire between the predicting frame and the confirming tick), the
    /// latch just times out: one extra burst with no matching shot, an accepted
    /// rare cosmetic artifact (spec-acknowledged, task-28-report.md). The
    /// suppression originally could not distinguish a player shot from a
    /// mob's (this component already shares one flash prop across both,
    /// pre-Task-28 — `HandleEvent` never filtered by owner, `SimEvent` carried
    /// no `ProjectileOwner`) — fix-round review #4 found that a MOB's real
    /// event landing inside the TTL window instead of the player's own made
    /// `HandleEvent` wrongly treat IT as the confirmation (consumes the
    /// latch), which both dropped that mob shot's own burst AND left the
    /// predicted burst unconsumed, so the player's own real event (arriving
    /// moments later, same or next flush) burst AGAIN on top of it — a double
    /// burst for the player's shot plus a missing one for the mob's, not
    /// merely "the mob's gets swallowed" (bd app-ai2). Fixed by the F-3
    /// fix-round: `SimEvent` now carries `Owner`
    /// (`SimulationWorld.SpawnProjectile`), and `HandleEvent` below only lets
    /// a PLAYER-owned event consume the latch — a mob's shot always bursts,
    /// unconditionally, and never touches `_predicted`.
    public sealed class MuzzleFlashView : MonoBehaviour
    {
        const int BurstCount = 8;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;

        ParticleSystem _particles;
        bool _predicted;
        float _predictedExpireAt;

        void Awake() => _particles = GetComponent<ParticleSystem>();

        /// Task 28: per-frame prediction — see the class doc above. Fix-round
        /// (review #1, Medium): the authoritative burst spawns at the MUZZLE
        /// (`WeaponSystem`'s `p.Pos + dir * cfg.MuzzleOffset`, `HandleEvent`
        /// below via `e.Pos`), not the hero's center — bursting the prediction
        /// from `player.Pos` was a visible regression once the predicted burst
        /// became the common case (the latch suppresses the real one on every
        /// ordinary shot). `MuzzleOffset` is read from `_runner.Config.Weapon`
        /// (Task 43) — the already-built `SimConfig` the authoritative path
        /// itself reads — rather than adding a THIRD `WeaponConfig` SO reference; the
        /// small sub-tick `overshoot` term `WeaponSystem` also folds in is
        /// simulation-internal (depends on the tick that hasn't happened yet)
        /// and skipped here, an imperceptible cosmetic approximation.
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
            dir.Normalize();

            float muzzleOffset = _runner.Config.Weapon.MuzzleOffset;
            EmitBurst(posW + dir * muzzleOffset, dir, _runner.RenderMuzzleHeight);
            _predicted = true;
            _predictedExpireAt = Time.unscaledTime + SimulationRunner.ImmediatePredictionTtlSeconds;
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out).
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind != SimEventKind.ProjectileFired) return;

            // F-3 fix-round (bd app-ai2): the predicted latch is only ever armed
            // from the PLAYER's own state (Update above reads `_runner.RenderCurr.
            // Player`), so only a player-owned event may consume it — a mob's shot
            // landing inside the TTL window must always burst on its own instead of
            // being wrongly swallowed as "the" confirmation (see the class doc above
            // for the double-burst-plus-missing-burst bug this used to cause).
            if (e.Owner == ProjectileOwner.Player
                && _predicted && Time.unscaledTime <= _predictedExpireAt)
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
            // Task 21 (QC9): a Gunner mob fires from `MobConfig.MuzzleHeight`
            // (0.95, Task 4/Task 17) in the sim — the flash must match that
            // exact height, not the old flat ground-anchor `lift = 0`, or the
            // burst would visibly float below/above the Gunner's own muzzle.
            // The player branch reads `_runner.RenderMuzzleHeight` (PC7's
            // single home) instead of `GameFeelConfig.MuzzleLiftY`, same
            // canonical accessor `SpawnCasing`/`AimRayView` now use.
            float lift = e.Owner == ProjectileOwner.Player
                ? _runner.RenderMuzzleHeight
                : _runner.Config.Gunner.MuzzleHeight;
            EmitBurst(SimSpace.ToWorld(e.Pos), dir, lift);
        }

        /// Phase B: lift raises the burst to the shooter's muzzle height (Task
        /// 21 — both branches now read a real muzzle height instead of the
        /// player-only doll lift / flat mob `0`, see `HandleEvent` above).
        void EmitBurst(Vector3 worldPos, Vector3 dir, float lift)
        {
            transform.position = worldPos + Vector3.up * lift;
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            _particles.Emit(BurstCount);
        }
    }
}
