using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// PUBLIC FOR EXACTLY ONE MEMBER (owner decision, 2026-08-08, Stage 2 Task
    /// 35 §0a, variant "a"): the class itself is `public` solely so `CanFire`
    /// below — the single home of the can-fire predicate — is reachable from
    /// outside `Ring.Simulation` at all. Everything else stays `internal`:
    /// `Update`/`AdvanceNoSpawn` are mutators no outside caller may drive
    /// (their own docs cover why), and `Advance`/`SpawnShot` were never public
    /// to begin with. The two consumers this opens the door for are ghost
    /// projectiles (Task 35, this same task) and the Presentation-side copy
    /// of this predicate at `SimulationRunner:127-146` (Task 43, not yet
    /// lifted). Resilience against future weapons/upgrades was checked before
    /// taking this decision: the predicate is parameterized entirely by
    /// `WeaponSimConfig`, and balance lives in data (CR 6) — no code changes
    /// with it.
    public static class WeaponSystem
    {
        /// Advances the weapon by one tick (spec §3.5): cooldown always ticks down,
        /// recoil always decays, and while FireHeld stays true the cooldown's
        /// fractional remainder carries into the next shot (no rounding drift) —
        /// possibly firing more than once per tick if dt outpaces the interval.
        ///
        /// ONE CORE, TWO SINKS (Stage 2 Task 30, C-1/I5). `worldOrNull` is the
        /// shot's sink: the authoritative world for `Update` below, and `null`
        /// for `AdvanceNoSpawn`, the seam a predicting client drives its own copy
        /// of PlayerState through. A null sink skips EXACTLY the three things a
        /// client must never own — the projectile itself, the spread draw that
        /// shapes it (together with the whole shot geometry, none of which writes
        /// to `p`), and the shooter's ShotsFired tally — and executes every other
        /// line, in the same order, on the same values. This is one body rather
        /// than two on purpose: the overshoot comes off FireCooldown BEFORE the
        /// increment, the cone comes off RecoilOffset BEFORE this shot
        /// accumulates into it, and the loop admits more than one shot per tick,
        /// so a second implementation of that bookkeeping would diverge on every
        /// single shot and reconciliation would be correcting the client for the
        /// whole length of a held trigger. Any reordering here moves the golden
        /// replay hash.
        ///
        /// Two fire modes (spec §3.2 v5, Task 15) share every line of that
        /// bookkeeping — including recoil accumulation — and part ways only on the
        /// shot's geometry and its cone: aimed fire (input.AimHeld) sends a genuine
        /// 3D round at (AimPoint, AimHeight) through a cone the aim-settle shrinks,
        /// hip fire keeps the flat horizontal shot through the movement-widened
        /// Spread.HipRadians cone. Both draw from the weapon RNG stream only when
        /// their cone is actually open, so perfectly settled recoil-free aim spends
        /// no randomness at all. That whole split lives in SpawnShot below, since
        /// it is the sink's business and not the bookkeeping's.
        static void Advance(ref PlayerState p, in SimInput input, in SimConfig cfg,
            SimulationWorld worldOrNull, byte ownerIndex)
        {
            float dt = SimulationWorld.TickDt;
            var weapon = cfg.Weapon;

            p.FireCooldown -= dt;
            p.RecoilOffset = math.max(0f, p.RecoilOffset - weapon.RecoilRecoveryRadPerSec * dt);

            if (!CanFire(in p, in input, in weapon))
            {
                // Clamp the floor so releasing-and-holding fire again doesn't cash in
                // a backlog of overshoot ticks as a burst — release means "reset to idle".
                p.FireCooldown = math.max(0f, p.FireCooldown);
                return;
            }

            // Safety net against an infinite loop if FireInterval is misconfigured to 0.
            float interval = math.max(weapon.FireInterval, 1e-3f);
            while (p.FireCooldown <= 0f)
            {
                if (worldOrNull != null)
                {
                    // The overshoot is read off FireCooldown as it stands NOW —
                    // before the increment at the bottom of this iteration — so
                    // it must be computed here, at the call, and not hoisted.
                    SpawnShot(worldOrNull, in p, in input, in cfg, ownerIndex,
                        math.min(-p.FireCooldown, dt));
                }
                p.RecoilOffset = math.min(weapon.RecoilMaxRad, p.RecoilOffset + weapon.RecoilPerShotRad);
                p.FireCooldown += interval;
            }
        }

        /// Authoritative weapon tick — the world's own weapon phase
        /// (SimulationWorld.TickAll).
        /// `ownerIndex` (Stage 2 Task 5) is the firing player's own index:
        /// ShotsFired is a personal counter, so it must land on THAT player's own
        /// MatchStats slot, not always player 0's. Widened from `int` to `byte` in
        /// Stage 2 Task 30 — every consumer downstream already speaks byte
        /// (SpawnProjectile's own ownerIndex, ProjectileIds.NoOwner), and the
        /// no-spawn twin below needs that sentinel to be expressible at all.
        internal static void Update(SimulationWorld w, ref PlayerState p, in SimInput input,
            byte ownerIndex)
            => Advance(ref p, in input, w.Config, w, ownerIndex);

        /// Prediction weapon tick (Stage 2 Task 30, spec §3.9) — the same core
        /// with the shot sink removed, so a client can advance the weapon's
        /// hashed state (FireCooldown, RecoilOffset) without ever spawning a
        /// round, drawing from the world RNG or crediting itself a shot. There is
        /// no owner to credit on this path, hence ProjectileIds.NoOwner: the
        /// sentinel is unreachable here by construction (the sink is null), and
        /// naming it is what keeps that fact readable instead of passing a 0 that
        /// would look like "player 0".
        internal static void AdvanceNoSpawn(ref PlayerState p, in SimInput input, in SimConfig cfg)
            => Advance(ref p, in input, in cfg, null, ProjectileIds.NoOwner);

        /// Single home of the "can fire this frame" predicate — consumed by
        /// Advance above, and by the client-side consumers that must agree with it
        /// exactly (SimulationRunner.WouldFireThisFrame, Stage 2 Task 43; ghost
        /// projectiles, Stage 2 Task 35), so the three can never drift apart.
        ///
        /// p.Alive is redundant for today's authoritative call site —
        /// SimulationWorld.TickAll (Task 23) only reaches Update from its Alive
        /// branch (Tick(in SimInput) is just the solo-player overload that
        /// forwards into TickAll, and throws outright for a multiplayer world) —
        /// and it is redundant for the prediction call site too, since prediction
        /// stops at death (Р41/Р59, Stage 2 Task 34) and a dead body is advanced
        /// by PlayerMovementSystem.UpdateDead instead. Kept as defense-in-depth so
        /// the predicate stays safe on a direct/future call site, not just its
        /// current ones.
        public static bool CanFire(in PlayerState p, in SimInput input, in WeaponSimConfig weapon)
            => input.FireHeld && p.Alive
               && (weapon.CanFireWhileDash || p.DashTimer <= 0f)
               && (weapon.CanFireWhileSlide || p.SlideTimer <= 0f);

        /// The shot itself: everything the authoritative sink owns and a
        /// predicting client must not (CR 3) — the round, the spread draw that
        /// shapes it, and the shooter's own ShotsFired tally. Lifted out of the
        /// former loop body of Update verbatim; `p` is passed `in` precisely
        /// because not one line here writes to it, which is what makes skipping
        /// the whole call on the prediction path incapable of moving the golden
        /// hash — the compiler holds that claim, not a comment.
        ///
        /// The one thing that changed place is `stats.ShotsFired++`, which the
        /// old loop body ran just AFTER the recoil accumulation instead of just
        /// before it. The two writes touch different memory and neither reads the
        /// other, so nothing observable is reordered; what MUST keep its place is
        /// the cone, which reads p.RecoilOffset BEFORE this tick's shot
        /// accumulates into it — and it does, because this whole call still
        /// precedes that accumulation.
        static void SpawnShot(SimulationWorld w, in PlayerState p, in SimInput input,
            in SimConfig cfg, byte ownerIndex, float overshoot)
        {
            var weapon = cfg.Weapon;
            // Task 15 (QC21): the fire branch reads the hero half of the config too
            // — muzzle heights (standing / mid-slide) and the aim-settle window.
            var hero = cfg.Hero;

            float muzzleH = p.SlideTimer > 0f ? hero.SlideMuzzleHeight : hero.MuzzleHeight;
            float a; float3 vel3;
            if (input.AimHeld)
            {
                // Aimed fire (Task 15): the round is a full 3D vector from the
                // muzzle to the aimed point, and the base cone shrinks as the
                // aim settles — but recoil never leaves it (D15: a spray is
                // never a laser, however settled the aim is).
                float settle = p.AimSettleTimer / hero.AimSettleSeconds;   // [0..1]
                a = p.RecoilOffset + weapon.SpreadRad * (1f - settle);
                float2 baseDir2 = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                float3 target3 = new float3(input.AimPoint, input.AimHeight);
                float3 muzzle3 = new float3(p.Pos + baseDir2 * weapon.MuzzleOffset, muzzleH);
                vel3 = math.normalizesafe(target3 - muzzle3, new float3(baseDir2, 0f))
                    * weapon.ProjectileSpeed;
            }
            else
            {
                // Hip fire: the flat Phase-1 geometry, widened by movement.
                a = Spread.HipRadians(in weapon, in p, in hero);
                float2 dir2 = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                vel3 = new float3(dir2 * weapon.ProjectileSpeed, 0f);
            }
            if (a > 0f)   // both modes draw — and only when there is a cone to draw from
            {
                float angle = w.SpreadRng.NextFloat(-a, a);
                // Rotation around the VERTICAL axis only (K10): the horizontal
                // pair turns, the climb rate rides along untouched, and the
                // renormalise keeps |vel3| at exactly ProjectileSpeed.
                float2 rotated = Geometry.Rotate(vel3.xy, angle);
                vel3 = math.normalizesafe(new float3(rotated, vel3.z), vel3) * weapon.ProjectileSpeed;
            }
            // K9: the fractional-remainder pre-advance walks the round along its
            // OWN line — horizontally by its horizontal speed, vertically by its
            // climb rate — so an aimed shot still passes through the aimed point.
            float2 dir2D = math.normalizesafe(vel3.xy,
                math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f)));
            float horizSpeed = math.length(vel3.xy);
            float2 spawnPos = p.Pos + dir2D * (weapon.MuzzleOffset + overshoot * horizSpeed);
            float height = muzzleH + overshoot * vel3.z;
            // ownerIndex (Stage 2 Task 7): this firing player's own index —
            // drives per-shooter ShotsHit/Kills credit (SimulationWorld.DamageMob).
            w.SpawnProjectile(ProjectileOwner.Player, ownerIndex, spawnPos, vel3.xy, height, vel3.z,
                weapon.Damage, weapon.ProjectileRadius, weapon.ProjectileLifetime);
            w.StatsRef(ownerIndex).ShotsFired++;
        }
    }
}
