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
    }
}
