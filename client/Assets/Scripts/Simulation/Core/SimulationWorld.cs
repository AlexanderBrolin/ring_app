using Ring.Simulation.AI;
using Ring.Simulation.Combat;
using Ring.Simulation.Loot;
using Ring.Simulation.Movement;
using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Deterministic world: fixed-dt ticks, three independent RNG streams seeded
    /// from match-config (Task 3: split from the former single shared Random;
    /// Stage 3 Т6 added the third) — weapon spread (_spreadRng), wave director
    /// (_waveRng) and loot placement (_lootRng) draw from separate streams so
    /// one system's RNG consumption never perturbs the other's sequence.
    /// No UnityEngine (asmdef: noEngineReferences) — Critical Rule 1.
    public sealed class SimulationWorld
    {
        /// ADR-002 T5: simulation runs at 30 Hz. The single source of dt.
        public const float TickDt = 1f / 30f;

        /// A length stated in SECONDS, expressed in the only unit a
        /// deterministic comparison may use: WHOLE TICKS (Stage 3, R-178 and
        /// R-190; Ф5 gate, review B-4).
        ///
        /// THIS PHASE PAID FOR THE RULE TWICE, BOTH TIMES BY MEASUREMENT, and
        /// then left it transcribed by hand in four places — which is how a
        /// rule stops being a rule. Т21: `elapsed * TickDt >= GateDelaySeconds`
        /// survived its mutation because at the boundary the two sides are
        /// bit-equal when spilled to float locals and NOT equal when the
        /// product stays inline at higher precision — an answer that depends
        /// on the compiler has no place in state that feeds StateHash. Т23:
        /// the extraction channel finished a whole tick LATE, because a SUM of
        /// six TickDt is 0.2f while six times TickDt is 0.20000002f.
        ///
        /// ROUNDING IS TO THE NEAREST TICK, and it is exact for every number
        /// that matters: the division is far more accurate than half a tick at
        /// any raid length, so both the shipped values (90 s = 2700 ticks,
        /// 20 s = 600) and every fixture stated as `N * TickDt` land on their
        /// own integer. Callers compare the RESULT, never the seconds.
        public static int TicksFromSeconds(float seconds)
            => (int)Unity.Mathematics.math.round(seconds / TickDt);


        int _tick;
        Random _spreadRng;
        Random _waveRng;
        /// Stage 3 Т6 (spec Р230): the THIRD stream, for loot placement —
        /// container layout at world start (Т15) and the drop rolls that
        /// follow it. Declared here, ahead of its consumer, because it is
        /// canonical world state the moment it exists: it enters StateHash
        /// and WorldSave in this task (spec Р294), and a replay that
        /// restored a save without it would diverge from the live world at
        /// the first draw Т15 makes. Kept out of _waveRng for the reason
        /// Р230 states outright — placing containers pulls RNG at world
        /// start, so sharing the wave stream would make a purely numeric
        /// balance change (one crate more) shift every later wave draw, i.e.
        /// move the golden for no behavioral reason.
        Random _lootRng;
        SimConfig _config;
        // Stage 2 Task 4: length is the match's fixed playerCount (constructor
        // param), not a cap — unlike _mobs/_projectiles below, the player
        // count never grows/shrinks over a match's lifetime in the MVP (no
        // mid-match join).
        readonly PlayerState[] _players;
        // Scratch buffer holding this tick's per-player sanitized input
        // (Stage 2 Task 4) — the movement loop computes it once per player
        // and the weapon loop below reuses the SAME sanitized value (not a
        // re-sanitize), exactly like the old single-player Tick did.
        // Preallocated to _players.Length so TickAll never allocates on the
        // hot path.
        readonly SimInput[] _sanitizedInputs;
        // Stage 2 Task 5: one MatchStats per player (personal counters), length
        // fixed to playerCount like _players/_sanitizedInputs above; WorldStats
        // is a single struct — counted once for the whole match, not per player.
        readonly MatchStats[] _matchStats;
        WorldStats _worldStats;
        // Stage 3 Task 4 (spec §3.6 "Рюкзак", Р232): one Inventory instance
        // per player, next to _matchStats above — lives outside PlayerState
        // (see PlayerState's own doc for why) so its backing byte array
        // never rides along on a wholesale PlayerState copy
        // (ReconcileData, snapshot fixtures, prediction). Length fixed to
        // playerCount like every other per-player array in this class; each
        // instance is itself sized to Hero.MaxInventoryItems at
        // construction (below) and never resized.
        readonly Inventory[] _inventories;

        // Entities appear in Phase 5/6 — arrays are preallocated to arena caps
        // now so the hot path never allocates once systems start filling them.
        MobState[] _mobs;
        int _mobCount;
        /// app-88jb Т24 (spec §3.6): the rewind ring. Preallocated to
        /// Arena.MaxMobs + Arena.MaxPlayers like the scratch buffers below,
        /// but it is STATE, not scratch -- it survives a tick, and the slot
        /// it hands out lives inside MobState/PlayerState and therefore
        /// inside StateHash.
        ///
        /// DEBT A-7 IS CLOSED HERE, NOT DEFERRED, and the shape of the fix is
        /// worth stating because the obvious fix was the wrong one. The
        /// allocator used to be a free LIST, whose answer depended on the
        /// order slots came back in -- real state, living outside WorldSave,
        /// deciding future state. A rolled-back run therefore handed the next
        /// spawn a different slot than a straight run, and HistorySlot is
        /// hashed, so the digests parted company; worse, a body that died
        /// after the save came back holding a slot the list had already
        /// handed on. Putting the list into the save would have HIDDEN that
        /// class of bug behind a bigger save format. Instead the allocator
        /// stopped being state at all (RULING 133): the slot handed out is
        /// the lowest free one, so the answer is a function of WHICH BODIES
        /// ARE ALIVE, and that set is rebuilt from the restored bodies by
        /// RestoreState -> PositionHistory.RederiveOccupancy.
        ///
        /// THE OTHER HALF -- THE ROWS -- IS PAID (app-88jb Т25). They are folded
        /// into StateHash and they ride SaveState/RestoreState by deep copy, at
        /// one canonical position between the container slots and the waves, so
        /// a restore now rewinds the recorded past along with the bodies
        /// instead of leaving a rolled-back future standing in the ring.
        /// ⭐ THE EIGHTH LYING COMMENT, AND IT WAS THIS ONE. Т25's audit listed
        /// seven doc sites to correct and every one of them was inside
        /// PositionHistory; this paragraph is the other side of the boundary
        /// that class's own header states, and the header points straight at
        /// it. A statement made in two files goes stale in two files -- said
        /// out loud here because the audit's list was complete for one file and
        /// incomplete for the claim.
        readonly PositionHistory _history;
        // Scratch buffer for SeparationSystem's per-tick pairwise impulses (Task
        // 20) — preallocated here so the hot path never allocates; recomputed
        // from scratch every tick, so it carries no state across ticks and is
        // deliberately excluded from SaveState/RestoreState and StateHash.
        // ⚠ app-88jb Т22 WIDENED IT TO HOLD COLLECTORS TOO (MaxMobs +
        // MaxPlayers, not MaxMobs): from that task the pair scan covers three
        // pair kinds, and a collector indexes past the mob count. The sizing
        // precedent is _projCandidates two lines below, which has budgeted for
        // both populations since Stage 2 Task 17.
        readonly float2[] _sepForces;
        // app-88jb Т22: the SECOND buffer of the same shape, for POSITIONAL
        // separation. It is separate from _sepForces rather than reused,
        // because the two quantities are applied to different fields (Vel and
        // Pos) at different points of the same tick, and folding them would
        // make "displacement" and "impulse" indistinguishable at the one place
        // that has to tell them apart.
        readonly float2[] _sepDisplace;
        // app-88jb Т22: the shove (owner decision Р442). A THIRD buffer rather
        // than a second use of _sepForces, which the SOFT separation above has
        // already spent by the time the hard pass runs: reusing it would make
        // the hard pass silently depend on the soft pass having finished, which
        // is the kind of coupling that survives review once and breaks the
        // tick it is reordered.
        readonly float2[] _sepPush;
        // app-88jb Т22: the per-collector body list handed to
        // BodySeparation.Accumulate, and the map from its slots back to the
        // buffers above. Both preallocated to the same bound as the buffers,
        // for the reason every scratch in this file is: the pair scan runs
        // every tick and must not allocate.
        readonly PushableBody[] _pushBodies;
        // app-88jb Т22 (finding Н-43): the overlapping mob pairs found by the
        // hard pass's ONE broad scan, so the relaxation iterations after it
        // never re-scan the arena. At Arena.MaxMobs 1350 a full scan is 911k
        // pairs, and four of them per tick is 3.6M — measured, not feared: it
        // blew AllocationTests' own 180-second ceiling. Sized to four entries
        // per mob, which is far past what a soft-separated crowd ever produces
        // (bodies are held 2.4 m apart while contact is 1.0 m); if it ever
        // fills, collection stops and the remaining pairs simply wait for the
        // next tick, which is the same graceful degradation SpawnContainer's
        // cap already uses.
        readonly (int a, int b)[] _pairCandidates;
        int _pairCandidateCount;
        // app-88jb Т22: the reciprocals Accumulate hands back, indexed like
        // _pushBodies -- whose slot layout IS the displacement buffers' own
        // (mobs first, then collectors), so CollectorPass scatters them without
        // a map. They exist because the shared routine stays indexed by its OWN
        // input -- teaching it the world's slot layout would be exactly the
        // coupling that keeps it from also serving the client, which has no
        // such layout. (An earlier wording named a `_pushSlot` array; ruling
        // 118 removed it when the pair scan's input was frozen for the tick,
        // and the reference outlived the field -- review round of Т22, M-3.)
        readonly float2[] _pushDisp;
        readonly float2[] _pushVel;
        // app-88jb Т22: how far the hard pass has already moved each collector
        // THIS TICK. Hero.MaxDepenetrationPerTick is a per-tick ceiling rather
        // than a per-iteration one, so the running total has to survive across
        // the relaxation passes — and keeping it here rather than dividing the
        // ceiling by RelaxIterations is what stops one config number from
        // silently changing the meaning of another.
        readonly float2[] _sepPlayerMoved;
        ProjectileState[] _projectiles;
        int _projectileCount;
        // Scratch buffer for ProjectileSystem's per-tick candidate min-scan
        // (Task 5) — preallocated here so the hot path never allocates; every
        // slot is overwritten before being read each tick, so like _sepForces
        // above it carries no state across ticks and is deliberately excluded
        // from SaveState/RestoreState and StateHash. Stage 2 Task 17 sized it
        // to MaxMobs + MaxPlayers + 2 — the exact worst case once the gather
        // fanned out over every live player instead of packing a single one:
        // one slot per live mob, one per live player, plus barrier and floor.
        // Neither owner reaches that bound on its own — a Player-owned round
        // gathers every mob (its OwnerEntityId is always the literal 0, never a
        // live mob id) but skips its own shooter among players, a Mob-owned one
        // (Stage 3 Task 5, spec Р252: friendly fire) gathers every OTHER live
        // mob (its own shooter excluded by id) and every player without
        // exclusion — so BOTH branches land on exactly
        // MaxMobs + MaxPlayers + 2 - 1 candidates, one short of the union.
        // Sizing to the union keeps the bound obvious instead of depending on
        // which branch of the damage matrix a given round took, at the cost of
        // one slot of slack neither branch actually spends.
        // Stage 2 Task 46: + 3 rather than + 2 — the barrier is TWO slots now
        // (interior obstacles/walls, and the ring boundary separately, since
        // only the interior ones have a modelled top). The true worst case rose
        // by exactly one candidate too, so this is not the difference between
        // fitting and throwing: at + 2 the worst-case gather would fill the
        // array to its last slot and still not overflow. It is the one slot of
        // slack described above, kept rather than quietly spent.
        readonly (float t, int kind, int index)[] _projCandidates;
        // Stage 3 Task 3 (spec §3.6): ground pickups — same capped-array/
        // swap-remove shape as _mobs/_projectiles above (rule 4). Sized to
        // Arena.MaxPickups at construction; ArenaTopologyMatches rejects a
        // hot-tweak that changes the cap, same contract as MaxMobs/
        // MaxProjectiles.
        PickupState[] _pickups;
        int _pickupCount;
        // Stage 3 Task 14 (spec §3.7, Р229): containers — same capped-
        // array/swap-remove shape as _pickups above, PLUS the flat
        // per-container slot content. `_containerSlots` is ONE array for
        // every container, sized MaxContainers * MaxContainerSlots and
        // addressed by a container's POSITION in `_containers` (its index,
        // times the fixed block width) — never by `Id`, and never resized:
        // RemoveContainerAt's swap-remove copies the moved container's own
        // block across when it relocates a struct, see that method's own
        // doc. Sized at construction, same ArenaTopologyMatches-guarded
        // immutable-cap contract as every other entity array here.
        ContainerState[] _containers;
        int _containerCount;
        byte[] _containerSlots;
        WaveState[] _waves;
        // Stage 3 Task 1 (spec Ф1, errata E-1/E-2): match-flow phase state.
        // Declared inert by Т1 and driven since Т21 by Objectives.
        // MatchFlowSystem, the last step of TickAll and the ONLY writer of
        // these two fields outside save/restore; Т1
        // only gave the phase a home so every field the extraction economy
        // needs entered StateHash together at the sanctioned re-pin (Т6,
        // done) instead of dribbling in across later Ф1 tasks (errata E-1's
        // structural rebuild). Defaults to Phase = Farm (the enum's zero
        // value) and DirectorDeathTick = 0 ("Director alive or not yet
        // activated") — both already correct as the C# struct default,
        // unlike WaveState's PhaseTicks above, so no explicit constructor
        // init is needed.
        MatchState _match;
        int _nextEntityId = 1;

        readonly SimEvent[] _events;
        int _eventCount;

        public int CurrentTick => _tick;
        /// Synonym for StatsAt(0) (Stage 2 Task 5) — every solo call site that
        /// predates Stage 2 Task 5 keeps compiling unchanged.
        public MatchStats Stats => StatsAt(0);
        /// Stage 2 Task 5 Interfaces: read-only access to one player's personal
        /// match counters by index.
        public MatchStats StatsAt(int index) => _matchStats[index];
        /// Match counters that are counted once for the whole match, not per
        /// player (Stage 2 Task 5) — WavesCleared, MobSpawnsSkipped, ProjectileSpawnsSkipped.
        public WorldStats WorldStats => _worldStats;
        /// The match's own flow phase (Stage 3 Task 1 Interfaces) — one per
        /// match, same "single struct field" shape as WorldStats above.
        /// Read-only here; Т21's state machine mutates it through MatchRef.
        public MatchState Match => _match;
        /// Synonym for PlayerAt(0) (Stage 2 Task 4) — every solo call site that
        /// predates Stage 2 Task 4 keeps compiling unchanged.
        public PlayerState Player => PlayerAt(0);
        /// Number of players in this match (Stage 2 Task 4) — fixed for the
        /// world's whole lifetime, set by the constructor's playerCount parameter.
        public int PlayerCount => _players.Length;
        public SimConfig Config => _config;

        /// Stage 2 Task 4 Interfaces: read-only access to one player's live state by index.
        public PlayerState PlayerAt(int index) => _players[index];

        /// Events emitted since the last ClearEvents() call.
        public int EventCount => _eventCount;
        /// Cumulative count of events dropped because the per-frame buffer was full.
        public int DroppedEvents { get; private set; }

        // Stage 2 Task 10: cumulative tally of edge requests (dash/slide, all
        // players) that the rate limit in PlayerMovementSystem.Update dropped.
        // DIAGNOSTICS ONLY, and deliberately so: it is excluded from StateHash,
        // from WorldSave and from MatchStats, because a dropped request is
        // something the world REFUSED to act on — folding it into any of those
        // three would turn it into world state and make an anti-spam counter
        // part of the replay/rollback contract. The shipped, network-facing
        // counter arrives with NetStats in Stage 2 Task 23/28; this seam exists
        // so EdgeRateLimitTests can observe the drops until then.
        /// bd `app-mi4`: PER PLAYER since the counter finally reached the
        /// network layer, which is what the paragraph above always meant by
        /// "the shipped, network-facing counter arrives with NetStats". A
        /// single total could not be reported per connection without telling
        /// every player about everybody else's dropped requests.
        int[] _rejectedEdgeRequests;

        /// The whole match's drops, which is the quantity `EdgeRateLimitTests`
        /// has always asked about (solo worlds, one player).
        internal int RejectedEdgeRequestsForTest
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _rejectedEdgeRequests.Length; i++)
                    total += _rejectedEdgeRequests[i];
                return total;
            }
        }

        /// One player's own drops (bd `app-mi4`) — read by the server once per
        /// tick into that connection's `NetStats.EdgeRequestsRejected`.
        public int RejectedEdgeRequestsFor(int index) => _rejectedEdgeRequests[index];

        /// `playerCount` defaults to 1 so every call site that predates Stage 2
        /// Task 4 (136 existing constructions) keeps compiling. Where it
        /// SPAWNS that lone player stopped being a special case in Stage 3
        /// Ф5-0 (owner decision R-173): solo takes the one-player point of the
        /// same ring every other lobby size takes (Geometry.SpawnPosFor), the
        /// arena center having become the Director's own ground. A fixture
        /// that wants its player at the origin says so through its config
        /// (TestConfigs.OpenField zeroes PlayerSpawnRingFrac) instead of
        /// leaning on a branch in a production formula.
        public SimulationWorld(long seed, in SimConfig config, int playerCount = 1)
        {
            if (playerCount < 1 || playerCount > config.Arena.MaxPlayers)
            {
                throw new System.ArgumentOutOfRangeException(nameof(playerCount), playerCount,
                    $"SimulationWorld: playerCount must be in [1, {config.Arena.MaxPlayers}] " +
                    "(Arena.MaxPlayers).");
            }
            uint folded = (uint)(seed ^ (seed >> 32));
            // Task 3: two independent streams, each derived from the same folded
            // seed XORed with a stream-specific constant so weapon spread and wave
            // spawns never share (and therefore never perturb) each other's RNG
            // sequence. Fold() re-applies the zero-guard per stream — u-suffixes
            // are required so the XOR operands stay uint (PA9).
            _spreadRng = new Random(Fold(folded ^ 0xB5297A4Du));
            _waveRng = new Random(Fold(folded ^ 0x68E31DA4u));
            // Stage 3 Т6 (spec Р230): the loot stream, folded from the SAME
            // seed with its own constant, exactly like the two above — same
            // per-stream zero-guard, same u-suffix requirement (PA9).
            _lootRng = new Random(Fold(folded ^ 0x1B56C4E9u));
            _config = config;
            _players = new PlayerState[playerCount];
            _sanitizedInputs = new SimInput[playerCount];
            // Stage 2 Task 5: fresh zero-valued MatchStats per player — no
            // explicit per-field init needed, same "struct array defaults are
            // already correct" reasoning the constructor already relies on for
            // _mobs/_projectiles below.
            _matchStats = new MatchStats[playerCount];
            // bd `app-mi4`: one slot per player, allocated with the rest of the
            // per-player scratch and never resized — the roster is fixed for a
            // match (spec §3.1).
            _rejectedEdgeRequests = new int[playerCount];
            // Stage 3 Task 4: one backpack per player, same "allocated with
            // the rest of the per-player scratch, never resized" contract —
            // each instance's own backing array is sized to
            // Hero.MaxInventoryItems inside the loop below.
            _inventories = new Inventory[playerCount];
            // app-88jb Т24 (spec §3.6): the rewind ring, built BEFORE the loop
            // below because that loop rents the collectors' slots -- so slots
            // 0..playerCount-1 belong to the collectors and every mob is
            // numbered after them.
            // Sized by Arena.MaxPlayers, NOT by playerCount: the same
            // "preallocate to the arena's cap, never resize" contract
            // _sepForces/_pushBodies follow below, and the cap is what
            // ArenaTopologyMatches refuses to hot-tweak.
            // Rows = RewindCapTicks + 1: the five ticks a shot may be rewound
            // by, plus the tick it is rewound FROM.
            _history = new PositionHistory(config.Arena.RewindCapTicks + 1,
                config.Arena.MaxMobs + config.Arena.MaxPlayers);
            for (int i = 0; i < playerCount; i++)
            {
                _inventories[i] = new Inventory(config.Hero.MaxInventoryItems);
                float2 pos = Geometry.SpawnPosFor(i, playerCount, in config.Arena);
                float2 vel = float2.zero; // fresh spawn, no inherited velocity
                // Same depenetration seam PlayerMovementSystem/SeparationSystem
                // use — a safety net for the (validated-clean, per
                // SimConfigBuilder's spawn-clearance check) case where an
                // obstacle still overlaps a spawn point.
                Geometry.Depenetrate(ref pos, ref vel, config.Hero.Radius, in config.Arena, 1);
                _players[i] = new PlayerState
                    {
                        Pos = pos, Hp = config.Hero.MaxHp, Stamina = config.Hero.StaminaMax, Alive = true,
                        // Stage 3 Task 2 (spec Р261): the magazine starts full at
                        // the config's own starting count.
                        Ammo = config.Weapon.AmmoStart,
                        // app-88jb Т24 (spec §3.6): the collector's row in the
                        // rewind ring. Rented ONCE, here, and never returned --
                        // a collector's body never leaves _players (KillPlayer
                        // clears Alive, it does not compact the array), so
                        // it never stops occupying its slot.
                        HistorySlot = _history.RentSlot()
                    };
            }
            // Wave director starts idle, counting down to the first wave (Task 22
            // Interfaces) — WavePhase.Waiting is the enum's zero value, but
            // PhaseTicks must be set explicitly or the countdown would start
            // already expired and fire a wave on tick 1.
            //
            // ALL THREE RINGS, one and the same number (bd app-ggvz Т4, spec
            // §3.3): the rings do not need staggered starts because they
            // diverge by themselves on their own pauses (Wave.WavePauseByZone),
            // and a ring left at PhaseTicks 0 would fire a wave on tick 1
            // instead — the very defect this explicit assignment exists to
            // rule out, just moved to the two rings Т3 had no cadence for yet.
            _waves = new WaveState[Zones.Count];
            for (int z = 0; z < Zones.Count; z++)
                _waves[z] = new WaveState
                {
                    Phase = WavePhase.Waiting,
                    PhaseTicks = TicksFromSeconds(config.Wave.FirstWaveDelay)
                };
            _mobs = new MobState[config.Arena.MaxMobs];
            _sepForces = new float2[config.Arena.MaxMobs + config.Arena.MaxPlayers];
            _sepDisplace = new float2[config.Arena.MaxMobs + config.Arena.MaxPlayers];
            _sepPush = new float2[config.Arena.MaxMobs + config.Arena.MaxPlayers];
            _pushBodies = new PushableBody[config.Arena.MaxMobs + config.Arena.MaxPlayers];
            _pairCandidates = new (int a, int b)[config.Arena.MaxMobs * 4];
            _pushDisp = new float2[config.Arena.MaxMobs + config.Arena.MaxPlayers];
            _pushVel = new float2[config.Arena.MaxMobs + config.Arena.MaxPlayers];
            _sepPlayerMoved = new float2[config.Arena.MaxPlayers];
            _projectiles = new ProjectileState[config.Arena.MaxProjectiles];
            _projCandidates = new (float t, int kind, int index)[
                config.Arena.MaxMobs + config.Arena.MaxPlayers + 3];
            // Stage 3 Task 3: same "preallocated to the arena cap, never
            // grown" contract as _mobs/_projectiles above.
            _pickups = new PickupState[config.Arena.MaxPickups];
            // Stage 3 Task 14: same contract — _containers preallocated to
            // MaxContainers, _containerSlots preallocated to the full
            // MaxContainers * MaxContainerSlots block grid, both zero-
            // valued at start (no container, no item, exactly like the
            // arrays above).
            _containers = new ContainerState[config.Arena.MaxContainers];
            _containerSlots = new byte[config.Arena.MaxContainers * config.Arena.MaxContainerSlots];
            _events = new SimEvent[config.Arena.MaxEventsPerFrame];

            // Stage 3 Task 15 (spec §3.7, Interfaces): the ONE legal call
            // site, last line of the constructor — every entity array this
            // task's search touches (_containers/_containerSlots above) and
            // every other one (players, mobs, ...) already exists by this
            // point, and player depenetration (the loop above, spec's own
            // "after player depenetration" requirement) has already run.
            ContainerStore.PlaceStartingContainers(this);
        }

        /// Solo overload (Stage 2 Task 4) — throws for a multiplayer world (spec §3.2): the
        /// pair `Tick(in SimInput)` + `Tick(ReadOnlySpan<SimInput>)` would make
        /// every existing `w.Tick(default)` call ambiguous (CS0121), which is
        /// why the canonical multi-player entry point is named TickAll instead.
        public void Tick(in SimInput input)
        {
            if (_players.Length > 1)
            {
                throw new System.InvalidOperationException(
                    "SimulationWorld.Tick(in SimInput) is the solo overload — call TickAll(ReadOnlySpan<SimInput>) " +
                    $"instead (this world has {_players.Length} players).");
            }
            System.Span<SimInput> single = stackalloc SimInput[1];
            single[0] = input;
            TickAll(single);
        }

        /// Canonical multi-player tick (Stage 2 Task 4 Interfaces). `inputs[i]` is
        /// player i's raw input for this tick; the span must have at least
        /// PlayerCount elements.
        public void TickAll(System.ReadOnlySpan<SimInput> inputs)
        {
            // Fix-round 1 M-1: checked BEFORE any mutation (_tick++ included)
            // — a short span throwing from the indexer mid-loop would leave
            // the world half-ticked (some players moved, some not, tick
            // counter already bumped). Symmetric with the constructor's
            // playerCount guard above.
            if (inputs.Length < _players.Length)
            {
                throw new System.ArgumentException(
                    $"SimulationWorld.TickAll: inputs.Length ({inputs.Length}) must be >= " +
                    $"PlayerCount ({_players.Length}).", nameof(inputs));
            }
            _tick++;
            // Canonical order (brief/context): movement of ALL players by
            // increasing index, THEN weapon of ALL players — two separate
            // loops, not interleaved per player. Player i's weapon phase must
            // see every player's POST-movement position for this tick, not
            // just its own (CanonicalTickOrder_MovementBeforeWeapon).
            for (int i = 0; i < _players.Length; i++)
            {
                _sanitizedInputs[i] = Sanitize(inputs[i], i);
                TickMovement(i, in _sanitizedInputs[i]);
            }
            for (int i = 0; i < _players.Length; i++)
            {
                if (_players[i].Alive)
                    WeaponSystem.Update(this, ref _players[i], in _sanitizedInputs[i], (byte)i);
            }
            // Canonical tick order (spec Interfaces, Task 16/19/20, app-88jb
            // Т22): movement → weapon → mobs (Phase 6) → BODY SEPARATION →
            // projectiles → (waves, Phase 6+). Separation runs right after
            // MobAiSystem so it sees this tick's post-movement positions.
            //
            // ⚠ THE PASS IS NO LONGER MOBS-ONLY AND NO LONGER Vel-ONLY, and
            // this comment said both until the review round of Т22 caught it
            // (finding I-2). It covers three pair kinds now — mob↔mob,
            // collector↔mob and collector↔collector — and its HARD half moves
            // Pos WITHIN THIS TICK, because "bodies do not interpenetrate" is a
            // statement about this tick's positions and a force cannot make it
            // true. Only the SOFT pass keeps the old promise that its Vel
            // addition shows up as motion on the next tick's MoveWithCollisions
            // (see SeparationSystem's own doc, which carries the full
            // renegotiation).
            MobAiSystem.Update(this);
            SeparationSystem.Apply(this, _players);
            ProjectileSystem.Update(this);
            // app-88jb Т5 (spec §3.2): the tilt spring steps HERE -- after
            // this tick's hits are resolved, so a body integrates from the
            // angular impulse it was just given instead of one tick later.
            // Unlike the Vel shove above, which inherits SeparationSystem's
            // one-tick lag because MoveWithCollisions has already run, tilt
            // has no such constraint: nothing earlier in the tick reads it.
            TiltSystem.Apply(this);
            // Runs last (Task 22 Interfaces) so spawns land after this tick's
            // movement/combat has settled — a mob spawned here doesn't get an
            // extra, unbudgeted movement/combat sub-step on its own spawn tick.
            WaveSystem.Update(this);
            // Stage 3 Task 17 (owner decision R-149): the loot channel ticks
            // HERE — after combat, before ContainerStore/PickupSystem. R-2's
            // canonical tail (see PickupSystem's call below) orders LootOps
            // against those two and against MatchFlowSystem; WaveSystem is not
            // in that chain at all and carries its own recorded requirement to
            // run last among the combat systems, so inserting BEFORE it would
            // reopen that decision without writing anything down. Nothing is
            // shared either way: looting never reads mobs, spawning never
            // reads loot timers.
            // Stage 3 Task 18 (coordinator R-162): the SANITIZED inputs ride
            // in as a parameter. The channel's completion re-check has to read
            // the window flag OF THIS TICK (spec §3.8 check 2), and this
            // project's precedent for that is the explicit hand-off
            // WeaponSystem.Update gets above, not a getter over
            // _sanitizedInputs whose only consumer would be one system.
            LootOps.Update(this, _sanitizedInputs);
            // Stage 3 Т23 (spec §3.5 Р256, R-2's canonical tail): the
            // extraction channel ticks HERE — after combat and after the loot
            // channel, before ContainerStore/PickupSystem, and therefore
            // before MatchFlowSystem, which is the last step of all. The order
            // is a rule, not a placement: a collector who fills the last tick
            // of his channel on the very tick a companion walks into the core
            // still gets out, because the portal closes from the NEXT tick
            // (Р256 п.1). Put after the phase machine, that same collector
            // would be caught by a door that shut retroactively.
            Objectives.ExtractionSystem.Update(this);
            // Stage 3 Task 14 (coordinator R-101): container TTL decay slots
            // in HERE, BEFORE PickupSystem — not after. The slot after
            // PickupSystem is reserved for MatchFlowSystem (Т21, see that
            // call's own R-2 doc immediately below): "THE SLOT AFTER THIS
            // CALL BELONGS TO MatchFlowSystem (Т21)… those two [LootOps/
            // ExtractionSystem] insert themselves BEFORE this call
            // instead" — ContainerStore is a third system in that same
            // "inserts before PickupSystem" set, not a fourth claimant on
            // Т21's own slot. Digest-inert today (no fixture spawns a
            // container before this tick runs), which is exactly why the
            // order has to be fixed by doc now rather than left for
            // whichever call compiles first and then have Т21 need to
            // shift a step that already sits at its position — a re-pin
            // with both sanctions already spent.
            ContainerStore.Update(this);
            // Stage 3 Task 3 (owner decision R-2): the canonical tail is
            // combat -> LootOps.Update (Т17, above) -> ExtractionSystem.Update
            // (Т23 — it landed BEFORE this call, not after, and
            // ChannelCompletingOnTheActivationTick_StillGetsOut is its
            // witness) -> PickupSystem.Update (this call) -> MatchFlowSystem.
            // Update (Т21, below). Spec §3.6's own "подбор после машины фазы"
            // phrasing disagrees with this order; R-2 resolves that
            // disagreement in favor of Р256 and the phase machine's own
            // ordering, not the spec sentence.
            PickupSystem.Update(this);
            // Stage 3 Т21: the phase machine, and it is LAST on purpose (Р256)
            // — it reads settled positions and settled mob liveness, so a
            // collector who crossed into the core during this tick activates
            // the Director on this tick, not the next one. See
            // MatchFlowSystem's own doc for the rest of the ordering contract.
            Objectives.MatchFlowSystem.Update(this);
            // app-88jb Т25 (spec §3.6): the rewind ring records the tick that
            // just ended, and it is the LAST line of this method rather than
            // any earlier one. The row for tick T must describe the world as T
            // LEFT it -- that is what makes PosAt's "k == 0 means the live
            // positions" true, and it is a real fork rather than a detail: a
            // write placed before the movement phase would shift every rewound
            // answer by exactly one tick, and RewindTests'
            // HistoryRowOfTickT_HoldsThePositionAtTheEndOfTickT is the witness
            // that would catch it (mutation M32).
            // `_tick` was incremented at the top of this method, so it already
            // names the tick being closed.
            _history.Write(_tick, this);
        }

        /// One player's movement sub-step of TickAll (Stage 2 Task 4 — split out of the
        /// old single-player Tick body so the movement-all-players loop and the
        /// weapon-all-players loop can share it without duplicating the
        /// dash/slide/ricochet event bookkeeping). Death semantics (spec
        /// §3.12, Task 23): once dead, input/dash/weapon are inert — the body
        /// just decelerates under friction and keeps resolving collisions, it
        /// never reacts to input again. AimPoint is part of that: it must stay
        /// pinned at its value at death, not keep tracking the raw
        /// (still-arriving) mouse input — it also feeds HashPlayer, so a live
        /// AimPoint would make the post-death state (and therefore the replay
        /// hash) depend on input the player can no longer act on.
        void TickMovement(int i, in SimInput input)
        {
            ref PlayerState p = ref _players[i];
            if (p.Alive)
            {
                p.AimPoint = input.AimPoint;
                MovementResult moveResult = PlayerMovementSystem.Update(ref p, in input, in _config);
                if (moveResult.DashStarted)
                {
                    _matchStats[i].DashesUsed++;
                    // playerIndex (Stage 2 Task 7): the ACTOR — this player's own
                    // loop index — same for every "own-action" Emit call below.
                    Emit(SimEventKind.PlayerDashed, p.Pos, 0, default, 0f, playerIndex: (byte)i);
                }
                if (moveResult.DashDenied)
                {
                    // Missing cost (Task 9 Interfaces): Update() never touches
                    // Stamina on a denied attempt, so the pre-tick value is
                    // exactly what's still sitting on p right now.
                    Emit(SimEventKind.StaminaDenied, p.Pos, 0, default,
                        _config.Hero.DashStaminaCost - p.Stamina, playerIndex: (byte)i);
                }
                if (moveResult.SlideStarted)
                {
                    _matchStats[i].SlidesUsed++;
                    Emit(SimEventKind.PlayerSlideStarted, p.Pos, 0, default, 0f,
                        hitDir: p.SlideDir, playerIndex: (byte)i);
                }
                if (moveResult.SlideDenied)
                {
                    // Same missing-cost contract as DashDenied above, against
                    // SlideStaminaCost (Task 10).
                    Emit(SimEventKind.StaminaDenied, p.Pos, 0, default,
                        _config.Hero.SlideStaminaCost - p.Stamina, playerIndex: (byte)i);
                }
                // Stage 2 Task 10: a request the edge-request rate limit dropped
                // is counted for diagnostics ONLY — no StaminaDenied, no
                // MatchStats write, nothing that reaches game state (see
                // _rejectedEdgeRequests' own comment for why).
                if (moveResult.DashRejected) _rejectedEdgeRequests[i]++;
                if (moveResult.SlideRejected) _rejectedEdgeRequests[i]++;
                if (moveResult.Ricocheted)
                {
                    // Task 12: Pos is the contact point (not the player's
                    // post-slide position), HitDir the surface normal.
                    Emit(SimEventKind.DashRicocheted, moveResult.RicochetPos, 0, default, 0f,
                        hitDir: moveResult.RicochetNormal, playerIndex: (byte)i);
                }
            }
            else
            {
                PlayerMovementSystem.UpdateDead(ref p, in _config);
            }
        }

        /// Hot-tweak migration (spec §3.9): atomically replaces the balance config on
        /// the tick boundary (caller must only invoke this between ticks). Arena
        /// topology — radius, obstacle count/positions/radii, wall count/endpoints/
        /// half-width, player cap, spawn ring fraction, the three per-match
        /// entity caps, (Stage 3 Task 4, owner decision R-19) the backpack's
        /// two capacity numbers, (Stage 3 Task 13, spec §3.7 Р264) the
        /// item catalog itself and (app-88jb Т24, RULING 134) the rewind
        /// window Arena.RewindCapTicks, which sizes PositionHistory's rows at
        /// construction (see ArenaTopologyMatches below for the full field
        /// list) — must stay identical: a change there invalidates
        /// collision/spawn geometry or array sizing that isn't reconciled here,
        /// so it throws instead; Presentation reacts by restarting the world.
        /// Migration: Hp clamps down to the new max, every player timer clamps into
        /// [0, its new max], and the THREE per-ring wave states (including
        /// each ring's WaveIndex and its PhaseTicks countdown) are left
        /// untouched — bd app-ggvz Т3, spec §3.2's "hot edit" note: a
        /// WavePauseByZone retuned in PlayMode leaves up to three already-
        /// armed countdowns to finish on the old number, and that is accepted
        /// behavior (the same every other timer in this world has), not
        /// drift.
        /// Stage 2 Task 4: migrates every player in the match, not just player 0 — for
        /// a solo world (_players.Length == 1) this is byte-for-byte the same
        /// single migration as before.
        /// app-88jb Т6: the migration is no longer players-only — a second
        /// loop clamps every live MOB's Tilt into the new TiltFallAngle and a
        /// DOWNED mob's StateTimer into the new DownedSeconds. What it
        /// deliberately does NOT migrate is MobAiState itself: a fallen body
        /// neither stands up when the threshold drops nor falls retroactively
        /// when it rises (see the loop's own doc).
        public void ApplyConfig(in SimConfig next)
        {
            if (!ArenaTopologyMatches(in _config.Arena, in next.Arena, in _config.Hero, in next.Hero,
                    _config.Items, next.Items))
            {
                throw new System.ArgumentException("SimulationWorld.ApplyConfig: arena topology " +
                    "changed (radius/obstacles/walls/player cap/spawn ring/entity caps/backpack " +
                    "capacity/item catalog/rewind window) — restart the world instead of " +
                    "hot-tweaking it.");
            }

            _config = next;

            for (int i = 0; i < _players.Length; i++)
            {
                PlayerState p = _players[i];
                p.Hp = math.min(p.Hp, next.Hero.MaxHp);
                p.Stamina = math.clamp(p.Stamina, 0f, next.Hero.StaminaMax);
                p.StaminaRegenDelayTimer = math.clamp(p.StaminaRegenDelayTimer, 0f, next.Hero.StaminaRegenDelay);
                p.DashTimer = math.clamp(p.DashTimer, 0f, next.Hero.DashDuration);
                p.DashCooldown = math.clamp(p.DashCooldown, 0f, next.Hero.DashCooldown);
                // Task 12: a ricochet-decayed DashSpeedCur must not exceed the new
                // config's DashSpeed ceiling — same clamp-to-new-ceiling contract
                // as every other dash timer/value here.
                p.DashSpeedCur = math.clamp(p.DashSpeedCur, 0f, next.Hero.DashSpeed);
                // app-88jb Т22 (Р443): the same reasoning for the slide's own
                // collision penalty — a hot-tweak that lowers SlideSpeed must not
                // leave a penalty bigger than the speed it is subtracted from.
                p.SlideSpeedPenalty = math.clamp(p.SlideSpeedPenalty, 0f, next.Hero.SlideSpeed);
                p.IframeTimer = math.clamp(p.IframeTimer, 0f, next.Hero.DashIframes);
                p.DashBufferTimer = math.clamp(p.DashBufferTimer, 0f, next.Hero.DashBufferWindow);
                p.FireCooldown = math.clamp(p.FireCooldown, 0f, next.Weapon.FireInterval);
                // Stage 3 Task 2 (spec Interfaces): the magazine clamps down to
                // the new AmmoMax ceiling, same hot-tweak contract as every
                // other magnitude in this loop — never clamped UP, a hot-tweak
                // raising AmmoMax does not hand out free ammo.
                p.Ammo = math.min(p.Ammo, next.Weapon.AmmoMax);
                // Task 10: slide timers, same clamp-to-new-ceiling contract as the
                // dash timers above.
                p.SlideTimer = math.clamp(p.SlideTimer, 0f, next.Hero.SlideDuration);
                p.SlideBufferTimer = math.clamp(p.SlideBufferTimer, 0f, next.Hero.SlideBufferWindow);
                p.RunUpTimer = math.clamp(p.RunUpTimer, 0f, next.Hero.RunUpSeconds);
                p.PostDashSlideTimer = math.clamp(p.PostDashSlideTimer, 0f, next.Hero.PostDashSlideWindow);
                p.LinkWindowTimer = math.clamp(p.LinkWindowTimer, 0f, next.Hero.LinkWindowSeconds);
                // Task 14: aim-settle progress, same clamp-to-new-ceiling contract.
                p.AimSettleTimer = math.clamp(p.AimSettleTimer, 0f, next.Hero.AimSettleSeconds);
                // Stage 3 Т23 (spec Р286, debt of this task): the extraction
                // channel is measured against Flow.ExtractChannelSeconds, so a
                // live timer must be clamped when that number is retuned mid-
                // match — otherwise a shortened channel would leave a collector
                // ALREADY past its end without ever having stood there.
                p.ExtractTimer = math.clamp(p.ExtractTimer, 0f, next.Flow.ExtractChannelSeconds);
                // Stage 2 Task 10: the two edge-request counters clamp into
                // [0, the new EdgeRequestMinTicks] — same contract as the timers
                // above, just counted in ticks instead of seconds. Without this,
                // lowering EdgeRequestMinTicks mid-match would leave a player
                // gated by the OLD, longer window until their counter drained.
                p.DashRequestCooldownTicks =
                    math.clamp(p.DashRequestCooldownTicks, 0, next.Hero.EdgeRequestMinTicks);
                p.SlideRequestCooldownTicks =
                    math.clamp(p.SlideRequestCooldownTicks, 0, next.Hero.EdgeRequestMinTicks);
                // Stage 3 Task 17 (errata E-6 A-I8): the loot channel clamps
                // down to the new tier table's longest transfer, same
                // clamp-to-the-new-ceiling contract as every timer above.
                // Its ceiling is an AGGREGATE, not one named number, which is
                // the one way this line differs from its neighbors — the
                // channel's own target tier is not recoverable here (the
                // container may already be gone), so the longest time any
                // tier can ask for is the only honest bound. The aggregate
                // itself lives in LootTransferTimes.Longest, next to the
                // table, and its own doc says why it is a max rather than the
                // last element. Called inside the loop like every neighbor
                // reads its own ceiling inside the loop — hoisting it would
                // make this the ONE line here with a precomputed operand, and
                // there is nothing to buy: ApplyConfig runs on a hot-tweak,
                // not on the tick, over at most Arena.MaxPlayers players and a
                // four-element table. ExtractTimer stays unclamped until Т23
                // gives it behavior and, with it, its ceiling.
                p.LootTimer = math.clamp(p.LootTimer, 0f, LootTransferTimes.Longest(in next.Loot));
                // Stage 3 Task 19: the repair channel clamps down to the new
                // config's own ceiling, same clamp-to-new-ceiling contract
                // as every timer above — and unlike LootTimer's neighbor, a
                // single named number, not an aggregate (there is exactly
                // one repair-kit channel length, not a per-tier table).
                p.RepairTimer = math.clamp(p.RepairTimer, 0f, next.Loot.RepairKitChannelSeconds);
                _players[i] = p;
            }

            // app-88jb Т6 (spec §3.2, finding D-I5): THE MOB HALF OF THE HOT
            // TWEAK. Before this task there was no mob pass here at all --
            // every migration above is a player's -- and the two magnitudes
            // Т5/Т6 gave a mob are the first ones a retune can leave standing
            // outside their own new ceiling.
            //
            // MobConfigFor reads _config, which `_config = next;` above
            // already replaced, so these ARE the new archetype's numbers; it
            // is called rather than a second Type switch written out here,
            // because "which archetype's numbers" has exactly one home
            // (rule 2, the same reason SpawnMob resolves through it).
            //
            // TWO CLAMPS AND NO THIRD:
            //   * Tilt into the new [-TiltFallAngle, TiltFallAngle]. Same
            //     clamp-down-to-the-new-ceiling contract as every player
            //     magnitude above, and it is signed, so the interval is
            //     two-sided rather than [0, max].
            //   * StateTimer of an ALREADY DOWNED body into the new
            //     DownedSeconds -- otherwise a shortened window would leave a
            //     mob lying past an end it can never reach. Only the downed
            //     one: for every other state StateTimer is measured against
            //     that state's own ceiling (TelegraphSeconds, AttackCooldown),
            //     and clamping it to DownedSeconds would corrupt a windup.
            //
            // Ai IS NOT TOUCHED, and that is the rule, not an omission: a mob
            // already down does NOT stand up when the threshold is lowered,
            // and a mob standing does NOT fall retroactively when it is
            // raised. A balance edit must not resurrect or fell bodies; the
            // threshold decides falls at the moment of a blow, in TiltSystem,
            // and nowhere else. ApplyConfig_LoweringTheFallAngle_
            // DoesNotStandTheFallenUp is the witness, and mutation M6b is the
            // form this would go wrong in.
            for (int i = 0; i < _mobCount; i++)
            {
                MobState m = _mobs[i];
                MobSimConfig mcfg = MobConfigFor(m.Type);
                m.Tilt = math.clamp(m.Tilt, -mcfg.TiltFallAngle, mcfg.TiltFallAngle);
                if (m.Ai == MobAiState.Downed)
                    m.StateTimer = math.clamp(m.StateTimer, 0f, mcfg.DownedSeconds);
                _mobs[i] = m;
            }
        }

        /// Unity.Mathematics.Random rejects seed 0 — remaps it to a fixed nonzero
        /// constant (Task 3, same zero-guard as Task 1's single-stream version).
        static uint Fold(uint x) => x == 0 ? 0x9E3779B9u : x;

        static bool ArenaTopologyMatches(in ArenaSimConfig a, in ArenaSimConfig b,
            in HeroSimConfig heroA, in HeroSimConfig heroB, ItemDef[] itemsA, ItemDef[] itemsB)
        {
            if (a.Radius != b.Radius || a.ObstacleCount != b.ObstacleCount) return false;
            for (int i = 0; i < a.ObstacleCount; i++)
            {
                if (!math.all(a.ObstaclePos[i] == b.ObstaclePos[i])) return false;
                if (a.ObstacleRadius[i] != b.ObstacleRadius[i]) return false;
            }
            // Stage 2 Task 14 (spec §3.3 + carryover-t14.md #2, from the Task 12
            // review): interior walls are arena topology exactly like
            // obstacles are — comparing only WallA/WallB and skipping
            // WallHalfWidth would let a corridor-width tuning pass as a
            // hot-tweak while Depenetrate (Task 12) keeps pushing bodies out
            // to the OLD width, silently desyncing collision geometry from
            // what ApplyConfig just accepted.
            if (a.WallCount != b.WallCount) return false;
            for (int i = 0; i < a.WallCount; i++)
            {
                if (!math.all(a.WallA[i] == b.WallA[i])) return false;
                if (!math.all(a.WallB[i] == b.WallB[i])) return false;
                if (a.WallHalfWidth[i] != b.WallHalfWidth[i]) return false;
            }
            // Stage 2 Task 14 (carryover-t14.md #1, deferred from Task 4's
            // review, M-4): MaxPlayers must also match — otherwise a
            // hot-tweak lowering it below the match's actual live player
            // count would silently succeed, leaving the world's player array
            // longer than its own new cap. Requiring an exact match (not
            // just ">= the current player count") forces ANY MaxPlayers
            // change through a restart instead, closing the hole.
            // Fix-round T14 (M-5): an earlier revision of this comment
            // justified the exact match by claiming ApplyConfig "has no way
            // to check [>= playerCount] without threading playerCount
            // through here" — false: `_players.Length` is right there, read
            // by the very loop this method returns into a few lines below.
            // The real reason is simplicity: exact equality keeps this
            // check the SAME shape as every other field compared in this
            // function (Radius, ObstacleCount, WallCount, ...), and it's a
            // strictly SAFER choice than the laxer ">= playerCount" rule,
            // not merely a simpler one — every hot-tweak this stricter
            // check rejects that the laxer one would allow just falls back
            // to a restart, which is always correct, only occasionally
            // more disruptive than strictly necessary. PlayerSpawnRingFrac
            // and the three per-match caps below are topology the same way
            // Radius is: they size arrays / define spawn geometry at
            // construction time, not something ApplyConfig reconciles
            // mid-match.
            // Stage 3 Task 9 (spec Р287): zone walls and their doors are
            // topology exactly like interior walls are (the same reasoning
            // WallHalfWidth's own comment above states) — a hot-tweak moving
            // a door would leave Depenetrate/SweepArena resolving collisions
            // against the OLD opening while the config already claims the
            // new one. The flat door arrays are compared as a whole rather
            // than re-sliced per wall here — the per-wall start/count pair is
            // already checked in the loop below, so re-deriving the same
            // slice a second time would only restate that comparison (rule 2).
            if (a.ZoneWallCount != b.ZoneWallCount) return false;
            for (int i = 0; i < a.ZoneWallCount; i++)
            {
                if (a.ZoneWallRadius[i] != b.ZoneWallRadius[i]) return false;
                if (a.ZoneWallHalfWidth[i] != b.ZoneWallHalfWidth[i]) return false;
                if (a.ZoneWallDoorStart[i] != b.ZoneWallDoorStart[i]) return false;
                if (a.ZoneWallDoorCount[i] != b.ZoneWallDoorCount[i]) return false;
            }
            if (a.DoorCenterRad.Length != b.DoorCenterRad.Length
                || a.DoorFreeWidth.Length != b.DoorFreeWidth.Length)
                return false;
            for (int i = 0; i < a.DoorCenterRad.Length; i++)
            {
                if (a.DoorCenterRad[i] != b.DoorCenterRad[i]) return false;
                if (a.DoorFreeWidth[i] != b.DoorFreeWidth[i]) return false;
            }
            // Spec §3.13 (Р286/Р287): the zone-boundary radii are topology
            // alongside the zone-wall/door geometry just checked above —
            // Geometry.ZoneOf reads them to decide loot tier and wave zone
            // budget, so a hot-tweak moving a boundary mid-match would
            // silently change that semantic without a restart. Named in the
            // spec, missed by Т9's own plan text (coordinator finding,
            // same shape as Т8's R-39 — plan text omits what the spec
            // states).
            if (a.ZoneRadius.Length != b.ZoneRadius.Length) return false;
            for (int i = 0; i < a.ZoneRadius.Length; i++)
                if (a.ZoneRadius[i] != b.ZoneRadius[i]) return false;
            if (a.MaxPlayers != b.MaxPlayers || a.PlayerSpawnRingFrac != b.PlayerSpawnRingFrac)
                return false;
            if (a.MaxMobs != b.MaxMobs || a.MaxProjectiles != b.MaxProjectiles
                || a.MaxEventsPerFrame != b.MaxEventsPerFrame)
                return false;
            // Stage 3 Task 3: MaxPickups joins the three per-match entity
            // caps above — same "backing array sized at construction, cannot
            // grow mid-match" reasoning (the constructor sizes _pickups off
            // exactly this field).
            if (a.MaxPickups != b.MaxPickups) return false;
            // Stage 3 Task 9 (spec Р287): the container caps join MaxPickups
            // above — same "backing array sized at construction, cannot grow
            // mid-match" reasoning (Т13/loot's own containers array, once it
            // exists, sizes off exactly these two fields).
            if (a.MaxContainers != b.MaxContainers) return false;
            if (a.MaxContainerSlots != b.MaxContainerSlots) return false;
            // app-88jb Т24 (spec §3.6, coordinator RULING 134): the rewind cap
            // joins the entity caps above on exactly their reasoning -- the
            // constructor sizes PositionHistory's rows to RewindCapTicks + 1
            // and never resizes them, so a hot-tweak deepening the window
            // would leave the config claiming a depth the ring cannot hold,
            // and every rewound shot past the old depth would silently read a
            // row that belongs to another tick.
            // ⚠ RewindPictureTicks is deliberately NOT here, and the
            // difference is the one this whole list is built on: it sizes
            // nothing. It is a pure number in the k split (spec §3.6), the
            // same kind of mid-match-tunable knob RelaxIterations is, and
            // both are absent from this comparator for the same reason.
            if (a.RewindCapTicks != b.RewindCapTicks) return false;
            // Spec §3.13/§3.15 (Р186/Р287): portals are topology for the
            // same reason BarrierTop below is — the CLIENT draws them from
            // its own copy of the config (Presentation reads ArenaSimConfig
            // to place the greybox), so a hot-tweak moving one would desync
            // the picture from the server exactly the way an unchecked
            // BarrierTop change did (the lesson Р186 records). Named in the
            // spec, missed by Т9's own plan text — same coordinator finding
            // as ZoneRadius above.
            if (a.ExtractPos.Length != b.ExtractPos.Length) return false;
            // Ф2 review B-m4: the three portal arrays are parallel by
            // convention, and SimConfigBuilder enforces it — but a hand-built
            // config never passes through the builder, and this comparator is
            // reached from ApplyConfig on exactly such configs in tests. A
            // comparator must answer false, not throw out of an index.
            if (a.ExtractZone.Length != a.ExtractPos.Length
                || a.ExtractKind.Length != a.ExtractPos.Length
                || b.ExtractZone.Length != b.ExtractPos.Length
                || b.ExtractKind.Length != b.ExtractPos.Length)
                return false;
            for (int i = 0; i < a.ExtractPos.Length; i++)
            {
                if (!math.all(a.ExtractPos[i] == b.ExtractPos[i])) return false;
                if (a.ExtractZone[i] != b.ExtractZone[i]) return false;
                if (a.ExtractKind[i] != b.ExtractKind[i]) return false;
            }
            if (a.ExtractRadius != b.ExtractRadius) return false;
            // Stage 2 Task 46 (bd app-r8x): the interior barriers' modelled
            // height is topology for the same reason WallHalfWidth is — it
            // decides which shots the geometry stops, and there is nothing for
            // ApplyConfig to migrate: rounds already in flight were gathered
            // and gated against the OLD height, and the greybox draws its
            // barriers at it. Letting it through as a hot-tweak would put the
            // picture and the collision out of step silently, which is exactly
            // the mine Task 14 closed one field over.
            if (a.BarrierTop != b.BarrierTop) return false;
            // Stage 3 Task 4 (owner decision R-19, spec Р286/Р287): the
            // backpack's two capacity numbers are topology too, despite
            // living on HeroSimConfig rather than ArenaSimConfig — same
            // "backing array sized at construction, cannot grow mid-match"
            // reasoning as MaxPickups above (Stage 3 Task 3 precedent,
            // HotTweak_MaxPickupsChange_Throws): MaxInventoryItems sizes
            // Loot.Inventory's own byte[] directly. InventoryCapacity earns
            // the same treatment for a different reason (Р286): unlike a
            // float magnitude (Hp, Stamina, ...) that ApplyConfig clamps
            // down continuously, backpack contents are DISCRETE items — a
            // hot-tweak lowering InventoryCapacity below a player's
            // currently occupied slot points has no sound reconciliation
            // (there is no fractional item to partially evict), so ANY
            // change to either number forces a restart instead.
            if (heroA.InventoryCapacity != heroB.InventoryCapacity) return false;
            if (heroA.MaxInventoryItems != heroB.MaxInventoryItems) return false;
            // Stage 3 Task 13 (spec §3.7 Р264, owner decision): the item
            // catalog is topology — its length and every record's SlotCost
            // decide what a wire byte Id and an occupied slot point MEAN in
            // a live world, so a hot-tweak changing it desyncs meaning, not
            // just a number (same reasoning MaxContainerSlots/MaxContainers
            // above already state for the arrays they size). Null-safe by
            // the same "answer false, not throw" contract ExtractZone/
            // ExtractKind's own comparison above follows (Ф2 review B-m4) —
            // a hand-built fixture can reach this comparator with a null
            // array, and a length of -1 (never a real array's length) makes
            // "one null, one not" compare unequal without a special case.
            int itemsALength = itemsA?.Length ?? -1;
            int itemsBLength = itemsB?.Length ?? -1;
            if (itemsALength != itemsBLength) return false;
            for (int i = 0; i < itemsALength; i++)
            {
                if (itemsA[i].Id != itemsB[i].Id || itemsA[i].Tier != itemsB[i].Tier
                    || itemsA[i].SlotCost != itemsB[i].SlotCost
                    || itemsA[i].CreditValue != itemsB[i].CreditValue
                    || itemsA[i].Kind != itemsB[i].Kind)
                    return false;
            }
            return true;
        }

        /// Stage 2 Task 4: takes the sanitizing player's index — every reference point
        /// (AimPoint fallback, Pos-relative AimPoint clamp) must read THAT
        /// player's own state, not always player 0's. Stage 2 Task 6: the body
        /// itself moved verbatim to the public seam SimInputSanitizer.Sanitize
        /// (spec §3.1, client-prediction shares it in Task 30) — this is now
        /// just the world supplying its own per-player state/config to that seam.
        SimInput Sanitize(in SimInput raw, int index) => SimInputSanitizer.Sanitize(raw, _players[index], _config);

        /// Records a VFX/SFX-relevant occurrence for this tick (spec §3.7). The
        /// per-frame buffer is preallocated to Arena.MaxEventsPerFrame; once full,
        /// further events are dropped (no allocation, no growth) and counted
        /// cumulatively in DroppedEvents so overflow is deterministic and visible.
        /// `owner` (F-3 fix-round) is meaningful only for ProjectileFired — every
        /// other call site omits it and gets the default `ProjectileOwner.Player`,
        /// same "unused for every other kind" contract `SimEvent.Owner`'s own doc
        /// describes. `zone`/`hitDir` (Task 6) are meaningful only for the four
        /// blow-carrying kinds (ProjectileHit, MobDied, PlayerDamaged,
        /// PlayerDied); both are optional so the existing call sites that emit
        /// non-blow events keep passing five arguments and get the neutral
        /// HitZone.None / zero direction. `playerIndex` (Stage 2 Task 7) is
        /// meaningful for the seven player-scoped kinds — see
        /// SimEvent.PlayerIndex's own doc for the actor/victim split — and
        /// defaults to ProjectileIds.NoOwner, same "unused for every other
        /// kind" contract as `owner`/`zone`/`hitDir` above. `secondaryEntityId`
        /// (Stage 2 Task 28) is meaningful for ProjectileHit and, since Stage 2
        /// Task 44a, ProjectileHitPlayer — the two kinds whose EntityId is
        /// spent on a victim, see SimEvent.SecondaryEntityId's own doc — and
        /// defaults to 0 ("none"),
        /// same trailing-optional shape as every parameter added before it.
        /// `height` (app-88jb Т3, finding D-C4) is meaningful for
        /// ProjectileHit, ProjectileHitPlayer, ProjectileBlocked and
        /// PlayerDamaged — the kinds a real contact stands behind, see
        /// SimEvent.Height's own doc for the exact "Filled for" list — and
        /// defaults to 0f ("no contact behind this event"), same
        /// trailing-optional shape as secondaryEntityId above.
        /// `birthSteps` (app-88jb Т32, coordinator Ruling 291) is meaningful
        /// for ProjectileFired alone — how many flight steps the round had
        /// already taken when its birth tick ended, see SimEvent.BirthSteps'
        /// own doc — and defaults to 0, the same trailing-optional shape every
        /// parameter added since Т3 has taken.
        internal void Emit(SimEventKind kind, float2 pos, int entityId, MobType mobType, float amount,
            ProjectileOwner owner = ProjectileOwner.Player,
            HitZone zone = HitZone.None, float2 hitDir = default,
            byte playerIndex = ProjectileIds.NoOwner,
            int secondaryEntityId = 0,
            float height = 0f,
            // app-88jb Т8: two more tail parameters with defaults, exactly the
            // way Т3 added `height` — every existing caller keeps compiling and
            // keeps meaning what it meant. `attackerIndex` defaults to NoOwner
            // and NOT to 0: zero is a real seat, so a byte default would have
            // every event of every kind quietly claim collector 0 fired it.
            float impactSpeed = 0f,
            byte attackerIndex = ProjectileIds.NoOwner,
            // app-88jb Т32: one more tail parameter with a default, the same
            // way Т3 added `height` and Т8 added the two above it.
            int birthSteps = 0)
        {
            if (_eventCount < _events.Length)
            {
                _events[_eventCount++] = new SimEvent
                {
                    Kind = kind, Tick = _tick, Pos = pos,
                    EntityId = entityId, MobType = mobType, Amount = amount, Owner = owner,
                    Zone = zone, HitDir = hitDir, PlayerIndex = playerIndex,
                    SecondaryEntityId = secondaryEntityId, Height = height,
                    ImpactSpeed = impactSpeed, AttackerIndex = attackerIndex,
                    BirthSteps = birthSteps
                };
            }
            else
            {
                DroppedEvents++;
            }
        }

        /// WeaponSystem's seam into the weapon-spread RNG stream (Task 3; consumer
        /// lands in Task 15) — Critical Rule: no ad-hoc Unity.Mathematics.Random
        /// instances in Simulation, every draw goes through one of these two seams.
        internal ref Random SpreadRng => ref _spreadRng;

        /// WaveSystem's seam into the wave-director RNG stream (Task 3) — split
        /// from SpreadRng so weapon fire never shifts wave spawn draws.
        internal ref Random WaveRng => ref _waveRng;

        /// The loot-placement stream's seam (Stage 3 Т6, spec Р230), same
        /// ref-return shape as the two above so container layout mutates it in
        /// place instead of round-tripping copies. Its production consumer is
        /// Т15 (container placement); until then the only draws through it are
        /// the ones WorldLifecycleTests makes to prove the stream is really
        /// hashed and really saved — which is exactly the property Т15 will
        /// depend on and cannot verify for itself after the fact.
        internal ref Random LootRng => ref _lootRng;

        /// Combat systems' seam into one player's personal counters (ShotsFired, ...)
        /// (Stage 2 Task 5 — was a single-slot property, now indexed by shooter).
        internal ref MatchStats StatsRef(int index) => ref _matchStats[index];

        /// Stage 3 Task 17: a system's seam into live player storage, same
        /// ref-return shape as StatsRef above and as the `ref _players[i]`
        /// TickAll already hands WeaponSystem. Loot.LootOps.Update needs to
        /// write one field of every player without copying the whole struct
        /// out and back, and it must NOT reach for SetPlayerForTest to do it —
        /// a battle path calling a method named "ForTest" is the exact defect
        /// Loot.PickupSystem.AdvanceTtl's own doc records being fixed.
        internal ref PlayerState PlayerRef(int index) => ref _players[index];

        /// WaveSystem's (and future shared-resource systems') seam into the
        /// match's world-scoped counters (WavesCleared, spawn-skip counts) —
        /// Stage 2 Task 5, same ref-return pattern as StatsRef above, just
        /// unindexed since there is exactly one WorldStats per match.
        internal ref WorldStats WorldStatsRef => ref _worldStats;

        /// ProjectileSystem's seam into live projectile storage (Task 16 sweep resolution).
        internal ProjectileState[] Projectiles => _projectiles;
        internal int ProjectileCount => _projectileCount;

        /// ProjectileSystem's seam into live mob storage (Task 16 damage matrix).
        internal MobState[] Mobs => _mobs;
        internal int MobCount => _mobCount;

        /// Loot.PickupSystem's seam into live pickup storage (Stage 3 Task 3)
        /// — same shape as Projectiles/Mobs above.
        internal PickupState[] Pickups => _pickups;
        internal int PickupCount => _pickupCount;

        /// Loot.ContainerStore's seam into live container storage (Stage 3
        /// Task 14) — same shape as Pickups/PickupCount above. Slot
        /// CONTENT has no array-typed accessor of its own: every reader
        /// goes through ContainerSlotAt/ContainerItemsInto/TryTakeFromContainer,
        /// which resolve the offset once rather than handing out the flat
        /// backing array for every caller to re-derive. (Т27 added the middle
        /// one — the same read addressed by container ID, for the snapshot
        /// assembler on the far side of the assembly boundary; Т32б made it
        /// the only id-addressed form, see its own doc.)
        internal ContainerState[] Containers => _containers;
        internal int ContainerCount => _containerCount;

        /// Whether a Director is standing in the arena right now (Stage 3
        /// Т27, coordinator R-218) — ONE home, two readers on two sides of an
        /// assembly boundary.
        ///
        /// WHY IT IS PUBLIC AND WHY IT IS HERE. `MatchFlowSystem` has asked
        /// this question since Т21 and owns the phase machine that acts on
        /// the answer; Т27 gives it a second reader that cannot see the
        /// first — `SnapshotAssembler` lives in `Ring.Networking`, and the
        /// Match block's `DirectorAlive` bit is NOT derivable from the phase
        /// (MatchWireFlags' own doc: he dies GateDelaySeconds before the
        /// phase moves, and that window is exactly when a client needs to
        /// know). The alternatives were a second copy of this loop over the
        /// networked side's own capture, or an assembly-wide
        /// InternalsVisibleTo — the same two the owner weighed and rejected
        /// for container slots (R-216), answered the same way and for the
        /// same reason: one public accessor beside the ones the assembler
        /// already reads.
        ///
        /// Linear over live mobs, like every other scan here; it runs once
        /// per tick for the phase machine and once per connection for the
        /// frame, against Arena.MaxMobs.
        public bool DirectorAlive
        {
            get
            {
                for (int i = 0; i < _mobCount; i++)
                    if (_mobs[i].Type == MobType.Director) return true;
                return false;
            }
        }

        /// Stage 3 Task 4 Interfaces: number of items currently carried by
        /// one player's backpack.
        public int InventoryCountOf(int playerIndex) => _inventories[playerIndex].Count;

        /// Stage 3 Task 4 Interfaces: the item id at one backpack slot —
        /// meaningful only for slot < InventoryCountOf(playerIndex), same
        /// "no bounds guard beyond the backing array" contract as
        /// PlayerAt/Pickups.
        public byte InventoryItemAt(int playerIndex, int slot) => _inventories[playerIndex].ItemAt(slot);

        /// Stage 3 Task 4 Interfaces: sum of Loot.Inventory.SlotCostOf
        /// across every item this player carries — what TryAddItem checks
        /// against Hero.InventoryCapacity, not InventoryCountOf itself.
        /// Stage 3 Task 13: the catalog is read fresh off _config.Items
        /// every call, same "hot-tweak honored next call" contract Capacity
        /// below already follows (moot for the catalog itself — it is
        /// topology, ArenaTopologyMatches rejects any change — but the seam
        /// stays uniform).
        public int InventoryUsedSlots(int playerIndex) => _inventories[playerIndex].UsedSlots(_config.Items);

        /// Stage 3 Т24 (spec §3.10): the price of one player's backpack,
        /// read through the world's own catalog — the same shape, and for the
        /// same reason, as InventoryUsedSlots above. `_inventories` is
        /// private and `StopMatch` releases the whole world, so the summary
        /// has exactly one moment to ask, and this is the seam it asks
        /// through.
        public int InventoryCreditsOf(int playerIndex)
            => _inventories[playerIndex].CreditsTotal(_config.Items);

        /// Stage 3 Task 4 Interfaces: adds one item to a player's backpack,
        /// refusing (false, backpack byte-for-byte unchanged) once the
        /// item's own SlotCostOf would push UsedSlots past
        /// Hero.InventoryCapacity, or the backing array is already at its
        /// Hero.MaxInventoryItems ceiling. Capacity is read fresh off
        /// _config every call, same "hot-tweak honored next call" contract
        /// Loot.PickupSystem.Collect's own PickupRadius read follows.
        internal bool TryAddItem(int playerIndex, byte itemId)
            => _inventories[playerIndex].TryAdd(itemId, _config.Hero.InventoryCapacity, _config.Items);

        /// Stage 3 Task 17: "would this item fit", asked without adding it —
        /// spec §3.8 check 8. Delegates to Loot.Inventory.CanAdd, the SAME
        /// predicate TryAddItem's own add is gated on (see that method's doc),
        /// reading capacity and catalog fresh off _config exactly as TryAddItem
        /// does, so a hot-tweak is honored the next call for both alike.
        internal bool CanAddItem(int playerIndex, byte itemId)
            => _inventories[playerIndex].CanAdd(itemId, _config.Hero.InventoryCapacity, _config.Items);

        /// Stage 3 Task 4 Interfaces: removes one item by backpack slot —
        /// swap-remove, same idiom as RemovePickupAt/RemoveProjectileAt.
        /// False (itemId left at its default) for a slot outside
        /// [0, InventoryCountOf(playerIndex)).
        internal bool TryRemoveItemAt(int playerIndex, int slot, out byte itemId)
            => _inventories[playerIndex].TryRemoveAt(slot, out itemId);

        /// Test-only seam (Stage 3 Task 4): overwrites a player's whole
        /// backpack with exactly these items, same "direct write" contract
        /// as SetPlayerForTest/SetPickupForTest above.
        internal void SetInventoryForTest(int playerIndex, params byte[] items)
            => _inventories[playerIndex].SetForTest(items);

        /// MobAiSystem's seam into the per-archetype balance numbers (Task 19).
        /// Stage 3 Task 10 (spec Р213/Р251): four-way switch, not the old
        /// Chaser/"everything else" ternary — Elite and Director are the
        /// third and fourth archetype, each with their OWN section
        /// (Core/SimConfig.cs). `default` throws rather than silently
        /// falling back to Gunner: a `MobState.Type` can only ever be one
        /// of the four values SpawnMob below constructs (this is the
        /// single choke point that does), so an unmatched value here means
        /// something upstream is already broken — the same "refuse loudly"
        /// contract SnapshotBlocks.MaxHpFor's own decode-time domain gate
        /// documents, applied at the call site instead of the wire.
        internal MobSimConfig MobConfigFor(MobType type) => MobConfigRefFor(type);

        /// The same answer WITHOUT COPYING (app-88jb Т22, finding Н-43).
        /// MobSimConfig is a fifteen-field struct, and the value-returning
        /// overload above copies all of it on every call — invisible while the
        /// only callers were per-mob, and measured the moment Т22 put config
        /// reads inside a per-PAIR loop: at Arena.MaxMobs the separation scan
        /// asks this question tens of thousands of times a tick, and the copies
        /// alone tripled the full test run.
        ///
        /// ONE HOME FOR THE SWITCH, not two: the value overload delegates here.
        /// Callers that keep the answer around still take the copy (a `ref
        /// readonly` into _config would go stale across a hot-tweak migration);
        /// callers inside a loop take the reference.
        ///
        /// AND SINCE app-88jb Т31 THIS METHOD DELEGATES IN TURN, to
        /// SimConfig.MobConfigFor (coordinator Ruling 259) — the branch moved
        /// onto the type that owns the four fields the moment a second reader
        /// appeared outside this assembly (the client's MobTiltIntegrator,
        /// which rebuilds a struck mob's tilt from the same archetype
        /// numbers). Nothing about the answer changed: the same four cases,
        /// the same throw on a fifth, and the same reference into _config
        /// rather than a copy of it, because an `in` parameter is a readonly
        /// REFERENCE to this very field.
        internal ref readonly MobSimConfig MobConfigRefFor(MobType type)
            => ref SimConfig.MobConfigFor(in _config, type);

        /// SeparationSystem's seam into its preallocated per-tick force buffer
        /// (Task 20) — sized to Arena.MaxMobs + Arena.MaxPlayers since app-88jb
        /// Т22, recomputed every tick, never grown. The collectors occupy the
        /// slots ABOVE the mob count, which is the only reason a single buffer
        /// can serve a scan over three pair kinds.
        internal float2[] SepForces => _sepForces;

        /// The positional twin of SepForces (app-88jb Т22), same size, same
        /// per-tick contract. Two buffers because the two quantities land on
        /// different fields at different points of the tick — see the field's
        /// own doc.
        internal float2[] SepDisplace => _sepDisplace;

        /// The hard pass's velocity buffer and its four scratch companions
        /// (app-88jb Т22) — same size, same "recomputed every tick, never
        /// grown, never hashed" contract as SepForces above.
        internal float2[] SepPush => _sepPush;
        internal PushableBody[] PushBodies => _pushBodies;
        internal (int a, int b)[] PairCandidates => _pairCandidates;
        internal int PairCandidateCount { get => _pairCandidateCount; set => _pairCandidateCount = value; }
        internal float2[] PushDisp => _pushDisp;
        internal float2[] PushVel => _pushVel;
        internal float2[] SepPlayerMoved => _sepPlayerMoved;

        /// ProjectileSystem's seam into its preallocated per-tick candidate
        /// scratch (Task 5) — sized to Arena.MaxMobs + Arena.MaxPlayers + 3
        /// since Stage 2 Task 46 (was + 2 from Stage 2 Task 17, before the
        /// barrier became two slots: interior barriers and the ring boundary),
        /// recomputed every tick, never grown. See the field's own doc above
        /// for where that bound comes from and for why the extra slot is slack
        /// rather than the difference between fitting and throwing.
        internal (float t, int kind, int index)[] ProjCandidates => _projCandidates;

        /// WaveSystem's seam into ONE RING's wave director state (Task 22) —
        /// same ref-return pattern as SpreadRng/WaveRng, so the system
        /// mutates it in place instead of round-tripping copies every tick.
        ///
        /// Wave-cadence-per-zone (bd app-ggvz Т3, spec §3.2): a METHOD taking
        /// the ring, where it used to be a parameterless property over a
        /// single WaveState — each ring runs its own wave now, so "the wave"
        /// is not a thing the world has one of. Callers that legitimately
        /// want a WORLD-level answer walk `for (int z = 0; z < Zones.Count;
        /// z++)` (WaveSystem.Update's clear check) or read the frame's own
        /// aggregate (WorldWave); reading ring zero and calling it the world
        /// is the mistake this shape exists to make visible.
        internal ref WaveState WaveRef(Zone zone) => ref _waves[(int)zone];

        /// Т21's seam into the match's own flow state (Stage 3 Task 1
        /// Interfaces) — the same ref-return seam idiom WaveRef above uses
        /// (the two stopped being a symmetric PAIR when the wave became three
        /// per-ring instances and its seam took a Zone; the match is genuinely
        /// one per raid, so this one stays a property), so the phase state
        /// machine mutates it in place instead of round-tripping copies every
        /// tick.
        internal ref MatchState MatchRef => ref _match;

        /// Spawns a projectile (spec §3.5/§3.6). Capped at Arena.MaxProjectiles —
        /// once full, spawns are skipped and counted rather than growing the array,
        /// keeping the cap degradation allocation-free and deterministic.
        /// `ownerIndex` (Stage 2 Task 7) is the shooter's own PlayerAt index for a
        /// Player-owned shot, else ProjectileIds.NoOwner — required (no default):
        /// both battle call sites (WeaponSystem, MobAiSystem) must say explicitly
        /// who fired. `ownerEntityId` (Stage 3 Task 5, spec Р252) is the shooting
        /// MOB's own entity id for a Mob-owned shot (MobAiSystem passes `m.Id`),
        /// else 0 (WeaponSystem's own literal — see ProjectileState.OwnerEntityId's
        /// own doc for why 0 can never collide with a live mob) — also required,
        /// same "say explicitly" discipline as ownerIndex.
        /// `rewindLeft` (app-88jb Т28, coordinator RULING 208) is the PICTURE
        /// half of the shooter's own rewind depth — the number of steps this
        /// round will ask of the past (ProjectileState.RewindLeft's own doc).
        /// ⛔ IT ARRIVES HERE RATHER THAN BEING ASSIGNED AFTER THE SPAWN, and
        /// the reason is checkable rather than stylistic:
        /// WeaponSystem.SpawnShot calls ProjectileSystem.CatchUp on the very
        /// next line, and Т27's catch-up steps are the round's FIRST steps —
        /// that is, the first steps the depth is meant to pay for. A field set
        /// after the spawn would have arrived too late for every one of them.
        /// Required, no default: both battle call sites say the depth out loud,
        /// and MobAiSystem's zero is a rule with its own note (RULING 177), not
        /// an omission. The test seam is where the trailing default lives.
        /// `birthSteps` (app-88jb Т32, coordinator Ruling 291) is how many
        /// flight steps this round will have taken by the end of the tick it is
        /// born in — the catch-up steps below plus the one ordinary step
        /// ProjectileSystem gives every live round — and it rides out on the
        /// ProjectileFired event so a networked client can seed its tracer
        /// where the round actually IS rather than at the muzzle
        /// (SimEvent.BirthSteps' own doc carries the whole account).
        /// ⛔ IT IS A TRAILING PARAMETER WITH A DEFAULT, unlike `rewindLeft`
        /// above, and the asymmetry is deliberate: the number is knowable
        /// BEFORE the spawn at both battle call sites, but the test seam
        /// SpawnProjectileForTest and its dozens of call sites state their own
        /// geometry in the present and have no shooter's lag to speak of, so 0
        /// — "nothing is known about the birth tick" — is the honest value
        /// there and the one that keeps every one of them compiling unchanged.
        internal int SpawnProjectile(ProjectileOwner owner, byte ownerIndex, int ownerEntityId,
            float2 pos, float2 vel, float height, float velZ, float damage, float radius, float ttl,
            byte rewindLeft, int birthSteps = 0)
        {
            if (_projectileCount >= _projectiles.Length)
            {
                // Stage 2 Task 5: shared arena resource, world-scoped counter.
                _worldStats.ProjectileSpawnsSkipped++;
                return -1;
            }
            int id = _nextEntityId++;
            _projectiles[_projectileCount++] = new ProjectileState
            {
                Id = id, Owner = owner, OwnerIndex = ownerIndex, OwnerEntityId = ownerEntityId,
                Pos = pos, PrevPos = pos, Vel = vel,
                Height = height, PrevHeight = height, VelZ = velZ,
                Damage = damage, Radius = radius, Ttl = ttl,
                RewindLeft = rewindLeft
            };
            // Amount carries the shot's sim-plane velocity angle (Presentation
            // fix-round app-2pl round 2): MuzzleFlashView needs a tick-accurate
            // fire direction, and reading it back off the render-frame's Curr
            // snapshot is wrong during a multi-tick catch-up flush (Curr reflects
            // only the batch's LAST tick, not necessarily the tick this shot fired
            // on) — this event field is the only tick-exact source available.
            // Owner (F-3 fix-round) lets Presentation tell a mob's shot from the
            // player's own — see SimEvent.Owner's doc. Events are excluded from
            // StateHash (spec §3.7), so neither field adds any new
            // determinism/replay surface. playerIndex (Stage 2 Task 7) mirrors
            // ownerIndex exactly — NoOwner for a Mob-owned shot, the shooter's
            // index otherwise — same "unused for every other kind" contract
            // SimEvent.PlayerIndex's own doc describes. birthSteps (app-88jb
            // Т32) rides out the same way and is covered by the same exclusion:
            // it is written into the EVENT and nowhere else — no ProjectileState
            // field takes it, nothing in this method branches on it — so it adds
            // no determinism/replay surface either.
            Emit(SimEventKind.ProjectileFired, pos, id, default, math.atan2(vel.y, vel.x), owner,
                playerIndex: ownerIndex, birthSteps: birthSteps);
            return id;
        }

        /// Removes a projectile by swapping the last slot into its place — O(1),
        /// no shifting, consistent with the _projectileCount pattern above.
        /// Consumer: Task 16 (projectile tick/expiry/hit resolution).
        internal void RemoveProjectileAt(int index)
        {
            _projectiles[index] = _projectiles[--_projectileCount];
        }

        /// Spawns a pickup (spec §3.6). `amount <= 0` is not a pathological
        /// cap-overflow, it is "no drop at all" — refused silently, BEFORE
        /// the cap check and BEFORE _nextEntityId is touched (owner decision
        /// R-18): a drop source configured to zero (TestConfigs' own
        /// Loot.CellsPerMob/CorpseCellFraction, this task's own
        /// golden-safety fixture) must burn no id and leave every hashed
        /// channel exactly as it was — spawning a PickupState with
        /// Amount = 0 would still advance _nextEntityId and, now that
        /// Pickups is in StateHash (Т6), shift the digest for a config that
        /// legitimately drops nothing.
        /// Capped at Arena.MaxPickups exactly like SpawnMob/SpawnProjectile
        /// above: past the cap the NEW drop is skipped and counted
        /// (WorldStats.PickupSpawnsSkipped) — the OLD pickups already on the
        /// ground are never evicted to make room (spec §3.6, Р260: eviction
        /// would take back loot a player already earned). Ttl seeds at
        /// Loot.PickupTtlSeconds (moved off this class's own TEMPORARY
        /// const, Т13, R-3) — not a parameter here for the same reason it
        /// never was: SpawnPickup's own Interfaces signature (kind, pos,
        /// amount) carries no ttl parameter.
        internal int SpawnPickup(PickupKind kind, float2 pos, int amount)
        {
            if (amount <= 0) return -1;
            if (_pickupCount >= _pickups.Length)
            {
                // Stage 3 Task 3: shared arena resource, world-scoped counter
                // (same pattern as MobSpawnsSkipped/ProjectileSpawnsSkipped).
                _worldStats.PickupSpawnsSkipped++;
                return -1;
            }
            int id = _nextEntityId++;
            _pickups[_pickupCount++] = new PickupState
            {
                Id = id, Pos = pos, Kind = kind, Amount = amount, Ttl = _config.Loot.PickupTtlSeconds
            };
            return id;
        }

        /// Removes a pickup by swapping the last slot into its place — O(1),
        /// same swap-remove pattern as RemoveProjectileAt above. Consumer:
        /// Loot.PickupSystem (TTL expiry and auto-pickup collection).
        internal void RemovePickupAt(int index)
        {
            _pickups[index] = _pickups[--_pickupCount];
        }

        // ---------------------------------------------------------------
        // Stage 3 Task 14 (spec §3.7, Р229): containers.
        // ---------------------------------------------------------------

        /// Spawns a container (spec §3.7). `SlotCount` is set to
        /// `items.Length` — the caller (a future task's drop table / corpse
        /// dump) decides how many slots this instance actually offers,
        /// simply by how many items it hands in; the storage layer carries
        /// no per-Kind policy of its own (coordinator R-100 — `Kind` is
        /// read exactly once, by ContainerStore.InitialTtlFor below, never
        /// here). Coordinator R-99 (named refusal, checked and tested):
        /// `items.Length` past `MaxContainerSlots` is refused BEFORE any
        /// mutation — same "guard first, touch nothing on refusal"
        /// contract TickAll's own inputs.Length check follows — because an
        /// unchecked write would run past this container's own reserved
        /// block and corrupt the NEXT container's slots on this flat
        /// array, with no exception and no other observable sign.
        ///
        /// Capped at Arena.MaxContainers exactly like SpawnPickup above:
        /// past the cap the spawn is skipped and counted
        /// (WorldStats.ContainerSpawnsSkipped) — no id consumed, same
        /// "refuse before touching _nextEntityId" contract SpawnPickup's
        /// own doc states for a zero-amount drop.
        internal int SpawnContainer(ContainerKind kind, float2 pos, System.ReadOnlySpan<byte> items)
        {
            if (items.Length > _config.Arena.MaxContainerSlots)
            {
                throw new System.ArgumentException(
                    $"SimulationWorld.SpawnContainer: items.Length ({items.Length}) exceeds " +
                    $"Arena.MaxContainerSlots ({_config.Arena.MaxContainerSlots}) — writing past " +
                    "this container's own block would corrupt its neighbor's slots.", nameof(items));
            }
            if (_containerCount >= _containers.Length)
            {
                _worldStats.ContainerSpawnsSkipped++;
                return -1;
            }
            int id = _nextEntityId++;
            int index = _containerCount++;
            _containers[index] = new ContainerState
            {
                Id = id, Pos = pos, Kind = kind, SlotCount = (byte)items.Length,
                Ttl = ContainerStore.InitialTtlFor(kind, in _config.Loot)
            };
            int offset = index * _config.Arena.MaxContainerSlots;
            for (int i = 0; i < items.Length; i++) _containerSlots[offset + i] = items[i];
            // Coordinator fix-round (Ф3 review A-2/I2): this array position
            // may hold a PREVIOUS occupant's leftover tail bytes past
            // `items.Length` — RemoveContainerAt moves a container's FULL
            // slot-width block, not just its own SlotCount, so a smaller
            // container spawned into a position a larger one just vacated
            // would otherwise read the larger one's own stale byte as a
            // phantom item. `ContainerState`'s own doc ("slots at or past
            // it are never read") is a promise about READERS staying
            // within SlotCount, not a guarantee those bytes are actually
            // zero — this loop is what makes it true regardless.
            for (int i = items.Length; i < _config.Arena.MaxContainerSlots; i++) _containerSlots[offset + i] = 0;
            return id;
        }

        /// Removes a container by swapping the last slot into its place —
        /// O(1), same swap-remove idiom as RemovePickupAt, PLUS the slot
        /// BLOCK (spec Р229): the moved container's own content must
        /// follow it to its new array position, or the position it
        /// vacates keeps stale bytes that the NEXT spawn (or, worse,
        /// nothing at all — the position simply stops being read once the
        /// count shrinks past it) would leave silently attributed to
        /// whichever struct now lives at that index. `Array.Copy` handles
        /// the `index == last` case (removing the LAST container) as a
        /// same-range no-op, same as the struct-array swap one line above.
        internal void RemoveContainerAt(int index)
        {
            int last = --_containerCount;
            _containers[index] = _containers[last];
            int slotWidth = _config.Arena.MaxContainerSlots;
            System.Array.Copy(_containerSlots, last * slotWidth, _containerSlots, index * slotWidth, slotWidth);
        }

        /// Takes one item out of a container slot (spec §3.7/§3.8).
        /// Addressed by the container's own `Id` (a linear search, same
        /// named-refusal-adjacent idiom as ItemCatalogLookup.Find, though
        /// "not found" reads as an ordinary `false` here rather than a
        /// thrown exception — a stale id from a container that already
        /// expired/emptied is exactly as ordinary an outcome as an empty
        /// slot, not a caller bug), then reads/writes by the found
        /// container's POSITION (Р229) — never by `containerId` itself,
        /// which would silently misread a container that isn't at the
        /// position matching its own id (true for every container after
        /// the FIRST one any world ever spawns, since ids start at 1 and
        /// positions start at 0). Consuming: a successful take zeroes the
        /// slot, so a second take of the same slot reads back "empty"
        /// (spec: 0 = пусто) instead of handing out the same item twice.
        ///
        /// ⚠ ASSUMPTION THIS METHOD CANNOT ENFORCE, WITH ITS ADDRESSEE NAMED
        /// (coordinator fix-round Ф3 review A-2/I2, same MaxBodyRadius/
        /// MinCatalogSlotCost shape): `slot` is NOT checked against
        /// `SlotCount` or `Arena.MaxContainerSlots` — a value in
        /// [SlotCount, MaxContainerSlots) reads a guaranteed-zeroed tail
        /// byte (SpawnContainer's own zeroing, R-99's mirror fix) and
        /// correctly returns false, but a value >= MaxContainerSlots reads
        /// (and, on a would-be successful take, ZEROES) a NEIGHBORING
        /// container's own slot — the exact cross-block corruption
        /// SpawnContainer's own named refusal (R-99) exists to prevent from
        /// the write side. `slot` is meant to arrive over the wire as
        /// `LootRequestNet.Slot` (spec §3.8) from an untrusted client — the
        /// server is authoritative (CR 3) and the range check belongs to
        /// that request's own validation. ADDRESSEE — Т17 (spec §3.8 point
        /// 5: "Slot ∈ [0, SlotCount)").
        ///
        /// Stage 3 Task 17 — THE ADDRESSEE HAS PAID. Loot.LootOps.Validate
        /// refuses `slot` outside [0, SlotCount) with its own
        /// LootRefusal.SlotOutOfRange BEFORE the byte is ever read, and the
        /// wire path (Т28 — LootRequestNet) has no caller here TODAY and,
        /// when it lands, reaches this method only through that same
        /// validation.
        /// The assumption stands as an assumption — this method still checks
        /// nothing itself, and a future SECOND caller would inherit the same
        /// obligation — but it now names a check that exists rather than one
        /// that is owed.
        internal bool TryTakeFromContainer(int containerId, int slot, out byte itemId)
        {
            int index = IndexOfContainer(containerId);
            if (index < 0)
            {
                itemId = 0;
                return false;
            }
            int offset = index * _config.Arena.MaxContainerSlots + slot;
            byte item = _containerSlots[offset];
            if (item == 0)
            {
                itemId = 0;
                return false;
            }
            _containerSlots[offset] = 0;
            itemId = item;
            return true;
        }

        /// Stage 3 Task 17: the ONE home of "container Id -> its position in
        /// the array", -1 when no live container carries that id. Extracted
        /// from TryTakeFromContainer above, which is now its first caller —
        /// Loot.LootOps.Validate is the second, and it needs the position
        /// WITHOUT taking anything (spec §3.8 checks 4/5/7 read SlotCount, the
        /// slot byte and Pos before anything moves). A second copy of this
        /// loop is exactly what rule 2 forbids and what ItemCatalogLookup's
        /// own doc records the cost of.
        ///
        /// Linear, like every other id lookup here: the array is capped at
        /// Arena.MaxContainers. ⚠ IT NO LONGER RUNS ONLY ON A REQUEST — that
        /// premise was Т17's and Stage 3 Ф6 outgrew it (gate Ф6, review B-4).
        /// Two per-tick paths reach it now: Loot.LootOps.Update asks
        /// `ContainerIsEmpty` on the tick a transfer completes, and the frame
        /// builder resolves a box before reading its slots. Hence
        /// `ContainerItemsInto` and `ContainerIsEmptyAt` below: a caller with
        /// many slots to read resolves the id ONCE for the whole box instead
        /// of once per slot, which is what the per-slot accessor cost before
        /// them.
        internal int IndexOfContainer(int containerId)
        {
            for (int i = 0; i < _containerCount; i++)
                if (_containers[i].Id == containerId) return i;
            return -1;
        }

        /// Reads a container's slot content by the container's own
        /// POSITION in the array (spec Р229) — 0 = empty. Same "no bounds
        /// guard beyond the backing array" contract as Loot.Inventory.
        /// ItemAt: callers stay within [0, SlotCount) for the container at
        /// `containerIndex`, exactly as every other indexed read in this
        /// codebase already assumes of its own caller.
        internal byte ContainerSlotAt(int containerIndex, int slot)
            => _containerSlots[containerIndex * _config.Arena.MaxContainerSlots + slot];

        /// Every slot of the container with this ID, ascending, into
        /// `destination` — 0 for an empty slot, and zeros throughout for a
        /// container no longer alive (Stage 3 Т27, owner decision R-216, form
        /// R-217; bulk shape from gate Ф6, review B-4). ONE id resolution for
        /// the whole box: the frame builder was paying up to eight scans of
        /// the container array for a single box's mask, every box, every
        /// connection, every tick.
        ///
        /// WHY IT IS PUBLIC. Slot CONTENT had no reader outside this assembly
        /// when this accessor was added: `RenderSnapshot` carried container
        /// metadata and no content, and `ContainerSlotAt` above is `internal`.
        /// (Т32б gave the render frame a flat interior pool of its own — see
        /// `RenderSnapshot.ContainerInteriors` — but that is the RECEIVING
        /// side's copy, filled through this very accessor on the local path.)
        /// Stage 3 spec §3.12 puts the content on the WIRE —
        /// the ContainerSlots block, sent only to a collector inside
        /// LootRadius — and `SnapshotAssembler` lives in `Ring.Networking`.
        /// The owner weighed three routes (grow RenderSnapshot, open the
        /// assembly's internals, or add one public accessor) and chose this
        /// one: it is the same route the backpack already takes through
        /// `InventoryItemAt`/`InventoryCountOf`/`InventoryUsedSlots` right
        /// above, and it costs no per-tick copy of data one connection out of
        /// three needs only while standing next to the box.
        ///
        /// BY ID, NOT BY ARRAY POSITION (R-217), which is what separates it
        /// from `ContainerSlotAt` above rather than merely a widening of it.
        /// A caller outside the simulation holds IDs — a visibility set
        /// carries them — and the position is this class's own business,
        /// resolved through `IndexOfContainer`, the one home of that mapping
        /// since Т17. An unknown id answers zeros rather than throwing: a
        /// container can legally disappear (TTL) between the tick that saw it
        /// and the tick that describes it, which is ordinary rather than
        /// exceptional.
        ///
        /// ⚠ THERE USED TO BE A PER-SLOT FORM BESIDE THIS ONE, and Т32б
        /// retired it (bd `app-ivy5`, owner decision 2026-08-22).
        /// `ContainerItemAt(containerId, slot)` was Т27's original shape; gate
        /// Ф6 (review B-4) moved the frame builder onto this bulk form because
        /// asking per slot cost an id resolution per slot, and after that
        /// nothing in production called the per-slot form at all — its only
        /// callers were the tests pinning the addressing, which now ask the
        /// same four questions of this method. A public entry point kept for
        /// symmetry with `InventoryItemAt` alone is a feature for its own sake
        /// (AGENT.md rule 3), and the addressing it pinned is a property of
        /// the LOOKUP rather than of the arity.
        ///
        /// `destination.Length` IS THE CALLER'S PROMISE, the same one
        /// `ContainerSlotAt` already asks of everyone: at most the container's
        /// own `SlotCount`. The wire's clamp to
        /// `Protocol.SnapshotBlocks.ContainerSlotsMaskWidth` deliberately
        /// stays in the assembler — R-235's whole point is that the format's
        /// ceiling and the world's fact are not one home.
        public void ContainerItemsInto(int containerId, System.Span<byte> destination)
        {
            int index = IndexOfContainer(containerId);
            if (index < 0)
            {
                destination.Clear();
                return;
            }

            for (int i = 0; i < destination.Length; i++)
                destination[i] = ContainerSlotAt(index, i);
        }

        /// One loot request from the wire, validated and — if legal — taken
        /// up, answering with the refusal code the client shows on the slot
        /// it pressed (Stage 3 Т28, spec §3.8, coordinator R-224). The ONE
        /// production entry into Loot.LootOps.Validate/Begin: until this
        /// method neither had a caller outside tests at all, which is why
        /// Begin's own doc has been written since Т17 in terms of "Т28's
        /// networking switch".
        ///
        /// WHY THE PAIR IS NOT CALLED FROM THE NETWORKING LAYER DIRECTLY, in
        /// two parts. First, Validate needs THIS tick's SANITIZED input
        /// (check 2 reads the window flag, and Т20's sanitizer is what forces
        /// that flag back down inside a dash or a slide) — and those live in
        /// this class's own private `_sanitizedInputs`, which LootOps.Update's
        /// doc refuses to put a getter on for a consumer that would be the
        /// only one. Second, Begin ASSUMES Validate has already answered
        /// `None` and re-checks nothing; splitting the two across an assembly
        /// boundary would put that contract in a place where breaking it is
        /// silent and the damage is a world mutation nobody validated.
        ///
        /// THE MOMENT IS THE TICK BOUNDARY, AND THE INPUT IS THE LAST
        /// COMPLETED TICK'S (spec §3.8: "on a tick boundary, in arrival
        /// order"). The caller is a FishNet broadcast handler, which the
        /// package dispatches inside `TimeManager.IncreaseTick`'s own loop
        /// BETWEEN `OnPreTick` and `OnPostTick` (TimeManager.cs:726/734/752 of
        /// the pinned 4.7.2) — i.e. after the last tick this world ran and
        /// before the next one, never inside `TickAll`. `_sanitizedInputs`
        /// therefore holds the inputs of the tick that just finished, which
        /// is the freshest authoritative view there is: the next tick's input
        /// has not been gathered, let alone sanitized. The one-tick lag this
        /// leaves is the same one `SimInputSanitizer.Sanitize` already
        /// documents for its own `reference`.
        ///
        /// `playerIndex` is NOT range-checked, the same contract PlayerAt and
        /// LootOps.Validate's own doc state: it is the server's connection ->
        /// slot mapping, not a wire value. `op`, `containerId` and `slot` ARE
        /// wire values, and every bound on them is Validate's.
        public LootRefusal TryBeginLoot(int playerIndex, LootOp op, int containerId, int slot)
        {
            LootRefusal refusal = LootOps.Validate(this, playerIndex, op, containerId, slot,
                in _sanitizedInputs[playerIndex]);
            if (refusal == LootRefusal.None) LootOps.Begin(this, playerIndex, op, containerId, slot);
            return refusal;
        }

        /// Stage 3 Т29: does this container hold nothing at all? -1 slots
        /// (no such container) answers `false` — a box that is not there is
        /// not an empty box, and the one caller (Loot.LootOps.Update, on the
        /// tick a transfer completes) asks about a container it has just
        /// taken from.
        ///
        /// ⚠ IT IS NOT `SnapshotAssembler.OccupancyMaskOf` UNDER ANOTHER
        /// NAME, and the difference is the reason both exist. That one builds
        /// the WIRE's mask and is clamped to `SnapshotBlocks.
        /// ContainerSlotsMaskWidth` — eight bits, the format's ceiling. This
        /// one is a fact about the WORLD and reads every slot the container
        /// actually has. Today `ArenaConfig.MaxContainerSlots` carries
        /// `[Range(1, 8)]` so the two always agree; the day that range grows,
        /// the wire's answer must stay clamped and this one must not, which
        /// is exactly what a single shared home would make impossible to say.
        internal bool ContainerIsEmpty(int containerId)
        {
            int index = IndexOfContainer(containerId);
            return index >= 0 && ContainerIsEmptyAt(index);
        }

        /// The same question asked of a container whose index the caller has
        /// ALREADY resolved (gate Ф6, review B-3). The predicate itself lives
        /// here and `ContainerIsEmpty` above delegates, so R-235's home does
        /// not split in two: the id form keeps the "no such container is not
        /// an empty container" guard, this one keeps the reading.
        ///
        /// It exists because the one caller resolves the index anyway — it
        /// needs the box's POSITION for the event it may emit — and asking by
        /// id would scan the array a second time for an answer already in
        /// hand. Same "no bounds guard beyond the backing array" contract as
        /// `ContainerSlotAt`: `containerIndex` is a live index.
        internal bool ContainerIsEmptyAt(int containerIndex)
        {
            int slots = _containers[containerIndex].SlotCount;
            for (int i = 0; i < slots; i++)
                if (ContainerSlotAt(containerIndex, i) != 0) return false;
            return true;
        }

        /// Test-only seam (Stage 3 Task 14), same contract as
        /// SetPickupForTest/SetMobForTest above — mutates a live slot
        /// directly, for the reflective hash sweep
        /// (WorldLifecycleTests.EveryPlayerAndStatsFieldAffectsHash) and for
        /// fixtures that need to force a specific Ttl without going through
        /// SpawnContainer's own seeding.
        internal void SetContainerForTest(int index, in ContainerState c) => _containers[index] = c;

        /// Applies projectile damage to a mob (spec Interfaces, Task 16); on death
        /// it swap-removes the mob the same way RemoveProjectileAt does for projectiles.
        /// The mob's Hp/death/MobDied event happen unconditionally — the world keeps
        /// playing out (spec §3.12) — but ShotsHit/Kills route through private
        /// helpers guarded on player Alive, so a projectile fired before death that
        /// connects afterwards still kills the mob without crediting the run's stats.
        /// `dmg` is the POST-multiplier amount (Task 6 — ProjectileSystem applies
        /// the hit zone's multiplier before calling in), `zone`/`dir` describe the
        /// blow and are forwarded to MobDied for Presentation. `ownerIndex` (Stage 2
        /// Task 7, carryover I-2 from the T5 review) is the projectile's shooter
        /// (ProjectileSystem passes proj.OwnerIndex) — ShotsHit/Kills/HeadshotKills
        /// now credit THAT player instead of the former hardcoded player 0.
        /// Deliberately REQUIRED, no default (fix-round 1 I-1): a default here would
        /// silently resurrect the exact "always player 0" hardcode this task exists
        /// to remove, on a PRODUCTION method — the only non-production call site
        /// (TestWorlds.ClearFirstWave) writes an explicit `0` (test default: the
        /// solo player, same convention SpawnProjectileForTest's own default
        /// documents). The `ownerIndex != NoOwner` guard is required
        /// defense-in-depth: today ProjectileSystem's gather phase only ever routes
        /// a Player-owned projectile into this method (a Mob-owned round is only
        /// ever eligible against players, via DamagePlayer — it may of course
        /// reach none of them and hit no player at all, ending instead on a
        /// barrier, the floor or its own expiry), so ownerIndex is never
        /// actually NoOwner on the production path — but crediting must not silently trust
        /// that invariant forever, and an unguarded `_matchStats[NoOwner]` would
        /// also be an out-of-range index on top of the wrong credit.
        /// Stage 2 Task 17 (carryover-t17.md item 2): `ownerIndex` also rides out
        /// on the MobDied event as SimEvent.PlayerIndex — see that field's own
        /// doc for the actor/attacker/victim split it belongs to.
        /// `hitHeight` (app-88jb Т3, finding D-C4) is REQUIRED, no default,
        /// same reasoning as `ownerIndex` above: a default would silently
        /// resurrect "the blow landed at ground level" the moment anything
        /// here started reading it. Today nothing does — MobDied does not
        /// carry a contact height (coordinator Ruling 15: no test requires
        /// one, the spec does not name one, and adding it "for the company"
        /// with ProjectileHit/ProjectileHitPlayer/PlayerDamaged would be a
        /// feature for its own sake, AGENT.md §4.3). The parameter exists so
        /// every call site already states a real number instead of a
        /// coordinated zero, exactly like ProjectileSystem's HitMob branch
        /// does, in case a future task gives MobDied a height of its own.
        /// `projectileMass`/`projectileSpeed3D` (app-88jb Т4, spec §3.2) are the
        /// two halves of the impact the shove below is computed from, and they
        /// ARRIVE AS PARAMETERS rather than being read off the round: this
        /// method never sees a projectile and must not start to. It is also
        /// called where no round exists at all — TestWorlds.ClearFirstWave and
        /// its neighbors clear bodies through this same seam, and a PIERCING
        /// round calls it more than once for a single projectile — a future
        /// tense here until app-88jb Т20 made it the present one
        /// — so the impact behind a blow is the CALLER's fact, not this
        /// method's to reconstruct. Both are REQUIRED, no default, for the
        /// third time in this signature and for the same reason `ownerIndex`
        /// and `hitHeight` are: a default of 0 reads as "a blow with no impact
        /// behind it", which is exactly what the sixteen service call sites
        /// mean and the one thing a real hit must never silently fall back to.
        /// `projectileSpeed3D` is the FULL 3D speed, length(float3(Vel, VelZ)):
        /// WeaponSimConfig.ProjectileSpeed is itself the length of the 3D
        /// vector in this project, so a horizontal-only magnitude would
        /// under-shove every angled shot — Impact.VelocityDelta's own doc
        /// carries the same warning for the same reason.
        internal void DamageMob(int index, float dmg, float2 pos, HitZone zone, float2 dir, byte ownerIndex,
            float hitHeight, float projectileMass, float projectileSpeed3D)
        {
            _mobs[index].Hp -= dmg;
            // Impact (app-88jb Т4, spec §3.2, owner decision Н14). The shove
            // lands in the SAME Vel SeparationSystem.Apply already adds into
            // (SeparationSystem.cs:65) — one more term in an existing sum, not
            // a second movement path. It shows up as motion on the NEXT tick's
            // MoveWithCollisions call, because ProjectileSystem runs AFTER
            // SeparationSystem in TickAll (:388-390): the one-tick lag
            // SeparationSystem's own doc already describes and accepts.
            //
            // BEFORE the death check below, and deliberately so: a mob that
            // dies on this blow is swap-removed a few lines down, so the Vel it
            // was just given is either overwritten by the tail mob or left in a
            // slot past _mobCount when it IS the tail — unreachable either way,
            // and never hashed (HashMob only walks live slots). Branching on
            // "is it still standing" would cost more than the addition it saves.
            //
            // damping is 1: a mob has no cocoon. The collector's divisor is
            // Hero.CocoonDamping and belongs to the blow that lands on HIM
            // (Т7) — see Impact.VelocityDelta's own doc for why the ceiling is
            // applied before that division rather than after.
            MobSimConfig target = MobConfigFor(_mobs[index].Type);
            float dv = Impact.VelocityDelta(projectileMass, projectileSpeed3D,
                target.Mass, target.ImpactSpeedCap, damping: 1f);
            _mobs[index].Vel += dir * dv;
            // The ANGULAR half of the same blow (app-88jb Т5, spec §3.2). The
            // arm is signed, so a hit above the center of mass tips the body
            // ALONG the shot and one below undercuts it -- there is no branch
            // here and there must never be one: the sign falls out of the
            // subtraction.
            //
            // Through Impact.AngularImpulse rather than written inline, and
            // that is a rule rather than a preference (round-3 finding C-I1):
            // THREE places need this one signed subtraction, and one of them is
            // outside Ring.Simulation -- this method, DamagePlayer (Т7) and
            // Ring.Networking.Client.MobTiltIntegrator, which rebuilds a struck
            // mob's tilt on a networked client (Т31). The client's own
            // ImpactPulse of Т9 is an EXPECTED fourth caller rather than a
            // present one: nothing builds that pulse today, and the work is
            // booked to app-7du2 (review round, A-2 -- this comment named
            // Presentation's MobVisual while the plan still put the integrator
            // there, and the owner moved it to the network backend). Even three
            // hand-written copies of one signed arm is the shape round 2 already
            // removed for the spring step.
            //
            // `target` is the archetype config resolved for the shove above,
            // deliberately reused: a second MobConfigFor call here would be a
            // second answer to "which archetype's numbers" in one method.
            //
            // BEFORE the death check, on exactly the shove's own reasoning: a
            // body that dies on this blow shows its tilt to nobody, and the
            // slot it leaves behind is never walked by HashMob.
            _mobs[index].TiltVel += Impact.AngularImpulse(hitHeight, target.CenterOfMassHeight,
                dv, target.TiltGain);
            if (ownerIndex != ProjectileIds.NoOwner) IncrementShotsHit(ownerIndex);
            if (_mobs[index].Hp <= 0f)
            {
                if (ownerIndex != ProjectileIds.NoOwner)
                {
                    IncrementKills(ownerIndex);
                    // Headshot kills count the KILLING blow's zone only: earlier
                    // headshots on the same mob are already reflected in Hp.
                    if (zone == HitZone.Head) IncrementHeadshotKills(ownerIndex);
                }
                Emit(SimEventKind.MobDied, pos, _mobs[index].Id, _mobs[index].Type, dmg,
                    zone: zone, hitDir: dir, playerIndex: ownerIndex);
                // Stage 3 Task 3 (spec §3.6, errata E-6 C-I10): energy-cell
                // drop — arithmetic lives in the ONE shared home
                // Loot.LootDrops.MobDeathCells, KillPlayer's own corpse drop
                // below is the second caller. A zero-configured drop (every
                // golden scenario, TestConfigs' own Loot.CellsPerMob = all
                // zero) is refused by SpawnPickup itself before
                // _nextEntityId moves — see that method's own doc.
                // Stage 3 Task 13 (R-3): MobDeathCells now indexes
                // Loot.CellsPerMob by archetype directly — no MobSimConfig
                // copy needed at this call site any more (CellsOnDeath
                // itself is gone from that struct).
                SpawnPickup(PickupKind.EnergyCell, pos,
                    LootDrops.MobDeathCells(_mobs[index].Type, in _config.Loot));

                // Stage 3 Task 16 (spec §3.7): item drop on death. The
                // Director's own drop is a fixed rule — three tier-3
                // containers plus one separate tier-4 memory-core
                // container — never a DropChance read (coordinator R-126);
                // every other archetype rolls through
                // LootDrops.TryRollMobItemTier, whose own doc carries the
                // golden-risk guard-before-ZoneOf requirement (R-120).
                //
                // Kind = Cache for all four (coordinator fix-round, Ф3
                // review A-1 — corrects this task's own original choice of
                // MobCorpse, recorded here for the reader who follows an
                // old cross-reference). Spec §3.6 names the non-expiring
                // trio "труп сборщика, ящик и тайник… там лежит
                // заработанное" — the guaranteed boss drop and the
                // 1000-credit, once-per-match memory core are exactly that,
                // and MobCorpse's own Ttl (ContainerTtlSeconds, 180s) would
                // let the core expire roughly 90s after the gate opens
                // (GateDelaySeconds). Spec §3.7 itself calls these "three
                // containers" and "a separate container with the memory
                // core", never "a corpse" — "труп моба" in that same
                // section names what an ORDINARY archetype leaves behind
                // when an item drops, a different case entirely (the
                // `else if` branch below, still Kind = MobCorpse). Kind
                // remains skin/spawn-table only (Р229) — Cache is not a
                // new state machine, just the existing permanent-Ttl kind
                // (ContainerStore.InitialTtlFor) applied to a death instead
                // of world-start placement.
                //
                // All four containers land at the SAME `pos` (R-129, spec
                // silent on a spread radius — a new balance number in code
                // would need a data-delivery gate this stage has already
                // spent, Т13). Accepted consequence, not a defect: owner
                // tuning item for milestone В1 (R-105's own open question
                // about a container layout radius covers this too).
                if (_mobs[index].Type == MobType.Director)
                {
                    System.Span<byte> trophyBuf = stackalloc byte[2];
                    for (int c = 0; c < 3; c++)
                    {
                        int n = LootDrops.RollTierItems(3, _config.Items, ref _lootRng, trophyBuf);
                        SpawnContainer(ContainerKind.Cache, pos, trophyBuf.Slice(0, n));
                    }
                    System.Span<byte> core = stackalloc byte[1];
                    core[0] = ItemCatalogLookup.FindByTier(4, _config.Items).Id;
                    SpawnContainer(ContainerKind.Cache, pos, core);
                }
                else if (LootDrops.TryRollMobItemTier(_mobs[index].Type, pos, in _config.Arena,
                             in _config.Loot, ref _lootRng, out byte tier))
                {
                    System.Span<byte> item = stackalloc byte[1];
                    item[0] = ItemCatalogLookup.FindByTier(tier, _config.Items).Id;
                    SpawnContainer(ContainerKind.MobCorpse, pos, item);
                }

                // app-88jb Т24 (spec §3.6): the rewind slot stops being
                // occupied. READ BEFORE THE SWAP, not after -- one line down
                // this index holds the body that used to be at the tail, and
                // returning ITS slot would free a live mob's row while leaking
                // the dead one's.
                _history.ReturnSlot(_mobs[index].HistorySlot);
                _mobs[index] = _mobs[--_mobCount];
            }
        }

        /// Guarded stat increments (spec §3.12): a player's own stats freeze the
        /// tick THAT player dies, even for damage from projectiles already in
        /// flight at that moment. Stage 2 Task 5: indexed by shooter — Stage 2
        /// Task 7: DamageMob above now passes the projectile's actual OwnerIndex
        /// instead of a hardcoded 0, see its own comment. Stage 2 Task 17:
        /// DamagePlayer below is the second caller — a round that lands on
        /// another PLAYER credits its shooter exactly the same way one that
        /// lands on a mob does.
        void IncrementShotsHit(int index) { if (_players[index].Alive) _matchStats[index].ShotsHit++; }
        void IncrementKills(int index) { if (_players[index].Alive) _matchStats[index].Kills++; }
        void IncrementHeadshotKills(int index) { if (_players[index].Alive) _matchStats[index].HeadshotKills++; }

        /// Stage 3 Task 19 (errata E-6/C-I7): the ONE home of "damage
        /// cancels a hold-to-act channel". Т23 adds ExtractTimer to this
        /// SAME method rather than growing a second copy of the rule — the
        /// errata's own text names this requirement by number. `ref`
        /// because the caller already holds one live PlayerState by
        /// reference (DamagePlayer's own `p`) and a copy-in/copy-out here
        /// would be the "lighter copy" this file's other channel code
        /// (Loot.LootOps.Update's own doc) explicitly refuses to write.
        static void AbortChannels(ref PlayerState p)
        {
            p.RepairTimer = 0f;
            // Stage 3 Т23 (spec §3.5 Р222, errata E-6/C-I7): the extraction
            // channel is canceled by damage too — ONE line in the ONE home,
            // exactly as this method's own doc and Т19's promised. Both callers
            // inherit it: DamagePlayer (after both guards, so an i-frame-eaten
            // blow does not break a channel it never landed on) and KillPlayer.
            p.ExtractTimer = 0f;
        }

        /// Applies damage to one player (spec Interfaces, Task 16/23): a
        /// no-op once the player is already dead (spec §3.12 — stats stay frozen and
        /// no further PlayerDamaged/PlayerDied events fire); otherwise active dash
        /// i-frames absorb the hit with no event — unless the caller has already
        /// answered that question against the past (`iframesDecidedByRewind`
        /// below, app-88jb Т28) — else Hp drops and, once it reaches
        /// zero, the player dies exactly once.
        /// `dmg` is the POST-multiplier amount, same contract as DamageMob above;
        /// `zone`/`dir` ride along on PlayerDamaged and, on the killing blow, on
        /// PlayerDied too (the death VFX wants the blow that ended the run).
        ///
        /// Stage 2 Task 17 replaced this method's two hardcoded zeroes with real
        /// parameters:
        /// `victimIndex` is WHO was hit — ProjectileSystem passes the player the
        /// gather phase actually found on the round's path, MobAiSystem passes the
        /// target its own FSM selected (carryover-t17.md item 1: mobs have chosen
        /// the nearest live player since Task 8 while the strike still paid out to
        /// player 0).
        /// `attackerIndex` is WHO landed it — the projectile's OwnerIndex, or
        /// ProjectileIds.NoOwner for a blow no player owns (a mob's round, a
        /// chaser's fist, the KillPlayerForTest seam). Credit is gated on that
        /// sentinel for exactly the reason DamageMob's own `ownerIndex` guard
        /// documents, and here the gate is load-bearing rather than
        /// defense-in-depth: mob-owned blows on a player are the COMMON case, so
        /// an unguarded increment would both credit a nonexistent shooter and
        /// index `_players[NoOwner]` out of range.
        /// Self-damage is impossible by construction — ProjectileSystem's gather
        /// skips a Player-owned round's own owner — so the two indices are never
        /// equal on the production path.
        /// `hitHeight` (app-88jb Т3, finding D-C4) is REQUIRED, no default —
        /// same reasoning as DamageMob's own `ownerIndex`/`hitHeight`: a
        /// default would silently resurrect "the blow landed at ground
        /// level", the exact defect this task removes. Unlike DamageMob's
        /// copy, this one is actually consumed below: it rides out on the
        /// PlayerDamaged event (coordinator Ruling 15 — the doc-list on
        /// SimEvent.Height names PlayerDamaged precisely because it is
        /// emitted from here, not from ProjectileSystem's switch).
        /// `projectileMass`/`projectileSpeed3D` (app-88jb Т7, spec §3.2) are the
        /// two halves of the impact the shove below is computed from — the same
        /// pair, for the same reason and with the same "no default" rule as
        /// DamageMob's own (see its doc above): this method never sees a
        /// projectile and must not start to, it is called where no round exists
        /// at all (KillPlayerForTest and six test fixtures clear bodies through
        /// this same seam), and a defaulted 0 would read as "a blow with no
        /// impact behind it" — which is exactly what those service call sites
        /// MEAN and the one thing a real hit must never silently fall back to.
        /// `projectileSpeed3D` is the FULL 3D speed, length(float3(Vel, VelZ)),
        /// on the same grounds Impact.VelocityDelta's own doc gives.
        /// ⚠ A FIST PASSES 0f, 0f AND THAT IS A DECISION (spec §3.2, plan Т7):
        /// MobAiSystem's contact strike gives no knockback at all. It is stated
        /// at that call site, not defaulted here.
        ///
        /// `iframesDecidedByRewind` (app-88jb Т28, spec §3.6, coordinator
        /// RULING 205) is the ONE parameter here that carries a default, and it
        /// says: "the i-frame question has already been answered, against the
        /// tick the shooter actually saw -- the live timer does not get a
        /// second vote." Only ProjectileSystem's HitPlayer arm ever raises it,
        /// and only when the answer came out of a written history row; a
        /// collector who was vulnerable k ticks ago and dashed since would
        /// otherwise have the guard below cancel a blow the past had already
        /// landed on him.
        /// ⛔ THE GUARD IS NOT REMOVED AND THE DEFAULT IS WHY. Every other
        /// caller -- MobAiSystem's contact strike, the KillPlayerForTest seam,
        /// the fixtures that clear bodies through this path, and an un-rewound
        /// round -- keeps compiling unchanged and keeps being decided by the
        /// guard, which stays the one home of the LIVE rule and stays
        /// defense-in-depth for the rest (its own note below). The rewound shot
        /// is the single case that answers the question upstream, and it is the
        /// single case that says so here.
        /// ⚠ AND IT DOES NOT REACH THE `Alive` GUARD, which is a different
        /// question with a different answer: a dead body takes no damage
        /// whatever the past says.
        internal void DamagePlayer(int victimIndex, byte attackerIndex, float dmg,
            float2 pos, HitZone zone, float2 dir, float hitHeight,
            float projectileMass, float projectileSpeed3D,
            bool iframesDecidedByRewind = false)
        {
            ref PlayerState p = ref _players[victimIndex];
            if (!p.Alive) return;
            if (!iframesDecidedByRewind && p.IframeTimer > 0f) return;

            // Stage 3 Task 19 (spec §3.7, errata E-6/C-I7): AFTER both
            // guards — an absorbed or posthumous "hit" must not reach here,
            // same "only an APPLIED blow counts" contract this method's own
            // credit gate follows a few lines below (Р222 — symmetry with
            // the extraction channel's own i-frame rule, which Т23 will
            // point at this same call).
            AbortChannels(ref p);

            p.Hp -= dmg;
            // Impact against a COLLECTOR (app-88jb Т7, spec §3.2, owner
            // decision Н14/Р393). Same arithmetic and the same one home as
            // DamageMob's shove above -- Impact.VelocityDelta, never inline
            // (coordinator Ruling 1, round-3 finding C-I1) -- with the one
            // difference that IS this body: `damping` is Hero.CocoonDamping
            // where a mob passes 1f. The cocoon is what makes a round read as
            // a stagger rather than as a launch, and the ceiling is applied
            // BEFORE that division, so the collector's effective cap is
            // ImpactSpeedCap / CocoonDamping (VelocityDelta's own doc).
            //
            // INSIDE DamagePlayer, AFTER BOTH GUARDS, and that placement is
            // load-bearing rather than tidy (finding D2-I13). The method
            // returns above on `!Alive` and on a LIVE `IframeTimer > 0`
            // WITHOUT emitting PlayerDamaged -- so an impulse applied over
            // either guard would be a shove the server delivered and the client
            // never heard about, i.e. a guaranteed divergence rather than a
            // balance question. A dash is immune to the blow AND to the
            // shove, together, because the two are decided in one place -- and
            // a blow that `iframesDecidedByRewind` lets through carries both,
            // for that same reason read the other way round (app-88jb Т28).
            //
            // THE SHOVE LANDS IN Vel, NEVER IN Pos, exactly as the mob's
            // does: a body that is shoved keeps traveling for the ticks that
            // follow, a body whose Pos is written jumps once and stops.
            // ProjectileSystem runs AFTER this tick's player movement
            // (TickAll's canonical order), so the impulse resolved on tick T
            // sits in Vel at the END of T and moves the body from T+1 -- the
            // semantics PlayerPrediction.Step mirrors on the client by
            // applying its own ImpactPulse at the end of ITS step for T
            // (finding A2-C5).
            float dv = Impact.VelocityDelta(projectileMass, projectileSpeed3D,
                _config.Hero.Mass, _config.Hero.ImpactSpeedCap, _config.Hero.CocoonDamping);
            p.Vel += dir * dv;
            // The ANGULAR half of the same blow, through the same single home
            // (Impact.AngularImpulse) for the same reason DamageMob uses it.
            // The arm is signed -- a hit above the center of mass tips the
            // body along the shot, one below undercuts it -- and there is no
            // branch here, nor may one appear: the sign falls out of the
            // subtraction.
            //
            // NO KNOCKDOWN THRESHOLD FOLLOWS (Р377). A mob past
            // MobSimConfig.TiltFallAngle enters Downed; HeroSimConfig carries
            // no such angle and is not to be given one, because taking control
            // away from a player because a round landed contradicts ADR-001 §9.
            // TiltSystem's collector pass therefore only integrates.
            //
            // BEFORE the death check below, on the mob's own reasoning, and
            // nothing is stranded by that: TiltSystem's collector pass steps
            // EVERY player's spring, corpse included, so a body felled by this
            // very blow settles back to zero exactly the way
            // PlayerMovementSystem.UpdateDead already lets its Vel decay.
            // Branching on "is he still standing" would cost more than the
            // addition it saves.
            p.TiltVel += Impact.AngularImpulse(hitHeight, _config.Hero.CenterOfMassHeight,
                dv, _config.Hero.TiltGain);
            // Credit sits AFTER both guards on purpose: an absorbed or
            // posthumous round dealt no damage, moved no Hp and emitted no
            // PlayerDamaged, so counting it as a landed hit would inflate the
            // shooter's accuracy against blows the world refused to apply.
            // Only the i-frame half of that is reachable from production today
            // (and both of its directions are pinned by
            // PvpDamageTests.IframesAbsorbPvpDamage); the posthumous half is
            // defense-in-depth: both production callers re-check Alive right
            // before calling in — ProjectileSystem's gather phase gates on
            // player.Alive once per projectile, and MobAiSystem re-runs
            // Targeting.NearestAlivePlayer once per mob per tick — and nothing
            // can kill the victim in between, since each round (and each fist)
            // resolves fully before the next one is looked at.
            // Placement mirrors DamageMob above, where the increment likewise
            // sits next to the Hp write that actually happened — but the
            // resulting RULE is deliberately not the same on both sides, and the
            // symmetry is one of position only. Unlike a mob, a player can refuse
            // the blow (i-frames, or being dead already), so PvE counts every
            // geometric contact while PvP counts only the applied ones: one
            // ShotsHit counter, two observable definitions of "landed". That is
            // the intended reading of accuracy — damage dealt, not rounds that
            // merely arrived — not an oversight to be "fixed" by hoisting this
            // line above the guards.
            if (attackerIndex != ProjectileIds.NoOwner) IncrementShotsHit(attackerIndex);
            _matchStats[victimIndex].DamageTaken += dmg;
            // EntityId/playerIndex (Stage 2 Task 7 decision 5): both carry the
            // VICTIM's index, spec §3.2 — the attacker is deliberately NOT what
            // these TWO report (for a PlayerDamaged/PlayerDied pair the victim
            // is the convention). Until app-88jb Т8 the reason given here was
            // that SimEvent HAD a single player slot; it has two since, and the
            // second one is filled three lines below.
            // app-88jb Т8: the SHOOTER rides beside them in a field of its own,
            // together with the speed the round landed at. Both are already
            // parameters of this method, so nothing is re-derived here. A mob's
            // fist arrives with speed 0 and attacker NoOwner, and that is the
            // truth about a contact strike rather than a gap: it carries no
            // impulse and no player credit.
            Emit(SimEventKind.PlayerDamaged, pos, victimIndex, default, dmg, zone: zone, hitDir: dir,
                playerIndex: (byte)victimIndex, height: hitHeight,
                impactSpeed: projectileSpeed3D, attackerIndex: attackerIndex);

            if (p.Hp <= 0f)
            {
                // Kill credit lives HERE, at the caller of KillPlayer, exactly as
                // it lives inside DamageMob rather than inside the mob's own
                // removal — KillPlayer is shared with the no-damage
                // KillPlayerNoDamage path, which by definition credits nobody.
                if (attackerIndex != ProjectileIds.NoOwner)
                {
                    IncrementKills(attackerIndex);
                    // Same "killing blow's zone only" rule as DamageMob: earlier
                    // headshots on this victim are already reflected in Hp.
                    if (zone == HitZone.Head) IncrementHeadshotKills(attackerIndex);
                }
                // Stage 2 Task 8: death bookkeeping (timers/Alive/DeathTick/
                // PlayerDied) moved into KillPlayer — the single home both this
                // damage-death path and the no-damage KillPlayerNoDamage path
                // now share, instead of each keeping its own copy of the timer list.
                // Fix-round 1 I-1: `pos` (the blow's own origin — e.g. the
                // killing mob's position for a contact strike, MobAiSystem's
                // `w.DamagePlayer(targetIndex, NoOwner, cfg.ContactDamage,
                // m.Pos, ...)`) is forwarded unchanged, so the paired
                // PlayerDamaged/PlayerDied above and below carry the SAME Pos,
                // exactly as before this task.
                KillPlayer(victimIndex, zone, dir, pos);
            }
        }

        /// EVERY TIMER A BODY LEAVING THE FIGHT MUST DROP (Stage 3 Т23, errata
        /// E-6/C-I9). Lifted verbatim out of KillPlayer the moment a SECOND way
        /// of leaving arrived — extraction — because the reason each line
        /// exists is not "he died", it is "he is no longer fighting", and all
        /// of these fields are HASHED: a body left mid-dash or mid-transfer
        /// would carry stale state into the digest and into WorldSave whichever
        /// way it left. Callers: KillPlayer and ExtractionSystem.
        ///
        /// The transfer trio (LootTimer and its target) belongs HERE and not in
        /// AbortChannels, and that distinction is load-bearing: damage must NOT
        /// abort a transfer (spec §3.8 is explicit that this is where it
        /// differs from the extraction channel), but leaving the fight
        /// certainly does.
        internal static void ClearCombatTimers(ref PlayerState p)
        {
            p.DashTimer = 0f;
            // Task 12: DashSpeedCur has no meaning without an active dash
            // (DashTimer == 0 already says "not dashing") — zeroed for the
            // same clean-corpse-read reason as DashTimer itself, unlike
            // DashDir (a heading, deliberately left as-is below).
            p.DashSpeedCur = 0f;
            // app-88jb Т22 (Р443): same rule as DashSpeedCur above — a penalty
            // with no slide to belong to reads as inconsistent state.
            p.SlideSpeedPenalty = 0f;
            p.IframeTimer = 0f;
            // Task 9: Stamina itself freezes for free (UpdateDead never
            // touches it), but the regen-delay countdown is reset so a
            // corpse's PlayerState reads clean, same as the dash timers above.
            p.StaminaRegenDelayTimer = 0f;
            // Task 10 (M11/QD9): every slide timer clears the same way —
            // SlideDir is a heading, not a timer, so (like DashDir) it is
            // deliberately left as-is.
            p.SlideTimer = 0f;
            p.SlideBufferTimer = 0f;
            p.RunUpTimer = 0f;
            p.PostDashSlideTimer = 0f;
            p.LinkWindowTimer = 0f;
            // Task 14: aim-settle progress clears the same way as the
            // other movement timers above — a corpse doesn't keep aiming.
            p.AimSettleTimer = 0f;
            // Stage 2 Task 10: the edge-request counters clear too — a corpse
            // receives no input (PlayerMovementSystem.UpdateDead never runs the
            // gate), so a nonzero leftover would be stale state that nothing
            // ever drains, and it is hashed. Same clean-corpse-read reason as
            // every timer above.
            p.DashRequestCooldownTicks = 0;
            p.SlideRequestCooldownTicks = 0;
            // Stage 3 Task 17 (spec §3.8: "прерывание — ... смерть"; errata
            // E-6 A-I8): the transfer channel dies with its owner — timer AND
            // target, because a target without a running timer is exactly the
            // inconsistent read DashSpeedCur's own doc above warns about.
            // Damage alone does NOT interrupt a transfer (spec §3.8 is
            // explicit that this is where it differs from the extraction
            // channel), so this belongs to death, not to DamagePlayer. It
            // matters more than a movement timer would: all three fields are
            // HASHED (since the Т6 re-pin), so a corpse left mid-channel would
            // carry stale state into the digest and into WorldSave.
            p.LootTimer = 0f;
            p.LootTargetContainerId = 0;
            p.LootTargetSlot = 0;
            // Stage 3 Task 19 (spec §3.8 symmetry, errata E-6/A-I8): the
            // repair channel dies with its owner too — a corpse left
            // mid-channel would carry stale state into the digest and
            // WorldSave, same reason the three lines above already exist.
            // Through AbortChannels rather than a second copy of its body
            // (phase review Ф4, B-4): that method is the ONE home of "this
            // channel is cancellable", and every channel damage cancels,
            // death cancels too — so Т23's ExtractTimer line lands there once
            // and death inherits it for free. The three transfer lines ABOVE
            // stay KillPlayer's own on purpose: damage must NOT abort a
            // transfer (spec §3.8), so they do not belong to that home.
            AbortChannels(ref p);
        }

        /// Stage 2 Task 8: single home for player-death bookkeeping — zeroes
        /// every death-relevant timer (through ClearCombatTimers above, which
        /// Т23 lifted out of this method when extraction became a SECOND way
        /// of leaving the fight), sets Alive=false + DeathTick, and emits
        /// exactly one PlayerDied. Extracted verbatim (same fields, same order,
        /// same values) from DamagePlayer's former death branch above, so a
        /// damage-caused death is byte-for-byte unchanged; KillPlayerNoDamage
        /// below is the second, no-damage caller. `blowPos` (fix-round 1 I-1):
        /// the two callers disagree on what this SHOULD be — DamagePlayer
        /// forwards its own `pos` (the blow's origin, same value the paired
        /// PlayerDamaged event above it already carries), while
        /// KillPlayerNoDamage has no blow at all and passes the victim's own
        /// position instead — so it is a required parameter here, not derived
        /// from `p.Pos` internally (that would have silently dropped the
        /// blow's origin for the damage-death path — the bug fix-round 1
        /// caught: `PlayerDamaged` and `PlayerDied` from the SAME hit used to
        /// carry the same Pos, and briefly didn't). See `SimEvent.Pos`'s own
        /// doc for the reader-facing version of this contract.
        ///
        /// Ф5 gate, review B-2: this block had drifted onto ClearCombatTimers
        /// when Т23 extracted that method — leaving the extraction helper
        /// claiming to set Alive/DeathTick and to take a `blowPos` it has no
        /// parameter for, and leaving this method with no doc at all.
        void KillPlayer(int index, HitZone zone, float2 dir, float2 blowPos)
        {
            ref PlayerState p = ref _players[index];
            p.Alive = false;
            _matchStats[index].DeathTick = _tick;
            ClearCombatTimers(ref p);
            Emit(SimEventKind.PlayerDied, blowPos, index, default, 0f, zone: zone, hitDir: dir,
                playerIndex: (byte)index);
            // Stage 3 Task 3 (spec §3.6, errata E-6 C-I10): the corpse's
            // remaining Ammo rasps out as energy cells — same shared home as
            // DamageMob's drop above (Loot.LootDrops.CorpseCells). p.Ammo is
            // deliberately NOT among the "clean corpse" timers zeroed above —
            // it has no further meaning once Alive is false, but zeroing it
            // BEFORE this line would erase the exact number this drop is
            // computed from, so it still reads whatever the player was
            // carrying at the moment of death. A zero-configured fraction
            // (every golden scenario, TestConfigs' own Loot.CorpseCellFraction
            // = 0) is refused by SpawnPickup itself before _nextEntityId
            // moves — see that method's own doc (owner decision R-18).
            // Stage 3 Task 13 (R-3): CorpseCellFraction moved off
            // WeaponSimConfig into Loot — ShotsPerCell stays on Weapon (the
            // ammo economy, not loot), so the call now takes both sections.
            SpawnPickup(PickupKind.EnergyCell, p.Pos,
                LootDrops.CorpseCells(p.Ammo, in _config.Weapon, in _config.Loot));

            // Stage 3 Task 16 (spec §3.7, С21, coordinator R-123): the
            // corpse holds the WHOLE backpack, created only when
            // non-empty — an unconditional spawn would waste
            // _nextEntityId/_containerCount on every player death, same
            // "refuse before touching _nextEntityId" contract SpawnPickup's
            // own zero-amount guard follows. Inventory.Count can never
            // exceed Arena.MaxContainerSlots (SimConfigBuilder's own
            // MaxContainerSlots >= InventoryCapacity/min(SlotCost) rule),
            // so no clamp/truncation is needed here — SpawnContainer's own
            // named refusal (R-99) is a backstop, not a path this call
            // takes.
            Inventory inv = _inventories[index];
            int invCount = inv.Count;
            if (invCount > 0)
            {
                System.Span<byte> items = stackalloc byte[invCount];
                for (int i = 0; i < invCount; i++) items[i] = inv.ItemAt(i);
                SpawnContainer(ContainerKind.PlayerCorpse, p.Pos, items);
                // Coordinator R-128: the container is now the sole owner of
                // these item ids — leaving them in the live backpack too
                // would let the same item exist twice, both hashed/saved.
                inv.Clear();
            }
        }

        /// Stage 2 Task 8 Interfaces: exits a player from the match with no
        /// damage and no credit to anyone — DamageTaken/Kills/etc. never move,
        /// only KillPlayer's death bookkeeping runs. There is no blow, so
        /// KillPlayer gets the neutral HitZone.None/zero direction (the same
        /// "unused for every other kind" contract Emit's own doc describes for
        /// non-blow event kinds) rather than a simulated hit like
        /// KillPlayerForTest's overkill-damage seam below uses, and the
        /// victim's OWN position as `blowPos` — there is no blow to place, so
        /// the victim's last-known position is the only meaningful value
        /// (fix-round 1 I-1 — see KillPlayer's own doc for why this is a
        /// required parameter rather than an internal `p.Pos` read). Guarded
        /// the same way DamagePlayer's own `if (!p.Alive) return;` is — an
        /// already-dead index is a no-op, not a second PlayerDied/DeathTick
        /// overwrite (fix-round 1 I-3). `index` itself is range-checked first
        /// (fix-round 1 M-8) — this is a public method a future Networking
        /// disconnect handler will call with an externally-sourced index, so
        /// an out-of-range value must fail with a clear
        /// ArgumentOutOfRangeException, not an opaque IndexOutOfRangeException
        /// from deep inside `_players[index]` — same "checked before any
        /// mutation" style the constructor's `playerCount` guard uses. An
        /// already-in-flight projectile owned by this player keeps flying and
        /// dealing damage — DamageMob/DamagePlayer never gate on the SHOOTER's
        /// Alive, only on crediting stats to it (see
        /// IncrementShotsHit/Kills/HeadshotKills above), so no new logic is
        /// needed here for that (task-8-context.md, scope item 3).
        public void KillPlayerNoDamage(int index)
        {
            if (index < 0 || index >= _players.Length)
            {
                throw new System.ArgumentOutOfRangeException(nameof(index), index,
                    $"SimulationWorld.KillPlayerNoDamage: index must be in [0, {_players.Length - 1}] " +
                    "(PlayerCount).");
            }
            if (!_players[index].Alive) return;
            KillPlayer(index, HitZone.None, float2.zero, _players[index].Pos);
        }

        /// THE ONE PRODUCTION WRITER OF MatchPhase.Ended (Stage 3 Т24,
        /// coordinator R-172) — and it is deliberately outside the
        /// simulation's own systems.
        ///
        /// WHY THIS CANNOT BE MatchFlowSystem'S DECISION: a raid ends when
        /// MatchEndPolicy says it does, that class lives in
        /// Ring.Networking.Server, and the assembly reference runs one way —
        /// the simulation neither sees it nor can. The duration limit it
        /// reads is NetConfig.MatchMaxDurationSeconds, which is not part of
        /// SimConfig at all (Р72). So the phase machine only ever READS Ended
        /// (its own first line, Р256 п.2/п.3) and refuses to move a raid that
        /// is over, while the writer is whoever holds the verdict:
        /// MatchServer, through this seam, once per match.
        ///
        /// IDEMPOTENT, AND SILENT ABOUT THE PHASE IT REPLACES. A raid can end
        /// on the very tick its gate would have opened (spec §3.5 п.3: Ended
        /// wins the tie), so overwriting GateOpen — or DirectorActive, or
        /// Farm — is the CORRECT behavior, not a case worth guarding. Calling
        /// it twice changes nothing.
        public void MarkMatchEnded() => _match.Phase = MatchPhase.Ended;

        /// Battle mob spawn (Task 22 Interfaces) — WaveSystem's sole entry point for
        /// turning a validated spawn position into a live mob. Spawned mobs start at
        /// Idle AI, but since Task 19 (Phase 6) MobAiSystem ticks every live mob
        /// unconditionally — a spawned mob is NOT a static target: from the very
        /// next Tick() it settles into Chase/Reposition-Fire like any other mob.
        /// Capped at Arena.MaxMobs: past the cap the spawn is skipped and counted
        /// (MobSpawnsSkipped) rather than growing the array; the caller (WaveSystem)
        /// is responsible for leaving the wave's spawn debt untouched when that
        /// happens so the skipped mob is retried once the cap has room again.
        internal int SpawnMob(MobType type, float2 pos, Zone zone)
        {
            if (_mobCount >= _mobs.Length)
            {
                // Stage 2 Task 5: shared arena resource, world-scoped counter.
                _worldStats.MobSpawnsSkipped++;
                return -1;
            }
            // Stage 3 Task 10 (spec Р251, second of the fourteen two-way
            // branches — the one that briefly masked
            // ProjectileGather_UsesArchetypeRadius_ForElite on RED):
            // resolved through MobConfigFor, not a second, independent
            // ternary — one home for "which archetype's numbers", per rule
            // 2. Read BEFORE _nextEntityId/_mobCount are touched:
            // MobConfigFor throws for an unrecognized type, and resolving
            // it first keeps a rejected spawn from leaking an entity id or
            // committing a half-built array slot.
            float maxHp = MobConfigFor(type).MaxHp;
            int id = _nextEntityId++;
            _mobs[_mobCount++] = new MobState
            {
                Id = id, Type = type, Pos = pos,
                Hp = maxHp,
                Ai = MobAiState.Idle,
                // Deterministic handedness for Gunner strafe / SteerAround's dead-on
                // tangent tiebreak (Task 19 Interfaces) — no RNG needed.
                StrafeSign = (id & 1) == 0 ? 1 : -1,
                // Wave-cadence-per-zone (bd app-ggvz Т1): the ring the
                // CALLER put this mob into -- not derived from `pos` (see
                // SpawnZone's own doc).
                SpawnZone = zone,
                // app-88jb Т24 (spec §3.6): this mob's row in the rewind
                // ring, rented here and returned by DamageMob when the body
                // leaves the array. Reached only PAST the cap refusal at the
                // top of this method, and that ordering is the point: a
                // rejected spawn must leak neither an entity id nor a slot --
                // the same reason MobConfigFor is resolved before
                // _nextEntityId is touched (its own comment above).
                HistorySlot = _history.RentSlot()
            };
            Emit(SimEventKind.MobSpawned, pos, id, type, 0f);
            return id;
        }

        /// Test-only alias for SpawnMob (Task 16 Interfaces, retargeted in Task 22
        /// once the battle spawn path existed — same cap/id/StrafeSign behavior,
        /// named for test call-sites). Now also emits MobSpawned like the battle
        /// path does; no EditMode test asserts a specific MobSpawned count/absence
        /// (checked by grep — call-sites either don't inspect events at all or
        /// ClearEvents() before the window they measure), so this is not a
        /// behavioral change any existing test depends on.
        internal int SpawnMobForTest(MobType type, float2 pos, Zone zone = Zone.Outer) => SpawnMob(type, pos, zone);

        /// Test-only wrapper over SpawnProjectile (Task 16 Interfaces) — same spawn
        /// path production code uses, named for test call-sites. Stage 2 Task 7:
        /// `ownerIndex` defaults to 0 — test default: the solo player (dozens of
        /// Э1 call sites model a solo player's own shot and assert its credit;
        /// a NoOwner default would silently rob them of it). WARNING (fix-round 1
        /// M-2): the default is unconditional — it does NOT infer NoOwner from
        /// `owner == ProjectileOwner.Mob`. A caller spawning a Mob-owned test
        /// projectile MUST pass `ownerIndex: ProjectileIds.NoOwner` explicitly —
        /// omitting it silently leaves OwnerIndex at 0, violating the "Mob ⇒
        /// NoOwner" invariant MobProjectile_HasNoOwnerIndex pins for the real
        /// production path. Stage 2 Task 10 made this load-bearing: OwnerIndex is
        /// part of StateHash from that task on, and the five Mob-owned fixtures
        /// that used to ride the `0` default (HitZoneTests.cs x2,
        /// ProjectileTests.cs x3) now pass ProjectileIds.NoOwner explicitly.
        /// `ownerEntityId` (Stage 3 Task 5, errata E-6 A-I1/A-I2) is a NEW
        /// trailing parameter with a default — every existing call site above
        /// keeps compiling unchanged, defaulting to 0 ("no shooter to exclude"),
        /// correct for a Player-owned fixture and for a Mob-owned one that is
        /// not itself testing the friendly-fire exclusion
        /// (MobFriendlyFireTests.MobRound_DoesNotDamageItsOwnShooter passes the
        /// real shooter's own id explicitly).
        /// `rewindLeft` (app-88jb Т28) is a NEW trailing parameter with a
        /// default, built exactly the way `ownerIndex` above was: every
        /// existing call site keeps compiling unchanged and defaults to 0 — "no
        /// rewound picture at all", which is what a fixture that states its own
        /// geometry in the present wants. A fixture that means to exercise the
        /// rewound question drives a real Tick through WeaponSystem instead,
        /// because the depth has to come from a SHOOTER's input to mean
        /// anything (RewindSplit's own doc on what the direct-call witnesses do
        /// not cover).
        internal int SpawnProjectileForTest(ProjectileOwner owner, float2 pos, float2 vel,
            float height, float velZ, float damage, float radius, float ttl, byte ownerIndex = 0,
            int ownerEntityId = 0, byte rewindLeft = 0)
            => SpawnProjectile(owner, ownerIndex, ownerEntityId, pos, vel, height, velZ,
                damage, radius, ttl, rewindLeft);

        /// Test-only seam (Task 19 Interfaces): kills the player outright via the
        /// normal damage path (overkill amount) so MobAiSystem's "player dead"
        /// branch (all mobs → Idle) can be exercised deterministically. Reports a
        /// Body hit from +X (Task 6 signature ripple): the seam models "something
        /// killed the player", and Body/no-multiplier is the neutral choice — no
        /// caller of this seam asserts on the zone. Stage 2 Task 17 signature
        /// ripple: the victim is player 0 (this seam is the solo "something
        /// killed you" shorthand — a fixture that needs another victim states it
        /// through DamagePlayer directly) and the attacker is
        /// ProjectileIds.NoOwner, because "something" is nobody: crediting a kill
        /// here would invent a killer none of this seam's callers asked for.
        internal void KillPlayerForTest()
            => DamagePlayer(0, ProjectileIds.NoOwner, _config.Hero.MaxHp + 1f, _players[0].Pos,
                HitZone.Body, new float2(1f, 0f), hitHeight: 0f,
                projectileMass: 0f, projectileSpeed3D: 0f);

        /// Test-only seam (Task 8 Interfaces): exposes the private Sanitize step
        /// so tests can assert the AimHeight NaN-map/clamp behavior directly,
        /// without threading it through a full Tick(). Stage 2 Task 4: stays a
        /// thin, single-argument call into index 0 — HostileInput_*/Sanitize_*
        /// tests are not rewritten.
        internal SimInput SanitizeForTest(in SimInput raw) => Sanitize(raw, 0);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// Dev-only mob placeholder spawn for Presentation milestone 2 (spec Interfaces).
        /// Stripped from production builds — the sole public dev-surface method here.
        public int DevSpawnMob(MobType type, float2 pos) => SpawnMobForTest(type, pos, Zone.Outer);
#endif

        public SimEvent GetEvent(int i) => _events[i];

        public void ClearEvents() => _eventCount = 0;

        /// The half of a frame that belongs to WHOEVER IS LOOKING rather than
        /// to the world (Stage 3 Т32б): `ownerIndex`'s backpack, and the
        /// interiors of the boxes within his reach.
        ///
        /// A SEPARATE CALL FROM `CaptureSnapshot`, AND THE OWNER IS A
        /// PARAMETER. `CaptureSnapshot` states in its own doc that this class
        /// has no notion of "the local client's player", and that stays true:
        /// nothing here is asked who is watching, it is TOLD, exactly as
        /// `InventoryItemAt(playerIndex, slot)` is told. What would have
        /// broken the invariant is folding this into the capture and reading
        /// the answer off the frame.
        ///
        /// WHY IT EXISTS. These fields reach a networked client through the
        /// Self and ContainerSlots blocks (spec §3.12 tags 7 and 10). Left
        /// unfilled on the local path, the inventory window would be full over
        /// the wire and blank in the PlayMode the owner tunes in — a system
        /// with a half, which AGENT.md rule 1 refuses.
        ///
        /// AND WHY BY THE SAME RULE THE WIRE USES. Only boxes within
        /// `Loot.LootOps.WithinLootReach` are described, exactly as spec Р238
        /// makes the assembler send them. That is meaning rather than thrift:
        /// the pool lists the boxes a frame DESCRIBES, so a box missing from it
        /// says "nothing known here" — and a local path that described every
        /// box on the map while the networked one described two would make the
        /// same field mean two things. The per-tick copy the owner rejected in
        /// R-216 — the whole `MaxContainers * MaxContainerSlots` table, every
        /// tick, for data one connection in three wants only while standing
        /// over a box — is not taken here either.
        public void CaptureOwnerView(RenderSnapshot target, int ownerIndex)
        {
            if (ownerIndex < 0 || ownerIndex >= _players.Length) return;

            int items = math.min(InventoryCountOf(ownerIndex), target.InventoryItems.Length);
            for (int i = 0; i < items; i++)
                target.InventoryItems[i] = InventoryItemAt(ownerIndex, i);
            target.InventoryItemCount = items;
            target.InventorySlotPoints = InventoryUsedSlots(ownerIndex);

            float2 eye = _players[ownerIndex].Pos;
            int records = 0;
            int pooled = 0;
            System.Span<byte> slots = stackalloc byte[math.max(1, _config.Arena.MaxContainerSlots)];
            for (int i = 0; i < target.ContainerCount; i++)
            {
                ContainerState box = target.Containers[i];
                if (!Loot.LootOps.WithinLootReach(eye, box.Pos, in _config.Loot)) continue;

                int width = math.min(box.SlotCount, slots.Length);
                // The pool is sized `MaxContainers * MaxContainerSlots` and the
                // reach filter can admit at most that many boxes of that many
                // slots, so this cannot overrun — but a promise that holds "by
                // construction" is the kind that stops holding when a cap
                // moves, and asking costs one comparison per box.
                if (pooled + width > target.ContainerInteriorItems.Length) break;

                System.Span<byte> mine = slots.Slice(0, width);
                ContainerItemsInto(box.Id, mine);

                // ONLY THE OCCUPIED SLOTS ARE POOLED, in ascending slot order —
                // the wire's own contract for these bytes and the one a reader
                // walks them by. Pooling the empty ones too would put a zero
                // where the next box's first item belongs, and the mask would
                // no longer index the pool.
                int written = 0;
                for (int slot = 0; slot < width; slot++)
                {
                    if (mine[slot] == 0) continue;
                    target.ContainerInteriorItems[pooled + written] = mine[slot];
                    written++;
                }

                target.ContainerInteriors[records] = new ContainerInterior
                {
                    Id = box.Id,
                    OccupancyMask = Loot.LootOps.OccupancyMaskOf(mine),
                    ItemOffset = pooled,
                    ItemCount = written,
                };
                pooled += written;
                records++;
            }

            target.ContainerInteriorCount = records;
            target.ContainerInteriorItemCount = pooled;
        }

        /// The wave a FRAME carries (spec §3.9 Р318/Р338): ONE WaveState for
        /// the whole world, aggregated from the three rings — never ring
        /// zero's own, and never an array. Keeping the frame single-valued is
        /// what spares the client a per-ring decoder, spares RenderSnapshot's
        /// reflective ArrayCountField guard a new key, and makes aliasing
        /// impossible here by construction (a struct is copied, not shared).
        ///
        /// Four rules, one per field group, each with its own assertion in
        /// WaveCadenceTests:
        ///
        /// * `Phase` — Active if ANY ring is active. A raid with one live wave
        ///   anywhere is a raid in a wave.
        /// * `WaveIndex` — the MAXIMUM difficulty step across the rings.
        ///   Steps only ever grow, so the number the HUD draws stays monotone
        ///   (Р318: a per-ring number would FALL as a collector walked inward,
        ///   and Geometry.ZoneOf is a hard threshold with no hysteresis to
        ///   soften the flicker at a ring boundary).
        /// * `PhaseTicks` — the smallest countdown among the rings that HAVE
        ///   one, i.e. "ticks until the next wave anywhere", and 0 when no
        ///   ring is counting at all. "Has one" is DERIVED, not stored: spec
        ///   §3.3 states that a frozen ring carries PhaseTicks = 0, so the
        ///   number already tells whether the ring is counting and a stored
        ///   flag beside it would be a second home for one fact (Р206). A
        ///   minimum over ALL rings would therefore read an eternal zero — on
        ///   a zoneless arena, where only Outer ever counts, and in the core
        ///   while the Director stands.
        /// * `AliveCount` and the three `Pending` — plain SUMS.
        ///
        /// Derived, and it lives only in the UN-hashed frame: StateHash folds
        /// the three rings themselves (StateHash's own canonical order), so
        /// this aggregate is never a second home for hashed state.
        WaveState WorldWave()
        {
            WaveState world = default;
            for (int z = 0; z < Zones.Count; z++)
            {
                ref WaveState ring = ref _waves[z];
                if (ring.Phase == WavePhase.Active) world.Phase = WavePhase.Active;
                world.WaveIndex = math.max(world.WaveIndex, ring.WaveIndex);
                if (ring.PhaseTicks > 0
                    && (world.PhaseTicks == 0 || ring.PhaseTicks < world.PhaseTicks))
                {
                    world.PhaseTicks = ring.PhaseTicks;
                }
                world.AliveCount += ring.AliveCount;
                world.PendingChaser += ring.PendingChaser;
                world.PendingGunner += ring.PendingGunner;
                world.PendingElite += ring.PendingElite;
            }
            return world;
        }

        /// Copies the current tick's render-relevant state into a preallocated
        /// target — no allocation, safe to call every render frame.
        public void CaptureSnapshot(RenderSnapshot target)
        {
            target.Tick = _tick;
            // Stage 2 Task 4: LocalPlayerIndex is intentionally left untouched
            // here — SimulationWorld has no notion of "the local client's
            // player", that's a Presentation/Networking concept (target
            // allocates with it defaulted to 0, matching the solo assumption
            // every Player-synonym read site from before Stage 2 Task 4 still makes).
            target.PlayerCount = _players.Length;
            System.Array.Copy(_players, target.Players, _players.Length);
            // Stage 2 Task 47a: both per-slot flags of the frame, filled here so
            // the local backend and the networked one describe a slot in the
            // same words. A world in memory has no fog and no packet loss — it
            // holds every seat's state — so `PlayerKnown` is the whole roster,
            // and the roster liveness is simply the world's own `Alive`. The
            // difference only appears on the other backend, where a frame
            // carries what one client was allowed to see.
            for (int i = 0; i < _players.Length; i++)
            {
                target.PlayerKnown[i] = true;
                target.PlayerAliveInMatch[i] = _players[i].Alive;
                // Playtest В1 round two (bd `app-1kei`): and WHY a seat stopped
                // being alive, which the bit above cannot say. Written here
                // beside its sibling rather than left to the reader's own
                // `Players[i].Extracted` — that field is only true for oneself
                // on the networked path, so a picture built on it would work in
                // solo and bury every teammate who made it out.
                target.PlayerExtractedInMatch[i] = _players[i].Extracted;
            }
            target.MobCount = _mobCount;
            System.Array.Copy(_mobs, target.Mobs, _mobCount);
            target.ProjectileCount = _projectileCount;
            System.Array.Copy(_projectiles, target.Projectiles, _projectileCount);
            // Stage 3 Т6 (spec Р294): ground pickups take their canonical
            // place right after the projectiles, same count-then-copy shape
            // as the two entity arrays above.
            target.PickupCount = _pickupCount;
            System.Array.Copy(_pickups, target.Pickups, _pickupCount);
            // Stage 3 Т14: container METADATA only (position/kind/etc, for
            // drawing the prop) — same count-then-copy shape as Pickups
            // above. Slot CONTENT is deliberately NOT copied here, on the
            // same reasoning CaptureSnapshot's own backpack note gives
            // below: it isn't rendered by the interpolated frame, only
            // carried by the ContainerSlots snapshot block (spec §3.12 tag
            // 10, Т25) and only inside LootRadius (Р238) — never by the
            // reliable pair, which carries requests and refusal codes (Т28).
            target.ContainerCount = _containerCount;
            System.Array.Copy(_containers, target.Containers, _containerCount);
            // Stage 3 Т32б: "already emptied" is a world fact about each box,
            // and the frame is where it is delivered rather than stored (see
            // RenderSnapshot.ContainerIsEmpty). Filled here so the local
            // backend and the networked one describe a box in the same words —
            // the same reason PlayerKnown is filled above.
            for (int i = 0; i < _containerCount; i++)
                target.ContainerIsEmpty[i] = ContainerIsEmptyAt(i);
            target.Wave = WorldWave();
            // Stage 3 Т6: the match's flow state, a single plain-struct
            // assignment right after the wave — same shape as WorldStats
            // below, and the same canonical position it holds in StateHash.
            target.Match = _match;
            // Стадия 3, фикс-раунд гейта Ф7 (находка ревью B-2): the phase
            // alone does not say whether the boss is still standing —
            // `DirectorActive` covers the stretch AFTER he falls too, while the
            // sharing window runs, which is why the wire carries a separate bit
            // (R-257). Filled here for the same reason `ContainerIsEmpty` is:
            // so the local backend and the networked one describe the raid in
            // the same words. Left unfilled, solo read "the Director has
            // fallen" over a living Director for the whole phase.
            target.DirectorAlive = DirectorAlive;
            // Stage 2 Task 5: PlayerStats mirrors Players' array-copy pattern
            // above; WorldStats is a single plain-struct assignment, same as Wave.
            System.Array.Copy(_matchStats, target.PlayerStats, _matchStats.Length);
            target.WorldStats = _worldStats;
            // Backpacks are deliberately NOT copied here (Stage 3 Т6). Every
            // other field of RenderSnapshot is a struct or a struct array, so
            // its CopyFrom is a plain assignment/indexed copy — an Inventory
            // is a reference type, and putting one in the render frame would
            // either alias the live world's own backpack into a frame the
            // renderer keeps across ticks, or force a per-frame clone on a
            // path whose whole contract is "no allocation". The backpack's
            // own consumer is the inventory window (Т32, Ф7), which reads the
            // world / the Self snapshot block (spec §3.12 tag 7) rather than
            // the interpolated render frame, so nothing is lost by leaving it
            // out. StateHash and
            // WorldSave — the two places backpacks ARE canonical state — do
            // carry them, at the canonical order's own last position.
        }

        /// Deep-copies the full canonical state (config excluded) for rollback/replay.
        /// Allocates — call outside the hot tick path.
        ///
        /// Stage 3 Т6: the initializer below is written in the canonical order
        /// of spec Р294, the same order StateHash folds the world in — so the
        /// two lists can be read side by side and a field present in one but
        /// missing from the other is visible by position, not only by search.
        public WorldSave SaveState()
        {
            var save = new WorldSave
            {
                Tick = _tick,
                SpreadRng = _spreadRng,
                WaveRng = _waveRng,
                // Stage 3 Т6 (spec Р230/Р294): the loot stream is saved with
                // the other two. Without it a restore would rewind the world
                // but not the stream Т15 draws container positions from, and
                // the replay would diverge at the first draw after the load.
                LootRng = _lootRng,
                NextEntityId = _nextEntityId,
                PlayerCount = _players.Length,
                Players = new PlayerState[_players.Length],
                MobCount = _mobCount,
                Mobs = new MobState[_mobs.Length],
                ProjectileCount = _projectileCount,
                Projectiles = new ProjectileState[_projectiles.Length],
                // Stage 3 Т6: pickups join the two entity arrays above, same
                // "whole backing array copied, live count carried beside it"
                // contract.
                PickupCount = _pickupCount,
                Pickups = new PickupState[_pickups.Length],
                // Stage 3 Т14: containers join the entity arrays above, same
                // "whole backing array copied, live count carried beside it"
                // contract — the slot content array copies whole too
                // (ContainerSlots), not just up to any one container's
                // SlotCount, so a restore doesn't have to re-derive offsets.
                ContainerCount = _containerCount,
                Containers = new ContainerState[_containers.Length],
                ContainerSlots = new byte[_containerSlots.Length],
                // app-88jb Т25: the rewind ring's own position in this
                // initializer is HELD, not filled — its two arrays are
                // allocated and copied by _history.SaveTo below, because their
                // lengths are the ring's own dimensions and handing those out
                // would give its shape a second home. Said here rather than
                // left blank: this initializer is one of the three lists
                // WorldSave's doc promises can be read side by side, and a
                // silent gap at a canonical position is exactly what that
                // promise exists to make impossible.
                // Wave-cadence-per-zone (bd app-ggvz Т3): one WaveState per
                // ring, same "fresh array here, filled by Array.Copy below"
                // contract as Mobs/Projectiles/Pickups above. A plain
                // `Waves = _waves` would hand the save a REFERENCE to the
                // live array and every later tick would rewrite the
                // "snapshot" underneath its holder.
                Waves = new WaveState[_waves.Length],
                // Stage 3 Т6: the match's flow state, right after the wave.
                Match = _match,
                WorldStats = _worldStats,
                Stats = new MatchStats[_matchStats.Length],
                // Stage 3 Task 4: one slot per player, same length contract
                // as Players/Stats above. LAST, per spec Р294 — see the
                // field's own doc in WorldSave.
                Inventories = new Inventory[_inventories.Length]
            };
            System.Array.Copy(_players, save.Players, _players.Length);
            System.Array.Copy(_mobs, save.Mobs, _mobs.Length);
            System.Array.Copy(_projectiles, save.Projectiles, _projectiles.Length);
            System.Array.Copy(_pickups, save.Pickups, _pickups.Length);
            System.Array.Copy(_containers, save.Containers, _containers.Length);
            System.Array.Copy(_containerSlots, save.ContainerSlots, _containerSlots.Length);
            // app-88jb Т25 (coordinator RULING 146): both halves of the rewind
            // ring, deep-copied, at their canonical position between the
            // container slots and the waves. One call rather than two
            // Array.Copy lines like the neighbors above, because the ring's
            // layout stays inside PositionHistory (rule 2).
            _history.SaveTo(save);
            System.Array.Copy(_waves, save.Waves, _waves.Length);
            // Stage 2 Task 5: Stats mirrors Players' array-copy pattern above.
            System.Array.Copy(_matchStats, save.Stats, _matchStats.Length);
            // Stage 3 Task 4: Inventory is a reference type — unlike the
            // struct arrays above, System.Array.Copy would only copy
            // REFERENCES, aliasing the live world's own backpacks into the
            // save instead of deep-copying them (WorldSave's own "deep
            // copy" contract, see this class's own doc). Clone() allocates
            // a fresh instance per player instead.
            for (int i = 0; i < _inventories.Length; i++) save.Inventories[i] = _inventories[i].Clone();
            return save;
        }

        public void RestoreState(WorldSave save)
        {
            // Fix-round 1 M-2: without this, a PlayerCount mismatch surfaces
            // as a bare ArgumentException out of Array.Copy below with no
            // indication of why — PlayerCount now earns its keep as a real
            // cross-check instead of just mirroring Players.Length.
            if (save.PlayerCount != _players.Length)
            {
                throw new System.ArgumentException(
                    $"SimulationWorld.RestoreState: save.PlayerCount ({save.PlayerCount}) must match " +
                    $"this world's PlayerCount ({_players.Length}).", nameof(save));
            }
            // T5 fix-round 1 M-2: PlayerCount matching doesn't guarantee the
            // backing arrays themselves are the right length — a hand-built
            // WorldSave (Networking, Stage 2 Task 7+) could get PlayerCount right
            // and still hand in a short Players/Stats array, which would
            // otherwise surface as a bare, unexplained exception straight out of
            // Array.Copy below.
            if (save.Players.Length != _players.Length)
            {
                throw new System.ArgumentException(
                    $"SimulationWorld.RestoreState: save.Players.Length ({save.Players.Length}) must " +
                    $"match this world's PlayerCount ({_players.Length}).", nameof(save));
            }
            if (save.Stats.Length != _matchStats.Length)
            {
                throw new System.ArgumentException(
                    $"SimulationWorld.RestoreState: save.Stats.Length ({save.Stats.Length}) must " +
                    $"match this world's PlayerCount ({_matchStats.Length}).", nameof(save));
            }
            // Stage 3 Task 4: same cross-check as Players/Stats above,
            // guarding the same hand-built-WorldSave scenario their own
            // comments describe.
            if (save.Inventories.Length != _inventories.Length)
            {
                throw new System.ArgumentException(
                    $"SimulationWorld.RestoreState: save.Inventories.Length ({save.Inventories.Length}) " +
                    $"must match this world's PlayerCount ({_inventories.Length}).", nameof(save));
            }
            _tick = save.Tick;
            _spreadRng = save.SpreadRng;
            _waveRng = save.WaveRng;
            _lootRng = save.LootRng;
            _nextEntityId = save.NextEntityId;
            System.Array.Copy(save.Players, _players, _players.Length);
            _mobCount = save.MobCount;
            System.Array.Copy(save.Mobs, _mobs, _mobs.Length);
            // app-88jb Т24 (coordinator RULING 133): the rewind ring's
            // occupancy, re-derived here because both of its inputs -- the
            // players above and the mobs on the line above this one -- have
            // just been restored.
            //
            // THIS IS NOT SYNCHRONIZING TWO COPIES OF THE SAME FACT. Who owns
            // which slot is stored in exactly ONE place, on the bodies
            // themselves (MobState.HistorySlot / PlayerState.HistorySlot),
            // and the save carries it because the save carries the bodies.
            // PositionHistory's occupancy set is an INDEX over that one copy,
            // not a second copy of it -- so it is rebuilt from the source
            // rather than saved beside it, and there is no second version of
            // the truth that could survive a restore out of step with the
            // first. The alternative -- an allocator whose answer depends on
            // the order slots were released in -- would have been real state
            // living outside the save, and a rolled-back run would have
            // handed the next spawn a different slot than a straight one:
            // HistorySlot is hashed, so the two digests would part company
            // (WorldLifecycleTests.SaveRestore_ReplaysToSameHash is the
            // witness that measured it).
            _history.RederiveOccupancy(this);
            _projectileCount = save.ProjectileCount;
            System.Array.Copy(save.Projectiles, _projectiles, _projectiles.Length);
            // Stage 3 Т6: pickups restore exactly like the two entity arrays
            // above — no length cross-check of their own, same as Mobs/
            // Projectiles, whose backing arrays are sized from the same
            // ArenaSimConfig caps ApplyConfig refuses to hot-tweak.
            _pickupCount = save.PickupCount;
            System.Array.Copy(save.Pickups, _pickups, _pickups.Length);
            // Stage 3 Т14: containers restore exactly like Pickups above —
            // no length cross-check of their own, same immutable-topology
            // reasoning (MaxContainers/MaxContainerSlots are guarded by
            // ArenaTopologyMatches, same as every other entity cap here).
            _containerCount = save.ContainerCount;
            System.Array.Copy(save.Containers, _containers, _containers.Length);
            System.Array.Copy(save.ContainerSlots, _containerSlots, _containerSlots.Length);
            // app-88jb Т25: the ring's ROWS and STAMPS come back here, at the
            // same canonical position SaveState wrote them. This is the other
            // half of the restore RederiveOccupancy above performs — that call
            // rebuilds WHICH SLOTS ARE TAKEN from the restored bodies, this one
            // brings back WHAT THOSE SLOTS RECORDED. Neither is derivable from
            // the other, which is why the ring needed both.
            _history.RestoreFrom(save);
            // The other half of SaveState's own no-aliasing contract: the
            // live array is FILLED from the save, never replaced by it — a
            // `_waves = save.Waves` would leave the world writing into the
            // holder's snapshot from the next tick on.
            System.Array.Copy(save.Waves, _waves, _waves.Length);
            _match = save.Match;
            _worldStats = save.WorldStats;
            // Stage 2 Task 5: Stats mirrors Players' array-copy pattern above.
            System.Array.Copy(save.Stats, _matchStats, _matchStats.Length);
            // Stage 3 Task 4: RestoreFrom copies INTO each live Inventory
            // instance rather than replacing _inventories[i] with
            // save.Inventories[i] directly — same "live objects keep their
            // identity across a restore" contract SaveState's own Clone()
            // doc states for the opposite direction.
            for (int i = 0; i < _inventories.Length; i++) _inventories[i].RestoreFrom(save.Inventories[i]);
        }

        /// Test-only seam for EveryPlayerAndStatsFieldAffectsHash (spec §3.13 item 12).
        /// Not a public API — no *ForTest wrapper ships in the battle surface.
        internal void SetPlayerForTest(in PlayerState p) => _players[0] = p;
        /// Stage 2 Task 4: index counterpart, for multiplayer test fixtures —
        /// the single-arg overload above still targets player 0, unchanged.
        internal void SetPlayerForTest(int index, in PlayerState p) => _players[index] = p;
        internal void SetStatsForTest(in MatchStats s) => _matchStats[0] = s;
        /// Stage 2 Task 5: index counterpart, for multiplayer test fixtures —
        /// the single-arg overload above still targets player 0, unchanged.
        internal void SetStatsForTest(int index, in MatchStats s) => _matchStats[index] = s;

        /// F-4 fix-round: three more test-only seams, same contract as
        /// SetPlayerForTest/SetStatsForTest above — mutate a live slot directly
        /// (RestoreState already handles the "reset to a saved snapshot" half) so
        /// EveryPlayerAndStatsFieldAffectsHash's per-field bump/assert pattern can
        /// be extended to MobState/ProjectileState/WaveState.
        /// Stage 2 Task 10: WorldStats counterpart of SetWaveForTest below —
        /// WorldStats is hashed at its own canonical position now, so the
        /// reflective hash sweep needs a seam to bump it with.
        internal void SetWorldStatsForTest(in WorldStats s) => _worldStats = s;
        internal void SetMobForTest(int index, in MobState m) => _mobs[index] = m;
        internal void SetProjectileForTest(int index, in ProjectileState p) => _projectiles[index] = p;
        internal void SetWaveForTest(Zone zone, in WaveState w) => _waves[(int)zone] = w;
        /// Stage 3 Task 1 Interfaces: test-only seam for MatchState, same
        /// contract as SetWaveForTest above — mutates the live slot directly,
        /// for a test that needs to force a specific phase/DirectorDeathTick.
        /// Its consumers arrived with Т21 (MatchFlowTests' own Ended
        /// fixtures): Ended has no production writer until Т24, so forcing it
        /// through this seam is the only way to state "the raid ended on this
        /// tick" at all (coordinator R-172).
        internal void SetMatchForTest(in MatchState m) => _match = m;

        /// Test-only: takes every mob off the arena. NEW seam -- no existing one
        /// expresses it (_mobCount is private and its only decrement lives in
        /// DamageMob), and the cadence tests need an emptied ring to observe a
        /// clear.
        ///
        /// TAKEN OFF, NOT KILLED, and that is the whole point of it existing
        /// beside TestWorlds.ClearFirstWave (which empties the arena the honest
        /// way, by damaging every mob to death). A death is not a quiet event
        /// here: DamageMob spawns a MobCorpse container or the Director's four
        /// caches, rolls the loot stream, credits Kills/ShotsHit to a player's
        /// MatchStats and emits MobDied. A test about a wave TIMER wants none
        /// of that in its world.
        ///
        /// ONE ASSIGNMENT IS THE WHOLE OPERATION, and that is a fact about
        /// this class rather than a shortcut. "Which mobs are on the arena" is
        /// carried by exactly two pieces of state -- `_mobs` and `_mobCount`
        /// -- and every reader walks `[0, _mobCount)`: StateHash, SaveState,
        /// CaptureSnapshot, SeparationSystem, VisibilitySystem,
        /// ProjectileSystem. Nothing holds a mob INDEX across a tick (MobState
        /// itself stores none, and `_sepForces`/`_projCandidates` are per-tick
        /// scratch, rewritten before they are read and deliberately outside
        /// both the hash and the save -- see their own field docs), so no
        /// reference is left dangling. What stays in `_mobs` past the new
        /// count is debris of exactly the kind DamageMob's own swap-remove
        /// (`_mobs[index] = _mobs[--_mobCount]`) leaves behind on every
        /// ordinary death, and it is invisible for the same reason.
        ///
        /// It empties the arena COMPLETELY, the Director included. A caller
        /// who leaves the raid in DirectorActive and clears will have the
        /// phase machine read "the boss is gone" on the next tick and open the
        /// gate -- the same conclusion it would draw if he had been killed.
        ///
        /// app-88jb Т24 (coordinator RULING 130): ONE ASSIGNMENT IS NO LONGER
        /// THE WHOLE OPERATION, and the paragraph above that says so is kept
        /// deliberately -- it records why the claim held until this task and
        /// what changed it. What changed it is that "which mobs are on the
        /// arena" is now carried by a THIRD piece of state: the rewind ring's
        /// occupancy set (PositionHistory, see _history's own doc). Unlike
        /// _mobs' debris past the count, that set is not merely invisible -- a
        /// slot left occupied by a mob this method removed would never come
        /// back, and the cadence tests call this repeatedly. So every live mob's
        /// slot is handed back first, exactly as DamageMob hands back the one
        /// mob it kills. A test seam that diverges from the battle path on
        /// something the battle path is careful about is a seam that lies.
        internal void ClearMobsForTest()
        {
            for (int i = 0; i < _mobCount; i++) _history.ReturnSlot(_mobs[i].HistorySlot);
            _mobCount = 0;
        }

        /// Test-only seam (Stage 3 Task 3), same contract as SetMobForTest/
        /// SetProjectileForTest above — mutates a live slot directly, and
        /// genuinely test-only again since the Ф1 fix-round (review B-I-5):
        /// PickupSystem.AdvanceTtl used to write TTL decay through it and now
        /// takes the `ref w.Pickups[i]` ProjectileSystem.Update has always
        /// used, so the promise SetPlayerForTest's own doc makes — "no
        /// *ForTest wrapper ships in the battle surface" — holds here too.
        internal void SetPickupForTest(int index, in PickupState p) => _pickups[index] = p;

        /// The world's own ammo-refill seam (Stage 3 Task 2, renamed out of a
        /// `*ForTest` name in the Ф1 fix-round — review B-I-5): supplies the
        /// player slot and the weapon config to WeaponSystem.AddAmmo, the ONE
        /// home of the AmmoMax ceiling and of the FireCooldown clamp-down on
        /// the 0-to-positive edge, so no caller restates either (CR 2).
        /// Deliberately not a raw field write like SetPlayerForTest above —
        /// that clamp is the whole point of routing through here.
        ///
        /// Production caller: Loot.PickupSystem.Collect (auto-pickup, Т3);
        /// AmmoTests drives the same seam directly. The old name described the
        /// one task before Т3 during which no production caller existed yet,
        /// and stopped being true the moment Т3 landed.
        internal void AddAmmo(int index, int shots)
            => WeaponSystem.AddAmmo(ref _players[index], _config.Weapon, shots);

        /// Test-only seam (Task 4): reads a live projectile slot back —
        /// SetProjectileForTest's counterpart, for tests asserting on
        /// post-tick projectile state (e.g. Height/PrevHeight after VelZ integration).
        internal ProjectileState GetProjectileForTest(int index) => _projectiles[index];

        /// The rewind ring itself (app-88jb Т25) -- the world's one accessor to
        /// the rows a body's past is kept in. Nothing else exposes a row.
        ///
        /// ⚠ IT WAS `HistoryForTest` UNTIL app-88jb Т28, AND THE RENAME IS THE
        /// WHOLE OF WHAT CHANGED HERE. Т25 wrote it for tests because the ring
        /// had no production reader yet and said so in as many words ("its
        /// first PRODUCTION reader is Т27/Т28"); Т28 is that reader --
        /// ProjectileSystem.RewoundBody asks PosAt through this property on
        /// every rewound step of every round -- so a name ending in `ForTest`
        /// would now be a lie about the hottest loop in the simulation. It sits
        /// among the `*ForTest` seams because that is where it was written and
        /// moving it would churn the file without changing anything; what it is
        /// is stated here instead.
        /// ⛔ AND THERE IS NO SECOND PROPERTY. A narrow read-only forwarder
        /// beside this one would be a SECOND SPELLING of PosAt on the world,
        /// which is what RULING 148 removed the other seam of Т25 for
        /// (`PlayerHistorySlotForTest` duplicated `PlayerAt(i).HistorySlot`).
        ///
        /// ⚠ IT HANDS BACK THE WHOLE OBJECT, MUTABLE (review finding A-5).
        /// PositionHistory is a class, so this is the live ring and not a copy
        /// -- a seam that cloned it would answer questions about a snapshot
        /// nobody writes to -- and through it a caller can reach Clear, Write,
        /// RentSlot and RestoreFrom, none of which anything outside TickAll has
        /// business calling. That width is real and is named here rather than
        /// left to be discovered. What guards it is what guards every other
        /// internal member of this class: the simulation and its test assembly
        /// are the only things that see it, and misuse is a review finding
        /// rather than a compile error.
        internal PositionHistory History => _history;

        /// Canonical order (spec §3.3 and, since Stage 3, spec Р294; Task 3 —
        /// split rng into spreadRng/waveRng; Stage 2 Task 10 — multiplayer
        /// reorder, the one sanctioned golden re-pin of the stage-2 network
        /// phase; Stage 3 Т6 — the extraction economy's own state, the FIRST
        /// of the two sanctioned golden re-pins of stage 3):
        /// tick → spreadRng → waveRng → lootRng → nextEntityId → playerCount
        /// → players[0..n) → mobCount+mobs → projectileCount+projectiles
        /// → pickupCount+pickups → containerCount+containers+containerSlots
        /// → history → wave → matchState → worldStats → stats[0..n)
        /// → inventories[0..n).
        ///
        /// THE HISTORY STEP (app-88jb Т25, spec §3.6.1, coordinator RULING
        /// 143) is one call into PositionHistory.Fold, at the position
        /// WorldSave gives the same state. Its shape lives there rather than
        /// here because the ring's index arithmetic has exactly one home; what
        /// belongs in THIS list is only that the step exists and where it sits.
        /// ⚠ It is the one step whose internal walk is not this method's walk:
        /// it goes by TICK across the rewind window, oldest to newest, and only
        /// then by live body. See PositionHistory.Fold's own doc, and
        /// WorldSave's, which says the same thing from the save's side.
        ///
        /// THE CONTAINERS' STEP WAS RESERVED BY Т6 AND IS FILLED HERE, Т14
        /// (spec Р294). Two walks follow `_containerCount`, both bounded by
        /// it and neither carrying a length marker of its own: walk A hashes
        /// each live `ContainerState` (HashContainer, same one-helper-per-
        /// entity shape as HashPickup); walk B hashes the flat
        /// `_containerSlots` content, per container bounded by THAT
        /// container's own `SlotCount` (already folded into the digest
        /// inside walk A) rather than the fixed `MaxContainerSlots` block
        /// width — the same "walk only what's counted, not the backing
        /// array" contract HashInventory below already follows for the
        /// exact same reason (a container's block can carry a previous
        /// occupant's leftover bytes past its own SlotCount, same as a
        /// swap-removed backpack slot). At `_containerCount == 0` both walks
        /// run zero iterations, so a world with no containers hashes
        /// identically to before this task — `StateHash64.Add` is an FNV-1a
        /// chain, and stage 3's two sanctioned golden movements (Т6, Т12)
        /// are both already spent.
        ///
        /// playerCount, _mobCount and _projectileCount are each hashed before
        /// their arrays for the same reason: a length is state in its own right,
        /// and folding it in first makes two different-length worlds
        /// distinguishable even when their common prefix matches.
        ///
        /// Stage 2 Task 16 (owner decision Р114, inside the sanctioned golden
        /// re-pin #2): the separate `statsCount` step Task 10 introduced is
        /// GONE. `_matchStats.Length` is `_players.Length` by construction and
        /// forever — both arrays are `readonly`, both are sized from the one
        /// constructor `playerCount`, and RestoreState validates both lengths
        /// against it — so hashing it a second time added a constant with no
        /// discriminating power. The stats array itself is still walked at its
        /// own canonical position; only the duplicated length is dropped.
        public ulong StateHash()
        {
            ulong h = StateHash64.Begin();
            h = StateHash64.Add(h, (ulong)_tick);
            h = StateHash64.Add(h, _spreadRng.state);
            h = StateHash64.Add(h, _waveRng.state);
            h = StateHash64.Add(h, _lootRng.state);
            h = StateHash64.Add(h, _nextEntityId);
            h = StateHash64.Add(h, _players.Length);
            for (int i = 0; i < _players.Length; i++) h = HashPlayer(h, in _players[i]);
            h = StateHash64.Add(h, _mobCount);
            for (int i = 0; i < _mobCount; i++) h = HashMob(h, in _mobs[i]);
            h = StateHash64.Add(h, _projectileCount);
            for (int i = 0; i < _projectileCount; i++) h = HashProjectile(h, in _projectiles[i]);
            h = StateHash64.Add(h, _pickupCount);
            for (int i = 0; i < _pickupCount; i++) h = HashPickup(h, in _pickups[i]);
            // Containers (Т14) — the reserved step, see this method's own
            // doc for the shape of the two walks and why they stay
            // digest-neutral at zero containers.
            h = StateHash64.Add(h, _containerCount);
            for (int i = 0; i < _containerCount; i++) h = HashContainer(h, in _containers[i]);
            for (int i = 0; i < _containerCount; i++)
            {
                int offset = i * _config.Arena.MaxContainerSlots;
                for (int s = 0; s < _containers[i].SlotCount; s++)
                    h = StateHash64.Add(h, (int)_containerSlots[offset + s]);
            }
            // The rewind ring (app-88jb Т25) — see this method's own doc for
            // why the step is a single call and why its walk is the one walk
            // here that is not shaped like this list.
            h = _history.Fold(h, this);
            // Wave-cadence-per-zone (bd app-ggvz Т3, spec §3.2): THREE wave
            // states now, one per ring, folded in Zone's own declared order
            // (Outer -> Middle -> Core) at the SAME position in the sequence
            // the single one held. No count step ahead of them, unlike the
            // entity arrays above: Zones.Count is a fixed fact of the arena,
            // not a live population.
            for (int z = 0; z < Zones.Count; z++) h = HashWave(h, in _waves[z]);
            h = HashMatch(h, in _match);
            // Stage 2 Task 10: the match-wide counters get their own hash step at
            // their own canonical position instead of riding interleaved inside
            // HashStats as Task 5 temporarily left them.
            h = HashWorldStats(h, in _worldStats);
            for (int i = 0; i < _matchStats.Length; i++) h = HashStats(h, in _matchStats[i]);
            // Backpacks LAST (spec Р294, and the debt Stage 3 Task 4 recorded
            // on WorldSave.Inventories for this task to discharge): after the
            // statistics, one entry per player, in player order.
            for (int i = 0; i < _inventories.Length; i++) h = HashInventory(h, _inventories[i]);
            return h;
        }

        static ulong HashPlayer(ulong h, in PlayerState p)
        {
            h = StateHash64.Add(h, p.Pos); h = StateHash64.Add(h, p.Vel);
            h = StateHash64.Add(h, p.AimPoint); h = StateHash64.Add(h, p.DashDir);
            h = StateHash64.Add(h, p.RecoilOffset); h = StateHash64.Add(h, p.Hp);
            h = StateHash64.Add(h, p.Stamina); h = StateHash64.Add(h, p.StaminaRegenDelayTimer);
            h = StateHash64.Add(h, p.DashTimer); h = StateHash64.Add(h, p.DashCooldown);
            h = StateHash64.Add(h, p.IframeTimer); h = StateHash64.Add(h, p.DashBufferTimer);
            h = StateHash64.Add(h, p.DashSpeedCur); // Task 12: ricochet-retained dash speed
            h = StateHash64.Add(h, p.SlideSpeedPenalty); // app-88jb Т22: collision tax on the slide
            h = StateHash64.Add(h, p.FireCooldown);
            // Stage 3 Т6 (Task 2's field, spec Р261): the magazine, folded in
            // right after the cooldown it shares a weapon with — the pair is
            // what decides whether the next tick fires at all and on which
            // interval, so a replay that ignored either could take a
            // different branch and still claim the same hash.
            h = StateHash64.Add(h, p.Ammo);
            h = StateHash64.Add(h, p.Alive);
            // Stage 3 Т6 (Task 1's fields): Extracted rides next to Alive —
            // the two carry one invariant between them, !(Alive && Extracted)
            // — and ExtractKind next to Extracted, the field it qualifies
            // (same "beside what it qualifies" placement Stage 2 Task 10 used
            // for ProjectileState.OwnerIndex).
            h = StateHash64.Add(h, p.Extracted);
            h = StateHash64.Add(h, (int)p.ExtractKind);
            // Task 14: aim-down-sights settle progress.
            h = StateHash64.Add(h, p.AimSettleTimer);
            // Task 10: slide state.
            h = StateHash64.Add(h, p.SlideDir); h = StateHash64.Add(h, p.SlideTimer);
            h = StateHash64.Add(h, p.SlideBufferTimer); h = StateHash64.Add(h, p.RunUpTimer);
            h = StateHash64.Add(h, p.PostDashSlideTimer); h = StateHash64.Add(h, p.LinkWindowTimer);
            // Stage 2 Task 10: the two edge-request rate-limit counters — real
            // per-player state that survives across ticks and decides whether the
            // next request is honored, so a replay/rollback that dropped them
            // would diverge the moment a request lands.
            h = StateHash64.Add(h, p.DashRequestCooldownTicks);
            h = StateHash64.Add(h, p.SlideRequestCooldownTicks);
            // Stage 3 Т6 (Task 1's fields): the three hold-to-act channel
            // timers and the loot channel's own target, as one trailing group
            // — a new subsystem with no existing neighbor to sit beside, kept
            // in declaration order among themselves. Т17 gave the loot timer
            // and its target their writers, Т19 gave RepairTimer its own; only
            // ExtractTimer is still inert, until Т23. Hashed from today,
            // which is the whole point
            // of errata E-1 (a field that joins the hash later moves the
            // digest later, and stage 3 has only two sanctioned movements).
            h = StateHash64.Add(h, p.LootTimer);
            h = StateHash64.Add(h, p.RepairTimer);
            h = StateHash64.Add(h, p.ExtractTimer);
            h = StateHash64.Add(h, p.LootTargetContainerId);
            h = StateHash64.Add(h, (int)p.LootTargetSlot);
            // app-88jb Т7 (spec §3.2): body tilt and its angular velocity,
            // folded as a pair at what was then the end of the struct and the
            // end of the fold -- the same placement HashMob gave the mob's own
            // pair in Т5.
            // ⚠ THE "LAST" HALF OF THAT CLAIM IS NO LONGER TRUE, and Т24 is
            // where it stopped being: PlayerState.HistorySlot is declared
            // after this pair and folded after it too, so the pair now closes
            // nothing.
            // ⛔ AND THE CORRECTION MUST NOT BE "the fold mirrors the struct".
            // RULING 129 examined that rule and rejected it, because this very
            // method disproves it: SlideSpeedPenalty is declared 26th and
            // folded 14th, DashSpeedCur 15th and 13th, and HashMob folds
            // SpawnZone third out of a tenth-place declaration. The rule these
            // helpers actually follow is that A FIELD IS FOLDED BESIDE WHAT IT
            // QUALIFIES -- SpawnZone beside Type, SlideSpeedPenalty beside the
            // dash speed it taxes. HistorySlot happens to be last in both the
            // struct and the fold, and that is a coincidence of one field, not
            // a law to derive the next placement from. (In HashMob the tilt
            // pair does still close the fold: that HistorySlot went in beside
            // SpawnZone, ahead of the tilt.)
            // Both are live
            // per-tick state that survives across ticks (TiltSystem's collector
            // pass integrates them, DamagePlayer adds into TiltVel), so a
            // replay or a rollback that dropped either would diverge the moment
            // a round lands; and WorldLifecycleTests'
            // EveryPlayerAndStatsFieldAffectsHash is what refuses to let a new
            // field of this struct join the state without joining the digest.
            // ⚠ THIS IS WHAT MOVES THE THREE GOLDEN DIGESTS a fourth time in
            // this epic -- sanctioned, and re-pinned once at Т34, never here.
            h = StateHash64.Add(h, p.Tilt);
            h = StateHash64.Add(h, p.TiltVel);
            // app-88jb Т24 (spec §3.6): the collector's rewind slot, LAST --
            // last field of the struct, last term of the fold. It is here for
            // MobState.HistorySlot's reasons (see HashMob) plus one of its
            // own: a collector's slot is issued in the constructor and never
            // returned, so it is the one piece of a player's state that is
            // constant for the whole match -- and a constant that is not
            // hashed is a constant nothing can prove two worlds agree on.
            h = StateHash64.Add(h, p.HistorySlot);
            return h;
        }

        static ulong HashMob(ulong h, in MobState m)
        {
            h = StateHash64.Add(h, m.Id); h = StateHash64.Add(h, (int)m.Type);
            // Wave-cadence-per-zone (bd app-ggvz Т1): SpawnZone right after
            // the Type field it qualifies -- which ring a mob was PUT INTO
            // by whoever spawned it, not where it stands now (see the
            // field's own doc, SimStates.cs). Not on the wire (MobRecord is
            // unchanged, 9 B).
            h = StateHash64.Add(h, (int)m.SpawnZone);
            // app-88jb Т24 (spec §3.6): the rewind slot sits beside SpawnZone
            // on the same rule that put SpawnZone beside Type -- a field is
            // folded next to what it qualifies. Both are server bookkeeping
            // about WHICH body this is rather than about where it stands, and
            // both are canonical state: the slot survives a tick, rides
            // SaveState/RestoreState, and decides which row a rewound shot
            // reads, so a replay that dropped it would aim at a different
            // body's past.
            h = StateHash64.Add(h, m.HistorySlot);
            h = StateHash64.Add(h, m.Pos); h = StateHash64.Add(h, m.Vel);
            h = StateHash64.Add(h, m.Hp); h = StateHash64.Add(h, m.StateTimer);
            h = StateHash64.Add(h, m.FireCooldown); h = StateHash64.Add(h, (int)m.Ai);
            h = StateHash64.Add(h, m.StrafeSign);
            // app-88jb Т5 (spec §3.2): the tilt pair CLOSES the fold, mirroring
            // the end of the struct -- they qualify nothing but each other, so
            // there is no field for them to sit beside the way SpawnZone sits
            // beside Type.
            //
            // HASHED EVEN THOUGH TILT IS COSMETIC TODAY, and the reason is not
            // "for the company". Three of them, in ascending order of force:
            //   1. it is canonical state that SURVIVES A TICK and rides
            //      SaveState/RestoreState, so a rollback that dropped it would
            //      resume a body at a different attitude than the one it saved;
            //   2. from Т6 the pair stops being cosmetic at all -- a tilt past
            //      TiltFallAngle puts the mob in Downed, where it neither
            //      shoots nor strikes, which is a game outcome by any reading;
            //   3. errata E-1's rule, already stated for the Т6 timer group
            //      above: a field that joins the digest LATER moves the digest
            //      later, and the sanctioned re-pins are counted. Joining now
            //      spends the re-pin this epic has already budgeted (Т34)
            //      instead of asking for a second one.
            // None of that contradicts Р375/Р383 -- tilt still decides no hit
            // resolution and still never rides the wire; the digest is about
            // what the SERVER must replay identically, not about what the
            // client is told.
            h = StateHash64.Add(h, m.Tilt); h = StateHash64.Add(h, m.TiltVel);
            return h;
        }

        static ulong HashProjectile(ulong h, in ProjectileState p)
        {
            h = StateHash64.Add(h, p.Id); h = StateHash64.Add(h, (int)p.Owner);
            // Stage 2 Task 10: OwnerIndex joins the hash right after Owner it
            // qualifies (Stage 2 Task 7 introduced the field and deferred this
            // step to the sanctioned re-pin). It decides who is credited with a
            // hit/kill, so a replay that ignored it could credit a different
            // player and still claim the same hash. Cast is explicit: byte has
            // implicit conversions to several StateHash64.Add overloads at once.
            h = StateHash64.Add(h, (int)p.OwnerIndex);
            // Stage 3 Т6 (Stage 3 Task 5's field, spec Р252): the shooting
            // ENTITY, beside the two owner fields it completes. It decides
            // which mob a round may NOT damage, so a replay that dropped it
            // could resolve a different friendly-fire outcome and still claim
            // the same digest — the same argument OwnerIndex was admitted on.
            h = StateHash64.Add(h, p.OwnerEntityId);
            h = StateHash64.Add(h, p.Pos); h = StateHash64.Add(h, p.PrevPos);
            h = StateHash64.Add(h, p.Vel); h = StateHash64.Add(h, p.Damage);
            h = StateHash64.Add(h, p.Radius); h = StateHash64.Add(h, p.Ttl);
            h = StateHash64.Add(h, p.Height); h = StateHash64.Add(h, p.PrevHeight);
            h = StateHash64.Add(h, p.VelZ);
            // app-88jb Т19 (spec §3.4): the ricochet counter CLOSES the fold,
            // mirroring the end of the struct — the same placement rule Т5 used
            // for the mob's tilt pair (HashMob's own note above): it qualifies
            // no other field, so there is none for it to sit beside.
            //
            // ⚠ THE PLAN SAID "RIGHT AFTER OwnerEntityId", AND THAT READS TWO
            // WAYS, which is why the choice is written down. `OwnerEntityId` is
            // the LAST field of the struct but the FOURTH of this fold: it was
            // deliberately hoisted up beside the two owner fields it completes
            // (its own note, twelve lines up). "After OwnerEntityId" in
            // DECLARATION order is exactly here, at the end; "after
            // OwnerEntityId" in FOLD order would bury the counter among the
            // ownership fields, where it qualifies nothing. The stated reason —
            // end of struct = end of fold, Т5's idiom — picks this one.
            //
            // HASHED, not skipped, and the argument is Т5's third one verbatim:
            // this is canonical state that SURVIVES A TICK and rides
            // SaveState/RestoreState, so a rollback that dropped it would
            // resume a round with a spent counter refilled — and the counter
            // decides whether the next contact reflects or retires the round,
            // which is a game outcome by any reading.
            h = StateHash64.Add(h, p.Ricochets);
            // app-88jb Т28 (spec §3.6): the picture half's countdown CLOSES the
            // fold in its turn, by the same "end of struct = end of fold" rule
            // Т5 and Т19 followed above -- it qualifies no other field, so there
            // is none for it to sit beside, and it is the last field of the
            // struct. Cast is explicit for the reason OwnerIndex's own note
            // gives: byte has implicit conversions to several
            // StateHash64.Add overloads at once.
            //
            // HASHED, on Ricochets' argument one line up: it survives a tick and
            // rides SaveState/RestoreState, and it decides WHICH TICK the round
            // asks the bodies about -- so a rollback that dropped it would
            // resume a round that answers a different question, which is a game
            // outcome and not a detail.
            h = StateHash64.Add(h, (int)p.RewindLeft);
            return h;
        }

        /// Stage 3 Т6: one ground pickup, fields in declaration order like
        /// every other Hash* helper here. `Kind` is cast for the same reason
        /// every other enum in this file is (MobState.Type/Ai,
        /// WaveState.Phase): an enum has no implicit conversion to any
        /// StateHash64.Add overload at all, so the cast is required, not
        /// merely disambiguating the way the byte casts above are.
        static ulong HashPickup(ulong h, in PickupState p)
        {
            h = StateHash64.Add(h, p.Id); h = StateHash64.Add(h, p.Pos);
            h = StateHash64.Add(h, (int)p.Kind); h = StateHash64.Add(h, p.Amount);
            h = StateHash64.Add(h, p.Ttl);
            return h;
        }

        /// Stage 3 Т14: one container's own struct fields — walk A of the
        /// two StateHash() adds for this task (see that method's own doc).
        /// `SlotCount` is included here, NOT re-added by walk B — walk B
        /// only uses it as a loop bound, so the field's own value already
        /// entering the digest here is what stands in for it, same
        /// "walked in the count position instead of a redundant marker"
        /// role _containerCount itself plays one level up. `Kind` and
        /// `SlotCount` both cast for the reasons HashPickup's own doc gives
        /// (enum has no implicit Add overload; byte matches every other
        /// byte field this file hashes, e.g. ProjectileState.OwnerIndex).
        static ulong HashContainer(ulong h, in ContainerState c)
        {
            h = StateHash64.Add(h, c.Id); h = StateHash64.Add(h, c.Pos);
            h = StateHash64.Add(h, (int)c.Kind); h = StateHash64.Add(h, (int)c.SlotCount);
            h = StateHash64.Add(h, c.Ttl);
            return h;
        }

        static ulong HashWave(ulong h, in WaveState w)
        {
            // Wave-cadence-per-zone (bd app-ggvz Т3): the zone left the field
            // NAMES and moved into the instance index, so the nine Add steps
            // are three again -- SAME archetype order (MobType's Chaser=0/
            // Gunner=1/Elite=2) and the SAME position in the sequence (between
            // WaveIndex and AliveCount) the matrix held.
            h = StateHash64.Add(h, (int)w.Phase); h = StateHash64.Add(h, w.WaveIndex);
            h = StateHash64.Add(h, w.PendingChaser); h = StateHash64.Add(h, w.PendingGunner);
            h = StateHash64.Add(h, w.PendingElite);
            h = StateHash64.Add(h, w.AliveCount); h = StateHash64.Add(h, w.PhaseTicks);
            return h;
        }

        /// Stage 3 Т6: the match's own flow state, hashed once for the whole
        /// world right after the wave — the two are the same kind of thing
        /// (a single director-ish struct, one per match) and sit next to each
        /// other in the canonical order for that reason.
        static ulong HashMatch(ulong h, in MatchState m)
        {
            h = StateHash64.Add(h, (int)m.Phase);
            h = StateHash64.Add(h, m.DirectorDeathTick);
            return h;
        }

        /// Stage 2 Task 10: one player's PERSONAL counters only — the three
        /// match-wide ones Task 5 had left interleaved here moved out to
        /// HashWorldStats below, so this now hashes MatchStats' own fields in
        /// their declaration order and nothing else.
        static ulong HashStats(ulong h, in MatchStats s)
        {
            h = StateHash64.Add(h, s.Kills); h = StateHash64.Add(h, s.HeadshotKills);
            h = StateHash64.Add(h, s.ShotsFired); h = StateHash64.Add(h, s.ShotsHit);
            h = StateHash64.Add(h, s.DashesUsed); h = StateHash64.Add(h, s.SlidesUsed);
            h = StateHash64.Add(h, s.DeathTick); h = StateHash64.Add(h, s.DamageTaken);
            // Stage 3 Т6 (Task 1's fields, errata E-1): the run's own two
            // economy counters. Both got their writers in the Ф1 fix-round
            // (review C1 / B-I-1, owner decision R-24) — AmmoSpent in
            // WeaponSystem.Advance's spend branch, CellsPicked in
            // Loot.PickupSystem.Collect — so this step is no longer a
            // placeholder for behavior that had not arrived: the composition
            // of state and the behavior behind it entered the digest in the
            // same phase, which is what errata E-1 asked for.
            h = StateHash64.Add(h, s.AmmoSpent); h = StateHash64.Add(h, s.CellsPicked);
            return h;
        }

        /// Stage 2 Task 10: the match-wide counters, hashed once for the whole
        /// world at their own canonical position (right after the wave, before
        /// the per-player stats array) — mirroring the WorldStats/MatchStats
        /// split Task 5 made in the state itself.
        static ulong HashWorldStats(ulong h, in WorldStats w)
        {
            h = StateHash64.Add(h, w.WavesCleared);
            h = StateHash64.Add(h, w.MobSpawnsSkipped);
            h = StateHash64.Add(h, w.ProjectileSpawnsSkipped);
            h = StateHash64.Add(h, w.PickupSpawnsSkipped);
            h = StateHash64.Add(h, w.ContainerSpawnsSkipped);
            return h;
        }

        /// Stage 3 Т6: one player's backpack. NOT `in`, and not a struct: an
        /// Inventory is a class (spec Р232 — it owns a byte array, and living
        /// inside PlayerState would make every wholesale PlayerState copy
        /// allocate), so this takes the reference itself.
        ///
        /// COUNT FIRST, THEN ONLY THE CARRIED ITEMS. The count is state in its
        /// own right, folded in ahead of its contents for the same reason
        /// playerCount/_mobCount/_projectileCount are. The walk then stops at
        /// Count rather than running the whole MaxInventoryItems array,
        /// because the bytes past Count are NOT state: Inventory.TryRemoveAt
        /// is a swap-remove and leaves the vacated tail slot holding whatever
        /// it held before, while SetForTest overwrites only a prefix. Hashing
        /// that tail would make two backpacks that carry exactly the same
        /// items disagree purely over how they got there — a false desync
        /// between a live world and a replay that reached the same contents by
        /// another route.
        static ulong HashInventory(ulong h, Inventory inv)
        {
            int count = inv.Count;
            h = StateHash64.Add(h, count);
            // Explicit cast for the same overload-resolution reason as
            // ProjectileState.OwnerIndex above.
            for (int i = 0; i < count; i++) h = StateHash64.Add(h, (int)inv.ItemAt(i));
            return h;
        }
    }
}
