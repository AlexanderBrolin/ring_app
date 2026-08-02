using Ring.Simulation.Movement;
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

        // Entities appear in Phase 5/6 — arrays are preallocated to arena caps
        // now so the hot path never allocates once systems start filling them.
        MobState[] _mobs;
        int _mobCount;
        ProjectileState[] _projectiles;
        int _projectileCount;
        WaveState _wave;
        int _nextEntityId = 1;

        readonly SimEvent[] _events;
        int _eventCount;

        public int CurrentTick => _tick;
        public MatchStats Stats => _stats;
        public PlayerState Player => _players[0];
        public SimConfig Config => _config;

        /// Events emitted since the last ClearEvents() call.
        public int EventCount => _eventCount;
        /// Cumulative count of events dropped because the per-frame buffer was full.
        public int DroppedEvents { get; private set; }

        public SimulationWorld(long seed, in SimConfig config)
        {
            uint folded = (uint)(seed ^ (seed >> 32));
            // Unity.Mathematics.Random rejects seed 0.
            _rng = new Random(folded == 0 ? 0x9E3779B9u : folded);
            _config = config;
            _players[0] = new PlayerState { Hp = config.Hero.MaxHp, Alive = true };
            _mobs = new MobState[config.Arena.MaxMobs];
            _projectiles = new ProjectileState[config.Arena.MaxProjectiles];
            _events = new SimEvent[config.Arena.MaxEventsPerFrame];
        }

        public void Tick(in SimInput rawInput)
        {
            SimInput input = Sanitize(rawInput);
            _tick++;
            _rng.NextUInt(); // every tick consumes RNG so an idle world still hashes alive
            _players[0].AimPoint = input.AimPoint;
            if (PlayerMovementSystem.Update(ref _players[0], in input, in _config))
            {
                _stats.DashesUsed++;
                Emit(SimEventKind.PlayerDashed, _players[0].Pos, 0, default, 0f);
            }
        }

        /// Hot-tweak migration (spec §3.9): atomically replaces the balance config on
        /// the tick boundary (caller must only invoke this between ticks). Arena
        /// topology (radius, obstacle count/positions/radii) must stay identical —
        /// a change there invalidates collision/spawn geometry that isn't reconciled
        /// here, so it throws instead; Presentation reacts by restarting the world.
        /// Migration: Hp clamps down to the new max, every player timer clamps into
        /// [0, its new max], wave-state (including WaveIndex) is left untouched.
        public void ApplyConfig(in SimConfig next)
        {
            if (!ArenaTopologyMatches(in _config.Arena, in next.Arena))
            {
                throw new System.ArgumentException("SimulationWorld.ApplyConfig: arena topology " +
                    "changed (radius/obstacles) — restart the world instead of hot-tweaking it.");
            }

            _config = next;

            PlayerState p = _players[0];
            p.Hp = math.min(p.Hp, next.Hero.MaxHp);
            p.DashTimer = math.clamp(p.DashTimer, 0f, next.Hero.DashDuration);
            p.DashCooldown = math.clamp(p.DashCooldown, 0f, next.Hero.DashCooldown);
            p.IframeTimer = math.clamp(p.IframeTimer, 0f, next.Hero.DashIframes);
            p.DashBufferTimer = math.clamp(p.DashBufferTimer, 0f, next.Hero.DashBufferWindow);
            p.FireCooldown = math.clamp(p.FireCooldown, 0f, next.Weapon.FireInterval);
            _players[0] = p;
        }

        static bool ArenaTopologyMatches(in ArenaSimConfig a, in ArenaSimConfig b)
        {
            if (a.Radius != b.Radius || a.ObstacleCount != b.ObstacleCount) return false;
            for (int i = 0; i < a.ObstacleCount; i++)
            {
                if (!math.all(a.ObstaclePos[i] == b.ObstaclePos[i])) return false;
                if (a.ObstacleRadius[i] != b.ObstacleRadius[i]) return false;
            }
            return true;
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

        /// Records a VFX/SFX-relevant occurrence for this tick (spec §3.7). The
        /// per-frame buffer is preallocated to Arena.MaxEventsPerFrame; once full,
        /// further events are dropped (no allocation, no growth) and counted
        /// cumulatively in DroppedEvents so overflow is deterministic and visible.
        internal void Emit(SimEventKind kind, float2 pos, int entityId, MobType mobType, float amount)
        {
            if (_eventCount < _events.Length)
            {
                _events[_eventCount++] = new SimEvent
                {
                    Kind = kind, Tick = _tick, Pos = pos,
                    EntityId = entityId, MobType = mobType, Amount = amount
                };
            }
            else
            {
                DroppedEvents++;
            }
        }

        public SimEvent GetEvent(int i) => _events[i];

        public void ClearEvents() => _eventCount = 0;

        /// Copies the current tick's render-relevant state into a preallocated
        /// target — no allocation, safe to call every render frame.
        public void CaptureSnapshot(RenderSnapshot target)
        {
            target.Tick = _tick;
            target.Player = _players[0];
            target.MobCount = _mobCount;
            System.Array.Copy(_mobs, target.Mobs, _mobCount);
            target.ProjectileCount = _projectileCount;
            System.Array.Copy(_projectiles, target.Projectiles, _projectileCount);
            target.Wave = _wave;
            target.Stats = _stats;
        }

        /// Deep-copies the full canonical state (config excluded) for rollback/replay.
        /// Allocates — call outside the hot tick path.
        public WorldSave SaveState()
        {
            var save = new WorldSave
            {
                Tick = _tick,
                Rng = _rng,
                NextEntityId = _nextEntityId,
                Player = _players[0],
                MobCount = _mobCount,
                Mobs = new MobState[_mobs.Length],
                ProjectileCount = _projectileCount,
                Projectiles = new ProjectileState[_projectiles.Length],
                Wave = _wave,
                Stats = _stats
            };
            System.Array.Copy(_mobs, save.Mobs, _mobs.Length);
            System.Array.Copy(_projectiles, save.Projectiles, _projectiles.Length);
            return save;
        }

        public void RestoreState(WorldSave save)
        {
            _tick = save.Tick;
            _rng = save.Rng;
            _nextEntityId = save.NextEntityId;
            _players[0] = save.Player;
            _mobCount = save.MobCount;
            System.Array.Copy(save.Mobs, _mobs, _mobs.Length);
            _projectileCount = save.ProjectileCount;
            System.Array.Copy(save.Projectiles, _projectiles, _projectiles.Length);
            _wave = save.Wave;
            _stats = save.Stats;
        }

        /// Test-only seam for EveryPlayerAndStatsFieldAffectsHash (spec §3.13 п.12).
        /// Not a public API — no *ForTest wrapper ships in the battle surface.
        internal void SetPlayerForTest(in PlayerState p) => _players[0] = p;
        internal void SetStatsForTest(in MatchStats s) => _stats = s;

        /// Canonical order (spec §3.3): tick → rng → nextEntityId → player →
        /// mobCount+mobs → projectileCount+projectiles → wave → stats.
        public ulong StateHash()
        {
            ulong h = StateHash64.Begin();
            h = StateHash64.Add(h, (ulong)_tick);
            h = StateHash64.Add(h, _rng.state);
            h = StateHash64.Add(h, _nextEntityId);
            h = HashPlayer(h, in _players[0]);
            h = StateHash64.Add(h, _mobCount);
            for (int i = 0; i < _mobCount; i++) h = HashMob(h, in _mobs[i]);
            h = StateHash64.Add(h, _projectileCount);
            for (int i = 0; i < _projectileCount; i++) h = HashProjectile(h, in _projectiles[i]);
            h = HashWave(h, in _wave);
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

        static ulong HashMob(ulong h, in MobState m)
        {
            h = StateHash64.Add(h, m.Id); h = StateHash64.Add(h, (int)m.Type);
            h = StateHash64.Add(h, m.Pos); h = StateHash64.Add(h, m.Vel);
            h = StateHash64.Add(h, m.Hp); h = StateHash64.Add(h, m.StateTimer);
            h = StateHash64.Add(h, m.FireCooldown); h = StateHash64.Add(h, (int)m.Ai);
            h = StateHash64.Add(h, m.StrafeSign);
            return h;
        }

        static ulong HashProjectile(ulong h, in ProjectileState p)
        {
            h = StateHash64.Add(h, p.Id); h = StateHash64.Add(h, (int)p.Owner);
            h = StateHash64.Add(h, p.Pos); h = StateHash64.Add(h, p.PrevPos);
            h = StateHash64.Add(h, p.Vel); h = StateHash64.Add(h, p.Damage);
            h = StateHash64.Add(h, p.Radius); h = StateHash64.Add(h, p.Ttl);
            return h;
        }

        static ulong HashWave(ulong h, in WaveState w)
        {
            h = StateHash64.Add(h, (int)w.Phase); h = StateHash64.Add(h, w.WaveIndex);
            h = StateHash64.Add(h, w.PendingChasers); h = StateHash64.Add(h, w.PendingGunners);
            h = StateHash64.Add(h, w.AliveCount); h = StateHash64.Add(h, w.PhaseTimer);
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
