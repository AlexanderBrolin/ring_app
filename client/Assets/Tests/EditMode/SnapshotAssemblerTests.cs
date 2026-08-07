using NUnit.Framework;
using Ring.Data;
using Ring.Networking.Protocol;
using Ring.Networking.Server;
using Ring.Simulation.Core;
using Ring.Simulation.Visibility;
using Unity.Mathematics;
using UnityEngine;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below are
// required by the file, not just convenience imports (SnapshotCodecTests.cs and
// InputCodecTests.cs carry the same pair for the same reason).
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    /// One assembled frame, decoded all the way down to event payloads
    /// (Stage 2 Task 28). Lives at namespace scope rather than inside
    /// SnapshotAssemblerTests because SnapshotCodecTests needs it too — the
    /// plan puts `WorstCase_ByCaps_TriggersTruncation` and
    /// `EventBudget_PrioritizesDeaths` in THAT file while the rest of the
    /// assembly scenarios live here, and a byte-identical copy in both would
    /// be exactly the duplication this project's rule 2 forbids.
    ///
    /// Deliberately ALLOCATES: it is a test-side view, never used inside the
    /// zero-allocation measurement below.
    public sealed class AssembledFrame
    {
        public ushort Epoch;
        public uint Tick;
        public byte Flags;
        public int Bytes;

        public SnapshotBlocks.PlayerRecord[] Players;
        public int PlayerCount;
        public byte AliveMask;
        public SnapshotBlocks.MobRecord[] Mobs;
        public int MobCount;
        public WavePhase WavePhase;
        public ushort WaveIndex;
        public byte WaveAliveCount;
        public SnapshotBlocks.EventRecord[] Events;
        public SnapshotEventPayload[] Payloads;
        public int EventCount;

        /// Bit 0 of the header's flags byte — "this frame dropped at least one
        /// entity for room" (SnapshotHeaderFlags.Truncated).
        public bool Truncated => (Flags & SnapshotHeaderFlags.Truncated) != 0;

        public int CountOf(SnapshotEventKind kind)
        {
            int n = 0;
            for (int i = 0; i < EventCount; i++)
                if ((SnapshotEventKind)Events[i].Kind == kind) n++;
            return n;
        }

        public bool TryFirstOf(SnapshotEventKind kind, out int index)
        {
            for (int i = 0; i < EventCount; i++)
                if ((SnapshotEventKind)Events[i].Kind == kind) { index = i; return true; }
            index = -1;
            return false;
        }

        public bool ContainsMob(int id)
        {
            for (int i = 0; i < MobCount; i++)
                if (Mobs[i].Id == id) return true;
            return false;
        }

        public bool TryPlayer(int index, out SnapshotBlocks.PlayerRecord record)
        {
            for (int i = 0; i < PlayerCount; i++)
                if (Players[i].Index == index) { record = Players[i]; return true; }
            record = default;
            return false;
        }

        static readonly byte[] AllKinds =
        {
            (byte)SnapshotBlockKind.Players, (byte)SnapshotBlockKind.Liveness,
            (byte)SnapshotBlockKind.Mobs, (byte)SnapshotBlockKind.Wave, (byte)SnapshotBlockKind.Events,
        };

        /// Reads `bytes` of `buffer` back through the Task 26/27 codec — the
        /// real receive path, not a private mirror of the writer, so a
        /// round-trip here proves the frame is decodable by what Task 32 will
        /// actually run.
        public static AssembledFrame Decode(byte[] buffer, int bytes, in SimConfig cfg)
        {
            var f = new AssembledFrame
            {
                Bytes = bytes,
                Players = new SnapshotBlocks.PlayerRecord[math.max(1, cfg.Arena.MaxPlayers)],
                Mobs = new SnapshotBlocks.MobRecord[math.max(1, cfg.Arena.MaxMobs)],
                Events = new SnapshotBlocks.EventRecord[math.max(1, cfg.Arena.MaxEventsPerFrame)],
            };
            f.Payloads = new SnapshotEventPayload[f.Events.Length];

            var reader = new SnapshotReader(new System.ReadOnlySpan<byte>(buffer, 0, bytes));
            Assert.IsTrue(reader.TryReadHeader(out f.Epoch, out f.Tick, out f.Flags),
                "an assembled frame must always carry a well-formed header");

            int blocks = 0;
            while (reader.TryReadBlock(AllKinds, out byte kind, out System.ReadOnlySpan<byte> payload))
            {
                blocks++;
                switch ((SnapshotBlockKind)kind)
                {
                    case SnapshotBlockKind.Players:
                        Assert.IsTrue(SnapshotBlocks.TryReadPlayersBlock(payload, cfg, f.Players,
                            out f.PlayerCount, out SnapshotBlockError pe), $"Players block refused: {pe}");
                        break;
                    case SnapshotBlockKind.Liveness:
                        Assert.IsTrue(SnapshotBlocks.TryReadLivenessBlock(payload, out f.AliveMask,
                            out SnapshotBlockError le), $"Liveness block refused: {le}");
                        break;
                    case SnapshotBlockKind.Mobs:
                        Assert.IsTrue(SnapshotBlocks.TryReadMobsBlock(payload, cfg, f.Mobs,
                            out f.MobCount, out SnapshotBlockError me), $"Mobs block refused: {me}");
                        break;
                    case SnapshotBlockKind.Wave:
                        Assert.IsTrue(SnapshotBlocks.TryReadWaveBlock(payload, out f.WavePhase,
                            out f.WaveIndex, out f.WaveAliveCount, out SnapshotBlockError we),
                            $"Wave block refused: {we}");
                        break;
                    case SnapshotBlockKind.Events:
                        Assert.IsTrue(SnapshotBlocks.TryReadEventsBlock(payload, cfg, f.Events,
                            out f.EventCount, out SnapshotBlockError ee), $"Events block refused: {ee}");
                        for (int i = 0; i < f.EventCount; i++)
                        {
                            SnapshotBlocks.EventRecord r = f.Events[i];
                            Assert.IsTrue(SnapshotEvents.TryReadPayload((SnapshotEventKind)r.Kind,
                                payload.Slice(r.PayloadOffset, r.PayloadLength), cfg,
                                out f.Payloads[i], out SnapshotBlockError xe),
                                $"event {i} (kind {r.Kind}) payload refused: {xe}");
                        }
                        break;
                }
            }

            Assert.IsFalse(reader.Failed, "an assembled frame must parse cleanly to its end");
            Assert.AreEqual(5, blocks,
                "the assembler always emits all five blocks in canonical order, even when a block is empty");
            return f;
        }
    }

    /// Stage 2 Task 28 (spec §3.5/§3.7 Р28/§3.8, Р32/Р49/Р61/Р62/Р70/Р101/
    /// Р132/Р133/Р136/Р137/Р140): per-connection snapshot ASSEMBLY — who is in
    /// the frame, which events reach whom, and what is dropped when the byte
    /// budget runs out.
    ///
    /// EVENTS ARE INJECTED THROUGH `SimulationWorld.Emit` (internal, visible to
    /// this assembly). Driving the real systems into emitting one specific kind
    /// at one specific position takes a fixture per kind and couples every
    /// routing assertion to unrelated balance numbers; the emit seam states the
    /// event directly, which is what these tests are about. The seam's own
    /// per-kind field conventions are pinned elsewhere (EventTests,
    /// EventDeliveryTests), and one fixture below deliberately goes the long
    /// way round through a real shot to keep the two honest.
    public class SnapshotAssemblerTests
    {
        const ushort Epoch = 0x4D31;    // 19761 — both bytes nonzero and different

        /// A NetConfig with the three numbers the assembler reads, stated per
        /// fixture. `CreateInstance` rather than a shipped asset: the .asset is
        /// the game's numbers, a fixture is the test's (spec §0's two homes).
        static NetConfig Net(int maxBytes = 1000, int eventBudget = 16, int redundancyTicks = 4)
        {
            var net = ScriptableObject.CreateInstance<NetConfig>();
            net.SnapshotMaxBytes = maxBytes;
            net.SnapshotEventBudget = eventBudget;
            net.EventRedundancyTicks = redundancyTicks;
            return net;
        }

        /// Open arena (no obstacles, no walls, waves pushed out of reach) with
        /// the three players placed by hand — Geometry.SpawnPosFor would put
        /// them on the spawn ring at radius 52, where every distance a
        /// visibility fixture states would be incidental.
        static SimulationWorld Trio(out SimConfig cfg, float2 p0, float2 p1, float2 p2)
        {
            cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, p0);
            TestWorlds.RelocatePlayerForTest(w, 1, p1);
            TestWorlds.RelocatePlayerForTest(w, 2, p2);
            return w;
        }

        static AssembledFrame Build(SnapshotAssembler asm, SimulationWorld w, in SimConfig cfg,
            int connection, int identityIndex, int viewpointIndex)
        {
            asm.BeginTick(w);
            int bytes = asm.BuildFor(connection, identityIndex, viewpointIndex, Epoch);
            return AssembledFrame.Decode(asm.BufferFor(connection), bytes, cfg);
        }

        // ---- T28.A1. A full frame round-trips through the real codec ----

        [Test]
        public void FullFrame_RoundTrips_WithPlayersLivenessMobsWaveAndEvents()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            int mobA = w.SpawnMobForTest(MobType.Chaser, new float2(3f, 3f));
            int mobB = w.SpawnMobForTest(MobType.Gunner, new float2(-4f, 2f));
            // SpawnMobForTest goes through the real spawn path, so it emits a
            // MobSpawned of its own — cleared here so the event assertions
            // below are about the one event this fixture states.
            w.ClearEvents();

            // A wave event: All-channel, so it must ride with NO position at
            // all (Р28 — today's wave position is simply the nearest player's).
            var wavePos = new float2(11f, -13f);
            w.Emit(SimEventKind.WaveStarted, wavePos, 0, default, 0f);

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, connection: 0, identityIndex: 0, viewpointIndex: 0);

            Assert.AreEqual(Epoch, f.Epoch, "the epoch the caller handed BuildFor must ride the header");
            Assert.AreEqual((uint)w.CurrentTick, f.Tick, "the frame is stamped with the world's own tick");
            Assert.IsFalse(f.Truncated, "nothing was dropped in a frame this small");

            // Players: the OTHER two, never oneself — one's own state comes
            // back through reconciliation, not through the snapshot (spec §3.8:
            // "up to MaxPlayers - 1").
            Assert.AreEqual(2, f.PlayerCount, "two other players, and never the connection's own");
            Assert.IsFalse(f.TryPlayer(0, out _), "the connection's own slot must NOT be in the Players block");
            Assert.IsTrue(f.TryPlayer(1, out SnapshotBlocks.PlayerRecord r1));
            Assert.IsTrue(f.TryPlayer(2, out SnapshotBlocks.PlayerRecord r2));
            Assert.That(math.distance(r1.Pos, w.PlayerAt(1).Pos), Is.LessThan(0.01f));
            Assert.That(math.distance(r2.Pos, w.PlayerAt(2).Pos), Is.LessThan(0.01f));
            Assert.That(r1.Hp, Is.EqualTo(w.PlayerAt(1).Hp).Within(0.5f));

            // Liveness: every slot of the match, not only the visible ones.
            Assert.AreEqual((byte)0b111, f.AliveMask, "all three slots are alive");

            Assert.AreEqual(2, f.MobCount);
            Assert.IsTrue(f.ContainsMob(mobA));
            Assert.IsTrue(f.ContainsMob(mobB));

            Assert.AreEqual(w.WaveRef.Phase, f.WavePhase);
            Assert.AreEqual((ushort)w.WaveRef.WaveIndex, f.WaveIndex);
            Assert.AreEqual((byte)w.WaveRef.AliveCount, f.WaveAliveCount);

            Assert.AreEqual(1, f.EventCount, "exactly the one event emitted this tick");
            Assert.AreEqual((byte)SnapshotEventKind.WaveStarted, f.Events[0].Kind);
            Assert.AreEqual((byte)0, f.Events[0].TickDelta, "an event of THIS tick rides at delta 0");
            Assert.That(math.length(f.Events[0].Pos), Is.LessThan(0.01f),
                "an All-channel event must carry no position — otherwise every observer learns "
                + "the position WaveSystem happened to take it from, for free, every wave");
            Assert.AreNotEqual(wavePos, f.Events[0].Pos);
        }

        // ---- T28.A2. A corpse is ordinary replicated state ----

        [Test]
        public void DeadOtherPlayer_IsStillReplicated_WithTheAliveBitClear()
        {
            // carryover-t28.md §8в: spec §3.5 keeps a dead player's body
            // visible by the ORDINARY rules, so the assembler must NOT add a
            // liveness guard of its own to the Players block — a corpse that
            // vanished from the frame the tick it died would pop out of the
            // world instead of lying where it fell. The Alive BIT is what
            // carries the news (Р68).
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            w.KillPlayerNoDamage(1);
            Assert.IsFalse(w.PlayerAt(1).Alive, "test setup: player 1 must actually be dead");

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.AreEqual(2, f.PlayerCount, "the corpse is still a record — both other players ride");
            Assert.IsTrue(f.TryPlayer(1, out SnapshotBlocks.PlayerRecord corpse));
            Assert.AreEqual(0, corpse.Flags & PlayerWireFlags.Alive,
                "the corpse's own record must carry a CLEAR Alive bit");
            Assert.That(math.distance(corpse.Pos, new float2(6f, 0f)), Is.LessThan(0.01f),
                "the body stays where it fell");

            Assert.IsTrue(f.TryPlayer(2, out SnapshotBlocks.PlayerRecord living),
                "witness: the living player is still there too, so PlayerCount means what it says");
            Assert.AreNotEqual(0, living.Flags & PlayerWireFlags.Alive);

            Assert.AreEqual((byte)0b101, f.AliveMask,
                "the liveness mask is the registry of the whole match (Р70) — slot 1 clear, 0 and 2 set");
        }

        // ---- T28.A3. Liveness covers EVERY slot, visible or not ----

        [Test]
        public void LivenessMask_CoversEverySlot_NotOnlyTheVisibleOnes()
        {
            // Р70: a dead player needs the full roster of spectate candidates,
            // so this mask is deliberately NOT filtered by visibility. It is
            // also not a position leak — one bit per slot says who is playing,
            // not where.
            SimConfig probe = TestConfigs.Open();
            float far = probe.Visibility.SightRadius + probe.Visibility.HearRadius + 50f;
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(far, 0f));

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.AreEqual(1, f.PlayerCount,
                "test setup: player 2 is far outside SightRadius, so only player 1 rides the Players block");
            Assert.IsFalse(f.TryPlayer(2, out _), "test setup: the far player really is filtered out of Players");
            Assert.AreEqual((byte)0b111, f.AliveMask,
                "yet the liveness mask still names all three slots — filtering it by visibility would "
                + "leave a dead observer with no roster to spectate from (Р70)");
        }

        // ---- T28.A4. Fog of war: CRITICAL RULE 4 ----

        [Test]
        public void MobFilter_InvisibleAbsent_LingeringPresent_BehindObstacleAbsent()
        {
            var cfg = TestConfigs.Open();
            // One obstacle dead on the ray to the mob at (10, 0) — the same
            // shape VisibilityTests.BehindObstacle_NotVisible uses.
            cfg.Arena.ObstacleCount = 1;
            cfg.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 2f };

            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            int blocked = w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));     // behind the obstacle
            int visible = w.SpawnMobForTest(MobType.Chaser, new float2(0f, 10f));     // clear line
            int faraway = w.SpawnMobForTest(MobType.Chaser,
                new float2(0f, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis + 5f));

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.IsTrue(f.ContainsMob(visible), "witness: a mob in plain sight must be replicated");
            Assert.IsFalse(f.ContainsMob(blocked),
                "CRITICAL RULE 4: a mob behind an obstacle must not reach the client at all — "
                + "shipping it and letting the client hide it IS the ESP hole fog of war exists to close");
            Assert.IsFalse(f.ContainsMob(faraway), "a mob past SightRadius + ExitHysteresis is not replicated");
            Assert.AreEqual(1, f.MobCount);

            // Linger (Р19): the visible mob is teleported out of range, and
            // must keep riding for LingerTicks so the client's interpolation
            // buffer — which draws it in the recent past — does not lose it
            // mid-timeline.
            MobState m = w.Mobs[0];
            Assert.AreEqual(blocked, m.Id, "test setup: slot 0 is the blocked mob (spawn order)");
            MobState moved = w.Mobs[1];
            Assert.AreEqual(visible, moved.Id, "test setup: slot 1 is the mob about to be moved");
            moved.Pos = new float2(0f, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis + 5f);
            w.SetMobForTest(1, moved);

            AssembledFrame f2 = Build(asm, w, cfg, 0, 0, 0);
            Assert.IsTrue(f2.ContainsMob(visible),
                "a mob that just left sight must keep riding through the linger window (Р19)");
        }

        // ---- T28.A5. `seq` is assigned once per tick, for every connection ----

        [Test]
        public void Seq_IsAssignedPerTick_NotPerConnection_AndTheTwoHalvesOfAShotDiffer()
        {
            // task-28-brief §2.7: Task 29's dedup key is (epoch, tick, seq), so
            // one wire event must carry ONE seq to every connection and to every
            // future resend. A counter that lived in BuildFor instead of
            // BeginTick would restart per connection: two observers would report
            // the same seq for DIFFERENT events, and dedup would silently eat one
            // of them.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));

            // Two separate events, so "every seq is 0" cannot pass either.
            w.Emit(SimEventKind.WaveStarted, float2.zero, 0, default, 0f);
            w.Emit(SimEventKind.WaveCleared, float2.zero, 0, default, 0f);

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 2);
            asm.BeginTick(w);
            int bytesA = asm.BuildFor(0, 0, 0, Epoch);
            int bytesB = asm.BuildFor(1, 1, 1, Epoch);
            AssembledFrame a = AssembledFrame.Decode(asm.BufferFor(0), bytesA, cfg);
            AssembledFrame b = AssembledFrame.Decode(asm.BufferFor(1), bytesB, cfg);

            Assert.AreEqual(2, a.EventCount);
            Assert.AreEqual(2, b.EventCount);
            Assert.IsTrue(a.TryFirstOf(SnapshotEventKind.WaveStarted, out int aStart));
            Assert.IsTrue(b.TryFirstOf(SnapshotEventKind.WaveStarted, out int bStart));
            Assert.IsTrue(a.TryFirstOf(SnapshotEventKind.WaveCleared, out int aClear));
            Assert.AreEqual(a.Events[aStart].Seq, b.Events[bStart].Seq,
                "the SAME wire event must carry the SAME seq on every connection");
            Assert.AreNotEqual(a.Events[aStart].Seq, a.Events[aClear].Seq,
                "witness: two different events must carry DIFFERENT seq, or the assertion above is vacuous");

            // The two halves of one shot are two wire events and must not share
            // a seq — dedup (Task 29) would otherwise collapse them into one
            // and the observer who only ever HEARS the shot would lose it.
            // They can never appear in the same frame (ShotHeard is suppressed
            // for anyone who got the spawn), so the pairing has to be observed
            // across two connections: one on the round's path, one only in
            // earshot of its muzzle.
            var w2 = Trio(out SimConfig cfg2, float2.zero, new float2(40f, 55f), new float2(40f, 0f));
            float sight = cfg2.Visibility.SightRadius;
            var muzzle = new float2(40f, 0f);
            Assert.Less(math.distance(muzzle, float2.zero), sight,
                "fixture premise: connection 0 sits on the round's own trajectory");
            Assert.Greater(math.distance(muzzle, new float2(40f, 55f)), sight,
                "fixture premise: connection 1 is off the trajectory");
            Assert.LessOrEqual(math.distance(muzzle, new float2(40f, 55f)), cfg2.Visibility.HearRadius,
                "fixture premise: connection 1 is nonetheless in earshot of the muzzle");

            // Player 2 fires straight along +X (Amount is the shot's velocity
            // angle, SimulationWorld.SpawnProjectile's own convention).
            w2.Emit(SimEventKind.ProjectileFired, muzzle, 4919, default, 0f,
                ProjectileOwner.Player, playerIndex: 2);
            var asm2 = new SnapshotAssembler(cfg2, Net(), connectionCount: 2);
            asm2.BeginTick(w2);
            AssembledFrame onPath = AssembledFrame.Decode(asm2.BufferFor(0), asm2.BuildFor(0, 0, 0, Epoch), cfg2);
            AssembledFrame inEarshot = AssembledFrame.Decode(asm2.BufferFor(1), asm2.BuildFor(1, 1, 1, Epoch), cfg2);

            Assert.IsTrue(onPath.TryFirstOf(SnapshotEventKind.ProjectileSpawned, out int spawned),
                "the observer on the round's path gets the tracer");
            Assert.AreEqual(0, onPath.CountOf(SnapshotEventKind.ShotHeard),
                "and must NOT also get the audio half of the same shot — one shot, one record per observer");
            Assert.IsTrue(inEarshot.TryFirstOf(SnapshotEventKind.ShotHeard, out int heard),
                "the observer merely in earshot gets the report");
            Assert.AreEqual(0, inEarshot.CountOf(SnapshotEventKind.ProjectileSpawned),
                "witness: the off-path observer really did not qualify for the tracer");
            Assert.AreNotEqual(onPath.Events[spawned].Seq, inEarshot.Events[heard].Seq,
                "ProjectileSpawned and ShotHeard of the SAME shot are two wire events with two seq values — "
                + "sharing one would make Task 29's dedup treat them as duplicates of each other");
        }

        // ---- T28.A6. The carry queue (Р61) ----

        [Test]
        public void CarryQueue_EventThatDidNotFit_ArrivesNextFrame_WithItsOriginalTickAndSeq()
        {
            // Р61: what does not fit is CARRIED, not dropped. The tick-delta
            // grows because the frame it finally rides is later — the (tick,
            // seq) identity underneath does not move, which is what makes Task
            // 29's dedup key stable across a deferral.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));

            // Budget of one: the second event cannot ride this frame.
            var asm = new SnapshotAssembler(cfg, Net(eventBudget: 1), connectionCount: 1);
            w.Emit(SimEventKind.WaveStarted, float2.zero, 0, default, 0f);
            w.Emit(SimEventKind.WaveCleared, float2.zero, 0, default, 0f);
            int firstTick = w.CurrentTick;

            AssembledFrame f1 = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, f1.EventCount, "the budget of one admits exactly one event");
            Assert.AreEqual((byte)SnapshotEventKind.WaveStarted, f1.Events[0].Kind,
                "same rank, so the tie-break is the earlier seq — the first event emitted");
            Assert.IsFalse(f1.Truncated,
                "a DEFERRED event must not set the truncation bit: carrying is the budget's ordinary mode, "
                + "not a dropped entity (task-28-brief §2.8 item 6)");
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEvents,
                "a deferred event is not a dropped one — DroppedEvents must still be zero");

            // Two ticks pass with nothing new to say.
            var idle = new SimInput[3];
            w.TickAll(idle);
            w.ClearEvents();
            w.TickAll(idle);
            w.ClearEvents();
            Assert.AreEqual(firstTick + 2, w.CurrentTick, "test setup: exactly two ticks passed");

            AssembledFrame f2 = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, f2.EventCount, "the carried event rides the next frame that has room");
            Assert.AreEqual((byte)SnapshotEventKind.WaveCleared, f2.Events[0].Kind);
            Assert.AreEqual((byte)2, f2.Events[0].TickDelta,
                "tick-delta counts back from the FRAME's tick, so a two-tick-old event reads 2 — "
                + "this is how a deferral stays identifiable rather than looking like a fresh event");
            Assert.AreEqual((ushort)(f1.Events[0].Seq + 1), f2.Events[0].Seq,
                "the carried event keeps the seq it was assigned on its OWN tick — the two were numbered "
                + "consecutively there, and a deferral must not renumber either of them");
        }

        // ---- T28.A7. Queue overflow drops deterministically and counts ----

        [Test]
        public void CarryQueue_Overflow_EvictsTheWorstRank_AndCountsIt()
        {
            // The queue is bounded (2 * MaxEventsPerFrame). Beyond that
            // something must go, and WHICH must be decided rather than
            // whichever happened to arrive last: the worst rank first, then the
            // oldest, then the highest seq (Р61's own ordering, read backwards).
            var cfg = TestConfigs.Open();
            cfg.Arena.MaxEventsPerFrame = 2;   // queue capacity 4
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, 8f));

            var asm = new SnapshotAssembler(cfg, Net(eventBudget: 1), connectionCount: 1);

            // Five ticks of two cosmetic events each against a four-slot queue
            // and a budget of one: one rides per frame, the rest pile up and
            // eventually evict.
            var idle = new SimInput[3];
            for (int i = 0; i < 5; i++)
            {
                w.TickAll(idle);
                w.ClearEvents();
                w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
                w.Emit(SimEventKind.PlayerSlideStarted, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
                Build(asm, w, cfg, 0, 0, 0);
            }

            Assert.Greater(asm.StatsFor(0).DroppedEvents, 0,
                "an overflowing queue must COUNT what it evicts — a silently shorter queue is "
                + "indistinguishable from a delivery bug");

            // A death arriving into a queue full of cosmetics must survive:
            // eviction picks the WORST rank, and the newcomer is not it.
            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, f.EventCount);
            Assert.AreEqual((byte)SnapshotEventKind.PlayerDied, f.Events[0].Kind,
                "a death outranks every cosmetic already queued and must be the one that rides");
        }

        // ---- T28.A8/A9. The app-vk1 latch (Р133) ----

        [Test]
        public void AudibleLatch_OscillationInsideTheMargin_KeepsOneCell_AndAveragingDoesNotRefine()
        {
            // Р133, closed here. Coarsening ONE position onto a 3 m grid hides
            // it; coarsening a STREAM of them does not — N independent
            // roundings of a smooth path recover it to about grid/sqrt(12N),
            // which for a burst of shots is finer than the hero's own radius.
            // The fix is a per-connection, per-source LATCH with hysteresis:
            // while the true position stays within grid/2 + grid/4 of the
            // latched cell's centre, the latched cell is what ships, so the
            // independent roundings that the averaging attack needs never
            // happen.
            //
            // The second invariant is the point (урок 110): "every event
            // carried the same cell" alone would also be true of a broken
            // implementation that latched onto a WRONG cell, so the test also
            // asserts that the MEAN of what was delivered stays further from
            // the truth than grid/4 — i.e. that averaging bought the attacker
            // nothing.
            var cfg = TestConfigs.Open();
            float grid = cfg.Visibility.HearPositionGridMeters;
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            // Audible but never visible: past SightRadius + ExitHysteresis,
            // inside HearRadius.
            const float boundaryX = 49.5f;   // exactly on a 3 m cell boundary (1.5 + 3*16)
            Assert.Greater(boundaryX, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: the source must be out of sight");
            Assert.Less(boundaryX, cfg.Visibility.HearRadius, "fixture premise: the source must be in earshot");
            const float amplitude = 0.1f;
            Assert.Less(amplitude, grid * 0.75f, "fixture premise: the wobble must be inside the hysteresis margin");

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            const int samples = 8;
            float2 sum = float2.zero;
            float2 trueSum = float2.zero;
            float2 firstCell = float2.zero;
            for (int i = 0; i < samples; i++)
            {
                // Alternates across the cell boundary — without a latch this
                // is precisely the dither that recovers the true position.
                var truePos = new float2(boundaryX + ((i & 1) == 0 ? -amplitude : amplitude), 0f);
                TestWorlds.RelocatePlayerForTest(w, 1, truePos);
                w.ClearEvents();
                w.Emit(SimEventKind.PlayerDashed, truePos, 0, default, 0f, playerIndex: 1);

                AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);
                Assert.AreEqual(1, f.EventCount, $"sample {i}: the dash must be delivered by hearing");
                if (i == 0) firstCell = f.Events[0].Pos;
                Assert.That(math.distance(f.Events[0].Pos, firstCell), Is.LessThan(0.01f),
                    $"sample {i}: every event of a source wobbling inside the margin must carry the SAME cell");
                sum += f.Events[0].Pos;
                trueSum += truePos;
            }

            float2 deliveredMean = sum / samples;
            float2 trueMean = trueSum / samples;
            Assert.Greater(math.distance(deliveredMean, trueMean), grid * 0.25f,
                "second invariant (урок 110): averaging the delivered stream must NOT converge on the truth — "
                + "that convergence is the whole attack Р133 describes");
        }

        [Test]
        public void AudibleLatch_Stationary_MovedFar_AndBecomingVisible()
        {
            var cfg = TestConfigs.Open();
            float grid = cfg.Visibility.HearPositionGridMeters;
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);

            float2 Dash(float2 pos)
            {
                TestWorlds.RelocatePlayerForTest(w, 1, pos);
                w.ClearEvents();
                w.Emit(SimEventKind.PlayerDashed, pos, 0, default, 0f, playerIndex: 1);
                AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);
                Assert.AreEqual(1, f.EventCount, "the dash must be delivered");
                return f.Events[0].Pos;
            }

            // (a) stationary out of sight: one cell, repeatedly.
            var restPos = new float2(49.4f, 0f);
            float2 cellA = Dash(restPos);
            Assert.That(math.distance(Dash(restPos), cellA), Is.LessThan(0.01f),
                "a stationary invisible source must not jitter between cells");
            Assert.That(math.distance(cellA, VisibilitySystem.QuantizeAudiblePos(restPos, cfg.Visibility)),
                Is.LessThan(0.01f), "the first latch is simply the plain coarsening of the first position");

            // (b) moved well past the margin: the latch follows.
            var farPos = new float2(53f, 0f);
            Assert.Greater(math.abs(farPos.x - cellA.x), grid * 0.75f,
                "fixture premise: this move must exceed the hysteresis margin");
            float2 cellB = Dash(farPos);
            Assert.That(math.distance(cellB, VisibilitySystem.QuantizeAudiblePos(farPos, cfg.Visibility)),
                Is.LessThan(0.01f), "a source that genuinely moved must get a fresh cell — the latch is "
                + "hysteresis, not a freeze");
            Assert.Greater(math.distance(cellB, cellA), 0.01f, "witness: the two cells really do differ");

            // (c) becoming visible: exact position, and the latch is dropped —
            // so a later return to hearing re-latches on where the source
            // actually IS, not on a stale cell from before it was seen.
            var seenPos = new float2(10f, 0f);
            float2 exact = Dash(seenPos);
            Assert.That(math.distance(exact, seenPos), Is.LessThan(0.01f),
                "a VISIBLE source's event carries its exact position — its body is already replicated "
                + "at full precision, so coarsening the event would protect nothing");

            // Р19's linger keeps a just-seen entity in the set for LingerTicks
            // more ticks, and while it is there it still counts as visible
            // (Р132) — so the fixture waits the window out before asking about
            // the coarse branch again.
            //
            // `backPos` is chosen so the two answers actually differ: it sits
            // INSIDE the hysteresis margin of the cell held before the source
            // became visible (cellB), so an implementation that kept its latch
            // across the visible spell would answer cellB, while a correctly
            // cleared one re-latches on backPos's own cell. Picking a position
            // outside that margin would have both implementations re-latch and
            // the test would prove nothing.
            var backPos = new float2(52.4f, 0f);
            Assert.Less(math.abs(backPos.x - cellB.x), grid * 0.75f,
                "fixture premise: backPos must sit INSIDE the stale latch's own hysteresis margin, "
                + "or a kept latch and a cleared one would answer alike");
            Assert.Greater(math.distance(VisibilitySystem.QuantizeAudiblePos(backPos, cfg.Visibility), cellB),
                0.01f, "fixture premise: backPos's own cell must differ from the stale one");

            float2 cellC = float2.zero;
            for (int i = 0; i <= cfg.Visibility.LingerTicks + 1; i++) cellC = Dash(backPos);
            Assert.That(math.distance(cellC, VisibilitySystem.QuantizeAudiblePos(backPos, cfg.Visibility)),
                Is.LessThan(0.01f),
                "after being seen, the source re-latches from scratch — a latch kept across visibility "
                + "would answer with the cell it held before");
            Assert.Greater(math.distance(cellC, cellB), 0.01f,
                "witness: the stale cell and the fresh one really do differ, so the assertion above discriminates");
        }

        // ---- T28.A10. Assembly is deterministic ----

        [Test]
        public void TwoConnectionsInTheSameState_ProduceByteIdenticalFrames()
        {
            // The foundation every later debugging session rests on (§2.13):
            // same world, same connection state, same bytes. Without it a
            // Task 32 mismatch could never be told apart from an assembly that
            // is simply not reproducible.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            TestWorlds.SpawnMobsAt(w,
                (MobType.Chaser, new float2(4f, 4f)), (MobType.Gunner, new float2(-5f, 1f)),
                (MobType.Chaser, new float2(2f, -7f)));
            w.Emit(SimEventKind.WaveStarted, float2.zero, 0, default, 0f);
            w.Emit(SimEventKind.MobSpawned, new float2(4f, 4f), w.Mobs[0].Id, MobType.Chaser, 0f);

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 2);
            asm.BeginTick(w);
            int a = asm.BuildFor(0, 0, 0, Epoch);
            int b = asm.BuildFor(1, 0, 0, Epoch);

            Assert.AreEqual(a, b, "two identically-configured connections must write the same number of bytes");
            Assert.Greater(a, SnapshotWriter.HeaderBytes, "fixture premise: the frame must not be empty");
            byte[] bufA = asm.BufferFor(0);
            byte[] bufB = asm.BufferFor(1);
            for (int i = 0; i < a; i++)
                Assert.AreEqual(bufA[i], bufB[i], $"byte {i} must match — assembly is a pure function of its inputs");
        }

        // ---- T28.A11. Zero allocations in steady state ----

        [Test]
        public void BeginTickAndBuildFor_DoNotAllocateGCMemory()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            TestWorlds.SpawnMobsToCap(w);
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 3);

            void EmitFixture()
            {
                w.ClearEvents();
                w.Emit(SimEventKind.WaveStarted, float2.zero, 0, default, 0f);
                w.Emit(SimEventKind.MobSpawned, new float2(4f, 4f), w.Mobs[0].Id, MobType.Chaser, 0f);
                w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            }

            // Warm-up OUTSIDE the measured lambda, with the fixture premise
            // that defeats a do-nothing implementation (Task 26 finding F-D):
            // the measured body must really assemble a populated frame.
            EmitFixture();
            asm.BeginTick(w);
            for (int c = 0; c < 3; c++)
            {
                int bytes = asm.BuildFor(c, c, c, Epoch);
                AssembledFrame f = AssembledFrame.Decode(asm.BufferFor(c), bytes, cfg);
                Assert.Greater(f.MobCount, 0, "fixture premise (stub-defeating): mobs must actually ride");
                Assert.Greater(f.EventCount, 0, "fixture premise (stub-defeating): events must actually ride");
                Assert.AreEqual(2, f.PlayerCount, "fixture premise (stub-defeating): both other players must ride");
            }

            Assert.That(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    asm.BeginTick(w);
                    for (int c = 0; c < 3; c++) asm.BuildFor(c, c, c, Epoch);
                }
            }, Is.Not.AllocatingGCMemory());
        }

        // ---- T28.A12. Player flag bits, one source field each ----

        [Test]
        public void PlayerFlags_EachBitComesFromItsOwnSourceField()
        {
            // task-28-brief §2.11, the producer half of the mapping Task 45
            // reads (Р68). Every bit is set from a DIFFERENT PlayerState field
            // and asserted alone, because the plausible defect here is a
            // near-miss (`DashCooldown > 0` for `Dashing`, `AimHeld` read off
            // an input the assembler does not have) rather than a wholesale
            // omission.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);

            byte FlagsOfPlayerOne()
            {
                AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);
                Assert.IsTrue(f.TryPlayer(1, out SnapshotBlocks.PlayerRecord r), "player 1 must be in the frame");
                return r.Flags;
            }

            // Baseline: alive, nothing else.
            PlayerState clean = w.PlayerAt(1);
            clean.DashTimer = 0f; clean.DashCooldown = 0f; clean.SlideTimer = 0f;
            clean.AimSettleTimer = 0f; clean.LinkWindowTimer = 0f;
            w.SetPlayerForTest(1, clean);
            Assert.AreEqual(PlayerWireFlags.Alive, FlagsOfPlayerOne(),
                "a plain living player carries the Alive bit and nothing else");

            // Dashing comes from DashTimer, never from DashCooldown — the two
            // are adjacent fields with adjacent meanings and a live cooldown is
            // the ORDINARY state of a player who is NOT dashing.
            PlayerState cooling = clean;
            cooling.DashCooldown = 1f;
            w.SetPlayerForTest(1, cooling);
            Assert.AreEqual(0, FlagsOfPlayerOne() & PlayerWireFlags.Dashing,
                "a dash COOLDOWN is not a dash — the bit must come from DashTimer");
            PlayerState dashing = clean;
            dashing.DashTimer = 0.1f;
            w.SetPlayerForTest(1, dashing);
            Assert.AreNotEqual(0, FlagsOfPlayerOne() & PlayerWireFlags.Dashing);

            PlayerState sliding = clean;
            sliding.SlideTimer = 0.2f;
            w.SetPlayerForTest(1, sliding);
            Assert.AreEqual(PlayerWireFlags.Alive | PlayerWireFlags.Sliding, FlagsOfPlayerOne());

            PlayerState aiming = clean;
            aiming.AimSettleTimer = 0.05f;
            w.SetPlayerForTest(1, aiming);
            Assert.AreEqual(PlayerWireFlags.Alive | PlayerWireFlags.AimHeld, FlagsOfPlayerOne(),
                "AimHeld is proxied by AimSettleTimer — the input flag itself never reaches the server-side "
                + "assembler, and the timer's own decay tail is cosmetically right");

            PlayerState linking = clean;
            linking.LinkWindowTimer = 0.1f;
            w.SetPlayerForTest(1, linking);
            Assert.AreEqual(PlayerWireFlags.Alive | PlayerWireFlags.LinkWindow, FlagsOfPlayerOne());

            w.KillPlayerNoDamage(1);
            Assert.AreEqual(0, FlagsOfPlayerOne() & PlayerWireFlags.Alive,
                "and Alive itself comes from PlayerState.Alive");
        }

        // ---- T28.A13. The app-dsh subscription patch ----

        [Test]
        public void SpawnSubscription_ExpiresOnItsOwn_WhenNoEndEventEverArrives()
        {
            // bd app-dsh: a round absorbed by a player's i-frames emits NOTHING
            // at all today (ProjectileSystem's HitPlayer branch), so a
            // subscription opened on its spawn would never be closed and the
            // per-connection set would fill up over a match. Task 44 decides
            // what that branch emits (owner decision Р128); until then every
            // subscription carries its own expiry — spawn tick plus the longest
            // a round can live plus the redundancy window — and is swept
            // lazily. Task 44 either removes this patch or confirms it.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));

            const int roundId = 4919;
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);

            // A round spawns right next to the observer, so its trajectory is
            // plainly relevant and the subscription really opens.
            w.Emit(SimEventKind.ProjectileFired, new float2(1f, 0f), roundId, default, 0f,
                ProjectileOwner.Player, playerIndex: 1);
            AssembledFrame spawn = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, spawn.CountOf(SnapshotEventKind.ProjectileSpawned),
                "test setup: the spawn must have been delivered, or there is no subscription to expire");

            int maxLifeTicks = (int)math.ceil(
                math.max(cfg.Weapon.ProjectileLifetime, cfg.Gunner.ProjectileLifetime) / SimulationWorld.TickDt);

            // Just inside the window: the end still finds its subscriber.
            var idle = new SimInput[2];
            for (int i = 0; i < 2; i++) { w.TickAll(idle); w.ClearEvents(); }
            w.Emit(SimEventKind.ProjectileExpired, new float2(30f, 0f), roundId, default, 0f);
            AssembledFrame inWindow = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, inWindow.CountOf(SnapshotEventKind.ProjectileEnded),
                "witness: inside the window the subscription is live and the ending is delivered");

            // A second round, this time abandoned: no ending is ever emitted,
            // and after the expiry window a LATE ending must find nobody.
            const int abandonedId = 4920;
            w.ClearEvents();
            w.Emit(SimEventKind.ProjectileFired, new float2(1f, 0f), abandonedId, default, 0f,
                ProjectileOwner.Player, playerIndex: 1);
            Assert.AreEqual(1, Build(asm, w, cfg, 0, 0, 0).CountOf(SnapshotEventKind.ProjectileSpawned),
                "test setup: the second spawn must be delivered too");

            for (int i = 0; i < maxLifeTicks + cfg.Arena.MaxPlayers + 30; i++) { w.TickAll(idle); w.ClearEvents(); }
            w.Emit(SimEventKind.ProjectileExpired, new float2(30f, 0f), abandonedId, default, 0f);
            AssembledFrame late = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(0, late.CountOf(SnapshotEventKind.ProjectileEnded),
                "past its expiry the subscription is gone — otherwise a round the simulation silently "
                + "swallowed (app-dsh) would hold a slot for the rest of the match");
        }

        // ---- T28.A14. The first tick of a match ----

        [Test]
        public void FirstTick_EmptyPreviousSet_AssemblesCleanly_AndHoldsBackMobDied()
        {
            // §2.1's recorded limit. MobDied is routed against the PREVIOUS
            // tick's set (a corpse is swap-removed the same tick it dies, so
            // the current set can never hold it) — and on the very first frame
            // of a connection there is no previous tick. The event is therefore
            // not delivered, which is right rather than merely tolerable: the
            // world was born this instant and nobody was there to watch.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            w.Emit(SimEventKind.MobDied, new float2(5f, 0f), mobId, MobType.Chaser, 1f,
                zone: HitZone.Body, playerIndex: 0);

            AssembledFrame first = null;
            Assert.DoesNotThrow(() => first = Build(asm, w, cfg, 0, 0, 0),
                "an empty previous-tick set must not make the very first assembly throw");
            Assert.AreEqual(0, first.CountOf(SnapshotEventKind.MobDied),
                "on the very first frame the previous-tick set is empty, so a death has no witness");

            // Witness on the next frame: now there IS a previous tick, and the
            // same death is delivered — so the assertion above is about the
            // first frame, not about MobDied never working.
            w.ClearEvents();
            w.Emit(SimEventKind.MobDied, new float2(5f, 0f), mobId, MobType.Chaser, 1f,
                zone: HitZone.Body, playerIndex: 0);
            AssembledFrame second = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, second.CountOf(SnapshotEventKind.MobDied),
                "witness: with a previous tick in hand the very same death IS delivered");
        }

        // ---- T28.A15. The tick-delta byte cannot silently wrap ----

        [Test]
        public void EventOlderThan255Ticks_IsDroppedWithACounter_NotWrappedIntoTheByte()
        {
            // TickDelta is one byte (Task 27's record layout). An event that
            // waited longer than that cannot be described at all, and writing
            // `(byte)delta` would place it 256 ticks in the client's future —
            // far worse than not sending it. It is dropped, and counted.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));

            // Budget zero is not configurable (Range starts at 1), so the event
            // is held back by a full queue instead: one slot, and a death that
            // keeps outranking it.
            var asm = new SnapshotAssembler(cfg, Net(eventBudget: 1), connectionCount: 1);
            w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            Build(asm, w, cfg, 0, 0, 0);   // the death rides, the dash waits

            var idle = new SimInput[2];
            for (int i = 0; i < 260; i++) { w.TickAll(idle); w.ClearEvents(); }

            int droppedBefore = asm.StatsFor(0).DroppedEvents;
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(0, f.EventCount,
                "an event older than the tick-delta byte can hold must not ride at all");
            Assert.Greater(asm.StatsFor(0).DroppedEvents, droppedBefore,
                "and it must be counted as dropped, not vanish silently");
        }

        // ---- T28 fix-round 1. Review findings F1 and F3 ----

        [Test]
        public void QueueOverflow_DroppedSpawn_LeavesNoSubscription_AndDoesNotSuppressShotHeard()
        {
            // Review finding F1. Enqueue used to be void, so the
            // ProjectileSpawned branch subscribed unconditionally after it —
            // and when a full queue refused the spawn itself as its worst
            // newcomer, the connection stayed subscribed to a round whose
            // tracer would never arrive: the round's ending (and the MobDied
            // union arm) would then reach a peer with no spawn context, which
            // is exactly what "to whoever received the spawn" rules out. The
            // same broken flag also suppressed the shot's ShotHeard fallback,
            // silencing BOTH halves of the shot.
            var cfg = TestConfigs.Open();
            cfg.Arena.MaxEventsPerFrame = 2;   // queue capacity 4
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            var asm = new SnapshotAssembler(cfg, Net(eventBudget: 1), connectionCount: 1);
            var idle = new SimInput[2];

            // Three ticks of two deaths each against a budget of one leave the
            // queue holding three rank-0 records...
            for (int i = 0; i < 3; i++)
            {
                w.TickAll(idle);
                w.ClearEvents();
                w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                    zone: HitZone.Body, playerIndex: 1);
                w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                    zone: HitZone.Body, playerIndex: 1);
                Build(asm, w, cfg, 0, 0, 0);
            }
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEvents,
                "fixture premise: nothing may have been dropped before the shot fires");

            // ...so one more death fills it to four, and the shot that follows
            // finds no room: its spawn (rank 2) and its ShotHeard (rank 3) are
            // both the worst newcomers against a queue of deaths. The exact
            // count is the point: TWO drops means the ShotHeard was genuinely
            // ATTEMPTED after the spawn was refused — the pre-fix flag
            // suppressed it and would leave this at one.
            const int roundId = 7001;
            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            w.Emit(SimEventKind.ProjectileFired, new float2(6f, 0f), roundId, default, 0f,
                ProjectileOwner.Player, playerIndex: 1);
            Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(2, asm.StatsFor(0).DroppedEvents,
                "the refused spawn AND its still-attempted ShotHeard must both be counted — "
                + "one drop would mean the dead flag still mutes the sound half");

            // The round ends next tick. A connection whose spawn never shipped
            // must not receive the ending — the subscription may only open for
            // a spawn the queue actually accepted.
            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.ProjectileBlocked, new float2(5.3f, 1.15f), roundId, default, 0.73f,
                hitDir: new float2(-1f, 0f));
            Build(asm, w, cfg, 0, 0, 0);

            // Drain everything left and account for every event that ever rode:
            // seven deaths went in, seven deaths must come out, and nothing of
            // the dropped shot — no spawn, no sound, no ending.
            int deathsDelivered = 0;
            for (int i = 0; i < 12; i++)
            {
                w.TickAll(idle);
                w.ClearEvents();
                AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);
                deathsDelivered += f.CountOf(SnapshotEventKind.PlayerDied);
                Assert.AreEqual(0, f.CountOf(SnapshotEventKind.ProjectileSpawned),
                    "the refused spawn must never ride");
                Assert.AreEqual(0, f.CountOf(SnapshotEventKind.ShotHeard),
                    "the shot's sound was refused by the same full queue and must never ride");
                Assert.AreEqual(0, f.CountOf(SnapshotEventKind.ProjectileEnded),
                    "an ending delivered to a peer that never saw the spawn means a "
                    + "subscription was opened for a dropped spawn (finding F1)");
            }
            // Accounting, so the kind assertions above scanned real frames and
            // not a drained-empty queue: seven deaths were emitted, the five
            // builds before this loop delivered one each (budget 1), so the
            // loop itself must deliver exactly the remaining two.
            Assert.AreEqual(2, deathsDelivered,
                "seven deaths emitted minus five delivered by the five earlier builds — "
                + "the drain loop must account for exactly the remaining two");
        }

        [Test]
        public void RicochetContactPoint_DoesNotPoisonTheActorsAudibleLatch()
        {
            // Review finding F3. DashRicocheted's Pos is the wall CONTACT
            // point (SimEvents.cs's own doc), not the actor's position — but
            // the audible latch is keyed by ACTOR. Routing the contact through
            // the actor's key delivered the actor's stale latched cell for a
            // point on a wall whenever the two happened to fall within the
            // hysteresis margin of each other. The contact must coarsen on its
            // own (plain QuantizeAudiblePos), and the actor's own latch must
            // survive it untouched.
            var cfg = TestConfigs.Open();
            float grid = cfg.Visibility.HearPositionGridMeters;
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            var actorPos = new float2(49.6f, 0f);
            TestWorlds.RelocatePlayerForTest(w, 1, actorPos);
            Assert.Greater(actorPos.x, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: the actor must be out of sight");
            Assert.Less(actorPos.x, cfg.Visibility.HearRadius,
                "fixture premise: the actor must be in earshot");

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            var idle = new SimInput[2];
            float2 actorCell = VisibilitySystem.QuantizeAudiblePos(actorPos, cfg.Visibility);

            // Tick 1: a dash latches the actor's cell.
            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDashed, actorPos, 0, default, 0f, playerIndex: 1);
            AssembledFrame f1 = Build(asm, w, cfg, 0, 0, 0);
            Assert.IsTrue(f1.TryFirstOf(SnapshotEventKind.PlayerDashed, out int d1));
            Assert.Less(math.distance(f1.Events[d1].Pos, actorCell), 0.01f,
                "fixture premise: the dash must have latched the actor's own cell");

            // Tick 2: the dash mirrors off a wall. The contact point rounds to
            // a DIFFERENT cell than the actor's, but sits INSIDE the latch's
            // hysteresis margin of the actor's latched centre — the exact
            // geometry in which the actor-keyed latch used to answer with the
            // actor's stale cell instead of the contact's own.
            var contact = new float2(52.6f, 0.37f);
            float2 contactCell = VisibilitySystem.QuantizeAudiblePos(contact, cfg.Visibility);
            Assert.Greater(math.distance(contactCell, actorCell), 0.1f,
                "fixture premise: the contact must round to a different cell");
            Assert.Less(math.distance(contact, actorCell), grid * 0.75f,
                "fixture premise: the contact must sit inside the latch margin of the actor's cell");
            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.DashRicocheted, contact, 0, default, 0f,
                hitDir: new float2(-1f, 0f), playerIndex: 1);
            AssembledFrame f2 = Build(asm, w, cfg, 0, 0, 0);
            Assert.IsTrue(f2.TryFirstOf(SnapshotEventKind.DashRicocheted, out int r2));
            Assert.Less(math.distance(f2.Events[r2].Pos, contactCell), 0.01f,
                "the contact point must coarsen on its own — the actor's stale cell "
                + "answering for a point on a wall is finding F3");

            // Tick 3: the next dash still reads the actor's original cell —
            // the contact never touched the actor's latch.
            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDashed, new float2(49.8f, 0f), 0, default, 0f, playerIndex: 1);
            AssembledFrame f3 = Build(asm, w, cfg, 0, 0, 0);
            Assert.IsTrue(f3.TryFirstOf(SnapshotEventKind.PlayerDashed, out int d3));
            Assert.Less(math.distance(f3.Events[d3].Pos, actorCell), 0.01f,
                "the actor's own latch must survive the ricochet untouched");
        }
    }
}
