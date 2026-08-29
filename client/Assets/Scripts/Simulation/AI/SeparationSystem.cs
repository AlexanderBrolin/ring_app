using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.AI
{
    /// Push-apart between bodies. TWO mechanisms live here, and they are
    /// neighbours rather than one thing:
    ///
    ///   * SOFT (spec Task 20, Phase 6) — a FORCE into Vel between overlapping
    ///     mobs, which keeps a converging wave from bunching onto one point
    ///     before it ever arrives. Every overlapping pair contributes an
    ///     equal-and-opposite impulse into a preallocated buffer first, and only
    ///     once every pair has been scanned are those impulses applied.
    ///   * HARD (app-88jb Т22, spec §3.5, owner decisions Н15/Н20/Н21/Р442) —
    ///     POSITIONAL separation plus a momentum shove, over three pair kinds:
    ///     mob↔mob, collector↔mob and collector↔collector. A force alone was
    ///     never enough: it takes ticks a dense wave does not give it, and a
    ///     collector walked through every body in the arena.
    ///
    /// ⚠ THE SOFT PASS STAYS. It is not superseded by the hard one — it spreads
    /// the formation out in ADVANCE, so the hard pass only ever has to fix the
    /// remainder, which is what keeps the relaxation converging in four passes
    /// instead of fighting a pile.
    ///
    /// ⚠ THIS SYSTEM NOW TOUCHES Pos, and the file's previous contract said in
    /// so many words that it never would ("This never touches Pos as a second
    /// movement path — MoveWithCollisions stays the only way a mob's position
    /// advances"). Т22 renegotiates that deliberately rather than quietly: a
    /// force cannot un-overlap two bodies within the tick they overlapped,
    /// because its effect only appears on the NEXT tick's MoveWithCollisions,
    /// and "bodies do not interpenetrate" is a statement about THIS tick's
    /// positions. The soft pass keeps the old contract; the hard pass is the
    /// exception, bounded by the double buffer (order-independent) and by
    /// Hero.MaxDepenetrationPerTick (a collector is never teleported). The
    /// canonical tick-order comment in SimulationWorld.Tick carries the same
    /// correction, because it stated the old promise too.
    ///
    /// ⚠⚠ THE COLLECTOR'S PASS RUNS ONCE, AFTER the mob relaxation, and that
    /// ordering is a PREDICTION requirement rather than a performance one. The
    /// client reproduces the collector's half from a span of body positions it
    /// cannot update (it has no mobs to simulate — CRITICAL RULE 3). If the
    /// collector were resolved INSIDE the relaxation loop, the server would see
    /// bodies that had already moved on iterations 2..N and the client would
    /// not, and the two would part company on the very first contact. Resolving
    /// the crowd first and the collector once against the settled result is the
    /// shape both sides can execute identically.
    ///
    /// ⚠ ORDER WITHIN THE TICK (finding D-C3): movement → BODIES → ARENA →
    /// one more body pass. Being pushed out of a body can drive a collector into
    /// a wall, so the arena has to be resolved AFTER the bodies; and resolving
    /// the arena can push it back into a body, which is what the final pass is
    /// for. That final pass carries NO shove — the tick's impulse has already
    /// landed, and a second helping would make the momentum a function of how
    /// many resolve steps the tick happened to need.
    internal static class SeparationSystem
    {
        /// One tick's worth of reciprocal, for the interpenetration cap on the
        /// shove (ruling 117). A constant rather than a parameter: this system
        /// already owns the tick, exactly as PlayerMovementSystem does.
        const float InvDt = 1f / SimulationWorld.TickDt;

        internal static void Apply(SimulationWorld w, PlayerState[] players)
        {
            float2[] moved = w.SepPlayerMoved;
            for (int p = 0; p < players.Length; p++) moved[p] = float2.zero;

            SoftSeparateMobs(w);
            RelaxMobPairs(w);

            // ⛔ ONE SNAPSHOT OF EVERY BODY, TAKEN ONCE, USED BY BOTH PASSES —
            // and this is a PARITY requirement, not a saving (session 72).
            // A tick runs the collector pass twice, before and after the arena.
            // If the second pass re-read the world it would see bodies the FIRST
            // pass had already shoved, while the client — whose span is a value
            // it cannot update — would see the original positions and compute a
            // larger second displacement. That mismatch is deterministic and
            // fixture-independent: it cost exactly 0.198 m in
            // PredictionAndServerAgree_WhenTheBodyIsVisible and survived five
            // different attempts to fix it from the test side, because it was
            // never a fixture problem. Freezing the input to the pair scan for
            // the whole tick makes the client's shape — one span, read twice —
            // the CORRECT one on both sides.
            int bodyCount = SnapshotBodies(w, players);

            CollectorPass(w, players, moved, bodyCount, withShove: true);
            ResolveArena(w, players, moved);
            CollectorPass(w, players, moved, bodyCount, withShove: false);
        }

        /// The original Task 20 pass, unchanged: a force between overlapping
        /// mobs, double-buffered, applied after the full scan so the mob
        /// earliest in SimulationWorld.Mobs never gets a different outcome than
        /// one discovered later.
        static void SoftSeparateMobs(SimulationWorld w)
        {
            MobState[] mobs = w.Mobs;
            int count = w.MobCount;
            float2[] forces = w.SepForces;
            ArenaSimConfig arena = w.Config.Arena;

            for (int i = 0; i < count; i++) forces[i] = float2.zero;

            for (int i = 0; i < count; i++)
            {
                ref readonly MobSimConfig cfgI = ref w.MobConfigRefFor(mobs[i].Type);
                for (int j = i + 1; j < count; j++)
                {
                    ref readonly MobSimConfig cfgJ = ref w.MobConfigRefFor(mobs[j].Type);
                    float threshold = cfgI.SeparationRadius + cfgJ.SeparationRadius;
                    if (threshold <= 0f) continue;

                    float2 d = mobs[i].Pos - mobs[j].Pos;
                    float dist = math.length(d);
                    if (dist >= threshold) continue;

                    float strength = (cfgI.SeparationStrength + cfgJ.SeparationStrength) * 0.5f;
                    float2 dir = math.normalizesafe(d, new float2(1f, 0f));
                    float2 force = dir * (1f - dist / threshold) * strength;

                    // Equal and opposite: the pair's midpoint never drifts from
                    // separation alone (spec Interfaces — "no first-in-list skew").
                    forces[i] += force;
                    forces[j] -= force;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (forces[i].x == 0f && forces[i].y == 0f) continue;
                mobs[i].Vel += forces[i];
                float radius = w.MobConfigRefFor(mobs[i].Type).Radius;
                Geometry.Depenetrate(ref mobs[i].Pos, ref mobs[i].Vel, radius, in arena, 1);
            }
        }

        /// Hard mob↔mob separation and shove, RelaxIterations passes (Р413).
        /// ONE Jacobi pass does not separate a chain of three: the middle body is
        /// pushed both ways in the same scan and the two contributions very
        /// nearly cancel.
        ///
        /// Both velocities are available here, so the law runs ONCE PER
        /// DIRECTION — each body's own motion shoves the other. Summed, that IS
        /// the full closing speed, and with PushRecoilFraction 1.0 on every
        /// archetype the pair conserves momentum exactly.
        static void RelaxMobPairs(SimulationWorld w)
        {
            MobState[] mobs = w.Mobs;
            int count = w.MobCount;
            if (count < 2) return;

            float2[] disp = w.SepDisplace;
            float2[] push = w.SepPush;
            var cand = w.PairCandidates;
            int iters = w.Config.Arena.RelaxIterations;

            for (int iter = 0; iter < iters; iter++)
            {
                for (int i = 0; i < count; i++) { disp[i] = float2.zero; push[i] = float2.zero; }

                // ⛔ ONE BROAD SCAN PER TICK, THEN THE CANDIDATE LIST (Н-43).
                // The first iteration walks every pair and REMEMBERS the ones
                // that touched; iterations two and up walk only those. At
                // Arena.MaxMobs 1350 a full scan is 911k pairs — four of them a
                // tick is 3.6M, which is not a cost a 30 Hz server can carry and
                // which blew AllocationTests' 180 s ceiling outright.
                //
                // A pair that comes into contact only BECAUSE of iteration one's
                // displacement is not picked up until the next tick. That is a
                // deliberate approximation and a small one: the displacements a
                // relaxation applies are fractions of an overlap, so a pair it
                // pushes into contact was already within a body-width, and the
                // next tick's broad scan takes it. The chain of three that Р413
                // exists for overlaps from the FIRST scan, so its witness is
                // untouched.
                bool broad = iter == 0;
                int scanned = broad ? 0 : w.PairCandidateCount;
                bool touched = false;
                if (broad) w.PairCandidateCount = 0;

                int outer = broad ? count : scanned;
                for (int idx = 0; idx < outer; idx++)
                {
                    int i, jStart;
                    if (broad) { i = idx; jStart = idx + 1; }
                    else { i = cand[idx].a; jStart = cand[idx].b; }

                    ref readonly MobSimConfig ci = ref w.MobConfigRefFor(mobs[i].Type);
                    int jEnd = broad ? count : jStart + 1;
                    for (int j = jStart; j < jEnd; j++)
                    {
                        ref readonly MobSimConfig cj = ref w.MobConfigRefFor(mobs[j].Type);
                        if (!Geometry.ResolveBodyPair(mobs[i].Pos, ci.Radius, ci.Mass, mobs[i].Id,
                                mobs[j].Pos, cj.Radius, cj.Mass, mobs[j].Id,
                                out float2 di, out float2 dj, out float2 n, out float overlap))
                            continue;
                        if (broad && w.PairCandidateCount < cand.Length)
                            cand[w.PairCandidateCount++] = (i, j);
                        touched = true;
                        disp[i] += di;
                        disp[j] += dj;
                        // `n` points from j to i, so i closes on j along -n and
                        // j closes on i along +n. Both directions are capped by
                        // the interpenetration this tick, for the reason
                        // BodySeparation.Accumulate's own note gives (ruling 117).
                        float cap = overlap * InvDt;
                        Shove(ci.Mass, cj.Mass, math.min(-math.dot(mobs[i].Vel, n), cap),
                            ci.PushRecoilFraction, n, ref push[i], ref push[j]);
                        Shove(cj.Mass, ci.Mass, math.min(math.dot(mobs[j].Vel, n), cap),
                            cj.PushRecoilFraction, -n, ref push[j], ref push[i]);
                    }
                }

                if (!touched) return;

                for (int i = 0; i < count; i++)
                {
                    mobs[i].Pos += disp[i];
                    mobs[i].Vel += push[i];
                }
            }
        }

        /// The collector's half — the ONE thing the client also runs, through the
        /// same BodySeparation.Accumulate. Buffer layout is mobs in
        /// [0, mobCount) and collectors in [mobCount, mobCount + playerCount).
        static void CollectorPass(SimulationWorld w, PlayerState[] players, float2[] moved,
            int bodyCount, bool withShove)
        {
            MobState[] mobs = w.Mobs;
            int mobCount = w.MobCount;
            int playerCount = players.Length;
            HeroSimConfig hero = w.Config.Hero;

            PushableBody[] bodies = w.PushBodies;
            float2[] bodyDisp = w.PushDisp;
            float2[] bodyVel = w.PushVel;
            float2[] disp = w.SepDisplace;
            float2[] push = w.SepPush;

            for (int i = 0; i < bodyCount; i++) { disp[i] = float2.zero; push[i] = float2.zero; }

            for (int p = 0; p < playerCount; p++)
            {
                if (!players[p].Alive) continue;
                for (int k = 0; k < bodyCount; k++) { bodyDisp[k] = float2.zero; bodyVel[k] = float2.zero; }

                float2 d = float2.zero, v = float2.zero;
                BodySeparation.Accumulate(players[p].Pos, players[p].Vel, hero.Radius,
                    hero.Mass, hero.PushRecoilFraction,
                    new System.ReadOnlySpan<PushableBody>(bodies, 0, bodyCount),
                    ref d, ref v,
                    new System.Span<float2>(bodyDisp, 0, bodyCount),
                    new System.Span<float2>(bodyVel, 0, bodyCount),
                    skipIndex: mobCount + p);

                // The snapshot's slots ARE the buffer's slots — mobs first, then
                // collectors — so the reciprocals need no map at all.
                disp[mobCount + p] += d;
                if (withShove) push[mobCount + p] += v;

                // ⛔⛔ THE RECIPROCALS GO TO MOBS ONLY, AND THE BOUND IS
                // `mobCount` RATHER THAN `bodyCount` FOR TWO REASONS THAT ARE
                // BOTH LOAD-BEARING (ruling 121, review round of Т22, finding
                // C-1). Accumulate fills a reciprocal for EVERY body it
                // overlaps, collectors included; spilling those into collector
                // slots was the defect.
                //
                //   1. IT PROCESSED THE PAIR TWICE. Every live collector runs a
                //      pass of its own, so the pair p↔q is resolved in p's pass
                //      and again in q's — and ResolveBodyPair is symmetric
                //      (swapping the arguments flips `n`, so one pass's dA IS
                //      the other's dB). Each slot therefore received its share
                //      twice: at equal mass, the WHOLE overlap where half was
                //      owed. Bounded here, the pair is still resolved from both
                //      sides, but each side keeps only what it computed for
                //      ITSELF, which is the same half the client computes.
                //   2. IT BROKE RULING 113. The reciprocal velocity is derived
                //      from the PUSHER's speed, and PlayerPrediction hands
                //      Accumulate two EMPTY reciprocal spans — it has no mobs
                //      to move and no other collector's velocity to read. A
                //      collector shoved by another collector's motion is
                //      therefore unreproducible by construction, which is the
                //      one thing a predicted quantity may never be.
                //
                // A mob has no pass of its own, so its side of a collector↔mob
                // pair exists ONLY as this reciprocal — which is why the loop
                // stays rather than going away.
                for (int k = 0; k < mobCount; k++)
                {
                    disp[k] += bodyDisp[k];
                    if (withShove) push[k] += bodyVel[k];
                }
            }

            for (int i = 0; i < mobCount; i++)
            {
                mobs[i].Pos += disp[i];
                mobs[i].Vel += push[i];
            }
            for (int p = 0; p < playerCount; p++)
            {
                if (!players[p].Alive) continue;
                BodySeparation.ApplyToCollector(ref players[p], in hero,
                    disp[mobCount + p], push[mobCount + p], ref moved[p]);
            }
        }

        /// Every body in the world as ONE list: mobs in [0, MobCount), then
        /// collectors. The slot layout is the same one the displacement buffers
        /// use, so a reciprocal never needs mapping back.
        static int SnapshotBodies(SimulationWorld w, PlayerState[] players)
        {
            MobState[] mobs = w.Mobs;
            int mobCount = w.MobCount;
            PushableBody[] bodies = w.PushBodies;
            int n = 0;
            for (int i = 0; i < mobCount; i++)
            {
                ref readonly MobSimConfig c = ref w.MobConfigRefFor(mobs[i].Type);
                bodies[n++] = new PushableBody(mobs[i].Pos, c.Radius, c.Mass);
            }
            float heroRadius = w.Config.Hero.Radius, heroMass = w.Config.Hero.Mass;
            for (int q = 0; q < players.Length; q++)
            {
                // A DEAD collector keeps its slot but gets ZERO RADIUS: the slot
                // layout has to stay `mobCount + q` for the reciprocals to land
                // without a map, and a corpse is not an obstacle.
                //
                // ⚠ THE RADIUS ALONE DOES NOT MAKE IT ONE, and an earlier
                // wording of this comment claimed it did — "a body of radius 0
                // can never overlap, so ResolveBodyPair returns false for every
                // pair it is in" (review round of Т22, finding I-1). The gate is
                // `(rA + rB) - dist > 0`: zero guarantees `false` only against a
                // SECOND zero, while against a living 0.45 m collector a corpse
                // overlapped at any distance under 0.45 m and shoved it off its
                // line. What actually declines the corpse is
                // BodySeparation.Accumulate's own `Radius <= 0` skip; the zero
                // here is what MARKS it, and the mark and the rule live in the
                // one place each belongs to.
                bool alive = players[q].Alive;
                bodies[n++] = new PushableBody(players[q].Pos,
                    alive ? heroRadius : 0f, heroMass);
            }
            return n;
        }

        /// Applies ONE direction of the push law: `pusher` closes on `target`
        /// at `approach`, i.e. it travels along `-towardPusher`. Written once
        /// and called from both directions of a mob pair, so the asymmetry
        /// between the two is the ARGUMENTS and never a second copy of the
        /// arithmetic.
        ///
        /// ⚠ THE PARAMETER WAS CALLED `awayFromPusher` AND POINTED THE OTHER
        /// WAY (review round of Т22, finding M-5). Both call sites hand it the
        /// contact normal, whose own convention — stated by ResolveBodyPair —
        /// is that it points from the second body TO the first, i.e. from the
        /// target to the pusher; the body is thrown along its NEGATION, which
        /// is why the first line below carries a minus. The arithmetic was
        /// right and the name was not, which is the worse of the two failures:
        /// the next reader to "fix" the sign to match the name would break a
        /// working law.
        static void Shove(float pusherMass, float targetMass, float approach,
            float recoilFraction, float2 towardPusher,
            ref float2 pusherPush, ref float2 targetPush)
        {
            if (!Impact.ResolveBodyPush(pusherMass, targetMass, approach, recoilFraction,
                    out float targetDelta, out float pusherDelta))
                return;
            targetPush += -towardPusher * targetDelta;
            pusherPush += towardPusher * pusherDelta;
        }

        /// Arena AFTER bodies (D-C3): being pushed out of a body can drive a
        /// collector into a wall, and the wall is the thing that must win.
        ///
        /// ⛔ ONLY FOR COLLECTORS A BODY ACTUALLY MOVED, and the guard is not an
        /// optimisation — running this unconditionally was a REGRESSION, caught
        /// by WallGeometryTests (session 72). Depenetrate takes `ref vel` and
        /// clips the component pointing into the surface; MoveWithCollisions has
        /// already resolved the arena for this tick, with its own collide-and-
        /// slide, so a second unconditional pass re-clipped a velocity that was
        /// deliberately left tangential and cost a collector sliding along a
        /// wall a third of its distance (13.71 m of an expected 22.35 m).
        /// The pass exists to answer ONE question -- "did the body push put me
        /// inside geometry?" -- and where no body pushed, there is no question.
        static void ResolveArena(SimulationWorld w, PlayerState[] players, float2[] moved)
        {
            ArenaSimConfig arena = w.Config.Arena;
            float radius = w.Config.Hero.Radius;
            for (int p = 0; p < players.Length; p++)
            {
                if (!players[p].Alive) continue;
                if (moved[p].x == 0f && moved[p].y == 0f) continue;
                Geometry.Depenetrate(ref players[p].Pos, ref players[p].Vel, radius, in arena, 1);
            }
        }
    }
}
