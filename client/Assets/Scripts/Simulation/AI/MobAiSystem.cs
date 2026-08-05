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

                // Stage 2 Task 8: target selection now goes through
                // NearestAlivePlayer (from THIS mob's own position) instead of
                // the old solo-only w.Player — for a solo world (PlayerCount
                // == 1) this reduces to exactly the same "the one player, if
                // alive" read as before. `false` (nobody alive) reuses the
                // SAME "go Idle" branch the old `!player.Alive` check used.
                if (!Targeting.NearestAlivePlayer(w, m.Pos, out int targetIndex))
                {
                    m.Ai = MobAiState.Idle;
                    m.StateTimer = 0f;
                    m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    ApplyMotion(ref m, in cfg, in arena, dt);
                    continue;
                }
                PlayerState player = w.PlayerAt(targetIndex);

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
                    // Entry criterion (Task 13/spec §3.6 v2): mob-centre to the
                    // player's PREDICTED position (Targeting.PredictPos, fed
                    // Hero.MaxSpeed/TelegraphSeconds/SwingLeadFactor/
                    // SwingLeadMaxMeters) <= AttackRange — not raw centre-to-centre.
                    // A player closing in (running or dashing at the mob) gets a
                    // forward-shifted prediction, so the windup can start before
                    // raw contact and the strike below lands on a target that kept
                    // closing instead of always being a beat late. A player
                    // standing still or moving away gets little or no forward
                    // shift — PredictPos degenerates to the exact raw position when
                    // SwingLeadFactor is 0 (see
                    // MobAiTests.SwingLeadZero_EntryTickEqualsE1Rule) — so entry
                    // stays at least as tight as the pre-Task-13 raw-distance rule
                    // in that case.
                    //
                    // This is intentionally NOT unified with the strike's hit
                    // criterion below (CircleOverlap, which folds in the hero's own
                    // body radius: effectively centre-to-centre < AttackRange +
                    // hero.Radius) — which of the two is the looser check now
                    // depends on the player's velocity at the entry tick (the
                    // predictive lead can make entry the looser one while the
                    // player is closing fast; it reverts to the old
                    // always-tighter-than-the-hit-check relationship otherwise).
                    // Either way, the strike still re-validates centre-to-centre
                    // distance honestly after TelegraphSeconds (see below) — an
                    // early predictive entry is never an automatic hit, only an
                    // earlier start of the windup clock.
                    float2 predictedPlayerPos = Targeting.PredictPos(player.Pos, player.Vel,
                        w.Config.Hero.MaxSpeed, cfg.TelegraphSeconds, cfg.SwingLeadFactor,
                        cfg.SwingLeadMaxMeters);
                    float dist = math.distance(m.Pos, predictedPlayerPos);
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
                // gunner's muzzle height with zero vertical velocity. ownerIndex
                // (Stage 2 Task 7): a mob never owns a player slot — NoOwner.
                w.SpawnProjectile(ProjectileOwner.Mob, ProjectileIds.NoOwner, m.Pos + aimDir * cfg.Radius,
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
        /// without RNG. Interior walls (Stage 2 Task 14, spec 3.3) compete for
        /// the SAME "nearest blocker" slot as obstacles below — a second loop
        /// over WallCount with the identical `t &lt; bestT` idiom, circles
        /// traversed first so an exact tie keeps the circle already recorded
        /// (mirrors SweepArena's fixed circle-then-wall order). A blocking
        /// wall is reduced to an equivalent circle at whichever END (WallA/
        /// WallB) is nearer the mob, not the nearest point on the wall's
        /// axis (task-14-context.md, coordinator decision): for a long side
        /// wall, "nearest point on axis" collapses to a tiny circle sitting
        /// right in front of the mob, whose shallow tangent barely deflects
        /// it — the mob keeps grinding into the wall via the physical
        /// collide-and-slide instead of actually detouring, exactly the
        /// "rubs along the corridor side" failure spec 3.3 calls out.
        /// Steering around the wall's actual end is what a detour physically
        /// means; the choice is re-evaluated every tick, so once the mob
        /// clears the end the wall stops blocking on its own. Only obstacles
        /// and walls are considered (matches Targeting.HasLineOfFire) — the
        /// ring wall is handled by the physical collide-and-slide in
        /// MoveWithCollisions, not by look-ahead steering.
        /// `avoidMargin` (MobSimConfig.AvoidMargin — see its doc comment for the
        /// full rationale) pads the obstruction check beyond `mobRadius` so the
        /// mob doesn't hug the obstacle at the bare minimum clearance.
        static float2 SteerAround(float2 pos, float2 targetPos, in ArenaSimConfig arena,
            float lookahead, float mobRadius, float avoidMargin, int id)
        {
            float2 dir = math.normalizesafe(targetPos - pos, new float2(1f, 0f));
            float2 aheadEnd = pos + dir * lookahead;
            float padR = mobRadius + avoidMargin;

            int blockedCircleIdx = -1;
            int blockedWallIdx = -1;
            float bestT = 1f;
            for (int o = 0; o < arena.ObstacleCount; o++)
            {
                if (Geometry.SegmentCircle(pos, aheadEnd, padR, arena.ObstaclePos[o],
                        arena.ObstacleRadius[o], out float t) && t < bestT)
                {
                    bestT = t;
                    blockedCircleIdx = o;
                    blockedWallIdx = -1;
                }
            }
            for (int wIdx = 0; wIdx < arena.WallCount; wIdx++)
            {
                if (Geometry.SegmentStadium(pos, aheadEnd, padR, arena.WallA[wIdx], arena.WallB[wIdx],
                        arena.WallHalfWidth[wIdx], out float t) && t < bestT)
                {
                    bestT = t;
                    blockedWallIdx = wIdx;
                    blockedCircleIdx = -1;
                }
            }
            if (blockedCircleIdx < 0 && blockedWallIdx < 0) return dir;

            if (blockedWallIdx >= 0)
            {
                // Stage 2 Task 14, coordinator fix after the implementer's
                // diagnosis. A wall is steered around by its SILHOUETTE, not by
                // reducing it to one equivalent circle at a chosen end.
                //
                // Why the simpler forms fail. A circle IS the whole obstacle, so
                // a tangent to it clears it. A wall's blocking body is its side;
                // an end cap is only that body's edge, so the tangent to one cap
                // can cut straight through the body — the mob then steers into
                // the wall, the physical collide-and-slide cancels that velocity,
                // and the next tick reproduces the same geometry: a stable dead
                // stop (measured at 5.5e-17 parallelism to the flat face,
                // reproduced from five independent geometries). Picking the cap
                // by "whichever end is nearer" adds a second failure: for a wall
                // straddling the mob's path the two ends are equidistant, the
                // choice flips between them tick to tick, and the mob oscillates
                // in place instead of committing to a detour.
                //
                // The fix therefore does not steer by a tangent at all. The mob
                // heads for a WAYPOINT placed just outside one end of the wall —
                // off the face on the mob's own side and past the cap along the
                // axis. Being off the body, that heading always keeps an outward
                // component, so the collide-and-slide can never cancel it whole
                // and no dead stop is reachable; once the mob rounds the end the
                // wall stops blocking and steering returns to the direct line.
                // A tangent-based rule cannot give that guarantee here: a wall is
                // routinely LONGER than AvoidLookahead, and local look-ahead has
                // no notion of "which way is out" of a surface that extends past
                // what it can see.
                float2 wallA = arena.WallA[blockedWallIdx];
                float2 wallB = arena.WallB[blockedWallIdx];
                float clearance = arena.WallHalfWidth[blockedWallIdx] + padR;

                // Which end to round: the one giving the shorter total detour.
                // An exact tie (the wall straddles the path symmetrically) breaks
                // on Id parity — a constant per mob, so the choice is STABLE
                // across ticks. Breaking such a tie on a float comparison instead
                // is what makes a mob oscillate on the spot: at the symmetric
                // point the winner flips every tick and no lateral progress is
                // ever committed to.
                float costA = math.distance(pos, wallA) + math.distance(wallA, targetPos);
                float costB = math.distance(pos, wallB) + math.distance(wallB, targetPos);
                bool roundA = costA < costB || (costA == costB && (id & 1) == 0);
                float2 end = roundA ? wallA : wallB;
                float2 farEnd = roundA ? wallB : wallA;

                // Aim just OUTSIDE that end, offset off the wall's face on the
                // mob's own side and past the cap along the axis. Steering at the
                // end itself would aim at the cap's centre — into the wall.
                float2 axis = math.normalizesafe(end - farEnd, dir);
                float2 face = new float2(-axis.y, axis.x);
                if (math.dot(pos - end, face) < 0f) face = -face;
                float2 waypoint = end + axis * clearance + face * clearance;
                float2 toWaypoint = waypoint - pos;
                // Standing ON the rounding point has to keep producing a heading:
                // normalizesafe would fall back to `dir`, which is the blocked
                // straight line, and the mob would park there for good (observed:
                // it stopped exactly one waypoint's distance short of the player).
                // Past that point the detour continues along the axis, out beyond
                // the cap, until the wall no longer blocks at all.
                return math.lengthsq(toWaypoint) > clearance * clearance
                    ? math.normalizesafe(toWaypoint, dir)
                    : axis;
            }

            // Circle branch — untouched by Stage 2 Task 14 and bit-for-bit as it
            // was before it: mob steering around obstacles is inside the golden
            // hash, so this arithmetic may not be re-expressed, only extended
            // alongside (the wall branch above returns before reaching here).
            float2 center = arena.ObstaclePos[blockedCircleIdx];
            float baseRadius = arena.ObstacleRadius[blockedCircleIdx];

            float2 toCenter = center - pos;
            float d = math.length(toCenter);
            float2 u = d > 1e-5f ? toCenter / d : new float2(-dir.y, dir.x);
            float radius = baseRadius + padR;
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
