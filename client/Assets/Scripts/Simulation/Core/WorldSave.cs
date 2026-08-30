using Ring.Simulation.Loot;
using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Deep copy of the full canonical world state (spec §3.13), for rollback/replay.
    /// Config is intentionally NOT included — the caller keeps using the same
    /// SimConfig instance across SaveState/RestoreState.
    ///
    /// FIELDS ARE DECLARED IN THE CANONICAL ORDER (spec Р294, Stage 3 Т6) —
    /// the same order SimulationWorld.StateHash folds the world in, and the
    /// same order SaveState's own initializer writes them. Reading the three
    /// lists side by side is how a piece of state present in one and missing
    /// from another becomes visible by position rather than only by search;
    /// Т6 re-sorted the pre-existing fields (PlayerCount/Players and
    /// WorldStats/Stats had been declared the other way round) to make that
    /// true of the whole class, not only of its new members. Declaration
    /// order carries no semantics of its own here — nothing serializes this
    /// class field-by-field — so the re-sort is a readability change and
    /// cannot move a digest.
    public sealed class WorldSave
    {
        public int Tick;
        public Random SpreadRng;
        public Random WaveRng;
        /// Stage 3 Т6 (spec Р230): the loot-placement stream, saved beside
        /// the other two. Its consumer arrives in Т15; it is saved from
        /// today because a restore that rewound the world without rewinding
        /// this stream would diverge from the live run at the first draw
        /// after the load, and a save format is not something to retrofit
        /// under a replay bug.
        public Random LootRng;
        public int NextEntityId;
        public int PlayerCount;
        public PlayerState[] Players;
        public int MobCount;
        public MobState[] Mobs;
        public int ProjectileCount;
        public ProjectileState[] Projectiles;
        /// Stage 3 Т6 (spec §3.6/Р294): ground pickups, same count-plus-whole-
        /// backing-array shape as Mobs/Projectiles above.
        public int PickupCount;
        public PickupState[] Pickups;
        /// Stage 3 Т14 (spec Р229/Р294): containers, between the pickups
        /// above and the wave below — the position spec Р294 gives them,
        /// and the one SimulationWorld.StateHash reserved with a zero count
        /// since Т6. `ContainerSlots` is the WHOLE flat backing array
        /// (MaxContainers * MaxContainerSlots), copied in full by
        /// SaveState/RestoreState same as every other array in this class —
        /// not trimmed to any one container's own SlotCount, so a restore
        /// is a straight Array.Copy with no offset re-derivation.
        public int ContainerCount;
        public ContainerState[] Containers;
        public byte[] ContainerSlots;

        /// app-88jb Т25 (spec §3.6.1, coordinator RULING 146): the rewind
        /// ring, BOTH halves of it -- the flat record array and the per-row
        /// tick stamps -- between the container slots and the waves, the same
        /// position the fold holds in SimulationWorld.StateHash. Rows without
        /// stamps would answer historical positions to questions about the
        /// wrong ticks, so the two travel together or not at all.
        /// The ring's OCCUPANCY set is deliberately absent: it is an index over
        /// HistorySlot, which Players/Mobs above already carry, and
        /// RestoreState re-derives it rather than restoring a second copy of
        /// one fact (PositionHistory.RederiveOccupancy's own doc).
        /// ⚠ HISTORY IS THE FIRST ENTRY WHOSE ORDER IN THIS CLASS AND ITS ORDER
        /// IN THE DIGEST ARE NOT THE SAME WALK, and this class's own contract
        /// above is why that has to be said out loud rather than left for a
        /// reader to reconcile. Every other field is folded the way it is
        /// declared: a count, then its array, front to back. The ring is folded
        /// BY TICK, oldest to newest across the window, and within a tick by
        /// the world's live bodies rather than by slot -- so the POSITION of
        /// this entry matches StateHash, while the shape of the walk inside it
        /// does not, and only PositionHistory.Fold knows that shape.
        ///
        /// ⛔ internal, NOT public, AND THAT IS FORCED RATHER THAN CHOSEN.
        /// PositionHistory is an internal class, so its nested Record is
        /// internal in effect; a public field of that type in this public class
        /// does not compile (CS0053, "inconsistent accessibility"). It costs
        /// nothing today, and that is measured rather than assumed: `new
        /// WorldSave` appears exactly ONCE in the whole of client/Assets, in
        /// SimulationWorld.SaveState, and every other mention is either a
        /// consumer of what SaveState returned or a comment.
        /// ⚠ THE DAY AN OUTSIDE BUILDER APPEARS, THIS BECOMES ITS PROBLEM, and
        /// RestoreState's own cross-checks already anticipate one ("a
        /// hand-built WorldSave (Networking, Stage 2 Task 7+) could get
        /// PlayerCount right and still hand in a short Players array"). Such a
        /// builder would live in another assembly, and Ring.Simulation's single
        /// InternalsVisibleTo names only Ring.Simulation.Tests -- so it would
        /// need either a second one or a public surface of its own. That is a
        /// decision for the task that brings the builder, not a surface to widen
        /// now for a caller that does not exist.
        internal PositionHistory.Record[] HistoryRows;
        internal int[] HistoryRowTicks;
        public WaveState[] Waves;
        /// Stage 3 Т6: the match's flow state (Stage 3 Task 1's struct),
        /// saved right after the wave — one per match, not per player.
        public MatchState Match;
        /// Stage 2 Task 5: match-wide counters, not per player — a single field,
        /// unlike the per-zone Waves array above.
        public WorldStats WorldStats;
        /// Stage 2 Task 5: one MatchStats per player, same length/indexing
        /// contract as Players/PlayerCount above.
        public MatchStats[] Stats;

        /// Stage 3 Task 4 (spec §3.6 "Рюкзак"): one backpack per player,
        /// same length/indexing contract as Players/PlayerCount above.
        /// Inventory is a reference type, so SaveState/RestoreState
        /// deep-copy element contents through Inventory.Clone/RestoreFrom
        /// rather than aliasing the live array — see those methods' own
        /// doc for why. LAST, after the statistics: the debt Task 4 recorded
        /// here for Т6 (spec Р294) is discharged — backpacks now hold that
        /// position in this class, in SaveState's initializer and in
        /// SimulationWorld.StateHash alike.
        public Inventory[] Inventories;

        // Stage 2 Task 10, deliberately ABSENT from this class: the tally of
        // edge requests the rate limit dropped (SimulationWorld.
        // RejectedEdgeRequestsForTest). Every field above is canonical world
        // state — it is saved, restored and hashed. A dropped request is the
        // opposite: something the world refused to act on, diagnostics only.
        // Adding it here would make an anti-spam counter part of the
        // rollback/replay contract, and StateHash's own field list would then
        // have to follow. The shipped network-facing counter lands in NetStats
        // (Stage 2 Task 23/28), outside this save.
        //
        // The two per-player edge-request counters (PlayerState.
        // DashRequestCooldownTicks / SlideRequestCooldownTicks) ARE real world
        // state and ARE saved — they ride inside Players above, needing no field
        // of their own here.
    }
}
