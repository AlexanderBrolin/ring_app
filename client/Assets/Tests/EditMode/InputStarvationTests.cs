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
    /// `StarveTicks = 3` throughout (task-36-brief §2.3: a small handwritten
    /// value, deliberately NOT the shipped default of 10 from
    /// `NetConfig.InputStarveTicks`) — small enough that "inside the window"
    /// and "past it" are two or three concrete tick numbers apart, not ten.
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

        // ---- pure batch gather (test 9, rewritten fix-round 1 C1) ----------
        //
        // C1 (CRITICAL, fix-round 1): the ORIGINAL form of this test built
        // `ServerTickInput`s with ticks in the SAME numeric domain as the
        // world tick (`worldTick - 2`, `worldTick - 10`) — which is exactly
        // the premise C1 found wrong in `MatchServer` itself
        // (`ServerTickInput.Tick` is FishNet's own tick, never the world's;
        // see `EffectiveInputBatch.Gather`'s class doc for the full account).
        // Every test below now drives `Gather` the way `MatchServer.
        // OnPostTick` actually does: several SEQUENTIAL calls sharing the
        // SAME persistent `lastSeenInputTick`/`lastFreshWorldTick` state, with
        // replicate ticks living in a deliberately FOREIGN numeric domain
        // (large, arbitrary offsets) to prove the result never depends on
        // that domain's relationship to the world tick's.

        /// One player's freshness state, reset before each test that needs a
        /// clean slate — the production sentinel (`uint.MaxValue`) lives in
        /// `MatchServer.StartMatch`; mirrored here since this file drives the
        /// pure function directly, the way `MatchServer` itself never does.
        static (uint[] lastSeen, int[] lastFresh) FreshState(int playerCount)
        {
            var lastSeen = new uint[playerCount];
            var lastFresh = new int[playerCount];
            for (int i = 0; i < playerCount; i++) lastSeen[i] = uint.MaxValue;
            return (lastSeen, lastFresh);
        }

        [Test]
        public void Gather_FreshHeldAndStarvingPlayers_AcrossForeignTickDomains()
        {
            SimInput fresh = Filled();
            SimInput held = Filled();
            SimInput starving = Filled();

            var (lastSeen, lastFresh) = FreshState(3);
            var effective = new SimInput[3];
            var starvedFlags = new bool[3];
            int starvedCount = 0;

            // Player 0 — a new replicate tick every world tick (never lost):
            // fresh at the end. Player 1 — last CHANGED at world tick 3, held
            // fixed after: two ticks held by the final call (world tick 5).
            // Player 2 — last changed at world tick 0, held fixed the whole
            // rest of the run: five ticks stale by the final call, well past
            // StarveTicks (3). All three domains (9xxxx/7xxxx/5xxxx) are
            // arbitrary and far from the 0..5 world-tick range on purpose.
            for (int worldTick = 0; worldTick <= 5; worldTick++)
            {
                uint player0Tick = 90000u + (uint)worldTick;
                uint player1Tick = worldTick <= 3 ? 70000u + (uint)worldTick : 70003u;
                const uint player2Tick = 50000u;

                var lastInputs = new[]
                {
                    new ServerTickInput(player0Tick, in fresh),
                    new ServerTickInput(player1Tick, in held),
                    new ServerTickInput(player2Tick, in starving),
                };

                starvedCount = EffectiveInputBatch.Gather(lastInputs, worldTick, StarveTicks,
                    lastSeen, lastFresh, effective, starvedFlags);
            }

            Assert.AreEqual(1, starvedCount);
            CollectionAssert.AreEqual(new[] { false, false, true }, starvedFlags);

            // Player 0 — fresh at the final tick: untouched, edges alive.
            Assert.IsTrue(effective[0].DashRequested);
            Assert.AreEqual(fresh.MoveDir.x, effective[0].MoveDir.x);

            // Player 1 — held (ticksSinceLast == 2, inside the 3-tick
            // window): edges cleared, movement alive.
            Assert.IsFalse(effective[1].DashRequested);
            Assert.AreEqual(held.MoveDir.x, effective[1].MoveDir.x);

            // Player 2 — starving (ticksSinceLast == 5, past the window):
            // movement/fire zeroed, aim alive.
            Assert.AreEqual(0f, effective[2].MoveDir.x);
            Assert.IsFalse(effective[2].FireHeld);
            Assert.IsTrue(effective[2].AimHeld);
        }

        /// Fix-round 1 test (a): domain-agnosticism, pinned by a single
        /// player whose raw replicate tick is fixed at a huge, arbitrary
        /// value (90000) for the ENTIRE run — hold and starvation still
        /// engage exactly on the WORLD tick schedule. The ORIGINAL buggy
        /// formula (`worldTick - (int)rawTick`) would read
        /// `4 - 90000 = -89996`, clamp to "fresh" under Р82 forever, and
        /// NEVER starve this player — this test's whole point is proving the
        /// fixed code does not do that.
        [Test]
        public void Gather_HoldAndStarvation_EngageOnTheWorldScale_DespiteAHugeForeignTickOffset()
        {
            SimInput last = Filled();
            var (lastSeen, lastFresh) = FreshState(1);
            var effective = new SimInput[1];
            var starvedFlags = new bool[1];
            const uint pinnedForeignTick = 90000u;

            // World tick 0: first observation — fresh.
            EffectiveInputBatch.Gather(new[] { new ServerTickInput(pinnedForeignTick, in last) },
                0, StarveTicks, lastSeen, lastFresh, effective, starvedFlags);
            Assert.IsFalse(starvedFlags[0]);

            // World ticks 1-3: same raw tick, never re-sent — inside the
            // 3-tick hold window the whole way (ticksSinceLast 1, 2, 3).
            for (int worldTick = 1; worldTick <= StarveTicks; worldTick++)
            {
                EffectiveInputBatch.Gather(new[] { new ServerTickInput(pinnedForeignTick, in last) },
                    worldTick, StarveTicks, lastSeen, lastFresh, effective, starvedFlags);
                Assert.IsFalse(starvedFlags[0], $"world tick {worldTick} should still be inside the hold window");
            }

            // World tick 4: one past the window — starves, by the WORLD
            // schedule, despite the replicate tick still being the same huge
            // foreign number it was on world tick 0.
            EffectiveInputBatch.Gather(new[] { new ServerTickInput(pinnedForeignTick, in last) },
                StarveTicks + 1, StarveTicks, lastSeen, lastFresh, effective, starvedFlags);
            Assert.IsTrue(starvedFlags[0]);
            Assert.AreEqual(0f, effective[0].MoveDir.x);
        }

        /// Fix-round 1 test (b): a single lost packet (the replicate tick
        /// does not change for exactly one world tick) reads as ONE tick into
        /// the hold window — edges cleared, movement alive — not as
        /// starvation and not as still-fresh.
        [Test]
        public void Gather_OneMissedWorldTick_ReadsAsHoldNotFreshOrStarved()
        {
            SimInput last = Filled();
            var (lastSeen, lastFresh) = FreshState(1);
            var effective = new SimInput[1];
            var starvedFlags = new bool[1];
            const uint tick = 12345u;

            // World tick 0: the packet arrives — fresh.
            EffectiveInputBatch.Gather(new[] { new ServerTickInput(tick, in last) },
                0, StarveTicks, lastSeen, lastFresh, effective, starvedFlags);
            Assert.IsFalse(starvedFlags[0]);
            Assert.IsTrue(effective[0].DashRequested);

            // World tick 1: the SAME tick again — no new packet arrived (one
            // lost). Exactly the hold regime: edges cleared, not starved.
            EffectiveInputBatch.Gather(new[] { new ServerTickInput(tick, in last) },
                1, StarveTicks, lastSeen, lastFresh, effective, starvedFlags);
            Assert.IsFalse(starvedFlags[0]);
            Assert.IsFalse(effective[0].DashRequested, "one missed tick must clear the edge flags (hold), not repeat them");
            Assert.AreEqual(last.MoveDir.x, effective[0].MoveDir.x, "one missed tick must not zero movement — that is starvation, not hold");
        }

        [Test]
        public void Gather_MismatchedEffectiveOrStarvedSpanLength_Throws()
        {
            var lastInputs = new ServerTickInput[2];
            var (lastSeen, lastFresh) = FreshState(2);
            var effectiveTooShort = new SimInput[1];
            var starvedFlags = new bool[2];

            Assert.Throws<System.ArgumentException>(() =>
                EffectiveInputBatch.Gather(lastInputs, 0, StarveTicks, lastSeen, lastFresh, effectiveTooShort, starvedFlags));
        }

        [Test]
        public void Gather_MismatchedFreshnessSpanLength_Throws()
        {
            var lastInputs = new ServerTickInput[2];
            var lastSeenTooShort = new uint[1];
            var lastFresh = new int[2];
            var effective = new SimInput[2];
            var starvedFlags = new bool[2];

            Assert.Throws<System.ArgumentException>(() =>
                EffectiveInputBatch.Gather(lastInputs, 0, StarveTicks, lastSeenTooShort, lastFresh, effective, starvedFlags));
        }
    }
}
