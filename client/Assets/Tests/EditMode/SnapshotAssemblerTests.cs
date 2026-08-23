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
        /// Stage 3 Task 25 (spec Р257): the Liveness block's SECOND mask.
        public byte ExtractedMask;
        public SnapshotBlocks.MobRecord[] Mobs;
        public int MobCount;
        public WavePhase WavePhase;
        public ushort WaveIndex;
        public byte WaveAliveCount;
        public SnapshotBlocks.EventRecord[] Events;
        public SnapshotEventPayload[] Payloads;
        public int EventCount;

        /// Stage 3 Task 27: the five blocks Task 25 built a codec for and
        /// nobody wrote into a frame until now (spec §3.12). They are read
        /// back through the same real decoders as the five above — a block
        /// this class did not decode would be a block the receiver cannot
        /// either.
        public MatchPhase MatchPhase;
        public ushort MatchSecondsRemaining;
        public byte MatchFlags;
        public byte SelfSlotPoints;
        public byte[] SelfItems;
        public int SelfItemCount;
        public SnapshotBlocks.PickupRecord[] Pickups;
        public int PickupCount;
        public SnapshotBlocks.ContainerRecord[] Containers;
        public int ContainerCount;
        public SnapshotBlocks.ContainerSlotsRecord[] Slots;
        public int SlotsCount;
        /// The ContainerSlots block's own payload, kept because a record's
        /// `ItemOffset` indexes INTO it on the read side (the struct's own
        /// doc says so) — without it a decoded record cannot be asked what
        /// the box actually holds.
        public byte[] SlotsPayload;

        public bool TryPickup(int id, out SnapshotBlocks.PickupRecord record)
        {
            for (int i = 0; i < PickupCount; i++)
                if (Pickups[i].Id == id) { record = Pickups[i]; return true; }
            record = default;
            return false;
        }

        public bool TryContainer(int id, out SnapshotBlocks.ContainerRecord record)
        {
            for (int i = 0; i < ContainerCount; i++)
                if (Containers[i].Id == id) { record = Containers[i]; return true; }
            record = default;
            return false;
        }

        public bool TrySlots(int id, out SnapshotBlocks.ContainerSlotsRecord record)
        {
            for (int i = 0; i < SlotsCount; i++)
                if (Slots[i].Id == id) { record = Slots[i]; return true; }
            record = default;
            return false;
        }

        /// The item ids one decoded ContainerSlots record carries, in
        /// ascending slot order — its mask's popcount worth of bytes taken
        /// from the block payload at its own offset.
        public byte[] ItemsOf(in SnapshotBlocks.ContainerSlotsRecord record)
        {
            int occupied = SnapshotBlocks.OccupiedSlotCount(record.OccupancyMask);
            var items = new byte[occupied];
            System.Array.Copy(SlotsPayload, record.ItemOffset, items, 0, occupied);
            return items;
        }

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
            // Stage 3 Task 27: the five new kinds, now that a frame carries them.
            (byte)SnapshotBlockKind.Match, (byte)SnapshotBlockKind.Self,
            (byte)SnapshotBlockKind.Pickups, (byte)SnapshotBlockKind.Containers,
            (byte)SnapshotBlockKind.ContainerSlots,
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
                SelfItems = new byte[math.max(1, cfg.Hero.MaxInventoryItems)],
                Pickups = new SnapshotBlocks.PickupRecord[math.max(1, cfg.Arena.MaxPickups)],
                Containers = new SnapshotBlocks.ContainerRecord[math.max(1, cfg.Arena.MaxContainers)],
                Slots = new SnapshotBlocks.ContainerSlotsRecord[math.max(1, cfg.Arena.MaxContainers)],
                SlotsPayload = System.Array.Empty<byte>(),
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
                            out f.ExtractedMask,
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

                    // Stage 3 Task 27: the five blocks Task 25 gave a codec
                    // and nobody wrote into a frame until now (spec §3.12).
                    // Read back through the real decoders — a block this
                    // class could not decode is one the receiver could not
                    // either.
                    case SnapshotBlockKind.Match:
                        Assert.IsTrue(SnapshotBlocks.TryReadMatchBlock(payload, out f.MatchPhase,
                            out f.MatchSecondsRemaining, out f.MatchFlags,
                            out SnapshotBlockError mte), $"Match block refused: {mte}");
                        break;
                    case SnapshotBlockKind.Self:
                        Assert.IsTrue(SnapshotBlocks.TryReadSelfBlock(payload, cfg, f.SelfItems,
                            out f.SelfSlotPoints, out f.SelfItemCount, out SnapshotBlockError sfe),
                            $"Self block refused: {sfe}");
                        break;
                    case SnapshotBlockKind.Pickups:
                        Assert.IsTrue(SnapshotBlocks.TryReadPickupsBlock(payload, cfg, f.Pickups,
                            out f.PickupCount, out SnapshotBlockError pke),
                            $"Pickups block refused: {pke}");
                        break;
                    case SnapshotBlockKind.Containers:
                        Assert.IsTrue(SnapshotBlocks.TryReadContainersBlock(payload, cfg, f.Containers,
                            out f.ContainerCount, out SnapshotBlockError cne),
                            $"Containers block refused: {cne}");
                        break;
                    case SnapshotBlockKind.ContainerSlots:
                        Assert.IsTrue(SnapshotBlocks.TryReadContainerSlotsBlock(payload, cfg, f.Slots,
                            out f.SlotsCount, out SnapshotBlockError cse),
                            $"ContainerSlots block refused: {cse}");
                        // A record's ItemOffset indexes into THIS payload on
                        // the read side (the struct's own doc), so the bytes
                        // are kept rather than parsed away.
                        f.SlotsPayload = payload.ToArray();
                        break;
                }
            }

            Assert.IsFalse(reader.Failed, "an assembled frame must parse cleanly to its end");
            Assert.AreEqual(10, blocks,
                "the assembler always emits all TEN blocks in canonical order, even when a block is empty "
                + "— Stage 3 Task 27 added Self, Match, ContainerSlots, Containers and Pickups to the "
                + "five of Stage 2, and none of them may be conditional on having content: a receiver "
                + "cannot tell an absent block from an empty one (SnapshotReader's own account)");
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
        /// bd app-3cph: the byte cap the two GC fixtures need is a FUNCTION of
        /// the mob cap, never a literal. Both fill the world to Arena.MaxMobs
        /// with every mob inside the observers' sight, and both need the EVENT
        /// block to ride as well — their own "events/resends must actually
        /// ride" premises say so. Т12's hand-computed 4000 covered 288 mobs
        /// (2595 B of records); at 1350 the mob block alone is 12 153 B, the
        /// assembler's documented precedence gives events nothing, and the
        /// premise fails without a single line of production code being wrong.
        /// Fixed part + the whole crowd + room for the events, so the arithmetic
        /// survives the next retune of MaxMobs too.
        static int RoomyCapForFullCrowd(in SimConfig cfg)
            => 64 + cfg.Arena.MaxMobs * SnapshotBlocks.MobRecordBytes + 512;

        static NetConfig Net(int maxBytes = 1000, int eventBudget = 16, int redundancyTicks = 4)
        {
            var net = ScriptableObject.CreateInstance<NetConfig>();
            net.SnapshotMaxBytes = maxBytes;
            net.SnapshotEventBudget = eventBudget;
            net.EventRedundancyTicks = redundancyTicks;
            return net;
        }

        /// Open arena (no obstacles, no walls, no zone walls, waves pushed out
        /// of reach) with the three players placed by hand — Geometry.
        /// SpawnPosFor would put them on the spawn ring (radius 103.96 since
        /// Stage 3 Task 12, 52 before it), where every distance a visibility
        /// fixture states would be incidental.
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

        /// Stage 3 Т29: the raid's own kinds REACH THE WIRE. Until this task
        /// `SnapshotAssembler.BeginTick`'s mapping switch had no case for
        /// DirectorActivated/DirectorDied/PlayerExtracted — and it has no
        /// `default` either, so all three were emitted by the simulation every
        /// raid and dropped on the floor without a counter, a log or a failing
        /// test. THIS is the witness that says otherwise; a mapping case
        /// removed again fails here by name.
        ///
        /// The Director pair is asserted through an All-channel frame: they
        /// reach a connection that can see nothing, and they arrive with no
        /// position — the spot a collector walked into the core is exactly
        /// what Р299 refused to ship.
        [Test]
        public void Stage3RaidKinds_ReachTheWire()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            w.ClearEvents();

            // Positions that are emphatically not the origin, so "no position"
            // cannot be confused with "the emitter passed zero".
            w.Emit(SimEventKind.DirectorActivated, new float2(31f, -17f), 0, default, 0f);
            w.Emit(SimEventKind.DirectorDied, new float2(-23f, 41f), 0, default, 0f);
            // Slot 1 walked out — not slot 0, whose own frame this is.
            w.Emit(SimEventKind.PlayerExtracted, w.PlayerAt(1).Pos, 0, default, 0f, playerIndex: 1);
            // Owner channel: addressed to slot 0, whose frame this is.
            w.Emit(SimEventKind.PickupTaken, new float2(1f, 1f), 4242, default, 0f, playerIndex: 0);
            // Two boxes: one under the connection's nose, one across the
            // arena. R-236 decides this kind in the assembler, against the
            // CONTAINERS set — so the far one must not ride, and an
            // implementation that enqueued unconditionally fails on it.
            int nearBox = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });
            int farBox = w.SpawnContainer(ContainerKind.Crate, new float2(400f, 400f), new byte[] { 1 });
            w.Emit(SimEventKind.ContainerEmptied, new float2(1f, 0f), nearBox, default, 0f);
            w.Emit(SimEventKind.ContainerEmptied, new float2(400f, 400f), farBox, default, 0f);

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, connection: 0, identityIndex: 0, viewpointIndex: 0);

            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.DirectorActivated),
                "the Director's arrival must ride the wire — before Т29 the assembler dropped it silently");
            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.DirectorDied),
                "and so must his fall, which is what opens the gate");
            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.PlayerExtracted),
                "a collector walking out is visible to whoever can see them, and slot 1 is");

            // Quantized on the wire, so the check is the same shape the wave
            // pair's own assertion uses: a length near zero, and emphatically
            // NOT the position emitted.
            Assert.IsTrue(f.TryFirstOf(SnapshotEventKind.DirectorActivated, out int activatedAt));
            Assert.That(math.length(f.Events[activatedAt].Pos), Is.LessThan(0.01f),
                "an All-channel event carries no position (Р28) — and the one it would carry names "
                + "the collector who woke him");
            Assert.IsTrue(f.TryFirstOf(SnapshotEventKind.DirectorDied, out int diedAt));
            Assert.That(math.length(f.Events[diedAt].Pos), Is.LessThan(0.01f),
                "same for the fall, whose position is the corpse everyone is about to fight over");

            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.PickupTaken),
                "a collected cell reaches the collector — the Owner channel names slot 0 here");
            Assert.IsTrue(f.TryFirstOf(SnapshotEventKind.PickupTaken, out int takenAt));
            Assert.AreEqual(4242, f.Payloads[takenAt].Id, "…and it names the cell it was about");

            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.ContainerEmptied),
                "ONE of the two boxes: the one this connection can see. An assembler branch that "
                + "enqueued without asking ContainersCurrent would ship both");
            Assert.IsTrue(f.TryFirstOf(SnapshotEventKind.ContainerEmptied, out int emptiedAt));
            Assert.AreEqual(nearBox, f.Payloads[emptiedAt].Id, "and it is the near one");
        }

        /// Gate Ф6 (review A-1): THE MAPPING SWITCH IS ITSELF A HOME OF THE
        /// CATALOG, AND A HOME THROWS ON A KIND IT DOES NOT KNOW (spec Р281).
        /// `BeginTick`'s `SimEventKind -> SnapshotEventKind` switch is the
        /// NINTH place a new kind must touch — R-231 counted seven, Т29 found
        /// `EventRelevance.VisibleSubjectId` as the eighth — and until this
        /// gate it was the only one of the nine that answered SILENCE: a kind
        /// with no case produced no wire record, no counter and no red test.
        ///
        /// That is not a hypothesis, it is this phase's own anamnesis.
        /// DirectorActivated, DirectorDied (Т21) and PlayerExtracted (Т23)
        /// were emitted every raid for two stages and fell through this exact
        /// switch, which is the diagnosis Т29 opened with (lesson 382).
        ///
        /// THE EXISTING GUARDS DO NOT WATCH THIS SEAM, AND THE CODE SAYS SO
        /// ITSELF: `EventRelevance.ChannelFor`'s own doc — "neither of them
        /// watches the wire". Precisely: `ChannelFor_HandlesEveryKind` walks
        /// the whole enumeration but asks only for a CHANNEL, and `ChannelFor`'s
        /// only production caller (`RouteEvents`) reads the already-assembled
        /// `_wire`, which an unmapped kind never reaches. `Stage3RaidKinds_
        /// ReachTheWire` above holds the five kinds that EXIST, by name, and
        /// says nothing about the next one.
        ///
        /// The probe is a value OUTSIDE the enumeration rather than a real
        /// kind, and that is the point rather than a shortcut: all twenty
        /// members are mapped today, so the branch guards TOMORROW's kind and
        /// its only possible witness is a value the switch has never seen.
        [Test]
        public void UnmappedEventKind_ThrowsInsteadOfVanishing()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            w.ClearEvents();

            // The SUBJECT IS THE SECOND EVENT (lesson 227): a mapped kind
            // rides first, so a switch that threw on everything — or one that
            // never reached the second element at all — would fail this
            // fixture rather than pass it.
            w.Emit(SimEventKind.WaveStarted, new float2(11f, -13f), 0, default, 0f);
            w.Emit((SimEventKind)99, new float2(2f, 3f), 7, default, 0f);

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            var refused = Assert.Throws<System.ArgumentException>(() => asm.BeginTick(w),
                "a SimEventKind with no wire mapping must fail LOUDLY here — the silent fall-through "
                + "this replaces is what lost three kinds of raid news for two stages");
            StringAssert.Contains("99", refused.Message,
                "and the refusal has to name the kind that has no mapping, or the next reader gets "
                + "a throw without an address");
        }

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

        [Test]
        public void DeadOtherPlayer_RecordCarriesTheAimHeadingItDiedWith()
        {
            // Stage 2 Task 47a fix-round 1. The corpse record's POSITION was
            // already pinned above; this is its other half. `PlayerRecordOf`
            // has no liveness branch, so `Dir` is written for a dead seat by
            // the same `normalizesafe(AimPoint - Pos)` a live one gets — and
            // that is the only direction a client which never saw the death
            // can lay the body along (`ViewRegistry.EnsureCorpse`, which faces
            // a rented body by the aim point of the very record it takes the
            // position from). A liveness guard added here, or an `AimPoint`
            // cleared by `KillPlayer`, would leave every body on the arena
            // lying the same way with nothing failing.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            var aimedAt = new float2(6f, -20f);
            var inputs = new SimInput[3];
            inputs[1] = new SimInput { AimPoint = aimedAt };
            w.TickAll(inputs);
            float2 fellAt = w.PlayerAt(1).Pos;
            w.KillPlayerNoDamage(1);

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.IsTrue(f.TryPlayer(1, out SnapshotBlocks.PlayerRecord corpse));
            Assert.AreEqual(0, corpse.Flags & PlayerWireFlags.Alive, "test setup: seat 1 is down");
            Assert.AreEqual(0, corpse.Flags & PlayerWireFlags.AimHeld,
                "and the pose bits are all clear on it — which is why the DIRECTION, not the flags, "
                + "is what a corpse's facing has to come off");

            // `Quantize.Dir` spends 256 codes on the full turn — 1.40625 deg a
            // step, so at most 0.703125 deg of error, which is 0.0123 of chord
            // between two unit vectors. The 0.02 bound is that, rounded up.
            float2 expected = math.normalizesafe(aimedAt - fellAt, new float2(1f, 0f));
            Assert.That(math.distance(corpse.Dir, expected), Is.LessThan(0.02f),
                "the body's record points where the player was aiming when it died");
            Assert.That(math.distance(corpse.Dir, new float2(1f, 0f)), Is.GreaterThan(0.5f),
                "and that is a real heading, not `normalizesafe`'s +X fallback");
        }

        // ---- T47b. One's own body, once it is a body ----

        /// Stage 2 Task 47b (the owner's decision 2a of 2026-08-11). The rule
        /// the assembler used to keep unconditionally — "never oneself" — is now
        /// conditional on being ALIVE, and this is the half that did not change:
        /// while the connection's own player is standing, its state comes back
        /// through reconciliation and the frame must not spend eight bytes
        /// repeating it. Pinned on its own rather than left to
        /// `FullFrame_RoundTrips`'s single line, because it is the one
        /// assertion that tells a correct implementation from "send the record
        /// always".
        [Test]
        public void LivingConnection_NeverReceivesItsOwnRecord()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            Assert.IsTrue(w.PlayerAt(0).Alive, "test setup: the connection's own player is standing");

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.IsFalse(f.TryPlayer(0, out _),
                "a living connection's own record must NOT ride its own frame — its state comes back "
                + "through reconciliation, and the byte budget is spent on what only the server knows");
            Assert.AreEqual(2, f.PlayerCount, "exactly the two others, and nothing else");
            Assert.AreEqual((byte)0b111, f.AliveMask, "witness: all three slots are alive this tick");
        }

        /// Stage 2 Task 47b, the other half (owner's decision 2a). A dead
        /// connection has no prediction left to fill its own seat from
        /// (`PlayerNetworkController.ShouldPredict` is false for good once the
        /// death is reported), so the snapshot is the ONLY source of its own
        /// body — and without this record the client's own seat goes from
        /// "known and alive" straight to "not known", which is the arena origin
        /// (`NetworkSimBackend.BeginSlot`'s `default(PlayerState)`) rather than
        /// the place the player fell.
        ///
        /// The POSE is asserted, not merely the presence: a body is laid along
        /// the heading of the record it is rented from
        /// (`ViewRegistry.EnsureCorpse`), so a record whose `Dir` came out as
        /// `normalizesafe`'s +X fallback would put every own-body on the arena
        /// facing the same way with nothing failing.
        [Test]
        public void DeadConnection_ReceivesItsOwnRecord_WithThePoseItDiedIn()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            var aimedAt = new float2(-14f, 9f);
            var inputs = new SimInput[3];
            inputs[0] = new SimInput { AimPoint = aimedAt };
            w.TickAll(inputs);
            float2 fellAt = w.PlayerAt(0).Pos;
            w.KillPlayerNoDamage(0);
            Assert.IsFalse(w.PlayerAt(0).Alive, "test setup: the connection's own player is down");

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.IsTrue(f.TryPlayer(0, out SnapshotBlocks.PlayerRecord own),
                "a dead connection must receive its OWN record — nothing else on this wire can "
                + "tell that client where its body is");
            Assert.AreEqual(0, own.Flags & PlayerWireFlags.Alive,
                "and it is a body: the Alive bit is what carries the news (Р68)");
            Assert.That(math.distance(own.Pos, fellAt), Is.LessThan(0.01f),
                "the body stays where it fell");

            // The same quantization bound the sibling assertion for another
            // player's corpse states: `Quantize.Dir` spends 256 codes on the
            // full turn, i.e. at most 0.0123 of chord between two unit vectors.
            float2 expected = math.normalizesafe(aimedAt - fellAt, new float2(1f, 0f));
            Assert.That(math.distance(own.Dir, expected), Is.LessThan(0.02f),
                "and it points where its player was aiming when it died");
            Assert.That(math.distance(own.Dir, new float2(1f, 0f)), Is.GreaterThan(0.5f),
                "which is a real heading, not `normalizesafe`'s +X fallback");
        }

        /// Stage 2 Task 47b: the own-body record is an ADDITION and nothing
        /// else. Two frames of the same world, one either side of the owner's
        /// own death, compared record by record — a rule that reached the
        /// others (a liveness guard, a reordering, a lost record) would show up
        /// here rather than in a playtest.
        [Test]
        public void OwnDeath_ChangesNothingAboutTheOtherRecords()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);

            AssembledFrame before = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(2, before.PlayerCount, "test setup: two others ride while the owner stands");
            Assert.IsTrue(before.TryPlayer(1, out SnapshotBlocks.PlayerRecord before1));
            Assert.IsTrue(before.TryPlayer(2, out SnapshotBlocks.PlayerRecord before2));

            w.KillPlayerNoDamage(0);
            AssembledFrame after = Build(asm, w, cfg, 0, 0, 0);

            Assert.AreEqual(3, after.PlayerCount,
                "the owner's body is one record MORE, not one record instead of another");
            Assert.IsTrue(after.TryPlayer(1, out SnapshotBlocks.PlayerRecord after1));
            Assert.IsTrue(after.TryPlayer(2, out SnapshotBlocks.PlayerRecord after2));
            AssertSameRecord(before1, after1, "slot 1");
            AssertSameRecord(before2, after2, "slot 2");
        }

        static void AssertSameRecord(in SnapshotBlocks.PlayerRecord expected,
            in SnapshotBlocks.PlayerRecord actual, string who)
        {
            Assert.AreEqual(expected.Index, actual.Index, $"{who}: index");
            Assert.AreEqual(expected.Flags, actual.Flags, $"{who}: flags");
            Assert.That(math.distance(expected.Pos, actual.Pos), Is.LessThan(0.001f), $"{who}: position");
            Assert.That(math.distance(expected.Dir, actual.Dir), Is.LessThan(0.001f), $"{who}: heading");
            Assert.That(actual.Hp, Is.EqualTo(expected.Hp).Within(0.001f), $"{who}: hp");
        }

        /// Stage 2 Task 47b: the two things one frame says about the owner's own
        /// seat must say the same thing. The Players block's Alive bit and the
        /// Liveness mask's bit are computed from the same capture but by two
        /// separate loops (`PlayerRecordOf` and `WriteFrame`'s own mask scan),
        /// and a client that believed one over the other would either predict a
        /// corpse or bury a standing player.
        [Test]
        public void OwnCorpseRecord_AndTheLivenessMask_AgreeInTheSameFrame()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            w.KillPlayerNoDamage(0);

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.IsTrue(f.TryPlayer(0, out SnapshotBlocks.PlayerRecord own), "the body rides");
            Assert.AreEqual(0, own.Flags & PlayerWireFlags.Alive, "the record says: not alive");
            Assert.AreEqual((byte)0b110, f.AliveMask,
                "and so does the roster mask — slot 0 clear, 1 and 2 set");
        }

        /// Stage 2 Task 47b: what decision 2a costs, MEASURED on two real
        /// frames of the same world rather than argued from the calculators —
        /// one player record, and only while that player is dead. The events
        /// the kill emits are cleared first, so the two frames differ in
        /// exactly one thing.
        [Test]
        public void OwnBodyRecord_CostsOnePlayerRecord_AndOnlyWhileDead()
        {
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);

            AssembledFrame alive = Build(asm, w, cfg, 0, 0, 0);
            w.KillPlayerNoDamage(0);
            // The death event itself is not what this test is measuring.
            w.ClearEvents();
            AssembledFrame dead = Build(asm, w, cfg, 0, 0, 0);

            Assert.AreEqual(0, alive.EventCount, "test setup: neither frame carries an event");
            Assert.AreEqual(0, dead.EventCount);
            Assert.AreEqual(SnapshotBlocks.PlayerRecordBytes, dead.Bytes - alive.Bytes,
                "the whole price of one's own body on the wire is one player record, and it is paid "
                + "only on the frames of a player who is already dead");
        }

        /// Stage 2 Task 47b: the constructor's own ceiling has to be the WIDEST
        /// Players block a frame can carry, which is now the whole roster.
        ///
        /// WHY A TEST AND NOT A COMMENT: a ceiling left at `MaxPlayers - 1`
        /// fails nowhere at startup. It accepts a configuration whose worst
        /// frame does not fit and then throws out of `SnapshotWriter.Reserve`
        /// INSIDE a server tick — the first time a player dies, mid-match,
        /// instead of at startup with a sentence. The cap below sits in the
        /// eight-byte gap between the two ceilings, which is the only place
        /// the two answers differ at all.
        [Test]
        public void Constructor_RefusesACapThatCannotHoldTheWholeRoster()
        {
            // Т27: the sum's own arithmetic is pinned by
            // FixedFrameBytes_CountsMatchSelfAndTheThreeNewEmptyHeaders; what
            // THIS test is about is the PLAYERS term of it, so it states the
            // two player counts and lets the one home add up the rest.
            SimConfig cfg = TestConfigs.Open();
            int wholeRoster = SnapshotAssembler.FixedFrameBytes(cfg.Arena.MaxPlayers,
                cfg.Hero.MaxInventoryItems);
            int oneSeatShort = SnapshotAssembler.FixedFrameBytes(cfg.Arena.MaxPlayers - 1,
                cfg.Hero.MaxInventoryItems);
            Assert.AreEqual(SnapshotBlocks.PlayerRecordBytes, wholeRoster - oneSeatShort,
                "premise: the two differ by exactly one player record, which is what this test is about");

            Assert.DoesNotThrow(() => new SnapshotAssembler(cfg, Net(maxBytes: wholeRoster),
                connectionCount: 1), "a cap that holds the whole roster is legal");

            var refused = Assert.Throws<System.ArgumentException>(
                () => new SnapshotAssembler(cfg, Net(maxBytes: oneSeatShort), connectionCount: 1),
                "a cap one player record short of the whole roster must be refused HERE — its worst "
                + "frame is the one a dead recipient gets, and that frame would throw inside a tick");
            StringAssert.Contains("fixed part", refused.Message,
                "and the refusal has to name what does not fit");
        }

        // ---- Т25.A1. The Liveness block's second mask (spec Р257) ----

        [Test]
        public void LivenessExtractedMask_MarksWhoWalkedOut_NotWhoDied()
        {
            // Р257, the reason the block grew a byte: one "alive" mask cannot
            // tell an extracted collector from a dead one, and BOTH consumers
            // that read it get the answer wrong in the same direction — the
            // overlay reports a teammate who got out as lost, and
            // SpectatePolicy, written for corpses, hands him someone else's
            // eyes. Slot 1 dies, slot 2 extracts: two different fates that
            // used to produce the identical byte.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f), new float2(0f, 8f));
            w.KillPlayerNoDamage(1);
            PlayerState p = w.PlayerAt(2);
            p.Alive = false;
            p.Extracted = true;
            w.SetPlayerForTest(2, in p);

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.AreEqual((byte)0b001, f.AliveMask, "only slot 0 is still on its feet");
            Assert.AreEqual((byte)0b100, f.ExtractedMask,
                "slot 2 walked out — and slot 1, who died, must NOT appear here");
            Assert.AreEqual(0, f.AliveMask & f.ExtractedMask,
                "the two masks never share a bit: a player is never both alive and extracted");
        }

        // ---- Т25.A2. The fixed part of a frame has ONE home (E-6 C-I2) ----

        [Test]
        public void FixedFrameBytes_IsOneHome_TheConstructorCeilingAndTheFrameAgree()
        {
            // Coordinator R-12: the ceiling and the per-frame subtraction used
            // to spell the same sum out separately, so a new always-riding
            // block had to be added twice with nothing demanding the second
            // edit. What this pins is that ONE function answers BOTH callers.
            // (Т27: the arithmetic of the sum itself moved to
            // FixedFrameBytes_CountsMatchSelfAndTheThreeNewEmptyHeaders when
            // the sum grew from five terms to eleven — spelling it out twice
            // would be the very duplication R-12 closed.)
            SimConfig cfg = TestConfigs.Open();
            int ceiling = SnapshotAssembler.FixedFrameBytes(cfg.Arena.MaxPlayers,
                cfg.Hero.MaxInventoryItems);

            // The constructor's own refusal is computed from the SAME home —
            // a cap one byte under it is refused, a cap exactly at it is not.
            Assert.DoesNotThrow(() => new SnapshotAssembler(cfg,
                Net(maxBytes: ceiling), connectionCount: 1));
            Assert.Throws<System.ArgumentException>(() => new SnapshotAssembler(cfg,
                Net(maxBytes: ceiling - 1), connectionCount: 1));

            // And the frame spends exactly what the home says for ITS OWN
            // player count and ITS OWN backpack: an idle solo world with an
            // empty pack writes the fixed part and nothing else, so the two
            // numbers are the same number.
            var solo = new SimulationWorld(1, cfg);
            Assert.AreEqual(0, solo.InventoryCountOf(0), "test setup: the solo pack starts empty");
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            asm.BeginTick(solo);
            int bytes = asm.BuildFor(0, 0, 0, Epoch);
            Assert.AreEqual(SnapshotAssembler.FixedFrameBytes(0, 0), bytes,
                "a solo connection is sent no player record at all (its own body rides reconciliation), "
                + "so its frame IS the fixed part at zero players and zero items");
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
            // latched cell's center, the latched cell is what ships, so the
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
            // Stage 3 Task 12: a roomier BYTE CAP than the shipped 1000, and
            // the reason is this fixture's own crowd. SpawnMobsToCap fills the
            // world to Arena.MaxMobs, which went 96 -> 288 (spec Р216), and
            // every one of them sits inside the observers' sight — 3 + 288 * 9
            // = 2595 B of mobs against a 1000 B frame. The assembler's
            // documented precedence then does exactly what it promises
            // (fixed part, then mobs into the remainder, then events into what
            // is left): mobs consume everything and the frame carries NO
            // events, which is what the "events must actually ride" premise
            // below reported. That precedence is intended and is not what this
            // test measures — GC allocation in steady state is — so the
            // fixture buys room for both blocks (38 fixed + 2595 mobs + 275
            // events = 2908) instead of thinning the crowd it exists to
            // measure. The shipped 1000 stays honest in production, where an
            // observer sees the mobs within SightRadius 45 (about 46 of the
            // 288 at even density, 417 B), not all of them.
            var asm = new SnapshotAssembler(cfg, Net(maxBytes: RoomyCapForFullCrowd(in cfg)),
                connectionCount: 3);

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
            // bd app-dsh, CONFIRMED by Task 44a on a new justification (the
            // assembler's own constructor comment carries the same record).
            //
            // This test used to stand for "the HitPlayer branch is silent, so
            // an ending may never exist". Task 44a made that branch emit
            // `ProjectileHitPlayer`, and all five `RemoveProjectileAt` sites in
            // `ProjectileSystem` now emit, so owner decision Р128 is settled.
            // The expiry survives because an ending can still fail to be
            // EMITTED: `SimulationWorld.Emit` drops events once the tick's
            // `Arena.MaxEventsPerFrame` buffer is full (counted in
            // `DroppedEvents`), upstream of every connection — and a
            // subscription whose ending was dropped there has nothing left
            // that could close it. The per-connection budget is NOT the reason
            // and cannot leak: `ProjectileEnded` routing unsubscribes
            // unconditionally, even when the carry queue refuses the ending.
            //
            // So what this test pins is unchanged and still load-bearing: a
            // subscription for which NO end event ever arrives closes itself —
            // spawn tick plus the longest a round can live plus the redundancy
            // window — and is swept lazily.
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
            // hysteresis margin of the actor's latched center — the exact
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
            // Same roomier cap, same reason as
            // BeginTickAndBuildFor_DoNotAllocateGCMemory above: at the shipped
            // 1000 B the 288-mob crowd leaves nothing for events, and a resend
            // is an event.
            var asm = new SnapshotAssembler(cfg, Net(maxBytes: RoomyCapForFullCrowd(in cfg)),
                connectionCount: 3);
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

        // ---- T26. A pickup is not a dead mob (errata E-8 C-I4) ----

        [Test]
        public void PickupInFrame_IsNotMistakenForADeadMob()
        {
            // THE DEFECT THIS PINS, stated as the mechanism rather than as a
            // rule (spec §3.9 Р268 item 2, errata E-8 C-I4). Entity ids come
            // from ONE counter in SimulationWorld, so a pickup's id is a
            // perfectly ordinary POSITIVE integer — the same shape a mob's id
            // has. WriteFrame's candidate loop dispatches on exactly that
            // sign: negative means a player, and everything else is looked up
            // with MobSlotOf. A pickup sharing the mob set would therefore be
            // asked "which capture slot is this mob in", answered -1, and
            // dropped through the `continue` that exists for a mob still
            // LINGERING after its death. No exception, no counter, no record:
            // the entity would simply never be in a frame, and every test in
            // this file would stay green.
            //
            // Three separate sets are the fix, and this is what proves the
            // fix is in force rather than merely written down.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(40f, 0f),
                new float2(0f, 40f));
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            int pickupId = w.SpawnPickup(PickupKind.EnergyCell, new float2(3f, 0f), 1);
            int containerId = w.SpawnContainer(ContainerKind.Crate, new float2(0f, 3f),
                new byte[] { 1 });
            w.ClearEvents();

            // The premise that makes the whole scenario dangerous in the first
            // place: all three ids are positive and DISTINCT, i.e. the sign
            // trick cannot tell them apart and only the set an id lives in
            // can. Stated, not assumed — if SimulationWorld ever gave the
            // three classes disjoint id spaces of their own, this test would
            // be pinning a hazard that no longer exists and should say so.
            Assert.Greater(pickupId, 0, "premise: a pickup id is positive, like a mob's");
            Assert.Greater(containerId, 0, "premise: a container id is positive, like a mob's");
            CollectionAssert.AllItemsAreUnique(new[] { mobId, pickupId, containerId },
                "premise: one counter feeds all three classes, so the ids are distinct but "
                + "indistinguishable by shape");

            var asm = new SnapshotAssembler(in cfg, Net(), connectionCount: 1);
            AssembledFrame frame = Build(asm, w, cfg, connection: 0, identityIndex: 0,
                viewpointIndex: 0);

            // Half one — the three sets really did sort the three classes.
            VisibilitySet mobs = asm.VisibleSetFor(0, VisibilityClass.Mobs);
            VisibilitySet pickups = asm.VisibleSetFor(0, VisibilityClass.Pickups);
            VisibilitySet containers = asm.VisibleSetFor(0, VisibilityClass.Containers);

            Assert.IsTrue(pickups.Contains(pickupId),
                "the pickup is three meters away in an open arena — it must be visible SOMEWHERE");
            Assert.IsFalse(mobs.Contains(pickupId),
                "and it must not be in the MOB set: there it would be looked up as a mob, found "
                + "missing, and dropped as a lingering corpse");
            Assert.IsTrue(containers.Contains(containerId),
                "the container must be visible in its own set for the same reason");
            Assert.IsFalse(mobs.Contains(containerId),
                "and must not be in the mob set either");
            Assert.IsTrue(mobs.Contains(mobId), "witness: the real mob IS in the mob set");

            // Half two — the frame itself. The mob block carries the mob and
            // nothing else: the pickup neither became a record nor consumed
            // one, which is what "not mistaken for a dead mob" means at the
            // wire.
            Assert.AreEqual(1, frame.MobCount,
                "the frame carries exactly the one live mob — a pickup that leaked into the "
                + "mob candidate list would either ride as a mob record or silently displace one");
            Assert.AreEqual(mobId, frame.Mobs[0].Id,
                "and the record that IS there is the mob's own, by id");

            // The counterfactual, so the two halves above are not merely
            // consistent with each other: had the pickup ridden the mob set,
            // THIS is the lookup that would have swallowed it. No mob in the
            // world answers to its id, and the branch that handles that
            // answer is a silent `continue`.
            for (int i = 0; i < w.MobCount; i++)
                Assert.AreNotEqual(pickupId, w.Mobs[i].Id,
                    "counterfactual: no mob carries the pickup's id, so MobSlotOf would have "
                    + "answered -1 and the frame would have lost it without a trace");
        }

        // ---- T26 review, I2 + I3: the two NEW pairs are live memory too ----

        /// A connection watching ONE pickup, close enough to be plainly
        /// visible — the pickup counterpart of LingerFixture above, and built
        /// the same way (a fresh world/assembler pair per call, so a witness
        /// and its target never share state).
        static (SimulationWorld world, SimConfig cfg, SnapshotAssembler asm, int pickupId)
            PickupLingerFixture()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            int pickupId = w.SpawnPickup(PickupKind.EnergyCell, new float2(0f, 10f), 1);
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            return (w, cfg, asm, pickupId);
        }

        static void MovePickupPastSightAndHysteresis(SimulationWorld w, in SimConfig cfg)
        {
            Assert.AreEqual(1, w.PickupCount, "test setup: PickupLingerFixture spawns exactly one pickup");
            PickupState pickup = w.Pickups[0];
            // Past the WIDENED radius, not merely past SightRadius, so what
            // carries it into the next frame is purely Р19's linger and never
            // the hysteresis bonus — the identical choice
            // MoveMobPastSightAndHysteresis makes, for the identical reason.
            pickup.Pos = new float2(0f, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis + 5f);
            w.SetPickupForTest(0, pickup);
        }

        [Test]
        public void PickupPair_LingersAcrossFrames_AndResetViewpointMemoryClearsItToo()
        {
            // Task 26 review, I2 and I3. Task 26 gave the connection two NEW
            // visibility pairs and wired them exactly like the mob pair — a
            // ping-pong in BuildFor and a Clear in ResetViewpointMemory — and
            // neither wire had a witness. Both failures are silent: an
            // unswapped pair leaves `previous` permanently empty, so
            // hysteresis and linger are simply dead for that class on the
            // real assembly path, and an uncleared pair keeps handing a
            // switched spectator the memory of where it used to look. The
            // frame carries no Pickups block until Task 27, so the sets
            // themselves are what has to be read.
            //
            // WITNESS FIRST, same shape as
            // ResetViewpointMemory_ClearsLinger_WitnessedAgainstNoReset: the
            // pickup that just left sight must keep riding through the linger
            // window. This half is what the ping-pong pays for — with
            // `previous` stuck empty it reads as "never seen" and is dropped
            // outright instead.
            (SimulationWorld wWitness, SimConfig cfgWitness, SnapshotAssembler asmWitness,
                int pickupWitness) = PickupLingerFixture();
            Build(asmWitness, wWitness, cfgWitness, 0, 0, 0);
            Assert.IsTrue(asmWitness.VisibleSetFor(0, VisibilityClass.Pickups).Contains(pickupWitness),
                "test setup: the pickup must start plainly visible");

            MovePickupPastSightAndHysteresis(wWitness, in cfgWitness);
            Build(asmWitness, wWitness, cfgWitness, 0, 0, 0);
            VisibilitySet lingering = asmWitness.VisibleSetFor(0, VisibilityClass.Pickups);
            Assert.IsTrue(lingering.Contains(pickupWitness),
                "witness: WITHOUT a reset, a pickup that just left sight must keep riding through "
                + "the linger window (Р19) — which it can only do if BuildFor ping-pongs the "
                + "PICKUP pair, so the set it just filled becomes the next call's `previous`");
            Assert.AreEqual(cfgWitness.Visibility.LingerTicks, lingering.LingerOf(pickupWitness),
                "and it must read as FRESHLY lingering, not as visible now — the counter is what "
                + "tells 'the previous tick remembered it' apart from 'it is in range again'");

            // The target: the identical sequence with ResetViewpointMemory in
            // between — MatchServer.OnSpectateRequest's own move.
            (SimulationWorld wReset, SimConfig cfgReset, SnapshotAssembler asmReset,
                int pickupReset) = PickupLingerFixture();
            Build(asmReset, wReset, cfgReset, 0, 0, 0);
            MovePickupPastSightAndHysteresis(wReset, in cfgReset);
            asmReset.ResetViewpointMemory(0);
            Build(asmReset, wReset, cfgReset, 0, 0, 0);
            Assert.IsFalse(asmReset.VisibleSetFor(0, VisibilityClass.Pickups).Contains(pickupReset),
                "ResetViewpointMemory must clear the PICKUP pair as well as the mob one — every "
                + "pair is keyed on the VIEWPOINT, so a spectator who just switched must not keep "
                + "receiving live coordinates of what the OLD viewpoint could see (Stage 2 Task "
                + "42a fix-round 1, I-1, now owed to all three classes)");
        }

        // ---- Т27.A. The fixed part grows: Match and Self (spec Р279) ----

        [Test]
        public void FixedFrameBytes_CountsMatchSelfAndTheThreeNewEmptyHeaders()
        {
            // Т25 built a codec for five new blocks; Т27 is the task that puts
            // them in a frame, so the ONE home of "what a frame costs before
            // its first mob record" has to grow with them. Spelled out here
            // independently of the home, exactly as the Т25 test above spells
            // out the five older terms — a home that agreed with itself and
            // with nothing else would pin nothing.
            SimConfig cfg = TestConfigs.Open();
            const int items = 4;
            int spelledOut = SnapshotWriter.HeaderBytes
                             + SnapshotWriter.PlayersBlockBytes(cfg.Arena.MaxPlayers)
                             + SnapshotWriter.LivenessBlockBytes()
                             + SnapshotWriter.WaveBlockBytes()
                             + SnapshotWriter.MobsBlockBytes(0)
                             + SnapshotWriter.EventsBlockBytes(0, 0)
                             + SnapshotWriter.MatchBlockBytes()
                             + SnapshotWriter.SelfBlockBytes(items)
                             + SnapshotWriter.PickupsBlockBytes(0)
                             + SnapshotWriter.ContainersBlockBytes(0)
                             + SnapshotWriter.ContainerSlotsBlockBytes(0, 0);
            Assert.AreEqual(spelledOut,
                SnapshotAssembler.FixedFrameBytes(cfg.Arena.MaxPlayers, items),
                "the fixed part is header + every block that always rides, and since Т27 that is TEN "
                + "blocks, not five");

            // The backpack is the one variable term, and it is variable by
            // ONE byte per item — the count byte and the slot-point byte ride
            // whether the backpack is empty or full.
            Assert.AreEqual(SnapshotAssembler.FixedFrameBytes(cfg.Arena.MaxPlayers, 0) + items,
                SnapshotAssembler.FixedFrameBytes(cfg.Arena.MaxPlayers, items),
                "one item id is one byte — the Self block's own head is already counted at zero items");
        }

        [Test]
        public void FixedCeiling_ThrowsWhenSelfBlockDoesNotFit()
        {
            // Spec Р279: the constructor is the ONE throw at server start-up,
            // and it must be sized for the WIDEST frame — which since Т27
            // includes a backpack at Hero.MaxInventoryItems. A ceiling that
            // asked for a SMALLER backpack would pass here and throw inside a
            // server tick out of SnapshotWriter.Reserve, the very first time a
            // collector filled his pack — the same failure shape Task 47b's
            // own "whole roster, not one seat fewer" paragraph describes.
            SimConfig cfg = TestConfigs.Open();
            int widest = SnapshotAssembler.FixedFrameBytes(cfg.Arena.MaxPlayers,
                cfg.Hero.MaxInventoryItems);
            int oneItemShort = widest - 1;

            Assert.DoesNotThrow(() => new SnapshotAssembler(cfg, Net(maxBytes: widest),
                connectionCount: 1), "a cap that holds the widest fixed part is legal");

            var refused = Assert.Throws<System.ArgumentException>(
                () => new SnapshotAssembler(cfg, Net(maxBytes: oneItemShort), connectionCount: 1),
                "a cap one backpack byte short of the widest fixed part must be refused HERE");
            StringAssert.Contains("fixed part", refused.Message,
                "and the refusal has to name what does not fit");

            // The witness that the ceiling really is the FULL backpack and not
            // some smaller number that happens to pass: a cap sized for an
            // EMPTY backpack is refused too, because a full one cannot fit in
            // it — this is the arm a ceiling that forgot MaxInventoryItems
            // would fail.
            Assert.Throws<System.ArgumentException>(
                () => new SnapshotAssembler(cfg,
                    Net(maxBytes: SnapshotAssembler.FixedFrameBytes(cfg.Arena.MaxPlayers, 0)),
                    connectionCount: 1),
                "a cap sized for an EMPTY backpack cannot hold a full one, so the ceiling must "
                + "refuse it: the widest Self block is Hero.MaxInventoryItems ids long");
        }

        // ---- Т27.B. The Match block (spec §3.12, R-204) ----

        [Test]
        public void MatchBlock_CarriesThePhase_TheRemainingSeconds_AndTheGateBit()
        {
            // R-204: the block carries what is LEFT of the raid, not what has
            // elapsed — elapsed is already in the frame header's own tick. The
            // number the countdown is measured against lives in NetConfig
            // (MatchMaxDurationSeconds), outside SimConfig entirely, which is
            // why the assembler has to carry it.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, 8f));

            const int matchSeconds = 60;
            NetConfig net = Net();
            net.MatchMaxDurationSeconds = matchSeconds;
            var asm = new SnapshotAssembler(cfg, net, connectionCount: 1);

            AssembledFrame first = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(MatchPhase.Farm, first.MatchPhase,
                "a raid nobody has walked into the core in is still farming");
            Assert.AreEqual((ushort)matchSeconds, first.MatchSecondsRemaining,
                "at tick zero the whole raid is still ahead");
            Assert.AreEqual(0, first.MatchFlags & MatchWireFlags.GateOpen,
                "and its gate is shut");

            // One second of ticks later the countdown has moved by exactly one
            // second — computed from the tick rate the match's own end is
            // measured against (ServerBootstrap hands MatchEndPolicy
            // MatchMaxDurationSeconds * TickRate), never from a second clock.
            TestWorlds.IdleTicks(w, net.TickRate);
            AssembledFrame later = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual((ushort)(matchSeconds - 1), later.MatchSecondsRemaining,
                "one second of ticks is one second off the countdown");

            // The gate bit is a view of the phase, and it is asserted against a
            // raid that really opened one (TestWorlds.OpenTheGate walks the
            // whole route: a collector in the core, the Director down, the
            // sharing window elapsed).
            SimConfig gateCfg = TestConfigs.Open();
            gateCfg.Flow.GateDelaySeconds = 0f;
            var gateWorld = new SimulationWorld(1, gateCfg, playerCount: 3);
            var gateAsm = new SnapshotAssembler(gateCfg, Net(), connectionCount: 1);
            TestWorlds.OpenTheGate(gateWorld, in gateCfg);
            AssembledFrame open = Build(gateAsm, gateWorld, gateCfg, 0, 0, 0);
            Assert.AreEqual(MatchPhase.GateOpen, open.MatchPhase, "premise: the gate really is open");
            Assert.AreNotEqual(0, open.MatchFlags & MatchWireFlags.GateOpen,
                "and the flags byte says so — a consumer reading the byte should not have to "
                + "re-derive the state machine's own verdict (MatchWireFlags' own doc)");
        }

        [Test]
        public void MatchBlock_DirectorAliveBit_IsNotDerivedFromThePhase()
        {
            // MatchWireFlags' own doc: this bit is NOT derivable from the
            // phase, and the window where the two disagree is exactly the one
            // a client most needs it in — the Director is dead, the gate has
            // not opened yet (GateDelaySeconds is still running), and the
            // phase is STILL DirectorActive. A bit computed from the phase
            // would keep reporting a boss who is already on the floor.
            SimConfig cfg = TestConfigs.Open();
            cfg.Flow.GateDelaySeconds = 10f;    // long enough that the window is still open below
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);

            TestWorlds.RelocatePlayerForTest(w, 2, TestWorlds.InsideCore(in cfg));
            TestWorlds.IdleTicks(w);
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase,
                "premise: a live collector in the core activates the Director (Р299)");

            AssembledFrame alive = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreNotEqual(0, alive.MatchFlags & MatchWireFlags.DirectorAlive,
                "witness: while he stands, the bit is set");

            for (int i = 0; i < w.MobCount; i++)
            {
                if (w.Mobs[i].Type != MobType.Director) continue;
                w.DamageMob(i, 1e9f, w.Mobs[i].Pos, HitZone.Body, float2.zero, ownerIndex: 0);
                break;
            }
            TestWorlds.IdleTicks(w);
            w.ClearEvents();
            Assert.AreEqual(MatchPhase.DirectorActive, w.Match.Phase,
                "premise: the phase has NOT moved yet — GateDelaySeconds is still running");

            AssembledFrame dead = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual(0, dead.MatchFlags & MatchWireFlags.DirectorAlive,
                "the bit must follow the BODY, not the phase: this is the window the doc names, "
                + "and a bit derived from MatchPhase would still be reporting him alive");
            Assert.AreEqual(0, dead.MatchFlags & MatchWireFlags.GateOpen,
                "and the gate bit is still clear, so the two bits are genuinely independent");
        }

        // ---- Т27.C. The Self block: the owner's own backpack (Р276) ----

        [Test]
        public void SelfBlock_CarriesTheOwnersBackpack_NotTheViewpointsOne()
        {
            // Spec §3.12 tag 7: the Self block goes to the OWNER — "who this
            // connection is", not "where it looks from". The two diverge the
            // moment a dead player spectates someone else (Stage 2 Task 42a),
            // and a Self block built from the viewpoint would hand a corpse
            // the contents of a living stranger's pack.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 2, new float2(0f, 8f));
            w.SetInventoryForTest(0, 1, 5);         // tier 1 trophy + a repair kit
            w.SetInventoryForTest(1, 2, 3, 4);      // a strictly different pack

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame own = Build(asm, w, cfg, connection: 0, identityIndex: 0, viewpointIndex: 0);

            Assert.AreEqual(w.InventoryCountOf(0), own.SelfItemCount,
                "the block carries every item of the owner's pack");
            CollectionAssert.AreEqual(new byte[] { 1, 5 },
                new[] { own.SelfItems[0], own.SelfItems[1] },
                "in the backpack's own order — the window that draws it addresses slots by index");
            Assert.AreEqual((byte)w.InventoryUsedSlots(0), own.SelfSlotPoints,
                "and the slot-point total, which is what the capacity bar reads (Р276: a derived "
                + "number the client must not re-derive against a catalog it may not have)");

            // The divergence: connection 0 is player 0, watching player 1.
            AssembledFrame spectating = Build(asm, w, cfg, connection: 0, identityIndex: 0,
                viewpointIndex: 1);
            Assert.AreEqual(w.InventoryCountOf(0), spectating.SelfItemCount,
                "a spectator's Self block is still HIS OWN pack — the block is keyed on identity");
            CollectionAssert.AreEqual(new byte[] { 1, 5 },
                new[] { spectating.SelfItems[0], spectating.SelfItems[1] },
                "and it must NOT be the viewpoint player's {2, 3, 4}");
        }

        // ---- Т27.D. The ground entities reach the frame (spec §3.12) ----

        /// One collector at the origin of an open arena, with a pickup and a
        /// container placed by the caller. Т26 taught the assembler to SEE
        /// them; this is the fixture for the task that puts them in a frame.
        static (SimulationWorld world, SimConfig cfg, SnapshotAssembler asm) GroundFixture(
            out int pickupId, out int containerId, float2 pickupPos, float2 containerPos,
            byte[] containerItems = null, NetConfig net = null)
        {
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            pickupId = w.SpawnPickup(PickupKind.EnergyCell, pickupPos, 1);
            containerId = w.SpawnContainer(ContainerKind.Crate, containerPos,
                containerItems ?? new byte[] { 1 });
            w.ClearEvents();
            var asm = new SnapshotAssembler(cfg, net ?? Net(), connectionCount: 1);
            return (w, cfg, asm);
        }

        [Test]
        public void PickupsAndContainers_RideTheFrame_ByVisibilityAndNotOtherwise()
        {
            // CRITICAL RULE 4 for the two classes Т26 gave their own sets:
            // what the collector can see rides at full precision, what he
            // cannot is not in his frame at all. Until this task the blocks
            // existed in the codec and NOTHING wrote them — the sets were
            // computed and spent on nobody.
            (SimulationWorld w, SimConfig cfg, SnapshotAssembler asm) = GroundFixture(
                out int nearPickup, out int nearContainer,
                new float2(3f, 0f), new float2(0f, 3f));
            float outOfSight = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis + 20f;
            int farPickup = w.SpawnPickup(PickupKind.EnergyCell, new float2(outOfSight, 0f), 1);
            int farContainer = w.SpawnContainer(ContainerKind.Crate, new float2(0f, outOfSight),
                new byte[] { 1 });

            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.IsTrue(f.TryPickup(nearPickup, out SnapshotBlocks.PickupRecord near),
                "a pickup three meters away rides the frame");
            Assert.AreEqual(PickupKind.EnergyCell, near.Kind, "with its own kind");
            // Inside one quantization step of where it really lies — the step
            // computed here from the same two numbers the codec uses, never
            // quoted (the codec tests' own tolerance discipline).
            float posStep = 2f * cfg.Arena.Radius / ushort.MaxValue;
            Assert.Less(math.distance(near.Pos, new float2(3f, 0f)), posStep,
                "and its position, inside one quantization step");
            Assert.IsTrue(f.TryContainer(nearContainer, out SnapshotBlocks.ContainerRecord box),
                "so does a container three meters away");
            Assert.AreEqual(ContainerKind.Crate, box.Kind, "with its own kind");

            Assert.IsFalse(f.TryPickup(farPickup, out _),
                "and a pickup past sight is NOT in the frame — fog of war is not a client-side "
                + "filter (CRITICAL RULE 4)");
            Assert.IsFalse(f.TryContainer(farContainer, out _),
                "nor is a container past sight");
        }

        [Test]
        public void ContainerRecord_CarriesTheEmptyFlag_OfABoxAlreadyLooted()
        {
            // Spec §3.12: "already looted" is what a collector reads AT A
            // DISTANCE to decide whether the walk is worth it, which is why
            // the flag rides the Containers record — sent to everyone who can
            // see the box — rather than only in the ContainerSlots block, sent
            // only to whoever is already standing next to it
            // (ContainerRecord's own doc states the asymmetry).
            (SimulationWorld w, SimConfig cfg, SnapshotAssembler asm) = GroundFixture(
                out _, out int containerId, new float2(3f, 0f), new float2(0f, 3f),
                new byte[] { 1, 2 });

            AssembledFrame stocked = Build(asm, w, cfg, 0, 0, 0);
            Assert.IsTrue(stocked.TryContainer(containerId, out SnapshotBlocks.ContainerRecord full));
            Assert.IsFalse(full.IsEmpty, "witness: a box that still holds two items is not empty");

            Assert.IsTrue(w.TryTakeFromContainer(containerId, 0, out _), "test setup: take the first");
            Assert.IsTrue(w.TryTakeFromContainer(containerId, 1, out _), "test setup: and the second");

            AssembledFrame looted = Build(asm, w, cfg, 0, 0, 0);
            Assert.IsTrue(looted.TryContainer(containerId, out SnapshotBlocks.ContainerRecord empty));
            Assert.IsTrue(empty.IsEmpty,
                "a box whose every slot is empty says so on the wire — the flag is DERIVED from the "
                + "slots, so it cannot drift out of step with them");
        }

        // ---- Т27.E. The budget order (Р243) and truncation of three classes ----

        [Test]
        public void MobsAreBudgetedBeforePickupsAndContainers()
        {
            // Р243, corrected by findings C-3/D-19: an earlier reading of the
            // order put the ground litter ABOVE the mobs, so in a tight frame
            // cells and empty crates would push out the picture of the threat
            // the snapshot exists to carry. The assertion is the consequence:
            // when the room runs out, it runs out for the LITTER first, and
            // the mobs that fit are the ones that ride.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            // A crowd close enough to be seen, and litter closer still — so
            // proximity alone would rank the litter first if the order were
            // by distance rather than by class.
            for (int i = 0; i < 20; i++)
                w.SpawnMobForTest(MobType.Chaser, new float2(10f + i * 0.5f, 0f));
            for (int i = 0; i < 8; i++) w.SpawnPickup(PickupKind.EnergyCell, new float2(1f, i * 0.3f), 1);
            // Just OUTSIDE LootRadius, deliberately: a box within reach also
            // sends its interior, which Р243 budgets ahead of the mobs, and
            // this test is about the mobs against the litter — not about the
            // interiors (ContainerSlots_AreTruncatedFarthestFirst is).
            float pastReach = cfg.Loot.LootRadius + 1f;
            for (int i = 0; i < 4; i++)
                w.SpawnContainer(ContainerKind.Crate, new float2(-pastReach, i * 0.3f),
                    new byte[] { 1 });
            w.ClearEvents();

            // A cap with room for SOME mobs and nothing beyond them.
            int fixedPart = SnapshotWriter.HeaderBytes
                            + SnapshotWriter.SelfBlockBytes(0)
                            + SnapshotWriter.MatchBlockBytes()
                            + SnapshotWriter.PlayersBlockBytes(0)
                            + SnapshotWriter.LivenessBlockBytes()
                            + SnapshotWriter.WaveBlockBytes()
                            + SnapshotWriter.ContainerSlotsBlockBytes(0, 0)
                            + SnapshotWriter.MobsBlockBytes(0)
                            + SnapshotWriter.ContainersBlockBytes(0)
                            + SnapshotWriter.PickupsBlockBytes(0)
                            + SnapshotWriter.EventsBlockBytes(0, 0);
            const int mobsThatFit = 6;
            var asm = new SnapshotAssembler(cfg,
                Net(maxBytes: fixedPart + mobsThatFit * SnapshotBlocks.MobRecordBytes),
                connectionCount: 1);

            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.AreEqual(mobsThatFit, f.MobCount,
                "the mobs take the room first — every byte of it");
            Assert.AreEqual(0, f.PickupCount,
                "and the litter gets none: a pickup ahead of a mob is the ordering this decision "
                + "reversed (Р243)");
            Assert.AreEqual(0, f.ContainerCount, "the same for containers");
            Assert.IsTrue(f.Truncated, "and the header says the frame was cut");
        }

        /// The tightest frame the constructor will accept — its own ceiling,
        /// which is sized for the WIDEST fixed part (whole roster, fullest
        /// backpack). A cap below it is refused at construction, so this is
        /// the smallest byte budget a truncation fixture can legally ask for.
        static int TightestLegalCap(in SimConfig cfg)
            => SnapshotAssembler.FixedFrameBytes(cfg.Arena.MaxPlayers, cfg.Hero.MaxInventoryItems);

        /// The record room such a frame leaves a SOLO connection with an empty
        /// backpack: the cap minus the fixed part that frame actually spends.
        /// Spelled out from the calculators, never from FixedFrameBytes — a
        /// guard reading the production home would agree with a wrong home
        /// too (lesson 324).
        static int RoomInTightestFrame(in SimConfig cfg)
            => TightestLegalCap(in cfg)
               - (SnapshotWriter.HeaderBytes
                  + SnapshotWriter.SelfBlockBytes(0)
                  + SnapshotWriter.MatchBlockBytes()
                  + SnapshotWriter.PlayersBlockBytes(0)
                  + SnapshotWriter.LivenessBlockBytes()
                  + SnapshotWriter.WaveBlockBytes()
                  + SnapshotWriter.ContainerSlotsBlockBytes(0, 0)
                  + SnapshotWriter.MobsBlockBytes(0)
                  + SnapshotWriter.ContainersBlockBytes(0)
                  + SnapshotWriter.PickupsBlockBytes(0)
                  + SnapshotWriter.EventsBlockBytes(0, 0));

        [TestCase(VisibilityClass.Mobs)]
        [TestCase(VisibilityClass.Containers)]
        [TestCase(VisibilityClass.Pickups)]
        public void TruncationDropsFarthest_ForEachOfThreeClasses(VisibilityClass cls)
        {
            // Р217/Р268 item 4: the drop rule stops being mob-specific. Each
            // class is cut on its own, by the SAME rule — the farthest from
            // the viewpoint go first — so a collector never loses the crate at
            // his feet while keeping one across the arena.
            //
            // ONE CLASS PER CASE, and that is forced rather than chosen: the
            // classes share one byte budget in Р243's own order, so a frame
            // tight enough to cut the mobs leaves the litter nothing at all to
            // be cut FROM (which is what MobsAreBudgetedBeforePickupsAndContainers
            // above asserts). Each case therefore populates its own class only.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            int recordBytes = cls switch
            {
                VisibilityClass.Mobs => SnapshotBlocks.MobRecordBytes,
                VisibilityClass.Containers => SnapshotBlocks.ContainerRecordBytes,
                _ => SnapshotBlocks.PickupRecordBytes,
            };
            int fits = RoomInTightestFrame(in cfg) / recordBytes;
            int spawned = fits + 2;     // two more than the frame can hold
            Assert.Greater(fits, 0, "fixture premise: the tightest legal frame still holds records");

            // Placed at strictly growing distances, so "farthest first" has a
            // total order to cut along and the survivors are decidable here.
            var ids = new int[spawned];
            for (int i = 0; i < spawned; i++)
            {
                float2 pos = new float2(2f + i * 1.5f, 0f);
                ids[i] = cls switch
                {
                    VisibilityClass.Mobs => w.SpawnMobForTest(MobType.Chaser, pos),
                    VisibilityClass.Containers => w.SpawnContainer(ContainerKind.Crate, pos,
                        new byte[] { 1 }),
                    _ => w.SpawnPickup(PickupKind.EnergyCell, pos, 1),
                };
            }
            w.ClearEvents();

            var asm = new SnapshotAssembler(cfg, Net(maxBytes: TightestLegalCap(in cfg)),
                connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            int riding = cls switch
            {
                VisibilityClass.Mobs => f.MobCount,
                VisibilityClass.Containers => f.ContainerCount,
                _ => f.PickupCount,
            };
            Assert.AreEqual(fits, riding, $"{cls}: exactly as many records as the room holds");

            for (int i = 0; i < spawned; i++)
            {
                bool present = cls switch
                {
                    VisibilityClass.Mobs => f.ContainsMob(ids[i]),
                    VisibilityClass.Containers => f.TryContainer(ids[i], out _),
                    _ => f.TryPickup(ids[i], out _),
                };
                Assert.AreEqual(i < fits, present,
                    $"{cls}: the {i}-th nearest must {(i < fits ? "ride" : "be dropped")} — the "
                    + "farthest go first, for this class exactly as for the mobs");
            }

            Assert.IsTrue(f.Truncated, "the header says the frame was cut");
            Assert.AreEqual(spawned - fits, asm.StatsFor(0).DroppedEntities,
                "and the counter is per ENTITY, not per class: what went missing is what the "
                + "escalation threshold (Р280) is measured in");
        }

        // ---- Т27.F. ContainerSlots: only within arm's reach (Р238/Р277) ----

        [Test]
        public void ContainerSlots_RideOnlyForBoxesInsideLootRadius()
        {
            // Spec §3.12 tag 10: the interior of a box is sent only to a
            // collector inside LootRadius, while the box ITSELF is sent to
            // everyone who can see it. The two travel on different terms on
            // purpose — "what is in it" is what you ask when you are already
            // standing there, "is it worth the walk" is what you read from
            // across the room (ContainerRecord's own doc).
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            float reach = cfg.Loot.LootRadius;
            int atHand = w.SpawnContainer(ContainerKind.Crate, new float2(reach * 0.5f, 0f),
                new byte[] { 1, 2 });
            int acrossTheRoom = w.SpawnContainer(ContainerKind.Crate, new float2(reach + 5f, 0f),
                new byte[] { 3 });
            w.ClearEvents();

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.IsTrue(f.TryContainer(atHand, out _), "premise: both boxes are plainly visible");
            Assert.IsTrue(f.TryContainer(acrossTheRoom, out _), "premise: including the far one");

            Assert.AreEqual(1, f.SlotsCount,
                "exactly one box has its interior in the frame — the one within reach");
            Assert.IsTrue(f.TrySlots(atHand, out SnapshotBlocks.ContainerSlotsRecord slots),
                "and it is the near one");
            Assert.IsFalse(f.TrySlots(acrossTheRoom, out _),
                "a box outside LootRadius keeps its contents to itself — sending them would hand "
                + "every observer the whole floor's loot table (Р238)");
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, f.ItemsOf(in slots),
                "the near box's own items, in ascending slot order");
        }

        [Test]
        public void ContainerSlots_CarryTheOccupancyMask_AndOnlyTheOccupiedItems()
        {
            // Р277 (finding D-14): the MASK is the point, not a compaction.
            // LootOps.Take addresses a slot BY INDEX, so a compact "here are
            // the two it still holds" list would systematically disagree with
            // the server's own numbering after any partial looting — every
            // second Take refused as "slot empty" by construction rather than
            // by a race.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            int boxId = w.SpawnContainer(ContainerKind.Crate, new float2(cfg.Loot.LootRadius * 0.5f, 0f),
                new byte[] { 1, 2, 3 });
            // The MIDDLE slot is emptied, so a compact list and a masked one
            // disagree about what slot 2 holds — which is the whole point.
            Assert.IsTrue(w.TryTakeFromContainer(boxId, 1, out _), "test setup: empty the middle slot");
            w.ClearEvents();

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.IsTrue(f.TrySlots(boxId, out SnapshotBlocks.ContainerSlotsRecord record));
            Assert.AreEqual((byte)0b101, record.OccupancyMask,
                "bit i means slot i is occupied: slots 0 and 2 hold something, slot 1 does not");
            CollectionAssert.AreEqual(new byte[] { 1, 3 }, f.ItemsOf(in record),
                "and only the OCCUPIED slots' ids follow, in ascending slot order — the mask is what "
                + "maps them back onto slot numbers");

            Assert.IsTrue(f.TryContainer(boxId, out SnapshotBlocks.ContainerRecord box));
            Assert.IsFalse(box.IsEmpty,
                "and the record's own empty flag agrees with the mask — one derivation, not two");

            // A FULL box, to the LAST slot the mask can name (Т27 fix-round,
            // mutation G5): the fixture above fills four slots, so a mask
            // narrowed to four bits would have shipped it unchanged and no
            // test would have noticed. `MaxContainerSlots` is 8 precisely
            // because the mask is one byte, and this is what holds the two
            // together.
            var full = new byte[cfg.Arena.MaxContainerSlots];
            for (int i = 0; i < full.Length; i++) full[i] = (byte)(1 + i % 4);
            int fullBox = w.SpawnContainer(ContainerKind.Crate,
                new float2(0f, cfg.Loot.LootRadius * 0.5f), full);
            w.ClearEvents();

            AssembledFrame second = Build(asm, w, cfg, 0, 0, 0);
            Assert.IsTrue(second.TrySlots(fullBox, out SnapshotBlocks.ContainerSlotsRecord fullRecord));
            Assert.AreEqual(SnapshotBlocks.ContainerSlotsMaskWidth, cfg.Arena.MaxContainerSlots,
                "premise: the arena's slot cap IS the mask's width — the two are one number in two "
                + "files, and ArenaConfig's own [Range(1, 8)] says why");
            Assert.AreEqual((byte)0xFF, fullRecord.OccupancyMask,
                "every slot of a full box is named, up to the eighth — a narrower mask would drop "
                + "the top slots silently and the client would think the box half empty");
            CollectionAssert.AreEqual(full, second.ItemsOf(in fullRecord),
                "and every one of their items rides, in ascending slot order");
        }

        [Test]
        public void ContainerSlots_AreTruncatedFarthestFirst_RatherThanOverrunningTheFrame()
        {
            // Coordinator R-221: the spec puts ContainerSlots inside
            // LootRadius and says nothing about what happens when more boxes
            // are inside it than the frame can carry. Silence is not "cannot
            // happen" — Arena.MaxContainers is 64, and 64 full boxes would
            // need 704 B of a frame that has ~900 to spend on EVERYTHING. The
            // writer would then throw INSIDE a server tick, which is exactly
            // what the constructor's ceiling exists to make impossible.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            // Eight full boxes, all within arm's reach, all at growing
            // distances so the cut has a total order to follow.
            var full = new byte[] { 1, 2, 3, 4, 1, 2, 3, 4 };
            const int boxes = 8;
            var ids = new int[boxes];
            for (int i = 0; i < boxes; i++)
                ids[i] = w.SpawnContainer(ContainerKind.Crate,
                    new float2(cfg.Loot.LootRadius * (0.1f + 0.1f * i), 0f), full);
            w.ClearEvents();

            int cap = TightestLegalCap(in cfg);
            int room = RoomInTightestFrame(in cfg);
            int perBox = SnapshotBlocks.ContainerSlotsRecordHeaderBytes + full.Length;
            int fits = room / perBox;
            Assert.Greater(fits, 0, "fixture premise: at least one interior fits");
            Assert.Less(fits, boxes, "fixture premise: and not all of them do");

            var asm = new SnapshotAssembler(cfg, Net(maxBytes: cap), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.LessOrEqual(f.Bytes, cap, "the whole point: the frame stays inside its cap");
            Assert.LessOrEqual(f.SlotsCount, fits, "no more interiors than the room holds");
            // AND NEVER AN INTERIOR WITHOUT ITS BOX (Task 27 review,
            // Important-1). The interiors are reserved before the Containers
            // budget runs, so the number that actually ships is bounded by
            // BOTH: what the room held and what kept its own record.
            // ContainerSlots_NeverRideForABoxWhoseOwnRecordWasCut is the
            // dedicated witness; here it is asserted per box, against the
            // truncation this fixture is built to produce.
            for (int i = 0; i < boxes; i++)
            {
                bool hasRecord = f.TryContainer(ids[i], out _);
                bool hasInterior = f.TrySlots(ids[i], out _);
                Assert.IsFalse(hasInterior && !hasRecord,
                    $"box {i}: an interior may never ride without the box's own record — the "
                    + "client cannot anchor a u16 code the frame does not otherwise mention (Р278)");
                Assert.AreEqual(i < f.SlotsCount, hasInterior,
                    $"and the interiors that DO ride are the {f.SlotsCount} nearest — the farthest "
                    + "go first here exactly as for the record classes");
            }

            // AND AN INTERIOR THAT DID NOT FIT IS NOT A DROPPED ENTITY
            // (coordinator R-222). The counter feeds the escalation threshold
            // (Р280), which is about entities that stopped updating — a box
            // whose interior was cut is still in the frame, at its position,
            // with its own "already looted" flag, and the collector standing
            // over it asks for the rest on the reliable channel. What the
            // counter must show here is the CONTAINERS that lost their whole
            // record, and nothing else.
            Assert.AreEqual(boxes - f.ContainerCount, asm.StatsFor(0).DroppedEntities,
                "the counter is exactly the containers that lost their record — if the cut "
                + "interiors were counted too it would read higher, and Р280's threshold would "
                + "escalate on something delta-snapshots do not fix");
            Assert.Greater(boxes - fits, 0,
                "premise: interiors really were cut, so the two counts genuinely differ");
        }

        // ---- Т27 review: findings Important-1, -3, -4 and Minor-8 ----

        [Test]
        public void ContainerSlots_NeverRideForABoxWhoseOwnRecordWasCut()
        {
            // Task 27 review, Important-1. The interiors are budgeted AHEAD of
            // every record class (Р243), so a frame can reserve room for a
            // box's contents and then fail to fit the box's own Containers
            // record — mobs are budgeted in between and take what they need.
            // Such an interior is UNANCHORABLE by construction: Р278 forbids
            // the receiver to carry a u16 entity code from one frame to the
            // next, so a ContainerSlots record naming a box this frame never
            // mentions is bytes spent on nothing, out of the highest-priority
            // class in the whole budget.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            // One box within reach, and a crowd of mobs that will eat every
            // byte the interiors did not already reserve.
            int boxId = w.SpawnContainer(ContainerKind.Crate,
                new float2(cfg.Loot.LootRadius * 0.5f, 0f), new byte[] { 1, 2, 3, 4 });
            for (int i = 0; i < 20; i++)
                w.SpawnMobForTest(MobType.Chaser, new float2(6f + i * 0.5f, 0f));
            w.ClearEvents();

            var asm = new SnapshotAssembler(cfg, Net(maxBytes: TightestLegalCap(in cfg)),
                connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.IsFalse(f.TryContainer(boxId, out _),
                "fixture premise: the mobs really did crowd the box's own record out of the frame");
            Assert.AreEqual(0, f.SlotsCount,
                "so its interior must not ride either — a ContainerSlots record whose Containers "
                + "record was cut names a box the client cannot anchor (Р278), and the bytes belong "
                + "to whoever comes next in the budget");

            // The counterfactual, so the assertion above is not merely a
            // consequence of the frame being tight: given room for the box's
            // own record, the SAME world ships both halves.
            var roomy = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame full = Build(roomy, w, cfg, 0, 0, 0);
            Assert.IsTrue(full.TryContainer(boxId, out _), "witness: with room, the box rides");
            Assert.IsTrue(full.TrySlots(boxId, out _), "and its interior rides with it");
        }

        [Test]
        public void OrphanedInteriorRefundsItsBytes_AndTheEventsGetThem()
        {
            // The other half of the Important-1 fix, and the one a surviving
            // mutant asked for: an interior reserves its bytes BEFORE the
            // Containers budget runs (Р243 ranks it above everything), so when
            // its box loses its own record the reservation has to come back.
            // Otherwise the frame silently shrinks — bytes taken from the
            // highest-priority class and given to nobody. The events are next
            // in line for them, so the events are the witness.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            // One box within reach carrying four items: its interior costs
            // 3 + 4 = 7 B, exactly what its own Containers record would.
            var items = new byte[] { 1, 2, 3, 4 };
            int boxId = w.SpawnContainer(ContainerKind.Crate,
                new float2(cfg.Loot.LootRadius * 0.5f, 0f), items);
            int interior = SnapshotBlocks.ContainerSlotsRecordHeaderBytes + items.Length;

            // Mobs sized so that after the interior's reservation they leave
            // LESS than one container record — the box loses its own record,
            // which is what makes its interior an orphan.
            int room = RoomInTightestFrame(in cfg);
            int mobs = (room - interior) / SnapshotBlocks.MobRecordBytes;
            for (int i = 0; i < mobs; i++)
                w.SpawnMobForTest(MobType.Chaser, new float2(6f + i * 0.5f, 0f));
            int leftWithoutRefund = room - interior - mobs * SnapshotBlocks.MobRecordBytes;
            Assert.Less(leftWithoutRefund, SnapshotBlocks.ContainerRecordBytes,
                "fixture premise: the box cannot fit its own record, so its interior is orphaned");

            // One narrow event — 9 B of header and 2 B of payload. It does NOT
            // fit what is left without the refund, and DOES fit with it.
            w.ClearEvents();
            w.Emit(SimEventKind.WaveStarted, float2.zero, 1, MobType.Chaser, 0f);
            int eventBytes = SnapshotBlocks.EventHeaderBytes
                             + SnapshotEvents.PayloadBytesFor(SnapshotEventKind.WaveStarted);
            Assert.Less(leftWithoutRefund, eventBytes,
                "fixture premise: without the refund the event has nowhere to go");
            Assert.GreaterOrEqual(leftWithoutRefund + interior, eventBytes,
                "fixture premise: and with the refund it fits exactly");

            var asm = new SnapshotAssembler(cfg, Net(maxBytes: TightestLegalCap(in cfg)),
                connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.AreEqual(0, f.SlotsCount, "premise: the orphaned interior really was dropped");
            Assert.IsFalse(f.TryContainer(boxId, out _), "premise: and its box has no record either");
            Assert.AreEqual(1, f.CountOf(SnapshotEventKind.WaveStarted),
                "the bytes the orphan gave back went to the event — an interior that reserved room "
                + "and shipped nothing must not keep the room");
        }

        [Test]
        public void EveryClassAtItsCap_RidesWhole_WhenTheFrameHasRoom()
        {
            // Task 27 review, Important-3. `CandidateList.Add` refuses
            // silently once full, and the refusal is invisible: no exception,
            // no counter, no truncation bit. It is unreachable by
            // construction — each list is sized to its own arena cap, and a
            // candidate only exists for an entity the capture still holds —
            // but "unreachable" is a claim about capacities, and this is what
            // checks it rather than asserting it in prose: fill all three
            // classes to their caps and require every single entity in the
            // frame.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            // Everything close enough to be seen, so visibility is not what
            // this test is measuring.
            // bd app-3cph: the SPACING is derived from SightRadius instead of
            // the old 0.05 m literal. Each class is laid along its own axis
            // starting 5 m out, so the last entity sat at 5 + (cap - 1) * step
            // — fine while the caps were 288/64/256 (19.4 m at worst) and
            // false the moment the В1 playtest doubled the mob density: at
            // MaxMobs 1350 the tail reached 72 m, well past SightRadius 45,
            // and 549 of them were filtered out by the very visibility this
            // fixture exists to take out of the picture.
            float step = (cfg.Visibility.SightRadius - 6f)
                         / math.max(cfg.Arena.MaxMobs,
                             math.max(cfg.Arena.MaxContainers, cfg.Arena.MaxPickups));
            for (int i = 0; i < cfg.Arena.MaxMobs; i++)
                w.SpawnMobForTest(MobType.Chaser, new float2(5f + i * step, 0f));
            for (int i = 0; i < cfg.Arena.MaxContainers; i++)
                w.SpawnContainer(ContainerKind.Crate, new float2(-5f - i * step, 0f),
                    new byte[] { 1 });
            for (int i = 0; i < cfg.Arena.MaxPickups; i++)
                w.SpawnPickup(PickupKind.EnergyCell, new float2(0f, 5f + i * step), 1);
            w.ClearEvents();
            Assert.AreEqual(cfg.Arena.MaxMobs, w.MobCount, "test setup: the mob cap is full");
            Assert.AreEqual(cfg.Arena.MaxContainers, w.ContainerCount, "…and the container cap");
            Assert.AreEqual(cfg.Arena.MaxPickups, w.PickupCount, "…and the pickup cap");

            // A cap with room for every record of every class, so the only
            // thing that could lose an entity is a candidate list refusing it.
            int roomy = 4096
                        + cfg.Arena.MaxMobs * SnapshotBlocks.MobRecordBytes
                        + cfg.Arena.MaxContainers * SnapshotBlocks.ContainerRecordBytes
                        + cfg.Arena.MaxPickups * SnapshotBlocks.PickupRecordBytes;
            var asm = new SnapshotAssembler(cfg, Net(maxBytes: roomy), connectionCount: 1);
            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);

            Assert.AreEqual(cfg.Arena.MaxMobs, f.MobCount,
                "every mob the world holds is in the frame — a candidate list one entry short "
                + "would drop the last one silently");
            Assert.AreEqual(cfg.Arena.MaxContainers, f.ContainerCount, "and every container");
            Assert.AreEqual(cfg.Arena.MaxPickups, f.PickupCount, "and every pickup");
            Assert.IsFalse(f.Truncated, "nothing was cut for room, so nothing may claim it was");
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEntities, "and the counter agrees");
        }

        [TestCase(VisibilityClass.Containers)]
        [TestCase(VisibilityClass.Pickups)]
        public void EntityGoneFromTheWorld_LeavesTheFrameSilently_AndTheSetForgetsItToo(
            VisibilityClass cls)
        {
            // Task 27 review, Important-4 — and the MEASUREMENT the review's
            // own account did not survive. The `slot < 0` branch was copied
            // from the mobs onto the two new classes, and the review asked for
            // a witness of "the set still lingers on an id the capture no
            // longer has". That state does not exist: `VisibilitySystem.
            // Compute*` walks the LIVE storage of the world and evaluates only
            // entities it finds there, so linger (Р19) keeps an entity that
            // left SIGHT, never one that left the WORLD. An id in `Current`
            // therefore implies the entity existed when the set was built.
            //
            // What IS worth pinning is the thing that really happens when a
            // container's TTL expires or a pickup is taken: the entity leaves
            // BOTH the set and the frame, in the same tick, without a
            // truncation bit and without a dropped-entity count — because
            // nothing was cut for room. The `slot < 0` guard stays as the
            // defense it is (see its own doc), for a capture and a set built
            // at different moments.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            var pos = new float2(0f, 6f);
            int id = cls == VisibilityClass.Containers
                ? w.SpawnContainer(ContainerKind.Crate, pos, new byte[] { 1 })
                : w.SpawnPickup(PickupKind.EnergyCell, pos, 1);
            w.ClearEvents();

            var asm = new SnapshotAssembler(cfg, Net(), connectionCount: 1);
            AssembledFrame seen = Build(asm, w, cfg, 0, 0, 0);
            bool presentFirst = cls == VisibilityClass.Containers
                ? seen.TryContainer(id, out _)
                : seen.TryPickup(id, out _);
            Assert.IsTrue(presentFirst, "test setup: the entity starts plainly visible");
            Assert.IsTrue(asm.VisibleSetFor(0, cls).Contains(id), "and its set knows it");

            if (cls == VisibilityClass.Containers) w.RemoveContainerAt(0);
            else w.RemovePickupAt(0);

            AssembledFrame after = Build(asm, w, cfg, 0, 0, 0);
            Assert.IsFalse(asm.VisibleSetFor(0, cls).Contains(id),
                "an entity gone from the WORLD is gone from the set the same tick — linger keeps "
                + "what left sight, not what stopped existing");
            bool presentAfter = cls == VisibilityClass.Containers
                ? after.TryContainer(id, out _)
                : after.TryPickup(id, out _);
            Assert.IsFalse(presentAfter, "so the frame carries no record for it either");
            Assert.IsFalse(after.Truncated,
                "this is not a truncation — nothing was dropped for ROOM, so the bit must stay clear");
            Assert.AreEqual(0, asm.StatsFor(0).DroppedEntities, "and nothing is counted as dropped");
        }

        [Test]
        public void MatchBlock_AfterTheRaidsLastTick_ReadsZeroSecondsRatherThanWrapping()
        {
            // Task 27 review, Minor-8. Of the clamps the countdown carries,
            // `max(0, matchMaxTicks - tick)` is the ONE that is reachable: a
            // frame built after the limit has passed is ordinary (the match
            // ends on the server's own decision, and frames keep being built
            // until it does). Without the floor the subtraction goes negative
            // and integer division would carry the sign onto the wire, where
            // the field is unsigned — a raid with 65 000 seconds left.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);

            NetConfig net = Net();
            net.MatchMaxDurationSeconds = 2;
            // A WHOLE SECOND past the limit, not a tick past it (fix-round,
            // mutation G6): integer division truncates toward zero, so a
            // deficit of one tick divides to 0 either way and a test built on
            // it would be blind to the floor it is checking. One full second
            // over is where the unfloored expression turns into -1 — and
            // `(ushort)(-1)` is 65535, a raid with eighteen hours left.
            int ticks = net.MatchMaxDurationTicks + net.TickRate + 1;
            var asm = new SnapshotAssembler(cfg, net, connectionCount: 1);
            TestWorlds.IdleTicks(w, ticks);
            Assert.Greater(w.CurrentTick - net.MatchMaxDurationTicks, net.TickRate,
                "test setup: the world is more than a second past the limit");

            AssembledFrame f = Build(asm, w, cfg, 0, 0, 0);
            Assert.AreEqual((ushort)0, f.MatchSecondsRemaining,
                "the countdown floors at zero — a negative remainder would ride the u16 field as "
                + "an enormous positive one");
        }

        [Test]
        public void VisibleSetFor_RefusesAnUnknownClass_RatherThanFallingBackToTheMobs()
        {
            // Task 26 review, Minor: the default arm's own message forbids the
            // one thing a missing default would do — hand back another class's
            // set. Unwitnessed, "return c.MobsCurrent" is a green mutation,
            // and a fourth entity class would then silently read the mobs' fog
            // of war as its own.
            SimulationWorld w = Trio(out SimConfig cfg, float2.zero, new float2(6f, 0f),
                new float2(0f, 8f));
            var asm = new SnapshotAssembler(in cfg, Net(), connectionCount: 1);
            Build(asm, w, cfg, connection: 0, identityIndex: 0, viewpointIndex: 0);

            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => asm.VisibleSetFor(0, (VisibilityClass)3),
                "a VisibilityClass with no set of its own must throw, not inherit the mobs'");
        }
    }
}
