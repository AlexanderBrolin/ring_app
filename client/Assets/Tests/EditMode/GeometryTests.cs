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
        public void SweepArena_ReportsNearestContactWithNormal()
        {
            var arena = TestConfigs.DefaultArena(); // obstacle (10,4) r=2.2
            bool hit = Geometry.SweepArena(new float2(6f, 4f), new float2(14f, 4f), 0.45f,
                arena, includeWall: false, out float t, out float2 n);
            Assert.IsTrue(hit);
            Assert.That(t, Is.InRange(0f, 1f));
            Assert.Less(n.x, 0f); // normal opposes the direction of travel
        }
    }
}
