using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Advances every live projectile by one tick (spec §3.5/§3.6): swept-circle
    /// collision against the ring wall, obstacles, and eligible targets under the
    /// damage matrix (Player-owned projectiles hit mobs; Mob-owned projectiles hit
    /// the player, gated only on Alive — the i-frame check happens inside
    /// SimulationWorld.DamagePlayer, not here). A projectile is single-target: it
    /// is consumed on its first contact, no piercing.
    internal static class ProjectileSystem
    {
        const int HitNone = 0, HitBarrier = 1, HitMob = 2, HitPlayer = 3, HitFloor = 4;

        /// Iterates back-to-front so RemoveProjectileAt's swap-remove never skips
        /// or re-visits a slot within this same pass (spec §3.13 item 11).
        public static void Update(SimulationWorld w)
        {
            float dt = SimulationWorld.TickDt;
            SimConfig config = w.Config;
            ArenaSimConfig arena = config.Arena;
            float chaserRadius = config.Chaser.Radius;
            float gunnerRadius = config.Gunner.Radius;
            float heroRadius = config.Hero.Radius;
            (float t, int kind, int index)[] candidates = w.ProjCandidates;

            for (int i = w.ProjectileCount - 1; i >= 0; i--)
            {
                ref ProjectileState proj = ref w.Projectiles[i];
                float2 startPos = proj.Pos;
                float2 target = startPos + proj.Vel * dt;
                proj.PrevPos = startPos;
                proj.Ttl -= dt;

                MobState[] mobs = w.Mobs;

                // Gather phase (Task 5 refactor): pack every actual geometry
                // hit into the scratch in canonical slot order — 0 = barrier,
                // then mobs by index, then player, then floor (Task 7) — so
                // the packed array's index order doubles as the tie-break
                // order below, matching Task 1's original streaming-min
                // bit-for-bit.
                int candCount = 0;
                if (Geometry.SweepArena(startPos, target, proj.Radius, in arena, true,
                        out float tArena, out float2 arenaNormal))
                {
                    candidates[candCount++] = (tArena, HitBarrier, -1);
                }

                if (proj.Owner == ProjectileOwner.Player)
                {
                    int mobCount = w.MobCount;
                    for (int m = 0; m < mobCount; m++)
                    {
                        float mobRadius = mobs[m].Type == MobType.Chaser ? chaserRadius : gunnerRadius;
                        if (Geometry.SegmentCircle(startPos, target, proj.Radius,
                                mobs[m].Pos, mobRadius, out float tm))
                        {
                            candidates[candCount++] = (tm, HitMob, m);
                        }
                    }
                }
                else
                {
                    PlayerState player = w.Player;
                    if (player.Alive
                        && Geometry.SegmentCircle(startPos, target, proj.Radius,
                            player.Pos, heroRadius, out float tp))
                    {
                        candidates[candCount++] = (tp, HitPlayer, -1);
                    }
                }

                // Floor candidate (Task 7): a descending shot (VelZ < 0) crosses
                // the ground when its centre height reaches Radius (the sphere's
                // underside at z = 0). t_floor solves proj.Height + t*VelZ*dt =
                // Radius for t; only gathered when that crossing genuinely falls
                // within THIS tick's step — clipped to [0,1] the same way
                // SegmentCircle/SegmentRingWall reject an out-of-range root above,
                // rather than forcing a distant crossing to register early.
                // Packed LAST (canonical slot order, M5): a barrier/mob/player tie
                // at the same t always outranks the floor.
                if (proj.VelZ < 0f)
                {
                    float tFloor = (proj.Radius - proj.Height) / (proj.VelZ * dt);
                    if (tFloor >= 0f && tFloor <= 1f)
                    {
                        candidates[candCount++] = (tFloor, HitFloor, -1);
                    }
                }

                // Repeated min-scan, no sort/delegates (AllocationTests): picks
                // the smallest-t candidate among those not yet excluded, using
                // strict `<` so the first-packed (= lowest canonical slot)
                // candidate wins ties. Task 6 activates the rejection branch: a
                // candidate the shot passes OVER or UNDER (height gate) is
                // excluded via swap-remove and the scan repeats over what is
                // left, so a target further down the line is still reachable
                // through a screening one (M5). Every rejection shrinks
                // candCount by one, so the loop runs at most candCount times.
                float bestT = 1f;
                int hitKind = HitNone;
                int hitMobIndex = -1;
                HitZone hitZone = HitZone.None;
                float hitMult = 1f;
                while (candCount > 0)
                {
                    int bestSlot = -1;
                    bestT = 1f;
                    for (int c = 0; c < candCount; c++)
                    {
                        if (candidates[c].t < bestT)
                        {
                            bestT = candidates[c].t;
                            bestSlot = c;
                        }
                    }
                    if (bestSlot < 0)
                    {
                        // Same "no hit" fallback as the rejection branch below —
                        // both exits must leave the pair consistent, even though
                        // the HitNone path never reads hitMobIndex.
                        hitKind = HitNone;
                        hitMobIndex = -1;
                        break;
                    }

                    hitKind = candidates[bestSlot].kind;
                    hitMobIndex = candidates[bestSlot].index;

                    if (AcceptCandidate(w, in config, in proj, startPos, target,
                            hitKind, hitMobIndex, out hitZone, out hitMult))
                    {
                        break;
                    }

                    // Rejected: fall back to "no hit" before rescanning, so a
                    // scan that exhausts every candidate leaves the projectile
                    // flying instead of resolving the last one it looked at.
                    hitKind = HitNone;
                    hitMobIndex = -1;
                    candidates[bestSlot] = candidates[--candCount];
                }

                switch (hitKind)
                {
                    case HitBarrier:
                    {
                        float2 contact = math.lerp(startPos, target, bestT);
                        float contactHeight = proj.Height + proj.VelZ * dt * bestT;
                        // Wall/obstacle: HitDir is the real SweepArena surface
                        // normal (Task 7 — D12/C5, no "≈0" heuristic).
                        w.Emit(SimEventKind.ProjectileBlocked, contact, proj.Id, default,
                            contactHeight, hitDir: arenaNormal);
                        w.RemoveProjectileAt(i);
                        break;
                    }
                    case HitFloor:
                    {
                        float2 contact = math.lerp(startPos, target, bestT);
                        float contactHeight = proj.Height + proj.VelZ * dt * bestT;
                        // Floor: no modelled surface normal — HitDir is exactly
                        // zero, not an approximation (Task 7 — D12/C5).
                        w.Emit(SimEventKind.ProjectileBlocked, contact, proj.Id, default,
                            contactHeight, hitDir: float2.zero);
                        w.RemoveProjectileAt(i);
                        break;
                    }
                    case HitMob:
                    {
                        float2 contact = math.lerp(startPos, target, bestT);
                        float2 hitDir = math.normalizesafe(proj.Vel, new float2(1f, 0f));
                        // Multiplier applies BEFORE the event: Amount is the
                        // damage actually dealt, so Presentation never has to
                        // re-derive it from a base value it cannot see.
                        float dmg = proj.Damage * hitMult;
                        MobState mob = mobs[hitMobIndex];
                        w.Emit(SimEventKind.ProjectileHit, contact, mob.Id, mob.Type, dmg,
                            zone: hitZone, hitDir: hitDir);
                        w.DamageMob(hitMobIndex, dmg, contact, hitZone, hitDir);
                        w.RemoveProjectileAt(i);
                        break;
                    }
                    case HitPlayer:
                    {
                        float2 contact = math.lerp(startPos, target, bestT);
                        float2 hitDir = math.normalizesafe(proj.Vel, new float2(1f, 0f));
                        w.DamagePlayer(proj.Damage * hitMult, contact, hitZone, hitDir);
                        w.RemoveProjectileAt(i);
                        break;
                    }
                    default:
                        proj.Pos = target;
                        proj.PrevHeight = proj.Height;
                        proj.Height += proj.VelZ * dt;
                        if (proj.Ttl <= 0f)
                        {
                            w.Emit(SimEventKind.ProjectileExpired, proj.Pos, proj.Id, default, 0f);
                            w.RemoveProjectileAt(i);
                        }
                        break;
                }
            }
        }

        /// Height gate + zone resolution for the candidate the min-scan just
        /// picked (Task 6). Returns false when the shot passes clear over (or
        /// under) the target's column, which sends the scan back for the next
        /// candidate; on true, `zone`/`mult` describe the blow.
        ///
        /// The projectile's height is not a point: it moves by VelZ·dt across the
        /// step, so the test uses the height at BOTH ends of the chord through
        /// the target — hence Geometry.SegmentCircleInterval rather than the
        /// gather phase's entry-only SegmentCircle. The zone itself is read at
        /// the ENTRY height: that is where the round first touches the body.
        static bool AcceptCandidate(SimulationWorld w, in SimConfig config, in ProjectileState proj,
            float2 p0, float2 p1, int kind, int mobIndex, out HitZone zone, out float mult)
        {
            zone = HitZone.None;
            mult = 1f;

            float2 targetPos;
            float targetRadius, legsTop, bodyTop, headTop, legsMult, bodyMult, headMult;
            if (kind == HitMob)
            {
                MobState mob = w.Mobs[mobIndex];
                MobSimConfig cfg = w.MobConfigFor(mob.Type);
                targetPos = mob.Pos;
                targetRadius = cfg.Radius;
                legsTop = cfg.LegsTop; bodyTop = cfg.BodyTop; headTop = cfg.HeadTop;
                legsMult = cfg.LegsDamageMult;
                bodyMult = cfg.BodyDamageMult;
                headMult = cfg.HeadDamageMult;
            }
            else if (kind == HitPlayer)
            {
                HeroSimConfig cfg = config.Hero;
                targetPos = w.Player.Pos;
                targetRadius = cfg.Radius;
                legsTop = cfg.LegsTop; bodyTop = cfg.BodyTop; headTop = cfg.HeadTop;
                legsMult = cfg.LegsDamageMult;
                bodyMult = cfg.BodyDamageMult;
                headMult = cfg.HeadDamageMult;
            }
            else if (kind == HitBarrier)
            {
                // Barrier (obstacle or ring wall): no modelled top — stops a
                // shot at any height.
                return true;
            }
            else // HitFloor
            {
                // Floor (Task 7): the gather phase already solved the exact
                // within-tick height crossing, so every gathered floor
                // candidate is a genuine contact — nothing further to test.
                // Not a damageable body: no zone (zone/mult already default to
                // None/1 at the top of this function).
                return true;
            }

            float hStart = proj.Height;
            float hEnd = hStart + proj.VelZ * SimulationWorld.TickDt;
            // Fallback [0,1] = the step's full height span. Unreachable for a
            // gathered candidate (both solvers answer the same quadratic, and the
            // gather phase already found this circle on this segment); keeping it
            // conservative means a hypothetical disagreement can only ever let a
            // hit through, never silently swallow one.
            float tEnter = 0f, tExit = 1f;
            if (Geometry.SegmentCircleInterval(p0, p1, proj.Radius, targetPos, targetRadius,
                    out float chordEnter, out float chordExit))
            {
                tEnter = chordEnter;
                tExit = chordExit;
            }
            float hEnter = math.lerp(hStart, hEnd, tEnter);
            float hExit = math.lerp(hStart, hEnd, tExit);

            if (!HitZones.Overlaps(hEnter, hExit, proj.Radius, headTop)) return false;
            zone = HitZones.Classify(hEnter, legsTop, bodyTop, headTop);
            mult = HitZones.MultFor(zone, legsMult, bodyMult, headMult);
            return true;
        }
    }
}
