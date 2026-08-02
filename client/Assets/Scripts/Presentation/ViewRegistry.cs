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
    ///    gets `MobView.Sync(in MobState)` called here (Task 21, resolution "Bind
    ///    contract") — the per-frame telegraph-pulse/Fire-glint accent read, same
    ///    "no new subscriber" rule (П-1) as everything else in this class.
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
        static readonly Vector3 MobOffset = Vector3.up * 1f;
        static readonly Vector3 ProjectileOffset = Vector3.up * 1f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] ArenaConfig _arena;
        [SerializeField] MobView _mobPrefab;
        [SerializeField] ProjectileView _projectilePrefab;

        Dictionary<int, MobView> _activeMobs;
        Dictionary<int, ProjectileView> _activeProjectiles;
        Stack<MobView> _mobPool;
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
            _mobPool = new Stack<MobView>(mobCap);
            _projectilePool = new Stack<ProjectileView>(projCap);
            _seenMobIds = new HashSet<int>(mobCap);
            _seenProjectileIds = new HashSet<int>(projCap);
            _staleIdsScratch = new List<int>(math.max(mobCap, projCap));
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

        void SyncMobs()
        {
            RenderSnapshot curr = _runner.Curr;
            RenderSnapshot prev = _runner.Prev;
            float alpha = _runner.Alpha;

            _seenMobIds.Clear();
            for (int i = 0; i < curr.MobCount; i++)
            {
                MobState m = curr.Mobs[i];
                _seenMobIds.Add(m.Id);

                if (!_activeMobs.TryGetValue(m.Id, out MobView view))
                {
                    view = RentMob();
                    // Position before Bind (spec/П-2 fix-round, app-2pl): canonical
                    // order for a freshly-rented view — see the matching comment on
                    // the projectile branch below for why this order matters at all.
                    view.transform.position = SimSpace.ToWorld(m.Pos) + MobOffset;
                    view.Bind(in m);
                    // Sync right away (Task 21 Bind/Sync contract) so a mob that's
                    // already mid-Telegraph the instant it becomes visible reads
                    // correctly this same frame, not one frame late.
                    view.Sync(in m);
                    _activeMobs.Add(m.Id, view);
                    continue;
                }

                float2 prevPos = FindMobPrevPos(prev, m.Id, m.Pos);
                Vector3 world = Vector3.Lerp(SimSpace.ToWorld(prevPos), SimSpace.ToWorld(m.Pos), alpha);
                view.transform.position = world + MobOffset;
                view.Sync(in m);
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
            RenderSnapshot curr = _runner.Curr;
            RenderSnapshot prev = _runner.Prev;
            float alpha = _runner.Alpha;

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
                    view.transform.position = SimSpace.ToWorld(p.Pos) + ProjectileOffset;
                    view.Bind(_gameFeel.TracerFadeSeconds);
                    _activeProjectiles.Add(p.Id, view);
                    continue;
                }

                float2 prevPos = FindProjectilePrevPos(prev, p.Id, p.Pos);
                Vector3 world = Vector3.Lerp(SimSpace.ToWorld(prevPos), SimSpace.ToWorld(p.Pos), alpha);
                view.transform.position = world + ProjectileOffset;
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

        MobView RentMob()
        {
            if (_mobPool.Count > 0)
            {
                MobView v = _mobPool.Pop();
                v.gameObject.SetActive(true);
                return v;
            }
            return Instantiate(_mobPrefab, transform);
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
            _mobPool.Push(view);
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
