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
        /// Fix-round 1 M-4: when halfW + padR &lt; 0, behaviour is UNDEFINED —
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
            // circle of radius halfW centred on a — SegmentCircle already IS
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
            // behaviourally equivalent to removing the check on every
            // fixture tried (tb's own [0,1] lower bound and the s-interior
            // check downstream already exclude the cases these would). Kept
            // for robustness against inputs neither review nor the test
            // suite has exercised, not because a specific case needs them.
            if (math.abs(d1 - d0) >= 1e-12f)
            {
                float r = halfW + padR;
                // d0 == 0f (path starts exactly ON the band's centreline) is
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
        /// direction-agnostic the way sitting at a circle's centre is —
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
                    // own centre). sideRef = p0 for the on-axis fallback's
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
        /// the inside. The ring is centred on the sim origin, so the outward
        /// radial IS the normalized contact point and the inward one is its
        /// negation; the (1,0) fallback covers a contact exactly at the centre,
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
        /// center, unchanged from the pre-Stage-2 single-player behaviour
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
