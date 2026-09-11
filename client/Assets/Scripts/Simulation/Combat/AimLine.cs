using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Where the aim line ends -- found through the same order of candidates
    /// the round itself walks. ⚠ ITS HOME IS Ring.Simulation, AND THAT IS
    /// THREE ARGUMENTS RATHER THAN CONVENIENCE: HitZones is internal by its
    /// own decision (its header says so), the answer is required to agree with
    /// the SERVER's (a physics cast against the view proxies answers a
    /// different question, and the divergence between those proxies and Parts
    /// is app-94sk's subject), and EditMode raises no MonoBehaviour at all.
    ///
    /// ⛔ IT WRITES NOTHING AND THE SIMULATION NEVER CALLS IT. The one consumer
    /// is Presentation/AimProvider. So this file moves no golden hash, and the
    /// owner's re-pin sanction (Н44) is not spent on it -- that one belongs to
    /// app-94sk.
    public enum AimStop : byte { Range = 0, Barrier = 1, RingWall = 2, Body = 3 }

    /// ⚠ EVERY FIELD HAS A READER (Ruling 73): End/Height are the ray itself
    /// (T2); Start/Dir/NotchAt/NotchHalfWidth are the notches (T3);
    /// Length/Stop/Zone/NotchDistance are the dev readout (T4). The index of
    /// the body that was hit is NOT declared: nothing reads it.
    /// ⚠ End and NotchAt are PROPERTIES rather than fields (ruling 291): one
    /// number, one home.
    public readonly struct AimLineSolution
    {
        public readonly float2 Start;          // the muzzle in plane view
        public readonly float2 Dir;            // unit direction of the line
        public readonly float Length;          // up to whatever stops it
        public readonly float Height;          // constant height of the line = the muzzle's
        public readonly AimStop Stop;
        public readonly HitZone Zone;          // the part under the axis; None outside a Body stop
        public readonly float NotchDistance;   // where the notches stand
        public readonly float NotchHalfWidth;  // half-width of the cone at that distance

        // ⛔ THE CONSTRUCTOR IS MANDATORY, AND WITHOUT IT Solve WOULD NOT
        // COMPILE: readonly fields are assignable only inside one, and an
        // object initializer does not reach them. Both neighboring shapes are
        // in this same folder -- ProjectileFlight.StepResult (an internal ctor
        // carrying a doc for why it is internal) and ShotGeometry.ShotSolution
        // (a public one). Internal is taken here: nothing outside this assembly
        // builds this struct, and the tests are let in through
        // InternalsVisibleTo.
        internal AimLineSolution(float2 start, float2 dir, float length, float height,
            AimStop stop, HitZone zone, float notchDistance, float notchHalfWidth)
        {
            Start = start;
            Dir = dir;
            Length = length;
            Height = height;
            Stop = stop;
            Zone = zone;
            NotchDistance = notchDistance;
            NotchHalfWidth = notchHalfWidth;
        }

        public float2 End => Start + Dir * Length;
        public float2 NotchAt => Start + Dir * NotchDistance;
    }

    public static class AimLine
    {
        /// The two kinds a candidate slot can carry. They are ONLY these two:
        /// the barrier and the rim reach Solve out of the projectile step and
        /// never enter the buffer, and a floor candidate cannot exist for a
        /// horizontal line. The values matter no more than their names do --
        /// the packing ORDER is what carries the tie-break, and that is stated
        /// where the gather happens.
        const int CandidateMob = 0;
        const int CandidatePlayer = 1;

        /// The muzzle in plane view -- the home of that formula FOR THE
        /// PICTURE.
        /// ⚠ THE NAME IS NOT `MuzzlePlan` FROM SPEC §3.2, AND THAT IS PLAN
        /// DEVIATION 6: in English `Plan` reads as "a plan (the document)",
        /// while the meaning wanted here is "in plan" = plane view. The
        /// repository's own convention is already spoken by the neighbors --
        /// AimProvider.ComputePlaneAimSimPos, its local planeAimSimPos, and
        /// SimulationRunner.RenderMuzzleSimPos, whose copy this member takes
        /// over.
        ///
        /// ⚠ THERE ARE THREE COPIES OF THIS FORMULA IN THE REPOSITORY, NOT
        /// TWO: both branches of ShotGeometry.Solve (the aimed one character
        /// for character, the hip one plus overshoot * horizSpeed) and
        /// SimulationRunner.RenderMuzzleSimPos, which T2 takes over. The two
        /// inside ShotGeometry must not be touched -- they are pinned by the
        /// goldens; test 29 runs BOTH branches so the copies cannot drift
        /// apart unnoticed.
        public static float2 MuzzleSimPos(float2 heroPos, float2 aimPoint, float muzzleOffset,
            out float2 dir)
        {
            // ⚠ THE FALLBACK (1,0) IS WORD FOR WORD THE ONE BOTH BRANCHES OF
            // ShotGeometry.Solve AND SimulationRunner.RenderMuzzleSimPos USE.
            // normalizesafe answers with that defaultvalue rather than a zero
            // vector, so a degenerate input -- the cursor exactly on the
            // collector -- yields a finite unit direction instead of a NaN or a
            // zero one.
            dir = math.normalizesafe(aimPoint - heroPos, new float2(1f, 0f));
            return heroPos + dir * muzzleOffset;
        }

        /// ⛔⛔ THE SCRATCH BUFFER ARRIVES AS A PARAMETER, AND THAT IS PLAN
        /// DEVIATION 7. The two-stage scan with a re-scan needs room for its
        /// candidates, and a pure function has no world of its own. THE SHAPE
        /// IS THE NEIGHBOR'S: ProjectileSystem takes the world's preallocated
        /// array as a parameter. Here the buffer belongs to the caller --
        /// AimProvider holds it in a field. That is what makes test 27 ("no
        /// allocations") provable rather than merely promised.
        ///
        /// ⛔ ITS SIZE IS MaxMobs + MaxPlayers, AND THE NEIGHBOR'S NUMBER IS
        /// NOT COPIED HERE: the world's own projectile buffer carries three
        /// index-less slots on top of the bodies, because the barrier, the rim
        /// and the floor are packed into that same array. Here the barrier and
        /// the rim arrive OUT OF the projectile step and never enter the
        /// buffer, and a floor candidate cannot exist by construction
        /// (VelZ = 0) -- so the buffer holds BODIES ONLY. Copying "+2" or "+3"
        /// would be borrowing someone else's spare room for someone else's
        /// packing.
        /// ⛔ Neither a static nor a stackalloc: a mutable static would be a
        /// construction new to Ring.Simulation, and 1350 records on the stack
        /// (16 KB) is a decision rather than a detail.
        ///
        /// ⭐⭐ WHAT `Length` MEASURES, DECIDED HERE AND STATED ONCE (app-461s
        /// T1 GREEN): on a BODY stop it is the winning PART's own first
        /// contact -- the number HitZones.Resolve hands back -- and NOT the
        /// entry into the body circle the candidates were ranked by. The two
        /// differ by up to (body radius - part radius), a third of a meter on
        /// a chaser, and the argument for taking the part is the argument the
        /// whole task stands on: the line must answer what the SHOT answers,
        /// and the shot's own contact point travels out of exactly this
        /// number (ProjectileSystem carries Resolve's `t` out for the
        /// two-dimensional contact of a body hit, Ruling 73, precisely so the
        /// reported point cannot disagree with the reported height). Ranking
        /// still happens on the BODY circle, because that is the order the
        /// round uses -- the two roles of `t` are separate, and test 19 pins
        /// the ranking half.
        public static AimLineSolution Solve(float2 heroPos, float2 aimPoint, float muzzleHeight,
            in SimConfig cfg, RenderSnapshot snap, int selfIndex,
            (float t, int kind, int index)[] scratch)
        {
            float2 start = MuzzleSimPos(heroPos, aimPoint, cfg.Weapon.MuzzleOffset,
                out float2 dir);
            // How far a round of this weapon travels before its own lifetime
            // ends it. No new dial: the line reaches exactly as far as the
            // shot does.
            float reach = cfg.Weapon.ProjectileSpeed * cfg.Weapon.ProjectileLifetime;
            float2 far = start + dir * reach;

            // 1. BARRIERS AND THE RIM -- through the EXISTING pair, over a
            //    synthetic round. The precedent for calling it from outside
            //    with a hand-built ProjectileState is TracerProjectiles, which
            //    does exactly this. ⛔ No overload of BarrierStops "by a pair
            //    of heights" is introduced: the existing member reads p.Radius
            //    too, and a mob's round does not carry the weapon's.
            var probe = new ProjectileState
            {
                Pos = start,
                Vel = dir * reach,
                Height = muzzleHeight,
                VelZ = 0f,
                Radius = cfg.Weapon.ProjectileRadius,
            };
            // ⚠ dt = 1 WITH Vel = dir * reach, so the step spans exactly
            // [start, far] and every `t` below -- the sweep's, the body
            // circles', the part's -- lives in ONE parameterization.
            ProjectileFlight.StepResult step = ProjectileFlight.Step(in probe, in cfg, dt: 1f);
            // ⚠ At VelZ = 0 the floor candidate cannot fire BY CONSTRUCTION
            // (its gate is `p.VelZ < 0f`), which is an argument rather than a
            // coincidence -- the line is horizontal by definition.

            // 2. THE BARRIER'S HEIGHT GATE, asked through the existing member
            //    under the same guard the precedent uses.
            // ⛔ IT IS ASKED ALWAYS, THOUGH TODAY'S ANSWER IS KNOWN: at
            // BarrierTop <= 0 it answers "holds everything", and at the
            // shipped 3.0 against a muzzle at 1.0 or 0.45 it answers true as
            // well. An answer that is right BY THE NUMBER stops being right
            // the day the number moves.
            // ⚠ contactHeight is muzzleHeight rather than the precedent's
            // `p.Height + p.VelZ * dt * step.BarrierT`: at VelZ = 0 the two
            // expressions are the same number, and the simple one is written.
            bool barrier = step.HasBarrier
                && ProjectileFlight.BarrierStops(in probe, in cfg, muzzleHeight, 1f);

            // The line's own min-scan starts where the round's does: at the
            // range limit, t = 1. Strict `<` throughout, and the ORDER of the
            // three tests below is ProjectileSystem's own packing order --
            // barrier, then rim, then bodies -- so an exact tie is broken the
            // same way the shot breaks it.
            float bestT = 1f;
            AimStop stop = AimStop.Range;
            if (barrier && step.BarrierT < bestT)
            {
                bestT = step.BarrierT;
                stop = AimStop.Barrier;
            }
            // ⚠ THE RIM HAS NO CROWN OF ITS OWN, DELIBERATELY (owner decision
            // 2026-08-11, which BarrierStops' own doc states): it holds the
            // edge of the world, so there is no height gate here and there
            // must not be one.
            if (step.HasRingWall && step.RingWallT < bestT)
            {
                bestT = step.RingWallT;
                stop = AimStop.RingWall;
            }

            // 3. BODIES -- TWO STAGES, exactly as the round does them.
            //    (a) the broad phase sweeps the BODY circle and packs what it
            //        meets into the caller's buffer;
            //    (b) a repeated min-scan resolves the nearest against its
            //        PARTS, and a refusal excludes that candidate (swap-remove)
            //        and rescans.
            // ⛔ ONE STAGE WOULD NOT DO, and it is not an economy: parts are
            // coaxial and never wider than the body, so a part's `t` is never
            // smaller than its body circle's, and the gap grows the closer the
            // line runs to a tangent. Ranking on parts would give a DIFFERENT
            // order than the round's -- and the whole value here is that the
            // order is the SAME one.
            int count = 0;
            int mobCount = snap.MobCount;
            for (int m = 0; m < mobCount; m++)
            {
                MobState mob = snap.Mobs[m];
                // ⛔ MOBS CARRY NO GATE, AND A HEALTH GATE WOULD BE A DEFECT:
                // Hp is quantized to a byte on the wire, so a live body under
                // 1/510 of its maximum decodes to zero -- the Director's last
                // few points -- and the pointer would lie exactly while the
                // boss is being finished off. The frame's mob array is live by
                // construction.
                if (Geometry.SegmentCircle(start, far, cfg.Weapon.ProjectileRadius, mob.Pos,
                        ProjectileSystem.MobRadiusFor(mob.Type, in cfg), out float tm))
                {
                    scratch[count++] = (tm, CandidateMob, m);
                }
            }

            int playerCount = snap.PlayerCount;
            for (int i = 0; i < playerCount; i++)
            {
                // The shooter's own body is not a target of his own line.
                if (i == selfIndex) continue;
                PlayerState other = snap.Players[i];
                // ⛔ COLLECTORS ARE GATED ON `Alive`, THE SAME RULE THE ROUND
                // GATES THEM BY -- and it is NOT `Hp > 0`: a collector who
                // extracts leaves the arena in good health, and a slot the
                // frame said nothing about reads as `default(PlayerState)`,
                // which is not alive.
                if (!other.Alive) continue;
                if (Geometry.SegmentCircle(start, far, cfg.Weapon.ProjectileRadius, other.Pos,
                        cfg.Hero.Radius, out float tp))
                {
                    scratch[count++] = (tp, CandidatePlayer, i);
                }
            }

            HitZone zone = HitZone.None;
            // The fraction of [start, far] the line actually ends at. It stays
            // the min-scan's own number for every stop but a body; see this
            // method's doc for why a body's is the winning PART's instead.
            float stopT = bestT;
            while (count > 0)
            {
                int bestSlot = -1;
                // ⚠ THE BOUND IS THE BARRIER/RIM/RANGE BEST, NOT INFINITY: a
                // body only wins if it is STRICTLY nearer than they are, which
                // is what packing it after them in one array would have meant.
                float bodyT = bestT;
                for (int c = 0; c < count; c++)
                {
                    if (scratch[c].t < bodyT)
                    {
                        bodyT = scratch[c].t;
                        bestSlot = c;
                    }
                }
                if (bestSlot < 0) break;

                if (ResolveBody(in cfg, snap, scratch[bestSlot].kind, scratch[bestSlot].index,
                        start, far, muzzleHeight, out zone, out float contactT))
                {
                    stop = AimStop.Body;
                    stopT = contactT;
                    break;
                }

                // Refused -- over its crown or under its feet, or past the
                // narrow part standing at this height. Drop it and rescan, so
                // a body BEHIND a screening one stays reachable.
                zone = HitZone.None;
                scratch[bestSlot] = scratch[--count];
            }

            float length = stopT * reach;

            // 4. THE NOTCHES. ⛔ THEY STAND AT min(stop, cursor), AND THAT IS
            //    THE POINT OF THE WHOLE INDICATOR: pinned to the END of an
            //    unobstructed line they would sit 78.75 m out, where the cone
            //    is 7.58 m wide standing and 15.31 m in a slide against a
            //    visible strip of ground about 25 m across -- off the screen
            //    entirely. Worse, sliding the cursor off a mob (a stop at 9 m)
            //    onto empty ground would move the gap by a factor of 8.75 in
            //    one frame, and one picture would then encode both the cone's
            //    angle and an accidental distance. The reticle stood at the
            //    cursor for a reason, and the notches stand there too.
            float toCursor = math.distance(start, aimPoint);
            float notchDistance = math.min(length, toCursor);
            PlayerState self = snap.Players[selfIndex];
            float notchHalfWidth = Spread.HipHalfWidth(in cfg.Weapon, in self, in cfg.Hero,
                notchDistance);

            return new AimLineSolution(start, dir, length, muzzleHeight, stop, zone,
                notchDistance, notchHalfWidth);
        }

        /// The narrow half of stage (b): the nearest candidate against its own
        /// stack of parts.
        ///
        /// ⛔ THE RADIUS AND THE PARTS COME OUT OF THE NAMED HOMES, NEVER OUT
        /// OF A SWITCH OF THIS FILE'S OWN: ProjectileSystem.MobRadiusFor for a
        /// mob's circle (its own doc explains that it was extracted so that a
        /// test could hold it in step with MobConfigFor), SimConfig.MobConfigFor
        /// for the parts, and cfg.Hero for a collector -- the same two reads
        /// ProjectileSystem makes.
        ///
        /// ⚠ THE HEIGHT SPAN IS A SINGLE NUMBER, TWICE: the line is horizontal
        /// at the muzzle's height by definition, so hStart and hEnd are that
        /// height and Resolve's climbing/descending machinery collapses to the
        /// flat case it names in its own doc.
        static bool ResolveBody(in SimConfig cfg, RenderSnapshot snap, int kind, int index,
            float2 p0, float2 p1, float muzzleHeight, out HitZone zone, out float contactT)
        {
            HitPart[] parts;
            float2 targetPos;
            float overlapTop;
            if (kind == CandidateMob)
            {
                MobState mob = snap.Mobs[index];
                parts = SimConfig.MobConfigFor(in cfg, mob.Type).Parts;
                targetPos = mob.Pos;
                overlapTop = HitZones.StackTop(parts);
            }
            else
            {
                PlayerState other = snap.Players[index];
                parts = cfg.Hero.Parts;
                targetPos = other.Pos;
                // Mid-slide a collector presents a lower silhouette, and the
                // slide bit rides the wire, so this holds in a PvP frame as
                // much as in a local one -- the same choice ProjectileSystem
                // makes at the same point.
                overlapTop = other.SlideTimer > 0f
                    ? cfg.Hero.SlideProfileTop
                    : HitZones.StackTop(parts);
            }

            return HitZones.Resolve(parts, p0, p1, cfg.Weapon.ProjectileRadius, targetPos,
                muzzleHeight, muzzleHeight, overlapTop, out zone, out _, out _, out contactT);
        }

        /// The four points of the two cross strokes (T3 draws them).
        public static void Notches(in AimLineSolution line, float strokeLength,
            out float2 a0, out float2 a1, out float2 b0, out float2 b1)
        {
            // The perpendicular is taken off the direction of the LINE. ⚠ The
            // mutation "take it off the direction to the cursor" is INEXPRESSIBLE
            // inside this signature -- there is no cursor here at all, and the
            // one available surrogate (NotchAt - Start) is collinear with Dir by
            // construction. That is why M321 was struck out as closed by
            // construction rather than run.
            float2 perp = new float2(-line.Dir.y, line.Dir.x);
            float2 anchor = line.NotchAt;
            float half = 0.5f * strokeLength;
            float2 left = anchor + perp * line.NotchHalfWidth;
            float2 right = anchor - perp * line.NotchHalfWidth;
            // ⚠ THE STROKE IS LAID ALONG THE PERPENDICULAR, i.e. ACROSS the
            // line: the pair reads as two flanking crossbars rather than as
            // a pair of sleepers.
            // ⇒ the ends stand at NotchHalfWidth -/+ half the stroke, so none of
            // these four points is at the half-width itself -- a reader
            // measuring the cone's edge measures the MIDDLE of a stroke.
            a0 = left - perp * half;
            a1 = left + perp * half;
            b0 = right - perp * half;
            b1 = right + perp * half;
        }

        /// ⛔ THE STROKE'S LENGTH IS A PURE FUNCTION TOO, AND THAT IS PLAN
        /// DEVIATION 8. The spec left the formula `max(floor, frac x hw)`
        /// inside AimRayView while putting test 26 and mutation M322 into
        /// AimLineTests -- that is, into a place which cannot see the formula
        /// at all. Both review rounds found this independently. The view hands
        /// over two numbers out of the ScriptableObject, this function makes
        /// the decision, and Р486 then holds: not one NUMERIC decision is left
        /// in AimRayView.
        public static float NotchStroke(float notchHalfWidth, float frac, float minLength)
            => math.max(minLength, frac * notchHalfWidth);
    }
}
