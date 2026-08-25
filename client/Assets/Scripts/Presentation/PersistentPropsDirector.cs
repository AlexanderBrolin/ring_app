using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Rendering.Universal;

namespace Ring.Presentation
{
    /// Persistent cosmetics — shell casings, impact decals, corpses, and the
    /// pooled spark/burst particle systems (Task 27, spec §3.11/§3.12,
    /// Приложение П; a fourth kind, slide dust, joins in Task 22 — see the pooling
    /// split doc below). Slots into `SimEventRouter`'s fan-out (П-1) between
    /// `GameFeelDirector` and `AudioDirector` — never subscribes to
    /// `TicksFlushed` itself, same rule as every other class in this
    /// namespace. Muzzle flash itself is Task 17's `MuzzleFlashView` and is
    /// NOT duplicated here (П-2 resolution).
    ///
    /// Every spawn position comes exclusively from the triggering `SimEvent`'s
    /// own `Pos` (via `SimSpace.ToWorld`) — never from `ViewRegistry`/`MobView`/
    /// `ProjectileView` state (owner requirement, веха 3: "партикли/декали/
    /// гильзы/трупы от позиций событий, никаких привязок к мешам вьюх" — a
    /// future model swap changes nothing here).
    ///
    /// THE OWNER SPLIT THAT RULE IN TWO ON 2026-08-10, and the shell casing
    /// falls on the other side of the split (bd `app-e2n`; Stage 2 Task 45b, the
    /// wording is the owner's own, recorded in fix-round 1): **the TRACE of an
    /// event** — the spark, the decal, the corpse, the dash mark — is born at
    /// the point of the event; **the EMISSION of a weapon** — the muzzle flash,
    /// the brass, the start of the aim ray — is born from the weapon's model,
    /// because that is physically where it leaves. This is not an exception to
    /// the rule above and not its repeal: it is which of the two kinds each
    /// cosmetic is. Everything in this class except the casing is a trace, and
    /// every one of them still takes its position from the event verbatim —
    /// with ONE reading of the same quantity from an earlier source, which bd
    /// `app-g21` added and which `PredictDashGlow` below carries in full: the
    /// mark for this client's OWN dash is drawn in the frame the dash starts,
    /// when its event does not exist yet, so the point comes from
    /// `SimulationRunner.RenderCurr.Player.Pos` — this client's own predicted
    /// state of the body that is dashing, and exactly where that body's doll is
    /// drawn. Not an exception to the trace rule and not its repeal: the same
    /// quantity about the same body, one source earlier. How close that lands
    /// to the event's own number is a question with a real answer rather than
    /// "identical", and `PredictDashGlow` gives it.
    ///
    /// For the casing that means `SpawnCasing` asks `ViewRegistry.
    /// TryGetPlayerView` for the shooter's doll and spawns from its
    /// ejection-port socket — the owner's smoke test found brass landing in
    /// FRONT of the pistol, because the simulation's muzzle point is a point
    /// ahead of the hero rather than a part of the gun. It also gets a property
    /// the trace rule could not give it: a shot whose position the server
    /// coarsened on purpose (`ShotHeard`, from a shooter this client cannot see)
    /// leaves no shell at all, because there is no doll to ask (F-3,
    /// `app-aq9`).
    ///
    /// Б1 milestone fix-wave 2 (app-9av, owner request) adds a fourth
    /// `RingBuffer&lt;T&gt;` kind, `DashGlowView` — a glowing floor mark at the
    /// dash start point that fades out over `GameFeelConfig.DashGlowSeconds`,
    /// spawned on `PlayerDashed` the way every other cosmetic here is spawned
    /// — FOR EVERY DASH BUT THIS CLIENT'S OWN ON A NETWORKED CLIENT, which bd
    /// `app-g21` moved off the event and onto the frame the dash actually
    /// starts in. The owner's В1 playtest asked for it: the authoritative event
    /// waits out half a round trip plus `NetConfig.InterpBufferTicks` (~170 ms
    /// at the measured `rtt=147`, `behind=3`) while the dash it marks lasts
    /// 90 ms (`HeroConfig.DashDuration`), so the mark lit up after the dash had
    /// all but ended. `PredictDashGlow` below draws that one mark from this
    /// client's own predicted state instead, and `SpawnDashGlow` swallows the
    /// event that confirms it, so one dash still leaves one mark.
    /// IN SOLO NOTHING CHANGED, and that is the same mechanism from the other
    /// side rather than an exemption: the local backend fans a tick's events
    /// out inside `SimulationRunner.Update`, before any view runs, so the mark
    /// is drawn from the event exactly as it always was and the prediction that
    /// follows is refused. There was no lateness to fix there — a solo dash and
    /// its event land in the same frame (`SimulationRunner.DashingThisFrame`'s
    /// doc has the ordering, `ImmediatePredictionLatch` has the refusal).
    /// A STRANGER'S DASH IS UNTOUCHED, and not by omission: this client
    /// predicts nothing about a body it does not drive (CR 3), so their mark
    /// comes from their event, at their event's own position, exactly as
    /// before.
    ///
    /// Task 24 (revised per `app-1zf`'s investigation — see `GibView`'s class
    /// doc for the full "primitives only" story) added a fifth kind,
    /// `GibView`, driving EVERY kill's small explosion-style primitive-chunk
    /// scatter plus an extra headshot chunk. T24-2 (owner-approved Blender
    /// split, spec item, `app-nco` vision) replaced that primitives-only
    /// scatter with REAL mech part meshes and a death-VARIANT split instead
    /// of "always scatter, always spawn a whole corpse".
    ///
    /// В3 fix-wave 1 (app-n6g item 2, owner playtest feedback: headshots need
    /// an unmistakable payoff, not a coin-flip) changed the variant-selection
    /// rule in `HandleMobDied` to this:
    /// — `e.Zone == HitZone.Head`: `SpawnFullExplodeGibs` — no corpse spawns
    /// for a headshot kill, and the `GibFullExplodeChance` roll below is
    /// skipped entirely for this event. STAGE 3 TASK 31 QUALIFIES THE
    /// "ALWAYS" THIS SENTENCE USED TO CARRY: both triggers now sit behind
    /// `HasGibParts`, because the Sci-Fi models Elite and the Director are
    /// drawn from were never cut into parts, so those two archetypes leave a
    /// corpse on every kill including a headshot. The alternative was
    /// scattering a GUNNER's parts out of a Director, which is the defect
    /// Task 31 exists to remove wearing a different hat. Within the explode, the
    /// HEAD part specifically launches along the killing blow's own `HitDir`
    /// (`GameFeelConfig.GibHeadImpulseSpeed`) instead of the random
    /// upward-biased scatter every other part gets — directional feedback
    /// that reads as "that shot took the head off" (`SpawnFullExplodeGibs`'s
    /// own doc has the exact split).
    /// — Otherwise (non-head kill): unchanged from T24-2 — rolls
    /// `GibFullExplodeChance` (a code const, not a GameFeelConfig field —
    /// cosmetic death-variant RNG, not a hot-tweak feel number, same
    /// "structural, not feel" split the retired `GibExplosionChunkCount`
    /// const used to draw) via `UnityEngine.Random` (legal, casings
    /// precedent): FULL EXPLODE (every part random-scatters, no directional
    /// treatment — a non-head kill has no "which way did the head fly"
    /// story to tell) or a whole corpse (`CorpseView.Spawn`, no head gib —
    /// that side branch only ever applied to headshots, which reach the
    /// corpse branch only for an archetype with no gib parts).
    /// Every gib part's `transform.localScale` mirrors the SAME
    /// `visualScale` (`GameFeelConfig.VisualScaleFor`, one number per
    /// archetype) the corpse
    /// itself gets, below — a part cut from the same mesh has to agree with
    /// the archetype's own live/corpse scale or it visibly mismatches.
    /// `GibMetal.mat` (Task 24's flat gunmetal material) is no longer the
    /// gib's own material — it's kept on disk purely as the fallback wired
    /// into `_chaserPartMaterial`/`_gunnerPartMaterial` if a remap material
    /// is ever missing (`StageOneSceneBootstrap.GetOrCreateGibPrefab`'s own
    /// doc), which should never happen in practice since the parts FBXs
    /// share their material NAME with the already-remapped live mechs.
    ///
    /// Pooling split (Приложение П-7): casings/decals/corpses/dash-glows/gibs
    /// have no "done with it" moment during a live match (spec: "живут до
    /// конца захода") — they use the single shared `RingBuffer&lt;T&gt;` (FIFO,
    /// oldest overwritten once full), one instance per kind, never separate
    /// copies of the same logic. The spark/burst particle systems (hit, block,
    /// death, and — Task 22 — slide dust) ARE ordinary "rent it, it finishes
    /// on its own, give it back" objects — they use
    /// `UnityEngine.Pool.ObjectPool&lt;ParticleSystem&gt;` instead, returned via
    /// `ParticleReturnToPool`'s `OnParticleSystemStopped` callback (prefab's
    /// `stopAction = Callback`, `StageOneSceneBootstrap`), not a second
    /// `RingBuffer&lt;T&gt;` instantiation.
    ///
    /// Every pool (`RingBuffer`s AND particle `ObjectPool`s) is fully
    /// pre-allocated in `Awake` (`Prewarm`) — a live match never pays an
    /// `Instantiate` cost mid-play, only whatever `Spawn`/`Play`
    /// reposition/reset work each event needs (zero allocation once warmed
    /// up, global constraint).
    ///
    /// Casing self-collision (Context resolution — decided here, documented,
    /// not left as an open question): `Physics.IgnoreLayerCollision(
    /// CasingsLayer, CasingsLayer, true)` is called once in `Awake`, a plain
    /// runtime call rather than a `ProjectSettings/DynamicsManager.asset`
    /// edit — it needs no extra idempotent asset patch, and it's scoped to
    /// exactly the lifetime of whichever scene actually carries this
    /// director.
    /// Review fix-round bug: casings originally shared `GreyboxBuilder.
    /// CosmeticsLayer` (8) with the arena's own floor/wall/obstacle colliders
    /// — `IgnoreLayerCollision(8, 8, true)` silently disabled BOTH
    /// casing-vs-casing AND casing-vs-arena collision (it's a single
    /// layer-pair toggle, it can't distinguish "which objects" share the
    /// layer), so casings fell straight through the floor. Casings now get
    /// their OWN dedicated `CasingsLayer` (9, `StageOneSceneBootstrap.
    /// EnsureCasingsLayer` — user layer 9 was empty, verified against
    /// `ProjectSettings/TagManager.asset` before claiming it) — only
    /// 9×9 (casing-vs-casing) is disabled; 9×8 (casing-vs-arena) is left at
    /// Unity's default "collide" and is exactly what makes casings bounce off
    /// the greybox geometry at all.
    ///
    /// `ProjectileBlocked`'s decal/block-spark normal and height are read
    /// straight off the triggering `SimEvent` (Task 21, PC4 — one home, not
    /// two): `ProjectileSystem` already computes the exact contact geometry
    /// server-side (a swept-circle collision result) and carries it out on
    /// the event itself since Task 7 — `HitDir` is the real surface normal
    /// for a wall/obstacle hit, or exactly `float2.zero` (never an
    /// approximation) for a floor hit, and `Height` is the contact height in
    /// both cases (app-88jb Т3, finding D-C4: this used to be `Amount`, a
    /// slot that means damage everywhere else on the struct — it moved to a
    /// field of its own). `HandleBlocked` below only has to tell the two cases apart
    /// (`HitDir == 0` ⇒ floor, decal flat with an up-facing normal) and
    /// convert into `UnityEngine.Vector3`/`Quaternion` — no more re-deriving
    /// the normal analytically against `ArenaConfig.Obstacles`/the ring wall
    /// the way the pre-Task-21 `ComputeBlockNormal` had to (back when the
    /// event carried neither a normal nor a height).
    [DefaultExecutionOrder(10)]
    public sealed class PersistentPropsDirector : MonoBehaviour
    {
        /// User layer 9 — "Casings" in `ProjectSettings/TagManager.asset`
        /// (review fix-round: dedicated layer, split off `GreyboxBuilder.
        /// CosmeticsLayer` — see class doc). Public so `StageOneSceneBootstrap`
        /// (prefab layer assignment + `EnsureCasingsLayer`'s TagManager patch)
        /// shares this exact constant instead of redeclaring the literal `9`.
        public const int CasingsLayer = 9;

        // Structural spawn-positioning offsets — NOT feel numbers (owner
        // guidance, review fix-round: these stay code constants, only the
        // actual game-feel numbers below moved into GameFeelConfig).
        // The casing's own pair (`CasingLateralOffset`, and the spawn height
        // that rode `SimulationRunner.RenderMuzzleHeight`) is gone in Stage 2
        // Task 45b — the shell now leaves a socket on the model, which is a
        // real point and needs neither an offset nor a lift (`SpawnCasing`).
        const float DecalNearOffset = 0.1f;

        // Mech pivot sits at the feet (same convention as MobVisual/ViewRegistry's
        // own mob root) — the Death clip itself lays the body down, so no
        // vertical spawn offset is needed (was 0.5f for the old capsule
        // primitive's "lying on its side" rest height; that path is no longer
        // wired anywhere after T12, Б4).
        const float CorpseLift = 0f;

        // Particle pool capacities (review fix-round: were a single shared
        // `SparkPoolPrewarm = 16`, too small for a dense fight — a maxed-out
        // pool forces `ObjectPool.Get()`/`Release()` to fall back to
        // `Instantiate`/`Destroy` mid-play, breaking the "zero allocation
        // after warmup" constraint). Sized off the player's own default fire
        // rate (`WeaponConfig.FireInterval = 0.12s` ⇒ ~8.3 shots/s) times a
        // generous burst-lifetime window, then padded well past the naive
        // peak for safety margin (multiple mobs clustered, etc.) — NOT SO fields:
        // this is a technical/performance sizing decision, not a "feel" knob
        // the owner would hot-tweak on a playtest (unlike the burst
        // lifetime/speed/size numbers in `GameFeelConfig`, which are).
        // Hit/block sparks: ~8.3/s × ~0.2s lifetime ≈ 1.7 naive concurrent
        // peak → 32 is ~19× that. Death bursts are far rarer (a mob dying,
        // not every shot) → 16 is still generous.
        const int HitSparkPoolCapacity = 32;
        const int BlockSparkPoolCapacity = 32;
        const int DeathBurstPoolCapacity = 16;
        // Task 22: slide dust — slides are gated by a run-up/stamina cost, far
        // rarer than gunfire, same "rare trigger → small pool" reasoning the
        // class doc already gives for DeathBurstPoolCapacity above.
        const int SlideDustPoolCapacity = 16;
        /// Stage 3 Task 31: pickups are collected one at a time by a body
        /// walking over them, so the burst never stacks the way hit sparks do
        /// — the same 16 the two pools above settled on is generous here.
        const int PickupPopPoolCapacity = 16;

        // Stage 2 Task 45b fix-round 1 (G-1): shots recorded by the event
        // fan-out and turned into brass in `LateUpdate`, once `ViewRegistry`
        // (pinned at -10, this class at 10) has placed the dolls this frame's
        // snapshot describes — an ejection port is a child of a hand bone on a
        // root that moves every frame, so at fan-out time it still stands where
        // the PREVIOUS frame left it. Same sizing reasoning as
        // `MuzzleFlashView`'s own buffer, and the same drop-rather-than-grow
        // rule on overflow.
        const int PendingCasingCapacity = 16;

        // T24-2 (app-nco vision, owner-approved Blender split): fraction of
        // kills that get the "mech explodes into every part" variant instead
        // of a whole corpse (+ at most one head gib on a headshot). Kept a
        // code const, not a GameFeelConfig field — cosmetic death-variant
        // RNG, not a hot-tweak feel number (class doc, same split
        // GibExplosionChunkCount used to draw before this task retired it).
        const float GibFullExplodeChance = 0.35f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        // Stage 2 Task 45b: the shooter's doll, asked per event and never
        // cached — see `SpawnCasing` and `ViewRegistry.TryGetPlayerView`.
        [SerializeField] ViewRegistry _viewRegistry;
        [SerializeField] CasingView _casingPrefab;
        [SerializeField] DecalProjector _decalPrefab;
        [SerializeField] CorpseView _corpsePrefab;
        [SerializeField] DashGlowView _dashGlowPrefab;
        [SerializeField] GibView _gibPrefab;
        // T24-2: per-archetype gib PART meshes (George: Head/ArmL/ArmR/LegL/
        // LegR/Torso; Leela: Head/LegL/LegR/Torso) plus the single material
        // each archetype's parts all share — wired by StageOneSceneBootstrap
        // from the George_Parts.fbx/Leela_Parts.fbx sub-assets
        // (`_Ring/Gibs/`). See class doc for the death-variant logic that
        // consumes these.
        [SerializeField] Mesh[] _chaserParts;
        [SerializeField] Mesh[] _gunnerParts;
        [SerializeField] Material _chaserPartMaterial;
        [SerializeField] Material _gunnerPartMaterial;
        [SerializeField] ParticleSystem _hitSparkPrefab;
        [SerializeField] ParticleSystem _blockSparkPrefab;
        [SerializeField] ParticleSystem _deathBurstPrefab;
        [SerializeField] ParticleSystem _slideDustPrefab; // Task 22
        // Stage 3 Task 31 fix-round (Ф7 review A-M2): the pop a cell makes when
        // it is taken. Spec §3.11 asks a pickup for a flash AND a sound on
        // `PickupTaken`;
        // the SOUND waits on a placeholder clip that does not exist yet
        // (`Assets/Audio/Placeholders` has eight, none of them a pickup — bd
        // app-9qqp), but the FLASH needs no binary asset, so it ships here
        // rather than riding along with the half that is genuinely blocked.
        [SerializeField] ParticleSystem _pickupPopPrefab;

        readonly int[] _pendingCasingSlots = new int[PendingCasingCapacity];
        int _pendingCasingCount;

        /// bd `app-g21`: this component's whole "one dash, one mark" ledger —
        /// at most one predicted mark outstanding waiting for the
        /// `PlayerDashed` that confirms it, and, in the other direction, the
        /// credit an event that got there first leaves for the edge still to
        /// come (`ImmediatePredictionLatch`'s own doc carries the three facts
        /// it enforces and what they cost). Held as this component's OWN
        /// instance exactly as `MuzzleFlashView` and `AudioDirector` hold
        /// theirs — an instance per predicted THING, not per component: this
        /// one counts dashes, and nothing here predicts anything else.
        ///
        /// `Clear` DELIBERATELY DOES NOT TOUCH IT, unlike `_pendingCasingCount`
        /// beside it (which names a doll slot of the match that just ended).
        /// Everything this holds is forgotten by its own window within a
        /// fraction of a second of a restart, and a restart zeroes every
        /// `PlayerState` behind the gate anyway, so the first dash of the new
        /// match gets a genuine rising edge either way — the latch's own
        /// restart paragraph has this in full.
        readonly ImmediatePredictionLatch _dashLatch = new ImmediatePredictionLatch();

        RingBuffer<CasingView> _casings;
        RingBuffer<DecalProjector> _decals;
        RingBuffer<CorpseView> _corpses;
        RingBuffer<DashGlowView> _dashGlows;
        RingBuffer<GibView> _gibs;
        ObjectPool<ParticleSystem> _hitSparkPool;
        ObjectPool<ParticleSystem> _blockSparkPool;
        ObjectPool<ParticleSystem> _deathBurstPool;
        ObjectPool<ParticleSystem> _slideDustPool; // Task 22
        ObjectPool<ParticleSystem> _pickupPopPool; // Task 31

        void Awake()
        {
            Physics.IgnoreLayerCollision(CasingsLayer, CasingsLayer, true);

            _casings = new RingBuffer<CasingView>(_gameFeel.MaxCasings, CreateCasing);
            _decals = new RingBuffer<DecalProjector>(_gameFeel.MaxDecals, CreateDecal);
            _corpses = new RingBuffer<CorpseView>(_gameFeel.MaxCorpses, CreateCorpse);
            _dashGlows = new RingBuffer<DashGlowView>(_gameFeel.MaxDashGlows, CreateDashGlow);
            _gibs = new RingBuffer<GibView>(_gameFeel.GibPartsFifoLimit, CreateGib);
            _casings.Prewarm();
            _decals.Prewarm();
            _corpses.Prewarm();
            _dashGlows.Prewarm();
            _gibs.Prewarm();

            _hitSparkPool = CreateParticlePool(_hitSparkPrefab, HitSparkPoolCapacity);
            _blockSparkPool = CreateParticlePool(_blockSparkPrefab, BlockSparkPoolCapacity);
            _deathBurstPool = CreateParticlePool(_deathBurstPrefab, DeathBurstPoolCapacity);
            _slideDustPool = CreateParticlePool(_slideDustPrefab, SlideDustPoolCapacity); // Task 22
            _pickupPopPool = CreateParticlePool(_pickupPopPrefab, PickupPopPoolCapacity); // Task 31
            PrewarmParticlePool(_hitSparkPool, HitSparkPoolCapacity);
            PrewarmParticlePool(_blockSparkPool, BlockSparkPoolCapacity);
            PrewarmParticlePool(_deathBurstPool, DeathBurstPoolCapacity);
            PrewarmParticlePool(_slideDustPool, SlideDustPoolCapacity); // Task 22
            PrewarmParticlePool(_pickupPopPool, PickupPopPoolCapacity); // Task 31
        }

        // WorldRestarted is not a tick event (П-1 only restricts TicksFlushed to
        // its sole SimEventRouter subscriber) — direct subscription, same shape
        // as every other class in this namespace.
        void OnEnable() => _runner.WorldRestarted += Clear;

        void OnDisable() => _runner.WorldRestarted -= Clear;

        /// Match-restart cleanup (spec §3.12: "Clear() на WorldRestarted — всё
        /// в пулы"). Particle pools need no equivalent — every burst is
        /// transient (finishes on its own within a fraction of a second) and
        /// self-releases via `ParticleReturnToPool`, so nothing from a
        /// previous match can still be visible by the time a restart actually
        /// happens.
        public void Clear()
        {
            _casings.Clear(view => view.gameObject.SetActive(false));
            _decals.Clear(decal => decal.gameObject.SetActive(false));
            _corpses.Clear(corpse => corpse.gameObject.SetActive(false));
            _dashGlows.Clear(glow => glow.gameObject.SetActive(false));
            _gibs.Clear(gib => gib.gameObject.SetActive(false)); // Task 24 (D10)
            // Fix-round 1 (G-1): a shot recorded this frame names a slot of the
            // match that just ended — `ViewRegistry` has handed that doll back
            // to its pool by the time this returns, so the record can only
            // describe somebody else.
            _pendingCasingCount = 0;
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's
        /// buffer (П-1 fan-out).
        public void HandleEvent(in SimEvent e)
        {
            switch (e.Kind)
            {
                case SimEventKind.ProjectileFired:
                    // F-3 fix-round: a mob's shot has no casing of its own (the
                    // sim doesn't model gunner brass) — gating on Owner keeps a
                    // Gunner's gunfire from spawning the PLAYER's shell casing at
                    // its own muzzle, which is what an owner-blind event let
                    // through before this field existed.
                    if (e.Owner == ProjectileOwner.Player) RecordCasing(in e);
                    break;
                case SimEventKind.ProjectileHit:
                    SpawnHitSpark(in e);
                    break;
                case SimEventKind.ProjectileHitPlayer:
                    SpawnPlayerHitSpark(in e);
                    break;
                case SimEventKind.ProjectileBlocked:
                    HandleBlocked(in e);
                    break;
                case SimEventKind.MobDied:
                    HandleMobDied(in e);
                    break;
                case SimEventKind.PlayerDashed:
                    SpawnDashGlow(in e);
                    break;
                case SimEventKind.PlayerSlideStarted:
                    // Task 22: dust kicked up at slide start — omnidirectional pop,
                    // same Quaternion.identity convention HitSpark/DeathBurst
                    // above already use for their own non-directional bursts.
                    PlayParticle(_slideDustPool, SimSpace.ToWorld(e.Pos), Quaternion.identity);
                    break;
                case SimEventKind.PickupTaken:
                    // Task 31: at the CELL's position, which is where the
                    // player's eye is — `PickupSystem` emits this event with
                    // `pickup.Pos` (that emitter's own call), not with the
                    // collector's, and the two differ by up to the pickup
                    // radius. Omnidirectional, hence `Quaternion.identity`,
                    // the same convention every other non-directional burst
                    // here already uses.
                    PlayParticle(_pickupPopPool, SimSpace.ToWorld(e.Pos), Quaternion.identity);
                    break;
                case SimEventKind.DashRicocheted:
                    HandleRicocheted(in e);
                    break;
            }
        }

        /// Stage 2 Task 45b (bd `app-e2n`, owner smoke test #1: "гильзы падают
        /// ПЕРЕД пистолетом"): the brass leaves the shooter's own EJECTION PORT
        /// — the socket on that doll's gun — instead of the simulation's muzzle
        /// point, which is the hero's center plus `WeaponConfig.MuzzleOffset`
        /// along the aim and therefore lands the shell a barrel's length in
        /// front of the model.
        ///
        /// THE RANDOM LATERAL SCATTER IS GONE WITH IT, and it is not a tuning
        /// loss: `CasingLateralOffset` jittered the spawn point by ±0.15 m in X
        /// and Z, which never modelled anything — it smeared a point that was in
        /// the wrong place to begin with. The living randomness stays exactly as
        /// it was, in the eject SPEED, the upward impulse and the torque, all
        /// three read from `GameFeelConfig` per shot.
        ///
        /// THE IMPULSE'S HEADING COMES OFF THE WEAPON, NOT OFF THE SHOT. It used
        /// to be the shot direction rotated -90°, i.e. a guess at where a pistol
        /// throws brass, derived from a number that describes where the ROUND
        /// went. The port's own forward axis supplies it now (`GameFeelConfig.
        /// GunEjectLocalEuler`, owner-tunable with the scene gizmo), so "вбок-
        /// назад" is a pose the owner can see and adjust rather than an angle
        /// baked in here. Only the HEADING: `SpawnCasing` flattens that axis to
        /// horizontal and takes the vertical component from `GameFeelConfig.
        /// CasingImpulseUpMin`/`Max` as it always did — that method's own doc
        /// (fix-round 1, G-5) has the measurement behind the split.
        ///
        /// NO DOLL, NO CASING — the same rule, and the same F-3 reason, as the
        /// muzzle flash (`MuzzleFlashView`'s class doc). A `ShotHeard` from a
        /// shooter behind the fog used to drop a shell at the position the
        /// server coarsened, and game feel then kept that shell on the floor for
        /// the rest of the match: a permanent marker over a player this client
        /// was never allowed to locate. The `Owner == Player` gate in
        /// `HandleEvent` above is unchanged — a mob's round still has no brass.
        ///
        /// Fix-round 1 (G-1): the event only RECORDS the shooter's slot. The
        /// port is a child of a hand bone on a doll `ViewRegistry` has not yet
        /// moved this frame, so reading its world pose here would spawn the
        /// shell off the previous frame's gun.
        void RecordCasing(in SimEvent e)
        {
            if (_pendingCasingCount == PendingCasingCapacity) return;
            _pendingCasingSlots[_pendingCasingCount++] = e.PlayerIndex;
        }

        /// The frame's recorded shots, turned into brass now that every doll
        /// stands where this frame's snapshot puts it (fix-round 1, G-1), and
        /// this frame's own dash mark if the dash started in it (bd `app-g21`).
        ///
        /// THE PREDICTION IS HERE RATHER THAN IN AN `Update` OF ITS OWN, AND
        /// NOT FOR `MuzzleFlashView`'s REASON. That class draws in `LateUpdate`
        /// because its burst leaves a socket on a doll `ViewRegistry` has not
        /// moved yet this frame (its own `[DefaultExecutionOrder]` block).
        /// Nothing here reads a transform: the mark is placed from the facade's
        /// own predicted state, which `SimulationRunner` (pinned -50) filled
        /// before either phase, so both would draw the same mark in the same
        /// rendered frame. This class already has a per-frame entry point and a
        /// second one would only spread its per-frame work over two methods.
        void LateUpdate()
        {
            for (int i = 0; i < _pendingCasingCount; i++) SpawnCasing(_pendingCasingSlots[i]);
            _pendingCasingCount = 0;

            PredictDashGlow();
        }

        /// The dash mark for THIS client's own dash, drawn in the frame the
        /// dash starts instead of the frame its event arrives (bd `app-g21` —
        /// the class doc has the owner's complaint and the numbers behind it).
        ///
        /// `SimulationRunner.DashingThisFrame` IS THE LEVEL AND THE LATCH IS
        /// THE EDGE, which is why `ShouldPredict` is called on EVERY frame this
        /// method reaches, mark or no mark: the edge is a function of the
        /// previous frame's answer (`ImmediatePredictionLatch.ShouldPredict`'s
        /// own doc). A dash cannot slip between two frames the way a shot can —
        /// the level lasts the whole 90 ms dash rather than one 33 ms tick — so
        /// the edge is missed only by a frame longer than the dash itself.
        /// Missing it is the harmless direction either way: the mark then
        /// comes with the event, as it did before this task.
        ///
        /// RECONCILIATION HANDS OUT A SECOND RISING EDGE FOR ONE DASH, AND THE
        /// LATCH IS ALREADY WHY THAT IS HARMLESS. In a clean simulation
        /// `DashTimer` goes positive exactly once per dash (`PlayerMovementSystem`
        /// starts one in the branch that is unreachable while a dash runs, and
        /// only decrements from there), but on a networked client
        /// `PlayerPredictionCore.BeginReconcile` assigns the authoritative
        /// state whole and the replay behind it can put the dash back on a tick
        /// the client had already cleared: positive → 0 → positive, with no
        /// second dash anywhere. That is defect G-2 of Task 28 in another
        /// costume, and `ImmediatePredictionLatch`'s second fact — one
        /// unconfirmed prediction at a time — closes it by construction,
        /// because the correction always arrives while this dash's own event is
        /// still crossing the interpolation buffer. Said out loud because no
        /// test in this project can say it: `Ring.Presentation` is outside the
        /// EditMode assembly's references (bd `app-8oz`).
        ///
        /// AN EVENT THAT CAME FIRST REFUSES THE EDGE, and the latch holds that
        /// half of the rule too (`ImmediatePredictionLatch.NoteShownFromEvent`,
        /// this task's fix-round; the gate's own doc has the ordering that makes
        /// it necessary). On the local backend the event is fanned out before
        /// any view runs at all, so solo NEVER draws from here: `SpawnDashGlow`
        /// has already put the mark down and taken out the credit that refuses
        /// the edge — which arrives that same frame (Task Т10, app-88jb,
        /// removed the on-hit render pin that used to be able to delay it by
        /// several frames instead; the credit's own window covered both cases
        /// while that mechanism existed). That is why the refusal is not a bit
        /// of this class's own cleared when the gate falls: a level-based bit
        /// would have had the same defect that render pin exposed elsewhere in
        /// this same fix-round — cleared mid-dash, before a delayed edge it had
        /// to remember ever arrived — and the fix-round this paragraph comes
        /// from was opened on the second mark that produced.
        ///
        /// THE POINT IS `RenderCurr.Player.Pos` AND NOT `RenderPlayerWorldPos`,
        /// and on the networked backend — the only one this method ever draws
        /// on, per the paragraph above — the two are the same number,
        /// unconditionally: `NetworkSimBackend.BlendOwnPlayer` writes one
        /// blended pose into BOTH halves of the render pair, so that
        /// property's lerp is an identity there on every frame (Task Т10,
        /// app-88jb, closed the one window — a catch-up ramp following an
        /// on-hit render pin — where the two used to part company;
        /// `BlendOwnPlayer`'s own doc records the same closure). The mark
        /// therefore lands where the doll is drawn, which is what a mark
        /// under a body has to do.
        ///
        /// WHAT THE POINT IS NOT is "the same number the authoritative event
        /// carries", and the earlier wording of this paragraph claimed exactly
        /// that. The blended pose takes every field off the freshest predicted
        /// state but lerps `Pos` from the previous predicted tick towards it by
        /// the render phase, trailing it by `1 - phase` ticks
        /// (`BlendOwnPlayer`'s own doc, half a tick on average), while the event
        /// carries `PlayerState.Pos` as it stood at the end of the dash's first
        /// tick (`SimulationWorld.TickMovement`). At `DashSpeed` 30 m/s and a
        /// 1/30 s tick that is up to a meter apart, typically half of it —
        /// against a mark `GameFeelConfig.DashGlowSize` 0.9 wide, and under the
        /// doll either way. The trace rule of the class doc is satisfied by the
        /// same quantity read one source earlier, not by an identical decimal.
        void PredictDashGlow()
        {
            if (!_dashLatch.ShouldPredict(_runner.DashingThisFrame, Time.unscaledTime)) return;

            SpawnDashGlowAt(SimSpace.ToWorld(_runner.RenderCurr.Player.Pos));
            _dashLatch.Arm(Time.unscaledTime, _runner.ImmediatePredictionWindowSeconds);
        }

        /// THE EJECTION DIRECTION IS HORIZONTAL, AND ONLY ITS HEADING COMES FROM
        /// THE MODEL (fix-round 1, G-5). The port's forward axis is a full 3D
        /// vector on a gun that hangs off a hand bone at a composite angle
        /// (`GameFeelConfig.GunLocalEuler` is `{344.7, 97.6, 82.9}` in the
        /// shipped asset), so using it whole would put an arbitrary vertical
        /// component into the impulse — and the shipped `CasingImpulseUp` pair
        /// (0.2/0.5 m/s) is small enough that a downward component of the eject
        /// speed (0.8–1.4 m/s) would simply drive the shell into the floor.
        /// Flattening it keeps what the socket is FOR — "which way, sideways,
        /// does this weapon throw brass" — and leaves the height where the owner
        /// tunes it, in the SO, exactly as the pre-Task-45b formula did. A port
        /// aimed straight up or down has no heading to give; the shell then
        /// simply drops, which is visible and tunable rather than silently
        /// normalized into a random direction.
        void SpawnCasing(int slot)
        {
            if (!_viewRegistry.TryGetPlayerView(slot, out PlayerView doll)) return;
            Transform port = doll.EjectSocket;
            if (port == null) return;

            Vector3 sideways = port.forward;
            sideways.y = 0f;
            float sidewaysSqr = sideways.sqrMagnitude;
            sideways = sidewaysSqr > 1e-6f ? sideways / Mathf.Sqrt(sidewaysSqr) : Vector3.zero;

            Vector3 impulse =
                sideways * Random.Range(_gameFeel.CasingEjectSpeedMin, _gameFeel.CasingEjectSpeedMax)
                + Vector3.up * Random.Range(_gameFeel.CasingImpulseUpMin, _gameFeel.CasingImpulseUpMax);
            Vector3 torque = Random.insideUnitSphere * _gameFeel.CasingTorqueScale;

            CasingView view = _casings.Rent();
            view.Spawn(port.position, impulse, torque,
                _gameFeel.CasingPhysicsSeconds, _gameFeel.CasingScale);
        }

        /// В3 fix-wave 1 (app-n6g item 3c, owner playtest feedback: hit
        /// sparks were unreadable — spawned at a fixed floor height
        /// (`SimSpace.ToWorld(e.Pos)`'s hardcoded Y=0) regardless of where
        /// the shot actually landed on the mob). Originally fixed by
        /// reconstructing an approximate contact height from `e.Zone`
        /// against the struck archetype's own legs/body/head belts
        /// (`ZoneHeight`, a band MIDPOINT, not the real contact point) —
        /// removed as of app-88jb Т3 (finding B2-I10): it was a second,
        /// worse copy of the exact zone geometry `ProjectileSystem` had
        /// already resolved and thrown away, existing only because the
        /// event itself carried no height. `SimEvent.Height` (Т3) is that
        /// missing field — `ProjectileSystem`'s `HitMob` case now carries
        /// the real contact height on the event, the same "one home, not
        /// two" precedent `HandleBlocked`'s `HitDir`/height already follow
        /// (class doc, PC4) — so this method reads it straight off the
        /// event instead of re-deriving a band-midpoint approximation.
        void SpawnHitSpark(in SimEvent e)
        {
            PlayParticle(_hitSparkPool, SimSpace.ToWorld(e.Pos) + Vector3.up * e.Height,
                Quaternion.identity);
        }

        /// The same spark, off a collector instead of a mech (Stage 2 Task 45c,
        /// bd `app-aq9`, ADR-001 §10's per-hit checklist): `ProjectileHitPlayer`
        /// had no consumer anywhere in Presentation until this task, so a round
        /// landing on a player produced no particle at all while a round landing
        /// on a mob produced one. Reads `e.Height` straight off the event, same
        /// as `SpawnHitSpark` above — see that method's own doc for why (the
        /// zone-band reconstruction both sparks used to run through,
        /// `ZoneHeight`, is gone as of app-88jb Т3).
        ///
        /// NO DECAL. A decal marks the SURFACE a round stopped against and lives
        /// to the end of the match (`HandleBlocked`); a body is not a surface,
        /// and a permanent scorch mark hanging in the air where a player was
        /// standing is a lie about geometry as well as a giveaway about where
        /// somebody fought.
        void SpawnPlayerHitSpark(in SimEvent e)
        {
            PlayParticle(_hitSparkPool, SimSpace.ToWorld(e.Pos) + Vector3.up * e.Height,
                Quaternion.identity);
        }

        void HandleBlocked(in SimEvent e)
        {
            // Floor vs wall/obstacle (class doc): HitDir is exactly zero for a
            // floor contact — ProjectileSystem's own gate (Task 7), not an
            // epsilon check.
            bool isFloor = e.HitDir.x == 0f && e.HitDir.y == 0f;
            Vector3 normal = isFloor ? Vector3.up : new Vector3(e.HitDir.x, 0f, e.HitDir.y);
            // Height is the sim's own contact height for BOTH branches
            // (ProjectileSystem's HitBarrier/HitFloor cases share one
            // formula) — its own field is the sole home for it (app-88jb Т3
            // moved it out of Amount, which means damage everywhere else on
            // this struct), same as HitDir already is for the normal (class
            // doc, PC4).
            Vector3 contactWorld = SimSpace.ToWorld(e.Pos) + Vector3.up * e.Height;
            // A floor's normal (world up) can't double as LookRotation's own
            // "up" hint — forward (-normal) and the hint would be
            // anti-parallel, a degenerate case. A horizontal hint sidesteps
            // it; the wall branch keeps its original roll convention.
            Vector3 upHint = isFloor ? Vector3.forward : Vector3.up;

            DecalProjector decal = _decals.Rent();
            decal.gameObject.SetActive(true);
            decal.transform.SetPositionAndRotation(
                contactWorld + normal * DecalNearOffset,
                Quaternion.LookRotation(-normal, upHint));

            PlayParticle(_blockSparkPool, contactWorld, Quaternion.LookRotation(normal, upHint));
        }

        /// The archetype homes this class shares with `ViewRegistry` (Stage 3
        /// Task 31, spec Р251). Each throws on an unknown archetype instead of
        /// answering with the Gunner's numbers — the fallback that had the
        /// Director dying at a Gunner's height and a Gunner's size.
        MobSimConfig ArchetypeConfigFor(MobType type) => type switch
        {
            MobType.Chaser => _runner.Config.Chaser,
            MobType.Gunner => _runner.Config.Gunner,
            MobType.Elite => _runner.Config.Elite,
            MobType.Director => _runner.Config.Director,
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type,
                "unknown archetype"),
        };

        /// The dying archetype's own cut-up meshes and the one remap material
        /// they share — EMPTY for the two archetypes whose models were never
        /// cut up (see `HandleMobDied`). Returning empty rather than throwing
        /// is deliberate here and only here: "this archetype has no parts" is a
        /// fact about the ASSET, answered honestly, while an unknown archetype
        /// is still a bug and still throws.
        (Mesh[] parts, Material material) GibPartsFor(MobType type) => type switch
        {
            MobType.Chaser => (_chaserParts, _chaserPartMaterial),
            MobType.Gunner => (_gunnerParts, _gunnerPartMaterial),
            MobType.Elite => (System.Array.Empty<Mesh>(), null),
            MobType.Director => (System.Array.Empty<Mesh>(), null),
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type,
                "unknown archetype"),
        };

        bool HasGibParts(MobType type) => GibPartsFor(type).parts.Length > 0;

        void HandleMobDied(in SimEvent e)
        {
            // В1/В2 fix-wave 2 (app-n6g item 2, BUG fix): same archetype-scale
            // read `ViewRegistry.SyncMobs` uses for the live `MobVisual.Bind`
            // call — `MobDied`'s own `MobType` is enough, no new SO field
            // (CorpseView.Spawn's own doc). T24-2: also the scale every
            // spawned gib part's transform.localScale mirrors (class doc).
            float visualScale = _gameFeel.VisualScaleFor(e.MobType);

            PlayParticle(_deathBurstPool, SimSpace.ToWorld(e.Pos), Quaternion.identity);

            // В3 fix-wave 1 (app-n6g item 2, owner playtest feedback):
            // headshots ALWAYS get the full-explode variant — never a
            // corpse, and the roll below never runs for this event (class
            // doc has the full rule). `SpawnFullExplodeGibs` itself checks
            // `e.Zone` to give the HEAD part its own directional impulse.
            // Stage 3 Task 31: BOTH gib triggers below are gated on the dying
            // archetype having parts at all. `_chaserParts`/`_gunnerParts` are
            // meshes cut from the mech FBXs in Blender (T24-2, owner-approved
            // split); the Sci-Fi models Elite and the Director are drawn from
            // ship whole, so there is nothing to scatter. The honest answer is
            // a corpse — the alternative would have been to explode the
            // Director into a GUNNER'S parts, which is the very defect this
            // task exists to remove, one layer deeper.
            if (HasGibParts(e.MobType))
            {
                // В3 fix-wave 1: headshots ALWAYS get the full-explode variant.
                if (e.Zone == HitZone.Head)
                {
                    SpawnFullExplodeGibs(in e, visualScale);
                    return;
                }

                if (Random.value < GibFullExplodeChance)
                {
                    SpawnFullExplodeGibs(in e, visualScale);
                    return;
                }
            }

            Vector3 pos = SimSpace.ToWorld(e.Pos) + Vector3.up * CorpseLift;
            CorpseView corpse = _corpses.Rent();
            corpse.Spawn(pos, e.MobType, _gameFeel.CorpseGlowFadeSeconds, visualScale);
        }

        /// T24-2 (app-nco vision, owner-approved Blender split): the "mech
        /// explodes" death variant — every part of the dying mob's own
        /// archetype (`_chaserParts`/`_gunnerParts`) scatters from the
        /// event's own `Pos` (owner requirement, веха 3 — XY always rides
        /// `e.Pos`, never `ViewRegistry`/`MobView` state), each at a
        /// belt-derived height keyed off its own `GibView.ClassifyPart` kind
        /// (`PartHeight` below) — the SAME `Config.Chaser`/`Gunner`
        /// zone-geometry belts `ProjectileSystem`'s hit-zone classification
        /// and the `AimProxy_*` colliders already read. NO corpse spawns in
        /// this variant — every part IS the "corpse" here (class doc).
        /// Called for BOTH triggers `HandleMobDied` recognizes
        /// (`GibFullExplodeChance` roll, or unconditionally on a headshot —
        /// class doc): every part's impulse is the usual upward-biased
        /// random scatter (`Random.onUnitSphere`, reflected onto the upper
        /// hemisphere when it rolls downward — a part launching straight
        /// into the floor reads as a bug, not a death) at
        /// `GameFeelConfig.GibExplosionSpeed`, EXCEPT the HEAD part on a
        /// headshot kill (`e.Zone == HitZone.Head`, В3 fix-wave 1 item 2):
        /// that one part instead launches along the killing blow's own
        /// `HitDir` at `GameFeelConfig.GibHeadImpulseSpeed` — directional
        /// feedback that reads as "that shot took the head off" (same
        /// formula/height Task 24's original standalone headshot chunk
        /// used, folded into this method now that a headshot always takes
        /// the full-explode path instead of adding one extra chunk on top
        /// of an intact corpse).
        void SpawnFullExplodeGibs(in SimEvent e, float visualScale)
        {
            (Mesh[] parts, Material material) = GibPartsFor(e.MobType);
            MobSimConfig archetype = ArchetypeConfigFor(e.MobType);
            Vector3 worldPos = SimSpace.ToWorld(e.Pos);
            float settleSeconds = _gameFeel.GibPhysicsSeconds;
            bool headshot = e.Zone == HitZone.Head;

            for (int i = 0; i < parts.Length; i++)
            {
                Mesh part = parts[i];
                GibPartKind kind = GibView.ClassifyPart(part.name);
                float height = PartHeight(kind, in archetype);
                Vector3 impulse;
                if (headshot && kind == GibPartKind.Head)
                {
                    impulse = _gameFeel.GibHeadImpulseSpeed * SimSpace.ToWorld(e.HitDir);
                }
                else
                {
                    Vector3 dir = Random.onUnitSphere;
                    if (dir.y < 0f) dir.y = -dir.y; // upward-biased (class doc)
                    impulse = dir * _gameFeel.GibExplosionSpeed;
                }

                GibView gib = _gibs.Rent();
                gib.SettleSeconds = settleSeconds;
                gib.Spawn(worldPos + Vector3.up * height, impulse, part, material, visualScale);
            }
        }

        /// T24-2: per-part spawn height for `SpawnFullExplodeGibs`, keyed off
        /// the dying mob's own archetype belts — head at the head belt (Task
        /// 24's original formula), legs low (below the legs belt), torso AND
        /// arms mid (the body belt band — George's arms read the same band
        /// as its torso, task brief: "arms mid for George").
        static float PartHeight(GibPartKind kind, in MobSimConfig archetype)
        {
            switch (kind)
            {
                case GibPartKind.Head:
                    return (archetype.BodyTop + archetype.HeadTop) * 0.5f;
                case GibPartKind.LegL:
                case GibPartKind.LegR:
                    return archetype.LegsTop * 0.5f;
                default: // Torso, ArmL, ArmR
                    return (archetype.LegsTop + archetype.BodyTop) * 0.5f;
            }
        }

        /// The authoritative mark, at the dashing player's own position on the
        /// tick the dash began (`SimEvent.Pos`, the trace rule of the class
        /// doc). Every stranger's dash reaches this and nothing else.
        ///
        /// THIS CLIENT'S OWN DASH IS THE ONE EXCEPTION, AND IT IS FILTERED BY
        /// SEAT BEFORE ANYTHING ELSE (bd `app-g21`). Until this task the method
        /// never read `e.PlayerIndex` at all — it drew for any `PlayerDashed`,
        /// which was right while nothing was ever drawn ahead of the event; now
        /// that `PredictDashGlow` draws one dash early, the mark for THAT dash
        /// must not be drawn a second time when its event lands. `PlayerIndex`
        /// on this kind is the ACTOR (`SimEvent`'s own doc: the five
        /// "own-action" kinds), so the test is exactly "was this my dash".
        /// The shape is `MuzzleFlashView.HandleEvent`'s, for the reason
        /// recorded there: `ProjectileOwner.Player` used to mean "mine" and on
        /// a networked client no longer does, so the seat is what a suppression
        /// must key on — a stranger's dash consuming my prediction would drop
        /// their mark AND double mine (bd `app-ai2`, one participant further
        /// out).
        ///
        /// AND THE CREDIT FOR THE OTHER ORDER IS TAKEN OUT ONLY WHERE THE MARK
        /// IS ACTUALLY DRAWN — below the `TryConsume` return, never above it.
        /// Taking one out for an event that drew nothing would refuse the
        /// prediction of a dash nothing has shown yet.
        void SpawnDashGlow(in SimEvent e)
        {
            if (e.PlayerIndex == _runner.RenderCurr.LocalPlayerIndex)
            {
                // Already marked this dash ahead of its event (`PredictDashGlow`)
                // — consume the prediction instead of drawing a visible
                // duplicate a meter up the dash line.
                if (_dashLatch.TryConsume(Time.unscaledTime)) return;
                // Nothing was predicted and this mark is now on the floor, so
                // the rising edge still to come for this same dash — this
                // frame's, or the one the freeze delayed it into — has to be
                // refused (`ImmediatePredictionLatch.NoteShownFromEvent`).
                _dashLatch.NoteShownFromEvent(
                    Time.unscaledTime, _runner.ImmediatePredictionWindowSeconds);
            }

            SpawnDashGlowAt(SimSpace.ToWorld(e.Pos));
        }

        /// One home for renting a mark and dressing it in the owner's two
        /// numbers, shared by the event above and the prediction (bd
        /// `app-g21`) — the two differ in where the point comes from and in
        /// nothing else.
        void SpawnDashGlowAt(Vector3 worldPos)
        {
            DashGlowView glow = _dashGlows.Rent();
            glow.Spawn(worldPos, _gameFeel.DashGlowSeconds, _gameFeel.DashGlowSize);
        }

        /// Task 22 (spec brief QA13/QC3): reuses the existing block-spark pool/
        /// prefab outright — no dedicated ricochet-spark asset, and no new
        /// burst-count field, `BlockSparkBurstCount` is already baked into the
        /// prefab (GameFeelConfig class doc: "RicochetSparkCount... deliberately
        /// NOT added — ricochet sparks reuse the baked BlockSparkBurstCount").
        /// Guard mirrors `HandleBlocked`'s own zero-normal check above:
        /// `Quaternion.LookRotation(Vector3.zero, ...)` logs an error (which
        /// would fail the log gate, the batchmode error-grep every Unity run
        /// is checked against) rather than throwing, so this can't rely on
        /// the sim never emitting a degenerate contact normal — Presentation
        /// checks for itself.
        void HandleRicocheted(in SimEvent e)
        {
            if (e.HitDir.x == 0f && e.HitDir.y == 0f) return;

            PlayParticle(_blockSparkPool, SimSpace.ToWorld(e.Pos),
                Quaternion.LookRotation(SimSpace.ToWorld(e.HitDir), Vector3.up));
        }

        void PlayParticle(ObjectPool<ParticleSystem> pool, Vector3 worldPos, Quaternion rotation)
        {
            ParticleSystem ps = pool.Get();
            ps.transform.SetPositionAndRotation(worldPos, rotation);
            ps.Clear(true);
            ps.Play();
        }

        CasingView CreateCasing()
        {
            CasingView view = Instantiate(_casingPrefab, transform);
            view.gameObject.SetActive(false);
            return view;
        }

        DecalProjector CreateDecal()
        {
            DecalProjector projector = Instantiate(_decalPrefab, transform);
            projector.gameObject.SetActive(false);
            return projector;
        }

        CorpseView CreateCorpse()
        {
            CorpseView view = Instantiate(_corpsePrefab, transform);
            view.gameObject.SetActive(false);
            return view;
        }

        DashGlowView CreateDashGlow()
        {
            DashGlowView view = Instantiate(_dashGlowPrefab, transform);
            view.gameObject.SetActive(false);
            return view;
        }

        GibView CreateGib()
        {
            GibView view = Instantiate(_gibPrefab, transform);
            view.gameObject.SetActive(false);
            return view;
        }

        ObjectPool<ParticleSystem> CreateParticlePool(ParticleSystem prefab, int capacity)
        {
            ObjectPool<ParticleSystem> pool = null;
            pool = new ObjectPool<ParticleSystem>(
                createFunc: () =>
                {
                    ParticleSystem instance = Instantiate(prefab, transform);
                    instance.GetComponent<ParticleReturnToPool>().ReleaseAction = pool.Release;
                    instance.gameObject.SetActive(false);
                    return instance;
                },
                actionOnGet: ps => ps.gameObject.SetActive(true),
                actionOnRelease: ps => ps.gameObject.SetActive(false),
                actionOnDestroy: ps => Destroy(ps.gameObject),
                collectionCheck: true,
                defaultCapacity: capacity,
                maxSize: capacity);
            return pool;
        }

        static void PrewarmParticlePool(ObjectPool<ParticleSystem> pool, int count)
        {
            var scratch = new ParticleSystem[count];
            for (int i = 0; i < count; i++) scratch[i] = pool.Get();
            for (int i = 0; i < count; i++) pool.Release(scratch[i]);
        }
    }
}
