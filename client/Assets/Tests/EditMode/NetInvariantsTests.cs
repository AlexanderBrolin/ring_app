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
            // TWO NUMBERS MOVE HERE BECAUSE TWO NUMBERS ARE NOW TIED, and that
            // is a consequence of invariant #11 rather than a workaround for
            // it. `AssertOnly` demands EXACTLY ONE violation, which is the
            // whole point of it (lesson 129: "some error came back" would pass
            // for a validator that reported the wrong rule). This fixture used
            // to isolate #1 by moving one field, and it isolated it only for
            // as long as `InterpBufferTicks` was tied to fields nobody read
            // back — #3 and #4 state THEMSELVES against it, so they move with
            // it and stay satisfied. app-88jb Т24 tied a SIMULATION field to
            // it as well (#11: the server must rewind to the tick the client
            // draws), and a simulation field does not follow along on its own:
            // left at 3 against a buffer of 0 it reports a second, entirely
            // TRUE violation.
            // Suppressing #11 for this fixture was the wrong repair and was
            // rejected: this validator's contract is to collect EVERY
            // violation, pinned by EveryViolationIsReported_NotJustTheFirst
            // below. So the fixture states the whole configuration it means --
            // a client that does not interpolate is a server that does not
            // rewind -- and #1 is again the only thing wrong with it.
            // Zero is legal for RewindPictureTicks on both sides of that
            // sentence: the builder's rule 12 bounds it from above only (and
            // is not on this path at all -- a hand-built SimConfig never
            // passes through SimConfigBuilder).
            //   A THIRD NUMBER JOINED THE FIXTURE WITH app-88jb Т7, and for the
            // same reason the second one did: #11 is no longer the only rule
            // here that reads RewindPictureTicks. #13 asks that the picture
            // plus Net.RewindSanityTicks reach Arena.RewindCapTicks, and with
            // the picture at zero the shipped tolerance alone does not reach
            // the shipped cap -- a second, entirely TRUE violation of a rule
            // this fixture is not about. The four-field move its neighbor
            // RewindSanityTicksNegative_IsReported uses cannot help here,
            // because THIS fixture needs the buffer at zero. So it finishes the
            // sentence above instead: a server that does not rewind has no
            // compensation window to cap either. With the cap at zero #13 holds
            // identically -- any tolerance >= 0 reaches it -- and #1 is again
            // the only thing wrong here.
            //   AND THE LOWERED CAP EARNS ITS KEEP TWICE: this is the only
            // fixture in the file whose Arena.RewindCapTicks is not the shipped
            // 5, so it is also what refuses a #13 written against a literal cap
            // -- under that substitution this fixture reports two violations
            // and AssertOnly fails.
            sim.Arena.RewindPictureTicks = 0;
            sim.Arena.RewindCapTicks = 0;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Net.InterpBufferTicks");
        }

        [Test]
        public void RaidCannotOutlastItsOwnEndgame_IsReported()
        {
            // Spec §3.5 Р255/Р300, the ONE cross-check that decision left
            // standing after it deleted the second duration number — and the
            // one nothing implemented (Ф5 gate, review A-2). Its home is here
            // because Р72 says only the nodes holding BOTH configs may state
            // it, and this validator is exactly that node.
            //
            // A raid whose gate delay plus extraction channel do not FIT
            // inside its own duration cannot be won by anybody: the Director
            // could die on tick one and the gate would still open too late to
            // walk through. That is a configuration bug, not a player's late
            // gamble (which Р300 deliberately refuses to validate).
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            sim.Flow.GateDelaySeconds = net.MatchMaxDurationSeconds;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Flow.GateDelaySeconds");
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

            string[] errors = NetInvariants.Validate(net, in sim, FittingMtu(net), sceneTickRate);
            AssertOnly(errors, "Net.TickRate");

            // Ф8 gate, re-review M-1: invariants #6 and #8 now share a subject,
            // so a prefix assert alone cannot tell which of the two answered —
            // and the gate's own requirement was that the message name BOTH
            // numbers. Without the line below, deleting the TimeManager half of
            // the message leaves this suite green.
            StringAssert.Contains("TimeManager.TickRate", errors[0]);
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

        // ==================================================================
        // Invariant #9 (Stage 2 Task 47c): NetConfig.EntityFadeTicks — the
        // duration of a stranger's doll fade, which moved out of a
        // NetworkSimBackend constant and into the asset the moment a reader
        // for it existed.
        // ==================================================================

        [Test]
        public void EntityFadeTicksZero_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // Zero does not mean "fade instantly" anywhere it is read:
            // `StalePolicy` floors its own `fadeTicks` at 1 (its fix-round-1
            // finding I-1), so a zero here is silently retuned to one tick
            // rather than honored — and the operator who asked for it never
            // learns that the asset said something the policy refused.
            net.EntityFadeTicks = 0;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Net.EntityFadeTicks");
        }

        [Test]
        public void EntityFadeTicksNegative_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // The floor has to be closed on both sides of zero: a hand-edited
            // YAML never passes through the `[Range]` slider (this class's own
            // type doc, Р115), so the only thing standing between a negative
            // tick count and `StalePolicy`'s clamp is this rule.
            net.EntityFadeTicks = -1;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Net.EntityFadeTicks");
        }

        [Test]
        public void EntityFadeTicksOne_IsLegal()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // Positive witness (lesson 129): one tick is the shortest fade
            // `StalePolicy` can actually run, and the rule is `> 0` rather than
            // some larger minimum — without this line a validator that demanded
            // any floor at all would pass the two negatives above.
            net.EntityFadeTicks = 1;

            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "one tick is a legal, if extreme, fade duration — the rule is > 0");
        }

        [Test]
        public void RewindPictureTicksDisagreesWithInterpBuffer_IsReported()
        {
            // Invariant #11 (app-88jb Т24 fix-round; spec §3.6, Н24/Р407).
            // The server rewinds Arena.RewindPictureTicks to ask "where was
            // the target"; the client draws Net.InterpBufferTicks behind the
            // newest frame. Drift between them is not a rounding error — it is
            // |difference| ticks of systematic uncompensated lag on every
            // shot, i.e. exactly the defect lag compensation exists to remove,
            // put back by configuration.
            //
            // THE SIMULATION SIDE IS THE ONE BROKEN HERE, deliberately.
            // Raising Net.InterpBufferTicks instead would trip #3 and #4 as
            // well (GhostConfirmTicks and LingerTicks are both stated against
            // it), and AssertOnly demands exactly one violation — so a
            // fixture that broke the network side would be testing three rules
            // at once and pinning none of them. RewindPictureTicks appears in
            // no other rule, so moving it isolates this one.
            //
            // A SECOND REASON THE PAIR HAS TO AGREE, AS OF app-88jb Т29, and it
            // is on the SERVER's side rather than the doll's: the rewind sanity
            // check weighs a claimed depth against half the round trip plus
            // Arena.RewindPictureTicks plus the owner's tolerance
            // (NetConfig.RewindSanityTicks — dropped from an earlier wording of
            // this paragraph, fix-round B-10;
            // MatchServer.SanitizedRewindDepth carries the whole formula), so a
            // pair that had drifted apart would make that comparison wrong by
            // the drift on every shot — and wrong SILENTLY, since the estimate
            // stays a plausible tick count and nothing downstream can tell it
            // apart from a correct one. This rule is what keeps the two
            // numbers the same number.
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            sim.Arena.RewindPictureTicks = net.InterpBufferTicks + 1;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Arena.RewindPictureTicks");
        }

        [Test]
        public void RewindPictureTicksEqualToInterpBuffer_IsLegal()
        {
            // Positive witness (lesson 129), and it carries a claim the blanket
            // AllInvariantsHold test above does not: that the two SHIPPED
            // numbers actually agree. They live in different assets by design
            // (Р52 keeps NetConfig out of SimConfig), so nothing but this
            // invariant makes them equal, and nothing but this line notices
            // the day one of them is retuned alone.
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();

            Assert.AreEqual(net.InterpBufferTicks, sim.Arena.RewindPictureTicks,
                "премиса: ArenaConfig и NetConfig приехали с разными числами — " +
                "инвариант #11 обязан их связывать, а фикстура обязана его удовлетворять");
            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "the shipped pair must satisfy #11: " + string.Join(" | ",
                    NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate)));
        }

        [Test]
        public void RewindPictureTicksAndInterpBuffer_MayMoveTogether()
        {
            // #11 PINS AN EQUALITY, NOT THE NUMBER 3, and this is the only
            // test that says so. The one above it stands on the SHIPPED pair,
            // where both fields happen to be 3 — so it is satisfied by a rule
            // written `RewindPictureTicks != 3`, or `!= InterpBufferTicks &&
            // != 3`, or any other form that pins the current value instead of
            // the relationship. This fixture moves the pair OFF that value and
            // still demands silence, so a rule pinning a constant fails here
            // and only here.
            //
            // THE SCENARIO IS THE ONE THE RULE'S OWN COMMENT NAMES: raising
            // the interpolation buffer 3 -> 5. That is a legitimate retune —
            // a lossier network wants a deeper buffer — and the whole point of
            // #11 is that it must drag the server's rewind depth along with
            // it, not forbid the move.
            //
            // THE TWO NEIGHBOURS ARE MOVED TO THEIR OWN BOUNDARY VALUES, and
            // stated as RELATIONSHIPS rather than as numbers (this file's own
            // header asks for exactly that). GhostConfirmTicks would in fact
            // have survived untouched — its default 12 already exceeds 5 — but
            // a fixture that leaned on that would break the day an unrelated
            // default moves, and would then look like a failure of #11.
            //   #1  InterpBufferTicks > 0                  -> 5
            //   #3  GhostConfirmTicks > InterpBufferTicks  -> 6, first legal
            //   #4  LingerTicks >= InterpBufferTicks + 2   -> 7, first legal
            //   #11 RewindPictureTicks == InterpBufferTicks -> 5
            // Nothing else in this validator reads any of the four (checked
            // rule by rule), and nothing caps the pair from above at all: the
            // only ceiling on InterpBufferTicks anywhere is a [Range] on the
            // asset, which Р115 records as an Inspector hint enforced nowhere.
            // Five is chosen to stay inside it regardless, so the fixture
            // describes a configuration an operator could really dial in.
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            Assert.AreNotEqual(5, net.InterpBufferTicks,
                "премиса: фикстура обязана уводить пару С дефолта, иначе она " +
                "повторяет тест выше и мутанта на константу не различает");

            net.InterpBufferTicks = 5;
            sim.Arena.RewindPictureTicks = net.InterpBufferTicks;
            net.GhostConfirmTicks = net.InterpBufferTicks + 1;
            sim.Visibility.LingerTicks = net.InterpBufferTicks + 2;

            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "пара уехала вместе и осталась законной — #11 обязан пинить равенство, " +
                "а не значение: " + string.Join(" | ",
                    NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate)));
        }

        // ==================================================================
        // Invariants #12 and #13, both about NetConfig.RewindSanityTicks — the
        // tolerance the server's rewind sanity check allows itself on top of
        // its own estimate.
        //   #12 (app-88jb Т29 review round, owner decision 4б) closes the field
        // at zero from below: under it the check does not tighten, it inverts;
        // the rule's own doc carries the measurement.
        //   #13 (app-88jb Т7) states the field's real lower bound against two
        // SimConfig numbers -- RewindSanityTicks >= Arena.RewindCapTicks -
        // Arena.RewindPictureTicks -- because below it the server's estimate
        // sinks under the cap while the client keeps drawing at the cap. It
        // REVOKED the "zero is always legal" half of #12's doc, which is why
        // the fixture that asserted that half is gone from this block and
        // RewindSanityTicksBelowTheFloor_IsReported stands in its place.
        //   THE TWO SHARE A SUBJECT, so AssertOnly's prefix cannot tell them
        // apart on its own: every fixture below either keeps one of the two out
        // of the answer by construction and says how, or names a number only
        // one of the messages carries.
        // ==================================================================

        [Test]
        public void RewindSanityTicksNegative_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // MEASURED, and it is why the rule exists (the finding both
            // reviewers raised independently, A-1/B-4): at -4, with the shipped
            // picture of 3 and the round trip a dedicated server actually reads
            // (zero), `MatchServer.SanitizedRewindDepth` computes 0 + 3 - 4 =
            // -1, the minimum of the three terms is -1, and the `(byte)` cast
            // answers 255 — which `SimInputSanitizer.Sanitize` inside `TickAll`
            // then clamps to `Arena.RewindCapTicks`. A tolerance the operator
            // wrote to be STRICTER is therefore the most permissive value the
            // field can hold: every collector gets the full compensation
            // window, including the one who claimed nothing.
            //   -4 AND NOT -1, AND THE DIFFERENCE IS REAL rather than a matter
            // of taste: the sum is `0 + 3 + sanityTicks` on a shipped server,
            // so -1..-3 leave it non-negative and merely make the estimate
            // stricter than the owner asked for — -4 is the first value that
            // crosses zero and wraps. The rule refuses all four alike (a
            // negative tolerance is a mode at no depth), and this fixture
            // stands on the one where the inversion is visible.
            net.RewindSanityTicks = -4;
            //   FOUR FIELDS MOVE WITH IT SINCE app-88jb Т7, AND WHY THEY MOVE
            // AS A FOUR IS THE POINT. Rule #13 asks that
            // Arena.RewindPictureTicks plus this tolerance reach
            // Arena.RewindCapTicks, so a negative tolerance on the shipped
            // picture is TWO violations, while AssertOnly demands exactly one:
            // a fixture reporting both would be testing two rules and pinning
            // neither. Raising the picture alone is not available -- #11
            // requires Net.InterpBufferTicks to name the same number, and #3
            // and #4 are stated against that in turn -- so the four move
            // together and as RELATIONSHIPS, the idiom
            // RewindPictureTicksAndInterpBuffer_MayMoveTogether above
            // established:
            //   #13 InterpBufferTicks = RewindCapTicks - RewindSanityTicks -> 9
            //   #11 RewindPictureTicks == InterpBufferTicks               -> 9
            //   #3  GhostConfirmTicks > InterpBufferTicks                 -> 10
            //   #4  LingerTicks >= InterpBufferTicks + 2                  -> 11
            // The sum is then 9 - 4 = 5, exactly the cap, so #13 is silent and
            // #12 is the single violation left.
            //   THE MOVE COSTS SOMETHING, AND IT IS NAMED RATHER THAN HIDDEN:
            // with the buffer this deep the estimate is 5, not -1, so this
            // fixture no longer stands on the WRAP the paragraph above
            // measures. It stands on the other half of #12 -- that a negative
            // tolerance is a value no band admits at any depth. The wrapping
            // configuration is unreachable under #13 by arithmetic and not by
            // taste: the sum has to stay >= the cap, so a sum below zero would
            // need a cap below zero. What is pinned here is therefore that #12
            // still answers wherever #13 is satisfied -- a picture deeper than
            // its own cap, which SimConfigBuilder's rule 12 refuses and a
            // hand-built SimConfig like this one reaches.
            net.InterpBufferTicks = sim.Arena.RewindCapTicks - net.RewindSanityTicks;
            sim.Arena.RewindPictureTicks = net.InterpBufferTicks;
            net.GhostConfirmTicks = net.InterpBufferTicks + 1;
            sim.Visibility.LingerTicks = net.InterpBufferTicks + 2;

            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Net.RewindSanityTicks");
        }

        [Test]
        public void RewindSanityTicksBelowTheFloor_IsReported()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // ⭐ THIS FIXTURE REPLACES `RewindSanityTicksZero_IsLegal`, WHICH
            // ASSERTED PRECISELY THE HALF OF #12 THAT app-88jb Т7 REVOKED. Zero
            // used to be "the strictest setting the field has", the same shape
            // as #7's "do not slew"; rule #13 puts a floor under the field at
            // Arena.RewindCapTicks - Arena.RewindPictureTicks, because below it
            // the server's rewind estimate sinks under the cap while the client
            // goes on drawing its own shot at the cap -- the picture running
            // ahead of the judge, 1.75 m of flight per tick of divergence. The
            // old fixture could not be repaired, only replaced: what it claimed
            // is now false at the shipped numbers (picture 3, cap 5).
            //
            // THE FLOOR IS AN EXPRESSION OVER THE FIXTURE'S OWN FIELDS, never
            // the number those fields happen to hold today -- this file's
            // header asks for exactly that, and MayMoveTogether above enforces
            // the same discipline for #11.
            int floor = sim.Arena.RewindCapTicks - sim.Arena.RewindPictureTicks;
            Assert.Greater(floor, 0,
                "премиса: пол обязан стоять строго выше нуля, иначе `floor - 1` уходит под " +
                "ноль, отвечает правило #12, и фикстура пинит не тот инвариант");

            // BOTH SIDES OF THE BOUNDARY, the idiom `Boundaries_AreExact` above
            // uses: one tick below the floor is refused, the floor itself is
            // silent. Either half alone would be satisfied by a rule off by a
            // tick in its own direction.
            net.RewindSanityTicks = floor - 1;
            string[] errors = NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate);
            AssertOnly(errors, "Net.RewindSanityTicks");
            // #12 AND #13 SHARE A SUBJECT, so the prefix alone cannot say which
            // of the two answered -- the same hole
            // SceneTickRateDisagreesWithNetConfig_IsReported closes for #6 and
            // #8. The premise above keeps #12 out of the answer (floor - 1
            // stays >= 0), and this line proves the message is the one that
            // weighs the cap: #12's names no cap at all.
            StringAssert.Contains("RewindCapTicks", errors[0]);

            // At the shipped numbers this second half lands on the same value
            // RewindSanityTicksShippedTolerance_IsLegal below stands on, and
            // the two are still different claims -- that one witnesses the
            // SHIPPED tolerance, this one the COMPUTED floor. Their arithmetic
            // coincides only while the shipped tolerance sits exactly on the
            // floor; TheFloorFollowsThePictureDepth_NotAShippedLiteral below
            // moves the picture so that no fixture can rest on the coincidence.
            net.RewindSanityTicks = floor;
            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "ровно на полу допуск законен — правило #13 стоит на `>=`, а не на `>`: " +
                string.Join(" | ",
                    NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate)));
        }

        [Test]
        public void RewindSanityTicksShippedTolerance_IsLegal()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // Positive witness (lesson 129) on the SHIPPED number, and what it
            // adds over the blanket AllInvariantsHold test above is stated
            // rather than assumed: a floor mutated to any value above the
            // shipped tolerance fails both, but this one fails NAMING the
            // field, and it asserts the premise the witness rests on instead of
            // leaning on it silently. There is no separate "first legal value"
            // fixture the way #9 needed `EntityFadeTicksOne_IsLegal`, and since
            // app-88jb Т7 that is true for a second reason rather than the one
            // written here first: the field's first legal value is no longer
            // #12's zero but #13's floor, and the case above stands on exactly
            // that boundary, both sides of it. What this fixture adds is that
            // the number the project SHIPS is on the legal side — a claim about
            // the asset, which a fixture computing its own floor cannot make.
            Assert.Greater(net.RewindSanityTicks, 0,
                "премиса: отгруженный допуск обязан быть строго положительным, иначе эта " +
                "фикстура повторяет проверку нуля выше и ничего своего не свидетельствует");

            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "отгруженный допуск обязан быть законным: " + string.Join(" | ",
                    NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate)));
        }

        [Test]
        public void TheFloorFollowsThePictureDepth_NotAShippedLiteral()
        {
            NetConfig net = DefaultNet();
            SimConfig sim = TestConfigs.Default();
            // ⭐ #13 HAS TWO SUMMANDS AND THE SECOND ONE NEEDS A WITNESS OF ITS
            // OWN. Every other fixture in this file leaves
            // Arena.RewindPictureTicks at the shipped 3, so the substitution
            //     net.RewindSanityTicks < sim.Arena.RewindCapTicks - 3
            // -- a shipped literal where the picture depth belongs -- survives
            // all of them; checked case by case rather than assumed, and it is
            // exactly the class of defect this project has already paid for.
            // This fixture moves the picture OFF that value and demands the
            // floor move with it: under the literal the floor would stay at 2
            // while the real one drops to 1, so the legal half below fails
            // there and only there.
            //
            // THE PAIR MOVES AS A FOUR, the idiom
            // RewindPictureTicksAndInterpBuffer_MayMoveTogether above
            // established: #11 ties the picture to Net.InterpBufferTicks, and
            // #3 and #4 are stated against that in turn, so moving one field
            // alone would report three violations and pin none of them. THE
            // PICTURE IS RAISED RATHER THAN LOWERED because that direction is
            // the retune the rules' own docs name -- a lossier network wants a
            // deeper buffer -- and because it keeps every neighbor clear of #1
            // and inside the [Range] band an operator could really dial in.
            // Either direction would kill the literal; this one describes a
            // configuration somebody would actually ask for.
            int picture = sim.Arena.RewindPictureTicks + 1;
            int floor = sim.Arena.RewindCapTicks - picture;
            Assert.Greater(floor, 0,
                "премиса: пол при поднятой картинке обязан остаться строго выше нуля, иначе " +
                "`floor - 1` уходит под ноль и отвечает правило #12, а не #13");

            net.InterpBufferTicks = picture;
            sim.Arena.RewindPictureTicks = picture;
            net.GhostConfirmTicks = net.InterpBufferTicks + 1;
            sim.Visibility.LingerTicks = net.InterpBufferTicks + 2;

            net.RewindSanityTicks = floor - 1;
            AssertOnly(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "Net.RewindSanityTicks");

            net.RewindSanityTicks = floor;
            CollectionAssert.IsEmpty(NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate),
                "пол уехал вместе с глубиной картинки — #13 обязан читать " +
                "Arena.RewindPictureTicks, а не отгруженную тройку: " + string.Join(" | ",
                    NetInvariants.Validate(net, in sim, FittingMtu(net), net.TickRate)));
        }
    }
}
