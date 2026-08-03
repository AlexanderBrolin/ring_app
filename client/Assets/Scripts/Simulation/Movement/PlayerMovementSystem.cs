using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Movement
{
    /// Result of one player-movement tick (Task 9) — the movement system's
    /// contract back to SimulationWorld.Tick, replacing the old plain-bool
    /// return (dash-started only). Later tasks extend it as more movement
    /// outcomes need to reach the world layer (slide/link-window — QC12);
    /// Task 9 itself only populates the two fields below.
    public struct MovementResult
    {
        public bool DashStarted, DashDenied;
    }

    internal static class PlayerMovementSystem
    {
        /// Advances movement (including dash and the stamina gate/regen, Task 9)
        /// by one tick. DashStarted/DashDenied (spec §3.4/§3.9) tell the world
        /// whether to bump MatchStats and emit PlayerDashed, or emit
        /// StaminaDenied, without this layer touching either.
        public static MovementResult Update(ref PlayerState p, in SimInput input, in SimConfig cfg)
        {
            float dt = SimulationWorld.TickDt;
            var hero = cfg.Hero;
            p.DashBufferTimer = input.DashRequested
                ? hero.DashBufferWindow
                : math.max(0f, p.DashBufferTimer - dt);
            p.DashCooldown = math.max(0f, p.DashCooldown - dt);
            p.IframeTimer = math.max(0f, p.IframeTimer - dt);
            // Task 9: counts down unconditionally like the timers above — set to
            // StaminaRegenDelay on dash start, gates regen below until it hits 0.
            p.StaminaRegenDelayTimer = math.max(0f, p.StaminaRegenDelayTimer - dt);

            var result = new MovementResult();
            if (p.DashTimer > 0f)
            {
                p.DashTimer = math.max(0f, p.DashTimer - dt);
                p.Vel = p.DashDir * hero.DashSpeed;
            }
            else if (p.DashBufferTimer > 0f && p.DashCooldown <= 0f)
            {
                if (p.Stamina >= hero.DashStaminaCost)
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
                    p.Stamina -= hero.DashStaminaCost;
                    p.StaminaRegenDelayTimer = hero.StaminaRegenDelay;
                    result.DashStarted = true;
                }
                else
                {
                    // Insufficient stamina: consume the buffered request right
                    // away (instead of leaving it to decay tick-by-tick) so the
                    // world's StaminaDenied emission fires once per buffer
                    // latch, not once per tick the buffer window happens to
                    // still be open (spec: denied no more than once per charge).
                    p.DashBufferTimer = 0f;
                    result.DashDenied = true;
                    p.Vel = RegularMoveVel(p.Vel, input.MoveDir, hero, dt);
                }
            }
            else
            {
                p.Vel = RegularMoveVel(p.Vel, input.MoveDir, hero, dt);
            }

            // Stamina regen (Task 9): only once the post-dash delay has fully
            // elapsed and the player isn't mid-dash this tick — the "and not
            // sliding" clause is added in Task 10 once slide exists.
            if (p.DashTimer <= 0f && p.StaminaRegenDelayTimer <= 0f)
                p.Stamina = math.min(hero.StaminaMax, p.Stamina + hero.StaminaRegenPerSec * dt);

            float2 target = p.Pos + p.Vel * dt;
            MoveWithCollisions(ref p.Pos, ref p.Vel, target, hero.Radius, cfg.Arena);
            return result;
        }

        static float2 RegularMoveVel(float2 vel, float2 moveDir, in HeroSimConfig hero, float dt)
            => math.lengthsq(moveDir) > 1e-6f
                ? MoveTowards(vel, moveDir * hero.MaxSpeed, hero.Accel * dt)
                : MoveTowards(vel, float2.zero, hero.Friction * dt);

        /// Advances a dead player's body by one tick (spec §3.12): input, dash and
        /// weapon are inert once Alive is false, but the world keeps ticking — the
        /// corpse still decelerates under friction and resolves collisions like a
        /// live body, it just never receives input or re-accelerates.
        public static void UpdateDead(ref PlayerState p, in SimConfig cfg)
        {
            float dt = SimulationWorld.TickDt;
            var hero = cfg.Hero;
            p.Vel = MoveTowards(p.Vel, float2.zero, hero.Friction * dt);
            float2 target = p.Pos + p.Vel * dt;
            MoveWithCollisions(ref p.Pos, ref p.Vel, target, hero.Radius, cfg.Arena);
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
