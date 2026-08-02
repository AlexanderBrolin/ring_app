using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Deterministic world: fixed-dt ticks, single RNG seeded from match-config.
    /// No UnityEngine (asmdef: noEngineReferences) — Critical Rule 1.
    public sealed class SimulationWorld
    {
        /// ADR-002 T5: simulation runs at 30 Hz. The single source of dt.
        public const float TickDt = 1f / 30f;

        int _tick;
        Random _rng;
        SimConfig _config;
        readonly PlayerState[] _players = new PlayerState[1];
        MatchStats _stats;

        public int CurrentTick => _tick;
        public MatchStats Stats => _stats;
        public PlayerState Player => _players[0];
        public SimConfig Config => _config;

        public SimulationWorld(long seed, in SimConfig config)
        {
            uint folded = (uint)(seed ^ (seed >> 32));
            // Unity.Mathematics.Random rejects seed 0.
            _rng = new Random(folded == 0 ? 0x9E3779B9u : folded);
            _config = config;
            _players[0] = new PlayerState { Hp = config.Hero.MaxHp, Alive = true };
        }

        public void Tick(in SimInput rawInput)
        {
            SimInput input = Sanitize(rawInput);
            _tick++;
            _rng.NextUInt(); // every tick consumes RNG so an idle world still hashes alive
            _players[0].AimPoint = input.AimPoint;
        }

        SimInput Sanitize(in SimInput raw)
        {
            SimInput s = raw;
            if (!math.all(math.isfinite(s.MoveDir))) s.MoveDir = float2.zero;
            float lsq = math.lengthsq(s.MoveDir);
            if (lsq > 1f) s.MoveDir /= math.sqrt(lsq);
            if (!math.all(math.isfinite(s.AimPoint))) s.AimPoint = _players[0].AimPoint;
            float2 rel = s.AimPoint - _players[0].Pos;
            float maxR = _config.Arena.Radius * 2f;
            if (math.lengthsq(rel) > maxR * maxR)
                s.AimPoint = _players[0].Pos + math.normalizesafe(rel) * maxR;
            return s;
        }

        /// Canonical order (spec §3.3): tick → rng → nextEntityId → players →
        /// mobs → projectiles → wave → stats. Entities are added in Phase 2+.
        public ulong StateHash()
        {
            ulong h = StateHash64.Begin();
            h = StateHash64.Add(h, (ulong)_tick);
            h = StateHash64.Add(h, _rng.state);
            h = HashPlayer(h, in _players[0]);
            h = HashStats(h, in _stats);
            return h;
        }

        static ulong HashPlayer(ulong h, in PlayerState p)
        {
            h = StateHash64.Add(h, p.Pos); h = StateHash64.Add(h, p.Vel);
            h = StateHash64.Add(h, p.AimPoint); h = StateHash64.Add(h, p.DashDir);
            h = StateHash64.Add(h, p.RecoilOffset); h = StateHash64.Add(h, p.Hp);
            h = StateHash64.Add(h, p.DashTimer); h = StateHash64.Add(h, p.DashCooldown);
            h = StateHash64.Add(h, p.IframeTimer); h = StateHash64.Add(h, p.DashBufferTimer);
            h = StateHash64.Add(h, p.FireCooldown); h = StateHash64.Add(h, p.Alive);
            return h;
        }

        static ulong HashStats(ulong h, in MatchStats s)
        {
            h = StateHash64.Add(h, s.Kills); h = StateHash64.Add(h, s.WavesCleared);
            h = StateHash64.Add(h, s.ShotsFired); h = StateHash64.Add(h, s.ShotsHit);
            h = StateHash64.Add(h, s.DashesUsed);
            h = StateHash64.Add(h, s.MobSpawnsSkipped);
            h = StateHash64.Add(h, s.ProjectileSpawnsSkipped);
            h = StateHash64.Add(h, s.DeathTick); h = StateHash64.Add(h, s.DamageTaken);
            return h;
        }
    }
}
