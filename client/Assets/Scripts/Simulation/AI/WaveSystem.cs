using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.AI
{
    /// Deterministic seed-driven wave director (spec §3.6, Task 22 Interfaces).
    /// While Waiting, counts PhaseTimer down to zero and starts a wave: the
    /// composition (BaseCount + CountGrowth*(WaveIndex-1), capped at
    /// MaxMobsPerWave; split Chaser/Gunner by GunnerShareBase +
    /// GunnerShareGrowth*(WaveIndex-1)) becomes spawn debt in
    /// WaveState.Pending{Chasers,Gunners}. While Active, every tick attempts
    /// exactly one spawn per outstanding debt unit (chasers first, then
    /// gunners); a unit that can't find a valid spot leaves its debt untouched
    /// for the next tick — the debt can never grow mid-wave, so this can't
    /// hang even when the ring is fully blocked (spec §3.13 item 5). Once all
    /// debt is gone and no mobs remain alive, the wave is cleared and the
    /// director goes back to Waiting for WavePause seconds. Does not tick at
    /// all while the player is dead (full death semantics land in Task 23).
    internal static class WaveSystem
    {
        public static void Update(SimulationWorld w)
        {
            if (!w.Player.Alive) return;

            ref WaveState wave = ref w.WaveRef;
            WaveSimConfig cfg = w.Config.Wave;

            if (wave.Phase == WavePhase.Waiting)
            {
                wave.PhaseTimer -= SimulationWorld.TickDt;
                if (wave.PhaseTimer <= 0f) StartWave(w, ref wave, in cfg);
            }

            // Deliberately re-reads wave.Phase rather than branching on the
            // Waiting check above: a wave that just started above falls straight
            // through into working off its own debt this same tick (no wasted
            // tick spent merely transitioning phase).
            if (wave.Phase == WavePhase.Active)
            {
                SpawnPendingOfType(w, ref wave, in cfg, MobType.Chaser);
                SpawnPendingOfType(w, ref wave, in cfg, MobType.Gunner);

                if (wave.PendingChasers == 0 && wave.PendingGunners == 0 && w.MobCount == 0)
                {
                    w.StatsRef.WavesCleared++;
                    w.Emit(SimEventKind.WaveCleared, w.Player.Pos, wave.WaveIndex, default, 0f);
                    wave.Phase = WavePhase.Waiting;
                    wave.PhaseTimer = cfg.WavePause;
                }
            }

            // Mirrors MobCount for wave-scoped telemetry/hash continuity (the field
            // has been part of WaveState/StateHash since Task 5, before any system
            // wrote to it). The clear-check above deliberately reads w.MobCount
            // directly rather than this field — they're the same value by
            // construction, this is just the seam DevOverlay/telemetry read off
            // WaveState without needing a whole RenderSnapshot.
            wave.AliveCount = w.MobCount;
        }

        static void StartWave(SimulationWorld w, ref WaveState wave, in WaveSimConfig cfg)
        {
            wave.WaveIndex++;
            int count = math.min(cfg.BaseCount + cfg.CountGrowth * (wave.WaveIndex - 1),
                cfg.MaxMobsPerWave);
            float gunnerShare = math.saturate(cfg.GunnerShareBase
                + cfg.GunnerShareGrowth * (wave.WaveIndex - 1));
            int gunners = (int)math.round(count * gunnerShare);
            wave.PendingGunners = gunners;
            wave.PendingChasers = count - gunners;
            w.Emit(SimEventKind.WaveStarted, w.Player.Pos, wave.WaveIndex, default, 0f);
            wave.Phase = WavePhase.Active;
        }

        /// One spawn attempt per outstanding debt unit of `type`, bounded to the
        /// count of pending units at the start of this call — this is what makes a
        /// fully-blocked ring terminate every tick instead of hanging: a failed
        /// attempt neither grows nor re-tries within the same tick, it just leaves
        /// the loop counter to advance to the next (still-pending) unit.
        static void SpawnPendingOfType(SimulationWorld w, ref WaveState wave,
            in WaveSimConfig cfg, MobType type)
        {
            int n = type == MobType.Chaser ? wave.PendingChasers : wave.PendingGunners;
            for (int i = 0; i < n; i++)
            {
                if (!TryFindSpawnPos(w, in cfg, type, out float2 pos)) continue; // debt stays
                if (w.SpawnMob(type, pos) < 0) continue; // MaxMobs cap — debt stays (MobSpawnsSkipped bumped)

                if (type == MobType.Chaser) wave.PendingChasers--;
                else wave.PendingGunners--;
            }
        }

        /// Candidate angles are drawn only from `w.Rng.NextFloat(0, 2*PI)` (RNG
        /// discipline, spec §3.6) — up to MaxSpawnAttempts draws. The FallbackSlots
        /// grid below is deliberately RNG-free (fixed, uniform angles): whether the
        /// fallback triggers or not never changes how much RNG state a candidate
        /// search consumes, keeping RNG consumption a pure function of world state
        /// (pending counts, live mobs, arena) rather than of luck.
        static bool TryFindSpawnPos(SimulationWorld w, in WaveSimConfig cfg, MobType type,
            out float2 pos)
        {
            ArenaSimConfig arena = w.Config.Arena;
            float ringRadius = arena.Radius - cfg.SpawnRingInset;
            float mobRadius = w.MobConfigFor(type).Radius;

            for (int i = 0; i < cfg.MaxSpawnAttempts; i++)
            {
                float angle = w.Rng.NextFloat(0f, 2f * math.PI);
                float2 candidate = ringRadius * new float2(math.cos(angle), math.sin(angle));
                if (IsValidSpawn(w, in arena, in cfg, candidate, mobRadius))
                {
                    pos = candidate;
                    return true;
                }
            }

            for (int i = 0; i < cfg.FallbackSlots; i++)
            {
                float angle = 2f * math.PI * i / cfg.FallbackSlots;
                float2 candidate = ringRadius * new float2(math.cos(angle), math.sin(angle));
                if (IsValidSpawn(w, in arena, in cfg, candidate, mobRadius))
                {
                    pos = candidate;
                    return true;
                }
            }

            pos = default;
            return false;
        }

        /// Rejects on obstacle overlap, live-mob overlap (both against the
        /// candidate's own archetype radius, the same CircleOverlap idiom used
        /// elsewhere for attack range / projectile hits) and distance-to-player
        /// below MinSpawnDistanceToPlayer.
        static bool IsValidSpawn(SimulationWorld w, in ArenaSimConfig arena,
            in WaveSimConfig cfg, float2 pos, float mobRadius)
        {
            if (math.distance(pos, w.Player.Pos) < cfg.MinSpawnDistanceToPlayer) return false;

            for (int o = 0; o < arena.ObstacleCount; o++)
                if (Geometry.CircleOverlap(pos, mobRadius, arena.ObstaclePos[o], arena.ObstacleRadius[o]))
                    return false;

            MobState[] mobs = w.Mobs;
            int count = w.MobCount;
            for (int m = 0; m < count; m++)
                if (Geometry.CircleOverlap(pos, mobRadius, mobs[m].Pos,
                        w.MobConfigFor(mobs[m].Type).Radius))
                    return false;

            return true;
        }
    }
}
