using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Movement
{
    /// Result of one player-movement tick (Task 9) — the movement system's
    /// contract back to SimulationWorld.Tick, replacing the old plain-bool
    /// return (dash-started only). Later tasks extend it as more movement
    /// outcomes need to reach the world layer (slide/link-window — QC12);
    /// Task 10 adds the slide pair below.
    public struct MovementResult
    {
        public bool DashStarted, DashDenied;
        public bool SlideStarted, SlideDenied;
        /// Task 12: an active dash mirrored off a wall/obstacle this tick.
        /// RicochetPos is MoveWithCollisions' first-contact point, RicochetNormal
        /// its surface normal — SimulationWorld.Tick emits DashRicocheted with
        /// these as Pos/HitDir.
        public bool Ricocheted;
        public float2 RicochetPos, RicochetNormal;
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
            // Contract: DashRequested/SlideRequested are edge inputs — SimInputFrame.
            // ForTick/InputSampler latch them true for exactly one tick per press, not
            // for the whole time a key is held. A raw held-true request re-arms the
            // buffer every tick it's seen, but by construction that means a fresh
            // latch each time, so any deny event gated on the buffer still fires at
            // most once per latch, not once per tick.
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
            // Task 14: aim-down-sights settle progress — grows towards
            // AimSettleSeconds while AimHeld, decays at 2x that growth rate
            // once released; unconditional like the timers above.
            p.AimSettleTimer = input.AimHeld
                ? math.min(p.AimSettleTimer + dt, hero.AimSettleSeconds)
                : math.max(0f, p.AimSettleTimer - 2f * dt);

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
            // Task 11: true only inside the "slide tick — link of the SAME
            // chain" branch below. MoveWithCollisions is a single shared call
            // site at the bottom of this method (see its call-site comment),
            // so wall-stop damping — which only applies to slide movement —
            // needs its own flag to know which branch owned this tick.
            bool slideMoveTick = false;
            // Task 12: mirrors slideMoveTick — MoveWithCollisions is the single
            // shared call site at the bottom of this method, so the ricochet
            // check (which only makes sense for a tick the DASH branch owned)
            // needs its own flag, same reasoning as slideMoveTick's own comment.
            bool dashMoveTick = false;
            if (p.DashTimer > 0f)
            {
                dashMoveTick = true;
                p.DashTimer = math.max(0f, p.DashTimer - dt);
                p.Vel = p.DashDir * p.DashSpeedCur;
                // C13: opened exactly on the tick DashTimer crosses to 0, not
                // held open for the whole dash — checked here, right after the
                // decrement that can cause the transition.
                if (p.DashTimer <= 0f) p.PostDashSlideTimer = hero.PostDashSlideWindow;
            }
            else if (p.DashBufferTimer > 0f && p.SlideTimer <= 0f
                && (p.DashCooldown <= 0f || p.LinkWindowTimer > 0f))
                // QD10: a dash never starts while a slide is active — the
                // buffered request just keeps latching/decaying above until
                // the slide ends (or the buffer window itself expires).
                // Task 11 (C6): a dash requested inside the post-slide link
                // window bypasses the ordinary DashCooldown gate entirely —
                // that bypass is the whole point of the window. В1 fix-wave 3
                // (owner economy rework): it still pays the FULL dash cost —
                // the old discounted-dash-in-window model is gone — but a
                // linked dash's stamina GATE is lowered by LinkRefund below,
                // since the refund is guaranteed the instant the dash starts.
            {
                bool linked = p.LinkWindowTimer > 0f;
                float gate = linked ? hero.DashStaminaCost - hero.LinkRefund : hero.DashStaminaCost;
                if (p.Stamina >= gate)
                {
                    dashMoveTick = true;
                    float2 dir = math.lengthsq(input.MoveDir) > 1e-6f
                        ? math.normalizesafe(input.MoveDir)
                        : math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                    p.DashDir = dir;
                    p.DashTimer = hero.DashDuration;
                    p.DashCooldown = hero.DashCooldown;
                    p.IframeTimer = hero.DashIframes;
                    p.DashBufferTimer = 0f;
                    // Task 12: DashSpeedCur starts every fresh dash at full
                    // Hero.DashSpeed — a linked/re-dash never inherits a
                    // previous dash's ricochet-decayed speed.
                    p.DashSpeedCur = hero.DashSpeed;
                    p.Vel = dir * p.DashSpeedCur;
                    p.Stamina -= hero.DashStaminaCost;
                    // В1 fix-wave 3: a linked dash's refund lands on top of
                    // its own full-price payment, clamped to the pool — see
                    // this branch's opening comment for the gate side of it.
                    if (linked)
                        p.Stamina = math.min(hero.StaminaMax, p.Stamina + hero.LinkRefund);
                    p.StaminaRegenDelayTimer = hero.StaminaRegenDelay;
                    // Task 11 (C6): the window is consumed by USE, whether it
                    // paid for this dash or (being un-opened) was already 0.
                    p.LinkWindowTimer = 0f;
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
                    p.Vel = RegularMoveVel(p.Vel, input.MoveDir, input.AimHeld, hero, dt);
                }
            }
            else if (p.SlideTimer > 0f) // slide tick — link of the SAME chain (QC11)
            {
                slideMoveTick = true;
                p.SlideTimer = math.max(0f, p.SlideTimer - dt);
                float2 want = math.lengthsq(input.MoveDir) > 1e-6f
                    ? math.normalize(input.MoveDir)
                    : p.SlideDir;
                p.SlideDir = Geometry.RotateTowards(p.SlideDir, want, hero.SlideSteerRadPerSec * dt);
                // Task 14 (A11): AimHeld slows the slide from THIS tick's Vel —
                // reads input.AimHeld fresh every tick, so the multiplier takes
                // effect the same tick aim goes up or down, never a tick late.
                p.Vel = p.SlideDir * hero.SlideSpeed * (input.AimHeld ? hero.AimSlideSpeedMult : 1f);
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
                // В1 fix-wave 3 (owner economy rework): "linked slide" =
                // started while the post-dash window was still open —
                // captured BEFORE the window is zeroed below, same
                // "started inside its window" contract as the dash branch's
                // own LinkWindowTimer check above. Lowers the stamina gate
                // by LinkRefund, same reasoning as the dash branch.
                bool linked = p.PostDashSlideTimer > 0f;
                float gate = linked ? hero.SlideStaminaCost - hero.LinkRefund : hero.SlideStaminaCost;
                if (p.Stamina >= gate)
                {
                    p.Stamina -= hero.SlideStaminaCost;
                    if (linked)
                    {
                        // Refund lands on top of the full-price payment
                        // above, clamped to the pool. Owner clarification
                        // (В1 fix-wave 3 review): this is NOT a banked/global
                        // cooldown reset — DashCooldown keeps counting down
                        // untouched here. Chain continuation is exclusively
                        // the dash branch's own LinkWindowTimer bypass above:
                        // only a dash that ITSELF lands inside the window
                        // skips the remainder (and gets its own refund).
                        p.Stamina = math.min(hero.StaminaMax, p.Stamina + hero.LinkRefund);
                    }
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
                else
                {
                    // C1 (final review wave, app-n6g): the gate-failed path
                    // must still drive Vel from this tick's input — same
                    // "denied path still moves" contract as the dash
                    // branch's own gate-fail else above (:153) — otherwise
                    // the player coasts at frozen velocity for the whole
                    // SlideBufferWindow (~5 ticks at TickDt) every tick the
                    // buffer keeps retrying/decaying (C11 below), not just
                    // the request's own tick.
                    p.Vel = RegularMoveVel(p.Vel, input.MoveDir, input.AimHeld, hero, dt);
                    if (input.SlideRequested)
                    {
                        // C11: unlike dash, the buffer is NOT cleared on a
                        // missed attempt — it keeps decaying/rechecking
                        // every tick so a late stamina regen can still
                        // cover the cost before the window closes (PD12).
                        // Denied fires only on the request's own tick, so a
                        // still-buffered retry doesn't re-emit it every
                        // tick (symmetry with dash: once per charge).
                        result.SlideDenied = true; // world: StaminaDenied
                    }
                }
            }
            else
            {
                p.Vel = RegularMoveVel(p.Vel, input.MoveDir, input.AimHeld, hero, dt);
            }

            // Stamina regen (Tasks 9/10): only once the post-dash delay has
            // fully elapsed and the player isn't mid-dash or mid-slide this
            // tick (QD10 — sliding freezes regen exactly like dashing does).
            if (p.DashTimer <= 0f && p.StaminaRegenDelayTimer <= 0f && p.SlideTimer <= 0f)
                p.Stamina = math.min(hero.StaminaMax, p.Stamina + hero.StaminaRegenPerSec * dt);

            float2 target = p.Pos + p.Vel * dt;
            MoveWithCollisions(ref p.Pos, ref p.Vel, target, hero.Radius, cfg.Arena,
                out bool hit, out float2 hitNormal, out float2 contact);

            // Task 11 (M3/QA12): a slide that rams into a wall/obstacle near
            // head-on kills the slide outright instead of sliding along it —
            // SlideWallStopDot is the dot(-normal, SlideDir) threshold that
            // separates "hit it flat" from "grazed it at a shallow angle"
            // (Geometry.Slide, run inside MoveWithCollisions above, already
            // lets a shallow hit keep its tangential speed and keep going —
            // this only overrides the steep case). The link window is not
            // just left unopened here: if THIS tick is also the tick
            // SlideTimer crossed to 0 above (a normal exit and a wall-stop
            // landing on the very same tick), that grant is revoked too.
            if (slideMoveTick && hit
                && math.dot(-hitNormal, p.SlideDir) > hero.SlideWallStopDot)
            {
                p.SlideTimer = 0f;
                p.Vel = math.normalizesafe(p.Vel, p.SlideDir) * hero.MaxSpeed;
                p.RunUpTimer = 0f;
                p.LinkWindowTimer = 0f;
            }

            // Task 12 (guard at the CALLER, not inside MoveWithCollisions):
            // mirror the dash off the surface it just rammed into, dashMoveTick
            // restricting this to a tick the dash branch actually owned (a
            // stale DashDir from a long-finished dash must never mirror off an
            // unrelated wall the player later just walks into).
            // !result.Ricocheted caps this at <=1 per tick (M8): MoveWithCollisions
            // may run up to 3 internal correction iterations in a corner, but
            // hit/normal/contact only ever report the FIRST of them.
            if (dashMoveTick && hit && math.dot(p.DashDir, hitNormal) < 0f && !result.Ricocheted)
            {
                p.DashDir = math.reflect(p.DashDir, hitNormal);        // mirror (D9)
                p.DashSpeedCur *= hero.RicochetRetention;
                result.Ricocheted = true;                           // MovementResult (QC12)
                result.RicochetPos = contact; result.RicochetNormal = hitNormal;
                // Reflected vector applies FROM THE NEXT tick (D16): this
                // tick's Vel was already resolved by the slide inside
                // MoveWithCollisions above (it is NOT overwritten here) — only
                // the NEXT dash-continue tick reads the updated DashDir/
                // DashSpeedCur via `p.Vel = p.DashDir * p.DashSpeedCur` at the
                // top of this method.
            }
            return result;
        }

        /// Task 14: `aimHeld` caps the target speed at MaxSpeed * AimMoveSpeedFrac
        /// (the dash branches never call this — dash speed is untouched by the
        /// aim cap by construction, not by a guard here).
        static float2 RegularMoveVel(float2 vel, float2 moveDir, bool aimHeld, in HeroSimConfig hero, float dt)
        {
            if (math.lengthsq(moveDir) <= 1e-6f)
                return MoveTowards(vel, float2.zero, hero.Friction * dt);
            float maxSpeed = aimHeld ? hero.MaxSpeed * hero.AimMoveSpeedFrac : hero.MaxSpeed;
            return MoveTowards(vel, moveDir * maxSpeed, hero.Accel * dt);
        }

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
            MoveWithCollisions(ref p.Pos, ref p.Vel, target, hero.Radius, cfg.Arena,
                out _, out _, out _);
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
        ///
        /// `hit`/`normal`/`contact` (Task 11, QA8/QC13) report only the FIRST
        /// contact this resolve makes: a later iteration of the same call (a
        /// corner catching the just-depenetrated slide vector again) is purely
        /// internal correction, not a second distinguishable hit a caller
        /// (wall-stop here, the Task 12 ricochet event) should react to. The
        /// safety-net Depenetrate call below never reports through these
        /// either — it only resolves starting overlaps/config swaps, not a
        /// motion-driven contact.
        public static void MoveWithCollisions(ref float2 pos, ref float2 vel,
            float2 target, float radius, in ArenaSimConfig arena,
            out bool hit, out float2 normal, out float2 contact)
        {
            hit = false;
            normal = float2.zero;
            contact = float2.zero;
            for (int iter = 0; iter < 3; iter++)
            {
                if (!Geometry.SweepArena(pos, target, radius, arena, true,
                        out float t, out float2 n))
                { pos = target; break; }
                float2 hitPoint = math.lerp(pos, target, t);
                if (iter == 0) { hit = true; normal = n; contact = hitPoint; }
                pos = hitPoint + n * Geometry.Skin;
                vel = Geometry.Slide(vel, n);
                target = pos + Geometry.Slide(target - hitPoint, n);
            }
            // safety net against starting overlaps/config swaps
            Geometry.Depenetrate(ref pos, ref vel, radius, arena, 1);
        }
    }
}
