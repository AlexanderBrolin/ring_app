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
