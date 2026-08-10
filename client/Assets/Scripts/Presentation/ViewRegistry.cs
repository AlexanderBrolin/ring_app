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
    ///    gets `MobView.Sync(in MobState, float, bool, Color, float)` called here (Task 21,
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
    /// A PLAYER CORPSE IS AN OBJECT, NOT A STATE OF ITS SLOT (Stage 2 Task 45a,
    /// fix-round 1; owner's own wording: "труп игрока = труп моба — где упал,
    /// там и лежит"). A slot gets a live doll for exactly as long as
    /// `Players[slot].Alive` — one rule, the same one that retires a mob whose
    /// Id left `Curr`. `PlayerDied` DETACHES that doll — **where it arrives in
    /// time to, which on the networked backend is never; see the two paragraphs
    /// below** — it leaves
    /// `_activePlayers` for `_corpses`, freeing the slot, and from that moment
    /// it is never `Sync`ed and never repositioned again. That is what makes it
    /// a corpse — the snapshot can no longer move it — and it is the whole fix
    /// for the defect the first round shipped: a slot whose record stops
    /// arriving reads `default(PlayerState)` ("not alive, at the origin",
    /// `NetworkSimBackend.BeginSlot`'s own doc), so anything still positioned
    /// FROM that slot teleports to the middle of the arena. Nothing is
    /// positioned from a dead slot any more.
    ///
    /// Corpses go back to the pool only in `Clear` (a new match), which is what
    /// client/CLAUDE.md's "трупы не исчезают до конца матча" asks for. The
    /// emission is killed at detach (owner decision): a body that keeps glowing
    /// misreports who is still standing, and it does so exactly when the cost of
    /// the mistake is highest.
    ///
    /// ON THE NETWORKED BACKEND NO DEATH LEAVES A CORPSE AT ALL — not merely
    /// the ones this client did not watch (Stage 2 Task 45b rewrote the
    /// paragraph that used to claim otherwise and its fix-round 1 qualified the
    /// detach sentence above; the mechanism is `app-2rf`, P1, and Task 47 is
    /// where it is closed). THE PICTURE LEADS THE EVENTS BY A
    /// TICK, by that backend's own construction: `NetworkSimBackend.
    /// ResolveRenderPair` copies the snapshot at `renderTick + 1` into `Curr`,
    /// while `ClientEventQueue.TryDequeue` refuses to hand out any event whose
    /// tick is still ahead of `renderTick`. So the frame that first shows the
    /// victim's slot as not-alive runs a whole render tick BEFORE `PlayerDied`
    /// is due — the diff below has already retired the doll into the pool, and
    /// the event, when it does arrive, finds no entry in `_activePlayers`,
    /// plays no Death01 and detaches nothing. What that backend loses is the
    /// corpse, not the position: a retired doll is simply gone, never left
    /// standing at the arena origin.
    ///
    /// IT POSITIONS EVERY DOLL BEFORE ANYTHING READS ONE (Stage 2 Task 45b
    /// fix-round 1, G-1) — hence `[DefaultExecutionOrder(-10)]` on this class.
    /// Three views now take a world point off a doll's gun in their own
    /// `LateUpdate` (`MuzzleFlashView`, `PersistentPropsDirector`,
    /// `AimRayView`, all pinned at 10), and the socket's world pose is only
    /// this frame's after two writes have happened: the Animator's, which puts
    /// the hand bone in place during `PreLateUpdate`, and this class's, which
    /// puts the doll's root where the snapshot says. Unity orders `LateUpdate`
    /// among equal-order scripts arbitrarily and this project ships no
    /// `ProjectSettings/MonoManager.asset`, so before the two pins the ray's
    /// origin could come from this frame or the previous one depending on the
    /// run. The number is negative rather than zero so that ordinary
    /// default-order readers (`CameraRig`, `CrosshairView`, `HudController`)
    /// keep seeing this frame's dolls without needing a pin of their own.
    ///
    /// ON THE LOCAL BACKEND THE MECHANISM ABOVE IS EXACTLY AS DESCRIBED,
    /// because there the tick's events are flushed in the same `Update` that
    /// produced the tick and `Curr` IS that tick — `PlayerDied` therefore
    /// reaches the doll before `LateUpdate`'s diff can notice the slot went
    /// quiet, which is the ordering the whole corpse path was written against.
    /// Nothing here branches on the backend; the difference is entirely in when
    /// the two facts arrive.
    [DefaultExecutionOrder(-10)]
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

        // Stage 2 Task 45a fix-round 1: dolls detached by `PlayerDied` (class
        // doc). Not a pool and not keyed by slot — a corpse has no slot any
        // more; it is simply a list of objects standing where they fell, which
        // `Clear` hands back to `_playerPool` and nothing else touches.
        List<PlayerView> _corpses;

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
            // TWICE THE ROSTER (fix-round 1): a match tops out at `playerCap`
            // live dolls PLUS `playerCap` corpses — each slot dies at most once
            // per match (`SimulationWorld.KillPlayer` has no revive; only a
            // restart brings a slot back, and that goes through `Clear`) — and
            // `Clear` hands every one of them back to this pool at once. Sized
            // for `playerCap` the first corpse would already make the stack
            // regrow, i.e. allocate, in the frame a player dies.
            _playerPool = new Stack<PlayerView>(playerCap * 2);
            _corpses = new List<PlayerView>(playerCap);
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
            // Stage 2 Task 45a: the dolls go back too — live ones and, fix-round
            // 1, the corpses. This is the ONLY place a corpse ever returns to
            // the pool: it is what "трупы не исчезают до конца матча"
            // (client/CLAUDE.md) means mechanically, and what keeps a fresh
            // match from opening with the previous one's body on the floor.
            foreach (KeyValuePair<int, PlayerView> kv in _activePlayers)
            {
                kv.Value.gameObject.SetActive(false);
                _playerPool.Push(kv.Value);
            }
            _activePlayers.Clear();
            for (int i = 0; i < _corpses.Count; i++)
            {
                _corpses[i].gameObject.SetActive(false);
                _playerPool.Push(_corpses[i]);
            }
            _corpses.Clear();

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

        /// The player half of `SimEventRouter`'s fan-out (Stage 2 Task 45a),
        /// called for every event in the same pass and at the same place in the
        /// order the doll's own slot used to hold, so the seven other slots keep
        /// their relative order exactly (Р98).
        ///
        /// SPEC Р65, QUOTED WHOLE (the sentence names a method, and an earlier
        /// draft of this doc cut the quote right before it): "Диспетчеризация
        /// событий игроков по `SimEvent.PlayerIndex` живёт **внутри**
        /// `ViewRegistry.HandleEvent`, а не отдельным подписчиком" — player
        /// event dispatch by `SimEvent.PlayerIndex` lives INSIDE
        /// `ViewRegistry.HandleEvent`, not in a subscriber of its own.
        /// Both halves that carry meaning are honored: the per-kind conventions
        /// live in this class, and no second `TicksFlushed` subscriber exists
        /// (П-1). The deviation is the method NAME — this is a second public
        /// entry point instead of a branch of `HandleEvent` above — and it is
        /// sanctioned by task-45a-brief §2.3, which requires the doll's
        /// reaction to keep its own place in the fan-out order, ahead of the
        /// retirement pass where the doll's slot used to sit, rather than
        /// merging into it.
        ///
        /// Only the two kinds the doll reacts to are routed, and the index each
        /// one names is a per-KIND convention off `SimEvent.PlayerIndex`'s own
        /// doc, not one rule:
        ///  - `PlayerDied` — VICTIM ("PlayerDamaged/PlayerDied (mirrors
        ///    EntityId's convention for those two kinds)"), i.e. the player who
        ///    died, which is the doll that must play Death01. Taking the
        ///    ATTACKER here would kill the shooter's doll instead;
        ///  - `ProjectileFired` — ACTOR ("the five 'own-action' kinds
        ///    ProjectileFired, … / SpawnProjectile's ownerIndex"), i.e. the
        ///    shooter, which is the doll that must replay Pistol_Shoot. A mob's
        ///    round carries `ProjectileIds.NoOwner` and names no doll at all.
        /// An event naming a slot with no live doll is ignored — which on the
        /// networked backend is every `PlayerDied` there is, the picture being
        /// a tick ahead of the events that explain it (`app-2rf`, class doc).
        public void HandlePlayerEvent(in SimEvent e)
        {
            switch (e.Kind)
            {
                case SimEventKind.PlayerDied:
                    DispatchToDoll(e.PlayerIndex, in e, death: true);
                    break;
                case SimEventKind.ProjectileFired:
                    DispatchToDoll(e.PlayerIndex, in e, death: false);
                    break;
            }
        }

        /// `death` is what turns the slot's doll into a corpse, and the ORDER
        /// below is the contract: the visual reacts first (Death01 crossfade)
        /// while the doll is still the slot's, then the doll leaves the slot for
        /// good. The transform is left exactly where the last frame put it —
        /// this event arrives in the `Update` phase, so that write is the
        /// previous frame's `SyncPlayers`, within one render frame of the tick
        /// the player actually died on (`KillPlayer` never moves the body). The
        /// alternative — placing the corpse from the event — is wrong here:
        /// `SimEvent.Pos` for `PlayerDied` is "normally the BLOW's own origin —
        /// the attacking mob's or projectile's position" (that field's own doc),
        /// so a corpse built from it would stand at the muzzle that killed it.
        void DispatchToDoll(byte playerIndex, in SimEvent e, bool death)
        {
            if (playerIndex == ProjectileIds.NoOwner) return;
            if (!_activePlayers.TryGetValue(playerIndex, out PlayerView view)) return;
            view.Visual?.HandleEvent(in e, _gameFeel.OneShotCrossFadeSeconds);
            if (!death) return;
            _activePlayers.Remove(playerIndex);
            _corpses.Add(view);
            view.DetachAsCorpse();
        }

        /// `GameFeelDirector`'s seam into per-mob view state (Task 25 Interfaces)
        /// — looks up the live view for a `ProjectileHit` event's `EntityId` so
        /// the director can call `MobView.Flash`/`FreezePosition` on the actual
        /// hit mob without this class needing to know anything about hitstop
        /// itself (П-1: no hitstop-specific branching lives here).
        public bool TryGetMobView(int id, out MobView view) => _activeMobs.TryGetValue(id, out view);

        /// The same seam for a player SLOT (Stage 2 Task 45b) — how the muzzle
        /// flash, the shell casing and the aim ray reach the doll whose barrel
        /// they must come off (`app-fl3`/`app-e2n`/`app-60c`). Modelled on
        /// `TryGetMobView` above deliberately: one shape for "ask the registry
        /// for a live view", not a second mechanism.
        ///
        /// IT ANSWERS FOR LIVE DOLLS ONLY, and every "no" it gives is
        /// load-bearing rather than defensive:
        ///  - a shooter behind the fog has no doll here, so a `ShotHeard` —
        ///    which reaches Presentation as an ordinary `ProjectileFired` at a
        ///    position the server coarsened on purpose — has nothing to draw
        ///    from, and the cosmetics that used to give that shooter away stop
        ///    being drawn at all (F-3);
        ///  - a corpse is not in `_activePlayers` (class doc), so a dead
        ///    player's gun cannot flash;
        ///  - the opening frames of a match, before the first snapshot, answer
        ///    "no" for every slot.
        /// A caller must therefore treat the false as "nothing to show", never
        /// as "show it somewhere else".
        ///
        /// NOBODY MAY KEEP WHAT THIS HANDS BACK. The doll is pooled: the same
        /// instance serves a different slot after a retire/rent, so a consumer
        /// that cached one would eventually decorate a stranger. Ask again for
        /// every event and every frame — the lookup is a dictionary probe.
        public bool TryGetPlayerView(int slot, out PlayerView view)
            => _activePlayers.TryGetValue(slot, out view);

        /// One doll per LIVE player slot (Stage 2 Task 45a, spec §3.12; corpses
        /// are objects, not slots — class doc). Same shape as `SyncMobs` below —
        /// a new key snaps straight to `Curr`, a continuing one lerps
        /// `Prev`→`Curr` by `Alpha`, a key that drops out returns its view to
        /// the pool — except that the key is the ARRAY INDEX (the player's
        /// slot), not an entity Id, so `Prev` is looked up by index instead of
        /// scanned.
        ///
        /// THE LOCAL DOLL IS NO LONGER A SPECIAL CASE OF POSITIONING. It used to
        /// read `SimulationRunner.RenderPlayerWorldPos`; the lerp below is that
        /// same formula (`RenderPrev`/`RenderCurr`/`RenderAlpha`, П-7) evaluated
        /// per slot, and while the local player is alive it produces the same
        /// number, because `RenderSnapshot.Player` IS `Players[LocalPlayerIndex]`.
        /// The two part company once that player dies — `RenderPlayerWorldPos`
        /// keeps evaluating a slot nothing is drawn from any more, and on a
        /// networked client that slot stops being written at all
        /// (`NetworkSimBackend.ApplyOwnPlayer` returns on `!_hasOwnSample`), so
        /// it reads the origin. The corpse does not care: it was detached from
        /// its slot at death and is never positioned again.
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
                // ONE RULE, the same one a mob gets: in the frame or not. A slot
                // that is not alive has either never been taken, is behind the
                // fog, or has already handed its doll over to `_corpses` — and
                // in every one of those cases it may be reading
                // `default(PlayerState)`, i.e. the arena origin (class doc).
                if (!state.Alive) continue;
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
                //    table) — otherwise the doll is handed its own position,
                //    which collapses `aimDir` and so skips BOTH aim-driven
                //    writes in `PlayerVisual.Sync` (the idle turn-in and the
                //    spine/chest yaw). A standing doll therefore holds its
                //    last facing; a moving one still turns along its own
                //    displacement, which is the movement branch and is
                //    honest. Either beats turning to face the arena origin,
                //    which is where a zeroed `AimPoint` points.
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

            // The corpses' whole per-frame budget (fix-round 1): no state, no
            // position — only the Aim-layer fade-out the death pose needs (Б3)
            // and the pause gate, so a body freezes mid-Death01 with everything
            // else when the game is paused. `PlayerVisual.SyncCorpse`'s own doc.
            for (int i = 0; i < _corpses.Count; i++) _corpses[i].Visual?.SyncCorpse(in visualParams);
        }

        /// The previous render half's position for one player slot. Unlike the
        /// mob/projectile lookups below there is nothing to scan — the slot IS
        /// the index — but there IS something to refuse: a slot that was not
        /// alive in `prev` either never had a record there (`default`, i.e. the
        /// arena origin) or had already died, and lerping FROM the origin would
        /// streak the doll across the arena for one frame. Falling back to `Curr`
        /// snaps instead, which is what a freshly-visible player wants.
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

        /// A LIVE doll leaving the frame — never a corpse, which is no longer in
        /// `_activePlayers` at all and goes back to the pool only in `Clear`.
        void RetirePlayer(int slot)
        {
            if (!_activePlayers.TryGetValue(slot, out PlayerView view)) return;
            _activePlayers.Remove(slot);
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
