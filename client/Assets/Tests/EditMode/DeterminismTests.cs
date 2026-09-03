using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class DeterminismTests
    {
        const int Ticks = 1000;

        static ulong HashAfterTicks(long seed, int ticks)
        {
            var world = new SimulationWorld(seed, TestConfigs.Default());
            var idle = default(SimInput);
            for (int i = 0; i < ticks; i++)
                world.Tick(idle);
            return world.StateHash();
        }

        /// Scripted input generator (Task 29 Interfaces) — a separate Random in
        /// TEST code is fine, this is not Simulation. Drives every input axis so
        /// the determinism/golden runs below exercise movement, aiming, firing,
        /// dashing, sliding, both fire modes and — since app-88jb Т34 — the
        /// rewind depth together instead of just idle-input replay. `aimHeld`
        /// is threaded in by ref from RunScripted
        /// (a LOCAL there, not a static field here — statics would leak across
        /// RunScripted's three per-test-session calls and make the golden hash
        /// order-dependent, Task 16 QA5/QB5/QD5) so the aim level persists
        /// across ticks within one scripted run. `maxAimHeight` comes in the same
        /// way, from the caller's own config (Г4 review): the AimHeight draw's
        /// upper bound has to BE Hero.MaxAimHeight — that is what Sanitize clamps
        /// to, and what keeps the tower head belts inside the scripted run's reach
        /// — so a literal here would silently drift the day that number moves.
        /// `rewindCap` comes in exactly the same way and for exactly the same
        /// reason, from the caller's own cfg.Arena.RewindCapTicks: the
        /// RewindTicks draw's upper bound has to BE the arena cap Sanitize
        /// clamps to, and a literal here would have gone stale, silently, on
        /// the day app-gtj6 lowered that cap.
        /// Draw order is FIXED (reorder only with a golden repin): MoveDir
        /// direction, MoveDir magnitude, AimPoint, FireHeld, DashRequested,
        /// SlideRequested, aimHeld toggle roll, AimHeight, RewindTicks.
        ///
        /// THE NINTH DRAW IS LAST ON PURPOSE (app-88jb Т34, plan Т34 step 2).
        /// The eight draws before it keep their order inside the tick, so
        /// nothing that reasons about "the N-th draw of a tick" has to be
        /// re-read; their VALUES from the second tick on do change, because
        /// every tick now consumes nine draws of the one stream instead of
        /// eight — and that movement of all three digests is the ONE sanctioned
        /// re-pin of Т34 (owner decision Н1/Р352), taken in a commit of its
        /// own that follows this change, not a side effect for anyone to hunt
        /// down.
        /// UNIFORM OVER THE WHOLE DOMAIN, 0..cap INCLUSIVE — NextInt(min, max)
        /// answers [min, max), hence the `+ 1` — because a depth has three
        /// different fates and the scenario has to reach all three: k = 0 is
        /// the path with no rewind at all, which stays in the digest rather
        /// than vanishing from it; 0 < k <= Arena.RewindPictureTicks buys the
        /// PICTURE half only (RewindSplit.PictureTicks — the round's
        /// RewindLeft, Т28); k above the picture depth buys the INPUT half as
        /// well (RewindSplit.InputTicks — Т27's catch-up steps on the birth
        /// tick, which is what puts a ProjectileFired's BirthSteps above 1).
        /// OFFLINE, NOBODY ELSE WRITES THIS FIELD. In a networked match the
        /// depth is measured on the client (NetworkSimBackend.
        /// MeasureRewindTicks) and rides the wire in; an offline world reads a
        /// zero forever, so the scripted scenario has to feed the field itself
        /// — exactly as it feeds AimHeight, which no offline device produces
        /// either. Every value drawn is inside the arena's own domain by
        /// construction, so Sanitize's clamp is a no-op on this stream.
        static SimInput Scripted(ref Unity.Mathematics.Random rng, ref bool aimHeld,
            float maxAimHeight, int rewindCap)
        {
            var moveDir = rng.NextFloat2Direction() * rng.NextFloat();
            var aimPoint = rng.NextFloat2(new float2(-30f, -30f), new float2(30f, 30f));
            bool fireHeld = rng.NextFloat() < 0.7f;
            bool dashRequested = rng.NextFloat() < 0.05f;
            bool slideRequested = rng.NextFloat() < 0.05f;
            if (rng.NextFloat() < 0.03f) aimHeld = !aimHeld; // ~3%/tick toggle chance
            // tower head belts [2.70, 3.50] stay reachable: the bound IS the
            // config's MaxAimHeight, threaded in rather than restated
            float aimHeight = rng.NextFloat(0f, maxAimHeight);
            byte rewindTicks = (byte)rng.NextInt(0, rewindCap + 1); // [0, cap]: NextInt's max is exclusive

            return new SimInput
            {
                MoveDir = moveDir,
                AimPoint = aimPoint,
                FireHeld = fireHeld,
                DashRequested = dashRequested,
                SlideRequested = slideRequested,
                AimHeld = aimHeld,
                AimHeight = aimHeight,
                RewindTicks = rewindTicks
            };
        }

        /// Fixed world seed (42, same as the other tests in this file) driven by
        /// scripted input from an independently-seeded rng — isolates
        /// input-driven determinism from world-seed-driven determinism.
        /// The scripted SOLO scenario's own start point (Stage 3 Ф5-0, owner
        /// decision R-173) — stated here instead of inherited from wherever
        /// the spawn formula happens to put player 0.
        ///
        /// WHY IT IS STATED AT ALL. Until Ф5-0 a solo world spawned at the
        /// arena center, in the middle of this fixture's inner cluster of
        /// circles, and the scenario ricocheted dashes off them without ever
        /// saying so (GoldenScenario_ExercisesAllMechanics_Coverage is what
        /// noticed). Ф5-0 moved the solo spawn onto the one-player ring point,
        /// an empty stretch of rim where the nearest circle is ~69 m away
        /// along the arc: the run still slid and dashed, but its ricochet
        /// count fell to zero — a silent loss of coverage in the very scenario
        /// the golden digest pins. Anchoring the run beside a circle restores
        /// it and, unlike the old arrangement, says out loud what the scenario
        /// needs from the arena.
        ///
        /// WHY THIS PARTICULAR SPOT. The anchor is the OUTERMOST circle the
        /// fixture ships (found by arithmetic, not by index — the layout is
        /// data and may be retuned), and the player stands half a dash clear
        /// of its surface, so a dash in its direction reaches it and a dash
        /// away from it does not. Two constraints it must satisfy, both
        /// checked by ScenarioStart_IsClearOfTheCoreAndInsideTheArena below:
        /// the run must stay well outside the CORE, because a live collector
        /// standing there is what activates the Director from Т21 on (Р299)
        /// and this digest must not depend on that; and it must sit inside the
        /// arena rim with room to move.
        static float2 ScenarioStart(in SimConfig cfg)
        {
            int anchor = 0;
            for (int i = 1; i < cfg.Arena.ObstacleCount; i++)
            {
                if (math.lengthsq(cfg.Arena.ObstaclePos[i]) >
                    math.lengthsq(cfg.Arena.ObstaclePos[anchor]))
                {
                    anchor = i;
                }
            }
            // bd app-3cph: the clearance is ONE HERO RADIUS now, not half a
            // dash. Half a dash bought a symmetric choice — "a dash toward it
            // reaches it, a dash away does not" — and that was enough while the
            // anchor sat in a tight pocket: at rim 113 the outermost circle
            // stood 101 m out with the rim 12 m past it and the zone arc 9 m
            // short of it. The В1 playtest tripled both rings, the same pocket
            // is now 43 m wide, and the scripted walk simply leaves and never
            // comes back — GoldenScenario_ExercisesAllMechanics_Coverage read
            // zero ricochets, which is the coverage loss Ф5-0 introduced this
            // helper to prevent in the first place. Standing against the
            // surface makes the FIRST inward dash a contact instead of a
            // lottery ticket, and it does so at any arena size.
            float2 obstacle = cfg.Arena.ObstaclePos[anchor];
            float gap = cfg.Arena.ObstacleRadius[anchor] + cfg.Hero.Radius * 2f;
            return obstacle + math.normalize(obstacle) * gap;
        }

        static ulong RunScripted(uint inputSeed, int ticks)
        {
            SimConfig cfg = TestConfigs.Default();
            var world = new SimulationWorld(42, cfg);
            TestWorlds.RelocatePlayerForTest(world, 0, ScenarioStart(in cfg));
            var rng = new Random(inputSeed);
            bool aimHeld = false; // LOCAL — RunScripted runs 3x/session, no static leak (QA5/QB5/QD5)
            for (int i = 0; i < ticks; i++)
                world.Tick(Scripted(ref rng, ref aimHeld, cfg.Hero.MaxAimHeight,
                    cfg.Arena.RewindCapTicks));
            return world.StateHash();
        }

        /// Stage 2 Task 10: the multiplayer counterpart of RunScripted above —
        /// three players, each fed its OWN scripted input drawn from the same
        /// local Random in increasing player order, so every player's stream is
        /// independent of the others' while the whole run stays reproducible
        /// from the one input seed. This is what pins the canonical hash order's
        /// player/stats ARRAY halves (playerCount + players[0..n), statsCount +
        /// stats[0..n)): the solo golden below can only ever exercise index 0.
        /// `aimHeld` is one LOCAL flag per player (an array element passed by
        /// ref) for the same no-static-leak reason RunScripted's own local has.
        static ulong RunMultiScripted(uint inputSeed, int ticks, int playerCount)
        {
            SimConfig cfg = TestConfigs.Default();
            var world = new SimulationWorld(42, cfg, playerCount);
            var rng = new Random(inputSeed);
            var aimHeld = new bool[playerCount];
            var inputs = new SimInput[playerCount];
            for (int i = 0; i < ticks; i++)
            {
                for (int p = 0; p < playerCount; p++)
                    inputs[p] = Scripted(ref rng, ref aimHeld[p], cfg.Hero.MaxAimHeight,
                        cfg.Arena.RewindCapTicks);
                world.TickAll(inputs);
            }
            return world.StateHash();
        }

        // ------------------------------------------------------------------
        // Stage 3 Т36 (plan Т36, spec §4 Р295): THE THIRD GOLDEN — the
        // extraction LOOP, end to end. The two constants above pin a thousand
        // ticks of farming; nothing in this file has ever pinned what the raid
        // is actually about: a collector walks into the core, the Director
        // wakes, dies, the gate opens ninety seconds later and somebody walks
        // out through it. Every one of those transitions is a branch the
        // farming scenarios never reach.
        //
        // IT IS A NEW CONSTANT, NOT A RE-PIN, and spends no sanction (errata
        // §4 says so in as many words). A new scenario cannot move a digest
        // that never covered it.
        // ------------------------------------------------------------------

        /// 18 000 ticks = 600 s at the 30 Hz tick (plan Т36). The window has to
        /// hold the whole chain and it is stated as the sum of its parts, not
        /// as a round number: 120 s before the walk-in (below), then the
        /// Director's fight, then GateDelaySeconds (90 s in the shipped Flow),
        /// then ExtractChannelSeconds (20 s) — with room to spare on either
        /// side, and still well inside NetConfig's own 900 s match cap.
        const int ExtractionTicks = 18_000;

        /// Р295: the walk-in starts at the 120th second, late enough that the
        /// scenario farms like a normal raid first (waves, loot, the outer
        /// ring) and the endgame is not simply the whole run.
        const int CoreEntryTick = 3_600;

        /// The SECOND player walks in (lesson 227). Index 0 is the one every
        /// solo path already exercises, so driving it here would leave the
        /// "somebody else triggered the endgame" half of the phase machine
        /// unpinned — MatchFlowSystem reads a live collector's zone, not
        /// player 0's.
        const int CoreWalker = 1;

        /// The walk-in input for that player, every tick from CoreEntryTick on.
        /// It never stops steering, and that is deliberate — the gate opens AT
        /// (0, 0) (ArenaConfig's own ExtractPos), so the same walk that wakes
        /// the Director is the walk that stands on the gate when it finally
        /// opens, and the run pins the channel as well as the activation.
        ///
        /// IT STEERS THROUGH THE DOORS, NOT AT THE CENTER, and that is not a
        /// refinement — it is the difference between this scenario existing and
        /// not. A straight line inward is what the first draft did, and
        /// ExtractionScenario_ReachesTheWholeLoop failed on it: player 1 of
        /// three spawns at 120 deg, the outer ring's doors are at 30/150/270,
        /// so the collector spent 480 s pressed against a solid arc while the
        /// digest sat perfectly stable on a raid where nothing ever happened.
        /// That is lesson 412 in its purest form, and the reason that guard was
        /// written before this helper was trusted.
        ///
        /// TWO PHASES PER RING, the way a person walks it: first go AROUND at
        /// the current radius until lined up with a doorway, then go IN through
        /// it. "Lined up" is measured in METERS of lateral offset rather than
        /// in radians, because a doorway is a fixed 6 m wide at any radius —
        /// a quarter of its width is the aim tolerance, which leaves the whole
        /// remaining half-width as margin against the shoving of a crowd.
        ///
        /// UNIT LENGTH throughout, because SimInputSanitizer caps MoveDir at
        /// one and a longer vector would merely be clamped — stating the cap
        /// here keeps the scripted stream honest about what the world will
        /// actually see.
        static float2 WalkInToCore(float2 pos, in SimConfig cfg)
        {
            float r = math.length(pos);
            if (r <= 1e-6f) return float2.zero;
            float2 inward = -pos / r;

            // The outermost boundary still standing between this body and the
            // core. Walls are authored outermost-last, so the scan runs down.
            for (int w = cfg.Arena.ZoneWallCount - 1; w >= 0; w--)
            {
                float ring = cfg.Arena.ZoneWallRadius[w];
                if (r <= ring) continue;

                float here = math.atan2(pos.y, pos.x);
                int first = cfg.Arena.ZoneWallDoorStart[w];
                int count = cfg.Arena.ZoneWallDoorCount[w];
                int best = first;
                float bestGap = float.MaxValue;
                for (int d = first; d < first + count; d++)
                {
                    float gap = math.abs(WrapPi(cfg.Arena.DoorCenterRad[d] - here));
                    // Strictly-less keeps the SMALLER INDEX on an exact tie,
                    // which a three-door ring produces whenever a body sits
                    // halfway between two of them — and this run does sit there
                    // (150 deg is 60 deg from both 90 and 210).
                    if (gap < bestGap) { bestGap = gap; best = d; }
                }

                float offset = WrapPi(cfg.Arena.DoorCenterRad[best] - here);
                if (math.abs(offset) * r > cfg.Arena.DoorFreeWidth[best] * 0.25f)
                {
                    // Around: the tangent, turned toward the doorway.
                    float2 tangent = new float2(-pos.y, pos.x) / r;
                    return offset >= 0f ? tangent : -tangent;
                }
                return inward;
            }
            return inward;
        }

        /// Signed angle difference folded into (-pi, pi] — the plain idiom, kept
        /// local to this file because it exists here for one scripted walk and
        /// Simulation has no need of it.
        static float WrapPi(float a)
        {
            while (a > math.PI) a -= 2f * math.PI;
            while (a < -math.PI) a += 2f * math.PI;
            return a;
        }

        /// The third golden's generator. Same shape as RunMultiScripted above —
        /// same fixed world seed, same per-player scripted streams drawn in
        /// increasing player order — with two differences, both of them the
        /// point of the scenario: the fixture is TestConfigs.Extraction() (the
        /// shipped arena, containers and drop chances), and one player's
        /// MoveDir is OVERRIDDEN from CoreEntryTick on.
        ///
        /// THE OVERRIDE HAPPENS AFTER THE DRAW, NEVER INSTEAD OF IT. Scripted()
        /// is called for every player on every tick regardless, so the rng draw
        /// order is exactly the one RunMultiScripted uses and the walk-in
        /// changes what the world receives without changing what the stream
        /// produces. Skipping the draw for the walking player would make every
        /// OTHER player's input depend on the walk-in, which is not a scenario
        /// anyone could reason about.
        /// bd app-ggvz (wave cadence per ring): AN HP BUDGET FOR THE WHOLE
        /// SCRIPTED RUN, handed to EVERY collector. Same seam and same
        /// derivation TestWorlds.TrioSaturated already uses — not a second
        /// invention, and not a number picked by eye.
        ///
        /// (1) WHY A FIXTURE MAY DO THIS AT ALL. These scenarios measure
        /// DETERMINISM, never survivability — the reason every number in
        /// TestConfigs is deliberately modest (Р325). A digest cannot tell a
        /// run that walked the whole loop from one that died in its first
        /// minute, which is precisely why ExtractionScenario_ReachesTheWholeLoop
        /// stands beside it; a scenario whose collectors are corpses is the
        /// same defect that guard already caught once from the other side (a
        /// walker who spent 480 s against a wall).
        ///
        /// (2) WHY IT WAS NOT NEEDED BEFORE AND IS NEEDED NOW. Until the
        /// per-ring cadence the arena held ONE wave of ten mobs for a whole
        /// raid and a scripted random walk outlived it by default. With every
        /// ring running its own cadence the farm phase is genuinely lethal.
        /// MEASURED on this very generator, seed and tick count: the walker
        /// died on tick 666 and all three collectors by tick 1247, while the
        /// walk-in does not begin until CoreEntryTick (3600) — so nobody
        /// reached the core at all, the guard went red, and the digest was
        /// pinning a run of corpses. Ring ceilings do not fix it and were
        /// measured not to: the walker's death tick is 666 with them and 666
        /// without them, because the mobs that kill him are the ones that
        /// arrive, not the ones that pile up behind.
        ///
        /// (3) WHY THE NUMBER IS SAFE RATHER THAN TIGHT. It is TrioSaturated's
        /// own bound, term for term: the whole window at a deliberately
        /// over-stated combined damage rate — the worst-case zone multiplier on
        /// the weapon's own DPS, plus every single one of Arena.MaxMobs landing
        /// Chaser.ContactDamage every Chaser.AttackCooldown at once. That
        /// second term is impossible on its own terms, which is exactly the
        /// point: the bound only has to hold, never to be tight, and a
        /// safe-but-huge Hp costs these fixtures nothing (SetPlayerForTest
        /// bypasses Hero.MaxHp's clamp, and neither scenario calls ApplyConfig).
        ///
        /// It hands out Hp and moves NOBODY: each collector's own current
        /// position is read back and written unchanged.
        static void BudgetHpForTheWholeRun(SimulationWorld world, in SimConfig cfg, int ticks)
        {
            float totalSeconds = ticks * SimulationWorld.TickDt;
            float shotDps = TestWorlds.MaxPartDamageMult(cfg.Hero.Parts) * cfg.Weapon.Damage / cfg.Weapon.FireInterval;
            float mobDps = cfg.Arena.MaxMobs * cfg.Chaser.ContactDamage / cfg.Chaser.AttackCooldown;
            float hpBudget = totalSeconds * (shotDps + mobDps);
            for (int p = 0; p < world.PlayerCount; p++)
                TestWorlds.RelocatePlayerForTest(world, p, world.PlayerAt(p).Pos, hp: hpBudget);
        }

        static ulong RunExtractionScripted(uint inputSeed, int ticks, int playerCount)
        {
            SimConfig cfg = TestConfigs.Extraction();
            var world = new SimulationWorld(42, cfg, playerCount);
            BudgetHpForTheWholeRun(world, in cfg, ticks);
            var rng = new Random(inputSeed);
            var aimHeld = new bool[playerCount];
            var inputs = new SimInput[playerCount];
            for (int i = 0; i < ticks; i++)
            {
                for (int p = 0; p < playerCount; p++)
                {
                    inputs[p] = Scripted(ref rng, ref aimHeld[p], cfg.Hero.MaxAimHeight,
                        cfg.Arena.RewindCapTicks);
                    if (p == CoreWalker && i >= CoreEntryTick)
                        inputs[p].MoveDir = WalkInToCore(world.PlayerAt(p).Pos, in cfg);
                }
                world.TickAll(inputs);
            }
            return world.StateHash();
        }

        [Test]
        public void ExtractionGoldenHash_ScriptedScenario()
        {
            // FIRST PIN of a THIRD constant (plan Т36; errata §4 states in as
            // many words that Т36 introduces a third constant and that doing so
            // spends no sanction). Same first-run
            // procedure the two constants above document: with the constant at
            // 0 this assert fails and NUnit prints the actual hash.
            //
            // WHAT IT COVERS THAT THE OTHER TWO CANNOT. Both farming scenarios
            // live and die inside MatchPhase.Farm — their own docs say so, and
            // the elite leash of `app-d2ki` relies on it. This one crosses
            // every remaining transition: a collector enters the core, Т21's
            // phase machine latches, Т22 spawns the Director and his retinue,
            // he dies, DirectorDeathTick is stamped, GateDelaySeconds counts
            // down, the gate opens and the channel runs. It also runs the only
            // fully-populated arena in the file — 41 starting containers and
            // live drop chances — so container placement, item rolls and
            // corpse containers all enter the digest.
            //
            // PINNED AFTER `app-3cph`, ON PURPOSE and by the owner's own
            // instruction: the arena's rings tripled and the mob density
            // doubled in this same phase, so pinning this constant first would
            // have meant re-pinning it immediately. Т36 was moved behind that
            // work for exactly this reason, and this constant held its first
            // value until `app-ggvz` — see RE-PIN #4 below, which is the only
            // time it has ever moved.
            //
            // ⚠ WHAT IT DOES NOT COVER, measured and named rather than left to
            // be assumed from the paragraph above: THE DIRECTOR'S DEATH, the
            // GateDelaySeconds countdown and the extraction channel. Plan Т36
            // asks for all three; the code says no, and the code wins over the
            // plan (the plan's own errata says as much about itself). Two
            // probes over this exact scenario: the Director finished on 2500 HP
            // of 2500, and 2431 when every surviving collector was additionally
            // aimed straight at him for the whole 480 s — the three of them are
            // dead well before that, and the walker's entire magazine is 499
            // rounds. Killing a 2500 HP boss is PLAY. Recorded as bd
            // `app-7vkd` for the owner to decide what, if anything, should pin
            // the far half of the loop.
            //
            // ExtractionScenario_ReachesTheWholeLoop below is what keeps this
            // whole account honest rather than merely claimed — a digest is
            // stable whether or not the scenario it pins does anything, and
            // that guard is what caught the first draft of the walk-in walking
            // into a solid wall for 480 s.
            //
            // ------------------------------------------------------------------
            // RE-PIN #4 (bd `app-ggvz`, "wave cadence per ring"), THE FIRST AND
            // ONLY MOVEMENT OF THIS CONSTANT. The solo golden carries the full
            // account — the owner's sanction К9, the six causes, and the
            // attribution, including the value this constant held after Т1
            // alone (16270681601866834963). All six act here as they do there.
            //
            // ⚠ THIS SCENARIO HAS A SEVENTH CAUSE THE OTHER TWO DO NOT HAVE,
            // and it is named because the spec's list of six was written before
            // the cause existed (ruling Т5-2, session 47), not because it is
            // small. `BudgetHpForTheWholeRun` now grants the scripted collectors
            // an HP budget for the length of the run, through the same seam and
            // the same term-for-term formula `TestWorlds.TrioSaturated` uses.
            //
            // WHY IT WAS UNAVOIDABLE, measured rather than argued. With the
            // cadence in and no budget, this run's collectors died at tick 666
            // of 18 000 — waves now arrive every 60/90/90 ticks instead of once
            // per raid — and the walk into the core does not even begin until
            // tick 3600. The guard below went red (`Expected: True But was:
            // False`), i.e. the digest would have been pinned on a world that
            // stood still for 17 000 of its 18 000 ticks: "stable because
            // nothing happens" is exactly what that guard exists to refuse. The
            // alternative measured and REJECTED was lowering the fixture's
            // ceilings until the collectors survived: it takes {2,1,1} to get
            // there, a peak of 7 live mobs — BELOW the ten this arena held
            // before the task — which would have hollowed out the only fully
            // populated arena in the file. The principle is the file's own
            // (Р325): this scenario measures DETERMINISM, not balance.
            //
            // The budget is not free of consequence and the consequence is
            // recorded rather than left to be noticed later. The suite's run
            // time rose from 141 s before the task to about 245 s after it,
            // against a finding threshold of 306 s. That rise belongs to the
            // task as a whole — the arena carries tens of live mobs where it
            // carried ten — and the budget is a NAMED part of it, not the whole
            // of it: with the collectors alive, the six 18 000-tick runs of
            // this file now execute for all 600 s instead of freezing at around
            // tick 1250. Measured, not estimated.
            const ulong ExtractionGoldenHash = 0xA94975DFEDB976E9UL; // = 12198410670336210665
            Assert.AreEqual(ExtractionGoldenHash,
                RunExtractionScripted(123, ExtractionTicks, 3));
        }

        [Test]
        public void ExtractionScriptedRun_SameSeed_SameHash()
        {
            // Companion to the constant above, exactly as the two farming
            // goldens have: a pinned digest means nothing unless the run is
            // reproducible in the first place, and unless a different input
            // seed actually reaches a different world. The scenario costs
            // about 2.8 s per run, which is what makes a three-run companion
            // affordable here at all.
            Assert.AreEqual(RunExtractionScripted(123, ExtractionTicks, 3),
                RunExtractionScripted(123, ExtractionTicks, 3));
            Assert.AreNotEqual(RunExtractionScripted(123, ExtractionTicks, 3),
                RunExtractionScripted(43, ExtractionTicks, 3));
        }

        [Test]
        public void ExtractionScenario_ReachesTheWholeLoop()
        {
            // The coverage guard for the third golden, and the reason it exists
            // is lesson 412: a property with no witness is a surface checked
            // blind. The constant above CLAIMS this scenario walks the whole
            // loop; a digest cannot tell the difference between a run that
            // opens the gate and one that spends 600 s farming the periphery,
            // because both are perfectly stable. This is what tells them apart,
            // and it is deliberately written over the SAME generator, seed and
            // tick count, so it can never drift away from what the golden pins.
            SimConfig cfg = TestConfigs.Extraction();
            var world = new SimulationWorld(42, cfg, 3);
            // The SAME budget the generator above hands out, through the same
            // one home: this loop is a deliberate copy of that generator, and a
            // copy that skipped the budget would measure a different run than
            // the digest it exists to describe.
            BudgetHpForTheWholeRun(world, in cfg, ExtractionTicks);
            var rng = new Random(123);
            var aimHeld = new bool[3];
            var inputs = new SimInput[3];

            bool sawDirector = false, sawWalkerInCore = false;
            MatchPhase deepestPhase = MatchPhase.Farm;
            int containersAtStart = world.ContainerCount;

            for (int i = 0; i < ExtractionTicks; i++)
            {
                for (int p = 0; p < 3; p++)
                {
                    inputs[p] = Scripted(ref rng, ref aimHeld[p], cfg.Hero.MaxAimHeight,
                        cfg.Arena.RewindCapTicks);
                    if (p == CoreWalker && i >= CoreEntryTick)
                        inputs[p].MoveDir = WalkInToCore(world.PlayerAt(p).Pos, in cfg);
                }
                world.TickAll(inputs);

                if (world.Match.Phase > deepestPhase) deepestPhase = world.Match.Phase;
                if (Geometry.ZoneOf(world.PlayerAt(CoreWalker).Pos, in cfg.Arena) == Zone.Core)
                    sawWalkerInCore = true;
                for (int m = 0; m < world.MobCount; m++)
                    if (world.Mobs[m].Type == MobType.Director) { sawDirector = true; break; }
            }

            Assert.Greater(containersAtStart, 0,
                "premise: this is the fully populated arena, not a bare one — the loop it pins "
                + "includes looting, and a fixture with no containers cannot pin that");
            Assert.IsTrue(sawWalkerInCore,
                "the scripted walk-in must actually reach the core — Р295's whole point is that "
                + "the endgame is triggered from inside the run, not assumed");
            Assert.IsTrue(sawDirector,
                "…and the Director must actually have been spawned by it (Т22)");
            // THE CEILING IS ASSERTED, NOT THE WISH — and it is a MEASURED
            // ceiling (bd `app-7vkd`). Plan Т36 asks for the Director's death
            // and the gate's opening to fall inside this window too. They do
            // not, and no amount of window would change it: a probe over this
            // very scenario left the Director on 2500 HP of 2500, and a second
            // probe that additionally aimed every survivor straight at him for
            // all 480 s took him to 2431 — because the collectors are dead long
            // before that and the walker's whole magazine is 499 rounds. Killing
            // a 2500 HP boss is PLAY, not a scripted random walk.
            //
            // So this pins the depth the scenario actually reaches, by equality
            // rather than by "at least": the day the loop becomes reachable in
            // a scripted run, THIS is the assertion that says so out loud and
            // asks to be updated, instead of quietly passing while the golden
            // covers less than its own doc claims.
            Assert.AreEqual(MatchPhase.DirectorActive, deepestPhase,
                "the scenario reaches the Director's activation and stops there — his death and "
                + "the gate's opening are NOT covered by this digest (bd app-7vkd), and the "
                + "golden's own doc says so rather than implying otherwise");
        }

        [Test]
        public void SameSeed_SameHash_After1000Ticks()
        {
            Assert.AreEqual(HashAfterTicks(42, Ticks), HashAfterTicks(42, Ticks));
        }

        /// Stage 3 Task 15 (spec §4 Р296, coordinator §4): the cheap
        /// "two worlds on one seed give an equal hash" smoke test, extended
        /// to a fixture that actually exercises the loot-placement systems
        /// this task adds — SameSeed_SameHash_After1000Ticks above (and
        /// ScriptedRun_SameSeed_SameHash/MultiPlayerScriptedRun_SameSeed_
        /// SameHash below) all run TestConfigs.Default(), whose Loot counts
        /// stay at their golden-safety zeros (Т13), so none of them has ever
        /// placed a single container. Non-empty PREMISE required first
        /// (lessons 267/302, coordinator §4's own explicit warning): without
        /// it this collapses into the already-existing zero-container smoke
        /// test above and proves nothing new.
        /// Fixture editors: Т16 (non-zero drop chances put ITEMS inside
        /// these containers), Т36 (the third golden, a completely separate
        /// scenario — TestConfigs.Populated() is not that fixture).
        /// Coordinator fix-round (Т16, R-110 debt closure): a THIRD premise
        /// below (a placed container actually carries a non-empty slot)
        /// closes the debt this class doc's own "Fixture editors" line
        /// named — ContainerCount > 0 alone cannot tell "containers carry
        /// items" apart from "containers are placed empty", the exact
        /// vacuous-premise defect Р296 exists to rule out.
        [Test]
        public void SameSeed_SameHash_WithContainers()
        {
            SimConfig cfg = TestConfigs.Populated();
            Assert.Greater(cfg.Loot.CrateCount + cfg.Loot.CacheCountMiddle + cfg.Loot.CacheCountCore, 0,
                "premise: the fixture itself must actually request non-zero container counts");

            var w1 = new SimulationWorld(42, cfg);
            var w2 = new SimulationWorld(42, cfg);
            Assert.Greater(w1.ContainerCount, 0,
                "premise: the world must have actually PLACED containers, not merely been asked to");
            bool anyContainerHasContent = false;
            for (int i = 0; i < w1.ContainerCount; i++)
            {
                if (w1.Containers[i].SlotCount > 0) { anyContainerHasContent = true; break; }
            }
            Assert.IsTrue(anyContainerHasContent,
                "premise: at least one placed container must carry a non-empty slot — Т16's own " +
                "drop chances/repair-kit share must actually put items inside these containers, " +
                "not merely place them empty");

            for (int i = 0; i < Ticks; i++)
            {
                w1.Tick(default);
                w2.Tick(default);
            }

            Assert.AreEqual(w1.StateHash(), w2.StateHash());
        }

        [Test]
        public void DifferentSeed_DifferentHash()
        {
            Assert.AreNotEqual(HashAfterTicks(42, Ticks), HashAfterTicks(43, Ticks));
        }

        [Test]
        public void HashChangesBetweenTicks()
        {
            var world = new SimulationWorld(42, TestConfigs.Default());
            ulong before = world.StateHash();
            world.Tick(default);
            Assert.AreNotEqual(before, world.StateHash());
        }

        [Test]
        public void ZeroSeed_WorldIsAlive()
        {
            // folded seed 0 must be remapped, not fed to the RNG:
            // xorshift with state 0 silently yields zeros forever in player builds.
            var world = new SimulationWorld(0, TestConfigs.Default());
            ulong before = world.StateHash();
            world.Tick(default);
            Assert.AreNotEqual(before, world.StateHash());
            Assert.AreNotEqual(HashAfterTicks(0, Ticks), HashAfterTicks(1, Ticks));
        }

        [Test]
        public void SeedsFoldingToZero_SharePinnedWorld()
        {
            // Documented consequence of the 64->32 fold: 0 and -1 both fold to 0
            // and land on the same remapped seed. Pinned so a guard refactor is loud.
            Assert.AreEqual(HashAfterTicks(0, Ticks), HashAfterTicks(-1, Ticks));
        }

        [Test]
        public void NegativeSeed_IsDeterministicAndAlive()
        {
            Assert.AreEqual(HashAfterTicks(-42, Ticks), HashAfterTicks(-42, Ticks));
            Assert.AreNotEqual(HashAfterTicks(-42, Ticks), HashAfterTicks(42, Ticks));
        }

        [Test]
        public void StateHash64_MatchesFnv1a64GoldenVector()
        {
            // FNV-1a 64 of eight zero bytes, verified against an independent
            // implementation. Pins the algorithm across platforms and refactors.
            Assert.AreEqual(0xA8C7F832281A39C5UL, StateHash64.Add(StateHash64.Begin(), 0UL));
        }

        [Test]
        public void HostileInput_StateStaysFinite_AndDeterministic()
        {
            static ulong Run()
            {
                var w = new SimulationWorld(7, TestConfigs.Default());
                var nan = new SimInput
                {
                    MoveDir = new float2(float.NaN, float.PositiveInfinity),
                    AimPoint = new float2(1e9f, float.NegativeInfinity),
                    FireHeld = true, DashRequested = true,
                    AimHeight = float.NaN, AimHeld = true
                };
                var tooLong = new SimInput
                {
                    MoveDir = new float2(100f, -50f),
                    AimHeight = float.PositiveInfinity, AimHeld = true
                };
                for (int i = 0; i < 50; i++) w.Tick(nan);
                for (int i = 0; i < 50; i++) w.Tick(tooLong); // finite over-length dir
                for (int i = 0; i < 50; i++) w.Tick(default); // zero moveDir
                var p = w.Player;
                Assert.IsTrue(math.all(math.isfinite(p.Pos)) && math.all(math.isfinite(p.Vel)));
                // Stage 2 Task 10: the `nan` block above also holds
                // DashRequested true for 50 straight ticks — request spam is
                // hostile input in its own right, and this is the one existing
                // scenario that already exercised it. The rate limit must be
                // dropping most of it (Hero.EdgeRequestMinTicks = 3 keeps
                // roughly one request in three), and the finiteness and
                // determinism asserted around this line must hold WITH the gate
                // in the loop, not merely without it. Asserted, not assumed, so
                // that a gate accidentally disabled for hostile/NaN input turns
                // this red instead of silently reverting the scenario.
                Assert.Greater(w.RejectedEdgeRequestsForTest, 0,
                    "50 ticks of held DashRequested must reach the edge-request rate limit");
                return w.StateHash();
            }
            Assert.AreEqual(Run(), Run()); // two independent worlds, same hash
        }

        [Test]
        public void Sanitize_ClampsAimHeight_AndMapsNaNToMuzzle()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var over = w.SanitizeForTest(new SimInput { AimHeld = true, AimHeight = cfg.Hero.MaxAimHeight + 5f });
            Assert.AreEqual(cfg.Hero.MaxAimHeight, over.AimHeight, 1e-5f);  // clamp (fixture expr - PA2)
            var nan = w.SanitizeForTest(new SimInput { AimHeld = true, AimHeight = float.NaN });
            Assert.AreEqual(cfg.Hero.MuzzleHeight, nan.AimHeight, 1e-5f);   // NaN -> muzzle height
        }

        [Test]
        public void Sanitize_ClampsRewindTicksToTheArenaCap()
        {
            // app-88jb Т26 (spec §3.6, finding D2-I21). The home of the clamp
            // is SimInputSanitizer and not the codec: the server has to bring
            // a rewind depth into the arena's domain even when it did not come
            // from a client of ours, and 200 is exactly the hostile value the
            // family of tests around this one exists for — the same subject as
            // HostileInput_StateStaysFinite_AndDeterministic and as the
            // AimHeight clamp directly above, whose fixture shape this test
            // copies deliberately.
            //
            // The codec's own reading of an out-of-domain byte is a SEPARATE
            // contract — three bits offer eight values, the eighth reads as
            // the cap — and it is pinned where the wire lives, by
            // InputCodecTests.RewindTicksSeven_ReadsAsSix_AndDoesNotThrow.
            // Neither pin substitutes for the other: the arena cap never
            // travels on the wire at all.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            SimInput over = w.SanitizeForTest(new SimInput { RewindTicks = 200 });
            Assert.AreEqual((byte)cfg.Arena.RewindCapTicks, over.RewindTicks,
                "глубина не заклампена капом арены");
        }

        [Test]
        public void Sanitize_LeavesALegalRewindDepthAlone()
        {
            // app-88jb Т26 fix-round A (review finding B-1). The case above is
            // the only sanitizer pin this repo has for RewindTicks and it feeds
            // 200 — so far above the cap that the MUTATION
            // `s.RewindTicks = (byte)cfg.Arena.RewindCapTicks` (a plain
            // assignment instead of `math.min`) satisfies it exactly and kills
            // nothing. This is the axis that mutation dies on: a depth BELOW
            // the cap has to come back untouched, and an assignment of the cap
            // would read as 5.
            //
            // It is a SEPARATE test rather than a second case inside the one
            // above, because that one is named for the clamp and a legal depth
            // is not clamped by anything; folding it in would make the name a
            // lie. Green from its first day, like every characterizing test —
            // the sanitizer is already right, and what makes this one a witness
            // is the mutation, not a red run.
            //
            // TestConfigs.Open() carries the cap of 5 it inherits from
            // Default() (6 until app-gtj6 lowered it), so 2 is legal by
            // construction.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            SimInput legal = w.SanitizeForTest(new SimInput { RewindTicks = 2 });
            Assert.AreEqual((byte)2, legal.RewindTicks,
                "легальная глубина ниже капа арены обязана пережить санитайзер без изменений");
        }

        [Test]
        public void Sanitizer_MatchesWorldBehaviour()
        {
            // Stage 2 Task 6 fix-round 1 (I-1, review): the world-vs-seam loop
            // below is a WIRING check, not a formula check. After GREEN,
            // w.SanitizeForTest(raw) resolves to SimulationWorld.Sanitize(raw, 0),
            // which itself just calls SimInputSanitizer.Sanitize(raw,
            // _players[0], _config) — `actual` below calls the exact same
            // static function with the exact same arguments (w.Player ==
            // _players[0], w.Config == _config), so every assertion in the loop
            // is x == x for ANY seam body, including a broken one; it cannot
            // catch a formula regression. What it still catches: the world
            // silently failing to pass ITS OWN reference player/config into the
            // seam (stale player index, stale config) — inputs 3-6 below read
            // the reference player's Pos/AimPoint and would diverge if that
            // wiring broke. Formula correctness is pinned separately, below,
            // by property-based asserts that call the seam directly and never
            // go through the world.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.Pos = new float2(5f, -3f);
            p.AimPoint = new float2(1f, 1f);
            w.SetPlayerForTest(p);

            SimInput[] hostileInputs =
            {
                // 0) non-finite MoveDir -> zero.
                new SimInput { MoveDir = new float2(float.NaN, float.PositiveInfinity),
                    AimPoint = new float2(2f, 2f), AimHeight = 1f },
                // 1) over-length MoveDir (|v| = 5) -> normalized down to unit length.
                new SimInput { MoveDir = new float2(3f, 4f),
                    AimPoint = new float2(2f, 2f), AimHeight = 1f },
                // 2) sub-unit MoveDir (|v| = 0.5) -> partial stick deflection
                //    preserved, NOT forced up to 1.0 (spec §3.8).
                new SimInput { MoveDir = new float2(0.5f, 0f),
                    AimPoint = new float2(2f, 2f), AimHeight = 1f },
                // 3) non-finite AimPoint -> falls back to the reference player's
                //    own AimPoint.
                new SimInput { MoveDir = float2.zero,
                    AimPoint = new float2(float.NaN, float.NegativeInfinity), AimHeight = 1f },
                // 4) AimPoint far outside Arena.Radius * 2 from the reference
                //    player's Pos -> clamped onto that radius, AND non-finite
                //    AimHeight -> muzzle height.
                new SimInput { MoveDir = float2.zero,
                    AimPoint = new float2(1e9f, -1e9f), AimHeight = float.NaN },
                // 5) AimHeight above the cap -> clamps down to Hero.MaxAimHeight
                //    (upper bound of the unconditional clamp, fix-round 1 I-2).
                new SimInput { MoveDir = float2.zero,
                    AimPoint = float2.zero, AimHeight = cfg.Hero.MaxAimHeight + 5f },
                // 6) AimHeight below zero -> clamps up to 0 (lower bound; not
                //    exercised anywhere else in the repo before this fix-round).
                new SimInput { MoveDir = float2.zero,
                    AimPoint = float2.zero, AimHeight = -1f },
            };

            foreach (var raw in hostileInputs)
            {
                SimInput expected = w.SanitizeForTest(raw);
                SimInput actual = SimInputSanitizer.Sanitize(raw, w.Player, w.Config);

                Assert.AreEqual(expected.MoveDir.x, actual.MoveDir.x, 1e-5f);
                Assert.AreEqual(expected.MoveDir.y, actual.MoveDir.y, 1e-5f);
                Assert.AreEqual(expected.AimPoint.x, actual.AimPoint.x, 1e-5f);
                Assert.AreEqual(expected.AimPoint.y, actual.AimPoint.y, 1e-5f);
                Assert.AreEqual(expected.AimHeight, actual.AimHeight, 1e-5f);
            }

            // Property-based asserts (fix-round 1, I-1/I-2, review decision 2):
            // pin the seam's actual sanitization BEHAVIOUR against its
            // documented contract via fixture expressions (Global Constraints
            // C14 — no literal restating a config number), calling the seam
            // directly against the fixed reference player `p` and `cfg` —
            // independent of the world, so a broken FORMULA (not just broken
            // wiring) turns these red.
            SimInput r0 = SimInputSanitizer.Sanitize(hostileInputs[0], p, cfg);
            Assert.IsTrue(math.all(r0.MoveDir == float2.zero),
                "non-finite MoveDir must sanitize to zero");

            SimInput r1 = SimInputSanitizer.Sanitize(hostileInputs[1], p, cfg);
            Assert.AreEqual(1f, math.length(r1.MoveDir), 1e-5f,
                "over-length MoveDir (|v| = 5) must normalize down to unit length");
            Assert.AreEqual(0f, r1.MoveDir.x * 4f - r1.MoveDir.y * 3f, 1e-4f,
                "normalization must preserve the raw (3,4) heading (zero cross product)");

            SimInput r2 = SimInputSanitizer.Sanitize(hostileInputs[2], p, cfg);
            Assert.AreEqual(0.5f, math.length(r2.MoveDir), 1e-5f,
                "sub-unit MoveDir (|v| = 0.5) must NOT be forced up to 1.0 " +
                "(spec §3.8, analog stick partial deflection)");

            SimInput r3 = SimInputSanitizer.Sanitize(hostileInputs[3], p, cfg);
            Assert.AreEqual(p.AimPoint.x, r3.AimPoint.x, 1e-5f,
                "non-finite AimPoint must fall back to the reference player's own AimPoint");
            Assert.AreEqual(p.AimPoint.y, r3.AimPoint.y, 1e-5f);

            SimInput r4 = SimInputSanitizer.Sanitize(hostileInputs[4], p, cfg);
            float maxR = cfg.Arena.Radius * 2f; // fixture expression (C14) - the same formula the seam itself computes
            Assert.AreEqual(maxR, math.length(r4.AimPoint - p.Pos), 1e-2f,
                "out-of-radius AimPoint must land exactly on the clamp circle around the reference player's Pos");
            Assert.AreEqual(cfg.Hero.MuzzleHeight, r4.AimHeight, 1e-5f,
                "non-finite AimHeight must map to standing muzzle height");

            SimInput r5 = SimInputSanitizer.Sanitize(hostileInputs[5], p, cfg);
            Assert.AreEqual(cfg.Hero.MaxAimHeight, r5.AimHeight, 1e-5f,
                "AimHeight above the cap must clamp down to Hero.MaxAimHeight");

            SimInput r6 = SimInputSanitizer.Sanitize(hostileInputs[6], p, cfg);
            Assert.AreEqual(0f, r6.AimHeight, 1e-5f,
                "AimHeight below zero must clamp up to 0");
        }

        [Test]
        public void ScriptedRun_SameSeed_SameHash()
        {
            Assert.AreEqual(RunScripted(123, Ticks), RunScripted(123, Ticks));
            Assert.AreNotEqual(RunScripted(123, Ticks), RunScripted(43, Ticks));
        }

        [Test]
        public void GoldenHash_ScriptedScenario()
        {
            // Pin against a silent simulation-behavior change (spec §3.13 item 14):
            // world seed 42, scripted input from Random(123), 1000 ticks. First
            // run: the constant below is 0, this assert fails and NUnit prints
            // the actual hash — paste that value in and rerun for a green PASS.
            // Re-pinned by the final-fix-wave review round (F-1): MobAiSystem's
            // gunner FireCooldown now floor-clamps to 0 every tick (previously
            // unclamped, letting a Reposition/no-LoS stretch accrue negative
            // "debt" that paid off as a several-shots volley on LoS acquisition)
            // — the scripted scenario's waves spawn gunners, so this legitimately
            // changes their FireCooldown trace and therefore the hash.
            // Re-pinned by Task 4 (projectile Height/PrevHeight/VelZ entered the
            // hash): HashProjectile now folds in the three new vertical-motion
            // fields on every live projectile, so any scripted run that ever
            // spawns a projectile legitimately changes the hash.
            // Re-pinned by Task 6 (height gating + hit zones entered the
            // outcomes AND the hash): shots are now gated on the target's
            // vertical column and scaled by the zone they land in — most
            // visibly, a flat shot at hero muzzle height reads as Legs on the
            // taller Gunner tower and deals 0.75x — so the scripted run's
            // damage/kill trace legitimately differs; on top of that
            // MatchStats.HeadshotKills is a new field inside HashStats.
            // Re-pinned by Task 9 (stamina core): PlayerState gained Stamina
            // and StaminaRegenDelayTimer, both folded into HashPlayer — every
            // scripted tick now carries their trace (dash cost/regen/gate),
            // so the hash legitimately changes even though the scripted
            // scenario's dash/move/aim inputs themselves are unchanged.
            // Re-pinned by Task 10 (slide core): PlayerState gained SlideDir,
            // SlideTimer, SlideBufferTimer, RunUpTimer, PostDashSlideTimer and
            // LinkWindowTimer — all six folded into HashPlayer — plus
            // MatchStats.SlidesUsed into HashStats. Scripted()'s input never
            // sets SlideRequested, so a slide itself never starts (Slide*/
            // LinkWindowTimer/SlideDir stay at their zero default the whole
            // run), but RunUpTimer accrues/decays every tick off the
            // scripted MoveDir and PostDashSlideTimer opens on every scripted
            // dash's end — both are new per-tick trace inside HashPlayer, so
            // the hash legitimately changes even with slide itself dormant.
            // Re-pinned by Task 12 (dash ricochet): PlayerState gained
            // DashSpeedCur, folded into HashPlayer right after DashBufferTimer
            // — every scripted tick now carries its trace even on runs where
            // no dash ever hits a wall (dash start alone sets it to
            // Hero.DashSpeed), so the hash legitimately changes on that field
            // alone; on top of that, Scripted()'s DashRequested (5% per tick)
            // has always been able to send a dash into one of the scripted
            // scenario's five obstacles, and a dash that does now mirrors
            // instead of stopping dead — a further, real behavior change.
            // Re-pinned by Task 13 (predictive telegraph entry): the Chaser's
            // Chase->Telegraph entry check now compares against
            // Targeting.PredictPos(player.Pos, player.Vel, ...) instead of the
            // player's raw position — the scripted scenario's player is moving
            // (Scripted()'s MoveDir/DashRequested), so the predicted position
            // legitimately differs from the raw one and shifts every Chaser's
            // telegraph timing (and therefore hit/miss/damage trace) downstream.
            // Re-pinned by Task 14 (aim-in-motion cap/slide-mult/settle):
            // PlayerState gained AimSettleTimer, folded into HashPlayer right
            // after Alive — Scripted()'s input never sets AimHeld (that arrives
            // in Task 16), so the field itself stays at its zero default the
            // whole run, but it is still a new per-tick trace inside HashPlayer,
            // so the hash legitimately changes even with AimHeld dormant.
            // Re-pinned by Task 15 (two fire modes): Scripted()'s input never sets
            // AimHeld, so every shot in this run still takes the HIP branch — but
            // that branch's cone is now Spread.HipRadians, i.e. the base cone plus
            // recoil TIMES a movement multiplier (x1.5 above RunSpreadSpeedFrac of
            // MaxSpeed, which the scripted MoveDir crosses constantly), where Phase
            // 1 had no multiplier at all. The number of RNG draws is unchanged
            // (SpreadRad > 0 keeps the new draw-guard open on every hip shot), but
            // the drawn ANGLE is wider, so every shot's velocity — and with it the
            // whole downstream hit/kill trace — legitimately differs.
            // Re-pinned by Task 16 (FINAL repin of this package): Scripted() now
            // draws SlideRequested (5%/tick), rolls a ~3%/tick aimHeld toggle, and
            // draws AimHeight (0..3.8, covering the tower head belts [2.70, 3.50])
            // on every tick, in that fixed order after DashRequested — so every
            // scripted run now legitimately drives SlideRequested-triggered slides
            // and both fire modes (hip AND aimed, per the toggled aimHeld level)
            // instead of hip-only with slide permanently dormant. This changes the
            // RNG draw sequence itself (three new draws/tick) on top of exercising
            // previously-dormant slide/aim-mode branches, so the hash legitimately
            // differs from Task 15's. The scripted scenario now covers slide and
            // both fire modes end to end.
            // Re-pinned by В1 fix-wave 3 (owner-decided economy rework,
            // app-n6g, repin #11): HeroConfig's stamina economy changed
            // shape — StaminaMax 90->100, DashStaminaCost 48->40,
            // SlideStaminaCost 13->30, StaminaRegenPerSec 22->20,
            // StaminaRegenDelay 0.72->0.8, LinkedDashStaminaCost is deleted
            // outright (replaced by the new LinkRefund field + gate/refund
            // mechanics in PlayerMovementSystem.Update's linked-slide/
            // linked-dash branches). PlayerState itself gained no new field
            // (LinkRefund lives only on HeroConfig/HeroSimConfig), but every
            // one of these numbers feeds Stamina, already folded into
            // HashPlayer — so the scripted run's dash/slide stamina trace
            // legitimately differs from Task 16's. Corrected note (final
            // review wave, this paragraph originally claimed a linked slide
            // "cancels the dash cooldown" — that was mis-scoped and never
            // shipped: DashCooldown counts down untouched by either linked
            // branch, per SlideTests.LinkedSlide_DoesNotCancelDashCooldown_
            // OutOfWindowDashStillDenied and PlayerMovementSystem's own
            // linked-slide comment; the hash delta here is the changed
            // stamina numbers alone, nothing DashCooldown-related).
            //
            // Re-pinned by the final-fix-wave review round (C1, app-n6g,
            // repin #12): the slide-start branch's gate-failed path used to
            // leave p.Vel completely untouched for the whole
            // SlideBufferWindow every tick the buffer kept retrying/decaying
            // — it now calls RegularMoveVel there too, same as the dash
            // branch's own gate-fail else always has. Scripted()'s
            // SlideRequested draw (~5%/tick) can land on a tick the stamina
            // gate fails, so the scripted run's Vel/Pos trace on those ticks
            // legitimately differs from repin #11's.
            //
            // Re-pinned by Stage 2 Task 10 (repin #13, the ONE sanctioned golden
            // shift of the stage-2 network phase): the player array, WorldStats,
            // ProjectileState.OwnerIndex, the two edge-request rate-limit
            // counters and the edge-request gate itself all entered the hash.
            // Four distinct causes, all legitimate: (1) the canonical order is
            // now playerCount + players[0..n) ... worldStats + statsCount +
            // stats[0..n), so the counts and the array shape are hashed where
            // Task 5 hashed a single player and a single interleaved stats
            // block; (2) HashProjectile folds in OwnerIndex, live on every shot
            // the scripted run fires; (3) HashPlayer folds in
            // DashRequestCooldownTicks/SlideRequestCooldownTicks, which move on
            // every scripted dash/slide request; (4) the gate itself changes
            // BEHAVIOUR — Scripted() rolls DashRequested and SlideRequested at
            // 5%/tick each, so over 1000 ticks it repeatedly asks twice inside
            // one EdgeRequestMinTicks window, and the second ask is now dropped
            // before it can latch the (hashed) input buffer.
            //
            // Re-pinned by Stage 2 Task 16 (repin #14, the SECOND and LAST
            // sanctioned golden shift of the stage-2 network phase — spec
            // §3.4/§3.15). Everything that moves this hash, by code:
            // (1) ARENA GEOMETRY. TestConfigs.DefaultArena() — which every
            //     scripted run is built from — grew from Radius 35 / 5 circles
            //     / 0 walls to Radius 65 / 8 circles / 6 walls. Radius feeds
            //     SimInputSanitizer's AimPoint clamp, ClampInsideRing,
            //     SweepArena's ring boundary, WaveSystem's wave-spawn ring AND
            //     Geometry.SpawnPosFor's PLAYER spawn ring (Radius *
            //     PlayerSpawnRingFrac: 28 -> 52 — added by the Task 16 review,
            //     M-2, which caught it missing from this list; it moves nothing
            //     solo, where spawn is the origin, and is the heaviest single
            //     channel for the multiplayer golden below, whose three start
            //     positions all shift by 24 m). The three new circles and six
            //     walls are new blockers for movement, dash ricochets,
            //     projectiles, LoS and mob steering. The scripted player walks
            //     and shoots into all of them.
            // (2) WORLD CAPS. MaxMobs 64 -> 96 and MaxProjectiles 256 -> 384
            //     are CAPABLE of moving the hash: a run that reaches either
            //     cap keeps mobs/rounds the old cap silently dropped, and the
            //     drop counters themselves (MobSpawnsSkipped/
            //     ProjectileSpawnsSkipped) are hashed via WorldStats.
            //     Wave.MaxMobsPerWave 24 -> 36 is a DIFFERENT mechanism, not a
            //     drop counter — WaveSystem.CountForTest clamps the wave's own
            //     mob COUNT to it directly (`math.min(scaled, MaxMobsPerWave)`,
            //     baked straight into WaveState before any mob spawns), so a
            //     run whose scaled wave size would exceed the old 24 gets a
            //     structurally different, larger wave composition under the
            //     new 36 rather than any mobs being skipped/counted. Whether
            //     this 1000-tick solo run actually reaches any of these three
            //     caps is not claimed here — it very likely does not (waves of
            //     4/6/8 mobs, ~12 live rounds), and the honest statement is
            //     "capable", not "did" (Task 16 review, M-1). MaxEventsPerFrame
            //     256 -> 512 is NOT a channel at all: events have been outside
            //     the hash since stage 1 and Emit drops silently with no
            //     hashed counter — it was listed here in error.
            // (3) WAVE SCALE. WaveSystem.CountForTest replaces the inline
            //     count formula. At playerCount 1 the scale factor is exactly
            //     1, so this changes NOTHING for the solo golden by itself —
            //     it is listed for completeness, and it is a real cause for the
            //     multiplayer golden below.
            // (4) statsCount LEFT THE HASH (owner decision Р114): the canonical
            //     order is now ... -> worldStats -> stats[0..n), with the
            //     duplicated _matchStats.Length step removed. One fewer Add
            //     shifts every subsequent byte of the fold.
            // Nothing else moved: the mob-projectile fixtures already carried
            // ProjectileIds.NoOwner since Task 10 (carryover-t16 item 6 was
            // already discharged there), no state field entered or left
            // PlayerState/MobState/ProjectileState/MatchStats, and no system's
            // per-tick order changed.
            //
            // Re-pinned by Stage 3 Т6 (repin #15, the FIRST of exactly TWO
            // sanctioned golden shifts of the whole of stage 3 — spec С28/§4.
            // The second is Т12, "the arena and its inhabitants". There is no
            // third: any other movement of this constant is a stop-and-ask-
            // the-owner event, not a re-pin).
            //
            // THE CAUSE IS NOT BEHAVIOR, IT IS THE COMPOSITION OF STATE. The
            // extraction economy's fields were declared inert across Ф1
            // (errata E-1's "structural rebuild": declare every hashable
            // field in one phase so the digest moves ONCE instead of once per
            // task), and all of them entered the canonical hash order (spec
            // Р294) in this single task. Twelve positions, in that order:
            //  (1) lootRng — a THIRD RNG stream (Р230), folded from the same
            //      seed with its own constant, hashed right after waveRng;
            //      its consumer (container layout) arrives in Т15, but a
            //      stream outside the hash/save would diverge a replay at its
            //      first draw;
            //  (2) pickupCount + PickupState{Id, Pos, Kind, Amount, Ttl};
            //  (3) containerCount — a ZERO count holding the containers'
            //      position until Т14 declares the type. An FNV chain moves
            //      when a step is ADDED, even at a zero value, so claiming
            //      the slot now is exactly what keeps Т14 from needing a
            //      third sanction that does not exist;
            //  (4) MatchState{Phase, DirectorDeathTick};
            //  (5) PlayerState.Ammo;
            //  (6) PlayerState.Extracted and ExtractKind;
            //  (7) PlayerState.LootTimer, RepairTimer, ExtractTimer;
            //  (8) PlayerState.LootTargetContainerId and LootTargetSlot;
            //  (9) MatchStats.AmmoSpent and CellsPicked;
            // (10) WorldStats.PickupSpawnsSkipped and ContainerSpawnsSkipped;
            // (11) ProjectileState.OwnerEntityId (Stage 3 Task 5's field,
            //      declared inert there and admitted here);
            // (12) inventories[0..n) — each backpack's item count and the
            //      items it actually carries, LAST in the order, after the
            //      statistics (spec Р294/Р231).
            // Only (5) carries a LIVE value in this run: Scripted() holds the
            // trigger and the run spends ~250-277 rounds over 1000 ticks.
            // Every other position sits at its default here and the digest
            // still moves, because each one adds a step to the FNV-1a chain
            // whether or not the value behind it is zero.
            //
            // WHY THIS CONSTANT MOVES AT ALL, HAVING STOOD THROUGH Т1-Т5.
            // Stage 3 Task 5 (mob friendly fire, Р252) moved only the
            // multiplayer golden below, because a mob never hits another mob
            // in the SOLO scenario — proven, not assumed, by that task's own
            // mutation #3, which crashed on every mob-on-mob hit and left
            // this test green. A change of state COMPOSITION is the opposite
            // kind of cause: it is scenario-independent, so both constants
            // move here. "The golden did not move" is a fact about a
            // scenario, never about a change.
            //
            // ATTRIBUTION, so the shift is proven rather than asserted:
            // Stage 3 Task 5's mutation #2 turned friendly fire off outright
            // and BOTH goldens returned to their pre-Ф1 values BIT FOR BIT.
            // Nothing else in Ф1 had leaked into the digest — not
            // OwnerEntityId, not the signature changes that carried it — so
            // this re-pin owns the list above and nothing besides it.
            //
            // MOVED A SECOND TIME INSIDE THE SAME SANCTION — Ф1 FIX-ROUND, NOT
            // A THIRD RE-PIN (owner decision R-24). The two independent
            // reviewers of Ф1 named the same defect first: positions (9) and
            // (10) of the list above — MatchStats.AmmoSpent and CellsPicked —
            // had entered the digest with NO WRITER anywhere in Scripts/. That
            // is the very failure errata E-1 exists to prevent, merely
            // deferred: a hashed field whose behavior arrives later moves this
            // constant later, i.e. outside a sanction, since Т12 spends the
            // only remaining one on the arena. The fix-round gave both fields
            // their writers — AmmoSpent in WeaponSystem.Advance's own spend
            // branch (so the emergency synthesis, which spends nothing, tallies
            // nothing — spec Р226), CellsPicked in Loot.PickupSystem.Collect,
            // in cells — and this constant absorbed the result. The SANCTION IS
            // ONE LOGICAL EVENT, "the composition of state entered the hash at
            // the end of Ф1", not one commit: two commits touch the number,
            // one budget entry is spent, and Т12 is still the second and last.
            //
            // ATTRIBUTION OF THE SECOND MOVEMENT, by the same method as the
            // first: mutation M-A removed the AmmoSpent increment and NOTHING
            // else, and both goldens came back to their pre-fix-round values
            // BIT FOR BIT (this constant to 0x425A1D080761AECA, the
            // multiplayer one to 0x8F176E2D733A14EE — observed green against
            // those still-pinned constants, 1038 tests, one red, the AmmoSpent
            // test alone). So the whole of this second movement belongs to a
            // single line. The same run proved two negatives worth recording:
            // the CellsPicked writer is digest-inert in BOTH scenarios (owner
            // decision R-18 zeroes the drop in TestConfigs, so no pickup is
            // ever born and Collect never runs), and PickupSystem.AdvanceTtl's
            // switch to the in-place `ref` idiom is digest-inert too (the hash
            // walks `i < _pickupCount`, so the debris past the count that the
            // switch leaves behind is read by nobody).
            // T12 (Stage 3 re-pin #2, sanctioned): arena radius 113, three zones
            // with arcs and doors, spawn ring 0.92, world caps, zonal wave
            // budget, elite and director archetypes.
            //
            // RE-PIN #2 — THE SECOND AND LAST SANCTION OF STAGE 3 (spec
            // С28/§4), "the arena and its inhabitants". WHAT MOVED, by name:
            // Arena.Radius 65 -> 113; three zones {65, 92} carrying two arc
            // walls, six doors and their jambs; PlayerSpawnRingFrac 0.8 -> 0.92
            // (the multiplayer ring from 52 m out to 103.96); the caps MaxMobs
            // 96 -> 288, MaxProjectiles 384 -> 1024, MaxEventsPerFrame 512 ->
            // 1024; the zonal wave budget, live for the first time because
            // ZoneRadius stopped being empty (the budget of ONE wave split
            // across the rings by WaveConfig.ZoneWeights {0.45, 0.45, 0.10} —
            // ⚠ THAT FIELD NO LONGER EXISTS: `app-ggvz` deleted it together
            // with the split itself and gave every ring a wave of its own, see
            // RE-PIN #4 below. The sentence stays because it is true of Т12,
            // and the tombstone stays because a live-sounding reference to a
            // deleted field is how the next reader is misled);
            // and the Elite/Director sections of TestConfigs.Default() — with
            // the Elite now genuinely SPAWNING in both scenarios, since the
            // middle zone took 45% of every wave (again: that share is Т12's
            // history, not today's rule) at EliteShareMiddle 0.35.
            //
            // WHAT IS *NOT* IN THIS MOVEMENT, recorded because it was measured
            // and not assumed: the wave-index-dependent half of the wave
            // composition. Both scenarios live at WaveIndex = 1 for their whole
            // 1000 ticks — FirstWaveDelay is 75 ticks and a gunner needs ~405
            // more to reach its firing distance, so the first wave never closes
            // — and every elite-share term carries the factor (WaveIndex - 1),
            // identically zero there. Т11 established it by mutation rather
            // than by arithmetic alone: M4 and M12 each doubled the elite
            // growth rate and moved NEITHER golden.
            //
            // WHY Т11's OWN SHIFT IS NOT MIXED IN: it was STRUCTURAL, not
            // numeric — seven extra StateHash64.Add calls in HashWave for the
            // 3x3 debt matrix. The mirror evidence on this side is just as
            // direct: the thirteen fixture repairs this task made, the two
            // consts that became the ExitKind enum, and all eight mutations
            // M1-M8 are digest-inert — three consecutive full runs returned
            // this pair bit for bit.
            //
            // THE TWO PLANNED SANCTIONS ARE SPENT (С28). Re-pin #1 (Т6, "the
            // economy of the raid") and re-pin #2 (Т12, "the arena and its
            // inhabitants") are both used. The third constant — Т36's
            // extraction-loop golden over 18 000 ticks — is a NEW pin and
            // spends no sanction.
            //
            // RE-PIN #3 — SANCTIONED BY THE OWNER OUT OF BUDGET (Stage 3 Ф5-0,
            // decision R-173), "the solo lobby leaves the arena center". This
            // is the one movement С28 did not foresee, and it was granted
            // knowingly, with the alternative measured: Ф5 could not begin
            // otherwise. WHAT MOVED, and nothing else did:
            //
            //   1. THE SPAWN. Geometry.SpawnPosFor lost its solo special case,
            //      so this scenario's player no longer starts at (0,0) — the
            //      Director's own ground since Т12, and the trigger of his
            //      activation since Р299. Measured, not assumed: a probe over
            //      this very scenario found the player inside the CORE on
            //      1000 ticks out of 1000, while the multiplayer scenario
            //      never left the OUTER ring (closest approach 93.45 m against
            //      a 92 m boundary). Left alone, Т21's phase machine would
            //      have moved this digest on its first tick and Т22 would have
            //      spawned the Director into every solo fixture in the suite.
            //   2. THE SCENARIO'S OWN START, now stated rather than inherited
            //      (ScenarioStart above). The spawn alone would have left the
            //      run in an empty stretch of rim where it never ricochets a
            //      dash — GoldenScenario_ExercisesAllMechanics_Coverage caught
            //      exactly that, and a coverage assertion is not something to
            //      weaken to keep a digest quiet.
            //
            // ATTRIBUTION, so the movement is proven rather than asserted, and
            // by the same method the two earlier re-pins used. The two causes
            // were measured SEPARATELY, in two full runs: the spawn change
            // alone took this constant to 0xFFFEE5C6C159FA89, and anchoring the
            // scenario took it from there to the value below. THE CONTROL is
            // the multiplayer golden, which sat at 0x03FD1C06FC2921DD through
            // both runs and still does — nothing structural leaked into the
            // hash, or it would have moved that constant too. The 76 fixture
            // repairs of the same commit are digest-inert by construction:
            // every one of them touches TestConfigs.OpenField(), a fixture this
            // scenario does not use.
            //
            // Any further movement of any of the three constants is a stop and
            // a question for the owner.
            //
            // ------------------------------------------------------------------
            // RE-PIN #3 (Ф8, bd `app-3cph` + `app-d2ki`) — AND IT IS NOT A
            // SANCTION SPENT, IT IS A SANCTION GRANTED. The budget of two
            // (errata §4) was spent by Т6 and Т12; this movement is the
            // OWNER's, decided twice in writing after he played milestone В1
            // (bd notes on both issues, 2026-08-22 and 2026-08-23) and asked
            // for one re-pin covering both edits of the difficulty curve,
            // committed on its own (R-23). The stop-and-ask rule above did
            // exactly what it exists for: it stopped, and the answer came back
            // "do it, and do it before Т36 so the third golden is pinned on the
            // new numbers and never has to move at all".
            //
            // THREE CAUSES, and the arena is only two of them:
            //   1. THE ARENA'S TWO RINGS TRIPLE IN AREA around an unchanged
            //      core (`app-3cph`): rim 113 -> 173, middle/outer boundary
            //      92 -> 130, the twelve non-core circles and eight non-core
            //      stadiums riding outward with their own rings, the three
            //      portals re-radiused. Every one of those numbers is inside
            //      TestConfigs.DefaultArena(), which is the struct BOTH
            //      scenarios run off — see ArenaConfig's own fields for the
            //      derivation of each.
            //   2. THE MOB DENSITY DOUBLES (`app-3cph`): MaxMobs 288 -> 1350,
            //      so every wave's zonal budget lands differently and the
            //      WaveRng is consumed on a different search.
            //   3. THE MIDDLE RING'S ELITE IS LEASHED TO IT (`app-d2ki`,
            //      MobAiSystem.LeashRingFor) — a rule that acts during Farm,
            //      which is the only phase either scenario ever reaches.
            //
            // ATTRIBUTION, by the same separated-runs method the earlier
            // re-pins used, and this time the control cuts the other way:
            //   - `app-d2ki` ALONE, measured on the unchanged arena, moved the
            //     MULTIPLAYER constant to 6391024973742485840 and left THIS one
            //     untouched. The reason is geometric, not lucky: the leash only
            //     bites when an elite in the middle ring has someone to chase
            //     in the outer one, and only the three-player scenario puts a
            //     collector out there. Mutation M1 of that task's own batch
            //     (remove the rule, everything else kept) put the multiplayer
            //     constant back to 0x03FD1C06FC2921DD exactly — which proves
            //     the whole `bool` -> `float` rewrite around it is behaviorally
            //     inert, since nothing else in that commit could return the
            //     digest to its old value.
            //   - THE ARENA then moved BOTH, this one included.
            // The eight fixture repairs in the same commit are digest-inert by
            // construction: `HostileFrame`, the two GC byte caps, the two
            // Snapshot spacings, the tie-break placement, the gunner's approach
            // and the corpse zone points are all outside TestConfigs.Default()
            // and none of them is read by either scenario.
            //
            // ONE OF THE EIGHT IS NOT INERT AND IS NAMED HERE ON PURPOSE:
            // ScenarioStart's clearance, half a dash -> two hero radii. That
            // is a fourth cause of THIS constant (and of this one only — the
            // multiplayer generator does not call it), and it is not a
            // convenience: at the new arena size the old anchor left the run
            // ricochet-free, and GoldenScenario_ExercisesAllMechanics_Coverage
            // failed rather than the digest — the same coverage guard that
            // caught the same class of loss at Ф5-0. Its own doc carries the
            // arithmetic.
            //
            // The stop-and-ask rule stands, unchanged and unweakened: any
            // further movement of any of the three constants is a stop and a
            // question for the owner.
            //
            // ------------------------------------------------------------------
            // RE-PIN #4 (bd `app-ggvz`, "wave cadence per ring") — SANCTIONED IN
            // ADVANCE BY THE OWNER, decision К9 of the task's brainstorm, and
            // spent here in a commit of its own (R-23). The rule above did what
            // it exists for: the task stopped and asked BEFORE the work began,
            // and the answer was granted for this one movement of all three
            // constants. NOTHING IS LEFT: any further movement of any of them is
            // a stop and a question for the owner again.
            //
            // WHY A SHIFT WAS UNAVOIDABLE, AND WHY IT IS NOT A NUMBER. The
            // defect this task repairs was structural: the next wave could be
            // queued by exactly ONE path — a full wipe of the WHOLE arena
            // (`PendingTotal == 0 && w.MobCount == 0`) — and the pause only
            // started ticking there. A collector runs 7.5 m/s against the mobs'
            // 4-5.2, so he outran the first wave and a second never came; once
            // the Director woke, `MobCount == 0` stopped being reachable at all
            // (he and his retinue live in the core). The owner's own raid of
            // 252 s with three players closed with wavesCleared = 0. Replacing
            // that with an independent cycle per ring changes what the world IS
            // on every tick of every scenario, not how quickly it gets there.
            //
            // SIX CAUSES, written out one by one because spec §4 / risk Р-З
            // requires each named rather than summarized:
            //
            //  1. THE SHAPE OF `WaveState`, AND THREE OF THEM IN THE HASH. The
            //     nine-field debt matrix `Pending{Zone}{Archetype}` (Т11 of
            //     stage 3, introduced for the split this task removes) collapsed
            //     back into three fields, and the world now holds one
            //     `WaveState` per ring. `HashWave` therefore runs THREE times,
            //     in the canonical order Outer -> Middle -> Core, at the same
            //     position of the sequence where it ran once. An FNV chain moves
            //     when steps are ADDED even if every value behind them is equal.
            //  2. THE PHASE TIMER IS WHOLE TICKS. `float PhaseTimer` in seconds
            //     became `int PhaseTicks` (Р316). This is the project's own rule
            //     — whole ticks are the only unit in which a deterministic
            //     comparison may be made (`SimulationWorld.TicksFromSeconds`,
            //     R-178/R-190) — and it moves the rounding boundary of every
            //     wave start and every clear.
            //  3. THE DIFFICULTY STEP COMES FROM THE RAID CLOCK. `WaveIndex`
            //     stopped being a per-ring counter of waves started and now
            //     carries `WaveSystem.DifficultyStepFor(tick, in cfg)` (Р315),
            //     assigned rather than incremented. Wave size and elite share
            //     read that step, so the INPUTS of both formulas differ even
            //     where the count of waves would have agreed.
            //  4. THE INFLOW IS BOUNDED, AND IN TWO WAYS AT ONCE. A wave no
            //     longer lands inside one tick: a ring spawns at most
            //     `MaxSpawnsPerZonePerTick` (2) per tick (Р317) and stops
            //     entirely at `MaxAliveByZone`, keeping its debt (Р306).
            //     ⚠ The spec names this cause "the smoothing of the inflow"
            //     alone. The CEILING belongs to the same cause and is named here
            //     because it is the half that actually holds the fixtures down:
            //     measured in session 47, the extraction fixture ran 719 live
            //     mobs without it against 48 with it, and the suite left its own
            //     time gate. Omitting it would make this list look complete
            //     while the largest term of the shift went unnamed.
            //  5. `MobState.SpawnZone`. A new hashed field, folded immediately
            //     after `Type`, the field it qualifies: the ring the SPAWNER put
            //     the mob into, which is what "this ring is cleared" is counted
            //     by (К7). It cannot be derived — every mob walks away from
            //     where it was born, which is exactly why it has to be stored,
            //     and stored means hashed.
            //  6. `ZoneWeights` AND `WavePause` LEFT THE CONFIG. This cause does
            //     NOT move the three constants, and that is why it is the one
            //     easiest to forget: `SimConfigHash` is not part of
            //     `StateHash64` and `SimulationWorld` never computes it. It is
            //     named sixth rather than dropped because it belongs to the same
            //     sanctioned event and because it has a consequence nothing else
            //     has — `simConfigHash` CHANGED, so the clients already built at
            //     `builds-f8` cannot join the new server, and BOTH sides are
            //     rebuilt for milestone В4.
            //
            // ATTRIBUTION, so the movement is proven rather than asserted. The
            // earlier re-pins separated their causes into separate runs; here
            // the TASK ORDER did the separating, and the intermediate values
            // were observed and recorded as they appeared:
            //   - CAUSE 5 ALONE (Т1 — `SpawnZone` entered `HashMob`, nothing
            //     else of the task existed yet): this constant went to
            //     14591900056746272100, the multiplayer one to
            //     15401656763043580689, the extraction one to
            //     16270681601866834963. Т2 then added four config fields that no
            //     code read yet and moved nothing, which is the control for that
            //     step.
            //   - CAUSES 1-4 TOGETHER (Т3, then the merged Т4+Т5): the values
            //     pinned below and at the two other constants. They are NOT
            //     separable further, and the reason is recorded rather than
            //     hidden: Т3 was written to be behavior-neutral on purpose, and
            //     the cadence could not be committed without its bounds — the
            //     measurement in cause 4 is what forced Т4 and Т5 into one task
            //     (owner decision, session 47).
            //   - TWO MEASURED NEGATIVES, each from a full run, and they are
            //     what make the list above exhaustive rather than merely long.
            //     Т6 moved the SHIPPED numbers (`BaseCount` 4 -> 16 and
            //     `EliteShareOuterGrowth` 0.02 -> 0.007, in the `.asset` and in
            //     the C# defaults together) and all three constants came back
            //     BIT FOR BIT: the goldens read `TestConfigs`, never the assets,
            //     which is precisely what Р325 separated the two sources for.
            //     Т7 added the HUD's wave-announce flash and they came back bit
            //     for bit again: `RenderSnapshot` is not in `StateHash64`.
            //
            // WHAT IS NOT IN THIS MOVEMENT: `RenderSnapshot.Wave` became the
            // WORLD AGGREGATE of the three rings (max step, min timer among the
            // unfrozen, sums of the rest), and the wire block stayed at four
            // bytes with `ProtocolVersion` at 3. None of that is hashed, and the
            // Т7 negative above is the evidence, not the argument.
            const ulong GoldenHash = 0xDAA519A7FF4C889DUL; // = 15755027080758986909
            Assert.AreEqual(GoldenHash, RunScripted(123, Ticks));
        }

        [Test]
        public void MultiPlayerGoldenHash_ScriptedScenario()
        {
            // Stage 2 Task 10, FIRST pin of this constant (not a re-pin): the
            // solo golden above walks exactly one player and one MatchStats
            // slot, so it cannot see the array halves of the canonical hash
            // order (playerCount + players[0..n), statsCount + stats[0..n)) —
            // a HashPlayer/HashStats loop silently truncated back to index 0
            // would leave it green. This pins a three-player run: world seed 42,
            // scripted input from Random(123) drawn per player in increasing
            // index order, 1000 ticks.
            //
            // Same first-run procedure the solo golden documents: with the
            // constant at 0 this assert fails and NUnit prints the actual hash.
            // Pinned in Stage 2 Task 10 (first pin of this constant, NOT a
            // re-pin — it never held a nonzero value before): three players
            // spawned on the ring, each drawing its own scripted input, over the
            // full canonical hash order including both array halves.
            //
            // Re-pinned by Stage 2 Task 16 (first RE-pin of this constant —
            // Task 10 only pinned it): every cause listed on the solo golden
            // above applies here too, plus the one that is exclusive to this
            // test — WAVE SCALE. Wave.PerPlayerCountFrac (0 before this task,
            // 0.7 now) multiplies each wave by 1 + (playerCount - 1) * frac, so
            // a three-player run's waves go from BaseCount 4 to round(4 * 2.4) =
            // 10 mobs, and every LATER wave's own scaled size grows the same
            // way (same formula, later waves' own larger BaseCount+CountGrowth
            // input) — capped at MaxMobsPerWave (36 now, up from 24) same as
            // the solo golden's own WORLD CAPS note above, and whether this
            // 1000-tick run's later waves actually reach that cap is likewise
            // not claimed here, only that the scale factor is CAPABLE of
            // pushing them into it sooner than the old cap would have. Either
            // way the scale factor changes how much WaveRng the spawn search
            // consumes, how many mobs live, shoot and die, and therefore the
            // whole downstream trace. Spec §6e sanctions exactly one further
            // shift of THIS constant, in Task 17 (owner decision Р113).
            //
            // Re-pinned by Stage 2 Task 17 (second and LAST re-pin of this
            // constant — the sanctioned shift spec §6e reserved above, owner
            // decision Р113 variant (a); it is limited to THIS constant, the
            // solo golden's own "two re-pins, then stop" invariant is untouched
            // and it did NOT move here). Cause: Task 17 completes the damage
            // matrix, and all three of its halves are live in a three-player
            // run while none of them exists in a solo one.
            // (1) VICTIM BY INDEX. A chaser's contact strike now lands on the
            //     player its own FSM selected (Targeting.NearestAlivePlayer,
            //     since Task 8) instead of always on player 0 — so Hp,
            //     DamageTaken, death and every downstream consequence move to a
            //     different player than the pre-Task-17 run recorded. Solo has
            //     one player, so the chosen target is player 0 either way.
            // (2) MOB ROUNDS AGAINST EVERY LIVE PLAYER. ProjectileSystem's
            //     gather packed exactly one player candidate (player 0); it now
            //     packs one per live player, so a gunner's round finally stops
            //     on whoever is actually standing in it. Solo gathers the same
            //     single candidate as before, bit for bit.
            // (3) PLAYER ROUNDS AGAINST OTHER PLAYERS. Player-owned rounds gather
            //     live players other than their own owner — a whole class of
            //     hits, deaths and ShotsHit/Kills/HeadshotKills credit that did
            //     not exist in this run before. Solo has no other player, so the
            //     branch is dead there (the owner is the only player and is
            //     skipped), which is exactly why the solo golden stands.
            // Nothing else moved: the candidate scratch is excluded from
            // SaveState/StateHash, SimEvent.PlayerIndex on ProjectileHit/MobDied
            // is an EVENT field and events are outside the hash entirely (spec
            // §3.2, the SimEvent.PlayerIndex bullet, which is where that norm
            // is stated — §3.7 is the networking section and says nothing about
            // the hash at all), and no state field entered or left
            // PlayerState/MobState/ProjectileState/MatchStats.
            //
            // Re-pinned by Stage 3 Т6 (the THIRD re-pin of this constant, and
            // the FIRST of exactly TWO sanctioned golden shifts of stage 3 —
            // spec С28/§4; the second is Т12, and there is no third).
            //
            // THE PARAGRAPH ABOVE CALLED TASK 17 THE "SECOND AND LAST" RE-PIN,
            // AND THAT WAS TRUE AS SCOPED: it spent the last of STAGE 2's own
            // sanctions (spec §6e, owner decision Р113). Stage 3 opens a new,
            // separately budgeted pair of its own (spec С28) — this is the
            // first of those two, not an overrun of the exhausted stage-2
            // budget. Stated here because the two claims read as a
            // contradiction otherwise, and a reader who resolves that
            // contradiction by assuming the newer text wins would also assume
            // the budget is open-ended. It is not: after Т12 there are none
            // left, and a third movement is a stop-and-ask-the-owner event.
            //
            // Cause: the TWELVE canonical-order positions listed on the solo
            // golden above, which apply here in full — and apply three times
            // over for the per-player halves, since players[0..n),
            // stats[0..n) and inventories[0..n) each walk a three-player
            // roster instead of one. Unlike Task 16 (wave scale) and Task 17
            // (the damage matrix), this task has NO cause exclusive to the
            // multiplayer run: a change of state COMPOSITION cannot be
            // scenario-specific, which is exactly why the solo golden — which
            // stood through Stage 3 Task 5 — moves alongside this one.
            //
            // THIS CONSTANT WAS ALREADY RED BEFORE THIS TASK, at
            // 0x3158C0E72DE3AA4C: Stage 3 Task 5's own legitimate shift (mob
            // friendly fire, Р252), left deliberately unpinned because the
            // plan reserves every golden movement of Ф1 for this one task
            // (errata E-6 D-I20: the phase's stop-condition reads "a shift
            // outside Т5 and Т6"). That value is NOT what is pinned below —
            // it predates the twelve positions. Both causes are folded into
            // the single number here, and Task 5's mutation #2 (friendly fire
            // off -> both goldens return to their pre-Ф1 values bit for bit)
            // is what proves the pair is the whole of it.
            //
            // MOVED A SECOND TIME INSIDE THE SAME SANCTION — Ф1 fix-round,
            // owner decision R-24, exactly as the solo golden above describes
            // in full: positions (9) and (10) of its list, MatchStats.
            // AmmoSpent and CellsPicked, were hashed in Т6 without writers,
            // and the fix-round supplied both rather than leaving the movement
            // to a phase with no sanction left. Still the FIRST of stage 3's
            // two shifts; Т12 is the second and last.
            //
            // The multiplayer run has no cause of its own here either — the
            // writer is per-player, so all three MatchStats slots carry a live
            // AmmoSpent instead of one, but that is the same cause counted
            // three times, not a second one. Attribution: mutation M-A (the
            // AmmoSpent increment removed, nothing else) returned this
            // constant to 0x8F176E2D733A14EE bit for bit alongside the solo
            // one — see the solo golden's own ATTRIBUTION paragraph for the
            // two negatives that run also settled.
            // RE-PIN #2 (Т12, the second and last sanction — the solo golden
            // above carries the full account: what moved, what provably did
            // not, and why the budget is now spent). This scenario has no cause
            // of its own. It runs the same arena, the same zonal budget and the
            // same two new archetypes with three players instead of one, so
            // every term is the solo cause counted three times over.
            //
            // One thing is worth recording separately: the multiplayer wave is
            // the larger one (CountForTest gives 10 at index 1 against the
            // solo's 4), so it is the run where an index-dependent elite term
            // would have shown up first — round(14 * 0.04) = 1 against
            // round(14 * 0.02) = 0 at wave index 2. It does not, and Т11's M12
            // mutation covered this scenario as well and moved neither
            // constant: the composition really is inert at WaveIndex = 1.
            //
            // RE-PIN #3 (Ф8, bd `app-3cph` + `app-d2ki`) — the solo golden
            // above carries the full account: whose sanction this is, the
            // three causes, and the attribution runs. Two things belong here
            // rather than there, because they are true of THIS scenario only:
            //
            //   - IT IS THE ONE THE LEASH CAN REACH. `app-d2ki` holds a middle-
            //     ring elite out of the outer ring, which requires somebody to
            //     be chased into the outer ring — and only a three-player run
            //     puts a collector there. Measured, not argued: the rule alone,
            //     on the unchanged arena, moved this constant to
            //     6391024973742485840 and left the solo one exactly where it
            //     was.
            //   - IT DOES NOT USE ScenarioStart. The fourth cause listed on the
            //     solo constant (its anchor clearance) cannot touch this
            //     digest, and did not.
            //
            // RE-PIN #4 (bd `app-ggvz`, "wave cadence per ring") — the solo
            // golden above carries the full account: whose sanction this is
            // (owner decision К9, spent in a commit of its own), the six causes,
            // and the attribution runs including the value this constant held
            // after Т1 alone (15401656763043580689). This scenario has no cause
            // of its own: it runs the same three independent rings with three
            // collectors instead of one, so every term is the solo cause counted
            // three times over.
            //
            // ONE THING IS TRUE OF THIS SCENARIO ONLY, and it is the half of
            // cause 4 the spec did not name — THE PER-RING CEILING BITES HERE
            // FROM THE VERY FIRST WAVE, and in the solo run it does not. The
            // arithmetic is the fixture's own, recomputed rather than recalled:
            // `CountForTest` gives round((4 + 2*0) * (1 + 2*0.7)) = 10 per ring
            // for three players against round(4 * 1) = 4 for one, and
            // TestConfigs.Default() caps the core at MaxAliveByZone[Core] = 8.
            // So the first core wave of this run is held at the ceiling with a
            // debt left over, while the solo run's first core wave (4) fits
            // underneath it untouched. The rings' own ceilings, 24 and 16, are
            // reached later in both runs as the clock raises the step.
            const ulong MultiGoldenHash = 0x06FA4F44F3722466UL; // = 502801465965945958
            Assert.AreEqual(MultiGoldenHash, RunMultiScripted(123, Ticks, 3));
        }

        [Test]
        public void MultiPlayerScriptedRun_SameSeed_SameHash()
        {
            // Companion to ScriptedRun_SameSeed_SameHash for the multiplayer
            // generator: the pinned constant above is only meaningful if the run
            // it pins is reproducible in the first place, and if a different
            // input seed actually reaches a different world.
            Assert.AreEqual(RunMultiScripted(123, Ticks, 3), RunMultiScripted(123, Ticks, 3));
            Assert.AreNotEqual(RunMultiScripted(123, Ticks, 3), RunMultiScripted(43, Ticks, 3));
        }

        [Test]
        public void GoldenScenario_ExercisesAllMechanics_Coverage()
        {
            // I4 (final review wave, app-n6g): a companion to
            // GoldenHash_ScriptedScenario over the SAME Scripted() generator,
            // same fixed world seed (42) / input seed (123) / tick count as
            // the golden — so this can never flake, it just asserts the
            // scenario the golden pins actually DRIVES the mechanics it
            // claims to (slide, dash, ricochet, both fire modes), not merely
            // that its hash is stable.
            //
            // app-88jb Т34 STEP 2 (plan Т34, finding D-I12): SEVEN MORE
            // MECHANICS, AND THEY COME BEFORE THE RE-PIN ON PURPOSE. The epic
            // put the impact, the tilt, the knockdown, the round's ricochet,
            // the pierce, the hard body separation and the rewind into the
            // simulation, and each of them is a branch the digest would have
            // pinned WITHOUT any witness that the scenario reaches it — a
            // re-pin taken first would have frozen a number that guards
            // nothing new. Every assertion of this test, old and new, in the
            // order they stand below:
            //   1. ZONE — the run never enters the core (Р299);
            //   2. SlidesUsed > 0, DashesUsed > 0, at least one DashRicocheted,
            //      and a Head-zone hit or an aimed shot (I4's originals, kept
            //      word for word);
            //   3. IMPACT, on both kinds of body — as "the blow REACHED the
            //      impact seam", not as "the impulse landed": a PlayerDamaged
            //      with ImpactSpeed > 0 (a mob's round struck the collector;
            //      the field is the round's own speed, Т8) and a ProjectileHit
            //      on a mob (emitted BEFORE DamageMob, which is where the
            //      Impact.VelocityDelta shove goes into Vel, Т4). The shove
            //      itself has no event and no trace of its own here; a mutant
            //      that dropped `Vel += dir * dv` survives both asserts and is
            //      caught by the digest and by ImpactPhysicsTests — said out
            //      loud (review round, finding M-8) rather than implied;
            //   4. TILT — the peak |MobState.Tilt| over every mob and every tick
            //      is above zero (Impact.AngularImpulse into TiltVel, integrated
            //      by TiltSystem, Т5);
            //   5. KNOCKDOWN — pinned at ZERO entries into MobAiState.Downed,
            //      the honest depth; the assertion's own note says why;
            //   6. RICOCHET — at least one ProjectileRicocheted (Т19/Т30);
            //   7. PIERCE — pinned at ZERO pierced rounds behind a numeric
            //      premise that says why; the assertion's own note again;
            //   8. SEPARATION, two of the three pairs Т22 resolves — mob↔mob and
            //      collector↔mob — each as "a contact was reached" plus "no
            //      overlap survives to the end of a tick". The third pair,
            //      collector↔collector, is NOT here and cannot be: this is the
            //      solo scenario, and the multiplayer golden has no coverage
            //      companion of its own;
            //   9. REWIND, as three separate witnesses: the scenario fed a depth
            //      above zero at all (the premise, counted off the SimInput this
            //      test itself hands to Tick), a ProjectileFired with
            //      BirthSteps > 1 (the INPUT half — Т27's catch-up steps, Т32's
            //      count of them), and a tick with a live round at
            //      RewindLeft > 0 (the PICTURE half, Т28).
            //
            // MEASURED, NOT ESTIMATED (2026-09-03, session 86). The
            // coordinator's probe loaded THIS test assembly
            // (Ring.Simulation.Tests.dll) under `mono -O=-float32` — the
            // editor's own float mode — called Scripted/ScenarioStart/
            // RunScripted through reflection, reproduced all three pinned
            // digests bit for bit first, and only then counted over this very
            // scenario with both edits of Т34 in: the cap of 5 (app-gtj6) and
            // the ninth draw. What it found, in the order of the list above:
            // 7 PlayerDamaged with ImpactSpeed > 0 and 23 ProjectileHit on
            // mobs; 295 ticks with a tilted mob and a peak of 0.7111 rad; 0
            // entries into Downed; 202 ProjectileRicocheted; 0 pierced rounds;
            // 7 mob↔mob pair-ticks in exact contact and 1 collector↔mob contact tick
            // (gap 0.0010), with 0 overlaps deeper than a millimeter in either
            // pair; 825 inputs with RewindTicks > 0 (the depths 0..5 drawn
            // 175/161/152/174/174/164 times), 76 births with BirthSteps > 1
            // and 134 ticks with a round at RewindLeft > 0 (108 rounds); and
            // for the originals SlidesUsed 2, DashesUsed 11, 4 DashRicocheted,
            // an aimed shot, and a run that stays in the outer ring. Every
            // threshold below is "at least one" or an equality at the measured
            // zero — never a number tuned to the run.
            //
            // The event buffer is not auto-cleared per tick (SimulationWorld.
            // Tick never calls ClearEvents() itself — only ClearEvents()
            // callers decide when), and it stops recording once it hits
            // Arena.MaxEventsPerFrame (256 in TestConfigs.Default()) rather
            // than wrapping — over 1000 ticks that cap would silently drop
            // most of the run's events. So this counts DIRECTLY off each
            // tick's freshly-emitted slice and clears immediately after,
            // instead of reading the accumulated buffer once at the end.
            SimConfig cfg = TestConfigs.Default();
            var world = new SimulationWorld(42, cfg);
            // Same start RunScripted states (Ф5-0) — this test only means
            // anything if it runs the very scenario the golden pins.
            TestWorlds.RelocatePlayerForTest(world, 0, ScenarioStart(in cfg));
            var rng = new Random(123);
            bool aimHeld = false; // LOCAL, same no-static-leak reasoning as RunScripted's own

            int dashRicochetCount = 0;
            int headshotProjectileHits = 0;
            bool anyAimedProjectileFired = false; // VelZ != 0 -> the AimHeld branch actually spawned a shot
            // Ф5-0: the scenario's own proof that it never sets foot in the
            // core. ScenarioStart checks the START; this checks all 1000 ticks
            // of wandering that follow it, which is the half a start position
            // cannot promise. From Т21 on a live collector inside the core
            // activates the Director (Р299) — that would move the digest this
            // file pins, and both re-pin sanctions are spent (see the golden's
            // own account), so the day the run drifts in, THIS is the test
            // that says so.
            Zone deepestZone = Zone.Outer;

            // app-88jb Т34: the seven mechanics' counters. EVERYTHING THAT
            // ALLOCATES IS BUILT HERE, before the first tick — the three sets
            // and the radius scratch are the only heap objects, and the loop
            // reads, clears and swaps them. A HashSet.Add can grow a set, but
            // both adds sit behind the two zero-pinned conditions (a Downed
            // entry, a pierced round) and never fire on this run.
            int collectorImpacts = 0;      // PlayerDamaged with ImpactSpeed > 0
            int mobImpacts = 0;            // ProjectileHit on a mob
            float peakTilt = 0f;           // max |Tilt| over every mob and tick
            int downedEntries = 0;         // transitions INTO MobAiState.Downed
            int projectileRicochets = 0;   // ProjectileRicocheted
            int rewindInputs = 0;          // inputs this test fed with RewindTicks > 0
            int catchUpBirths = 0;         // ProjectileFired with BirthSteps > 1
            int pictureTicks = 0;          // ticks with a live round at RewindLeft > 0
            int exactMobContacts = 0;      // mob↔mob pairs at |gap| <= 1e-4 at a tick's end
            float deepestMobOverlap = 0f;  // most negative mob↔mob gap seen (0 = none)
            int collectorContactTicks = 0; // ticks with a collector↔mob gap <= Skin + 1e-4
            float deepestCollectorOverlap = 0f;
            // Which mob ids are Downed as of the previous tick (`downedBefore`)
            // and as of this one (`downedNow`); the two swap roles every tick,
            // so an ENTRY is "Downed now, not Downed before" and a mob that
            // stays down for its DownedSeconds is counted once, not per tick.
            var downedBefore = new System.Collections.Generic.HashSet<int>();
            var downedNow = new System.Collections.Generic.HashSet<int>();
            // Round ids ever seen carrying a Damage below their own base — the
            // ONLY trace a pierce leaves: ProjectileFlight.TryPierce is the one
            // writer of Damage after birth, it cuts the field and emits nothing
            // (bd app-tbvg). A set, so a round that flies on for many ticks
            // after its pierce is one pierce, not many.
            var piercedRoundIds = new System.Collections.Generic.HashSet<int>();
            // Body radius per live mob slot, refilled every tick from the
            // world's own archetype lookup — the same MobConfigRefFor the
            // separation pass reads, so the gaps below are measured with the
            // radii the world actually resolved, not a restated table.
            var mobRadius = new float[world.Mobs.Length];

            for (int i = 0; i < Ticks; i++)
            {
                SimInput input = Scripted(ref rng, ref aimHeld, cfg.Hero.MaxAimHeight,
                    cfg.Arena.RewindCapTicks);
                if (input.RewindTicks > 0) rewindInputs++;
                world.Tick(input);
                Zone here = Geometry.ZoneOf(world.Player.Pos, in cfg.Arena);
                if (here > deepestZone) deepestZone = here;

                for (int e = 0; e < world.EventCount; e++)
                {
                    SimEvent ev = world.GetEvent(e);
                    if (ev.Kind == SimEventKind.DashRicocheted) dashRicochetCount++;
                    if (ev.Kind == SimEventKind.ProjectileHit && ev.Zone == HitZone.Head)
                        headshotProjectileHits++;
                    if (ev.Kind == SimEventKind.ProjectileHit) mobImpacts++;
                    if (ev.Kind == SimEventKind.PlayerDamaged && ev.ImpactSpeed > 0f) collectorImpacts++;
                    if (ev.Kind == SimEventKind.ProjectileRicocheted) projectileRicochets++;
                    if (ev.Kind == SimEventKind.ProjectileFired && ev.BirthSteps > 1) catchUpBirths++;
                }
                world.ClearEvents();

                bool anyPictureRound = false;
                for (int p = 0; p < world.ProjectileCount; p++)
                {
                    ProjectileState round = world.Projectiles[p];
                    if (round.VelZ != 0f) anyAimedProjectileFired = true;
                    if (round.RewindLeft > 0) anyPictureRound = true;
                    // A mob's round is born with its own archetype's
                    // ProjectileDamage (MobAiSystem); the gunner's stands for
                    // all three shooting archetypes here because the fixture
                    // gives them one and the same number.
                    float baseDamage = round.Owner == ProjectileOwner.Player
                        ? cfg.Weapon.Damage : cfg.Gunner.ProjectileDamage;
                    if (round.Damage < baseDamage) piercedRoundIds.Add(round.Id);
                }
                if (anyPictureRound) pictureTicks++;

                // Bodies at the END of the tick — tilt, knockdown entries, and
                // the gaps the separation pass is answerable for. The
                // collector↔mob gap is measured ONLY WHILE HE IS ALIVE: a corpse
                // is not a body (SeparationSystem.SnapshotBodies gives it no
                // radius), so a mob walking over one would read here as an
                // "overlap" the mechanic never promised to prevent. Measured on
                // this run: he dies on the 887th tick (loop index 886), the one
                // contact tick is 520,
                // and the gate changes neither number — 1 contact tick and no
                // overlap, gated or not.
                downedNow.Clear();
                int mobCount = world.MobCount;
                PlayerState collector = world.Player;
                float minCollectorGap = float.MaxValue;
                for (int m = 0; m < mobCount; m++)
                {
                    MobState mob = world.Mobs[m];
                    mobRadius[m] = world.MobConfigRefFor(mob.Type).Radius;
                    peakTilt = math.max(peakTilt, math.abs(mob.Tilt));
                    if (mob.Ai == MobAiState.Downed)
                    {
                        if (!downedBefore.Contains(mob.Id)) downedEntries++;
                        downedNow.Add(mob.Id);
                    }
                    minCollectorGap = math.min(minCollectorGap,
                        math.distance(collector.Pos, mob.Pos) - (cfg.Hero.Radius + mobRadius[m]));
                }
                var swap = downedBefore; downedBefore = downedNow; downedNow = swap;
                if (mobCount > 0 && collector.Alive)
                {
                    if (minCollectorGap <= Geometry.Skin + 1e-4f) collectorContactTicks++;
                    deepestCollectorOverlap = math.min(deepestCollectorOverlap, minCollectorGap);
                }
                for (int a = 0; a < mobCount; a++)
                {
                    for (int b = a + 1; b < mobCount; b++)
                    {
                        float gap = math.distance(world.Mobs[a].Pos, world.Mobs[b].Pos)
                            - (mobRadius[a] + mobRadius[b]);
                        if (math.abs(gap) <= 1e-4f) exactMobContacts++;
                        deepestMobOverlap = math.min(deepestMobOverlap, gap);
                    }
                }
            }

            Assert.AreNotEqual(Zone.Core, deepestZone,
                "the scripted scenario must never enter the core — the Director's own ground (Р299)");
            Assert.Greater(world.Stats.SlidesUsed, 0,
                "the golden scenario must actually exercise sliding, not leave it dormant");
            Assert.Greater(world.Stats.DashesUsed, 0,
                "the golden scenario must actually exercise dashing, not leave it dormant");
            Assert.GreaterOrEqual(dashRicochetCount, 1,
                "the golden scenario must ricochet a dash off an obstacle at least once");
            Assert.IsTrue(headshotProjectileHits >= 1 || anyAimedProjectileFired,
                "the golden scenario must either land a Head-zone hit, or at minimum fire at " +
                "least one aimed (VelZ != 0) shot, proving the AimHeld branch actually fired");

            // IMPACT (item 3). ImpactSpeed is the field a receiver sizes the
            // shove by (Т8), and it is zero for a chaser's contact strike by
            // decision — so "above zero" is precisely "a round hit him". Both
            // asserts witness that the blow REACHED the seam where the impulse
            // is applied; the impulse itself (DamageMob / DamagePlayer adding
            // Impact.VelocityDelta into Vel) leaves no event and is guarded by
            // the digest and by ImpactPhysicsTests, not by these two lines.
            Assert.GreaterOrEqual(collectorImpacts, 1,
                "импакт по сборщику не исполнился: ни одного PlayerDamaged с ImpactSpeed > 0 — " +
                "раунд моба ни разу не дошёл до шва импакта по сборщику");
            Assert.GreaterOrEqual(mobImpacts, 1,
                "импакт по мобу не исполнился: ни одного ProjectileHit — раунд ни разу не дошёл до " +
                "шва импакта по мобу (само событие идёт до DamageMob; толчок в Vel сторожат " +
                "дайджест и ImpactPhysicsTests)");

            // TILT (item 4). Any non-zero Tilt on any mob is the spring having
            // been struck at all; the measured peak is 0.7111 rad.
            Assert.Greater(peakTilt, 0f,
                "крен не исполнился: ни один моб ни на одном тике не был наклонён — " +
                "Impact.AngularImpulse не дошёл до TiltVel или TiltSystem его не проинтегрировал");

            // KNOCKDOWN (item 5) — PINNED AT THE HONEST ZERO, form (a) of brief
            // §2.3, on the precedent of ExtractionScenario_ReachesTheWholeLoop:
            // the depth ACTUALLY reached is pinned by equality, so that the day
            // it becomes reachable the test says so out loud and asks to be
            // updated, instead of quietly passing under a doc that claims
            // more than the run delivers. The entry is TiltSystem's
            // `|Tilt| > TiltFallAngle`, strictly, and the measured peak of this
            // run is 0.71 rad against the fixture's 0.9 on the chaser and the
            // gunner alike: the blows that tilt a mob here land further apart
            // than the spring's TiltSettleSeconds (0.9 s), so one shot's tilt
            // has rung down before the next arrives and never accumulates
            // toward the threshold. The witnesses of a knockdown that DOES
            // happen are ImpactPhysicsTests' TiltAboveTheThreshold_PutsTheMobDown_
            // AndItGetsUpOnItsOwn and TiltExactlyAtTheThreshold_DoesNotKnockDown
            // (spec tests 8/10), with a fixture built to cross the line. THIS
            // DIGEST DOES NOT GUARD THE KNOCKDOWN — said here, not implied.
            // The premise is ASSERTED IN NUMBERS, like the pierce's below
            // (review round, finding I-1): the peak tilt of the run sits under
            // the lowest fall angle of the four archetypes, so the zero pinned
            // next is the consequence of a measured margin, not of a wish.
            float lowestFallAngle = math.min(
                math.min(cfg.Chaser.TiltFallAngle, cfg.Gunner.TiltFallAngle),
                math.min(cfg.Elite.TiltFallAngle, cfg.Director.TiltFallAngle));
            Assert.Less(peakTilt, lowestFallAngle,
                "премисса: пик крена сценария обязан лежать ниже самого низкого TiltFallAngle — " +
                "если он его перешагнул, пин нуля ниже обязан обновиться, а не молча зеленеть");
            Assert.AreEqual(0, downedEntries,
                "опрокидывание в этом сценарии недостижимо (пик крена ниже TiltFallAngle), и дайджест " +
                "его НЕ охраняет — свидетели живут в ImpactPhysicsTests; стало достижимо — обнови пин");

            // RICOCHET (item 6). Т19's reflection, reported by Т30's kind.
            Assert.GreaterOrEqual(projectileRicochets, 1,
                "рикошет раунда не исполнился: ни одного ProjectileRicocheted за весь сценарий");

            // PIERCE (item 7) — PINNED AT THE HONEST ZERO, form (a) of brief
            // §2.3, same precedent, behind a premise that says WHY in numbers.
            // The rule is Impact.Pierces: projectileMass / targetMass above
            // PierceMassRatio AND strict overkill. At the fixture's numbers the
            // mass clause fails against every body a collector's round can
            // meet — the lightest mob is the GUNNER at 70, not the chaser at 90
            // (checked against all four archetypes, which is why the premise
            // takes the minimum of the four rather than naming one), and
            // 2.6 / 70 = 0.037 sits below the 0.06 ratio; the mob's own round
            // (3.0 against the 120 collector or a 70 gunner) fares no better.
            // ProjectileFlightTests.ShippedNumbers_PierceNobody pins that
            // arithmetic for both shooters and all five masses, and the
            // witnesses of a pierce that DOES happen live in that file's other
            // pierce tests. THIS DIGEST DOES NOT GUARD THE PIERCE — said, not
            // implied.
            float lightestMobMass = math.min(math.min(cfg.Chaser.Mass, cfg.Gunner.Mass),
                math.min(cfg.Elite.Mass, cfg.Director.Mass));
            Assert.Less(cfg.Weapon.ProjectileMass / lightestMobMass, cfg.Weapon.PierceMassRatio,
                "премисса: при фикстурных числах выстрел сборщика не пробивает даже самое лёгкое тело — " +
                "если это перестало быть так, пин ниже обязан обновиться, а не молча зеленеть");
            Assert.AreEqual(0, piercedRoundIds.Count,
                "пробитие в этом сценарии недостижимо при фикстурных числах, и дайджест его НЕ охраняет — " +
                "свидетели пробития живут в ProjectileFlightTests; стало достижимо — обнови пин");

            // SEPARATION (item 8). An EXACT contact at a tick's end is the
            // fingerprint of Geometry.ResolveBodyPair — it kills the overlap to
            // zero and adds no skin to a pair (ruling 107); SoftSeparateMobs, a
            // force, never lands two bodies exactly on their radii. And no pair
            // may end a tick more than a millimeter inside each other: that is
            // what the hard pass guarantees, and the mutant that drops the
            // collector's pass walks him through bodies and overlaps them.
            Assert.GreaterOrEqual(exactMobContacts, 1,
                "разведение моб↔моб не исполнилось: ни одна пара мобов ни на одном тике не кончила тик " +
                "в точном контакте — отпечатка Geometry.ResolveBodyPair нет");
            Assert.GreaterOrEqual(deepestMobOverlap, -1e-3f,
                "разведение моб↔моб нарушено: пара мобов кончила тик с перекрытием глубже миллиметра");
            Assert.GreaterOrEqual(collectorContactTicks, 1,
                "премисса разведения сборщик↔моб пуста: сборщик ни на одном тике не был в контакте с мобом");
            Assert.GreaterOrEqual(deepestCollectorOverlap, -1e-3f,
                "разведение сборщик↔моб нарушено: сборщик кончил тик внутри тела моба глубже миллиметра — " +
                "жёсткое разведение не сработало");

            // REWIND (item 9), three witnesses because the depth has two
            // halves and a premise: without inputs above zero the other two
            // are vacuous; BirthSteps > 1 is the input half actually having
            // stepped a fresh round (needs k above RewindPictureTicks); a live
            // RewindLeft is the picture half actually asking PositionHistory.
            Assert.GreaterOrEqual(rewindInputs, 1,
                "премисса отмотки пуста: сценарий ни разу не подал ввод с RewindTicks > 0");
            Assert.GreaterOrEqual(catchUpBirths, 1,
                "половина ввода отмотки не исполнилась: ни одного ProjectileFired с BirthSteps > 1 — " +
                "догоняющие шаги Т27 ни разу не сделаны");
            Assert.GreaterOrEqual(pictureTicks, 1,
                "половина картинки отмотки не исполнилась: ни на одном тике не было живого раунда " +
                "с RewindLeft > 0 (Т28)");
        }

        /// Ф5-0: the two premises ScenarioStart leans on, checked rather than
        /// asserted in prose. Both are about the ARENA, so a retune of the
        /// circle layout or of the zone radii fails HERE, with a message
        /// naming what broke, instead of silently moving a golden digest.
        [Test]
        public void ScenarioStart_IsClearOfTheCoreAndInsideTheArena()
        {
            SimConfig cfg = TestConfigs.Default();
            float2 start = ScenarioStart(in cfg);
            float dist = math.length(start);

            Assert.AreEqual(Zone.Outer, Geometry.ZoneOf(start, in cfg.Arena),
                "the scripted scenario must start in the OUTER ring — a live collector in the core " +
                "activates the Director (Р299), and this digest must not depend on that");
            Assert.Less(dist + cfg.Hero.Radius, cfg.Arena.Radius,
                "the start must be inside the arena rim, body included");
        }

        [Test]
        public void SpreadDrawDoesNotShiftWaves()
        {
            // Same seed; world A fires for 100 ticks, world B stays idle.
            // Split streams: composition/positions of the FIRST wave must match at spawn tick.
            var cfg = TestConfigs.Default();
            cfg.Weapon.ProjectileLifetime = 0.2f; // ~7 m, never reaches the spawn ring (QA9)
            var a = new SimulationWorld(7, cfg);
            var b = new SimulationWorld(7, cfg);
            var fire = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
            var idle = new SimInput();
            int spawnTick = -1;
            for (int i = 0; i < 100; i++)
            {
                a.Tick(fire); b.Tick(idle);
                if (spawnTick < 0 && b.MobCount > 0) { spawnTick = i; break; } // QD4: compare AT spawn
            }
            Assert.GreaterOrEqual(spawnTick, 0, "wave never spawned");
            Assert.AreEqual(b.MobCount, a.MobCount);
            for (int m = 0; m < a.MobCount; m++)
            {
                Assert.AreEqual(b.Mobs[m].Type, a.Mobs[m].Type);
                Assert.AreEqual(b.Mobs[m].Pos.x, a.Mobs[m].Pos.x, 1e-4f);
                Assert.AreEqual(b.Mobs[m].Pos.y, a.Mobs[m].Pos.y, 1e-4f);
            }
        }
    }
}
