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
        /// Ground pickups of this tick (Stage 3 Т6) — same count-plus-array
        /// pair as Mobs/Projectiles above, sized to Arena.MaxPickups. A LOCAL
        /// world fills them from SimulationWorld.CaptureSnapshot; the
        /// networked backend leaves the count at zero until the pickups block
        /// reaches the wire (Т25), which reads as "this frame carries no
        /// pickups" — the same thing every other count says when nothing was
        /// decoded into it.
        public int PickupCount;
        public PickupState[] Pickups;
        /// Container METADATA of this tick (Stage 3 Т14) — same count-plus-
        /// array pair as Pickups above, sized to Arena.MaxContainers. Slot
        /// CONTENT is deliberately absent from this class, same reasoning
        /// as the backpack note on SimulationWorld.CaptureSnapshot: it
        /// isn't drawn by the interpolated frame, only opened through a
        /// reliable message a later task adds. A LOCAL world fills this
        /// from CaptureSnapshot; the networked backend leaves the count at
        /// zero until containers reach the wire, same "zero means nothing
        /// decoded yet" convention PickupCount's own doc states.
        public int ContainerCount;
        public ContainerState[] Containers;
        public WaveState Wave;
        /// The match's flow phase (Stage 3 Т6) — a single struct like Wave
        /// above and WorldStats below, at the same canonical position it
        /// holds in StateHash/WorldSave. Its consumers are the extraction UI
        /// and the gate/portal views (Ф6-Ф7); like PickupCount above it stays
        /// at its default on the networked path until Т25 puts the phase on
        /// the wire.
        public MatchState Match;
        /// Personal per-player match counters (Stage 2 Task 5) — name symmetric
        /// to Players above (both arrays indexed by player); Stats below is the
        /// synonym for the local player's own entry, same pattern as Player/Players.
        public MatchStats[] PlayerStats;
        /// Match-wide counters (Stage 2 Task 5) — WavesCleared/MobSpawnsSkipped/
        /// ProjectileSpawnsSkipped, counted once regardless of player count; a
        /// single field like Wave above, not an array.
        public WorldStats WorldStats;

        /// Whether this frame carries a STATE for the slot at all (Stage 2 Task
        /// 47a, bd `app-2rf`) — indexed like `Players` above, and the answer to
        /// a question `Players[i].Alive` cannot be asked: "not alive" means a
        /// BODY when this frame carried a record for the slot, and "out of
        /// sight, or a seat this frame says nothing about" when it did not.
        /// Before this field the two were one read, because a slot no record
        /// arrived for reads `default(PlayerState)` — not alive, at the origin —
        /// so a networked client could not tell a corpse from a stranger behind
        /// the fog and drew neither.
        ///
        /// A LOCAL WORLD ANSWERS `true` FOR ITS WHOLE ROSTER (see
        /// `SimulationWorld.CaptureSnapshot`): there is no fog between a world
        /// in memory and the frame it captures itself into, so every slot's
        /// state is in hand. The networked backend, which fills a frame from
        /// what the server chose to send this client, is the only writer that
        /// ever leaves an entry `false`.
        public bool[] PlayerKnown;

        /// Whether the slot is alive IN THE MATCH, visible to this client or
        /// not (Stage 2 Task 47a) — the ROSTER fact, where `Players[i].Alive` is
        /// the visible one. It comes off the wire as the Liveness block's mask,
        /// spread into this array at the border by the networked backend so that
        /// nothing above it has to know a bit layout; a local world simply
        /// copies its own `Alive`.
        ///
        /// WHY BOTH FLAGS EXIST. `PlayerKnown` says what THIS FRAME saw, which
        /// is what decides whether a doll stands, lies, or is not drawn at all;
        /// this one says whether the seat is still standing anywhere in the
        /// arena, which is what a spectate candidate list and a "who is left"
        /// readout need (Stage 2 Task 47b, Р70). A player alive behind the fog
        /// is `false` in the first and `true` in the second, and neither flag
        /// can be derived from the other.
        ///
        /// `false` MEANS "NOT ALIVE OR NOT A SEAT OF THIS MATCH", and the
        /// difference is not recoverable from this frame (fix-round 1; the
        /// paragraph above used to promise "who is still standing" outright).
        /// `PlayerCount` is what bounds a reader's loop, and it means two things
        /// depending on who filled the frame: `SimulationWorld.CaptureSnapshot`
        /// writes the world's REAL roster, while `NetworkSimBackend.BeginSlot`
        /// writes `Arena.MaxPlayers` — the arena's cap — because the roster size
        /// never reaches a client at all. Nothing carries it: the welcome names
        /// the epoch, the seed and one's own index (`MatchWelcomeNet`), the
        /// restart names the epoch and the seed, the snapshot header names the
        /// tick and the epoch, the Liveness block is one bare byte of mask, and
        /// the Players block carries only the others this client may see. So on
        /// a two-player match in a three-seat arena the third entry reads
        /// `false` here for a seat nobody ever took, indistinguishable from a
        /// player who died out of sight — and a "who is left" readout built on
        /// this alone would report a death that never happened.
        ///
        /// AN OPEN END FOR TASK 47b, STATED SO IT IS NOT REDISCOVERED. A
        /// spectate candidate list is safe on this field as it stands (an empty
        /// seat and a dead player are both "not a candidate", so the ambiguity
        /// falls the harmless way), and a ROSTER readout is not. Whoever needs
        /// the second has to bring the roster size across the wire first — the
        /// server has it (`SnapshotAssembler`'s own capture `PlayerCount`) and
        /// simply never says it — rather than infer it from this array.
        public bool[] PlayerAliveInMatch;

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
            // Stage 3 Т6: sized to the arena's own pickup cap, exactly like
            // Mobs/Projectiles above — the same cap SimulationWorld sizes its
            // live array from, so a capture can never overrun this one.
            Pickups = new PickupState[arena.MaxPickups];
            // Stage 3 Т14: sized to the arena's own container cap, exactly
            // like Pickups above.
            Containers = new ContainerState[arena.MaxContainers];
            PlayerStats = new MatchStats[arena.MaxPlayers];
            // Sized to the WHOLE roster, like Players/PlayerStats above and for
            // the same reason: the array index IS the player's slot, so a
            // backend must be free to write any seat of the match (Stage 2 Task
            // 47a) rather than only the seats this frame happened to fill.
            PlayerKnown = new bool[arena.MaxPlayers];
            PlayerAliveInMatch = new bool[arena.MaxPlayers];
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
            for (int i = 0; i < other.PlayerCount; i++) PlayerKnown[i] = other.PlayerKnown[i];
            for (int i = 0; i < other.PlayerCount; i++)
                PlayerAliveInMatch[i] = other.PlayerAliveInMatch[i];
            LocalPlayerIndex = other.LocalPlayerIndex;
            MobCount = other.MobCount;
            for (int i = 0; i < other.MobCount; i++) Mobs[i] = other.Mobs[i];
            ProjectileCount = other.ProjectileCount;
            for (int i = 0; i < other.ProjectileCount; i++) Projectiles[i] = other.Projectiles[i];
            PickupCount = other.PickupCount;
            for (int i = 0; i < other.PickupCount; i++) Pickups[i] = other.Pickups[i];
            ContainerCount = other.ContainerCount;
            for (int i = 0; i < other.ContainerCount; i++) Containers[i] = other.Containers[i];
            Wave = other.Wave;
            Match = other.Match;
            for (int i = 0; i < other.PlayerCount; i++) PlayerStats[i] = other.PlayerStats[i];
            WorldStats = other.WorldStats;
        }
    }
}
