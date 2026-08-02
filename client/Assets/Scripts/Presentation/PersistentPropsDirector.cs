using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Rendering.Universal;

namespace Ring.Presentation
{
    /// Persistent cosmetics — shell casings, impact decals, corpses, and the
    /// three pooled spark/burst particle systems (Task 27, spec §3.11/§3.12,
    /// Приложение П). Slots into `SimEventRouter`'s fan-out (П-1) between
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
    /// Pooling split (Приложение П-7): casings/decals/corpses have no "done
    /// with it" moment during a live match (spec: "живут до конца захода") —
    /// they use the single shared `RingBuffer&lt;T&gt;` (FIFO, oldest
    /// overwritten once full), one instance per kind, never three copies of
    /// the same logic. The three spark/burst particle systems ARE ordinary
    /// "rent it, it finishes on its own, give it back" objects — they use
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
    /// `ProjectileBlocked`'s decal/block-spark normal is computed purely
    /// analytically from `ArenaConfig` (spec/resolution: "нормаль — от
    /// ближайшего препятствия/стены арены из World.Config — вычисли
    /// аналитически") — the contact point (a swept-circle collision result,
    /// `ProjectileSystem`) sits at combined-radius distance from whichever
    /// obstacle/the outer wall it actually hit; `ComputeBlockNormal` below
    /// picks whichever candidate surface (each of `ArenaConfig.Obstacles`, or
    /// the ring wall) the contact's distance best matches and returns the
    /// outward-facing normal for that surface. All arithmetic stays in
    /// Unity's own `Vector3` (world space, Y=0 plane — same convention
    /// `GreyboxBuilder.BuildObstacles` already uses for
    /// `ArenaConfig.Obstacle.Pos`, a plain `Vector2`) rather than
    /// `Unity.Mathematics.float2` specifically so this file never needs both
    /// `Unity.Mathematics` and `UnityEngine` `Random` in scope at once (the
    /// two `Random` types would otherwise collide).
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
        const float CasingSpawnLift = 0.25f;
        const float CasingLateralOffset = 0.15f;
        const float DecalHeightOffset = 1f;
        const float DecalNearOffset = 0.1f;

        // Half the default primitive Capsule's diameter (radius 0.5, untouched
        // by `GetOrCreateCorpsePrefab` — same unscaled capsule `MobView`'s own
        // prefab uses) — lets the "lying on its side" capsule rest flush on
        // the floor (world Y=0, `GreyboxBuilder`'s floor top surface) instead
        // of clipping halfway through it.
        const float CorpseLift = 0.5f;

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

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] ArenaConfig _arena;
        [SerializeField] CasingView _casingPrefab;
        [SerializeField] DecalProjector _decalPrefab;
        [SerializeField] CorpseView _corpsePrefab;
        [SerializeField] ParticleSystem _hitSparkPrefab;
        [SerializeField] ParticleSystem _blockSparkPrefab;
        [SerializeField] ParticleSystem _deathBurstPrefab;

        RingBuffer<CasingView> _casings;
        RingBuffer<DecalProjector> _decals;
        RingBuffer<CorpseView> _corpses;
        ObjectPool<ParticleSystem> _hitSparkPool;
        ObjectPool<ParticleSystem> _blockSparkPool;
        ObjectPool<ParticleSystem> _deathBurstPool;

        void Awake()
        {
            Physics.IgnoreLayerCollision(CasingsLayer, CasingsLayer, true);

            _casings = new RingBuffer<CasingView>(_gameFeel.MaxCasings, CreateCasing);
            _decals = new RingBuffer<DecalProjector>(_gameFeel.MaxDecals, CreateDecal);
            _corpses = new RingBuffer<CorpseView>(_gameFeel.MaxCorpses, CreateCorpse);
            _casings.Prewarm();
            _decals.Prewarm();
            _corpses.Prewarm();

            _hitSparkPool = CreateParticlePool(_hitSparkPrefab, HitSparkPoolCapacity);
            _blockSparkPool = CreateParticlePool(_blockSparkPrefab, BlockSparkPoolCapacity);
            _deathBurstPool = CreateParticlePool(_deathBurstPrefab, DeathBurstPoolCapacity);
            PrewarmParticlePool(_hitSparkPool, HitSparkPoolCapacity);
            PrewarmParticlePool(_blockSparkPool, BlockSparkPoolCapacity);
            PrewarmParticlePool(_deathBurstPool, DeathBurstPoolCapacity);
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
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's
        /// buffer (П-1 fan-out).
        public void HandleEvent(in SimEvent e)
        {
            switch (e.Kind)
            {
                case SimEventKind.ProjectileFired:
                    SpawnCasing(in e);
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
            }
        }

        void SpawnCasing(in SimEvent e)
        {
            Vector3 lateral = new Vector3(
                Random.Range(-CasingLateralOffset, CasingLateralOffset),
                CasingSpawnLift,
                Random.Range(-CasingLateralOffset, CasingLateralOffset));
            Vector3 pos = SimSpace.ToWorld(e.Pos) + lateral;
            float sideMax = _gameFeel.CasingImpulseSideMax;
            Vector3 impulse = new Vector3(
                Random.Range(-sideMax, sideMax),
                Random.Range(_gameFeel.CasingImpulseUpMin, _gameFeel.CasingImpulseUpMax),
                Random.Range(-sideMax, sideMax));
            Vector3 torque = Random.insideUnitSphere * _gameFeel.CasingTorqueScale;

            CasingView view = _casings.Rent();
            view.Spawn(pos, impulse, torque, _gameFeel.CasingPhysicsSeconds);
        }

        void HandleBlocked(in SimEvent e)
        {
            Vector3 contactFlat = SimSpace.ToWorld(e.Pos);
            Vector3 normal = ComputeBlockNormal(contactFlat, _arena);
            Vector3 contactWorld = contactFlat + Vector3.up * DecalHeightOffset;

            DecalProjector decal = _decals.Rent();
            decal.gameObject.SetActive(true);
            decal.transform.SetPositionAndRotation(
                contactWorld + normal * DecalNearOffset,
                Quaternion.LookRotation(-normal, Vector3.up));

            PlayParticle(_blockSparkPool, contactWorld, Quaternion.LookRotation(normal, Vector3.up));
        }

        void HandleMobDied(in SimEvent e)
        {
            Vector3 pos = SimSpace.ToWorld(e.Pos) + Vector3.up * CorpseLift;

            CorpseView corpse = _corpses.Rent();
            corpse.Spawn(pos, e.MobType, _gameFeel.CorpseGlowFadeSeconds);

            PlayParticle(_deathBurstPool, SimSpace.ToWorld(e.Pos), Quaternion.identity);
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

        /// See class doc — analytic normal for a `ProjectileBlocked` contact
        /// point. `contactFlat` is world-space with Y already pinned to 0 by
        /// `SimSpace.ToWorld`.
        static Vector3 ComputeBlockNormal(Vector3 contactFlat, ArenaConfig arena)
        {
            float bestError = Mathf.Abs(contactFlat.magnitude - arena.Radius);
            Vector3 bestNormal = SafeNormalize(-contactFlat); // ring wall: inward, toward center

            ArenaConfig.Obstacle[] obstacles = arena.Obstacles;
            if (obstacles != null)
            {
                for (int i = 0; i < obstacles.Length; i++)
                {
                    Vector3 center = new Vector3(obstacles[i].Pos.x, 0f, obstacles[i].Pos.y);
                    Vector3 delta = contactFlat - center;
                    float error = Mathf.Abs(delta.magnitude - obstacles[i].Radius);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestNormal = SafeNormalize(delta); // obstacle: outward, away from its center
                    }
                }
            }
            return bestNormal;
        }

        static Vector3 SafeNormalize(Vector3 v) => v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.right;
    }
}
