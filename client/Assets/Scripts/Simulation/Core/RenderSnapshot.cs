using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// One container's interior as a FRAME describes it (Stage 3 Т32б, owner
    /// decision R-253) — the id it belongs to, which of its slots are occupied,
    /// and where this frame's copy of those slots' item ids begins.
    ///
    /// A FLAT POOL, NOT A TABLE, and the owner weighed both. A
    /// `MaxContainers x MaxContainerSlots` table would be 512 bytes copied on
    /// every frame and every `CopyFrom` for the sake of a window that shows ONE
    /// box at a time; the pool costs a record per box the frame actually
    /// describes. It is deliberately the same shape the wire already uses
    /// (`Protocol.SnapshotBlocks.ContainerSlotsRecord`) rather than a second
    /// one — but it cannot BE that type: `Ring.Simulation` references
    /// `Unity.Mathematics` and nothing else, so a networking type in a render
    /// frame is not available to it, and would be the wrong dependency even if
    /// it were (a local world fills this without a wire in sight).
    ///
    /// ABSENT IS NOT EMPTY. A box with no record here is one this frame says
    /// NOTHING about — out of reach, or cut for room — which is a different
    /// fact from a box whose `OccupancyMask` is zero. That distinction is the
    /// whole reason the pool lists boxes rather than indexing all of them, and
    /// it is the same one `PlayerKnown` exists for on the player side.
    ///
    /// `ItemOffset` INDEXES `RenderSnapshot.ContainerInteriorItems`, the frame's
    /// own pool — never the wire payload the record was decoded from. The wire
    /// record's offset points into the block's bytes and dies with the datagram;
    /// this one has to outlive it, because the frame is read a whole render
    /// frame later.
    public struct ContainerInterior
    {
        public int Id;
        /// Bit `i` set means slot `i` holds something; the item ids that follow
        /// at `ItemOffset` are those slots' contents in ascending slot order.
        /// `ItemCount` is this mask's popcount, carried rather than recomputed
        /// so a reader never has to know the mask's width.
        public byte OccupancyMask;
        public int ItemOffset;
        public int ItemCount;
    }

    /// Sentinel for `RenderSnapshot.MatchSecondsRemaining` (Stage 3 Т32б).
    /// Any negative number reads the same way; this is the one every writer
    /// uses, so "this frame carries no countdown" is one constant rather than a
    /// convention each writer re-invents.
    ///
    /// A CLASS OF ITS OWN, like `ProjectileIds` and `ExtractKinds` — the shape
    /// this project gives sentinel values, and for the reason `ExtractKinds`'
    /// own doc measured: a const declared on the type it describes is a static
    /// field, and the reflective sweeps that walk these types then try to write
    /// it.
    public static class MatchCountdown
    {
        public const int None = -1;
    }

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
        /// array pair as Pickups above, sized to Arena.MaxContainers: where a
        /// box is, what kind it is, whether it is empty.
        ///
        /// ⚠ SLOT CONTENT IS NO LONGER ABSENT FROM THIS CLASS — it arrived in
        /// Т32б, and it did NOT arrive as the table R-216 refused. This
        /// paragraph used to say content was "deliberately absent"; what R-216
        /// weighed and rejected was a per-tick copy of
        /// MaxContainers * MaxContainerSlots bytes for data one connection in
        /// three needs only while standing over a box, and that judgement is
        /// intact: `ContainerInteriors` below is a flat pool listing the
        /// handful of boxes a frame actually describes (owner decision R-253),
        /// filled on both backends by the same reach rule the wire uses.
        /// The assembler still reads the world directly through
        /// `SimulationWorld.ContainerItemsInto` rather than through this class,
        /// because it builds ONE connection's frame and this class is the
        /// RECEIVING side's.
        ///
        /// WHERE THE CONTENT TRAVELS (Stage 3 Т27 — an earlier draft of this
        /// paragraph predicted "a reliable message a later task adds", and the
        /// prediction did not come true). Spec §3.12 puts it in the SNAPSHOT,
        /// as the ContainerSlots block (tag 10), sent only to a collector
        /// inside LootRadius; the reliable pair Т28 added (LootRequestNet/
        /// LootResultNet) carries loot REQUESTS and refusal codes, not the
        /// contents of a box.
        ///
        /// A LOCAL world fills this from CaptureSnapshot; the networked
        /// backend leaves the count at zero until it DECODES the containers
        /// block. Between Т27 and Т32б those were different moments — the
        /// server wrote Containers on every frame while the receiver stepped
        /// over the block, its views being Ф7 — and Т32б closed the gap by
        /// teaching `NetworkSimBackend` all five remaining kinds. Same "zero
        /// means nothing decoded yet" convention PickupCount's own doc states
        /// — the emphasis being on DECODED.
        public int ContainerCount;
        public ContainerState[] Containers;

        /// Whether the box at the same index has already been emptied (Stage 3
        /// Т32б) — indexed like `Containers` above and bounded by
        /// `ContainerCount`, the shape `PlayerKnown` established for a decoded
        /// per-entity flag with no room in the state struct.
        ///
        /// NOT A FIELD OF `ContainerState`, and the reason is not tidiness:
        /// that struct is hashed (`SimulationWorld.StateHash` walks every live
        /// container), and the extraction stage has no golden sanctions left.
        /// It is also the wrong layer — emptiness is answered by the world's
        /// own `ContainerIsEmptyAt`, and a frame is where an ANSWER is
        /// delivered, not where it is stored.
        ///
        /// IT RIDES SEPARATELY FROM THE INTERIOR ON PURPOSE, and the wire says
        /// why (`SnapshotBlocks.ContainerRecord`'s own doc): interiors reach
        /// only a collector inside `LootRadius`, while this flag reaches
        /// everyone who can see the box, because "already looted" is what a
        /// player reads AT A DISTANCE to decide whether the walk is worth it.
        /// A client that had to stand over a box to learn it was empty would
        /// have learned it too late to matter.
        public bool[] ContainerIsEmpty;
        public WaveState Wave;
        /// The match's flow phase (Stage 3 Т6) — a single struct like Wave
        /// above and WorldStats below, at the same canonical position it
        /// holds in StateHash/WorldSave. Its consumers are the extraction UI
        /// and the gate/portal views (Ф6-Ф7); like PickupCount above it stays
        /// at its default on the networked path until Т25 puts the phase on
        /// the wire.
        public MatchState Match;

        /// Seconds left in the raid, or a NEGATIVE number when this frame
        /// carries no countdown at all (Stage 3 Т32б, spec §3.12 tag 6).
        ///
        /// WHY A SENTINEL AND NOT ZERO. Zero is a legal reading — it is what
        /// the last second before the raid ends looks like — so it cannot also
        /// mean "nobody told me". The two need telling apart for the same
        /// reason `PlayerKnown` exists beside `Players[i].Alive`: a HUD that
        /// cannot tell them apart shows a raid ending forever.
        ///
        /// AND A LOCAL WORLD REALLY HAS NO COUNTDOWN. The match's length lives
        /// in `NetConfig.MatchMaxDurationSeconds` and its expiry is enforced by
        /// `MatchEndPolicy`, both on the server's side of the authority line
        /// (CRITICAL RULE 3 names the match timer among the things the server
        /// decides); `SimulationWorld.MarkMatchEnded` has exactly one caller
        /// and it is `MatchServer`. So a world ticking in-process is not
        /// "missing" the number — it is a raid nothing ends on time, and the
        /// honest answer is that there is no countdown to show.
        public int MatchSecondsRemaining;

        /// Whether the Director is still standing (Stage 3 Т32б, spec §3.12
        /// tag 6, the Match block's `DirectorAlive` bit).
        ///
        /// A DECODED FACT, NOT A FLAGS BYTE, because a bit layout is the
        /// wire's business: `ReadLiveness` already spreads its mask into
        /// `PlayerAliveInMatch` at the border "so that nothing above it has to
        /// know a bit layout", and `Ring.Presentation` has no reference to the
        /// assembly the layout lives in and must not grow one (Р180).
        ///
        /// THE GATE BIT IS NOT MIRRORED HERE, deliberately. It is derivable —
        /// it is `Match.Phase == MatchPhase.GateOpen` — and the wire's own doc
        /// names the phase the source of truth and the bit a convenience view
        /// of it. Carrying it a second time would put two authorities behind
        /// one question. `DirectorAlive` is the opposite case and that is why
        /// it is here: the Director dies `GateDelaySeconds` BEFORE the phase
        /// moves, so no phase can answer for him during exactly the window a
        /// client most needs the answer.
        public bool DirectorAlive;

        /// The frame owner's own backpack (Stage 3 Т32б, spec §3.12 tag 7) —
        /// item ids, bounded by `InventoryItemCount`, and the slot points they
        /// cost together.
        ///
        /// SINGULAR, NOT ONE PER SEAT. The Self block is sent to its owner and
        /// nobody else (Р276), so an array indexed by slot would be a field the
        /// networked backend could only ever fill for one entry — and a reader
        /// could not tell "his pack is empty" from "his pack is not mine to
        /// see", which is the exact ambiguity `PlayerKnown` was added to end.
        ///
        /// NOTHING ELSE OF THE OWNER LIVES HERE (Р276): `Ammo`, `LootTimer`,
        /// `ExtractTimer` and `Extracted` are `PlayerState` fields and already
        /// reach their owner through reconciliation. Only the pack — which
        /// `PlayerState` has no room for, an `Inventory` being a reference type
        /// `CaptureSnapshot` refuses to put in a frame — and the points derived
        /// from it are here.
        public int InventorySlotPoints;
        public int InventoryItemCount;
        public byte[] InventoryItems;

        /// The interiors this frame describes, as a flat pool (owner decision
        /// R-253) — see `ContainerInterior` above for the form and for why a
        /// box absent from this list is not a box that is empty.
        ///
        /// Filled from the ContainerSlots block on the networked path and, on
        /// the local one, by `LocalSimBackend` for the boxes within
        /// `LootOps.WithinLootReach` of the owner — the SAME rule Р238 gives
        /// the wire, so the field means one thing on both backends.
        public int ContainerInteriorCount;
        public ContainerInterior[] ContainerInteriors;
        /// Every described box's slot contents, back to back, addressed by each
        /// record's `ItemOffset`. One pool rather than an array per record: the
        /// records are already bounded and a per-record array would allocate on
        /// the one path that must not.
        public int ContainerInteriorItemCount;
        public byte[] ContainerInteriorItems;

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

        /// Whether the slot WALKED OUT of the raid (Stage 3, playtest В1 round
        /// two, bd `app-1kei`) — indexed like `PlayerAliveInMatch` beside it,
        /// and filled from the Liveness block's SECOND mask, the one spec Р257
        /// put on the wire for exactly this.
        ///
        /// IT IS NOT DERIVABLE FROM `Alive`, AND THAT IS THE WHOLE POINT.
        /// Extraction sets `Alive = false` and `Extracted = true` in one tick
        /// (`ExtractionSystem`), so a reader with only the first bit sees a
        /// collector who stopped being alive and draws the one thing that
        /// means — a body. The spec forbids exactly that: the body is TAKEN
        /// AWAY, and unlike a death it leaves no corpse and nothing to loot
        /// (§3.5), and the simulation already obeys it (`ExtractionTests.
        /// Completing_MarksExtracted_LeavesNoCorpse_AndAnnouncesIt`). Only the
        /// picture did not, because this fact never reached it.
        ///
        /// `Players[i].Extracted` IS NOT THE SAME QUESTION, for the reason
        /// `PlayerAliveInMatch` exists beside `Players[i].Alive`: a stranger's
        /// record off the wire carries Index/Pos/Dir/Hp/Flags and nothing else
        /// (`PlayerWireFlags` has no bit for this), so his `Extracted` reads
        /// `false` however he left. One's OWN state rides reconciliation and is
        /// therefore true on both backends — which is precisely why the two
        /// must not be conflated: a rule written against the local slot alone
        /// would work in solo and quietly draw a body for every teammate who
        /// made it out.
        ///
        /// A LOCAL WORLD COPIES ITS OWN `Extracted`, the same way it copies its
        /// own `Alive` into the array above (`SimulationWorld.CaptureSnapshot`)
        /// — so the field means one thing on both backends rather than two.
        public bool[] PlayerExtractedInMatch;

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

        /// TAKES THE WHOLE `SimConfig` SINCE Т32б, where it took only the
        /// arena half before. The frame grew a field the arena cannot size:
        /// the owner's backpack is bounded by `Hero.MaxInventoryItems`, the
        /// same number `SnapshotAssembler` sizes its own Self scratch from. The
        /// alternatives were a literal ceiling in this file (a Data-layer
        /// `[Range]` this assembly cannot see, restated where nothing could
        /// check it) or a second constructor, i.e. two truths about how a frame
        /// is sized. Every call site already held a `SimConfig` and was reaching
        /// into its `.Arena`, so the widening cost them a shorter argument.
        public RenderSnapshot(in SimConfig cfg)
        {
            ArenaSimConfig arena = cfg.Arena;
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
            PlayerExtractedInMatch = new bool[arena.MaxPlayers];
            // Stage 3 Т32б. The backpack is sized from the HERO cap, the one
            // number in this constructor that is not the arena's — see the
            // constructor's own doc. The interiors and their item pool take the
            // container caps, the same pair `SnapshotAssembler` sizes its
            // scratch from, so a frame can hold every box the world can hold
            // even though it will normally describe one or two.
            InventoryItems = new byte[math.max(1, cfg.Hero.MaxInventoryItems)];
            ContainerIsEmpty = new bool[arena.MaxContainers];
            ContainerInteriors = new ContainerInterior[arena.MaxContainers];
            ContainerInteriorItems = new byte[math.max(1,
                arena.MaxContainers * arena.MaxContainerSlots)];
            // A frame nobody has decoded a Match block into carries no
            // countdown, and says so rather than reading as "zero seconds
            // left" (MatchSecondsRemaining's own doc). Every writer of a
            // recycled frame restates this; the constructor states it for the
            // first tick, before any writer has run.
            MatchSecondsRemaining = MatchCountdown.None;
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
        /// built from the SAME caps (both constructed via `new
        /// RenderSnapshot(in cfg)` off one `SimConfig`), so every index up
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
            for (int i = 0; i < other.PlayerCount; i++)
                PlayerExtractedInMatch[i] = other.PlayerExtractedInMatch[i];
            LocalPlayerIndex = other.LocalPlayerIndex;
            MobCount = other.MobCount;
            for (int i = 0; i < other.MobCount; i++) Mobs[i] = other.Mobs[i];
            ProjectileCount = other.ProjectileCount;
            for (int i = 0; i < other.ProjectileCount; i++) Projectiles[i] = other.Projectiles[i];
            PickupCount = other.PickupCount;
            for (int i = 0; i < other.PickupCount; i++) Pickups[i] = other.Pickups[i];
            ContainerCount = other.ContainerCount;
            for (int i = 0; i < other.ContainerCount; i++) Containers[i] = other.Containers[i];
            for (int i = 0; i < other.ContainerCount; i++)
                ContainerIsEmpty[i] = other.ContainerIsEmpty[i];
            Wave = other.Wave;
            Match = other.Match;
            // Stage 3 Т32б: the five blocks' own fields, in the order they are
            // declared above. `MatchSecondsRemaining` copies whatever `other`
            // holds INCLUDING the no-countdown sentinel — a frozen frame that
            // invented a countdown the live one never had would be worse than
            // one that admits it has none.
            MatchSecondsRemaining = other.MatchSecondsRemaining;
            DirectorAlive = other.DirectorAlive;
            InventorySlotPoints = other.InventorySlotPoints;
            InventoryItemCount = other.InventoryItemCount;
            for (int i = 0; i < other.InventoryItemCount; i++)
                InventoryItems[i] = other.InventoryItems[i];
            ContainerInteriorCount = other.ContainerInteriorCount;
            for (int i = 0; i < other.ContainerInteriorCount; i++)
                ContainerInteriors[i] = other.ContainerInteriors[i];
            // THE POOL IS COPIED BY ITS OWN COUNT, not by the records' offsets.
            // A record's `ItemOffset` addresses this pool, so copying "what the
            // records point at" would mean walking them to find the high-water
            // mark — the same number `ContainerInteriorItemCount` already is.
            ContainerInteriorItemCount = other.ContainerInteriorItemCount;
            for (int i = 0; i < other.ContainerInteriorItemCount; i++)
                ContainerInteriorItems[i] = other.ContainerInteriorItems[i];
            for (int i = 0; i < other.PlayerCount; i++) PlayerStats[i] = other.PlayerStats[i];
            WorldStats = other.WorldStats;
        }
    }
}
