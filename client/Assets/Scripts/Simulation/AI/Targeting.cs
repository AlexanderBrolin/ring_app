using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.AI
{
    public static class Targeting
    {
        /// Intercept: shot aim point with lead factor (0 — aim at current position).
        public static float2 AimWithLead(float2 from, float2 targetPos, float2 targetVel,
            float projSpeed, float leadFactor)
        {
            float2 toT = targetPos - from;
            float a = math.dot(targetVel, targetVel) - projSpeed * projSpeed;
            float b = 2f * math.dot(toT, targetVel);
            float c = math.dot(toT, toT);
            float t = 0f;
            if (math.abs(a) < 1e-4f)
            {
                if (math.abs(b) > 1e-6f) t = math.max(0f, -c / b);
            }
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc >= 0f)
                {
                    float sq = math.sqrt(disc);
                    float t1 = (-b - sq) / (2f * a);
                    float t2 = (-b + sq) / (2f * a);
                    t = t1 > 0f ? t1 : math.max(0f, t2);
                }
            }
            float2 predicted = targetPos + targetVel * (t * leadFactor);
            return math.normalizesafe(predicted - from, new float2(1f, 0f));
        }

        /// Is the line of fire (segment from→to, projectile radius) clear of obstacles?
        public static bool HasLineOfFire(float2 from, float2 to, float padR,
            in ArenaSimConfig arena)
        {
            for (int o = 0; o < arena.ObstacleCount; o++)
                if (Geometry.SegmentCircle(from, to, padR,
                        arena.ObstaclePos[o], arena.ObstacleRadius[o], out _))
                    return false;
            return true;
        }
    }
}
