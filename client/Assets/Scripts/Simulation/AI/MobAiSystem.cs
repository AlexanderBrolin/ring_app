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

                // Stage 2 Task 17 (carryover-t17.md item 1, from the Task 8
                // review): the chaser's contact strike needs the target's INDEX,
                // not just its state — before this task the strike paid out to
                // player 0 regardless of whom the FSM had chosen, so a chaser
                // standing on player 1 hit someone across the arena. UpdateGunner
                // deliberately does NOT take the index: its shot is a projectile,
                // and a projectile's victim is decided by geometry in
                // ProjectileSystem (it can legitimately hit a player who walked
                // into the line of fire), so an index parameter there would be
                // carried and never read.
                if (m.Type == MobType.Chaser)
                {
                    UpdateChaser(w, ref m, in cfg, in player, targetIndex, in arena, dt);
                }
                else if (m.Type == MobType.Gunner)
                {
                    UpdateGunner(w, ref m, in cfg, in player, in arena, dt);
                }
                else
                {
                    // Elite/Director (Stage 3 Task 10, spec Р214/Р248,
                    // fourth of the fourteen two-way branches): "an
                    // enhanced chaser with ranged finishing" — no new
                    // sub-FSM, UpdateChaser/UpdateGunner are reused
                    // wholesale (rule 2), picked by DISTANCE to the
                    // current target: inside AttackRange it fights like a
                    // Chaser (melee windup + strike, Chase/Telegraph/
                    // Recover); outside it holds/kites like a Gunner
                    // (Reposition to PreferredRange, Fire under LoS). Both
                    // archetypes share this same six-value MobAiState the
                    // other two already use (Р214) — MaxMobAiStateValue
                    // does not move. Director never leaving the arena core
                    // (Р248) is Т22's own leash/activation logic, not this
                    // dispatch — this switch only decides HOW it fights
                    // once a target is already in range, the same as any
                    // other archetype here.
                    if (math.distance(m.Pos, player.Pos) <= cfg.AttackRange)
                        UpdateChaser(w, ref m, in cfg, in player, targetIndex, in arena, dt);
                    else
                        UpdateGunner(w, ref m, in cfg, in player, in arena, dt);
                }
            }
        }

        static void UpdateChaser(SimulationWorld w, ref MobState m, in MobSimConfig cfg,
            in PlayerState player, int targetIndex, in ArenaSimConfig arena, float dt)
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
                            // Stage 2 Task 17: the victim is `targetIndex` — the
                            // very player this FSM selected and re-validated the
                            // overlap against — and the attacker is
                            // ProjectileIds.NoOwner, since a mob owns no player
                            // slot and no player earns credit for its kill.
                            w.DamagePlayer(targetIndex, ProjectileIds.NoOwner, cfg.ContactDamage,
                                m.Pos, HitZone.Body,
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
                // ownerEntityId (Stage 3 Task 5, spec Р252): this gunner's OWN
                // id, so ProjectileSystem's gather phase can exclude it from its
                // own round's mob targets — the muzzle spawn point below sits ON
                // this mob's own collision circle, so without the exclusion a
                // gunner would wound itself at the moment it fires.
                w.SpawnProjectile(ProjectileOwner.Mob, ProjectileIds.NoOwner, m.Id,
                    m.Pos + aimDir * cfg.Radius,
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

        /// Steering direction toward `targetPos`: the direct line unless an
        /// obstacle (circle) or a wall lies within `lookahead` along it, in
        /// which case an avoidance direction is returned instead. Obstacles and
        /// walls compete for the SAME "nearest blocker" slot — whichever gives
        /// the smaller sweep parameter `t` wins; an exact tie keeps whichever
        /// was recorded first (circles are swept before walls, mirroring
        /// SweepArena's fixed circle-then-wall order).
        ///
        /// Circle blocker: the exact external tangent from the mob's position
        /// to the obstacle's padded circle (classic point-to-circle tangent —
        /// right triangle pos/tangent point/centre, hypotenuse `d` = distance
        /// to centre, opposite side = padded radius, half-angle at pos =
        /// `asin(radius / d)`). Unlike a pure sideways tangent, this direction
        /// still has a net component toward the target — a pure-tangent
        /// version left zero radial velocity relative to the obstacle centre,
        /// so the mob settled into a stable circular orbit right at the
        /// lookahead trigger boundary and never closed in (found via the RED
        /// run of Chaser_BehindObstacle_SteersAroundNotStuck: it parked at
        /// exactly obstacleRadius+padR+lookahead from the centre). Side is the
        /// sign of the cross product of the approach direction and the
        /// direction to the obstacle's centre; a dead-on approach (cross == 0)
        /// breaks the tie on the mob's Id parity so the choice stays
        /// deterministic without RNG.
        ///
        /// Wall blocker (Stage 2 Task 14, spec 3.3): NOT a tangent — see the
        /// coordinator's comment in the wall branch below for why a tangent
        /// (to the wall's body or to either end cap) fails for a wall. The mob
        /// instead heads for a WAYPOINT placed just outside whichever end
        /// (WallA/WallB) gives the shorter total detour (mob-&gt;end distance +
        /// end-&gt;target distance; a near-tie — within Geometry.Skin, the wall
        /// straddles the path close to symmetrically — breaks on the mob's Id
        /// parity so the choice is stable across ticks). The waypoint sits off
        /// the wall's face on the mob's own side (a near-tie there breaks the
        /// same way) AND past that end along the wall's axis, both offsets
        /// equal to `clearance` (wallHalfWidth + padR): being off the body on
        /// both axes guarantees an outward component the collide-and-slide can
        /// never fully cancel, so the mob can't dead-stop against the wall.
        /// Within `clearance` of the waypoint the heading switches to running
        /// straight along the axis instead (`normalizesafe` would otherwise
        /// fall back to the still-blocked direct line and park the mob
        /// there) — see the wall branch's own comment on why this fallback
        /// radius is NOT narrower than `clearance` despite the wobble/capture
        /// defect that width causes (app-jyv tracks the real fix). Past the
        /// end, steering re-evaluates every tick and reverts to the direct
        /// line as soon as the wall stops blocking.
        ///
        /// Only obstacles and walls are considered (matches
        /// `Targeting.HasLineOfFire`) — the ring wall is handled by the
        /// physical collide-and-slide in `MoveWithCollisions`, not by
        /// look-ahead steering.
        /// `avoidMargin` (MobSimConfig.AvoidMargin — see its doc comment for
        /// the full rationale) pads the obstruction check beyond `mobRadius`
        /// so the mob doesn't hug the obstacle/wall at the bare minimum
        /// clearance.
        static float2 SteerAround(float2 pos, float2 targetPos, in ArenaSimConfig arena,
            float lookahead, float mobRadius, float avoidMargin, int id)
        {
            float2 dir = math.normalizesafe(targetPos - pos, new float2(1f, 0f));
            float2 aheadEnd = pos + dir * lookahead;
            float padR = mobRadius + avoidMargin;

            int blockedCircleIdx = -1;
            int blockedWallIdx = -1;
            int blockedZoneWallIdx = -1;
            float bestT = 1f;
            for (int o = 0; o < arena.ObstacleCount; o++)
            {
                if (Geometry.SegmentCircle(pos, aheadEnd, padR, arena.ObstaclePos[o],
                        arena.ObstacleRadius[o], out float t) && t < bestT)
                {
                    bestT = t;
                    blockedCircleIdx = o;
                    blockedWallIdx = -1;
                    blockedZoneWallIdx = -1;
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
                    blockedZoneWallIdx = -1;
                }
            }
            // Stage 3 Task 9 (spec §3.2): zone-wall arcs join the SAME
            // "nearest blocker" competition, checked after obstacles/walls —
            // the same fixed circle-then-wall-then-arc order SweepArena uses.
            for (int zIdx = 0; zIdx < arena.ZoneWallCount; zIdx++)
            {
                var doorCenter = new System.ReadOnlySpan<float>(arena.DoorCenterRad,
                    arena.ZoneWallDoorStart[zIdx], arena.ZoneWallDoorCount[zIdx]);
                var doorFreeWidth = new System.ReadOnlySpan<float>(arena.DoorFreeWidth,
                    arena.ZoneWallDoorStart[zIdx], arena.ZoneWallDoorCount[zIdx]);
                if (Geometry.SegmentArc(pos, aheadEnd, padR, arena.ZoneWallRadius[zIdx],
                        arena.ZoneWallHalfWidth[zIdx], doorCenter, doorFreeWidth,
                        out float t, out _) && t < bestT)
                {
                    bestT = t;
                    blockedZoneWallIdx = zIdx;
                    blockedCircleIdx = -1;
                    blockedWallIdx = -1;
                }
            }
            if (blockedCircleIdx < 0 && blockedWallIdx < 0 && blockedZoneWallIdx < 0) return dir;

            if (blockedZoneWallIdx >= 0)
            {
                // Р118: a full-circle barrier has no "end" a tangent could
                // round — a tangent to it is a permanent mismatch with the
                // arc's own curvature (the mob would skate the ring forever,
                // never converging on the opening). The waypoint is instead
                // the nearest DOOR of the blocking wall — "nearest" measured
                // by total round-trip length (mob -> door -> target), the
                // same detour-cost idiom the wall branch below uses for
                // choosing an end. A near-tie (within Geometry.Skin) breaks
                // on the mob's own Id parity, same stability reasoning as
                // every other near-tie in this file (I-5/I-2 fix-round T14):
                // a bare `<` comparison is one ULP of independent sqrt-chain
                // rounding away from flipping which door two otherwise-
                // identical mobs commit to.
                int zIdx = blockedZoneWallIdx;
                int doorStart = arena.ZoneWallDoorStart[zIdx];
                int doorCount = arena.ZoneWallDoorCount[zIdx];
                float ringR = arena.ZoneWallRadius[zIdx];

                float2 bestDoorPoint = pos; // overwritten before use whenever doorCount > 0
                float bestCost = float.MaxValue;
                for (int j = 0; j < doorCount; j++)
                {
                    float doorAngle = arena.DoorCenterRad[doorStart + j];
                    float2 doorPoint = ringR * new float2(math.cos(doorAngle), math.sin(doorAngle));
                    float cost = math.distance(pos, doorPoint) + math.distance(doorPoint, targetPos);
                    bool takeIt = j == 0
                        || (math.abs(cost - bestCost) < Geometry.Skin ? (id & 1) == 0 : cost < bestCost);
                    if (takeIt)
                    {
                        bestCost = cost;
                        bestDoorPoint = doorPoint;
                    }
                }

                float2 toDoor = bestDoorPoint - pos;
                return math.normalizesafe(toDoor, dir);
            }

            if (blockedWallIdx >= 0)
            {
                // Coordinator fix (Stage 2 Task 14) — why NOT a tangent. A
                // circle IS the whole obstacle, so a tangent to it clears it.
                // A wall's blocking body is its side; an end cap is only that
                // body's edge, so the tangent to one cap can cut straight
                // through the body — the mob then steers into the wall, the
                // physical collide-and-slide cancels that velocity, and the
                // next tick reproduces the same geometry: a stable dead stop
                // (measured at 5.5e-17 parallelism to the flat face,
                // reproduced from five independent geometries). Picking the
                // cap by "whichever end is nearer" adds a second failure: for
                // a wall straddling the mob's path the two ends are
                // equidistant, the choice flips between them tick to tick,
                // and the mob oscillates in place instead of committing to a
                // detour. A tangent-based rule cannot fix this by choosing
                // differently either: a wall is routinely LONGER than
                // AvoidLookahead, and local look-ahead has no notion of
                // "which way is out" of a surface that extends past what it
                // can see. See the XML doc above for the waypoint approach
                // used instead (fix-round T14, C-1: this comment used to
                // duplicate that description verbatim — trimmed here to just
                // the "why not tangent" reasoning so the two can't drift out
                // of sync again).
                float2 wallA = arena.WallA[blockedWallIdx];
                float2 wallB = arena.WallB[blockedWallIdx];
                float clearance = arena.WallHalfWidth[blockedWallIdx] + padR;

                // Which end to round: the one giving the shorter total detour.
                // A near-tie (the wall straddles the path close to
                // symmetrically) breaks on Id parity — a constant per mob, so
                // the choice is STABLE across ticks. Breaking such a tie on a
                // bare float comparison instead is what makes a mob oscillate
                // on the spot: at the symmetric point the winner flips every
                // tick and no lateral progress is ever committed to.
                // I-5 (fix-round T14): costA/costB are two INDEPENDENT sqrt
                // chains, so an exact `==` comparison is one ULP of rounding
                // noise away from flipping — and unlike most float slop, the
                // consequence here is macroscopic: the mob commits to the
                // OPPOSITE side of the wall, not a barely different steering
                // angle. Widening the tie window to Geometry.Skin catches that
                // noise deterministically instead of leaving it to chance
                // (and, from Task 16 onward, to the golden hash).
                float costA = math.distance(pos, wallA) + math.distance(wallA, targetPos);
                float costB = math.distance(pos, wallB) + math.distance(wallB, targetPos);
                bool roundA = math.abs(costA - costB) < Geometry.Skin
                    ? (id & 1) == 0
                    : costA < costB;
                float2 end = roundA ? wallA : wallB;
                float2 farEnd = roundA ? wallB : wallA;

                // Aim just OUTSIDE that end, offset off the wall's face on the
                // mob's own side and past the cap along the axis. Steering at the
                // end itself would aim at the cap's centre — into the wall.
                float2 axis = math.normalizesafe(end - farEnd, dir);
                float2 face = new float2(-axis.y, axis.x);
                // I-2 (fix-round T14): the raw sign of this dot flips as the
                // mob passes the end's axis-perpendicular line (the waypoint
                // would jump to the wall's OTHER side from one tick to the
                // next), and at dot == 0 the result silently depended on
                // WallA/WallB authoring order. Same near-tie idiom as the end
                // selection above: within Geometry.Skin of zero, resolve on
                // Id parity instead of the raw sign.
                float faceDot = math.dot(pos - end, face);
                bool keepFace = math.abs(faceDot) < Geometry.Skin
                    ? (id & 1) == 0
                    : faceDot >= 0f;
                if (!keepFace) face = -face;
                float2 waypoint = end + axis * clearance + face * clearance;
                float2 toWaypoint = waypoint - pos;
                // Standing ON the waypoint has to keep producing a heading:
                // normalizesafe would fall back to `dir`, which is the blocked
                // straight line, and the mob would park there for good
                // (observed: it stopped exactly one waypoint's distance short
                // of the player). C-2 (fix-round T14): the fallback radius is
                // still the full `clearance` disc, NOT narrowed to
                // Geometry.Skin — see app-jyv for why the narrow radius that
                // review asked for turned out not to be safe. Inside ANY
                // fallback radius the heading (`axis`) ignores both the
                // target and the mob's own position, so a radius as wide as
                // `clearance` (2-2.5m at AvoidLookahead 3m) lets a mob wobble
                // near a single wall's end, and gives a stable capture
                // between two walls whose gap fits inside it — that's the
                // real defect app-jyv tracks, and its proper fix is per-tick
                // hysteresis on the chosen wall/end/side (needs a new
                // hashable MobState field, out of scope here). Narrowing this
                // radius to Geometry.Skin looked like a cheap partial
                // mitigation, but empirically it swaps one instability for a
                // WORSE one: shrunk that far, the mob keeps re-aiming at the
                // literal waypoint point at full MaxSpeed with no arrival
                // damping, which is an undamped pursuit-of-a-fixed-point
                // controller — it overshoots past the waypoint every tick and
                // never converges, orbiting it forever instead of ever
                // reaching the `axis`-only fallback (reproduced with
                // Chaser_NavigatesAroundWall's exact fixture: final distance
                // to player 0.72 at the `clearance` radius here, 11.01 at
                // Geometry.Skin — confirmed the wall branch's own
                // near-tie band changes from I-2/I-5 are NOT the cause, only
                // this radius is). A smaller-but-not-tiny radius does not
                // save it either — a sweep of this same fixture found the
                // orbit sets in below ~0.8x of `clearance`, too close to the
                // original value to buy any meaningful narrowing. Left at
                // `clearance`, unchanged, pending app-jyv's real fix. Past
                // the waypoint the detour continues along the axis, out
                // beyond the cap, until the wall no longer blocks at all.
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
