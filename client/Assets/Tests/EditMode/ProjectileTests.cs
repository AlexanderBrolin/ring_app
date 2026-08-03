using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class ProjectileTests
    {
        static readonly SimInput FireRight = new SimInput
            { AimPoint = new float2(20f, 0f), FireHeld = true };

        static SimConfig NoSpread()
        {
            var c = TestConfigs.Open();
            c.Weapon.SpreadRad = 0f; c.Weapon.RecoilPerShotRad = 0f;
            return c;
        }

        [Test]
        public void PlayerShot_KillsMob_EmitsHitAndDeath()
        {
            var w = new SimulationWorld(1, NoSpread());
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            int hits = 0, deaths = 0;
            for (int i = 0; i < 60 && deaths == 0; i++)
            {
                w.ClearEvents();
                w.Tick(FireRight);
                for (int e = 0; e < w.EventCount; e++)
                {
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileHit) hits++;
                    if (w.GetEvent(e).Kind == SimEventKind.MobDied) deaths++;
                }
            }
            Assert.Greater(hits, 0);
            Assert.AreEqual(1, deaths);
            Assert.AreEqual(1, w.Stats.Kills);
        }

        [Test]
        public void FastProjectile_SmallTarget_NoTunnel()
        {
            var c = NoSpread();
            c.Weapon.ProjectileSpeed = 120f; // 4 m/tick >> target diameter of 1 m
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));
            for (int i = 0; i < 30; i++) w.Tick(FireRight);
            Assert.Greater(w.Stats.ShotsHit, 0);
        }

        [Test]
        public void ObstacleBeforeMob_BlocksShot_NoDamage()
        {
            var c = NoSpread();
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            c.Arena.ObstacleRadius = new[] { 1.5f };
            var w = new SimulationWorld(1, c);
            w.SpawnMobForTest(MobType.Chaser, new float2(9f, 0f));
            bool blocked = false;
            for (int i = 0; i < 60; i++)
            {
                w.ClearEvents();
                w.Tick(FireRight);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileBlocked) blocked = true;
            }
            Assert.IsTrue(blocked);
            Assert.AreEqual(0, w.Stats.ShotsHit);
        }

        [Test]
        public void TwoTargetsOnPath_NearestDiesFirst()
        {
            var w = new SimulationWorld(1, NoSpread());
            int nearId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnMobForTest(MobType.Chaser, new float2(8f, 0f));
            int firstDeadId = -1;
            for (int i = 0; i < 90 && firstDeadId < 0; i++)
            {
                w.ClearEvents();
                w.Tick(FireRight);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.MobDied)
                    { firstDeadId = w.GetEvent(e).EntityId; break; }
            }
            Assert.AreEqual(nearId, firstDeadId); // the nearer target dies first
        }

        [Test]
        public void MobShot_IframesAbsorb_ThenDamagePasses()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(1, c);
            // enemy projectile right in front of the player during the dash frame — i-frames are active
            w.SpawnProjectileForTest(ProjectileOwner.Mob,
                w.Player.Pos + new float2(1.2f, 0f), new float2(-14f, 0f), 1f, 0f, 8f, 0.15f, 3f);
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true });
            w.Tick(default);
            Assert.AreEqual(c.Hero.MaxHp, w.Player.Hp); // i-frames absorbed it
            for (int i = 0; i < 10; i++) w.Tick(default); // dash and i-frames have expired
            // second projectile — from the player's CURRENT position (shifted after the dash)
            w.SpawnProjectileForTest(ProjectileOwner.Mob,
                w.Player.Pos + new float2(1.2f, 0f), new float2(-14f, 0f), 1f, 0f, 8f, 0.15f, 3f);
            for (int i = 0; i < 4; i++) w.Tick(default);
            Assert.Less(w.Player.Hp, c.Hero.MaxHp);
        }

        [Test]
        public void MultiKillSameTick_SwapRemoveKeepsListConsistent() // spec §3.13 item 11
        {
            var w = new SimulationWorld(1, NoSpread());
            int a = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            int b = w.SpawnMobForTest(MobType.Chaser, new float2(5.2f, 0.4f));
            int c = w.SpawnMobForTest(MobType.Chaser, new float2(5.2f, -0.4f));
            Assert.IsTrue(a != b && b != c); // ids are stable and unique
            // three wide projectiles wipe everyone out in one tick — swap-remove mid-list
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, 0f),
                new float2(35f, 0f), 1f, 0f, 100f, 0.6f, 1f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, 0.4f),
                new float2(35f, 0f), 1f, 0f, 100f, 0.6f, 1f);
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(4f, -0.4f),
                new float2(35f, 0f), 1f, 0f, 100f, 0.6f, 1f);
            int died = 0;
            for (int i = 0; i < 5; i++)
            {
                w.ClearEvents();
                w.Tick(default);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.MobDied) died++;
            }
            Assert.AreEqual(3, died); // nobody lost, nobody double-counted
            var snap = new RenderSnapshot(NoSpread().Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(0, snap.MobCount);
        }

        [Test]
        public void DamageMatrix_MobShotIgnoresMobs_PlayerShotNoPiercing() // §3.5 negative cases
        {
            var cfg = NoSpread();
            var w = new SimulationWorld(1, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnMobForTest(MobType.Chaser, new float2(8f, 0f));
            // enemy projectile flies toward the player through two mobs — ignores the mobs
            w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(10f, 0f),
                new float2(-30f, 0f), 1f, 0f, 5f, 0.15f, 2f);
            for (int i = 0; i < 12; i++) w.Tick(default);
            var snap = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(2, snap.MobCount);
            for (int m = 0; m < snap.MobCount; m++)
                Assert.AreEqual(cfg.Chaser.MaxHp, snap.Mobs[m].Hp); // mobs untouched
            Assert.Less(w.Player.Hp, cfg.Hero.MaxHp);               // player — hit
            // no piercing: an overkill player projectile only kills the nearest
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(3f, 0f),
                new float2(35f, 0f), 1f, 0f, 1000f, 0.12f, 1f);
            for (int i = 0; i < 6; i++) w.Tick(default);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(1, snap.MobCount);
            Assert.AreEqual(cfg.Chaser.MaxHp, snap.Mobs[0].Hp); // the far one is alive and unscathed
        }

        [Test]
        public void Ttl_ExpiresWithEvent()
        {
            var c = NoSpread();
            c.Weapon.ProjectileLifetime = 0.1f; // 3 ticks
            var w = new SimulationWorld(1, c);
            w.Tick(FireRight);
            bool expired = false;
            for (int i = 0; i < 6; i++)
            {
                w.ClearEvents();
                w.Tick(default);
                for (int e = 0; e < w.EventCount; e++)
                    if (w.GetEvent(e).Kind == SimEventKind.ProjectileExpired) expired = true;
            }
            Assert.IsTrue(expired);
        }
    }
}
