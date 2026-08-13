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
