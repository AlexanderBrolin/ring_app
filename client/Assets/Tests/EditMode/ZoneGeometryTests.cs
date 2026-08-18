using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 7 (bd app-35g, spec §3.2): the ARC barrier primitive — a
    /// ring of radius `ringR` and half-width `halfW` with angular door cutouts
    /// and round jambs at the cutouts' corners. Three new functions
    /// (OverlapsArc / SegmentArc / PushOutOfArc) composed out of the existing,
    /// golden-pinned ones: SegmentCircleInterval for the body of the ring
    /// (never SegmentCircle in the raw — its "start inside → t = 0" branch,
    /// Geometry.cs:26, would report a contact in a body's own point for
    /// anything standing INSIDE the ring, spec §3.2 finding B-I1), SegmentCircle
    /// / PushOutOfCircle for the jambs, PushOutOfCircle / ClampInsideRing for
    /// depenetration, RingWallNormal (and its negation) for the face normals.
    ///
    /// Fixture family, shared by every test below and stated as literals in
    /// each test body (convention app-n6g C14, same as GeometryTests):
    /// ringR = 10, halfW = 1, a body/pad of 0.5, and TWO doors — a decoy at
    /// π/2 (index 0) and the 4 m door every fixture actually exercises at
    /// angle 0 (index 1). The door under test is deliberately the SECOND
    /// element (lesson 227): a `j &lt; 1` / hard-coded-index mutant then dies on
    /// the door tests instead of surviving them.
    ///
    /// This file moves neither golden hash: TestConfigs carries no arcs yet
    /// (ZoneWallCount == 0 until Task 12), so the golden scenarios never
    /// execute a single line of the code under test here — the same relation
    /// WallGeometryTests has to TestConfigs.Open().
    public class ZoneGeometryTests
    {
        /// Angular HALF-extent of a door's cutout (ledger R-26, spec Р246):
        /// `doorFreeWidth` is the FREE width in metres, and each side of the
        /// cutout is closed off by a jamb circle of radius halfW, so the full
        /// cutout measures `freeWidth + 2*halfW` of arc at radius ringR.
        /// A formula, not a fixture — the tests below need to know where the
        /// jambs sit in order to assert a contact lands ON one.
        static float HalfCutout(float freeWidth, float ringR, float halfW)
            => (freeWidth + 2f * halfW) / (2f * ringR);

        /// Center of one of a door's two jamb circles: on radius ringR, at the
        /// corner of the cutout. `side` is +1 (counter-clockwise corner) or -1.
        static float2 JambCenter(float doorCenter, float freeWidth, float ringR,
            float halfW, float side)
        {
            float angle = doorCenter + side * HalfCutout(freeWidth, ringR, halfW);
            return new float2(math.cos(angle), math.sin(angle)) * ringR;
        }

        // --- the two faces of the ring's body ---

        [Test]
        public void ApproachFromOutside_HitsOuterFace()
        {
            float ringR = 10f, halfW = 1f, padR = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f }; // index 1 = the door under test
            float[] doorFreeWidth = { 4f, 4f };
            // Angle π — solid wall, clear of both doors. Straight in along -x.
            float2 p0 = new float2(-14f, 0f);
            float2 p1 = new float2(-9f, 0f);

            Assert.Greater(math.length(p0), ringR + halfW + padR,
                "fixture premise: the sweep starts outside the arc's padded outer face");
            Assert.Less(math.length(p1), ringR + halfW,
                "fixture premise: and ends inside the ring's body, so a contact is mandatory");

            bool hit = Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out float t, out float2 normal);

            Assert.IsTrue(hit);
            // The padded outer face sits at |p| = ringR + halfW + padR; the
            // sweep runs 5 m along -x from x = -14 (fixture expression).
            float expected = (14f - (ringR + halfW + padR)) / 5f;
            Assert.AreEqual(expected, t, 1e-4f);

            float2 contact = math.lerp(p0, p1, t);
            Assert.AreEqual(ringR + halfW + padR, math.length(contact), 1e-4f);

            // The outer face's normal is the OUTWARD radial — RingWallNormal is
            // the inward one (Geometry.cs:459), so this face reports its negation.
            float2 expectedNormal = -Geometry.RingWallNormal(contact);
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);
            Assert.AreEqual(-1f, normal.x, 1e-4f); // ... which on this fixture is (-1, 0)
            Assert.AreEqual(0f, normal.y, 1e-4f);
        }

        [Test]
        public void ApproachFromInside_HitsInnerFace()
        {
            float ringR = 10f, halfW = 1f, padR = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            // Same wall sector (angle π), approached from the middle of the
            // arena instead: the OTHER face, and the case a raw SegmentCircle
            // against the outer bounding circle would answer "t = 0, you are
            // already inside" (lesson 268).
            float2 p0 = new float2(-4f, 0f);
            float2 p1 = new float2(-10f, 0f);

            Assert.Less(math.length(p0), ringR - halfW - padR,
                "fixture premise: the sweep starts clear inside the ring's inner face");

            bool hit = Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out float t, out float2 normal);

            Assert.IsTrue(hit);
            // Padded inner face at |p| = ringR - halfW - padR; the sweep runs 6 m.
            float expected = ((ringR - halfW - padR) - 4f) / 6f;
            Assert.AreEqual(expected, t, 1e-4f);

            float2 contact = math.lerp(p0, p1, t);
            Assert.AreEqual(ringR - halfW - padR, math.length(contact), 1e-4f);

            // Approached from inside, so the surface normal is the INWARD
            // radial — RingWallNormal itself, unnegated.
            float2 expectedNormal = Geometry.RingWallNormal(contact);
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);
            Assert.AreEqual(1f, normal.x, 1e-4f); // ... which on this fixture is (1, 0)
            Assert.AreEqual(0f, normal.y, 1e-4f);
        }

        // --- the door cutout ---

        [Test]
        public void ThroughDoor_NoContact()
        {
            float ringR = 10f, halfW = 1f, padR = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            // Dead down the middle of door 1 (angle 0), from the core out.
            float2 p0 = new float2(4f, 0f);
            float2 p1 = new float2(16f, 0f);

            Assert.IsFalse(Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out _, out _),
                "a body narrower than the door's free width crosses it untouched");

            // Control (the door is a HOLE, not a missing ring): the same sweep
            // 5 m to the side crosses the ring where there is no cutout and must
            // be stopped. Without this the test is green on an "always false"
            // stub and pins nothing (the SegmentStadium_MissesPastEnd lesson).
            float2 asideP0 = new float2(4f, 5f);
            float2 asideP1 = new float2(16f, 5f);
            bool asideHit = Geometry.SegmentArc(asideP0, asideP1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out float asideT, out _);
            Assert.IsTrue(asideHit, "one step to the side of the door the ring is solid");
            float2 asideContact = math.lerp(asideP0, asideP1, asideT);
            Assert.AreEqual(ringR - halfW - padR, math.length(asideContact), 1e-3f,
                "and the contact sits on the padded inner face it reached first");
            Assert.Greater(math.abs(math.atan2(asideContact.y, asideContact.x)),
                HalfCutout(doorFreeWidth[1], ringR, halfW),
                "fixture premise: that contact really is outside the cutout, not inside it");

            // Wrap-around control: a door stated at 1.5π (270°, the form
            // ArenaConfig will carry once Task 12 lands doors at 90/210/330°)
            // must read the same as one at -π/2, which is what math.atan2
            // returns for the sweep below. A missing angle wrap makes this
            // doorway solid.
            float[] wrappedCenter = { 1.5f * math.PI };
            float[] wrappedWidth = { 4f };
            Assert.IsFalse(Geometry.SegmentArc(new float2(0f, -4f), new float2(0f, -16f),
                padR, ringR, halfW, wrappedCenter, wrappedWidth, out _, out _),
                "a door center beyond π must wrap onto atan2's own range");
        }

        // --- the jambs (spec Р246: the hole an angular pad alone would leave) ---

        [Test]
        public void TangentialDriftIntoJamb_HitsJambCircle()
        {
            float ringR = 10f, halfW = 1f, padR = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            // The Р246 shape: a body standing IN the doorway is radially inside
            // the wall's own band, and drifting sideways it crosses NEITHER
            // bounding circle — the interval arithmetic alone reports nothing
            // and the body ends up inside the wall, to be depenetrated out of
            // an arbitrary side. The jamb circle is what actually stops it.
            float2 p0 = new float2(10f, 0f);
            float2 p1 = new float2(10f, 6f);
            float2 jamb = JambCenter(doorCenter[1], doorFreeWidth[1], ringR, halfW, +1f);

            Assert.Less(math.abs(math.length(p0) - ringR), halfW,
                "fixture premise: the drift starts radially INSIDE the wall's band (in the doorway)");
            Assert.Greater(math.length(p0 - jamb), halfW + padR,
                "fixture premise: and clear of the jamb it is about to drift into");

            bool hit = Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out float t, out float2 normal);

            Assert.IsTrue(hit, "the jamb circle closes the corner an angular test alone leaves open");

            // Independent expectation through the already-pinned primitive: the
            // jamb IS a circle of radius halfW, so SegmentCircle answers it.
            Assert.IsTrue(Geometry.SegmentCircle(p0, p1, padR, jamb, halfW, out float expected));
            Assert.AreEqual(expected, t, 1e-5f);

            float2 contact = math.lerp(p0, p1, t);
            Assert.AreEqual(halfW + padR, math.length(contact - jamb), 1e-4f,
                "the contact lands on the jamb's own padded circle");
            Assert.AreEqual(1f, math.length(normal), 1e-4f);

            // And it is genuinely EARLIER than the body's own band candidate:
            // this drift leaves the band through the outer face much later.
            Assert.IsTrue(Geometry.SegmentCircleInterval(p0, p1, padR, float2.zero,
                ringR + halfW, out _, out float bandExit));
            Assert.Greater(bandExit, t,
                "fixture premise: the jamb contact precedes anything the band arithmetic sees");

            // Mirror, into the door's OTHER corner (-1): a "one jamb per door"
            // mutant survives the +1 side above completely untouched.
            float2 mirrorP1 = new float2(10f, -6f);
            float2 mirrorJamb = JambCenter(doorCenter[1], doorFreeWidth[1], ringR, halfW, -1f);
            bool mirrorHit = Geometry.SegmentArc(p0, mirrorP1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out float mirrorT, out _);
            Assert.IsTrue(mirrorHit, "the -1 corner closes the doorway exactly as the +1 one does");
            Assert.IsTrue(Geometry.SegmentCircle(p0, mirrorP1, padR, mirrorJamb, halfW,
                out float mirrorExpected));
            Assert.AreEqual(mirrorExpected, mirrorT, 1e-5f);
        }

        [Test]
        public void JambNormal_PointsAwayFromJambCenter()
        {
            float ringR = 10f, halfW = 1f, padR = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            // Same drift as above — the jamb is a rounded corner, so its normal
            // is radial from the jamb's OWN center, not the ring's (the same
            // rule SweepArena applies at a stadium's end cap).
            float2 p0 = new float2(10f, 0f);
            float2 p1 = new float2(10f, 6f);
            float2 jamb = JambCenter(doorCenter[1], doorFreeWidth[1], ringR, halfW, +1f);

            bool hit = Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out float t, out float2 normal);

            Assert.IsTrue(hit);
            float2 contact = math.lerp(p0, p1, t);
            float2 expectedNormal = math.normalize(contact - jamb);
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);

            // The two things that tell this normal apart from the ring's own
            // face normals here: it is NOT collinear with the radial through
            // the contact, and it opposes the direction of travel.
            float2 radial = math.normalize(contact);
            Assert.Less(math.abs(math.dot(normal, radial)), 0.99f,
                "a jamb normal is not the ring's radial — that is the whole point of the corner");
            Assert.Less(math.dot(normal, math.normalize(p1 - p0)), 0f,
                "and it opposes the direction of travel");
        }

        [Test]
        public void SegmentArc_NearestCandidateWins_JambBeforeFarFace()
        {
            // Coordinator's rule "one mutation per branch": the candidate set of
            // SegmentArc is (band entry | jambs), and only a fixture with TWO
            // live candidates can tell "nearest wins" from "first accepted
            // candidate wins" — the direct analogue of Task 11's own
            // SegmentStadium_NearestCandidateWins_StripOverEndCap.
            //
            // A long chord entering through door 1 slightly off-center: its
            // band entry is inside the cutout (rejected), it clips the door's
            // jamb almost at once, and it would go on to cross the far side of
            // the ring where the wall IS solid. The jamb is strictly nearer, and
            // the far face is a live, accepted candidate — so an implementation
            // that returns the first accepted candidate in a fixed order
            // (band before jambs) answers the far face instead.
            float ringR = 10f, halfW = 1f, padR = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            float2 p0 = new float2(12.3f, 2.1f);
            float2 p1 = new float2(-10f, 6f);
            float2 jamb = JambCenter(doorCenter[1], doorFreeWidth[1], ringR, halfW, +1f);

            // Premise 1: the band entry lands inside the cutout and is rejected.
            Assert.IsTrue(Geometry.SegmentCircleInterval(p0, p1, padR, float2.zero,
                ringR + halfW, out float bandEnter, out _));
            float2 bandEntryPoint = math.lerp(p0, p1, bandEnter);
            Assert.Less(math.abs(math.atan2(bandEntryPoint.y, bandEntryPoint.x)),
                HalfCutout(doorFreeWidth[1], ringR, halfW),
                "fixture premise: the chord enters the ring THROUGH the door");

            // Premise 2: both remaining candidates are live, jamb strictly first.
            Assert.IsTrue(Geometry.SegmentCircle(p0, p1, padR, jamb, halfW, out float tJamb));
            Assert.IsTrue(Geometry.SegmentCircleInterval(p0, p1, -padR, float2.zero,
                ringR - halfW, out _, out float tFarFace),
                "fixture premise: the chord crosses the core and reaches the far side of the ring");
            float2 farContact = math.lerp(p0, p1, tFarFace);
            Assert.Greater(math.abs(math.atan2(farContact.y, farContact.x)),
                HalfCutout(doorFreeWidth[0], ringR, halfW) + doorCenter[0],
                "fixture premise: and the far crossing is solid wall, so it is accepted too");
            Assert.Less(tJamb, tFarFace, "fixture premise: the jamb is the nearer of the two");

            bool hit = Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out float t, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(tJamb, t, 1e-5f, "the NEAREST contact wins, not the first one checked");
        }

        // --- static overlap ---

        [Test]
        public void OverlapsArc_TrueInsideBody_FalseInDoor()
        {
            float ringR = 10f, halfW = 1f, radius = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };

            Assert.IsTrue(Geometry.OverlapsArc(new float2(-ringR, 0f), radius, ringR, halfW,
                doorCenter, doorFreeWidth), "the ring's body at angle π is solid");
            Assert.IsTrue(Geometry.OverlapsArc(new float2(0f, -ringR), radius, ringR, halfW,
                doorCenter, doorFreeWidth), "so is angle -π/2 — no door there either");

            // Both doorways are open, checked separately so that an index bug
            // (only door 0 consulted, or only door 1) fails here.
            Assert.IsFalse(Geometry.OverlapsArc(new float2(ringR, 0f), radius, ringR, halfW,
                doorCenter, doorFreeWidth), "door 1 (angle 0) is a hole");
            Assert.IsFalse(Geometry.OverlapsArc(new float2(0f, ringR), radius, ringR, halfW,
                doorCenter, doorFreeWidth), "door 0 (angle π/2) is a hole too");

            // Off-center in door 1's opening, grazing its -1 jamb: angularly
            // INSIDE the cutout, so the ring's body declines this point and the
            // jamb circle is the only thing that can report it.
            float2 grazingJamb = new float2(ringR, -2.4f);
            Assert.Less(math.abs(math.atan2(grazingJamb.y, grazingJamb.x)),
                HalfCutout(doorFreeWidth[1], ringR, halfW),
                "fixture premise: inside the cutout — the body test cannot be what answers here");
            Assert.IsTrue(Geometry.OverlapsArc(grazingJamb, radius, ringR, halfW,
                doorCenter, doorFreeWidth), "but a door's corner is solid");

            // Clear on either side of the band: the arc is a WALL, not a disk.
            Assert.IsFalse(Geometry.OverlapsArc(new float2(-13f, 0f), radius, ringR, halfW,
                doorCenter, doorFreeWidth), "outside the ring entirely");
            Assert.IsFalse(Geometry.OverlapsArc(new float2(-5f, 0f), radius, ringR, halfW,
                doorCenter, doorFreeWidth), "and inside it entirely");
        }

        // --- line of fire (padR = 0: a ray, not a body) ---

        [Test]
        public void SegmentArc_RayThroughDoor_IsNotBlocked()
        {
            float ringR = 10f, halfW = 1f, padR = 0f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            // Targeting.HasLineOfFire's own shape: a ray from deep inside the
            // core, out through the door.
            float2 p0 = new float2(2f, 0f);
            float2 p1 = new float2(14f, 0f);

            // Lesson 268, stated as an assertion: this is EXACTLY the call that
            // must not be made in the raw. SegmentCircle against the ring's
            // outer bounding circle reports "already inside, t = 0" for every
            // shooter standing in the arena, which would blank every line of
            // fire in the game, doors included.
            Assert.IsTrue(Geometry.SegmentCircle(p0, p1, padR, float2.zero, ringR + halfW,
                out float naive), "fixture premise: the naive outer-circle call fires...");
            Assert.AreEqual(0f, naive, "... and reports a contact in the shooter's own point");

            Assert.IsFalse(Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out _, out _),
                "a ray through the door is not blocked by the ring around it");

            // Control: the same ray one step to the side is blocked — the
            // opening is a door, not an absent wall.
            float2 asideP0 = new float2(2f, 5f);
            float2 asideP1 = new float2(14f, 5f);
            Assert.IsTrue(Geometry.SegmentArc(asideP0, asideP1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out float asideT, out _));
            Assert.AreEqual(ringR - halfW, math.length(math.lerp(asideP0, asideP1, asideT)), 1e-3f,
                "and it dies on the inner face, the first surface it reaches");
        }

        [Test]
        public void SegmentArc_RayThroughBody_IsBlocked()
        {
            float ringR = 10f, halfW = 1f, padR = 0f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            float2 p0 = new float2(-2f, 0f);
            float2 p1 = new float2(-14f, 0f);

            bool hit = Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out float t, out float2 normal);

            Assert.IsTrue(hit);
            // An unpadded ray meets the bare inner face at |p| = ringR - halfW;
            // the ray spans 12 m from x = -2 (fixture expression).
            float expected = ((ringR - halfW) - 2f) / 12f;
            Assert.AreEqual(expected, t, 1e-4f);

            float2 contact = math.lerp(p0, p1, t);
            Assert.AreEqual(ringR - halfW, math.length(contact), 1e-4f);
            float2 expectedNormal = Geometry.RingWallNormal(contact);
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);
        }

        // --- depenetration ---

        [Test]
        public void PushOutOfArc_FromInsideBody_ChoosesNearerFace()
        {
            float ringR = 10f, halfW = 1f, radius = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };

            // Case 1 — nearer the outer face (0.3 m from it, 1.7 from the inner).
            float2 outward = new float2(-10.7f, 0f);
            Assert.Less(math.abs(math.length(outward) - ringR), halfW,
                "fixture premise: the body really is buried in the ring's body");
            float2 outwardExpected = outward;
            Geometry.PushOutOfCircle(ref outwardExpected, radius, float2.zero, ringR + halfW,
                out float2 outwardExpectedNormal); // the very call the task text names

            bool pushedOut = Geometry.PushOutOfArc(ref outward, radius, ringR, halfW,
                doorCenter, doorFreeWidth, out float2 outwardNormal);

            Assert.IsTrue(pushedOut);
            Assert.AreEqual(outwardExpected.x, outward.x, 1e-5f);
            Assert.AreEqual(outwardExpected.y, outward.y, 1e-5f);
            Assert.AreEqual(outwardExpectedNormal.x, outwardNormal.x, 1e-5f);
            Assert.AreEqual(outwardExpectedNormal.y, outwardNormal.y, 1e-5f);
            Assert.AreEqual(ringR + halfW + radius + Geometry.Skin, math.length(outward), 1e-4f,
                "separated by the body's radius plus the depenetration skin, outward");

            // Case 2 (the second element, lesson 227) — nearer the INNER face,
            // 0.8 m from it against 1.2 to the outer: the other branch.
            float2 inward = new float2(-9.2f, 0f);
            Assert.Less(math.abs(math.length(inward) - ringR), halfW,
                "fixture premise: buried in the body as well, just on the inner half");
            float2 inwardExpected = inward;
            Geometry.ClampInsideRing(ref inwardExpected, radius, ringR - halfW,
                out float2 inwardExpectedNormal);

            bool pushedIn = Geometry.PushOutOfArc(ref inward, radius, ringR, halfW,
                doorCenter, doorFreeWidth, out float2 inwardNormal);

            Assert.IsTrue(pushedIn);
            Assert.AreEqual(inwardExpected.x, inward.x, 1e-5f);
            Assert.AreEqual(inwardExpected.y, inward.y, 1e-5f);
            Assert.AreEqual(inwardExpectedNormal.x, inwardNormal.x, 1e-5f);
            Assert.AreEqual(inwardExpectedNormal.y, inwardNormal.y, 1e-5f);
            Assert.AreEqual(ringR - halfW - radius - Geometry.Skin, math.length(inward), 1e-4f,
                "and by the same amount inward, on the side it started on");

            // The two cases must have chosen OPPOSITE faces — otherwise "nearer
            // face" is satisfied by a constant.
            Assert.Less(math.dot(outwardNormal, inwardNormal), 0f);
        }

        [Test]
        public void PushOutOfArc_NeverCrossesToTheOtherSide()
        {
            // The invariant the whole primitive exists for (spec Р246): a radial
            // push that picks the wrong side hands out a FREE zone transition —
            // the body walks into the wall on one side and is spat out on the
            // other, past a barrier the match is built around.
            float ringR = 10f, halfW = 1f, radius = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };

            // Straight across the band at angle π, from just inside the inner
            // face to just inside the outer one.
            float[] startRadii = { 9.05f, 9.5f, 9.95f, 10.05f, 10.5f, 10.95f };
            foreach (float startRadius in startRadii)
            {
                float2 pos = new float2(-startRadius, 0f);
                bool pushed = Geometry.PushOutOfArc(ref pos, radius, ringR, halfW,
                    doorCenter, doorFreeWidth, out float2 normal);

                Assert.IsTrue(pushed, $"|p| = {startRadius}: a body inside the ring's body is depenetrated");
                Assert.AreEqual(1f, math.length(normal), 1e-4f, $"|p| = {startRadius}: unit normal");
                Assert.GreaterOrEqual(math.abs(math.length(pos) - ringR), halfW + radius,
                    $"|p| = {startRadius}: and ends clear of the band");
                if (startRadius > ringR)
                    Assert.Greater(math.length(pos), ringR,
                        $"|p| = {startRadius}: started on the outer half, must stay outside");
                else
                    Assert.Less(math.length(pos), ringR,
                        $"|p| = {startRadius}: started on the inner half, must stay inside");
            }

            // The doorway stays FREE: a body standing in it is radially inside
            // the band, and a side-blind radial push would fling it to whichever
            // side the arithmetic happened to pick.
            float2 inDoor = new float2(ringR, 0f);
            float2 inDoorBefore = inDoor;
            Assert.IsFalse(Geometry.PushOutOfArc(ref inDoor, radius, ringR, halfW,
                doorCenter, doorFreeWidth, out float2 doorNormal),
                "a body in the doorway is not penetrating anything");
            Assert.AreEqual(inDoorBefore, inDoor, "and must not be moved at all");
            Assert.AreEqual(float2.zero, doorNormal);

            // Clear of the arc on either side: nothing to push, nothing moved.
            float2 outside = new float2(-13f, 0f);
            float2 outsideBefore = outside;
            Assert.IsFalse(Geometry.PushOutOfArc(ref outside, radius, ringR, halfW,
                doorCenter, doorFreeWidth, out _));
            Assert.AreEqual(outsideBefore, outside);

            float2 inside = new float2(-5f, 0f);
            float2 insideBefore = inside;
            Assert.IsFalse(Geometry.PushOutOfArc(ref inside, radius, ringR, halfW,
                doorCenter, doorFreeWidth, out _));
            Assert.AreEqual(insideBefore, inside);
        }

        [Test]
        public void PushOutOfArc_InsideJamb_PushesOutOfJambCircle()
        {
            // The depenetration half of Р246 (spec's reuse table names
            // PushOutOfCircle for the jamb; the plan's Task 7 text mentions only
            // the two faces). Without it a body wedged into the door's corner is
            // reported clear and stays inside the wall: angularly it is in the
            // cutout, so the face branch declines it, and nothing else looks.
            float ringR = 10f, halfW = 1f, radius = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            // The SECOND jamb of the door under test (lesson 227 again): a
            // "checks one corner per door" mutant survives the +1 side.
            float2 jamb = JambCenter(doorCenter[1], doorFreeWidth[1], ringR, halfW, -1f);
            float2 pos = new float2(10f, -1.8f);

            Assert.Less(math.abs(math.atan2(pos.y, pos.x)),
                HalfCutout(doorFreeWidth[1], ringR, halfW),
                "fixture premise: the body is angularly INSIDE the cutout — the faces decline it");
            Assert.Less(math.length(pos - jamb), halfW + radius,
                "fixture premise: and genuinely overlapping the jamb circle");

            float2 expected = pos;
            Geometry.PushOutOfCircle(ref expected, radius, jamb, halfW, out float2 expectedNormal);

            bool pushed = Geometry.PushOutOfArc(ref pos, radius, ringR, halfW,
                doorCenter, doorFreeWidth, out float2 normal);

            Assert.IsTrue(pushed);
            Assert.AreEqual(expected.x, pos.x, 1e-5f);
            Assert.AreEqual(expected.y, pos.y, 1e-5f);
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-5f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-5f);
            Assert.AreEqual(halfW + radius + Geometry.Skin, math.length(pos - jamb), 1e-4f,
                "pushed out along the jamb's own radial");

            // It slid ALONG the corner into the doorway, not across the ring:
            // the free passage is where a wedged body belongs.
            Assert.IsFalse(Geometry.OverlapsArc(pos, radius, ringR, halfW,
                doorCenter, doorFreeWidth), "and ends clear of the arc entirely");
            Assert.Less(math.abs(math.length(pos) - ringR), halfW + radius,
                "still in the doorway — not flung to either side of the ring");
        }

        // --- degenerate and limit cases ---

        [Test]
        public void ZeroDoors_BehavesAsFullRing()
        {
            float ringR = 10f, halfW = 1f, padR = 0.5f, radius = 0.5f;
            float[] noDoorCenter = System.Array.Empty<float>();
            float[] noDoorFreeWidth = System.Array.Empty<float>();
            // The exact sweep ThroughDoor_NoContact lets pass — with the cutout
            // gone it must be stopped on the inner face like any other bearing.
            float2 p0 = new float2(4f, 0f);
            float2 p1 = new float2(16f, 0f);

            bool hit = Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                noDoorCenter, noDoorFreeWidth, out float t, out float2 normal);

            Assert.IsTrue(hit);
            float expected = ((ringR - halfW - padR) - 4f) / 12f;
            Assert.AreEqual(expected, t, 1e-4f);
            float2 contact = math.lerp(p0, p1, t);
            float2 expectedNormal = Geometry.RingWallNormal(contact);
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-4f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-4f);

            Assert.IsTrue(Geometry.OverlapsArc(new float2(ringR, 0f), radius, ringR, halfW,
                noDoorCenter, noDoorFreeWidth), "and the ring is solid at every angle");

            // Contrast against the same geometry WITH the door — otherwise
            // "behaves as a full ring" is satisfied by "always blocks".
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            Assert.IsFalse(Geometry.SegmentArc(p0, p1, padR, ringR, halfW,
                doorCenter, doorFreeWidth, out _, out _));
            Assert.IsFalse(Geometry.OverlapsArc(new float2(ringR, 0f), radius, ringR, halfW,
                doorCenter, doorFreeWidth));
        }

        [Test]
        public void BodyWiderThanDoor_AlwaysContacts()
        {
            // Spec Р247's rule from the other end: a door only passes a body
            // whose DIAMETER fits inside the free width. The jambs are what
            // enforce it — the body arithmetic never sees the doorway at all.
            float ringR = 10f, halfW = 1f;
            float[] doorCenter = { math.PI / 2f, 0f };
            float[] doorFreeWidth = { 4f, 4f };
            float tooWide = 2.5f;  // diameter 5 > 4 m of free width
            float fits = 1.5f;     // diameter 3 < 4

            // Dead center of the doorway, at three radii spanning the wall's
            // own band: nowhere in the opening does a too-wide body fit.
            float2[] alongTheDoorway =
            {
                new float2(ringR - halfW, 0f),
                new float2(ringR, 0f),
                new float2(ringR + halfW, 0f),
            };
            foreach (float2 p in alongTheDoorway)
            {
                Assert.IsTrue(Geometry.OverlapsArc(p, tooWide, ringR, halfW,
                    doorCenter, doorFreeWidth), $"{p} — a body wider than the door touches a jamb");
                Assert.IsFalse(Geometry.OverlapsArc(p, fits, ringR, halfW,
                    doorCenter, doorFreeWidth), $"{p} — a body that fits does not");
            }

            // And in motion, straight through the middle of the door.
            float2 p0 = new float2(4f, 0f);
            float2 p1 = new float2(16f, 0f);
            bool wideHit = Geometry.SegmentArc(p0, p1, tooWide, ringR, halfW,
                doorCenter, doorFreeWidth, out float t, out _);
            Assert.IsTrue(wideHit, "and it cannot drive through either");

            float2 jamb = JambCenter(doorCenter[1], doorFreeWidth[1], ringR, halfW, +1f);
            Assert.IsTrue(Geometry.SegmentCircle(p0, p1, tooWide, jamb, halfW, out float expected));
            Assert.AreEqual(expected, t, 1e-5f, "stopped by the jamb, not by the ring's faces");

            Assert.IsFalse(Geometry.SegmentArc(p0, p1, fits, ringR, halfW,
                doorCenter, doorFreeWidth, out _, out _),
                "while a body that fits still passes — the discriminator that keeps "
                + "this test from being satisfied by 'the door is closed'");
        }

        // --- Stage 3 Task 9 (bd app-35g): six consumers of the arc primitive.
        // SweepArena/Depenetrate grow an arc branch — order fixed by spec
        // §3.2/§3.3: circles -> stadiums -> arcs -> the ring boundary.
        // TestConfigs carries ZoneWallCount == 0 until Т12, so neither golden
        // scenario ever executes a line here — the same relation this whole
        // file already has to TestConfigs.Default() (see the class doc above).

        [Test]
        public void SweepArena_ContactOrder_StadiumBeforeArc()
        {
            // RED-discipline note (mirrors SweepArena_TieBreak_CircleBeforeWall,
            // GeometryTests.cs): this fixture is green even against a
            // SweepArena that never looks at ZoneWallCount at all — the
            // stadium wins trivially by the arc never being a candidate. It is
            // NOT a RED-discipline witness by itself; its job is killing a
            // "tArc <= t" or "arcs traversed before stadiums" mutant once the
            // arc loop lands (mutation round), exactly as the existing pair of
            // TieBreak tests does for circle/wall and wall/ring.
            //
            // An arc's inner face and a stadium wall's rounded cap produce an
            // EXACT tie in t (both reduce to the same rational 0.5, correctly
            // rounded to the same float32 bit pattern — small integers
            // throughout, no tolerance needed on the t comparison).
            float2 p0 = new float2(0f, 0f);
            float2 p1 = new float2(16f, 0f);
            float padR = 0f;

            float ringR = 10f, halfW = 2f; // inner face at ringR - halfW = 8
            float[] noDoorCenter = System.Array.Empty<float>();
            float[] noDoorWidth = System.Array.Empty<float>();

            float2 wallA = new float2(11f, 4f); // cap circle crosses the same sweep at t = 0.5
            float2 wallB = new float2(11f, 20f); // far away — never the winning candidate
            float wallHalfW = 5f;

            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 1,
                WallA = new[] { wallA },
                WallB = new[] { wallB },
                WallHalfWidth = new[] { wallHalfW },
                ZoneWallCount = 1,
                ZoneWallRadius = new[] { ringR },
                ZoneWallHalfWidth = new[] { halfW },
                ZoneWallDoorStart = new[] { 0 },
                ZoneWallDoorCount = new[] { 0 },
                DoorCenterRad = noDoorCenter,
                DoorFreeWidth = noDoorWidth,
            };

            bool hit = Geometry.SweepArena(p0, p1, padR, in arena, includeWall: false,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Geometry.SegmentStadium(p0, p1, padR, wallA, wallB, wallHalfW, out float tWallExpected);
            Geometry.SegmentArc(p0, p1, padR, ringR, halfW, noDoorCenter, noDoorWidth,
                out float tArcExpected, out _);
            Assert.AreEqual(tWallExpected, tArcExpected); // fixture precondition: exact tie
            Assert.AreEqual(tWallExpected, t); // no tolerance — the tie itself is exact

            // The winning normal must be the STADIUM's (radial from wallA's
            // own cap center), not the arc's (radial from the sim origin) —
            // observably different at this contact.
            float2 contact = math.lerp(p0, p1, t);
            float2 expectedNormal = math.normalizesafe(contact - wallA, new float2(1f, 0f));
            Assert.AreEqual(expectedNormal.x, normal.x, 1e-5f);
            Assert.AreEqual(expectedNormal.y, normal.y, 1e-5f);
        }

        [Test]
        public void SweepArena_ContactOrder_ArcBeforeRing()
        {
            // Companion order-pin: a zone wall NESTED inside the arena
            // boundary must be found before the ring — the arc has to occupy
            // its own slot ahead of `includeWall`'s block, not merely exist
            // somewhere in the function.
            //
            // Unlike StadiumBeforeArc above, this fixture IS a genuine RED
            // witness: today SweepArena never consults ZoneWallCount, so the
            // ONLY candidate along this sweep is the ring wall at
            // t ~ 0.534; once the arc loop lands, the nested zone wall's door
            // jamb is reached first, at t ~ 0.254 — a different t AND a
            // materially different (non-radial) normal, not a razor-thin tie
            // a future refactor could land on by accident.
            float ringR = 10f, halfW = 1f, padR = 0.5f;
            float[] doorCenter = { math.PI / 2f, 0f }; // index 1 = the door under test
            float[] doorFreeWidth = { 4f, 4f };
            float2 p0 = new float2(10f, 0f);
            float2 p1 = new float2(10f, 6f); // TangentialDriftIntoJamb's own drift (Task 7 fixture)

            var arena = new ArenaSimConfig
            {
                Radius = 11f, // encloses the zone wall (outer face at ringR+halfW=11) with room to spare
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 0,
                WallA = System.Array.Empty<float2>(),
                WallB = System.Array.Empty<float2>(),
                WallHalfWidth = System.Array.Empty<float>(),
                ZoneWallCount = 1,
                ZoneWallRadius = new[] { ringR },
                ZoneWallHalfWidth = new[] { halfW },
                ZoneWallDoorStart = new[] { 0 },
                ZoneWallDoorCount = new[] { 2 },
                DoorCenterRad = doorCenter,
                DoorFreeWidth = doorFreeWidth,
            };

            // Independent expectation through the already-pinned SegmentArc/
            // SegmentRingWall primitives (Task 7).
            Assert.IsTrue(Geometry.SegmentArc(p0, p1, padR, ringR, halfW, doorCenter, doorFreeWidth,
                out float tArcExpected, out float2 normalArcExpected));
            Assert.IsTrue(Geometry.SegmentRingWall(p0, p1, padR, arena.Radius, out float tRingExpected));
            Assert.Less(tArcExpected, tRingExpected,
                "fixture premise: the nested zone wall's door jamb is reached before the arena boundary");

            bool hit = Geometry.SweepArena(p0, p1, padR, in arena, includeWall: true,
                out float t, out float2 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(tArcExpected, t, 1e-5f);
            Assert.AreEqual(normalArcExpected.x, normal.x, 1e-4f);
            Assert.AreEqual(normalArcExpected.y, normal.y, 1e-4f);

            // Discriminator: the ring's OWN normal at this exact contact point
            // differs materially from the arc's — proves the arc supplied the
            // winning candidate, not the ring.
            float2 contact = math.lerp(p0, p1, t);
            float2 ringNormalHere = Geometry.RingWallNormal(contact);
            Assert.Greater(math.distance(normal, ringNormalHere), 0.1f,
                "the winning normal must be the arc's door jamb, not the ring's own radial");
        }

        [Test]
        public void Depenetrate_OutOfArc_Terminates()
        {
            // Stage 3 Task 9: Depenetrate grows an arc branch between walls
            // and the ring clamp (same fixed order as SweepArena) — a body
            // embedded in a zone wall's body must be pushed clear and STAY
            // clear across further iterations, not oscillate or walk off
            // indefinitely.
            float ringR = 10f, halfW = 1f, radius = 0.5f;
            float[] doorCenter = System.Array.Empty<float>();
            float[] doorFreeWidth = System.Array.Empty<float>();
            float2 start = new float2(-10.7f, 0f); // buried near the outer face (Task 7 fixture)

            var arena = new ArenaSimConfig
            {
                Radius = 35f,
                ObstacleCount = 0,
                ObstaclePos = System.Array.Empty<float2>(),
                ObstacleRadius = System.Array.Empty<float>(),
                WallCount = 0,
                WallA = System.Array.Empty<float2>(),
                WallB = System.Array.Empty<float2>(),
                WallHalfWidth = System.Array.Empty<float>(),
                ZoneWallCount = 1,
                ZoneWallRadius = new[] { ringR },
                ZoneWallHalfWidth = new[] { halfW },
                ZoneWallDoorStart = new[] { 0 },
                ZoneWallDoorCount = new[] { 0 },
                DoorCenterRad = doorCenter,
                DoorFreeWidth = doorFreeWidth,
            };

            float2 pos = start;
            float2 vel = new float2(3f, -2f); // nonzero, to prove Slide() runs against the arc's normal
            Geometry.Depenetrate(ref pos, ref vel, radius, in arena, 10);

            // Independent expectation: PushOutOfArc alone resolves this in ONE
            // call (same shape as PushOutOfCircle/PushOutOfStadium — no
            // internal iteration of its own), so ten Depenetrate iterations
            // must land EXACTLY where a single PushOutOfArc call would, not
            // walk further with each pass (the termination this test's name
            // names).
            float2 expected = start;
            Geometry.PushOutOfArc(ref expected, radius, ringR, halfW, doorCenter, doorFreeWidth,
                out float2 expectedNormal);
            Assert.AreEqual(expected.x, pos.x, 1e-5f);
            Assert.AreEqual(expected.y, pos.y, 1e-5f);

            Assert.IsFalse(Geometry.OverlapsArc(pos, radius, ringR, halfW, doorCenter, doorFreeWidth));
            // Slide removed the into-surface component: the resting velocity's
            // dot with the outward normal must no longer be negative.
            Assert.GreaterOrEqual(math.dot(vel, expectedNormal), -1e-4f);
        }

        [Test]
        public void OpenFixture_HasNoBarrierThroughCenter()
        {
            // Stage 3 Task 12 (owner decision R-76). TestConfigs.Open() is the
            // fixture dozens of movement/combat tests call "the open field",
            // and Т12 gave DefaultArena() two zone rings — so "open" stopped
            // being self-evident and became a property that has to be
            // asserted, not read off a diff. A sweep straight across the arena
            // through the center meets every barrier there is, at every radius
            // there is: circles, interior walls and the two arcs alike.
            //
            // Both halves matter. The control arm (Default(), same sweep)
            // proves the assertion can fail at all — without it, an Open()
            // that silently stopped clearing anything would still pass.
            var open = TestConfigs.Open();
            float2 from = new float2(-open.Arena.Radius + 1f, 0f);
            float2 to = new float2(open.Arena.Radius - 1f, 0f);

            Assert.AreEqual(0, open.Arena.ZoneWallCount, "Open() must carry no zone walls");
            Assert.AreEqual(0, open.Arena.ObstacleCount, "Open() must carry no circles");
            Assert.AreEqual(0, open.Arena.WallCount, "Open() must carry no interior walls");
            Assert.AreEqual(2, open.Arena.ZoneRadius.Length,
                "Open() must KEEP the zone boundaries — they are not barriers, and " +
                "Geometry.ZoneOf reads them directly (R-76)");
            Assert.IsFalse(
                Geometry.SweepArena(from, to, open.Hero.Radius, in open.Arena,
                    includeWall: false, out _, out _),
                "a sweep straight across Open() must meet no barrier at all");

            var closed = TestConfigs.Default();
            Assert.IsTrue(
                Geometry.SweepArena(from, to, closed.Hero.Radius, in closed.Arena,
                    includeWall: false, out _, out _),
                "control: the same sweep across Default() must be stopped by the zone rings — " +
                "otherwise the assertion above proves nothing");
        }
    }
}
