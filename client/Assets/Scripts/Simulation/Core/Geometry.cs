using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Analytic 2D geometry shared by movement, dash, projectiles and AI LoS.
    public static class Geometry
    {
        public const float Skin = 1e-3f;

        public static bool CircleOverlap(float2 aPos, float aR, float2 bPos, float bR)
        {
            float r = aR + bR;
            return math.lengthsq(bPos - aPos) < r * r;
        }

        /// Swept circle (segment p0→p1, inflated by padR) vs static circle; t ∈ [0,1].
        public static bool SegmentCircle(float2 p0, float2 p1, float padR,
            float2 c, float cR, out float t)
        {
            t = 0f;
            float2 d = p1 - p0;
            float2 f = p0 - c;
            float r = padR + cR;
            float a = math.dot(d, d);
            if (a < 1e-12f) return math.lengthsq(f) < r * r;
            if (math.lengthsq(f) < r * r) return true; // start inside → t=0
            float b = 2f * math.dot(f, d);
            float cc = math.dot(f, f) - r * r;
            float disc = b * b - 4f * a * cc;
            if (disc < 0f) return false;
            float t0 = (-b - math.sqrt(disc)) / (2f * a);
            if (t0 < 0f || t0 > 1f) return false;
            t = t0;
            return true;
        }

        /// Entry AND exit of the swept circle through a static circle, clipped to
        /// the segment: the chord interval [tEnter, tExit] ⊆ [0,1]. Task 6 needs
        /// both ends because a projectile's height changes along the tick, so the
        /// hit-volume test is an interval-vs-interval overlap, not a point test.
        ///
        /// NB: this deliberately re-solves the same quadratic as SegmentCircle
        /// above instead of SegmentCircle delegating to it. SegmentCircle is the
        /// hot path (SweepArena runs it per obstacle per moving body per tick) and
        /// is pinned bit-for-bit by the golden hash; routing it through a
        /// two-root/clamp variant would change its float rounding. The two must be
        /// changed as a pair (QC18).
        public static bool SegmentCircleInterval(float2 p0, float2 p1, float padR,
            float2 c, float cR, out float tEnter, out float tExit)
        {
            tEnter = 0f; tExit = 0f;
            float2 d = p1 - p0;
            float2 f = p0 - c;
            float r = padR + cR;
            float a = math.dot(d, d);
            if (a < 1e-12f)
            {
                // Degenerate sweep (no horizontal motion this tick): the body
                // spends the whole step at p0, so the interval is the full step.
                if (math.lengthsq(f) >= r * r) return false;
                tExit = 1f;
                return true;
            }
            float b = 2f * math.dot(f, d);
            float cc = math.dot(f, f) - r * r;
            float disc = b * b - 4f * a * cc;
            if (disc < 0f) return false;
            float sqrtDisc = math.sqrt(disc);
            float inv = 1f / (2f * a);
            // Clip the chord to the step: a root before the start means the body
            // began inside (enter at 0), a root past the end means it is still
            // inside when the step runs out (exit at 1).
            tEnter = math.max((-b - sqrtDisc) * inv, 0f);
            tExit = math.min((-b + sqrtDisc) * inv, 1f);
            if (tEnter > tExit)
            {
                // The whole chord lies outside [0,1] — the circle is either
                // entirely behind the start or entirely beyond the end.
                tEnter = 0f; tExit = 0f;
                return false;
            }
            return true;
        }

        /// Exit through the ring wall from inside; solves |p0 + d·t| = ringR − padR.
        public static bool SegmentRingWall(float2 p0, float2 p1, float padR,
            float ringR, out float t)
        {
            t = 0f;
            float limit = ringR - padR;
            float2 d = p1 - p0;
            float a = math.dot(d, d);
            if (a < 1e-12f) return false;
            float b = 2f * math.dot(p0, d);
            float c = math.dot(p0, p0) - limit * limit;
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return false;
            float t1 = (-b + math.sqrt(disc)) / (2f * a);
            if (t1 < 0f || t1 > 1f) return false;
            t = t1;
            return true;
        }

        public static bool PushOutOfCircle(ref float2 pos, float radius,
            float2 c, float cR, out float2 normal)
        {
            normal = float2.zero;
            float2 delta = pos - c;
            float r = radius + cR;
            float distSq = math.lengthsq(delta);
            if (distSq >= r * r) return false;
            float dist = math.sqrt(distSq);
            normal = dist > 1e-6f ? delta / dist : new float2(1f, 0f);
            pos = c + normal * (r + Skin);
            return true;
        }

        public static bool ClampInsideRing(ref float2 pos, float radius,
            float ringR, out float2 normal)
        {
            normal = float2.zero;
            float limit = ringR - radius;
            float distSq = math.lengthsq(pos);
            if (distSq <= limit * limit) return false;
            float dist = math.sqrt(distSq);
            float2 outward = dist > 1e-6f ? pos / dist : new float2(1f, 0f);
            pos = outward * (limit - Skin);
            normal = -outward;
            return true;
        }

        /// Remove the velocity component pointing into the surface.
        public static float2 Slide(float2 vel, float2 normal)
        {
            float into = math.dot(vel, normal);
            return into < 0f ? vel - normal * into : vel;
        }

        public static float2 Rotate(float2 v, float rad)
        {
            float s = math.sin(rad), c = math.cos(rad);
            return new float2(c * v.x - s * v.y, s * v.x + c * v.y);
        }

        /// Rotates `from` towards `to` by at most `maxRad` radians, along the
        /// shorter arc, preserving `from`'s magnitude (direction-only steer —
        /// Task 10 slide steering, QC19). A zero-length `from` or `to` can't
        /// supply a heading, so the other vector (or `from` itself, unchanged)
        /// is returned rather than dividing by zero.
        public static float2 RotateTowards(float2 from, float2 to, float maxRad)
        {
            float lenFrom = math.length(from);
            if (lenFrom < 1e-6f) return from;
            float lenTo = math.length(to);
            if (lenTo < 1e-6f) return from;

            float2 nFrom = from / lenFrom;
            float2 nTo = to / lenTo;
            float cosAngle = math.clamp(math.dot(nFrom, nTo), -1f, 1f);
            float angle = math.acos(cosAngle);
            if (angle <= maxRad) return nTo * lenFrom;

            // Sign of the shorter rotation: a positive 2D cross product means
            // `to` lies counter-clockwise from `from`.
            float cross = nFrom.x * nTo.y - nFrom.y * nTo.x;
            float rad = cross >= 0f ? maxRad : -maxRad;
            return Rotate(nFrom, rad) * lenFrom;
        }

        /// First contact along p0→p1 vs all obstacles (and optionally the wall).
        /// Returns t ∈ [0,1] and the surface normal at the contact point.
        public static bool SweepArena(float2 p0, float2 p1, float padR,
            in ArenaSimConfig arena, bool includeWall, out float t, out float2 normal)
        {
            t = 1f; normal = float2.zero; bool hit = false;
            for (int o = 0; o < arena.ObstacleCount; o++)
                if (SegmentCircle(p0, p1, padR, arena.ObstaclePos[o],
                        arena.ObstacleRadius[o], out float to) && to < t)
                {
                    t = to; hit = true;
                    normal = math.normalizesafe(
                        math.lerp(p0, p1, to) - arena.ObstaclePos[o], new float2(1f, 0f));
                }
            if (includeWall && SegmentRingWall(p0, p1, padR, arena.Radius, out float tw)
                && tw < t)
            {
                t = tw; hit = true;
                normal = -math.normalizesafe(math.lerp(p0, p1, tw), new float2(1f, 0f));
            }
            return hit;
        }

        /// Iterative depenetration from obstacles and the wall; slides velocity.
        public static void Depenetrate(ref float2 pos, ref float2 vel, float radius,
            in ArenaSimConfig arena, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                bool any = false;
                for (int o = 0; o < arena.ObstacleCount; o++)
                    if (PushOutOfCircle(ref pos, radius, arena.ObstaclePos[o],
                            arena.ObstacleRadius[o], out float2 n))
                    { vel = Slide(vel, n); any = true; }
                if (ClampInsideRing(ref pos, radius, arena.Radius, out float2 wn))
                { vel = Slide(vel, wn); any = true; }
                if (!any) break;
            }
        }

        /// Canonical multiplayer spawn-point formula (Stage 2 Task 4, spec
        /// §3.2, fix-round 1 M-8) — single source of truth shared by
        /// SimulationWorld's constructor and SimConfigBuilder.Validate's
        /// spawn-clearance check (reuse > duplication). Lives here rather
        /// than on SimulationWorld because it is a pure formula with no
        /// dependency on world state, and Ring.Data (the builder's home)
        /// should not have to reach into the stateful world class just to
        /// call a formula. Solo (playerCount <= 1) spawns at the arena
        /// center, unchanged from the pre-Stage-2 single-player behaviour
        /// (189 pre-Stage-2 tests depend on it); otherwise index sits on a
        /// ring at Radius * PlayerSpawnRingFrac, evenly spaced by angle, with
        /// no seed-dependent rotation — spawn layout must stay reproducible
        /// across replays regardless of match seed.
        public static float2 SpawnPosFor(int index, int playerCount, in ArenaSimConfig arena)
        {
            if (playerCount <= 1) return float2.zero;
            float angle = index * 2f * math.PI / playerCount;
            float ringRadius = arena.Radius * arena.PlayerSpawnRingFrac;
            return new float2(math.cos(angle), math.sin(angle)) * ringRadius;
        }
    }
}
