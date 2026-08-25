using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Simulation.Combat;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// The client's tick -> impulse table (app-88jb Т9, decision Р417): the
    /// memory of which tick the server resolved which blow on, which a FishNet
    /// reconcile is free to ask about again and again.
    ///
    /// WITHOUT THIS TABLE OWNER DECISION Н18 DOES NOT WORK AT ALL (finding
    /// D2-C5). FishNet replays the queue of ReplicateData from the tick of a
    /// correction, and PerformReplicate knows nothing about impulses — so the
    /// predicted shove Т7 introduced would simply vanish at the first
    /// correction that arrived. Every test below is a question about that
    /// replay, not about arithmetic: the arithmetic of a blow is Impact's, and
    /// ImpactKnockbackTests already owns it.
    ///
    /// A RING, NOT A DICTIONARY, AND THE TESTS ARE WRITTEN ON THAT SHAPE.
    /// AllocationTests forbids per-tick allocation and this project has
    /// refused hash structures five times in writing, so the table is a
    /// preallocated ring addressed by `tick % capacity`. That is why
    /// PrunedTick_ForgetsItsBlow_AndDoesNotAliasANewerOne asks about ticks 8
    /// and 40 rather than about two arbitrary ones: at a capacity of 32 those
    /// two SHARE A SLOT, and a ring that forgets by wiping instead of by
    /// checking the tick answers the newer one with the older one's blow.
    ///
    /// TWO OF THE SIX WITNESS BRANCHES THE PLAN DID NOT COVER, and both were
    /// found by asking what a stub would survive rather than by reading.
    /// `Reset` is named in the class's interface and wired from
    /// `ClientMatchReset.ResetForEpoch`, yet no test called it and no mutation
    /// touched it — an empty body would have shipped, and its failure shows
    /// only on an epoch change, as a shove from the previous match
    /// (coordinator Ruling 37). The capacity guard is the same story one step
    /// down: the constructor floors a hostile capacity the way
    /// `CorrectionWindow` does, and `CorrectionWindowTests` witnesses ITS floor
    /// (Ruling 38), so this one is witnessed too — behaviorally, through the
    /// single slot, rather than by widening the class with a `Capacity` member
    /// no production caller wants.
    ///
    /// NUMBERS ARE THE FIXTURES' OWN. Nothing here reads an `.asset` or the
    /// production capacity — every test names the capacity it wants and spells
    /// its impulses out as literals, so a retune of the correction window
    /// cannot turn one of these red for a reason that has nothing to do with
    /// the table.
    public class ImpactPulseLogTests
    {
        [Test]
        public void TwoHitsOnOneTick_Sum()
        {
            var log = new ImpactPulseLog(capacityTicks: 32);
            log.Add(10u, new ImpactPulse(new float2(0.2f, 0f), 0.1f));
            log.Add(10u, new ImpactPulse(new float2(0f, 0.3f), 0.4f));
            ImpactPulse got = log.For(10u);
            Assert.AreEqual(0.2f, got.Delta.x, 1e-5f);
            Assert.AreEqual(0.3f, got.Delta.y, 1e-5f);
            Assert.AreEqual(0.5f, got.TiltImpulse, 1e-5f, "моменты не сложились");
        }

        [Test]
        public void ReplayingTheSameTick_GivesTheSameAnswer()
        {
            // Rule Р417: an impulse is applied in the tick of its own event
            // EXACTLY once, but a replay may ASK about it any number of times.
            var log = new ImpactPulseLog(capacityTicks: 32);
            log.Add(7u, new ImpactPulse(new float2(0.5f, 0f), 0f));
            Assert.AreEqual(0.5f, log.For(7u).Delta.x, 1e-5f);
            Assert.AreEqual(0.5f, log.For(7u).Delta.x, 1e-5f, "повторный запрос съел импульс");
        }

        [Test]
        public void TickWithoutAnyBlow_IsNone()
        {
            var log = new ImpactPulseLog(capacityTicks: 32);
            ImpactPulse got = log.For(5u);
            Assert.AreEqual(0f, math.length(got.Delta), 1e-6f);
            Assert.AreEqual(0f, got.TiltImpulse, 1e-6f);
        }

        [Test]
        public void PrunedTick_ForgetsItsBlow_AndDoesNotAliasANewerOne()
        {
            // The ring: tick 40 and tick 8 share a slot at a capacity of 32.
            // Without Prune the old entry would silently come back under the
            // new tick — exactly the class of defect a slot-addressed history
            // has, and exactly why the Ф3 history is addressed that way.
            var log = new ImpactPulseLog(capacityTicks: 32);
            log.Add(8u, new ImpactPulse(new float2(9f, 0f), 0f));
            // ⚠ THE ASSERTION STANDS BEFORE `Prune` — round-3 fix (finding
            // D-I3): if both reads stand AFTER `Prune`, and `Prune` physically
            // zeroes the slot (a legitimate implementation), the mutation
            // "`For` does not check `tickOf[slot]`" returns zero on both lines
            // and SURVIVES. Here slot 8 is still held by tick 8, so the mutant
            // hands out somebody else's 9 under tick 40.
            Assert.AreEqual(0f, log.For(40u).Delta.x, 1e-6f,
                "слот кольца отдал чужой импульс ДО Prune — сверки тика нет");
            log.Prune(oldestKeptTick: 20u);
            Assert.AreEqual(0f, log.For(8u).Delta.x, 1e-6f, "Prune не забыл старый тик");
            Assert.AreEqual(0f, log.For(40u).Delta.x, 1e-6f, "слот кольца отдал чужой импульс");
        }

        [Test]
        public void ResetForgetsEveryTick_IncludingTickZero()
        {
            // Ruling 37. `Reset` is wired from ClientMatchReset.ResetForEpoch (Step 4),
            // and without this witness an empty body survives the whole task -- the
            // failure only shows on an epoch change, as a shove from the match before.
            // TICK ZERO ON PURPOSE: zero is a legal tick, so a table that forgets by
            // zeroing `_tickOf` instead of by the sentinel -- and does not clear the
            // impulses -- would answer tick 0 with whatever slot 0 still holds.
            // HONEST BOUND: a `Reset` that zeroes `_tickOf` AND clears `_pulses` is
            // indistinguishable here. This witnesses the BRANCH, not the sentinel.
            var log = new ImpactPulseLog(capacityTicks: 32);
            log.Add(0u, new ImpactPulse(new float2(0.7f, 0f), 0.3f));
            log.Reset();
            ImpactPulse got = log.For(0u);
            Assert.AreEqual(0f, math.length(got.Delta), 1e-6f,
                "Reset не забыл тик — смена эпохи принесёт толчок из прошлого матча");
            Assert.AreEqual(0f, got.TiltImpulse, 1e-6f, "Reset не забыл момент");
        }

        [Test]
        public void HostileCapacity_IsFlooredToOneSlot_NeverThrown()
        {
            // Ruling 38, and the shape CorrectionWindow already carries one file over
            // (CorrectionWindow.cs:89-90, witnessed by CorrectionWindowTests.cs:131):
            // a ring of zero slots has no representation -- `tick % 0` throws on the
            // first read -- so the constructor floors the capacity instead of handing
            // a caller an exception on some later tick.
            var log = new ImpactPulseLog(capacityTicks: 0);
            log.Add(5u, new ImpactPulse(new float2(0.4f, 0f), 0.2f));
            Assert.AreEqual(0.4f, log.For(5u).Delta.x, 1e-5f,
                "враждебная ёмкость не сведена к одному слоту");
            Assert.AreEqual(0f, log.For(6u).Delta.x, 1e-6f,
                "единственный слот отдал импульс чужого тика");
        }
    }
}
