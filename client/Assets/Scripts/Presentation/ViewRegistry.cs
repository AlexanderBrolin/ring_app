using System.Collections.Generic;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Sole owner of `PlayerView`/`MobView`/`ProjectileView` lifecycle — and,
    /// since Stage 3 Task 31, of `PickupView`/`ContainerView` too (П-1):
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
    ///    ProjectileExpired and — Stage 2 Task 45c — ProjectileHitPlayer for
    ///    projectiles, the last one off `SecondaryEntityId` because its
    ///    `EntityId` is the victim's slot; NOT ProjectileHit, which retires
    ///    nothing at all — since app-88jb Т11 (Ruling 48) that kind instead
    ///    forwards the hit event's direction to a STILL-LIVE mob's `Visual`,
    ///    see that method's own doc for the whole reasoning, fuller than the
    ///    one this paragraph used to give), ahead of that frame's `LateUpdate`
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
    /// там и лежит"). A slot gets a live doll for exactly as long as the frame
    /// says the slot is KNOWN AND ALIVE; when the frame says known and NOT
    /// alive, the doll leaves `_activePlayers` for `_corpses`, freeing the slot,
    /// and from that moment it is never `Sync`ed and never repositioned again.
    /// That is what makes it a corpse — the snapshot can no longer move it.
    ///
    /// Corpses go back to the pool in `Clear` (a new match), which is what
    /// client/CLAUDE.md's "трупы не исчезают до конца матча" asks for, and in
    /// nothing else that is expected to run — `IntoCorpse` carries one leak
    /// guard that would also pool one, and its own doc says why it cannot fire
    /// (fix-round 1: this sentence used to say "only in `Clear`" flat, and a
    /// reader who found that branch would have stopped believing the doc). The
    /// emission is killed at detach (owner decision): a body that keeps glowing
    /// misreports who is still standing, and it does so exactly when the cost of
    /// the mistake is highest.
    ///
    /// THE FRAME DECIDES, NOT THE EVENT — Stage 2 Task 47a, and the paragraph
    /// this replaces recorded exactly why it had to (bd `app-2rf`, P1). THE
    /// PICTURE LEADS THE EVENTS BY A TICK, by the networked backend's own
    /// construction: `NetworkSimBackend.ResolveRenderPair` copies the snapshot
    /// at `renderTick + 1` into `Curr`, while `ClientEventQueue.TryDequeue`
    /// refuses to hand out any event whose tick is still ahead of `renderTick`.
    /// So the frame that first shows the victim's slot as not-alive runs a whole
    /// render tick BEFORE `PlayerDied` is due, and while the corpse waited on
    /// that event no death on that backend left a body at all: the diff had
    /// already retired the doll into the pool, and the event found no entry in
    /// `_activePlayers` to detach. Since Task 47a the corpse is made where the
    /// FRAME says one belongs — `SyncPlayers`/`EnsureCorpse` — so the race
    /// stopped deciding anything. The event still has work: on a backend where
    /// it arrives FIRST (the local one) it is what makes the body, and either
    /// way exactly one of the two plays Death01, whichever got there first.
    ///
    /// TELLING A BODY FROM AN ABSENCE IS THE WHOLE OF WHY THAT IS POSSIBLE.
    /// Before `RenderSnapshot.PlayerKnown` a slot whose record simply stopped
    /// arriving read `default(PlayerState)` — "not alive, at the origin",
    /// `NetworkSimBackend.BeginSlot`'s own doc — so "not alive" could not be
    /// acted on: a live player who walked behind the fog would have been turned
    /// into a corpse, which is a lie about who is still fighting. The flag
    /// separates the two, and only the pair (known, not alive) makes a body.
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
    /// ON THE LOCAL BACKEND THE TWO FACTS ARRIVE IN THE OTHER ORDER, because
    /// there the tick's events are flushed in the same `Update` that produced
    /// the tick and `Curr` IS that tick — `PlayerDied` therefore reaches the
    /// doll before `LateUpdate`'s diff can notice the slot went quiet. Nothing
    /// here branches on the backend, and nothing needs to: the corpse is made
    /// by whichever fact comes first and the other one finds it already made
    /// (`DispatchToDoll` and `EnsureCorpse` each check). Both orders end with
    /// the same body in the same place, playing the same clip — which is the
    /// property that matters, since packet loss can put a networked client into
    /// the local backend's order for one death and back for the next.
    /// What a frame says to DRAW for one player slot (playtest В1 round two, bd
    /// `app-1kei`) — three answers, because "not alive" is three situations and
    /// the code that read it as one drew a body for all of them.
    ///
    /// The same remedy `AimCast` is (Т33c) and for the same defect class
    /// (lesson 399/401): two facts collapsed into one condition break on a
    /// playtest, not in the tests.
    public enum PlayerSlotPicture : byte
    {
        /// A live collector — rent a doll and sync it.
        Doll,

        /// A body. The slot is known, not alive, and did not walk out.
        Body,

        /// Nothing to draw here: the collector left through a portal or the
        /// gate. The spec is explicit that this is NOT a death — the body is
        /// TAKEN AWAY, and unlike a death it leaves no corpse and nothing to
        /// loot (§3.5) — and the simulation has always obeyed it; only the
        /// picture did not.
        Gone,
    }

    [DefaultExecutionOrder(-10)]
    public sealed class ViewRegistry : MonoBehaviour
    {
        /// WHAT TO DRAW FOR A SLOT, as a pure function of the three facts a
        /// frame states about it — so the rule can be tested at all. A
        /// `MonoBehaviour`'s per-frame loop is unreachable from EditMode, and
        /// this rule broke in exactly the way an untested rule breaks: the
        /// loop asked `!Alive` and drew a corpse, and a collector who had just
        /// won the raid played the death clip (owner's В1 playtest, round two).
        /// Same precedent as `InventoryWindowController.WindowMustClose`
        /// (Т33a) and `Core.ExitRules.IsOpen` (Т33).
        ///
        /// `known` COMES FIRST because it is a fact about the FRAME and the
        /// other two are facts about the player: a slot this frame says nothing
        /// about may be reading `default(PlayerState)`, so neither of the other
        /// arguments means anything until it is true.
        ///
        /// `extracted` BEATS `alive` IF THEY EVER DISAGREE, though the pair
        /// carries the invariant `!(Alive && Extracted)` (SimulationWorld's own
        /// hash doc, pinned by `ResultsTests`): the masks arrive as two
        /// independent bytes off the wire and a hostile or out-of-sync sender
        /// can set both bits, and of the two readings "he walked out" is the
        /// one that draws nothing — a decoder that never throws (Р82) must not
        /// be given a way to make this layer rent a doll for a seat that is out
        /// of the raid.
        public static PlayerSlotPicture PictureFor(bool known, bool alive, bool extracted)
        {
            if (!known) return PlayerSlotPicture.Gone;
            if (extracted) return PlayerSlotPicture.Gone;
            return alive ? PlayerSlotPicture.Doll : PlayerSlotPicture.Body;
        }

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
        // Stage 3 Task 31 (spec Р251/§3.11, owner decision R-192 — the
        // acceptance condition of milestone В1): Elite and the Director stop
        // borrowing the Gunner's prefab. Until this task every branch below
        // was a two-way ternary, so the arena's boss rendered as a rank-and-
        // file Gunner at a rank-and-file Gunner's size.
        [SerializeField] MobView _elitePrefab;
        [SerializeField] MobView _directorPrefab;
        [SerializeField] ProjectileView _projectilePrefab;
        // Stage 3 Task 31 (spec §3.11): the raid's own furniture. One prefab
        // per DRAWN container kind — `MobCorpse`/`PlayerCorpse` share the
        // marker, because their body is already on the floor (`ContainerView`'s
        // own doc).
        [SerializeField] PickupView _pickupPrefab;
        [SerializeField] ContainerView _crateContainerPrefab;
        [SerializeField] ContainerView _cacheContainerPrefab;
        [SerializeField] ContainerView _groundContainerPrefab;
        [SerializeField] ContainerView _corpseMarkerPrefab;

        Dictionary<int, PlayerView> _activePlayers;
        Dictionary<int, MobView> _activeMobs;
        Dictionary<int, ProjectileView> _activeProjectiles;
        Dictionary<int, PickupView> _activePickups;
        Dictionary<int, ContainerView> _activeContainers;
        Stack<PlayerView> _playerPool;
        Stack<MobView> _chaserPool;
        Stack<MobView> _gunnerPool;
        Stack<MobView> _elitePool;
        Stack<MobView> _directorPool;
        Stack<ProjectileView> _projectilePool;
        Stack<PickupView> _pickupPool;
        Stack<ContainerView> _cratePool;
        Stack<ContainerView> _cachePool;
        Stack<ContainerView> _groundPool;
        Stack<ContainerView> _corpseMarkerPool;

        // Stage 2 Task 45a fix-round 1: the detached dolls (class doc). Not a
        // pool — `Clear` hands them back to `_playerPool` and nothing else
        // touches them.
        //
        // KEYED BY SLOT SINCE TASK 47a, where Task 45a left a flat list. A
        // corpse still owns no slot in the sense that mattered then — the slot
        // is free for a live doll again and nothing here positions a body — but
        // the two facts that make a corpse (the frame and the `PlayerDied`
        // event) can arrive in either order, so each has to be able to ask
        // whether the other one has already been acted on. Without the key that
        // question has no answer and the second fact makes a SECOND body.
        Dictionary<int, PlayerView> _corpses;

        // Stage 2 Task 47c: the slots whose doll is being HELD through its
        // fade-out — still in `_activePlayers` (it is still that slot's doll and
        // still returns to the pool through `RetirePlayer`), but no longer the
        // slot's LIVE doll in the sense the rest of this class means. The
        // distinction is the same one `_corpses` draws and it earns its keep at
        // three read sites, each of which would otherwise draw a fact at a place
        // that has stopped being true: `TryGetPlayerView`, `DispatchToDoll` and
        // `EnsureCorpse`. Every one of the three restores exactly the behavior
        // that predates this task, where the doll was already in the pool by
        // then and each of those lookups simply found nothing.
        //
        // THE SET IS NOT THE WHOLE RULE — fix-round 1 counted the readers and
        // found a fourth, which asks PHYSICS rather than this class: the aim
        // raycast (`AimProvider.TryAimProxy`) walks up from a collider it struck
        // and never consults any container here, so no membership test could
        // have refused it. That one is closed on the doll itself, by switching
        // its proxies off for the length of the hold (`PlayerView.SetAimable`).
        // The set therefore says who is held; whether a given reader is covered
        // is answered per reader — see `HoldFadingDoll`'s enumeration.
        HashSet<int> _fadingPlayerSlots;

        // Per-frame scratch buffers, cleared and reused every call — no allocation
        // once warmed up.
        HashSet<int> _seenPlayerSlots;
        HashSet<int> _seenMobIds;
        HashSet<int> _seenProjectileIds;
        HashSet<int> _seenPickupIds;
        HashSet<int> _seenContainerIds;
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
            _corpses = new Dictionary<int, PlayerView>(playerCap);
            _seenPlayerSlots = new HashSet<int>(playerCap);
            _fadingPlayerSlots = new HashSet<int>(playerCap);
            _activeMobs = new Dictionary<int, MobView>(mobCap);
            _activeProjectiles = new Dictionary<int, ProjectileView>(projCap);
            // Every pool capped at mobCap (Б6 — not split by archetype ratio):
            // a match's archetype mix can vary, so each pool is sized as if the
            // whole cap were one archetype rather than guessing a split. Task 31
            // adds two more pools on the same rule rather than inventing a
            // smaller number for Elite and the Director — a `Stack` of that
            // capacity is one array of references, and guessing a split is the
            // thing Б6 refused.
            _chaserPool = new Stack<MobView>(mobCap);
            _gunnerPool = new Stack<MobView>(mobCap);
            _elitePool = new Stack<MobView>(mobCap);
            _directorPool = new Stack<MobView>(mobCap);
            _projectilePool = new Stack<ProjectileView>(projCap);
            _seenMobIds = new HashSet<int>(mobCap);
            _seenProjectileIds = new HashSet<int>(projCap);

            // Stage 3 Task 31. Pickups are capped per match by
            // `Arena.MaxPickups` and containers by `Arena.MaxContainers`, the
            // same "size the pool from the simulation's own ceiling" rule the
            // three pools above already follow. The four CONTAINER pools are
            // each sized at the whole container cap rather than at a guessed
            // share of it — Б6's rule, and the kind mix genuinely varies:
            // crates and caches are placed once at match start, ground drops
            // and corpses accumulate as the raid goes.
            int pickupCap = _arena.MaxPickups;
            int containerCap = _arena.MaxContainers;
            _activePickups = new Dictionary<int, PickupView>(pickupCap);
            _activeContainers = new Dictionary<int, ContainerView>(containerCap);
            _pickupPool = new Stack<PickupView>(pickupCap);
            _cratePool = new Stack<ContainerView>(containerCap);
            _cachePool = new Stack<ContainerView>(containerCap);
            _groundPool = new Stack<ContainerView>(containerCap);
            _corpseMarkerPool = new Stack<ContainerView>(containerCap);
            _seenPickupIds = new HashSet<int>(pickupCap);
            _seenContainerIds = new HashSet<int>(containerCap);

            _staleIdsScratch = new List<int>(
                math.max(math.max(mobCap, projCap), math.max(pickupCap, containerCap)));
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
            // 1, the corpses. This is the only place a corpse returns to the
            // pool on any path that runs (`IntoCorpse`'s leak guard is the
            // other one, and unreachable — its own doc): it is what "трупы не
            // исчезают до конца матча" (client/CLAUDE.md) means mechanically,
            // and what keeps a fresh match from opening with the previous one's
            // body on the floor.
            foreach (KeyValuePair<int, PlayerView> kv in _activePlayers)
            {
                kv.Value.gameObject.SetActive(false);
                _playerPool.Push(kv.Value);
            }
            _activePlayers.Clear();
            foreach (KeyValuePair<int, PlayerView> kv in _corpses)
            {
                kv.Value.gameObject.SetActive(false);
                _playerPool.Push(kv.Value);
            }
            _corpses.Clear();
            // Stage 2 Task 47c: both loops above have already pooled whatever
            // dolls were held mid-fade, so the marks they carried describe
            // nothing any more.
            _fadingPlayerSlots.Clear();

            foreach (KeyValuePair<int, MobView> kv in _activeMobs)
            {
                kv.Value.gameObject.SetActive(false);
                // Type is set by Bind before a view ever reaches _activeMobs
                // (the only path in — see RentMob/SyncMobs), so it's always
                // valid here (Б6).
                PoolFor(kv.Value.Type).Push(kv.Value);
            }
            _activeMobs.Clear();

            foreach (KeyValuePair<int, ProjectileView> kv in _activeProjectiles)
            {
                kv.Value.gameObject.SetActive(false);
                _projectilePool.Push(kv.Value);
            }
            _activeProjectiles.Clear();

            // Stage 3 Task 31 (spec Р291, and Т35's reset list names this
            // explicitly): the pickups and containers go back too. A restart
            // hands out entity ids from 1 again, so a cell or a crate left
            // active here would both leak and collide with a fresh id.
            foreach (KeyValuePair<int, PickupView> kv in _activePickups)
            {
                kv.Value.gameObject.SetActive(false);
                _pickupPool.Push(kv.Value);
            }
            _activePickups.Clear();

            foreach (KeyValuePair<int, ContainerView> kv in _activeContainers)
            {
                kv.Value.gameObject.SetActive(false);
                // Kind is set by Bind before a view ever reaches
                // _activeContainers (the only path in — see RentContainer/
                // SyncContainers), so it is always valid here.
                ContainerPoolFor(kv.Value.Kind).Push(kv.Value);
            }
            _activeContainers.Clear();
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
            SyncPickups();
            SyncContainers();
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out) — retirement for most kinds (see class doc), plus, since
        /// app-88jb Т11 (Ruling 48), one branch that retires nothing at all.
        ///
        /// TWO KINDS PUT THE ROUND IN `EntityId` AND ONE PUTS IT ELSEWHERE.
        /// `ProjectileBlocked`/`ProjectileExpired` name the round directly, so
        /// they retire off `EntityId`. `ProjectileHitPlayer` (Stage 2 Task 45c)
        /// spends `EntityId` on the VICTIM's player slot and carries the round's
        /// own id in `SecondaryEntityId` instead (`SimEvent.SecondaryEntityId`'s
        /// own doc) — so it retires off THAT field, and retiring it off
        /// `EntityId` would delete whichever projectile view happens to share a
        /// number with a player slot.
        ///
        /// `ProjectileHit` USED TO stay wholly absent, and this paragraph used
        /// to explain why: the round's id used to be missing from the event
        /// (Stage 2 Task 28 fixed that), and even after it wasn't, nothing
        /// needed it here — this whole method is an early, explicit version of
        /// a retirement the next `LateUpdate` diff performs anyway (class doc),
        /// so a THIRD retiring branch would only have duplicated that diff.
        /// THAT REASONING STILL HOLDS FOR RETIREMENT, and is why the branch
        /// below does none: app-88jb Т11 (Ruling 48, coordinator) needed
        /// `EntityId` for something this method had never done for any kind
        /// before — reaching a STILL-LIVE mob rather than retiring one. Body
        /// tilt (`MobVisual.Sync`, Ruling 46/47) reads its magnitude straight
        /// off the authoritative `MobState.Tilt`, but the axis lives only on
        /// this event's `HitDir` (`MobState.Tilt`'s own doc: the field is a
        /// signed scalar with no direction of its own), and `_activeMobs` is
        /// the one place in Presentation that knows `id -> MobView` at all
        /// (class doc). So the branch below is a forward, not a retirement: it
        /// looks the mob up by `EntityId` and hands its `Visual` the hit
        /// direction. The lookup is not a guard against a race — `ProjectileHit`
        /// is always emitted before the `DamageMob` call that can end in
        /// `MobDied` for the very same blow (`ProjectileSystem.cs`'s `HitMob`
        /// branch, the emit ahead of the `DamageMob` call in program order),
        /// and `MobDied` is what retires a `MobView` here — so offline this
        /// lookup practically always finds its mob, killing blow included. It
        /// is there for the OTHER side, WHICH SINCE app-88jb Т31 IS NO LONGER
        /// A NO-OP. This paragraph used to end by saying that on a networked
        /// client `EntityId` is always the wire's safe zero for this kind, so
        /// the branch never found anything — `_activeMobs` holds no key 0,
        /// entity ids start at 1. Т31 widened `ProjectileEnded` to carry the
        /// VICTIM's id beside the round's, and `ClientEventDecoder` now puts
        /// it in exactly this field, so the lookup finds its mob over the wire
        /// too and the axis reaches the visual on both paths. The magnitude
        /// arrives with it: `NetworkSimBackend` synthesizes `MobState.Tilt`
        /// into the published pair through `MobTiltIntegrator`, so the
        /// authoritative-offline / zero-over-the-wire boundary this paragraph
        /// and `MobVisual`'s class doc used to draw is gone, and nothing in
        /// this file had to change for it.
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
                case SimEventKind.ProjectileHitPlayer:
                    RetireProjectile(e.SecondaryEntityId);
                    break;
                case SimEventKind.ProjectileHit:
                    // Ruling 48 (app-88jb Т11) — see this method's own doc for
                    // the full reasoning. `TryGetValue` rather than an indexer:
                    // a miss is silent on both paths and worth logging on
                    // neither. It used to be the RULE over the wire, where the
                    // event named no victim at all; since Т31 it is the same
                    // ordinary residue as offline — a mob this client has not
                    // been told about, or one whose view was already retired.
                    if (_activeMobs.TryGetValue(e.EntityId, out MobView hitMobView))
                        hitMobView.Visual?.SetHitDir(e.HitDir);
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
        /// THREE kinds are routed here as of bd `app-9m57`, which added the
        /// third — this paragraph used to open "Only the two kinds the doll
        /// reacts to are routed", and app-9m57 is what cancels that count,
        /// not the rule underneath it: the index each kind names is still a
        /// per-KIND convention off `SimEvent.PlayerIndex`'s own doc, not one
        /// rule shared by all three:
        ///  - `PlayerDied` — VICTIM ("PlayerDamaged/PlayerDied (mirrors
        ///    EntityId's convention for those two kinds)"), i.e. the player who
        ///    died, which is the doll that must play Death01. Taking the
        ///    ATTACKER here would kill the shooter's doll instead;
        ///  - `ProjectileFired` — ACTOR ("the five 'own-action' kinds
        ///    ProjectileFired, … / SpawnProjectile's ownerIndex"), i.e. the
        ///    shooter, which is the doll that must replay Pistol_Shoot. A mob's
        ///    round carries `ProjectileIds.NoOwner` and names no doll at all;
        ///  - `PlayerDamaged` — VICTIM, the SAME convention and the same
        ///    quoted doc line as `PlayerDied` above, i.e. the player who took
        ///    the blow: `PlayerVisual.SetHitDir` needs `e.HitDir` to give the
        ///    body's tilt (`PlayerState.Tilt`, authoritative magnitude, no
        ///    direction of its own) an axis to tip around. Taking the
        ///    ATTACKER here would tilt the shooter instead of the one hit.
        /// An event naming a slot with no live doll is ignored, which on the
        /// networked backend is the ORDINARY `PlayerDied` and not every one of
        /// them (Stage 2 Task 47a fix-round 1 — this paragraph used to say
        /// "every", which the class doc two hundred lines up already
        /// contradicts). The two orders both happen, and which one runs is
        /// decided by whether one packet arrived:
        ///  - ORDINARY. The frame at render tick R shows the snapshot at R + 1
        ///    (`NetworkSimBackend.ResolveRenderPair`) while the queue refuses
        ///    any event past R (`ClientEventQueue.TryDequeue`), so a death on
        ///    tick T is already in the picture at R = T − 1, a whole render tick
        ///    before the event is due at R = T. The body has been laid down by
        ///    the frame and this path finds nothing to detach;
        ///  - THE SNAPSHOT AT T IS LOST — ordinary in itself at the 5% loss
        ///    every playtest build must survive. At R = T − 1 the pair collapses
        ///    onto T − 1 with the victim still standing, so the picture does not
        ///    carry the death at all; at R = T the same `Advance` resolves the
        ///    pair to T + 1 AND drains the event, and the drain happens in
        ///    `Update` while `SyncPlayers` runs in `LateUpdate`. The event
        ///    therefore reaches a doll that is still live, and THIS path is what
        ///    makes the body.
        /// Ignoring the ordinary one is the right answer rather than the defect
        /// it was — before Task 47a the corpse waited on this event and the
        /// networked backend therefore had no corpses at all (`app-2rf`) — and
        /// keeping this path is not redundancy: it is the only maker of a body
        /// on the frames where the picture never carried the death.
        /// `PlayerDamaged` shares that same "no live doll, no-op" refusal for
        /// a different reason than the two kinds above (bd `app-9m57`): it
        /// makes no corpse and plays no one-shot, so a missed doll costs
        /// nothing but the axis for a hit-tilt this same frame's absent
        /// `Sync` was never going to draw anyway.
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
                case SimEventKind.PlayerDamaged:
                    // bd `app-9m57`: VICTIM convention (this method's own doc
                    // above), same DispatchToDoll → PlayerVisual.HandleEvent
                    // path the other two kinds already use — one dispatch
                    // mechanism, not a second lookup grown next to it.
                    // `death: false` — a hit is not a death, and `IntoCorpse`
                    // must not run off this kind.
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
        ///
        /// A SLOT WITH NO LIVE DOLL IS NOT A FAILURE ANY MORE (Stage 2 Task
        /// 47a). It is the ordinary networked case — the frame reached the death
        /// a render tick earlier and `EnsureCorpse` has already laid the body
        /// down and played its clip — and it is also what a duplicate
        /// `PlayerDied` looks like. Both mean the same thing here: the work is
        /// done, and repeating it would restart Death01 on a body that has
        /// already fallen. What must NOT happen is making a second corpse for
        /// the slot, which is why the body is registered under its slot and not
        /// merely appended (see `_corpses`).
        /// A DOLL FADING OUT RECEIVES NOTHING (Stage 2 Task 47c). Its slot has
        /// stopped being described by the frame, so it stands where it was last
        /// SEEN: replaying `Pistol_Shoot` on it draws a shot at a place that is
        /// no longer true, and — the heavier half — turning it into a corpse
        /// would lay the body down at the last-seen spot instead of where the
        /// frame says the player fell, breaking the owner's own "где упал, там и
        /// лежит" — quoted in full, with its recorded address, in this class's
        /// own doc (Stage 2 Task 45a fix-round 1), which is the only place that
        /// wording comes from: it is NOT a line of `client/CLAUDE.md`, whose
        /// rule about bodies is the neighboring "трупы не исчезают до конца
        /// матча". The body is made by the FRAME instead, in `EnsureCorpse`,
        /// where a position actually exists; if the frame never carries that
        /// slot again, no body is drawn at all, which is right — this client
        /// never saw it. Before this task the doll was already pooled by then
        /// and the lookup below simply failed, so this restores that exactly.
        void DispatchToDoll(byte playerIndex, in SimEvent e, bool death)
        {
            if (playerIndex == ProjectileIds.NoOwner) return;
            if (_fadingPlayerSlots.Contains(playerIndex)) return;
            if (!_activePlayers.TryGetValue(playerIndex, out PlayerView view)) return;
            view.Visual?.HandleEvent(in e, _gameFeel.OneShotCrossFadeSeconds);
            if (!death) return;
            IntoCorpse(playerIndex, view);
        }

        /// The bookkeeping half of becoming a corpse, shared by the two facts
        /// that can trigger it (Stage 2 Task 47a): the `PlayerDied` event above
        /// and the frame itself in `EnsureCorpse` below. The doll leaves the
        /// slot map, is filed under that slot among the corpses, and stops
        /// glowing and being aimable at (`PlayerView.DetachAsCorpse`). The doll
        /// this method files is NOT pooled — `Clear` is what pools a body. The
        /// one exception is in this very method, and is the subject of the next
        /// paragraph rather than a hole in this one (fix-round 1).
        ///
        /// THE OLDER-BODY BRANCH IS A LEAK GUARD, not a case that is expected to
        /// run. A slot can only be occupied again after `SimulationRunner`
        /// raises `WorldRestarted`, which is wired straight to `Clear` on both
        /// backends (the networked one raises it from `MatchRestarted`), so by
        /// the time a slot can die twice its first body is already back in the
        /// pool. If that ever stops being true, a body dropped from every
        /// container here would be an object nothing could ever switch off
        /// again — a permanent stranger on the floor — so the older one is
        /// pooled rather than orphaned, and the newer one takes the key.
        void IntoCorpse(int slot, PlayerView view)
        {
            _activePlayers.Remove(slot);
            if (_corpses.TryGetValue(slot, out PlayerView older) && older != view)
            {
                older.gameObject.SetActive(false);
                _playerPool.Push(older);
            }
            _corpses[slot] = view;
            view.DetachAsCorpse();
        }

        /// The frame's half: this slot is KNOWN and NOT ALIVE, so a body belongs
        /// here (Stage 2 Task 47a, bd `app-2rf`). Three states of the slot reach
        /// this method — the two containers give four combinations, and the
        /// fourth is dealt with at the end of this doc rather than below:
        ///  - a body is already filed under the slot — nothing to do. This is
        ///    every frame after the first one that saw the death, and it is also
        ///    how the local backend's order comes out: `PlayerDied` made the
        ///    corpse in `Update`, and `LateUpdate` finds it here;
        ///  - the slot still has a live doll — it is dying now. The doll is
        ///    detached where it stands, which is where the last frame put it,
        ///    and crossfades into Death01;
        ///  - the slot has neither. A body is RENTED and placed at the position
        ///    the frame carries. This is the client who never saw the death —
        ///    "и его видят ВСЕ, а не только тот, кто видел смерть" (owner,
        ///    2026-08-10), the half of `app-2rf` its own title is about: the
        ///    server replicates a corpse as ordinary state (`SnapshotAssembler`
        ///    has no liveness guard on the player record), so walking up to a
        ///    body is exactly the case where the record starts arriving with the
        ///    Alive bit clear and this client has nothing standing to detach.
        ///    Without this branch that player still sees an empty floor.
        ///
        /// THE FOURTH COMBINATION — a body already filed under the slot AND a
        /// live doll standing in it — takes the first line's early return, and
        /// that is only safe because it cannot happen (fix-round 1). It would
        /// mean the slot died, was reoccupied and died again inside one match;
        /// a slot can only be reoccupied after `WorldRestarted`, which is wired
        /// straight to `Clear` on both backends, and `Clear` empties both
        /// containers at once. This is the same unreachability `IntoCorpse`'s
        /// own older-body branch is a guard against, stated in both places so
        /// the two cannot drift apart. If it ever DOES become reachable, the
        /// early return is where it bites: the live doll would not be filed
        /// under `_seenPlayerSlots`, and the retirement pass would pool it with
        /// no body left behind — so the return would have to grow the
        /// `_activePlayers` half of the question rather than the guard alone.
        ///
        /// A RENTED BODY IS ALSO FACED, and only a rented one. A doll that fell
        /// on its feet carries the facing its last `Sync` integrated and is
        /// left alone; a rented one has just been reset to the model's rest
        /// direction by `Bind`, and without a facing every body found after the
        /// fact would lie the same way (fix-round 1, both review axes). The
        /// heading comes from the same record the position did — see
        /// `PlayerVisual.FaceAimInstantly`.
        ///
        /// THE TWO CLIENTS STILL DO NOT AGREE TO THE DEGREE, and the residual is
        /// named here rather than promised away. A witness's body keeps a facing
        /// the RENDER integrated — rate-limited toward the target by
        /// `VisualTurnDegPerSec`/`IdleAimTurnDegPerSec` and, while moving,
        /// aimed along the doll's own displacement rather than along the aim at
        /// all — while a body found after the fact snaps to the SIMULATION's aim
        /// heading. So the two agree exactly only for a victim that died
        /// standing still with its facing already settled; a victim killed
        /// mid-run can lie up to the angle between its run and its aim apart on
        /// the two screens, plus one wire quantization step of `Quantize.Dir`
        /// (1.40625 deg). What is fixed is the part that was a lie: a body now
        /// lies along a direction that player really faced, instead of along the
        /// prefab's.
        ///
        /// NOTHING HERE DECIDES A DEATH (CR 3). The Alive bit is the server's,
        /// arriving in the frame; this method draws what it is told, and on the
        /// local backend the same bit is the world's own.
        void EnsureCorpse(int slot, in PlayerState state, bool local)
        {
            if (_corpses.ContainsKey(slot)) return;

            // Stage 2 Task 47c: a doll being held through its fade is standing
            // where its slot was last SEEN, not where this frame says the body
            // is — so it goes back to the pool here and the branch below rents
            // one and places it properly. Detaching the held doll in place
            // instead would lay the body down as far from the truth as the
            // player travelled between the last sighting and the death, which
            // is up to the whole fade budget's worth of running. This is also
            // exactly what happened before the hold existed: the doll had
            // already been retired, so `standing` read false and a body was
            // rented. `RetirePlayer` clears the mark as it pools.
            if (_fadingPlayerSlots.Contains(slot)) RetirePlayer(slot);

            bool standing = _activePlayers.TryGetValue(slot, out PlayerView view);
            if (!standing)
            {
                view = RentPlayer();
                // Position before Bind, the same canonical order the live
                // branch uses — `PlayerVisual`'s own speed read is a frame delta
                // of this very transform, and a body must not open its life
                // with a delta from wherever the pooled doll last stood.
                view.transform.position = SimSpace.ToWorld(state.Pos);
                view.Bind(local);
                view.Visual?.Bind(in state, _gameFeel.PlayerVisualScale);
            }
            IntoCorpse(slot, view);
            view.Visual?.PlayDeath(standing, _gameFeel.OneShotCrossFadeSeconds);
            // Last, after the clip has landed its pose: `PlayDeath`'s body
            // branch drives the Animator (`Play` + `Update(0f)`), and the
            // facing is a write on the visual's own transform, so stating it
            // afterwards is the same order `Sync` keeps for a live doll.
            if (!standing) view.Visual?.FaceAimInstantly(in state, _gameFeel.PlayerYawOffsetDeg);
        }

        /// `GameFeelDirector`'s seam into per-mob view state (Task 25 Interfaces)
        /// — looks up the live view for a `ProjectileHit` event's `EntityId` so
        /// the director can call `MobView.Flash` on the actual hit mob without
        /// this class needing to know anything about the hit-flash itself.
        public bool TryGetMobView(int id, out MobView view) => _activeMobs.TryGetValue(id, out view);

        /// The same seam for a player SLOT (Stage 2 Task 45b) — how the muzzle
        /// flash, the shell casing and the aim ray reach the doll whose barrel
        /// they must come off (`app-fl3`/`app-e2n`/`app-60c`), and since Stage 2
        /// Task 45c how `GameFeelDirector` reaches the doll that was just hit.
        /// Modelled on `TryGetMobView` above deliberately: one shape for "ask
        /// the registry for a live view", not a second mechanism.
        ///
        /// THE HIT LOOKUP IS KEYED OFF `PlayerDamaged`, NOT OFF
        /// `ProjectileHitPlayer`, and the difference is not a preference
        /// (Task 45c fix-round 1). The round-ending kind carries the victim's
        /// slot only inside the simulation: on a networked client the decoder
        /// leaves that field at its default, and zero there is not "no victim"
        /// — it is seat 0, a real seat, so every hit would decorate whoever
        /// sits in it. `PlayerDamaged` carries the victim's slot on the wire,
        /// which is why the victim-addressed half of the feedback lives there.
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
        /// A DOLL FADING OUT ANSWERS "NO" TOO (Stage 2 Task 47c), which is the
        /// fourth load-bearing refusal and the one this task had to add rather
        /// than inherit. A held doll stands at the last position the picture
        /// ever showed for its slot, and two of the callers above spawn world
        /// props off its GUN: a `ShotHeard` — a shot from someone this client
        /// cannot see, arriving as an ordinary `ProjectileFired` at a
        /// deliberately coarsened position — would otherwise light a muzzle
        /// flash and throw brass at the exact spot that player was last seen
        /// standing, for as long as the fade lasts. Since bd `app-p7t`,
        /// `MuzzleFlashView.HandleEvent`'s own guard returns before a
        /// `ShotHeard` ever reaches this lookup, so the flash half of that
        /// clause is moot — but `PersistentPropsDirector.SpawnCasing` still
        /// calls this same method unconditionally for any player-owned shot,
        /// and this refusal remains the only thing keeping a `ShotHeard` from
        /// throwing brass at a stale spot. That is the F-3 defect in a
        /// narrower window, and the narrower window is not a defense. Before
        /// this task the doll was already pooled by then and this lookup found
        /// nothing; the line below is what keeps that true.
        public bool TryGetPlayerView(int slot, out PlayerView view)
        {
            // `view` is cleared rather than left holding the doll on the
            // refusal: a `false` from a Try- method must not hand back an
            // object, or a caller that reads the out parameter first gets
            // exactly the doll this refusal exists to withhold.
            if (_fadingPlayerSlots.Contains(slot))
            {
                view = null;
                return false;
            }
            return _activePlayers.TryGetValue(slot, out view);
        }

        /// One doll per LIVE player slot (Stage 2 Task 45a, spec §3.12; corpses
        /// are objects, not slots — class doc). Same shape as `SyncMobs` below —
        /// a new key snaps straight to `Curr`, a continuing one lerps
        /// `Prev`→`Curr` by `Alpha`, a key that drops out returns its view to
        /// the pool — except that the key is the ARRAY INDEX (the player's
        /// slot), not an entity Id, so `Prev` is looked up by index instead of
        /// scanned.
        ///
        /// THREE READINGS OF A SLOT, NOT TWO (Stage 2 Task 47a): the frame is
        /// silent about it, the frame knows it and it is alive, or the frame
        /// knows it and it is not. The third is a corpse and the first is an
        /// absence, and telling them apart is the whole of `app-2rf` — see the
        /// class doc and `EnsureCorpse`.
        ///
        /// AND THE FIRST READING IS NO LONGER ONE OUTCOME (Stage 2 Task 47c, bd
        /// `app-wcy`). An absence used to retire the doll on the spot; now it
        /// asks `HoldFadingDoll` whether anything is still going out there, and
        /// only a "no" reaches the retirement pass. That is what turns a
        /// stranger stepping behind a wall from a pop into a freeze and a fade —
        /// and it is also why the fade could not be delivered by the seam alone:
        /// with the doll pooled in the same frame its slot went quiet, there
        /// was nothing left on screen for a fade to act on.
        ///
        /// ONE'S OWN BODY IS AMONG THEM AS OF STAGE 2 TASK 47b, and this method
        /// needed no branch for it — which was the point of fixing it where it
        /// was broken. The assembler used to leave a connection's own record out
        /// of its frame UNCONDITIONALLY, so the local seat was known only while
        /// `NetworkSimBackend.ApplyOwnPlayer` wrote it from prediction, and
        /// prediction ends at one's own death: the seat went from "known and
        /// alive" straight to "not known", never through the "known and dead"
        /// state the corpse branch below keys on, and no body was ever made for
        /// it. The server now sends a DEAD connection its own record (the
        /// owner's decision 2a, `SnapshotAssembler.WriteFrame`'s candidate
        /// phase), so the third reading arrives on the wire like anyone else's
        /// and `EnsureCorpse` lays the body down off it. Everyone ELSE saw that
        /// body correctly all along.
        ///
        /// WHAT IT USED TO COST WAS NOT AN EMPTY FLOOR — that is what this
        /// paragraph said before fix-round 1, and it understated the defect.
        /// `CameraRig` followed `RenderCurr.Player.Pos`, which is
        /// `Players[LocalPlayerIndex]`, and with nothing writing that seat
        /// `BeginSlot`'s `default(PlayerState)` read as the ARENA ORIGIN: the
        /// camera did not stay over the place of death looking at nothing, it
        /// smooth-damped away to the geometric center. Both halves of that are
        /// closed — the seat is written, and the camera follows
        /// `SimulationRunner.ObservedIndex` rather than the local seat at all.
        ///
        /// THE LOCAL DOLL IS NO LONGER A SPECIAL CASE OF POSITIONING. It used to
        /// read `SimulationRunner.RenderPlayerWorldPos`; the lerp below is that
        /// same formula (`RenderPrev`/`RenderCurr`/`RenderAlpha`, П-7) evaluated
        /// per slot, and while the local player is alive it produces the same
        /// number, because `RenderSnapshot.Player` IS `Players[LocalPlayerIndex]`.
        /// The two part company once that player dies — `RenderPlayerWorldPos`
        /// keeps evaluating a slot nothing is DRAWN from any more, while the
        /// slot itself goes on being written: on a networked client the server
        /// sends a dead connection its own record (the owner's decision 2a),
        /// `ReadPlayers` lays it down by index, and prediction stands aside for
        /// it because `NetworkSimBackend.ApplyOwnPlayer` is gated on the
        /// FRAME'S ROSTER MASK (`PlayerAliveInMatch`) rather than on the
        /// predicted pose outliving the player. So that expression reads the
        /// pose of the BODY from the tick of death onwards, not the origin —
        /// which is the whole difference this task made, and the sentence that
        /// used to stand here described the defect rather than the fix
        /// (fix-round 1, Ф-4). The corpse does not care either way: it was
        /// detached from its slot at death and is never positioned again.
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
                // ONE RULE, the same one a mob gets: in the frame or not — and
                // since Stage 2 Task 47a "in the frame" is a fact the frame
                // states rather than one inferred from `Alive`. A slot this
                // frame knows nothing about may be reading
                // `default(PlayerState)`, i.e. the arena origin (class doc), so
                // nothing may be drawn or positioned from it; since Stage 2
                // Task 47c its doll may nevertheless be KEPT, untouched and
                // dimming, until the fade behind `HoldFadingDoll` has run out —
                // and the retirement pass below is still what takes it away
                // when it has.
                // Playtest В1 round two (bd `app-1kei`): THREE answers, not two.
                // This loop used to ask `!state.Alive` and make a corpse of
                // whatever said yes, which drew a death for a collector who had
                // just walked out of the gate. `PictureFor` is where the three
                // cases are named and where they are tested.
                PlayerSlotPicture picture = PictureFor(
                    curr.PlayerKnown[i], state.Alive, curr.PlayerExtractedInMatch[i]);
                if (picture == PlayerSlotPicture.Gone)
                {
                    // Two ways to be gone, and only one of them holds a doll.
                    // Stage 2 Task 47c: a doll the frame has gone quiet about
                    // survives this frame's retirement pass while it is still
                    // fading out — being in `_seenPlayerSlots` is exactly what
                    // "do not retire me" means to the pass below. A collector
                    // who EXTRACTED is not that case: the frame is not quiet
                    // about him, it says outright that he left, so there is
                    // nothing to hold and the retirement pass below pools his
                    // doll on this very frame — which is what the spec's
                    // "the body is taken away" (§3.5) looks like from here.
                    if (!curr.PlayerKnown[i] && HoldFadingDoll(i, local)) _seenPlayerSlots.Add(i);
                    continue;
                }
                // A BODY, and it is the frame that says so — see `EnsureCorpse`.
                // The slot is deliberately left out of `_seenPlayerSlots`: a
                // corpse is not a live doll, and the pass below must not find
                // one to retire, which `EnsureCorpse` has already seen to by
                // taking it out of `_activePlayers`.
                if (picture == PlayerSlotPicture.Body)
                {
                    EnsureCorpse(i, in state, local);
                    continue;
                }
                _seenPlayerSlots.Add(i);
                // Stage 2 Task 47c: the frame carries this slot again, so
                // whatever was fading here is a live doll once more. The policy
                // has already zeroed its own progress off the fresh sighting;
                // this is the view-side half of the same fact, and fix-round 1
                // moved the whole of it into ONE call — the hold undoes more
                // than a mark, and the rest of it is invisible from here (see
                // `ResumeHeldDoll`). A slot that was never held returns from it
                // immediately, on the same probe the bare `Remove` used to cost.
                ResumeHeldDoll(i);

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
                // Stage 2 Task 47c. One's own doll never dims — see
                // `HoldFadingDoll` for the whole of why, and note that the
                // literal below is the exception itself rather than a
                // shortcut: a local doll is NOT uniformly black (the combo
                // window pulses on it and a hit flashes it), so multiplying it
                // by a remainder would be visible.
                float fadeRemaining = local ? 1f : 1f - _runner.PlayerFadeProgress(i);

                if (!_activePlayers.TryGetValue(i, out PlayerView view))
                {
                    view = RentPlayer();
                    // Position before Bind, same canonical order as the mob
                    // branch below — PlayerVisual's own speed read is a frame
                    // delta of this very transform.
                    view.transform.position = SimSpace.ToWorld(state.Pos);
                    view.Bind(local);
                    view.Visual?.Bind(in state, _gameFeel.PlayerVisualScale);
                    view.Sync(in state, linkHz, linkBoost, accent, fadeRemaining);
                    view.Visual?.Sync(in state, in visualParams);
                    _activePlayers.Add(i, view);
                    continue;
                }

                float2 prevPos = FindPlayerPrevPos(prev, i, state.Pos);
                view.transform.position = Vector3.Lerp(
                    SimSpace.ToWorld(prevPos), SimSpace.ToWorld(state.Pos), alpha);
                view.Sync(in state, linkHz, linkBoost, accent, fadeRemaining);
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
            foreach (KeyValuePair<int, PlayerView> kv in _corpses)
                kv.Value.Visual?.SyncCorpse(in visualParams);
        }

        /// A slot THIS FRAME SAYS NOTHING ABOUT, whose doll is still standing
        /// (Stage 2 Task 47c, bd `app-wcy`, spec §3.9 Р39/Р77): keep it where it
        /// is and let it dim, instead of pooling it the instant the records
        /// stop. Answers whether the doll must survive this frame's retirement
        /// pass. Before this task the branch was one `continue` and every
        /// stranger who stepped behind a wall was deleted between two frames —
        /// which reads as a network glitch rather than as fog of war, and which
        /// is what `app-wcy` was opened about.
        ///
        /// HELD MEANS NOT TOUCHED, NOT "SYNCED WITH ZEROES". A slot the frame is
        /// silent about reads `default(PlayerState)` — the arena origin, and not
        /// alive (`NetworkSimBackend.BeginSlot`'s own doc) — so BOTH of the
        /// writes the live path makes are refused here: the transform is not
        /// positioned (the doll stands where the last frame it appeared in left
        /// it, which is the "freezes" half of Р39/Р77), and neither `Sync` nor
        /// `PlayerVisual.Sync` is called, because each takes the state that does
        /// not exist. Only the emission is written, through the one member that
        /// dims what was ALREADY composed rather than composing it again
        /// (`PlayerView.FadeEmission`). What still moves is the Animator, which
        /// runs on its own and keeps playing whatever clip the doll was last
        /// put into — a residual, named rather than promised away, and a
        /// smaller lie than a doll that snaps to the origin.
        ///
        /// THREE THINGS ARE NEVER HELD, each for its own reason:
        ///  - ONE'S OWN DOLL. Own visibility is unconditional, and the branch
        ///    returns before it can reach the policy at all. It matters more
        ///    since Stage 2 Task 47b than it would have before: the server now
        ///    sends a DEAD connection its own record, so one's own slot feeds
        ///    the policy too and could genuinely report a fade — while the local
        ///    seat's silence has an entirely different cause (`ApplyOwnPlayer`
        ///    stops writing it once the roster says that player is down), and
        ///    holding on it would keep a doll standing over one's own corpse;
        ///  - A CORPSE. Bodies are not in `_activePlayers` at all (class doc),
        ///    so the lookup below simply does not find one — which is what keeps
        ///    "трупы не исчезают до конца матча" (client/CLAUDE.md) true through
        ///    this task: a body is not a doll whose records stopped, it is an
        ///    object that has left the slot system, and only `Clear` pools it;
        ///  - A MOB. Nothing registers mobs with the policy (the owner's
        ///    decision 3a leaves them to a task with numbers of its own), and
        ///    this method is only ever reached from the player loop.
        ///
        /// A HELD DOLL IS NOT THE SLOT'S LIVE DOLL, and that is ONE RULE WITH
        /// FOUR ENFORCEMENT POINTS rather than four special cases (fix-round 1
        /// counted them; the paragraph this replaces said three and named the
        /// three that go through this class). The doll keeps its entry in
        /// `_activePlayers` — it is still that slot's doll and still returns to
        /// the pool through `RetirePlayer` — and it is marked in
        /// `_fadingPlayerSlots`. Every reader that would otherwise treat "in
        /// `_activePlayers`" as "positioned by this frame":
        ///  - `TryGetPlayerView` refuses the mark, so no muzzle flash and no
        ///    brass are thrown at a vanished player's last known position;
        ///  - `DispatchToDoll` refuses it, so no event replays on a doll the
        ///    frame has stopped describing;
        ///  - `EnsureCorpse` retires the marked doll first, so a body is laid
        ///    down where the frame says the player fell rather than where they
        ///    were last SEEN;
        ///  - the AIM RAYCAST, which asks none of the above — it walks up from
        ///    whatever collider it struck (`AimProvider.TryAimProxy`) and drops
        ///    only this client's own doll — is refused ON THE DOLL, by switching
        ///    its `AimProxy_*` triggers off for the length of the hold
        ///    (`PlayerView.SetAimable`, its own doc for the consumers).
        /// All four restore exactly what happened before this task, when the
        /// doll was already pooled and every one of these lookups — the raycast
        /// included, since a pooled doll is inactive — found nothing.
        ///
        /// COMING BACK IS ITS OWN POINT, `ResumeHeldDoll` (fix-round 1; this
        /// paragraph used to claim it needed no code at all, and that was true
        /// only of the mark). What is free is the LIGHT: the policy zeroes a
        /// slot's fade the instant a fresh sighting arrives, so the live path's
        /// own `Sync` recomposes full brightness with no special case. What is
        /// not free is everything a fresh `Bind` would have reset and a
        /// continuing doll never sees — the proxies switched off above, and the
        /// pose anchor `PlayerVisual` keeps for its speed read. The picture
        /// still snaps rather than streaks, because `FindPlayerPrevPos` refuses
        /// a `prev` half that does not have the slot alive. So a doll that was
        /// half out comes back to full brightness where it stands and then
        /// resumes moving, in that order: the policy hears of the return at
        /// DECODE time while the picture reaches it `InterpBufferTicks` later,
        /// so the light returns first.
        bool HoldFadingDoll(int slot, bool local)
        {
            if (local) return false;
            if (!_activePlayers.TryGetValue(slot, out PlayerView view)) return false;
            if (!_runner.ShouldKeepPlayerDoll(slot)) return false;

            // Fix-round 1: `Add` answers whether this is the FIRST held frame,
            // so the proxy switch is thrown once per hold rather than re-thrown
            // every frame of it — the same shape `ResumeHeldDoll`'s `Remove`
            // uses at the other end.
            if (_fadingPlayerSlots.Add(slot)) view.SetAimable(false);
            view.FadeEmission(1f - _runner.PlayerFadeProgress(slot));
            return true;
        }

        /// THE OTHER END OF `HoldFadingDoll` (Stage 2 Task 47c fix-round 1):
        /// the frame carries this slot again, so its doll stops being held.
        /// One point for the whole transition, because the hold undoes more than
        /// the mark the live path above used to clear inline, and the rest of it
        /// is invisible from there.
        ///
        /// A RETURNING SLOT NEVER GOES THROUGH `Bind`, and that is what makes
        /// this a method rather than a tidy-up. Its doll never left
        /// `_activePlayers`, so the live path takes the CONTINUING branch —
        /// position, `Sync`, `PlayerVisual.Sync` — and every reset a freshly
        /// rented doll gets from `Bind` simply does not happen here. Two of
        /// those resets are load-bearing on this path:
        ///  - THE AIM PROXIES (`PlayerView.SetAimable`), switched off when the
        ///    hold began. Nothing else would ever switch them back on: `Bind` is
        ///    not called, so the doll would stay un-aimable for the rest of the
        ///    match — a player nobody can point at;
        ///  - THE SPEED REFERENCE (`PlayerVisual.ForgetPrevPos`). The line above
        ///    is about to snap the transform to wherever that player is NOW,
        ///    across everything they walked while the frame was silent, and the
        ///    `Sync` right after it reads its own displacement.
        /// The emission needs nothing of the sort: `Sync` recomposes it from
        /// this frame's own remainder, which is `1` for a slot the frame
        /// carries.
        ///
        /// THE LOOKUP IS HOW THE DOLL IS REACHED, NOT A GUARD. The mark is only
        /// ever set for a slot that has one (`HoldFadingDoll` checks first), and
        /// the two paths that take a doll away — `RetirePlayer` and `Clear` —
        /// clear the mark as they go, so the second `if` cannot fail.
        void ResumeHeldDoll(int slot)
        {
            if (!_fadingPlayerSlots.Remove(slot)) return;
            if (!_activePlayers.TryGetValue(slot, out PlayerView view)) return;
            view.SetAimable(true);
            view.Visual?.ForgetPrevPos();
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
            // Task Т10 (app-88jb) removed the on-hit frame pin that used to make
            // the two pairs diverge, but this class still goes through the
            // `Render*` facade rather than the live one, same as every other
            // interpolating view.
            RenderSnapshot curr = _runner.RenderCurr;
            RenderSnapshot prev = _runner.RenderPrev;
            float alpha = _runner.RenderAlpha;
            // L-13 fix-round: read once per frame (not baked into MobView as a
            // duplicated constant) so a balance re-tune of MobConfig.TelegraphSeconds
            // (hot-tweak, spec §3.9) is reflected in the telegraph pulse immediately —
            // the backend is `Ready` here, LateUpdate's own guard above already
            // returned early otherwise.
            //
            // PER ARCHETYPE SINCE TASK 31 (fix-round, Ф7 review B-I2), and the
            // defect it closes is the same one this task exists for, wearing a
            // scalar instead of a branch: the windup pulse normalizes
            // `StateTimer` against this number, and one number for every mob
            // meant the CHASER'S windup. The Director's is 1.1 s against the
            // Chaser's 0.35 (`MobDirectorConfig`/`MobChaserConfig`), so his
            // telegraph saturated at 32 % of the real windup and then sat at
            // "the blow lands NOW" for three quarters of a second while nothing
            // landed — a boss whose tell lies is worse than a boss with no
            // tell. The Elite escaped only by carrying the Chaser's own 0.35.
            // The whole config is copied once here (it is a large struct behind
            // a by-value property) and the archetype's field is picked per mob
            // inside the loop.
            SimConfig config = _runner.Config;
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
                    view.Visual?.Bind(in m, _gameFeel.VisualScaleFor(m.Type));
                    // Sync right away (Task 21 Bind/Sync contract) so a mob that's
                    // already mid-Telegraph the instant it becomes visible reads
                    // correctly this same frame, not one frame late.
                    view.Sync(in m, TelegraphSecondsFor(m.Type, in config), view == hoveredMob,
                        hoverAccent, hoverGlowBoost);
                    view.Visual?.Sync(in m, in visualParams);
                    _activeMobs.Add(m.Id, view);
                    continue;
                }

                // Task Т10 (app-88jb) removed the per-target position pin a
                // struck mob's own view used to hold while the rest of the
                // frame kept moving — every mob's transform is written here
                // every frame now, the same live-pair interpolation every
                // other mob/projectile/the player/the camera already used.
                float2 prevPos = FindMobPrevPos(prev, m.Id, m.Pos);
                Vector3 world = Vector3.Lerp(SimSpace.ToWorld(prevPos), SimSpace.ToWorld(m.Pos), alpha);
                view.transform.position = world + MobOffset;
                view.Sync(in m, TelegraphSecondsFor(m.Type, in config), view == hoveredMob,
                    hoverAccent, hoverGlowBoost);
                view.Visual?.Sync(in m, in visualParams);
            }

            _staleIdsScratch.Clear();
            // Stage 3 Т32б (bd `app-dut`): a mob the frame stopped mentioning is
            // not retired on the spot any more — it FADES, the way a player
            // doll has since Task 47c, and for the reason the issue records:
            // players froze and dimmed at the edge of sight while mobs
            // vanished instantly, so the picture was inconsistent and read as
            // a bug in the mobs rather than as the limit of the fog it is.
            //
            // THE ANSWER IS THE BACKEND'S, NOT THIS CLASS'S. How long a mob may
            // go unheard before it freezes, whether the fade may start at all,
            // and whether the whole connection is merely quiet are
            // `StalePolicy`'s decisions (`ShouldKeepMobView` is the wire out of
            // them). A local backend answers false to every id, so nothing here
            // changes for solo: a mob absent from a local frame is a mob that
            // is dead.
            foreach (KeyValuePair<int, MobView> kv in _activeMobs)
            {
                if (_seenMobIds.Contains(kv.Key)) continue;
                if (_runner.ShouldKeepMobView(kv.Key))
                {
                    // No `Sync`: there is no `MobState` this tick, so the last
                    // pose is what stays on screen and only the brightness
                    // moves (`MobView.FadeEmission`'s own doc).
                    kv.Value.FadeEmission(1f - _runner.MobFadeProgress(kv.Key));
                    continue;
                }

                _staleIdsScratch.Add(kv.Key);
            }
            for (int i = 0; i < _staleIdsScratch.Count; i++) RetireMob(_staleIdsScratch[i]);
        }

        void SyncProjectiles()
        {
            // Task 25 (Приложение П-7): same Render* switch as `SyncMobs` above.
            // Nothing ever holds a projectile still any more (Task Т10,
            // app-88jb, removed the on-hit frame pin that used to be the
            // only way one could) — this always reads straight off the live
            // pair through the `Render*` facade, same as every interpolating
            // view.
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

        /// The cells on the floor (spec §3.11, Stage 3 Task 31). Deliberately
        /// the simplest sync in this class: a pickup does not move, so there is
        /// no `prev`/`alpha` interpolation to do — it is spawned where the
        /// simulation says and it stays there until somebody takes it.
        ///
        /// THIS RUNS OFF `Curr`, NOT `RenderCurr` — a distinction Task Т10
        /// (app-88jb) made purely historical: `RenderCurr` used to diverge from
        /// `Curr` while an on-hit frame pin held it, and a stationary pickup
        /// had nothing for that pin to hold, so this method never bothered
        /// routing through the `Render*` facade at all. With that mechanism
        /// gone, `RenderCurr` is `Curr` on every frame (`SimulationRunner`'s
        /// own doc), so the two reads are equivalent either way now — this one
        /// stays `Curr` because a cell appearing/disappearing on the tick the
        /// server says, with no render-buffer lag on top of the network's own,
        /// is still the simplest correct reading for something that never
        /// interpolates.
        ///
        /// IT WAS EMPTY OVER THE WIRE UNTIL Т32б, and is not any more (fix
        /// round, Ф7 review B-5 — this paragraph still said "does not decode
        /// the `Pickups` block yet" a whole task after it did). The client
        /// decodes all ten blocks now (`ClientFrameDecoder`), so cells draw on
        /// both backends; on the local one `RenderSnapshot.Pickups` is the
        /// world's own array, as it always was.
        void SyncPickups()
        {
            RenderSnapshot curr = _runner.Curr;

            _seenPickupIds.Clear();
            for (int i = 0; i < curr.PickupCount; i++)
            {
                PickupState pickup = curr.Pickups[i];
                _seenPickupIds.Add(pickup.Id);

                if (!_activePickups.TryGetValue(pickup.Id, out PickupView view))
                {
                    view = RentPickup();
                    view.Bind(_gameFeel.PickupVisualDiameter);
                    _activePickups.Add(pickup.Id, view);
                }
                // Written every frame rather than only on rent: the owner can
                // move a cell by hot-tweaking nothing at all, but a RESTART
                // reuses ids, and a pooled view that kept last match's position
                // for one frame would visibly jump.
                view.transform.position = SimSpace.ToWorld(pickup.Pos);
            }

            _staleIdsScratch.Clear();
            // Stage 3 Т33d (bd `app-tut2`): a cell the frame stopped mentioning
            // FADES, exactly as a mob has since Т32б and a player doll since
            // Task 47c. Until this task it popped, and it popped BESIDE a mob
            // that faded — one picture speaking two languages about the same
            // edge of the same fog. The backend answers false to every id on a
            // local backend, so solo is unchanged: a cell absent from a local
            // frame has been picked up.
            foreach (KeyValuePair<int, PickupView> kv in _activePickups)
            {
                if (_seenPickupIds.Contains(kv.Key)) continue;
                if (_runner.ShouldKeepPickupView(kv.Key))
                {
                    kv.Value.FadeEmission(1f - _runner.PickupFadeProgress(kv.Key));
                    continue;
                }

                _staleIdsScratch.Add(kv.Key);
            }
            for (int i = 0; i < _staleIdsScratch.Count; i++) RetirePickup(_staleIdsScratch[i]);
        }

        /// The crates, caches, dropped bundles and corpse markers (spec
        /// §3.7/§3.11, Stage 3 Task 31). Same shape as `SyncPickups` above and
        /// for the same reasons — a container never moves either — with one
        /// addition: the KIND picks the prefab, so a view whose container
        /// somehow changed kind under the same id has to be re-rented rather
        /// than re-pointed. That cannot happen today (`SpawnContainer` gives
        /// every container a fresh id and `ContainerState.Kind` is never
        /// written again), and the guard is one comparison — the cheap side of
        /// a trade whose expensive side is a crate silently drawn as a corpse
        /// marker.
        void SyncContainers()
        {
            RenderSnapshot curr = _runner.Curr;

            _seenContainerIds.Clear();
            for (int i = 0; i < curr.ContainerCount; i++)
            {
                ContainerState container = curr.Containers[i];
                _seenContainerIds.Add(container.Id);

                if (_activeContainers.TryGetValue(container.Id, out ContainerView view)
                    && view.Kind != container.Kind)
                {
                    RetireContainer(container.Id);
                    view = null;
                }
                if (view == null)
                {
                    view = RentContainer(container.Kind);
                    view.Bind(container.Kind, ContainerScaleFor(container.Kind));
                    _activeContainers.Add(container.Id, view);
                }
                view.transform.position = SimSpace.ToWorld(container.Pos);
            }

            _staleIdsScratch.Clear();
            // The boxes' half of the same fade — see `SyncPickups` above.
            foreach (KeyValuePair<int, ContainerView> kv in _activeContainers)
            {
                if (_seenContainerIds.Contains(kv.Key)) continue;
                if (_runner.ShouldKeepContainerView(kv.Key))
                {
                    kv.Value.FadeEmission(1f - _runner.ContainerFadeProgress(kv.Key));
                    continue;
                }

                _staleIdsScratch.Add(kv.Key);
            }
            for (int i = 0; i < _staleIdsScratch.Count; i++) RetireContainer(_staleIdsScratch[i]);
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
        /// `_activePlayers` at all and is pooled by `Clear`, or by `IntoCorpse`'s
        /// unreachable leak guard (that method's own doc), and by nothing else.
        void RetirePlayer(int slot)
        {
            if (!_activePlayers.TryGetValue(slot, out PlayerView view)) return;
            _activePlayers.Remove(slot);
            _fadingPlayerSlots.Remove(slot); // Stage 2 Task 47c — the doll is gone, the mark with it.
            view.gameObject.SetActive(false);
            _playerPool.Push(view);
        }

        /// The pool an archetype's views live in. One of the three homes Task 31
        /// puts the archetype dispatch in (pool, prefab, visual scale), each a
        /// `switch` that THROWS on an unrecognized value rather than falling
        /// back to the Gunner — the fallback is precisely how Elite and the
        /// Director came to be drawn as Gunners for two whole stages, silently,
        /// with no error and no red test (lesson 385, R-237: a catalog home
        /// throws even while it is exhaustive today). `SimulationWorld.
        /// MobConfigFor` and `SnapshotBlocks.MaxHpFor` are the same shape on
        /// the simulation's side of the same enum.
        Stack<MobView> PoolFor(MobType type) => type switch
        {
            MobType.Chaser => _chaserPool,
            MobType.Gunner => _gunnerPool,
            MobType.Elite => _elitePool,
            MobType.Director => _directorPool,
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type,
                "unknown archetype"),
        };

        MobView PrefabFor(MobType type) => type switch
        {
            MobType.Chaser => _chaserPrefab,
            MobType.Gunner => _gunnerPrefab,
            MobType.Elite => _elitePrefab,
            MobType.Director => _directorPrefab,
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type,
                "unknown archetype"),
        };

        /// The archetype's own windup length, for `MobView`'s telegraph ramp.
        /// Same throwing shape as the pool/prefab homes above; see the read
        /// site in `SyncMobs` for what one shared number cost.
        static float TelegraphSecondsFor(MobType type, in SimConfig config) => type switch
        {
            MobType.Chaser => config.Chaser.TelegraphSeconds,
            MobType.Gunner => config.Gunner.TelegraphSeconds,
            MobType.Elite => config.Elite.TelegraphSeconds,
            MobType.Director => config.Director.TelegraphSeconds,
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type,
                "unknown archetype"),
        };

        MobView RentMob(MobType type)
        {
            Stack<MobView> pool = PoolFor(type);
            if (pool.Count > 0)
            {
                MobView v = pool.Pop();
                v.gameObject.SetActive(true);
                return v;
            }
            return Instantiate(PrefabFor(type), transform);
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
            PoolFor(view.Type).Push(view);
        }

        void RetireProjectile(int id)
        {
            if (!_activeProjectiles.TryGetValue(id, out ProjectileView view)) return;
            _activeProjectiles.Remove(id);
            view.gameObject.SetActive(false);
            _projectilePool.Push(view);
        }

        /// The container homes (Stage 3 Task 31), same throwing shape as
        /// `PoolFor`/`PrefabFor` above. `MobCorpse` and `PlayerCorpse` answer
        /// with the SAME pool and the same prefab on purpose: both are a marker
        /// laid over a body somebody else already drew, and splitting them
        /// would be two pools holding identical objects.
        Stack<ContainerView> ContainerPoolFor(ContainerKind kind) => kind switch
        {
            ContainerKind.Crate => _cratePool,
            ContainerKind.Cache => _cachePool,
            ContainerKind.Ground => _groundPool,
            ContainerKind.MobCorpse => _corpseMarkerPool,
            ContainerKind.PlayerCorpse => _corpseMarkerPool,
            _ => throw new System.ArgumentOutOfRangeException(nameof(kind), kind,
                "unknown container kind"),
        };

        /// How big a container is drawn. The corpse kinds are NOT props and do
        /// not take the prop scale (fix-round, Ф7 review B-I1): their view is
        /// the little emissive marker, and it is sized with the CELL so the two
        /// "something to pick up" tells read alike. Before this fix the prefab
        /// was built at the cell's size and then every `Bind` overwrote it with
        /// the container scale, so the marker came out at 1 m instead of 0.5
        /// and the bootstrap's own sizing line was dead code with a comment
        /// that said otherwise.
        float ContainerScaleFor(ContainerKind kind) => kind switch
        {
            ContainerKind.Crate => _gameFeel.ContainerVisualScale,
            ContainerKind.Cache => _gameFeel.ContainerVisualScale,
            ContainerKind.Ground => _gameFeel.ContainerVisualScale,
            ContainerKind.MobCorpse => _gameFeel.PickupVisualDiameter,
            ContainerKind.PlayerCorpse => _gameFeel.PickupVisualDiameter,
            _ => throw new System.ArgumentOutOfRangeException(nameof(kind), kind,
                "unknown container kind"),
        };

        ContainerView ContainerPrefabFor(ContainerKind kind) => kind switch
        {
            ContainerKind.Crate => _crateContainerPrefab,
            ContainerKind.Cache => _cacheContainerPrefab,
            ContainerKind.Ground => _groundContainerPrefab,
            ContainerKind.MobCorpse => _corpseMarkerPrefab,
            ContainerKind.PlayerCorpse => _corpseMarkerPrefab,
            _ => throw new System.ArgumentOutOfRangeException(nameof(kind), kind,
                "unknown container kind"),
        };

        PickupView RentPickup()
        {
            if (_pickupPool.Count > 0)
            {
                PickupView v = _pickupPool.Pop();
                v.gameObject.SetActive(true);
                return v;
            }
            return Instantiate(_pickupPrefab, transform);
        }

        ContainerView RentContainer(ContainerKind kind)
        {
            Stack<ContainerView> pool = ContainerPoolFor(kind);
            if (pool.Count > 0)
            {
                ContainerView v = pool.Pop();
                v.gameObject.SetActive(true);
                return v;
            }
            return Instantiate(ContainerPrefabFor(kind), transform);
        }

        void RetirePickup(int id)
        {
            if (!_activePickups.TryGetValue(id, out PickupView view)) return;
            _activePickups.Remove(id);
            view.gameObject.SetActive(false);
            _pickupPool.Push(view);
        }

        void RetireContainer(int id)
        {
            if (!_activeContainers.TryGetValue(id, out ContainerView view)) return;
            _activeContainers.Remove(id);
            view.gameObject.SetActive(false);
            ContainerPoolFor(view.Kind).Push(view);
        }
    }
}
