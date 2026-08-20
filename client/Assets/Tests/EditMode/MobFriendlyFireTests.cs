using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 5 (spec Р252, bd app-46m — owner decision 2026-08-17): a
    /// mob-owned round now hits every OTHER live mob too, not only players —
    /// the guard that used to gate ProjectileSystem's mob-gather loop on
    /// `proj.Owner == ProjectileOwner.Player` is gone. ADR-003 §1's own
    /// diegesis: a gunner spraying its own patrol is machine dementia, not a
    /// rebellion — and deliberately carries NO aggro (app-9pr stays open): the
    /// shooter's own target selection is untouched by taking a hit. The one
    /// exclusion is the round's own shooter (ProjectileState.OwnerEntityId),
    /// so a gunner never wounds itself at the muzzle.
    ///
    /// ROOT CAUSE OF FIX-ROUND 1 (three of four tests below shipped red,
    /// coordinator's diagnosis, systematic-debugging Phase 1): a solo world's
    /// OWN player spawned at the arena center back then — `Geometry.SpawnPosFor`
    /// answered `float2.zero` for `playerCount &lt;= 1` until Stage 3 Ф5-0
    /// removed that special case (owner decision R-173), so solo now takes the
    /// one-player ring point like everybody else. The shooter/victim pair below straddles
    /// that exact center point on the y = 0 line, and a Mob-owned round is
    /// ALWAYS eligible against every live player (no owner exclusion on that
    /// side of the gather, unaffected by this task) — so the round hit the
    /// player sitting at (0, 0), 4-9 m closer than the intended mob victim,
    /// and was consumed there (single-target, no piercing) before it ever
    /// reached the mob. The spawn has moved since, but the fix has not and
    /// must not: every fixture below relocates the solo player WAY off that
    /// line explicitly, which is what makes the firing line a stated premise
    /// rather than a coincidence of wherever the spawn happens to be
    /// (`TestWorlds.RelocatePlayerForTest`, the
    /// existing seam — reuse, not a new one), clear of every round's flight
    /// path, so the friendly-fire round's only eligible target really is the
    /// mob the test names.
    public class MobFriendlyFireTests
    {
        /// Behind the shooter, off the (-5..5, y=0) corridor every fixture
        /// below fires along, and on the SAME y = 0 line as the mobs — a
        /// Chaser's own AI (Targeting.NearestAlivePlayer) then closes on the
        /// player along a purely horizontal line, so it never drifts off the
        /// round's own flight line even while it moves during the tick budget.
        static readonly float2 PlayerOutOfTheWay = new float2(-50f, 0f);

        [Test]
        public void GunnerRound_DamagesAnotherMob()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 1);
            TestWorlds.RelocatePlayerForTest(w, 0, PlayerOutOfTheWay);
            int shooter = w.SpawnMobForTest(MobType.Gunner, new float2(-5f, 0f));
            // SECOND element (lesson 227) — the subject under test.
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            float hpBefore = w.Mobs[1].Hp;
            w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(-4f, 0f), new float2(40f, 0f),
                height: 1f, velZ: 0f, damage: 8f, radius: 0.1f, ttl: 1f,
                ownerIndex: ProjectileIds.NoOwner, ownerEntityId: shooter);
            for (int t = 0; t < 10; t++) w.Tick(default);
            Assert.Less(w.Mobs[1].Hp, hpBefore);
        }

        [Test]
        public void MobRound_DoesNotDamageItsOwnShooter()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 1);
            TestWorlds.RelocatePlayerForTest(w, 0, PlayerOutOfTheWay);
            int shooter = w.SpawnMobForTest(MobType.Gunner, new float2(-5f, 0f));
            float hpBefore = w.Mobs[0].Hp;
            // Same muzzle geometry MobAiSystem.UpdateGunner actually spawns from
            // (m.Pos + aimDir * cfg.Radius): the spawn point sits ON the
            // shooter's own collision circle, so without the owner exclusion
            // this round would register a HitMob candidate on itself at t≈0 —
            // the branch Step 4's mutation (removing the exclusion) is meant to
            // turn red.
            w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(-5f + cfg.Gunner.Radius, 0f),
                new float2(cfg.Gunner.ProjectileSpeed, 0f), height: cfg.Gunner.MuzzleHeight, velZ: 0f,
                damage: cfg.Gunner.ProjectileDamage, radius: cfg.Gunner.ProjectileRadius, ttl: 1f,
                ownerIndex: ProjectileIds.NoOwner, ownerEntityId: shooter);
            for (int t = 0; t < 5; t++) w.Tick(default);
            Assert.AreEqual(hpBefore, w.Mobs[0].Hp,
                "the round must exclude its own shooter from HitMob candidates");
        }

        [Test]
        public void MobKilledByMob_CreditsNobody()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 1);
            TestWorlds.RelocatePlayerForTest(w, 0, PlayerOutOfTheWay);
            int shooter = w.SpawnMobForTest(MobType.Gunner, new float2(-5f, 0f));
            // SECOND element (lesson 227) — the victim.
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            // Overkill damage: the round's own hit must be the killing blow.
            w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(-4f, 0f), new float2(40f, 0f),
                height: 1f, velZ: 0f, damage: cfg.Chaser.MaxHp + 1f, radius: 0.1f, ttl: 1f,
                ownerIndex: ProjectileIds.NoOwner, ownerEntityId: shooter);
            for (int t = 0; t < 10; t++) w.Tick(default);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.MobDied, out _),
                "fixture premise: the round must actually kill the victim");
            Assert.AreEqual(0, w.Stats.Kills,
                "a mob owns no player slot — the NoOwner guard (DamageMob) credits nobody");
        }

        [Test]
        public void MobDiedEvent_FromFriendlyFire_HasNoOwnerIndex()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 1);
            TestWorlds.RelocatePlayerForTest(w, 0, PlayerOutOfTheWay);
            int shooter = w.SpawnMobForTest(MobType.Gunner, new float2(-5f, 0f));
            // SECOND element (lesson 227) — the victim.
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnProjectileForTest(ProjectileOwner.Mob, new float2(-4f, 0f), new float2(40f, 0f),
                height: 1f, velZ: 0f, damage: cfg.Chaser.MaxHp + 1f, radius: 0.1f, ttl: 1f,
                ownerIndex: ProjectileIds.NoOwner, ownerEntityId: shooter);
            for (int t = 0; t < 10; t++) w.Tick(default);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.MobDied, out SimEvent died));
            Assert.AreEqual(ProjectileIds.NoOwner, died.PlayerIndex,
                "MobDied from friendly fire must carry NO owner — no hitmarker draws for anybody");
        }
    }
}
