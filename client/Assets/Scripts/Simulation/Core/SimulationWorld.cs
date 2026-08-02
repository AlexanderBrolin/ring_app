using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Deterministic world: fixed-dt ticks, RNG seeded from match-config.
    /// No UnityEngine (asmdef: noEngineReferences) — Critical Rule 1.
    public sealed class SimulationWorld
    {
        /// ADR-002 T5: simulation runs at 30 Hz.
        public const float TickDt = 1f / 30f;

        int _tick;
        Random _rng;
        uint _lastNoise;

        public SimulationWorld(long seed)
        {
            uint folded = (uint)(seed ^ (seed >> 32));
            // Unity.Mathematics.Random rejects seed 0.
            _rng = new Random(folded == 0 ? 0x9E3779B9u : folded);
        }

        public void Tick()
        {
            _lastNoise = _rng.NextUInt();
            _tick++;
        }

        /// Canonical order: tick counter, RNG state, last consumed value.
        public ulong StateHash()
        {
            ulong h = StateHash64.Begin();
            h = StateHash64.Add(h, (ulong)_tick);
            h = StateHash64.Add(h, _rng.state);
            h = StateHash64.Add(h, _lastNoise);
            return h;
        }
    }
}
