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
    }
}
