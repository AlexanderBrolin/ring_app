using System.Collections.Generic;
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
            // TASK 29 AMENDED THE IDENTIFICATION, NOT THE CLAIM. This frame now
            // also carries the REDUNDANT resend of the first round's spawn
            // (Р58), so the record is picked out by its own round id instead of
            // by counting the kind — which is what the assertion always meant.
            AssembledFrame secondSpawn = Build(asm, w, cfg, 0, 0, 0);
            int abandonedSpawns = 0;
            for (int i = 0; i < secondSpawn.EventCount; i++)
                if ((SnapshotEventKind)secondSpawn.Events[i].Kind == SnapshotEventKind.ProjectileSpawned
                    && secondSpawn.Payloads[i].Id == abandonedId) abandonedSpawns++;
            Assert.AreEqual(1, abandonedSpawns,
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

            // TASK 29 AMENDED THE ACCOUNTING, NOT THE CLAIM. Once every frame
            // repeats the last few ticks' events (Р58), counting death RECORDS
            // says nothing — a frame can carry one fresh death and a resend of
            // another. What "a death was delivered" means is a distinct
            // (original tick, seq), which is Task 29's own dedup key, and it is
            // collected from EVERY frame this fixture builds rather than from
            // the drain loop alone: a resend of a death delivered before the
            // loop lands inside it. The arithmetic below is unchanged — seven
            // deaths in, seven distinct deaths out, five of them before the
            // loop.
            var deathsDelivered = new HashSet<(uint Tick, ushort Seq)>();

            void CollectDeaths(AssembledFrame frame)
            {
                for (int e = 0; e < frame.EventCount; e++)
                    if ((SnapshotEventKind)frame.Events[e].Kind == SnapshotEventKind.PlayerDied)
                        deathsDelivered.Add((frame.Tick - frame.Events[e].TickDelta, frame.Events[e].Seq));
            }

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
                CollectDeaths(Build(asm, w, cfg, 0, 0, 0));
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
            CollectDeaths(Build(asm, w, cfg, 0, 0, 0));
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
            CollectDeaths(Build(asm, w, cfg, 0, 0, 0));
            Assert.AreEqual(5, deathsDelivered.Count,
                "five builds against a budget of one have delivered five distinct deaths so far");

            // Drain everything left and account for every event that ever rode:
            // seven deaths went in, seven deaths must come out, and nothing of
            // the dropped shot — no spawn, no sound, no ending.
            for (int i = 0; i < 12; i++)
            {
                w.TickAll(idle);
                w.ClearEvents();
                AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);
                CollectDeaths(f);
                Assert.AreEqual(0, f.CountOf(SnapshotEventKind.ProjectileSpawned),
                    "the refused spawn must never ride");
                Assert.AreEqual(0, f.CountOf(SnapshotEventKind.ShotHeard),
                    "the shot's sound was refused by the same full queue and must never ride");
                Assert.AreEqual(0, f.CountOf(SnapshotEventKind.ProjectileEnded),
                    "an ending delivered to a peer that never saw the spawn means a "
                    + "subscription was opened for a dropped spawn (finding F1)");
            }
            // Accounting, so the kind assertions above scanned real frames and
            // not a drained-empty queue: seven deaths were emitted, five had
            // been delivered by the five builds before this loop (budget 1), so
            // the loop itself must have delivered exactly the remaining two.
            Assert.AreEqual(7, deathsDelivered.Count,
                "seven deaths emitted, seven distinct deaths delivered — the drain loop accounts for "
                + "the two that had not been delivered before it");
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

        // ================= Stage 2 Task 29 — redundancy (server half) =======

        static readonly byte[] EveryBlockKind =
        {
            (byte)SnapshotBlockKind.Players, (byte)SnapshotBlockKind.Liveness,
            (byte)SnapshotBlockKind.Mobs, (byte)SnapshotBlockKind.Wave, (byte)SnapshotBlockKind.Events,
        };

        /// The RAW bytes of a built frame's Events block. Task 29's resend
        /// contract is a BYTE contract (task-29-brief §2.4) — the record that
        /// rides again must be the one that rode, not a re-derivation that
        /// happens to decode to a similar float — and a decoded `float2` is one
        /// lossy step away from the two u16 codes actually on the wire.
        static byte[] EventsBlockBytesOf(byte[] buffer, int bytes)
        {
            var reader = new SnapshotReader(new System.ReadOnlySpan<byte>(buffer, 0, bytes));
            Assert.IsTrue(reader.TryReadHeader(out _, out _, out _));
            byte[] found = null;
            while (reader.TryReadBlock(EveryBlockKind, out byte kind, out System.ReadOnlySpan<byte> payload))
                if ((SnapshotBlockKind)kind == SnapshotBlockKind.Events) found = payload.ToArray();
            Assert.IsFalse(reader.Failed, "an assembled frame must parse cleanly to its end");
            Assert.IsNotNull(found, "every assembled frame carries an Events block, empty or not");
            return found;
        }

        static AssembledFrame BuildIdle(SnapshotAssembler asm, SimulationWorld w, in SimConfig cfg,
            SimInput[] idle)
        {
            w.TickAll(idle);
            w.ClearEvents();
            return Build(asm, w, cfg, 0, 0, 0);
        }

        // ---- T29.A1. A resend is the delivered record, byte for byte ----

        [Test]
        public void Resend_IsByteIdentical_ToTheDeliveredRecord_EvenAfterTheSourceMoved()
        {
            // task-29-brief §2.1/§2.4, and the regression on the hole that
            // re-routing a resend would open. A resend is a FROZEN copy of what
            // shipped: kind, seq, position and payload are copied at delivery
            // and never recomputed; only `tickDelta` is alive. Re-deriving the
            // position would hand a stream of resends the FRESH cell of a source
            // that has since moved — and a stream of independent cells is
            // precisely the ESP-grade leak the app-vk1 latch closes (Р133).
            var cfg = TestConfigs.Open();
            float grid = cfg.Visibility.HearPositionGridMeters;
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            // Out of sight (past SightRadius + ExitHysteresis), inside earshot:
            // the only rail on which a delivered position is coarsened at all.
            var srcA = new float2(49.4f, 0f);
            var srcB = new float2(55f, 0f);
            Assert.Greater(srcA.x, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: the source must be out of sight when it fires the event");
            Assert.Greater(srcB.x, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: and still out of sight after it moves");
            Assert.Less(srcB.x, cfg.Visibility.HearRadius, "fixture premise: and still in earshot");
            // THE MOVE MUST EXCEED THE LATCH MARGIN, or a re-deriving
            // implementation would answer with the very same latched cell and
            // this test would discriminate nothing (урок 109). The brief's
            // wording says "inside the margin"; inside it the latch is a
            // no-change by construction, so the discriminating fixture is the
            // one that would genuinely re-latch. Recorded in the report.
            Assert.Greater(math.abs(srcB.x - srcA.x), grid * 0.75f,
                "fixture premise: the move must exceed the hysteresis margin, so a recomputed position "
                + "would land on a DIFFERENT cell — otherwise the byte comparison below proves nothing");
            Assert.Greater(math.distance(
                    VisibilitySystem.QuantizeAudiblePos(srcB, cfg.Visibility),
                    VisibilitySystem.QuantizeAudiblePos(srcA, cfg.Visibility)), 0.1f,
                "fixture premise: and the two cells really do differ");

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            var idle = new SimInput[2];

            TestWorlds.RelocatePlayerForTest(w, 1, srcA);
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDashed, srcA, 0, default, 0f, playerIndex: 1);
            asm.BeginTick(w);
            int bytesD = asm.BuildFor(0, 0, 0, Epoch);
            byte[] blockD = EventsBlockBytesOf(asm.BufferFor(0), bytesD);
            Assert.AreEqual(SnapshotBlocks.EventHeaderBytes
                + SnapshotEvents.PayloadBytesFor(SnapshotEventKind.PlayerDashed), blockD.Length,
                "fixture premise: frame D carries exactly one record");

            // Frame D+1: the source has moved, and nothing new is emitted, so
            // the only record in the frame is the resend.
            w.TickAll(idle);
            w.ClearEvents();
            TestWorlds.RelocatePlayerForTest(w, 1, srcB);
            asm.BeginTick(w);
            int bytesD1 = asm.BuildFor(0, 0, 0, Epoch);
            byte[] blockD1 = EventsBlockBytesOf(asm.BufferFor(0), bytesD1);

            Assert.AreEqual(blockD.Length, blockD1.Length,
                "the resend is the same record, so it occupies the same number of bytes");
            for (int i = 0; i < blockD.Length; i++)
            {
                if (i == 3)
                {
                    // Byte 3 is `tickDelta` (Task 27's record layout: kind(1),
                    // seq(2), tickDelta(1), posX(2), posY(2), payloadBytes(1)).
                    Assert.AreEqual(blockD[3] + 1, blockD1[3],
                        "the ONLY live field of a resend is the tick delta, one tick larger");
                    continue;
                }
                Assert.AreEqual(blockD[i], blockD1[i],
                    $"byte {i} of the resend must be the byte that was delivered — position included "
                    + "(task-29-brief §2.4: re-deriving it would leak the source's fresh cell)");
            }
        }

        // ---- T29.A2. Resends and fresh events share ONE budget (Р61) ----

        [Test]
        public void Resends_ShareTheEventBudget_FreshFirst_ThenByRank()
        {
            // Р61 says the cap covers "including redundant resends", so a resend
            // spends the same budget and the same bytes a fresh event does. The
            // ORDER is fresh first: a fresh event has been transmitted zero
            // times and has zero probability of having arrived, while any resend
            // is already at ~95% after one 5%-loss hop.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            var asm = new SnapshotAssembler(cfg, Net(eventBudget: 4), connectionCount: 1);
            var idle = new SimInput[3];

            // Frame D: three events, all inside a budget of four.
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            w.Emit(SimEventKind.PlayerDamaged, new float2(6f, 0f), 1, default, 12f,
                zone: HitZone.Body, hitDir: new float2(1f, 0f), playerIndex: 1);
            w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            AssembledFrame d = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(3, d.EventCount, "fixture premise: all three ride in their own frame");

            // Frame D+1: two fresh events, so exactly two of the three possible
            // resends fit — and they are the two best-ranked ones.
            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.WaveStarted, float2.zero, 0, default, 0f);
            w.Emit(SimEventKind.PlayerSlideStarted, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            AssembledFrame d1 = Build(asm, w, cfg, 0, 0, 0);

            Assert.AreEqual(4, d1.EventCount,
                "the budget is ONE cap over fresh and resent alike (Р61) — four, not two plus three");
            Assert.AreEqual((byte)0, d1.Events[0].TickDelta, "fresh events are written first...");
            Assert.AreEqual((byte)0, d1.Events[1].TickDelta, "...both of them");
            Assert.AreEqual((byte)SnapshotEventKind.WaveStarted, d1.Events[0].Kind,
                "and among the fresh ones, by rank: a state change before a cosmetic");
            Assert.AreEqual((byte)SnapshotEventKind.PlayerSlideStarted, d1.Events[1].Kind);
            Assert.AreEqual((byte)1, d1.Events[2].TickDelta, "then the resends, one tick old");
            Assert.AreEqual((byte)1, d1.Events[3].TickDelta);
            Assert.AreEqual((byte)SnapshotEventKind.PlayerDied, d1.Events[2].Kind,
                "and among the resends, by rank too — a death is repeated before a cosmetic is");
            Assert.AreEqual((byte)SnapshotEventKind.PlayerDamaged, d1.Events[3].Kind);
            Assert.AreEqual(0, d1.CountOf(SnapshotEventKind.PlayerDashed),
                "the worst-ranked resend did not fit and must NOT have been packed past the cap");

            // Frame D+2: nothing fresh, so the resend that missed its turn gets
            // one — it stayed in the history rather than being dropped for not
            // fitting once (task-29-brief §2.3).
            AssembledFrame d2 = BuildIdle(asm, w, cfg, idle);
            Assert.AreEqual(4, d2.EventCount, "a full budget of resends");
            Assert.AreEqual(1, d2.CountOf(SnapshotEventKind.PlayerDashed),
                "the resend that lost a place to the budget stays in the history and tries again");
            Assert.AreEqual(0, d2.CountOf(SnapshotEventKind.PlayerSlideStarted),
                "and it outranks the younger cosmetic on the older-tick tie-break, which is the same "
                + "ordering key the carry queue uses");
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEvents,
                "nothing here is a DROP — a resend that does not fit is a degree of redundancy, "
                + "not a lost event (task-29-brief §2.3)");
        }

        // ---- T29.A3. The horizon, at both boundaries ----

        [Test]
        public void ResendHorizon_PresentUntilRedundancyTicksMinusOne_ThenGone()
        {
            // "Every snapshot repeats the events of the last EventRedundancyTicks
            // ticks" (Р58) = one first delivery plus N-1 resends, so an event
            // delivered in frame D is present in D+1 .. D+N-1 and gone in D+N.
            // BOTH boundaries are asserted: an off-by-one in either direction is
            // silent, and one of them doubles every death on the client.
            const int redundancy = 4;
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            var asm = new SnapshotAssembler(cfg, Net(redundancyTicks: redundancy), connectionCount: 1);
            var idle = new SimInput[3];

            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            AssembledFrame d = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, d.EventCount,
                "in its OWN frame the event rides exactly once — a history filled before the resends "
                + "are picked would send it twice in one frame");
            Assert.AreEqual((byte)0, d.Events[0].TickDelta);

            for (int age = 1; age <= redundancy - 1; age++)
            {
                AssembledFrame f = BuildIdle(asm, w, cfg, idle);
                Assert.AreEqual(1, f.EventCount, $"frame D+{age} must still carry the resend");
                Assert.AreEqual((byte)SnapshotEventKind.PlayerDied, f.Events[0].Kind);
                Assert.AreEqual((byte)age, f.Events[0].TickDelta,
                    $"and its tick delta counts back to the ORIGINAL tick, so at D+{age} it reads {age}");
            }

            AssembledFrame gone = BuildIdle(asm, w, cfg, idle);
            Assert.AreEqual(0, gone.EventCount,
                $"frame D+{redundancy} is past the horizon — {redundancy} transmissions is the whole "
                + "of EventRedundancyTicks, and one more would be an off-by-one nobody would ever see");
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEvents,
                "and ageing out of the history is not a drop: the event WAS delivered");
        }

        // ---- T29.A4. A deferred event enters the history when it SHIPS ----

        [Test]
        public void DeferredEvent_EntersHistoryWhenItShips_NotWhenItIsQueued()
        {
            // task-29-brief §2.2: the history is fed by the frame SELECTION, not
            // by the carry queue. An event that waits a tick for room has not
            // been delivered yet, so its redundancy window has not started —
            // feeding the history at `Enqueue` would burn the window on frames
            // that never carried the event at all, and it would expire early.
            const int redundancy = 4;
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            var asm = new SnapshotAssembler(cfg,
                Net(eventBudget: 2, redundancyTicks: redundancy), connectionCount: 1);
            var idle = new SimInput[3];

            // Frame D: three events, a budget of two — the cosmetic waits.
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            w.Emit(SimEventKind.PlayerDamaged, new float2(6f, 0f), 1, default, 12f,
                zone: HitZone.Body, hitDir: new float2(1f, 0f), playerIndex: 1);
            w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            AssembledFrame d = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(2, d.EventCount, "fixture premise: the budget of two admits two");
            Assert.AreEqual(0, d.CountOf(SnapshotEventKind.PlayerDashed),
                "fixture premise: the cosmetic is the one that waits");

            // D+1: the deferred cosmetic finally ships, so ITS window starts here
            // — three ticks later than the window of the two that went first.
            AssembledFrame d1 = BuildIdle(asm, w, cfg, idle);
            Assert.AreEqual(1, d1.CountOf(SnapshotEventKind.PlayerDashed),
                "the deferred event ships in the first frame with room");
            Assert.AreEqual(2, d1.EventCount, "and a resend fills the other half of the budget");

            for (int i = 0; i < 2; i++) BuildIdle(asm, w, cfg, idle);   // D+2, D+3

            AssembledFrame d4 = BuildIdle(asm, w, cfg, idle);
            Assert.AreEqual(1, d4.CountOf(SnapshotEventKind.PlayerDashed),
                "at D+4 the two events delivered in frame D are past their horizon, but the deferred "
                + "one — delivered at D+1 — is only at age 3 and must still be resent. A history fed "
                + "at ENQUEUE time would have expired it one frame ago");
            Assert.AreEqual((byte)4, d4.Events[0].TickDelta,
                "its tick delta still counts back to the tick it HAPPENED, not to the frame it shipped in");

            AssembledFrame d5 = BuildIdle(asm, w, cfg, idle);
            Assert.AreEqual(0, d5.EventCount, "witness: one frame later it really is gone");
        }

        // ---- T29.A5. Degenerate EventRedundancyTicks ----

        [Test]
        public void RedundancyTicksZeroOrOne_ProduceNoResends()
        {
            // NetConfig's Range is 0..15 and both degenerate ends are legal
            // settings, not accidents: 0 disables redundancy, and 1 means "sent
            // exactly once", which is the same thing said the other way. Neither
            // may resend, and neither may throw.
            foreach (int redundancy in new[] { 0, 1 })
            {
                SimulationWorld w = Trio(out SimConfig cfg,
                    float2.zero, new float2(6f, 0f), new float2(0f, 8f));
                var asm = new SnapshotAssembler(cfg, Net(redundancyTicks: redundancy), connectionCount: 1);
                var idle = new SimInput[3];

                w.ClearEvents();
                w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                    zone: HitZone.Body, playerIndex: 1);
                AssembledFrame d = Build(asm, w, cfg, 0, 0, 0);
                Assert.AreEqual(1, d.EventCount, $"redundancy {redundancy}: the event itself still ships");

                AssembledFrame d1 = BuildIdle(asm, w, cfg, idle);
                Assert.AreEqual(0, d1.EventCount,
                    $"redundancy {redundancy}: nothing may be repeated");
            }

            // WITNESS, and the thing that keeps the loop above from passing on a
            // do-nothing implementation: the very same fixture at the shipped
            // default DOES repeat.
            SimulationWorld w2 = Trio(out SimConfig cfg2, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            var asm2 = new SnapshotAssembler(cfg2, Net(), connectionCount: 1);
            var idle2 = new SimInput[3];
            w2.ClearEvents();
            w2.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            Build(asm2, w2, cfg2, 0, 0, 0);
            Assert.AreEqual(1, BuildIdle(asm2, w2, cfg2, idle2).EventCount,
                "witness: at the default EventRedundancyTicks the same event IS repeated, so the two "
                + "zero-assertions above are about the setting and not about an unimplemented feature");
        }

        // ---- T29.A6. A resend has no side effects ----

        [Test]
        public void Resend_HasNoSideEffects_NoResubscribe_NoCounters()
        {
            // task-29-brief §2.1/§2.7: every side effect of an event — the spawn
            // subscription, the audible latch, the counters — happened once, when
            // the event was first routed. A resend is bytes. If a resent
            // ProjectileSpawned re-opened its subscription, a round would keep
            // delivering endings after the ending that closed it.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));

            const int roundId = 4919;
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            var idle = new SimInput[2];

            w.ClearEvents();
            w.Emit(SimEventKind.ProjectileFired, new float2(1f, 0f), roundId, default, 0f,
                ProjectileOwner.Player, playerIndex: 1);
            AssembledFrame t0 = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, t0.CountOf(SnapshotEventKind.ProjectileSpawned),
                "fixture premise: the spawn is delivered, so a subscription exists to be reopened");

            // T1: the round ends, the subscription closes, and the spawn is
            // resent alongside.
            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.ProjectileExpired, new float2(30f, 0f), roundId, default, 0f);
            AssembledFrame t1 = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, CountWithDelta(t1, SnapshotEventKind.ProjectileEnded, 0),
                "the ending is delivered fresh");
            Assert.AreEqual(1, CountWithDelta(t1, SnapshotEventKind.ProjectileSpawned, 1),
                "and the spawn rides again as a resend — the fixture premise that makes T2 below "
                + "discriminate at all");

            // T2: a second ending for the same round must find nobody. If the
            // resent spawn had gone back through the routing switch, it would
            // have re-subscribed and this would be delivered.
            w.TickAll(idle);
            w.ClearEvents();
            w.Emit(SimEventKind.ProjectileExpired, new float2(31f, 0f), roundId, default, 0f);
            AssembledFrame t2 = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(0, CountWithDelta(t2, SnapshotEventKind.ProjectileEnded, 0),
                "no FRESH ending: the subscription closed with the first one and a resend must not "
                + "have reopened it");
            Assert.AreEqual(1, CountWithDelta(t2, SnapshotEventKind.ProjectileEnded, 1),
                "witness: the first ending's own resend IS there, so the frame is not simply empty");
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEvents,
                "and no counter moves for a resend (task-29-brief §2.7)");
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEntities);
        }

        static int CountWithDelta(AssembledFrame f, SnapshotEventKind kind, byte tickDelta)
        {
            int n = 0;
            for (int i = 0; i < f.EventCount; i++)
                if ((SnapshotEventKind)f.Events[i].Kind == kind && f.Events[i].TickDelta == tickDelta) n++;
            return n;
        }

        // ---- T29.A7. A resend past the tick-delta byte leaves silently ----

        [Test]
        public void ResendPastTheTickDeltaByte_IsEvictedSilently_WithoutCountingADrop()
        {
            // task-29-brief §2.4. A record delivered with its delta already near
            // the edge of the one-byte field cannot be described a tick later, so
            // it leaves the history — but WITHOUT touching DroppedEvents, unlike
            // the same overflow on the fresh path. The difference is real: there
            // a meaning that was never delivered is lost; here only a degree of
            // redundancy on something the client already has.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));

            var asm = new SnapshotAssembler(cfg, Net(eventBudget: 1), connectionCount: 1);
            var idle = new SimInput[2];

            // The dash is held back by a budget of one behind a death, then left
            // to age to the very edge of the tick-delta byte.
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
            Build(asm, w, cfg, 0, 0, 0);

            for (int i = 0; i < byte.MaxValue - 1; i++) { w.TickAll(idle); w.ClearEvents(); }
            AssembledFrame edge = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(1, edge.EventCount, "the carried dash finally ships at the edge of the byte");
            Assert.AreEqual((byte)SnapshotEventKind.PlayerDashed, edge.Events[0].Kind);
            Assert.AreEqual((byte)(byte.MaxValue - 1), edge.Events[0].TickDelta);
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEvents,
                "fixture premise: nothing has been dropped up to here");

            AssembledFrame last = BuildIdle(asm, w, cfg, idle);
            Assert.AreEqual(1, last.EventCount, "one more tick still fits the byte exactly");
            Assert.AreEqual(byte.MaxValue, last.Events[0].TickDelta,
                "witness: the resend really does ride at the maximum describable delta");

            AssembledFrame past = null;
            Assert.DoesNotThrow(() => past = BuildIdle(asm, w, cfg, idle),
                "a resend that outgrew the tick-delta byte must not make the frame throw");
            Assert.AreEqual(0, past.EventCount, "and it must not ride with a wrapped delta either");
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEvents,
                "and it must NOT be counted as a dropped event: the record was delivered, only its "
                + "redundancy expired (task-29-brief §2.4)");
        }

        // ---- T29.A8. Resends allocate nothing in steady state ----

        [Test]
        public void Resends_DoNotAllocateGCMemory()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            TestWorlds.SpawnMobsToCap(w);
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 3);
            var idle = new SimInput[3];

            // Three ticks of events, so every connection's history is populated
            // across several ages.
            for (int t = 0; t < 3; t++)
            {
                w.ClearEvents();
                w.Emit(SimEventKind.WaveStarted, float2.zero, 0, default, 0f);
                w.Emit(SimEventKind.PlayerDashed, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
                w.Emit(SimEventKind.PlayerSlideStarted, new float2(6f, 0f), 0, default, 0f, playerIndex: 1);
                asm.BeginTick(w);
                for (int c = 0; c < 3; c++) asm.BuildFor(c, c, c, Epoch);
                w.TickAll(idle);
            }
            w.ClearEvents();

            // Warm-up OUTSIDE the measured lambda, with the premise that defeats
            // a do-nothing implementation (Task 26 finding F-D): with no fresh
            // events left, every record in the frame is a RESEND.
            asm.BeginTick(w);
            for (int c = 0; c < 3; c++)
            {
                int bytes = asm.BuildFor(c, c, c, Epoch);
                AssembledFrame f = AssembledFrame.Decode(asm.BufferFor(c), bytes, cfg);
                Assert.Greater(f.EventCount, 0, "fixture premise (stub-defeating): resends must ride");
                for (int i = 0; i < f.EventCount; i++)
                    Assert.Greater(f.Events[i].TickDelta, (byte)0,
                        "fixture premise: nothing fresh is left, so every record is a resend");
                Assert.Greater(f.MobCount, 0, "fixture premise: the frame is a full one, not an empty shell");
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

        // ---- T29.A9. Frames with resends are still reproducible ----

        [Test]
        public void TwoConnectionsInTheSameState_ProduceByteIdenticalFrames_WithResends()
        {
            // The Task 28 invariant, extended over the new state: two
            // connections that have seen the same frames must hold the same
            // history and emit the same resend bytes. Without it a Task 32
            // mismatch could never be told apart from a history that simply is
            // not reproducible.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            TestWorlds.SpawnMobsAt(w,
                (MobType.Chaser, new float2(4f, 4f)), (MobType.Gunner, new float2(-5f, 1f)));
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 2);
            var idle = new SimInput[3];

            w.ClearEvents();
            w.Emit(SimEventKind.WaveStarted, float2.zero, 0, default, 0f);
            w.Emit(SimEventKind.PlayerDied, new float2(6f, 0f), 1, default, 0f,
                zone: HitZone.Body, playerIndex: 1);
            asm.BeginTick(w);
            asm.BuildFor(0, 0, 0, Epoch);
            asm.BuildFor(1, 0, 0, Epoch);

            w.TickAll(idle);
            w.ClearEvents();
            asm.BeginTick(w);
            int a = asm.BuildFor(0, 0, 0, Epoch);
            int b = asm.BuildFor(1, 0, 0, Epoch);

            AssembledFrame fa = AssembledFrame.Decode(asm.BufferFor(0), a, cfg);
            Assert.Greater(fa.EventCount, 0, "fixture premise (stub-defeating): the compared frame "
                + "must actually carry resends");
            for (int i = 0; i < fa.EventCount; i++)
                Assert.Greater(fa.Events[i].TickDelta, (byte)0, "fixture premise: all of them resends");

            Assert.AreEqual(a, b, "two identically-fed connections must write the same number of bytes");
            byte[] bufA = asm.BufferFor(0);
            byte[] bufB = asm.BufferFor(1);
            for (int i = 0; i < a; i++)
                Assert.AreEqual(bufA[i], bufB[i],
                    $"byte {i} must match — the resend history is a pure function of what was delivered");
        }

        // ---- Phase gate fix wave (finding G7). One assembler, one config ----

        [Test]
        public void BeginTick_RefusesAWorldWithMoreEventCapacityThanConstructed()
        {
            // The wire buffers are sized once, from the constructor's caps; a
            // world carrying a larger Arena.MaxEventsPerFrame could overrun
            // them without diagnosis. Task 36 builds both from one
            // MatchConfig, so today the mismatch takes a caller bug — which
            // is exactly what a loud refusal is for (same asymmetry as the
            // writer: this is a server-side programming error, not hostile
            // input).
            SimulationWorld matched = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f),
                new float2(0f, 8f));
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);

            SimConfig bigger = cfg;
            bigger.Arena.MaxEventsPerFrame = cfg.Arena.MaxEventsPerFrame * 2;
            var biggerWorld = new SimulationWorld(1, bigger, playerCount: 3);

            Assert.Throws<System.InvalidOperationException>(() => asm.BeginTick(biggerWorld),
                "a world with more event capacity than the constructor sized for must be refused loudly");
            // The matched world still works after the refusal — the guard
            // rejects the call, not the instance.
            asm.BeginTick(matched);
            Assert.Greater(asm.BuildFor(0, 0, 0, Epoch), 0,
                "the refusal must leave the assembler fully usable for its own config");
        }

        // ---- Stage 2 Task 42a fix-round 1, I-1: viewpoint memory reset ----

        /// One mob starting in plain sight, then moved past `SightRadius +
        /// ExitHysteresis` — the exact fixture shape
        /// `MobFilter_InvisibleAbsent_LingeringPresent_BehindObstacleAbsent`
        /// already uses to exercise Р19's linger. A fresh `SimulationWorld`/
        /// `SnapshotAssembler` pair per call, so the witness and the reset
        /// branch below never share state with each other.
        static (SimulationWorld world, SimConfig cfg, SnapshotAssembler asm, int mobId) LingerFixture()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(0f, 10f));
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            return (w, cfg, asm, mobId);
        }

        static void MoveMobPastSightAndHysteresis(SimulationWorld w, in SimConfig cfg)
        {
            // Fix-round 2, M-c: the same "test setup" discipline
            // MobFilter_InvisibleAbsent_LingeringPresent_BehindObstacleAbsent
            // already uses before trusting a spawn-order slot index — even
            // though LingerFixture spawns exactly one mob, so `Mobs[0]` could
            // not actually be ambiguous today, the assert is what keeps that
            // true a mechanical fact rather than an assumption the next
            // reader has to re-derive by counting spawn calls.
            MobState m = w.Mobs[0];
            Assert.AreEqual(1, w.MobCount, "test setup: LingerFixture spawns exactly one mob");
            m.Pos = new float2(0f, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis + 5f);
            w.SetMobForTest(0, m);
        }

        [Test]
        public void ResetViewpointMemory_ClearsLinger_WitnessedAgainstNoReset()
        {
            // Fix-round 2, M-a: named for what this fixture ACTUALLY
            // exercises. The mob moves to SightRadius + ExitHysteresis + 5 —
            // past the WIDENED hysteresis radius too, not merely past
            // SightRadius — so what carries it into the second frame is
            // PURELY Р19's linger, never the hysteresis widening itself
            // (`MobFilter_..._LingeringPresent...`'s own fixture makes the
            // identical choice, for the identical reason: a mob sitting
            // INSIDE the hysteresis band would leave it ambiguous which of
            // the two mechanisms the assert is actually about).
            //
            // Positive witness FIRST: without a reset, a mob that just left
            // sight keeps riding through the linger window (Р19) — proving
            // this fixture genuinely exercises linger, so the "no longer
            // held" assert below is actually about ResetViewpointMemory and
            // not an accident of a fixture that never lingered anything to
            // begin with.
            (SimulationWorld wWitness, SimConfig cfgWitness, SnapshotAssembler asmWitness, int mobWitness) =
                LingerFixture();
            Build(asmWitness, wWitness, cfgWitness, 0, 0, 0);
            MoveMobPastSightAndHysteresis(wWitness, in cfgWitness);
            AssembledFrame witnessFrame = Build(asmWitness, wWitness, cfgWitness, 0, 0, 0);
            Assert.IsTrue(witnessFrame.ContainsMob(mobWitness),
                "witness: WITHOUT a reset, a mob that just left sight must keep riding through the "
                + "linger window (Р19) — this is the premise the reset assert below defeats");

            // The target: the identical sequence, but ResetViewpointMemory
            // runs between the move and the next BuildFor — exactly what
            // MatchServer.OnSpectateRequest does on an accepted switch
            // (Stage 2 Task 42a fix-round 1, I-1).
            (SimulationWorld wReset, SimConfig cfgReset, SnapshotAssembler asmReset, int mobReset) =
                LingerFixture();
            Build(asmReset, wReset, cfgReset, 0, 0, 0);
            MoveMobPastSightAndHysteresis(wReset, in cfgReset);
            asmReset.ResetViewpointMemory(0);
            AssembledFrame resetFrame = Build(asmReset, wReset, cfgReset, 0, 0, 0);
            Assert.IsFalse(resetFrame.ContainsMob(mobReset),
                "ResetViewpointMemory must clear the linger memory (Previous) — a mob that just left "
                + "sight must NOT keep riding once this connection's viewpoint memory was reset in "
                + "between (Stage 2 Task 42a fix-round 1, I-1: a switched spectator must not receive "
                + "live positions computed from the OLD viewpoint)");
        }

        [Test]
        public void ResetViewpointMemory_PreventsAnEntityLeakingAcrossARealViewpointSwitch()
        {
            // Fix-round 2, I-B: the SHAPE the previous test does not cover.
            // `ResetViewpointMemory_ClearsLinger_WitnessedAgainstNoReset`
            // above calls BuildFor twice with the SAME viewpointIndex (0)
            // and moves the ENTITY — it pins linger, but MUT-1 of fix-round 1
            // showed it does not pin the actual defect I-1 fixes, because
            // that defect is about `viewpointIndex` itself CHANGING between
            // two BuildFor calls (exactly what MatchServer.OnSpectateRequest
            // does on an accepted switch), not about an entity moving while
            // the viewpoint stays put. This test drives that real shape.
            //
            // Geometry: a mob sits next to player 0 (visible from viewpoint
            // 0); player 2 sits far enough away that it could never see that
            // mob on its own merits (distance from the mob comfortably past
            // SightRadius + ExitHysteresis — the same widened bound the
            // fixture above defeats). Build once from viewpoint 0 (the mob
            // enters `Current`, hence next call's `Previous`), then again
            // from viewpoint 2 — a DIFFERENT, unrelated position.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(200f, 0f));
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(0f, 10f));

            float distanceFromPlayer2 = math.distance(new float2(200f, 0f), new float2(0f, 10f));
            Assert.Greater(distanceFromPlayer2, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: player 2 must sit far enough away that it could never see the mob "
                + "on the strength of its own position — anything else would confound the leak this "
                + "test is about with an ordinary, legitimate sighting");

            // Witness FIRST, WITHOUT a reset: the mob — visible only from
            // viewpoint 0 — must leak into the frame built for viewpoint 2.
            // This is the defect fix-round 1 actually introduced and fixed;
            // proving it reproduces here is what makes the reset assert
            // below a real regression guard rather than a tautology.
            var asmWitness = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            Build(asmWitness, w, cfg, connection: 0, identityIndex: 0, viewpointIndex: 0);
            AssembledFrame beforeSwitch = Build(asmWitness, w, cfg, connection: 0, identityIndex: 0,
                viewpointIndex: 2);
            Assert.IsTrue(beforeSwitch.ContainsMob(mobId),
                "witness: WITHOUT ResetViewpointMemory between two BuildFor calls that change "
                + "viewpointIndex, an entity visible only from the OLD viewpoint leaks into the frame "
                + "built for the NEW one — this is the actual shape of fix-round 1's I-1 finding");

            // The target: the identical sequence, on a fresh assembler, with
            // ResetViewpointMemory called between the two BuildFor calls —
            // exactly what MatchServer.OnSpectateRequest does on an accepted
            // switch.
            var asmReset = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            Build(asmReset, w, cfg, connection: 0, identityIndex: 0, viewpointIndex: 0);
            asmReset.ResetViewpointMemory(0);
            AssembledFrame afterSwitch = Build(asmReset, w, cfg, connection: 0, identityIndex: 0,
                viewpointIndex: 2);
            Assert.IsFalse(afterSwitch.ContainsMob(mobId),
                "ResetViewpointMemory must prevent an entity visible only from the OLD viewpoint from "
                + "leaking into the frame built for a genuinely DIFFERENT new viewpointIndex — the real "
                + "shape of the spectate-switch leak, not merely an entity moving under a fixed viewpoint");
        }

        [Test]
        public void AudibleLatch_SurvivesResetViewpointMemory_AcrossARealViewpointSwitch()
        {
            // Ф8 gate W-10. `ResetViewpointMemory`'s own doc records a
            // DELIBERATE decision (fix-round 2, C-1, reversing fix-round 1's
            // own ruling): the Р133 anti-dither latch (`LatchIds`/
            // `LatchCells`/`LatchCount`) is memory of the SOURCE, not of this
            // connection's viewpoint, and clearing it on every accepted
            // switch would force a fresh, independent rounding of that
            // source's position on its very next audible event — exactly the
            // averaging attack Р133 exists to close, once per switch, for
            // every source a spectator can still only hear. Nothing in this
            // suite pinned that decision before this test: every OTHER
            // ResetViewpointMemory fixture is about the VISIBILITY pair
            // (Previous/Current), and every OTHER AudibleLatch fixture never
            // calls ResetViewpointMemory at all — so a future "fix" that
            // taught ResetViewpointMemory to also clear the latch would pass
            // every existing test in this file and silently reopen the leak.
            var cfg = TestConfigs.Open();
            float grid = cfg.Visibility.HearPositionGridMeters;
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            // Player 2 is the switch's DESTINATION viewpoint, placed at the
            // IDENTICAL position as player 0 on purpose: this isolates
            // ResetViewpointMemory's own effect on the latch from any change
            // in hearing/sight geometry a genuinely different viewpoint
            // position would also introduce (ResetViewpointMemory_
            // PreventsAnEntityLeakingAcrossARealViewpointSwitch above already
            // covers the geometry-changes case, for visibility, not hearing).
            TestWorlds.RelocatePlayerForTest(w, 2, float2.zero);

            // The source STRADDLES a grid cell boundary between the two
            // frames — AudibleLatch_OscillationInsideTheMargin's own
            // technique, reused here for the reason that test explains: a
            // STATIONARY source cannot discriminate "the old latch survived"
            // from "the latch was cleared and immediately re-latched onto
            // the SAME unmoved position", because both give the identical
            // answer (measured directly: an earlier draft of this fixture
            // used one fixed position and passed unchanged even after
            // ResetViewpointMemory was mutated to also clear the latch).
            // `boundaryX` is exactly on a 3 m cell boundary (1.5 + 3*16, the
            // same value that fixture uses) and both positions stay out of
            // sight (> SightRadius + ExitHysteresis) and in earshot
            // (< HearRadius) throughout.
            const float boundaryX = 49.5f;
            const float amplitude = 0.1f;
            Assert.Greater(boundaryX - amplitude, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "fixture premise: the source must stay out of sight on both sides of the boundary");
            Assert.Less(boundaryX + amplitude, cfg.Visibility.HearRadius,
                "fixture premise: the source must stay in earshot on both sides of the boundary");
            Assert.Less(amplitude, grid * 0.75f,
                "fixture premise: the move must stay inside the latch's own hysteresis margin — "
                + "otherwise even a SURVIVING latch would legitimately re-latch, and the test would "
                + "prove nothing about ResetViewpointMemory specifically");
            var pos1 = new float2(boundaryX - amplitude, 0f);
            var pos2 = new float2(boundaryX + amplitude, 0f);
            Assert.Greater(math.distance(
                    VisibilitySystem.QuantizeAudiblePos(pos1, cfg.Visibility),
                    VisibilitySystem.QuantizeAudiblePos(pos2, cfg.Visibility)),
                grid * 0.5f,
                "fixture premise: a FRESH coarsening of the two positions must land in DIFFERENT cells, "
                + "or a cleared-and-relatched result could not be told apart from a kept one");

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);

            // Frame 1: viewpoint 0, the source (player 1) at pos1 — latches
            // a cell.
            TestWorlds.RelocatePlayerForTest(w, 1, pos1);
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDashed, pos1, 0, default, 0f, playerIndex: 1);
            AssembledFrame first = Build(asm, w, cfg, connection: 0, identityIndex: 0, viewpointIndex: 0);
            Assert.AreEqual(1, first.EventCount, "fixture premise: the dash must be delivered by hearing");
            float2 latchedCell = first.Events[0].Pos;
            Assert.That(math.distance(latchedCell, VisibilitySystem.QuantizeAudiblePos(pos1, cfg.Visibility)),
                Is.LessThan(0.01f),
                "fixture premise: the first delivery is the plain coarsening of the source's own position");

            // The switch: MatchServer.OnSpectateRequest's own sequence —
            // reset this connection's viewpoint memory, then (in the next
            // BuildFor call) move viewpointIndex itself.
            asm.ResetViewpointMemory(0);

            // Frame 2: the source moves to pos2 — just across the grid
            // boundary, still well inside the latch's hysteresis margin —
            // and is now viewed from player 2 (a genuinely different
            // viewpointIndex, at the same position as player 0 — see the
            // geometry note above).
            TestWorlds.RelocatePlayerForTest(w, 1, pos2);
            w.ClearEvents();
            w.Emit(SimEventKind.PlayerDashed, pos2, 0, default, 0f, playerIndex: 1);
            AssembledFrame second = Build(asm, w, cfg, connection: 0, identityIndex: 0, viewpointIndex: 2);
            Assert.AreEqual(1, second.EventCount, "the dash must still be delivered by hearing after the switch");

            Assert.That(math.distance(second.Events[0].Pos, latchedCell), Is.LessThan(0.01f),
                "the Р133 latch must survive ResetViewpointMemory — a switch that cleared it would force "
                + "a fresh, independent rounding of the source's new (but still-within-margin) position "
                + "on its very next audible event, exactly the averaging leak Р133 exists to close "
                + "(SnapshotAssembler.ResetViewpointMemory's own doc, fix-round 2 C-1)");
        }
    }
}
