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
    /// gated only on Alive here — the i-frame question is asked in the HitPlayer
    /// arm below, which since app-88jb Т20 decides whether a blow that never
    /// landed may pierce and since Т28 decides the blow itself (that arm carries
    /// the full reasoning); SimulationWorld.DamagePlayer's own guard stands
    /// behind it for every OTHER caller and as defense-in-depth for this one.
    ///
    /// AND SINCE app-88jb Т28 EVERY ONE OF THOSE QUESTIONS MAY BE ASKED OF THE
    /// PAST (spec §3.6). While a round still owes rewound steps
    /// (ProjectileState.RewindLeft), the gather phase and AcceptCandidate ask
    /// PositionHistory where each body STOOD at tick
    /// (CurrentTick - RewindLeft) and what it was doing there, instead of
    /// reading the live struct -- the Valve form of lag compensation, in which
    /// the round itself is not moved by a single meter. RewoundBody below is
    /// the one place that asks; every consumer goes through it so that a
    /// candidate and its resolution can never disagree about which moment they
    /// are talking about.
    ///
    /// A PROJECTILE IS NO LONGER SINGLE-TARGET BY CONSTRUCTION (app-88jb Т20,
    /// spec §3.4, owner decision Н13): a round that OVERKILLS a light enough
    /// body kills it and flies on with part of its damage spent, from the next
    /// tick. Both body arms below offer that, through
    /// ProjectileFlight.TryPierce. Every contact the pierce refuses ends
    /// exactly as it always has — the blow lands and the round is retired —
    /// and at the SHIPPED numbers the pierce refuses every body in the game
    /// (2.6 against the lightest 70 kg is 0.037 under a threshold of 0.06),
    /// which is deliberate: the mechanic ships with the knob that turns it on,
    /// and the growth epic (app-vb5u) is what turns it.
    ///
    /// TWO ENTRY POINTS SINCE app-88jb Т27, NOT ONE, and the sentence this doc
    /// opens with describes only the first. Update below is the per-tick pass
    /// over every live round. CatchUp is the other: it drives a SINGLE freshly
    /// spawned round through the extra steps its shooter's one-way delay owes
    /// it, from inside the weapon phase, before that pass runs at all. Both go
    /// through one shared body (StepProjectile), so a round born to a lagging
    /// shooter meets the world under exactly the rules every other round meets
    /// it under.
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
            float heroRadius = config.Hero.Radius;
            (float t, int kind, int index)[] candidates = w.ProjCandidates;

            for (int i = w.ProjectileCount - 1; i >= 0; i--)
            {
                // app-88jb Т27: the whole of one round's tick now lives in the
                // callable step below, and it has TWO callers -- this loop,
                // which walks every live round once, and CatchUp further down
                // this file, which walks ONE freshly spawned round several
                // times from inside the weapon phase.
                //
                // NEITHER OF THEM NAMES THE TICK THE BODIES ARE ASKED ABOUT
                // ANY MORE (app-88jb Т28, coordinator RULING 207). Т27 handed that
                // number down as a parameter and both callers passed the same
                // literal -1; the step now derives it from the round it is
                // stepping, because the round is the only thing that knows how
                // much of its own picture depth is left. Both callers would
                // otherwise have written one expression twice.
                //
                // ITS ANSWER IS DROPPED HERE AND READ THERE, and that asymmetry
                // is the contract rather than dead weight. "The round still
                // occupies slot i" is what a caller needs before stepping the
                // SAME index again, and this loop never does: it runs backwards
                // precisely so a swap-remove lands on a slot already visited,
                // which makes the removal the iteration's own business. CatchUp
                // re-enters one index repeatedly and therefore has to read it.
                StepProjectile(w, i, dt, in config, heroRadius, candidates);
            }
        }

        /// ONE ROUND'S WHOLE TICK, LIFTED OUT OF THE LOOP ABOVE (app-88jb Т27,
        /// spec §3.6, which names this loop body as the place a catch-up step
        /// has to be driven from). The lift is a pure refactor: the step, the
        /// gather phase, the canonical packing order, the repeated min-scan and
        /// every resolution arm arrive here in the order they were written in,
        /// with no operator moved and no value changed. Only the two additions
        /// below are new, and both are contract rather than behavior.
        ///
        /// WHICH TICK THE BODIES ARE ASKED ABOUT IS DERIVED HERE, NOT PASSED
        /// IN (app-88jb Т28, spec §3.6, coordinator RULING 207). Т27 lifted
        /// this body out of the loop with an `int historyTick` parameter that
        /// nobody read and both callers filled with the same literal; Т28 gives
        /// it a reader and takes the parameter away in the same breath, because
        /// the number is a function of the ROUND -- `CurrentTick - RewindLeft`
        /// while the round still owes rewound steps, and the present otherwise
        /// -- and the two callers would have had to write that one expression
        /// twice. -1 keeps its old meaning as the value that says "the present,
        /// no history read at all"; it is simply computed here now.
        /// ⚠ IT IS READ BEFORE THE COUNTDOWN BELOW SPENDS A STEP, so the step
        /// asks about the tick its own remaining depth names rather than about
        /// the next one; and a delegate is still refused outright, on Т27's own
        /// grounds -- allocations are forbidden on this path
        /// (AllocationTests.Tick_DoesNotAllocateGC).
        ///
        /// RETURNS "THE ROUND STILL OCCUPIES SLOT `i`" (coordinator RULING
        /// 172), which is the one thing a caller that steps the same round more
        /// than once inside a tick cannot do without.
        /// SimulationWorld.RemoveProjectileAt is a SWAP-REMOVE: it writes the
        /// last live element over `index` and drops the count, so a freshly
        /// spawned round -- which lies LAST -- puts its own slot PAST
        /// ProjectileCount the moment it dies. A second step at that index
        /// would then work off a copy that no longer belongs to the live set:
        /// it would report a second ending for one round, and its own removal
        /// would overwrite the live neighbor now standing at the end. So
        /// `false` means "stop", and the flag is raised beside each of the five
        /// removals rather than derived from a before/after ProjectileCount --
        /// the count is a statement about today's body (which spawns nothing
        /// and removes nothing else), the flag is a statement about the
        /// contract.
        ///
        /// ⚠ `true` DOES NOT MEAN "NOTHING WAS HIT". A pierce
        /// (ProjectileFlight.TryPierce) leaves the round in its slot on purpose
        /// (Р376: it flies on from the next tick), and a caller stepping it
        /// further must keep stepping -- otherwise a round that pierced would
        /// silently forfeit the rest of its catch-up, which is different
        /// physics rather than a smaller bug.
        ///
        /// The four locals the loop above hoists travel in as parameters
        /// instead of being re-read from `w` here, and `config` is the reason
        /// the others follow: SimulationWorld.Config is a PROPERTY over a large
        /// struct, so reading it per round would add one full copy of
        /// Hero/Weapon/Chaser/Gunner/Wave/Arena/Visibility/Flow/Elite/Director
        /// to the hottest loop in the simulation. It arrives by `in` for the
        /// same reason every neighbor here already takes it that way.
        static bool StepProjectile(SimulationWorld w, int i, float dt, in SimConfig config,
            float heroRadius, (float t, int kind, int index)[] candidates)
        {
            // RULING 172: raised beside every RemoveProjectileAt below, never
            // inferred from the count.
            bool stillInSlot = true;
            ref ProjectileState proj = ref w.Projectiles[i];
            // app-88jb Т28 (spec §3.6, coordinator RULING 203): the tick this
            // step's questions are asked at. `CurrentTick - RewindLeft` is the
            // plan's own reading of the spec sentence, and it is the one that
            // walks the round back into the present a tick at a time (the lag
            // runs 3, 2, 1, 0 over a picture depth of three) instead of
            // dropping it there in one jump.
            int historyTick = proj.RewindLeft > 0 ? w.CurrentTick - proj.RewindLeft : -1;
            float2 startPos = proj.Pos;
            // app-88jb Т18: the step itself -- where this tick ends and
            // what STATIC geometry stands along the way -- comes from the
            // one public home the client's tracer cranks too
            // (ProjectileFlight's own doc). It picks no winner and refuses
            // nothing: its three candidates arrive side by side and are
            // packed into the canonical slots below, around the bodies.
            // The round travels there by `in`: everything that MUTATES it
            // stays here, on the two lines under this call and in the
            // `default:` arm at the bottom.
            ProjectileFlight.StepResult step = ProjectileFlight.Step(in proj, in config, dt);
            float2 target = step.Target;
            proj.PrevPos = startPos;
            proj.Ttl -= dt;
            // app-88jb Т28: the picture half is spent by the SAME bookkeeping
            // that ages the round, one unit per step, and for the same reason
            // -- a round is charged for the distance it covered, not for the
            // ticks that passed. So a catch-up step spends one too: those are
            // the round's FIRST steps (ProjectileSystem.CatchUp), which is
            // exactly what the depth was owed for.
            if (proj.RewindLeft > 0) proj.RewindLeft--;

            MobState[] mobs = w.Mobs;

            // Gather phase (Task 5 refactor): pack every actual geometry
            // hit into the scratch in canonical slot order — 0 = interior
            // barrier, 1 = ring boundary (Stage 2 Task 46), then mobs by
            // index, then players by index, then floor (Task 7) — so the
            // packed array's index order doubles as the tie-break order
            // below, matching Task 1's original streaming-min bit-for-bit.
            //
            // app-88jb Т18: the three STATIC slots are filled off the
            // step above instead of from solver calls written out here,
            // and not one index moved — the ORDER is this file's business
            // and nobody else's, because it is what the min-scan's
            // tie-break means. Stage 2 Task 46's split of the single
            // barrier slot in two survives the move for exactly the reason
            // it was made: the interior barrier and the ring boundary are
            // two flags on the step and never one candidate, so refusing
            // the barrier on its height cannot throw away a boundary the
            // round has NOT cleared. Why the two are arithmetically the
            // same as the one SweepArena call they replaced, and why the
            // interior sweep is asked first, is stated once, in
            // ProjectileFlight.Step beside the calls themselves.
            int candCount = 0;
            if (step.HasBarrier)
            {
                candidates[candCount++] = (step.BarrierT, HitBarrier, -1);
            }

            if (step.HasRingWall)
            {
                candidates[candCount++] = (step.RingWallT, HitRingWall, -1);
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
            //
            // app-88jb Т28 (spec §3.6): AND THE GATHER PHASE IS WHERE THE
            // REWOUND QUESTION HAS TO BE ASKED FIRST, not only at the
            // resolution below. This loop walks bodies at their CURRENT stands,
            // so a body that stood on the round's line k ticks ago and has
            // since walked off it would never become a candidate at all -- and
            // the rewind would return a miss in precisely the situation it
            // exists for. RewoundBody answers both halves at once, "was it a
            // target then" and "where was it", and for a mob the first half can
            // only come out of the row: `_mobs[0.._mobCount)` holds live bodies
            // by construction, so there is no live flag to fall back on and
            // none is wanted (PositionHistory.Write's own COLLECTORS ARE WALKED
            // WHOLE note draws the same asymmetry from the other side).
            int mobCount = w.MobCount;
            for (int m = 0; m < mobCount; m++)
            {
                if (mobs[m].Id == proj.OwnerEntityId) continue;
                float mobRadius = MobRadiusFor(mobs[m].Type, in config);
                if (RewoundBody(w, historyTick, mobs[m].HistorySlot, mobs[m].Pos,
                        liveAlive: true, liveSliding: false, liveInvulnerable: false,
                        out float2 mobPos, out _, out _, out _)
                    && Geometry.SegmentCircle(startPos, target, proj.Radius,
                        mobPos, mobRadius, out float tm))
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
            //
            // app-88jb Т28 (coordinator RULINGs 206 and 213): the `Alive` gate
            // this loop has always carried does NOT change hands on a rewound
            // step -- IT GAINS A SECOND ASKER. A collector is gathered only if
            // he is alive BOTH NOW AND AT THE TICK THE ROUND IS REWOUND TO: the
            // live half is the `continue` below, the historical half is
            // RewoundBody's own return value. PosAt answers `false` on exactly
            // one question -- "was this body alive at that moment" -- so a round
            // rewound to a tick the victim did not live through finds nobody
            // instead of hitting a ghost; on every un-rewound step, and on a
            // rewound one the ring has no row for, the live answer is the only
            // one there is, exactly as before this task.
            // ⛔ WHAT DROPPING THE LIVE HALF WOULD COST, because RULING 206's
            // first wording ("the gate changes hands") invited exactly that: a
            // victim who lived at the rewound tick and has DIED SINCE would be
            // gathered as a corpse. AcceptCandidate would pass him, the
            // HitPlayer arm would emit ProjectileHitPlayer carrying the round's
            // whole damage and RETIRE THE ROUND, and SimulationWorld.DamagePlayer
            // would then return on its own `!Alive` guard having touched
            // nothing. So a round would end on a body it cannot damage, and
            // Presentation would spend a hit spark and a hit sound
            // (PersistentPropsDirector.SpawnPlayerHitSpark, AudioDirector's
            // `_hitClip`) on a corpse.
            // ⚠ THE `Amount` ON THAT EVENT WOULD NOT BE A NEW KIND OF LIE, and
            // the ruling's second ground overstates this one thing: the kind's
            // own doc already says Amount is what the round CARRIED and is
            // "strictly more than it when the victim absorbed the hit". What
            // that clause licenses is a LIVE body that refused the blow, not one
            // that was already dead when the round arrived -- the round's ending
            // is what changes, not the payload's contract.
            // ⭐ AND THE MOB LOOP ABOVE CANNOT REACH THAT CASE AT ALL:
            // SimulationWorld.DamageMob swap-removes a dead mob out of
            // `_mobs[0.._mobCount)` in the same breath it returns its history
            // slot, so "alive then, dead now" does not exist for a mob. No
            // document asks the two body arms to behave differently, and keeping
            // the live half is what keeps them the same. It also keeps TODAY's
            // behavior exactly where Т28 promised to change nothing: a dead
            // collector was never a candidate and does not become one.
            // ⛔ THE OWNER SKIP ABOVE THIS IS UNTOUCHED (Р411): the shooter is
            // never rewound, and it is not gathered at all, so the exclusion
            // stays exactly where and what it was.
            int playerCount = w.PlayerCount;
            for (int pi = 0; pi < playerCount; pi++)
            {
                if (proj.Owner == ProjectileOwner.Player && pi == proj.OwnerIndex) continue;
                PlayerState player = w.PlayerAt(pi);
                // The LIVE half of the gate, spelled ahead of the rewound one so
                // the rule reads in a single glance: a corpse is never a
                // candidate, whatever the past says. A `continue` and not a
                // conjunction, because the loop's OTHER gather-phase exclusion
                // -- the owner skip one line up -- is already spelled this way,
                // and because `player.Alive` would otherwise appear twice inside
                // one condition: RewoundBody still receives it as its live
                // fallback, which is what its `liveAlive` parameter is
                // contracted to be (the value the caller reads off the struct it
                // is holding). Past this line the two answers agree by
                // construction, so the un-rewound path behaves exactly as it did
                // before Т28 -- and as it did before this fix.
                if (!player.Alive) continue;
                if (RewoundBody(w, historyTick, player.HistorySlot, player.Pos,
                        player.Alive, player.SlideTimer > 0f, player.IframeTimer > 0f,
                        out float2 playerPos, out _, out _, out _)
                    && Geometry.SegmentCircle(startPos, target, proj.Radius,
                        playerPos, heroRadius, out float tp))
                {
                    candidates[candCount++] = (tp, HitPlayer, pi);
                }
            }

            // Floor candidate (Task 7): a descending shot crosses the
            // ground when its center height reaches Radius (the sphere's
            // underside at z = 0). The crossing itself, its VelZ gate and
            // its [0,1] clip are solved in ProjectileFlight.Step — same
            // arithmetic, same comparisons, one home (app-88jb Т18); what
            // is decided HERE is the slot the answer lands in.
            // Packed LAST (canonical slot order, M5): a barrier/mob/player tie
            // at the same t always outranks the floor. That is also why the
            // step hands the floor back behind a flag of ITS OWN instead of
            // folded in with the two barrier candidates — those two are
            // packed before the bodies and this one after them, so no
            // single candidate could ever stand for both sides of the
            // array.
            if (step.HasFloor)
            {
                candidates[candCount++] = (step.FloorT, HitFloor, -1);
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
            // Contact height (app-88jb Т3): AcceptCandidate's own new out
            // parameter, same "never read on a HitNone/rejected verdict"
            // contract as hitZone/hitMult above.
            float hitHeight = 0f;
            // The fraction of the step the CONTACT sits at (app-88jb T14,
            // coordinator Ruling 73). For a body it is the winning PART's own
            // first contact, which is a later point than `bestT` (the entry
            // into the whole body's circle) whenever the part struck is
            // narrower than the body -- 0.33 m later on a chaser headshot.
            // ⚠ ITS ONLY READERS ARE THE TWO DAMAGEABLE ARMS BELOW (Т14/Т23
            // fix-round, Ruling 197 / review finding B-4): the barrier, ring
            // and floor arms build their contact from `bestT` directly, so
            // for those kinds this value is dead and no branch ever reads
            // AcceptCandidate's default. Same "never read on a HitNone/
            // rejected verdict" contract as its three neighbors.
            float hitContactT = 0f;
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
                        hitKind, hitTargetIndex, historyTick, out hitZone, out hitMult,
                        out hitHeight, out hitContactT))
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
                    // obstacle from the rim to begin with. app-88jb Т18:
                    // the obstacle's normal rides here on the step, the
                    // only one of its three candidates that carries one —
                    // the ring's is still derived from the contact right
                    // here, exactly as it was.
                    float2 blockedNormal = hitKind == HitRingWall
                        ? Geometry.RingWallNormal(contact)
                        : step.BarrierNormal;
                    // app-88jb Т19 (spec §3.4, owner decision Н19): the
                    // ricochet gets FIRST refusal on this contact, and only
                    // this one -- the floor below has no modelled normal
                    // and bodies are not static geometry, so neither of the
                    // other retiring arms offers it. The arithmetic lives
                    // in ProjectileFlight beside Step (Ruling 92), because
                    // the client's tracer cranks the same function; what is
                    // decided HERE is only which contact gets offered.
                    //
                    // A REFUSAL CHANGES NOTHING (owner decision Р439): the
                    // round falls through to the two lines below, which are
                    // the two lines that have always ended a blocked round,
                    // byte for byte. So every contact this task does not
                    // reflect ends exactly as it ended before it existed --
                    // which is why the barrier suite stays green on any
                    // fixture that states MaxRicochets = 0.
                    //
                    // NO EVENT ON SUCCESS. `ProjectileRicocheted` is Т30's;
                    // until then a reflection is silent on the wire, and
                    // Presentation sees only the round's own moved Pos.
                    if (ProjectileFlight.TryRicochet(ref proj, in config, blockedNormal,
                            contact, hitHeight))
                    {
                        break;
                    }

                    // Amount (app-88jb Т3, finding D-C4): 0f — a blocked
                    // round deals no damage, and Amount is spent on
                    // damage everywhere else in this struct. The contact
                    // height AcceptCandidate already computed for its own
                    // gate travels in `height`, its own field, instead of
                    // being re-derived here: the duplicate copy that used
                    // to live right here is gone, and the one remaining
                    // `contactHeight` lives inside AcceptCandidate.
                    w.Emit(SimEventKind.ProjectileBlocked, contact, proj.Id, default,
                        0f, hitDir: blockedNormal, height: hitHeight);
                    w.RemoveProjectileAt(i);
                    stillInSlot = false;
                    break;
                }
                case HitFloor:
                {
                    float2 contact = math.lerp(startPos, target, bestT);
                    // Floor: no modelled surface normal — HitDir is exactly
                    // zero, not an approximation (Task 7 — D12/C5).
                    // Amount is 0f for the same reason the barrier/ring
                    // branch above states it — see that comment.
                    w.Emit(SimEventKind.ProjectileBlocked, contact, proj.Id, default,
                        0f, hitDir: float2.zero, height: hitHeight);
                    w.RemoveProjectileAt(i);
                    stillInSlot = false;
                    break;
                }
                case HitMob:
                {
                    // app-88jb T14 (Ruling 73): the contact is the winning
                    // PART's entry, not the body circle's. `bestT` is
                    // untouched and stays what it has always been -- the
                    // min-scan's own answer to WHICH candidate won; this
                    // line asks the different question of WHERE on the step
                    // the blow landed, and only the resolver knows that.
                    float2 contact = math.lerp(startPos, target, hitContactT);
                    float2 hitDir = math.normalizesafe(proj.Vel, new float2(1f, 0f));
                    // Multiplier applies BEFORE the event: Amount is the
                    // damage actually dealt, so Presentation never has to
                    // re-derive it from a base value it cannot see.
                    float dmg = proj.Damage * hitMult;
                    MobState mob = mobs[hitTargetIndex];
                    // app-88jb Т20 (spec §3.4, owner decision Н13,
                    // coordinator Rulings 101/103/106): the PIERCE, decided
                    // BEFORE the blow AND BEFORE THE EVENT, never after
                    // either. `mob` above is
                    // the copy taken before any damage, and this is the
                    // second question it exists to answer: DamageMob below
                    // swap-removes a body that dies, so the health the rule
                    // is judged against has to be read while the body is
                    // still standing. `dmg` is likewise already computed, so
                    // the victim takes the FULL blow and only what flies on
                    // is reduced -- TryPierce cuts `proj.Damage`, the base
                    // number, not this local.
                    //
                    // The mass comes through SimulationWorld.MobConfigFor,
                    // the same seam AcceptCandidate already uses on this
                    // RESOLUTION path. The gather phase's one-float
                    // MobRadiusFor exists because it runs once per
                    // CANDIDATE in the hottest loop in the simulation; this
                    // runs once per resolved hit, where the copy is what
                    // the neighboring code already pays and a second
                    // archetype switch would be a second place to keep in
                    // sync (rule 2).
                    //
                    // On `true` the round keeps flying: TryPierce has
                    // seated it at the contact and cut its damage, and BOTH
                    // the event below AND the removal are skipped -- which
                    // is the whole of "the round flies on from the NEXT
                    // tick" (Р376), since `Pos` is advanced only by the
                    // `default:` arm. On `false` TryPierce writes NOTHING
                    // (its own contract), so every payload below is the
                    // number it was before this line existed.
                    bool piercedMob = ProjectileFlight.TryPierce(ref proj, in config,
                        w.MobConfigFor(mob.Type).Mass, dmg, mob.Hp, contact, hitHeight);
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
                    //
                    // ⚠ NOT EMITTED WHEN THE ROUND PIERCED (app-88jb Т20,
                    // coordinator Ruling 106, review finding C-1), and the
                    // form is TryRicochet's own one branch up -- that arm
                    // returns BEFORE its ProjectileBlocked for exactly this
                    // reason. THIS EVENT MEANS THE ROUND ENDED, not "a blow
                    // landed": SnapshotAssembler maps it to
                    // ProjectileEnded, whose routing unsubscribes every
                    // viewer from the round's id, after which the client
                    // retires its tracer. A pierced round that emitted here
                    // would report its own ending in mid-flight, go
                    // invisible, and address its REAL ending to a set that
                    // no longer contains anybody.
                    // What the pierced body still reports is its DEATH, and
                    // that is not a consolation but the whole payload: the
                    // pierce requires a STRICT overkill, so MobDied always
                    // follows, it carries the killing blow's own amount,
                    // and its routing unions the killing round's
                    // subscribers -- which still exist precisely because
                    // this line stayed silent. A mid-life event of the
                    // pierce's own is Т30's, beside ProjectileRicocheted
                    // (bd app-tbvg).
                    if (!piercedMob)
                    {
                        w.Emit(SimEventKind.ProjectileHit, contact, mob.Id, mob.Type, dmg,
                            zone: hitZone, hitDir: hitDir, playerIndex: proj.OwnerIndex,
                            secondaryEntityId: proj.Id, height: hitHeight);
                    }
                    // ownerIndex (Stage 2 Task 7): the projectile carries its
                    // own shooter forward into the credit routing. hitHeight
                    // (app-88jb Т3) is AcceptCandidate's own contact height —
                    // see DamageMob's own doc for why it accepts one.
                    // projectileMass/projectileSpeed3D (app-88jb Т4, spec
                    // §3.2): the impact behind this blow, which only the
                    // caller can know — DamageMob never sees a round (its
                    // own doc). The owner -> mass fork goes through
                    // Impact.ProjectileMassFor and NOWHERE else (coordinator
                    // Ruling 1, round-3 finding C-I2): a mob's round is
                    // heavier than a collector's, and that rule has exactly
                    // one home, the same way SnapshotEvents.SpeedCapFor is
                    // the one home of its own fork. The speed is the FULL 3D
                    // magnitude, not length(proj.Vel): ProjectileSpeed is
                    // itself a 3D length in this project, so the flat one
                    // would under-shove every angled shot.
                    w.DamageMob(hitTargetIndex, dmg, contact, hitZone, hitDir, proj.OwnerIndex,
                        hitHeight, Impact.ProjectileMassFor(proj.OwnerIndex, in config),
                        math.length(new float3(proj.Vel, proj.VelZ)));
                    if (!piercedMob)
                    {
                        w.RemoveProjectileAt(i);
                        stillInSlot = false;
                    }
                    break;
                }
                case HitPlayer:
                {
                    // app-88jb T14 (Ruling 73): the winning PART's entry,
                    // exactly as the HitMob branch above -- see its note.
                    float2 contact = math.lerp(startPos, target, hitContactT);
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
                    // Stage 2 Task 17: victim = the player this scan actually
                    // resolved onto, attacker = the round's own shooter
                    // (ProjectileIds.NoOwner for a mob's round, which credits
                    // nobody — see DamagePlayer's own doc). hitHeight
                    // (app-88jb Т3) is AcceptCandidate's own contact height.
                    // projectileMass/projectileSpeed3D (app-88jb Т7, spec
                    // §3.2): the impact behind this blow, which only the
                    // caller can know -- DamagePlayer never sees a round
                    // (its own doc), exactly as DamageMob does not. The
                    // owner -> mass fork goes through
                    // Impact.ProjectileMassFor and NOWHERE else
                    // (coordinator Ruling 1, round-3 finding C-I2): a mob's
                    // round is heavier than a collector's, and that rule has
                    // one home. The speed is the FULL 3D magnitude, not
                    // length(proj.Vel) -- ProjectileSpeed is itself a 3D
                    // length in this project, so the flat one would
                    // under-shove every angled shot.
                    //
                    // app-88jb Т20 (spec §3.4, owner decision Н13,
                    // coordinator Rulings 101/103/106): the COLLECTOR's half of
                    // the pierce, and the spec is what says there are two
                    // halves -- its own table computes the ratio for FIVE
                    // bodies, the collector among them, and its account of
                    // v1's number names "the collector in PvP" as one of
                    // the bodies that number wrongly pierced. A body the
                    // rule is never computed for cannot be pierced at any
                    // number.
                    //
                    // The victim is copied BEFORE the blow for the same
                    // reason the mob branch copies `mob`: both questions
                    // are about the body as it stood when the round met it.
                    //
                    // ⚠ THE I-FRAME CHECK IS HERE AND NOT INSIDE THE RULE,
                    // and that boundary is the ruling's own (101): Impact
                    // answers whether a ROUND PIERCES, this line answers
                    // whether the BLOW ARRIVES. A blow that does not arrive
                    // deals no damage, and a round allowed to "pierce" a body
                    // it never damaged would meet that same LIVE body again on
                    // the very next tick -- the gather phase gates on
                    // `Alive` alone -- halving its damage once per tick
                    // until its lifetime ran out. `&&` short-circuits, so on
                    // an absorbed blow TryPierce is not called and writes
                    // nothing.
                    //
                    // `Alive` is NOT re-checked HERE, and DamagePlayer's own
                    // guard is what stands behind that. Un-rewound, the gather
                    // phase above already refused a dead player and nothing can
                    // kill this victim in between -- each round resolves fully
                    // before the next is looked at, which is the same reasoning
                    // DamagePlayer's own doc gives for calling its matching
                    // guard defense-in-depth. ⚠ AND A REWOUND STEP DOES NOT
                    // WEAKEN IT (app-88jb Т28, coordinator RULING 213), which
                    // is written down because the first form of this task did:
                    // the gather phase asks BOTH questions there, the live
                    // `Alive` it always asked and the recorded one beside it,
                    // so a body dead at that tick and a body dead right now are
                    // both refused before a candidate is ever packed. Neither a
                    // rewound resurrection nor a round that ends on a corpse is
                    // on the table, and the live `Alive` rule stays exactly
                    // where it was (RULING 205 moved the i-frame question and
                    // nothing else).
                    PlayerState victim = w.PlayerAt(hitTargetIndex);
                    // app-88jb Т28 (spec §3.6, coordinator RULING 205): THE
                    // I-FRAME QUESTION IS ASKED IN ONE PLACE AND IT IS THIS
                    // ONE, rewound or not. AcceptCandidate does not read
                    // invulnerability and must not start to -- it answers
                    // whether the round TOUCHED the body, and this arm answers
                    // whether the blow ARRIVES, which is the boundary Ruling
                    // 101 drew -- so the rewound answer lands where the live
                    // one already stood. On an un-rewound step, and on a
                    // rewound one the ring has no row for, `invulnerableThen`
                    // IS `victim.IframeTimer > 0f`, i.e. the exact test that
                    // used to be written out here.
                    // ⚠ Hp IS STILL READ OFF THE LIVE BODY, and that is a
                    // stated boundary rather than an oversight: the row carries
                    // three bits and no health, so the pierce judges the
                    // overkill against the victim as he stands now. Naming it
                    // costs nothing and hiding it would cost the next reader an
                    // afternoon.
                    RewoundBody(w, historyTick, victim.HistorySlot, victim.Pos, victim.Alive,
                        victim.SlideTimer > 0f, victim.IframeTimer > 0f,
                        out _, out _, out bool invulnerableThen, out bool iframesFromHistory);
                    bool blowArrives = !invulnerableThen;
                    bool piercedPlayer = blowArrives
                        && ProjectileFlight.TryPierce(ref proj, in config,
                            config.Hero.Mass, dmg, victim.Hp, contact, hitHeight);
                    // EMITTED ON EVERY ENDING AND ONLY ON AN ENDING. An
                    // ABSORBED blow still ends the round, so it still
                    // reports: an absorbed blow deals no damage at all -- the
                    // call below is skipped outright since app-88jb Т28, and
                    // was a no-op inside DamagePlayer's own guard before that
                    // -- and a consumed round whose end went unreported is
                    // precisely the hanging tracer this event exists to
                    // prevent.
                    // ⚠ A PIERCED blow does NOT end the round, so it does
                    // NOT report (app-88jb Т20, coordinator Ruling 106,
                    // review finding C-1) -- the mob branch above carries
                    // the full reasoning, and TryRicochet's arm returns
                    // before its own ProjectileBlocked for the same one.
                    // The collector's damage is not lost with the silence:
                    // PlayerDamaged is emitted by DamagePlayer itself and
                    // is the event that means "a blow landed", while a
                    // pierce needs a STRICT overkill, so PlayerDied follows
                    // every one of them.
                    if (!piercedPlayer)
                    {
                        w.Emit(SimEventKind.ProjectileHitPlayer, contact, hitTargetIndex, default, dmg,
                            zone: hitZone, hitDir: hitDir, playerIndex: proj.OwnerIndex,
                            secondaryEntityId: proj.Id, height: hitHeight);
                    }
                    // app-88jb Т28: the SAME answer decides the blow, because
                    // "does it arrive" has one answer and not two.
                    //   A blow that does NOT arrive skips the call outright, and
                    // that is behaviorally identical to the absorbed blow this
                    // arm has always produced: DamagePlayer's own i-frame guard
                    // returns before it writes anything at all -- no Hp, no
                    // channel abort, no impulse, no PlayerDamaged -- so there is
                    // nothing for the skipped call to have done.
                    //   A blow that DOES arrive off a written row carries
                    // `iframesDecidedByRewind`, and that flag is the whole
                    // reason the parameter exists: a victim who was vulnerable
                    // at the rewound tick and dashed since would otherwise have
                    // the live guard cancel a blow the past had already landed.
                    // It is raised ONLY on the historical answer, so on every
                    // un-rewound shot the guard keeps deciding exactly as it
                    // does today (its own doc calls itself defense-in-depth,
                    // and Т28 does not take that away from it).
                    // ⚠ THE END-OF-ROUND EVENT ABOVE IS UNCHANGED IN BOTH
                    // CASES. An absorbed blow still ENDS the round, so it still
                    // reports -- a consumed round whose end went unreported is
                    // the hanging tracer that event exists to prevent, and a
                    // rewound absorption is an absorption.
                    if (blowArrives)
                    {
                        w.DamagePlayer(hitTargetIndex, proj.OwnerIndex, dmg,
                            contact, hitZone, hitDir, hitHeight,
                            Impact.ProjectileMassFor(proj.OwnerIndex, in config),
                            math.length(new float3(proj.Vel, proj.VelZ)),
                            iframesDecidedByRewind: iframesFromHistory);
                    }
                    if (!piercedPlayer)
                    {
                        w.RemoveProjectileAt(i);
                        stillInSlot = false;
                    }
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
                        stillInSlot = false;
                    }
                    break;
            }

            return stillInSlot;
        }

        /// DRIVES A FRESHLY SPAWNED ROUND THROUGH ITS CATCH-UP STEPS
        /// (app-88jb Т27, spec §3.6, coordinator RULING 178) — the INPUT half
        /// of the shooter's rewind depth, the ticks his input really spent on
        /// the wire, paid to the round that just left his muzzle.
        /// WeaponSystem.SpawnShot is the only caller there is, and the debt is
        /// settled ONCE, on the birth tick, never again for the rest of the
        /// flight: cranking a round by its depth every tick is the canceled
        /// design (Р381), which turned the weapon into a hitscan inside 10.5 m.
        ///
        /// `index` is the round's own slot and `steps` the extra ticks of
        /// flight it owes. The round still receives its ORDINARY step from
        /// Update above later in the same tick, so a shot born owing `steps`
        /// has moved `steps + 1` times by the end of its birth tick — and its
        /// Ttl is shorter by exactly that many, because every one of those
        /// steps ages it. That is the spec's own rule rather than a side
        /// effect: the round ages by the distance it covered, not by the ticks
        /// that passed, or a lagging shooter would be handed a longer-ranged
        /// weapon than everybody else, paid for by nobody.
        ///
        /// ⚠ THE SHOOTER IS NEVER REWOUND (Р411). Nothing here touches the
        /// muzzle, the aim ray or the shooter's own body; only the round in
        /// slot `index` moves.
        ///
        /// `steps == 0` IS THE ORDINARY CASE AND NOT AN EDGE: every shooter
        /// whose one-way delay fits inside the arena's picture depth — most of
        /// them, and all of them on a local match — spends the whole of his
        /// depth on the question of where the bodies stood and none of it on
        /// the round. The loop then simply does not run, and no branch guards
        /// it, because a guard would state nothing the loop does not already
        /// state.
        ///
        /// IT STOPS ON THE FIRST STEP THAT TAKES THE ROUND OFF THE BOARD
        /// (RULING 172), which is why StepProjectile answers at all — see its
        /// own return contract for what a swap-remove does to the index of a
        /// LAST-slot round, and a freshly spawned round is exactly that. A
        /// pierce does NOT stop this loop: it leaves the round in its slot on
        /// purpose, and the rest of the catch-up is still owed to it.
        ///
        /// ⚠ WHICH MOMENT A CATCH-UP STEP LOOKS AT, SAID PLAINLY BECAUSE IT
        /// CHANGED WITH app-88jb Т28. These are the round's FIRST steps, so
        /// they are the first ones its picture depth is owed for: a round born
        /// with RewindLeft above zero asks each of them about the tick that
        /// depth names, and spends one unit of it per step, exactly as the
        /// ordinary steps in Update do. A round whose whole depth went to the
        /// input half -- the only kind that reaches this loop at all with
        /// nothing left over -- asks about the present here.
        ///   AND WHERE "THE PRESENT" IS MEASURED FROM STILL MATTERS, because
        /// the ring does not answer every question. This runs inside the WEAPON
        /// phase, and SimulationWorld.TickAll runs MobAiSystem,
        /// SeparationSystem and ProjectileSystem AFTER that phase rather than
        /// before it. So a step answered off the LIVE bodies -- an un-rewound
        /// one, or a rewound one the ring holds no row for -- sees collectors
        /// where they stand at the end of THIS tick's movement and mobs where
        /// they stood at the end of the PREVIOUS tick, and sees that same
        /// frozen mixture on all of its steps. That mixture is the freshest
        /// picture this tick has to offer and is not "the past"; the past is
        /// what PositionHistory answers with, and only when it has the row.
        ///
        /// ⚠ AND A CATCH-UP STEP CAN KILL FROM INSIDE THE WEAPON PHASE, which
        /// nothing could do before this task: until Т27 every kill happened in
        /// Update above, AFTER the phase, so the order collectors are walked in
        /// decided nothing. The phase loop tests `_players[i].Alive` on each
        /// iteration and SimulationWorld.KillPlayer clears that flag at once,
        /// so a round cranked for collector 0 can kill collector 1 before
        /// collector 1's own weapon phase is reached -- and collector 1 then
        /// does not fire this tick at all. The reverse is impossible: collector
        /// 0 has already fired by the time collector 1 shoots. DamageMob
        /// swap-removes a dying mob just as immediately, so a later collector
        /// can likewise lose the mob he was about to shoot.
        ///   THE REACH IS NARROW AND IS STATED RATHER THAN GLOSSED. It needs an
        /// input half above zero, i.e. a one-way delay deeper than the arena's
        /// picture depth -- past 100 ms one way at the shipped
        /// RewindPictureTicks of 3, which is more than twice the 80 ms round
        /// trip CRITICAL RULE 7's playtest gate runs at -- and it needs a
        /// victim already wounded enough to die inside the meters that half can
        /// cover: three steps of ProjectileSpeed * TickDt, which is 5.25 m at
        /// the balance asset's own ProjectileSpeed of 52.5 (WeaponConfig.cs's
        /// C# default is the older 35, so the source matters here). At the
        /// gate's own latency it does not happen at all.
        ///
        /// THE FOUR VALUES Update HOISTS ABOVE ITS OWN LOOP ARE GATHERED AGAIN
        /// HERE instead of being threaded in, and the difference is deliberate
        /// rather than an oversight of the lift. Three of them really are
        /// re-read from `w` -- the SimConfig copy, the hero radius taken off it
        /// and the candidate scratch; the fourth, `dt`, comes from no world at
        /// all, because SimulationWorld.TickDt is a static constant and Update
        /// reads it from that same place.
        ///   Update pays for its SimConfig copy once per TICK and
        /// then steps every live round with it, so the copy is the thing worth
        /// hoisting there; this runs once per SHOT, a rate at which that
        /// argument does not apply. Threading the four in as parameters would
        /// buy nothing measurable and would hand a caller in another file the
        /// projectile system's own scratch buffer.
        internal static void CatchUp(SimulationWorld w, int index, int steps)
        {
            float dt = SimulationWorld.TickDt;
            SimConfig config = w.Config;
            float heroRadius = config.Hero.Radius;
            (float t, int kind, int index)[] candidates = w.ProjCandidates;

            for (int s = 0; s < steps; s++)
            {
                // app-88jb Т28: no tick is named here any more (RULING 207).
                // A catch-up step is an ordinary step of the round in every
                // respect, THIS ONE INCLUDED -- it asks the bodies about the
                // tick the round's own remaining depth names, and the step
                // itself works that number out. See this method's own note on
                // which moment these steps look at.
                if (!StepProjectile(w, index, dt, in config, heroRadius, candidates))
                {
                    break;
                }
            }
        }

        /// Stage 3 Task 10 (coordinator finding, Pack B): the body radius the
        /// GATHER phase's candidate scan uses — its own home, deliberately
        /// SEPARATE from SimulationWorld.MobConfigFor (AcceptCandidate below,
        /// MobAiSystem, WaveSystem, SeparationSystem, VisibilitySystem all go
        /// through that one instead). The split is a Stage 2 decision, not an
        /// oversight: a per-mob MobConfigFor(...) call here would copy the
        /// whole MobSimConfig struct (~30 floats) once per candidate in the
        /// hottest loop in the simulation, where this needs exactly one of
        /// them. `in SimConfig cfg` avoids copying SimConfig itself (a larger
        /// struct still — Hero/Weapon/Chaser/Gunner/Wave/Arena/Visibility/
        /// Flow/Elite/Director); the field reads below (`cfg.Chaser.Radius`
        /// etc.) touch only the one float each returns, never materializing a
        /// MobSimConfig copy — so this extraction changes nothing about that
        /// Stage 2 tradeoff, only NAMES the switch that used to live inline
        /// as four precomputed locals, so ProjectileGatherAndMobConfigForTests.
        /// MobRadiusFor_AgreesWith_MobConfigFor_ForEveryArchetype can call it
        /// directly and prove the two homes stay in sync.
        ///
        /// `default` THROWS, unlike SnapshotBlocks.MaxHpFor's own `_` arm:
        /// that one is gated upstream by the wire's own MaxMobTypeValue check
        /// (TryReadMobsBlock refuses an out-of-domain byte before MaxHpFor is
        /// ever called), an independent gate that does not depend on this
        /// switch's own case list staying current. This one has no such
        /// second gate — SimulationWorld.SpawnMob's own MobConfigFor(type)
        /// call keeps every LIVE mob's Type inside today's four archetypes,
        /// but that guarantee is only as good as THIS switch being kept in
        /// sync with MobConfigFor's, which is exactly the coordination the
        /// agreement test above exists to enforce. A future archetype added
        /// to MobConfigFor and forgotten here must fail loudly (a crash that
        /// names the archetype) rather than silently render it Gunner-sized
        /// for the rest of the match — the same lesson the `0x20` sentinel
        /// literal (R-47) already cost this task once.
        internal static float MobRadiusFor(MobType type, in SimConfig cfg) => type switch
        {
            MobType.Chaser => cfg.Chaser.Radius,
            MobType.Gunner => cfg.Gunner.Radius,
            MobType.Elite => cfg.Elite.Radius,
            MobType.Director => cfg.Director.Radius,
            _ => throw new System.ArgumentOutOfRangeException(nameof(type), type,
                "unknown archetype"),
        };

        /// ⭐⭐ THE ONE HOME OF "WHERE THAT BODY WAS AND WHAT IT WAS DOING"
        /// (app-88jb Т28, spec §3.6). Three callers ask it -- the two gather
        /// loops in StepProjectile and AcceptCandidate below, plus the HitPlayer
        /// arm for the i-frame half -- and they are obliged to get the SAME
        /// answer: a candidate gathered against a past stand and then resolved
        /// against the live one would be a round that connects with a body
        /// standing somewhere else entirely, which is a worse outcome than
        /// either reading alone.
        ///
        /// IT ANSWERS THE WHOLE QUESTION AT ONCE -- "is this a target at all"
        /// as the return value, and where it stood and its two profile bits as
        /// out parameters -- because the four are one reading of one moment.
        /// The un-rewound path is the FIRST branch rather than a separate code
        /// path at the call sites: `historyTick < 0` names the present, the
        /// live values are handed straight back, and every caller therefore
        /// keeps exactly the behavior it had before this task without a second
        /// spelling of it anywhere.
        ///
        /// ⛔⛔ ON A DEGENERATE ANSWER NOT ONE FLAG COMES OUT OF THE RECORD,
        /// AND THAT INCLUDES `Alive`. PositionHistory.PosAt hands back
        /// `new Record(currentPos, FlagAlive)` when the ring holds no row for
        /// the tick -- FlagAlive raised UNCONDITIONALLY, whatever the live body
        /// is doing -- and returns `true` with it. So the record of that branch
        /// states three things and knows none of them: reading `Alive` off it
        /// would resurrect a dead collector, and reading the other two would
        /// invent a profile. `fromHistory` is exactly the mark PosAt grew for
        /// this (coordinator RULING 204), and this method is the only place in
        /// the combat path that reads it: below `false` the live triple is what
        /// the caller already passed in, which is the un-rewound behavior its
        /// own doc demands ("a caller that has fallen into this branch must
        /// read SlideTimer/IframeTimer off the live body").
        ///
        /// `liveAlive`/`liveSliding`/`liveInvulnerable` are what the CALLER
        /// reads off the live struct it is holding, and a mob passes
        /// `true, false, false` rather than being special-cased here: mobs are
        /// live by construction inside `_mobs[0.._mobCount)`, and MobState
        /// carries neither SlideTimer nor IframeTimer, so there is no field to
        /// read and a branch on the body kind would only be a longer way of
        /// writing those three literals. That is the same argument
        /// PositionHistory.Write gives one level down for why a mob's row
        /// carries FlagAlive and nothing else.
        ///
        /// ⚠ `fromHistory` TRAVELS OUT FOR EXACTLY ONE READER, the i-frame
        /// decision in the HitPlayer arm, which has to tell "the rewind said
        /// the blow arrives" from "the live body did" -- only the first may
        /// overrule SimulationWorld.DamagePlayer's own guard. Every other
        /// caller discards it, and the `out _` at those sites says so.
        ///
        /// NO ALLOCATION AND NO DELEGATE: it is a static method over value
        /// parameters, called once per body per step in the hottest loop in the
        /// simulation, and the first branch retires the whole of the
        /// un-rewound case before the ring is ever touched
        /// (AllocationTests.Tick_DoesNotAllocateGC).
        static bool RewoundBody(SimulationWorld w, int historyTick, int slot, float2 livePos,
            bool liveAlive, bool liveSliding, bool liveInvulnerable,
            out float2 pos, out bool sliding, out bool invulnerable, out bool fromHistory)
        {
            pos = livePos;
            sliding = liveSliding;
            invulnerable = liveInvulnerable;
            fromHistory = false;
            if (historyTick < 0) return liveAlive;

            bool aliveThen = w.History.PosAt(slot, historyTick, livePos,
                out PositionHistory.Record record, out fromHistory);
            if (!fromHistory) return liveAlive;

            pos = record.Pos;
            sliding = (record.Flags & PositionHistory.FlagSliding) != 0;
            invulnerable = (record.Flags & PositionHistory.FlagInvulnerable) != 0;
            return aliveThen;
        }

        /// Height gate + zone resolution for the candidate the min-scan just
        /// picked (Task 6). Returns false when the shot passes clear over (or
        /// under) the target's body, which sends the scan back for the next
        /// candidate; on true, `zone`/`mult`/`hitHeight` describe the blow.
        ///
        /// The projectile's height is not a point: it moves by VelZ·dt across the
        /// step, so the test uses the height at BOTH ends of the chord through
        /// the target rather than the gather phase's entry-only SegmentCircle.
        /// The zone itself is read at the part's FIRST CONTACT -- the earliest
        /// point where the round is inside the part's circle and its band at
        /// once (Ruling 191): that is where the round first touches the body.
        ///
        /// app-88jb T14: for the two DAMAGEABLE kinds that whole judgement now
        /// lives in HitZones.Resolve, over the body's ORDERED STACK OF PARTS,
        /// and this method only assembles its inputs -- which body, its parts,
        /// the height span of the step and the overlap ceiling. The barrier,
        /// ring-wall and floor branches below are untouched: they have no parts
        /// and never had a zone.
        ///
        /// `targetIndex` (Stage 2 Task 17: was `mobIndex`) is the winning
        /// candidate's own index — a mob index under HitMob, a player index under
        /// HitPlayer, unused for the index-less barrier/floor kinds.
        ///
        /// `t` (Stage 2 Task 46) is the winning candidate's own hit fraction,
        /// handed down from the min-scan rather than re-solved here: the
        /// interior-barrier gate needs the round's height AT the contact, and
        /// the scan already knows where that contact is.
        ///
        /// `historyTick` (app-88jb Т28, spec §3.6) is the tick the two BODY
        /// branches are answered at, -1 for the present. It is the same number
        /// the gather phase above was answered with, and it travels down rather
        /// than being recomputed for the reason RewoundBody's own doc gives:
        /// gathering a candidate at one moment and resolving it at another
        /// would seat the contact on a body that is not there. The barrier,
        /// ring-wall and floor branches ignore it -- static geometry does not
        /// move, so it has no past to be asked about.
        ///
        /// `hitHeight` (app-88jb Т3, finding D-C4) is the contact height this
        /// candidate lands at — every branch below fills it with the height
        /// arithmetic it already had to compute for its own gate (or, for
        /// the ring wall and an un-topped barrier, the same formula a gated
        /// branch would have used); for the two damageable kinds it is the
        /// winning PART's own entry height, which HitZones.Resolve hands back.
        /// Initialized up front so an early `return false` (the body branch's
        /// own gate further down) still leaves it definitely assigned, as C#
        /// requires — a rejected candidate's height is never read by the
        /// caller, only its true/false verdict.
        static bool AcceptCandidate(SimulationWorld w, in SimConfig config, in ProjectileState proj,
            float2 p0, float2 p1, float t, int kind, int targetIndex, int historyTick,
            out HitZone zone, out float mult, out float hitHeight, out float contactT)
        {
            zone = HitZone.None;
            mult = 1f;
            hitHeight = 0f;
            // A DEAD WRITE KEPT FOR DEFINITE ASSIGNMENT, and it says so
            // (Т14/Т23 fix-round, Ruling 197 / review finding B-4): C# demands
            // every `out` be assigned on every path, and the non-body branches
            // below return without touching this one. NOBODY READS THE
            // DEFAULT -- the caller's barrier/ring/floor arms build their
            // contact from the min-scan's own `bestT` directly, and the two
            // damageable kinds overwrite this with the winning part's own
            // first contact (Ruling 73). The min-scan fraction is still the
            // least-wrong value to leave behind, which is why it is `t` and
            // not zero.
            contactT = t;

            // The round's height AT the contact the min-scan handed down (app-88jb
            // Т3). One home, not four: every non-body branch below answers the
            // contact height with exactly this expression, and the gated barrier
            // branch needs it as its own gate input as well. Bodies do NOT use it
            // -- their contact is the ENTRY into the winning PART's own circle
            // (HitZones.Resolve, far below), which is a different point on the
            // same step.
            float contactHeight = proj.Height + proj.VelZ * SimulationWorld.TickDt * t;

            float2 targetPos;
            float overlapTop;
            // app-88jb T14: the body arrives as its ORDERED STACK OF PARTS,
            // and since T15 that array is the only hit volume there is --
            // the three zone tops and three multipliers it replaced are gone
            // from SimConfig entirely. HitZones.Resolve reads every number it needs
            // (each part's radius, its height band, its zone and its own
            // multiplier) off this one array. The array is a DIRECT ALIAS of
            // the config's, never copied: Resolve only reads it.
            HitPart[] parts;
            if (kind == HitMob)
            {
                MobState mob = w.Mobs[targetIndex];
                MobSimConfig cfg = w.MobConfigFor(mob.Type);
                // app-88jb Т28: WHERE THE BODY STOOD, through the one home the
                // gather phase above asked as well -- so this resolution seats
                // the contact on the same stand the candidate was gathered at.
                // A mob has no slide and no dash, so the two profile bits are
                // discarded here as they are at the gather.
                RewoundBody(w, historyTick, mob.HistorySlot, mob.Pos,
                    liveAlive: true, liveSliding: false, liveInvulnerable: false,
                    out targetPos, out _, out _, out _);
                parts = cfg.Parts;
                // THE CROWN OF THE MODEL, NOT OF THE COLUMN (app-88jb T14 Step
                // 4, coordinator Ruling 68). This read was the column's head
                // top, i.e. 1.85 m for a chaser whose model is 2.70 m tall -- measured in
                // session 43, the bodies stand 1.46/1.20/1.37 times higher than
                // the column that gated them, so the top third of every mob was
                // not shootable at all. Repointing it at the last part's Top is
                // the single move that closes the flying half of playtest debt
                // app-hoe6: a round aimed into the chaser's head belt
                // [2.12, 2.70] used to pass clean over the body.
                overlapTop = HitZones.StackTop(parts);
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
                // app-88jb Т28 (spec §3.6, finding C-I5): the stand AND the
                // slide bit come out of the SAME answer, which is the whole
                // reason RewoundBody hands back both at once -- a contact
                // seated at a past position but gated by a present profile
                // would be two different moments in one verdict. On an
                // un-rewound step, and on a rewound one with no row, `sliding`
                // IS `target.SlideTimer > 0f`, the test this branch has always
                // written out here.
                RewoundBody(w, historyTick, target.HistorySlot, target.Pos, target.Alive,
                    target.SlideTimer > 0f, target.IframeTimer > 0f,
                    out targetPos, out bool sliding, out _, out _);
                parts = cfg.Parts;
                // Task 11: mid-slide, the hero presents a lower profile — the
                // OVERLAP gate caps at SlideProfileTop instead of the standing
                // crown, so a shot on a high horizontal line (e.g. a
                // Gunner's muzzle height) passes clean over a sliding target.
                // Which PART a shot that DOES connect resolves onto is a
                // separate decision and stays untouched by the profile: the
                // parts are the standing ones either way, so a low round reads
                // as Legs (or as low Body, since SlideProfileTop sits below the
                // body part's top).
                // The standing arm follows the mob branch onto the parts
                // (T14 Step 4); the SLIDING arm does not move at all, and the
                // reason it does not have to is a RULE rather than a
                // coincidence: T13's validation rule 5 requires
                // Hero.SlideProfileTop to "coincide with a part boundary"
                // (SimConfigBuilder's own refusal wording -- cited by its
                // words, not by a line number that has already drifted once,
                // Ruling 196), so the profile the slide presents
                // is expressible in the new model exactly as it was in the old
                // one. For the collector this repointing moved no number at
                // all -- his parts end at 1.75, which is where his column ended
                // too -- and that is measured, not assumed: only his RADII
                // differed from the column (0.32 / 0.45 / 0.16 against one body
                // radius of 0.45).
                overlapTop = sliding
                    ? cfg.SlideProfileTop
                    : HitZones.StackTop(parts);
            }
            else if (kind == HitRingWall)
            {
                // The arena's outer boundary holds the edge of the world
                // (owner decision 2026-08-11, bd app-r8x): it is the one
                // barrier with no modelled top, because a round flying over it
                // would leave the arena for good — there is nothing out there
                // to reach, and nothing to come back down onto.
                // Height (app-88jb Т3): same contact-height formula every
                // other blocked branch below uses — there is no height gate
                // here to reuse an intermediate from, so it is computed fresh.
                hitHeight = contactHeight;
                return true;
            }
            else if (kind == HitBarrier)
            {
                // Interior barrier — an obstacle circle, a stadium wall or,
                // since Т9, a zone-wall ARC (Ф2 fix-round: this line named only
                // the first two, and the omission is why spec §3.2's "одно
                // правило на все внутренние барьеры" held transitively but was
                // executed by no test until BarrierHeightTests grew its two arc
                // cases). All three arrive through the one SweepArena call
                // ProjectileFlight.Step makes as the one HitBarrier candidate
                // (app-88jb Т18 moved that call out of the gather phase; the
                // candidate it answers with did not change), so there is no
                // per-shape height branch here and none is wanted
                // (Stage 2 Task 46, bd app-r8x). They share one modelled top,
                // Arena.BarrierTop; a non-positive value means there is none,
                // which is what every barrier did before this task and what
                // every hand-built fixture still gets by default.
                float barrierTop = config.Arena.BarrierTop;
                if (barrierTop <= 0f)
                {
                    // Height (app-88jb Т3): no gate ran on this path, so
                    // nothing below computed a contact height yet — same
                    // formula the gated path just past this `if` uses.
                    hitHeight = contactHeight;
                    return true;
                }

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
                float hStepEnd = proj.Height + proj.VelZ * SimulationWorld.TickDt;
                // Height (app-88jb Т3): the contact height, regardless of
                // which way Overlaps below resolves — a rejected candidate's
                // height is never read by the caller, only the false verdict.
                hitHeight = contactHeight;
                return HitZones.Overlaps(contactHeight, hStepEnd, proj.Radius, barrierTop);
            }
            else // HitFloor
            {
                // Floor (Task 7): ProjectileFlight.Step already solved the
                // exact within-tick height crossing — it was the gather phase
                // itself until app-88jb Т18 — so every gathered floor
                // candidate is a genuine contact — nothing further to test.
                // Not a damageable body: no zone (zone/mult already default to
                // None/1 at the top of this function).
                // Height (app-88jb Т3): same contact-height formula as every
                // other branch above — for a genuine floor candidate this
                // equals proj.Radius by construction (t_floor's own defining
                // equation, ProjectileFlight.Step: the sphere's underside
                // touching the ground plane).
                hitHeight = contactHeight;
                return true;
            }

            float hStart = proj.Height;
            float hEnd = hStart + proj.VelZ * SimulationWorld.TickDt;
            // app-88jb T14: ONE call answers all three questions -- does the
            // round connect with any part at all, which one, and where. The
            // pair it replaced (HitZones.Overlaps against the column, then a
            // zone/multiplier lookup at the entry height into the BODY circle;
            // both halves of that lookup were deleted in T15) could not: the
            // column had one radius for the whole body, so a shoulder-wide head
            // was the only shape it could express, and the contact it measured
            // belonged to the body circle rather than to the part that was
            // actually struck (findings B-I6/D-I2).
            //
            // The sweep interval this used to solve here moves INSIDE Resolve,
            // where it is solved once PER PART at that part's own radius --
            // the whole point of the change. The height span of the step,
            // [hStart, hEnd], still comes from here, because only this method
            // knows the round.
            //
            // `false` still means "rejected, rescan": the caller's min-scan
            // drops this candidate and looks at the next one, so a target
            // behind a screening body stays reachable (M5).
            //
            // AND THE WINNING PART'S `t` TRAVELS OUT (coordinator Ruling 73,
            // which overrules Ruling 67). Its reader is the TWO-DIMENSIONAL
            // contact of a body hit: Update used to build it from the
            // min-scan's `bestT`, the entry into the BODY circle, and leaving
            // XY there while the HEIGHT moved to the part would have left the
            // event carrying a point that disagrees with itself by (body radius
            // - part radius) -- 0.33 m on a chaser headshot. `contactT` is in
            // the caller's own [p0, p1] parameterization, the same one `bestT`
            // is in, so those two branches lerp it with no conversion.
            return HitZones.Resolve(parts, p0, p1, proj.Radius, targetPos, hStart, hEnd,
                overlapTop, out zone, out mult, out hitHeight, out contactT);
        }
    }
}
