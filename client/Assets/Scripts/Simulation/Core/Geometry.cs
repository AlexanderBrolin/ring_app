using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Analytic 2D geometry shared by movement, dash, projectiles and AI LoS.
    public static class Geometry
    {
        public const float Skin = 1e-3f;

        public static bool CircleOverlap(float2 aPos, float aR, float2 bPos, float bR)
        {
            float r = aR + bR;
            return math.lengthsq(bPos - aPos) < r * r;
        }

        /// Swept circle (segment p0→p1, inflated by padR) vs static circle; t ∈ [0,1].
        public static bool SegmentCircle(float2 p0, float2 p1, float padR,
            float2 c, float cR, out float t)
        {
            t = 0f;
            float2 d = p1 - p0;
            float2 f = p0 - c;
            float r = padR + cR;
            float a = math.dot(d, d);
            if (a < 1e-12f) return math.lengthsq(f) < r * r;
            if (math.lengthsq(f) < r * r) return true; // start inside → t=0
            float b = 2f * math.dot(f, d);
            float cc = math.dot(f, f) - r * r;
            float disc = b * b - 4f * a * cc;
            if (disc < 0f) return false;
            float t0 = (-b - math.sqrt(disc)) / (2f * a);
            if (t0 < 0f || t0 > 1f) return false;
            t = t0;
            return true;
        }

        /// Entry AND exit of the swept circle through a static circle, clipped to
        /// the segment: the chord interval [tEnter, tExit] ⊆ [0,1]. Task 6 needs
        /// both ends because a projectile's height changes along the tick, so the
        /// hit-volume test is an interval-vs-interval overlap, not a point test.
        ///
        /// NB: this deliberately re-solves the same quadratic as SegmentCircle
        /// above instead of SegmentCircle delegating to it. SegmentCircle is the
        /// hot path (SweepArena runs it per obstacle per moving body per tick) and
        /// is pinned bit-for-bit by the golden hash; routing it through a
        /// two-root/clamp variant would change its float rounding. The two must be
        /// changed as a pair (QC18).
        public static bool SegmentCircleInterval(float2 p0, float2 p1, float padR,
            float2 c, float cR, out float tEnter, out float tExit)
        {
            tEnter = 0f; tExit = 0f;
            float2 d = p1 - p0;
            float2 f = p0 - c;
            float r = padR + cR;
            float a = math.dot(d, d);
            if (a < 1e-12f)
            {
                // Degenerate sweep (no horizontal motion this tick): the body
                // spends the whole step at p0, so the interval is the full step.
                if (math.lengthsq(f) >= r * r) return false;
                tExit = 1f;
                return true;
            }
            float b = 2f * math.dot(f, d);
            float cc = math.dot(f, f) - r * r;
            float disc = b * b - 4f * a * cc;
            if (disc < 0f) return false;
            float sqrtDisc = math.sqrt(disc);
            float inv = 1f / (2f * a);
            // Clip the chord to the step: a root before the start means the body
            // began inside (enter at 0), a root past the end means it is still
            // inside when the step runs out (exit at 1).
            tEnter = math.max((-b - sqrtDisc) * inv, 0f);
            tExit = math.min((-b + sqrtDisc) * inv, 1f);
            if (tEnter > tExit)
            {
                // The whole chord lies outside [0,1] — the circle is either
                // entirely behind the start or entirely beyond the end.
                tEnter = 0f; tExit = 0f;
                return false;
            }
            return true;
        }

        /// Exit through the ring wall from inside; solves |p0 + d·t| = ringR − padR.
        public static bool SegmentRingWall(float2 p0, float2 p1, float padR,
            float ringR, out float t)
        {
            t = 0f;
            float limit = ringR - padR;
            float2 d = p1 - p0;
            float a = math.dot(d, d);
            if (a < 1e-12f) return false;
            float b = 2f * math.dot(p0, d);
            float c = math.dot(p0, p0) - limit * limit;
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return false;
            float t1 = (-b + math.sqrt(disc)) / (2f * a);
            if (t1 < 0f || t1 > 1f) return false;
            t = t1;
            return true;
        }

        /// The single home for point-to-segment projection (Stage 2 Task 11):
        /// OverlapsStadium and PushOutOfStadium both route through this
        /// instead of re-deriving the clamp math, so it lives in exactly one
        /// place. `s` is always clamped into [0,1] — the parameter along the
        /// segment of the closest point actually returned.
        public static float2 ClosestPointOnSegment(float2 p, float2 a, float2 b, out float s)
        {
            float2 d = b - a;
            float len2 = math.dot(d, d);
            if (len2 < 1e-12f) { s = 0f; return a; } // degenerate segment = a point
            s = math.clamp(math.dot(p - a, d) / len2, 0f, 1f);
            return a + d * s;
        }

        /// Shared stadium-surface normal (fixwave Ф3 item 3, backlog M-4):
        /// PushOutOfStadium and SweepArena's wall branch both derived a
        /// normal from a `point` and its already-projected `closest` point
        /// on the wall's a→b axis the same way — radial delta when the two
        /// differ, falling back to the axis' own perpendicular (oriented by
        /// which side of the axis `sideRef` sits on) when `point` lands
        /// exactly on the axis, and further back to (1,0) only if the axis
        /// itself is degenerate (a == b). The point-to-segment PROJECTION
        /// itself is not duplicated here — that's ClosestPointOnSegment's
        /// job, called once by each caller before this — only the normal
        /// derivation FROM that projection is. `sideRef` lets PushOutOfStadium
        /// and SweepArena each supply their own reference point for the
        /// on-axis fallback's side test (PushOutOfStadium passes `point`
        /// itself, since it has no separate sweep-start point; SweepArena
        /// passes the sweep's own p0) without this function needing to know
        /// which kind of caller it has.
        static float2 StadiumNormal(float2 point, float2 closest, float2 a, float2 b, float2 sideRef)
        {
            float2 delta = point - closest;
            float distSq = math.lengthsq(delta);
            if (distSq > 1e-12f)
            {
                return delta / math.sqrt(distSq);
            }
            float2 axis = b - a;
            float axisLen = math.length(axis);
            if (axisLen > 1e-6f)
            {
                float2 perp = new float2(-axis.y, axis.x) / axisLen;
                float side = math.dot(sideRef - a, perp);
                return side >= 0f ? perp : -perp;
            }
            return new float2(1f, 0f);
        }

        /// Static stadium overlap — segment a→b inflated by halfW (Stage 2
        /// Task 11). Used directly for spawn-clearance rejection, and as
        /// SegmentStadium's "already inside at the start" branch, which also
        /// makes a zero-length sweep (p0 == p1) behave correctly for free.
        public static bool OverlapsStadium(float2 p, float radius, float2 a, float2 b, float halfW)
        {
            float2 closest = ClosestPointOnSegment(p, a, b, out _);
            float r = radius + halfW;
            return math.lengthsq(p - closest) < r * r;
        }

        /// Swept circle (segment p0→p1, inflated by padR) vs the stadium
        /// a→b/halfW — first contact along the sweep, t ∈ [0,1] (Stage 2
        /// Task 11, spec §3.3). padR is NOT clamped here when negative:
        /// Targeting.HasLineOfFire (Task 13) owns that clamp on its side of
        /// the call, exactly like SegmentCircle above leaves it to its callers.
        /// A contact found exactly AT t == 1 counts as a miss (best starts at
        /// 1f and every candidate must beat it with a strict `&lt;`), matching
        /// SweepArena's convention over this same geometry — whereas
        /// SegmentCircle alone, asked the equivalent question directly at an
        /// end cap, would report `true, t = 1`.
        /// The degenerate-wall branch below is the one exception: it returns
        /// SegmentCircle's verdict verbatim and therefore inherits ITS
        /// convention, reporting a tangent contact at t == 1 as a hit
        /// (pinned by GeometryTests' DegenerateWall_TangentAtSweepEnd). That
        /// path has no production caller — SimConfigBuilder rejects a
        /// zero-length wall — and SweepArena filters t == 1 on its own side,
        /// so the divergence is documented rather than smoothed over.
        /// Fix-round 1 M-4: when halfW + padR &lt; 0, behavior is UNDEFINED —
        /// the end caps (candidates 2/3) work off `|R|` inside SegmentCircle's
        /// own quadratic, while the flat side (candidate 4) works off the
        /// signed `R`, so the two disagree about where the surface even is.
        /// Clamping `padR` to at least `-halfW` is Targeting.HasLineOfFire's
        /// job (Task 13), not this function's.
        public static bool SegmentStadium(float2 p0, float2 p1, float padR,
            float2 a, float2 b, float halfW, out float t)
        {
            t = 0f;
            float2 axis = b - a;

            // Degenerate wall (zero-length axis): the stadium collapses to a
            // circle of radius halfW centered on a — SegmentCircle already IS
            // that shape, so delegate rather than duplicate it.
            if (math.lengthsq(axis) < 1e-12f)
                return SegmentCircle(p0, p1, padR, a, halfW, out t);

            // Candidate 1: already inside at the start. Reusing
            // OverlapsStadium here (rather than a bespoke check) also
            // correctly resolves a degenerate sweep p0 == p1, since
            // OverlapsStadium is a pure static test.
            if (OverlapsStadium(p0, padR, a, b, halfW))
            {
                t = 0f;
                return true;
            }

            bool hit = false;
            float best = 1f;

            // Candidates 2 & 3: the two rounded end caps, via the existing,
            // golden-pinned SegmentCircle — the caps ARE circles of radius
            // halfW, not re-derived here.
            if (SegmentCircle(p0, p1, padR, a, halfW, out float tA) && tA < best)
            { best = tA; hit = true; }
            if (SegmentCircle(p0, p1, padR, b, halfW, out float tB) && tB < best)
            { best = tB; hit = true; }

            // Candidate 4: entry into the flat-side band, clipped to the
            // segment via ClosestPointOnSegment.
            float2 dir = axis / math.length(axis);
            float2 n = new float2(-dir.y, dir.x);
            float d0 = math.dot(p0 - a, n);
            float d1 = math.dot(p1 - a, n);
            // The |d1-d0| and tb<=1f checks below are defensive-only, not
            // load-bearing: fix-round 1 review verified both branches are
            // behaviorally equivalent to removing the check on every
            // fixture tried (tb's own [0,1] lower bound and the s-interior
            // check downstream already exclude the cases these would). Kept
            // for robustness against inputs neither review nor the test
            // suite has exercised, not because a specific case needs them.
            if (math.abs(d1 - d0) >= 1e-12f)
            {
                float r = halfW + padR;
                // d0 == 0f (path starts exactly ON the band's centerline) is
                // safe even though math.sign(0f) == 0f makes target == 0:
                // tb then resolves to 0 and contact == p0. When R > 0 (the
                // usual case) candidate 1 above would already have caught p0
                // sitting on the axis WITHIN the segment's own span (its
                // distance to the segment is exactly 0 < r) and returned
                // before reaching here, so d0 == 0 at this point means p0's
                // projection falls OUTSIDE that span — p0's projection is
                // pinned to whichever end it sits nearest, s lands at a
                // boundary, and the s-interior check below rejects it,
                // leaving the end-cap candidates (2/3) to resolve the
                // contact instead. When R == 0 EXACTLY, though — the case
                // Targeting.HasLineOfFire's own clamp produces when it pads a
                // wall all the way down to r = halfW + wallPad = 0 — candidate
                // 1's STRICT `<` never fires even at distance 0, so p0 CAN
                // sit on-axis and INSIDE the segment's span here; s is then
                // genuinely interior, and this candidate correctly resolves
                // it as a t == 0 contact.
                float target = r * math.sign(d0); // approach from p0's own side of the band
                float tb = (target - d0) / (d1 - d0);
                if (tb >= 0f && tb <= 1f)
                {
                    float2 contact = math.lerp(p0, p1, tb);
                    ClosestPointOnSegment(contact, a, b, out float s);
                    // ClosestPointOnSegment always clamps s into [0,1], so a
                    // contact whose RAW projection actually falls outside the
                    // segment shows up here as s pinned exactly to 0 or 1.
                    // Those pinned-to-endpoint cases are end-cap territory:
                    // candidates 2/3 above are guaranteed to already catch
                    // any such point, since its distance to that end is
                    // <= R by construction (spec §3.3's completeness
                    // argument) — so the flat side only claims the strictly
                    // interior case, leaving the boundary to the caps.
                    if (s > 0f && s < 1f && tb < best)
                    { best = tb; hit = true; }
                }
            }

            if (hit) t = best;
            return hit;
        }

        // --- Stage 3 Task 8: which zone a position is in (spec §3.2, Р206) ---

        /// Which of the arena's three concentric rings `pos` falls in — a
        /// PURE function of position and arena.ZoneRadius, called wherever a
        /// zone is needed (wave spawn, loot tier, portal gate) rather than
        /// stored on any entity (see Zone's own doc, SimConfig.cs).
        ///
        /// Boundary convention (task-8-brief's own callout: "the boundary is
        /// a branch, not decoration"): a position exactly ON a boundary
        /// belongs to the INNER (smaller-radius) zone, which is why both
        /// comparisons below are `&lt;=`, not this file's usual strict `&lt;`
        /// "inside" idiom (CircleOverlap et al.) — a plain `&lt;` would push a
        /// point exactly on ZoneRadius[0] past the Core check and into
        /// Middle instead. ZoneConfigTests.ZoneOf_OnCoreBoundary_BelongsToCore
        /// and .ZoneOf_OnMiddleBoundary_BelongsToMiddle pin the two
        /// boundaries independently, as two separate test methods (not one
        /// with two sequential asserts) — NUnit stops a method at its first
        /// failed assertion, so sharing one method would let a mutation on
        /// r0 mask whether r1 is guarded at all in the same run.
        public static Zone ZoneOf(float2 pos, in ArenaSimConfig arena)
        {
            float distSq = math.lengthsq(pos);
            if (distSq <= arena.ZoneRadius[0] * arena.ZoneRadius[0]) return Zone.Core;
            if (distSq <= arena.ZoneRadius[1] * arena.ZoneRadius[1]) return Zone.Middle;
            return Zone.Outer;
        }

        // --- Stage 3 Task 7: the arc barrier (spec §3.2, Р246/Р247) ---
        //
        // An arc is the ring of radius `ringR` and half-width `halfW` with an
        // angular cutout per door, each cutout's corners closed off by a jamb
        // circle of radius `halfW` — the same construction a stadium uses
        // (a body plus rounded ends), wrapped around a circle instead of a
        // segment. Every piece of arithmetic below is one of the existing
        // primitives; the only genuinely new thing is the ANGULAR test.

        /// Angular HALF-extent of a door's cutout. `doorFreeWidth` is the FREE
        /// width in metres (ledger R-26), and each corner of the cutout is
        /// filled back in by a jamb circle of radius halfW, so the cutout
        /// itself measures `freeWidth + 2*halfW` of arc at radius `ringR` and
        /// what actually passes between the two jambs is `freeWidth`.
        ///
        /// Public since Stage 3 Task 8 (coordinator ledger, plan-vs-brief
        /// gap fix): SimConfigBuilder.ValidateZoneWalls reuses this exact
        /// formula for two rules — a wall's doors must not overlap and
        /// must not together occupy half the ring or more — rather than
        /// restating "(freeWidth + 2*halfW) / R" a second time (rule 2,
        /// reuse > duplication). No behavior change to any existing caller.
        public static float DoorHalfCutout(float freeWidth, float ringR, float halfW)
            => (freeWidth + 2f * halfW) / (2f * ringR);

        /// Center of one of a door's two jamb circles: on radius `ringR`, at a
        /// corner of the cutout. `side` is +1 (counter-clockwise corner) or -1.
        static float2 JambCenter(float doorCenter, float freeWidth, float ringR,
            float halfW, float side)
        {
            float angle = doorCenter + side * DoorHalfCutout(freeWidth, ringR, halfW);
            return new float2(math.cos(angle), math.sin(angle)) * ringR;
        }

        /// Wraps an angular delta into (-pi, pi]. Public since Stage 3 Task 8
        /// (coordinator ledger): SimConfigBuilder.ValidateZoneWalls' door-
        /// overlap rule needs the identical wrap to compare two door
        /// centers' angular distance — pulled out of InDoorCutout's own
        /// inline formula rather than restated a second time (rule 2). No
        /// behavior change: InDoorCutout below now calls this instead of
        /// computing the same two lines itself.
        public static float WrapAngle(float delta)
        {
            const float twoPi = 2f * math.PI;
            return delta - twoPi * math.round(delta / twoPi);
        }

        /// Is `angle` inside some door's cutout? The one new test in the whole
        /// primitive (spec §3.2's own "what is written fresh" list).
        /// `doorCenter` comes from config in whatever winding the arena was
        /// authored in (Stage 3 lands doors at 90/210/330°), while math.atan2
        /// answers in [-π, π] — so the difference is wrapped before it is
        /// compared, or a door past π would read as solid wall.
        static bool InDoorCutout(float angle, float ringR, float halfW,
            System.ReadOnlySpan<float> doorCenter, System.ReadOnlySpan<float> doorFreeWidth)
        {
            for (int j = 0; j < doorCenter.Length; j++)
            {
                float delta = WrapAngle(angle - doorCenter[j]);
                if (math.abs(delta) < DoorHalfCutout(doorFreeWidth[j], ringR, halfW))
                    return true;
            }
            return false;
        }

        /// Does the circle (p, radius) reach the ring's band, doors ignored?
        /// `lengthsq` against a squared radius is this file's canonical radial
        /// test — CircleOverlap, SegmentCircle, SegmentCircleInterval,
        /// OverlapsStadium and PushOutOfCircle all state it exactly this way,
        /// and spec Р206 prescribes the same idiom for ZoneOf. Reusing the
        /// idiom is not duplicating logic; routing it through a mutating
        /// primitive (ClampInsideRing writes to pos and normal) used as a
        /// predicate would be worse on every count.
        ///
        /// The inner limit is clamped at 0 for the degenerate `radius + halfW
        /// >= ringR` config — a body that could never fit inside the ring at
        /// all — where the hole in the middle simply does not exist.
        static bool InArcBand(float2 p, float radius, float ringR, float halfW)
        {
            float outer = ringR + halfW + radius;
            float inner = math.max(ringR - halfW - radius, 0f);
            float distSq = math.lengthsq(p);
            return distSq < outer * outer && distSq > inner * inner;
        }

        /// Which face of the ring a point in the band belongs to: the outward
        /// radial on the outer half, the inward one (RingWallNormal itself) on
        /// the inner half. Shared by SegmentArc's contact normal and
        /// PushOutOfArc's face choice, so a contact and the depenetration that
        /// follows it can never disagree about which side the body is on.
        static float2 ArcFaceNormal(float2 p, float ringR)
            => math.lengthsq(p) >= ringR * ringR ? -RingWallNormal(p) : RingWallNormal(p);

        /// One candidate contact with the ring's BODY, kept only if it is
        /// nearer than the best so far AND its angle is outside every door.
        static void TryBandContact(float2 p0, float2 p1, float candidate, float ringR, float halfW,
            System.ReadOnlySpan<float> doorCenter, System.ReadOnlySpan<float> doorFreeWidth,
            ref float best, ref bool hit, ref float2 normal)
        {
            if (candidate >= best) return;
            float2 contact = math.lerp(p0, p1, candidate);
            if (InDoorCutout(math.atan2(contact.y, contact.x), ringR, halfW,
                    doorCenter, doorFreeWidth)) return;
            best = candidate; hit = true;
            normal = ArcFaceNormal(contact, ringR);
        }

        /// One candidate contact with a jamb — a circle of radius halfW, so
        /// the golden-pinned SegmentCircle answers it directly, exactly as
        /// SegmentStadium's rounded end caps do. Its "start inside → t = 0"
        /// branch is CORRECT here (unlike against the ring's own bounding
        /// circles): a body starting inside the corner really is in contact.
        static void TryJambContact(float2 p0, float2 p1, float padR, float2 jamb, float halfW,
            ref float best, ref bool hit, ref float2 normal)
        {
            if (!SegmentCircle(p0, p1, padR, jamb, halfW, out float candidate)
                || candidate >= best) return;
            best = candidate; hit = true;
            // Radial from the jamb's OWN center, the same normal SweepArena
            // derives at a stadium's cap and at an obstacle circle.
            normal = math.normalizesafe(math.lerp(p0, p1, candidate) - jamb, new float2(1f, 0f));
        }

        /// Static overlap of the circle (p, radius) with an arc barrier: used
        /// for spawn-clearance rejection and config validation, and as the
        /// shape the two functions below are stated against.
        public static bool OverlapsArc(float2 p, float radius, float ringR, float halfW,
            System.ReadOnlySpan<float> doorCenter, System.ReadOnlySpan<float> doorFreeWidth)
        {
            if (InArcBand(p, radius, ringR, halfW)
                && !InDoorCutout(math.atan2(p.y, p.x), ringR, halfW, doorCenter, doorFreeWidth))
                return true;

            // Р246: the corners are jamb CIRCLES, not an angular pad. A body in
            // the doorway sits radially inside the band and, drifting sideways,
            // crosses neither bounding circle — without the jambs it ends up
            // inside the wall, to be pushed out of an arbitrary side of it.
            for (int j = 0; j < doorCenter.Length; j++)
            {
                if (CircleOverlap(p, radius,
                        JambCenter(doorCenter[j], doorFreeWidth[j], ringR, halfW, +1f), halfW))
                    return true;
                if (CircleOverlap(p, radius,
                        JambCenter(doorCenter[j], doorFreeWidth[j], ringR, halfW, -1f), halfW))
                    return true;
            }
            return false;
        }

        /// First contact of the swept circle (p0→p1, inflated by padR) with the
        /// arc barrier, t ∈ [0,1], plus the surface normal there. Candidate
        /// protocol identical to SegmentStadium's: `best` starts at 1 and every
        /// candidate must beat it with a strict `&lt;`, so a contact exactly at
        /// t == 1 counts as a miss — the convention SweepArena applies over
        /// this same geometry.
        ///
        /// The body of the ring is resolved as the outer interval MINUS the
        /// inner one (spec §3.2, finding B-I2) rather than by calling
        /// SegmentCircle against the bounding circles: SegmentCircle's "start
        /// inside → t = 0" branch (:26) would report a contact in its own
        /// point for every body standing anywhere INSIDE the ring, and
        /// Targeting.HasLineOfFire — which has no side dispatcher — would call
        /// every ray from inside a zone blocked, doorways included.
        public static bool SegmentArc(float2 p0, float2 p1, float padR, float ringR, float halfW,
            System.ReadOnlySpan<float> doorCenter, System.ReadOnlySpan<float> doorFreeWidth,
            out float t, out float2 normal)
        {
            t = 0f; normal = float2.zero;
            bool hit = false;
            float best = 1f;

            if (SegmentCircleInterval(p0, p1, padR, float2.zero, ringR + halfW,
                    out float enterOuter, out float exitOuter))
            {
                // ⚠ THE SIGN IS LOAD-BEARING. This asks "when is the body
                // ENTIRELY inside the core", i.e. |p| < (ringR - halfW) - padR,
                // and SegmentCircleInterval solves for r = padR + cR — so the
                // core's own interval takes -padR. With +padR it would solve a
                // circle padR wider than the hole and report the wrong face
                // (pinned by ZoneGeometryTests mutation M2). Neither the spec
                // nor the plan states the sign; the geometry does.
                bool core = SegmentCircleInterval(p0, p1, -padR, float2.zero, ringR - halfW,
                    out float enterCore, out float exitCore);

                // The two ways INTO the band, in ascending t: crossing the
                // outer face inward, and leaving the core through the inner
                // face. The matching exits are deliberately NOT candidates — a
                // body sliding out of a doorway would otherwise be reported as
                // hitting the wall it just left.
                if (!core || enterOuter < enterCore)
                    TryBandContact(p0, p1, enterOuter, ringR, halfW,
                        doorCenter, doorFreeWidth, ref best, ref hit, ref normal);
                if (core && exitCore <= exitOuter)
                    TryBandContact(p0, p1, exitCore, ringR, halfW,
                        doorCenter, doorFreeWidth, ref best, ref hit, ref normal);
            }

            // A body already inside the ring's body needs no branch of its own:
            // its outer interval is clipped to enterOuter == 0, and the angular
            // test at p0 either accepts that contact or hands the step to the
            // jambs below — which is exactly what a body in a doorway wants.
            for (int j = 0; j < doorCenter.Length; j++)
            {
                TryJambContact(p0, p1, padR,
                    JambCenter(doorCenter[j], doorFreeWidth[j], ringR, halfW, +1f), halfW,
                    ref best, ref hit, ref normal);
                TryJambContact(p0, p1, padR,
                    JambCenter(doorCenter[j], doorFreeWidth[j], ringR, halfW, -1f), halfW,
                    ref best, ref hit, ref normal);
            }

            if (hit) t = best;
            return hit;
        }

        public static bool PushOutOfCircle(ref float2 pos, float radius,
            float2 c, float cR, out float2 normal)
        {
            normal = float2.zero;
            float2 delta = pos - c;
            float r = radius + cR;
            float distSq = math.lengthsq(delta);
            if (distSq >= r * r) return false;
            float dist = math.sqrt(distSq);
            normal = dist > 1e-6f ? delta / dist : new float2(1f, 0f);
            pos = c + normal * (r + Skin);
            return true;
        }

        /// Mirrors PushOutOfCircle for the stadium shape (Stage 2 Task 11).
        /// The degenerate-normal fallback differs from PushOutOfCircle's
        /// plain (1,0): sitting exactly on the wall's axis is not
        /// direction-agnostic the way sitting at a circle's center is —
        /// pushing along the axis would slide the body along the wall
        /// instead of clearing it, and Depenetrate's next iteration would
        /// find it still penetrating. The fallback is instead the axis' own
        /// perpendicular, falling back further to (1,0) only when the wall
        /// itself is degenerate (a == b), matching PushOutOfCircle in that
        /// corner case.
        public static bool PushOutOfStadium(ref float2 pos, float radius,
            float2 a, float2 b, float halfW, out float2 normal)
        {
            normal = float2.zero;
            float2 closest = ClosestPointOnSegment(pos, a, b, out _);
            float r = radius + halfW;
            float distSq = math.lengthsq(pos - closest);
            if (distSq >= r * r) return false;
            // sideRef = pos: PushOutOfStadium has no separate sweep-start
            // point to offer the on-axis fallback's side test, so it hands
            // StadiumNormal the same point it's already pushing — which the
            // on-axis case makes ~coincident with `closest` anyway, landing
            // `side` AT OR EXTREMELY CLOSE TO zero (not necessarily exactly
            // 0 in general — two independently-rounded divisions can leave a
            // residue on the order of 1e-7 * |axis|), taking the `>= 0f`
            // branch to +perp (fix-round 1 M-7's pinned choice, verified
            // numerically unchanged by this refactor). Either sign is a
            // correct outward perpendicular this close to the axis — the
            // point is effectively ON it either way.
            normal = StadiumNormal(pos, closest, a, b, pos);
            pos = closest + normal * (r + Skin);
            return true;
        }

        /// Depenetration out of the arc barrier (Stage 3 Task 7): the NEARER
        /// face for a body buried in the ring's own body, the jamb's own radial
        /// for one wedged into a door's corner. Both branches are the existing
        /// primitives — PushOutOfCircle outward through `ringR + halfW`,
        /// ClampInsideRing inward against `ringR - halfW`, PushOutOfCircle
        /// again for the jamb (spec §3.2's reuse table).
        ///
        /// Picking the nearer face is what keeps a barrier a barrier: a
        /// side-blind radial push would spit a body out of whichever side the
        /// arithmetic happened to land on, handing out a free zone transition
        /// through a wall the match is built around. A body in the DOORWAY is
        /// not penetrating anything and must not be moved at all.
        ///
        /// ASSUMPTION, not a checked precondition: `radius + halfW &lt; ringR` —
        /// a body that can actually fit inside the ring. Under it the band test
        /// and the two primitives agree about what "inside" means, so whichever
        /// push is chosen fires. Violate it and behavior is UNDEFINED, the same
        /// way SegmentStadium's own negative-pad case is: ClampInsideRing would
        /// be handed a limit at or below zero, which is not a meaningful clamp.
        /// InArcBand's max(..., 0) only keeps the BAND well-formed there; it
        /// does not make the push sensible, and the two must not be read as one
        /// promise.
        ///
        /// Today the assumption holds by LAYOUT, not by code: zone walls sit at
        /// R = 65 and 92 against bodies of at most 2.2 m. Nothing states it as a
        /// rule — it is absent from the arc's validation list in the spec
        /// (§3.2: zone radii, half-width, door width, non-overlapping doors,
        /// zone reachability, spawn clearance), and this function cannot state
        /// it either, since the arc's numbers reach it from config. Owner of
        /// that gap is TASK 8, which brings zone-wall validation into
        /// SimConfigBuilder.Validate — named here so the debt has one address
        /// rather than none.
        public static bool PushOutOfArc(ref float2 pos, float radius, float ringR, float halfW,
            System.ReadOnlySpan<float> doorCenter, System.ReadOnlySpan<float> doorFreeWidth,
            out float2 normal)
        {
            normal = float2.zero;

            // The InArcBand half of this guard is DEFENSIVE-ONLY, not
            // load-bearing — the same status SegmentStadium's own |d1-d0| and
            // tb<=1f checks carry, and it is documented for the same reason.
            //
            // Why it decides nothing today: each of the two face branches
            // repeats the band's own test inside the primitive it calls
            // (PushOutOfCircle declines at distSq >= (radius + ringR + halfW)^2,
            // ClampInsideRing at distSq <= (ringR - halfW - radius)^2), and the
            // side discriminator below is the SAME comparison as band
            // membership — so whichever side a body is on, the primitive that
            // gets called is exactly the one that will either push it honestly
            // or decline honestly. The jamb loop needs no band test either:
            // jamb circles are inscribed in the band (centers on radius ringR,
            // radius halfW), so overlapping one implies being in it. Verified,
            // not assumed: the Task 7 mutation round replaced this call with
            // `true` and not one verdict in ZoneGeometryTests moved, which is
            // what identified it as an equivalent mutant in the first place.
            //
            // Why it stays: without it, PushOutOfArc's correctness rests on a
            // NON-LOCAL property — two other functions' internal guards plus a
            // geometric fact about where jambs sit. Nothing holds that property
            // in place: no test can see it (every verdict is identical either
            // way) and the compiler cannot either, so a future edit to
            // PushOutOfCircle or ClampInsideRing would break this function
            // silently. The guard states the contract locally: this function
            // acts on bodies that overlap the arc, and on nothing else.
            if (InArcBand(pos, radius, ringR, halfW)
                && !InDoorCutout(math.atan2(pos.y, pos.x), ringR, halfW,
                        doorCenter, doorFreeWidth))
                return math.lengthsq(pos) >= ringR * ringR
                    ? PushOutOfCircle(ref pos, radius, float2.zero, ringR + halfW, out normal)
                    : ClampInsideRing(ref pos, radius, ringR - halfW, out normal);

            // Wedged into a corner of a cutout: angularly the body is IN the
            // doorway, so the faces decline it and only the jamb can free it —
            // the depenetration half of Р246. The push runs along the jamb's
            // own radial, which slides the body into the opening rather than
            // across the ring.
            for (int j = 0; j < doorCenter.Length; j++)
            {
                if (PushOutOfCircle(ref pos, radius,
                        JambCenter(doorCenter[j], doorFreeWidth[j], ringR, halfW, +1f),
                        halfW, out normal))
                    return true;
                if (PushOutOfCircle(ref pos, radius,
                        JambCenter(doorCenter[j], doorFreeWidth[j], ringR, halfW, -1f),
                        halfW, out normal))
                    return true;
            }
            return false;
        }

        public static bool ClampInsideRing(ref float2 pos, float radius,
            float ringR, out float2 normal)
        {
            normal = float2.zero;
            float limit = ringR - radius;
            float distSq = math.lengthsq(pos);
            if (distSq <= limit * limit) return false;
            float dist = math.sqrt(distSq);
            float2 outward = dist > 1e-6f ? pos / dist : new float2(1f, 0f);
            pos = outward * (limit - Skin);
            normal = -outward;
            return true;
        }

        /// Remove the velocity component pointing into the surface.
        public static float2 Slide(float2 vel, float2 normal)
        {
            float into = math.dot(vel, normal);
            return into < 0f ? vel - normal * into : vel;
        }

        public static float2 Rotate(float2 v, float rad)
        {
            float s = math.sin(rad), c = math.cos(rad);
            return new float2(c * v.x - s * v.y, s * v.x + c * v.y);
        }

        /// Rotates `from` towards `to` by at most `maxRad` radians, along the
        /// shorter arc, preserving `from`'s magnitude (direction-only steer —
        /// Task 10 slide steering, QC19). A zero-length `from` or `to` can't
        /// supply a heading, so the other vector (or `from` itself, unchanged)
        /// is returned rather than dividing by zero.
        public static float2 RotateTowards(float2 from, float2 to, float maxRad)
        {
            float lenFrom = math.length(from);
            if (lenFrom < 1e-6f) return from;
            float lenTo = math.length(to);
            if (lenTo < 1e-6f) return from;

            float2 nFrom = from / lenFrom;
            float2 nTo = to / lenTo;
            float cosAngle = math.clamp(math.dot(nFrom, nTo), -1f, 1f);
            float angle = math.acos(cosAngle);
            if (angle <= maxRad) return nTo * lenFrom;

            // Sign of the shorter rotation: a positive 2D cross product means
            // `to` lies counter-clockwise from `from`.
            float cross = nFrom.x * nTo.y - nFrom.y * nTo.x;
            float rad = cross >= 0f ? maxRad : -maxRad;
            return Rotate(nFrom, rad) * lenFrom;
        }

        /// First contact along p0→p1 vs all obstacles, interior walls, and
        /// (optionally) the ring boundary. Returns t ∈ [0,1] and the surface
        /// normal at the contact point. "Wall" means two different things
        /// below (fix-round 1 M-2): the interior stadium walls (WallA/
        /// WallB/WallHalfWidth, Stage 2 Task 12) are always consulted;
        /// `includeWall` gates ONLY the arena's outer ring boundary.
        public static bool SweepArena(float2 p0, float2 p1, float padR,
            in ArenaSimConfig arena, bool includeWall, out float t, out float2 normal)
        {
            t = 1f; normal = float2.zero; bool hit = false;
            for (int o = 0; o < arena.ObstacleCount; o++)
                if (SegmentCircle(p0, p1, padR, arena.ObstaclePos[o],
                        arena.ObstacleRadius[o], out float to) && to < t)
                {
                    t = to; hit = true;
                    normal = math.normalizesafe(
                        math.lerp(p0, p1, to) - arena.ObstaclePos[o], new float2(1f, 0f));
                }

            // Stage 2 Task 12 (spec §3.3): interior walls, traversed AFTER
            // circles and BEFORE the ring boundary below — a fixed order,
            // pinned by SweepArena_TieBreak_CircleBeforeWall (strict
            // `tWall < t` means circles win an exact tie, since they were
            // already compared above).
            for (int wIdx = 0; wIdx < arena.WallCount; wIdx++)
                if (SegmentStadium(p0, p1, padR, arena.WallA[wIdx], arena.WallB[wIdx],
                        arena.WallHalfWidth[wIdx], out float tWall) && tWall < t)
                {
                    t = tWall; hit = true;
                    // SegmentStadium only reports t, not a normal, so rebuild
                    // it the same way PushOutOfStadium already does (Task 11,
                    // fixwave Ф3 item 3: shared via StadiumNormal): project
                    // the contact onto the wall's own segment and take the
                    // direction away from that projection. One formula
                    // covers both the flat side (projection lands in the
                    // interior, delta ⊥ axis) and a rounded cap (projection
                    // lands on an endpoint, delta is radial from that cap's
                    // own center). sideRef = p0 for the on-axis fallback's
                    // side test — fix-round 1 M-7: this branch only runs when
                    // the CONTACT sits exactly on the axis, which forces p0
                    // onto the axis too (the start-inside branch of
                    // SegmentStadium is the only way to reach t == 0 with a
                    // zero delta) — so `side` sits AT OR EXTREMELY CLOSE TO
                    // 0.0f here, not necessarily exactly zero in general
                    // (two independently-rounded divisions can leave a
                    // residue on the order of 1e-7 * |axis|). Either sign is
                    // a CORRECT outward perpendicular this close to the axis
                    // — the point is effectively on it either way — so which
                    // branch of StadiumNormal's ternary fires is not a live
                    // discriminator on today's callers, only documentation
                    // of the intended orientation.
                    float2 contact = math.lerp(p0, p1, tWall);
                    float2 closest = ClosestPointOnSegment(
                        contact, arena.WallA[wIdx], arena.WallB[wIdx], out _);
                    normal = StadiumNormal(contact, closest,
                        arena.WallA[wIdx], arena.WallB[wIdx], p0);
                }

            if (includeWall && SegmentRingWall(p0, p1, padR, arena.Radius, out float tw)
                && tw < t)
            {
                t = tw; hit = true;
                normal = RingWallNormal(math.lerp(p0, p1, tw));
            }
            return hit;
        }

        /// Surface normal of the arena's outer ring boundary at `contact` —
        /// inward, because the only side a body can touch that boundary from is
        /// the inside. The ring is centered on the sim origin, so the outward
        /// radial IS the normalized contact point and the inward one is its
        /// negation; the (1,0) fallback covers a contact exactly at the center,
        /// which a real ring contact cannot be (SweepArena's own branch above
        /// only reaches this for a sweep that crossed |p| = Radius - padR) but
        /// which normalizesafe must be told what to do about anyway.
        ///
        /// Public and shared rather than restated (Stage 2 Task 46): the
        /// projectile gather packs the ring boundary as a candidate of its own
        /// now, and the ProjectileBlocked event it emits has to carry the SAME
        /// normal SweepArena's own ring branch above computes — one home for
        /// the formula is what keeps the two identical instead of promising it.
        public static float2 RingWallNormal(float2 contact)
            => -math.normalizesafe(contact, new float2(1f, 0f));

        /// Iterative depenetration from obstacles, walls, and the ring; slides velocity.
        public static void Depenetrate(ref float2 pos, ref float2 vel, float radius,
            in ArenaSimConfig arena, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                bool any = false;
                for (int o = 0; o < arena.ObstacleCount; o++)
                    if (PushOutOfCircle(ref pos, radius, arena.ObstaclePos[o],
                            arena.ObstacleRadius[o], out float2 n))
                    { vel = Slide(vel, n); any = true; }

                // Stage 2 Task 12 (spec §3.3): interior walls, after circles
                // and before the ring clamp below — the same order as
                // SweepArena above. Loop body mirrors the circle loop exactly.
                for (int wIdx = 0; wIdx < arena.WallCount; wIdx++)
                    if (PushOutOfStadium(ref pos, radius, arena.WallA[wIdx], arena.WallB[wIdx],
                            arena.WallHalfWidth[wIdx], out float2 n))
                    { vel = Slide(vel, n); any = true; }

                if (ClampInsideRing(ref pos, radius, arena.Radius, out float2 wn))
                { vel = Slide(vel, wn); any = true; }
                if (!any) break;
            }
        }

        /// Canonical multiplayer spawn-point formula (Stage 2 Task 4, spec
        /// §3.2, fix-round 1 M-8) — single source of truth shared by
        /// SimulationWorld's constructor and SimConfigBuilder.Validate's
        /// spawn-clearance check (reuse > duplication). Lives here rather
        /// than on SimulationWorld because it is a pure formula with no
        /// dependency on world state, and Ring.Data (the builder's home)
        /// should not have to reach into the stateful world class just to
        /// call a formula. Solo (playerCount <= 1) spawns at the arena
        /// center, unchanged from the pre-Stage-2 single-player behavior
        /// (189 pre-Stage-2 tests depend on it); otherwise index sits on a
        /// ring at Radius * PlayerSpawnRingFrac, evenly spaced by angle, with
        /// no seed-dependent rotation — spawn layout must stay reproducible
        /// across replays regardless of match seed.
        public static float2 SpawnPosFor(int index, int playerCount, in ArenaSimConfig arena)
        {
            if (playerCount <= 1) return float2.zero;
            float angle = index * 2f * math.PI / playerCount;
            float ringRadius = arena.Radius * arena.PlayerSpawnRingFrac;
            return new float2(math.cos(angle), math.sin(angle)) * ringRadius;
        }
    }
}
