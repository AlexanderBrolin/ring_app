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
        /// across ticks within one scripted run.
        /// Draw order is FIXED (reorder only with a golden repin): MoveDir
        /// direction, MoveDir magnitude, AimPoint, FireHeld, DashRequested,
        /// SlideRequested, aimHeld toggle roll, AimHeight.
        static SimInput Scripted(ref Unity.Mathematics.Random rng, ref bool aimHeld)
        {
            var moveDir = rng.NextFloat2Direction() * rng.NextFloat();
            var aimPoint = rng.NextFloat2(new float2(-30f, -30f), new float2(30f, 30f));
            bool fireHeld = rng.NextFloat() < 0.7f;
            bool dashRequested = rng.NextFloat() < 0.05f;
            bool slideRequested = rng.NextFloat() < 0.05f;
            if (rng.NextFloat() < 0.03f) aimHeld = !aimHeld; // ~3%/tick toggle chance
            float aimHeight = rng.NextFloat(0f, 3.8f); // tower head belts [2.70, 3.50] reachable

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
            var world = new SimulationWorld(42, TestConfigs.Default());
            var rng = new Random(inputSeed);
            bool aimHeld = false; // LOCAL — RunScripted runs 3x/session, no static leak (QA5/QB5/QD5)
            for (int i = 0; i < ticks; i++)
                world.Tick(Scripted(ref rng, ref aimHeld));
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
            const ulong GoldenHash = 0xF66DA6A65EBBD03AUL; // = 17757032139275882554
            Assert.AreEqual(GoldenHash, RunScripted(123, Ticks));
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
