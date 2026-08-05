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
        /// Personal per-player match counters (Stage 2 Task 5) — name symmetric
        /// to Players above (both arrays indexed by player); Stats below is the
        /// synonym for the local player's own entry, same pattern as Player/Players.
        public MatchStats[] PlayerStats;
        /// Match-wide counters (Stage 2 Task 5) — WavesCleared/MobSpawnsSkipped/
        /// ProjectileSpawnsSkipped, counted once regardless of player count; a
        /// single field like Wave above, not an array.
        public WorldStats WorldStats;

        /// Synonym for Players[LocalPlayerIndex] (Stage 2 Task 4) — every read
        /// call site that predates Stage 2 Task 4 (~94 across Presentation/
        /// tests, verified by grep) keeps compiling unchanged; only the two write sites
        /// (SimulationWorld.CaptureSnapshot, SimulationRunner's private
        /// CopySnapshot) needed updating to the array underneath.
        public PlayerState Player => Players[LocalPlayerIndex];

        /// Synonym for PlayerStats[LocalPlayerIndex] (Stage 2 Task 5) — was a
        /// plain field before this task; every existing read call site (DevOverlay,
        /// DeathOverlayController) keeps compiling unchanged, same Player/Players trick.
        public MatchStats Stats => PlayerStats[LocalPlayerIndex];

        public RenderSnapshot(in ArenaSimConfig arena)
        {
            Players = new PlayerState[arena.MaxPlayers];
            Mobs = new MobState[arena.MaxMobs];
            Projectiles = new ProjectileState[arena.MaxProjectiles];
            PlayerStats = new MatchStats[arena.MaxPlayers];
        }
    }
}
