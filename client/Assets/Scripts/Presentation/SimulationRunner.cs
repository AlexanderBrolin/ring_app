using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

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

        readonly FixedStepAccumulator _acc = new FixedStepAccumulator();
        InputSampler _sampler;
        SimulationWorld _world;
        bool _pendingApplyConfig;

        public RenderSnapshot Prev, Curr;
        public float Alpha;
        public SimulationWorld World => _world;
        public long Seed { get; private set; }
        public bool ConfigTweaked;

        public event System.Action TicksFlushed;
        public event System.Action WorldRestarted;

        void Awake()
        {
            _sampler = new InputSampler(); // П-5 — Task 11 rebuilds this with real args
            RestartNewSeed();
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
                    Restart(Seed);
                }
            }

            SimInput frame = _sampler.SampleFrame();
            int ticks = _acc.Advance(Time.unscaledDeltaTime);
            for (int i = 0; i < ticks; i++)
            {
                _world.Tick(SimInputFrame.ForTick(frame, i)); // защёлка — первому тику
                (Prev, Curr) = (Curr, Prev);
                _world.CaptureSnapshot(Curr);
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
            WorldRestarted?.Invoke();
        }

        // Environment.TickCount64 does not exist under this project's API
        // Compatibility Level (.NET Standard 2.1 — CS0117); UtcNow.Ticks is the
        // built-in equivalent (100ns-resolution, monotonic enough for a dev reseed).
        public void RestartNewSeed() => Restart(System.DateTime.UtcNow.Ticks);

        public void RequestApplyConfig() => _pendingApplyConfig = true;
    }
}
