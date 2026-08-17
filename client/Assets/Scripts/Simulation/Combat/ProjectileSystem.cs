using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Advances every live projectile by one tick (spec §3.5/§3.6): swept-circle
    /// collision against the ring wall, obstacles, and eligible targets under the
    /// damage matrix. Stage 2 Task 17 completed the player half of that matrix: a
    /// Player-owned round hits mobs AND every live player except its own owner (no
    /// self-damage by construction — the owner is never gathered). Stage 3 Task 5
    /// (spec Р252) opened the mob half: a Mob-owned round now hits every OTHER
    /// live mob too — a gunner's round can wound another mob standing in its line
    /// of fire (ADR-003 §1's diegesis: machine dementia, not rebellion — no aggro
    /// follows, the shooter's target selection is untouched) — excluding only its
    /// own shooter (mobs[m].Id == proj.OwnerEntityId, below). Player targets are
    /// gated only on Alive here — the i-frame check happens inside
    /// SimulationWorld.DamagePlayer, not here. A projectile is single-target: it
    /// is consumed on its first contact, no piercing.
    internal static class ProjectileSystem
    {
        // HitRingWall (Stage 2 Task 46) splits off the arena's outer boundary,
        // which HitBarrier used to cover together with the interior obstacles
        // and walls. The two are gathered separately because only the interior
        // ones have a modelled top (Arena.BarrierTop): rejecting a candidate
        // that stood for both would throw the ring boundary away along with the
        // obstacle the round actually cleared. Its number is appended rather
        // than inserted so the existing kinds keep their values — nothing
        // serializes these, but the packing order below reads as a table and a
        // renumbering would make every neighboring comment lie.
        const int HitNone = 0, HitBarrier = 1, HitMob = 2, HitPlayer = 3, HitFloor = 4,
            HitRingWall = 5;

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
                // hit into the scratch in canonical slot order — 0 = interior
                // barrier, 1 = ring boundary (Stage 2 Task 46), then mobs by
                // index, then players by index, then floor (Task 7) — so the
                // packed array's index order doubles as the tie-break order
                // below, matching Task 1's original streaming-min bit-for-bit.
                //
                // Stage 2 Task 46 splits Task 5's single barrier slot in two,
                // and the split is arithmetically identical to the one call it
                // replaces: SweepArena consults the interior circles and walls
                // first and only then lets the ring boundary win with a strict
                // `tw < t` (its own doc), so packing the interior sweep FIRST
                // and the ring SECOND reproduces both the same minimum and the
                // same "interior takes an exact tie" rule through the min-scan
                // below, which also breaks ties by lowest packed slot. The ring
                // is asked with the same Geometry.SegmentRingWall call and the
                // same arguments SweepArena itself would have used, so the `t`
                // is the same number, not merely the same rule.
                int candCount = 0;
                if (Geometry.SweepArena(startPos, target, proj.Radius, in arena, false,
                        out float tBarrier, out float2 barrierNormal))
                {
                    candidates[candCount++] = (tBarrier, HitBarrier, -1);
                }

                if (Geometry.SegmentRingWall(startPos, target, proj.Radius, arena.Radius,
                        out float tRing))
                {
                    candidates[candCount++] = (tRing, HitRingWall, -1);
                }

                // Stage 3 Task 5 (spec Р252): the gate that used to read
                // `if (proj.Owner == ProjectileOwner.Player)` is GONE — every
                // round, mob-owned or player-owned, now gathers mob candidates.
                // The one exclusion left is the round's OWN shooter:
                // `mobs[m].Id == proj.OwnerEntityId` skips it so a gunner never
                // wounds itself at the muzzle (MobAiSystem's own spawn point
                // sits ON its shooter's collision circle — see its own comment).
                // A Player-owned round's OwnerEntityId is always the literal 0
                // (WeaponSystem's own call), and no live mob can ever have id 0
                // (SimulationWorld._nextEntityId starts at 1), so this same
                // check is a no-op for that branch — nothing changes for a
                // player's own shot.
                int mobCount = w.MobCount;
                for (int m = 0; m < mobCount; m++)
                {
                    if (mobs[m].Id == proj.OwnerEntityId) continue;
                    float mobRadius = mobs[m].Type == MobType.Chaser ? chaserRadius : gunnerRadius;
                    if (Geometry.SegmentCircle(startPos, target, proj.Radius,
                            mobs[m].Pos, mobRadius, out float tm))
                    {
                        candidates[candCount++] = (tm, HitMob, m);
                    }
                }

                // Player targets (Stage 2 Task 17, spec §3.6): BOTH owners reach
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
                // the ground when its center height reaches Radius (the sphere's
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

                    if (AcceptCandidate(w, in config, in proj, startPos, target, bestT,
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
                    case HitRingWall:
                    {
                        float2 contact = math.lerp(startPos, target, bestT);
                        float contactHeight = proj.Height + proj.VelZ * dt * bestT;
                        // Wall/obstacle: HitDir is the real SweepArena surface
                        // normal (Task 7 — D12/C5, no "≈0" heuristic). Stage 2
                        // Task 46: the ring boundary answers the same question
                        // through Geometry.RingWallNormal, which is the very
                        // formula SweepArena's own ring branch used to inline —
                        // fed the same contact point, so the event carries the
                        // same direction it carried before the split. Both
                        // kinds share this branch because the ENDING is the
                        // same event either way: only the normal's source
                        // differs, and Presentation never had a way to tell an
                        // obstacle from the rim to begin with.
                        float2 blockedNormal = hitKind == HitRingWall
                            ? Geometry.RingWallNormal(contact)
                            : barrierNormal;
                        w.Emit(SimEventKind.ProjectileBlocked, contact, proj.Id, default,
                            contactHeight, hitDir: blockedNormal);
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
                        // secondaryEntityId (Stage 2 Task 28): the ROUND's own
                        // id. EntityId is spent on the victim here — unlike the
                        // Blocked/Expired branches above, which carry proj.Id
                        // there — so without this the round's identity is lost
                        // at the emit and the snapshot assembler cannot close
                        // the per-connection spawn subscription this hit ends
                        // (spec §3.8 ProjectileEndedNet, table Р28).
                        w.Emit(SimEventKind.ProjectileHit, contact, mob.Id, mob.Type, dmg,
                            zone: hitZone, hitDir: hitDir, playerIndex: proj.OwnerIndex,
                            secondaryEntityId: proj.Id);
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
                        // Multiplier applies BEFORE both the event and the blow,
                        // exactly as in the HitMob branch above.
                        float dmg = proj.Damage * hitMult;
                        // Stage 2 Task 44a (bd app-dsh): this branch used to
                        // remove the round without emitting anything at all,
                        // while every neighboring branch emits — so a PvP hit
                        // produced no end-of-round event, the client's ghost
                        // tracer had to time out instead of being cut, and
                        // ADR-001 §10's per-hit feedback had nothing to fire on.
                        // The kind is its own rather than the mob branch's:
                        // EntityId here is a PLAYER SLOT, and the assembler maps
                        // ProjectileHit to a hardcoded HitMob (see
                        // SimEventKind.ProjectileHitPlayer's own doc for both
                        // halves of the reasoning).
                        // playerIndex is the SHOOTER (ATTACKER convention),
                        // secondaryEntityId the ROUND — EntityId is spent on the
                        // victim here, same as in the mob branch, and without it
                        // the assembler cannot close the spawn subscription this
                        // hit ends.
                        // EMITTED UNCONDITIONALLY, next to the removal it
                        // describes: DamagePlayer below is a no-op while the
                        // victim's i-frames are up, but the round is consumed
                        // either way, and a consumed round whose end went
                        // unreported is precisely the hanging tracer this event
                        // exists to prevent.
                        w.Emit(SimEventKind.ProjectileHitPlayer, contact, hitTargetIndex, default, dmg,
                            zone: hitZone, hitDir: hitDir, playerIndex: proj.OwnerIndex,
                            secondaryEntityId: proj.Id);
                        // Stage 2 Task 17: victim = the player this scan actually
                        // resolved onto, attacker = the round's own shooter
                        // (ProjectileIds.NoOwner for a mob's round, which credits
                        // nobody — see DamagePlayer's own doc).
                        w.DamagePlayer(hitTargetIndex, proj.OwnerIndex, dmg,
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
        ///
        /// `t` (Stage 2 Task 46) is the winning candidate's own hit fraction,
        /// handed down from the min-scan rather than re-solved here: the
        /// interior-barrier gate needs the round's height AT the contact, and
        /// the scan already knows where that contact is.
        static bool AcceptCandidate(SimulationWorld w, in SimConfig config, in ProjectileState proj,
            float2 p0, float2 p1, float t, int kind, int targetIndex, out HitZone zone, out float mult)
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
            else if (kind == HitRingWall)
            {
                // The arena's outer boundary holds the edge of the world
                // (owner decision 2026-08-11, bd app-r8x): it is the one
                // barrier with no modelled top, because a round flying over it
                // would leave the arena for good — there is nothing out there
                // to reach, and nothing to come back down onto.
                return true;
            }
            else if (kind == HitBarrier)
            {
                // Interior barrier — an obstacle circle or a stadium wall
                // (Stage 2 Task 46, bd app-r8x). They share one modelled top,
                // Arena.BarrierTop; a non-positive value means there is none,
                // which is what every barrier did before this task and what
                // every hand-built fixture still gets by default.
                float barrierTop = config.Arena.BarrierTop;
                if (barrierTop <= 0f) return true;

                // THE WHOLE REMAINING STEP, NOT THE CONTACT POINT. A round
                // descends inside a tick, so judging the contact alone would
                // hand back a shot that is above the crown where it MEETS the
                // barrier and inside its body a fraction of a tick later — and
                // the next tick would start behind the barrier, through solid
                // geometry. The pair (height at the contact, height at the end
                // of the step) covers everything that is left of the step, so a
                // rejection means "clear of the crown for the whole rest of the
                // step", never "clear just at the moment of contact".
                //
                // That same span is what lets the gather phase keep ONE slot
                // for the nearest interior barrier, and Overlaps refuses in
                // BOTH directions, so both have to be monotone in `t` for that
                // to hold (fix-round 1, Ф-5: this passage used to argue only
                // the first). Over the crown: the LOWER end of this pair never
                // decreases with `t` — it is the step's end height for a
                // descending round and the contact height itself for a climbing
                // one — so a barrier further along the same step is cleared
                // whenever the nearest one is. Under the floor line: the UPPER
                // end never increases with `t`, by the mirror of the same case
                // split, so a round already below −radius at the nearest
                // barrier is still below it at every later one. Practically the
                // second branch is dead today (WeaponSystem launches from a
                // muzzle at or above 0.45 m and a round that sinks that far is
                // taken by the floor candidate first), which is why it is worth
                // writing down rather than leaving to be re-derived.
                //
                // The bias is deliberately toward "the barrier stopped it":
                // Overlaps grows the column by the round's own radius at both
                // ends, so the shot that pays for this is one that visually
                // grazed the crown and is stopped anyway — never one that
                // passes through a wall.
                float hContact = proj.Height + proj.VelZ * SimulationWorld.TickDt * t;
                float hStepEnd = proj.Height + proj.VelZ * SimulationWorld.TickDt;
                return HitZones.Overlaps(hContact, hStepEnd, proj.Radius, barrierTop);
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
