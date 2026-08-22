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
        // Stage 3 Task 19 (coordinator D-1, the twelfth check): Validate's
        // own 6b returns this when a `Use` request addresses an item whose
        // ItemDef.Kind is not RepairKit.
        ItemNotUsable = 12,
    }

    /// Stage 3 Tasks 17-18 (spec §3.8): the ONE home of loot validation, of
    /// the transfer channel's own timer, and of what happens when that timer
    /// runs out.
    ///
    /// WHAT LIVES HERE AND WHAT DOES NOT (R-152). Т17 stood up `Validate`'s
    /// nine checks (it answers for all three ops — checks 3 and 6 have nowhere
    /// else to live) plus `Begin`/`Update` for `Take`. Т18 added the other
    /// half: the completion re-check on the expiry tick, the per-slot race
    /// between two collectors that falls out of it, and `Drop`'s ground
    /// container. Т19 added the repair kit's own channel (it runs on
    /// RepairTimer, not LootTimer) — `Begin`'s `Use` branch, `Validate`'s
    /// twelfth check (ItemNotUsable), and `Update`'s second completion loop.
    /// Т28 built the wire on top of all of it — LootRequestNet/LootResultNet
    /// and their epoch gate live in Networking/Protocol/LootNet.cs, and
    /// SimulationWorld.TryBeginLoot is the one production caller of Validate
    /// and Begin below. Still elsewhere: SimEventKind's own loot entries are
    /// Т29. The wire, the movement slowdown,
    /// WeaponSystem.CanFire's `!InventoryOpen` term and the window
    /// sanitizer landed in Т20 — see SimInputSanitizer.Sanitize, InputCodec
    /// and WeaponSystem.CanFire's own docs for that half of the story; none
    /// of it lives here, this file only READS the flag (check 2 below).
    public static class LootOps
    {
        /// Whether a collector standing `distance` away from a container is
        /// close enough to loot it — the ONE home of spec Р238's reach test
        /// (Stage 3 Т32б).
        ///
        /// IT WAS WRITTEN TWICE BEFORE THIS METHOD EXISTED, and the third
        /// writer is what made one home mandatory rather than tidy.
        /// `Validate`'s check 7 spelled `math.distance(p.Pos, c.Pos) >
        /// cfg.Loot.LootRadius`; `SnapshotAssembler` spelled
        /// `Distance[i] > _cfg.Loot.LootRadius` over a distance it had already
        /// measured; and Т32б needs the same question a third time, to decide
        /// which interiors a LOCAL frame describes. The two spellings agreed —
        /// both inclusive at the boundary — but nothing held them together,
        /// and the day they disagree a client is refused a box the frame just
        /// showed him the inside of.
        ///
        /// INCLUSIVE AT THE BOUNDARY, because the spec says "<= LootRadius".
        /// Expressed as `<=` rather than as the negation of the `>` both call
        /// sites used, so the boundary reads the way the spec states it
        /// instead of the way a refusal happened to be phrased.
        public static bool WithinLootReach(float distance, in LootSimConfig loot)
            => distance <= loot.LootRadius;

        /// The same question asked of two positions, for callers that have not
        /// already measured the distance. One home, one comparison: this
        /// overload only supplies the measurement.
        public static bool WithinLootReach(in float2 collectorPos, in float2 containerPos,
            in LootSimConfig loot)
            => WithinLootReach(math.distance(collectorPos, containerPos), in loot);

        /// The ONE home of all nine server checks (spec §3.8, Р265). A PURE
        /// function: it mutates nothing, it only answers whether the operation
        /// is legal RIGHT NOW. That purity is what lets the same nine checks
        /// run twice — once when the request arrives (Begin's precondition)
        /// and once on the tick the transfer completes (Update below, Т18),
        /// which the spec requires because the container may have emptied and
        /// the collector may have walked away in between.
        ///
        /// Stage 3 Task 18 — THE SECOND CALLER EXISTS NOW, and it is what makes
        /// "one home" load-bearing rather than tidy: the completion path calls
        /// THIS function, not a lighter copy of some of its conditions. A
        /// second copy would drift the moment Т19/Т20 touch either one, and the
        /// checks it would be tempting to omit (7 and 4b) are exactly the two
        /// the spec names as the REASON the re-check exists.
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

                // (7) within reach — through the one home of Р238's test
                // (WithinLootReach above), which is also what the frame
                // builder and the local frame's interiors ask.
                if (!WithinLootReach(p.Pos, c.Pos, in cfg.Loot)) return LootRefusal.TooFar;

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

                // (6b) Use only (Stage 3 Task 19, coordinator D-1: the
                // twelfth check). The addressed item must actually BE a
                // repair kit — Validate has no other check of item KIND
                // anywhere, and without this a Use on a Trophy would open a
                // channel (RepairTimer armed by Begin) that heals nothing
                // and, on completion, finds nothing sensible to consume.
                // Placed immediately after (6) — the one place both the
                // index and the catalog are already in hand.
                if (op == LootOp.Use)
                {
                    byte usedItemId = w.InventoryItemAt(playerIndex, slot);
                    if (ItemCatalogLookup.Find(usedItemId, cfg.Items).Kind != ItemKind.RepairKit)
                        return LootRefusal.ItemNotUsable;
                }
            }

            // (9) no transfer AND no repair channel already running (Stage 3
            // Task 19, coordinator D-3): "one channel per collector" was
            // only half-enforced before this task — a running Use did not
            // block a new Take. Same code, same position, one extra term.
            if (p.LootTimer > 0f || p.RepairTimer > 0f) return LootRefusal.Busy;

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
        /// `Take` OPENS a channel; `Drop` does not (Stage 3 Task 18, owner
        /// ruling R-161) — it begins and ends inside this same tick. The
        /// asymmetry is not a shortcut: TransferSeconds is indexed by the tier
        /// of an item taken FROM A CONTAINER, while LootTargetContainerId/
        /// LootTargetSlot address a container a discard does not have, so a
        /// channel for `Drop` would need a sentinel in a field whose zero
        /// already means "no channel". Spec §3.17 keeps the discard as the
        /// cheap valve it is.
        ///
        /// `Use`'s repair-kit channel runs on RepairTimer, not this one
        /// (Stage 3 Task 19, spec §3.7) — armed below, its own branch,
        /// deliberately NOT remembering which backpack slot asked for it
        /// (coordinator D-2, see that branch's own doc). Begin stays the ONE
        /// entry point of all three ops (its signature takes `op` for
        /// exactly that reason); splitting Drop or Use out would push a game
        /// rule into Т28's networking switch. THAT CALLER NOW EXISTS and the
        /// prediction held: `MatchServer.OnLootRequest` forwards a wire `op`
        /// straight through `SimulationWorld.TryBeginLoot` and carries no
        /// switch of its own.
        public static void Begin(SimulationWorld w, int playerIndex, LootOp op,
            int containerId, int slot)
        {
            if (op == LootOp.Drop)
            {
                // Stage 3 Task 18 (owner ruling R-161). ASSUMES Validate has
                // already answered None, same contract as the Take path below:
                // check 6 has bounded `slot` against this player's own backpack
                // one moment ago on this same state, so TryRemoveItemAt cannot
                // refuse. No defensive branch for a case that cannot arise —
                // if you believe it can, that is a finding, not a reason for a
                // just-in-case guard.
                //
                // ORDER IS THE TRANSFER'S OWN: out of the backpack FIRST, into
                // the world second. Between the two lines the item exists in
                // exactly one place, never zero and never two — the same
                // invariant SimulationWorld.KillPlayer keeps when it copies the
                // whole backpack onto a corpse and then clears it (R-128).
                w.TryRemoveItemAt(playerIndex, slot, out byte dropped);
                // stackalloc, never `new byte[1]`: this is the shape all three
                // existing single-item spawns use (the memory core, a mob
                // corpse, and DamageMob's own drop) and the reason is the same
                // — a heap array here would be an allocation on a path Т28
                // now calls from the tick's own request handling
                // (SimulationWorld.TryBeginLoot -> Validate/Begin).
                System.Span<byte> one = stackalloc byte[1];
                one[0] = dropped;
                // ContainerKind.Ground gets its FIRST production producer here:
                // until now the only Ground container in the codebase was a
                // test fixture, and ContainerStore.InitialTtlFor's default
                // branch (Ttl = Loot.ContainerTtlSeconds) was waiting for it.
                // The discard therefore ages out on its own, with no TTL
                // machinery of this task's making (coordinator R-164: the
                // "empty" qualifier in spec §3.6 is descriptive prose, not a
                // condition — a ground container is never born empty in the
                // first place, exactly like a mob corpse).
                //
                // A refusal from SpawnContainer (Arena.MaxContainers reached,
                // R-99's own skip-and-count) would silently consume the item.
                // ACCEPTED, NOT OVERLOOKED (coordinator F-4): the identical
                // exposure is already accepted at all four existing spawns,
                // rolling the item back would be a branch the spec never asks
                // for, and §3.17 keeps the discard cheap on purpose. Recorded
                // as a debt with an owner decision attached, not fixed here.
                w.SpawnContainer(ContainerKind.Ground, w.PlayerAt(playerIndex).Pos, one);
                return;
            }
            if (op == LootOp.Use)
            {
                // Stage 3 Task 19 (spec §3.7, errata E-6/C-I7, coordinator
                // D-2): ASSUMES Validate has already answered None for these
                // arguments, same contract as Take/Drop above (including the
                // twelfth check above — the addressed item really is a
                // repair kit right now).
                //
                // DELIBERATELY DOES NOT REMEMBER `slot`. LootTargetContainerId/
                // LootTargetSlot belong to the transfer channel (their own
                // doc at the field says so) — writing Use's backpack index
                // there would give that field a second, disagreeing meaning
                // depending on which op opened the channel it rides beside.
                // The consequence, named here because the next reader will
                // otherwise "fix" it: the completion tick (Update below)
                // cannot re-check the SAME address — it re-finds a repair
                // kit by KIND instead (FindRepairKitSlot), weaker than the
                // Id-based protection Take gets against the same kind of
                // drift (Р266) and accepted for the same reason D-2 names:
                // consuming a DIFFERENT repair kit than the one the player
                // pointed at is still a repair kit, where a different
                // container is a different asset entirely.
                ref PlayerState pUse = ref w.PlayerRef(playerIndex);
                pUse.RepairTimer = w.Config.Loot.RepairKitChannelSeconds;
                return;
            }
            if (op != LootOp.Take)
            {
                throw new System.ArgumentException(
                    $"LootOps.Begin: {op} is not a recognized loot operation.", nameof(op));
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
        /// nobody loots — every LootTimer is zero from the world's birth, and
        /// the golden scripted input never raises SimInput.InventoryOpen at
        /// all (checked by grep against DeterminismTests.cs, Т20's own
        /// finding) — a fact about the SCENARIO, not about whether the flag
        /// has a wire bit (it does, since Т20). The inertness that keeps
        /// both pinned digests still is the NAMED exit below, taken
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
        /// THREE ASYMMETRIES, ALL DELIBERATE AND ALL RECORDED — a later reader
        /// will read any of them as an oversight and "fix" it unless they are
        /// named here together:
        ///
        /// (1) DAMAGE DOES NOT INTERRUPT a transfer (spec §3.8, in deliberate
        ///     contrast with the extraction channel): looting under a wave is
        ///     meant to be possible but expensive greed, and a channel any
        ///     stray round could cancel would make it impossible instead.
        ///     SimulationWorld.DamagePlayer touches none of the three loot
        ///     fields, and that is the implementation of this rule.
        /// (2) DEATH DOES, IMMEDIATELY, and not from here —
        ///     SimulationWorld.KillPlayer zeroes timer and target with the rest
        ///     of the corpse's clean read. A dead collector must not be left
        ///     holding a channel that a later tick would try to finish.
        /// (3) WALKING OUT OF REACH AND CLOSING THE WINDOW DO NEITHER: they
        ///     make the transfer FAIL TO COMPLETE, they do not break the
        ///     channel the moment they happen (owner ruling R-160). The checks
        ///     run ONCE, on the expiry tick. Spec §3.8's own justification for
        ///     having a re-check at all ("the container may have emptied,
        ///     the collector may have walked away") is a sentence ABOUT THE
        ///     EXPIRY TICK, and it says nothing at all under a polling
        ///     reading. The player sees the end of the
        ///     operation immediately anyway: §3.11 closes the window client-
        ///     side on leaving the radius, and the progress bar lives inside
        ///     that window.
        ///
        /// `inputs` is THIS tick's SANITIZED per-player input (Stage 3 Task 18,
        /// coordinator R-162). It arrives as a PARAMETER, the same way TickAll
        /// already hands WeaponSystem its own (`in _sanitizedInputs[i]`), not
        /// through a new world getter whose only consumer would be this one
        /// system — which also keeps looting an honest
        /// `(state, input) -> state` function (CR 2). Sanitized rather than
        /// raw because Т20's sanitizer forces the window flag back down
        /// inside a dash or a slide: the transfer's last look at the world
        /// must see exactly what movement saw.
        ///
        /// `inputs.Length` is NOT checked against PlayerCount, the same
        /// contract Validate's own `playerIndex` doc states and
        /// SimulationWorld.PlayerAt/ContainerSlotAt/Loot.Inventory.ItemAt
        /// already follow: this is the server's own per-player array, not a
        /// wire value. TickAll, the one production caller, passes
        /// _sanitizedInputs, sized to playerCount at construction.
        public static void Update(SimulationWorld w, System.ReadOnlySpan<SimInput> inputs)
        {
            for (int i = 0; i < w.PlayerCount; i++)
            {
                ref PlayerState p = ref w.PlayerRef(i);
                if (p.LootTimer <= 0f) continue; // ← the guard R-150 names
                p.LootTimer -= SimulationWorld.TickDt;
                if (p.LootTimer > 0f) continue;

                // ⚠ THE DECREMENT ABOVE MUST STAY BEFORE THIS CALL. Check 9
                // answers Busy while LootTimer > 0, so a re-check run one line
                // earlier would refuse EVERY transfer this system ever starts —
                // silently, permanently, and with every negative test still
                // green. On the expiry tick the timer is already <= 0, which is
                // precisely what lets the same nine checks run a second time.
                //
                // The nine are re-run through the SAME function, never a
                // lighter copy (rule 2, and see Validate's own doc): the
                // container may have emptied and the collector may have walked
                // away or shut the window since Begin (spec §3.8).
                //
                // A REFUSAL CANCELS THE OPERATION SILENTLY: no exception, no
                // event, nothing moves — the client learns of it from the next
                // snapshot (spec §3.8). The channel is closed either way, three
                // lines below; a refusal that left it open would strand the
                // collector in a channel that can never finish.
                //
                // Ascending player index (the loop above) IS the tie-break Р267
                // asks for when two transfers expire on the same tick: the
                // lower index takes the item, and the higher one's own re-check
                // — running later in this very same loop — finds the slot empty
                // and refuses with SlotEmpty. That is the spec's own account of
                // the race (С18/Р236): not blocked, just lost.
                if (Validate(w, i, LootOp.Take, p.LootTargetContainerId, p.LootTargetSlot,
                        in inputs[i]) == LootRefusal.None)
                {
                    // BOTH HALVES IN ONE TICK, in this order. There is no point
                    // between these two lines at which the item does not exist
                    // or exists twice — the same invariant the Drop path above
                    // and KillPlayer's corpse container (R-128) both keep.
                    //
                    // Neither call can fail here, and neither return value is
                    // therefore examined: check 4b established one line ago that
                    // the slot is non-empty, and check 8 (CanAddItem — the SAME
                    // predicate TryAddItem gates its own add on) that the
                    // backpack has room, both on this exact state in this exact
                    // tick. A "just in case" branch would be untested code
                    // guarding an unreachable case.
                    //
                    // ADDRESSEE — Т29 (coordinator R-163, doc shape R-96), AND
                    // IT HAS PAID: the emission stands immediately below, with
                    // its own emptiness guard. THIS is where a container
                    // becomes empty, and spec §3.16 wanted that observable.
                    // Until Т29 it was NOT emitted, deliberately —
                    // SimEventKind carried no Stage 3 entry at all THEN, and an
                    // emitter without delivery is half a system: SnapshotAssembler
                    // would drop an unknown kind silently while
                    // EventRelevance.ChannelFor (typed on SimEventKind, and
                    // built to throw on an unaccounted one) would take the whole
                    // tick down. Т29 owns the enum, the channel and this
                    // emission together, and the assembler's own mapping switch
                    // stopped dropping silently at the Ф6 gate (review A-1, its
                    // `default` throws now). The twin debt at
                    // Loot.PickupSystem.AdvanceTtl for PickupTaken is PAID by
                    // the same task — that comment now draws its contrast with
                    // a real neighbor rather than a promised one.
                    w.TryTakeFromContainer(p.LootTargetContainerId, p.LootTargetSlot, out byte itemId);
                    w.TryAddItem(i, itemId);

                    // Stage 3 Т29 — THE ADDRESSEE ABOVE HAS PAID. Emitted
                    // only when the box is now EMPTY, not on every take: the
                    // kind's name is a claim about the container, and one
                    // fired per item would make "emptied" mean "touched".
                    // The predicate is the world's own (ContainerIsEmpty),
                    // not a loop written here — see its doc for why it is
                    // deliberately not the assembler's wire mask.
                    //
                    // The container is still THERE: TryTakeFromContainer
                    // empties a slot and removes nothing, and an emptied box
                    // ages out on its own TTL like any other. So its position
                    // is readable, and the delivery needs it — the collectors
                    // who can see the box are exactly who the news is for.
                    //
                    // ⚠ DELIVERED BY VISIBILITY, BUT NOT BY THE CHANNEL TABLE
                    // (R-236). EventRelevance.ChannelFor answers `None` for
                    // this kind — its own word for "decided elsewhere" — and
                    // the decision lives in SnapshotAssembler, against
                    // `ContainersCurrent`. Do NOT "fix" the table to
                    // `Visible`: that seam is handed the MOBS set, so a
                    // container's id would be resolved in the wrong id space
                    // and the event would reach whichever mob shares the
                    // number (a CR 4 leak, not cosmetics). See SimEvents.cs's
                    // entry for this kind, which carries the same warning.
                    //
                    // ⚠ ONE LOSS IS ACCEPTED, NOT OVERLOOKED (Т29 review, M-3),
                    // and it is named because this task's whole subject is
                    // silent losses. This system runs BEFORE ContainerStore
                    // (SimulationWorld.TickAll's own order, R-149/R-101), and
                    // a Ground/MobCorpse container's TTL decays regardless of
                    // what is left in it. On the one tick where a transfer
                    // takes the last item AND that TTL crosses zero, the box
                    // is swap-removed later in the same tick, so no
                    // connection's ContainersCurrent holds it and the event
                    // reaches nobody. Deliberately NOT fixed by widening the
                    // assembler to the PREVIOUS set: an event about a box that
                    // is already gone from the frame is unanchorable by Р278,
                    // and the client sees the box vanish that same frame
                    // anyway — which is the stronger news of the two.
                    // ONE resolution of the id, not two (gate Ф6, review
                    // B-3): the position is needed anyway, so the emptiness
                    // question is asked of the index already in hand. The
                    // `box >= 0` term is the guard `ContainerIsEmpty` keeps
                    // for its own callers, moved here with the lookup —
                    // unreachable from here (the transfer above just took
                    // from this very box) and kept for the same reason its
                    // sibling is: the next caller may not be so lucky.
                    int box = w.IndexOfContainer(p.LootTargetContainerId);
                    if (box >= 0 && w.ContainerIsEmptyAt(box))
                    {
                        w.Emit(SimEventKind.ContainerEmptied, w.Containers[box].Pos,
                            p.LootTargetContainerId, default, 0f);
                    }
                }

                // Closed in BOTH outcomes — success and refusal alike.
                p.LootTimer = 0f;
                p.LootTargetContainerId = 0;
                p.LootTargetSlot = 0;
            }

            // Stage 3 Task 19 (spec §3.7/§3.8, errata E-6/C-I7): the repair
            // kit's own completion pass. A SEPARATE loop, not folded into
            // the one above: LootTimer's own early exit already `continue`s
            // past the rest of that iteration on the (overwhelmingly common)
            // idle tick, and folding RepairTimer's decrement in there would
            // either never run on that tick or force rewriting a loop whose
            // golden-inertness the R-150 guard-class doc already pins. Same
            // discipline, second channel: named early exit BEFORE any read,
            // decrement before the re-check, the SAME Validate re-run rather
            // than a lighter copy (rule 2).
            for (int i = 0; i < w.PlayerCount; i++)
            {
                ref PlayerState p = ref w.PlayerRef(i);
                if (p.RepairTimer <= 0f) continue; // ← same golden-inertness shape as R-150, second channel
                p.RepairTimer -= SimulationWorld.TickDt;
                if (p.RepairTimer > 0f) continue;

                // Coordinator review round 1 (mandatory fix 2): `Config` is a
                // by-value getter (see this file's own note above, "Config's
                // getter returns the struct BY VALUE"), and R-150's own
                // promise is "named early exit BEFORE any read" — reading it
                // above the guard would run a whole-config copy on every
                // idle tick of both golden scenarios for nothing. Read AFTER
                // the guard, same spot Validate itself reads `w.Config`
                // (after its own first checks, not at the top of the
                // method).
                SimConfig cfg = w.Config;
                int slot = FindRepairKitSlot(w, i, cfg.Items);
                if (Validate(w, i, LootOp.Use, 0, slot, in inputs[i]) == LootRefusal.None)
                {
                    w.TryRemoveItemAt(i, slot, out _);
                    p.Hp = math.min(p.Hp + cfg.Loot.RepairKitHealAmount, cfg.Hero.MaxHp);
                }
                p.RepairTimer = 0f;
            }
        }

        /// Coordinator D-2: Use's Begin does not remember which backpack
        /// slot it started from (see that branch's own doc), so the
        /// completion tick re-finds a repair kit by KIND instead of by the
        /// ORIGINAL address. Ascending slot order, first match —
        /// deterministic, and the only order this codebase ever reads a
        /// backpack in (same convention TryAdd/UsedSlots already read it
        /// in). -1 when the backpack carries none (the player dropped or
        /// used their last one mid-channel): Validate's own check 6 turns a
        /// negative slot into InventoryIndexOutOfRange, the same silent-
        /// cancellation shape every other Update abort in this file already
        /// takes — no new refusal code is owed to "nothing left to
        /// consume".
        static int FindRepairKitSlot(SimulationWorld w, int playerIndex, ItemDef[] catalog)
        {
            int count = w.InventoryCountOf(playerIndex);
            for (int i = 0; i < count; i++)
            {
                if (ItemCatalogLookup.Find(w.InventoryItemAt(playerIndex, i), catalog).Kind == ItemKind.RepairKit)
                    return i;
            }
            return -1;
        }
    }
}
