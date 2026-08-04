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
    /// Б1 milestone fix-wave 2 (app-9av, owner request) adds a fourth
    /// `RingBuffer&lt;T&gt;` kind, `DashGlowView` — a glowing floor mark at the
    /// dash start point that fades out over `GameFeelConfig.DashGlowSeconds`,
    /// spawned on `PlayerDashed` the same way every other event here spawns
    /// its own cosmetic.
    ///
    /// Task 24 (revised per `app-1zf`'s investigation — see `GibView`'s class
    /// doc for the full "primitives only" story) added a fifth kind,
    /// `GibView`, driving EVERY kill's small explosion-style primitive-chunk
    /// scatter plus an extra headshot chunk. T24-2 (owner-approved Blender
    /// split, spec item, `app-nco` vision) replaces that primitives-only
    /// scatter with REAL mech part meshes and a death-VARIANT split instead
    /// of "always scatter, always spawn a whole corpse":
    /// `HandleMobDied` rolls `GibFullExplodeChance` (a code const, not a
    /// GameFeelConfig field — cosmetic death-variant RNG, not a hot-tweak
    /// feel number, same "structural, not feel" split the retired
    /// `GibExplosionChunkCount` const used to draw) via `UnityEngine.Random`
    /// (legal, casings precedent):
    /// — FULL EXPLODE (`SpawnFullExplodeGibs`): every part of the dying
    /// mob's own archetype (`_chaserParts`/`_gunnerParts`, wired by
    /// `StageOneSceneBootstrap` from `George_Parts.fbx`/`Leela_Parts.fbx`,
    /// `_Ring/Gibs/`) scatters from the event's own `Pos`, each at a
    /// belt-derived height keyed off `GibView.ClassifyPart`'s per-part kind
    /// (head at the head belt, legs low, torso/arms mid) with an
    /// upward-biased random impulse (`GameFeelConfig.GibExplosionSpeed`) — NO
    /// corpse spawns in this variant.
    /// — OTHERWISE: a whole corpse spawns as before (`CorpseView.Spawn`);
    /// IF the killing blow's own `Zone` is `HitZone.Head`, `SpawnHeadGib`
    /// additionally launches ONLY that archetype's head part along the
    /// blow's own `HitDir` (`GameFeelConfig.GibHeadImpulseSpeed`) from the
    /// head belt — the one piece of directional feedback that reads as
    /// "that shot took the head off," on top of the intact corpse.
    /// Every gib part's `transform.localScale` mirrors the SAME
    /// `visualScale` (`ChaserVisualScale`/`GunnerVisualScale`) the corpse
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
    /// approximation) for a floor hit, and `Amount` is the contact height in
    /// both cases. `HandleBlocked` below only has to tell the two cases apart
    /// (`HitDir == 0` ⇒ floor, decal flat with an up-facing normal) and
    /// convert into `UnityEngine.Vector3`/`Quaternion` — no more re-deriving
    /// the normal analytically against `ArenaConfig.Obstacles`/the ring wall
    /// the way the pre-Task-21 `ComputeBlockNormal` had to (back when the
    /// event carried neither a normal nor a height).
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
        // Casing spawn height rides SimulationRunner.RenderMuzzleHeight (Task
        // 21, PC7 — was GameFeelConfig.MuzzleLiftY; the muzzle height is a
        // single source, Б1-веха fix: casings were born at ankle height
        // inside the doll mesh).
        const float CasingLateralOffset = 0.15f;
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
        // peak for safety margin (multiple mobs clustered, a hitstop-adjacent
        // frame catching up several ticks at once, etc.) — NOT SO fields:
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

        // T24-2 (app-nco vision, owner-approved Blender split): fraction of
        // kills that get the "mech explodes into every part" variant instead
        // of a whole corpse (+ at most one head gib on a headshot). Kept a
        // code const, not a GameFeelConfig field — cosmetic death-variant
        // RNG, not a hot-tweak feel number (class doc, same split
        // GibExplosionChunkCount used to draw before this task retired it).
        const float GibFullExplodeChance = 0.35f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
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

        RingBuffer<CasingView> _casings;
        RingBuffer<DecalProjector> _decals;
        RingBuffer<CorpseView> _corpses;
        RingBuffer<DashGlowView> _dashGlows;
        RingBuffer<GibView> _gibs;
        ObjectPool<ParticleSystem> _hitSparkPool;
        ObjectPool<ParticleSystem> _blockSparkPool;
        ObjectPool<ParticleSystem> _deathBurstPool;
        ObjectPool<ParticleSystem> _slideDustPool; // Task 22

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
            PrewarmParticlePool(_hitSparkPool, HitSparkPoolCapacity);
            PrewarmParticlePool(_blockSparkPool, BlockSparkPoolCapacity);
            PrewarmParticlePool(_deathBurstPool, DeathBurstPoolCapacity);
            PrewarmParticlePool(_slideDustPool, SlideDustPoolCapacity); // Task 22
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
                    if (e.Owner == ProjectileOwner.Player) SpawnCasing(in e);
                    break;
                case SimEventKind.ProjectileHit:
                    PlayParticle(_hitSparkPool, SimSpace.ToWorld(e.Pos), Quaternion.identity);
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
                case SimEventKind.DashRicocheted:
                    HandleRicocheted(in e);
                    break;
            }
        }

        void SpawnCasing(in SimEvent e)
        {
            Vector3 lateral = new Vector3(
                Random.Range(-CasingLateralOffset, CasingLateralOffset),
                _runner.RenderMuzzleHeight,
                Random.Range(-CasingLateralOffset, CasingLateralOffset));
            Vector3 pos = SimSpace.ToWorld(e.Pos) + lateral;
            // Eject to the shooter's RIGHT of the shot direction (e.Amount is the
            // projectile's sim-plane velocity angle, tick-exact — MuzzleFlashView's
            // contract): right = shot direction rotated -90° about world up.
            Vector3 right = new Vector3(Mathf.Sin(e.Amount), 0f, -Mathf.Cos(e.Amount));
            Vector3 impulse =
                right * Random.Range(_gameFeel.CasingEjectSpeedMin, _gameFeel.CasingEjectSpeedMax)
                + Vector3.up * Random.Range(_gameFeel.CasingImpulseUpMin, _gameFeel.CasingImpulseUpMax);
            Vector3 torque = Random.insideUnitSphere * _gameFeel.CasingTorqueScale;

            CasingView view = _casings.Rent();
            view.Spawn(pos, impulse, torque, _gameFeel.CasingPhysicsSeconds, _gameFeel.CasingScale);
        }

        void HandleBlocked(in SimEvent e)
        {
            // Floor vs wall/obstacle (class doc): HitDir is exactly zero for a
            // floor contact — ProjectileSystem's own gate (Task 7), not an
            // epsilon check.
            bool isFloor = e.HitDir.x == 0f && e.HitDir.y == 0f;
            Vector3 normal = isFloor ? Vector3.up : new Vector3(e.HitDir.x, 0f, e.HitDir.y);
            // Amount is the sim's own contact height for BOTH branches
            // (ProjectileSystem's HitBarrier/HitFloor cases share one
            // formula) — the event is now the sole home for height, same as
            // HitDir already is for the normal (class doc, PC4).
            Vector3 contactWorld = SimSpace.ToWorld(e.Pos) + Vector3.up * e.Amount;
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

        void HandleMobDied(in SimEvent e)
        {
            // В1/В2 fix-wave 2 (app-n6g item 2, BUG fix): same archetype-scale
            // read `ViewRegistry.SyncMobs` uses for the live `MobVisual.Bind`
            // call — `MobDied`'s own `MobType` is enough, no new SO field
            // (CorpseView.Spawn's own doc). T24-2: also the scale every
            // spawned gib part's transform.localScale mirrors (class doc).
            float visualScale = e.MobType == MobType.Chaser
                ? _gameFeel.ChaserVisualScale : _gameFeel.GunnerVisualScale;

            PlayParticle(_deathBurstPool, SimSpace.ToWorld(e.Pos), Quaternion.identity);

            // T24-2 (app-nco vision): a rolled fraction of kills explode into
            // every part instead of leaving a whole corpse — class doc has
            // the full variant split.
            if (Random.value < GibFullExplodeChance)
            {
                SpawnFullExplodeGibs(in e, visualScale);
                return;
            }

            Vector3 pos = SimSpace.ToWorld(e.Pos) + Vector3.up * CorpseLift;
            CorpseView corpse = _corpses.Rent();
            corpse.Spawn(pos, e.MobType, _gameFeel.CorpseGlowFadeSeconds, visualScale);

            if (e.Zone == HitZone.Head) SpawnHeadGib(in e, visualScale);
        }

        /// T24-2 (app-nco vision, owner-approved Blender split): the "mech
        /// explodes" death variant (`GibFullExplodeChance` roll in
        /// `HandleMobDied`) — every part of the dying mob's own archetype
        /// (`_chaserParts`/`_gunnerParts`) scatters from the event's own
        /// `Pos` (owner requirement, веха 3 — XY always rides `e.Pos`, never
        /// `ViewRegistry`/`MobView` state), each at a belt-derived height
        /// keyed off its own `GibView.ClassifyPart` kind (`PartHeight` below)
        /// — the SAME `World.Config.Chaser`/`Gunner` zone-geometry belts
        /// `ProjectileSystem`'s hit-zone classification and the `AimProxy_*`
        /// colliders already read. Impulse direction is upward-biased
        /// (`Random.onUnitSphere`, reflected onto the upper hemisphere when
        /// it rolls downward — a part launching straight into the floor
        /// reads as a bug, not a death) at `GameFeelConfig.GibExplosionSpeed`.
        /// NO corpse spawns in this variant — every part IS the "corpse"
        /// here (class doc).
        void SpawnFullExplodeGibs(in SimEvent e, float visualScale)
        {
            (Mesh[] parts, Material material) = e.MobType == MobType.Chaser
                ? (_chaserParts, _chaserPartMaterial) : (_gunnerParts, _gunnerPartMaterial);
            MobSimConfig archetype = e.MobType == MobType.Chaser
                ? _runner.World.Config.Chaser : _runner.World.Config.Gunner;
            Vector3 worldPos = SimSpace.ToWorld(e.Pos);
            float settleSeconds = _gameFeel.GibPhysicsSeconds;

            for (int i = 0; i < parts.Length; i++)
            {
                Mesh part = parts[i];
                float height = PartHeight(GibView.ClassifyPart(part.name), in archetype);
                Vector3 dir = Random.onUnitSphere;
                if (dir.y < 0f) dir.y = -dir.y; // upward-biased (class doc)
                Vector3 impulse = dir * _gameFeel.GibExplosionSpeed;

                GibView gib = _gibs.Rent();
                gib.SettleSeconds = settleSeconds;
                gib.Spawn(worldPos + Vector3.up * height, impulse, part, material, visualScale);
            }
        }

        /// T24-2: the single head-part gib a headshot kill adds ON TOP OF the
        /// otherwise-intact corpse (`HandleMobDied`'s `e.Zone == HitZone.Head`
        /// branch, class doc) — launched along the killing blow's own
        /// `HitDir`, from the head belt, same `GibHeadImpulseSpeed`/height
        /// formula Task 24's original headshot chunk used, now carrying the
        /// archetype's real head mesh instead of a primitive.
        void SpawnHeadGib(in SimEvent e, float visualScale)
        {
            (Mesh[] parts, Material material) = e.MobType == MobType.Chaser
                ? (_chaserParts, _chaserPartMaterial) : (_gunnerParts, _gunnerPartMaterial);
            Mesh headPart = FindPart(parts, GibPartKind.Head);
            // Defensive only — every archetype ships a Head part and
            // StageOneSceneBootstrap validates that at wiring time; a live
            // match should never actually hit this.
            if (headPart == null) return;

            MobSimConfig archetype = e.MobType == MobType.Chaser
                ? _runner.World.Config.Chaser : _runner.World.Config.Gunner;
            float headHeight = (archetype.BodyTop + archetype.HeadTop) * 0.5f;
            Vector3 headImpulse = _gameFeel.GibHeadImpulseSpeed * SimSpace.ToWorld(e.HitDir);

            GibView headGib = _gibs.Rent();
            headGib.SettleSeconds = _gameFeel.GibPhysicsSeconds;
            headGib.Spawn(SimSpace.ToWorld(e.Pos) + Vector3.up * headHeight, headImpulse,
                headPart, material, visualScale);
        }

        static Mesh FindPart(Mesh[] parts, GibPartKind kind)
        {
            for (int i = 0; i < parts.Length; i++)
                if (GibView.ClassifyPart(parts[i].name) == kind) return parts[i];
            return null;
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

        void SpawnDashGlow(in SimEvent e)
        {
            DashGlowView glow = _dashGlows.Rent();
            glow.Spawn(SimSpace.ToWorld(e.Pos), _gameFeel.DashGlowSeconds, _gameFeel.DashGlowSize);
        }

        /// Task 22 (spec brief QA13/QC3): reuses the existing block-spark pool/
        /// prefab outright — no dedicated ricochet-spark asset, and no new
        /// burst-count field, `BlockSparkBurstCount` is already baked into the
        /// prefab (GameFeelConfig class doc: "RicochetSparkCount... deliberately
        /// NOT added — ricochet sparks reuse the baked BlockSparkBurstCount").
        /// Guard mirrors `HandleBlocked`'s own zero-normal check above:
        /// `Quaternion.LookRotation(Vector3.zero, ...)` logs an error (ГЕЙТ-ЛОГ)
        /// rather than throwing, so this can't rely on the sim never emitting a
        /// degenerate contact normal — Presentation checks for itself.
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
