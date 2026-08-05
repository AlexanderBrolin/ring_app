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

        /// Melee swing-attack prediction (Task 13): the target's expected position
        /// `seconds` from now, extrapolated linearly from its current velocity.
        /// Unlike AimWithLead (projectile intercept at a fixed projectile speed —
        /// B12), this is NOT reused for that purpose: it double-clamps instead of
        /// solving an intercept time. First `maxSpeed` bounds the velocity itself,
        /// so a burst well above normal running speed (a dash) pulls the same lead
        /// a plain run at `maxSpeed` would, never further (A4/D2 — a dash must not
        /// bait the swing from farther away than running would). Then `maxLead`
        /// bounds the resulting offset distance in metres, so the swing's
        /// anticipation never reaches absurdly far even for a very fast target.
        public static float2 PredictPos(float2 pos, float2 vel, float maxSpeed,
            float seconds, float factor, float maxLead)
        {
            float2 lead = vel;
            float len = math.length(lead);
            if (len > maxSpeed) lead *= maxSpeed / len;          // a dash doesn't bait (A4/D2)
            float2 offset = lead * (seconds * factor);
            float offLen = math.length(offset);
            if (offLen > maxLead) offset *= maxLead / offLen;    // cap the lead distance
            return pos + offset;
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

        /// Stage 2 Task 8 Interfaces: the alive player nearest to `from` — the
        /// single shared seam MobAiSystem's per-mob target selection and
        /// WaveSystem's early-exit/spawn-distance/event-position reads all
        /// route through, replacing the old solo-only `w.Player`. Ties break
        /// on the SMALLER index (deterministic, no RNG — spec Р85); `false` +
        /// `index = -1` when nobody is alive. Plain loop over PlayerCount
        /// (<= Arena.MaxPlayers, currently 3) — no LINQ, no allocation, safe
        /// on the hot tick path.
        public static bool NearestAlivePlayer(SimulationWorld w, float2 from, out int index)
        {
            index = -1;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < w.PlayerCount; i++)
            {
                PlayerState p = w.PlayerAt(i);
                if (!p.Alive) continue;
                float distSq = math.distancesq(from, p.Pos);
                if (distSq < bestDistSq) // strict < — a later equal distance never displaces the smaller index
                {
                    bestDistSq = distSq;
                    index = i;
                }
            }
            return index >= 0;
        }
    }
}
