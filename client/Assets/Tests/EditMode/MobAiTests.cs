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
        public void Chaser_TelegraphsAheadOfRunner_AndConnects()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            var run = new SimInput { MoveDir = new float2(1f, 0f) }; // player charges straight at the chaser
            float hp0 = c.Hero.MaxHp;

            bool sawTelegraph = false;
            float distAtEntry = 0f;
            MobAiState prevAi = MobAiState.Idle;
            for (int i = 0; i < 200 && w.Player.Hp >= hp0; i++)
            {
                w.Tick(run);
                MobAiState ai = w.Mobs[0].Ai;
                if (!sawTelegraph && ai == MobAiState.Telegraph && prevAi != MobAiState.Telegraph)
                {
                    sawTelegraph = true;
                    distAtEntry = math.distance(w.Mobs[0].Pos, w.Player.Pos);
                }
                prevAi = ai;
            }

            Assert.IsTrue(sawTelegraph, "chaser never entered Telegraph");
            // Predicted lead pulls the windup earlier than raw contact (A15/D9):
            // entry still fires while the runner is outside melee range.
            Assert.Greater(distAtEntry, c.Chaser.AttackRange);
            // ...and thanks to the strike's honest re-validation, the runner's own
            // continued closing still lands the hit — the early windup is not a
            // free miss.
            Assert.AreEqual(hp0 - c.Chaser.ContactDamage, w.Player.Hp, 1e-3f);
        }

        [Test]
        public void Chaser_Standing_FarPlayer_NoTelegraph()
        {
            var c = TestConfigs.Open();
            // QA15: keeps the chaser from physically closing the gap itself over the
            // 60-tick budget — isolates the entry check from turning into a tick-count race.
            c.Chaser.MaxSpeed = 0f;
            var w = new SimulationWorld(1, c);
            float dist = c.Chaser.AttackRange + c.Chaser.SwingLeadMaxMeters + 0.5f;
            w.SpawnMobForTest(MobType.Chaser, new float2(dist, 0f)); // player stands still at the origin
            for (int i = 0; i < 60; i++) w.Tick(Idle);
            Assert.AreNotEqual(MobAiState.Telegraph, w.Mobs[0].Ai);
        }

        [Test]
        public void Chaser_DashDoesNotBaitFromAfar()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            w.Tick(Idle); // mandatory Idle->Chase warm-up tick (no entry check happens on it)
            var p = w.Player;
            p.Vel = new float2(c.Hero.DashSpeed, 0f); // dash-speed burst toward the chaser
            w.SetPlayerForTest(p);
            w.Tick(Idle); // the tick that evaluates Telegraph entry against this burst velocity
            Assert.AreNotEqual(MobAiState.Telegraph, w.Mobs[0].Ai); // lead is clamped by Hero.MaxSpeed, not the dash speed
        }

        [Test]
        public void Chaser_LeadClampedByMaxMeters()
        {
            var c = TestConfigs.Open();
            c.Chaser.MaxSpeed = 0f; // isolate the cap check from the chaser's own approach
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(20f, 0f));
            var run = new SimInput { MoveDir = new float2(1f, 0f) };

            float distAtEntry = -1f;
            for (int i = 0; i < 200 && distAtEntry < 0f; i++)
            {
                w.Tick(run);
                if (w.Mobs[0].Ai == MobAiState.Telegraph)
                    distAtEntry = math.distance(w.Mobs[0].Pos, w.Player.Pos);
            }

            Assert.GreaterOrEqual(distAtEntry, 0f, "chaser never entered Telegraph");
            // One tick's worth of the runner's own closing speed as discretisation slack.
            float maxEntryDist = c.Chaser.AttackRange + c.Chaser.SwingLeadMaxMeters
                + c.Hero.MaxSpeed * SimulationWorld.TickDt;
            Assert.LessOrEqual(distAtEntry, maxEntryDist);
        }

        [Test]
        public void SwingLeadZero_EntryTickEqualsE1Rule()
        {
            var c = TestConfigs.Open();
            // Factor 0 -> PredictPos degenerates to the raw player position exactly
            // (offset = lead * (seconds * 0) = zero vector) — the pre-Task-13 (Э1)
            // raw-distance rule as a special case, bit-exact.
            c.Chaser.SwingLeadFactor = 0f;
            const float spawnX = 8f;

            // Sim A: the tick the AI itself actually enters Telegraph.
            var wa = new SimulationWorld(1, c);
            wa.SpawnMobForTest(MobType.Chaser, new float2(spawnX, 0f));
            int entryTickAi = -1;
            for (int i = 1; i <= 200 && entryTickAi < 0; i++)
            {
                wa.Tick(Idle);
                if (wa.Mobs[0].Ai == MobAiState.Telegraph) entryTickAi = i;
            }

            // Sim B: identical setup/seed, independently tracking the first tick the
            // raw centre-to-centre distance crosses AttackRange — using exactly the
            // positions the entry check itself reads: the mob's position as it stood
            // BEFORE this tick's motion, against the player's position AFTER this
            // tick's movement (movement runs before the AI check inside Tick()).
            var wb = new SimulationWorld(1, c);
            wb.SpawnMobForTest(MobType.Chaser, new float2(spawnX, 0f));
            int entryTickRaw = -1;
            for (int i = 1; i <= 200 && entryTickRaw < 0; i++)
            {
                float2 mobPosBefore = wb.Mobs[0].Pos;
                wb.Tick(Idle);
                float2 playerPosAfter = wb.Player.Pos;
                if (math.distance(mobPosBefore, playerPosAfter) <= c.Chaser.AttackRange)
                    entryTickRaw = i;
            }

            Assert.Greater(entryTickAi, 0, "chaser never entered Telegraph");
            Assert.AreEqual(entryTickRaw, entryTickAi);
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
