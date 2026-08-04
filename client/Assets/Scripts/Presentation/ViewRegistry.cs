using System.Collections.Generic;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Sole owner of `MobView`/`ProjectileView` lifecycle (П-1): maps the runner's
    /// live snapshot to a pool of views purely by entity Id (spec §3.7). Two
    /// independent responsibilities:
    ///  - `LateUpdate` (self-driven, every render frame — not a `TicksFlushed`
    ///    subscription, same shape as `PlayerView`/`CameraRig`): diffs `Curr`
    ///    against the tracked Id set. A new Id rents a view from the pool and snaps
    ///    it straight to `Curr` (no interpolation, spec §3.7); a continuing Id
    ///    lerps between its position in `Prev` (falling back to `Curr` if that Id
    ///    isn't in `Prev`) and `Curr` by `Alpha`; an Id that drops out of `Curr`
    ///    returns its view to the pool. Every live mob (new or continuing) also
    ///    gets `MobView.Sync(in MobState, float, bool, float)` called here (Task 21,
    ///    resolution "Bind contract") — the per-frame telegraph-pulse/Fire-glint/
    ///    hover-glow (В1/В2 fix-wave 2) accent read, same "no new subscriber"
    ///    rule (П-1) as everything else in this class.
    ///  - `HandleEvent` (called by `SimEventRouter`, П-1's ordered fan-out — never
    ///    subscribed directly to any runner event): retires a view the instant its
    ///    entity's terminal event fires (MobDied for mobs; ProjectileBlocked /
    ///    ProjectileExpired for projectiles — NOT ProjectileHit: that event's
    ///    `EntityId` is the hit mob's Id, not the consumed projectile's, per
    ///    `ProjectileSystem`'s emit contract, so it can never match a live entry in
    ///    `_activeProjectiles`; a projectile's on-hit retirement is left to the
    ///    ordinary `LateUpdate` diff below), ahead of that frame's `LateUpdate`
    ///    diff. This is redundant with the diff on a normal frame (the Id is
    ///    already gone from `Curr` by then too) — it exists so retirement is
    ///    explicit and immediate rather than only an incidental side effect of
    ///    diffing.
    /// Dictionaries/pools/scratch buffers are pre-sized from `ArenaConfig`'s caps in
    /// `Awake` and never rebuilt — steady-state play allocates nothing (spec §3.7).
    public sealed class ViewRegistry : MonoBehaviour
    {
        // Mech pivots sit at the feet (Task 10, assets phase B) — the old
        // capsule fallback's pivot was its center, hence the +1m lift; a real
        // model needs none.
        static readonly Vector3 MobOffset = Vector3.zero;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] ArenaConfig _arena;
        [SerializeField] AimProvider _aimProvider;
        [SerializeField] MobView _chaserPrefab;
        [SerializeField] MobView _gunnerPrefab;
        [SerializeField] ProjectileView _projectilePrefab;

        Dictionary<int, MobView> _activeMobs;
        Dictionary<int, ProjectileView> _activeProjectiles;
        Stack<MobView> _chaserPool;
        Stack<MobView> _gunnerPool;
        Stack<ProjectileView> _projectilePool;

        // Per-frame scratch buffers, cleared and reused every call — no allocation
        // once warmed up.
        HashSet<int> _seenMobIds;
        HashSet<int> _seenProjectileIds;
        List<int> _staleIdsScratch;

        void Awake()
        {
            int mobCap = _arena.MaxMobs;
            int projCap = _arena.MaxProjectiles;

            _activeMobs = new Dictionary<int, MobView>(mobCap);
            _activeProjectiles = new Dictionary<int, ProjectileView>(projCap);
            // Both pools capped at mobCap (Б6 — not split by archetype ratio):
            // a match's Chaser/Gunner mix can vary, so each pool is sized as if
            // the whole cap were one archetype rather than guessing a split.
            _chaserPool = new Stack<MobView>(mobCap);
            _gunnerPool = new Stack<MobView>(mobCap);
            _projectilePool = new Stack<ProjectileView>(projCap);
            _seenMobIds = new HashSet<int>(mobCap);
            _seenProjectileIds = new HashSet<int>(projCap);
            _staleIdsScratch = new List<int>(math.max(mobCap, projCap));
        }

        // WorldRestarted is not a tick event (П-1 only restricts TicksFlushed to
        // its sole SimEventRouter subscriber) — direct subscription here, same
        // shape as the deleted PracticeTargets' pattern. Awake above always runs
        // before this object's own OnEnable, so the dictionaries below are never
        // null by the time this can fire, regardless of cross-object Awake/
        // OnEnable ordering against SimulationRunner.
        void OnEnable() => _runner.WorldRestarted += Clear;

        void OnDisable() => _runner.WorldRestarted -= Clear;

        /// Returns every active view to its pool (Task 24 spec Interfaces): a
        /// match restart swaps in a brand-new `SimulationWorld` with entity Ids
        /// starting back at 1, so any views still keyed by the OLD world's Ids
        /// would otherwise leak (never retired by the ordinary diff/HandleEvent
        /// paths above, which only ever see the new world's snapshots) and a
        /// fresh Id could collide with one still marked active here. Idempotent:
        /// safe to call on an already-empty registry (e.g. a restart before any
        /// mob/projectile ever spawned).
        public void Clear()
        {
            foreach (KeyValuePair<int, MobView> kv in _activeMobs)
            {
                kv.Value.gameObject.SetActive(false);
                // Type is set by Bind before a view ever reaches _activeMobs
                // (the only path in — see RentMob/SyncMobs), so it's always
                // valid here (Б6).
                Stack<MobView> pool = kv.Value.Type == MobType.Chaser ? _chaserPool : _gunnerPool;
                pool.Push(kv.Value);
            }
            _activeMobs.Clear();

            foreach (KeyValuePair<int, ProjectileView> kv in _activeProjectiles)
            {
                kv.Value.gameObject.SetActive(false);
                _projectilePool.Push(kv.Value);
            }
            _activeProjectiles.Clear();
        }

        void LateUpdate()
        {
            // One-frame ordering edge case before the runner's own Awake has run
            // (mirrors HudController) — skip rather than throw.
            if (_runner.World == null) return;

            SyncMobs();
            SyncProjectiles();
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out) — retirement only, see class doc. `ProjectileHit` is
        /// deliberately absent: its `EntityId` names the hit mob, not the
        /// projectile (that Id is still surfaced here for a future flash-hook,
        /// Phase 8's Task 25 — just not for retirement).
        public void HandleEvent(in SimEvent e)
        {
            switch (e.Kind)
            {
                case SimEventKind.MobDied:
                    RetireMob(e.EntityId);
                    break;
                case SimEventKind.ProjectileBlocked:
                case SimEventKind.ProjectileExpired:
                    RetireProjectile(e.EntityId);
                    break;
            }
        }

        /// `GameFeelDirector`'s seam into per-mob view state (Task 25 Interfaces)
        /// — looks up the live view for a `ProjectileHit` event's `EntityId` so
        /// the director can call `MobView.Flash`/`FreezePosition` on the actual
        /// hit mob without this class needing to know anything about hitstop
        /// itself (П-1: no hitstop-specific branching lives here).
        public bool TryGetMobView(int id, out MobView view) => _activeMobs.TryGetValue(id, out view);

        void SyncMobs()
        {
            // Task 25 (Приложение П-7): reads ONLY `SimulationRunner.RenderPrev`/
            // `RenderCurr`/`RenderAlpha` — never `Prev`/`Curr`/`Alpha` directly.
            // A `FullFrame` hitstop freeze is entirely `SimulationRunner`'s doing;
            // this class has no `if (HitstopActive)` branch anywhere.
            RenderSnapshot curr = _runner.RenderCurr;
            RenderSnapshot prev = _runner.RenderPrev;
            float alpha = _runner.RenderAlpha;
            // L-13 fix-round: read once per frame (not baked into MobView as a
            // duplicated constant) so a balance re-tune of MobConfig.TelegraphSeconds
            // (hot-tweak, spec §3.9) is reflected in the telegraph pulse immediately —
            // `_runner.World` is guaranteed non-null here, LateUpdate's own guard above
            // already returned early otherwise.
            float telegraphSeconds = _runner.World.Config.Chaser.TelegraphSeconds;
            // В1/В2 fix-wave 2 (app-n6g item 3b): read once per frame, same
            // shape as telegraphSeconds above — MobView.Sync takes plain
            // values, never a GameFeelConfig/AimProvider reference of its own.
            MobView hoveredMob = _aimProvider != null ? _aimProvider.CurrentHoveredMob : null;
            float hoverGlowBoost = _gameFeel.AimHoverGlowBoost;

            // Task 10 (assets phase B spec §3.7): built once per frame, not
            // per-view — every live MobVisual reads the same feel numbers this
            // frame (T9's contract).
            MobVisualParams visualParams = new MobVisualParams
            {
                WalkEnterSpeed = _gameFeel.MobWalkEnterSpeed,
                WalkExitSpeed = _gameFeel.MobWalkExitSpeed,
                RunEnterSpeed = _gameFeel.MobRunEnterSpeed,
                RunExitSpeed = _gameFeel.MobRunExitSpeed,
                HoldSeconds = _gameFeel.LocomotionHoldSeconds,
                TurnDegPerSec = _gameFeel.MobTurnDegPerSec,
                YawOffsetDeg = _gameFeel.MechYawOffsetDeg,
                LocomotionCrossFadeSeconds = _gameFeel.LocomotionCrossFadeSeconds,
                OneShotCrossFadeSeconds = _gameFeel.OneShotCrossFadeSeconds,
                DeltaTime = Time.unscaledDeltaTime,
                PlayerPos = _runner.RenderPlayerWorldPos,
                Paused = _runner.Paused,
            };

            _seenMobIds.Clear();
            for (int i = 0; i < curr.MobCount; i++)
            {
                MobState m = curr.Mobs[i];
                _seenMobIds.Add(m.Id);

                if (!_activeMobs.TryGetValue(m.Id, out MobView view))
                {
                    view = RentMob(m.Type);
                    // Position before Bind (spec/П-2 fix-round, app-2pl): canonical
                    // order for a freshly-rented view — see the matching comment on
                    // the projectile branch below for why this order matters at all.
                    view.transform.position = SimSpace.ToWorld(m.Pos) + MobOffset;
                    view.Bind(in m);
                    view.Visual?.Bind(in m, m.Type == MobType.Chaser
                        ? _gameFeel.ChaserVisualScale : _gameFeel.GunnerVisualScale);
                    // Sync right away (Task 21 Bind/Sync contract) so a mob that's
                    // already mid-Telegraph the instant it becomes visible reads
                    // correctly this same frame, not one frame late.
                    view.Sync(in m, telegraphSeconds, view == hoveredMob, hoverGlowBoost);
                    view.Visual?.Sync(in m, in visualParams);
                    _activeMobs.Add(m.Id, view);
                    continue;
                }

                // Task 25 (Приложение П-7, `TargetOnly` hitstop scope): a mob
                // `GameFeelDirector.HandleProjectileHit` just froze
                // (`MobView.FreezePosition`) holds its transform exactly where it
                // was instead of being overwritten here — everyone else keeps
                // interpolating normally off the live pair. `Sync` (accent/flash
                // color) still runs regardless: the hit-flash itself must never
                // freeze, only the position does.
                if (!view.IsPositionFrozen)
                {
                    float2 prevPos = FindMobPrevPos(prev, m.Id, m.Pos);
                    Vector3 world = Vector3.Lerp(SimSpace.ToWorld(prevPos), SimSpace.ToWorld(m.Pos), alpha);
                    view.transform.position = world + MobOffset;
                }
                view.Sync(in m, telegraphSeconds, view == hoveredMob, hoverGlowBoost);
                // After the position write above (Б7): when frozen, position
                // wasn't written this frame, so MobVisual's own prev/curr delta
                // reads zero and it settles on Idle — no separate "frozen" branch
                // needed here or in MobVisual.
                view.Visual?.Sync(in m, in visualParams);
            }

            _staleIdsScratch.Clear();
            foreach (KeyValuePair<int, MobView> kv in _activeMobs)
            {
                if (!_seenMobIds.Contains(kv.Key)) _staleIdsScratch.Add(kv.Key);
            }
            for (int i = 0; i < _staleIdsScratch.Count; i++) RetireMob(_staleIdsScratch[i]);
        }

        void SyncProjectiles()
        {
            // Task 25 (Приложение П-7): same Render* switch as `SyncMobs` above —
            // projectiles have no `TargetOnly` freeze of their own (only
            // `MobView` does), so a `FullFrame` hitstop is the only way they
            // ever hold still, and that already falls straight out of reading
            // `RenderPrev`/`RenderCurr`/`RenderAlpha` here.
            RenderSnapshot curr = _runner.RenderCurr;
            RenderSnapshot prev = _runner.RenderPrev;
            float alpha = _runner.RenderAlpha;

            _seenProjectileIds.Clear();
            for (int i = 0; i < curr.ProjectileCount; i++)
            {
                ProjectileState p = curr.Projectiles[i];
                _seenProjectileIds.Add(p.Id);

                if (!_activeProjectiles.TryGetValue(p.Id, out ProjectileView view))
                {
                    view = RentProjectile();
                    // Position BEFORE Bind (fix-round, app-2pl/bd app-2pl): Bind
                    // calls TrailRenderer.Clear(), which seeds the trail's first
                    // point at the transform's CURRENT position. Clearing before
                    // the teleport left that first point at the pooled view's old
                    // (pre-rent) position — typically wherever the previous
                    // projectile died — so the trail drew a spurious segment from
                    // there to the new spawn point every time a view came out of
                    // the pool: exactly the "rays at ~20° off the aim direction on
                    // almost every shot" the owner saw in the milestone-2 playtest.
                    // Task 21 (K8): height comes from the projectile's own
                    // simulated `Height` (Task 4) instead of a flat guessed
                    // lift — no interpolation needed for a brand-new entity,
                    // same "snap, don't lerp" rule the position half already
                    // follows (spec §3.7).
                    view.transform.position = SimSpace.ToWorld(p.Pos) + Vector3.up * p.Height;
                    view.Bind(_gameFeel.TracerFadeSeconds, _gameFeel.TracerScale);
                    _activeProjectiles.Add(p.Id, view);
                    continue;
                }

                float2 prevPos = FindProjectilePrevPos(prev, p.Id, p.Pos);
                Vector3 world = Vector3.Lerp(SimSpace.ToWorld(prevPos), SimSpace.ToWorld(p.Pos), alpha);
                // Task 21 (K8): vertical interpolation mirrors the horizontal
                // lerp above, but sources both ends straight off THIS struct's
                // own Height/PrevHeight pair (`ProjectileState`'s own doc:
                // "PrevHeight mirrors PrevPos's role for interpolation") rather
                // than a second `Find...PrevHeight` snapshot walk — Task 25's
                // render double-buffer shifts by exactly one tick per flush
                // (SimulationRunner.Update), so `p.PrevHeight` (this struct,
                // read off RenderCurr) already equals what a `prev`-snapshot
                // lookup of `.Height` would return, with no extra scan.
                world.y = Mathf.Lerp(p.PrevHeight, p.Height, alpha);
                view.transform.position = world;
            }

            _staleIdsScratch.Clear();
            foreach (KeyValuePair<int, ProjectileView> kv in _activeProjectiles)
            {
                if (!_seenProjectileIds.Contains(kv.Key)) _staleIdsScratch.Add(kv.Key);
            }
            for (int i = 0; i < _staleIdsScratch.Count; i++) RetireProjectile(_staleIdsScratch[i]);
        }

        static float2 FindMobPrevPos(RenderSnapshot prev, int id, float2 fallback)
        {
            for (int i = 0; i < prev.MobCount; i++)
                if (prev.Mobs[i].Id == id) return prev.Mobs[i].Pos;
            return fallback;
        }

        static float2 FindProjectilePrevPos(RenderSnapshot prev, int id, float2 fallback)
        {
            for (int i = 0; i < prev.ProjectileCount; i++)
                if (prev.Projectiles[i].Id == id) return prev.Projectiles[i].Pos;
            return fallback;
        }

        MobView RentMob(MobType type)
        {
            Stack<MobView> pool = type == MobType.Chaser ? _chaserPool : _gunnerPool;
            if (pool.Count > 0)
            {
                MobView v = pool.Pop();
                v.gameObject.SetActive(true);
                return v;
            }
            MobView prefab = type == MobType.Chaser ? _chaserPrefab : _gunnerPrefab;
            return Instantiate(prefab, transform);
        }

        ProjectileView RentProjectile()
        {
            if (_projectilePool.Count > 0)
            {
                ProjectileView v = _projectilePool.Pop();
                v.gameObject.SetActive(true);
                return v;
            }
            return Instantiate(_projectilePrefab, transform);
        }

        void RetireMob(int id)
        {
            if (!_activeMobs.TryGetValue(id, out MobView view)) return;
            _activeMobs.Remove(id);
            view.gameObject.SetActive(false);
            // Type is set by Bind before a view ever reaches _activeMobs (the
            // only path in — see RentMob/SyncMobs), so it's always valid here
            // (Б6).
            Stack<MobView> pool = view.Type == MobType.Chaser ? _chaserPool : _gunnerPool;
            pool.Push(view);
        }

        void RetireProjectile(int id)
        {
            if (!_activeProjectiles.TryGetValue(id, out ProjectileView view)) return;
            _activeProjectiles.Remove(id);
            view.gameObject.SetActive(false);
            _projectilePool.Push(view);
        }
    }
}
