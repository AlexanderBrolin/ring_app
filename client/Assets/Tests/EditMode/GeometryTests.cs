using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class GeometryTests
    {
        /// Shared fixture builder (fixwave Ф3 item 4, M-5): a single interior
        /// wall with no obstacles is the ArenaSimConfig shape most of
        /// SweepArena/Depenetrate's wall-only tests need — building it
        /// inline duplicated the same ~8-field object literal across most of
        /// them. The wall's own geometry (wallA/wallB/halfW) and the ring
        /// radius stay literals IN THE CALLING TEST (convention app-n6g
        /// C14, fixtures as literals in the test) — only the ArenaSimConfig
        /// construction itself moves here; TestConfigs is untouched.
        static ArenaSimConfig SingleWallArena(float2 wallA, float2 wallB, float halfW, float radius = 35f)
        {
            return new ArenaSimConfig
            {
                Radius = radius,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { halfW },
            };
        }

        [Test]
        public void SegmentCircle_FastSegment_HitsSmallCircle()
        {
            // 2 m segment "through" a target r=0.5 — sweep must catch it (anti-tunneling)
            bool hit = Geometry.SegmentCircle(new float2(-1f, 0f), new float2(1f, 0f),
                0.1f, new float2(0f, 0f), 0.5f, out float t);
            Assert.IsTrue(hit);
            Assert.That(t, Is.InRange(0f, 0.5f));
        }

        [Test]
        public void SegmentCircle_Miss_ReturnsFalse()
        {
            Assert.IsFalse(Geometry.SegmentCircle(new float2(-1f, 2f), new float2(1f, 2f),
                0.1f, float2.zero, 0.5f, out _));
        }

        [Test]
        public void SegmentCircle_StartInside_HitsAtZero()
        {
            Assert.IsTrue(Geometry.SegmentCircle(float2.zero, new float2(1f, 0f),
                0.1f, float2.zero, 0.5f, out float t));
            Assert.AreEqual(0f, t);
        }

        [Test]
        public void SegmentCircleInterval_ReturnsEnterAndExit()
        {
            bool hit = Geometry.SegmentCircleInterval(
                new float2(-2f, 0f), new float2(2f, 0f), 0f,
                float2.zero, 1f, out float tEnter, out float tExit);
            Assert.IsTrue(hit);
            Assert.AreEqual(0.25f, tEnter, 1e-4f); // enters the circle at x = -1
            Assert.AreEqual(0.75f, tExit, 1e-4f);  // leaves it at x = +1
        }

        [Test]
        public void SegmentCircleInterval_Tangent_EnterEqualsExit()
        {
            // grazes the top of the unit circle: the quadratic has a double root
            bool hit = Geometry.SegmentCircleInterval(
                new float2(-2f, 1f), new float2(2f, 1f), 0f,
                float2.zero, 1f, out float tEnter, out float tExit);
            Assert.IsTrue(hit);
            Assert.AreEqual(tEnter, tExit, 1e-4f);
            Assert.AreEqual(0.5f, tEnter, 1e-4f);
        }

        [Test]
        public void SegmentCircleInterval_StartInside_ClipsEnterToZero()
        {
            bool hit = Geometry.SegmentCircleInterval(
                float2.zero, new float2(2f, 0f), 0f,
                float2.zero, 1f, out float tEnter, out float tExit);
            Assert.IsTrue(hit);
            Assert.AreEqual(0f, tEnter, 1e-6f); // the negative root is clipped away
            Assert.AreEqual(0.5f, tExit, 1e-4f);
        }

        [Test]
        public void SegmentCircleInterval_EndsInside_ClipsExitToOne()
        {
            bool hit = Geometry.SegmentCircleInterval(
                new float2(-2f, 0f), float2.zero, 0f,
                float2.zero, 1f, out float tEnter, out float tExit);
            Assert.IsTrue(hit);
            Assert.AreEqual(0.5f, tEnter, 1e-4f);
            Assert.AreEqual(1f, tExit, 1e-6f);
        }

        [Test]
        public void SegmentCircleInterval_Miss_ReturnsFalse()
        {
            Assert.IsFalse(Geometry.SegmentCircleInterval(
                new float2(-2f, 2f), new float2(2f, 2f), 0f,
                float2.zero, 1f, out _, out _));
        }

        [Test]
        public void SegmentCircleInterval_CircleBeyondSegmentEnd_ReturnsFalse()
        {
            // the whole chord lies at t > 1 — a miss for THIS tick's sweep
            Assert.IsFalse(Geometry.SegmentCircleInterval(
                new float2(-4f, 0f), new float2(-3f, 0f), 0f,
                float2.zero, 1f, out _, out _));
        }

        [Test]
        public void SegmentCircleInterval_PadRadiusWidensTheChord()
        {
            // padding inflates the target circle by padR, exactly like SegmentCircle
            Assert.IsTrue(Geometry.SegmentCircleInterval(
                new float2(-2f, 0f), new float2(2f, 0f), 0.5f,
                float2.zero, 1f, out float tEnter, out float tExit));
            Assert.AreEqual(0.125f, tEnter, 1e-4f); // enters at x = -1.5
            Assert.AreEqual(0.875f, tExit, 1e-4f);  // leaves at x = +1.5
        }

        [Test]
        public void SegmentRingWall_ExitFromInside_Found()
        {
            Assert.IsTrue(Geometry.SegmentRingWall(new float2(34f, 0f), new float2(36f, 0f),
                0.45f, 35f, out float t));
            Assert.That(t, Is.InRange(0f, 1f));
        }

        [Test]
        public void PushOut_SeparatesAndReportsNormal()
        {
            float2 pos = new float2(1.5f, 0f);
            bool pushed = Geometry.PushOutOfCircle(ref pos, 0.5f, float2.zero, 2f, out float2 n);
            Assert.IsTrue(pushed);
            Assert.Greater(math.length(pos), 2.5f);
            Assert.AreEqual(1f, n.x, 1e-3f);
        }

        [Test]
        public void ClampInsideRing_PullsBackAndNormalInward()
        {
            float2 pos = new float2(36f, 0f);
            Assert.IsTrue(Geometry.ClampInsideRing(ref pos, 0.45f, 35f, out float2 n));
            Assert.Less(math.length(pos), 34.56f);
            Assert.AreEqual(-1f, n.x, 1e-3f);
        }

        [Test]
        public void Slide_RemovesOnlyIntoComponent()
        {
            float2 v = Geometry.Slide(new float2(1f, -1f), new float2(0f, 1f));
            Assert.AreEqual(new float2(1f, 0f), v);
            // motion AWAY from the surface is not clipped
            Assert.AreEqual(new float2(1f, 1f), Geometry.Slide(new float2(1f, 1f), new float2(0f, 1f)));
        }

        [Test]
        public void Rotate_QuarterTurn()
        {
            float2 r = Geometry.Rotate(new float2(1f, 0f), math.PI / 2f);
            Assert.AreEqual(0f, r.x, 1e-5f);
            Assert.AreEqual(1f, r.y, 1e-5f);
        }

        [Test]
        public void RotateTowards_WithinMaxAngle_SnapsToTarget()
        {
            // 10-degree gap, 90-degree/step budget: reaches the target exactly.
            float2 from = new float2(1f, 0f);
            float2 to = Geometry.Rotate(from, math.radians(10f));
            float2 r = Geometry.RotateTowards(from, to, math.PI / 2f);
            Assert.AreEqual(to.x, r.x, 1e-4f);
            Assert.AreEqual(to.y, r.y, 1e-4f);
        }

        [Test]
        public void RotateTowards_BeyondMaxAngle_ClampsRotation_PreservesLength()
        {
            // 180-degree flip, clamped to a much smaller per-call budget.
            float2 from = new float2(2f, 0f); // non-unit on purpose: length must survive
            float2 to = new float2(-1f, 0f);
            float maxRad = 0.1f;
            float2 r = Geometry.RotateTowards(from, to, maxRad);

            Assert.AreEqual(math.length(from), math.length(r), 1e-4f); // magnitude preserved
            float angle = math.acos(math.clamp(
                math.dot(math.normalizesafe(from), math.normalizesafe(r)), -1f, 1f));
            Assert.AreEqual(maxRad, angle, 1e-4f); // rotated by exactly the budget, no more
        }

        [Test]
        public void RotateTowards_ZeroLengthInput_ReturnsFromUnchanged()
        {
            Assert.AreEqual(float2.zero, Geometry.RotateTowards(float2.zero, new float2(1f, 0f), 1f));
            float2 from = new float2(1f, 0f);
            Assert.AreEqual(from, Geometry.RotateTowards(from, float2.zero, 1f));
        }

        [Test]
        public void SweepArena_ReportsNearestContactWithNormal()
        {
            var arena = TestConfigs.DefaultArena(); // obstacle (10,4) r=2.2
            bool hit = Geometry.SweepArena(new float2(6f, 4f), new float2(14f, 4f), 0.45f,
                arena, includeWall: false, out float t, out float2 n);
            Assert.IsTrue(hit);
            Assert.That(t, Is.InRange(0f, 1f));
            Assert.Less(n.x, 0f); // normal opposes the direction of travel
        }

        // --- Stage 2 Task 11: stadium wall primitive ---

        [Test]
        public void ClosestPointOnSegment_ClampsToEnds()
        {
            float2 a = new float2(0f, 0f);
            float2 b = new float2(4f, 0f);
            float2 pastB = new float2(10f, 3f);
            float2 pastA = new float2(-5f, 2f);

            float2 closestToB = Geometry.ClosestPointOnSegment(pastB, a, b, out float sB);
            Assert.AreEqual(b, closestToB);
            Assert.AreEqual(1f, sB);

            float2 closestToA = Geometry.ClosestPointOnSegment(pastA, a, b, out float sA);
            Assert.AreEqual(a, closestToA);
            Assert.AreEqual(0f, sA);
        }

        [Test]
        public void ClosestPointOnSegment_DegenerateSegment_ReturnsA()
        {
            float2 a = new float2(3f, -1f);
            float2 degenerateB = a; // zero-length segment
            float2 p = new float2(10f, 10f);

            float2 closest = Geometry.ClosestPointOnSegment(p, a, degenerateB, out float s);

            Assert.AreEqual(a, closest);
            Assert.AreEqual(0f, s);
        }

        [Test]
        public void SegmentStadium_HitsFlatSide()
        {
            // vertical wall axis at x=0, y spans past the crossing point; the
            // segment crosses it head-on along x — a pure flat-side contact.
            float2 a = new float2(0f, -5f);
            float2 b = new float2(0f, 5f);
            float halfW = 0.5f;
            float padR = 0f;
            float2 p0 = new float2(-3f, 0f);
            float2 p1 = new float2(3f, 0f);

            bool hit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float t);

            Assert.IsTrue(hit);
            // flat-side entry at x = -(halfW+padR); segment length (p1.x - p0.x) is 6
            float expected = (3f - (halfW + padR)) / 6f;
            Assert.AreEqual(expected, t, 1e-4f);
        }

        [Test]
        public void SegmentStadium_HitsFlatSide_FromOppositeSide()
        {
            // Fix-round 1 I-1: mirror of HitsFlatSide, approaching from +x
            // instead of -x. All of the plan's flat-side fixtures happened to
            // have d0 > 0 (p0 on the "positive" side of the band); a mutant
            // that hard-codes `target = r` (dropping `* math.sign(d0)`)
            // survives every one of them, because for those it IS the same
            // side. This fixture starts on the opposite (negative-d0) side,
            // so a sign-blind `target` would compute the wrong crossing.
            float2 a = new float2(0f, -5f);
            float2 b = new float2(0f, 5f);
            float halfW = 0.5f;
            float padR = 0f;
            float2 p0 = new float2(3f, 0f);
            float2 p1 = new float2(-3f, 0f);

            bool hit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float t);

            Assert.IsTrue(hit);
            // mirror of HitsFlatSide's expression: same segment length (6),
            // same offset from centre (3) to the inflated band (halfW+padR)
            float expected = (3f - (halfW + padR)) / 6f;
            Assert.AreEqual(expected, t, 1e-4f);
        }

        [Test]
        public void SegmentStadium_HitsRoundedCap()
        {
            // approach aimed past the segment's end, offset to the side: the
            // flat-side strip's projection falls outside [0,1] here, so only
            // the endpoint's rounded cap (via SegmentCircle) can catch it.
            float2 a = new float2(0f, 0f);
            float2 b = new float2(4f, 0f);
            float halfW = 1f;
            float padR = 1.5f;
            float2 p0 = new float2(6f, 2f);
            float2 p1 = new float2(6f, 0f);

            bool hit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float t);

            Assert.IsTrue(hit);
            Assert.That(t, Is.InRange(0f, 1f));
            float2 contact = math.lerp(p0, p1, t);
            float r = halfW + padR;
            Assert.AreEqual(r, math.length(contact - b), 1e-3f); // lands on the end cap, not the flat side
        }

        [Test]
        public void SegmentStadium_HitsRoundedCap_AtStartCap()
        {
            // Fix-round 1 I-2: mirror of HitsRoundedCap, aimed at the a-end
            // instead of the b-end. The single existing rounded-cap test only
            // ever exercises SegmentCircle(..., a, ...); a mutant that drops
            // that candidate entirely leaves the stadium "open" at the a-end
            // and nothing catches it.
            float2 a = new float2(0f, 0f);
            float2 b = new float2(4f, 0f);
            float halfW = 1f;
            float padR = 1.5f;
            float2 p0 = new float2(-2f, 2f);
            float2 p1 = new float2(-2f, 0f);

            bool hit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float t);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.25f, t, 1e-4f);
            float2 contact = math.lerp(p0, p1, t);
            float r = halfW + padR;
            Assert.AreEqual(r, math.length(contact - a), 1e-3f); // lands on the a-end cap
        }

        [Test]
        public void SegmentStadium_NearestCandidateWins_StripOverEndCap()
        {
            // Fix-round 1 I-3 (closes the SegmentStadium half of the
            // honestly-reported "min vs first candidate" gap from Task 11's
            // original report): both the b-end cap (t ~= 0.3557) and the
            // flat-side strip (t = 1/3) are live here, with the strip
            // strictly nearer. Candidates are evaluated end-caps-then-strip,
            // so a "first true candidate wins" mutant returns the b-end cap's
            // later t instead of the strip's earlier one.
            float2 a = new float2(0f, 0f);
            float2 b = new float2(4f, 0f);
            float halfW = 1f;
            float padR = 0f;
            float2 p0 = new float2(3.5f, 3f);
            float2 p1 = new float2(3.5f, -3f);

            bool hit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float t);

            Assert.IsTrue(hit);
            // the strip's own crossing expression: offset (3) to the band
            // (halfW+padR) over the segment's y-span (6) — the SAME shape as
            // HitsFlatSide's expected, just for a different wall/path pair
            float expected = (3f - (halfW + padR)) / 6f;
            Assert.AreEqual(expected, t, 1e-4f);
        }

        [Test]
        public void SegmentStadium_MissesPastEnd()
        {
            // path travels straight past the segment's endpoint, well outside
            // the rounded cap's radius — must miss even though the infinite
            // strip extension (ignoring the endpoint clip) would have caught it.
            //
            // Fix-round 1 M-6: this test is green on Task 11's empty ("always
            // false") RED stub too, so it is NOT a RED-discipline witness by
            // itself (see the Task 11 RED log). Its actual value is killing
            // the mutant that widens the strip's interior test from
            // s ∈ (0,1) to the un-clipped s ∈ [0,1]: without that clip the
            // strip candidate here fires at tb = 1/3 using the b-end's
            // clamped s = 1, which this test catches via Assert.IsFalse.
            float2 a = new float2(0f, 0f);
            float2 b = new float2(4f, 0f);
            float halfW = 1f;
            float padR = 0f;
            float2 p0 = new float2(7f, 3f);
            float2 p1 = new float2(7f, -3f);

            bool hit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float t);

            Assert.IsFalse(hit);
        }

        [Test]
        public void SegmentStadium_MissesPastStart()
        {
            // Fix-round 1 M-1: mirror of MissesPastEnd, clipping at the a-end
            // (s = 0) instead of the b-end (s = 1). MissesPastEnd alone only
            // pins the upper clip (s < 1); a mutant that loosens the lower
            // clip from `s > 0f` to `s >= 0f` survives it untouched, because
            // that fixture's clamped s lands at 1, not 0. Here the strip's
            // raw projection is negative, clamped to exactly s = 0, so only
            // the strict `s > 0f` correctly defers this to the (missing)
            // a-end cap and reports a miss.
            float2 a = new float2(0f, 0f);
            float2 b = new float2(4f, 0f);
            float halfW = 1f;
            float padR = 0f;
            float2 p0 = new float2(-3f, 3f);
            float2 p1 = new float2(-3f, -3f);

            bool hit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float t);

            Assert.IsFalse(hit);
        }

        [Test]
        public void SegmentStadium_DegenerateWall_BehavesAsCircle()
        {
            // Fix-round 1 I-5: |b - a| below the epsilon collapses the
            // stadium to a circle at a (SegmentStadium's own documented
            // fallback). Pins the documented contract as a regression check.
            //
            // Falsifiability note (corrected in scoped re-review): deleting
            // the early-return branch does NOT redden THIS fixture, because
            // with a == b the two end-cap candidates become the same
            // SegmentCircle call and reproduce the branch's answer — the
            // flat-side candidate excludes itself, since a zero-length axis
            // makes its direction NaN and every NaN comparison below is
            // false. That is a property of this fixture, NOT of the branch:
            // the two disagree whenever SegmentCircle reports a contact at
            // exactly t == 1, which the candidate protocol rejects (best
            // starts at 1f, strict `<`) and the delegating branch accepts.
            // DegenerateWall_TangentAtSweepEnd below is that case and kills
            // the mutation. The branch stays for that reason, not only for
            // clarity.
            float2 a = new float2(2f, 3f);
            float2 b = a; // zero-length axis
            float halfW = 1f;
            float padR = 0.2f;
            float2 p0 = new float2(-3f, 3f);
            float2 p1 = new float2(5f, 3f);

            bool stadiumHit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float tStadium);
            bool circleHit = Geometry.SegmentCircle(p0, p1, padR, a, halfW, out float tCircle);

            Assert.IsTrue(circleHit); // sanity: a genuine hit, not a vacuous false==false match
            Assert.AreEqual(circleHit, stadiumHit);
            Assert.AreEqual(tCircle, tStadium, 1e-6f);
        }

        [Test]
        public void SegmentStadium_DegenerateWall_TangentAtSweepEnd_MatchesCircle()
        {
            // Scoped re-review of fix-round 1: the case that actually
            // separates the degenerate-wall branch from the candidate
            // protocol. The sweep runs tangent to the cap and touches it at
            // exactly t == 1 (integer arithmetic: disc = b*b - 4ac = 0), and
            // the two paths disagree there by construction — the candidate
            // protocol treats t == 1 as a miss (best starts at 1f, strict
            // `<`), while the branch hands SegmentCircle's own verdict back
            // unchanged, and SegmentCircle calls it a hit.
            //
            // Pinning the delegating behaviour, not "fixing" it: a
            // degenerate wall is rejected by SimConfigBuilder validation, so
            // this is a defensive path with no production caller, and
            // SweepArena (Stage 2 Task 12) filters t == 1 on its own side
            // anyway. What must not happen silently is the branch being
            // deleted as "redundant" — this test is what makes that visible.
            float2 a = new float2(0f, 0f);
            float2 b = a; // zero-length axis
            float halfW = 1f;
            float padR = 0f;
            float2 p0 = new float2(1f, -5f); // tangent line x = halfW
            float2 p1 = new float2(1f, 0f);  // touches the cap exactly at the end of the sweep

            bool stadiumHit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float tStadium);
            bool circleHit = Geometry.SegmentCircle(p0, p1, padR, a, halfW, out float tCircle);

            Assert.IsTrue(circleHit);          // sanity: the tangent really is a contact
            Assert.AreEqual(1f, tCircle, 1e-6f); // ... and it lands on the sweep's last instant
            Assert.AreEqual(circleHit, stadiumHit);
            Assert.AreEqual(tCircle, tStadium, 1e-6f);
        }

        [Test]
        public void SegmentStadium_StartInside_ReturnsZero()
        {
            float2 a = new float2(0f, -5f);
            float2 b = new float2(0f, 5f);
            float halfW = 1f;
            float padR = 0.2f;

            float2 insideStart = new float2(0.5f, 0f); // within halfW+padR of the axis
            float2 p1 = new float2(5f, 0f);
            bool hit = Geometry.SegmentStadium(insideStart, p1, padR, a, b, halfW, out float t);
            Assert.IsTrue(hit);
            Assert.AreEqual(0f, t);

            // contrast, same axis: starting well clear of the stadium and moving
            // further away must NOT report a start-inside hit — pins that t=0
            // above reflects a genuine inside check, not an unconditional true.
            float2 outsideStart = new float2(20f, 0f);
            float2 p1Away = new float2(25f, 0f);
            Assert.IsFalse(Geometry.SegmentStadium(outsideStart, p1Away, padR, a, b, halfW, out _));
        }

        [Test]
        public void SegmentStadium_NegativePadShrinksContact()
        {
            // negative padR is NOT clamped here (Task 13 clamps on the caller
            // side): R = halfW + padR shrinks the inflated band toward the
            // bare wall, so contact happens later (larger t) than padR=0 would.
            float2 a = new float2(0f, -5f);
            float2 b = new float2(0f, 5f);
            float halfW = 1f;
            float padR = -0.3f;
            float2 p0 = new float2(-3f, 0f);
            float2 p1 = new float2(3f, 0f);

            bool hit = Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float t);

            Assert.IsTrue(hit);
            float expected = (3f - (halfW + padR)) / 6f;
            Assert.AreEqual(expected, t, 1e-4f);
        }

        [Test]
        public void OverlapsStadium_MatchesSweepAtZeroLength()
        {
            // OverlapsStadium and a zero-length SegmentStadium sweep must
            // agree — the latter's "start already inside" branch literally
            // reuses the former.
            float2 a = new float2(-2f, 0f);
            float2 b = new float2(2f, 0f);
            float halfW = 0.75f;
            float radius = 0.25f;
            float2 inside = new float2(0f, 0.9f);  // 0.9 < halfW + radius (1.0)
            float2 outside = new float2(0f, 1f);   // exactly at halfW + radius: strict '<' excludes it

            bool overlapsInside = Geometry.OverlapsStadium(inside, radius, a, b, halfW);
            bool sweptInside = Geometry.SegmentStadium(inside, inside, radius, a, b, halfW, out float tIn);
            Assert.IsTrue(overlapsInside);
            Assert.AreEqual(overlapsInside, sweptInside);
            Assert.AreEqual(0f, tIn);

            bool overlapsOutside = Geometry.OverlapsStadium(outside, radius, a, b, halfW);
            bool sweptOutside = Geometry.SegmentStadium(outside, outside, radius, a, b, halfW, out _);
            Assert.IsFalse(overlapsOutside);
            Assert.AreEqual(overlapsOutside, sweptOutside);
        }

        [Test]
        public void PushOutOfStadium_NormalPerpendicularToSide()
        {
            float2 a = new float2(0f, -5f);
            float2 b = new float2(0f, 5f);
            float halfW = 1f;
            float radius = 0.3f;
            float2 pos = new float2(0.5f, 0f); // inside the band, well clear of both caps

            bool pushed = Geometry.PushOutOfStadium(ref pos, radius, a, b, halfW, out float2 normal);

            Assert.IsTrue(pushed);
            float2 axisDir = math.normalize(b - a);
            Assert.AreEqual(0f, math.dot(normal, axisDir), 1e-4f); // normal is perpendicular to the wall's axis
            Assert.Greater(pos.x, 0f); // pushed further toward the side pos started on
            // Fix-round 1 I-4: pin the actual separation distance (fixture
            // expression), not just its sign/direction — otherwise mutants
            // that push by the wrong amount (no Skin, half the radius, ...)
            // survive on this test.
            Assert.AreEqual(halfW + radius + Geometry.Skin, pos.x, 1e-4f);
        }

        [Test]
        public void PushOutOfStadium_Clear_ReturnsFalseAndLeavesPos()
        {
            // Fix-round 1 I-4: a body already outside the stadium must be
            // left untouched. Without this test, a mutant that always
            // returns true (and always pushes) is caught by nothing — every
            // other PushOutOfStadium test starts already-penetrating.
            float2 a = new float2(0f, -5f);
            float2 b = new float2(0f, 5f);
            float halfW = 1f;
            float radius = 0.3f;
            float2 pos = new float2(10f, 0f); // far outside halfW + radius
            float2 posBefore = pos;

            bool pushed = Geometry.PushOutOfStadium(ref pos, radius, a, b, halfW, out float2 normal);

            Assert.IsFalse(pushed);
            Assert.AreEqual(posBefore, pos);
            Assert.AreEqual(float2.zero, normal);
        }

        [Test]
        public void PushOutOfStadium_OnAxis_PushesPerpendicular()
        {
            // pos sits exactly on the wall's axis: delta = pos - closest is
            // zero, so the coordinator's perpendicular fallback (not a fixed
            // world direction) must apply — pushing along the axis would
            // slide the body along the wall instead of clearing it.
            //
            // Fix-round 1 M-7: reformulated as a property (unit length,
            // orthogonal to the wall's OWN axis, ends up outside the
            // stadium) instead of pinning the exact vector. The wall's axis
            // is deliberately diagonal (not aligned to either world axis):
            // on an axis-aligned wall, +normal and -normal — and so a
            // world-space (1,0) fallback, when the axis happens to be
            // vertical — are equally "orthogonal to the axis" and equally
            // "outside," so no property distinguishes the fallback from the
            // wrong-signed perpendicular. A diagonal axis breaks that
            // symmetry: a mutant that always falls back to (1,0) is no
            // longer orthogonal to THIS wall's axis, so the orthogonality
            // assertion alone catches it (verified below in the
            // falsifiability pass).
            float2 a = new float2(0f, 0f);
            float2 b = new float2(6f, 8f); // length 10, direction (0.6, 0.8)
            float halfW = 1f;
            float radius = 0.5f;
            float2 pos = new float2(3f, 4f); // exactly on the axis (s = 0.5)

            bool pushed = Geometry.PushOutOfStadium(ref pos, radius, a, b, halfW, out float2 normal);

            Assert.IsTrue(pushed);
            Assert.AreEqual(1f, math.length(normal), 1e-5f); // unit length
            float2 axisDir = math.normalize(b - a);
            Assert.AreEqual(0f, math.dot(normal, axisDir), 1e-5f); // orthogonal to the wall's own axis
            Assert.IsFalse(Geometry.OverlapsStadium(pos, radius, a, b, halfW)); // pushed clear of the wall
            // Scoped re-review of fix-round 1: the property assertions above
            // cannot tell the two perpendiculars apart — for a body sitting
            // exactly ON the axis both sides are equally valid physically,
            // and both clear the wall. Which one is chosen is nonetheless
            // observable: once walls carry real data (Stage 2 Task 16) this
            // fallback feeds Depenetrate and therefore the golden hash, so it
            // has to stay DETERMINISTIC. Pinning the documented choice
            // (+perpendicular) restores the mirror-mutant coverage that the
            // property rewrite would otherwise have dropped.
            float2 pinnedChoice = new float2(-axisDir.y, axisDir.x);
            Assert.AreEqual(pinnedChoice.x, normal.x, 1e-5f);
            Assert.AreEqual(pinnedChoice.y, normal.y, 1e-5f);
        }

        [Test]
        public void PushOutOfStadium_DegenerateWall_FallsBackToUnitX()
        {
            // Fix-round 1 I-5: a == b makes even the axis' own perpendicular
            // undefined (no direction to derive), so the further fallback to
            // (1,0) — matching PushOutOfCircle's degenerate-centre case —
            // must apply. The existing OnAxis test exercises the
            // perpendicular-fallback branch only; this is the other branch,
            // reached solely by axisLen <= 1e-6.
            float2 a = new float2(0f, 0f);
            float2 b = a; // degenerate wall: zero-length axis
            float halfW = 0.5f;
            float radius = 0.2f;
            float2 pos = new float2(0f, 0f); // exactly at the degenerate wall's point

            bool pushed = Geometry.PushOutOfStadium(ref pos, radius, a, b, halfW, out float2 normal);

            Assert.IsTrue(pushed);
            Assert.AreEqual(new float2(1f, 0f), normal);
        }

        // --- Stage 2 Task 12: walls in SweepArena/Depenetrate ---

        [Test]
        public void SweepArena_PrefersNearestAcrossKinds()
        {
            // Straight sweep along +x; a circle and a wall both sit on the
            // path. Two arrangements (circle nearer / wall nearer) so a
            // mutant that only checks kind ordering, or only checks one kind,
            // cannot survive on a single side.
            float2 p0 = new float2(-10f, 0f);
            float2 p1 = new float2(10f, 0f);
            float padR = 0f;

            // Case 1: the circle is the nearer obstacle.
            float2 circlePosNear = new float2(2f, 0f);
            float circleRadiusNear = 1f;
            float2 wallAFar = new float2(8f, -5f);
            float2 wallBFar = new float2(8f, 5f);
            float wallHalfWFar = 1f;

            var arenaCircleNear = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 1,
                ObstaclePos = new[] { circlePosNear },
                ObstacleRadius = new[] { circleRadiusNear },
                WallCount = 1,
                WallA = new[] { wallAFar },
                WallB = new[] { wallBFar },
                WallHalfWidth = new[] { wallHalfWFar },
            };

            bool hitCircleNear = Geometry.SweepArena(p0, p1, padR, arenaCircleNear,
                includeWall: false, out float tCircleNear, out float2 nCircleNear);
            Assert.IsTrue(hitCircleNear);

            Geometry.SegmentCircle(p0, p1, padR, circlePosNear, circleRadiusNear,
                out float tExpectedCircle);
            Assert.AreEqual(tExpectedCircle, tCircleNear, 1e-5f);
            float2 contactCircle = math.lerp(p0, p1, tCircleNear);
            float2 expectedNormalCircle = math.normalizesafe(
                contactCircle - circlePosNear, new float2(1f, 0f));
            Assert.AreEqual(expectedNormalCircle.x, nCircleNear.x, 1e-4f);
            Assert.AreEqual(expectedNormalCircle.y, nCircleNear.y, 1e-4f);

            // Case 2: the wall is the nearer obstacle (mirrored distances).
            // Fix-round 1 M-3: the far circle's centre is deliberately OFF
            // the sweep's y = 0 line (unlike Case 1, where centre and sweep
            // share the line). On the line, the "radial from centre" and
            // "wall's flat-side" normals are both purely horizontal, so the
            // normal-based assertions below would pass even under a "walls
            // never checked" mutant (only the t assertion would catch it,
            // which the original comment here wrongly claimed the normal
            // also did). Off the line, the far circle's radial normal has a
            // nonzero y-component, so the normal assertions become a real,
            // independent witness too.
            float2 circlePosFar = new float2(8f, 3f);
            float circleRadiusFar = 4f; // large enough to still reach the y = 0 sweep line
            float2 wallANear = new float2(2f, -5f);
            float2 wallBNear = new float2(2f, 5f);
            float wallHalfWNear = 1f;

            var arenaWallNear = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 1,
                ObstaclePos = new[] { circlePosFar },
                ObstacleRadius = new[] { circleRadiusFar },
                WallCount = 1,
                WallA = new[] { wallANear },
                WallB = new[] { wallBNear },
                WallHalfWidth = new[] { wallHalfWNear },
            };

            bool hitWallNear = Geometry.SweepArena(p0, p1, padR, arenaWallNear,
                includeWall: false, out float tWallNear, out float2 nWallNear);
            Assert.IsTrue(hitWallNear);

            Geometry.SegmentStadium(p0, p1, padR, wallANear, wallBNear, wallHalfWNear,
                out float tExpectedWall);
            Assert.AreEqual(tExpectedWall, tWallNear, 1e-5f);
            // the wall's own (vertical-axis) normal is purely horizontal —
            // if walls were never checked and the far, off-axis circle won
            // instead, its radial normal WOULD have a nonzero y-component
            // (verified: circlePosFar sits off the sweep's y = 0 line), so
            // this assertion is a real, independent witness of "walls
            // checked" — not just the t assertion above.
            Assert.AreEqual(0f, nWallNear.y, 1e-4f);
            Assert.Less(nWallNear.x, 0f);
        }

        [Test]
        public void SweepArena_WallNormal_PerpendicularToSide()
        {
            // Fix-round 1 I-1: the axis MUST be diagonal, not axis-aligned.
            // With an axis-aligned wall and a straight +x approach,
            // "orthogonal to the axis" collapses to "horizontal" — which
            // several WRONG formulas also satisfy by accident:
            // -normalize(p1-p0) (direction of travel, not the surface),
            // perp(axis) applied unconditionally (right answer here, wrong
            // at a rounded cap — see AtCap below), all read as correct on an
            // axis-aligned fixture. Mirrors Task 11 fix-round M-7's own
            // lesson (PushOutOfStadium_OnAxis uses a diagonal axis for
            // exactly this reason). The FULL normal vector is asserted, not
            // just unit-length/orthogonality properties, each side derived
            // independently via the already-pinned Task 11 primitive
            // (ClosestPointOnSegment) rather than copying SweepArena's own
            // formula text — a wrong formula in production (start point
            // instead of contact, travel direction instead of surface) is
            // then an outright numeric mismatch here, not just a weak
            // property that a wrong formula might still happen to satisfy.
            float2 wallA = new float2(0f, 0f);
            float2 wallB = new float2(6f, 8f); // length 10, direction (0.6, 0.8)
            float halfW = 1f;
            float padR = 0f;
            float2 p0 = new float2(-4f, 5f);
            float2 p1 = new float2(6f, 5f);

            var arena = SingleWallArena(wallA, wallB, halfW);

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Geometry.SegmentStadium(p0, p1, padR, wallA, wallB, halfW, out float tExpected);
            Assert.AreEqual(tExpected, t, 1e-5f);

            float2 contact = math.lerp(p0, p1, tExpected);
            float2 closest = Geometry.ClosestPointOnSegment(contact, wallA, wallB, out float s);
            Assert.Greater(s, 0f); Assert.Less(s, 1f); // sanity: flat side, not a rounded cap
            float2 expectedNormal = math.normalize(contact - closest);

            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);
        }

        [Test]
        public void SweepArena_WallNormal_PerpendicularToSide_FromOppositeSide()
        {
            // Fix-round 1 I-1: mirror of PerpendicularToSide above, approaching
            // the SAME diagonal wall from the other side (direct analogue of
            // Task 11's SegmentStadium_HitsFlatSide_FromOppositeSide). Pins
            // the sign, not just the direction: a mutant that gets the side
            // discriminator backwards would pass the main test's side but
            // fail this one, or vice versa.
            float2 wallA = new float2(0f, 0f);
            float2 wallB = new float2(6f, 8f);
            float halfW = 1f;
            float padR = 0f;
            float2 p0 = new float2(10f, 4f);
            float2 p1 = new float2(0f, 4f);

            var arena = SingleWallArena(wallA, wallB, halfW);

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Geometry.SegmentStadium(p0, p1, padR, wallA, wallB, halfW, out float tExpected);
            Assert.AreEqual(tExpected, t, 1e-5f);

            float2 contact = math.lerp(p0, p1, tExpected);
            float2 closest = Geometry.ClosestPointOnSegment(contact, wallA, wallB, out float s);
            Assert.Greater(s, 0f); Assert.Less(s, 1f);
            float2 expectedNormal = math.normalize(contact - closest);

            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);

            // This contact sits on the opposite side of the axis from
            // PerpendicularToSide's (d0 there is positive, here negative —
            // verified numerically), so a mutant with the side discriminator
            // backwards would flip exactly one of the two tests' signs.
            Assert.Greater(normal.x, 0f);
        }

        [Test]
        public void SweepArena_WallNormal_AtCap()
        {
            // Fix-round 1 I-1: contact on a ROUNDED CAP, not the flat side —
            // the docstring claims "one formula covers both the flat side
            // and a rounded cap," but nothing exercised the cap half of that
            // claim. This also kills the "perp(axis) unconditionally"
            // mutant, which PerpendicularToSide (flat side) structurally
            // cannot: for a flat-side hit the correct delta IS parallel to
            // perp(axis), so that mutant is indistinguishable from correct
            // there — only a cap contact (radial from the cap's OWN centre,
            // generally NOT perpendicular to the axis) tells them apart.
            // Fixture reused verbatim from Task 11's own
            // SegmentStadium_HitsRoundedCap (reuse > duplication) — already
            // pinned to land on cap b, not the flat side.
            float2 a = new float2(0f, 0f);
            float2 b = new float2(4f, 0f);
            float halfW = 1f;
            float padR = 1.5f;
            float2 p0 = new float2(6f, 2f);
            float2 p1 = new float2(6f, 0f);

            var arena = SingleWallArena(a, b, halfW);

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Geometry.SegmentStadium(p0, p1, padR, a, b, halfW, out float tExpected);
            Assert.AreEqual(tExpected, t, 1e-5f);

            float2 contact = math.lerp(p0, p1, tExpected);
            float2 closest = Geometry.ClosestPointOnSegment(contact, a, b, out float s);
            Assert.IsTrue(s <= 0f || s >= 1f); // sanity: this really is a cap contact
            float2 expectedNormal = math.normalize(contact - closest); // radial from the cap's own centre

            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);
        }

        [Test]
        public void SweepArena_WallNormal_OnAxisFallback()
        {
            // Body starts exactly on the wall's axis (start-inside branch of
            // SegmentStadium fires, t == 0): the naive delta-based normal is
            // the zero vector, so the perpendicular-to-axis fallback
            // (carryover-t12.md #1) must apply instead of a fixed (1,0),
            // which here would run ALONG the (deliberately diagonal) wall.
            float2 wallA = new float2(0f, 0f);
            float2 wallB = new float2(6f, 8f); // length 10, direction (0.6, 0.8)
            float halfW = 1f;
            float2 p0 = wallA + (wallB - wallA) * 0.3f; // interior point exactly on the axis
            float2 p1 = p0 + new float2(1f, 0f);
            float padR = 0.1f;

            var arena = SingleWallArena(wallA, wallB, halfW);

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(0f, t); // start-inside contact
            float2 axisDir = math.normalize(wallB - wallA);
            Assert.AreEqual(1f, math.length(normal), 1e-4f); // unit length
            Assert.AreEqual(0f, math.dot(normal, axisDir), 1e-4f); // orthogonal — NOT along the wall
            // orthogonality alone already rules out a fixed (1,0) fallback,
            // since (1,0) is not perpendicular to this diagonal axis
            Assert.AreNotEqual(new float2(1f, 0f), normal);
        }

        [Test]
        public void SweepArena_StartInsideWallBand_OffAxis()
        {
            // Fix-round 1 M-8: the most common real "stuck in a wall" shape
            // — start point already inside the band (t == 0, same
            // start-inside branch as OnAxisFallback above) but NOT on the
            // axis, so delta is a genuine nonzero vector and the REGULAR
            // (non-fallback) branch must fire. Complements OnAxisFallback
            // (delta == 0 at t == 0) and PerpendicularToSide (t > 0, reached
            // mid-sweep) — this t == 0 / delta != 0 combination was
            // untouched.
            float2 wallA = new float2(0f, 0f);
            float2 wallB = new float2(6f, 8f);
            float halfW = 1f;
            float padR = 0f;
            // wallA + 0.4 * (wallB - wallA), offset 0.5 along the axis' own
            // perpendicular — inside the band (0.5 < halfW + padR = 1), off-axis.
            float2 p0 = new float2(2f, 3.5f);
            float2 p1 = new float2(3f, 3.5f);

            var arena = SingleWallArena(wallA, wallB, halfW);

            Assert.IsTrue(Geometry.OverlapsStadium(p0, padR, wallA, wallB, halfW)); // fixture precondition

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(0f, t);

            float2 closest = Geometry.ClosestPointOnSegment(p0, wallA, wallB, out _);
            float2 delta = p0 - closest;
            Assert.Greater(math.length(delta), 1e-3f); // sanity: NOT the on-axis fallback case
            float2 expectedNormal = math.normalize(delta);
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);
        }

        [Test]
        public void SweepArena_DegenerateWall_FallsBackToUnitX()
        {
            // Fix-round 1 M-8: the axisLen <= 1e-6 branch (wall collapsed to
            // a single point) is untouched by every other SweepArena wall
            // test — mirrors Task 11's own
            // PushOutOfStadium_DegenerateWall_FallsBackToUnitX.
            float2 wallPoint = new float2(5f, 5f);
            float halfW = 1f;
            float padR = 0f;
            float2 p0 = wallPoint; // starts exactly at the degenerate wall's point
            float2 p1 = wallPoint + new float2(3f, 0f);

            var arena = SingleWallArena(wallPoint, wallPoint, halfW); // degenerate: zero-length axis

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(0f, t);
            Assert.AreEqual(new float2(1f, 0f), normal);
        }

        [Test]
        public void SweepArena_TieBreak_CircleBeforeWall()
        {
            // RED-discipline note (mirrors SegmentStadium_MissesPastEnd's
            // Task 11 precedent): this test is green even against the
            // pre-Task-12 SweepArena, which does not look at walls at all —
            // the circle "wins" trivially by the wall never being a
            // candidate. It is NOT a RED-discipline witness by itself; its
            // real job is killing the "walls before circles" and
            // "tw <= t" mutants (Task 12 report, mutation table), which a
            // pre-implementation baseline can't exercise.
            //
            // A circle and a wall that produce an EXACT tie in t (both
            // divisions reduce to the same exact rational 1/5, correctly
            // rounded to the same float32 bit pattern by IEEE 754 — no
            // tolerance needed on the t comparison). Circles are traversed
            // first with strict `tWall < t`, so on a tie the circle's
            // candidate must win — spec §3.3's fixed traversal order.
            float2 p0 = new float2(0f, 0f);
            float2 p1 = new float2(10f, 0f);
            float padR = 0f;

            float2 circlePos = new float2(5f, 4f);
            float circleRadius = 5f; // sweep at y=0 crosses it at t = 0.2

            float2 wallA = new float2(3f, -5f);
            float2 wallB = new float2(3f, 5f);
            float wallHalfW = 1f; // flat-side crossing also lands at t = 0.2

            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 1,
                ObstaclePos = new[] { circlePos },
                ObstacleRadius = new[] { circleRadius },
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { wallHalfW },
            };

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Geometry.SegmentCircle(p0, p1, padR, circlePos, circleRadius, out float tCircleExpected);
            Geometry.SegmentStadium(p0, p1, padR, wallA, wallB, wallHalfW, out float tWallExpected);
            Assert.AreEqual(tCircleExpected, tWallExpected); // fixture precondition: exact tie
            Assert.AreEqual(tCircleExpected, t); // no tolerance — the tie itself is exact

            // The winning normal must be the CIRCLE's (radial from centre),
            // not the wall's (perpendicular to its axis) — the two are
            // observably different at this contact, which is what actually
            // distinguishes "circle won" from "wall won".
            float2 contact = math.lerp(p0, p1, t);
            float2 expectedNormal = math.normalizesafe(contact - circlePos, new float2(1f, 0f));
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-5f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-5f);
        }

        [Test]
        public void SweepArena_TieBreak_WallBeforeRing()
        {
            // Fix-round 1 I-2: the SAME order pin as TieBreak_CircleBeforeWall
            // above, for the OTHER unpinned boundary — walls vs the ring
            // wall. `includeWall: true` (every production caller passes
            // true; no existing test called SweepArena that way at all — a
            // mutant moving the wall loop below the ring block survived
            // every one of the 253 tests going into this fix-round).
            //
            // The tie is forced through the wall's ROUNDED CAP at wallA, not
            // the flat side: the flat side needs `axis / |axis|` to find its
            // offset band, and for a genuinely diagonal axis that
            // normalization is NOT exact in float32 (verified: it broke an
            // earlier version of this fixture by a couple of ULPs after
            // round-tripping through the ring's own sqrt-based formula). A
            // cap contact goes through SegmentCircle exactly like a bare
            // circle does, with no axis normalization at all — so this
            // reuses the SAME wallA/halfW/Radius literals as
            // TieBreak_CircleBeforeWall's already-Unity-verified exact tie
            // (0x3E4CCCCD both sides), just packaged as a wall's cap instead
            // of a bare circle. wallB sits far off to the side so neither
            // its own cap nor the flat side ever competes for the win.
            float2 p0 = new float2(0f, 0f);
            float2 p1 = new float2(10f, 0f);
            float padR = 0f;

            float2 wallA = new float2(5f, 4f); // same centre/radius as the circle above
            float2 wallB = new float2(5f, 20f); // far away — never the winning candidate
            float wallHalfW = 5f;

            // ring: t = 2/10 = 0.2, same exact-integer chain as above
            var arena = SingleWallArena(wallA, wallB, wallHalfW, radius: 2f);

            Geometry.SegmentStadium(p0, p1, padR, wallA, wallB, wallHalfW, out float tWallExpected);
            Geometry.SegmentRingWall(p0, p1, padR, arena.Radius, out float tRingExpected);
            Assert.AreEqual(tWallExpected, tRingExpected); // fixture precondition: exact tie

            // Sanity: the wall's winning candidate really is the cap at
            // wallA (SegmentCircle), not the flat side or wallB's cap —
            // otherwise the exact-tie precondition above would be fragile.
            Geometry.SegmentCircle(p0, p1, padR, wallA, wallHalfW, out float tCapAExpected);
            Assert.AreEqual(tCapAExpected, tWallExpected);

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: true,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(tWallExpected, t); // no tolerance — the tie itself is exact

            // The winning normal must be the WALL's (spec §3.3: walls before
            // the ring boundary) — observably different from the ring's
            // radial normal at this contact, which is what distinguishes
            // "wall won" from "ring won".
            float2 contact = math.lerp(p0, p1, t);
            float2 closest = Geometry.ClosestPointOnSegment(contact, wallA, wallB, out _);
            float2 expectedNormal = math.normalize(contact - closest);
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-5f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-5f);
        }

        [Test]
        public void SweepArena_MultipleWalls_NearestWins()
        {
            // Fix-round 1 I-3: WallCount > 1 — Task 16 lands six walls, and
            // an indexing bug (constant index, `wIdx < 1`, an array desync
            // between WallA/WallB/WallHalfWidth) would ship wrong-width
            // corridors that only surface in a playtest, not a unit run.
            // The nearer wall is deliberately at index 1, NOT 0, so a
            // "only checks wall 0" mutant picks the wrong one.
            float2 p0 = new float2(-10f, 0f);
            float2 p1 = new float2(10f, 0f);
            float padR = 0f;

            float2 wall0A = new float2(9f, -3f);
            float2 wall0B = new float2(9f, 3f);
            float wall0HalfW = 1f; // index 0, farther

            float2 wall1A = new float2(2f, -5f);
            float2 wall1B = new float2(2f, 5f);
            float wall1HalfW = 0.5f; // index 1, nearer — must win

            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 2,
                WallA = new[] { wall0A, wall1A },
                WallB = new[] { wall0B, wall1B },
                WallHalfWidth = new[] { wall0HalfW, wall1HalfW },
            };

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Geometry.SegmentStadium(p0, p1, padR, wall0A, wall0B, wall0HalfW, out float t0);
            Geometry.SegmentStadium(p0, p1, padR, wall1A, wall1B, wall1HalfW, out float t1);
            Assert.Less(t1, t0); // fixture precondition: wall 1 really is nearer
            Assert.AreEqual(t1, t, 1e-5f);

            float2 contact = math.lerp(p0, p1, t1);
            float2 closest = Geometry.ClosestPointOnSegment(contact, wall1A, wall1B, out _);
            float2 expectedNormal = math.normalize(contact - closest);
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);
        }

        [Test]
        public void Depenetrate_PushesOutOfWall()
        {
            // Same wall fixture as PushOutOfStadium_NormalPerpendicularToSide
            // (Task 11) — reused rather than re-derived — routed through the
            // full Depenetrate loop instead of the bare primitive.
            float2 a = new float2(0f, -5f);
            float2 b = new float2(0f, 5f);
            float halfW = 1f;
            float radius = 0.3f;
            float2 posStart = new float2(0.5f, 0f); // inside the band, clear of both caps
            float2 velStart = new float2(-3f, 2f);  // negative x drives further into the wall

            var arena = SingleWallArena(a, b, halfW); // default Radius 35 — far away, the ring never intervenes here

            // Independent expectation via the already-pinned primitive
            // (Task 11), on a scratch copy — NOT re-deriving the geometry.
            float2 posExpected = posStart;
            bool pushedExpected = Geometry.PushOutOfStadium(ref posExpected, radius, a, b, halfW,
                out float2 normalExpected);
            Assert.IsTrue(pushedExpected);
            float2 velExpected = Geometry.Slide(velStart, normalExpected);

            float2 pos = posStart;
            float2 vel = velStart;
            Geometry.Depenetrate(ref pos, ref vel, radius, arena, 1);

            Assert.AreEqual(posExpected.x, pos.x, 1e-5f);
            Assert.AreEqual(posExpected.y, pos.y, 1e-5f);
            Assert.AreEqual(velExpected.x, vel.x, 1e-5f);
            Assert.AreEqual(velExpected.y, vel.y, 1e-5f);

            // Fixture-expression sanity, independent of PushOutOfStadium's
            // own return: separation distance is radius + halfW + Skin along
            // the wall's perpendicular, and the into-wall velocity component
            // (negative x) is gone.
            Assert.AreEqual(halfW + radius + Geometry.Skin, pos.x, 1e-4f);
            Assert.GreaterOrEqual(vel.x, 0f);
        }

        [Test]
        public void Depenetrate_CircleAndWall_OrderMatters()
        {
            // Fix-round 1 I-2: the OTHER unpinned Depenetrate order — circles
            // before walls. No existing fixture had a body overlapping BOTH
            // a circle and a wall at once, so a "walls before circles"
            // mutant survived every test. The two pushes are non-commutative
            // (each push's landing spot re-enters the OTHER shape's band),
            // so the two orders land on different final positions.
            float radius = 0.3f;
            float2 circleCenter = new float2(0f, 0f);
            float circleRadius = 1f;
            float2 wallA = new float2(1.5f, -5f);
            float2 wallB = new float2(1.5f, 5f);
            float halfW = 0.5f;
            float2 posStart = new float2(0.9f, 0f); // inside both the circle's and the wall's band
            float2 velStart = new float2(-2f, 1f);

            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 1,
                ObstaclePos = new[] { circleCenter },
                ObstacleRadius = new[] { circleRadius },
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { halfW },
            };

            // Independent expectation: circle push, THEN wall push (spec
            // §3.3's fixed order), chaining the already-pinned primitives on
            // a scratch copy.
            float2 posExpected = posStart;
            bool pushedCircle = Geometry.PushOutOfCircle(ref posExpected, radius, circleCenter,
                circleRadius, out float2 circleNormal);
            Assert.IsTrue(pushedCircle);
            float2 velExpected = Geometry.Slide(velStart, circleNormal);

            bool pushedWall = Geometry.PushOutOfStadium(ref posExpected, radius, wallA, wallB, halfW,
                out float2 wallNormal);
            Assert.IsTrue(pushedWall); // sanity: the circle-adjusted position still overlaps the wall
            velExpected = Geometry.Slide(velExpected, wallNormal);

            float2 pos = posStart;
            float2 vel = velStart;
            Geometry.Depenetrate(ref pos, ref vel, radius, arena, 1);

            Assert.AreEqual(posExpected.x, pos.x, 1e-4f);
            Assert.AreEqual(posExpected.y, pos.y, 1e-4f);
            Assert.AreEqual(velExpected.x, vel.x, 1e-4f);
            Assert.AreEqual(velExpected.y, vel.y, 1e-4f);
        }

        [Test]
        public void Depenetrate_MultipleWalls_OrderMatters()
        {
            // Fix-round 1 I-3: WallCount > 1 for Depenetrate too — pushes
            // are non-commutative between two walls just like between a
            // circle and a wall above, so a "wIdx < 1" mutant AND a
            // reversed-traversal mutant both land on the same wrong position
            // here (only wall 0's push applied), distinguishable from the
            // correct sequential wall0-then-wall1 result.
            float radius = 0.3f;
            float2 wall0A = new float2(0f, -5f);
            float2 wall0B = new float2(0f, 5f);
            float wall0HalfW = 1f;
            float2 wall1A = new float2(2.5f, -5f);
            float2 wall1B = new float2(2.5f, 5f);
            float wall1HalfW = 1f;
            float2 posStart = new float2(1f, 0f); // inside wall0's band only, at first
            float2 velStart = new float2(3f, -1f);

            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 2,
                WallA = new[] { wall0A, wall1A },
                WallB = new[] { wall0B, wall1B },
                WallHalfWidth = new[] { wall0HalfW, wall1HalfW },
            };

            float2 posExpected = posStart;
            bool pushed0 = Geometry.PushOutOfStadium(ref posExpected, radius, wall0A, wall0B, wall0HalfW,
                out float2 n0);
            Assert.IsTrue(pushed0);
            float2 velExpected = Geometry.Slide(velStart, n0);

            bool pushed1 = Geometry.PushOutOfStadium(ref posExpected, radius, wall1A, wall1B, wall1HalfW,
                out float2 n1);
            Assert.IsTrue(pushed1); // sanity: wall0's push re-enters wall1's band
            velExpected = Geometry.Slide(velExpected, n1);

            float2 pos = posStart;
            float2 vel = velStart;
            Geometry.Depenetrate(ref pos, ref vel, radius, arena, 1);

            Assert.AreEqual(posExpected.x, pos.x, 1e-4f);
            Assert.AreEqual(posExpected.y, pos.y, 1e-4f);
            Assert.AreEqual(velExpected.x, vel.x, 1e-4f);
            Assert.AreEqual(velExpected.y, vel.y, 1e-4f);
        }

        [Test]
        public void Depenetrate_WallAndRing_BothApplied()
        {
            // Body inside a wall placed right at the arena's edge: after the
            // wall push-out the position also ends up outside the ring, so a
            // SINGLE Depenetrate iteration must apply both pushes in order
            // (walls, then the ring clamp) — spec §3.3's fixed order, mirrored
            // from SweepArena.
            //
            // Scoped re-review item 3 (fixwave Ф3): the wall is TILTED, not
            // vertical. A vertical wall here makes its normal collinear with
            // the ring's radial normal at the contact point, so the wall's
            // own Slide zeroes out the ENTIRE velocity component the ring's
            // Slide would otherwise act on — the ring branch's Slide(vel, wn)
            // call becomes unwitnessed dead code as far as this test can
            // tell (the position clamp still fires and is still checked, but
            // the velocity half of the ring branch is not). The tilt keeps a
            // nonzero, NEGATIVE projection onto the ring's normal alive after
            // the wall's slide, so the ring's own Slide call is an observable
            // part of this test's expectation too.
            float radius = 0.45f;
            float2 wallA = new float2(33.5f, -2f);
            float2 wallB = new float2(34.5f, 2f);
            float halfW = 1f;
            float2 posStart = new float2(34.2f, 0f); // inside the wall band
            // Fix-round 1 M-6: negative dot with the wall's normal so the
            // wall's Slide is not a no-op; the tilt above additionally keeps
            // a negative dot with the ring's normal alive after that slide.
            float2 velStart = new float2(-4f, 4f);

            var arena = SingleWallArena(wallA, wallB, halfW);

            // Independent expectation: chain the two already-pinned
            // primitives (Task 11's PushOutOfStadium, then ClampInsideRing)
            // on a scratch copy, in the order the spec fixes.
            float2 posExpected = posStart;
            bool pushedWall = Geometry.PushOutOfStadium(ref posExpected, radius, wallA, wallB, halfW,
                out float2 wallNormal);
            Assert.IsTrue(pushedWall);
            float2 velAfterWallSlide = Geometry.Slide(velStart, wallNormal);

            bool pushedRing = Geometry.ClampInsideRing(ref posExpected, radius, arena.Radius,
                out float2 ringNormal);
            Assert.IsTrue(pushedRing); // sanity: the wall-adjusted position really is outside the ring

            // The witness for the ring branch's Slide call: this projection
            // must be NEGATIVE (velocity still pointing further past the
            // ring even after the wall's own slide), not merely nonzero — a
            // non-negative projection leaves Slide a no-op whether or not it
            // is actually called, which would make the assertion vacuous.
            float ringProjection = math.dot(velAfterWallSlide, ringNormal);
            Assert.Less(ringProjection, -1e-3f);
            float2 velExpected = Geometry.Slide(velAfterWallSlide, ringNormal);

            float2 pos = posStart;
            float2 vel = velStart;
            Geometry.Depenetrate(ref pos, ref vel, radius, arena, 1); // single iteration

            Assert.AreEqual(posExpected.x, pos.x, 1e-4f);
            Assert.AreEqual(posExpected.y, pos.y, 1e-4f);
            Assert.AreEqual(velExpected.x, vel.x, 1e-4f);
            Assert.AreEqual(velExpected.y, vel.y, 1e-4f);

            // Fix-round 1 I-4: recomputed offline, the body remains INSIDE
            // the wall's band even after both pushes (distance to axis is
            // well under the halfW + radius = 1.45 threshold) — with
            // `iterations: 1` (every production caller), the wall pushes out
            // and the ring pushes straight back in, so this configuration
            // never converges. That is a KNOWN limitation of iterations: 1,
            // not a bug this task fixes: SimConfigBuilder's wall validation
            // (Task 16) is responsible for rejecting a wall this close to the
            // ring boundary. Asserted explicitly here rather than left
            // silent in the fixture.
            float2 closestAfter = Geometry.ClosestPointOnSegment(pos, wallA, wallB, out _);
            Assert.Less(math.length(pos - closestAfter), halfW + radius);
        }

        [Test]
        public void ResolveBodyPair_LighterBodyYieldsMore_ByMassRatio()
        {
            // Test 26: the expectation is a NUMBER (4000/4120 = 0.9709), not
            // "almost all of it" (lesson 428). The overlap is exactly 0.5 m.
            bool hit = Geometry.ResolveBodyPair(
                new float2(0f, 0f), 0.45f, 120f, idA: 1,
                new float2(2.15f, 0f), 2.2f, 4000f, idB: 2,
                out float2 dA, out float2 dB);
            Assert.IsTrue(hit, "перекрытие не распознано");
            float overlap = (0.45f + 2.2f) - 2.15f;
            Assert.AreEqual(-overlap * 4000f / 4120f, dA.x, 1e-4f,
                "сборщик уступил не по отношению масс");
            Assert.AreEqual(overlap * 120f / 4120f, dB.x, 1e-4f,
                "Директор сдвинулся не по отношению масс");
        }

        [Test]
        public void ResolveBodyPair_NoOverlap_ReturnsFalse_AndZeroes()
        {
            bool hit = Geometry.ResolveBodyPair(
                new float2(0f, 0f), 0.5f, 90f, 1,
                new float2(5f, 0f), 0.5f, 90f, 2,
                out float2 dA, out float2 dB);
            Assert.IsFalse(hit);
            Assert.AreEqual(0f, math.length(dA), 1e-6f);
            Assert.AreEqual(0f, math.length(dB), 1e-6f);
        }

        [Test]
        public void ResolveBodyPair_FullOverlap_BreaksTheTieByIdNotByTheXAxis()
        {
            // Test 28, and it pins EXACTLY ONE HALF of the tie-break: that the
            // SIGN of the direction comes from the ids, because swapping them
            // negates both components — exactly, not within the tolerance, since
            // it is one angle multiplied by +1 and by -1.
            //
            // IT DOES NOT PIN THE DIRECTION ITSELF, and an earlier wording of
            // this comment claimed that it did (ruling 111). The forbidden
            // constant (1,0), multiplied by that same antisymmetric sign, flips
            // sign on an id swap too, so it passes all three assertions below —
            // with both y values zero, which quietly degenerates the third one
            // into -0 == -0. The half this test cannot see belongs to
            // ResolveBodyPair_FullOverlap_DifferentIdPairs_PointDifferentWays
            // right below; neither of the two is the whole witness alone.
            Geometry.ResolveBodyPair(float2.zero, 0.5f, 90f, idA: 1,
                float2.zero, 0.5f, 90f, idB: 2, out float2 dA1, out _);
            Geometry.ResolveBodyPair(float2.zero, 0.5f, 90f, idA: 2,
                float2.zero, 0.5f, 90f, idB: 1, out float2 dA2, out _);
            Assert.Greater(math.length(dA1), 0f, "полное перекрытие не разведено вовсе");
            Assert.AreEqual(-dA1.x, dA2.x, 1e-6f, "тай-брейк не зависит от id — это константа");
            Assert.AreEqual(-dA1.y, dA2.y, 1e-6f);
        }

        [Test]
        public void ResolveBodyPair_FullOverlap_DifferentIdPairs_PointDifferentWays()
        {
            // The OTHER half of the tie-break, and the reason it needs a test of
            // its own (ruling 111): the sibling right above,
            // ResolveBodyPair_FullOverlap_BreaksTheTieByIdNotByTheXAxis, pins
            // that the SIGN comes from the ids, and it cannot tell an id-derived
            // DIRECTION from the constant (1,0) wearing that same sign. What
            // separates the two is this — a constant points every pair the SAME
            // way, so two DIFFERENT pairs must point DIFFERENT ways.
            //
            // Both pairs are deliberately ordered idA < idB, so the sign is +1
            // in both calls and cannot contribute to the difference measured
            // below. What is left is the direction alone.
            //
            // THE PREMISES ARE LOAD-BEARING, not decoration: math.normalize of a
            // zero vector is NaN, every comparison against NaN is false, and an
            // unseparated pair would therefore fail the assertion below for the
            // wrong reason instead of reporting what actually went wrong.
            Geometry.ResolveBodyPair(float2.zero, 0.5f, 90f, idA: 1,
                float2.zero, 0.5f, 90f, idB: 2, out float2 dPair12, out _);
            Geometry.ResolveBodyPair(float2.zero, 0.5f, 90f, idA: 1,
                float2.zero, 0.5f, 90f, idB: 3, out float2 dPair13, out _);
            Assert.Greater(math.length(dPair12), 0f, "пара 1-2 не разведена вовсе");
            Assert.Greater(math.length(dPair13), 0f, "пара 1-3 не разведена вовсе");

            // The floor is an ARC, not a round number: two unit directions one
            // degree apart lie 2*sin(0.5 deg) = 0.01745 from each other, and
            // anything closer than a degree is not a tie-break telling two pairs
            // apart, it is rounding. 0.017 is that chord rounded DOWN, so the
            // literal stays a strict floor. The shipped pair clears it 17-fold
            // (the distance is 0.2956); a constant direction scores exactly 0.
            //
            // The expectation is the RULE, not a recomputation of the formula
            // (lesson 428): pinning 0.2956 itself would pin the particular angle
            // function, and that is the implementer's choice, not the contract.
            float apart = math.distance(math.normalize(dPair12), math.normalize(dPair13));
            Assert.Greater(apart, 0.017f,
                "разные пары id разошлись в одну сторону — направление не зависит от id");
        }

        [Test]
        public void ResolveBodyPair_ExactTouch_IsNotAnOverlap_Guard()
        {
            // A GUARD, GREEN ON TODAY'S CODE (lesson 427), and named one rather
            // than passed off as a witness: the gate is `overlap <= 0`, so a
            // contact at exactly one point is NOT an overlap and has to be
            // declined. It earns its place against the mutant that relaxes that
            // gate to `< 0` — a mutant the three tests above all survive, their
            // overlaps being 0.5, -4 and 1.0, not one of them on the border.
            // The border is introduced by this very task, so its witness is
            // written here rather than left to the caller (rulings 81/82, 37).
            //
            // THE FIXTURE IS EXACTLY REPRESENTABLE IN FLOAT ON PURPOSE: 0.5 and
            // 1.0 are powers of two, so rA + rB and the distance are both 1.0
            // bit for bit and the overlap is exactly 0f. A border witness built
            // on rounded numbers would be measuring the rounding, not the gate.
            bool hit = Geometry.ResolveBodyPair(
                new float2(0f, 0f), 0.5f, 90f, idA: 1,
                new float2(1f, 0f), 0.5f, 90f, idB: 2,
                out float2 dA, out float2 dB);
            Assert.IsFalse(hit, "касание ровно в точку принято за перекрытие");
            Assert.AreEqual(0f, math.length(dA), 1e-6f, "касание сдвинуло первое тело");
            Assert.AreEqual(0f, math.length(dB), 1e-6f, "касание сдвинуло второе тело");
        }
    }
}
