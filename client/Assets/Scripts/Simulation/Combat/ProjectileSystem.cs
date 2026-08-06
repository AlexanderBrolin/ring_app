using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Advances every live projectile by one tick (spec §3.5/§3.6): swept-circle
    /// collision against the ring wall, obstacles, and eligible targets under the
    /// damage matrix. Stage 2 Task 17 completed that matrix: a Player-owned round
    /// hits mobs AND every live player except its own owner (no self-damage by
    /// construction — the owner is never gathered), a Mob-owned round hits every
    /// live player and no mobs. Player targets are gated only on Alive here — the
    /// i-frame check happens inside SimulationWorld.DamagePlayer, not here. A
    /// projectile is single-target: it is consumed on its first contact, no
    /// piercing.
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
                // then mobs by index, then players by index, then floor (Task
                // 7) — so the packed array's index order doubles as the
                // tie-break order below, matching Task 1's original
                // streaming-min bit-for-bit.
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

                // Player targets (Stage 2 Task 17, spec §Ф4): BOTH owners reach
                // this loop — a mob's round is eligible against every live
                // player, a player's round against every live player but its own
                // shooter. The owner skip is what makes self-damage impossible;
                // it is a gather-phase rule rather than a check further down so
                // the owner never occupies a candidate slot at all. The loop
                // deliberately sits AFTER the mob loop and BEFORE the floor: it
                // occupies the slot the single hardcoded player candidate used
                // to, so the canonical packing order — and with it every t-tie
                // this scan resolves — is unchanged for a world that has only
                // one player.
                int playerCount = w.PlayerCount;
                for (int pi = 0; pi < playerCount; pi++)
                {
                    if (proj.Owner == ProjectileOwner.Player && pi == proj.OwnerIndex) continue;
                    PlayerState player = w.PlayerAt(pi);
                    if (player.Alive
                        && Geometry.SegmentCircle(startPos, target, proj.Radius,
                            player.Pos, heroRadius, out float tp))
                    {
                        candidates[candCount++] = (tp, HitPlayer, pi);
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
                // Stage 2 Task 17: the winning candidate's own index, which is a
                // MOB index for HitMob and a PLAYER index for HitPlayer (-1 for
                // the index-less barrier/floor kinds) — named for the target it
                // identifies rather than for one of the two kinds, now that
                // HitPlayer carries a real index of its own.
                int hitTargetIndex = -1;
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
                        // the HitNone path never reads hitTargetIndex.
                        hitKind = HitNone;
                        hitTargetIndex = -1;
                        break;
                    }

                    hitKind = candidates[bestSlot].kind;
                    hitTargetIndex = candidates[bestSlot].index;

                    if (AcceptCandidate(w, in config, in proj, startPos, target,
                            hitKind, hitTargetIndex, out hitZone, out hitMult))
                    {
                        break;
                    }

                    // Rejected: fall back to "no hit" before rescanning, so a
                    // scan that exhausts every candidate leaves the projectile
                    // flying instead of resolving the last one it looked at.
                    hitKind = HitNone;
                    hitTargetIndex = -1;
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
                        MobState mob = mobs[hitTargetIndex];
                        // playerIndex (Stage 2 Task 17, carryover-t17.md item 2):
                        // the SHOOTER, so Presentation can tell its own hitmarker
                        // from another player's in a multiplayer match.
                        w.Emit(SimEventKind.ProjectileHit, contact, mob.Id, mob.Type, dmg,
                            zone: hitZone, hitDir: hitDir, playerIndex: proj.OwnerIndex);
                        // ownerIndex (Stage 2 Task 7): the projectile carries its
                        // own shooter forward into the credit routing.
                        w.DamageMob(hitTargetIndex, dmg, contact, hitZone, hitDir, proj.OwnerIndex);
                        w.RemoveProjectileAt(i);
                        break;
                    }
                    case HitPlayer:
                    {
                        float2 contact = math.lerp(startPos, target, bestT);
                        float2 hitDir = math.normalizesafe(proj.Vel, new float2(1f, 0f));
                        // Stage 2 Task 17: victim = the player this scan actually
                        // resolved onto, attacker = the round's own shooter
                        // (ProjectileIds.NoOwner for a mob's round, which credits
                        // nobody — see DamagePlayer's own doc).
                        w.DamagePlayer(hitTargetIndex, proj.OwnerIndex, proj.Damage * hitMult,
                            contact, hitZone, hitDir);
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
        ///
        /// `targetIndex` (Stage 2 Task 17: was `mobIndex`) is the winning
        /// candidate's own index — a mob index under HitMob, a player index under
        /// HitPlayer, unused for the index-less barrier/floor kinds.
        static bool AcceptCandidate(SimulationWorld w, in SimConfig config, in ProjectileState proj,
            float2 p0, float2 p1, int kind, int targetIndex, out HitZone zone, out float mult)
        {
            zone = HitZone.None;
            mult = 1f;

            float2 targetPos;
            float targetRadius, legsTop, bodyTop, headTop, overlapTop, legsMult, bodyMult, headMult;
            if (kind == HitMob)
            {
                MobState mob = w.Mobs[targetIndex];
                MobSimConfig cfg = w.MobConfigFor(mob.Type);
                targetPos = mob.Pos;
                targetRadius = cfg.Radius;
                legsTop = cfg.LegsTop; bodyTop = cfg.BodyTop; headTop = cfg.HeadTop;
                overlapTop = headTop;
                legsMult = cfg.LegsDamageMult;
                bodyMult = cfg.BodyDamageMult;
                headMult = cfg.HeadDamageMult;
            }
            else if (kind == HitPlayer)
            {
                HeroSimConfig cfg = config.Hero;
                // Stage 2 Task 17: read ONCE off the player the gather phase
                // actually picked (was a pair of w.Player reads, i.e. always
                // player 0 — see this method's `targetIndex` note). Position and
                // slide profile must come from the SAME player, and one copy of
                // the struct is also one fewer indexer call on the hot path.
                PlayerState target = w.PlayerAt(targetIndex);
                targetPos = target.Pos;
                targetRadius = cfg.Radius;
                legsTop = cfg.LegsTop; bodyTop = cfg.BodyTop; headTop = cfg.HeadTop;
                // Task 11: mid-slide, the hero presents a lower profile — the
                // OVERLAP gate caps at SlideProfileTop instead of the standing
                // HeadTop, so a shot on a high horizontal line (e.g. a
                // Gunner's muzzle height) passes clean over a sliding target.
                // Zone CLASSIFICATION below is untouched: Classify still reads
                // the standing legs/body/head table, so a shot that DOES
                // connect while sliding resolves to whatever zone its entry
                // height actually falls in (Legs, or low Body, since
                // SlideProfileTop sits below BodyTop).
                overlapTop = target.SlideTimer > 0f ? cfg.SlideProfileTop : headTop;
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

            if (!HitZones.Overlaps(hEnter, hExit, proj.Radius, overlapTop)) return false;
            zone = HitZones.Classify(hEnter, legsTop, bodyTop, headTop);
            mult = HitZones.MultFor(zone, legsMult, bodyMult, headMult);
            return true;
        }
    }
}
