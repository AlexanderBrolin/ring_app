using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Vertical hit-volume maths shared by every damageable body (Task 6): the
    /// hero and both mob archetypes carry the same LegsTop/BodyTop/HeadTop +
    /// per-zone multiplier shape, so these are scalar helpers taking the four
    /// numbers directly rather than an overload per config struct — one body,
    /// every caller.
    ///
    /// Internal on purpose: nothing outside the simulation assembly needs to
    /// re-derive a zone. Presentation reads the resolved `SimEvent.Zone`.
    internal static class HitZones
    {
        /// Zone of a hit landing at height `h`, in half-open bands
        /// [0, legsTop) → Legs, [legsTop, bodyTop) → Body, [bodyTop, headTop] → Head.
        ///
        /// `h` is CLAMPED into [0, headTop] first, which is what makes the edges
        /// forgiving: a round that only grazes the crown (h slightly above
        /// headTop, still inside the projectile's own radius — see Overlaps)
        /// reads as Head rather than falling off the table, and one that cuts in
        /// just under the floor line reads as Legs. Clamping and hit/no-hit are
        /// deliberately separate decisions: Overlaps below decides IF the shot
        /// connects at all, this only decides WHERE.
        public static HitZone Classify(float h, float legsTop, float bodyTop, float headTop)
        {
            float clamped = math.clamp(h, 0f, headTop);
            if (clamped < legsTop) return HitZone.Legs;
            if (clamped < bodyTop) return HitZone.Body;
            return HitZone.Head;
        }

        /// Damage multiplier for a zone. HitZone.None is neutral (1) — a blow
        /// with no zone behind it (melee, any future zone-less source) deals
        /// exactly what it says.
        public static float MultFor(HitZone zone, float legsMult, float bodyMult, float headMult)
        {
            switch (zone)
            {
                case HitZone.Legs: return legsMult;
                case HitZone.Body: return bodyMult;
                case HitZone.Head: return headMult;
                default: return 1f;
            }
        }

        /// app-88jb T14 (spec 3.3, plan Task T14): which PART of a body a shot
        /// entered, and where. Replaces the Classify/MultFor pair AT THE CALL
        /// SITE (ProjectileSystem.AcceptCandidate) for the two damageable
        /// kinds; those two stay below because the LegsTop/BodyTop/HeadTop
        /// column they are the arithmetic of lives until T15, and their own
        /// unit tests are written on it (coordinator Ruling 66).
        ///
        /// Returns false when the shot passes clear over or under every part
        /// (the caller then rejects the candidate and rescans -- a target
        /// further down the line stays reachable).
        ///
        /// THE HEIGHT GATE DECIDES, t ONLY REFINES (finding C-M4): the parts
        /// are coaxial, so a smaller radius ALWAYS yields a larger t and the
        /// head would always "lose" to the body on t alone. The minimum is
        /// taken only among parts that passed their own height gate.
        ///
        /// THE CONTACT COMES FROM THE WINNING PART, not from the body circle
        /// (finding D-I2): the gather phase's bestT is the entry into the BODY
        /// circle; the part has its own t, and that is what gives the contact
        /// height and therefore the moment arm.
        ///
        /// `t` IS THE WINNING PART'S OWN FRACTION OF THE STEP, in exactly the
        /// parameterization the caller's min-scan uses for its own `bestT` --
        /// the same [p0, p1], so `math.lerp(startPos, target, t)` is the
        /// contact point and nothing has to be re-normalized.
        ///
        /// AND IT IS DECLARED BECAUSE IT HAS A READER, not for symmetry
        /// (coordinator Ruling 73, which OVERRULES Ruling 67). The rule stands
        /// -- an `out` nobody reads is worse than none at all, because it
        /// promises a reader -- and the reader was found: the TWO-DIMENSIONAL
        /// contact of a body hit. ProjectileSystem.Update builds it as
        /// `math.lerp(startPos, target, bestT)` for the mob branch
        /// (ProjectileSystem.cs:259) and for the collector branch (:303), and
        /// `bestT` is the entry into the BODY circle -- the gather phase's
        /// number. Once the HEIGHT of that same blow starts coming from the
        /// winning part, an unchanged XY would leave the event carrying a point
        /// inconsistent with itself: 0.33 m apart on a chaser headshot, which is
        /// exactly (body radius 0.50 - head radius 0.17). This method's own
        /// contract above says THE CONTACT comes from the winning part, not
        /// merely the contact height, so both halves of that point are its
        /// business. The GREEN step is what threads it up through
        /// AcceptCandidate to those two call sites; Ruling 67's verdict, that
        /// nothing reads it, held only for as long as that mismatch went
        /// unnoticed.
        ///
        /// The crown of a body's own model: the top of its LAST part
        /// (app-88jb T14). One home, because both damageable branches of
        /// ProjectileSystem.AcceptCandidate need exactly this number as the
        /// silhouette ceiling they hand to Resolve, and a body's crown written
        /// out twice is a crown that drifts.
        ///
        /// AN EMPTY OR ABSENT STACK ANSWERS 0, NOT A CRASH, and that is the
        /// half this method exists for. `parts[parts.Length - 1]` at the call
        /// site would throw a NullReferenceException on a hand-built fixture
        /// that never filled the array -- inside ProjectileSystem.Update, i.e.
        /// on the hot path, for a body the simulation was merely asked to shoot
        /// at. A zero crown pairs with Resolve's own empty-stack refusal below:
        /// a body that declares no hit volume presents nothing to hit, which is
        /// SimConfigBuilder's own wording for the same rule ("Parts must not be
        /// empty -- a body with no parts cannot be hit at all",
        /// SimConfigBuilder.cs:1935). Every config that went through the
        /// builder is non-empty by validation, so this arm is unreachable in
        /// the game and reachable only from a fixture.
        public static float StackTop(HitPart[] parts)
            => parts == null || parts.Length == 0 ? 0f : parts[parts.Length - 1].Top;

        /// THE ORDER OF THE THREE DECISIONS IS LOAD-BEARING, so it is written
        /// out here rather than left to be read off the loop:
        ///
        ///   1. SILHOUETTE GATE. Does the round's height travel over this step
        ///      reach the body's presented column at all? That column is
        ///      [0, overlapTop] grown by the round's own radius at both ends --
        ///      the same question Overlaps has always answered, asked through
        ///      Overlaps itself. `overlapTop` is NOT the last part's Top in
        ///      general: mid-slide a collector presents SlideProfileTop instead
        ///      (Task 11), which is why the ceiling arrives as an argument
        ///      rather than being read off `parts` here.
        ///   2. EDGE FORGIVENESS, per part and only once that part is known to
        ///      be met rather than cleared: the heights are CLAMPED into
        ///      [0, ceiling]. This is Classify's own clamp into [0, headTop],
        ///      moved here -- a round grazing the crown sits above every band,
        ///      and gating on the raw height would turn the graze step 1 just
        ///      accepted into a miss.
        ///   3. THE PART. Every part whose band meets its own clamped chord is
        ///      a candidate; the winner is the one entered EARLIEST along the
        ///      step.
        ///
        /// EVERY HEIGHT QUESTION IS ASKED OF THE PART'S OWN CHORD, never of the
        /// whole step, and the difference is not academic (implementer's finding
        /// T14-F4, measured). The column version clipped hEnter/hExit to the
        /// BODY circle before judging anything; the natural-looking
        /// generalization -- judge the step's whole height span -- is far more
        /// permissive, because a step is ~1.1 m of travel where a chord through
        /// a 0.29 m circle is 0.58 m, and on a CLIMBING round the step reaches
        /// back to heights the round had well before the target. Measured on
        /// ProjectileHeightTests' own geometry (a chaser screening a gunner at
        /// 6.5 m): to clear a 2.70 m crown the step's span would have to start
        /// above 2.82, which even an aim at the gunner's very crown (4.20 m)
        /// does not achieve -- span [2.69, 3.09] -- while the torso's CHORD
        /// there is [3.09, 3.53] and clears it comfortably. Judging the step
        /// would therefore have made "the round passes over a screening body
        /// and reaches the one behind it" -- the M5 rescan branch this method's
        /// `false` exists to drive -- unreachable for every climbing shot.
        /// The step is still used, but only as a CHEAP NECESSARY CONDITION
        /// (the early-out below): a chord's span is a subset of its step's, so
        /// a part the whole step cannot reach is reachable through no chord.
        ///
        /// BANDS ARE HALF-OPEN [Bottom, Top) EXCEPT THE TOPMOST, whose Top is
        /// inclusive -- HitPart's own contract, and the reason a hit landing
        /// exactly on a boundary belongs to the UPPER part.
        ///
        /// NO PADDING BETWEEN PARTS. Growing each band by the round's radius the
        /// way the silhouette gate grows the column would overlap neighbors by
        /// 2r and hand a borderline headshot to the wide torso -- the opposite
        /// of what this whole task is for. The forgiveness lives at the two ENDS
        /// of the stack (step 2 above) and nowhere in between.
        ///
        /// AN EMPTY STACK PRESENTS NOTHING TO HIT, and answering `false` is the
        /// only answer that cannot invent a hit volume nobody configured. It is
        /// unreachable for any config that went through SimConfigBuilder, whose
        /// own rule says so in the same words ("Parts must not be empty -- a
        /// body with no parts cannot be hit at all", SimConfigBuilder.cs:1935);
        /// what it covers is a hand-built fixture, and for one of those a miss
        /// is the honest answer rather than a NullReferenceException.
        public static bool Resolve(HitPart[] parts, float2 p0, float2 p1, float projRadius,
            float2 targetPos, float hStart, float hEnd, float overlapTop,
            out HitZone zone, out float mult, out float hitHeight, out float t)
        {
            zone = HitZone.None;
            mult = 1f;
            hitHeight = 0f;
            t = 0f;

            if (parts == null || parts.Length == 0) return false;

            int last = parts.Length - 1;
            // THE CROWN ACTUALLY PRESENTED. Normally the model's own, but a
            // collector mid-slide shows SlideProfileTop instead, and T13's
            // validation rule 5 is what makes that expressible here: the
            // profile is required to COINCIDE with a part boundary
            // (SimConfigBuilder.cs:634), so capping at it hides whole parts
            // rather than slicing one in half.
            float ceiling = math.min(parts[last].Top, overlapTop);
            // Cheap necessary condition on the step, before any quadratic: a
            // chord's height span is a SUBSET of its step's, so a body the
            // whole step cannot reach is reachable through no part of it.
            if (!Overlaps(hStart, hEnd, projRadius, ceiling)) return false;

            int winner = -1;
            float bestEnter = 0f, bestHeight = 0f;
            for (int i = 0; i <= last; i++)
            {
                HitPart part = parts[i];
                // THE PART'S OWN RADIUS, and this is the whole change: the
                // column had one half-width for the entire body, so a head was
                // as wide as a pair of shoulders (findings B-I6/D-I2).
                if (!Geometry.SegmentCircleInterval(p0, p1, projRadius, targetPos, part.Radius,
                        out float tEnter, out float tExit)) continue;

                // The heights the round holds WHILE IT IS INSIDE THIS PART'S
                // CIRCLE -- both ends, because the round descends or climbs
                // across the step and can enter one band and leave through the
                // next.
                float hIn = math.lerp(hStart, hEnd, tEnter);
                float hOut = math.lerp(hStart, hEnd, tExit);
                // Clean over the crown, or clean under the feet, of the
                // silhouette this body presents -- the same question, the same
                // radius forgiveness and the same function the column version
                // asked of the body circle.
                if (!Overlaps(hIn, hOut, projRadius, ceiling)) continue;

                // EDGE FORGIVENESS, and only now that the round is known to be
                // against the body rather than clear of it: Classify's own
                // clamp into [0, headTop], moved here. A round grazing the
                // crown sits above every band, and gating on the raw height
                // would turn the graze the line above just accepted into a
                // miss.
                float lo = math.clamp(math.min(hIn, hOut), 0f, ceiling);
                float hi = math.clamp(math.max(hIn, hOut), 0f, ceiling);
                // [Bottom, Top) for every part but the last, whose Top is
                // inclusive -- HitPart's own contract, and the reason a hit
                // exactly on a boundary belongs to the UPPER part.
                if (!(hi >= part.Bottom && (i == last ? lo <= part.Top : lo < part.Top))) continue;

                // EARLIEST ENTRY WINS, AMONG THOSE THAT PASSED THEIR OWN HEIGHT
                // GATE -- never among all of them (finding C-M4). The parts are
                // coaxial, so a smaller radius ALWAYS yields a larger t: on `t`
                // alone the head could never beat the torso, and the height gate
                // is what makes the choice mean anything at all. Strict `<`
                // keeps the LOWER part on an exact tie, which is what two parts
                // of equal radius produce; the boundary rule above has already
                // settled which of two touching bands owns the seam.
                if (winner < 0 || tEnter < bestEnter)
                {
                    winner = i;
                    bestEnter = tEnter;
                    // THE CONTACT COMES FROM THE WINNING PART (finding D-I2):
                    // the gather phase's bestT is the entry into the BODY
                    // circle, this is the entry into the part actually struck.
                    // Carried under the same clamp the gate used, so a graze
                    // reports the crown rather than a height no part of the
                    // body occupies -- Т3's contact height is the moment arm,
                    // and an arm longer than the body is not one.
                    bestHeight = math.clamp(hIn, 0f, ceiling);
                }
            }
            if (winner < 0) return false;

            zone = parts[winner].Zone;
            mult = parts[winner].DamageMult;
            t = bestEnter;
            hitHeight = bestHeight;
            return true;
        }

        /// Does the projectile's height span over its chord through the target
        /// overlap the target's column? The column is [0, top] grown by the
        /// projectile's own `radius` at both ends, so a round is a sphere against
        /// a capsule-ish body rather than a dimensionless point: it connects while
        /// any part of it is inside, and clears the target only once all of it is
        /// past the crown (or under the feet).
        ///
        /// `hEnter`/`hExit` come from the clipped sweep interval, so this is an
        /// interval-vs-interval test — a descending shot that is above the crown
        /// on entry but inside the body on exit still counts as a hit.
        public static bool Overlaps(float hEnter, float hExit, float radius, float top)
        {
            float lo = math.min(hEnter, hExit);
            float hi = math.max(hEnter, hExit);
            return hi >= -radius && lo <= top + radius;
        }
    }
}
