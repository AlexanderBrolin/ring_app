using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Loot
{
    /// The three server-side loot operations (spec §3.8, Р234). All three are
    /// executed by the SERVER, on a tick boundary, in arrival order; the
    /// client predicts none of them (CR 3).
    public enum LootOp : byte { Take = 0, Drop = 1, Use = 2 }

    /// Why a loot request was refused — one code per check, so §3.11's promise
    /// ("the refusal lights up on the slot the player pressed") is expressible.
    /// `None` is the zero value and means "legal right now".
    ///
    /// Named DashingOrSliding, not Dashing (plan errata E-7): the window is
    /// closed by BOTH a dash and a slide, and a name mentioning only one of
    /// them would read as an asymmetry the spec explicitly refuses.
    ///
    /// This is the type that travels to the client as LootResultNet.Code
    /// (spec §3.8, errata E-7) — a Ring.Simulation type on the wire, not a
    /// parallel networking enum that would have to be kept in step by hand.
    public enum LootRefusal : byte
    {
        None = 0, DeadOrExtracted = 1, WindowClosed = 2, UnknownOp = 3,
        NoSuchContainer = 4, SlotOutOfRange = 5, SlotEmpty = 6,
        InventoryIndexOutOfRange = 7, TooFar = 8, NotEnoughSlots = 9, Busy = 10,
        DashingOrSliding = 11,
    }

    /// Stage 3 Task 17 (spec §3.8): the ONE home of loot validation and of the
    /// transfer channel's own timer.
    ///
    /// WHAT THIS TASK OWNS AND WHAT IT DOES NOT (R-152). `Validate` answers
    /// for all three ops — checks 3 and 6 have nowhere else to live — but
    /// `Begin`/`Update` carry only what `Take` needs. Finishing a transfer
    /// (the re-check on the expiry tick, the per-slot race between two
    /// collectors) and `Drop` spawning a ground container are Т18; the repair
    /// kit's own channel is Т19; the wire, the movement slowdown, CanFire and
    /// the window sanitizer are Т20. None of that is invented here ahead of
    /// its own task.
    public static class LootOps
    {
        /// The ONE home of all nine server checks (spec §3.8, Р265). A PURE
        /// function: it mutates nothing, it only answers whether the operation
        /// is legal RIGHT NOW. That purity is what lets the same nine checks
        /// run twice — once when the request arrives (Begin's precondition)
        /// and once on the tick the transfer completes (Т18), which the spec
        /// requires because the container may have emptied and the collector
        /// may have walked away in between.
        ///
        /// ORDER IS THE SPEC'S, with one recorded ruling. Spec §3.8 lists
        /// "container exists and the slot is non-empty" (4) BEFORE "Slot in
        /// [0, SlotCount)" (5), which cannot be executed in that sequence:
        /// reading the slot's content without first bounding the index is the
        /// very cross-block read check 5 exists to stop (see
        /// SimulationWorld.TryTakeFromContainer's own ASSUMPTION doc, which
        /// addresses this task by name). Coordinator R-151: THE BOUND IS
        /// CHECKED BEFORE THE CONTENT, and the two refusals stay distinct
        /// codes exactly as the spec names them. This is not a departure from
        /// the spec — it is the only order in which both of its checks are
        /// executable.
        ///
        /// `slot` is the operation's ADDRESS and means different things per
        /// op: a container slot for `Take`, a backpack index for `Drop`/`Use`
        /// (spec §3.8's own signatures, and LootRequestNet carries one byte
        /// for both). `containerId` is read only by `Take`; the other two
        /// ignore it. Both arrive from an untrusted client, which is why every
        /// bound below is checked rather than assumed.
        ///
        /// `playerIndex` is NOT range-checked, same contract as
        /// SimulationWorld.PlayerAt: it is the server's own connection ->
        /// index mapping, not a wire value.
        public static LootRefusal Validate(SimulationWorld w, int playerIndex, LootOp op,
            int containerId, int slot, in SimInput input)
        {
            PlayerState p = w.PlayerAt(playerIndex);

            // (1) alive and not extracted. Both halves, not just Alive: an
            // extracted collector is not a corpse, and looting after walking
            // out would be looting from outside the run.
            if (!p.Alive || p.Extracted) return LootRefusal.DeadOrExtracted;

            // (2) the window flag is up in THIS tick's input (Р239, Р265
            // item 2). Without this check the whole price of looting — the
            // slowdown, the holstered weapon — is paid only by an honest
            // client, while a modified one loots at full speed and keeps
            // shooting. That is a CR 3 violation, not a cosmetic gap, which
            // is why the flag rides the input (predicted, identical on both
            // sides) rather than being a server-only field.
            if (!input.InventoryOpen) return LootRefusal.WindowClosed;

            // (3) a known op. An unknown one is refused out loud, not
            // silently ignored — a client that sends garbage gets an answer
            // it can show on the slot it pressed.
            if (op != LootOp.Take && op != LootOp.Drop && op != LootOp.Use)
                return LootRefusal.UnknownOp;

            // Read once, same idiom PickupSystem.Collect/ProjectileSystem.Update
            // use: Config's getter returns the struct BY VALUE.
            SimConfig cfg = w.Config;

            if (op == LootOp.Take)
            {
                // (4a) the container exists — BY ID, never by array position
                // (Р266). IndexOfContainer is the one home of that search,
                // shared with TryTakeFromContainer.
                int index = w.IndexOfContainer(containerId);
                if (index < 0) return LootRefusal.NoSuchContainer;
                ContainerState c = w.Containers[index];

                // (5) the slot is inside THIS container's own block —
                // BEFORE the byte is read (R-151, see the method doc).
                if (slot < 0 || slot >= c.SlotCount) return LootRefusal.SlotOutOfRange;

                // (4b) ...and it holds something. 0 means empty (spec Р229).
                // This is also the refusal the LOSER of a race gets: the
                // race is deliberately not blocked (С18/Р236), the second
                // collector simply finds the slot empty.
                byte itemId = w.ContainerSlotAt(index, slot);
                if (itemId == 0) return LootRefusal.SlotEmpty;

                // (7) within reach. Inclusive — the spec says "<= LootRadius".
                if (math.distance(p.Pos, c.Pos) > cfg.Loot.LootRadius) return LootRefusal.TooFar;

                // (8) the backpack can take it — slot POINTS, not item count
                // (and the hard MaxInventoryItems ceiling too; see
                // Inventory.CanAdd for why both answer with one code).
                if (!w.CanAddItem(playerIndex, itemId)) return LootRefusal.NotEnoughSlots;
            }
            else
            {
                // (6) the backpack index is in range and belongs to the
                // requester. "Belongs to" needs no check of its own: the
                // index is resolved against THIS player's own backpack and
                // no other is reachable from here.
                if (slot < 0 || slot >= w.InventoryCountOf(playerIndex))
                    return LootRefusal.InventoryIndexOutOfRange;
            }

            // (9) no transfer already running. Last of the nine, and shared
            // by all three ops: one channel per collector.
            if (p.LootTimer > 0f) return LootRefusal.Busy;

            // `Use` only (spec §3.8): neither dashing NOR sliding. Both close
            // the window, so gating on only one of them would be a hole —
            // the spec calls the asymmetry out by name.
            if (op == LootOp.Use && (p.DashTimer > 0f || p.SlideTimer > 0f))
                return LootRefusal.DashingOrSliding;

            return LootRefusal.None;
        }

        /// Opens the transfer channel: arms LootTimer from the target item's
        /// own tier and records what is being moved (spec §3.8, С20/Р235).
        /// ASSUMES Validate has already answered None for these arguments —
        /// this method re-checks nothing, exactly like every other
        /// "precondition already established" seam in this codebase.
        ///
        /// THE ITEM STAYS IN THE CONTAINER while the timer runs, deliberately:
        /// if the item moved now, two collectors starting at the same moment
        /// would each have "reserved" it, and the race the spec wants
        /// (С18/Р236 — unresolved until the last moment) would be decided by
        /// whoever pressed first instead.
        ///
        /// THE TARGET IS AN ID, NOT AN INDEX (Р266, finding D-17). Containers
        /// are swap-removed, so an index stored here would silently re-aim at
        /// a different container by the time the transfer finishes — and the
        /// completion re-check would not catch it, because all nine checks
        /// pass honestly on that stranger.
        ///
        /// Only `Take` has a transfer channel in this task (R-152): `Drop`'s
        /// ground container is Т18's and the repair kit's channel is Т19's
        /// (and it runs on RepairTimer, not this one). A silent no-op for
        /// those would read as an operation that happened, so they get a
        /// named refusal instead — same shape SimulationWorld.SpawnContainer
        /// (R-99) uses for a call it cannot honor.
        public static void Begin(SimulationWorld w, int playerIndex, LootOp op,
            int containerId, int slot)
        {
            if (op != LootOp.Take)
            {
                throw new System.ArgumentException(
                    $"LootOps.Begin: {op} has no transfer channel — only Take does. Drop's own " +
                    "ground container belongs to Т18 and Use's repair-kit channel (RepairTimer, " +
                    "not LootTimer) to Т19; whichever of those you are wiring, its behavior is " +
                    "not implemented here yet.", nameof(op));
            }

            SimConfig cfg = w.Config;
            byte itemId = w.ContainerSlotAt(w.IndexOfContainer(containerId), slot);
            ref PlayerState p = ref w.PlayerRef(playerIndex);
            p.LootTimer = LootTransferTimes.ForTier(
                ItemCatalogLookup.Find(itemId, cfg.Items).Tier, in cfg.Loot);
            p.LootTargetContainerId = containerId;
            p.LootTargetSlot = (byte)slot;
        }

        /// One tick of every running transfer channel. Called from
        /// SimulationWorld.TickAll between WaveSystem and ContainerStore
        /// (owner decision R-149 — see that call site's own comment).
        ///
        /// ⚠ THE EARLY EXIT IS LOAD-BEARING (coordinator R-150, guard class
        /// R-120). This runs on every tick of both golden scenarios, where
        /// nobody loots — every LootTimer is zero from the world's birth and
        /// SimInput.InventoryOpen has no wire bit until Т20. The inertness
        /// that keeps both pinned digests still is the NAMED exit below, taken
        /// before any container, catalog or distance is read, not an accident
        /// of "the code happens not to get that far". Same shape as
        /// SimulationWorld.SpawnPickup refusing a zero amount before
        /// _nextEntityId moves, and ContainerStore.Update skipping a permanent
        /// container before the decrement. Т18 hangs the completion work off
        /// the branch below, and it inherits this protection by construction.
        ///
        /// Ascending player index, like every other per-player pass here —
        /// which is also the tie-break Р267 asks for when two transfers
        /// complete on the same tick (Т18 relies on it).
        ///
        /// Damage does NOT interrupt a transfer (spec §3.8, in deliberate
        /// contrast with the extraction channel): looting under a wave is
        /// meant to be possible but expensive greed. Death does, in
        /// SimulationWorld.KillPlayer.
        public static void Update(SimulationWorld w)
        {
            for (int i = 0; i < w.PlayerCount; i++)
            {
                ref PlayerState p = ref w.PlayerRef(i);
                if (p.LootTimer <= 0f) continue; // ← the guard R-150 names
                p.LootTimer -= SimulationWorld.TickDt;
                if (p.LootTimer > 0f) continue;

                // The channel expired on this tick. Т18 completes the
                // transfer HERE — re-running Validate and moving the item —
                // before the channel is closed below.
                p.LootTimer = 0f;
                p.LootTargetContainerId = 0;
                p.LootTargetSlot = 0;
            }
        }
    }
}
