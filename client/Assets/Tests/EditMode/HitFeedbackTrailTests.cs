using NUnit.Framework;
using Ring.Networking.Client;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// The instrument bd `app-03et` is measured with: the gap between a blow
    /// being resolved and the player being told, and the distance his body
    /// covered inside that gap.
    ///
    /// EVERY TEST BELOW IS A QUESTION ABOUT THE RING, NOT ABOUT ARITHMETIC.
    /// Subtracting two ticks and measuring one distance is not worth a suite;
    /// what is worth one is that the ring answers about a tick it still holds,
    /// refuses about a tick it has lost, and cannot answer a newer tick with an
    /// older occupant's position. That last shape is why the aliasing test asks
    /// about ticks 8 and 40 at a capacity of 32 — the same reason
    /// `ImpactPulseLogTests` picks those two numbers.
    ///
    /// THE KNOWN-ANSWER QUANTITY IS THE LAG, AND IT IS HERE ON PURPOSE
    /// (lesson 680: a new instrument needs a quantity whose answer is already
    /// settled, or its first reading has nobody to contradict it). A gap of
    /// four ticks between two stamped positions IS four ticks, whatever the
    /// distance turns out to be, so a ring that quietly mismatched its slots
    /// would fail on the lag before anyone had to trust the meters.
    public sealed class HitFeedbackTrailTests
    {
        [Test]
        public void Measure_ReportsHowFarTheBodyMovedWhileTheNewsWasInTheBuffer()
        {
            var trail = new HitFeedbackTrail(32);

            trail.NotePosition(100, new float2(0f, 0f));
            trail.NotePosition(104, new float2(3f, 0f));

            Assert.That(trail.TryMeasure(100, 104, out int lagTicks, out float movedMeters),
                Is.True, "обе отметки в кольце — прибор обязан ответить");
            Assert.That(lagTicks, Is.EqualTo(4), "отставание в тиках");
            Assert.That(movedMeters, Is.EqualTo(3f).Within(1e-4f), "путь тела за это время");
        }

        [Test]
        public void ATickTheRingNoLongerHolds_IsRefused_NotGuessed()
        {
            var trail = new HitFeedbackTrail(32);

            trail.NotePosition(104, new float2(3f, 0f));

            Assert.That(trail.TryMeasure(100, 104, out int lagTicks, out float movedMeters),
                Is.False, "отметки на тике 100 не было — измерять нечем");
            Assert.That(lagTicks, Is.EqualTo(0), "отказ не оставляет мусора в выходных");
            Assert.That(movedMeters, Is.EqualTo(0f), "отказ не оставляет мусора в выходных");
        }

        [Test]
        public void AnOverwrittenSlot_DoesNotAnswerWithItsPreviousOccupant()
        {
            // 8 and 40 SHARE A SLOT at a capacity of 32, which is the entire
            // point of the pair — the same reason ImpactPulseLogTests picks it.
            var trail = new HitFeedbackTrail(32);

            trail.NotePosition(8, new float2(0f, 0f));
            trail.NotePosition(40, new float2(9f, 0f));

            Assert.That(trail.TryMeasure(8, 40, out _, out _),
                Is.False, "тик 8 вытеснен тиком 40 — прибор обязан отказать, а не выдать чужую точку");
        }
    }
}
