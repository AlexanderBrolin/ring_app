using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.AI
{
    /// Deterministic seed-driven wave director (spec §3.6, Task 22 Interfaces;
    /// zone budget spec §3.3, Stage 3 Task 11). While Waiting, counts
    /// PhaseTimer down to zero and starts a wave: the TOTAL size (BaseCount +
    /// CountGrowth*(WaveIndex-1), capped at MaxMobsPerWave — unchanged since
    /// Task 16, CountForTest below) is split across the three zones by
    /// SplitByZones, and each zone's own share is split again into
    /// Elite/Chaser/Gunner debt (StartWave) — nine numbers total, one per
    /// (zone, archetype) pair, WaveState.Pending{Zone}{Archetype}. While
    /// Active, every tick attempts exactly one spawn per outstanding debt
    /// unit, zone-major then archetype-minor (Outer before Middle before
    /// Core; within a zone, Chaser before Gunner before Elite — coordinator
    /// R-50); a unit that can't find a valid spot leaves its debt untouched
    /// for the next tick — the debt can never grow mid-wave, so this can't
    /// hang even when the ring is fully blocked (spec §3.13 item 5). Once all
    /// debt is gone (WaveState.PendingTotal == 0) and no mobs remain alive,
    /// the wave is cleared and the director goes back to Waiting for
    /// WavePause seconds. Does not tick at all while no player is alive (full
    /// death semantics landed in Task 23; extended from "the one player" to
    /// "every player" in Stage 2 Task 8).
    internal static class WaveSystem
    {
        /// Coordinator R-53: a zoneless arena (ArenaSimConfig.ZoneRadius.Length
        /// &lt; 2 — every TestConfigs fixture before Т12) is a LEGAL input that
        /// must mean exactly what it meant before this task: the WHOLE wave
        /// budget lands in Zone.Outer, byte-for-byte the old one-group wave.
        /// StartWave reaches this through SplitByZones (the SAME code path a
        /// real 3-zone arena uses), not a parallel branch — a real
        /// ZoneWeights array is simply swapped for this one.
        static readonly float[] ZonelessWeights = { 1f, 0f, 0f };

        const int ZoneCount = 3;
        // Wave archetypes only — Chaser(0)/Gunner(1)/Elite(2). Director(3)
        // never spawns through a wave (spec Р248/§3.4).
        const int WaveArchetypeCount = 3;

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
                // Zone-major, archetype-minor (coordinator R-50) — the SAME
                // order StartWave fills debt in and HashWave reads it in.
                for (int z = 0; z < ZoneCount; z++)
                    for (int t = 0; t < WaveArchetypeCount; t++)
                        SpawnPendingOfType(w, ref wave, in cfg, (Zone)z, (MobType)t);

                if (wave.PendingTotal == 0 && w.MobCount == 0)
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

        /// Wave size for `waveIndex`, scaled by the number of players and
        /// capped at MaxMobsPerWave (spec §3.4). Stage 2 Task 16 — the single
        /// seam that owns the formula, so a test can exercise it without
        /// running a whole world.
        ///
        /// `waveIndex` here is **0-BASED** (wave 0 is the first wave, worth
        /// BaseCount at one player). The live WaveState.WaveIndex is 1-based —
        /// StartWave below therefore passes `wave.WaveIndex - 1`.
        internal static int CountForTest(in WaveSimConfig cfg, int waveIndex, int playerCount)
        {
            float scale = 1f + (playerCount - 1) * cfg.PerPlayerCountFrac;
            int scaled = (int)math.round((cfg.BaseCount + cfg.CountGrowth * waveIndex) * scale);
            // The cap bites AFTER the scale — MaxMobsPerWave is the arena's own
            // ceiling, not a per-player one.
            return math.min(scaled, cfg.MaxMobsPerWave);
        }

        static void StartWave(SimulationWorld w, ref WaveState wave, in WaveSimConfig cfg, float2 eventPos)
        {
            wave.WaveIndex++;
            int count = CountForTest(in cfg, wave.WaveIndex - 1, w.PlayerCount);

            // Coordinator R-53: zoneless arena -> the whole budget goes to
            // Outer, through the SAME SplitByZones call a real 3-zone arena
            // uses (not a parallel branch).
            System.ReadOnlySpan<float> zoneWeights = w.Config.Arena.ZoneRadius.Length < 2
                ? ZonelessWeights : cfg.ZoneWeights;
            System.Span<int> perZone = stackalloc int[ZoneCount];
            SplitByZones(count, zoneWeights, perZone);

            for (int z = 0; z < ZoneCount; z++)
            {
                var zone = (Zone)z;
                int zoneBudget = perZone[z];

                // Coordinator R-59: elite share peels off first, the
                // EXISTING GunnerShare formula (unchanged since Task 16)
                // splits what is left — one extra line, not a second
                // system.
                float eliteShare = EliteShareFor(zone, wave.WaveIndex, in cfg);
                int elites = (int)math.round(zoneBudget * eliteShare);
                int rest = zoneBudget - elites;
                float gunnerShare = math.saturate(cfg.GunnerShareBase
                    + cfg.GunnerShareGrowth * (wave.WaveIndex - 1));
                int gunners = (int)math.round(rest * gunnerShare);
                int chasers = rest - gunners;

                // Fresh wave: every zone's debt starts at 0 (the Waiting ->
                // Active transition only fires once WaveState.PendingTotal ==
                // 0), so a plain assignment through the ONE mapping home
                // (coordinator R-51) is correct, not an accumulation.
                PendingRef(ref wave, zone, MobType.Elite) = elites;
                PendingRef(ref wave, zone, MobType.Gunner) = gunners;
                PendingRef(ref wave, zone, MobType.Chaser) = chasers;
            }

            // eventPos (Stage 2 Task 8): the nearest-alive-player position
            // Update already resolved above — see its own doc for why
            // StartWave doesn't re-resolve it itself.
            w.Emit(SimEventKind.WaveStarted, eventPos, wave.WaveIndex, default, 0f);
            wave.Phase = WavePhase.Active;
        }

        /// Splits `total` indivisible units across `weights.Length` zones by
        /// the LARGEST REMAINDER method (Hamilton apportionment) with a FIXED
        /// zone order for tie-breaking (spec §3.3 Р211): every zone first
        /// gets floor(total*weight), then the leftover units (total minus
        /// that sum) go one-by-one to the zones with the largest fractional
        /// remainder, ties broken by the LOWER index (Zone's own declared
        /// order, Outer first) — so the parts always sum to EXACTLY `total`
        /// no matter how the rounding falls (a naive per-zone round() can
        /// under- or overshoot `total` by up to the number of zones, and
        /// Р211 requires the debt to close, never drift).
        ///
        /// `leftover` itself can legitimately be 0 (every zone's exact share
        /// already lands on an integer, e.g. total=0, or total=20 against
        /// {0.45,0.45,0.10}) — the loop below is bounded by `leftover`
        /// exactly, not forced to run at least once, which is what keeps
        /// SplitByZones(0, ...) genuinely a no-op (WaveZoneTests.
        /// SplitByZones_ZeroTotal_GivesThreeZeros) instead of inventing a
        /// unit of debt nobody asked for.
        ///
        /// PRECONDITION, not checked (coordinator F5, R-36's own
        /// "defensive-only, not load-bearing" precedent): `weights.Length
        /// == perZone.Length`. Both live production call sites hand this
        /// method a length-3 span on both sides (StartWave's `perZone` and
        /// either `cfg.Wave.ZoneWeights` — SimConfigBuilder.Validate's own
        /// new rule fixes it at exactly 3 elements — or the length-3
        /// `ZonelessWeights` fallback), so the mismatch this precondition
        /// names cannot occur through any path SimConfigBuilder.Build
        /// gates. A hand-built fixture that skips the builder and violates
        /// it gets `IndexOutOfRangeException` from `perZone[i]`/
        /// `perZone[best]` (mismatched lengths) — a loud framework
        /// exception, not a silently wrong split.
        internal static void SplitByZones(int total, System.ReadOnlySpan<float> weights,
            System.Span<int> perZone)
        {
            int n = weights.Length;
            System.Span<float> remainder = stackalloc float[n];
            int assigned = 0;
            for (int i = 0; i < n; i++)
            {
                float exact = total * weights[i];
                int flr = (int)math.floor(exact);
                perZone[i] = flr;
                remainder[i] = exact - flr;
                assigned += flr;
            }

            int leftover = total - assigned;
            for (int u = 0; u < leftover; u++)
            {
                int best = 0;
                for (int i = 1; i < n; i++)
                    if (remainder[i] > remainder[best]) best = i;
                perZone[best]++;
                remainder[best] = -1f; // claimed -- never picked twice
            }
        }

        /// The ONE home for "(zone, archetype) -> which WaveState field
        /// carries its debt" (coordinator R-51, lesson 279: a second mapping
        /// home makes a mutation on this one invisible). StartWave (writer),
        /// SpawnPendingOfType (reader/decrementer) and WaveZoneTests' own
        /// sentinel test go through this and nothing else. `default` is a
        /// THROW, not a silent fallback onto Chaser (spec Р251's own
        /// warning: "a new archetype silently counted as a gunner" is
        /// exactly the failure this project already refused once for
        /// MobType itself).
        internal static ref int PendingRef(ref WaveState w, Zone zone, MobType type)
        {
            switch (zone, type)
            {
                case (Zone.Outer, MobType.Chaser): return ref w.PendingOuterChaser;
                case (Zone.Outer, MobType.Gunner): return ref w.PendingOuterGunner;
                case (Zone.Outer, MobType.Elite): return ref w.PendingOuterElite;
                case (Zone.Middle, MobType.Chaser): return ref w.PendingMiddleChaser;
                case (Zone.Middle, MobType.Gunner): return ref w.PendingMiddleGunner;
                case (Zone.Middle, MobType.Elite): return ref w.PendingMiddleElite;
                case (Zone.Core, MobType.Chaser): return ref w.PendingCoreChaser;
                case (Zone.Core, MobType.Gunner): return ref w.PendingCoreGunner;
                case (Zone.Core, MobType.Elite): return ref w.PendingCoreElite;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(type),
                        $"no wave-debt field for (zone={zone}, type={type})");
            }
        }

        /// Elite's SHARE of a zone's own budget (spec §3.3 Р212/Р298,
        /// coordinator R-59): Outer grows a flat rate per wave up to
        /// EliteShareOuterCap (coordinator R-60 — a WaveSimConfig field, not
        /// a code constant: CRITICAL RULE 6 puts every wave balance number in
        /// a ScriptableObject); Middle is a flat share; Core is always 100%
        /// elite (spec's own table — the boss's honor guard, not a wave
        /// mob).
        static float EliteShareFor(Zone zone, int waveIndex, in WaveSimConfig cfg) => zone switch
        {
            Zone.Outer => math.min(cfg.EliteShareOuterGrowth * (waveIndex - 1), cfg.EliteShareOuterCap),
            Zone.Middle => cfg.EliteShareMiddle,
            Zone.Core => 1f,
            _ => throw new System.ArgumentOutOfRangeException(nameof(zone), zone,
                "EliteShareFor: unknown zone"),
        };

        /// One spawn attempt per outstanding debt unit of `(zone, type)`,
        /// bounded to the count of pending units at the start of this call —
        /// this is what makes a fully-blocked ring terminate every tick
        /// instead of hanging: a failed attempt neither grows nor re-tries
        /// within the same tick, it just leaves the loop counter to advance
        /// to the next (still-pending) unit.
        static void SpawnPendingOfType(SimulationWorld w, ref WaveState wave,
            in WaveSimConfig cfg, Zone zone, MobType type)
        {
            ref int pending = ref PendingRef(ref wave, zone, type);
            int n = pending;
            for (int i = 0; i < n; i++)
            {
                if (!TryFindSpawnPos(w, in cfg, zone, type, out float2 pos)) continue; // debt stays
                if (w.SpawnMob(type, pos) < 0) continue; // MaxMobs cap — debt stays (MobSpawnsSkipped bumped)

                pending--;
            }
        }

        /// Candidate angles are drawn only from `w.WaveRng.NextFloat(0, 2*PI)` (RNG
        /// discipline, spec §3.6; Task 3 — dedicated wave-director stream, split
        /// from weapon spread) — up to MaxSpawnAttempts draws. The FallbackSlots
        /// grid below is deliberately RNG-free (fixed, uniform angles): whether the
        /// fallback triggers or not never changes how much RNG state a candidate
        /// search consumes, keeping RNG consumption a pure function of world state
        /// (pending counts, live mobs, arena) rather than of luck.
        static bool TryFindSpawnPos(SimulationWorld w, in WaveSimConfig cfg, Zone zone, MobType type,
            out float2 pos)
        {
            ArenaSimConfig arena = w.Config.Arena;
            // Coordinator R-54: the zone's own spawn ring, not the arena-wide
            // one — Geometry.ZoneSpawnRingRadius is the one home for this
            // arithmetic (also used by SimConfigBuilder.Validate's R-55
            // rule).
            float ringRadius = Geometry.ZoneSpawnRingRadius(zone, in arena, cfg.SpawnRingInset);
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

            // Stage 3 Task 9: zone-wall arcs, same "reuse the existing overlap
            // primitive" idiom as the obstacle/wall checks above — a spawn
            // candidate inside a zone wall's solid body (outside every door
            // cutout) is rejected exactly like one inside an obstacle or wall.
            for (int zIdx = 0; zIdx < arena.ZoneWallCount; zIdx++)
            {
                var doorCenter = new System.ReadOnlySpan<float>(arena.DoorCenterRad,
                    arena.ZoneWallDoorStart[zIdx], arena.ZoneWallDoorCount[zIdx]);
                var doorFreeWidth = new System.ReadOnlySpan<float>(arena.DoorFreeWidth,
                    arena.ZoneWallDoorStart[zIdx], arena.ZoneWallDoorCount[zIdx]);
                if (Geometry.OverlapsArc(pos, mobRadius, arena.ZoneWallRadius[zIdx],
                        arena.ZoneWallHalfWidth[zIdx], doorCenter, doorFreeWidth))
                    return false;
            }

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
