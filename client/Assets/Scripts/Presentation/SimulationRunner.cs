using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ring.Presentation
{
    /// Sole owner of `SimulationWorld.Tick` (spec §3.2): accumulates
    /// `Time.unscaledDeltaTime` into fixed 30 Hz ticks via `FixedStepAccumulator`.
    /// `Time.timeScale` is never touched anywhere in the project — this is the only
    /// clock source for the sim, so pausing/slow-mo must never route through it.
    ///
    /// Task 28 fix-round (review #2, Low): `[DefaultExecutionOrder(-50)]` makes
    /// this run its own `Update` before every default-order (0) script's —
    /// `MuzzleFlashView`/`AudioDirector`'s `Update` read `LastFrameInput`/
    /// `RenderCurr` (below) and need THIS frame's values, not whatever was left
    /// over from last frame; Unity does not otherwise guarantee `Update` order
    /// among same-order scripts. Verified safe for every other `Update`-phase
    /// reader of this runner: `GameFeelDirector.Update` only decays its own
    /// unscaledDeltaTime-driven timers (hitstop/trauma/shake/vignette), never
    /// reads `_runner`'s per-frame state; `ViewRegistry`/`PlayerView`/`CameraRig`/
    /// `HudController`/`CrosshairView` all read via `LateUpdate` (already
    /// guaranteed to run after every `Update`, order or no); `DevOverlay`/
    /// `PauseController`/`DeathOverlayController`'s own `Update`s only poll
    /// keyboard/dev state and call `Restart`/toggle `Paused` — nothing in the
    /// project currently depends on running BEFORE this class's `Update` within
    /// the same frame, so pinning this one earlier is a strict improvement, not
    /// a trade-off.
    [DefaultExecutionOrder(-50)]
    public sealed class SimulationRunner : MonoBehaviour
    {
        /// Task 28 fix-round (review #3, Minor): the shared TTL both
        /// `MuzzleFlashView` and `AudioDirector`'s ImmediateMuzzleFeedback
        /// prediction latches use — was two separate `PredictedTtlSeconds`
        /// constants (one per class) that could silently drift apart; lives
        /// here (not `GameFeelConfig`) because it is a tick-timing correctness
        /// constant tied to this class's own 30Hz accumulator (spec §3.2), not
        /// a game-feel number the owner would hot-tweak on the milestone-4
        /// playtest. ~1.5 tick periods (33ms/tick @ 30Hz): long enough for the
        /// matching real tick to land and flush even under a slightly-late
        /// accumulator crossing, short enough to bound how long a false
        /// prediction lingers (see the two consumers' own docs for specifics).
        public const float ImmediatePredictionTtlSeconds = 0.05f;

        [SerializeField] HeroConfig _hero;
        [SerializeField] WeaponConfig _weapon;
        [SerializeField] MobConfig _chaser;
        [SerializeField] MobConfig _gunner;
        [SerializeField] WaveConfig _wave;
        [SerializeField] ArenaConfig _arena;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] CameraConfig _camera;
        [SerializeField] InputActionAsset _actionsAsset;
        [SerializeField] AimProvider _aimProvider;

        readonly FixedStepAccumulator _acc = new FixedStepAccumulator();
        InputSampler _sampler;
        SimulationWorld _world;
        bool _pendingApplyConfig;

        public RenderSnapshot Prev, Curr;
        public float Alpha;

        /// Task 28 (spec §3.11, ImmediateMuzzleFeedback): the exact `SimInput`
        /// this render frame's `Update` sampled below — `MuzzleFlashView`/
        /// `AudioDirector`'s per-frame prediction reads `FireHeld` off THIS
        /// instead of calling `InputSampler.SampleFrame()` a second time, which
        /// would double-sample Input System and could double-latch the dash edge
        /// (`InputSampler._dashLatch`, spec §3.8 — a same-frame dash press must
        /// only ever be consumed once).
        public SimInput LastFrameInput { get; private set; }

        // Task 25 (Приложение П-7): the SOLE point every interpolating view
        // (ViewRegistry, PlayerView, CameraRig) reads — `Prev`/`Curr`/`Alpha`
        // above are the raw double-buffer this class itself owns and keeps
        // advancing every tick no matter what (the simulation is never paused
        // for hitstop, spec §3.2/§3.11); `RenderPrev`/`RenderCurr`/`RenderAlpha`
        // are what actually reaches the screen, and `GameFeelDirector` is the
        // only thing that ever calls `FreezeRender`/`UnfreezeRender` to make the
        // two diverge. A `RenderSnapshot` is a mutable class this runner recycles
        // every tick (`(Prev, Curr) = (Curr, Prev)` then `CaptureSnapshot(Curr)`
        // overwrites whichever object that lands on) — so freezing "the render
        // pair" can't just mean holding onto whatever `Curr` currently points at,
        // that same object gets overwritten again within a couple of ticks.
        // `_renderPrevFrozen`/`_renderCurrFrozen` are separate, permanently-owned
        // buffers `FreezeRender` deep-copies the live pair into instead.
        RenderSnapshot _renderPrevFrozen, _renderCurrFrozen;
        bool _renderFrozen;
        float _catchUpRemaining, _catchUpDuration;

        public RenderSnapshot RenderPrev =>
            _renderFrozen || _catchUpRemaining > 0f ? _renderPrevFrozen : Prev;
        public RenderSnapshot RenderCurr => _renderFrozen ? _renderCurrFrozen : Curr;
        public float RenderAlpha { get; private set; }

        /// Interpolated player ground position of the RENDER pair (П-7): the single
        /// shared formula for PlayerView/PlayerVisual/ViewRegistry — screen-space
        /// consumers never re-derive it and never read each other's transforms.
        public Vector3 RenderPlayerWorldPos => Vector3.Lerp(
            SimSpace.ToWorld(RenderPrev.Player.Pos),
            SimSpace.ToWorld(RenderCurr.Player.Pos), RenderAlpha);

        /// Task 21 (PC7 — single home of the muzzle-height ternary): the exact
        /// slide-aware pick `WeaponSystem.Update` uses for the authoritative
        /// shot's own muzzle height (`SlideTimer > 0 ? SlideMuzzleHeight :
        /// MuzzleHeight`). Every Presentation-layer consumer of the hero's
        /// muzzle height (`MuzzleFlashView`'s prediction and player-branch
        /// burst, `PersistentPropsDirector.SpawnCasing`, `AimRayView`'s ray
        /// origin) reads THIS instead of re-deriving the ternary locally —
        /// previously each one duplicated it ad hoc (`AimRayView`'s own,
        /// pre-Task-21 doc explicitly flagged this as a placeholder). Reads
        /// off `RenderCurr` — the last COMPLETE tick's state — same
        /// client-boundary rule as `WouldFireThisFrame` below.
        public float RenderMuzzleHeight => RenderCurr.Player.SlideTimer > 0f
            ? World.Config.Hero.SlideMuzzleHeight : World.Config.Hero.MuzzleHeight;

        public SimulationWorld World => _world;
        public long Seed { get; private set; }
        public bool ConfigTweaked;

        /// Task 28 (spec §3.11, ImmediateMuzzleFeedback): true when this frame's
        /// cached input predicts the weapon fires on the NEXT tick — single
        /// source of truth for `MuzzleFlashView`/`AudioDirector`'s per-frame
        /// prediction, so the two components' decisions can never drift apart.
        /// Mirrors `WeaponSystem.Update`'s own `canFire` gate exactly (`FireHeld
        /// && Alive && FireCooldown <= 0 && (CanFireWhileDash || DashTimer <= 0)
        /// && (CanFireWhileSlide || SlideTimer <= 0)` — fix-round review #4: an
        /// earlier revision of this doc paraphrased the gate without the
        /// `FireCooldown <= 0` term even though the code below always had it;
        /// the property itself was correct, only the prose was incomplete;
        /// Task 21 adds the `CanFireWhileSlide` term, previously missing from
        /// both the code and this doc), but reads it off
        /// `RenderCurr` — the last COMPLETE tick's state — instead of any
        /// Simulation internals, per client/CLAUDE.md's "клиент не решает
        /// игровые исходы" boundary (this predicts client-local cosmetics only;
        /// the authoritative shot still comes from the tick's own
        /// `ProjectileFired` event).
        public bool WouldFireThisFrame
        {
            get
            {
                PlayerState p = RenderCurr.Player;
                return LastFrameInput.FireHeld && p.Alive && p.FireCooldown <= 0f
                    && (_weapon.CanFireWhileDash || p.DashTimer <= 0f)
                    && (_weapon.CanFireWhileSlide || p.SlideTimer <= 0f);
            }
        }

        bool _paused;

        /// Task 24 (spec Interfaces): the sole pause gate for the whole project —
        /// `Time.timeScale` is never touched (class doc above). Setting this true
        /// zeroes only the accumulator's phase (`ResetAccumulatorOnly` — review
        /// round: plain `Reset()` would also zero `DroppedTime`, silently erasing
        /// the dropped-time diagnostic DevOverlay surfaces every time the owner
        /// pauses, which is exactly the "silent loss" spec §3.7 forbids) so no
        /// backlog of real time is waiting to burst-tick once unpaused; from that
        /// point on, `Update` skips input sampling and tick advancement entirely
        /// — `Alpha` is left exactly as it was at the moment pause started, so
        /// interpolated views hold their last visual position instead of
        /// snapping toward `Prev`. Setting it back to false does not itself
        /// resume ticking on the same frame; `Update` simply stops
        /// early-returning starting next frame.
        public bool Paused
        {
            get => _paused;
            set
            {
                if (_paused == value) return;
                _paused = value;
                if (_paused) _acc.ResetAccumulatorOnly();
            }
        }

        /// DevOverlay's seam into the accumulator's dropped-time counter (Task 24
        /// Приложение П-6) — `FixedStepAccumulator` itself has no UnityEngine
        /// dependency and isn't otherwise exposed outside this class. Survives
        /// pause (see `Paused` above); only a full match restart (`Restart`'s
        /// plain `_acc.Reset()`) zeroes it.
        public float AccumulatorDroppedTime => _acc.DroppedTime;

        public event System.Action TicksFlushed;
        public event System.Action WorldRestarted;

        /// Fires once per individual tick (tick number, `StateHash()` at that
        /// tick) — Task 24 review round, П-9's tick→hash dev-log: `TicksFlushed`
        /// only fires once per RENDER frame (after a whole multi-tick catch-up
        /// batch), which would silently skip every tick but the last one in a
        /// batch — exactly the catch-up hitches most likely to hide a
        /// determinism divergence. This is a distinct event from `TicksFlushed`,
        /// not a new subscriber to it, so it doesn't touch П-1's "sole
        /// `TicksFlushed` subscriber is `SimEventRouter`" invariant.
        /// `StateHash()` walks every live mob/projectile — not free — so the
        /// call below is guarded on `TickAdvanced != null`: with no subscriber
        /// (the common case — dev-only, logging toggle off), this costs one
        /// null check per tick and nothing else.
        public event System.Action<int, ulong> TickAdvanced;

        void Awake()
        {
            _sampler = new InputSampler(_actionsAsset, _aimProvider);
            RestartNewSeed();
        }

        void OnEnable()
        {
            _sampler?.Enable();
            // Task 28 (spec §3.9): the hot-tweak subscription itself is a dev
            // workflow, not a shipped gameplay feature (unlike ImmediateMuzzleFeedback
            // below, which stays unguarded) — OnValidate never fires outside the
            // Editor anyway, but guarding the subscription keeps a Release build
            // from ever wiring up to an event no production code raises.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            RingDataChanged.Changed += RequestApplyConfig;
#endif
        }

        void OnDisable()
        {
            _sampler?.Disable();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            RingDataChanged.Changed -= RequestApplyConfig;
#endif
        }

        void Update()
        {
            if (_pendingApplyConfig)
            {
                _pendingApplyConfig = false;
                SimConfig next = SimConfigBuilder.Build(_hero, _weapon, _chaser, _gunner, _wave, _arena);
                try
                {
                    _world.ApplyConfig(next);
                    ConfigTweaked = true;
                }
                catch (System.ArgumentException)
                {
                    // Arena topology changed under hot-tweak — spec §3.9 forbids in-place
                    // migration for that case; the only safe recovery is a full restart.
                    Debug.Log("SimulationRunner: arena topology changed under hot-tweak " +
                        "(ArenaConfig.Radius/Obstacles) — ApplyConfig rejected it, restarting " +
                        "with the same seed instead.");
                    Restart(Seed);
                }
            }

            if (_paused) return;

            SimInput frame = _sampler.SampleFrame();
            LastFrameInput = frame; // Task 28 — see the property's own doc above.
            int ticks = _acc.Advance(Time.unscaledDeltaTime);
            for (int i = 0; i < ticks; i++)
            {
                _world.Tick(SimInputFrame.ForTick(frame, i)); // защёлка — первому тику
                (Prev, Curr) = (Curr, Prev);
                _world.CaptureSnapshot(Curr);
                // Guarded — see TickAdvanced's doc comment: StateHash() is only
                // ever computed when something is actually subscribed.
                if (TickAdvanced != null) TickAdvanced.Invoke(_world.CurrentTick, _world.StateHash());
            }
            Alpha = _acc.Alpha;
            UpdateRenderAlpha();
            if (ticks > 0)
            {
                TicksFlushed?.Invoke();
                _world.ClearEvents();
                _sampler.ClearLatches();
            }
        }

        /// Advances `RenderAlpha` every render frame (Task 25, Приложение П-7).
        /// Three mutually exclusive states: pinned while `_renderFrozen`
        /// (`FreezeRender` is active — `GameFeelDirector` hasn't unfrozen yet);
        /// easing 0→1 over `_catchUpDuration` while `_catchUpRemaining > 0`
        /// (`UnfreezeRender` just ran with a non-zero catch-up window); plain
        /// pass-through to the live `Alpha` otherwise. See the `RenderPrev`/
        /// `RenderCurr`/`RenderAlpha` doc block above for why a frozen picture
        /// needs its own buffers instead of just holding `Alpha` still.
        void UpdateRenderAlpha()
        {
            if (_renderFrozen) return;

            if (_catchUpRemaining > 0f)
            {
                _catchUpRemaining -= Time.unscaledDeltaTime;
                RenderAlpha = _catchUpDuration > 0f
                    ? 1f - Mathf.Clamp01(_catchUpRemaining / _catchUpDuration)
                    : 1f;
                if (_catchUpRemaining <= 0f) _catchUpRemaining = 0f;
                return;
            }

            RenderAlpha = Alpha;
        }

        /// `GameFeelDirector`'s hook for a `FullFrame`-scope hitstop trigger
        /// (Приложение П-7) — deep-copies the CURRENT live pair into the frozen
        /// buffers and pins `RenderAlpha` at today's live value, then flips
        /// `RenderPrev`/`RenderCurr`/`RenderAlpha` over to that frozen state.
        /// Safe to call again while already frozen (a follow-up hit resetting
        /// the hitstop timer, spec Interfaces "переустанавливается, не
        /// суммируется") — re-copying re-pins the frozen picture to the newest
        /// moment instead of leaving it stuck on the FIRST hit in a chain.
        public void FreezeRender()
        {
            CopySnapshot(Prev, _renderPrevFrozen);
            CopySnapshot(Curr, _renderCurrFrozen);
            RenderAlpha = Alpha;
            _renderFrozen = true;
            _catchUpRemaining = 0f; // a fresh freeze cancels any catch-up in flight
        }

        /// Ends a `FreezeRender` freeze. `catchUpSeconds <= 0` (`GameFeelDirector.
        /// ForceEndHitstop`, e.g. on `PlayerDied`) snaps straight back to the live
        /// pair; otherwise `RenderPrev` holds at the last frozen picture while
        /// `RenderCurr` immediately starts tracking the live (still-advancing)
        /// `Curr` again and `RenderAlpha` eases 0→1 over `catchUpSeconds` instead
        /// of jumping to whatever `Alpha` reads that frame — several ticks can
        /// have landed while the frame was pinned, so an instant snap would read
        /// as every mob/projectile popping forward in a single frame.
        public void UnfreezeRender(float catchUpSeconds)
        {
            if (!_renderFrozen) return;
            _renderFrozen = false;
            if (catchUpSeconds > 0f)
            {
                // The pose actually on screen the instant before unfreezing is
                // `_renderCurrFrozen` (RenderCurr while frozen) — re-anchor
                // `_renderPrevFrozen` (RenderPrev's frozen backing store) to it so
                // the catch-up blends FROM there, not from the older `Prev` half
                // of the pair that was frozen alongside it.
                CopySnapshot(_renderCurrFrozen, _renderPrevFrozen);
                _catchUpDuration = catchUpSeconds;
                _catchUpRemaining = catchUpSeconds;
            }
            else
            {
                _catchUpRemaining = 0f;
            }
        }

        /// Deep-copies one tick's worth of render data between two
        /// `RenderSnapshot` instances of matching capacity (`FreezeRender`/
        /// `UnfreezeRender` above — both `to` buffers are allocated in `Restart`
        /// with this same runner's `ArenaConfig` caps, same as `Prev`/`Curr`
        /// themselves). Every field on `RenderSnapshot` is either a struct or a
        /// struct array, so plain assignment/indexed-copy IS the deep copy —
        /// nothing here reaches into `Ring.Simulation.Core` beyond reading its
        /// already-public fields (Simulation itself is untouched by this task).
        /// Stage 2 Task 4: `Player` moved from a plain field to
        /// `Players`/`PlayerCount` (plus the new `LocalPlayerIndex`) — all
        /// three copied explicitly here, same indexed-copy pattern as
        /// `Mobs`/`Projectiles` below; `Stats` itself is unchanged (still a
        /// single plain field in this task).
        static void CopySnapshot(RenderSnapshot from, RenderSnapshot to)
        {
            to.Tick = from.Tick;
            to.PlayerCount = from.PlayerCount;
            for (int i = 0; i < from.PlayerCount; i++) to.Players[i] = from.Players[i];
            to.LocalPlayerIndex = from.LocalPlayerIndex;
            to.MobCount = from.MobCount;
            for (int i = 0; i < from.MobCount; i++) to.Mobs[i] = from.Mobs[i];
            to.ProjectileCount = from.ProjectileCount;
            for (int i = 0; i < from.ProjectileCount; i++) to.Projectiles[i] = from.Projectiles[i];
            to.Wave = from.Wave;
            to.Stats = from.Stats;
        }

        public void Restart(long seed)
        {
            Seed = seed;
            SimConfig cfg = SimConfigBuilder.Build(_hero, _weapon, _chaser, _gunner, _wave, _arena);
            _world = new SimulationWorld(seed, cfg);
            Prev = new RenderSnapshot(cfg.Arena);
            Curr = new RenderSnapshot(cfg.Arena);
            _world.CaptureSnapshot(Prev);
            _world.CaptureSnapshot(Curr);
            _acc.Reset();
            Alpha = 0f;
            _renderPrevFrozen = new RenderSnapshot(cfg.Arena);
            _renderCurrFrozen = new RenderSnapshot(cfg.Arena);
            _renderFrozen = false;
            _catchUpRemaining = 0f;
            RenderAlpha = 0f;
            ConfigTweaked = false;
            _pendingApplyConfig = false;
            // A fresh match never starts paused (Task 24) — covers a restart
            // requested while paused (dev-overlay forced-seed restart, or the
            // death overlay's R/Shift+R firing during an unlikely death+pause
            // overlap) without every restart call-site having to remember to
            // clear this itself.
            _paused = false;
            WorldRestarted?.Invoke();
        }

        // Environment.TickCount64 does not exist under this project's API
        // Compatibility Level (.NET Standard 2.1 — CS0117); UtcNow.Ticks is the
        // built-in equivalent (100ns-resolution, monotonic enough for a dev reseed).
        public void RestartNewSeed() => Restart(System.DateTime.UtcNow.Ticks);

        public void RequestApplyConfig() => _pendingApplyConfig = true;
    }
}
