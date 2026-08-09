using Ring.Data;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ring.Presentation
{
    /// The single facade the whole Presentation layer reads the simulation
    /// through, and the sole driver of one render frame's worth of it (spec
    /// §3.2/§3.12). Stage 2 Task 43 split this class in two along the line
    /// between producing state and showing it: `ISimBackend` now owns the
    /// world, the fixed-step accumulator and the `Prev`/`Curr` pair, while this
    /// class keeps the balance ScriptableObjects, input sampling, the hitstop
    /// freeze layer, the pause gate and every event the views subscribe to. The
    /// backend seam exists because the networked one (Task 44) has no world at
    /// all — see `ISimBackend`'s own doc; the split is deliberately invisible
    /// from outside, since seventeen classes hold a `SimulationRunner`
    /// reference and read it by member name.
    ///
    /// `Time.unscaledDeltaTime` is fed to the backend here and nowhere else, and
    /// `Time.timeScale` is never touched anywhere in the project — this is the
    /// only clock source for the sim, so pausing/slow-mo must never route
    /// through it.
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
        // Stage 2 Task 22: seventh SimConfigBuilder.Build() parameter.
        [SerializeField] VisibilityConfig _visibility;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] CameraConfig _camera;
        [SerializeField] InputActionAsset _actionsAsset;
        [SerializeField] AimProvider _aimProvider;

        /// The state producer behind the facade (Task 43). Not `readonly`: the
        /// local backend is the only one this task ships, but which backend a
        /// session runs on is Task 44's decision, and the field initializer is
        /// what keeps `Ready` answerable from the very first frame — a view's
        /// `Awake`/`OnGUI` can run before this component's own `Awake`, and the
        /// guards it replaced (`World == null`) tolerated exactly that.
        ISimBackend _backend = new LocalSimBackend();

        InputSampler _sampler;
        bool _pendingApplyConfig;

        /// The raw tick double buffer, straight off the backend (Task 43 turned
        /// these from fields the loop above wrote into pass-throughs; the names
        /// are unchanged because the views read them by name). Views read
        /// `RenderPrev`/`RenderCurr`/`RenderAlpha` below instead — see the П-7
        /// block there for why the two pairs differ.
        public RenderSnapshot Prev => _backend.Prev;

        public RenderSnapshot Curr => _backend.Curr;

        public float Alpha => _backend.Alpha;

        /// Whether the backend has state to show (Р66) — what the seven views
        /// that used to test `World == null` ask now. The world itself is no
        /// longer exposed: a networked backend has none, so any view holding a
        /// `SimulationWorld` reference would have been writing code that cannot
        /// run in the mode this stage is building.
        public bool Ready => _backend.Ready;

        /// The single config source for the whole Presentation layer (Р87). By
        /// value, like `SimulationWorld.Config` it forwards to.
        public SimConfig Config => _backend.Config;

        /// The simulation's own tick counter — dev overlay only.
        public int CurrentTick => _backend.CurrentTick;

        /// This flush's event buffer, read by `SimEventRouter` inside
        /// `TicksFlushed` and dropped right after it returns (see `Update`).
        public int EventCount => _backend.EventCount;

        public SimEvent GetEvent(int index) => _backend.GetEvent(index);

        /// Dev-overlay diagnostics (Приложение П-6/П-9). `HasStateHash` is false
        /// on a backend for which the hash is a server-side quantity — the
        /// overlay prints a dash then rather than an invented number.
        public bool HasStateHash => _backend.HasStateHash;

        public ulong StateHash => _backend.StateHash;

        public int DroppedEvents => _backend.DroppedEvents;

        /// Whether the dev overlay may offer its spawn buttons at all (CR 3).
        public bool CanDevSpawnMob => _backend.CanDevSpawnMob;

        public void DevSpawnMob(MobType type, float2 pos) => _backend.DevSpawnMob(type, pos);

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
            ? Config.Hero.SlideMuzzleHeight : Config.Hero.MuzzleHeight;

        public long Seed { get; private set; }
        public bool ConfigTweaked;

        /// Task 28 (spec §3.11, ImmediateMuzzleFeedback): true when this frame's
        /// cached input predicts the weapon fires on the NEXT tick — single
        /// source of truth for `MuzzleFlashView`/`AudioDirector`'s per-frame
        /// prediction, so the two components' decisions can never drift apart.
        ///
        /// CALLS the authoritative gate rather than restating it (Task 43): this
        /// used to be a hand-written copy of `WeaponSystem`'s terms reading
        /// `_weapon` (the SO) directly, and `WouldFireThisTick`'s own doc had
        /// already dated the defect that copy carried — it tested
        /// `FireCooldown <= 0f` where the simulation tests
        /// `(FireCooldown - TickDt) <= 0f`, so the half-open window
        /// `(0, TickDt]` predicted "no shot" for a tick that then fired. Both
        /// halves of this property now come off `Config` (Р87), the same built
        /// `SimConfig` the authoritative path reads.
        ///
        /// Still evaluated against `RenderCurr` — the last COMPLETE tick's state
        /// — rather than any Simulation internals, per client/CLAUDE.md's
        /// "клиент не решает игровые исходы" boundary: this predicts
        /// client-local cosmetics only, and the authoritative shot still arrives
        /// as the tick's own `ProjectileFired` event. `Ready` leads because
        /// `Config` reads `default` before the first `Restart`, and a zeroed
        /// `WeaponSimConfig` is not a state worth asking the gate about.
        public bool WouldFireThisFrame => Ready
            && WeaponSystem.WouldFireThisTick(RenderCurr.Player, LastFrameInput, Config.Weapon);

        bool _paused;

        /// Task 24 (spec Interfaces): the sole pause gate for the whole project —
        /// `Time.timeScale` is never touched (class doc above). From the moment
        /// it goes true, `Update` skips input sampling and tick advancement
        /// entirely — `Alpha` is left exactly as it was at the moment pause
        /// started (the backend latches it rather than deriving it live), so
        /// interpolated views hold their last visual position instead of
        /// snapping toward `Prev`. The backend is told separately
        /// (`OnPausedChanged`) so it can settle its own clock; what pause MEANS
        /// for the screen is decided here and only here. Setting it back to
        /// false does not itself resume ticking on the same frame; `Update`
        /// simply stops early-returning starting next frame.
        public bool Paused
        {
            get => _paused;
            set
            {
                if (_paused == value) return;
                _paused = value;
                _backend.OnPausedChanged(_paused);
            }
        }

        /// DevOverlay's seam into the backend's dropped-time counter (Task 24
        /// Приложение П-6) — the clock behind it has no UnityEngine dependency
        /// and isn't otherwise exposed to Presentation. Survives pause (see
        /// `Paused` above); only a full match restart zeroes it.
        public float AccumulatorDroppedTime => _backend.DroppedTime;

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
        /// hash is only ever computed when this event has a subscriber: `Update`
        /// passes the event itself into `ISimBackend.Advance` (inside its
        /// declaring class an event reads as a plain field, hence null with
        /// nobody subscribed), and the backend skips both the hash and the
        /// callback on null. With the dev-only logging toggle off — the common
        /// case — that costs one null check per tick and nothing else. Task 43
        /// moved the call site, not the rule.
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
                SimConfig next = SimConfigBuilder.Build(_hero, _weapon, _chaser, _gunner, _wave, _arena, _visibility);
                try
                {
                    _backend.ApplyConfig(next);
                    ConfigTweaked = true;
                }
                catch (System.ArgumentException)
                {
                    // Arena topology changed under hot-tweak — spec §3.9 forbids in-place
                    // migration for that case; the only safe recovery is a full restart.
                    Debug.Log("SimulationRunner: arena topology changed under hot-tweak " +
                        "(radius/obstacles/walls/player cap/spawn ring/entity caps) — ApplyConfig " +
                        "rejected it, restarting with the same seed instead.");
                    Restart(Seed);
                }
            }

            if (_paused) return;

            SimInput frame = _sampler.SampleFrame();
            LastFrameInput = frame; // Task 28 — see the property's own doc above.
            // `TickAdvanced` reads as a plain field inside its declaring class,
            // so this hands the backend null whenever nobody is subscribed —
            // which is what keeps the per-tick `StateHash()` call from ever
            // running in the common case (see the event's own doc, and
            // `ISimBackend.Advance`'s).
            int ticks = _backend.Advance(frame, Time.unscaledDeltaTime, TickAdvanced);
            UpdateRenderAlpha();
            if (ticks > 0)
            {
                // ORDER IS THE CONTRACT (Task 43): the fan-out behind
                // `TicksFlushed` reads this flush's events out of the backend's
                // buffer, and `EndFrame` is what drops them. Swapping the two
                // lines costs every casing, corpse, flash and shot sound, and
                // nothing would fail to compile or to run green.
                TicksFlushed?.Invoke();
                _backend.EndFrame();
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
            _renderPrevFrozen.CopyFrom(Prev);
            _renderCurrFrozen.CopyFrom(Curr);
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
                _renderPrevFrozen.CopyFrom(_renderCurrFrozen);
                _catchUpDuration = catchUpSeconds;
                _catchUpRemaining = catchUpSeconds;
            }
            else
            {
                _catchUpRemaining = 0f;
            }
        }

        public void Restart(long seed)
        {
            Seed = seed;
            SimConfig cfg = SimConfigBuilder.Build(_hero, _weapon, _chaser, _gunner, _wave, _arena, _visibility);
            // The seven balance SOs stay serialized on THIS component (spec
            // §3.12 is about whose config is authoritative, not about where the
            // assets are wired): the backend is handed the finished `SimConfig`
            // by value and never sees a `ScriptableObject`.
            _backend.Restart(seed, cfg);
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
