using FishNet.Managing.Transporting;
using NUnit.Framework;
using Ring.Data;
using Ring.Networking;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 33 (spec §3.14, plan Task 33, Р107, CR 7 "80 ms RTT / 5%
    /// loss"): pins DevLatencySetup.Apply against a REAL FishNet
    /// LatencySimulator (task-33-brief.md §2.4) — the packaged type is
    /// `[Serializable]`, constructed with `new()`, and fully readable
    /// through its own public getters (task-33-brief.md §0a,
    /// LatencySimulator.cs), so there is no need for a mock here. `net` is
    /// a plain ScriptableObject.CreateInstance&lt;NetConfig&gt;() and the
    /// C# defaults ARE the numbers most of these tests assert against (the
    /// project's "two sources" rule — no literal is copied out of a
    /// .asset); tests that need an edge case set the field directly on the
    /// instance (the [Range] attributes on NetConfig are Inspector hints
    /// only, per task-33-brief.md §0a/§2.1).
    public class DevLatencySetupTests
    {
        static (LatencySimulator sim, NetConfig net, NetStats stats) Fixture()
        {
            return (new LatencySimulator(), ScriptableObject.CreateInstance<NetConfig>(), new NetStats());
        }

        [Test]
        public void Apply_HandsTransportHalfTheRtt()
        {
            // Р107, the main test of this task: NetConfig's shipped default
            // is LatencySimRttMs = 80 (round-trip); the transport must
            // receive HALF of that, 40, not 80 — the doubling bug this
            // whole task exists to prevent (urok 56, FishNet's own tooltip
            // is false — task-33-brief.md §0a).
            var (sim, net, stats) = Fixture();
            Assert.AreEqual(80, net.LatencySimRttMs, "fixture premise: NetConfig's shipped default");

            DevLatencySetup.Apply(sim, net, stats);

            Assert.AreEqual(40, sim.GetLatency());
        }

        [Test]
        public void Apply_ConvertsLossPercentToPerDirectionFraction()
        {
            // LatencySimLossPercent is a percentage (5f); FishNet's
            // SetPacketLoss expects a [0,1] fraction (LatencySimulator.cs
            // :126/:140).
            var (sim, net, stats) = Fixture();
            Assert.AreEqual(5f, net.LatencySimLossPercent, "fixture premise: NetConfig's shipped default");

            DevLatencySetup.Apply(sim, net, stats);

            Assert.AreEqual(0.05d, sim.GetPacketLost(), 1e-9d);
        }

        [Test]
        public void Apply_EnablesTheSimulator()
        {
            var (sim, net, stats) = Fixture();

            DevLatencySetup.Apply(sim, net, stats);

            Assert.IsTrue(sim.GetEnabled());
            // Witness that this is a genuinely active simulator and not
            // just a flipped flag — mirrors LatencySimulator.CanSimulate's
            // own condition (LatencySimulator.cs:46) at the shipped
            // defaults.
            Assert.IsTrue(sim.GetEnabled() && (sim.GetLatency() > 0 || sim.GetPacketLost() > 0),
                "an enabled simulator at NetConfig's shipped defaults must actually simulate something");
        }

        [Test]
        public void Apply_WritesBothNumbersAndTheFlagToNetStats()
        {
            // Plan sanction :1603-1606 / brief §2.3: both the owner-facing
            // RTT and the one-way figure actually handed to the transport
            // land in NetStats, so Task 48's overlay never has to divide in
            // its head.
            var (sim, net, stats) = Fixture();

            DevLatencySetup.Apply(sim, net, stats);

            Assert.IsTrue(stats.LatencySimActive);
            Assert.AreEqual(80, stats.LatencySimRttMs);
            Assert.AreEqual(40, stats.LatencySimOneWayMs);
            Assert.AreEqual(5f, stats.LatencySimLossPercent);
        }

        [Test]
        public void OneWayLatencyMs_OddRttRoundsDown()
        {
            // Documents the truncation (Р107): 81 -> 40, not 41 — integer
            // division, not Math.Round or Math.Ceiling. Deliberate, not
            // accidental.
            Assert.AreEqual(40, DevLatencySetup.OneWayLatencyMs(81));
        }

        [Test]
        public void Apply_HostileNegativesAreClampedNotThrown()
        {
            // Р82: a [Range] attribute sits between the OWNER and the
            // .asset Inspector, not between a hostile caller and this code
            // — FishNet's own setters do not clamp either (task-33-brief.md
            // §0a, LatencySimulator.cs:99/:140). A negative RTT/loss must
            // not throw and must not reach the transport as a negative
            // number.
            var (sim, net, stats) = Fixture();
            net.LatencySimRttMs = -80;
            net.LatencySimLossPercent = -5f;

            Assert.DoesNotThrow(() => DevLatencySetup.Apply(sim, net, stats));

            Assert.AreEqual(0, sim.GetLatency());
            Assert.AreEqual(0d, sim.GetPacketLost());
            Assert.IsFalse(stats.LatencySimActive);

            // Witness (same assertion as Apply_WritesBothNumbersAndTheFlagToNetStats /
            // test 1/3): the same pair of methods fed VALID numbers DOES
            // report activity, proving the false above is the negative
            // clamp doing its job rather than a broken Apply that always
            // reports inactive.
            var (sim2, net2, stats2) = Fixture();
            DevLatencySetup.Apply(sim2, net2, stats2);
            Assert.IsTrue(stats2.LatencySimActive);
        }

        [Test]
        public void Apply_ZeroKnobsMeanInactive()
        {
            // Mirrors LatencySimulator.CanSimulate (LatencySimulator.cs:46):
            // an ENABLED simulator with both knobs at zero is inert. The
            // flag must say so even though SetEnabled(true) still ran.
            var (sim, net, stats) = Fixture();
            net.LatencySimRttMs = 0;
            net.LatencySimLossPercent = 0f;

            DevLatencySetup.Apply(sim, net, stats);

            Assert.IsTrue(sim.GetEnabled());
            Assert.IsFalse(stats.LatencySimActive);
        }

        [Test]
        public void Apply_LossAboveHundredPercentClampsToOne()
        {
            // FishNet's SetPacketLoss does not clamp (task-33-brief.md
            // §0a, LatencySimulator.cs:140) — an owner-entered percentage
            // above 100 must not reach the transport as a fraction above 1.
            var (sim, net, stats) = Fixture();
            net.LatencySimLossPercent = 250f;

            DevLatencySetup.Apply(sim, net, stats);

            Assert.AreEqual(1.0d, sim.GetPacketLost());
        }
    }
}
