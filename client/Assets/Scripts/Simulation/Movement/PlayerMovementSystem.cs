using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Movement
{
    /// Result of one player-movement tick (Task 9) — the movement system's
    /// contract back to SimulationWorld.Tick, replacing the old plain-bool
    /// return (dash-started only). Later tasks extend it as more movement
    /// outcomes need to reach the world layer (slide/link-window — QC12);
    /// Task 10 adds the slide pair below (ricochet fields arrive in Task 12).
    public struct MovementResult
    {
        public bool DashStarted, DashDenied;
        public bool SlideStarted, SlideDenied;
    }

    internal static class PlayerMovementSystem
    {
        /// Advances movement (dash, slide, and the stamina gate/regen — Tasks 9
        /// and 10) by one tick. DashStarted/DashDenied/SlideStarted/SlideDenied
        /// (spec §3.4/§3.9) tell the world whether to bump MatchStats and emit
        /// PlayerDashed/PlayerSlideStarted, or emit StaminaDenied, without this
        /// layer touching either.
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
            // Task 10: same DashBufferTimer edge-latch pattern, for a buffered
            // slide request; PostDashSlideTimer/LinkWindowTimer are plain
            // countdowns (opened elsewhere in this method, below).
            p.SlideBufferTimer = input.SlideRequested
                ? hero.SlideBufferWindow
                : math.max(0f, p.SlideBufferTimer - dt);
            p.PostDashSlideTimer = math.max(0f, p.PostDashSlideTimer - dt);
            p.LinkWindowTimer = math.max(0f, p.LinkWindowTimer - dt);

            // Run-up accrues only while neither dash nor slide own this tick's
            // Vel (M9/C32), and only while actual speed clears the slide-start
            // threshold; below it, it decays instead. Reads Vel as it stood at
            // the END of the previous tick (this tick's chain hasn't set it
            // yet) — a deliberate one-tick lag, not a bug (see RunUp_Decays...
            // BelowThreshold's test comment).
            bool moving = math.length(p.Vel) >= hero.SlideMinSpeedFrac * hero.MaxSpeed;
            if (p.DashTimer <= 0f && p.SlideTimer <= 0f)
                p.RunUpTimer = moving
                    ? math.min(p.RunUpTimer + dt, hero.RunUpSeconds)
                    : math.max(0f, p.RunUpTimer - hero.RunUpDecayMult * dt);

            // A slide may start either off a full run-up, or inside the short
            // post-dash window that substitutes for one (spec §3.3 v5).
            bool slideGate = p.RunUpTimer >= hero.RunUpSeconds || p.PostDashSlideTimer > 0f;

            var result = new MovementResult();
            if (p.DashTimer > 0f)
            {
                p.DashTimer = math.max(0f, p.DashTimer - dt);
                p.Vel = p.DashDir * hero.DashSpeed;
                // C13: opened exactly on the tick DashTimer crosses to 0, not
                // held open for the whole dash — checked here, right after the
                // decrement that can cause the transition.
                if (p.DashTimer <= 0f) p.PostDashSlideTimer = hero.PostDashSlideWindow;
            }
            else if (p.DashBufferTimer > 0f && p.DashCooldown <= 0f && p.SlideTimer <= 0f)
                // QD10: a dash never starts while a slide is active — the
                // buffered request just keeps latching/decaying above until
                // the slide ends (or the buffer window itself expires).
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
            else if (p.SlideTimer > 0f) // slide tick — link of the SAME chain (QC11)
            {
                p.SlideTimer = math.max(0f, p.SlideTimer - dt);
                float2 want = math.lengthsq(input.MoveDir) > 1e-6f
                    ? math.normalize(input.MoveDir)
                    : p.SlideDir;
                p.SlideDir = Geometry.RotateTowards(p.SlideDir, want, hero.SlideSteerRadPerSec * dt);
                // AimHeld speed multiplier arrives in Task 14 — plain SlideSpeed here.
                p.Vel = p.SlideDir * hero.SlideSpeed;
                // C22: a normal exit opens the link window and keeps this
                // tick's full slide-speed Vel as exit momentum — the NEXT
                // tick's regular-movement branch decays it towards MaxSpeed.
                if (p.SlideTimer <= 0f) p.LinkWindowTimer = hero.LinkWindowSeconds;
                // Movement resolution for this branch happens via the single
                // shared MoveWithCollisions call below (same call site as
                // every other branch) — wall-stop damping specific to sliding
                // is Task 11's addition on top of that shared call, not a
                // second call site here.
            }
            else if (p.SlideBufferTimer > 0f && slideGate) // slide start
            {
                if (p.Stamina >= hero.SlideStaminaCost)
                {
                    p.Stamina -= hero.SlideStaminaCost;
                    p.StaminaRegenDelayTimer = hero.StaminaRegenDelay;
                    p.SlideTimer = hero.SlideDuration;
                    // M2: no chaining off the run-up/post-dash gate that just
                    // fired — a second slide needs its own fresh gate.
                    p.SlideBufferTimer = 0f;
                    p.PostDashSlideTimer = 0f;
                    p.RunUpTimer = 0f;
                    // D6: MoveDir, else current Vel, else face the aim point —
                    // never slide "in place" with a zero direction.
                    p.SlideDir = math.lengthsq(input.MoveDir) > 1e-6f
                        ? math.normalize(input.MoveDir)
                        : math.lengthsq(p.Vel) > 1e-6f
                            ? math.normalize(p.Vel)
                            : math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                    result.SlideStarted = true; // world: PlayerSlideStarted + SlidesUsed++
                }
                else if (input.SlideRequested)
                {
                    // C11: unlike dash, the buffer is NOT cleared on a missed
                    // attempt — it keeps decaying/rechecking every tick so a
                    // late stamina regen can still cover the cost before the
                    // window closes (PD12). Denied fires only on the request's
                    // own tick, so a still-buffered retry doesn't re-emit it
                    // every tick (symmetry with dash: once per charge).
                    result.SlideDenied = true; // world: StaminaDenied
                }
            }
            else
            {
                p.Vel = RegularMoveVel(p.Vel, input.MoveDir, hero, dt);
            }

            // Stamina regen (Tasks 9/10): only once the post-dash delay has
            // fully elapsed and the player isn't mid-dash or mid-slide this
            // tick (QD10 — sliding freezes regen exactly like dashing does).
            if (p.DashTimer <= 0f && p.StaminaRegenDelayTimer <= 0f && p.SlideTimer <= 0f)
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
