using System.Collections.Generic;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Sole owner of `PlayerView`/`MobView`/`ProjectileView` lifecycle (П-1):
    /// maps the runner's live snapshot to pools of views — players by SLOT,
    /// mobs and projectiles by entity Id (spec §3.7/§3.12). Two
    /// independent responsibilities:
    ///  - `LateUpdate` (self-driven, every render frame — not a `TicksFlushed`
    ///    subscription, same shape as `CameraRig`): diffs `Curr`
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
    ///  - `HandlePlayerEvent` (Stage 2 Task 45a, also called by
    ///    `SimEventRouter`, at the place in the fan-out the doll's own slot used
    ///    to occupy): routes a player-scoped event to the ONE doll it concerns.
    ///    The per-kind meaning of `SimEvent.PlayerIndex` is resolved here rather
    ///    than by the router — the router is wiring and owns no conventions.
    /// Dictionaries/pools/scratch buffers are pre-sized from `ArenaConfig`'s caps in
    /// `Awake` and never rebuilt — steady-state play allocates nothing (spec §3.7).
    ///
    /// WHICH PLAYER SLOTS GET A DOLL (Stage 2 Task 45a). `Alive == false` in
    /// `Players[]` is NOT the same fact as "this slot is not in the frame", and
    /// the two need opposite treatment. A slot is drawn while it is `Alive`, and
    /// it keeps being drawn after that IF THIS CLIENT WATCHED IT DIE — a
    /// `PlayerDied` event that reached `HandlePlayerEvent` while the slot's doll
    /// was rented. That is what leaves a corpse standing where it fell (the doll
    /// in its Death01 pose IS the player corpse; client/CLAUDE.md's own rule is
    /// that corpses do not disappear before the match ends), and it is what this
    /// client's own death has always done. Every other not-alive slot is
    /// retired: one nobody has joined, one outside this client's visibility, one
    /// whose death happened out of view — all three arrive as
    /// `default(PlayerState)` from `NetworkSimBackend.BeginSlot` (its own doc:
    /// "the slots no record arrived for read `default(PlayerState)`: not alive,
    /// at the origin"), and drawing them would put dolls at the arena origin.
    /// A death for a slot with no doll rented is deliberately not latched: a
    /// death this client cannot place is one it does not show.
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
        [SerializeField] PlayerView _playerPrefab;
        [SerializeField] MobView _chaserPrefab;
        [SerializeField] MobView _gunnerPrefab;
        [SerializeField] ProjectileView _projectilePrefab;

        Dictionary<int, PlayerView> _activePlayers;
        Dictionary<int, MobView> _activeMobs;
        Dictionary<int, ProjectileView> _activeProjectiles;
        Stack<PlayerView> _playerPool;
        Stack<MobView> _chaserPool;
        Stack<MobView> _gunnerPool;
        Stack<ProjectileView> _projectilePool;

        // Stage 2 Task 45a: the corpse latch behind the presence rule in the
        // class doc — indexed by player slot, cleared by `Clear` alongside the
        // views themselves.
        bool[] _playerDeathSeen;

        // Per-frame scratch buffers, cleared and reused every call — no allocation
        // once warmed up.
        HashSet<int> _seenPlayerSlots;
        HashSet<int> _seenMobIds;
        HashSet<int> _seenProjectileIds;
        List<int> _staleIdsScratch;

        void Awake()
        {
            int playerCap = _arena.MaxPlayers;
            int mobCap = _arena.MaxMobs;
            int projCap = _arena.MaxProjectiles;

            _activePlayers = new Dictionary<int, PlayerView>(playerCap);
            _playerPool = new Stack<PlayerView>(playerCap);
            _playerDeathSeen = new bool[playerCap];
            _seenPlayerSlots = new HashSet<int>(playerCap);
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
            // Stage 2 Task 45a: the dolls go back too, and the corpse latches
            // with them — a fresh match must not open with the previous one's
            // corpse still standing on a slot that is alive again.
            foreach (KeyValuePair<int, PlayerView> kv in _activePlayers)
            {
                kv.Value.gameObject.SetActive(false);
                _playerPool.Push(kv.Value);
            }
            _activePlayers.Clear();
            for (int i = 0; i < _playerDeathSeen.Length; i++) _playerDeathSeen[i] = false;

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
            // (mirrors HudController) — skip rather than throw. Task 43: asks
            // the backend `Ready` instead of testing `World == null`.
            if (!_runner.Ready) return;

            SyncPlayers();
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

        /// The player half of `SimEventRouter`'s fan-out (Stage 2 Task 45a, spec
        /// §3.12 Р65: "диспетчеризация событий игроков по `SimEvent.PlayerIndex`
        /// живёт внутри `ViewRegistry`") — called for every event in the same
        /// pass and at the same place in the order the doll's own slot used to
        /// hold, so the seven other slots keep their relative order exactly
        /// (Р98). Only the two kinds the doll reacts to are routed, and the
        /// index each one names is a per-KIND convention off
        /// `SimEvent.PlayerIndex`'s own doc, not one rule:
        ///  - `PlayerDied` — VICTIM ("PlayerDamaged/PlayerDied (mirrors
        ///    EntityId's convention for those two kinds)"), i.e. the player who
        ///    died, which is the doll that must play Death01. Taking the
        ///    ATTACKER here would kill the shooter's doll instead;
        ///  - `ProjectileFired` — ACTOR ("the five 'own-action' kinds
        ///    ProjectileFired, … / SpawnProjectile's ownerIndex"), i.e. the
        ///    shooter, which is the doll that must replay Pistol_Shoot. A mob's
        ///    round carries `ProjectileIds.NoOwner` and names no doll at all.
        /// An event naming a slot with no rented doll is ignored — see the
        /// class doc's presence rule.
        public void HandlePlayerEvent(in SimEvent e)
        {
            switch (e.Kind)
            {
                case SimEventKind.PlayerDied:
                    DispatchToDoll(e.PlayerIndex, in e, latchDeath: true);
                    break;
                case SimEventKind.ProjectileFired:
                    DispatchToDoll(e.PlayerIndex, in e, latchDeath: false);
                    break;
            }
        }

        void DispatchToDoll(byte playerIndex, in SimEvent e, bool latchDeath)
        {
            if (playerIndex == ProjectileIds.NoOwner) return;
            if (!_activePlayers.TryGetValue(playerIndex, out PlayerView view)) return;
            if (latchDeath) _playerDeathSeen[playerIndex] = true;
            view.Visual?.HandleEvent(in e, _gameFeel.OneShotCrossFadeSeconds);
        }

        /// `GameFeelDirector`'s seam into per-mob view state (Task 25 Interfaces)
        /// — looks up the live view for a `ProjectileHit` event's `EntityId` so
        /// the director can call `MobView.Flash`/`FreezePosition` on the actual
        /// hit mob without this class needing to know anything about hitstop
        /// itself (П-1: no hitstop-specific branching lives here).
        public bool TryGetMobView(int id, out MobView view) => _activeMobs.TryGetValue(id, out view);

        /// One doll per present player slot (Stage 2 Task 45a, spec §3.12).
        /// Same shape as `SyncMobs` below — a new key snaps straight to `Curr`,
        /// a continuing one lerps `Prev`→`Curr` by `Alpha`, a key that drops out
        /// returns its view to the pool — except that the key is the ARRAY INDEX
        /// (the player's slot), not an entity Id, so `Prev` is looked up by
        /// index instead of scanned.
        ///
        /// THE LOCAL DOLL IS NO LONGER A SPECIAL CASE OF POSITIONING. It used to
        /// read `SimulationRunner.RenderPlayerWorldPos`; the lerp below is that
        /// same formula (`RenderPrev`/`RenderCurr`/`RenderAlpha`, П-7) evaluated
        /// per slot, and for `LocalPlayerIndex` it produces the identical
        /// number, because `RenderSnapshot.Player` IS `Players[LocalPlayerIndex]`.
        void SyncPlayers()
        {
            RenderSnapshot curr = _runner.RenderCurr;
            RenderSnapshot prev = _runner.RenderPrev;
            float alpha = _runner.RenderAlpha;
            int localIndex = curr.LocalPlayerIndex;

            // Read once per frame, exactly like SyncMobs' own telegraphSeconds/
            // hover reads below: the views take plain values and never hold a
            // GameFeelConfig/SimulationRunner reference of their own.
            PlayerVisualParams visualParams = new PlayerVisualParams
            {
                MaxSpeed = _runner.Config.Hero.MaxSpeed,
                SpeedDampTime = _gameFeel.SpeedDampTime,
                MoveThreshold01 = _gameFeel.PlayerMoveThreshold01,
                YawOffsetDeg = _gameFeel.PlayerYawOffsetDeg,
                VisualTurnDegPerSec = _gameFeel.VisualTurnDegPerSec,
                IdleAimTurnDegPerSec = _gameFeel.IdleAimTurnDegPerSec,
                AimYawClampDeg = _gameFeel.AimYawClampDeg,
                SpineYawShare = _gameFeel.SpineYawShare,
                DashLeanDeg = _gameFeel.DashLeanDeg,
                DashLeanInOutSeconds = _gameFeel.DashLeanInOutSeconds,
                LocomotionCrossFadeSeconds = _gameFeel.LocomotionCrossFadeSeconds,
                OneShotCrossFadeSeconds = _gameFeel.OneShotCrossFadeSeconds,
                DeltaTime = Time.unscaledDeltaTime,
                Paused = _runner.Paused,
            };
            float linkHz = _gameFeel.LinkWindowFlashHz;
            float linkBoost = _gameFeel.LinkWindowFlashBoost;
            Color remoteEmission = _gameFeel.RemotePlayerEmission;
            bool hasAimProvider = _aimProvider != null;
            float2 localAimSimPos = hasAimProvider ? _aimProvider.CurrentAimSimPos : default;

            _seenPlayerSlots.Clear();
            for (int i = 0; i < curr.PlayerCount; i++)
            {
                PlayerState state = curr.Players[i];
                bool local = i == localIndex;
                if (!state.Alive && !_playerDeathSeen[i]) continue; // class doc: presence rule
                _seenPlayerSlots.Add(i);

                // THE AIM POINT IS RESOLVED HERE, AND ONLY HERE — the doll must
                // not be able to tell whose slot it is (spec §3.12: a stranger's
                // doll gets none of the local player's rights).
                //  - own slot: this render frame's cursor, which is what the doll
                //    has always oriented from. `PlayerState.AimPoint` already
                //    carries the same quantity for the local player
                //    (SimulationWorld copies it off the tick's input), but one
                //    tick older — reading the provider keeps the spine exactly as
                //    responsive as it was before pooling;
                //  - anyone else: the snapshot's own synthetic aim point, which
                //    exists only while that player holds aim
                //    (`PlayerFlags.ToSyntheticState` writes `AimSettleTimer`
                //    and `AimPoint` off the SAME wire bit, spec §3.12's flags
                //    table) — otherwise the doll is handed its own position, and
                //    the `aimDir` guards in `PlayerVisual.Sync` read that as
                //    "hold the last facing" rather than turning to face the
                //    arena origin, which is where a zeroed `AimPoint` points.
                state.AimPoint = local
                    ? (hasAimProvider ? localAimSimPos : state.AimPoint)
                    : (state.AimSettleTimer > 0f ? state.AimPoint : state.Pos);
                Color accent = local ? Color.black : remoteEmission;

                if (!_activePlayers.TryGetValue(i, out PlayerView view))
                {
                    view = RentPlayer();
                    // Position before Bind, same canonical order as the mob
                    // branch below — PlayerVisual's own speed read is a frame
                    // delta of this very transform.
                    view.transform.position = SimSpace.ToWorld(state.Pos);
                    view.Bind(local);
                    view.Visual?.Bind(in state, _gameFeel.PlayerVisualScale);
                    view.Sync(in state, linkHz, linkBoost, accent);
                    view.Visual?.Sync(in state, in visualParams);
                    _activePlayers.Add(i, view);
                    continue;
                }

                float2 prevPos = FindPlayerPrevPos(prev, i, state.Pos);
                view.transform.position = Vector3.Lerp(
                    SimSpace.ToWorld(prevPos), SimSpace.ToWorld(state.Pos), alpha);
                view.Sync(in state, linkHz, linkBoost, accent);
                view.Visual?.Sync(in state, in visualParams);
            }

            _staleIdsScratch.Clear();
            foreach (KeyValuePair<int, PlayerView> kv in _activePlayers)
            {
                if (!_seenPlayerSlots.Contains(kv.Key)) _staleIdsScratch.Add(kv.Key);
            }
            for (int i = 0; i < _staleIdsScratch.Count; i++) RetirePlayer(_staleIdsScratch[i]);
        }

        /// The previous render half's position for one player slot. Unlike the
        /// mob/projectile lookups below there is nothing to scan — the slot IS
        /// the index — but there IS something to refuse: a slot that was not
        /// alive in `prev` either never had a record there (`default`, i.e. the
        /// arena origin) or was already a corpse, and lerping FROM the origin
        /// would streak the doll across the arena for one frame. Falling back to
        /// `Curr` snaps instead, which is exactly what a corpse (which does not
        /// move) and a freshly-visible player both want.
        static float2 FindPlayerPrevPos(RenderSnapshot prev, int slot, float2 fallback)
            => slot < prev.PlayerCount && prev.Players[slot].Alive
                ? prev.Players[slot].Pos : fallback;

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
            // the backend is `Ready` here, LateUpdate's own guard above already
            // returned early otherwise.
            float telegraphSeconds = _runner.Config.Chaser.TelegraphSeconds;
            // В1/В2 fix-wave 2 (app-n6g item 3b): read once per frame, same
            // shape as telegraphSeconds above — MobView.Sync takes plain
            // values, never a GameFeelConfig/AimProvider reference of its own.
            MobView hoveredMob = _aimProvider != null ? _aimProvider.CurrentHoveredMob : null;
            // app-7pk (cheap version, Task 24 fold-in): the hover rim is
            // tinted by the SAME hovered zone the crosshair/aim-ray already
            // teach the player, via the shared `AimZoneColors.Resolve`
            // lookup (Reuse > duplication, AGENT.md §4 — one switch, not a
            // third copy). Fallback is `MobView.HoverGlowAccent`'s own
            // pre-app-7pk neutral white, for the defensive `HitZone.None`
            // case `hovered` shouldn't actually reach (see MobView.Sync's
            // own doc).
            HitZone hoveredZone = _aimProvider != null ? _aimProvider.CurrentAimZone : HitZone.None;
            Color hoverAccent = AimZoneColors.Resolve(hoveredZone, MobView.HoverGlowAccent, _gameFeel);
            // В3 fix-wave 2 (app-n6g item 3b, owner playtest feedback: "хочется
            // больше акцента на хедшоте"): a Head hover boosts the glow further
            // still — derived (×1.5 on top of the existing AimHoverGlowBoost)
            // rather than a new SO field, per the brief's own instruction ("derive,
            // don't add a field"). The COLOR side of "strictly AimZoneHeadColor"
            // needs no extra code — AimZoneColors.Resolve above already maps
            // HitZone.Head to AimZoneHeadColor unconditionally (its own class
            // doc); only the intensity multiplier needed strengthening.
            float hoverGlowBoost = hoveredZone == HitZone.Head
                ? _gameFeel.AimHoverGlowBoost * 1.5f : _gameFeel.AimHoverGlowBoost;

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
                    view.Sync(in m, telegraphSeconds, view == hoveredMob, hoverAccent, hoverGlowBoost);
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
                view.Sync(in m, telegraphSeconds, view == hoveredMob, hoverAccent, hoverGlowBoost);
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
                    // В3 fix-wave 2 (app-n6g item 1): the ball's own diameter, read
                    // straight off THIS shot's real sim radius — ProjectileState.Radius
                    // is Weapon.ProjectileRadius for a player shot, Gunner.ProjectileRadius
                    // for a mob shot (SimulationWorld.SpawnProjectile's own `radius`
                    // param, both owners flow through the SAME field, no per-owner
                    // branch needed here) — × the owner-tunable GameFeelConfig.
                    // ProjectileBallScale multiplier. See ProjectileBallScale's own
                    // doc for why a Gunner shot's ball growing slightly past the old
                    // flat placeholder size is correct, not a regression.
                    float ballDiameter = p.Radius * 2f * _gameFeel.ProjectileBallScale;
                    view.Bind(_gameFeel.TracerFadeSeconds, _gameFeel.TracerScale, ballDiameter);
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

        PlayerView RentPlayer()
        {
            if (_playerPool.Count > 0)
            {
                PlayerView v = _playerPool.Pop();
                v.gameObject.SetActive(true);
                return v;
            }
            return Instantiate(_playerPrefab, transform);
        }

        void RetirePlayer(int slot)
        {
            if (!_activePlayers.TryGetValue(slot, out PlayerView view)) return;
            _activePlayers.Remove(slot);
            // The corpse latch goes with the doll. A latched slot is never
            // retired by the ordinary diff (the presence rule keeps it in the
            // frame) — the one way here is a slot that fell outside
            // `PlayerCount` entirely, and a re-entry then has to come back
            // through `Alive` rather than resume somebody else's corpse.
            _playerDeathSeen[slot] = false;
            view.gameObject.SetActive(false);
            _playerPool.Push(view);
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
