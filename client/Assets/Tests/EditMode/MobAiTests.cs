using NUnit.Framework;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class TargetingTests
    {
        [Test]
        public void StationaryTarget_AimsExactlyAtIt()
        {
            float2 dir = Targeting.AimWithLead(float2.zero, new float2(10f, 0f),
                float2.zero, 14f, 0.8f);
            Assert.AreEqual(1f, dir.x, 1e-4f);
            Assert.AreEqual(0f, dir.y, 1e-4f);
        }

        [Test]
        public void MovingTarget_LeadsAhead()
        {
            float2 dir = Targeting.AimWithLead(float2.zero, new float2(10f, 0f),
                new float2(0f, 5f), 14f, 1f);
            Assert.Greater(dir.y, 0.1f); // aims ahead along target's movement direction
        }

        [Test]
        public void TargetFasterThanProjectile_FallbackNoNaN()
        {
            float2 dir = Targeting.AimWithLead(float2.zero, new float2(10f, 0f),
                new float2(0f, 50f), 14f, 1f);
            Assert.IsTrue(math.all(math.isfinite(dir)));
            Assert.AreEqual(1f, math.length(dir), 1e-3f);
        }

        [Test]
        public void LineOfFire_BlockedByObstacle()
        {
            var arena = TestConfigs.DefaultArena(); // obstacle (10,4) r2.2
            Assert.IsFalse(Targeting.HasLineOfFire(new float2(10f, 0f),
                new float2(10f, 8f), 0.15f, arena));
            Assert.IsTrue(Targeting.HasLineOfFire(new float2(-20f, -20f),
                new float2(-25f, -20f), 0.15f, arena));
        }
    }

    public class MobAiTests
    {
        static readonly SimInput Idle = default;

        [Test]
        public void Chaser_ClosesDistanceToPlayer()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.SpawnMobForTest(MobType.Chaser, new float2(15f, 0f));
            float d0 = 15f;
            for (int i = 0; i < 60; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(TestConfigs.Open().Arena);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), d0 - 3f);
        }

        [Test]
        public void Chaser_TelegraphThenStrike_DamagesPlayer()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(1.0f, 0f)); // already within AttackRange
            float hp0 = c.Hero.MaxHp;
            // wait for the strike with margin (FSM may spend a tick or two on Idle→Chase→Telegraph)
            for (int i = 0; i < 40 && w.Player.Hp >= hp0; i++) w.Tick(Idle);
            // exactly one strike: AttackCooldown 0.9s = 27 ticks — the second doesn't make it in time
            Assert.AreEqual(hp0 - c.Chaser.ContactDamage, w.Player.Hp, 1e-3f);
        }

        [Test]
        public void Chaser_BehindObstacle_SteersAroundNotStuck()
        {
            var c = TestConfigs.Open();
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(7f, 0f) };
            c.Arena.ObstacleRadius = new[] { 2f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(14f, 0f)); // player at (0,0) behind the obstacle
            for (int i = 0; i < 300; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.Less(math.distance(snap.Mobs[0].Pos, w.Player.Pos), 3f); // reached it by going around
        }

        [Test]
        public void Gunner_KeepsPreferredRange_AndFiresOnlyWithLoS()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Gunner, new float2(20f, 0f));
            int fired = 0;
            for (int i = 0; i < 300; i++)
            {
                w.ClearEvents();
                w.Tick(Idle);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileFired) fired++;
            }
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            float dist = math.distance(snap.Mobs[0].Pos, w.Player.Pos);
            Assert.That(dist, Is.InRange(c.Gunner.PreferredRange - 2f, c.Gunner.PreferredRange + 2f));
            Assert.Greater(fired, 0);
        }

        [Test]
        public void Gunner_NoLoS_HoldsFire()
        {
            var c = TestConfigs.Open();
            c.Gunner.StrafeSpeed = 0f; // isolate the LoS gate: strafing would move it out of the shadow in ~60 ticks
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            c.Arena.ObstacleRadius = new[] { 3f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Gunner, new float2(9f, 0f)); // within range tolerance, but behind the wall
            int fired = 0;
            for (int i = 0; i < 120; i++)
            {
                w.ClearEvents();
                w.Tick(Idle);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileFired) fired++;
            }
            Assert.AreEqual(0, fired);
        }

        [Test]
        public void Gunner_LongApproach_FiresAtMostOnceOnFirstWindow()
        {
            // F-1 regression: MobAiSystem.UpdateGunner decrements FireCooldown every
            // tick with no floor clamp, unlike the player's WeaponSystem (WeaponSystem.
            // cs:29, `p.FireCooldown = math.max(0f, p.FireCooldown);` while not firing).
            // A gunner spending several seconds in Reposition (outside PreferredRange
            // +-RangeTolerance) racks up a negative "debt" on FireCooldown; the instant
            // it steps inside the tolerance band (LoS is unobstructed the whole way in
            // this open arena), the un-clamped debt lets it fire on several consecutive
            // ticks instead of the single "shoot immediately on acquiring the target"
            // shot the FSM intends.
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            // Far enough that closing into the tolerance band (PreferredRange 9 +-
            // RangeTolerance 1.5) takes several seconds of pure Reposition — long
            // enough for the un-clamped cooldown to rack up multiple FireIntervals
            // (1.6s) of "debt" before it ever gets a legal shot.
            w.SpawnMobForTest(MobType.Gunner, new float2(60f, 0f));

            int windowFired = 0;
            int windowTicksLeft = -1;
            for (int i = 0; i < 600 && windowTicksLeft != 0; i++)
            {
                w.ClearEvents();
                w.Tick(Idle);
                int firedThisTick = 0;
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileFired) firedThisTick++;

                if (windowTicksLeft < 0 && firedThisTick > 0)
                    windowTicksLeft = 5; // opens the 5-tick observation window on the first shot

                if (windowTicksLeft > 0)
                {
                    windowFired += firedThisTick;
                    windowTicksLeft--;
                }
            }

            Assert.Greater(windowFired, 0); // sanity: the gunner did eventually get a shot off
            Assert.LessOrEqual(windowFired, 1,
                "gunner volley-fired its FireCooldown backlog instead of exactly one shot on LoS acquisition (F-1)");
        }

        [Test]
        public void PlayerDead_MobsGoIdle()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            w.KillPlayerForTest();
            for (int i = 0; i < 30; i++) w.Tick(Idle);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(MobAiState.Idle, snap.Mobs[0].Ai);
        }

        [Test]
        public void Separation_PreventsStackingSymmetrically()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(11.9f, 10f));
            w.SpawnMobForTest(MobType.Chaser, new float2(12.1f, 10f));
            w.KillPlayerForTest(); // mobs go Idle — only separation acts
            for (int i = 0; i < 60; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            float dist = math.distance(snap.Mobs[0].Pos, snap.Mobs[1].Pos);
            Assert.Greater(dist, 1.0f); // pushed apart
            // symmetry: the pair's midpoint hasn't drifted
            float2 mid = (snap.Mobs[0].Pos + snap.Mobs[1].Pos) * 0.5f;
            Assert.AreEqual(12f, mid.x, 0.05f);
        }
    }
}
