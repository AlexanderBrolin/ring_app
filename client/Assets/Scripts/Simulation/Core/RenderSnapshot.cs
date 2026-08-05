namespace Ring.Simulation.Core
{
    /// Preallocated render view of one tick. Matching by entity Id (spec §3.7).
    public sealed class RenderSnapshot
    {
        public int Tick;
        public PlayerState[] Players;
        public int PlayerCount;
        /// Index into Players for this client's own player (Stage 2 Task 4
        /// Interfaces). Defaults to 0 — CaptureSnapshot never touches it
        /// (SimulationWorld has no notion of "the local client"); Networking
        /// is the only later consumer expected to ever set it to something else.
        public int LocalPlayerIndex;
        public int MobCount;
        public MobState[] Mobs;
        public int ProjectileCount;
        public ProjectileState[] Projectiles;
        public WaveState Wave;
        public MatchStats Stats;

        /// Synonym for Players[LocalPlayerIndex] (Stage 2 Task 4) — every read
        /// call site that predates Stage 2 Task 4 (~94 across Presentation/
        /// tests, verified by grep) keeps compiling unchanged; only the two write sites
        /// (SimulationWorld.CaptureSnapshot, SimulationRunner's private
        /// CopySnapshot) needed updating to the array underneath.
        public PlayerState Player => Players[LocalPlayerIndex];

        public RenderSnapshot(in ArenaSimConfig arena)
        {
            Players = new PlayerState[arena.MaxPlayers];
            Mobs = new MobState[arena.MaxMobs];
            Projectiles = new ProjectileState[arena.MaxProjectiles];
        }
    }
}
