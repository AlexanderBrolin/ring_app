using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Movement
{
    internal static class PlayerMovementSystem
    {
        public static void Update(ref PlayerState p, in SimInput input, in SimConfig cfg)
        {
            float dt = SimulationWorld.TickDt;
            if (math.lengthsq(input.MoveDir) > 1e-6f)
                p.Vel = MoveTowards(p.Vel, input.MoveDir * cfg.Hero.MaxSpeed, cfg.Hero.Accel * dt);
            else
                p.Vel = MoveTowards(p.Vel, float2.zero, cfg.Hero.Friction * dt);
            float2 target = p.Pos + p.Vel * dt;
            MoveWithCollisions(ref p.Pos, ref p.Vel, target, cfg.Hero.Radius, cfg.Arena);
        }

        public static float2 MoveTowards(float2 cur, float2 target, float maxDelta)
        {
            float2 d = target - cur;
            float lsq = math.lengthsq(d);
            if (lsq <= maxDelta * maxDelta) return target;
            return cur + d / math.sqrt(lsq) * maxDelta;
        }

        /// Collide-and-slide (спека §3.4): sweep to contact, step off by Skin,
        /// slide the velocity AND the remaining motion, retry ≤3 times.
        /// (Naive "sweep then depenetrate" doesn't work: the sweep stops exactly
        /// at the surface, depenetration never triggers, velocity is never cut —
        /// the body freezes at the wall. Found in self-review.)
        public static void MoveWithCollisions(ref float2 pos, ref float2 vel,
            float2 target, float radius, in ArenaSimConfig arena)
        {
            for (int iter = 0; iter < 3; iter++)
            {
                if (!Geometry.SweepArena(pos, target, radius, arena, true,
                        out float t, out float2 n))
                { pos = target; break; }
                float2 contact = math.lerp(pos, target, t);
                pos = contact + n * Geometry.Skin;
                vel = Geometry.Slide(vel, n);
                target = pos + Geometry.Slide(target - contact, n);
            }
            // safety net against starting overlaps/config swaps
            Geometry.Depenetrate(ref pos, ref vel, radius, arena, 1);
        }
    }
}
