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
    /// the single deep-copy routine every consumer of a snapshot pair now
    /// shares.
    ///
    /// TWO SUBJECTS, ONE FILE, BECAUSE THE TASK BRIEF PUTS THEM IN ONE FILE.
    /// `SnapshotQueue`'s own admission logic (tests 1, 3, 4, 5, 8, 9, 10, 11,
    /// 12 below) and `RenderSnapshot.CopyFrom` (test 6) are otherwise
    /// unrelated — the queue hands out `CopyFrom`'s TARGET (a preallocated
    /// slot to decode into), it never calls `CopyFrom` itself. They are pinned
    /// together here because both halves of Task 32 exist to serve the exact
    /// same downstream consumer: Task 44's admitted-frame pipeline into the
    /// render pair `SimulationRunner` owns.
    ///
    /// THE INTEGRATION TESTS (2, 8, 9) DRIVE A REAL `EventDedup` THROUGH A
    /// TEST-ONLY DRIVER, `DriveFrame`. Task 32's own scope is the queue's
    /// admission decision alone — wiring a live `EventDedup` into it is Task
    /// 44's job — but the CONTRACT between the two (task brief §2.2) is
    /// exactly the thing `EventDedup`'s own KNOWN LIMIT paragraph (fix round
    /// 1, reviewer F2) hands to this task to close, so proving the contract
    /// actually composes with the real class, not a mock of it, is the whole
    /// point of Р150е. `DriveFrame` is not production code and does not
    /// pretend to be FishNet-wired Task 44 — it is the smallest thing that
    /// follows the four numbered steps of task brief §2.2 exactly.
    ///
    /// NUMBERS COME FROM FIXTURES, NEVER FROM `.asset` (Р56, same discipline
    /// as `RenderClockTests`): `Timings()` builds `NetTimings` by hand,
    /// `TestConfigs.DefaultArena()`/`TestConfigs.Default()` are the existing
    /// Simulation-side fixtures (no new literal caps are invented here), and
    /// `SnapshotQueue.FutureHorizonTicks` is read off the real constant
    /// rather than restated as a literal 270.
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
            var arena = TestConfigs.DefaultArena();
            timings = Timings(interpBufferTicks);
            return new SnapshotQueue(in arena, in timings);
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
        ///      sees this frame's events at all.
        ///   3. `Stale`/`Duplicate` — state is not applied, but every event
        ///      still goes through `EventDedup.TryAcceptEvent`.
        ///   4. `Accepted` — the slot was handed out (state "applied"), and
        ///      the events go through dedup exactly the same as step 3.
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
                eventAccepted[i] = dedup.TryAcceptEvent(epoch, tick, in events[i]);

            if (verdict == SnapshotQueue.AdmitVerdict.Accepted)
            {
                Assert.IsNotNull(slot, "fixture premise: Accepted always hands back a slot");
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
            var arena = TestConfigs.DefaultArena();
            var timings = Timings(interpBufferTicks: 3);
            var queue = new SnapshotQueue(in arena, in timings);

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
            // brief §2.1: "older than the ring can hold at the current newest".
            uint staleTick = 50u - (uint)queue.Depth;
            var freshEvent = Record(seq: 2, tickDelta: 0);

            var verdict = DriveFrame(queue, dedup, Epoch, staleTick, new[] { freshEvent },
                out bool applied, out bool[] results);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Stale, verdict);
            Assert.IsFalse(applied, "task brief §2.2 п.3: a Stale frame's STATE is never applied");
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
            Assert.AreEqual(101u, queue.NewestTick);

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 100, out RenderSnapshot slot),
                "Р37: a reordered frame still inside the window fills the hole it left — the entire "
                + "reason the ring exists");
            Assert.IsNotNull(slot);
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

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Duplicate, queue.Admit(Epoch, 50, out RenderSnapshot slot),
                "the exact same tick, still resident in the ring, is a duplicate — not a fresh admission");
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
            Assert.AreEqual(1000u, queue.NewestTick, "fixture premise: deep inside the old epoch");

            queue.Reset(newEpoch);
            Assert.IsFalse(queue.HasNewestTick, "a reset forgets the newest tick, not merely re-bases it");
            Assert.AreEqual(0, queue.Count, "and empties the ring");

            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(newEpoch, 5, out RenderSnapshot slot),
                "a tick far SMALLER than anything the old epoch ever saw is ordinary in the new one — "
                + "a restarted match replays its ticks from (near) zero");
            Assert.IsNotNull(slot);
            Assert.AreEqual(5u, queue.NewestTick);
        }

        // ---------------------------------------------------------------------
        // T32.5 (plan Step 1 #5). Overflow evicts the oldest, and counts it (Р83).
        // ---------------------------------------------------------------------

        [Test]
        public void Overflow_EvictsOldest_CountsAndKeepsNewestAlive()
        {
            var queue = NewQueue(out _);
            queue.Reset(Epoch);
            int depth = queue.Depth;
            Assert.AreEqual(5, depth, "fixture premise");

            for (uint t = 1; t <= (uint)depth; t++)
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _));
            Assert.AreEqual(depth, queue.Count, "fixture premise: the ring is full, nothing discharged");
            Assert.AreEqual(0, queue.OverflowDroppedSnapshots, "fixture premise: no eviction has happened yet");

            // A burst of MORE frames than the ring can hold, none of them
            // discharged — the Р83 scenario (a batch of delayed datagrams).
            const int extra = 3;
            for (uint t = (uint)depth + 1; t <= (uint)(depth + extra); t++)
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out RenderSnapshot slot),
                    $"tick {t}: a newer frame is accepted even while the ring is completely full");

            Assert.AreEqual(extra, queue.OverflowDroppedSnapshots,
                "Р83: one eviction per frame admitted past the ring's capacity");
            Assert.AreEqual(depth, queue.Count, "the ring never holds more entries than its own depth");

            for (uint t = 1; t <= extra; t++)
                Assert.IsFalse(queue.TryGet(t, out _), $"tick {t}: evicted — it was the oldest");
            for (uint t = (uint)extra + 1; t <= (uint)(depth + extra); t++)
                Assert.IsTrue(queue.TryGet(t, out _),
                    $"tick {t}: still resident — nothing this new was touched by the eviction");
            Assert.AreEqual((uint)(depth + extra), queue.NewestTick);
        }

        // ---------------------------------------------------------------------
        // T32.6 (plan Step 1 #6). CopyFrom, by reflection over every public field.
        // ---------------------------------------------------------------------

        static readonly FieldInfo[] RenderSnapshotFields =
            typeof(RenderSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance);

        /// Array fields mapped to the COUNT field that bounds their meaningful
        /// content (task brief §2.5 test 6: "по содержимому до счётчиков" —
        /// the arena-capacity slots past the count are never written by
        /// anything and comparing them would only pin `default`s). A future
        /// array field this map has not been taught about fails LOUDLY in the
        /// loop below instead of silently comparing nothing.
        static readonly Dictionary<string, string> ArrayCountField = new Dictionary<string, string>
        {
            { nameof(RenderSnapshot.Players), nameof(RenderSnapshot.PlayerCount) },
            { nameof(RenderSnapshot.Mobs), nameof(RenderSnapshot.MobCount) },
            { nameof(RenderSnapshot.Projectiles), nameof(RenderSnapshot.ProjectileCount) },
            { nameof(RenderSnapshot.PlayerStats), nameof(RenderSnapshot.PlayerCount) },
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
            s.Wave = new WaveState
            {
                Phase = WavePhase.Active,
                WaveIndex = 3,
                PendingChasers = 2,
                PendingGunners = 1,
                AliveCount = 5,
                PhaseTimer = 1.25f,
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
        }

        [Test]
        public void CopyFrom_CopiesEveryPublicField_ByReflection()
        {
            var arena = TestConfigs.DefaultArena();
            var source = new RenderSnapshot(in arena);
            var dest = new RenderSnapshot(in arena);
            FillDistinctValues(source, in arena);

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
                + "§2.2 п.2, 'СТОП, дедуп кадра НЕ ВИДИТ'");

            Assert.AreEqual(100u, queue.NewestTick,
                "Р150е: a rejected frame must not move the admission floor — the poisoned tick never "
                + "lands as NewestTick");

            // Direct witness that EventDedup itself never saw the poisoned
            // key: if the driver HAD fed it through (a contract violation),
            // this exact key would already read as seen here.
            Assert.IsTrue(dedup.TryAcceptEvent(Epoch, poison, in poisonEvent),
                "the poisoned frame's own event is still UNSEEN by dedup — proof the driver stopped "
                + "before calling TryAcceptEvent, not merely that the call would have been refused");

            // The very next ordinary frame is judged against the floor it
            // already had, not against the tick the rejected frame named.
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, 101, out _),
                "a rejected frame must not poison admission for the very next honest one");

            // Boundary witness, on a fresh queue: EXACTLY at the horizon is
            // still accepted — only STRICTLY further is rejected.
            var boundaryQueue = NewQueue(out _);
            boundaryQueue.Reset(Epoch);
            Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, boundaryQueue.Admit(Epoch, 200, out _));
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

            Assert.IsFalse(dedup.TryAcceptEvent(OtherEpoch, 50, in record),
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
                Assert.AreEqual(SnapshotQueue.AdmitVerdict.Accepted, queue.Admit(Epoch, t, out _));

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
                Assert.IsTrue(queue.TryGet(t, out _));
            }
            queue.DiscardBelow(3);
            Assert.IsFalse(queue.TryGet(1, out _), "fixture premise: DiscardBelow really did discharge");

            Assert.That(() =>
            {
                for (uint t = 6; t < 6 + 500; t++)
                {
                    queue.Admit(Epoch, t, out RenderSnapshot _);
                    queue.TryGet(t, out RenderSnapshot _);
                    queue.DiscardBelow(t - 3);
                }
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
