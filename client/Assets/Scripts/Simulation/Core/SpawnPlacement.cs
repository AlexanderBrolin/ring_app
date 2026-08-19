using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// A caller-supplied predicate for SpawnPlacement.TryFind below — a
    /// GENERIC STRUCT constraint, not a delegate (coordinator R-102):
    /// AllocationTests.Tick_DoesNotAllocateGC (`:20`) measures 1000 ticks of
    /// a saturated world where WaveSystem's own spawn search runs inside the
    /// measured window, and a delegate/closure/interface-by-reference would
    /// allocate there. A `struct` implementing this interface, consumed
    /// through `where TFilter : struct, ISpawnFilter`, is devirtualized and
    /// specialized per TFilter by the JIT/IL2CPP — no boxing, no allocation.
    public interface ISpawnFilter
    {
        bool IsValid(float2 pos);
    }

    /// Stage 3 Task 15 (coordinator R-102/R-11): the shared "candidates from
    /// RNG -> predicate -> RNG-free fallback grid -> refusal" search and the
    /// shared "circles + stadiums + arcs" geometry half, factored out of
    /// WaveSystem (its sole occupant since Stage 2 Task 11 through Stage 3
    /// Task 11) so container placement (Т15) can become its SECOND consumer
    /// without a parallel copy of the same arithmetic (errata E-6 I6).
    /// PUBLIC — same reasoning as Geometry.SpawnPosFor/ZoneSpawnRingRadius:
    /// Ring.Data.SimConfigBuilder (a separate assembly, references
    /// Ring.Simulation) needs GeometryBlocked below for its own two mirror
    /// copies (RingSlotBlocked, coordinator R-111).
    public static class SpawnPlacement
    {
        /// Coordinator R-115: the ONE home for "fixed fallback slot index ->
        /// position on a ring" — this formula is BIT-SENSITIVE (R-103's own
        /// digest-inert proof for the whole search rests on it being this
        /// EXACT expression, not an equivalent rewrite: `2f * math.PI * i /
        /// slots` and `2f * math.PI * (i / (float)slots)` are different
        /// float expressions). Before this fix it had drifted into THREE
        /// separate copies (SpawnPlacement.TryFind's own fallback loop below,
        /// SimConfigBuilder's ring-lock scanner, ConfigTests.FreeRingSlots)
        /// — the same triad Ф2 review A-6 already caught missing the arc
        /// loop once. Deliberately NOT folded into TryFind itself: TryFind
        /// is a SEARCH that draws from an RNG stream, and enumerating the
        /// `slots` fixed positions on a ring is not a search at all — the
        /// two callers that only enumerate (SimConfigBuilder, ConfigTests)
        /// have no RNG stream to hand it and shouldn't need one.
        public static float2 FallbackSlotPos(float ringRadius, int slotIndex, int slots)
        {
            float angle = 2f * math.PI * slotIndex / slots;
            return ringRadius * new float2(math.cos(angle), math.sin(angle));
        }

        /// Up to `maxAttempts` candidates drawn from `rng` (a RING-radius
        /// angle, uniform in [0, 2*PI)), each tested against `filter`; if
        /// none pass, a further `fallbackSlots` FIXED, evenly-spaced angles
        /// are tried via FallbackSlotPos above, RNG-free (coordinator
        /// R-1/spec §3.6 doc: whether the fallback triggers or not never
        /// changes how much RNG state a search consumes). `rng` is
        /// `ref Random` deliberately — a by-value `Random` would stop the
        /// caller's own stream from advancing (coordinator's danger #1) and
        /// silently return the SAME candidate angle on the next call.
        ///
        /// The candidate-draw arithmetic below is copied SYMBOL FOR SYMBOL
        /// from WaveSystem.TryFindSpawnPos's own pre-Task-15 body — a
        /// literal extraction, not a rewrite.
        public static bool TryFind<TFilter>(ref Random rng, int maxAttempts, int fallbackSlots,
            float ringRadius, in TFilter filter, out float2 pos)
            where TFilter : struct, ISpawnFilter
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                float angle = rng.NextFloat(0f, 2f * math.PI);
                float2 candidate = ringRadius * new float2(math.cos(angle), math.sin(angle));
                if (filter.IsValid(candidate))
                {
                    pos = candidate;
                    return true;
                }
            }

            for (int i = 0; i < fallbackSlots; i++)
            {
                float2 candidate = FallbackSlotPos(ringRadius, i, fallbackSlots);
                if (filter.IsValid(candidate))
                {
                    pos = candidate;
                    return true;
                }
            }

            pos = default;
            return false;
        }

        /// The geometry half of spawn-candidate rejection shared by every
        /// consumer: obstacle circles, wall stadiums, and zone-wall arcs.
        /// `doorsPassable` is the door-policy PARAMETER the coordinator's
        /// notes call for (same "parameterize the existing home" precedent
        /// as SimConfigBuilder.MaxBodyRadius's own `waveArchetypesOnly`
        /// bool) rather than a second copy: `true` calls Geometry.OverlapsArc
        /// (a candidate inside a door cutout is forgiven — correct for a mob,
        /// which walks through doors), `false` calls Geometry.InArcBand (a
        /// RADIAL-ONLY band test with no door exception at all — correct for
        /// a container, which must not block a doorway, coordinator R-106).
        ///
        /// Deliberately does NOT cover distance-to-player or live-entity
        /// overlap — those are volley-specific (a wave mob cares about
        /// nearby players and other mobs; a container cares about other
        /// containers instead) and stay in each caller's own ISpawnFilter
        /// implementation (coordinator §2: "половина отбраковки, которая
        /// НЕ переезжает").
        public static bool GeometryBlocked(in ArenaSimConfig arena, float2 pos, float radius,
            bool doorsPassable)
        {
            for (int o = 0; o < arena.ObstacleCount; o++)
                if (Geometry.CircleOverlap(pos, radius, arena.ObstaclePos[o], arena.ObstacleRadius[o]))
                    return true;

            for (int wIdx = 0; wIdx < arena.WallCount; wIdx++)
                if (Geometry.OverlapsStadium(pos, radius, arena.WallA[wIdx], arena.WallB[wIdx],
                        arena.WallHalfWidth[wIdx]))
                    return true;

            for (int zIdx = 0; zIdx < arena.ZoneWallCount; zIdx++)
            {
                if (doorsPassable)
                {
                    var doorCenter = new System.ReadOnlySpan<float>(arena.DoorCenterRad,
                        arena.ZoneWallDoorStart[zIdx], arena.ZoneWallDoorCount[zIdx]);
                    var doorFreeWidth = new System.ReadOnlySpan<float>(arena.DoorFreeWidth,
                        arena.ZoneWallDoorStart[zIdx], arena.ZoneWallDoorCount[zIdx]);
                    if (Geometry.OverlapsArc(pos, radius, arena.ZoneWallRadius[zIdx],
                            arena.ZoneWallHalfWidth[zIdx], doorCenter, doorFreeWidth))
                        return true;
                }
                else
                {
                    if (Geometry.InArcBand(pos, radius, arena.ZoneWallRadius[zIdx],
                            arena.ZoneWallHalfWidth[zIdx]))
                        return true;
                }
            }

            return false;
        }
    }
}
