using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WeaponTests
    {
        /// Aimed counterpart of `TestWorlds.HipFire` (Task 15): same +X aim
        /// line, held aim, and an aim height equal to the standing muzzle
        /// height so the shot stays flat — the vertical axis is
        /// ProjectileHeightTests' subject, these fixtures measure the
        /// horizontal cone. app-8dv T1: the hip fixture this one is the
        /// counterpart OF moved to TestWorlds, because a second class needed
        /// it (rule 2); this aimed one has no second consumer and stays here.
        static SimInput AimedFire(in SimConfig cfg) => new SimInput
            { AimPoint = new float2(10f, 0f), AimHeight = cfg.Hero.MuzzleHeight,
              AimHeld = true, FireHeld = true };

        /// Number of shots a cone measurement samples.
        const int SpreadSamples = 64;

        /// Holds aimed fire until `SpreadSamples` rounds have left the barrel and
        /// returns the horizontal angle of each. The aim line is +X from a player
        /// who never moves, so ProjectileFired's Amount (atan2 of the shot's
        /// sim-plane velocity) IS the deviation from that line — the spray
        /// PATTERN, isolated. The settle/recoil pair under test is re-pinned
        /// through the QA1 seam before every tick, so all samples describe the
        /// same weapon state while the shots themselves keep walking the pattern.
        /// app-8dv: that last clause used to read "keep walking the one spread
        /// RNG stream", and the reason ONE world is held across all samples
        /// survived the change of mechanism intact — only its argument moved.
        /// It used to be that fresh `Random(seed)` states differing in their low
        /// bits produce correlated FIRST draws, which would make a cross-seed
        /// sample far narrower than the cone it measures. It is now that
        /// `BurstShots` is the pattern's own input: a fresh world per sample
        /// would ask for shot number one sixty-four times and measure a single
        /// point instead of a cone.
        static float[] AimedShotAngles(SimConfig cfg, float aimSettleTimer, float recoilOffset)
        {
            var w = new SimulationWorld(1, cfg);
            SimInput fire = AimedFire(in cfg);
            var angles = new float[SpreadSamples];
            int got = 0;
            for (int tick = 0; got < SpreadSamples && tick < SpreadSamples * 8; tick++)
            {
                var p = w.Player;
                p.AimSettleTimer = aimSettleTimer;
                p.RecoilOffset = recoilOffset;
                w.SetPlayerForTest(p);
                w.ClearEvents();
                w.Tick(fire);
                for (int e = 0; e < w.EventCount && got < SpreadSamples; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileFired)
                        angles[got++] = w.GetEvent(e).Amount;
            }
            Assert.AreEqual(SpreadSamples, got, "fixture: the tick budget must cover every sample");
            return angles;
        }

        /// The two halves of "there is a cone of exactly this width": the draws
        /// genuinely disperse (it is not a laser), and none of them leaves the
        /// cone the formula under test predicts (nothing wider leaked in).
        static void AssertCone(float[] angles, float cone, string what)
        {
            float widest = 0f, min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < angles.Length; i++)
            {
                widest = math.max(widest, math.abs(angles[i]));
                min = math.min(min, angles[i]);
                max = math.max(max, angles[i]);
            }
            Assert.Greater(max - min, 0f, what + ": the draws must actually disperse");
            Assert.LessOrEqual(widest, cone + 1e-5f, what + ": a draw left the predicted cone");
        }

        [Test]
        public void HoldFire_AverageRpmMatchesInterval()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            for (int i = 0; i < 300; i++) w.Tick(TestWorlds.HipFire()); // 10 s
            // 10 s / 0.12 s = 83.3 -> 83+-1 (fractional remainder carry, not 80 or 90)
            Assert.That(w.Stats.ShotsFired, Is.InRange(82, 84));
        }

        [Test]
        public void Recoil_AccumulatesWhileFiring_DecaysToZeroAfter()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            float peak = 0f;
            for (int i = 0; i < 60; i++)
            {
                w.Tick(TestWorlds.HipFire());
                peak = math.max(peak, w.Player.RecoilOffset);
            }
            // recoil genuinely accumulates (recovery < accumulation rate), not a phase lottery
            Assert.Greater(peak, cfg.Weapon.RecoilPerShotRad * 2f);
            for (int i = 0; i < 120; i++) w.Tick(default);
            Assert.AreEqual(0f, w.Player.RecoilOffset, 1e-4f);
        }

        [Test]
        public void NoFireWhileDashing_WhenConfigForbids()
        {
            var w = new SimulationWorld(1, TestConfigs.Open()); // CanFireWhileDash=false
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), FireHeld = true,
                                  DashRequested = true, AimPoint = new float2(10f, 0f) });
            Assert.AreEqual(0, w.Stats.ShotsFired);
        }

        [Test]
        public void NoFireWhileWindowOpen()
        {
            // Stage 3 Task 20 (spec §3.8 check 2's mirror on the weapon side,
            // Р239): CanFire's fifth term. No config toggle exists for this
            // one — unlike CanFireWhileDash/CanFireWhileSlide above, the loot
            // window's price is unconditional (spec: "стрельба ... недоступны",
            // no exception offered).
            var w = new SimulationWorld(1, TestConfigs.Open());
            SimInput input = TestWorlds.HipFire(); input.InventoryOpen = true;
            w.Tick(input);
            Assert.AreEqual(0, w.Stats.ShotsFired);
        }

        [Test]
        public void ProjectileCap_SkipsDeterministically()
        {
            var cfg = TestConfigs.Open();
            cfg.Weapon.ProjectileLifetime = 60f; // projectiles never expire
            cfg.Weapon.FireInterval = 0.001f;    // flood the cap instantly
            // Stage 3 Task 12: the flood has to outlast the cap, and since
            // Т2 it is the MAGAZINE that decides how long it lasts. At
            // FireInterval 0.001 against TickDt the while loop fires 33 rounds
            // a tick, so the fixture's 400 rounds are gone by tick 12 and the
            // emergency interval (1.25 s) adds barely two more over the
            // remaining 48 — about 402 rounds against a cap that went 384 ->
            // 1024 (spec Р216). The cap was simply never reached and
            // ProjectileSpawnsSkipped stayed 0. Tying the magazine to the cap
            // makes the flood outlast it by construction, whatever either
            // number becomes later: 1124 rounds at 33 a tick fill 1024 slots
            // by tick 31, inside this run's own 60.
            cfg.Weapon.AmmoStart = cfg.Arena.MaxProjectiles + 100;
            cfg.Weapon.AmmoMax = cfg.Weapon.AmmoStart;
            // bd app-3cph: the RUN LENGTH has to be derived too, for the very
            // reason Т12 derived the magazine one line above. At 33 rounds a
            // tick the fixture's fixed 60 ticks filled 384 and then 1024
            // slots, but not the 4096 the doubled mob density brought with it
            // (ArenaConfig.MaxProjectiles' own doc) — the cap was simply never
            // reached again and ProjectileSpawnsSkipped went back to 0.
            // Ticks = cap/33 rounded up, doubled for slack, so the flood
            // outlasts the cap whatever either number becomes later.
            int ticks = 2 * (cfg.Arena.MaxProjectiles / 33 + 1);
            ulong Run(SimConfig c2)
            {
                var w2 = new SimulationWorld(1, c2);
                for (int i = 0; i < ticks; i++) w2.Tick(TestWorlds.HipFire());
                Assert.Greater(w2.WorldStats.ProjectileSpawnsSkipped, 0);
                return w2.StateHash();
            }
            Assert.AreEqual(Run(cfg), Run(cfg)); // cap degradation is deterministic
        }

        [Test]
        public void FiredEvent_EmittedPerShot()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(TestWorlds.HipFire()); // first shot is instant
            int fired = 0;
            for (int i = 0; i < w.EventCount; i++)
                if (w.GetEvent(i).Kind == SimEventKind.ProjectileFired) fired++;
            Assert.AreEqual(1, fired);
        }

        [Test]
        public void FiredEvent_AmountIsVelocitySimAngle()
        {
            // Amount carries the shot's sim-plane velocity angle (atan2(vel.y, vel.x),
            // Presentation fix-round app-2pl round 2) so MuzzleFlashView can orient the
            // muzzle burst tick-accurately from the event alone, instead of the
            // render-frame's Curr snapshot (wrong during a multi-tick catch-up flush).
            // Zero spread/recoil removes every other source of angle variance, so this
            // isolates exactly the field under test. Diagonal aim (not the shared
            // hip fixture's straight +X, fix-round 3 round 2): a straight +X shot has
            // atan2 == 0, which is indistinguishable from the OLD hardcoded Amount=0f —
            // that scenario can't actually catch a regression back to the hardcoded
            // value. A 45-degree aim gives a non-zero expected angle the old code would
            // fail, making this a genuinely discriminating regression test.
            var cfg = TestConfigs.OpenField();
            cfg.Weapon.SpreadRad = 0f;
            cfg.Weapon.RecoilPerShotRad = 0f;
            var w = new SimulationWorld(1, cfg);
            var diagonalFire = new SimInput { AimPoint = new float2(10f, 10f), FireHeld = true };
            w.Tick(diagonalFire); // aim (10,10) from Pos (0,0) -> 45 degrees; first shot is instant

            SimEvent fired = default;
            bool found = false;
            for (int i = 0; i < w.EventCount; i++)
            {
                if (w.GetEvent(i).Kind != SimEventKind.ProjectileFired) continue;
                fired = w.GetEvent(i);
                found = true;
                break;
            }
            Assert.IsTrue(found);
            Assert.AreEqual(math.PI / 4f, fired.Amount, 1e-3f);
        }

        [Test]
        public void NoFireWhileSliding_WhenConfigForbids() // Task 15
        {
            var cfg = TestConfigs.Open();
            cfg.Weapon.CanFireWhileSlide = false;
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.SlideTimer = cfg.Hero.SlideDuration; // QA1 seam
            p.SlideDir = new float2(1f, 0f);
            w.SetPlayerForTest(p);
            w.Tick(TestWorlds.HipFire());
            Assert.AreEqual(0, w.Stats.ShotsFired);

            // ...and the very same slide fires normally once the weapon allows it,
            // so the gate above is the reason, not some other slide-time block.
            cfg.Weapon.CanFireWhileSlide = true;
            var allowed = new SimulationWorld(1, cfg);
            var q = allowed.Player;
            q.SlideTimer = cfg.Hero.SlideDuration;
            q.SlideDir = new float2(1f, 0f);
            allowed.SetPlayerForTest(q);
            allowed.Tick(TestWorlds.HipFire());
            Assert.AreEqual(1, allowed.Stats.ShotsFired);
        }

        [Test]
        public void AimedShot_FullSpeed3D() // Task 15, K10
        {
            // The aimed round is a genuine 3D vector: it climbs towards an aim
            // point above the muzzle, and its speed is the config's
            // ProjectileSpeed in THREE dimensions — the climb is not free extra
            // velocity on top of a full-speed horizontal shot. The spray pattern
            // (aim is one tick old here, so the cone is wide open) turns it about
            // the vertical axis and nudges its climb, and must not rescale it
            // either — the renormalize is what holds the speed budget. app-8dv:
            // "rotates it around the vertical axis ONLY" was true of the draw and
            // is not true of the pattern, which owns a vertical half as well.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.Tick(new SimInput { AimPoint = new float2(6f, 4f), AimHeight = cfg.Hero.MaxAimHeight,
                                  AimHeld = true, FireHeld = true });

            Assert.AreEqual(1, w.ProjectileCount);
            ProjectileState shot = w.GetProjectileForTest(0);
            Assert.Greater(shot.VelZ, 0f, "aiming above the muzzle must give the round a climb");
            Assert.AreEqual(cfg.Weapon.ProjectileSpeed,
                math.length(new float3(shot.Vel, shot.VelZ)), 1e-3f);
        }

        [Test]
        public void HipSpread_RunAndSlideMultipliers() // Task 15, D8
        {
            var cfg = TestConfigs.Open();
            WeaponSimConfig weapon = cfg.Weapon;
            HeroSimConfig hero = cfg.Hero;

            var standing = new PlayerState();
            Assert.AreEqual(weapon.SpreadRad, Spread.HipRadians(in weapon, in standing, in hero), 1e-6f);

            // the run threshold is inclusive: exactly at RunSpreadSpeedFrac of
            // MaxSpeed the wider running cone already applies
            var atThreshold = new PlayerState
                { Vel = new float2(weapon.RunSpreadSpeedFrac * hero.MaxSpeed, 0f) };
            Assert.AreEqual(weapon.SpreadRad * weapon.SpreadRunMult,
                Spread.HipRadians(in weapon, in atThreshold, in hero), 1e-6f);

            var justBelow = new PlayerState
                { Vel = new float2(weapon.RunSpreadSpeedFrac * hero.MaxSpeed - 1e-2f, 0f) };
            Assert.AreEqual(weapon.SpreadRad,
                Spread.HipRadians(in weapon, in justBelow, in hero), 1e-6f);

            // sliding widens it further and outranks the run branch outright
            var sliding = new PlayerState
                { SlideTimer = hero.SlideDuration, Vel = new float2(hero.SlideSpeed, 0f) };
            Assert.AreEqual(weapon.SpreadRad * weapon.SpreadSlideMult,
                Spread.HipRadians(in weapon, in sliding, in hero), 1e-6f);

            // recoil rides INSIDE the movement multiplier, not beside it
            var recoiling = new PlayerState
                { SlideTimer = hero.SlideDuration, RecoilOffset = weapon.RecoilMaxRad };
            Assert.AreEqual((weapon.SpreadRad + weapon.RecoilMaxRad) * weapon.SpreadSlideMult,
                Spread.HipRadians(in weapon, in recoiling, in hero), 1e-6f);
        }

        [Test]
        public void FirstAimTick_SpreadNotZero() // Task 15, C2
        {
            // Aim that has only just gone up is not yet a laser: on the first
            // aimed tick the settle fraction is a single tick of AimSettleSeconds,
            // so almost the whole base cone still applies.
            var cfg = TestConfigs.OpenField();
            float settle = SimulationWorld.TickDt / cfg.Hero.AimSettleSeconds;
            // recoil is still zero on a fresh world's first tick, so the base
            // cone's leftover is the entire effective cone
            float cone = cfg.Weapon.SpreadRad * (1f - settle);
            Assert.Greater(cone, 0f, "fixture: the base cone must survive the first tick's settle");

            AssertCone(AimedShotAngles(cfg, 0f, 0f), cone, "first aim tick");
        }

        [Test]
        public void AimedSpray_HasSpread() // Task 15, D15
        {
            // Fully settled aim never becomes a laser while the weapon is
            // spraying: the base cone is gone by then, but accumulated recoil IS
            // a cone of its own — and, settled, it is the ONLY term left.
            var cfg = TestConfigs.OpenField();
            // the shot's own tick decays recoil once before drawing from it
            float cone = cfg.Weapon.RecoilMaxRad
                - cfg.Weapon.RecoilRecoveryRadPerSec * SimulationWorld.TickDt;

            AssertCone(AimedShotAngles(cfg, cfg.Hero.AimSettleSeconds, cfg.Weapon.RecoilMaxRad),
                cone, "settled spray");
        }

        [Test]
        public void NoShotEverTouchesTheSpreadStream() // Task 15; app-8dv T1 (test 2, M231)
        {
            // app-8dv (spec §3.2): this used to assert that a CONE-LESS shot
            // spends no randomness. It now asserts the whole claim -- NO shot
            // takes the spread stream, whatever its cone -- because the draw is
            // gone and SprayPattern answers the angle from state both sides
            // already hold. The stream itself STAYS in the world (Р450): it is
            // still seeded, still saved and still there for whatever may want it.
            // What it has lost is the right to sit on the path of a shot.
            //
            // ⛔⛔ THE SECOND-SHOT LOOP AND THE RecoilOffset PREMISE BELOW ARE
            // LOAD-BEARING AND STAY. On the FIRST shot the cone is zero (settled
            // aim, recoil still 0), so the `if (a > 0f)` branch does not execute
            // even on a mutant that puts the draw back — the first half alone
            // would pass against M231. The only place a restored draw can move
            // the stream is the SECOND shot, whose own recoil has opened a cone
            // by then, which is what makes this a witness and not a tautology.
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.AimSettleTimer = cfg.Hero.AimSettleSeconds; // QA1 seam; recoil is still 0
            w.SetPlayerForTest(p);
            uint before = w.SaveState().SpreadRng.state;

            w.Tick(AimedFire(in cfg));
            Assert.AreEqual(1, w.Stats.ShotsFired, "fixture: the tick under test must fire");
            Assert.AreEqual(before, w.SaveState().SpreadRng.state,
                "a cone-less shot must not consume a spread draw");

            // ...and once that shot's own recoil HAS opened a cone, the next one
            // still leaves the stream alone: the pattern is not a draw.
            for (int i = 0; i < 8 && w.Stats.ShotsFired < 2; i++) w.Tick(AimedFire(in cfg));
            Assert.AreEqual(2, w.Stats.ShotsFired, "fixture: a second shot must fit the budget");
            Assert.Greater(w.Player.RecoilOffset, 0f, "fixture: recoil must have opened a cone");
            Assert.AreEqual(before, w.SaveState().SpreadRng.state,
                "a shot with an OPEN cone consumed a spread draw — the stream is back on the shot path");
        }

        [Test]
        public void Recoil_AccumulatesAndDecays_InAimMode() // Task 15, D8
        {
            // Recoil is weapon state, not a hip-fire quirk: the aimed branch
            // accumulates and decays it exactly like the hip one
            // (Recoil_AccumulatesWhileFiring_DecaysToZeroAfter above).
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            SimInput aimedFire = AimedFire(in cfg);
            float peak = 0f;
            for (int i = 0; i < 60; i++)
            {
                w.Tick(aimedFire);
                peak = math.max(peak, w.Player.RecoilOffset);
            }
            Assert.Greater(peak, cfg.Weapon.RecoilPerShotRad * 2f);
            for (int i = 0; i < 120; i++) w.Tick(default);
            Assert.AreEqual(0f, w.Player.RecoilOffset, 1e-4f);
        }

        [Test]
        public void ReleasingFire_ResetsTheBurst_ButADashDoesNot()   // tests 5 and 6, M236/M237
        {
            // The reset belongs to the RELEASE of fire and stands BEFORE the
            // early return on !CanFire. The opposite order (a reset inside the
            // !CanFire branch) is an exploit: a dash, a slide or the backpack
            // window would each put the pattern back onto its pinpoint first
            // shot.
            //
            // ⛔⛔ WHAT IS PINNED IS THE DASH, NOT THE SLIDE, AND THAT DECIDES
            // THE FATE OF MUTATION M237 (review finding D, checked against the
            // code). A slide does NOT close fire: CanFire reads
            // (weapon.CanFireWhileSlide || p.SlideTimer <= 0f), and
            // CanFireWhileSlide is true both in this fixture and in the shipped
            // default -- on the mutant the !CanFire branch would simply never
            // execute, BurstShots would go on growing, and the assert would be
            // green. A dash does execute it: CanFireWhileDash is false in both
            // of those sources.
            //
            // The hip fixture is TestWorlds.HipFire, whose own doc carries why
            // it takes no parameter and why it leaves AimHeight unset.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            SimInput fire = TestWorlds.HipFire();
            for (int i = 0; i < 30; i++) w.Tick(fire);
            Assert.Greater(w.Player.BurstShots, 2, "премисса: очередь должна набрать длину");

            int held = w.Player.BurstShots;
            var p = w.Player; p.DashTimer = cfg.Hero.DashDuration; w.SetPlayerForTest(p);
            w.Tick(fire);                       // fire is still HELD, but the dash closed CanFire
            Assert.AreEqual(held, w.Player.BurstShots,
                "дэш сбросил рисунок — сброс стоит в ветке !CanFire вместо !FireHeld");

            // The other half of the same rule, through the same mechanism: the
            // backpack window.
            var p2 = w.Player; p2.DashTimer = 0f; w.SetPlayerForTest(p2);
            SimInput fireWithBackpack = fire; fireWithBackpack.InventoryOpen = true;
            w.Tick(fireWithBackpack);
            Assert.AreEqual(held, w.Player.BurstShots, "окно рюкзака сбросило рисунок");

            w.Tick(default);   // fire released
            Assert.AreEqual(0, w.Player.BurstShots, "отпускание огня не сбросило очередь");
        }

        [Test]
        public void TheClientAndTheServerAgreeOnTheShotCounters()   // ⭐⭐ test 10, M241
        {
            // ⛔⛔ THIS TEST IS ABOUT THE COUNTERS, NOT ABOUT THE ANGLE, AND
            // THAT IS A FACT OF THE CODE RATHER THAN A SIMPLIFICATION (found
            // while checking round 1's own repair). There is nothing here to
            // compare an angle against: the cone the world fires from is taken
            // AFTER recoil decays -- `Advance` lowers RecoilOffset by
            // RecoilRecoveryRadPerSec * dt on its very first line, ahead of the
            // loop -- while the predicted copy has not lived through that decay
            // until `Step` runs. A test computing the client's angle from the
            // pre-tick state would differ from the event by one tick of decay
            // and would be RED ON CORRECT CODE. Reproducing the decay in the
            // test body instead means rewriting the code under test into the
            // test -- the tautology rule 428 refuses.
            // ⇒ The witness for "client and server give ONE angle" is test 14
            // of task T4: there the journal record is born INSIDE the same
            // tick and therefore carries exactly the cone the world fired
            // from. The spec's DoD item travels there with it.
            //
            // What is checked here is what is observable before the journal
            // exists and what mutation M241 touches directly: both counters
            // grow in the SHARED body of Advance, not in its server half.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            var predicted = w.Player;                       // the client's copy
            SimInput fire = TestWorlds.HipFire();

            for (int tick = 0; tick < 40; tick++)
            {
                w.Tick(fire);
                PlayerPrediction.Step(ref predicted, in fire, in cfg, in ImpactPulse.None,
                    System.ReadOnlySpan<PushableBody>.Empty);   // the journal arrives only in T4
                Assert.AreEqual(w.Player.ShotOrdinal, predicted.ShotOrdinal,
                    "счётчик выстрелов разошёлся — он растёт не в общем теле Advance");
                Assert.AreEqual(w.Player.BurstShots, predicted.BurstShots,
                    "номер в очереди разошёлся — сброс или инкремент стоят не в общем теле");
            }
            Assert.Greater(w.Player.ShotOrdinal, 3, "премисса: очередь должна была отстреляться");
            Assert.Greater(w.Player.BurstShots, 3, "премисса: очередь должна была набрать длину");
        }

        [Test]
        public void HipFire_NoLongerFlies_PerfectlyFlat()   // test 11, M242
        {
            // The vertical is what walks a round between zones -- Н32 and the
            // whole gameplay argument of spec §3.3 stand on it. Today VelZ from
            // the hip is a hard zero in WeaponSystem's hip branch.
            // ⚠ THE EXPECTATION IS A NUMBER OUT OF THE GEOMETRY, NOT "greater
            // than zero": by the end of the burst pitchBase reaches
            // SprayPitchAmplitude, so the climb equals
            // ProjectileSpeed * tan(cone * 0.35 * (1 - SprayVariance)) in the
            // pure part.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Weapon.SprayVariance = 0f;                 // pure pattern: the expectation is computable
            var w = new SimulationWorld(1, cfg);
            SimInput fire = TestWorlds.HipFire();
            float peak = 0f;
            for (int i = 0; i < 60; i++)
            {
                w.Tick(fire);
                for (int j = 0; j < w.ProjectileCount; j++)
                    peak = math.max(peak, math.abs(w.Projectiles[j].VelZ));
            }
            float cone = cfg.Weapon.SpreadRad + cfg.Weapon.RecoilMaxRad;
            float expected = cfg.Weapon.ProjectileSpeed
                * math.tan(cone * cfg.Weapon.SprayPitchAmplitude);
            Assert.Greater(peak, 0.5f * expected,
                "вертикали в разбросе нет или она вдвое мельче рисунка");
        }

        [Test]
        public void AimedFire_AlsoClimbs_ButTheShiftDecaysWithTheExistingTilt()   // test 12, M243
        {
            // ⛔ WITHOUT THIS TEST THE MUTATION "pitch only in the hip branch"
            // SURVIVES: the previous test fires from the hip alone and cannot
            // see that mutant.
            // ⚠ THE EXPECTATION IS DERIVED FROM THE GEOMETRY, NOT FROM A CALL
            // TO THE FUNCTION: the addend goes onto vel3.z, i.e. it is a SHIFT
            // and not a rotation, and the gain in elevation angle equals
            // atan(tan(theta) + tan(p)) - theta, which is about p * cos^2(theta).
            // In aimed fire theta is set by AimHeight, so the expectation is
            // computed from it.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Weapon.SprayVariance = 0f;
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.RecoilOffset = cfg.Weapon.RecoilMaxRad;      // the aimed cone is recoil alone
            p.AimSettleTimer = cfg.Hero.AimSettleSeconds;
            w.SetPlayerForTest(p);
            SimInput aimed = AimedFire(in cfg);            // the existing fixture above

            for (int i = 0; i < 30 && w.ProjectileCount == 0; i++) w.Tick(aimed);
            Assert.AreEqual(1, w.ProjectileCount, "премисса: прицельный выстрел обязан состояться");
            // ⚠ THE EXPECTATION IS A NUMBER, NOT "greater than zero" (round 2's
            // correction): at SprayVariance 0 and a settled aim the cone equals
            // RecoilMaxRad minus one tick of decay, the first shot's amplitude
            // is 1/SprayPatternShots, and theta is about 0 (a flat shot), so
            // cos^2(theta) is about 1 and the climb is computed directly.
            float cone = cfg.Weapon.RecoilMaxRad
                - cfg.Weapon.RecoilRecoveryRadPerSec * SimulationWorld.TickDt;
            float pitch = cone * cfg.Weapon.SprayPitchAmplitude / cfg.Weapon.SprayPatternShots;
            Assert.AreEqual(cfg.Weapon.ProjectileSpeed * math.tan(pitch),
                w.GetProjectileForTest(0).VelZ, 0.02f,
                "в прицеле вертикали нет или она не та — рисунок применён только к бедровой ветке");
        }

        // ⛔⛔ THERE IS NO DEGENERATE-CASE TEST BECAUSE THERE IS NO DEGENERATE
        // CASE -- this cancels the spec §3.2 caveat rather than skipping it
        // (round 2's finding, two reviewers independently, checked against the
        // package source). `math.normalizesafe` returns NOT zero but a
        // fallback: it selects the default value unless the length clears
        // FLT_MIN_NORMAL, and both branches of `SpawnShot` hand it a non-zero
        // fallback (`new float2(1f, 0f)`). ⇒ `vel3.xy` is NEVER zeroed,
        // and firing "at one's own feet" leaves along +X and goes through the
        // pattern in full. A test written to the letter of the spec would be
        // RED ON CORRECT CODE -- the same class as the two red
        // ProjectileHeightTests, only in a new test.
    }
}
