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
    public sealed class SimulationRunner : MonoBehaviour
    {
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
        public SimulationWorld World => _world;
        public long Seed { get; private set; }
        public bool ConfigTweaked;

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

        void OnEnable() => _sampler?.Enable();

        void OnDisable() => _sampler?.Disable();

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
                    Restart(Seed);
                }
            }

            if (_paused) return;

            SimInput frame = _sampler.SampleFrame();
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
            if (ticks > 0)
            {
                TicksFlushed?.Invoke();
                _world.ClearEvents();
                _sampler.ClearLatches();
            }
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
