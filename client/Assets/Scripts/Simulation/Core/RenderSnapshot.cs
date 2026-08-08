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

        /// Deep-copies one tick's worth of render data FROM `other` INTO this
        /// instance (Stage 2 Task 32) — the ONE copy routine `SimulationRunner`'s
        /// frozen hitstop pair uses, so a field this class grows in a future
        /// phase only needs teaching to ONE place, not to every call site that
        /// happens to duplicate a snapshot. Moved here, unchanged in body, from
        /// `SimulationRunner`'s private `CopySnapshot(from, to)` (Task 25/Task 4/
        /// Task 5): `SimulationRunner.FreezeRender`/`UnfreezeRender` call
        /// `to.CopyFrom(from)` where they used to call `CopySnapshot(from, to)`.
        /// `Networking.Client.SnapshotQueue` (Task 32's other half) does NOT
        /// call this method — it hands the caller (Task 44) a preallocated,
        /// empty slot to DECODE wire bytes directly into, never a snapshot to
        /// copy FROM (fix-round 1 correction: an earlier draft of this doc
        /// claimed otherwise).
        ///
        /// Every field here is either a struct or a struct array, so plain
        /// assignment/indexed-copy IS the deep copy — nothing reaches beyond this
        /// class's own already-public fields. Contract: `other` and `this` are
        /// built from the SAME arena caps (both constructed via `new
        /// RenderSnapshot(in arena)` off one `ArenaSimConfig`), so every index up
        /// to `other`'s counts is in bounds on this side too — callers that
        /// preallocate every `RenderSnapshot` they ever copy between off one
        /// config (both `SimulationRunner` and `SnapshotQueue` do) get this for
        /// free.
        public void CopyFrom(RenderSnapshot other)
        {
            Tick = other.Tick;
            PlayerCount = other.PlayerCount;
            for (int i = 0; i < other.PlayerCount; i++) Players[i] = other.Players[i];
            LocalPlayerIndex = other.LocalPlayerIndex;
            MobCount = other.MobCount;
            for (int i = 0; i < other.MobCount; i++) Mobs[i] = other.Mobs[i];
            ProjectileCount = other.ProjectileCount;
            for (int i = 0; i < other.ProjectileCount; i++) Projectiles[i] = other.Projectiles[i];
            Wave = other.Wave;
            for (int i = 0; i < other.PlayerCount; i++) PlayerStats[i] = other.PlayerStats[i];
            WorldStats = other.WorldStats;
        }
    }
}
