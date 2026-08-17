using Ring.Simulation.Loot;
using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Deep copy of the full canonical world state (spec §3.13), for rollback/replay.
    /// Config is intentionally NOT included — the caller keeps using the same
    /// SimConfig instance across SaveState/RestoreState.
    public sealed class WorldSave
    {
        public int Tick;
        public Random SpreadRng;
        public Random WaveRng;
        public int NextEntityId;
        public PlayerState[] Players;
        public int PlayerCount;
        public int MobCount;
        public MobState[] Mobs;
        public int ProjectileCount;
        public ProjectileState[] Projectiles;
        public WaveState Wave;
        /// Stage 2 Task 5: one MatchStats per player, same length/indexing
        /// contract as Players/PlayerCount above.
        public MatchStats[] Stats;
        /// Stage 2 Task 5: match-wide counters, not per player — a single field
        /// like Wave above, not an array.
        public WorldStats WorldStats;

        /// Stage 3 Task 4 (spec §3.6 "Рюкзак"): one backpack per player,
        /// same length/indexing contract as Players/PlayerCount above.
        /// Inventory is a reference type, so SaveState/RestoreState
        /// deep-copy element contents through Inventory.Clone/RestoreFrom
        /// rather than aliasing the live array — see those methods' own
        /// doc for why. DEBT for Т6 (owner decision, not this task's to
        /// resolve): the canonical save/hash ORDER this field joins is
        /// Т6's job (spec Р294) — Т6 must place backpacks LAST, after
        /// MatchStats/WorldStats, when it wires this array into StateHash
        /// at the sanctioned re-pin. Not yet part of StateHash — see
        /// SimulationWorld.StateHash's own doc and SimConfigHashTests'
        /// PendingHashFields precedent for the equivalent config-side
        /// deferral.
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
