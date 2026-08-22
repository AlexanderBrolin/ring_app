using Ring.Networking.Protocol;
using Ring.Simulation.Core;

namespace Ring.Networking.Client
{
    /// The five snapshot blocks Stage 3 put on the wire, landed into the
    /// render frame they describe (Т32б, spec §3.12 tags 6-10).
    ///
    /// IT LIVES HERE, BESIDE `ClientEventDecoder`, SO THAT IT CAN BE TESTED —
    /// the owner's decision of 2026-08-10, applied to the same situation a
    /// second time. Т32б first wrote these five as private methods of
    /// `NetworkSimBackend`, and every branch of them was unreachable by a unit
    /// test there: that class takes a live `NetworkManager` in its constructor
    /// and refuses a null one, so no EditMode fixture can build it (bd
    /// `app-xkir`). Nothing about the landing needs a `NetworkManager`, a scene
    /// or a connection — it is a function of a payload, the match's config and
    /// a preallocated frame.
    ///
    /// THE LOGGING STAYS WITH THE CALLER, for the reason `ClientEventDecoder`
    /// gives about its own side effects: a refusal LINE is the backend's to
    /// write (it owns the logger and the one-line-per-block rule that keeps
    /// the receive path free of garbage), while the refusal itself is a value
    /// this class returns.
    ///
    /// A NULL `slot` IS ORDINARY, NOT AN ERROR. The ring declines a slot to a
    /// duplicate or an out-of-window frame, and such a frame still has to be
    /// PARSED — a malformed one is malformed whether or not its state was
    /// wanted, and the caller counts the frame incomplete on that basis. So
    /// every method below validates first and only then asks whether there is
    /// anywhere to write.
    ///
    /// THE SCRATCH IS THE CALLER'S, for the same reason `SnapshotBlocks` takes
    /// its destinations as parameters: nothing on the receive path may
    /// allocate, so the buffers are sized once from the config that sized the
    /// frame and handed in.
    public static class ClientFrameDecoder
    {
        /// The raid's own flow: phase, what is left of the clock, and whether
        /// the Director is still standing (spec §3.12 tag 6).
        ///
        /// THE FLAGS BYTE STOPS HERE. `DirectorAlive` is spread into a decoded
        /// bool exactly as the Liveness mask is spread into
        /// `PlayerAliveInMatch`, so that nothing above this border has to know
        /// a bit layout — and `Ring.Presentation`, which draws the phase line,
        /// has no reference to the assembly `MatchWireFlags` lives in (Р180).
        /// The GATE bit is deliberately NOT carried across: it is a
        /// convenience view of the phase, the wire's own doc names the phase
        /// the source of truth, and a consumer told to believe the phase when
        /// the two disagree can only do so if the phase is all it was given.
        ///
        /// `MatchState.DirectorDeathTick` IS NOT ON THE WIRE and stays at its
        /// default, the same way the Wave block leaves the director's own
        /// bookkeeping alone: it is a server tick, and nothing this side draws
        /// reads it.
        public static bool TryLandMatch(System.ReadOnlySpan<byte> payload, RenderSnapshot slot,
            out SnapshotBlockError error)
        {
            if (!SnapshotBlocks.TryReadMatchBlock(payload, out MatchPhase phase,
                    out ushort secondsRemaining, out byte flags, out error))
                return false;

            if (slot == null) return true;
            slot.Match = new MatchState { Phase = phase };
            slot.MatchSecondsRemaining = secondsRemaining;
            slot.DirectorAlive = (flags & MatchWireFlags.DirectorAlive) != 0;
            return true;
        }

        /// This client's OWN backpack (spec §3.12 tag 7, Р276) — the item ids
        /// and the slot points they cost, and nothing else: every other fact
        /// about oneself is a `PlayerState` field and rides reconciliation.
        ///
        /// INTO SCRATCH FIRST, NEVER STRAIGHT INTO THE FRAME, and here that is
        /// load-bearing rather than a house style. A frame the ring declined a
        /// slot to still has to be parsed, and `TryReadSelfBlock` needs
        /// somewhere to put the ids while it does; writing them into the frame
        /// on screen would replace the backpack the window is drawing with a
        /// stale frame's contents while its count still described the old one.
        ///
        /// THE IDS ARE ALREADY CHECKED AGAINST THE CATALOG by the decoder, so
        /// what lands here resolves through `ItemCatalogLookup.Find` without
        /// turning one hostile packet into an exception (Р82).
        public static bool TryLandSelf(System.ReadOnlySpan<byte> payload, in SimConfig cfg,
            System.Span<byte> scratch, RenderSnapshot slot, out SnapshotBlockError error)
        {
            if (!SnapshotBlocks.TryReadSelfBlock(payload, in cfg, scratch,
                    out byte slotPoints, out int itemCount, out error))
                return false;

            if (slot == null) return true;
            for (int i = 0; i < itemCount; i++) slot.InventoryItems[i] = scratch[i];
            slot.InventorySlotPoints = slotPoints;
            slot.InventoryItemCount = itemCount;
            return true;
        }

        /// The ground pickups this client may see (spec §3.12 tag 8) — a DENSE
        /// list keyed by id, the contract the Mobs landing keeps too, because
        /// `ViewRegistry` diffs it to rent and retire views.
        ///
        /// `Amount` AND `Ttl` ARE LEFT AT ZERO, deliberately: neither is on the
        /// wire (the Pickups row is id, position and kind), and inventing a
        /// number would state a magnitude where the truth is unknown. The cell
        /// is drawn from its kind and its position.
        public static bool TryLandPickups(System.ReadOnlySpan<byte> payload, in SimConfig cfg,
            System.Span<SnapshotBlocks.PickupRecord> scratch, RenderSnapshot slot,
            out SnapshotBlockError error)
        {
            if (!SnapshotBlocks.TryReadPickupsBlock(payload, in cfg, scratch, out int count,
                    out error))
                return false;

            if (slot == null) return true;
            for (int i = 0; i < count; i++)
            {
                SnapshotBlocks.PickupRecord r = scratch[i];
                slot.Pickups[i] = new PickupState { Id = r.Id, Kind = r.Kind, Pos = r.Pos };
            }

            slot.PickupCount = count;
            return true;
        }

        /// The containers this client may see, as metadata (spec §3.12 tag 9):
        /// where the box is, what kind it is, and whether it has been emptied.
        /// Its INTERIOR is a different block on different terms — see
        /// `TryLandContainerSlots` below.
        ///
        /// `SlotCount` AND `Ttl` ARE LEFT AT ZERO for the reason the pickups
        /// landing gives: they are not on the wire. The consumer that would
        /// want a slot count is the inventory window, and it reads the
        /// interior's own occupancy mask, which carries the truth for the box
        /// it is showing.
        ///
        /// "ALREADY EMPTIED" LANDS BESIDE THE RECORD, not inside it —
        /// `RenderSnapshot.ContainerIsEmpty`, whose own doc gives the two
        /// reasons (`ContainerState` is hashed, and a frame delivers the
        /// answer rather than storing it).
        public static bool TryLandContainers(System.ReadOnlySpan<byte> payload, in SimConfig cfg,
            System.Span<SnapshotBlocks.ContainerRecord> scratch, RenderSnapshot slot,
            out SnapshotBlockError error)
        {
            if (!SnapshotBlocks.TryReadContainersBlock(payload, in cfg, scratch, out int count,
                    out error))
                return false;

            if (slot == null) return true;
            for (int i = 0; i < count; i++)
            {
                SnapshotBlocks.ContainerRecord r = scratch[i];
                slot.Containers[i] = new ContainerState { Id = r.Id, Kind = r.Kind, Pos = r.Pos };
                slot.ContainerIsEmpty[i] = r.IsEmpty;
            }

            slot.ContainerCount = count;
            return true;
        }

        /// The interiors of the boxes this client is standing over (spec §3.12
        /// tag 10, Р238) — into the frame's flat pool (owner decision R-253).
        ///
        /// THE OFFSETS ARE REWRITTEN, NOT COPIED, and that is why this is a
        /// loop rather than an array copy. A decoded
        /// `ContainerSlotsRecord.ItemOffset` points into the BLOCK PAYLOAD it
        /// was read from — bytes belonging to a datagram this frame outlives —
        /// while `ContainerInterior.ItemOffset` has to point into the frame's
        /// own pool, which is read a whole render frame later. Carrying the
        /// wire's offset across would be a pointer into freed meaning; the two
        /// sides of that field are exactly what `ContainerSlotsRecord`'s own
        /// doc separates.
        ///
        /// THE POOL IS MEASURED BEFORE A BYTE MOVES, and that guard is not
        /// decoration (Р82: a wire decoder throws on no byte). The frame's
        /// pool is sized `MaxContainers * MaxContainerSlots`, while the wire's
        /// occupancy mask is eight bits wide whatever `MaxContainerSlots`
        /// happens to be — the format's ceiling and the world's fact are
        /// deliberately not one home (R-235). So on a world configured with
        /// fewer than eight slots per box, a hostile frame can declare more
        /// items than this pool holds, and without the check the copy below
        /// would be an IndexOutOfRange inside the receive path.
        public static bool TryLandContainerSlots(System.ReadOnlySpan<byte> payload,
            in SimConfig cfg, System.Span<SnapshotBlocks.ContainerSlotsRecord> scratch,
            RenderSnapshot slot, out SnapshotBlockError error)
        {
            if (!SnapshotBlocks.TryReadContainerSlotsBlock(payload, in cfg, scratch, out int count,
                    out error))
                return false;

            if (slot == null) return true;

            int needed = 0;
            for (int i = 0; i < count; i++)
                needed += SnapshotBlocks.OccupiedSlotCount(scratch[i].OccupancyMask);
            if (needed > slot.ContainerInteriorItems.Length)
            {
                error = SnapshotBlockError.DestinationTooSmall;
                return false;
            }

            int pooled = 0;
            for (int i = 0; i < count; i++)
            {
                SnapshotBlocks.ContainerSlotsRecord r = scratch[i];
                int occupied = SnapshotBlocks.OccupiedSlotCount(r.OccupancyMask);
                slot.ContainerInteriors[i] = new ContainerInterior
                {
                    Id = r.Id,
                    OccupancyMask = r.OccupancyMask,
                    ItemOffset = pooled,
                    ItemCount = occupied,
                };
                for (int k = 0; k < occupied; k++)
                    slot.ContainerInteriorItems[pooled + k] = payload[r.ItemOffset + k];
                pooled += occupied;
            }

            slot.ContainerInteriorCount = count;
            slot.ContainerInteriorItemCount = pooled;
            return true;
        }
    }
}
