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
        /// T1 GREEN): on a BODY stop it is the winning VOLUME's own first
        /// contact -- the number HitVolumes.Resolve hands back (app-94sk T3;
        /// until then it was the band-era HitZones.Resolve, deleted by that same
        /// task) -- and NOT the entry into the gather circle the candidates
        /// were ranked by. The two differ by up to (gather radius - part
        /// radius), which on a chaser is 1.08 m -- his gather circle is 1.25 and
        /// his narrowest volume, the head, is 0.17. (Before T3 the same bound
        /// was 0.33 m, measured off the physical circle of 0.50 the ranking then
        /// used.) The argument for taking the part is the
        /// argument the whole task stands on: the line must answer what the SHOT answers,
        /// and the shot's own contact point travels out of exactly this
        /// number (ProjectileSystem carries Resolve's `t` out for the
        /// two-dimensional contact of a body hit, Ruling 73, precisely so the
        /// reported point cannot disagree with the reported height). Ranking
        /// still happens on the CIRCLE -- since T3 the GATHER circle, the one
        /// the round's own broad phase sweeps -- because that is the order the
        /// round uses; the two roles of `t` are separate, and test 19 pins
        /// the ranking half.
        ///
        /// ⛔ `poseScratch` IS THE SECOND BUFFER OF THE SAME KIND (app-94sk T6b,
        /// spec §3.7): HitVolumes.Resolve reads a READY pose, PoseTable.Sample
        /// writes one into a buffer the caller owns, and the rule above admits
        /// no other owner. One pose, not a memo: the line resolves in the
        /// PRESENT, one body at a time, and has no rewind depth to key by.
        /// Sized by SimConfig.MaxBoneCount, the one home of "how wide is the
        /// widest body". No default value, for the reason the candidate
        /// scratch has none.
        public static AimLineSolution Solve(float2 heroPos, float2 aimPoint, float muzzleHeight,
            in SimConfig cfg, RenderSnapshot snap, int selfIndex,
            (float t, int kind, int index)[] scratch, float3[] poseScratch)
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
                // app-94sk T3 (spec §3.3): THE BROAD PHASE ASKS THE GATHER
                // RADIUS, NOT THE PHYSICAL ONE -- the same move the round made in
                // T2, and for the same reason: a chaser's foot swings 0.9 m out
                // of his 0.5 m circle, so the physical radius discarded the leg
                // before the narrow phase could be asked about it at all.
                // ⛔ NO SWITCH OF THIS FILE'S OWN AND NO SECOND MobRadiusFor:
                // MobConfigFor returns `ref readonly` since Т31, so reading the
                // field straight off it copies nothing -- which is why
                // MobRadiusFor, whose whole reason for existing was that copy,
                // is deleted by this task.
                // app-94sk T6b: a leaning body's circle is its leaned pose's
                // reach (HitVolumes.GatherRadiusFor); an upright one keeps the
                // table's number, and neither packs a key nor samples here.
                ref readonly MobSimConfig mobCfg = ref SimConfig.MobConfigFor(in cfg, mob.Type);
                float mobRadius = mobCfg.GatherRadius;
                if (HitVolumes.LeanOf(mob.Tilt).Leaning)
                {
                    mobRadius = LeaningCircleOf(mobCfg.Parts, in mobCfg.Poses, PoseKey.FromMob(in mob),
                        singleLayer: true, mob.Dir, BakedClips.MobForward, mob.Tilt, mobCfg.GatherRadius,
                        poseScratch);
                }
                if (Geometry.SegmentCircle(start, far, cfg.Weapon.ProjectileRadius, mob.Pos,
                        mobRadius, out float tm))
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
                // ⚠ AND THIS HALF HAS NO WITNESS, WHICH IS SAID RATHER THAN
                // IMPLIED: a collector's fixture bones all sit on his own axis,
                // so GatherReachOf answers max(part radius) = 0.45 -- the same
                // number Hero.Radius carries, and a mutant reading the physical
                // radius here is green on the whole suite. The gap closes in
                // T4b, where the collector's volumes move onto real bones and
                // his arms leave the axis; the mob half is witnessed today
                // (fixture 42 stands 0.89 m out against a physical 0.62).
                float otherRadius = cfg.Hero.GatherRadius;
                if (HitVolumes.LeanOf(other.Tilt).Leaning)
                {
                    otherRadius = LeaningCircleOf(cfg.Hero.Parts, in cfg.Hero.Poses,
                        PoseKey.FromPlayer(in other), singleLayer: false, other.Dir,
                        BakedClips.CollectorForward, other.Tilt, cfg.Hero.GatherRadius, poseScratch);
                }
                if (Geometry.SegmentCircle(start, far, cfg.Weapon.ProjectileRadius, other.Pos,
                        otherRadius, out float tp))
                {
                    scratch[count++] = (tp, CandidatePlayer, i);
                }
            }

            HitZone zone = HitZone.None;
            // The fraction of [start, far] the line actually ends at. It stays
            // the min-scan's own number for every stop but a body; see this
            // method's doc for why a body's is the winning PART's instead.
            // ⚠ AND A BODY'S NUMBER IS TAKEN UNCONDITIONALLY, WHICH IS SAID
            // HERE BECAUSE T3 WIDENED IT: the scan proves the body's GATHER
            // circle is entered nearer than the barrier, not that its VOLUME is,
            // so a line stopping on a body can be drawn up to (gather radius -
            // part radius) past a barrier that stood between them -- 1.08 m on a
            // chaser now, against 0.33 m while the ranking used his physical
            // circle. It is left that way on purpose: the round resolves the
            // same order in the same two stages, so changing it here would make
            // the picture disagree with the shot, which is the one thing this
            // whole file exists to prevent. The round's own overshoot is bounded
            // by its step (1.17 m on the fixtures' numbers) rather than by the
            // range, so the two stay the same shape.
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
                        start, far, muzzleHeight, poseScratch, out zone, out float contactT))
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
        /// hit volumes (app-94sk T3, spec §3.12 -- the line follows the geometry
        /// the round already moved onto in T2).
        ///
        /// ⛔ THE PARTS, THE POSE TABLE AND THE GATHER RADIUS COME OUT OF THE
        /// NAMED HOMES, NEVER OUT OF A SWITCH OF THIS FILE'S OWN:
        /// SimConfig.MobConfigFor for a mob and cfg.Hero for a collector -- the
        /// same two SOURCES ProjectileSystem reads at the same point. ⚠ Not the
        /// same MEMBER: that one goes through SimulationWorld.MobConfigFor, which
        /// hands back a COPY, because it has a world to ask; a pure function has
        /// only the config, and the static form returns `ref readonly`.
        ///
        /// ⛔⛔ THE RESOLVER IS ASKED ABOUT THE CHORD, NOT ABOUT THE WHOLE RAY,
        /// AND THAT IS CORRECTNESS RATHER THAN ECONOMY. HitVolumes.Resolve rests
        /// on Geometry.SegmentCapsule, which finds the first entry by SCANNING
        /// the step with 16 probes; that count is chosen against the ROUND's
        /// step, and its own doc says so -- "the round's step is 1.75 at the
        /// shipped speed, so 16 probes stand 0.109 apart". This ray is the whole
        /// range: 52.5 m on the fixtures' numbers and 78.75 m on the game's, so
        /// its probes would stand 3.28 m and 4.92 m apart while a head-on
        /// passage through a capsule is 0.56 m (a collector's head) to 1.24 m (a
        /// chaser's torso) -- only the Director's own bulk, 3.32 m and 4.64 m,
        /// is too wide to be stepped over.
        /// ⛔ AND THE FAILURE IS BY PHASE, NOT ALWAYS, WHICH IS WORSE THAN A
        /// PLAIN MISS: whether a probe lands inside depends on where the body
        /// stands along the ray, so the SAME chaser torso answers HIT at 10 m
        /// and MISS at 8 m on the fixtures' own numbers. Handing over the whole
        /// ray would therefore have shipped an indicator that disagrees with the
        /// round roughly two times in three and agrees the rest of the time --
        /// a defect no fixture could name, only sample. Mutation M393 is exactly
        /// that experiment: nine fixtures die and four survive.
        /// ⇒ the segment handed down is the ray's passage through the body's
        /// GATHER circle, taken from the existing Geometry.SegmentCircleInterval
        /// -- the very member the band-era resolver used for the same purpose --
        /// and the winning `t` is carried back into the ray's own
        /// parameterization, the one the min-scan above ranks in. The probes
        /// then stand 0.07 m to 0.29 m apart against a thinnest capsule of
        /// 0.56 m, on all five bodies.
        /// ⚠ WHAT IT COSTS, STATED HONESTLY: the chord is narrower than the ray,
        /// so this is safe exactly while every volume lies inside its own body's
        /// gather circle -- validation rule 9, "the gather covers the furthest
        /// bone plus the volume on it". Under it the argument closes: a contact
        /// point is within (projRadius + partRadius) of a bone, a bone is within
        /// (gather - partRadius) of the axis, so every contact sits inside the
        /// gather circle and the chord holds every `t` the whole ray would.
        /// ⛔⛔ AND THE RULE IS NOT IN THE CODE YET -- IT ARRIVES WITH THE BAKER
        /// IN T4, which SimConfigBuilder.ValidateParts says in as many words.
        /// The fixtures satisfy it because TestConfigs.GatherReachOf computes it;
        /// the shipped `.asset` does NOT -- its GatherRadius is a placeholder
        /// equal to the physical radius until the baker fills it, and ConfigTests
        /// pins that placeholder deliberately. ⭐ What keeps the window T3 -> T4
        /// harmless is a SECOND absence: the shipped side carries no pose table
        /// either (SimConfigBuilder maps `Poses` only from T4), so Resolve
        /// refuses BOTH paths on its table guard and the ray cannot disagree
        /// with the round about a body neither of them can hit. When T4 lands
        /// both halves together, rule 9 makes this chord exact.
        ///
        /// ⚠ THE HEIGHT SPAN IS A SINGLE NUMBER, TWICE: the line is horizontal
        /// at the muzzle's height by definition, so both ends of the step carry
        /// that height and the resolver's climbing machinery collapses to the
        /// flat case.
        /// ⛔ AND THE SLIDE CEILING STAYS IN FRONT OF THE VOLUMES, exactly as it
        /// does for the round: the crown of the presented silhouette is the pose
        /// table's business (HitParts.PoseTop, inside Resolve), while the
        /// mid-slide profile is a RULE ABOUT A STATE that no table can express.
        /// Validation rule 5 keeps it alive until T4.
        static bool ResolveBody(in SimConfig cfg, RenderSnapshot snap, int kind, int index,
            float2 p0, float2 p1, float muzzleHeight, float3[] poseScratch,
            out HitZone zone, out float contactT)
        {
            zone = HitZone.None;
            contactT = 0f;

            HitPart[] parts;
            PoseTable poses;
            float2 targetPos;
            float gatherRadius;
            // app-94sk T6b: the pose's key, its layer count, the course
            // turned by the rig's forward and the lean -- what ProjectileSystem
            // reads off the same body for the same call. ⚠ OFF THE FRAME, AND
            // THE FRAME IS WHAT THE WIRE GAVE: on a networked client a mob's
            // record carries no pose fields and (bd app-4io9) no course, and a
            // foreign collector's `Dir` is his AIM (Р68), so the line answers
            // with the rest pose in the table's orientation for them -- the
            // client-side derivation of a foreign body's phase is spec
            // §3.14's, plan 2's. In a local or host frame the state is the
            // world's and the line agrees with the round to the bone.
            PoseKey key;
            bool singleLayer;
            float2 dir, rigForward, tilt;
            float facingSin, facingCos;
            // NaN means "no ceiling of that kind", the same "stand down rather
            // than invent a bound" convention ProjectileSystem's own branch uses.
            float slideCeiling = float.NaN;
            if (kind == CandidateMob)
            {
                MobState mob = snap.Mobs[index];
                ref readonly MobSimConfig mobCfg = ref SimConfig.MobConfigFor(in cfg, mob.Type);
                parts = mobCfg.Parts;
                poses = mobCfg.Poses;
                gatherRadius = mobCfg.GatherRadius;
                targetPos = mob.Pos;
                key = PoseKey.FromMob(in mob);
                singleLayer = true;
                dir = mob.Dir; rigForward = BakedClips.MobForward; tilt = mob.Tilt;
            }
            else
            {
                PlayerState other = snap.Players[index];
                parts = cfg.Hero.Parts;
                poses = cfg.Hero.Poses;
                gatherRadius = cfg.Hero.GatherRadius;
                targetPos = other.Pos;
                key = PoseKey.FromPlayer(in other);
                singleLayer = false;
                dir = other.Dir; rigForward = BakedClips.CollectorForward; tilt = other.Tilt;
                // Mid-slide a collector presents a lower silhouette, and the
                // slide bit rides the wire, so this holds in a PvP frame as
                // much as in a local one -- the same choice ProjectileSystem
                // makes at the same point.
                if (other.SlideTimer > 0f) slideCeiling = cfg.Hero.SlideProfileTop;
            }

            if (!float.IsNaN(slideCeiling) && !HitZones.Overlaps(muzzleHeight, muzzleHeight,
                    cfg.Weapon.ProjectileRadius, slideCeiling)) return false;

            // The ray's passage through this body's gather circle. A refusal here
            // cannot happen for a candidate the broad phase just accepted -- it
            // solves the same quadratic against the same circle -- and is kept
            // because this method is also the one that must not read past a
            // degenerate interval.
            // app-94sk T6b: a body without a table is refused HERE, where
            // Resolve refused it before this task -- Sample would refuse it by
            // name, and the line is drawn every frame.
            if (poses.Bones == null || poses.BoneCount <= 0) return false;
            // The same circle the broad phase used for this body, its leaned
            // reach if it leans (the sample lands in the scratch Resolve reads
            // a moment later and is re-done there: one body, one buffer, two
            // readers a few lines apart, and only for a leaning body).
            HitVolumes.YawOf(dir, rigForward, out facingSin, out facingCos);
            if (HitVolumes.LeanOf(tilt).Leaning)
            {
                gatherRadius = LeaningCircleOf(parts, in poses, in key, singleLayer, dir, rigForward,
                    tilt, gatherRadius, poseScratch);
            }
            if (!Geometry.SegmentCircleInterval(p0, p1, cfg.Weapon.ProjectileRadius, targetPos,
                    gatherRadius, out float tEnter, out float tExit)) return false;

            // ⛔ THE STEP IS WOVEN THE ORDINARY WAY -- `new float3(plane, height)`,
            // the very form ShotGeometry builds its muzzle and aim points with.
            // The bones arrive in the BODY frame and HitVolumes.ToWorld is what
            // reconciles the two; nothing is transposed here (app-coou).
            // app-94sk T6b: the READY pose, sampled into the caller's scratch
            // -- one body at a time, in the present, so no memo (spec §3.7).
            PoseTable.Sample(in poses, in key, singleLayer, poseScratch);
            if (!HitVolumes.Resolve(parts, in poses, poseScratch, tilt,
                    bodyOrigin: new float3(targetPos, 0f),
                    facingSin, facingCos,
                    p0: new float3(math.lerp(p0, p1, tEnter), muzzleHeight),
                    p1: new float3(math.lerp(p0, p1, tExit), muzzleHeight),
                    projRadius: cfg.Weapon.ProjectileRadius,
                    out zone, out _, out _, out _, out float chordT)) return false;

            // Back into the RAY's parameterization: the min-scan above ranks in
            // [start, far], and so does everything downstream of `Length`.
            contactT = tEnter + chordT * (tExit - tEnter);
            return true;
        }

        /// app-94sk T6b: the broad phase's circle for a LEANING body --
        /// ProjectileSystem.LeaningCircleOf's twin over the caller's scratch
        /// instead of the world's memo (that helper's doc says why the twin
        /// is a twin and not a shared helper: the buffer's owner). The law
        /// lives in HitVolumes.GatherRadiusFor; the caller asks `LeanOf` first.
        static float LeaningCircleOf(HitPart[] parts, in PoseTable table, in PoseKey key,
            bool singleLayer, float2 dir, float2 rigForward, float2 tilt, float gatherRadius,
            float3[] poseScratch)
        {
            if (table.Bones == null || table.BoneCount <= 0) return gatherRadius;   // refused downstream
            PoseTable.Sample(in table, in key, singleLayer, poseScratch);
            return HitVolumes.GatherRadiusFor(parts, poseScratch, table.BoneCount, dir, rigForward, tilt, gatherRadius);
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
