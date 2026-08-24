using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WeaponTests
    {
        static readonly SimInput Fire = new SimInput
            { AimPoint = new float2(10f, 0f), FireHeld = true };

        /// Aimed counterpart of `Fire` (Task 15): same +X aim line, held aim, and
        /// an aim height equal to the standing muzzle height so the shot stays
        /// flat — the vertical axis is ProjectileHeightTests' subject, these
        /// fixtures measure the horizontal cone.
        static SimInput AimedFire(in SimConfig cfg) => new SimInput
            { AimPoint = new float2(10f, 0f), AimHeight = cfg.Hero.MuzzleHeight,
              AimHeld = true, FireHeld = true };

        /// Number of shots a cone measurement samples.
        const int SpreadSamples = 64;

        /// Holds aimed fire until `SpreadSamples` rounds have left the barrel and
        /// returns the horizontal angle of each. The aim line is +X from a player
        /// who never moves, so ProjectileFired's Amount (atan2 of the shot's
        /// sim-plane velocity) IS the deviation from that line — the spread draw,
        /// isolated. The settle/recoil pair under test is re-pinned through the QA1
        /// seam before every tick, so all samples describe the same weapon state
        /// while the shots themselves keep walking the one spread RNG stream (one
        /// stream, not one draw per fresh world: `Random(seed)` states that differ
        /// only in their low bits produce correlated FIRST draws, which would make
        /// a cross-seed sample far narrower than the cone it is measuring).
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
            for (int i = 0; i < 300; i++) w.Tick(Fire); // 10 s
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
                w.Tick(Fire);
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
            SimInput input = Fire; input.InventoryOpen = true;
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
                for (int i = 0; i < ticks; i++) w2.Tick(Fire);
                Assert.Greater(w2.WorldStats.ProjectileSpawnsSkipped, 0);
                return w2.StateHash();
            }
            Assert.AreEqual(Run(cfg), Run(cfg)); // cap degradation is deterministic
        }

        [Test]
        public void FiredEvent_EmittedPerShot()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.Tick(Fire); // first shot is instant
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
            // isolates exactly the field under test. Diagonal aim (not the shared `Fire`
            // fixture's straight +X, fix-round 3 round 2): a straight +X shot has
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
            w.Tick(Fire);
            Assert.AreEqual(0, w.Stats.ShotsFired);

            // ...and the very same slide fires normally once the weapon allows it,
            // so the gate above is the reason, not some other slide-time block.
            cfg.Weapon.CanFireWhileSlide = true;
            var allowed = new SimulationWorld(1, cfg);
            var q = allowed.Player;
            q.SlideTimer = cfg.Hero.SlideDuration;
            q.SlideDir = new float2(1f, 0f);
            allowed.SetPlayerForTest(q);
            allowed.Tick(Fire);
            Assert.AreEqual(1, allowed.Stats.ShotsFired);
        }

        [Test]
        public void AimedShot_FullSpeed3D() // Task 15, K10
        {
            // The aimed round is a genuine 3D vector: it climbs towards an aim
            // point above the muzzle, and its speed is the config's
            // ProjectileSpeed in THREE dimensions — the climb is not free extra
            // velocity on top of a full-speed horizontal shot. The spread draw
            // (aim is one tick old here, so the cone is wide open) rotates it
            // around the vertical axis only and must not rescale it either.
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
        public void SettledAimWithoutRecoil_DrawsNoSpread() // Task 15
        {
            // The cone guard is exact, not "narrow enough": fully settled aim with
            // no recoil left has NO cone, so that shot must not touch the weapon
            // RNG stream at all. Invisible in the shot itself (a zero-wide draw
            // would rotate it by zero anyway) but not in the stream, which every
            // later draw inherits — so the stream state is what this asserts.
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

            // ...and once that shot's own recoil has opened a cone again, the next
            // one does draw: the guard is about the cone, not about aiming.
            for (int i = 0; i < 8 && w.Stats.ShotsFired < 2; i++) w.Tick(AimedFire(in cfg));
            Assert.AreEqual(2, w.Stats.ShotsFired, "fixture: a second shot must fit the budget");
            Assert.Greater(w.Player.RecoilOffset, 0f, "fixture: recoil must have opened a cone");
            Assert.AreNotEqual(before, w.SaveState().SpreadRng.state);
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
    }
}
