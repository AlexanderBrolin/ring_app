using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т32б: the five blocks Т25/Т27 put on the wire, landed into a
    /// render frame by `ClientFrameDecoder`.
    ///
    /// THE PAYLOADS ARE BUILT BY THE WRITER AND EXTRACTED BY THE READER, never
    /// hand-assembled here. The byte layout of each block is pinned once, by
    /// `SnapshotCodecTests`' own layout tests; restating it in this file would
    /// be a second copy of the format that could agree with the spec while
    /// disagreeing with the code (AGENT.md rule 2). What this fixture owns is
    /// the LANDING — what a payload becomes once it is inside a frame.
    ///
    /// `TestConfigs.Open()` throughout, the same fixture the loot tests use:
    /// its catalog carries ids 1-5, and item ids off the wire are validated
    /// against exactly that catalog.
    public class ClientFrameDecoderTests
    {
        const ushort Epoch = 7;
        const uint Tick = 4242;

        static SimConfig Cfg() => TestConfigs.Open();

        static RenderSnapshot Frame(in SimConfig cfg) => new RenderSnapshot(in cfg);

        /// Every kind this fixture ever writes, so `TryReadBlock` hands back
        /// the block it was asked for instead of stepping over it.
        static readonly byte[] AllKinds =
        {
            (byte)SnapshotBlockKind.Match,
            (byte)SnapshotBlockKind.Self,
            (byte)SnapshotBlockKind.Pickups,
            (byte)SnapshotBlockKind.Containers,
            (byte)SnapshotBlockKind.ContainerSlots,
        };

        /// A fresh buffer big enough for the header and ONE block of
        /// `blockBytes`, with the header already written. The caller appends
        /// its block and hands the result to `PayloadIn` below.
        ///
        /// TWO HELPERS RATHER THAN ONE TAKING A CALLBACK, because
        /// `SnapshotWriter` is a `ref struct`: it cannot be a type argument,
        /// so there is no delegate that could carry it into a helper.
        static byte[] Buffer(int blockBytes) => new byte[SnapshotWriter.HeaderBytes + blockBytes];

        /// The ONE block's payload the reader finds in `buffer` — the same span
        /// `NetworkSimBackend.ReadFrame` passes to a landing.
        static byte[] PayloadIn(byte[] buffer, int bytesWritten)
        {
            Assert.AreEqual(buffer.Length, bytesWritten,
                "fixture premise: the buffer holds the header and exactly one block");
            var reader = new SnapshotReader(buffer);
            Assert.IsTrue(reader.TryReadHeader(out _, out _, out _), "fixture premise: header parses");
            Assert.IsTrue(reader.TryReadBlock(AllKinds, out _, out System.ReadOnlySpan<byte> payload),
                "fixture premise: the block parses");
            return payload.ToArray();
        }

        static SnapshotWriter WriterOver(byte[] buffer)
        {
            var writer = new SnapshotWriter(buffer);
            writer.WriteHeader(Epoch, Tick, 0);
            return writer;
        }

        // ---------------------------------------------------------------- Match

        /// The phase and the countdown land, and the Director's bit becomes a
        /// DECODED bool — the border where a bit layout stops, exactly as the
        /// Liveness mask stops at `PlayerAliveInMatch`.
        [Test]
        public void Match_LandsPhaseCountdownAndDirectorBit()
        {
            SimConfig cfg = Cfg();
            RenderSnapshot f = Frame(in cfg);
            var buffer = Buffer(SnapshotWriter.MatchBlockBytes());
            SnapshotWriter writer = WriterOver(buffer);
            writer.WriteMatchBlock(MatchPhase.DirectorActive, 743, MatchWireFlags.DirectorAlive);
            byte[] payload = PayloadIn(buffer, writer.BytesWritten);

            Assert.IsTrue(ClientFrameDecoder.TryLandMatch(payload, f, out SnapshotBlockError error));
            Assert.AreEqual(SnapshotBlockError.None, error);
            Assert.AreEqual(MatchPhase.DirectorActive, f.Match.Phase);
            Assert.AreEqual(743, f.MatchSecondsRemaining,
                "the countdown lands as a number, replacing the frame's no-countdown sentinel");
            Assert.IsTrue(f.DirectorAlive, "the DirectorAlive bit is spread into a decoded bool");
        }

        /// The gate is answered by the PHASE and by nothing else. The wire
        /// carries a GateOpen bit too, and the wire's own doc names it a
        /// convenience view whose disagreements are resolved in the phase's
        /// favor — so this frame deliberately has no second field for it, and
        /// this test is what would fail if one were ever added.
        [Test]
        public void Match_GateIsReadFromThePhase_NotFromASecondField()
        {
            SimConfig cfg = Cfg();
            RenderSnapshot f = Frame(in cfg);
            // A HOSTILE PAIR: the flags byte says the gate is open while the
            // phase says the Director is still up. A landing that mirrored the
            // bit would carry the contradiction into the frame.
            var buffer = Buffer(SnapshotWriter.MatchBlockBytes());
            SnapshotWriter writer = WriterOver(buffer);
            writer.WriteMatchBlock(MatchPhase.DirectorActive, 10, (byte)(MatchWireFlags.GateOpen | MatchWireFlags.DirectorAlive));
            byte[] payload = PayloadIn(buffer, writer.BytesWritten);

            Assert.IsTrue(ClientFrameDecoder.TryLandMatch(payload, f, out _));
            Assert.AreEqual(MatchPhase.DirectorActive, f.Match.Phase,
                "the phase is the source of truth and the frame carries only it");
            Assert.AreNotEqual(MatchPhase.GateOpen, f.Match.Phase,
                "the flags byte does not get to overrule the phase");
        }

        /// A phase byte outside the domain is refused WHOLE, and the frame is
        /// left as it was — Р82's rule that a decoder answers rather than
        /// throws, and this class's rule that a refusal writes nothing.
        [Test]
        public void Match_IllegalPhase_IsRefusedAndWritesNothing()
        {
            SimConfig cfg = Cfg();
            RenderSnapshot f = Frame(in cfg);
            var payload = new byte[] { 200, 0, 0, 0 };

            Assert.IsFalse(ClientFrameDecoder.TryLandMatch(payload, f, out SnapshotBlockError error));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
            Assert.AreEqual(MatchCountdown.None, f.MatchSecondsRemaining,
                "a refused block leaves the frame's no-countdown sentinel standing");
        }

        // ----------------------------------------------------------------- Self

        /// The owner's pack lands: the ids in order, their count, and the slot
        /// points they cost.
        [Test]
        public void Self_LandsTheBackpackAndItsSlotPoints()
        {
            SimConfig cfg = Cfg();
            RenderSnapshot f = Frame(in cfg);
            var items = new byte[] { 3, 1 };
            var buffer = Buffer(SnapshotWriter.SelfBlockBytes(items.Length));
            SnapshotWriter writer = WriterOver(buffer);
            writer.WriteSelfBlock(4, items);
            byte[] payload = PayloadIn(buffer, writer.BytesWritten);
            var scratch = new byte[cfg.Hero.MaxInventoryItems];

            Assert.IsTrue(ClientFrameDecoder.TryLandSelf(payload, in cfg, scratch, f, out _));
            Assert.AreEqual(2, f.InventoryItemCount);
            Assert.AreEqual(3, f.InventoryItems[0], "the ids land in the order they were sent…");
            Assert.AreEqual(1, f.InventoryItems[1], "…and the second is not the first");
            Assert.AreEqual(4, f.InventorySlotPoints);
        }

        /// A frame the ring declined a slot to is still PARSED, and this is
        /// the half of that contract with teeth: the malformed case.
        ///
        /// A LANDING THAT SHORT-CIRCUITED ON `slot == null` would answer true
        /// to anything, and `ReadFrame` counts a frame incomplete on these
        /// answers — so a datagram cut mid-block on a duplicate tick would be
        /// reported healthy. The well-formed half is asserted beside it
        /// because it is the case that must NOT be refused; on its own it
        /// would pass for a decoder that never looked at a byte.
        [Test]
        public void Self_NullSlot_StillParsesAndStillRefusesMalformed()
        {
            SimConfig cfg = Cfg();
            var scratch = new byte[cfg.Hero.MaxInventoryItems];

            var buffer = Buffer(SnapshotWriter.SelfBlockBytes(2));
            SnapshotWriter writer = WriterOver(buffer);
            writer.WriteSelfBlock(2, new byte[] { 1, 2 });
            byte[] wellFormed = PayloadIn(buffer, writer.BytesWritten);
            Assert.IsTrue(ClientFrameDecoder.TryLandSelf(wellFormed, in cfg, scratch, null, out _),
                "a declined frame's healthy block is not a refusal");

            // The header says two items and one byte follows: a datagram cut
            // on a record boundary parses as a shorter, valid-looking block
            // everywhere except here.
            var truncated = new byte[] { 1, 2, 1 };
            Assert.IsFalse(ClientFrameDecoder.TryLandSelf(truncated, in cfg, scratch, null,
                    out SnapshotBlockError error),
                "and a malformed one is refused whether or not anybody wanted its state");
            Assert.AreEqual(SnapshotBlockError.MalformedLength, error);
        }

        /// An item id the catalog does not know refuses the WHOLE block. The
        /// wire is untrusted and `ItemCatalogLookup.Find` throws on an unknown
        /// id, so an unchecked byte would become an exception inside whichever
        /// consumer resolved it first.
        [Test]
        public void Self_UnknownItemId_IsRefusedWhole()
        {
            SimConfig cfg = Cfg();
            RenderSnapshot f = Frame(in cfg);
            // slotPoints, count, then one id outside the fixture's 1-5 catalog.
            var payload = new byte[] { 1, 1, 200 };
            var scratch = new byte[cfg.Hero.MaxInventoryItems];

            Assert.IsFalse(ClientFrameDecoder.TryLandSelf(payload, in cfg, scratch, f,
                out SnapshotBlockError error));
            Assert.AreEqual(SnapshotBlockError.MalformedContent, error);
            Assert.AreEqual(0, f.InventoryItemCount, "nothing of a refused block reaches the frame");
        }

        // -------------------------------------------------------------- Pickups

        /// Pickups land as a dense list keyed by id, and the two facts the wire
        /// does NOT carry stay at zero rather than being invented.
        [Test]
        public void Pickups_LandDense_AndDoNotInventAmountOrTtl()
        {
            SimConfig cfg = Cfg();
            RenderSnapshot f = Frame(in cfg);
            var records = new[]
            {
                new SnapshotBlocks.PickupRecord
                {
                    Id = 91, Kind = PickupKind.EnergyCell, Pos = new float2(3f, -4f),
                },
                new SnapshotBlocks.PickupRecord
                {
                    Id = 92, Kind = PickupKind.EnergyCell, Pos = new float2(-5f, 6f),
                },
            };
            var buffer = Buffer(SnapshotWriter.PickupsBlockBytes(records.Length));
            SnapshotWriter writer = WriterOver(buffer);
            writer.WritePickupsBlock(records, cfg);
            byte[] payload = PayloadIn(buffer, writer.BytesWritten);
            var scratch = new SnapshotBlocks.PickupRecord[cfg.Arena.MaxPickups];

            Assert.IsTrue(ClientFrameDecoder.TryLandPickups(payload, in cfg, scratch, f, out _));
            Assert.AreEqual(2, f.PickupCount);
            Assert.AreEqual(91, f.Pickups[0].Id, "dense, in wire order");
            Assert.AreEqual(92, f.Pickups[1].Id);
            Assert.AreEqual(PickupKind.EnergyCell, f.Pickups[1].Kind);
            Assert.AreEqual(0, f.Pickups[0].Amount,
                "Amount is not on the wire — an invented magnitude is worse than an honest zero");
            Assert.AreEqual(0f, f.Pickups[0].Ttl, "…and neither is Ttl");
        }

        // ------------------------------------------------------------ Containers

        /// The box's metadata lands in the state array while "already emptied"
        /// lands BESIDE it, because `ContainerState` is hashed and this is a
        /// decoded fact rather than world state.
        [Test]
        public void Containers_LandMetadata_AndEmptinessBesideIt()
        {
            SimConfig cfg = Cfg();
            RenderSnapshot f = Frame(in cfg);
            var records = new[]
            {
                new SnapshotBlocks.ContainerRecord
                {
                    Id = 401, Kind = ContainerKind.Crate, IsEmpty = false, Pos = new float2(2f, 2f),
                },
                new SnapshotBlocks.ContainerRecord
                {
                    Id = 402, Kind = ContainerKind.Cache, IsEmpty = true, Pos = new float2(-2f, 8f),
                },
            };
            var buffer = Buffer(SnapshotWriter.ContainersBlockBytes(records.Length));
            SnapshotWriter writer = WriterOver(buffer);
            writer.WriteContainersBlock(records, cfg);
            byte[] payload = PayloadIn(buffer, writer.BytesWritten);
            var scratch = new SnapshotBlocks.ContainerRecord[cfg.Arena.MaxContainers];

            Assert.IsTrue(ClientFrameDecoder.TryLandContainers(payload, in cfg, scratch, f, out _));
            Assert.AreEqual(2, f.ContainerCount);
            Assert.AreEqual(401, f.Containers[0].Id);
            Assert.AreEqual(ContainerKind.Cache, f.Containers[1].Kind,
                "the kind is the box's own, not the first record's");
            Assert.IsFalse(f.ContainerIsEmpty[0], "the full box reads as not emptied…");
            Assert.IsTrue(f.ContainerIsEmpty[1], "…and the looted one does — at a distance");
        }

        // -------------------------------------------------------- ContainerSlots

        /// The interior lands in the frame's own flat pool, and the offsets are
        /// REWRITTEN to address it.
        ///
        /// THIS IS THE TEST THE POOL EXISTS FOR. A decoded record's ItemOffset
        /// points into the block payload — bytes belonging to a datagram the
        /// frame outlives — so a landing that copied the offset across would
        /// leave the window reading whatever those indices mean in the pool.
        /// The fixture makes the two DIFFERENT by sending two boxes: the second
        /// record's payload offset is past the first record's header, while its
        /// pool offset is simply the count of items already pooled.
        [Test]
        public void ContainerSlots_LandInThePool_WithOffsetsRewritten()
        {
            SimConfig cfg = Cfg();
            RenderSnapshot f = Frame(in cfg);
            var itemPool = new byte[] { 1, 2, 3 };
            var records = new[]
            {
                new SnapshotBlocks.ContainerSlotsRecord
                {
                    Id = 501, OccupancyMask = 0b0000_0011, ItemOffset = 0,
                },
                new SnapshotBlocks.ContainerSlotsRecord
                {
                    Id = 502, OccupancyMask = 0b0000_0100, ItemOffset = 2,
                },
            };
            var buffer = Buffer(SnapshotWriter.ContainerSlotsBlockBytes(2, 3));
            SnapshotWriter writer = WriterOver(buffer);
            writer.WriteContainerSlotsBlock(records, itemPool);
            byte[] payload = PayloadIn(buffer, writer.BytesWritten);
            var scratch = new SnapshotBlocks.ContainerSlotsRecord[cfg.Arena.MaxContainers];

            Assert.IsTrue(ClientFrameDecoder.TryLandContainerSlots(payload, in cfg, scratch, f,
                out _));
            Assert.AreEqual(2, f.ContainerInteriorCount);
            Assert.AreEqual(3, f.ContainerInteriorItemCount, "three items pooled across two boxes");

            ContainerInterior first = f.ContainerInteriors[0];
            ContainerInterior second = f.ContainerInteriors[1];
            Assert.AreEqual(501, first.Id);
            Assert.AreEqual(2, first.ItemCount, "popcount of the mask, carried rather than recomputed");
            Assert.AreEqual(0, first.ItemOffset);
            Assert.AreEqual(502, second.Id);
            Assert.AreEqual(1, second.ItemCount);
            Assert.AreEqual(2, second.ItemOffset,
                "the SECOND box's items start where the first box's ended — a pool index, not the "
                + "payload index the wire record carried");

            Assert.AreEqual(1, f.ContainerInteriorItems[0], "box 501, slot 0");
            Assert.AreEqual(2, f.ContainerInteriorItems[1], "box 501, slot 1");
            Assert.AreEqual(3, f.ContainerInteriorItems[2], "box 502, its single occupied slot");
        }

        /// A frame that declares more items than the pool can hold is REFUSED,
        /// not thrown out of (Р82).
        ///
        /// THE CASE IS REACHABLE, not theoretical: the wire's occupancy mask is
        /// eight bits wide whatever `MaxContainerSlots` is, so a world
        /// configured with fewer slots per box has a pool smaller than the
        /// wire can describe. The fixture shrinks the caps to one container of
        /// one slot and sends a box claiming two.
        [Test]
        public void ContainerSlots_MoreItemsThanThePoolHolds_IsRefusedNotThrown()
        {
            SimConfig cfg = Cfg();
            cfg.Arena.MaxContainers = 1;
            cfg.Arena.MaxContainerSlots = 1;
            RenderSnapshot f = Frame(in cfg);
            Assert.AreEqual(1, f.ContainerInteriorItems.Length,
                "fixture premise: the pool holds one item, and the wire may declare eight");

            var records = new[]
            {
                new SnapshotBlocks.ContainerSlotsRecord
                {
                    Id = 601, OccupancyMask = 0b0000_0011, ItemOffset = 0,
                },
            };
            var buffer = Buffer(SnapshotWriter.ContainerSlotsBlockBytes(1, 2));
            SnapshotWriter writer = WriterOver(buffer);
            writer.WriteContainerSlotsBlock(records, new byte[] { 1, 2 });
            byte[] payload = PayloadIn(buffer, writer.BytesWritten);
            var scratch = new SnapshotBlocks.ContainerSlotsRecord[8];

            Assert.IsFalse(ClientFrameDecoder.TryLandContainerSlots(payload, in cfg, scratch, f,
                out SnapshotBlockError error));
            Assert.AreEqual(SnapshotBlockError.DestinationTooSmall, error);
            Assert.AreEqual(0, f.ContainerInteriorCount, "nothing of a refused block reaches the frame");
        }
    }
}
