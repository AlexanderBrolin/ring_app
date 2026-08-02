using Ring.Simulation.AI;
using Ring.Simulation.Combat;
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
        // Scratch buffer for SeparationSystem's per-tick pairwise impulses (Task
        // 20) — preallocated here so the hot path never allocates; recomputed
        // from scratch every tick, so it carries no state across ticks and is
        // deliberately excluded from SaveState/RestoreState and StateHash.
        readonly float2[] _sepForces;
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
            _sepForces = new float2[config.Arena.MaxMobs];
            _projectiles = new ProjectileState[config.Arena.MaxProjectiles];
            _events = new SimEvent[config.Arena.MaxEventsPerFrame];
        }

        public void Tick(in SimInput rawInput)
        {
            SimInput input = Sanitize(rawInput);
            _tick++;
            _players[0].AimPoint = input.AimPoint;
            if (PlayerMovementSystem.Update(ref _players[0], in input, in _config))
            {
                _stats.DashesUsed++;
                Emit(SimEventKind.PlayerDashed, _players[0].Pos, 0, default, 0f);
            }
            WeaponSystem.Update(this, ref _players[0], in input);
            // Canonical tick order (spec Interfaces, Task 16/19/20): movement →
            // weapon → mobs (Phase 6) → mob separation → projectiles → (waves,
            // Phase 6+). Separation runs right after MobAiSystem so it sees this
            // tick's post-movement positions; its Vel addition only shows up as
            // motion on the next tick's MoveWithCollisions call (see
            // SeparationSystem's doc comment).
            MobAiSystem.Update(this);
            SeparationSystem.Apply(this);
            ProjectileSystem.Update(this);
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

        /// Combat systems' seam into the single world RNG (Critical Rule: one shared
        /// Random, no ad-hoc Unity.Mathematics.Random instances in Simulation).
        internal ref Random Rng => ref _rng;

        /// Combat systems' seam into per-match counters (ShotsFired, skip counts, ...).
        internal ref MatchStats StatsRef => ref _stats;

        /// ProjectileSystem's seam into live projectile storage (Task 16 sweep resolution).
        internal ProjectileState[] Projectiles => _projectiles;
        internal int ProjectileCount => _projectileCount;

        /// ProjectileSystem's seam into live mob storage (Task 16 damage matrix).
        internal MobState[] Mobs => _mobs;
        internal int MobCount => _mobCount;

        /// MobAiSystem's seam into the per-archetype balance numbers (Task 19).
        internal MobSimConfig MobConfigFor(MobType type)
            => type == MobType.Chaser ? _config.Chaser : _config.Gunner;

        /// SeparationSystem's seam into its preallocated per-tick force buffer
        /// (Task 20) — sized to Arena.MaxMobs, recomputed every tick, never grown.
        internal float2[] SepForces => _sepForces;

        /// Spawns a projectile (spec §3.5/§3.6). Capped at Arena.MaxProjectiles —
        /// once full, spawns are skipped and counted rather than growing the array,
        /// keeping the cap degradation allocation-free and deterministic.
        internal int SpawnProjectile(ProjectileOwner owner, float2 pos, float2 vel,
            float damage, float radius, float ttl)
        {
            if (_projectileCount >= _projectiles.Length)
            {
                _stats.ProjectileSpawnsSkipped++;
                return -1;
            }
            int id = _nextEntityId++;
            _projectiles[_projectileCount++] = new ProjectileState
            {
                Id = id, Owner = owner, Pos = pos, PrevPos = pos, Vel = vel,
                Damage = damage, Radius = radius, Ttl = ttl
            };
            // Amount carries the shot's sim-plane velocity angle (Presentation
            // fix-round app-2pl round 2): MuzzleFlashView needs a tick-accurate
            // fire direction, and reading it back off the render-frame's Curr
            // snapshot is wrong during a multi-tick catch-up flush (Curr reflects
            // only the batch's LAST tick, not necessarily the tick this shot fired
            // on) — this event field is the only tick-exact source available.
            // Events are excluded from StateHash (spec §3.7), so this adds no new
            // determinism/replay surface.
            Emit(SimEventKind.ProjectileFired, pos, id, default, math.atan2(vel.y, vel.x));
            return id;
        }

        /// Removes a projectile by swapping the last slot into its place — O(1),
        /// no shifting, consistent with the _projectileCount pattern above.
        /// Consumer: Task 16 (projectile tick/expiry/hit resolution).
        internal void RemoveProjectileAt(int index)
        {
            _projectiles[index] = _projectiles[--_projectileCount];
        }

        /// Applies projectile damage to a mob (spec Interfaces, Task 16); on death
        /// it swap-removes the mob the same way RemoveProjectileAt does for projectiles.
        internal void DamageMob(int index, float dmg, float2 pos)
        {
            _mobs[index].Hp -= dmg;
            _stats.ShotsHit++;
            if (_mobs[index].Hp <= 0f)
            {
                _stats.Kills++;
                Emit(SimEventKind.MobDied, pos, _mobs[index].Id, _mobs[index].Type, dmg);
                _mobs[index] = _mobs[--_mobCount];
            }
        }

        /// Applies projectile damage to the player (spec Interfaces, Task 16): active
        /// dash i-frames absorb the hit with no event; otherwise Hp drops and, once it
        /// reaches zero, the player dies exactly once — the Alive gate stops further
        /// hits on an already-dead player from re-emitting PlayerDied.
        internal void DamagePlayer(float dmg, float2 pos)
        {
            ref PlayerState p = ref _players[0];
            if (p.IframeTimer > 0f) return;

            p.Hp -= dmg;
            _stats.DamageTaken += dmg;
            Emit(SimEventKind.PlayerDamaged, pos, 0, default, dmg);

            if (p.Hp <= 0f && p.Alive)
            {
                p.Alive = false;
                _stats.DeathTick = _tick;
                p.DashTimer = 0f;
                p.IframeTimer = 0f;
                Emit(SimEventKind.PlayerDied, pos, 0, default, 0f);
            }
        }

        /// Test-only mob spawn seam (Task 16 Interfaces). Spawned mobs start at
        /// Idle AI, but since Task 19 (Phase 6) MobAiSystem ticks every live mob
        /// unconditionally — a spawned mob is NOT a static target: from the very
        /// next Tick() it settles into Chase/Reposition-Fire like any other mob.
        /// Callers that need a stationary target must either not tick the world or
        /// account for movement/contact damage/gunfire. StrafeSign is seeded
        /// deterministically by Id parity (Task 19 Interfaces) — no RNG. Capped at
        /// Arena.MaxMobs like the real spawner will be.
        internal int SpawnMobForTest(MobType type, float2 pos)
        {
            if (_mobCount >= _mobs.Length)
            {
                _stats.MobSpawnsSkipped++;
                return -1;
            }
            int id = _nextEntityId++;
            _mobs[_mobCount++] = new MobState
            {
                Id = id, Type = type, Pos = pos,
                Hp = type == MobType.Chaser ? _config.Chaser.MaxHp : _config.Gunner.MaxHp,
                Ai = MobAiState.Idle,
                // Deterministic handedness for Gunner strafe / SteerAround's dead-on
                // tangent tiebreak (Task 19 Interfaces) — no RNG needed.
                StrafeSign = (id & 1) == 0 ? 1 : -1
            };
            return id;
        }

        /// Test-only wrapper over SpawnProjectile (Task 16 Interfaces) — same spawn
        /// path production code uses, named for test call-sites.
        internal int SpawnProjectileForTest(ProjectileOwner owner, float2 pos, float2 vel,
            float damage, float radius, float ttl)
            => SpawnProjectile(owner, pos, vel, damage, radius, ttl);

        /// Test-only seam (Task 19 Interfaces): kills the player outright via the
        /// normal damage path (overkill amount) so MobAiSystem's "player dead"
        /// branch (all mobs → Idle) can be exercised deterministically.
        internal void KillPlayerForTest() => DamagePlayer(_config.Hero.MaxHp + 1f, _players[0].Pos);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// Dev-only mob placeholder spawn for Presentation milestone 2 (spec Interfaces).
        /// Stripped from production builds — the sole public dev-surface method here.
        public int DevSpawnMob(MobType type, float2 pos) => SpawnMobForTest(type, pos);
#endif

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
