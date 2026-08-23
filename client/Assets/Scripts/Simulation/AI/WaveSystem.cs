using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.AI
{
    /// Deterministic seed-driven wave director. THREE INDEPENDENT CADENCES,
    /// one per ring (bd app-ggvz Т4, spec §3.3): every WaveState instance
    /// runs its own timer, its own phase and its own debt, and the rings are
    /// only ever walked in the fixed order Outer -> Middle -> Core.
    ///
    /// THE TIMER RUNS ALWAYS, IN BOTH PHASES, and that is the whole fix.
    /// While it counts down the ring is quiet; when it reaches zero the ring
    /// gets a wave, whatever it was doing. What this replaces was the defect
    /// the task exists for: until Т4 the queue was moved by a full wipe of
    /// the WHOLE arena and by nothing else, so a live 252-second raid
    /// three-handed met exactly one wave of ten mobs.
    ///
    /// DIFFICULTY COMES FROM THE CLOCK, NEVER FROM A RING'S OWN HISTORY
    /// (DifficultyStepFor, spec Р315): the size (CountForTest) and the elite
    /// share (EliteShareFor) are indexed by the raid's difficulty step, so
    /// clearing a ring early buys SILENCE — a full pause window, handed back
    /// at the clear — and never a weaker wave.
    ///
    /// EVERY RING GETS A WHOLE WAVE OF ITS OWN. There is no single budget to
    /// divide any longer (SplitByZones and Wave.ZoneWeights are gone with Т4,
    /// spec §3.7): each ring calls CountForTest once, and the composition
    /// inside it is the existing elite-then-gunner-then-chaser split
    /// (StartWave).
    ///
    /// A RING THAT IS NOT RUNNING IS FROZEN IDEMPOTENTLY (RingIsFrozen):
    /// phase back to Waiting, timer to zero, all three debts to zero, EVERY
    /// TICK rather than on a detected transition — nothing has to remember
    /// whether the ring was live a tick ago, and no ring carries phantom debt
    /// it will never spawn. Two cases are frozen: everything but Outer on a
    /// zoneless arena, and Core from the moment the Director wakes (§3.6).
    ///
    /// While Active, every tick attempts one spawn per outstanding debt unit,
    /// archetype-minor inside the ring (Chaser before Gunner before Elite —
    /// coordinator R-50); a unit that cannot find a valid spot leaves its
    /// debt untouched for the next tick — the debt can never grow mid-wave,
    /// so this cannot hang even when the ring is fully blocked (spec §3.13
    /// item 5). A ring is CLEARED when its OWN debt is closed and none of its
    /// OWN mobs is left alive; that grows WorldStats.WavesCleared (still one
    /// counter for the world, meaning "rings cleared" from this task on) and
    /// reloads the ring's timer with the FULL WavePauseByZone window rather
    /// than with whatever was left of it.
    ///
    /// Does not tick at all while no player is alive (full death semantics
    /// landed in Task 23; extended from "the one player" to "every player" in
    /// Stage 2 Task 8).
    internal static class WaveSystem
    {
        // Wave archetypes only — Chaser(0)/Gunner(1)/Elite(2). Director(3)
        // never spawns through a wave (spec Р248/§3.4).
        const int WaveArchetypeCount = 3;

        public static void Update(SimulationWorld w)
        {
            // Stage 2 Task 8: early exit + WaveStarted/WaveCleared event
            // positions route through NearestAlivePlayer (from the arena
            // center — WaveSystem has no per-mob "from" point the way
            // MobAiSystem does) instead of the old solo-only
            // w.Player.Alive/w.Player.Pos. For a solo world this is
            // byte-for-byte the old "the one player, if alive" read. `false`
            // (nobody alive) reuses the SAME early return WaveSystem already had.
            if (!Targeting.NearestAlivePlayer(w, float2.zero, out int nearestIdx)) return;
            float2 nearestPlayerPos = w.PlayerAt(nearestIdx).Pos;

            // ONE read of SimulationWorld.Config per TICK, not one per ring
            // and not one per field: it is a PROPERTY returning the whole
            // struct by value (the rule every caller in this file already
            // obeys), so `w.Config.Arena.ZoneRadius.Length` inside the ring
            // loop would copy all of SimConfig three times a tick to read one
            // array length.
            SimConfig config = w.Config;
            WaveSimConfig cfg = config.Wave;
            bool zoneless = config.Arena.ZoneRadius.Length < 2;
            MatchPhase matchPhase = w.Match.Phase;

            // ONE pass over the mobs, counting the living by their RING OF
            // ATTRIBUTION (MobState.SpawnZone, Т1) and not by where they
            // happen to stand: a chaser that walked out of the middle ring is
            // still the middle ring's wave, and a ring is cleared when ITS
            // mobs are gone, not when the arena is empty. `stackalloc` keeps
            // the hot path allocation-free (AllocationTests' own rule); this
            // file has no `using System;`, hence the qualifier — the same one
            // TryFindMobSpawnPos' neighbors below already carry.
            System.Span<int> alive = stackalloc int[Zones.Count];
            MobState[] mobs = w.Mobs;
            int mobCount = w.MobCount;
            for (int i = 0; i < mobCount; i++) alive[(int)mobs[i].SpawnZone]++;

            for (int z = 0; z < Zones.Count; z++)
            {
                var zone = (Zone)z;
                ref WaveState wave = ref w.WaveRef(zone);

                if (RingIsFrozen(zone, zoneless, matchPhase))
                {
                    // Idempotent and unconditional, every tick, rather than on
                    // a detected transition (spec §3.3): nothing has to
                    // remember whether this ring was live a tick ago, and the
                    // state cannot carry phantom debt for a ring that will
                    // never spawn again. AliveCount is still written — the
                    // ring's mobs are real and telemetry has to see them.
                    wave.Phase = WavePhase.Waiting;
                    wave.PhaseTicks = 0;
                    wave.PendingChaser = wave.PendingGunner = wave.PendingElite = 0;
                    wave.AliveCount = alive[z];
                    continue;
                }

                // ONE unconditional decrement, in BOTH phases: while the ring
                // is Active this is counting down to its NEXT wave, which is
                // exactly what lets a wave arrive without a single kill.
                wave.PhaseTicks--;
                if (wave.PhaseTicks <= 0) StartWave(w, ref wave, in cfg, zone, nearestPlayerPos);

                // Deliberately re-reads wave.Phase rather than branching on the
                // countdown above: a wave that just started falls straight
                // through into working off its own debt this same tick (no
                // wasted tick spent merely transitioning phase).
                if (wave.Phase == WavePhase.Active)
                {
                    // ONE spawn budget PER RING PER TICK, declared here and
                    // threaded through all three archetypes by reference
                    // (Т5, spec §3.4 Р317). Declaring it inside the archetype
                    // loop — or letting SpawnPendingOfType keep its own — would
                    // turn MaxSpawnsPerZonePerTick into "N per archetype", i.e.
                    // three times the number the config actually names.
                    int spawnedThisTick = 0;

                    // Archetype-minor inside the ring (coordinator R-50) — the
                    // SAME order StartWave fills the debt in and HashWave
                    // reads it in. The ring-major half of that order is the
                    // loop this sits inside.
                    for (int t = 0; t < WaveArchetypeCount; t++)
                        SpawnPendingOfType(w, ref wave, in cfg, zone, (MobType)t,
                            alive, ref spawnedThisTick);

                    // THIS ring's debt against THIS ring's living mobs — the
                    // arena as a whole is no longer asked anything, which is
                    // the interim Т3 left behind. `alive[z]` was kept current
                    // by the spawns just above, so a ring can never be called
                    // clear in the very tick it seated its own wave.
                    if (wave.PendingTotal == 0 && alive[z] == 0)
                    {
                        // Stage 2 Task 5: world-scoped counter — counted once
                        // per match regardless of player count, not per
                        // player. Its MEANING moved with Т4 and that is a
                        // decision, not drift (spec §3.3): with three
                        // independent cycles it counts RINGS cleared rather
                        // than waves. The results screen's own "waves
                        // repelled" line (Presentation's DeathOverlayController)
                        // reads true in the new sense too, so the label stays.
                        w.WorldStatsRef.WavesCleared++;
                        w.Emit(SimEventKind.WaveCleared, nearestPlayerPos, wave.WaveIndex, default, 0f);
                        wave.Phase = WavePhase.Waiting;
                        // THE FULL WINDOW, even when less of it was left: this
                        // is the reward for clearing early (spec §3.3), and
                        // the reason this assignment sits AFTER the decrement
                        // above instead of before it.
                        wave.PhaseTicks = SimulationWorld.TicksFromSeconds(cfg.WavePauseByZone[z]);
                    }
                }

                wave.AliveCount = alive[z];
            }
        }

        /// THE ONE GUARD for "this ring runs no wave cycle at all" (spec
        /// §3.3/§3.6) — one home, so a mutation against it has one point of
        /// application and each of the two cases it covers is reachable by a
        /// test of its own.
        ///
        /// A ZONELESS ARENA (ArenaSimConfig.ZoneRadius.Length &lt; 2 — a legal
        /// input, lesson 315, and what several TestConfigs fixtures ship) has
        /// exactly one ring, Zone.Outer. This guard is also what keeps
        /// Geometry.ZoneSpawnRingRadius' named refusal for Middle/Core
        /// unreachable: the promise once held jointly by ZonelessWeights and
        /// SplitByZones is held here now, in a single place.
        ///
        /// THE CORE FALLS SILENT WITH THE DIRECTOR AND DOES NOT COME BACK
        /// (owner decision К8, Р185/Р253). The condition is `!= Farm`, NOT
        /// `== DirectorActive`, and that is the point: the latch is one-way,
        /// so this one test says both "the core belongs to the Director from
        /// the moment he wakes" and "waves never return to it after he dies"
        /// — the window over his body has to pass without fresh elites, or it
        /// stops being a window.
        static bool RingIsFrozen(Zone zone, bool zoneless, MatchPhase matchPhase)
            => (zone != Zone.Outer && zoneless)
                || (zone == Zone.Core && matchPhase != MatchPhase.Farm);

        /// Wave size for `waveIndex`, scaled by the number of players and
        /// capped at MaxMobsPerWave (spec §3.4). Stage 2 Task 16 — the single
        /// seam that owns the formula, so a test can exercise it without
        /// running a whole world.
        ///
        /// `waveIndex` here is **0-BASED** (wave 0 is the first wave, worth
        /// BaseCount at one player). The live WaveState.WaveIndex is 1-based —
        /// StartWave below therefore passes `wave.WaveIndex - 1`.
        internal static int CountForTest(in WaveSimConfig cfg, int waveIndex, int playerCount)
        {
            float scale = 1f + (playerCount - 1) * cfg.PerPlayerCountFrac;
            int scaled = (int)math.round((cfg.BaseCount + cfg.CountGrowth * waveIndex) * scale);
            // The cap bites AFTER the scale — MaxMobsPerWave is the arena's own
            // ceiling, not a per-player one.
            return math.min(scaled, cfg.MaxMobsPerWave);
        }

        /// THE ONE HOME OF THE DIFFICULTY CURVE (spec §3.3 Р315): the wave a
        /// ring starts is sized by the CLOCK of the raid, never by how many
        /// waves that ring has already seen. A counter would make every clear
        /// push the curve back by a whole pause, so the player who clears well
        /// would meet WEAKER waves than the one who ignores his ring — the
        /// exact opposite of ADR-001 §3.1 ("difficulty is tied to the clock,
        /// value to the place"). The step is stored NOWHERE (Р206): it is a
        /// pure function of the tick, computed where it is needed.
        ///
        /// Named after this repository's own convention for pure mappings
        /// (MobConfigFor, EliteShareFor, MaxHpFor, VisualScaleFor).
        ///
        /// Step 1 at the first wave and at every tick before it, then one more
        /// per DifficultyStepSeconds of raid.
        internal static int DifficultyStepFor(int tick, in WaveSimConfig cfg)
        {
            int stepTicks = SimulationWorld.TicksFromSeconds(cfg.DifficultyStepSeconds);
            // A NAMED REFUSAL TO DIVIDE, not math.max(1, stepTicks):
            // SimConfigBuilder.Validate holds DifficultyStepSeconds at two
            // ticks or more (Т2, Р336/Р320), so a non-positive divisor can
            // only arrive from a hand-built fixture that skipped the builder.
            // Same case and same answer as MatchFlowSystem's own `if
            // (periodTicks <= 0) return;` (R-180): no curve at all rather than
            // a DivideByZeroException, and never a silently invented number.
            if (stepTicks <= 0) return 1;
            int sinceFirstWave = tick - SimulationWorld.TicksFromSeconds(cfg.FirstWaveDelay);
            return 1 + math.max(0, sinceFirstWave) / stepTicks;
        }

        /// Hands ONE ring a whole fresh wave of its own (spec §3.3/§3.4).
        ///
        /// THE SIZE COMES FROM THE CLOCK: WaveIndex is ASSIGNED this tick's
        /// difficulty step, never incremented (Р334/Р315) — a counter would
        /// let a ring that is cleared often fall behind a ring nobody
        /// touches. `w.CurrentTick`, not `w.Tick`: the latter is the tick
        /// METHOD, and the number the ring needs is the world's tick counter.
        ///
        /// THE DEBT IS ASSIGNED, NOT ACCUMULATED (Р305). Until Т4 a wave
        /// could only begin on an empty debt, so a plain assignment was
        /// merely correct; with a timer that fires whatever the ring is
        /// doing, a wave can land on an unfinished one, and the remainder is
        /// DELIBERATELY OVERWRITTEN — debt piling up on a saturated ring
        /// would grow unbounded and discharge in one burst at the first
        /// thinning, which is the bomb Р305 exists to refuse.
        ///
        /// The ring reloads its OWN pause here as well as at a clear, so the
        /// next wave is one full window away either way.
        static void StartWave(SimulationWorld w, ref WaveState wave, in WaveSimConfig cfg,
            Zone zone, float2 eventPos)
        {
            wave.WaveIndex = DifficultyStepFor(w.CurrentTick, in cfg);
            int count = CountForTest(in cfg, wave.WaveIndex - 1, w.PlayerCount);

            // Coordinator R-59: the elite share peels off first and the
            // EXISTING GunnerShare formula (unchanged since Task 16) splits
            // what is left — one extra line, not a second system. Both are
            // indexed by the difficulty step, for the same reason the size is
            // (Р315): a cleared ring must not come back softer.
            float eliteShare = EliteShareFor(zone, wave.WaveIndex, in cfg);
            int elites = (int)math.round(count * eliteShare);
            int rest = count - elites;
            float gunnerShare = math.saturate(cfg.GunnerShareBase
                + cfg.GunnerShareGrowth * (wave.WaveIndex - 1));
            int gunners = (int)math.round(rest * gunnerShare);
            int chasers = rest - gunners;

            // Through the ONE mapping home (coordinator R-51), never by
            // touching the three fields directly.
            PendingRef(ref wave, MobType.Elite) = elites;
            PendingRef(ref wave, MobType.Gunner) = gunners;
            PendingRef(ref wave, MobType.Chaser) = chasers;

            // eventPos (Stage 2 Task 8): the nearest-alive-player position
            // Update already resolved above — see its own doc for why
            // StartWave doesn't re-resolve it itself. The number the event
            // carries is the DIFFICULTY STEP from this task on, not a wave
            // ordinal (SnapshotEvents' own doc says so on the wire side).
            w.Emit(SimEventKind.WaveStarted, eventPos, wave.WaveIndex, default, 0f);
            wave.Phase = WavePhase.Active;
            wave.PhaseTicks = SimulationWorld.TicksFromSeconds(cfg.WavePauseByZone[(int)zone]);
        }

        /// The ONE home for "archetype -> which WaveState field carries its
        /// debt" (coordinator R-51, lesson 279: a second mapping home makes a
        /// mutation on this one invisible). StartWave (writer),
        /// SpawnPendingOfType (reader/decrementer) and WaveZoneTests' own
        /// sentinel test go through this and nothing else. `default` is a
        /// THROW, not a silent fallback onto Chaser (spec Р251's own
        /// warning: "a new archetype silently counted as a gunner" is
        /// exactly the failure this project already refused once for
        /// MobType itself).
        ///
        /// Wave-cadence-per-zone (bd app-ggvz Т3): the ZONE half of the old
        /// (zone, archetype) pair is gone from here -- it is the index of the
        /// WaveState instance the caller hands in (SimulationWorld.WaveRef
        /// (Zone)), so the nine-way switch is a three-way one again.
        internal static ref int PendingRef(ref WaveState w, MobType type)
        {
            switch (type)
            {
                case MobType.Chaser: return ref w.PendingChaser;
                case MobType.Gunner: return ref w.PendingGunner;
                case MobType.Elite: return ref w.PendingElite;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(type),
                        $"no wave-debt field for type={type}");
            }
        }

        /// Elite's SHARE of a zone's own budget (spec §3.3 Р212/Р298,
        /// coordinator R-59): Outer grows a flat rate per wave up to
        /// EliteShareOuterCap (coordinator R-60 — a WaveSimConfig field, not
        /// a code constant: CRITICAL RULE 6 puts every wave balance number in
        /// a ScriptableObject); Middle is a flat share; Core is always 100%
        /// elite (spec's own table — the boss's honor guard, not a wave
        /// mob).
        static float EliteShareFor(Zone zone, int waveIndex, in WaveSimConfig cfg) => zone switch
        {
            Zone.Outer => math.min(cfg.EliteShareOuterGrowth * (waveIndex - 1), cfg.EliteShareOuterCap),
            Zone.Middle => cfg.EliteShareMiddle,
            Zone.Core => 1f,
            _ => throw new System.ArgumentOutOfRangeException(nameof(zone), zone,
                "EliteShareFor: unknown zone"),
        };

        /// One spawn attempt per outstanding debt unit of `(zone, type)`,
        /// bounded to the count of pending units at the start of this call —
        /// this is what makes a fully-blocked ring terminate every tick
        /// instead of hanging: a failed attempt neither grows nor re-tries
        /// within the same tick, it just leaves the loop counter to advance
        /// to the next (still-pending) unit.
        ///
        /// THREE LIMITS STAND IN THE LOOP, and every one of them leaves the
        /// debt where it is for the next tick (Т5): the Director's arena-wide
        /// slot reserve, the ring's living-mob ceiling, and the ring's
        /// per-tick spawn budget. `alive` is the WHOLE per-ring tally Update
        /// scanned this tick — not one ring's number — because the ceiling is
        /// read and bumped through the same array Update's own clear check
        /// reads afterwards. `spawnedThisTick` is `ref` and belongs to the
        /// RING, not to this call: the method runs once per archetype, so a
        /// by-value counter would spend the ring's budget three times over.
        static void SpawnPendingOfType(SimulationWorld w, ref WaveState wave,
            in WaveSimConfig cfg, Zone zone, MobType type,
            System.Span<int> alive, ref int spawnedThisTick)
        {
            // Stage 3 Т22 (spec §3.4 Р254, coordinator R-182): THE DIRECTOR'S
            // SLOT RESERVE, HELD FOR THE WHOLE RAID. The wave stops
            // DirectorReserveSlots short of MaxMobs so the Director and his
            // retinue always have room to be born — Р299 made the activation a
            // player's decision at a moment nothing can predict, so the slots
            // cannot be armed in advance, they have to be free the whole time.
            // Without it a packed world would send the phase to DirectorActive
            // with no Director in it, the liveness scan would read "already
            // dead", and the gate would open off a boss nobody fought.
            //
            // The gate sits BEFORE the placement search: a wave that is not
            // allowed to spawn should not spend a candidate search either. It
            // deliberately does NOT touch WorldStats.MobSpawnsSkipped — that
            // counter means "the world hit its PHYSICAL cap" (SpawnMob's own
            // contract, a shared arena outcome since Stage 2 Т5), and the
            // reserve is this director's own policy, not an arena refusal. The
            // debt is left untouched exactly as the cap branch below leaves it.
            int ceiling = w.Config.Arena.MaxMobs - w.Config.Flow.DirectorReserveSlots;

            ref int pending = ref PendingRef(ref wave, type);
            int n = pending;
            for (int i = 0; i < n; i++)
            {
                if (w.MobCount >= ceiling) return; // reserve — debt stays, retried next tick
                // Т5 (spec §3.4, owner decisions К6/Р317): the ring's own two
                // limits, standing here for exactly the reasons the reserve
                // paragraph above already gives — before the placement search,
                // and deliberately not touching WorldStats.MobSpawnsSkipped,
                // because neither of them is the arena refusing a spawn. The
                // first is HOW MANY of this ring's mobs may stand at once, the
                // second is HOW MANY of them may be seated in one tick.
                //
                // A RING WHOSE CEILING IS BELOW ITS WAVE IS NEVER CLEARED, and
                // that is intended rather than tolerated (spec §3.4): its debt
                // cannot reach zero, so its phase stays Active and its window
                // of quiet never comes. For the core, whose ceiling is a
                // garrison rather than a farm, that is the whole point.
                if (alive[(int)zone] >= cfg.MaxAliveByZone[(int)zone]) return; // debt stays
                if (spawnedThisTick >= cfg.MaxSpawnsPerZonePerTick) return;    // debt stays
                if (!TryFindMobSpawnPos(w, in cfg, zone, type, out float2 pos)) continue; // debt stays
                if (w.SpawnMob(type, pos, zone) < 0) continue; // MaxMobs cap — debt stays (MobSpawnsSkipped bumped)

                pending--;
                // BOTH counters follow the spawn WITHIN the tick. The ring's
                // live count, because Update's clear check reads it after this
                // loop and a stale zero would call a ring cleared in the very
                // tick it seated its wave — and because the ceiling above is
                // read once per attempt, so without this a single wave would
                // step straight over it. The tick budget, because the same
                // holds for it one archetype at a time.
                alive[(int)zone]++;
                spawnedThisTick++;
            }
        }

        /// Candidate angles are drawn only from `w.WaveRng.NextFloat(0, 2*PI)` (RNG
        /// discipline, spec §3.6; Task 3 — dedicated wave-director stream, split
        /// from weapon spread) — up to MaxSpawnAttempts draws. The FallbackSlots
        /// grid below is deliberately RNG-free (fixed, uniform angles): whether the
        /// fallback triggers or not never changes how much RNG state a candidate
        /// search consumes, keeping RNG consumption a pure function of world state
        /// (pending counts, live mobs, arena) rather than of luck.
        ///
        /// Stage 3 Task 15 (coordinator R-102/R-11): the search loop itself now
        /// lives in SpawnPlacement.TryFind, shared with container placement
        /// (Loot.ContainerStore) — this method only resolves the zone's own
        /// ring radius/mob radius and hands them, plus a WaveSpawnFilter
        /// closing over this call's own world/config/mob radius, to that
        /// shared home. `ref Random rng = ref w.WaveRng;` — same "local ref
        /// alias, then pass the LOCAL onward" idiom Update already uses for
        /// `ref WaveState wave = ref w.WaveRef(Zone.Outer);` above — the
        /// shared search mutates the SAME stream this call has always drawn
        /// from, not a copy (coordinator danger #1).
        /// Stage 3 Т22 (coordinator R-183): `internal`, and named for what it
        /// is — THE one home for "where may a battle mob be put down in this
        /// zone". The Director's retinue is spawned by the phase machine, not
        /// by a wave (Р215), but the rules it must respect are identical
        /// (distance to the nearest player, live-mob overlap, obstacles, walls,
        /// arcs with doors forgiven), and a second copy of that filter is
        /// exactly the drift that bit this project once already (Ф2 review A-6:
        /// arcs missing from RingSlotBlocked while its tests stayed green).
        internal static bool TryFindMobSpawnPos(SimulationWorld w, in WaveSimConfig cfg,
            Zone zone, MobType type, out float2 pos)
        {
            ArenaSimConfig arena = w.Config.Arena;
            // Coordinator R-54: the zone's own spawn ring, not the arena-wide
            // one — Geometry.ZoneSpawnRingRadius is the one home for this
            // arithmetic (also used by SimConfigBuilder.Validate's R-55
            // rule).
            float ringRadius = Geometry.ZoneSpawnRingRadius(zone, in arena, cfg.SpawnRingInset);
            float mobRadius = w.MobConfigFor(type).Radius;

            ref Random rng = ref w.WaveRng;
            var filter = new WaveSpawnFilter(w, in arena, cfg.MinSpawnDistanceToPlayer, mobRadius);
            return SpawnPlacement.TryFind(ref rng, cfg.MaxSpawnAttempts, cfg.FallbackSlots,
                ringRadius, in filter, out pos);
        }

        /// Rejects on obstacle overlap, wall overlap (Stage 2 Task 14, spec
        /// §3.3 — the same Geometry.OverlapsStadium the obstacle-clearance
        /// check elsewhere already uses, no second overlap function),
        /// live-mob overlap (both against the candidate's own archetype
        /// radius, the same CircleOverlap idiom used elsewhere for attack
        /// range / projectile hits) and distance-to-player below
        /// MinSpawnDistanceToPlayer.
        ///
        /// Stage 3 Task 15 (coordinator R-102): the circles+stadiums+arcs
        /// half now runs through SpawnPlacement.GeometryBlocked(doorsPassable:
        /// true) — a candidate inside a door cutout stays forgiven, exactly as
        /// before (a mob walks through doors; only a container may not,
        /// coordinator R-106 — Loot.ContainerStore's own filter passes
        /// `false` there instead). The two halves that stay HERE —
        /// distance-to-player, live-mob overlap — are wave-specific: a
        /// container has no "nearest player" rule and no other mobs to
        /// avoid, only other containers (coordinator §2: "половина
        /// отбраковки, которая не переезжает").
        readonly struct WaveSpawnFilter : ISpawnFilter
        {
            readonly SimulationWorld _w;
            // Coordinator R-114 (Ф2-precedent R-49/Т10, ProjectileSystem.cs:46 —
            // that hot loop already refused to copy a WHOLE MobSimConfig for a
            // single field): ONE copy of the struct per SEARCH CALL (i.e. per
            // TryFindSpawnPos invocation, not per candidate inside it — the
            // candidate/fallback loops both run against this SAME already-copied
            // value) — cheap and unavoidable, C# 9 has no ref-typed struct
            // fields, and GeometryBlocked genuinely needs the whole struct
            // (every array field). MinSpawnDistanceToPlayer below is the OPPOSITE
            // case: a single float read out of WaveSimConfig, so the filter
            // holds just that float instead of a second whole-struct copy.
            readonly ArenaSimConfig _arena;
            readonly float _minSpawnDistanceToPlayer;
            readonly float _mobRadius;

            public WaveSpawnFilter(SimulationWorld w, in ArenaSimConfig arena,
                float minSpawnDistanceToPlayer, float mobRadius)
            {
                _w = w;
                _arena = arena;
                _minSpawnDistanceToPlayer = minSpawnDistanceToPlayer;
                _mobRadius = mobRadius;
            }

            public bool IsValid(float2 pos)
            {
                // Stage 2 Task 8: distance-to-player check now respects EVERY
                // alive player via NearestAlivePlayer(from the candidate spawn
                // point) instead of the old solo-only w.Player.Pos — a candidate
                // must clear MinSpawnDistanceToPlayer from whichever alive player
                // is closest to IT specifically (not the Update-level "nearest to
                // arena center" player — a candidate can be close to a player who
                // isn't the one nearest the center, so this is recomputed per
                // candidate, not threaded down from Update). `!NearestAlivePlayer`
                // can't actually happen here (Update's own early exit above
                // already returns before this is ever reached), but the
                // short-circuit still reads correctly on its own terms: no alive
                // player means no distance constraint to violate.
                if (Targeting.NearestAlivePlayer(_w, pos, out int nearestIdx)
                    && math.distance(pos, _w.PlayerAt(nearestIdx).Pos) < _minSpawnDistanceToPlayer)
                    return false;

                if (SpawnPlacement.GeometryBlocked(in _arena, pos, _mobRadius, doorsPassable: true))
                    return false;

                MobState[] mobs = _w.Mobs;
                int count = _w.MobCount;
                for (int m = 0; m < count; m++)
                    if (Geometry.CircleOverlap(pos, _mobRadius, mobs[m].Pos,
                            _w.MobConfigFor(mobs[m].Type).Radius))
                        return false;

                return true;
            }
        }
    }
}
