using NUnit.Framework;
using Ring.Data;
using Ring.Networking;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 41 (spec §3.15 validation-homes table, §3.9, Р72/Р154;
    /// plan Т41). Covers `NetInvariants.Validate` — the one place in the
    /// project that sees `NetConfig` and `SimConfig` at the same time, and
    /// therefore the only place the cross-config rules can be checked at all.
    ///
    /// EVERY NEGATIVE TEST BREAKS EXACTLY ONE NUMBER and asserts that the
    /// answer names THAT field. "Some error came back" would be a hollow
    /// assert: a validator whose messages all named the same field would pass
    /// it, and so would one that reported the wrong violation. `AssertOnly`
    /// below therefore pins BOTH the count and the field name (lesson 129).
    ///
    /// FIXTURES ARE `ScriptableObject.CreateInstance` C# DEFAULTS, never
    /// numbers copied out of `Assets/Data/NetConfig.asset` (spec §0's
    /// two-sources-of-numbers rule, lesson 45). Where a test needs a specific
    /// relationship between two numbers it states the relationship — e.g.
    /// `net.GhostConfirmTicks = net.InterpBufferTicks` — rather than the two
    /// values that happen to satisfy it today.
    public class NetInvariantsTests
    {
        static NetConfig DefaultNet() => ScriptableObject.CreateInstance<NetConfig>();

        /// The transport MTU the fixtures pass unless the test is ABOUT the
        /// MTU: exactly one byte of slack above invariant #5's boundary, so a
        /// test that breaks some OTHER number is never simultaneously sitting
        /// on that boundary and reporting two violations.
        static int FittingMtu(NetConfig net) =>
            net.SnapshotMaxBytes + NetInvariants.SnapshotWireOverheadBytes + 1;

        /// Asserts the answer is exactly one violation and that the violation
        /// is ABOUT `fieldName`. Both halves matter: the count catches a
        /// validator that reports collateral damage, the name catches one that
        /// reports the wrong violation.
        ///
        /// THE NAME IS ANCHORED TO THE HEAD OF THE MESSAGE, NOT SEARCHED FOR
        /// ANYWHERE IN IT (fix-round 1). A plain `Contains` is not
        /// discriminating here, because several messages mention a field they
        /// are not about: #3 reads "Net.GhostConfirmTicks must be >
        /// Net.InterpBufferTicks" and #4 reads "Visibility.LingerTicks must be
        /// >= Net.InterpBufferTicks + 2", so `Contains("Net.InterpBufferTicks")`
        /// would be satisfied by a validator that answered #1 with either of
        /// them. Requiring the field to open the sentence tests what the
        /// message actually CLAIMS — every message in this validator is
        /// "&lt;field&gt; must ..." — rather than which words appear in it.
        static void AssertOnly(string[] errors, string fieldName)
        {
            Assert.AreEqual(1, errors.Length,
                "expected exactly one violation, got: " + string.Join(" | ", errors));
            Assert.IsTrue(errors[0].StartsWith(fieldName + " "),
                $"the violation must be ABOUT {fieldName} — it has to be the subject of the " +
                $"message, not merely mentioned in it. Got: {errors[0]}");
        }

        [Test]
        public void AllInvariantsHold_ReturnsNoErrors()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();

            string[] errors = NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate);

            Assert.IsNotNull(errors, "Validate must answer with a value, never null");
            CollectionAssert.IsEmpty(errors,
                "the shipped C# defaults must satisfy every invariant: " + string.Join(" | ", errors));
        }

        [Test]
        public void InterpBufferTicksZero_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            net.InterpBufferTicks = 0;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Net.InterpBufferTicks");
        }

        [Test]
        public void SnapshotEventBudgetZero_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            net.SnapshotEventBudget = 0;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Net.SnapshotEventBudget");
        }

        [Test]
        public void GhostConfirmTicksEqualToInterpBufferTicks_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // Strictly greater is required, so equality is the first failing
            // value — and the one a "> vs >=" slip would let through.
            net.GhostConfirmTicks = net.InterpBufferTicks;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Net.GhostConfirmTicks");
        }

        [Test]
        public void LingerTicksOneBelowRequiredMinimum_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            sim.Visibility.LingerTicks = net.InterpBufferTicks + 1;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Visibility.LingerTicks");
        }

        [Test]
        public void SnapshotMaxBytesOverTransportBudget_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            int mtu = FittingMtu(net);
            // One byte past what this MTU can carry once FishNet's own
            // per-broadcast and per-datagram bytes are paid for.
            net.SnapshotMaxBytes = mtu - NetInvariants.SnapshotWireOverheadBytes + 1;

            AssertOnly(NetInvariants.Validate(net, in sim, mtu, net.TickRate), "Net.SnapshotMaxBytes");
        }

        [Test]
        public void TickRateAboveWorldTickRate_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            net.TickRate = 60;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate), "Net.TickRate");
        }

        [Test]
        public void TickRateOneBelowWorldTickRate_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // 29 vs 30 is the case a naive `TickRate * TickDt == 1f` test
            // would also catch; 60 above is the case a too-loose tolerance
            // would miss. Both are pinned so neither mistake can hide.
            net.TickRate = 29;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate), "Net.TickRate");
        }

        [Test]
        public void SlewFractionAboveBand_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            net.SlewFraction = 0.15f;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate), "Net.SlewFraction");
        }

        [Test]
        public void SlewFractionZero_IsLegal()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // Р154: zero means "do not slew at all" — a deliberate mode
            // (RenderClock.SlewFractionOf reads anything <= 0 that way), not a
            // misconfiguration, so the invariant must NOT reject it.
            net.SlewFraction = 0f;
            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "SlewFraction = 0 is the legal 'slew disabled' mode, not an error");

            // A negative fraction is NOT that mode — it is a value no band
            // admits, and the floor has to be closed or the check would be
            // one-sided.
            net.SlewFraction = -0.01f;
            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate), "Net.SlewFraction");
        }

        [Test]
        public void SlewFractionNaN_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // NaN passes BOTH ordinary comparisons — `NaN < 0f` and
            // `NaN > 0.10f` are each false — so a floor written as `x < 0f`
            // would wave it through, and the NaN would reach the render clock,
            // where `SlewFractionOf` reads it as "do not correct at all"
            // (its own `!(fraction > 0f)` branch names NaN explicitly, and
            // `RenderClockTests.HostileSlewFraction_NeverReversesTime` already
            // pins that). NaN is therefore NOT poisonous downstream — the harm
            // is quieter than that: a typo switches the correction OFF and
            // nothing anywhere says so. Zero means the same thing but is a
            // DELIBERATE mode (Р154); NaN is not a mode, so it has to be
            // reported here, where a human still reads the message. The
            // validator is written as `!(x >= 0f)` precisely to refuse it, and
            // that reasoning is stated in its doc; without this test the doc
            // would be the only thing holding it, and a refactor back to
            // `x < 0f` would pass every other test in the suite (fix-round 1).
            net.SlewFraction = float.NaN;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate), "Net.SlewFraction");
        }

        [Test]
        public void EveryViolationIsReported_NotJustTheFirst()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // Two unrelated numbers broken at once. An operator raising a
            // server with both must learn about both in ONE run, so a
            // validator that returns on the first violation is wrong even
            // though it refuses correctly.
            net.SnapshotEventBudget = 0;
            net.SlewFraction = 0.15f;

            string[] errors = NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate);

            Assert.AreEqual(2, errors.Length,
                "both violations must be reported: " + string.Join(" | ", errors));
            Assert.IsTrue(System.Array.Exists(errors, e => e.Contains("Net.SnapshotEventBudget")),
                "no message named Net.SnapshotEventBudget: " + string.Join(" | ", errors));
            Assert.IsTrue(System.Array.Exists(errors, e => e.Contains("Net.SlewFraction")),
                "no message named Net.SlewFraction: " + string.Join(" | ", errors));
        }

        [Test]
        public void Boundaries_AreExact()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            int mtu = FittingMtu(net);
            int ghostConfirmDefault = net.GhostConfirmTicks;

            // #3: strictly greater. Equal fails, one more holds.
            net.GhostConfirmTicks = net.InterpBufferTicks;
            AssertOnly(NetInvariants.Validate(net, in sim, mtu, net.TickRate), "Net.GhostConfirmTicks");
            net.GhostConfirmTicks = net.InterpBufferTicks + 1;
            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, mtu, net.TickRate),
                "GhostConfirmTicks == InterpBufferTicks + 1 is the first legal value");
            net.GhostConfirmTicks = ghostConfirmDefault;

            // #4: at least InterpBufferTicks + 2. Exactly that holds, one less fails.
            sim.Visibility.LingerTicks = net.InterpBufferTicks + 2;
            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, mtu, net.TickRate),
                "LingerTicks == InterpBufferTicks + 2 is legal — the rule is >=, not >");
            sim.Visibility.LingerTicks = net.InterpBufferTicks + 1;
            AssertOnly(NetInvariants.Validate(net, in sim, mtu, net.TickRate), "Visibility.LingerTicks");
            sim.Visibility.LingerTicks = net.InterpBufferTicks + 2;

            // #5: the whole budget may be spent. Exactly the budget holds, one
            // byte more fails.
            int budget = mtu - NetInvariants.SnapshotWireOverheadBytes;
            net.SnapshotMaxBytes = budget;
            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, mtu, net.TickRate),
                "SnapshotMaxBytes may use the whole budget — the rule is <=, not <");
            net.SnapshotMaxBytes = budget + 1;
            AssertOnly(NetInvariants.Validate(net, in sim, mtu, net.TickRate), "Net.SnapshotMaxBytes");
        }

        [Test]
        public void NullNetConfig_Throws()
        {
            SimConfig sim = TestConfigs.Default();
            // A missing NetConfig is broken WIRING, not a bad configuration:
            // there is no value to report on yet, so there is nothing the
            // caller could print and act upon. Refusals are values here
            // (§2.1); this one is not a refusal.
            Assert.Throws<System.ArgumentNullException>(
                () => NetInvariants.Validate(null, in sim, 1024, 30));
        }

        // ==================================================================
        // Invariant #8 (Ф8 gate W-1): NetConfig.TickRate vs the scene's own
        // TimeManager.TickRate — a DIFFERENT agreement from #6 above, which
        // only ever compared NetConfig.TickRate against SimulationWorld.
        // TickDt and had nothing to say about the scene actually producing
        // the ticks.
        // ==================================================================

        [Test]
        public void SceneTickRateDisagreesWithNetConfig_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // TickDt/NetConfig still agree (invariant #6 stays satisfied) —
            // only the SCENE's own TimeManager is out of step, isolating #8
            // from #6 the same way every other fixture in this file isolates
            // its own invariant.
            int sceneTickRate = net.TickRate * 2;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), sceneTickRate),
                "Net.TickRate");
        }

        [Test]
        public void SceneTickRateMatchesNetConfig_ReportsNothing()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();

            // Positive witness (lesson 129, this file's own discipline): the
            // negative test above proves a mismatch is CAUGHT; this proves
            // agreement is not itself mistaken for one — without it, a
            // validator that always reported "Net.TickRate" regardless of
            // the fourth argument would still pass the negative test alone.
            CollectionAssert.IsEmpty(
                NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "the scene's TimeManager.TickRate agreeing with Net.TickRate must report nothing");
        }
    }
}
