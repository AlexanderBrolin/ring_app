using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class StateHashTests
    {
        [Test]
        public void FloatAdd_MatchesRawBits()
        {
            ulong viaFloat = StateHash64.Add(StateHash64.Begin(), 1.5f);
            ulong viaBits = StateHash64.Add(StateHash64.Begin(), (ulong)math.asuint(1.5f));
            Assert.AreEqual(viaBits, viaFloat);
        }

        [Test]
        public void NegativeZero_NormalizedToPositiveZero()
        {
            Assert.AreEqual(StateHash64.Add(StateHash64.Begin(), 0f),
                            StateHash64.Add(StateHash64.Begin(), -0f));
        }

        [Test]
        public void Float2_HashesBothComponentsInOrder()
        {
            ulong h = StateHash64.Add(StateHash64.Begin(), new float2(1f, 2f));
            ulong manual = StateHash64.Add(StateHash64.Add(StateHash64.Begin(), 1f), 2f);
            Assert.AreEqual(manual, h);
            Assert.AreNotEqual(h, StateHash64.Add(StateHash64.Begin(), new float2(2f, 1f)));
        }

        [Test]
        public void BoolAndInt_Distinct()
        {
            Assert.AreNotEqual(StateHash64.Add(StateHash64.Begin(), true),
                               StateHash64.Add(StateHash64.Begin(), false));
            Assert.AreNotEqual(StateHash64.Add(StateHash64.Begin(), 1),
                               StateHash64.Add(StateHash64.Begin(), 2));
        }
    }
}
