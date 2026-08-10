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
    /// can never disagree). `_latch` (`ImmediatePredictionLatch`, Stage 2 Task
    /// 45b — it was a `_predicted`/`_predictedExpireAt` pair of fields here and
    /// a second copy of the same pair in `AudioDirector` until then) is what
    /// makes at most one burst be predicted per SHOT, not one per render frame
    /// while `RenderCurr` is stale between two ticks, and what makes the
    /// matching `ProjectileFired` in `HandleEvent` suppress its own burst rather
    /// than double it. Its fix-round-1 shape holds ONE unconfirmed prediction at
    /// a time, which is what keeps a reconciliation from showing one round
    /// twice — that class's own doc has the mechanism and what the choice
    /// costs. Across a
    /// held burst that is one prediction per ROUND, not one per press: Task 43
    /// put this gate on `WeaponSystem.WouldFireThisTick`, whose cooldown term is
    /// `(FireCooldown - TickDt) <= 0f`, and `WeaponSystem.Advance` leaves
    /// `FireCooldown` strictly positive after every tick of sustained fire (its
    /// `while` loop can only exit above zero; the clamp back to zero lives in
    /// the "not firing" branch) — so that term comes true once per
    /// `FireInterval / TickDt` ticks (~4 at the shipped balance), i.e. once per
    /// round actually leaving the barrel. The pre-Task-43 copy tested
    /// `FireCooldown <= 0f` against that same always-positive value, so it was
    /// true practically only for the FIRST shot after a press/dash/start — that
    /// is what the old "ONCE per press" wording here described, and it no longer
    /// holds. The timeout is `SimulationRunner.ImmediatePredictionWindowSeconds`,
    /// one number for this component and `AudioDirector` (fix-round review #3's
    /// rule, kept) — since Stage 2 Task 45b they share the mechanism itself, and
    /// since its fix-round 1 the number comes from the BACKEND, because a
    /// prediction is confirmed in the next frame on one of them and only after
    /// the wire and the interpolation buffer on the other
    /// (`ISimBackend.ImmediatePredictionWindowSeconds`).
    /// A false prediction (e.g. `CanFireWhileDash=false` and a dash starts the
    /// same frame, or the player releases Fire between the predicting frame and
    /// the confirming tick) is never confirmed and simply times out: one extra
    /// burst with no matching shot, an accepted rare cosmetic artifact
    /// (spec-acknowledged, task-28-report.md). The
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
    /// unconditionally, and never touches the latch.
    ///
    /// STAGE 2 TASK 45b — THE FLASH LEAVES THE BARREL OF THE MODEL. Owner
    /// requirement of 2026-08-10 (bd `app-fl3`): a player's burst is placed at
    /// the muzzle socket of THAT PLAYER'S DOLL — `ViewRegistry.TryGetPlayerView`
    /// on `SimEvent.PlayerIndex`, or on `RenderSnapshot.LocalPlayerIndex` for
    /// the predicted burst — instead of at the simulation's own muzzle point
    /// (the hero's centre plus `WeaponConfig.MuzzleOffset` along the aim, lifted
    /// to `SimulationRunner.RenderMuzzleHeight`), which is a point in front of
    /// the collector and visibly not the pistol in his hand.
    ///
    /// NO DOLL, NO FLASH — THERE IS NO FALLBACK, AND THAT IS THE POINT (F-3,
    /// `app-aq9`). A shot from someone this client cannot see arrives as
    /// `SnapshotEventKind.ShotHeard` and is decoded into an ordinary
    /// `ProjectileFired` (`ClientEventDecoder`) whose position the server
    /// coarsened on purpose and whose direction it did not send at all. Bursting
    /// at that position — which is what this class did until now — draws a flash
    /// where a hidden shooter roughly is, handing away the one thing interest
    /// management exists to withhold. A hidden shooter has no doll, so this
    /// class now has nothing to draw from and draws nothing; the SOUND of that
    /// shot, which is the whole reason the event is sent, is `AudioDirector`'s
    /// and is untouched. A visible shooter has a doll and keeps their flash.
    ///
    /// THE DIRECTION IS STILL THE SHOT'S, NOT THE SOCKET'S FORWARD. `e.Amount`
    /// is tick-exact by construction (two fix-rounds went into making it the
    /// source, see above) and the aim supplies the predicted burst's; a socket's
    /// forward axis is only as good as its tuned rotation, and the muzzle socket
    /// is deliberately a POINT (`GameFeelConfig.GunMuzzleLocalPosition` has no
    /// euler beside it). The one place that costs something is a `ShotHeard`
    /// from a shooter this client CAN see: the wire carries no angle for that
    /// kind, so `e.Amount` reads 0 and the cone points due east while the burst
    /// itself sits correctly on their barrel. That is a smaller lie than the
    /// position was, and it is the shooter's cone, not their location.
    ///
    /// EVERY BURST OFF A DOLL IS DRAWN IN `LateUpdate`, AND THAT IS AN ORDERING
    /// FACT, NOT A STYLE (fix-round 1, G-1). A doll's gun rides a hand bone the
    /// Animator writes in `PreLateUpdate`, on a root `ViewRegistry` positions in
    /// its own `LateUpdate` — so the socket's world pose is only this frame's
    /// after that class has run. The event fan-out that used to draw these
    /// bursts arrives in the `Update` phase (`SimulationRunner` raises
    /// `TicksFlushed` inside its own `Update`, pinned at −50), which is BEFORE
    /// either write: bursting there put this frame's flash on last frame's
    /// barrel, a lag that reads as a lead on a running or turning collector and
    /// which did not exist before Task 45b, when flash and doll came out of the
    /// same `RenderCurr`. So a player-owned event now only RECORDS its shot
    /// (`HandleEvent`), and `LateUpdate` draws it after `ViewRegistry` — this
    /// class is pinned at `[DefaultExecutionOrder(10)]`, that one at −10, which
    /// is what makes "after" a guarantee rather than a hope (Unity orders
    /// equal-order `LateUpdate`s arbitrarily and this project ships no
    /// `ProjectSettings/MonoManager.asset`).
    /// A MOB's burst is drawn on the spot, still in the fan-out: it is placed
    /// from the event's own position and there is no transform for it to wait
    /// on. The PREDICTED burst moves to `LateUpdate` with the rest and stays in
    /// the frame the player pressed Fire — the same frame, a later phase of it,
    /// which is what Task 28's game feel asks for.
    [DefaultExecutionOrder(10)]
    public sealed class MuzzleFlashView : MonoBehaviour
    {
        const int BurstCount = 8;

        /// Player-owned bursts recorded in the fan-out and drawn in
        /// `LateUpdate` (class doc). Sized well past what one flush can carry —
        /// a flush holds the events of every tick the frame advanced, and each
        /// tick can fire at most one round per player (`WeaponSystem`'s own
        /// cooldown), so filling this would take a five-tick catch-up with three
        /// players firing every tick of it. A record that would overflow is
        /// dropped rather than allowed to grow the buffer: the cost is one
        /// missing eight-particle burst in a frame that was already stuttering,
        /// and the alternative is an allocation during a firefight.
        const int PendingCapacity = 16;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        // Stage 2 Task 45b: asked per event and per frame, never cached — a
        // doll is pooled and the slot it serves can change (`TryGetPlayerView`'s
        // own doc).
        [SerializeField] ViewRegistry _viewRegistry;

        ParticleSystem _particles;
        readonly ImmediatePredictionLatch _latch = new ImmediatePredictionLatch();
        readonly PendingBurst[] _pending = new PendingBurst[PendingCapacity];
        int _pendingCount;

        /// One recorded shot: whose doll to fire it from, and which way its cone
        /// points. The direction is resolved at EVENT time on purpose — it comes
        /// off `SimEvent.Amount`, which is tick-exact, and nothing about it
        /// improves by waiting for the dolls to move.
        struct PendingBurst
        {
            public int Slot;
            public Vector3 Dir;
        }

        void Awake() => _particles = GetComponent<ParticleSystem>();

        /// Task 28: per-frame prediction — see the class doc above. Fix-round
        /// (review #1, Medium): the authoritative burst spawns at the MUZZLE,
        /// not the hero's center — bursting the prediction from `player.Pos`
        /// was a visible regression once the predicted burst became the common
        /// case (the latch suppresses the real one on every ordinary shot).
        /// Stage 2 Task 45b: BOTH bursts now come off the doll's own muzzle
        /// socket, so the two agree on a point neither of them computes, and
        /// `WeaponConfig.MuzzleOffset` — the sim's own muzzle, which the burst
        /// used to approximate here — is no longer read by this class at all.
        ///
        /// THE ONE-PER-SHOT GATE IS THE RISING EDGE OF `WouldFireThisFrame`
        /// (`ImmediatePredictionLatch.ShouldPredict`), evaluated on EVERY frame
        /// this method reaches, edge or not — it is a function of the previous
        /// frame's answer. It replaced "is the latch already armed?", which
        /// silently doubled as the matching test and could not tell a second
        /// frame of the same shot from a second shot whose predecessor was
        /// still unconfirmed (bd `app-id9`).
        ///
        /// Fix-round 1 (G-1): drawn in `LateUpdate`, after the recorded
        /// authoritative bursts and after `ViewRegistry` has placed the dolls —
        /// still the frame the player pressed Fire (class doc).
        void LateUpdate()
        {
            FlushRecordedBursts();
            PredictBurst();
        }

        /// The shots recorded by `HandleEvent` this frame, now that every doll
        /// stands where this frame's snapshot says. A record whose shooter has
        /// no doll by the time the buffer is drained draws nothing, exactly as
        /// it would have at event time — the doll is what F-3 turns on.
        void FlushRecordedBursts()
        {
            for (int i = 0; i < _pendingCount; i++)
            {
                if (!TryGetMuzzle(_pending[i].Slot, out Vector3 muzzle)) continue;
                EmitBurst(muzzle, _pending[i].Dir);
            }
            _pendingCount = 0;
        }

        void PredictBurst()
        {
            if (!_gameFeel.ImmediateMuzzleFeedback) return;
            if (!_latch.ShouldPredict(_runner.WouldFireThisFrame, Time.unscaledTime)) return;

            RenderSnapshot curr = _runner.RenderCurr;
            // No doll yet (the opening frames of a match), or none any more
            // (this player is dead) — nothing to fire from, and nowhere else to
            // fire from either (class doc). The latch is left unarmed, so the
            // real event will show this shot itself.
            if (!TryGetMuzzle(curr.LocalPlayerIndex, out Vector3 muzzle)) return;

            PlayerState player = curr.Player;
            Vector3 dir = SimSpace.ToWorld(player.AimPoint) - SimSpace.ToWorld(player.Pos);
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward; // aim degenerately coincides with Pos
            dir.Normalize();

            EmitBurst(muzzle, dir);
            _latch.Arm(Time.unscaledTime, _runner.ImmediatePredictionWindowSeconds);
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out).
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind != SimEventKind.ProjectileFired) return;

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
            // Stage 2 Task 45b leaves this branch alone on purpose: a mob has
            // no doll and never will (its model carries no weapon socket), so
            // the event's own position plus that height stays the honest
            // answer for it.
            if (e.Owner != ProjectileOwner.Player)
            {
                EmitBurst(SimSpace.ToWorld(e.Pos)
                    + Vector3.up * _runner.Config.Gunner.MuzzleHeight, dir);
                return;
            }

            // F-3 fix-round (bd app-ai2), narrowed by Stage 2 Task 45b: only
            // the LOCAL player's own shot may consume the predicted latch. Owner
            // alone was enough while `ProjectileOwner.Player` meant "me" — it no
            // longer does, because every other player's rounds decode to that
            // same owner on a networked client (`ClientEventDecoder`), and a
            // stranger's shot swallowing my prediction is the very bug the Owner
            // check was added to stop, one participant further out.
            if (e.PlayerIndex == _runner.RenderCurr.LocalPlayerIndex
                && _latch.TryConsume(Time.unscaledTime))
            {
                // Already shown this shot's feedback ahead of time (Update above)
                // — consume the prediction instead of bursting a visible
                // duplicate (Task 28).
                return;
            }

            // The shooter's own barrel, or nothing at all (class doc — this is
            // where F-3 closes). Recorded, not drawn: the barrel is not where
            // this frame says it is until `ViewRegistry` has run (class doc,
            // fix-round 1 G-1).
            if (_pendingCount == PendingCapacity) return;
            _pending[_pendingCount++] = new PendingBurst { Slot = e.PlayerIndex, Dir = dir };
        }

        /// The world point a player's flash leaves from (Stage 2 Task 45b): the
        /// muzzle socket of the doll bound to `slot`, if there is one. False
        /// means "this player has no visible weapon right now" — behind the fog,
        /// dead, or not yet drawn — and every caller answers it by drawing
        /// nothing (class doc).
        ///
        /// The socket's own null check is not defensive noise: a doll prefab
        /// built before Task 45b has no socket child, and a stale one in a
        /// working copy must fail as "no flash" rather than as an exception per
        /// shot. `StageOneSceneBootstrap.SelfHealGunSocketsOnPrefab` is what
        /// fixes it, on the next Apply.
        bool TryGetMuzzle(int slot, out Vector3 worldPos)
        {
            worldPos = default;
            if (!_viewRegistry.TryGetPlayerView(slot, out PlayerView doll)) return false;
            Transform muzzle = doll.MuzzleSocket;
            if (muzzle == null) return false;
            worldPos = muzzle.position;
            return true;
        }

        /// Stage 2 Task 45b: the caller supplies the finished world point — the
        /// `lift` parameter went with the muzzle height, which for a player is
        /// now wherever the animated hand is holding the gun and for a mob is
        /// still `MobConfig.MuzzleHeight` above the event.
        void EmitBurst(Vector3 worldPos, Vector3 dir)
        {
            transform.position = worldPos;
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            _particles.Emit(BurstCount);
        }
    }
}
