using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Ring.Simulation.Movement;
using Unity.Mathematics;

namespace Ring.Simulation.AI
{
    /// Advances every live mob's FSM by one tick (spec Task 19, Phase 6).
    /// Chaser: Idle→Chase→Telegraph→Recover with a contact strike gated by
    /// AttackRange/TelegraphSeconds/AttackCooldown. Gunner: holds
    /// PreferredRange±RangeTolerance (Reposition when outside it), strafes by
    /// StrafeSign and fires under a FireCooldown/line-of-fire gate while inside
    /// the tolerance band (Fire). Movement always goes through
    /// PlayerMovementSystem.MoveWithCollisions — mobs share the player's
    /// collide-and-slide rules. No RNG: every branch is a pure function of
    /// current state (side selection ties break on entity Id parity).
    internal static class MobAiSystem
    {
        /// Strafe is considered blocked (StrafeSign inverts next tick) once the
        /// resulting speed drops below this fraction of the configured StrafeSpeed.
        const float StrafeBlockedFactor = 0.1f;

        public static void Update(SimulationWorld w)
        {
            float dt = SimulationWorld.TickDt;
            ArenaSimConfig arena = w.Config.Arena;
            MobState[] mobs = w.Mobs;
            int count = w.MobCount;

            for (int i = 0; i < count; i++)
            {
                ref MobState m = ref mobs[i];
                MobSimConfig cfg = w.MobConfigFor(m.Type);
                PlayerState player = w.Player;

                if (!player.Alive)
                {
                    m.Ai = MobAiState.Idle;
                    m.StateTimer = 0f;
                    m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    ApplyMotion(ref m, in cfg, in arena, dt);
                    continue;
                }

                if (m.Type == MobType.Chaser)
                    UpdateChaser(w, ref m, in cfg, in player, in arena, dt);
                else
                    UpdateGunner(w, ref m, in cfg, in player, in arena, dt);
            }
        }

        static void UpdateChaser(SimulationWorld w, ref MobState m, in MobSimConfig cfg,
            in PlayerState player, in ArenaSimConfig arena, float dt)
        {
            switch (m.Ai)
            {
                case MobAiState.Idle:
                    // Spends this tick settling into Chase — next tick starts closing
                    // distance. Tests budget for this ("a tick or two" of FSM warm-up).
                    m.Ai = MobAiState.Chase;
                    m.StateTimer = 0f;
                    m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    ApplyMotion(ref m, in cfg, in arena, dt);
                    return;

                case MobAiState.Chase:
                {
                    // Entry criterion (centre-to-centre <= AttackRange) is
                    // intentionally stricter than the strike's hit criterion below
                    // (CircleOverlap, which also folds in the hero's own body
                    // radius: effectively centre-to-centre < AttackRange +
                    // hero.Radius). That's a deliberate asymmetry, not something to
                    // unify: since AttackRange and hero.Radius are both positive,
                    // the moment Telegraph is entered the player is already well
                    // inside the strike's looser hit range too (by a hero.Radius
                    // margin), so the windup tolerates the player drifting or
                    // dashing a bit without the strike missing purely because of
                    // the two checks' different shapes. Tightening the entry check
                    // to match the hit check (or vice versa) would only make the
                    // Chaser commit to a windup either later or from further away
                    // than intended — not something either radius/range pair in
                    // the current balance needs.
                    float dist = math.distance(m.Pos, player.Pos);
                    if (dist <= cfg.AttackRange)
                    {
                        m.Ai = MobAiState.Telegraph;
                        m.StateTimer = 0f;
                        m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    }
                    else
                    {
                        float2 dir = SteerAround(m.Pos, player.Pos, in arena,
                            cfg.AvoidLookahead, cfg.Radius, cfg.AvoidMargin, m.Id);
                        m.Vel = PlayerMovementSystem.MoveTowards(m.Vel, dir * cfg.MaxSpeed,
                            cfg.Accel * dt);
                    }
                    ApplyMotion(ref m, in cfg, in arena, dt);
                    return;
                }

                case MobAiState.Telegraph:
                {
                    m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    ApplyMotion(ref m, in cfg, in arena, dt);
                    m.StateTimer += dt;
                    if (m.StateTimer >= cfg.TelegraphSeconds)
                    {
                        // Re-validated at the strike tick — the player may have moved
                        // (or dashed) out of range since the telegraph started. See
                        // the Chase-entry comment above for why this check being
                        // looser than the entry check is intentional.
                        if (Geometry.CircleOverlap(m.Pos, cfg.AttackRange,
                                player.Pos, w.Config.Hero.Radius))
                        {
                            // A fist is not aimed: it always reports Body and is
                            // NOT scaled by the zone table (Task 6) — the
                            // multipliers exist to reward aiming, which melee
                            // does none of. Direction is attacker → victim, the
                            // knock-reaction axis Presentation needs; the same
                            // pre-motion `player` snapshot the overlap check
                            // above uses, so both read one consistent frame.
                            w.DamagePlayer(cfg.ContactDamage, m.Pos, HitZone.Body,
                                math.normalizesafe(player.Pos - m.Pos, new float2(1f, 0f)));
                        }
                        m.Ai = MobAiState.Recover;
                        m.StateTimer = 0f;
                    }
                    return;
                }

                case MobAiState.Recover:
                {
                    m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    ApplyMotion(ref m, in cfg, in arena, dt);
                    m.StateTimer += dt;
                    if (m.StateTimer >= cfg.AttackCooldown)
                    {
                        m.Ai = MobAiState.Chase;
                        m.StateTimer = 0f;
                    }
                    return;
                }

                default:
                    // Defensive: a Chaser should never land in a Gunner-only state.
                    m.Ai = MobAiState.Chase;
                    m.StateTimer = 0f;
                    return;
            }
        }

        static void UpdateGunner(SimulationWorld w, ref MobState m, in MobSimConfig cfg,
            in PlayerState player, in ArenaSimConfig arena, float dt)
        {
            m.FireCooldown -= dt;
            // Floor clamp — mirrors WeaponSystem.cs's guarded player cooldown
            // (`p.FireCooldown = math.max(0f, p.FireCooldown);` while not firing).
            // Without it, every tick spent in Reposition or without LoS (the branches
            // below never touch FireCooldown) lets the decrement run unchecked into
            // negative "debt"; the instant the gunner re-acquires range+LoS, the debt
            // pays itself off as a several-shots-in-as-many-ticks volley instead of the
            // single immediate shot the FSM intends (F-1, up to ~4 shots in 0.13s
            // observed on a 5s approach — see MobAiTests.
            // Gunner_LongApproach_FiresAtMostOnceOnFirstWindow). Clamping to exactly 0
            // (rather than preserving the negative remainder) trades a fraction of a
            // tick's precision on the long-run average fire rate for that guarantee —
            // acceptable here since, unlike WeaponSystem's while-loop, this gate only
            // ever fires once per tick anyway.
            if (m.FireCooldown < 0f) m.FireCooldown = 0f;

            float2 toPlayer = player.Pos - m.Pos;
            float dist = math.length(toPlayer);
            float lower = cfg.PreferredRange - cfg.RangeTolerance;
            float upper = cfg.PreferredRange + cfg.RangeTolerance;

            if (dist < lower || dist > upper)
            {
                m.Ai = MobAiState.Reposition;
                float2 awayTarget = m.Pos
                    - math.normalizesafe(toPlayer, new float2(1f, 0f)) * cfg.AvoidLookahead;
                float2 target = dist > upper ? player.Pos : awayTarget;
                float2 dir = SteerAround(m.Pos, target, in arena, cfg.AvoidLookahead,
                    cfg.Radius, cfg.AvoidMargin, m.Id);
                m.Vel = PlayerMovementSystem.MoveTowards(m.Vel, dir * cfg.MaxSpeed, cfg.Accel * dt);
                ApplyMotion(ref m, in cfg, in arena, dt);
                return;
            }

            m.Ai = MobAiState.Fire;
            float2 radial = math.normalizesafe(toPlayer, new float2(1f, 0f));
            float2 tangent = new float2(-radial.y, radial.x) * m.StrafeSign;
            m.Vel = PlayerMovementSystem.MoveTowards(m.Vel, tangent * cfg.StrafeSpeed, cfg.Accel * dt);
            ApplyMotion(ref m, in cfg, in arena, dt);

            if (cfg.StrafeSpeed > 0f && math.length(m.Vel) < StrafeBlockedFactor * cfg.StrafeSpeed)
                m.StrafeSign = -m.StrafeSign;

            if (m.FireCooldown <= 0f
                && Targeting.HasLineOfFire(m.Pos, player.Pos, cfg.ProjectileRadius, in arena))
            {
                float2 aimDir = Targeting.AimWithLead(m.Pos, player.Pos, player.Vel,
                    cfg.ProjectileSpeed, cfg.LeadFactor);
                // Height/VelZ (Task 4): flat trajectory for now — spawns at the
                // gunner's muzzle height with zero vertical velocity.
                w.SpawnProjectile(ProjectileOwner.Mob, m.Pos + aimDir * cfg.Radius,
                    aimDir * cfg.ProjectileSpeed, cfg.MuzzleHeight, 0f,
                    cfg.ProjectileDamage, cfg.ProjectileRadius,
                    cfg.ProjectileLifetime);
                m.FireCooldown += cfg.FireInterval;
            }
        }

        /// Decelerates toward zero at `maxDelta` per tick (reuses MobSimConfig.Accel —
        /// mobs have no separate friction field, unlike the player hero).
        static float2 DecayVelocity(float2 vel, float maxDelta)
            => PlayerMovementSystem.MoveTowards(vel, float2.zero, maxDelta);

        /// Applies the current velocity through the shared collide-and-slide solver.
        static void ApplyMotion(ref MobState m, in MobSimConfig cfg, in ArenaSimConfig arena, float dt)
        {
            float2 target = m.Pos + m.Vel * dt;
            PlayerMovementSystem.MoveWithCollisions(ref m.Pos, ref m.Vel, target, cfg.Radius, in arena,
                out _, out _, out _);
        }

        /// Steering direction toward `targetPos`: the direct line unless an obstacle
        /// lies within `lookahead` along it, in which case it returns the exact
        /// tangent line from the mob's position to that obstacle's padded circle
        /// (classic external-point-to-circle tangent — right triangle pos/tangent
        /// point/centre, hypotenuse `d` = distance to centre, opposite side =
        /// padded radius, so the half-angle at pos is `asin(radius / d)`). Unlike a
        /// pure sideways tangent, this direction still has a net component toward
        /// the target — the earlier pure-tangent version left zero radial velocity
        /// relative to the obstacle centre, so the mob settled into a stable
        /// circular orbit right at the lookahead trigger boundary and never closed
        /// in (found via the RED run of Chaser_BehindObstacle_SteersAroundNotStuck:
        /// it parked at exactly obstacleRadius+padR+lookahead from the centre).
        /// Side is the sign of the cross product of the approach direction and the
        /// direction to the obstacle's centre; a dead-on approach (cross == 0)
        /// breaks the tie on the mob's Id parity so the choice stays deterministic
        /// without RNG. Only obstacles are considered (matches
        /// Targeting.HasLineOfFire) — the ring wall is handled by the physical
        /// collide-and-slide in MoveWithCollisions, not by look-ahead steering.
        /// `avoidMargin` (MobSimConfig.AvoidMargin — see its doc comment for the
        /// full rationale) pads the obstruction check beyond `mobRadius` so the
        /// mob doesn't hug the obstacle at the bare minimum clearance.
        static float2 SteerAround(float2 pos, float2 targetPos, in ArenaSimConfig arena,
            float lookahead, float mobRadius, float avoidMargin, int id)
        {
            float2 dir = math.normalizesafe(targetPos - pos, new float2(1f, 0f));
            float2 aheadEnd = pos + dir * lookahead;
            float padR = mobRadius + avoidMargin;

            int blockedIdx = -1;
            float bestT = 1f;
            for (int o = 0; o < arena.ObstacleCount; o++)
            {
                if (Geometry.SegmentCircle(pos, aheadEnd, padR, arena.ObstaclePos[o],
                        arena.ObstacleRadius[o], out float t) && t < bestT)
                {
                    bestT = t;
                    blockedIdx = o;
                }
            }
            if (blockedIdx < 0) return dir;

            float2 toCenter = arena.ObstaclePos[blockedIdx] - pos;
            float d = math.length(toCenter);
            float2 u = d > 1e-5f ? toCenter / d : new float2(-dir.y, dir.x);
            float radius = arena.ObstacleRadius[blockedIdx] + padR;
            // Clamped to 1 when pos is already at/inside the padded circle (grazing,
            // 90-degree tangent) instead of NaN-ing out of range for asin.
            float ratio = d > 1e-5f ? math.clamp(radius / d, 0f, 1f) : 1f;
            float theta = math.asin(ratio);

            float2 tangentPlus = Geometry.Rotate(u, theta);
            float2 tangentMinus = Geometry.Rotate(u, -theta);
            float cross = dir.x * u.y - dir.y * u.x;
            float2 tangent = cross > 0f ? tangentMinus
                : cross < 0f ? tangentPlus
                : (id & 1) == 0 ? tangentPlus : tangentMinus;
            return math.normalizesafe(tangent, dir);
        }
    }
}
