using System.Globalization;
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

        // ==================================================================
        // Stage 2 app-ck7 (task-ck7-wire-brief.md §2.1), the RED half: the
        // overload that is told what the `-ring-latency` switch asked for.
        // Everything above pins the CONFIGURED path and stays exactly as it
        // was; the block below adds the three answers the command line can
        // give, and nothing here weakens a test above.
        //
        // WHAT MAKES THESE DISCRIMINATE (lesson 129). The stub they are
        // written against is a constant in `options` — it applies NetConfig's
        // numbers whatever was typed, which is the ONE defect that would make
        // this whole task pointless: a switch nobody's transport obeys. Every
        // test below therefore asserts the NUMBERS that reached the simulator
        // and the contents of NetStats, never that Apply was called; and the
        // two tests whose own subject IS "NetConfig's numbers" (the absent
        // switch and a refused one) end on a clause a constant cannot satisfy
        // — their answer must DIFFER from `off`'s.
        //
        // NUMBERS. 120, 2.5, 5000 and 250 are inputs these tests invent, so
        // they are literals; everything expected OF NetConfig is read off the
        // fixture instance (`net.LatencySimRttMs`, `net.LatencySimLossPercent`)
        // or from the pinned helper that converts it, never copied out of a
        // .asset — the same rule the fixture above follows.
        // ==================================================================

        /// The options a real launch produces, built the ONLY way production
        /// builds them: through `DevLatencyOptions.Parse`. The struct's
        /// constructor is private on purpose — no caller may assemble a
        /// combination the parse cannot produce — so a test that wanted to
        /// hand-build an `Off` value could not, and should not: what this
        /// block asserts is what the SWITCH does, end to end, applier and
        /// parser together.
        ///
        /// The surrounding arguments are here for the same reason
        /// DevLatencyOptionsTests puts them there (a real command line never
        /// hands the switch over alone); that file's own helper is private to
        /// it and cannot be borrowed, and the three lines are not worth
        /// opening a seam between two fixtures for.
        static DevLatencyOptions Options(string value)
        {
            return DevLatencyOptions.Parse(new[]
            {
                "ring-server.x86_64", "-batchmode",
                DevLatencyOptions.LatencyArgument, value, "-nographics",
            });
        }

        /// What an `off` launch records in NetStats, for the clause the two
        /// "runs on NetConfig's numbers" tests end with. Kept as a helper
        /// because both of them need it and neither is ABOUT `off` — the
        /// state of that mode is pinned by its own test below.
        static bool OffLaunchReportsActive()
        {
            var (sim, net, stats) = Fixture();
            DevLatencySetup.Apply(sim, net, stats, Options(DevLatencyOptions.OffValue));
            return stats.LatencySimActive;
        }

        [Test]
        public void Apply_WithoutTheSwitch_IsExactlyTodaysBehavior_AndThatIsNotOff()
        {
            // "Exactly today's behavior" is asserted against today's CODE —
            // the three-argument form on a twin fixture — rather than against
            // a table of numbers. A literal table would have to be rewritten
            // the day NetConfig's defaults move, and would then be pinning
            // this file's memory of Critical Rule 7 instead of the applier's
            // behavior. `default(DevLatencyOptions)` is the value a launch
            // without the switch produces (that mode is 0 on purpose), so
            // this is the unflagged launch, not a hand-built stand-in.
            //
            // FAILS ON THE STUB at the LAST assertion: a form that ignores
            // its options answers `off` exactly as it answers "no switch",
            // and one constant cannot hold two answers. That clause is not a
            // trick to force a red — it is the task itself stated as an
            // assertion, and DevLatencyOptionsTests' first test is red for
            // the same reason on the parser's side.
            var (sim, net, stats) = Fixture();
            var (twinSim, twinNet, twinStats) = Fixture();

            DevLatencySetup.Apply(sim, net, stats, default(DevLatencyOptions));
            DevLatencySetup.Apply(twinSim, twinNet, twinStats);

            Assert.IsTrue(twinSim.GetLatency() > 0 && twinStats.LatencySimActive,
                "fixture premise: today's form applies a genuinely active simulator, so the "
                + "comparison below cannot be satisfied by two untouched fixtures");

            Assert.AreEqual(twinSim.GetLatency(), sim.GetLatency(),
                "an unflagged launch hands the transport the same one-way milliseconds it "
                + "did before the switch existed");
            Assert.AreEqual(twinSim.GetPacketLost(), sim.GetPacketLost());
            Assert.AreEqual(twinSim.GetEnabled(), sim.GetEnabled());
            Assert.AreEqual(twinStats.LatencySimActive, stats.LatencySimActive);
            Assert.AreEqual(twinStats.LatencySimRttMs, stats.LatencySimRttMs);
            Assert.AreEqual(twinStats.LatencySimOneWayMs, stats.LatencySimOneWayMs);
            Assert.AreEqual(twinStats.LatencySimLossPercent, stats.LatencySimLossPercent);

            Assert.AreNotEqual(OffLaunchReportsActive(), stats.LatencySimActive,
                "\"the switch was absent\" and \"off\" are two states, not one: the first "
                + "applies NetConfig's numbers, the second applies nothing at all");
        }

        [Test]
        public void Apply_Off_AppliesNothing_AndNetStatsSaysSo()
        {
            // The fixture is the state `off` actually meets on a dev build:
            // a simulator ALREADY carrying Critical Rule 7's numbers and a
            // NetStats already carrying that verdict. Standing it down from a
            // blank simulator would prove nothing — a form that did nothing
            // at all would pass — which is why the premise below insists
            // there is something to disturb.
            //
            // FAILS ON THE STUB at the first assertion: the constant applies
            // and ENABLES the simulator, so `off` leaves it running.
            var (sim, net, stats) = Fixture();
            DevLatencySetup.Apply(sim, net, stats);
            long latencyBefore = sim.GetLatency();
            double lossBefore = sim.GetPacketLost();
            Assert.IsTrue(latencyBefore > 0 && lossBefore > 0d && stats.LatencySimActive,
                "fixture premise: there is a running simulator for the switch to stand down");

            DevLatencySetup.Apply(sim, net, stats, Options(DevLatencyOptions.OffValue));

            Assert.IsFalse(sim.GetEnabled(),
                "`off` is the one mode that does not apply the simulator: enabled is the bit "
                + "FishNet's own CanSimulate reads first (LatencySimulator.cs:46)");
            Assert.AreEqual(latencyBefore, sim.GetLatency(),
                "not applied means not WRITTEN: the transport is left exactly as FishNet's own "
                + "start-up left it (DevLatencyOptions' type doc), not re-applied with zeros");
            Assert.AreEqual(lossBefore, sim.GetPacketLost(),
                "the loss knob is left alone for the same reason as the latency knob");

            // NetStats carries APPLIED FACTS (its own class doc). Nothing is
            // applied here, so every one of them is zero — a stale 80/40/5
            // surviving an `off` launch would put numbers on the Task 48
            // overlay that no packet on the wire is paying, which is the
            // exact confusion this switch exists to end (lessons 185/186).
            Assert.IsFalse(stats.LatencySimActive,
                "the overlay must say plainly that nothing is being simulated");
            Assert.AreEqual(0, stats.LatencySimRttMs,
                "an applied fact of a simulator that was never applied is zero, not the number "
                + "an earlier call left behind");
            Assert.AreEqual(0, stats.LatencySimOneWayMs);
            Assert.AreEqual(0f, stats.LatencySimLossPercent);
        }

        [Test]
        public void Apply_OverrideRttOnly_TakesTheRttFromTheSwitch_AndTheLossFromConfig()
        {
            // FAILS ON THE STUB at the first assertion: 40 (NetConfig's 80
            // halved) where 60 (the operator's 120 halved) is required.
            var (sim, net, stats) = Fixture();
            Assert.AreNotEqual(net.LatencySimRttMs, 120,
                "fixture premise: 120 is not what NetConfig ships, so the override is observable");

            DevLatencySetup.Apply(sim, net, stats, Options("120"));

            Assert.AreEqual(60, sim.GetLatency(),
                "the switch names the ROUND TRIP, the unit NetConfig and Critical Rule 7 use; "
                + "halving it stays in OneWayLatencyMs, the one place that divides");
            Assert.AreEqual(net.LatencySimLossPercent / 100.0, sim.GetPacketLost(), 1e-9d,
                "the switch overrides what it NAMES and nothing else: an unstated loss half is "
                + "NetConfig's, exactly as an absent switch's would be");
            Assert.IsTrue(sim.GetEnabled());
            Assert.IsTrue(stats.LatencySimActive);
            Assert.AreEqual(120, stats.LatencySimRttMs,
                "the overlay shows what is APPLIED — the operator's round trip, not the asset's");
            Assert.AreEqual(60, stats.LatencySimOneWayMs);
            Assert.AreEqual(net.LatencySimLossPercent, stats.LatencySimLossPercent,
                "the loss half nobody typed is still NetConfig's on the overlay");
        }

        [Test]
        public void Apply_OverrideRttAndLoss_TakesBothFromTheSwitch()
        {
            // FAILS ON THE STUB at the first assertion (40 where 60 is
            // required); the loss assertion is the second witness, since a
            // form that read only the RTT half would pass everything else.
            var (sim, net, stats) = Fixture();
            Assert.AreNotEqual(net.LatencySimRttMs, 120, "fixture premise");
            Assert.AreNotEqual(net.LatencySimLossPercent, 2.5f, "fixture premise");

            DevLatencySetup.Apply(sim, net, stats, Options("120/2.5"));

            Assert.AreEqual(60, sim.GetLatency());
            Assert.AreEqual(0.025d, sim.GetPacketLost(), 1e-9d,
                "2.5% per direction, converted to the [0,1] fraction FishNet's SetPacketLoss "
                + "takes by the same helper NetConfig's percentage goes through");
            Assert.IsTrue(stats.LatencySimActive);
            Assert.AreEqual(120, stats.LatencySimRttMs);
            Assert.AreEqual(60, stats.LatencySimOneWayMs);
            Assert.AreEqual(2.5f, stats.LatencySimLossPercent);
        }

        [Test]
        public void Apply_Override_GoesThroughTheSameClampsAsNetConfigsNumbers()
        {
            // Project rule 2, and the reason the parser refuses to clamp:
            // the ceilings have ONE home, and it is this file. Its own tests
            // pin 5000/250 as readable values that travel on untouched, so if
            // the override path skipped these clamps an operator's 250% would
            // reach SetPacketLoss as a fraction of 2.5 — every packet after
            // it a coin flip the overlay could not explain.
            //
            // FAILS ON THE STUB at the first assertion (40 where 2500 is
            // required).
            var (sim, net, stats) = Fixture();
            DevLatencyOptions huge = Options("5000/250");
            Assert.AreEqual(DevLatencyMode.Override, huge.Mode,
                "fixture premise: the parser hands out-of-range numbers on rather than "
                + "refusing them, so this applier is what stands between them and the transport");

            DevLatencySetup.Apply(sim, net, stats, huge);

            Assert.AreEqual(2500, sim.GetLatency());
            Assert.AreEqual(1.0d, sim.GetPacketLost(),
                "loss above 100% clamps to 1.0, exactly as an owner-entered 250 in the .asset "
                + "does (Apply_LossAboveHundredPercentClampsToOne, same clamp)");
            Assert.AreEqual(100f, stats.LatencySimLossPercent,
                "the applied fact mirrors the clamp: 100, never the 250 nobody is running");
            Assert.AreEqual(5000, stats.LatencySimRttMs);
            Assert.AreEqual(2500, stats.LatencySimOneWayMs);

            // FishNet's own [Range(0, 60000)] ceiling on _latency, which
            // SetLatency itself does not enforce (LatencySimulator.cs:85-87).
            // OneWayLatencyMs is the only place it is enforced at all, and
            // the command line reaches it through the same door.
            var (ceilingSim, ceilingNet, ceilingStats) = Fixture();
            DevLatencySetup.Apply(ceilingSim, ceilingNet, ceilingStats,
                Options(int.MaxValue.ToString(CultureInfo.InvariantCulture)));

            Assert.AreEqual(60000, ceilingSim.GetLatency());
            Assert.AreEqual(60000, ceilingStats.LatencySimOneWayMs);
            Assert.AreEqual(120000, ceilingStats.LatencySimRttMs,
                "the applied facts keep RttMs == OneWayMs * 2 even where the ceiling bites: "
                + "the overlay must never print a round trip the transport is not running");
        }

        [Test]
        public void Apply_ZeroOverride_IsAppliedAndInert_UnlikeOff()
        {
            // The third state earning its keep on the APPLIER's side. Zeros
            // are numbers the operator typed: the simulator is applied,
            // enabled and inert, and NetStats reports inactive — which is a
            // DIFFERENT observation from a simulator that was never applied,
            // even though both print "inactive" on the overlay. The transport's
            // own enabled bit is the only place the two differ, so that is
            // what the closing assertion reads.
            //
            // FAILS ON THE STUB at the second assertion (40 where 0 is
            // required); the NetStats flag below is the second witness.
            DevLatencyOptions zero = Options("0/0");
            Assert.AreEqual(DevLatencyMode.Override, zero.Mode,
                "fixture premise: 0 is a value, not a synonym for off");

            var (sim, net, stats) = Fixture();
            DevLatencySetup.Apply(sim, net, stats, zero);

            Assert.IsTrue(sim.GetEnabled(),
                "an override of zeros still runs the whole apply path");
            Assert.AreEqual(0, sim.GetLatency(),
                "the operator's zero, not NetConfig's 80 halved");
            Assert.AreEqual(0d, sim.GetPacketLost());
            Assert.IsFalse(stats.LatencySimActive,
                "enabled and inert reads as inactive — FishNet's own CanSimulate condition "
                + "(LatencySimulator.cs:46), the same one Apply_ZeroKnobsMeanInactive pins");
            Assert.AreEqual(0, stats.LatencySimRttMs);
            Assert.AreEqual(0f, stats.LatencySimLossPercent);

            var (offSim, offNet, offStats) = Fixture();
            DevLatencySetup.Apply(offSim, offNet, offStats, Options(DevLatencyOptions.OffValue));

            Assert.AreNotEqual(offSim.GetEnabled(), sim.GetEnabled(),
                "`off` never applies the simulator while an override of zeros applies it and "
                + "leaves it inert; collapsing 0 onto off would make the two indistinguishable "
                + "at the transport, which is where it matters");
        }

        [Test]
        public void Apply_RefusedSwitch_StillAppliesConfigNumbers_SoCriticalRuleSevenStaysOn()
        {
            // Owner decision 1, said on the applier's side: a value the parse
            // could not read falls back to NetConfig's numbers — the SAFE
            // half — so a typo in a launch script cannot quietly stand the
            // simulator down and hand back a playtest whose verdict on
            // netcode means nothing. The parser's own tests pin the refusal
            // as a VALUE; this pins what the transport then runs.
            //
            // FAILS ON THE STUB at the last assertion, for the same reason
            // the unflagged-launch test does: a constant answers a refusal
            // and an `off` launch identically, and those two must never be
            // the same simulator.
            DevLatencyOptions refused = Options("120abc");
            Assert.IsNotNull(refused.Complaint,
                "fixture premise: this value is a refusal, not an override");
            Assert.AreEqual(DevLatencyMode.UseConfig, refused.Mode, "fixture premise");

            var (sim, net, stats) = Fixture();
            DevLatencySetup.Apply(sim, net, stats, refused);

            Assert.AreEqual(DevLatencySetup.OneWayLatencyMs(net.LatencySimRttMs),
                sim.GetLatency(),
                "a refused switch runs on the asset's round trip — Critical Rule 7 stays on");
            Assert.AreEqual(net.LatencySimLossPercent / 100.0, sim.GetPacketLost(), 1e-9d);
            Assert.IsTrue(stats.LatencySimActive);
            Assert.AreEqual(net.LatencySimLossPercent, stats.LatencySimLossPercent);

            Assert.AreNotEqual(OffLaunchReportsActive(), stats.LatencySimActive,
                "a value nobody could read must not stand the simulator down: the complaint is "
                + "the caller's to print, the numbers stay NetConfig's");
        }

        /// `Describe` is the ONLY report an operator gets of what a launch line
        /// did to this process, on both sides of the link, and it is production
        /// code like any other. These four assertions exist because the method
        /// arrived without them: a review measured four separate mutations of
        /// it that the whole 912-test run survived, which is the definition of
        /// untested rather than of trusted.
        ///
        /// EACH ASSERTION NAMES A MUTATION IT KILLS, and that is how their
        /// discriminating power was checked rather than assumed (lesson 168).
        [Test]
        public void Describe_ReportsWhatWasAPPLIED_NotWhatWasTyped()
        {
            var (sim, net, stats) = Fixture();

            // THE LOSS HALF IS WHERE THIS INPUT'S TYPED AND APPLIED DIFFER.
            // Both halves are bounded — `OneWayLatencyMs` has its own ceiling,
            // pinned by this file's `int.MaxValue` test — but 100000 sits
            // BELOW that ceiling, so the round trip comes back verbatim and
            // cannot discriminate anything here, while 250% cannot survive
            // `ClampedLossFraction`. Measured, twice: an earlier draft of this
            // test asserted the round trip would differ, the run refused it,
            // and a second draft calling the round trip unbounded was refused
            // by the neighboring test.
            DevLatencyOptions huge = Options("100000/250");
            DevLatencySetup.Apply(sim, net, stats, huge);
            string describedHuge = DevLatencySetup.Describe(huge, stats);

            Assert.IsFalse(describedHuge.Contains("250"),
                "kills `stats.LatencySimLossPercent -> options.LossPercent`: the doc promises "
                + "APPLIED facts, and 250% is not a thing a packet can do");

            // `off` is not a shade of "applied": it is the one state in which
            // nothing reaches the transport at all.
            var (sim2, net2, stats2) = Fixture();
            DevLatencyOptions off = Options(DevLatencyOptions.OffValue);
            DevLatencySetup.Apply(sim2, net2, stats2, off);

            StringAssert.Contains(DevLatencyOptions.OffValue,
                DevLatencySetup.Describe(off, stats2),
                "kills inverting or deleting the `Off` branch: a stood-down simulator that "
                + "reports NetConfig's numbers is exactly the indistinguishability this switch "
                + "was added to remove");

            // The three sources are three different sentences, and each is
            // asserted BY ITS OWN WORDS. Comparing two of them for inequality
            // was the first draft and it was too weak to say so: a permutation
            // of three distinct strings keeps them distinct, so it survived —
            // measured on two permutations, not reasoned about.
            var (sim3, net3, stats3) = Fixture();
            DevLatencyOptions rttOnly = Options("120");
            DevLatencySetup.Apply(sim3, net3, stats3, rttOnly);

            StringAssert.Contains("with NetConfig's loss",
                DevLatencySetup.Describe(rttOnly, stats3),
                "kills swapping the `source` branches: an operator who named only the round "
                + "trip must be told in words that the loss half is still the asset's");
            StringAssert.Contains("NetConfig's numbers",
                DevLatencySetup.Describe(default, stats3),
                "kills the same swap from the other side: no switch at all reads as the "
                + "asset's numbers, and nothing else does");

            // Fractional loss is printed by a machine and read by a human on
            // another machine; a comma decided by the operator's locale is a
            // number that means something else in half the world. The locale is
            // SET here rather than inherited, exactly as the parser's own
            // culture test sets it: a green that depends on the machine's LANG
            // is not a green.
            var (sim4, net4, stats4) = Fixture();
            DevLatencyOptions fractional = Options("120/2.5");
            DevLatencySetup.Apply(sim4, net4, stats4, fractional);

            CultureInfo previous = CultureInfo.CurrentCulture;
            var commaDecimal = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commaDecimal.NumberFormat.NumberDecimalSeparator = ",";
            try
            {
                CultureInfo.CurrentCulture = commaDecimal;
                StringAssert.Contains("2.5", DevLatencySetup.Describe(fractional, stats4),
                    "kills `InvariantCulture -> CurrentCulture`: under this locale the ambient "
                    + "format would print 2,5, which reads as twenty-five in half the world");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
