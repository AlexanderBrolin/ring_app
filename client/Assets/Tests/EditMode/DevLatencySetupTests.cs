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
            // whole task exists to prevent (lesson 56, FishNet's own
            // tooltip is false — task-33-brief.md §0a).
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
            Assert.AreEqual(80, net.LatencySimRttMs, "fixture premise: NetConfig's shipped default");
            Assert.AreEqual(5f, net.LatencySimLossPercent, "fixture premise: NetConfig's shipped default");

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
            // Fix-round 1, I-3: NetStats stores APPLIED facts, not the raw
            // hostile knobs straight through — a mutation that writes
            // net.LatencySimRttMs verbatim would leak -80 here instead of
            // the clamped 0.
            Assert.AreEqual(0, stats.LatencySimRttMs);
            Assert.AreEqual(0, stats.LatencySimOneWayMs);
            Assert.AreEqual(0f, stats.LatencySimLossPercent);

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
            // Fix-round 1, I-3: the applied fact in NetStats mirrors the
            // clamp too — 250f in, 100f (not 250f) recorded as applied.
            Assert.AreEqual(100f, stats.LatencySimLossPercent);
        }

        [Test]
        public void Apply_LossAtExactlyHundredPercentGivesExactlyOne()
        {
            // Fix-round 1, M-3: the boundary itself, distinct from
            // Apply_LossAboveHundredPercentClampsToOne (250f, ABOVE the
            // ceiling) — 100f must land on EXACTLY 1.0 through the ordinary
            // conversion path, not through the clamp branch, and not on
            // 0.999... or 1.000...1 from a rounding artifact.
            var (sim, net, stats) = Fixture();
            net.LatencySimLossPercent = 100f;

            DevLatencySetup.Apply(sim, net, stats);

            Assert.AreEqual(1.0d, sim.GetPacketLost());
        }

        [Test]
        public void OneWayLatencyMs_TinyPositiveRttRoundsDownToZero()
        {
            // Fix-round 1, M-3: the nearest surprise of Р107 — a POSITIVE
            // RTT of 1 ms still rounds down to 0 one-way ms (integer
            // division truncates), not just a negative-input edge case.
            Assert.AreEqual(0, DevLatencySetup.OneWayLatencyMs(1));
        }

        [Test]
        public void OneWayLatencyMs_ClampsToSixtyThousandCeiling()
        {
            // Fix-round 1, M-1: FishNet's own Inspector ceiling on
            // _latency (LatencySimulator.cs:85-87, [Range(0, 60000)]) —
            // SetLatency itself does not enforce it (task-33-brief.md §0a),
            // so OneWayLatencyMs is the only place it's enforced at all.
            Assert.AreEqual(60000, DevLatencySetup.OneWayLatencyMs(int.MaxValue));
        }

        [Test]
        public void Apply_NaNLossIsTreatedAsZero()
        {
            // Fix-round 1, I-2: `lossPercent <= 0f` is FALSE for NaN, so a
            // naive guard lets NaN fall through to SetPacketLoss and
            // NetStats unclamped (every comparison against NaN is false,
            // including the upper clamp's `> 1d` check). NaN must be
            // treated as "no loss configured", same as an ordinary
            // non-positive percentage — and must not poison
            // LatencySimActive, since the RTT knob (still the shipped
            // default 80) keeps the simulator genuinely active on its own.
            var (sim, net, stats) = Fixture();
            net.LatencySimLossPercent = float.NaN;

            DevLatencySetup.Apply(sim, net, stats);

            Assert.AreEqual(0d, sim.GetPacketLost());
            Assert.AreEqual(0f, stats.LatencySimLossPercent);
            Assert.IsTrue(stats.LatencySimActive, "the RTT knob alone must still report activity");
        }
    }
}
