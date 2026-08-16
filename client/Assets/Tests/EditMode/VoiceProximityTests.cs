using NUnit.Framework;
using Ring.Networking.Voice;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 55 (plan Ф11, spec С15): the voice falloff that the
    /// spike's "distance attenuation works" clause is measured against.
    ///
    /// THE RADIUS IS THE CONTRACT, THE SHAPE IS NOT. What the criterion pins
    /// is that a speaker beyond `HearRadius` is inaudible and one at zero
    /// distance is at full volume; the linear amplitude in between is a spike
    /// decision that Stage 4 may retune. So the tests below assert the two
    /// ends and monotonicity hard, and the midpoint only as the shape that is
    /// shipped today — a retune breaks exactly one test, by design, and it is
    /// named so the next reader knows it is the soft one.
    ///
    /// NUMBERS ARE THE FIXTURES' OWN. Nothing here reads `VisibilityConfig`:
    /// the shipped `HearRadius` is 60 m today, and a balance change to it must
    /// not turn these red.
    public class VoiceProximityTests
    {
        [Test]
        public void AtTheSpeaker_IsFullVolume()
        {
            Assert.AreEqual(1f, VoiceProximity.Gain(0f, 60f), 1e-5f);
        }

        [Test]
        public void AtTheRadius_IsSilent()
        {
            // Exactly at the boundary, not merely past it: `HearRadius` is the
            // simulation's own "can this be heard" predicate
            // (`VisibilitySystem.CanHear` uses `<=`), and a voice that is still
            // faintly audible one meter outside what the fog of war calls
            // audible would be a second, disagreeing definition of the same
            // word.
            Assert.AreEqual(0f, VoiceProximity.Gain(60f, 60f), 1e-5f);
        }

        [Test]
        public void BeyondTheRadius_StaysSilent()
        {
            Assert.AreEqual(0f, VoiceProximity.Gain(61f, 60f), 1e-5f);
            Assert.AreEqual(0f, VoiceProximity.Gain(1000f, 60f), 1e-5f);
        }

        [Test]
        public void Midway_IsHalfVolume_ShippedShapeOnly()
        {
            // THE SOFT TEST. Linear in amplitude is today's choice (see
            // `VoiceProximity`'s own doc); if Stage 4 reshapes the curve this
            // is the test that is expected to move, and the three above are
            // the ones that are not.
            Assert.AreEqual(0.5f, VoiceProximity.Gain(30f, 60f), 1e-5f);
        }

        [Test]
        public void Falloff_IsMonotonic()
        {
            float previous = VoiceProximity.Gain(0f, 60f);
            for (int meters = 1; meters <= 70; meters++)
            {
                float current = VoiceProximity.Gain(meters, 60f);
                Assert.LessOrEqual(current, previous,
                    $"gain rose between {meters - 1} m and {meters} m");
                previous = current;
            }
        }

        [Test]
        public void NegativeDistance_IsTreatedAsZero()
        {
            // Rendered positions are subtracted every frame; a distance that
            // arrives negative is a caller's arithmetic bug, and the answer
            // that keeps the audio pipeline sane is "full volume", not a
            // gain above one.
            Assert.AreEqual(1f, VoiceProximity.Gain(-5f, 60f), 1e-5f);
        }

        [Test]
        public void NonFiniteDistance_IsSilent()
        {
            // NaN reaching `AudioSource.volume` puts the audio pipeline into an
            // undefined state, and NaN compares false against every bound, so
            // it has to be rejected explicitly rather than clamped.
            Assert.AreEqual(0f, VoiceProximity.Gain(float.NaN, 60f), 1e-5f);
            Assert.AreEqual(0f, VoiceProximity.Gain(float.PositiveInfinity, 60f), 1e-5f);
        }

        [Test]
        public void NonPositiveRadius_IsSilent()
        {
            // A radius of zero is "nobody hears anybody" and must not divide.
            Assert.AreEqual(0f, VoiceProximity.Gain(0f, 0f), 1e-5f);
            Assert.AreEqual(0f, VoiceProximity.Gain(1f, -60f), 1e-5f);
        }

        [Test]
        public void RadiusIsRead_NotAssumed()
        {
            // The same distance means different things under different radii —
            // the mutation this catches is a hard-coded 60 in the formula.
            Assert.AreEqual(0.5f, VoiceProximity.Gain(15f, 30f), 1e-5f);
            Assert.AreEqual(0f, VoiceProximity.Gain(30f, 30f), 1e-5f);
        }
    }
}
