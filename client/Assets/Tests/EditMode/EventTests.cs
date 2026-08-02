using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class EventTests
    {
        [Test]
        public void Emit_RecordsKindTickAndPayload()
        {
            var w = new SimulationWorld(1, TestConfigs.Default());
            w.Tick(default); // tick = 1
            w.Emit(SimEventKind.PlayerDashed, new float2(1f, 2f), 0, default, 0f);
            Assert.AreEqual(1, w.EventCount);
            SimEvent e = w.GetEvent(0);
            Assert.AreEqual(SimEventKind.PlayerDashed, e.Kind);
            Assert.AreEqual(1, e.Tick);
            Assert.AreEqual(new float2(1f, 2f), e.Pos);
        }

        [Test]
        public void ClearEvents_ResetsCount()
        {
            var w = new SimulationWorld(1, TestConfigs.Default());
            w.Emit(SimEventKind.WaveStarted, float2.zero, 1, default, 0f);
            w.ClearEvents();
            Assert.AreEqual(0, w.EventCount);
        }

        [Test]
        public void Overflow_DropsDeterministicallyWithoutGrowth()
        {
            var cfg = TestConfigs.Default();
            var w = new SimulationWorld(1, cfg);
            int cap = cfg.Arena.MaxEventsPerFrame;
            for (int i = 0; i < cap + 10; i++)
                w.Emit(SimEventKind.ProjectileFired, float2.zero, i, default, 0f);
            Assert.AreEqual(cap, w.EventCount);
            Assert.AreEqual(10, w.DroppedEvents);
        }
    }
}
