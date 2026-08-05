using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class GeometryTests
    {
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
            // cannot survive on a single side (context: "обе стороны").
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
            float2 circlePosFar = new float2(8f, 0f);
            float circleRadiusFar = 1f;
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
            // if the circle had won instead, the far circle's radial normal
            // at this contact would not be (which is what actually catches a
            // "walls never checked" mutant here)
            Assert.AreEqual(0f, nWallNear.y, 1e-4f);
            Assert.Less(nWallNear.x, 0f);
        }

        [Test]
        public void SweepArena_WallNormal_PerpendicularToSide()
        {
            // Flat-side contact: normal must be orthogonal to the wall's
            // axis and point toward the side the body approaches from — the
            // exact vector that MoveWithCollisions uses for
            // `pos = hitPoint + n * Skin`.
            float2 p0 = new float2(-5f, 0f);
            float2 p1 = new float2(5f, 0f);
            float padR = 0.2f;
            float2 wallA = new float2(0f, -5f);
            float2 wallB = new float2(0f, 5f);
            float halfW = 0.5f;

            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { halfW },
            };

            bool hit = Geometry.SweepArena(p0, p1, padR, arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            float2 axisDir = math.normalize(wallB - wallA);
            Assert.AreEqual(1f, math.length(normal), 1e-4f); // unit length
            Assert.AreEqual(0f, math.dot(normal, axisDir), 1e-4f); // orthogonal to the axis
            Assert.Less(normal.x, 0f); // p0 approaches from the -x side
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

            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { halfW },
            };

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

            var arena = new ArenaSimConfig
            {
                Radius = 35f, // far away — the ring never intervenes here
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { a },
                WallB = new[] { b },
                WallHalfWidth = new[] { halfW },
            };

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
        public void Depenetrate_WallAndRing_BothApplied()
        {
            // Body inside a wall placed right at the arena's edge: after the
            // wall push-out the position also ends up outside the ring, so a
            // SINGLE Depenetrate iteration must apply both pushes in order
            // (walls, then the ring clamp) — spec §3.3's fixed order, mirrored
            // from SweepArena.
            float radius = 0.45f;
            float2 wallA = new float2(34f, -2f);
            float2 wallB = new float2(34f, 2f);
            float halfW = 1f;
            float2 posStart = new float2(34.5f, 0f); // inside the wall band
            float2 velStart = new float2(5f, -3f);

            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { halfW },
            };

            // Independent expectation: chain the two already-pinned
            // primitives (Task 11's PushOutOfStadium, then ClampInsideRing)
            // on a scratch copy, in the order the spec fixes.
            float2 posExpected = posStart;
            bool pushedWall = Geometry.PushOutOfStadium(ref posExpected, radius, wallA, wallB, halfW,
                out float2 wallNormal);
            Assert.IsTrue(pushedWall);
            float2 velExpected = Geometry.Slide(velStart, wallNormal);

            bool pushedRing = Geometry.ClampInsideRing(ref posExpected, radius, arena.Radius,
                out float2 ringNormal);
            Assert.IsTrue(pushedRing); // sanity: the wall-adjusted position really is outside the ring
            velExpected = Geometry.Slide(velExpected, ringNormal);

            float2 pos = posStart;
            float2 vel = velStart;
            Geometry.Depenetrate(ref pos, ref vel, radius, arena, 1); // single iteration

            Assert.AreEqual(posExpected.x, pos.x, 1e-4f);
            Assert.AreEqual(posExpected.y, pos.y, 1e-4f);
            Assert.AreEqual(velExpected.x, vel.x, 1e-4f);
            Assert.AreEqual(velExpected.y, vel.y, 1e-4f);
        }
    }
}
