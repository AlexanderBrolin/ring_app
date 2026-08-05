using Ring.Simulation.AI;
using Ring.Simulation.Combat;
using Ring.Simulation.Movement;
using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Deterministic world: fixed-dt ticks, two independent RNG streams seeded
    /// from match-config (Task 3: split from the former single shared Random) —
    /// weapon spread (_spreadRng) and wave director (_waveRng) draw from
    /// separate streams so one system's RNG consumption never perturbs the
    /// other's sequence.
    /// No UnityEngine (asmdef: noEngineReferences) — Critical Rule 1.
    public sealed class SimulationWorld
    {
        /// ADR-002 T5: simulation runs at 30 Hz. The single source of dt.
        public const float TickDt = 1f / 30f;

        int _tick;
        Random _spreadRng;
        Random _waveRng;
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

        // Entities appear in Phase 5/6 — arrays are preallocated to arena caps
        // now so the hot path never allocates once systems start filling them.
        MobState[] _mobs;
        int _mobCount;
        // Scratch buffer for SeparationSystem's per-tick pairwise impulses (Task
        // 20) — preallocated here so the hot path never allocates; recomputed
        // from scratch every tick, so it carries no state across ticks and is
        // deliberately excluded from SaveState/RestoreState and StateHash.
        readonly float2[] _sepForces;
        ProjectileState[] _projectiles;
        int _projectileCount;
        // Scratch buffer for ProjectileSystem's per-tick candidate min-scan
        // (Task 5) — preallocated here so the hot path never allocates; every
        // slot is overwritten before being read each tick, so like _sepForces
        // above it carries no state across ticks and is deliberately excluded
        // from SaveState/RestoreState and StateHash. Sized to MaxMobs + 3: one
        // slot per live mob plus barrier, player, and floor (Task 7).
        // Stage 2 Task 17: the spec's eventual size is MaxMobs + MaxPlayers +
        // 2 (one player slot per live player, not one total) — MaxMobs + 3
        // stays correct until then because no candidate gather packs more
        // than a single player today: a Player-owned projectile's candidates
        // are barrier + up to MaxMobs mobs + floor, a Mob-owned one's are
        // barrier + the one hardcoded player (w.Player) + floor. Task 17
        // grows this array in step with fanning that gather out to every
        // live player.
        readonly (float t, int kind, int index)[] _projCandidates;
        WaveState _wave;
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
        int _rejectedEdgeRequests;
        internal int RejectedEdgeRequestsForTest => _rejectedEdgeRequests;

        /// `playerCount` defaults to 1 so every call site that predates Stage 2
        /// Task 4 (136 existing constructions) keeps compiling and behaving
        /// identically — solo still spawns at the arena center (spec §3.2), not
        /// on the ring.
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
            _config = config;
            _players = new PlayerState[playerCount];
            _sanitizedInputs = new SimInput[playerCount];
            // Stage 2 Task 5: fresh zero-valued MatchStats per player — no
            // explicit per-field init needed, same "struct array defaults are
            // already correct" reasoning the constructor already relies on for
            // _mobs/_projectiles below.
            _matchStats = new MatchStats[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                float2 pos = Geometry.SpawnPosFor(i, playerCount, in config.Arena);
                float2 vel = float2.zero; // fresh spawn, no inherited velocity
                // Same depenetration seam PlayerMovementSystem/SeparationSystem
                // use — a safety net for the (validated-clean, per
                // SimConfigBuilder's spawn-clearance check) case where an
                // obstacle still overlaps a spawn point.
                Geometry.Depenetrate(ref pos, ref vel, config.Hero.Radius, in config.Arena, 1);
                _players[i] = new PlayerState
                    { Pos = pos, Hp = config.Hero.MaxHp, Stamina = config.Hero.StaminaMax, Alive = true };
            }
            // Wave director starts idle, counting down to the first wave (Task 22
            // Interfaces) — WavePhase.Waiting is the enum's zero value, but
            // PhaseTimer must be set explicitly or the countdown would start
            // already expired and fire a wave on tick 1.
            _wave = new WaveState { Phase = WavePhase.Waiting, PhaseTimer = config.Wave.FirstWaveDelay };
            _mobs = new MobState[config.Arena.MaxMobs];
            _sepForces = new float2[config.Arena.MaxMobs];
            _projectiles = new ProjectileState[config.Arena.MaxProjectiles];
            _projCandidates = new (float t, int kind, int index)[config.Arena.MaxMobs + 3];
            _events = new SimEvent[config.Arena.MaxEventsPerFrame];
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
                    WeaponSystem.Update(this, ref _players[i], in _sanitizedInputs[i], i);
            }
            // Canonical tick order (spec Interfaces, Task 16/19/20): movement →
            // weapon → mobs (Phase 6) → mob separation → projectiles → (waves,
            // Phase 6+). Separation runs right after MobAiSystem so it sees this
            // tick's post-movement positions; its Vel addition only shows up as
            // motion on the next tick's MoveWithCollisions call (see
            // SeparationSystem's doc comment).
            MobAiSystem.Update(this);
            SeparationSystem.Apply(this);
            ProjectileSystem.Update(this);
            // Runs last (Task 22 Interfaces) so spawns land after this tick's
            // movement/combat has settled — a mob spawned here doesn't get an
            // extra, unbudgeted movement/combat sub-step on its own spawn tick.
            WaveSystem.Update(this);
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
                if (moveResult.DashRejected) _rejectedEdgeRequests++;
                if (moveResult.SlideRejected) _rejectedEdgeRequests++;
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
        /// topology (radius, obstacle count/positions/radii) must stay identical —
        /// a change there invalidates collision/spawn geometry that isn't reconciled
        /// here, so it throws instead; Presentation reacts by restarting the world.
        /// Migration: Hp clamps down to the new max, every player timer clamps into
        /// [0, its new max], wave-state (including WaveIndex) is left untouched.
        /// Stage 2 Task 4: migrates every player in the match, not just player 0 — for
        /// a solo world (_players.Length == 1) this is byte-for-byte the same
        /// single migration as before.
        public void ApplyConfig(in SimConfig next)
        {
            if (!ArenaTopologyMatches(in _config.Arena, in next.Arena))
            {
                throw new System.ArgumentException("SimulationWorld.ApplyConfig: arena topology " +
                    "changed (radius/obstacles) — restart the world instead of hot-tweaking it.");
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
                p.IframeTimer = math.clamp(p.IframeTimer, 0f, next.Hero.DashIframes);
                p.DashBufferTimer = math.clamp(p.DashBufferTimer, 0f, next.Hero.DashBufferWindow);
                p.FireCooldown = math.clamp(p.FireCooldown, 0f, next.Weapon.FireInterval);
                // Task 10: slide timers, same clamp-to-new-ceiling contract as the
                // dash timers above.
                p.SlideTimer = math.clamp(p.SlideTimer, 0f, next.Hero.SlideDuration);
                p.SlideBufferTimer = math.clamp(p.SlideBufferTimer, 0f, next.Hero.SlideBufferWindow);
                p.RunUpTimer = math.clamp(p.RunUpTimer, 0f, next.Hero.RunUpSeconds);
                p.PostDashSlideTimer = math.clamp(p.PostDashSlideTimer, 0f, next.Hero.PostDashSlideWindow);
                p.LinkWindowTimer = math.clamp(p.LinkWindowTimer, 0f, next.Hero.LinkWindowSeconds);
                // Task 14: aim-settle progress, same clamp-to-new-ceiling contract.
                p.AimSettleTimer = math.clamp(p.AimSettleTimer, 0f, next.Hero.AimSettleSeconds);
                // Stage 2 Task 10: the two edge-request counters clamp into
                // [0, the new EdgeRequestMinTicks] — same contract as the timers
                // above, just counted in ticks instead of seconds. Without this,
                // lowering EdgeRequestMinTicks mid-match would leave a player
                // gated by the OLD, longer window until their counter drained.
                p.DashRequestCooldownTicks =
                    math.clamp(p.DashRequestCooldownTicks, 0, next.Hero.EdgeRequestMinTicks);
                p.SlideRequestCooldownTicks =
                    math.clamp(p.SlideRequestCooldownTicks, 0, next.Hero.EdgeRequestMinTicks);
                _players[i] = p;
            }
        }

        /// Unity.Mathematics.Random rejects seed 0 — remaps it to a fixed nonzero
        /// constant (Task 3, same zero-guard as Task 1's single-stream version).
        static uint Fold(uint x) => x == 0 ? 0x9E3779B9u : x;

        static bool ArenaTopologyMatches(in ArenaSimConfig a, in ArenaSimConfig b)
        {
            if (a.Radius != b.Radius || a.ObstacleCount != b.ObstacleCount) return false;
            for (int i = 0; i < a.ObstacleCount; i++)
            {
                if (!math.all(a.ObstaclePos[i] == b.ObstaclePos[i])) return false;
                if (a.ObstacleRadius[i] != b.ObstacleRadius[i]) return false;
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
        /// kind" contract as `owner`/`zone`/`hitDir` above.
        internal void Emit(SimEventKind kind, float2 pos, int entityId, MobType mobType, float amount,
            ProjectileOwner owner = ProjectileOwner.Player,
            HitZone zone = HitZone.None, float2 hitDir = default,
            byte playerIndex = ProjectileIds.NoOwner)
        {
            if (_eventCount < _events.Length)
            {
                _events[_eventCount++] = new SimEvent
                {
                    Kind = kind, Tick = _tick, Pos = pos,
                    EntityId = entityId, MobType = mobType, Amount = amount, Owner = owner,
                    Zone = zone, HitDir = hitDir, PlayerIndex = playerIndex
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

        /// Combat systems' seam into one player's personal counters (ShotsFired, ...)
        /// (Stage 2 Task 5 — was a single-slot property, now indexed by shooter).
        internal ref MatchStats StatsRef(int index) => ref _matchStats[index];

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

        /// MobAiSystem's seam into the per-archetype balance numbers (Task 19).
        internal MobSimConfig MobConfigFor(MobType type)
            => type == MobType.Chaser ? _config.Chaser : _config.Gunner;

        /// SeparationSystem's seam into its preallocated per-tick force buffer
        /// (Task 20) — sized to Arena.MaxMobs, recomputed every tick, never grown.
        internal float2[] SepForces => _sepForces;

        /// ProjectileSystem's seam into its preallocated per-tick candidate
        /// scratch (Task 5) — sized to Arena.MaxMobs + 3, recomputed every
        /// tick, never grown. Stage 2 Task 17 grows this to Arena.MaxMobs +
        /// MaxPlayers + 2 per spec; safe at the current size until then —
        /// see the field's own doc above for why.
        internal (float t, int kind, int index)[] ProjCandidates => _projCandidates;

        /// WaveSystem's seam into the wave director's live state (Task 22) — same
        /// ref-return pattern as SpreadRng/WaveRng, so the system mutates it in
        /// place instead of round-tripping copies every tick.
        internal ref WaveState WaveRef => ref _wave;

        /// Spawns a projectile (spec §3.5/§3.6). Capped at Arena.MaxProjectiles —
        /// once full, spawns are skipped and counted rather than growing the array,
        /// keeping the cap degradation allocation-free and deterministic.
        /// `ownerIndex` (Stage 2 Task 7) is the shooter's own PlayerAt index for a
        /// Player-owned shot, else ProjectileIds.NoOwner — required (no default):
        /// both battle call sites (WeaponSystem, MobAiSystem) must say explicitly
        /// who fired.
        internal int SpawnProjectile(ProjectileOwner owner, byte ownerIndex, float2 pos, float2 vel,
            float height, float velZ, float damage, float radius, float ttl)
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
                Id = id, Owner = owner, OwnerIndex = ownerIndex, Pos = pos, PrevPos = pos, Vel = vel,
                Height = height, PrevHeight = height, VelZ = velZ,
                Damage = damage, Radius = radius, Ttl = ttl
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
            // SimEvent.PlayerIndex's own doc describes.
            Emit(SimEventKind.ProjectileFired, pos, id, default, math.atan2(vel.y, vel.x), owner,
                playerIndex: ownerIndex);
            return id;
        }

        /// Removes a projectile by swapping the last slot into its place — O(1),
        /// no shifting, consistent with the _projectileCount pattern above.
        /// Consumer: Task 16 (projectile tick/expiry/hit resolution).
        internal void RemoveProjectileAt(int index)
        {
            _projectiles[index] = _projectiles[--_projectileCount];
        }

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
        /// a Player-owned projectile into this method (a Mob-owned one always hits
        /// the player instead, via DamagePlayer), so ownerIndex is never actually
        /// NoOwner on the production path — but crediting must not silently trust
        /// that invariant forever, and an unguarded `_matchStats[NoOwner]` would
        /// also be an out-of-range index on top of the wrong credit.
        internal void DamageMob(int index, float dmg, float2 pos, HitZone zone, float2 dir, byte ownerIndex)
        {
            _mobs[index].Hp -= dmg;
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
                    zone: zone, hitDir: dir);
                _mobs[index] = _mobs[--_mobCount];
            }
        }

        /// Guarded stat increments (spec §3.12): a player's own stats freeze the
        /// tick THAT player dies, even for damage from projectiles already in
        /// flight at that moment. Stage 2 Task 5: indexed by shooter — Stage 2
        /// Task 7: DamageMob above now passes the projectile's actual OwnerIndex
        /// instead of a hardcoded 0, see its own comment.
        void IncrementShotsHit(int index) { if (_players[index].Alive) _matchStats[index].ShotsHit++; }
        void IncrementKills(int index) { if (_players[index].Alive) _matchStats[index].Kills++; }
        void IncrementHeadshotKills(int index) { if (_players[index].Alive) _matchStats[index].HeadshotKills++; }

        /// Applies projectile damage to the player (spec Interfaces, Task 16/23): a
        /// no-op once the player is already dead (spec §3.12 — stats stay frozen and
        /// no further PlayerDamaged/PlayerDied events fire); otherwise active dash
        /// i-frames absorb the hit with no event, else Hp drops and, once it reaches
        /// zero, the player dies exactly once.
        /// `dmg` is the POST-multiplier amount, same contract as DamageMob above;
        /// `zone`/`dir` ride along on PlayerDamaged and, on the killing blow, on
        /// PlayerDied too (the death VFX wants the blow that ended the run).
        internal void DamagePlayer(float dmg, float2 pos, HitZone zone, float2 dir)
        {
            // T5 fix-round 1 M-3: single named source for "who" instead of two
            // mechanically-unrelated-looking `0` literals (ref _players[0] here,
            // _matchStats[0] below) — both act on the same victim by construction,
            // this makes that fact explicit instead of implicit-by-coincidence.
            int victim = 0; // real victim index arrives in Stage 2 Task 17 (PvP)
            ref PlayerState p = ref _players[victim];
            if (!p.Alive) return;
            if (p.IframeTimer > 0f) return;

            p.Hp -= dmg;
            _matchStats[victim].DamageTaken += dmg;
            // EntityId/playerIndex (Stage 2 Task 7 decision 5): both carry the
            // VICTIM's index, spec §3.2 — reuse the named `victim` above, not a
            // new literal.
            Emit(SimEventKind.PlayerDamaged, pos, victim, default, dmg, zone: zone, hitDir: dir,
                playerIndex: (byte)victim);

            if (p.Hp <= 0f)
            {
                // Stage 2 Task 8: death bookkeeping (timers/Alive/DeathTick/
                // PlayerDied) moved into KillPlayer — the single home both this
                // damage-death path and the no-damage KillPlayerNoDamage path
                // now share, instead of each keeping its own copy of the timer list.
                // Fix-round 1 I-1: `pos` (the blow's own origin — e.g. the
                // killing mob's position for a contact strike, MobAiSystem's
                // `w.DamagePlayer(cfg.ContactDamage, m.Pos, ...)`) is forwarded
                // unchanged, so the paired PlayerDamaged/PlayerDied above and
                // below carry the SAME Pos, exactly as before this task.
                KillPlayer(victim, zone, dir, pos);
            }
        }

        /// Stage 2 Task 8: single home for player-death bookkeeping — zeroes
        /// every death-relevant timer, sets Alive=false + DeathTick, and emits
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
        void KillPlayer(int index, HitZone zone, float2 dir, float2 blowPos)
        {
            ref PlayerState p = ref _players[index];
            p.Alive = false;
            _matchStats[index].DeathTick = _tick;
            p.DashTimer = 0f;
            // Task 12: DashSpeedCur has no meaning without an active dash
            // (DashTimer == 0 already says "not dashing") — zeroed for the
            // same clean-corpse-read reason as DashTimer itself, unlike
            // DashDir (a heading, deliberately left as-is below).
            p.DashSpeedCur = 0f;
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
            Emit(SimEventKind.PlayerDied, blowPos, index, default, 0f, zone: zone, hitDir: dir,
                playerIndex: (byte)index);
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

        /// Battle mob spawn (Task 22 Interfaces) — WaveSystem's sole entry point for
        /// turning a validated spawn position into a live mob. Spawned mobs start at
        /// Idle AI, but since Task 19 (Phase 6) MobAiSystem ticks every live mob
        /// unconditionally — a spawned mob is NOT a static target: from the very
        /// next Tick() it settles into Chase/Reposition-Fire like any other mob.
        /// Capped at Arena.MaxMobs: past the cap the spawn is skipped and counted
        /// (MobSpawnsSkipped) rather than growing the array; the caller (WaveSystem)
        /// is responsible for leaving the wave's spawn debt untouched when that
        /// happens so the skipped mob is retried once the cap has room again.
        internal int SpawnMob(MobType type, float2 pos)
        {
            if (_mobCount >= _mobs.Length)
            {
                // Stage 2 Task 5: shared arena resource, world-scoped counter.
                _worldStats.MobSpawnsSkipped++;
                return -1;
            }
            int id = _nextEntityId++;
            _mobs[_mobCount++] = new MobState
            {
                Id = id, Type = type, Pos = pos,
                Hp = type == MobType.Chaser ? _config.Chaser.MaxHp : _config.Gunner.MaxHp,
                Ai = MobAiState.Idle,
                // Deterministic handedness for Gunner strafe / SteerAround's dead-on
                // tangent tiebreak (Task 19 Interfaces) — no RNG needed.
                StrafeSign = (id & 1) == 0 ? 1 : -1
            };
            Emit(SimEventKind.MobSpawned, pos, id, type, 0f);
            return id;
        }

        /// Test-only alias for SpawnMob (Task 16 Interfaces, retargeted in Task 22
        /// once the battle spawn path existed — same cap/id/StrafeSign behaviour,
        /// named for test call-sites). Now also emits MobSpawned like the battle
        /// path does; no EditMode test asserts a specific MobSpawned count/absence
        /// (checked by grep — call-sites either don't inspect events at all or
        /// ClearEvents() before the window they measure), so this is not a
        /// behavioural change any existing test depends on.
        internal int SpawnMobForTest(MobType type, float2 pos) => SpawnMob(type, pos);

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
        internal int SpawnProjectileForTest(ProjectileOwner owner, float2 pos, float2 vel,
            float height, float velZ, float damage, float radius, float ttl, byte ownerIndex = 0)
            => SpawnProjectile(owner, ownerIndex, pos, vel, height, velZ, damage, radius, ttl);

        /// Test-only seam (Task 19 Interfaces): kills the player outright via the
        /// normal damage path (overkill amount) so MobAiSystem's "player dead"
        /// branch (all mobs → Idle) can be exercised deterministically. Reports a
        /// Body hit from +X (Task 6 signature ripple): the seam models "something
        /// killed the player", and Body/no-multiplier is the neutral choice — no
        /// caller of this seam asserts on the zone.
        internal void KillPlayerForTest()
            => DamagePlayer(_config.Hero.MaxHp + 1f, _players[0].Pos,
                HitZone.Body, new float2(1f, 0f));

        /// Test-only seam (Task 8 Interfaces): exposes the private Sanitize step
        /// so tests can assert the AimHeight NaN-map/clamp behaviour directly,
        /// without threading it through a full Tick(). Stage 2 Task 4: stays a
        /// thin, single-argument call into index 0 — HostileInput_*/Sanitize_*
        /// tests are not rewritten.
        internal SimInput SanitizeForTest(in SimInput raw) => Sanitize(raw, 0);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// Dev-only mob placeholder spawn for Presentation milestone 2 (spec Interfaces).
        /// Stripped from production builds — the sole public dev-surface method here.
        public int DevSpawnMob(MobType type, float2 pos) => SpawnMobForTest(type, pos);
#endif

        public SimEvent GetEvent(int i) => _events[i];

        public void ClearEvents() => _eventCount = 0;

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
            target.MobCount = _mobCount;
            System.Array.Copy(_mobs, target.Mobs, _mobCount);
            target.ProjectileCount = _projectileCount;
            System.Array.Copy(_projectiles, target.Projectiles, _projectileCount);
            target.Wave = _wave;
            // Stage 2 Task 5: PlayerStats mirrors Players' array-copy pattern
            // above; WorldStats is a single plain-struct assignment, same as Wave.
            System.Array.Copy(_matchStats, target.PlayerStats, _matchStats.Length);
            target.WorldStats = _worldStats;
        }

        /// Deep-copies the full canonical state (config excluded) for rollback/replay.
        /// Allocates — call outside the hot tick path.
        public WorldSave SaveState()
        {
            var save = new WorldSave
            {
                Tick = _tick,
                SpreadRng = _spreadRng,
                WaveRng = _waveRng,
                NextEntityId = _nextEntityId,
                PlayerCount = _players.Length,
                Players = new PlayerState[_players.Length],
                MobCount = _mobCount,
                Mobs = new MobState[_mobs.Length],
                ProjectileCount = _projectileCount,
                Projectiles = new ProjectileState[_projectiles.Length],
                Wave = _wave,
                Stats = new MatchStats[_matchStats.Length],
                WorldStats = _worldStats
            };
            System.Array.Copy(_players, save.Players, _players.Length);
            System.Array.Copy(_mobs, save.Mobs, _mobs.Length);
            System.Array.Copy(_projectiles, save.Projectiles, _projectiles.Length);
            // Stage 2 Task 5: Stats mirrors Players' array-copy pattern above.
            System.Array.Copy(_matchStats, save.Stats, _matchStats.Length);
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
            _tick = save.Tick;
            _spreadRng = save.SpreadRng;
            _waveRng = save.WaveRng;
            _nextEntityId = save.NextEntityId;
            System.Array.Copy(save.Players, _players, _players.Length);
            _mobCount = save.MobCount;
            System.Array.Copy(save.Mobs, _mobs, _mobs.Length);
            _projectileCount = save.ProjectileCount;
            System.Array.Copy(save.Projectiles, _projectiles, _projectiles.Length);
            _wave = save.Wave;
            // Stage 2 Task 5: Stats mirrors Players' array-copy pattern above.
            System.Array.Copy(save.Stats, _matchStats, _matchStats.Length);
            _worldStats = save.WorldStats;
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
        internal void SetWaveForTest(in WaveState w) => _wave = w;

        /// Test-only seam (Task 4): reads a live projectile slot back —
        /// SetProjectileForTest's counterpart, for tests asserting on
        /// post-tick projectile state (e.g. Height/PrevHeight after VelZ integration).
        internal ProjectileState GetProjectileForTest(int index) => _projectiles[index];

        /// Canonical order (spec §3.3; Task 3 — split rng into spreadRng/waveRng;
        /// Stage 2 Task 10 — multiplayer reorder, the one sanctioned golden re-pin
        /// of the stage-2 network phase):
        /// tick → spreadRng → waveRng → nextEntityId → playerCount → players[0..n)
        /// → mobCount+mobs → projectileCount+projectiles → wave → worldStats
        /// → statsCount+stats[0..n).
        ///
        /// Both counts are hashed before their arrays for the same reason
        /// _mobCount/_projectileCount always were: a length is state in its own
        /// right, and folding it in first makes two different-length worlds
        /// distinguishable even when their common prefix matches. playerCount and
        /// statsCount are equal by construction today (both are _players.Length);
        /// they are still hashed separately because the two arrays are separate
        /// state that a future desync must be able to tell apart.
        public ulong StateHash()
        {
            ulong h = StateHash64.Begin();
            h = StateHash64.Add(h, (ulong)_tick);
            h = StateHash64.Add(h, _spreadRng.state);
            h = StateHash64.Add(h, _waveRng.state);
            h = StateHash64.Add(h, _nextEntityId);
            h = StateHash64.Add(h, _players.Length);
            for (int i = 0; i < _players.Length; i++) h = HashPlayer(h, in _players[i]);
            h = StateHash64.Add(h, _mobCount);
            for (int i = 0; i < _mobCount; i++) h = HashMob(h, in _mobs[i]);
            h = StateHash64.Add(h, _projectileCount);
            for (int i = 0; i < _projectileCount; i++) h = HashProjectile(h, in _projectiles[i]);
            h = HashWave(h, in _wave);
            // Stage 2 Task 10: the match-wide counters get their own hash step at
            // their own canonical position instead of riding interleaved inside
            // HashStats as Task 5 temporarily left them.
            h = HashWorldStats(h, in _worldStats);
            h = StateHash64.Add(h, _matchStats.Length);
            for (int i = 0; i < _matchStats.Length; i++) h = HashStats(h, in _matchStats[i]);
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
            h = StateHash64.Add(h, p.FireCooldown); h = StateHash64.Add(h, p.Alive);
            // Task 14: aim-down-sights settle progress.
            h = StateHash64.Add(h, p.AimSettleTimer);
            // Task 10: slide state.
            h = StateHash64.Add(h, p.SlideDir); h = StateHash64.Add(h, p.SlideTimer);
            h = StateHash64.Add(h, p.SlideBufferTimer); h = StateHash64.Add(h, p.RunUpTimer);
            h = StateHash64.Add(h, p.PostDashSlideTimer); h = StateHash64.Add(h, p.LinkWindowTimer);
            // Stage 2 Task 10: the two edge-request rate-limit counters — real
            // per-player state that survives across ticks and decides whether the
            // next request is honoured, so a replay/rollback that dropped them
            // would diverge the moment a request lands.
            h = StateHash64.Add(h, p.DashRequestCooldownTicks);
            h = StateHash64.Add(h, p.SlideRequestCooldownTicks);
            return h;
        }

        static ulong HashMob(ulong h, in MobState m)
        {
            h = StateHash64.Add(h, m.Id); h = StateHash64.Add(h, (int)m.Type);
            h = StateHash64.Add(h, m.Pos); h = StateHash64.Add(h, m.Vel);
            h = StateHash64.Add(h, m.Hp); h = StateHash64.Add(h, m.StateTimer);
            h = StateHash64.Add(h, m.FireCooldown); h = StateHash64.Add(h, (int)m.Ai);
            h = StateHash64.Add(h, m.StrafeSign);
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
            h = StateHash64.Add(h, p.Pos); h = StateHash64.Add(h, p.PrevPos);
            h = StateHash64.Add(h, p.Vel); h = StateHash64.Add(h, p.Damage);
            h = StateHash64.Add(h, p.Radius); h = StateHash64.Add(h, p.Ttl);
            h = StateHash64.Add(h, p.Height); h = StateHash64.Add(h, p.PrevHeight);
            h = StateHash64.Add(h, p.VelZ);
            return h;
        }

        static ulong HashWave(ulong h, in WaveState w)
        {
            h = StateHash64.Add(h, (int)w.Phase); h = StateHash64.Add(h, w.WaveIndex);
            h = StateHash64.Add(h, w.PendingChasers); h = StateHash64.Add(h, w.PendingGunners);
            h = StateHash64.Add(h, w.AliveCount); h = StateHash64.Add(h, w.PhaseTimer);
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
            return h;
        }
    }
}
