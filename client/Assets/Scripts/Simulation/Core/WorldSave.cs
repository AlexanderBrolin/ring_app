using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Deep copy of the full canonical world state (spec §3.13), for rollback/replay.
    /// Config is intentionally NOT included — the caller keeps using the same
    /// SimConfig instance across SaveState/RestoreState.
    public sealed class WorldSave
    {
        public int Tick;
        public Random Rng;
        public int NextEntityId;
        public PlayerState Player;
        public int MobCount;
        public MobState[] Mobs;
        public int ProjectileCount;
        public ProjectileState[] Projectiles;
        public WaveState Wave;
        public MatchStats Stats;
    }
}
