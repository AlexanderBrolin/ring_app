using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below
// are required by the file, not just convenience imports. The alias shadows
// NUnit's own `Is`, same discipline as RenderClockTests.
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 32 (spec §3.9 Р37/Р38/Р83, §6i Р150е): the ring buffer
    /// between the network and interpolation, and `RenderSnapshot.CopyFrom`,
    /// the single deep-copy routine `SimulationRunner`'s frozen hitstop pair
    /// now shares.
    ///
    /// TWO SUBJECTS, ONE FILE, BECAUSE THE TASK BRIEF PUTS THEM IN ONE FILE.
    /// `SnapshotQueue`'s own admission logic and `RenderSnapshot.CopyFrom`
    /// (`CopyFrom_CopiesEveryPublicField_ByReflection`) are otherwise
    /// unrelated — the queue hands the caller an EMPTY slot to decode wire
    /// bytes into, it never calls `CopyFrom` itself (fix-round 1 correction:
    /// an earlier draft of this doc, and of `RenderSnapshot.cs`'s own doc,
    /// claimed otherwise). They are pinned together here because both halves
    /// of Task 32 exist to serve the exact same downstream consumer: Task 44's
    /// admitted-frame pipeline into the render pair `SimulationRunner` owns.
    ///
    /// THE INTEGRATION TESTS DRIVE A REAL `EventDedup` THROUGH A TEST-ONLY
    /// DRIVER, `DriveFrame`. Task 32's own scope is the queue's admission
    /// decision alone — wiring a live `EventDedup` into it is Task 44's job —
    /// but the CONTRACT between the two (task brief §2.2) is exactly the thing
    /// `EventDedup`'s own KNOWN LIMIT paragraph (fix round 1, reviewer F2)
    /// hands to this task to close, so proving the contract actually composes
    /// with the real class, not a mock of it, is the whole point of Р150е.
    /// `DriveFrame` is not production code and does not pretend to be
    /// FishNet-wired Task 44 — it is the smallest thing that follows the four
    /// numbered steps of task brief §2.2 exactly, including the `Commit` half
    /// of two-phase admission (fix-round 1) on the `Accepted` path.
    ///
    /// NUMBERS COME FROM FIXTURES, NEVER FROM `.asset` (Р56, same discipline
    /// as `RenderClockTests`): `Timings()` builds `NetTimings` by hand,
    /// `TestConfigs.DefaultArena()`/`TestConfigs.Default()` are the existing
    /// Simulation-side fixtures (no new literal caps are invented here), and
    /// `SnapshotQueue.FutureHorizonTicks` is read off the real constant
    /// rather than restated as a literal 270.
    ///
    /// EVERY ADMISSION THAT MUST BE VISIBLE LATER IS FOLLOWED BY `Commit`
    /// (fix-round 1, IMPORTANT #2). `Admit` only RESERVES a slot — `TryGet`,
    /// `Count` and `NewestTick` all read the COMMITTED state, so any test
    /// assertion about residency, capacity or the newest tick that skips the
    /// matching `Commit` call is testing a reservation the class was never
    /// told to publish, not the queue's real behavior. `DriveFrame` calls
    /// `Commit` for every `Accepted` verdict automatically; direct `Admit`
    /// calls in a few tests call it explicitly where the test's own later
    /// assertions depend on it.
    public class InterpolationBufferTests
    {
        const ushort Epoch = 7;
        const ushort OtherEpoch = 8;

        // ---------------------------------------------------------------- fixtures

        /// `NetTimings` fixture. `SnapshotQueue`'s constructor reads only
        /// `InterpBufferTicks`; the struct's other three fields are
        /// `RenderClock`'s concern (Task 31) and are left at their harmless
        /// zero default here rather than restated for no reader.
        static NetTimings Timings(int interpBufferTicks = 3) =>
            new NetTimings { InterpBufferTicks = interpBufferTicks };

        static SnapshotQueue NewQueue(out NetTimings timings, int interpBufferTicks = 3)
        {
            var cfg = TestConfigs.Default();
            timings = Timings(interpBufferTicks);
            return new SnapshotQueue(in cfg, in timings);
        }

        /// Minimal `EventRecord` fixture. `EventDedup.TryAcceptEvent` only
        /// ever reads `Seq` and `TickDelta` (task brief's own EventDedup.cs) —
        /// `Kind`/`Pos`/`PayloadOffset`/`PayloadLength` exist for the wire
        /// codec Task 27/28 own and are irrelevant to the dedup contract this
        /// file pins, so they are left at zero rather than borrowing
        /// `SnapshotCodecTests`' heavier `DedupRecord` helper for fields
        /// nothing here reads.
        static SnapshotBlocks.EventRecord Record(ushort seq, byte tickDelta = 0) =>
            new SnapshotBlocks.EventRecord { Seq = seq, TickDelta = tickDelta };

        /// Test-only driver playing Task 44's role (task brief §2.2) — the
        /// four-step contract every real caller of `SnapshotQueue.Admit` is
        /// obligated to follow:
        ///   1. Admit the frame.
        ///   2. `FutureRejected`/`ForeignEpoch` — STOP. `EventDedup` never
        ///      sees this frame's events at all — the driver never even
        ///      calls `TryAcceptEvent` for them.
        ///   3. `Stale`/`Duplicate` — state is not applied, but every event
        ///      still goes through `EventDedup.TryAcceptEvent`.
        ///   4. `Accepted` — the slot was handed out AND `Commit`ted (state
        ///      "applied" — fix-round 1's two-phase admission means a real
        ///      caller only commits after successfully decoding, which this
        ///      driver always does since it never simulates a decode
        ///      failure), and the events go through dedup exactly the same
        ///      as step 3.
        static SnapshotQueue.AdmitVerdict DriveFrame(SnapshotQueue queue, EventDedup dedup,
            ushort epoch, uint tick, SnapshotBlocks.EventRecord[] events,
            out bool stateApplied, out bool[] eventAccepted)
        {
            var verdict = queue.Admit(epoch, tick, out RenderSnapshot slot);
            stateApplied = false;
            eventAccepted = System.Array.Empty<bool>();

            if (verdict == SnapshotQueue.AdmitVerdict.FutureRejected
                || verdict == SnapshotQueue.AdmitVerdict.ForeignEpoch)
            {
                return verdict; // step 2: dedup is never consulted.
            }

            eventAccepted = new bool[events.Length];
            for (int i = 0; i < events.Length; i++)
                eventAccepted[i] = dedup.TryAcceptEvent(epoch, tick, in events[i], out _);

            if (verdict == SnapshotQueue.AdmitVerdict.Accepted)
            {
                Assert.IsNotNull(slot, "fixture premise: Accepted always hands back a slot");
                queue.Commit(tick);
                stateApplied = true;
            }

            return verdict;
        }

        // ---------------------------------------------------------------------
        // T32.1 (plan Step 1 #1). The ring's physical capacity.
        // ---------------------------------------------------------------------

        [Test]
        public void RingDepth_IsInterpBufferTicksPlusTwo()
        {
            var cfg = TestConfigs.Default();
            var timings = Timings(interpBufferTicks: 3);
            var queue = new SnapshotQueue(in cfg, in timings);

            Assert.AreEqual(timings.InterpBufferTicks + 2, queue.Depth,
                "Р37: the ring holds InterpBufferTicks + 2 ticks of history — 5 at the shipped "
                + "default of 3");
        }

        // ---------------------------------------------------------------------
        // T32.2 (plan Step 1 #2). A Stale frame drops state, not events (Р31).
        // ---------------------------------------------------------------------

        [Test]
        public void StaleFrame_DropsState_ButDeliversUnseenEvents()
        {
            var cfg = TestConfigs.Default();
            var queue = NewQueue(out _);
            var dedup = new EventDedup(cfg);
            queue.Reset(Epoch);
            dedup.Reset(Epoch);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted,
                DriveFrame(queue, dedup, Epoch, 50, new[] { Record(1) }, out _, out _),
                "fixture premise: the newest frame the window is measured against");

            // Exactly Depth ticks behind the newest — the window edge, task
            // brief §2.1: older than the ring can still use at the current newest.
            uint staleTick = 50u - (uint)queue.Depth;
            var freshEvent = Record(seq: 2, tickDelta: 0);

            var verdict = DriveFrame(queue, dedup, Epoch, staleTick, new[] { freshEvent },
                out bool applied, out bool[] results);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Stale, verdict);
            Assert.IsFalse(applied, "task brief §2.2 item 3: a Stale frame's STATE is never applied");
            Assert.AreEqual(1, results.Length);
            Assert.IsTrue(results[0],
                "but its event, never seen before, is still handled — Р31: a packet that merely "
                + "overtook another must not swallow the death it carried");

            // Resent on the SAME stale tick a second time: dedup's own memory
            // now answers "seen", even though the STATE verdict is Stale both
            // times — the two questions really are independent (task brief §2.2).
            DriveFrame(queue, dedup, Epoch, staleTick, new[] { freshEvent }, out _, out bool[] results2);
            Assert.IsFalse(results2[0],
                "positive witness: dedup's memory persisted across the Stale-state frame — the "
                + "state refusal did not also erase the event that rode along with it");
        }

        // ---------------------------------------------------------------------
        // T32.3 (plan Step 1 #3). Reorder fills the hole; a repeat is a duplicate.
        // ---------------------------------------------------------------------

        [Test]
        public void ReorderedFrame_FillsTheHole_WithoutMovingNewestBackward()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 101, out _),
                "fixture premise: N+1 arrives first");
            queue.Commit(101);
            Assert.AreEqual(101u, queue.NewestTick);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 100, out RenderSnapshot slot),
                "Р37: a reordered frame still inside the window fills the hole it left — the entire "
                + "reason the ring exists");
            Assert.IsNotNull(slot);
            queue.Commit(100);
            Assert.AreEqual(101u, queue.NewestTick,
                "an older frame arriving after a newer one must not pull NewestTick backward");

            Assert.IsTrue(queue.TryGet(100, out _), "positive witness: the hole-filling frame is resident");
            Assert.IsTrue(queue.TryGet(101, out _), "and the frame that arrived first is still there too");
        }

        [Test]
        public void RepeatedFrame_IsDuplicate()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 50, out _));
            queue.Commit(50); // must be a COMMITTED resident to read as Duplicate below.

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Duplicate, queue.Admit(Epoch, 50, out RenderSnapshot slot),
                "the exact same tick, still a committed resident, is a duplicate — not a fresh admission");
            Assert.IsNull(slot, "a refused admission never hands back a slot");
        }

        // ---------------------------------------------------------------------
        // T32.4 (plan Step 1 #4). A restart (Р60) accepts a smaller tick again.
        // ---------------------------------------------------------------------

        [Test]
        public void AfterReset_ALowerTickIsAccepted()
        {
            var queue = NewQueue(out _);
            const ushort oldEpoch = 4;
            const ushort newEpoch = 5;

            queue.Reset(oldEpoch);
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(oldEpoch, 1000, out _));
            queue.Commit(1000);
            Assert.AreEqual(1000u, queue.NewestTick, "fixture premise: deep inside the old epoch");

            queue.Reset(newEpoch);
            Assert.IsFalse(queue.HasNewestTick, "a reset forgets the newest tick, not merely re-bases it");
            Assert.AreEqual(0, queue.Count, "and empties the ring");

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(newEpoch, 5, out RenderSnapshot slot),
                "a tick far SMALLER than anything the old epoch ever saw is ordinary in the new one — "
                + "a restarted match replays its ticks from (near) zero");
            Assert.IsNotNull(slot);
            queue.Commit(5);
            Assert.AreEqual(5u, queue.NewestTick);
        }

        // ---------------------------------------------------------------------
        // T32.5 (plan Step 1 #5, fix-round 1 IMPORTANT #1). Overflow evicts the
        // TRUE oldest resident, never a physical-slot coincidence (Р83).
        // ---------------------------------------------------------------------

        [Test]
        public void Overflow_EvictsTrueOldest_NotAModuloCoincidence()
        {
            // Fix-round 1, IMPORTANT #1: the ORIGINAL bug was a `tick % Depth`
            // SLOT-ADDRESSING scheme, where a tick exactly `Depth` ahead of the
            // newest resident collided with that NEWEST resident's own slot
            // address, evicting it instead of the true oldest — the
            // reviewer's reproduction used residents 96..100 and an incoming
            // tick of 105 (96 + Depth) to demonstrate it. That storage scheme
            // no longer exists (see the class doc): slots are now assigned by
            // `FreeSlotIndex` — first-come, first-slot — so residents admitted
            // in increasing order land at slot 0, 1, 2, … in arrival order,
            // and the true oldest is always slot 0 regardless of its own tick
            // VALUE. A regression back to "evict `tick % Depth`" would
            // therefore, by a coincidence specific to THIS fixture's fill
            // order, still land on slot 0 if the incoming tick happened to be
            // a multiple of `Depth` (105 = 21 * 5) — passing this test for the
            // wrong reason. `106` (not a multiple of 5) is chosen instead:
            // `106 % Depth == 1`, which is slot 1 — resident 97, NOT the true
            // oldest (96, slot 0) — so a `tick % Depth` regression provably
            // evicts the WRONG resident here, and this test catches it.
            var queue = NewQueue(out _);
            queue.Reset(Epoch);
            int depth = queue.Depth;
            Assert.AreEqual(5, depth, "fixture premise");

            // Fill the ring exactly to capacity via FREE slots — no eviction
            // should happen here at all.
            for (uint t = 96; t <= 100; t++)
            {
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _));
                queue.Commit(t);
            }
            Assert.AreEqual(depth, queue.Count, "fixture premise: ring full via free slots alone");
            Assert.AreEqual(0, queue.OverflowDroppedSnapshots,
                "fix-round 1 IMPORTANT #1: filling free slots is not overflow — the counter must not "
                + "grow while a free slot was available for every one of these admissions");

            const uint incoming = 106; // NOT a multiple of Depth (5) — see the note above.
            Assert.AreNotEqual(0u, incoming % (uint)depth,
                "fixture premise: the incoming tick must NOT be a multiple of Depth, or a `tick % "
                + "Depth` regression would coincidentally land on slot 0 (the true oldest here) and "
                + "this test would pass for the wrong reason");
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, incoming, out RenderSnapshot slot));
            Assert.IsNotNull(slot);
            queue.Commit(incoming);

            Assert.AreEqual(1, queue.OverflowDroppedSnapshots,
                "exactly one eviction, of a genuinely committed resident — the ring's true oldest");
            Assert.AreEqual(depth, queue.Count, "the ring never holds more entries than its own depth");
            Assert.AreEqual(incoming, queue.NewestTick);

            Assert.IsFalse(queue.TryGet(96, out _), "96 was the TRUE oldest — the one that must be evicted");
            Assert.IsTrue(queue.TryGet(97, out _),
                "97 — slot 1, exactly where a `tick % Depth` regression would wrongly evict from — "
                + "must survive");
            Assert.IsTrue(queue.TryGet(98, out _));
            Assert.IsTrue(queue.TryGet(99, out _));
            Assert.IsTrue(queue.TryGet(100, out _), "the NEWEST resident before the gap must survive too");
            Assert.IsTrue(queue.TryGet(incoming, out _));
        }

        [Test]
        public void Overflow_Burst_EvictsInOrder_CountsEveryEviction()
        {
            // Follow-on Р83 stress case: several frames past capacity land in
            // a row, none of them discharged — the counter must track every
            // eviction, and the survivors must always be the newest ones.
            var queue = NewQueue(out _);
            queue.Reset(Epoch);
            int depth = queue.Depth;

            for (uint t = 1; t <= (uint)depth; t++)
            {
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _));
                queue.Commit(t);
            }
            Assert.AreEqual(depth, queue.Count, "fixture premise: the ring is full, nothing discharged");
            Assert.AreEqual(0, queue.OverflowDroppedSnapshots, "fixture premise: no eviction has happened yet");

            const int extra = 3;
            for (uint t = (uint)depth + 1; t <= (uint)(depth + extra); t++)
            {
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out RenderSnapshot slot),
                    $"tick {t}: a newer frame is accepted even while the ring is completely full");
                Assert.IsNotNull(slot);
                queue.Commit(t);
            }

            Assert.AreEqual(extra, queue.OverflowDroppedSnapshots,
                "Р83: one eviction per frame committed past the ring's capacity");
            Assert.AreEqual(depth, queue.Count, "the ring never holds more entries than its own depth");

            for (uint t = 1; t <= extra; t++)
                Assert.IsFalse(queue.TryGet(t, out _), $"tick {t}: evicted — it was the oldest at the time");
            for (uint t = (uint)extra + 1; t <= (uint)(depth + extra); t++)
                Assert.IsTrue(queue.TryGet(t, out _),
                    $"tick {t}: still resident — nothing this new was touched by the eviction");
            Assert.AreEqual((uint)(depth + extra), queue.NewestTick);
        }

        // ---- Stage 2 (bd app-0wm): the accounting, not the ring ----
        //
        // `OverflowDroppedSnapshots` counted every eviction, and evicting is
        // ordinary here: the ring holds `InterpBufferTicks + 2` ticks, the
        // render clock sits up to `InterpBufferTicks` behind the newest, and
        // `NetworkSimBackend.Advance` discharges at `RenderTick - 1` — so
        // whenever the clock is at its full distance the residents fill `Depth`
        // of `Depth`, and the tick pushed out is the one the renderer finished
        // with a frame ago. Not every frame: the measured 1371 in 83 seconds is
        // 16.5/s against ~28.5 admissions/s, because that distance breathes
        // between 2 and 4 and one tick nearer leaves a slot free. Either way the
        // counter grew on a HEALTHY connection and DevOverlay painted it red. The fix subtracts that case from the COUNT and
        // touches nothing else, the same move `app-c3m` made for `DroppedTime`
        // in `FixedStepAccumulator` (`AccumulatorTests`' own Task 48 block).
        //
        // The ring's own behavior — which resident is evicted, when, and what
        // verdict `Admit` returns — is unchanged, and
        // `NewAccounting_EvictsTheSameTicks_...` below is what makes that a
        // fact rather than a claim.

        /// THE TEST THIS TASK WAS OPENED FOR. It drives the queue the way
        /// `NetworkSimBackend.Advance` really drives it: admit the newest
        /// tick, then `DiscardBelow(RenderTick - 1)` where `RenderTick` is
        /// `newest - InterpBufferTicks` (`RenderClock`'s own target). Every
        /// frame past the fill evicts, every evicted tick IS the floor — a
        /// moment the render clock walked past — and the counter must stay at
        /// zero through all of it.
        [Test]
        public void SteadyStateRotation_EvictsOnlyTicksTheRenderClockPassed_AndCountsNone()
        {
            var queue = NewQueue(out NetTimings timings);
            queue.Reset(Epoch);
            int depth = queue.Depth;
            Assert.AreEqual(5, depth, "fixture premise: the shipped default ring");

            const uint firstTick = 200;
            const int frames = 60;
            int framesThatEvicted = 0;

            for (uint newest = firstTick; newest < firstTick + (uint)frames; newest++)
            {
                // A FULL ring before the admission is the only state in which
                // `Admit` can reach its eviction branch at all, so this is the
                // premise without which the assertion below would be empty
                // (Task 26 finding F-D: prove the thing under test actually
                // happened before concluding anything from a zero).
                if (queue.Count == depth) framesThatEvicted++;

                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted,
                    queue.Admit(Epoch, newest, out RenderSnapshot slot),
                    $"tick {newest}: a healthy stream is admitted every frame");
                Assert.IsNotNull(slot);
                queue.Commit(newest);

                uint renderTick = newest - (uint)timings.InterpBufferTicks;
                queue.DiscardBelow(renderTick - 1u);

                Assert.AreEqual(0, queue.OverflowDroppedSnapshots,
                    $"frame at tick {newest}: the ring pushed out a tick the render clock had "
                    + "already walked past — routine rotation, not a lost snapshot. Counting it "
                    + "is the whole defect: 1371 'losses' in 83 seconds of a healthy match under "
                    + "80 ms / 5%, with stale and dup both at zero");
            }

            Assert.Greater(framesThatEvicted, frames / 2,
                "fixture premise: the eviction branch really did run, on most of these frames — a "
                + "counter that stays at zero proves nothing if nothing was ever evicted");
            Assert.AreEqual(depth, queue.Count, "the ring stays exactly full in the steady state");

            const uint lastTick = firstTick + (uint)frames - 1u;
            for (uint t = lastTick - (uint)depth + 1u; t <= lastTick; t++)
                Assert.IsTrue(queue.TryGet(t, out _),
                    $"tick {t}: the residents are the newest Depth ticks, as they were all along");
            Assert.IsFalse(queue.TryGet(lastTick - (uint)depth, out _),
                "and the tick just behind them is the one the last frame evicted — the renderer "
                + "was done with it before it went");
        }

        /// THE OTHER HALF OF THE SAME COIN, and the reason "never count
        /// anything" is not a legal answer. A consumer that stops discharging
        /// — a hitch, a freeze, a burst of delayed datagrams landing faster
        /// than render frames go by (Р83's own scenario) — loses frames the
        /// render clock never reached. Those are real, and they are exactly
        /// what `NetDiagnostics.DroppedSnapshots` has promised all along: "a
        /// snapshot that arrived intact and was thrown away unshown".
        [Test]
        public void EvictionAboveTheFloor_IsCountedAsARealLoss()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);
            int depth = queue.Depth;
            Assert.AreEqual(5, depth, "fixture premise: the shipped default ring");

            // The consumer discharged once, long ago, and has not run since.
            queue.DiscardBelow(50);

            for (uint t = 100; t <= 104; t++)
            {
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _));
                queue.Commit(t);
            }
            Assert.AreEqual(depth, queue.Count, "fixture premise: full, and nothing here was shown");
            Assert.AreEqual(0, queue.OverflowDroppedSnapshots, "fixture premise: nothing evicted yet");

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 105, out _));
            queue.Commit(105);
            Assert.IsFalse(queue.TryGet(100, out _), "fixture premise: tick 100 really was evicted");
            Assert.AreEqual(1, queue.OverflowDroppedSnapshots,
                "tick 100 sat far above a floor of 50 — the render clock never reached it, so it "
                + "went unshown: a genuine loss, and the one thing this counter exists to say");

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 106, out _));
            queue.Commit(106);
            Assert.AreEqual(2, queue.OverflowDroppedSnapshots,
                "one per frame lost, not one per stall — 101 went unshown too");
        }

        /// THE OFF-BY-ONE, FROM BOTH SIDES. Both runs below hold the exact
        /// same residents and evict the exact same tick (100); the floor is
        /// the only thing that differs, and only by one. `DiscardBelow`'s
        /// floor is `RenderTick - 1` — the last tick the consumer is DONE
        /// with — so a tick equal to the floor has been shown and a tick one
        /// above it has not.
        [Test]
        public void FloorBoundary_EvictionAtTheFloorIsFree_OneTickAboveItIsALoss()
        {
            int EvictTick100(uint floor)
            {
                var queue = NewQueue(out _);
                queue.Reset(Epoch);
                Assert.AreEqual(5, queue.Depth, "fixture premise: the shipped default ring");
                queue.DiscardBelow(floor);

                for (uint t = 100; t <= 104; t++)
                {
                    Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _),
                        $"fixture premise (floor {floor}): tick {t} fills a free slot");
                    queue.Commit(t);
                }
                Assert.AreEqual(queue.Depth, queue.Count,
                    $"fixture premise (floor {floor}): the ring is full, nothing discharged");

                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 105, out _));
                queue.Commit(105);
                Assert.IsFalse(queue.TryGet(100, out _),
                    $"fixture premise (floor {floor}): tick 100 is the evicted one, both times");
                return queue.OverflowDroppedSnapshots;
            }

            Assert.AreEqual(0, EvictTick100(floor: 100),
                "the evicted tick IS the floor: `DiscardBelow(RenderTick - 1)` means the consumer "
                + "is finished with it, so pushing it out is routine rotation");
            Assert.AreEqual(1, EvictTick100(floor: 99),
                "one lower a floor, the same ring evicting the same tick: 100 now sits one ABOVE "
                + "the floor, which means the renderer had not reached it — a lost snapshot");
        }

        /// NO FLOOR AT ALL — named here rather than left to fall out of an
        /// implementation detail (bd `app-0wm`: a real loss is an evicted tick
        /// above the floor, "or there being no floor yet"). Before the first
        /// `DiscardBelow` the consumer has shown NOTHING, so nothing the ring
        /// throws away can have been shown. In production that window is only
        /// the opening frames — `NetworkSimBackend.Advance` discharges every
        /// render frame — so a ring that manages to overflow inside it is
        /// genuinely ill, which is precisely what the counter is for.
        ///
        /// TICK ZERO IS THE FIXTURE ON PURPOSE. "No floor" and "a floor of 0"
        /// are different states, and a test evicting tick 100 could not tell
        /// them apart: `100 > 0` answers the same either way. Evicting tick
        /// ZERO is the only pair of runs where the `_hasFloor` half of the
        /// decision is the thing under test.
        [Test]
        public void WithNoFloorYet_AnEvictionIsALoss_AFloorOfZeroMakesItRotation()
        {
            int EvictTickZero(bool discharge)
            {
                var queue = NewQueue(out _);
                queue.Reset(Epoch);
                Assert.AreEqual(5, queue.Depth, "fixture premise: the shipped default ring");
                if (discharge) queue.DiscardBelow(0); // the opening frames' own floor

                for (uint t = 0; t <= 4; t++)
                {
                    Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _),
                        $"fixture premise (discharge {discharge}): tick {t} fills a free slot");
                    queue.Commit(t);
                }
                Assert.AreEqual(queue.Depth, queue.Count,
                    $"fixture premise (discharge {discharge}): the ring is full");

                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 5, out _));
                queue.Commit(5);
                Assert.IsFalse(queue.TryGet(0, out _),
                    $"fixture premise (discharge {discharge}): tick 0 is the evicted one, both times");
                return queue.OverflowDroppedSnapshots;
            }

            Assert.AreEqual(1, EvictTickZero(discharge: false),
                "no `DiscardBelow` has ever run, so the consumer has shown nothing at all — the "
                + "frame the ring threw away was thrown away unshown, by definition");
            Assert.AreEqual(0, EvictTickZero(discharge: true),
                "the same ring evicting the same tick 0, the consumer now finished with tick 0: "
                + "routine rotation. `_floor == 0` and 'no floor yet' are DIFFERENT states and the "
                + "count has to tell them apart — a bare `evicted > _floor` cannot");
        }

        /// THE BEHAVIOR-NEUTRALITY PROOF — the precedent's own
        /// `AccumulatorTests.IgnoreNextFrameGap_ChangesNoTickCountAndNoPhase`
        /// ("THE HASH-NEUTRALITY PROOF") applied to this class.
        /// `OverflowDroppedSnapshots` is a diagnostic nothing reads back, but
        /// the branch it lives in is the ring's EVICTION branch — the one that
        /// decides which arrived frame the renderer will never see. So this
        /// pins the whole observable shape of that branch against a scripted
        /// stream: the verdict of every admission, the tick evicted by every
        /// admission IN ORDER, the occupancy after each one, and the residents
        /// at the end. Exactly one number in this test is allowed to move
        /// between the old accounting and the new one, and it is the last
        /// assertion.
        ///
        /// THE TRACE ASSERTIONS HOLD IN BOTH WORLDS ON PURPOSE — that IS the
        /// proof, the same way `Assert.AreEqual(chargedTicks, excusedTicks)`
        /// is in the precedent. What they discriminate against is a fix that
        /// reaches the ring instead of the count: variant (b) of the bug
        /// report (a wider `Depth`) moves the occupancies AND the eviction
        /// ticks AND the `Stale` verdicts, since `Depth` is also the backward
        /// admission window; a fix that refused the admission rather than
        /// evicting moves the verdicts; a fix that left the below-floor
        /// resident in place moves both.
        [Test]
        public void NewAccounting_EvictsTheSameTicks_InTheSameOrder_WithTheSameVerdicts()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);
            Assert.AreEqual(5, queue.Depth, "fixture premise: the shipped default ring");

            var verdicts = new List<SnapshotQueue.AdmitVerdict>();
            var evicted = new List<uint>();
            var occupancy = new List<int>();

            // Every tick this script can ever make resident. A reservation is
            // invisible until `Commit`, and `Admit` adds nothing to the ring
            // on its own, so the ONE tick that disappears across an `Admit`
            // call is the tick that admission evicted.
            List<uint> Residents()
            {
                var live = new List<uint>();
                for (uint t = 195; t <= 220; t++)
                    if (queue.TryGet(t, out _)) live.Add(t);
                return live;
            }

            void Step(uint tick)
            {
                List<uint> before = Residents();
                var verdict = queue.Admit(Epoch, tick, out RenderSnapshot slot);
                foreach (uint t in before)
                    if (!queue.TryGet(t, out _)) evicted.Add(t);

                verdicts.Add(verdict);
                if (verdict == SnapshotQueue.AdmitVerdict.Accepted)
                {
                    Assert.IsNotNull(slot, $"tick {tick}: Accepted always hands back a slot");
                    queue.Commit(tick);
                }
                occupancy.Add(queue.Count);
            }

            // 1-5. The ring fills through FREE slots while the consumer keeps
            // up: `DiscardBelow(newest - InterpBufferTicks - 1)` every frame,
            // which is `NetworkSimBackend.Advance`'s own call spelled out.
            Step(200); queue.DiscardBelow(196);
            Step(201); queue.DiscardBelow(197);
            Step(202); queue.DiscardBelow(198);
            Step(203); queue.DiscardBelow(199);
            Step(204); queue.DiscardBelow(200);

            // 6-8. The steady state: full ring, one eviction per frame, and
            // the evicted tick IS the floor every time.
            Step(205); queue.DiscardBelow(201);
            Step(206); queue.DiscardBelow(202);
            Step(207);

            // 9-11. The consumer stalls — no `DiscardBelow` at all — while
            // frames keep arriving (Р83's burst). The floor stays at 202, so
            // these three evictions are of ticks nobody has seen.
            Step(208);
            Step(209);
            Step(210);

            // The consumer catches up in one jump, discharging three residents.
            queue.DiscardBelow(209);

            Step(211); // 12. A free slot again — no eviction at all.
            Step(208); // 13. Below the floor — Stale.
            Step(210); // 14. Still a committed resident — Duplicate.
            Step(213); // 15. A gap in the stream, accepted on a free slot.
            Step(212); // 16. The hole behind it, filled (Р37).
            Step(214); // 17. Full again — evicts 209, which IS the floor.
            Step(214u + (uint)SnapshotQueue.FutureHorizonTicks + 1u); // 18. FutureRejected (Р150е).
            Step(209); // 19. Evicted AND behind the backward window — Stale.
            Step(215); // 20. Evicts 210, one above the floor — a real loss.

            CollectionAssert.AreEqual(new[]
            {
                SnapshotQueue.AdmitVerdict.Accepted,       // 200
                SnapshotQueue.AdmitVerdict.Accepted,       // 201
                SnapshotQueue.AdmitVerdict.Accepted,       // 202
                SnapshotQueue.AdmitVerdict.Accepted,       // 203
                SnapshotQueue.AdmitVerdict.Accepted,       // 204
                SnapshotQueue.AdmitVerdict.Accepted,       // 205
                SnapshotQueue.AdmitVerdict.Accepted,       // 206
                SnapshotQueue.AdmitVerdict.Accepted,       // 207
                SnapshotQueue.AdmitVerdict.Accepted,       // 208
                SnapshotQueue.AdmitVerdict.Accepted,       // 209
                SnapshotQueue.AdmitVerdict.Accepted,       // 210
                SnapshotQueue.AdmitVerdict.Accepted,       // 211
                SnapshotQueue.AdmitVerdict.Stale,          // 208 again, below the floor
                SnapshotQueue.AdmitVerdict.Duplicate,      // 210 again, still resident
                SnapshotQueue.AdmitVerdict.Accepted,       // 213
                SnapshotQueue.AdmitVerdict.Accepted,       // 212, the hole
                SnapshotQueue.AdmitVerdict.Accepted,       // 214
                SnapshotQueue.AdmitVerdict.FutureRejected, // 214 + horizon + 1
                SnapshotQueue.AdmitVerdict.Stale,          // 209, past the backward window
                SnapshotQueue.AdmitVerdict.Accepted,       // 215
            }, verdicts, "every admission answers exactly what it answered before — the accounting "
                + "change does not reach a single verdict");

            CollectionAssert.AreEqual(new uint[] { 200, 201, 202, 203, 204, 205, 209, 210 }, evicted,
                "the same eight ticks are evicted, in the same order, by the same admissions — the "
                + "TRUE oldest resident each time (Р83), untouched");

            CollectionAssert.AreEqual(
                new[] { 1, 2, 3, 4, 5, 5, 5, 5, 5, 5, 5, 3, 3, 3, 4, 5, 5, 5, 5, 5 }, occupancy,
                "and the ring holds the same number of residents after every one of them");

            Assert.AreEqual(215u, queue.NewestTick);
            CollectionAssert.AreEqual(new uint[] { 211, 212, 213, 214, 215 }, Residents(),
                "ending on exactly the newest Depth ticks, as it began");

            // THE ONE NUMBER ALLOWED TO MOVE. Four of those eight evictions —
            // 200, 201, 202 and 209 — were of ticks at or below the floor at
            // the moment they went: the render clock had walked past them.
            // The old accounting charged all eight.
            Assert.AreEqual(4, queue.OverflowDroppedSnapshots,
                "only the four evictions of ticks the consumer had NOT reached count as lost "
                + "snapshots: 203, 204 and 205 during the stall, and 210 after it");
        }

        // ---------------------------------------------------------------------
        // Fix-round 1, IMPORTANT #2. Admission is two-phase: a reservation
        // that is never Committed must not be visible, must not count, and
        // must not advance NewestTick.
        // ---------------------------------------------------------------------

        [Test]
        public void Admit_WithoutCommit_LeavesSlotInvisible_AndDoesNotAdvanceState()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);

            int depth = queue.Depth;
            for (uint t = 1; t <= (uint)depth; t++)
            {
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _));
                queue.Commit(t);
            }
            Assert.AreEqual(depth, queue.Count, "fixture premise: ring full, all committed");

            // A new tick reserves a slot by evicting the true oldest (tick 1)
            // — but the caller (simulating Task 44 abandoning a malformed
            // decode) never calls Commit.
            uint abandonedTick = (uint)depth + 1;
            var verdict = queue.Admit(Epoch, abandonedTick, out RenderSnapshot slot);
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, verdict);
            Assert.IsNotNull(slot, "fixture premise: Accepted still hands back a buffer to decode into");

            Assert.IsFalse(queue.TryGet(1, out _), "the evicted resident's bookkeeping is already gone");
            Assert.IsFalse(queue.TryGet(abandonedTick, out _),
                "fix-round 1 IMPORTANT #2: an uncommitted reservation must never be visible to TryGet — "
                + "a consumer must not read a buffer that was never actually filled under this tick's "
                + "identity");
            Assert.AreEqual(depth - 1, queue.Count,
                "the reservation does not count until committed — capacity reads exactly the eviction, "
                + "nothing added yet");
            Assert.AreEqual((uint)depth, queue.NewestTick,
                "NewestTick must not run ahead of what is actually resident — an abandoned decode must "
                + "not let a downstream render clock target a moment with no real data behind it");

            // The dangling reservation is silently reclaimed by the NEXT
            // Admit call (Р82 discipline), regardless of what that call
            // decides — one abandoned decode costs at most one slot for one
            // cycle, never a permanent leak.
            uint nextTick = abandonedTick + 1;
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, nextTick, out RenderSnapshot slot2));
            Assert.IsNotNull(slot2);
            queue.Commit(nextTick);
            Assert.IsTrue(queue.TryGet(nextTick, out _), "positive witness: a fresh, committed admission works normally");
            Assert.AreEqual(depth, queue.Count, "and capacity is back to full — nothing was permanently lost");
        }

        [Test]
        public void Commit_WithWrongOrMissingTick_IsSilentNoOp()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);

            // No pending reservation at all yet.
            queue.Commit(1);
            Assert.AreEqual(0, queue.Count, "Commit with nothing pending is a silent no-op (Р82)");

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 10, out _));
            // A tick that does not match the pending reservation.
            queue.Commit(11);
            Assert.IsFalse(queue.TryGet(10, out _), "a mismatched Commit must not publish someone else's reservation");
            Assert.AreEqual(0, queue.Count);

            // Positive witness: the RIGHT tick still works.
            queue.Commit(10);
            Assert.IsTrue(queue.TryGet(10, out _));
            Assert.AreEqual(1, queue.Count);

            // A second Commit of the same tick, with nothing pending anymore,
            // is also a silent no-op — not a double-count.
            queue.Commit(10);
            Assert.AreEqual(1, queue.Count, "a repeated Commit must not double-count the same resident");
        }

        // ---------------------------------------------------------------------
        // T32.6 (plan Step 1 #6). CopyFrom, by reflection over every public field.
        // ---------------------------------------------------------------------

        static readonly FieldInfo[] RenderSnapshotFields =
            typeof(RenderSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance);

        /// Array fields mapped to the COUNT field that bounds their meaningful
        /// content (task brief §2.5 test 6: the arena-capacity slots past the
        /// count are never written by anything and comparing them would only
        /// pin defaults). A future array field this map has not been taught
        /// about fails LOUDLY in the loop below instead of silently comparing
        /// nothing.
        static readonly Dictionary<string, string> ArrayCountField = new Dictionary<string, string>
        {
            { nameof(RenderSnapshot.Players), nameof(RenderSnapshot.PlayerCount) },
            { nameof(RenderSnapshot.Mobs), nameof(RenderSnapshot.MobCount) },
            { nameof(RenderSnapshot.Projectiles), nameof(RenderSnapshot.ProjectileCount) },
            { nameof(RenderSnapshot.PlayerStats), nameof(RenderSnapshot.PlayerCount) },
            // Stage 2 Task 47a: both per-slot flags are bounded by the roster,
            // exactly like Players/PlayerStats above.
            { nameof(RenderSnapshot.PlayerKnown), nameof(RenderSnapshot.PlayerCount) },
            { nameof(RenderSnapshot.PlayerAliveInMatch), nameof(RenderSnapshot.PlayerCount) },
            // Playtest В1 round two (bd `app-1kei`): who walked out is the
            // third per-slot flag, bounded by the same roster as the two above.
            { nameof(RenderSnapshot.PlayerExtractedInMatch), nameof(RenderSnapshot.PlayerCount) },
            // Stage 3 Т6: ground pickups, bounded by their own count exactly
            // like Mobs/Projectiles above.
            { nameof(RenderSnapshot.Pickups), nameof(RenderSnapshot.PickupCount) },
            // Stage 3 Т14: containers, same bounded-by-their-own-count shape.
            { nameof(RenderSnapshot.Containers), nameof(RenderSnapshot.ContainerCount) },
            { nameof(RenderSnapshot.ContainerIsEmpty), nameof(RenderSnapshot.ContainerCount) },
            // Stage 3 Т32б: the three arrays the five decoded blocks landed in.
            // The owner's pack and the interiors are each bounded by their own
            // count exactly like the entity arrays above; the interiors' ITEM
            // POOL is bounded by its own count too, and deliberately not by the
            // records that address it — a record's ItemOffset points INTO this
            // pool, so bounding the pool by the records would be a derivation
            // where a count already exists.
            { nameof(RenderSnapshot.InventoryItems), nameof(RenderSnapshot.InventoryItemCount) },
            { nameof(RenderSnapshot.ContainerInteriors),
                nameof(RenderSnapshot.ContainerInteriorCount) },
            { nameof(RenderSnapshot.ContainerInteriorItems),
                nameof(RenderSnapshot.ContainerInteriorItemCount) },
        };

        /// Fills every field of `s` with a distinct, non-default value so a
        /// field `CopyFrom` forgot to copy is observably wrong afterwards
        /// (dest stays at its construction-time default: 0/false/none).
        static void FillDistinctValues(RenderSnapshot s, in ArenaSimConfig arena)
        {
            s.Tick = 4242;
            s.PlayerCount = math.min(2, arena.MaxPlayers);
            for (int i = 0; i < s.PlayerCount; i++)
                s.Players[i] = new PlayerState
                {
                    Pos = new float2(10f + i, 20f + i),
                    Hp = 55f + i,
                    Alive = true,
                    DashRequestCooldownTicks = 3 + i,
                };
            // Stage 2 Task 47a. `true` for every seat because `false` IS this
            // type's default and would leave the guard below unable to tell
            // "copied" from "never written". A MIXED pattern — which is what
            // discriminates a copy that writes a constant — is pinned by
            // `FramePresenceTests` instead; this fixture's job is the "no
            // public field is forgotten" half.
            for (int i = 0; i < s.PlayerCount; i++)
            {
                s.PlayerKnown[i] = true;
                s.PlayerAliveInMatch[i] = true;
                s.PlayerExtractedInMatch[i] = true;
            }
            s.LocalPlayerIndex = s.PlayerCount > 1 ? 1 : 0;
            s.MobCount = math.min(2, arena.MaxMobs);
            for (int i = 0; i < s.MobCount; i++)
                s.Mobs[i] = new MobState
                {
                    Id = 100 + i,
                    Type = MobType.Gunner,
                    Pos = new float2(1f + i, 2f + i),
                    Hp = 12f + i,
                    Ai = MobAiState.Chase,
                };
            s.ProjectileCount = math.min(2, arena.MaxProjectiles);
            for (int i = 0; i < s.ProjectileCount; i++)
                s.Projectiles[i] = new ProjectileState
                {
                    Id = 200 + i,
                    Owner = ProjectileOwner.Player,
                    Pos = new float2(3f + i, 4f + i),
                    Damage = 9f + i,
                };
            // Stage 3 Т6: the pickup array and the match's flow state, the two
            // fields RenderSnapshot grew with the extraction economy. Both are
            // filled with values no constructor could have left behind
            // (nonzero id/amount, a phase past Farm) so the guard below can
            // tell "copied" from "never written".
            s.PickupCount = math.min(2, arena.MaxPickups);
            for (int i = 0; i < s.PickupCount; i++)
                s.Pickups[i] = new PickupState
                {
                    Id = 300 + i,
                    Pos = new float2(5f + i, 6f + i),
                    Kind = PickupKind.EnergyCell,
                    Amount = 7 + i,
                    Ttl = 42.5f + i,
                };
            // Stage 3 Т14: the container array RenderSnapshot grew for the
            // container economy — same "distinct nonzero values, never a
            // constructor default" discipline as Pickups above.
            s.ContainerCount = math.min(2, arena.MaxContainers);
            for (int i = 0; i < s.ContainerCount; i++)
                s.Containers[i] = new ContainerState
                {
                    Id = 400 + i,
                    Pos = new float2(8f + i, 9f + i),
                    Kind = ContainerKind.Crate,
                    SlotCount = (byte)(1 + i),
                    Ttl = 12.5f + i,
                };
            // Stage 3 Т32б: `true` for every described box, for the reason the
            // PlayerKnown/PlayerAliveInMatch filler above states — `false` IS
            // this type's default and would leave the guard unable to tell
            // "copied" from "never written".
            for (int i = 0; i < s.ContainerCount; i++) s.ContainerIsEmpty[i] = true;
            s.Match = new MatchState { Phase = MatchPhase.GateOpen, DirectorDeathTick = 77 };
            // Stage 3 Task 11 (errata A-I6) / bd app-ggvz Т3: the debt grew
            // from two named fields to nine (zone x archetype) and then, when
            // the zone moved out of the field names into the index of a
            // per-zone WaveState instance, back to three -- and the frame
            // carries the world AGGREGATE of the three, one plain WaveState.
            // Every field gets a distinct nonzero value here, same "no field
            // left at its constructor default" discipline the rest of this
            // filler follows.
            s.Wave = new WaveState
            {
                Phase = WavePhase.Active,
                WaveIndex = 3,
                PendingChaser = 2,
                PendingGunner = 1,
                PendingElite = 4,
                AliveCount = 5,
                PhaseTicks = 6,
            };
            for (int i = 0; i < s.PlayerCount; i++)
                s.PlayerStats[i] = new MatchStats
                {
                    Kills = 5 + i,
                    HeadshotKills = 1,
                    ShotsFired = 20,
                    ShotsHit = 10,
                    DashesUsed = 2,
                    SlidesUsed = 1,
                    DeathTick = -1,
                    DamageTaken = 33.5f,
                };
            s.WorldStats = new WorldStats { WavesCleared = 2, MobSpawnsSkipped = 1, ProjectileSpawnsSkipped = 4 };
            // Stage 3 Т32б: the five blocks' own fields. Same discipline as
            // everything above — nothing left at a value a constructor could
            // have produced.
            //
            // `MatchSecondsRemaining` is filled with a PLAUSIBLE COUNTDOWN and
            // not with `MatchCountdown.None`: the sentinel is what an untouched
            // frame reads, so a `CopyFrom` that skipped the field would agree
            // with the source by accident. (The constructor writes the sentinel
            // and `AssertEveryFieldIsNonDefault` only rejects `default(int)`,
            // which the sentinel is not — hence stating this here rather than
            // relying on the guard to notice.)
            s.MatchSecondsRemaining = 421;
            s.DirectorAlive = true;
            s.InventorySlotPoints = 5;
            s.InventoryItemCount = math.min(2, s.InventoryItems.Length);
            for (int i = 0; i < s.InventoryItemCount; i++)
                s.InventoryItems[i] = (byte)(11 + i);
            s.ContainerInteriorCount = math.min(2, s.ContainerInteriors.Length);
            s.ContainerInteriorItemCount = 0;
            for (int i = 0; i < s.ContainerInteriorCount; i++)
            {
                // Two occupied slots apiece, so the pool carries something for
                // every record and the offsets differ between them.
                s.ContainerInteriors[i] = new ContainerInterior
                {
                    Id = 500 + i,
                    OccupancyMask = (byte)0b0000_0011,
                    ItemOffset = s.ContainerInteriorItemCount,
                    ItemCount = 2,
                };
                s.ContainerInteriorItems[s.ContainerInteriorItemCount++] = (byte)(21 + i);
                s.ContainerInteriorItems[s.ContainerInteriorItemCount++] = (byte)(31 + i);
            }
        }

        /// `value == default(T)` for a boxed value of any VALUE type the
        /// fixture might produce (int/uint/float/bool/byte, enums, `float2`,
        /// and the plain structs `PlayerState`/`MobState`/`ProjectileState`/
        /// `PickupState`/`ContainerState`/`WaveState`/`MatchState`/
        /// `MatchStats`/`WorldStats`, and `ContainerInterior` since Т32б —
        /// none of them nest a reference type, confirmed by inspection of
        /// `Simulation/Core/SimStates.cs` and `RenderSnapshot.cs`, so comparing
        /// against a freshly-activated instance of the same type is exact. The
        /// backpack OBJECT (`Loot.Inventory`) is still the one piece of world
        /// state that is a reference type and it still never reaches a render
        /// frame — see `SimulationWorld.CaptureSnapshot`'s own note. What Т32б
        /// put in the frame is its CONTENTS, as a `byte[]` of item ids, which
        /// this helper meets one element at a time like any other mapped array.
        /// A REFERENCE type this helper was never
        /// taught to compare fails LOUDLY (fix-round 1, IMPORTANT #3:
        /// "unknown type = loud Fail demanding the filler be taught") rather
        /// than silently reporting a wrong answer.
        static bool IsDefaultValue(object value)
        {
            System.Type t = value.GetType();
            if (t.IsValueType) return value.Equals(System.Activator.CreateInstance(t));
            Assert.Fail($"fixture guard: IsDefaultValue does not know reference type {t} — teach it "
                + "before trusting the reflection test's coverage of a field of this type");
            return false; // unreachable — Assert.Fail throws.
        }

        /// Fix-round 1, IMPORTANT #3. The reflection loop in
        /// `CopyFrom_CopiesEveryPublicField_ByReflection` compares `source`
        /// against `dest` — a future NON-array field `FillDistinctValues`
        /// forgets to populate stays at ITS TYPE'S default in `source` too,
        /// so a `CopyFrom` that never copies it would still read as
        /// "agreement" (both sides default) instead of "never exercised".
        /// This guard runs BEFORE that comparison and independently confirms
        /// every public field of `source` — and, for the four mapped array
        /// fields, every element up to the relevant count — reads as
        /// non-default, so a commented-out line in the filler is caught here,
        /// by name, rather than passing the CopyFrom comparison for the wrong
        /// reason.
        static void AssertEveryFieldIsNonDefault(RenderSnapshot source)
        {
            foreach (FieldInfo field in RenderSnapshotFields)
            {
                if (field.FieldType.IsArray)
                {
                    Assert.IsTrue(ArrayCountField.TryGetValue(field.Name, out string countFieldName),
                        $"fixture guard: unmapped array field {field.Name} — teach ArrayCountField "
                        + "before trusting the fixture's coverage of it");
                    int count = (int)typeof(RenderSnapshot).GetField(countFieldName).GetValue(source);
                    Assert.Greater(count, 0,
                        $"fixture guard: {field.Name}'s count is zero — FillDistinctValues must "
                        + "populate at least one element or this field's copy is never exercised");
                    System.Array arr = (System.Array)field.GetValue(source);
                    for (int i = 0; i < count; i++)
                        Assert.IsFalse(IsDefaultValue(arr.GetValue(i)),
                            $"fixture guard: {field.Name}[{i}] is still default — FillDistinctValues "
                            + "must give it a non-default value before the CopyFrom comparison means "
                            + "anything");
                }
                else
                {
                    Assert.IsFalse(IsDefaultValue(field.GetValue(source)),
                        $"fixture guard: {field.Name} is still default after FillDistinctValues — "
                        + "extend the filler before trusting the reflection test's coverage of it");
                }
            }
        }

        [Test]
        public void CopyFrom_CopiesEveryPublicField_ByReflection()
        {
            var cfg = TestConfigs.Default();
            ArenaSimConfig arena = cfg.Arena;
            var source = new RenderSnapshot(in cfg);
            var dest = new RenderSnapshot(in cfg);
            FillDistinctValues(source, in arena);
            AssertEveryFieldIsNonDefault(source); // fix-round 1 IMPORTANT #3 guard

            dest.CopyFrom(source);

            foreach (FieldInfo field in RenderSnapshotFields)
            {
                if (field.FieldType.IsArray)
                {
                    Assert.IsTrue(ArrayCountField.TryGetValue(field.Name, out string countFieldName),
                        $"RenderSnapshot grew a new array field ({field.Name}) this test's count map "
                        + "does not know about — extend ArrayCountField before trusting CopyFrom with "
                        + "it (task brief §2.5, test 6)");
                    FieldInfo countField = typeof(RenderSnapshot).GetField(countFieldName);
                    int count = (int)countField.GetValue(dest);
                    System.Array sourceArr = (System.Array)field.GetValue(source);
                    System.Array destArr = (System.Array)field.GetValue(dest);
                    for (int i = 0; i < count; i++)
                        Assert.AreEqual(sourceArr.GetValue(i), destArr.GetValue(i),
                            $"{field.Name}[{i}] diverged after CopyFrom");
                }
                else
                {
                    Assert.AreEqual(field.GetValue(source), field.GetValue(dest),
                        $"{field.Name} diverged after CopyFrom — a field CopyFrom forgot to copy stays "
                        + "at dest's construction-time default");
                }
            }

            // Explicit insurance asserts (task brief §2.5, test 6) — the
            // frozen hitstop pair's own most load-bearing fields, pinned by
            // name rather than only through the generic loop above.
            Assert.AreEqual(source.PlayerCount, dest.PlayerCount);
            Assert.AreEqual(source.LocalPlayerIndex, dest.LocalPlayerIndex);
            Assert.AreEqual(source.WorldStats, dest.WorldStats);
            for (int i = 0; i < source.PlayerCount; i++)
                Assert.AreEqual(source.Players[i], dest.Players[i]);
        }

        // T32.7 (plan Step 1 #7, hitstop regression) — deliberately NOT a
        // separate test. `SimulationRunner.FreezeRender`/`UnfreezeRender` now
        // call `CopyFrom` at the same three call sites the removed
        // `CopySnapshot` occupied, with no logic of their own beyond the
        // call — `CopyFrom_CopiesEveryPublicField_ByReflection` above already
        // exercises the copy exhaustively, including the exact fields
        // (`Players`/`PlayerCount`/`LocalPlayerIndex`/`WorldStats`) the task
        // brief calls out as the frozen pair's insurance. A `SimulationRunner`
        // test would need a `MonoBehaviour`/scene fixture this test assembly
        // has no other precedent for, to re-prove a copy already proven at
        // its source — see the task report for the full accounting.

        // ---------------------------------------------------------------------
        // T32.8 (coordinator #8, Р150е). FutureRejected does not poison NewestTick.
        // ---------------------------------------------------------------------

        [Test]
        public void FutureRejected_DoesNotPoisonNewest_AndDedupNeverSeesIt()
        {
            var cfg = TestConfigs.Default();
            var queue = NewQueue(out _);
            var dedup = new EventDedup(cfg);
            queue.Reset(Epoch);
            dedup.Reset(Epoch);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted,
                DriveFrame(queue, dedup, Epoch, 100, new[] { Record(1) }, out bool applied0, out _));
            Assert.IsTrue(applied0);

            uint poison = 100u + (uint)SnapshotQueue.FutureHorizonTicks + 1u;
            var poisonEvent = Record(seq: 99, tickDelta: 0);
            var verdict = DriveFrame(queue, dedup, Epoch, poison, new[] { poisonEvent },
                out bool poisonApplied, out bool[] poisonResults);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.FutureRejected, verdict);
            Assert.IsFalse(poisonApplied);
            Assert.AreEqual(0, poisonResults.Length,
                "the driver never even asks EventDedup about a FutureRejected frame — task brief "
                + "§2.2 item 2: dedup never sees this frame's events at all");

            Assert.AreEqual(100u, queue.NewestTick,
                "Р150е: a rejected frame must not move the admission floor — the poisoned tick never "
                + "lands as NewestTick");

            // Direct witness that EventDedup itself never saw the poisoned
            // key: if the driver HAD fed it through (a contract violation),
            // this exact key would already read as seen here.
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, poison, in poisonEvent, out _),
                "the poisoned frame's own event is still UNSEEN by dedup — proof the driver stopped "
                + "before calling TryAcceptEvent, not merely that the call would have been refused");

            // The very next ordinary frame is judged against the floor it
            // already had, not against the tick the rejected frame named.
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 101, out _),
                "a rejected frame must not poison admission for the very next honest one");
            queue.Commit(101);

            // Boundary witness, on a fresh queue: EXACTLY at the horizon is
            // still accepted — only STRICTLY further is rejected. The first
            // admission MUST be committed, or `_hasNewestAccepted` stays false
            // and the horizon check below would be skipped entirely (the
            // "first frame since Reset" bypass), testing nothing.
            var boundaryQueue = NewQueue(out _);
            boundaryQueue.Reset(Epoch);
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, boundaryQueue.Admit(Epoch, 200, out _));
            boundaryQueue.Commit(200);
            uint boundary = 200u + (uint)SnapshotQueue.FutureHorizonTicks;
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, boundaryQueue.Admit(Epoch, boundary, out _),
                "the boundary tick — exactly FutureHorizonTicks ahead — is still accepted");
        }

        // ---------------------------------------------------------------------
        // T32.9 (coordinator #9). A foreign epoch is a silent refusal on both sides.
        // ---------------------------------------------------------------------

        [Test]
        public void ForeignEpoch_IsSilentlyRejectedByQueueAndDedup_OwnEpochWorks()
        {
            var cfg = TestConfigs.Default();
            var queue = NewQueue(out _);
            var dedup = new EventDedup(cfg);
            queue.Reset(Epoch);
            dedup.Reset(Epoch);

            var record = Record(seq: 4);
            var verdict = DriveFrame(queue, dedup, OtherEpoch, 50, new[] { record },
                out bool applied, out bool[] results);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.ForeignEpoch, verdict);
            Assert.IsFalse(applied);
            Assert.AreEqual(0, results.Length, "a foreign epoch never reaches EventDedup through the driver");

            Assert.IsFalse(dedup.TryAcceptEvent(OtherEpoch, 50, in record, out _),
                "direct witness: EventDedup itself refuses the stray epoch too, on its own account");

            // Positive witness: the SAME tick, the TRACKED epoch, works on both sides.
            var ownVerdict = DriveFrame(queue, dedup, Epoch, 50, new[] { record },
                out bool ownApplied, out bool[] ownResults);
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, ownVerdict);
            Assert.IsTrue(ownApplied);
            Assert.IsTrue(ownResults[0]);
        }

        // ---------------------------------------------------------------------
        // T32.10 (coordinator #10). Nothing is accepted before the first Reset.
        // ---------------------------------------------------------------------

        [Test]
        public void NothingAcceptedBeforeFirstReset_ThenAccepted()
        {
            var queue = NewQueue(out _);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.ForeignEpoch, queue.Admit(Epoch, 1, out RenderSnapshot slot),
                "before the first Reset there is no tracked epoch, so nothing is admitted");
            Assert.IsNull(slot);
            Assert.AreEqual(0, queue.Count);

            queue.Reset(Epoch);
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 1, out RenderSnapshot slot2),
                "positive witness: the very same tick works right after Reset");
            Assert.IsNotNull(slot2);
        }

        // ---------------------------------------------------------------------
        // T32.11 (coordinator #11). DiscardBelow: discharged is Stale, not gone forever.
        // ---------------------------------------------------------------------

        [Test]
        public void DiscardBelow_DischargedTickBecomesStale_ValidNewestSurvives()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);

            for (uint t = 1; t <= 5; t++)
            {
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _));
                queue.Commit(t);
            }

            queue.DiscardBelow(3); // ticks 1, 2 discharged; 3, 4, 5 remain resident.
            Assert.IsFalse(queue.TryGet(1, out _));
            Assert.IsFalse(queue.TryGet(2, out _));
            Assert.IsTrue(queue.TryGet(3, out _), "DiscardBelow's floor is exclusive of the tick named");
            Assert.IsTrue(queue.TryGet(4, out _));
            Assert.IsTrue(queue.TryGet(5, out _));
            Assert.AreEqual(3, queue.Count);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Stale, queue.Admit(Epoch, 1, out RenderSnapshot slot),
                "task brief §2.5 test 11: a re-arriving DISCHARGED tick is Stale, not Duplicate — the "
                + "ring holds no resident copy of it to be a duplicate OF");
            Assert.IsNull(slot);

            Assert.AreEqual(5u, queue.NewestTick, "discharging old ticks must not disturb the valid newest");
            Assert.IsTrue(queue.TryGet(5, out _), "positive witness: still resident after the discharge");

            // Regression coverage for Р82 discipline: a repeated or REGRESSIVE
            // floor must not resurrect anything already discharged.
            queue.DiscardBelow(3); // same floor again — a no-op, not a re-scan that breaks anything.
            Assert.IsTrue(queue.TryGet(3, out _));
            queue.DiscardBelow(2); // an OLDER floor than already reached — silently ignored.
            Assert.IsFalse(queue.TryGet(1, out _),
                "a regressive DiscardBelow must never resurrect an already-discharged tick");
        }

        // ---------------------------------------------------------------------
        // Fix-round 2, W1. A tick beyond int.MaxValue can never be
        // represented downstream (RenderClock.OnSnapshot/StalePolicy's own
        // MaxRepresentableTick guards) — this queue is the sole OTHER
        // consumer of the same wire tick and, until this fix, had no guard
        // of its own.
        // ---------------------------------------------------------------------

        [Test]
        public void UnrepresentableTick_IsFutureRejected_NextOrdinaryFrameStillAccepted()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);

            const uint unrepresentable = uint.MaxValue; // > int.MaxValue.
            var verdict = queue.Admit(Epoch, unrepresentable, out RenderSnapshot slot);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.FutureRejected, verdict,
                "a tick beyond int.MaxValue can never be represented downstream — the queue must "
                + "refuse it the same way RenderClock.OnSnapshot/StalePolicy already do at this bound.");
            Assert.IsNull(slot, "a refused admission never hands back a slot");
            Assert.IsFalse(queue.HasNewestTick,
                "the absurd tick must never become NewestTick — nothing has been accepted yet");

            // Positive witness: the very next ORDINARY frame is unaffected —
            // the floor was never poisoned by the refusal above.
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 1, out RenderSnapshot slot2),
                "the FutureRejected refusal above must not poison admission for the very next honest frame");
            Assert.IsNotNull(slot2);
            queue.Commit(1);
            Assert.AreEqual(1u, queue.NewestTick);

            // Boundary witness, on a fresh queue: EXACTLY int.MaxValue is
            // still representable and passes through the ORDINARY branches
            // — only STRICTLY beyond it triggers the new guard. The very
            // first tick since Reset bypasses the horizon check entirely
            // (class doc's KNOWN LIMIT), isolating this assertion to the
            // representability guard alone rather than the horizon one.
            var boundaryQueue = NewQueue(out _);
            boundaryQueue.Reset(Epoch);
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted,
                boundaryQueue.Admit(Epoch, (uint)int.MaxValue, out RenderSnapshot boundarySlot),
                "tick == int.MaxValue exactly is still representable — only strictly beyond it is refused");
            Assert.IsNotNull(boundarySlot);
            boundaryQueue.Commit((uint)int.MaxValue);
            Assert.AreEqual((uint)int.MaxValue, boundaryQueue.NewestTick);
        }

        // ---------------------------------------------------------------------
        // T32.12 (coordinator #12). The data path allocates nothing.
        // ---------------------------------------------------------------------

        [Test]
        public void Admission_TryGet_DiscardBelow_DoNotAllocateGCMemory()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);

            // Warm-up: JIT, and the stub-defeating premise (Task 26 finding
            // F-D) that the thing being measured actually works before
            // anything is concluded from the absence of allocation.
            for (uint t = 1; t <= 5; t++)
            {
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _));
                queue.Commit(t);
                Assert.IsTrue(queue.TryGet(t, out _));
            }
            queue.DiscardBelow(3);
            Assert.IsFalse(queue.TryGet(1, out _), "fixture premise: DiscardBelow really did discharge");

            Assert.That(() =>
            {
                for (uint t = 6; t < 6 + 500; t++)
                {
                    queue.Admit(Epoch, t, out RenderSnapshot _);
                    queue.Commit(t);
                    queue.TryGet(t, out RenderSnapshot _);
                    queue.DiscardBelow(t - 3);
                }
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
