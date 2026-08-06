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
        /// dashing, sliding and both fire modes together instead of just
        /// idle-input replay. `aimHeld` is threaded in by ref from RunScripted
        /// (a LOCAL there, not a static field here — statics would leak across
        /// RunScripted's three per-test-session calls and make the golden hash
        /// order-dependent, Task 16 QA5/QB5/QD5) so the aim level persists
        /// across ticks within one scripted run. `maxAimHeight` comes in the same
        /// way, from the caller's own config (Г4 review): the AimHeight draw's
        /// upper bound has to BE Hero.MaxAimHeight — that is what Sanitize clamps
        /// to, and what keeps the tower head belts inside the scripted run's reach
        /// — so a literal here would silently drift the day that number moves.
        /// Draw order is FIXED (reorder only with a golden repin): MoveDir
        /// direction, MoveDir magnitude, AimPoint, FireHeld, DashRequested,
        /// SlideRequested, aimHeld toggle roll, AimHeight.
        static SimInput Scripted(ref Unity.Mathematics.Random rng, ref bool aimHeld,
            float maxAimHeight)
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

            return new SimInput
            {
                MoveDir = moveDir,
                AimPoint = aimPoint,
                FireHeld = fireHeld,
                DashRequested = dashRequested,
                SlideRequested = slideRequested,
                AimHeld = aimHeld,
                AimHeight = aimHeight
            };
        }

        /// Fixed world seed (42, same as the other tests in this file) driven by
        /// scripted input from an independently-seeded rng — isolates
        /// input-driven determinism from world-seed-driven determinism.
        static ulong RunScripted(uint inputSeed, int ticks)
        {
            SimConfig cfg = TestConfigs.Default();
            var world = new SimulationWorld(42, cfg);
            var rng = new Random(inputSeed);
            bool aimHeld = false; // LOCAL — RunScripted runs 3x/session, no static leak (QA5/QB5/QD5)
            for (int i = 0; i < ticks; i++)
                world.Tick(Scripted(ref rng, ref aimHeld, cfg.Hero.MaxAimHeight));
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
                    inputs[p] = Scripted(ref rng, ref aimHeld[p], cfg.Hero.MaxAimHeight);
                world.TickAll(inputs);
            }
            return world.StateHash();
        }

        [Test]
        public void SameSeed_SameHash_After1000Ticks()
        {
            Assert.AreEqual(HashAfterTicks(42, Ticks), HashAfterTicks(42, Ticks));
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
            // Pin against a silent simulation-behaviour change (spec §3.13 item 14):
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
            // instead of stopping dead — a further, real behaviour change.
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
            const ulong GoldenHash = 0x5BD8AC0DE1D0C454UL; // = 6618228828044051540
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
            // §3.7), and no state field entered or left
            // PlayerState/MobState/ProjectileState/MatchStats.
            const ulong MultiGoldenHash = 0x136FA6114112E44FUL; // = 1400520602171925583
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
            var rng = new Random(123);
            bool aimHeld = false; // LOCAL, same no-static-leak reasoning as RunScripted's own

            int dashRicochetCount = 0;
            int headshotProjectileHits = 0;
            bool anyAimedProjectileFired = false; // VelZ != 0 -> the AimHeld branch actually spawned a shot

            for (int i = 0; i < Ticks; i++)
            {
                world.Tick(Scripted(ref rng, ref aimHeld, cfg.Hero.MaxAimHeight));

                for (int e = 0; e < world.EventCount; e++)
                {
                    SimEvent ev = world.GetEvent(e);
                    if (ev.Kind == SimEventKind.DashRicocheted) dashRicochetCount++;
                    if (ev.Kind == SimEventKind.ProjectileHit && ev.Zone == HitZone.Head)
                        headshotProjectileHits++;
                }
                world.ClearEvents();

                for (int p = 0; p < world.ProjectileCount; p++)
                {
                    if (world.Projectiles[p].VelZ != 0f) { anyAimedProjectileFired = true; break; }
                }
            }

            Assert.Greater(world.Stats.SlidesUsed, 0,
                "the golden scenario must actually exercise sliding, not leave it dormant");
            Assert.Greater(world.Stats.DashesUsed, 0,
                "the golden scenario must actually exercise dashing, not leave it dormant");
            Assert.GreaterOrEqual(dashRicochetCount, 1,
                "the golden scenario must ricochet a dash off an obstacle at least once");
            Assert.IsTrue(headshotProjectileHits >= 1 || anyAimedProjectileFired,
                "the golden scenario must either land a Head-zone hit, or at minimum fire at " +
                "least one aimed (VelZ != 0) shot, proving the AimHeld branch actually fired");
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
