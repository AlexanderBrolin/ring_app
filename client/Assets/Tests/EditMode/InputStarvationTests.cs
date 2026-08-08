using NUnit.Framework;
using Ring.Networking;
using Ring.Networking.Server;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 36 (spec §3.7 Р22/Р25/Р60, §3.11; plan Task 36; task-34-report
    /// §8.1): `InputStarvation.Effective` — the pure per-player starvation
    /// function `MatchServer.OnPostTick` runs before `world.TickAll` — plus the
    /// two small pure helpers that ride along in `MatchServer.cs`
    /// (`EffectiveInputBatch.Gather`, `TickTimeAccumulator`). None of this needs
    /// FishNet or a `NetworkManager`; `MatchServer`'s own wiring is proven only by
    /// R-COMPILE (Files §1's "the assembler pair" precedent — SnapshotAssembler is
    /// unit-tested, the FishNet glue around it is not).
    ///
    /// `StarveTicks = 3` throughout (§2.3's "маленький рукотворный, НЕ 10 из
    /// .asset") — small enough that "inside the window" and "past it" are two or
    /// three concrete tick numbers apart, not ten.
    public class InputStarvationTests
    {
        const int StarveTicks = 3;

        /// Every field set to a distinct, non-default value — a stub that
        /// forgets to carry a field (or clears the wrong one) is observably
        /// wrong rather than accidentally right. Same fixture discipline as
        /// ReconcileCodecTests.FilledPlayerState (Stage 2 Task 34), by hand
        /// here since SimInput has only six fields.
        static SimInput Filled() => new SimInput
        {
            MoveDir = new float2(0.6f, 0.8f),
            AimPoint = new float2(12.5f, -7.25f),
            AimHeight = 1.75f,
            FireHeld = true,
            DashRequested = true,
            SlideRequested = true,
            AimHeld = true,
        };

        static void AssertMovementAndAimPreserved(in SimInput last, in SimInput result)
        {
            Assert.AreEqual(last.MoveDir.x, result.MoveDir.x);
            Assert.AreEqual(last.MoveDir.y, result.MoveDir.y);
            Assert.AreEqual(last.AimPoint.x, result.AimPoint.x);
            Assert.AreEqual(last.AimPoint.y, result.AimPoint.y);
            Assert.AreEqual(last.AimHeight, result.AimHeight);
        }

        // ---- plan tests (1-4) ---------------------------------------------

        [Test]
        public void HoldWindow_RepeatsLastWithEdgeFlagsCleared()
        {
            SimInput last = Filled();

            SimInput result = InputStarvation.Effective(in last, 1, StarveTicks, out bool starved);

            Assert.IsFalse(result.DashRequested, "a held dash-request would re-fire the dash every held tick");
            Assert.IsFalse(result.SlideRequested);
            Assert.IsFalse(starved);
            // Witness: this is a REPEAT, not a starvation stand-in — movement
            // and aim ride along untouched.
            AssertMovementAndAimPreserved(in last, in result);
            Assert.IsTrue(result.FireHeld, "FireHeld is held, not edge — it must survive the repeat");
        }

        [Test]
        public void PastTheWindow_ZeroesMovementAndFire()
        {
            SimInput last = Filled();

            SimInput result = InputStarvation.Effective(in last, StarveTicks + 1, StarveTicks, out bool starved);

            Assert.AreEqual(0f, result.MoveDir.x);
            Assert.AreEqual(0f, result.MoveDir.y);
            Assert.IsFalse(result.FireHeld);
            Assert.IsFalse(result.DashRequested);
            Assert.IsFalse(result.SlideRequested);
            Assert.IsTrue(starved);
        }

        [Test]
        public void AimHeldAndAimSurviveBothTheHoldWindowAndStarvation()
        {
            SimInput last = Filled();

            SimInput held = InputStarvation.Effective(in last, 1, StarveTicks, out _);
            Assert.IsTrue(held.AimHeld);
            Assert.AreEqual(last.AimPoint.x, held.AimPoint.x);
            Assert.AreEqual(last.AimPoint.y, held.AimPoint.y);
            Assert.AreEqual(last.AimHeight, held.AimHeight);

            SimInput starving = InputStarvation.Effective(in last, StarveTicks + 2, StarveTicks, out _);
            Assert.IsTrue(starving.AimHeld, "the aim must not jerk while starved (Р25)");
            Assert.AreEqual(last.AimPoint.x, starving.AimPoint.x);
            Assert.AreEqual(last.AimPoint.y, starving.AimPoint.y);
            Assert.AreEqual(last.AimHeight, starving.AimHeight);
        }

        [Test]
        public void StarvedFlag_IsFalseFalseTrueAcrossTheThreeRegimes()
        {
            SimInput last = Filled();

            InputStarvation.Effective(in last, 0, StarveTicks, out bool freshStarved);
            InputStarvation.Effective(in last, StarveTicks, StarveTicks, out bool heldStarved);
            InputStarvation.Effective(in last, StarveTicks + 1, StarveTicks, out bool starvingStarved);

            Assert.IsFalse(freshStarved);
            Assert.IsFalse(heldStarved);
            Assert.IsTrue(starvingStarved);
        }

        // ---- coordinator tests (5-7) ----------------------------------------

        [Test]
        public void Fresh_PassesThroughUntouchedWithLiveEdgeFlags()
        {
            SimInput last = Filled();

            SimInput result = InputStarvation.Effective(in last, 0, StarveTicks, out bool starved);

            // The whole point of this test: mode 0 must NOT collapse into mode
            // 1 — a fresh input's edge requests are still live, unlike a held
            // repeat's.
            Assert.IsTrue(result.DashRequested);
            Assert.IsTrue(result.SlideRequested);
            Assert.IsTrue(result.FireHeld);
            Assert.IsTrue(result.AimHeld);
            AssertMovementAndAimPreserved(in last, in result);
            Assert.IsFalse(starved);
        }

        [Test]
        public void WindowBoundary_EqualsHolds_OneMoreStarves()
        {
            SimInput last = Filled();

            SimInput atBoundary = InputStarvation.Effective(in last, StarveTicks, StarveTicks, out bool boundaryStarved);
            Assert.IsFalse(boundaryStarved, "ticksSinceLast == starveTicks is still the hold window (<=)");
            Assert.AreEqual(last.MoveDir.x, atBoundary.MoveDir.x, "a hold repeat must not zero movement");
            Assert.IsFalse(atBoundary.DashRequested);

            SimInput pastBoundary = InputStarvation.Effective(in last, StarveTicks + 1, StarveTicks, out bool pastStarved);
            Assert.IsTrue(pastStarved);
            Assert.AreEqual(0f, pastBoundary.MoveDir.x);
        }

        [Test]
        public void NegativeTicksSinceLast_ClampsToFresh()
        {
            SimInput last = Filled();

            SimInput result = InputStarvation.Effective(in last, -5, StarveTicks, out bool starved);

            Assert.IsFalse(starved);
            Assert.IsTrue(result.DashRequested, "clamped-to-fresh must behave exactly like ticksSinceLast == 0");
            Assert.IsTrue(result.SlideRequested);
            AssertMovementAndAimPreserved(in last, in result);
        }

        [Test]
        public void NonPositiveStarveTicks_StarvesFromTheFirstHeldTick()
        {
            SimInput last = Filled();

            SimInput result = InputStarvation.Effective(in last, 1, 0, out bool starved);

            Assert.IsTrue(starved, "starveTicks <= 0 means there is no hold window at all");
            Assert.AreEqual(0f, result.MoveDir.x);
            Assert.IsFalse(result.FireHeld);
        }

        // ---- tick-time accumulator (test 8) --------------------------------

        [Test]
        public void TickTimeAccumulator_TracksCountAverageAndRunningMax()
        {
            var acc = new TickTimeAccumulator();

            acc.Record(5.0);
            acc.Record(9.0);
            acc.Record(3.0);

            Assert.AreEqual(3, acc.Count);
            // The running max, not the LAST recorded value (3.0) — a stub that
            // just overwrites would report 3.0 here.
            Assert.AreEqual(9.0, acc.MaxMs);
            Assert.AreEqual((5.0 + 9.0 + 3.0) / 3.0, acc.AverageMs, 1e-9);

            acc.Reset();

            Assert.AreEqual(0, acc.Count);
            Assert.AreEqual(0.0, acc.AverageMs);
            Assert.AreEqual(0.0, acc.MaxMs);

            acc.Record(10.0);
            Assert.AreEqual(1, acc.Count);
            Assert.AreEqual(10.0, acc.AverageMs);
            Assert.AreEqual(10.0, acc.MaxMs);
        }

        // ---- pure batch gather (test 9) ------------------------------------

        [Test]
        public void Gather_CombinesAFreshAHeldAndAStarvingPlayerInOneTick()
        {
            const int worldTick = 100;
            SimInput fresh = Filled();
            SimInput held = Filled();
            SimInput starving = Filled();

            var lastInputs = new[]
            {
                new ServerTickInput(worldTick, in fresh),           // ticksSinceLast 0
                new ServerTickInput(worldTick - 2, in held),        // ticksSinceLast 2 (<= 3)
                new ServerTickInput(worldTick - 10, in starving),   // ticksSinceLast 10 (> 3)
            };
            var effective = new SimInput[3];
            var starvedFlags = new bool[3];

            int starvedCount = EffectiveInputBatch.Gather(lastInputs, worldTick, StarveTicks, effective, starvedFlags);

            Assert.AreEqual(1, starvedCount);
            CollectionAssert.AreEqual(new[] { false, false, true }, starvedFlags);

            // Player 0 — fresh: untouched, edges alive.
            Assert.IsTrue(effective[0].DashRequested);
            Assert.AreEqual(fresh.MoveDir.x, effective[0].MoveDir.x);

            // Player 1 — held: edges cleared, movement alive.
            Assert.IsFalse(effective[1].DashRequested);
            Assert.AreEqual(held.MoveDir.x, effective[1].MoveDir.x);

            // Player 2 — starving: movement/fire zeroed, aim alive.
            Assert.AreEqual(0f, effective[2].MoveDir.x);
            Assert.IsFalse(effective[2].FireHeld);
            Assert.IsTrue(effective[2].AimHeld);
        }

        [Test]
        public void Gather_MismatchedSpanLengths_Throws()
        {
            var lastInputs = new ServerTickInput[2];
            var effectiveTooShort = new SimInput[1];
            var starvedFlags = new bool[2];

            Assert.Throws<System.ArgumentException>(() =>
                EffectiveInputBatch.Gather(lastInputs, 0, StarveTicks, effectiveTooShort, starvedFlags));
        }
    }
}
