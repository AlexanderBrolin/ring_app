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
            // Stage 3 Ф5 gate (owner decision R-200): read ONCE for the tick —
            // the leash below is a property of the raid's phase, not of any
            // one mob, and re-reading a property inside the loop is the
            // pattern this file already avoids for `arena`.
            MatchPhase phase = w.Match.Phase;

            for (int i = 0; i < count; i++)
            {
                ref MobState m = ref mobs[i];
                MobSimConfig cfg = w.MobConfigFor(m.Type);
                // DECIDED BEFORE THE MOB MOVES, and that is the whole trick
                // (owner decision R-200): an elite is retinue because it
                // STANDS in the core, so asking after the move would let a
                // step across the boundary decide the answer — and asking
                // about a mob that is already outside would TELEPORT it in.
                float leashRing = LeashRingFor(in m, in arena, phase);

                // app-88jb Т6 (spec §3.2, coordinator Ruling 20): THE EXIT
                // FROM Downed LIVES HERE, ahead of the dispatch by MobType
                // below, and NOT in UpdateChaser's `switch (m.Ai)`. Three
                // reasons, all read off this file:
                //   * a Gunner never enters UpdateChaser at all, so a branch
                //     there could not keep one from firing while it is down;
                //   * UpdateGunner never READS m.Ai and rewrites it every
                //     tick regardless (Reposition :338 / Fire :349, by
                //     distance alone), so it would overwrite Downed on the
                //     very next tick -- and UpdateChaser's own `default:` arm
                //     (:304-308) would do the same to a downed Chaser, and to
                //     a downed Elite inside AttackRange, resetting it to
                //     Chase. All four archetypes, not one;
                //   * the entry lives in TiltSystem, where the tilt is
                //     integrated; splitting the exit into one archetype's arm
                //     would give one state two unrelated homes.
                // The SHAPE is the "nobody alive" guard's, a few lines below:
                // a state that cancels the archetype FSM wholesale is settled
                // before the dispatch picks one, and leaves through `continue`.
                //
                // WHAT THIS GUARD CANCELS IS THE ARCHETYPE FSM, NOT THE TICK
                // (coordinator Ruling 22, lesson 512 -- a doc that promises
                // more than it delivers is a defect at birth). A downed body
                // neither steers, nor strikes, nor fires, and it never calls
                // ApplyMotion, so it advances no Pos of its own. It is still
                // acted upon: SeparationSystem runs right after this loop and
                // can push it out of geometry through Geometry.Depenetrate,
                // and TiltSystem keeps walking its spring. "Downed" is a
                // canceled decision, not a frozen body.
                //
                // AHEAD OF THE "NOBODY ALIVE" GUARD ON PURPOSE. That guard
                // writes m.Ai = Idle and m.StateTimer = 0f unconditionally,
                // so behind it a downed mob would pop upright the moment the
                // last collector died or extracted -- and, because the guard
                // fires again every such tick, its timer would re-zero
                // forever. Ordering here is by how much of the FSM the state
                // leaves standing: Downed (nothing) before no-target
                // (movement only) before the archetype FSM.
                //
                // Vel IS BLED OFF, NOT LEFT ALONE AND NOT SNAPPED TO ZERO
                // (coordinator Ruling 23), with the same DecayVelocity line
                // the guard below uses. Leaving it alone is not neutral:
                // SeparationSystem adds into Vel with `+=` and no ceiling, so
                // across a whole Downed window an untouched Vel is an
                // accumulator with no drain, and the body would launch on its
                // feet. At Accel * dt the drain is 0.83 m/s per tick against a
                // MaxSpeed of 4, so any legal speed is gone within five ticks
                // of the 36.
                if (m.Ai == MobAiState.Downed)
                {
                    m.StateTimer += dt;
                    if (m.StateTimer >= cfg.DownedSeconds)
                    {
                        m.Ai = MobAiState.Idle;
                        m.StateTimer = 0f;
                    }
                    m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    continue;
                }

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
                    ApplyMotion(ref m, in cfg, in arena, dt, leashRing);
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
                    UpdateChaser(w, ref m, in cfg, in player, targetIndex, in arena, dt, leashRing);
                }
                else if (m.Type == MobType.Gunner)
                {
                    UpdateGunner(w, ref m, in cfg, in player, in arena, dt, leashRing);
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
                    // archetypes share the same MobAiState the other two
                    // already use, adding no state of their own (Р214).
                    // ⚠ This used to end "— six-value … MaxMobAiStateValue
                    // does not move", and app-88jb Т6 canceled both halves:
                    // Downed joined the enum and MaxMobAiStateValue moved to
                    // it, with ProtocolVersion 3 → 4 in the same commit. Р214
                    // still holds, because Downed is nobody's archetype state
                    // — it is what any body past TiltFallAngle does, and the
                    // gate at the top of Update is where it is served.
                    // Director never leaving the arena core
                    // (Р248) is NOT decided here: since Т22 it is enforced
                    // in ApplyMotion below (LeashToRing), the one place
                    // every mob's motion lands. This switch only decides HOW
                    // it fights once a target is already in range, the same
                    // as any other archetype here.
                    if (math.distance(m.Pos, player.Pos) <= cfg.AttackRange)
                        UpdateChaser(w, ref m, in cfg, in player, targetIndex, in arena, dt, leashRing);
                    else
                        UpdateGunner(w, ref m, in cfg, in player, in arena, dt, leashRing);
                }
            }
        }

        static void UpdateChaser(SimulationWorld w, ref MobState m, in MobSimConfig cfg,
            in PlayerState player, int targetIndex, in ArenaSimConfig arena, float dt,
            float leashRing)
        {
            switch (m.Ai)
            {
                case MobAiState.Idle:
                    // Spends this tick settling into Chase — next tick starts closing
                    // distance. Tests budget for this ("a tick or two" of FSM warm-up).
                    m.Ai = MobAiState.Chase;
                    m.StateTimer = 0f;
                    m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    ApplyMotion(ref m, in cfg, in arena, dt, leashRing);
                    return;

                case MobAiState.Chase:
                {
                    // Entry criterion (Task 13/spec §3.6 v2): mob-center to the
                    // player's PREDICTED position (Targeting.PredictPos, fed
                    // Hero.MaxSpeed/TelegraphSeconds/SwingLeadFactor/
                    // SwingLeadMaxMeters) <= AttackRange — not raw center-to-center.
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
                    // body radius: effectively center-to-center < AttackRange +
                    // hero.Radius) — which of the two is the looser check now
                    // depends on the player's velocity at the entry tick (the
                    // predictive lead can make entry the looser one while the
                    // player is closing fast; it reverts to the old
                    // always-tighter-than-the-hit-check relationship otherwise).
                    // Either way, the strike still re-validates center-to-center
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
                    ApplyMotion(ref m, in cfg, in arena, dt, leashRing);
                    return;
                }

                case MobAiState.Telegraph:
                {
                    m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    ApplyMotion(ref m, in cfg, in arena, dt, leashRing);
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
                            // hitHeight (app-88jb Т3): the TARGET's own center of
                            // mass, not the attacker's — `cfg` here is
                            // `w.MobConfigFor(m.Type)`, the config of the mob
                            // THROWING the punch (its own ContactDamage is the
                            // neighboring argument above), and its
                            // CenterOfMassHeight belongs to a different struct
                            // entirely (MobSimConfig, not HeroSimConfig). Reading
                            // it here would substitute the chaser's own CoM
                            // (1.17) for the victim collector's (0.95), turning a
                            // level moment arm into +0.22 m and making a fist
                            // knock the collector down along the swing — a defect
                            // no test catches, only the source read does. The
                            // right source is the VICTIM's own body, and every
                            // collector shares one HeroSimConfig regardless of
                            // which one this FSM picked.
                            // projectileMass/projectileSpeed3D (app-88jb Т7):
                            // ZERO, AND THAT IS A DECISION RATHER THAN A
                            // DEFAULT (spec §3.2). A contact strike gives no
                            // knockback: there is no round behind it, and the
                            // only mass this FSM could offer -- the puncher's
                            // own body -- would make a chaser's fist shove
                            // harder than a rifle round. Impact.VelocityDelta
                            // returns exactly 0 for a zero mass, so the shove
                            // and the moment both vanish arithmetically
                            // instead of through a branch. Stated here, at the
                            // call site, because DamagePlayer takes both
                            // REQUIRED for precisely this reason.
                            w.DamagePlayer(targetIndex, ProjectileIds.NoOwner, cfg.ContactDamage,
                                m.Pos, HitZone.Body,
                                math.normalizesafe(player.Pos - m.Pos, new float2(1f, 0f)),
                                hitHeight: w.Config.Hero.CenterOfMassHeight,
                                projectileMass: 0f, projectileSpeed3D: 0f);
                        }
                        m.Ai = MobAiState.Recover;
                        m.StateTimer = 0f;
                    }
                    return;
                }

                case MobAiState.Recover:
                {
                    m.Vel = DecayVelocity(m.Vel, cfg.Accel * dt);
                    ApplyMotion(ref m, in cfg, in arena, dt, leashRing);
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
            in PlayerState player, in ArenaSimConfig arena, float dt, float leashRing)
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
                ApplyMotion(ref m, in cfg, in arena, dt, leashRing);
                return;
            }

            m.Ai = MobAiState.Fire;
            float2 radial = math.normalizesafe(toPlayer, new float2(1f, 0f));
            float2 tangent = new float2(-radial.y, radial.x) * m.StrafeSign;
            m.Vel = PlayerMovementSystem.MoveTowards(m.Vel, tangent * cfg.StrafeSpeed, cfg.Accel * dt);
            ApplyMotion(ref m, in cfg, in arena, dt, leashRing);

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
                //
                // ⛔ A MOB'S REWIND DEPTH IS ZERO, AND THAT IS A RULE RATHER
                // THAN A DEFAULT (app-88jb Т27, spec §3.6, coordinator RULING
                // 177). A mob has no client and no one-way delay, so there is
                // no lag to compensate: its round takes no catch-up steps and
                // is answered against no rewound picture. THE ZERO IS PRODUCED
                // BY THIS PATH BEING A DIFFERENT ONE, not by a check — the
                // catch-up lives in the collector's weapon phase
                // (WeaponSystem.SpawnShot), and this call goes straight to the
                // world, past WeaponSystem entirely. Written down because a
                // quantity produced by the ABSENCE of a rule leaves nothing for
                // the next reader to tell apart from an oversight, and because
                // RewindTests.MobFiredRound_GetsNoRewindAtAll is a sentinel
                // guarding exactly this: what kills it is moving the catch-up
                // into this path, not damaging a line of it.
                //   THE PICTURE HALF IS ZERO ON THE SAME GROUNDS AND IS STATED
                // OUT LOUD (app-88jb Т28): the literal below is the depth this
                // round will ask of the past, and a mob asks nothing -- it is
                // answered against the bodies where they stand, exactly as
                // every round was before lag compensation existed.
                //   AND THE BIRTH-TICK STEP COUNT IS THEREFORE EXACTLY ONE,
                // WRITTEN OUT RATHER THAN LEFT TO THE DEFAULT (app-88jb Т32,
                // coordinator Ruling 291). The parameter's default is 0, which
                // means "nothing is known about this round's birth tick" — the
                // honest answer for the test seam and a FALSEHOOD here: this
                // round takes no catch-up (the two lines above say why), but
                // ProjectileSystem.Update still steps it once before the tick
                // ends, because MobAiSystem runs ahead of the projectile phase
                // in SimulationWorld.TickAll. One step is known, so one step is
                // stated.
                w.SpawnProjectile(ProjectileOwner.Mob, ProjectileIds.NoOwner, m.Id,
                    m.Pos + aimDir * cfg.Radius,
                    aimDir * cfg.ProjectileSpeed, cfg.MuzzleHeight, 0f,
                    cfg.ProjectileDamage, cfg.ProjectileRadius,
                    cfg.ProjectileLifetime, 0, birthSteps: 1);
                m.FireCooldown += cfg.FireInterval;
            }
        }

        /// Decelerates toward zero at `maxDelta` per tick (reuses MobSimConfig.Accel —
        /// mobs have no separate friction field, unlike the player hero).
        static float2 DecayVelocity(float2 vel, float maxDelta)
            => PlayerMovementSystem.MoveTowards(vel, float2.zero, maxDelta);

        /// Applies the current velocity through the shared collide-and-slide solver.
        static void ApplyMotion(ref MobState m, in MobSimConfig cfg, in ArenaSimConfig arena, float dt,
            float leashRing)
        {
            float2 target = m.Pos + m.Vel * dt;
            PlayerMovementSystem.MoveWithCollisions(ref m.Pos, ref m.Vel, target, cfg.Radius, in arena,
                out _, out _, out _);
            if (leashRing > NoLeash) LeashToRing(ref m, in cfg, leashRing);
        }

        /// "No ring" — a leash radius no arena ring can take, because a ring
        /// of zero radius is not a place a body can be held inside. Kept as a
        /// named constant rather than a bare 0f so the one comparison in
        /// ApplyMotion above reads as the question it asks.
        const float NoLeash = 0f;

        /// WHICH RING A MOB MAY NOT LEAVE — its radius, or NoLeash (spec §3.4
        /// Р248 for the Director; Ф5 gate review A-5 and owner decision R-200
        /// for his retinue; bd app-d2ki, owner decision on the В1 playtest, for
        /// the middle ring's elite).
        ///
        /// A RADIUS, NOT A BOOL, SINCE app-d2ki: two different rings now hold
        /// two different populations, and the caller threads ONE answer down
        /// through the whole FSM to the single place motion lands. A second
        /// bool would have meant a second clamp call at every one of those
        /// eight sites (rule 2).
        ///
        /// THE DIRECTOR, ALWAYS — the unconditional half, unchanged from Т22:
        /// his fight is the core's fight, and no FSM branch, not even the
        /// "nobody alive, go Idle" drift, may carry him out.
        ///
        /// AN ELITE STANDING IN THE CORE, ONCE THE RAID'S ENDGAME HAS BEGUN.
        /// This is the retinue, and it is derived exactly the way Р215 demands
        /// — "retinue" is not a mark on a mob, it is a live elite in the core,
        /// which is the same reading MatchFlowSystem.LiveRetinueCount takes.
        /// Before this, that reading was GAMEABLE: nothing held the retinue in,
        /// so a collector could walk two elites out of the core, the count
        /// would drop, and the top-up would breed replacements every period —
        /// an unbounded supply of loot-dropping elites, wave slots eaten, and
        /// the "unreachable" cap branch in TopUpRetinue made reachable after
        /// all. Holding them in makes the definition true by construction
        /// instead of by hope.
        ///
        /// THE PHASE GUARD IS R-185'S, REUSED, NOT A SECOND RULE. The core
        /// belongs to the Director from the moment he wakes — which is exactly
        /// when it leaves the wave budget — so `Phase != Farm` says "the
        /// endgame is running" once, for both. During Farm an elite in the
        /// core is an ORDINARY WAVE MOB and keeps its ordinary freedom to
        /// chase; that half is also what keeps every golden scenario
        /// untouched, since neither ever leaves Farm.
        ///
        /// THE MIDDLE RING'S ELITE, IN EVERY PHASE (bd app-d2ki). The outer
        /// ring is the raid's ENTRANCE: a collector lands there with nothing,
        /// and ADR-001 §3.1 gives the arena a difficulty curve that RISES
        /// toward the core. An elite that follows a runner out of the middle
        /// ring carries the middle ring's difficulty to the entrance and
        /// flattens that curve — which is what the owner reported on the В1
        /// playtest. The ring it is held against is the one it STANDS in, so
        /// an elite born in the entrance ring belongs to the entrance ring and
        /// is never dragged inward: the clamp holds a body IN, it never pulls
        /// one IN.
        ///
        /// AND IT CARRIES NO PHASE GUARD, deliberately. R-185's latch answers
        /// "whose home ground is the core", a question only the endgame
        /// raises. This rule answers "how hard may the entrance be", which is
        /// true from the first wave to the last — and the FARM phase is the
        /// only phase the reported defect ever occurred in.
        ///
        /// ZONELESS ARENAS ARE A LEGAL INPUT (lesson 315) and Geometry.ZoneOf
        /// has no guard of its own — so the answer is NoLeash before anything
        /// indexes ZoneRadius. Since app-d2ki that guard stands FIRST rather
        /// than beside the core test: every branch below now returns a radius
        /// read out of ZoneRadius, the Director's included. Its old home was
        /// the clamp helper, which made the Director's "always" a half-truth —
        /// true of the decision, silently false of the motion.
        static float LeashRingFor(in MobState m, in ArenaSimConfig arena, MatchPhase phase)
        {
            if (arena.ZoneRadius.Length < 2) return NoLeash;
            if (m.Type == MobType.Director) return arena.ZoneRadius[0];
            if (m.Type != MobType.Elite) return NoLeash;

            Zone zone = Geometry.ZoneOf(m.Pos, in arena);
            if (zone == Zone.Middle) return arena.ZoneRadius[1];
            if (zone == Zone.Core && phase != MatchPhase.Farm) return arena.ZoneRadius[0];
            return NoLeash;
        }

        /// Stage 3 Т22 (spec §3.4 Р248, coordinator R-184): THE DIRECTOR NEVER
        /// LEAVES THE CORE. Applied here, in the one place every mob's motion
        /// actually lands, so no FSM branch — including the "nobody alive, go
        /// Idle" one that drifts on decaying velocity — can carry him out.
        ///
        /// IT IS THE BODY THAT IS LEASHED, NOT THE TARGET, and that is a
        /// deliberate departure from the plan's own wording (plan :1439 asks
        /// for the TARGET to be clamped to the core radius). The dispatch above hands the
        /// SAME target to the melee and the ranged half, so a clamped target
        /// would have him SHOOTING AT THE ZONE BOUNDARY instead of at the
        /// collector standing past it — the spec asks that he not walk out
        /// (§3.4), not that he go blind. A clamped target would not even give
        /// the invariant: inertia and SeparationSystem's push can carry a body
        /// across a line its target never crossed.
        ///
        /// The primitive pair is the arena rim's own (Geometry.Depenetrate:
        /// ClampInsideRing, then Slide against the returned normal) — the same
        /// arithmetic that keeps every body inside the arena, aimed at a zone
        /// boundary instead.
        ///
        /// SINCE app-d2ki IT TAKES THE RING AS A NUMBER and holds no opinion
        /// about which one: LeashRingFor above is the single place that
        /// decides, and it has already answered NoLeash for a zoneless arena
        /// before this is ever reached (lesson 315). Two rules, one clamp.
        static void LeashToRing(ref MobState m, in MobSimConfig cfg, float ringRadius)
        {
            if (Geometry.ClampInsideRing(ref m.Pos, cfg.Radius, ringRadius, out float2 normal))
                m.Vel = Geometry.Slide(m.Vel, normal);
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
        /// right triangle pos/tangent point/center, hypotenuse `d` = distance
        /// to center, opposite side = padded radius, half-angle at pos =
        /// `asin(radius / d)`). Unlike a pure sideways tangent, this direction
        /// still has a net component toward the target — a pure-tangent
        /// version left zero radial velocity relative to the obstacle center,
        /// so the mob settled into a stable circular orbit right at the
        /// lookahead trigger boundary and never closed in (found via the RED
        /// run of Chaser_BehindObstacle_SteersAroundNotStuck: it parked at
        /// exactly obstacleRadius+padR+lookahead from the center). Side is the
        /// sign of the cross product of the approach direction and the
        /// direction to the obstacle's center; a dead-on approach (cross == 0)
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
                // end itself would aim at the cap's center — into the wall.
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
