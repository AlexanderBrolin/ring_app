using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Movement
{
    internal static class PlayerMovementSystem
    {
        /// Advances movement (including dash) by one tick. Returns true the tick a
        /// dash starts (spec §3.4) so the world can bump MatchStats and emit
        /// PlayerDashed without this layer touching either.
        public static bool Update(ref PlayerState p, in SimInput input, in SimConfig cfg)
        {
            float dt = SimulationWorld.TickDt;
            var hero = cfg.Hero;
            p.DashBufferTimer = input.DashRequested
                ? hero.DashBufferWindow
                : math.max(0f, p.DashBufferTimer - dt);
            p.DashCooldown = math.max(0f, p.DashCooldown - dt);
            p.IframeTimer = math.max(0f, p.IframeTimer - dt);
            bool started = false;
            if (p.DashTimer > 0f)
            {
                p.DashTimer = math.max(0f, p.DashTimer - dt);
                p.Vel = p.DashDir * hero.DashSpeed;
            }
            else if (p.DashBufferTimer > 0f && p.DashCooldown <= 0f)
            {
                float2 dir = math.lengthsq(input.MoveDir) > 1e-6f
                    ? math.normalizesafe(input.MoveDir)
                    : math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                p.DashDir = dir;
                p.DashTimer = hero.DashDuration;
                p.DashCooldown = hero.DashCooldown;
                p.IframeTimer = hero.DashIframes;
                p.DashBufferTimer = 0f;
                p.Vel = dir * hero.DashSpeed;
                started = true;
            }
            else
            {
                p.Vel = math.lengthsq(input.MoveDir) > 1e-6f
                    ? MoveTowards(p.Vel, input.MoveDir * hero.MaxSpeed, hero.Accel * dt)
                    : MoveTowards(p.Vel, float2.zero, hero.Friction * dt);
            }
            float2 target = p.Pos + p.Vel * dt;
            MoveWithCollisions(ref p.Pos, ref p.Vel, target, hero.Radius, cfg.Arena);
            return started;
        }

        public static float2 MoveTowards(float2 cur, float2 target, float maxDelta)
        {
            float2 d = target - cur;
            float lsq = math.lengthsq(d);
            if (lsq <= maxDelta * maxDelta) return target;
            return cur + d / math.sqrt(lsq) * maxDelta;
        }

        /// Collide-and-slide (spec §3.4): sweep to contact, step off by Skin,
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
