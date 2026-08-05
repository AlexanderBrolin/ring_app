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
    /// all while no player is alive (full death semantics landed in Task 23;
    /// extended from "the one player" to "every player" in Stage 2 Task 8).
    internal static class WaveSystem
    {
        public static void Update(SimulationWorld w)
        {
            // Stage 2 Task 8: early exit + WaveStarted/WaveCleared event
            // positions route through NearestAlivePlayer (from the arena
            // center — WaveSystem has no per-mob "from" point the way
            // MobAiSystem does) instead of the old solo-only
            // w.Player.Alive/w.Player.Pos. For a solo world this is
            // byte-for-byte the old "the one player, if alive" read. `false`
            // (nobody alive) reuses the SAME early return WaveSystem already had.
            if (!Targeting.NearestAlivePlayer(w, float2.zero, out int nearestIdx)) return;
            float2 nearestPlayerPos = w.PlayerAt(nearestIdx).Pos;

            ref WaveState wave = ref w.WaveRef;
            WaveSimConfig cfg = w.Config.Wave;

            if (wave.Phase == WavePhase.Waiting)
            {
                wave.PhaseTimer -= SimulationWorld.TickDt;
                if (wave.PhaseTimer <= 0f) StartWave(w, ref wave, in cfg, nearestPlayerPos);
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
                    // Stage 2 Task 5: world-scoped counter — counted once per
                    // match regardless of player count, not per player.
                    w.WorldStatsRef.WavesCleared++;
                    w.Emit(SimEventKind.WaveCleared, nearestPlayerPos, wave.WaveIndex, default, 0f);
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

        static void StartWave(SimulationWorld w, ref WaveState wave, in WaveSimConfig cfg, float2 eventPos)
        {
            wave.WaveIndex++;
            int count = math.min(cfg.BaseCount + cfg.CountGrowth * (wave.WaveIndex - 1),
                cfg.MaxMobsPerWave);
            float gunnerShare = math.saturate(cfg.GunnerShareBase
                + cfg.GunnerShareGrowth * (wave.WaveIndex - 1));
            int gunners = (int)math.round(count * gunnerShare);
            wave.PendingGunners = gunners;
            wave.PendingChasers = count - gunners;
            // eventPos (Stage 2 Task 8): the nearest-alive-player position
            // Update already resolved above — see its own doc for why
            // StartWave doesn't re-resolve it itself.
            w.Emit(SimEventKind.WaveStarted, eventPos, wave.WaveIndex, default, 0f);
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

        /// Candidate angles are drawn only from `w.WaveRng.NextFloat(0, 2*PI)` (RNG
        /// discipline, spec §3.6; Task 3 — dedicated wave-director stream, split
        /// from weapon spread) — up to MaxSpawnAttempts draws. The FallbackSlots
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
                float angle = w.WaveRng.NextFloat(0f, 2f * math.PI);
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

        /// Rejects on obstacle overlap, wall overlap (Stage 2 Task 14, spec
        /// §3.3 — the same Geometry.OverlapsStadium the obstacle-clearance
        /// check elsewhere already uses, no second overlap function),
        /// live-mob overlap (both against the candidate's own archetype
        /// radius, the same CircleOverlap idiom used elsewhere for attack
        /// range / projectile hits) and distance-to-player below
        /// MinSpawnDistanceToPlayer.
        static bool IsValidSpawn(SimulationWorld w, in ArenaSimConfig arena,
            in WaveSimConfig cfg, float2 pos, float mobRadius)
        {
            // Stage 2 Task 8: distance-to-player check now respects EVERY
            // alive player via NearestAlivePlayer(from the candidate spawn
            // point) instead of the old solo-only w.Player.Pos — a candidate
            // must clear MinSpawnDistanceToPlayer from whichever alive player
            // is closest to IT specifically (not the Update-level "nearest to
            // arena center" player — a candidate can be close to a player who
            // isn't the one nearest the center, so this is recomputed per
            // candidate, not threaded down from Update). `!NearestAlivePlayer`
            // can't actually happen here (Update's own early exit above
            // already returns before this is ever reached), but the
            // short-circuit still reads correctly on its own terms: no alive
            // player means no distance constraint to violate.
            if (Targeting.NearestAlivePlayer(w, pos, out int nearestIdx)
                && math.distance(pos, w.PlayerAt(nearestIdx).Pos) < cfg.MinSpawnDistanceToPlayer)
                return false;

            for (int o = 0; o < arena.ObstacleCount; o++)
                if (Geometry.CircleOverlap(pos, mobRadius, arena.ObstaclePos[o], arena.ObstacleRadius[o]))
                    return false;

            for (int wIdx = 0; wIdx < arena.WallCount; wIdx++)
                if (Geometry.OverlapsStadium(pos, mobRadius, arena.WallA[wIdx], arena.WallB[wIdx],
                        arena.WallHalfWidth[wIdx]))
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
