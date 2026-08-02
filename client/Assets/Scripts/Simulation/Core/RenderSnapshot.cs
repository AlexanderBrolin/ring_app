namespace Ring.Simulation.Core
{
    /// Preallocated render view of one tick. Matching by entity Id (спека §3.7).
    public sealed class RenderSnapshot
    {
        public int Tick;
        public PlayerState Player;
        public int MobCount;
        public MobState[] Mobs;
        public int ProjectileCount;
        public ProjectileState[] Projectiles;
        public WaveState Wave;
        public MatchStats Stats;

        public RenderSnapshot(in ArenaSimConfig arena)
        {
            Mobs = new MobState[arena.MaxMobs];
            Projectiles = new ProjectileState[arena.MaxProjectiles];
        }
    }
}
