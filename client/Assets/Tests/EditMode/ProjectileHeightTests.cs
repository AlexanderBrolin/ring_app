using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class ProjectileHeightTests
    {
        /// Д15 shot geometry — normative fixture numbers, not derived here: the
        /// hero fires from muzzle height at a point in the Gunner's head band 9 m
        /// out, which puts the straight trajectory over a Chaser's crown at 6.5 m
        /// and under it at 2 m. Their relation to the config is asserted in the
        /// fixtures below rather than assumed.
        const float MuzzleH = 1f, AimH = 3.1f, GunnerX = 9f;

        /// A Chaser screening the line of fire plus the Gunner behind it, both
        /// frozen where they spawn: this pair measures the height gate, so a
        /// Chaser closing in (or a Gunner orbiting at StrafeSpeed) must not drag
        /// the sweep entry away from the geometry above.
        static SimulationWorld SpawnPair(float chaserX, float gunnerX, out SimConfig cfg)
        {
            cfg = TestConfigs.Open();
            cfg.Chaser.MaxSpeed = 0f;
            cfg.Gunner.MaxSpeed = 0f;
            cfg.Gunner.StrafeSpeed = 0f;
            var w = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(w,
                (MobType.Chaser, new float2(chaserX, 0f)),
                (MobType.Gunner, new float2(gunnerX, 0f)));
            return w;
        }

        [Test]
        public void Projectile_WithVelZ_AdvancesHeightPerTick()
        {
            var w = new SimulationWorld(1, TestConfigs.Open());
            w.SpawnProjectileForTest(ProjectileOwner.Player,
                new float2(0f, 0f), new float2(10f, 0f),
                height: 1f, velZ: -3f, damage: 1f, radius: 0.1f, ttl: 5f);
            w.Tick(new SimInput());
            var p = w.GetProjectileForTest(0);
            Assert.AreEqual(1f - 3f * SimulationWorld.TickDt, p.Height, 1e-5f);
            Assert.AreEqual(1f, p.PrevHeight, 1e-5f);
        }

        [Test]
        public void GunnerHeadOverCrowd_HitFromFarChaser()
        {
            // Д15 + M5: the trajectory clears the Chaser at 6.5 m (sweep entry
            // x = 5.88 sits at h = 2.37, above HeadTop + ProjectileRadius = 1.97),
            // so that candidate is rejected on height and the scan carries on to
            // the Gunner at 9 m, whose head band [BodyTop, HeadTop] the shot is
            // aimed into.
            var w = SpawnPair(6.5f, GunnerX, out SimConfig cfg);
            Assert.That(AimH, Is.InRange(cfg.Gunner.BodyTop, cfg.Gunner.HeadTop));
            Assert.GreaterOrEqual(cfg.Weapon.Damage * cfg.Gunner.HeadDamageMult, cfg.Gunner.MaxHp);

            TestWorlds.FireAimed3D(w, float2.zero, MuzzleH, new float2(GunnerX, 0f), AimH);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, w.MobCount);
            Assert.AreEqual(MobType.Chaser, w.Mobs[0].Type);
            // the Chaser was passed over, not merely survived
            Assert.AreEqual(cfg.Chaser.MaxHp, w.Mobs[0].Hp, 1e-4f);
        }

        [Test]
        public void CloseChaser_ScreensGunnerHead()
        {
            // the same shot 4.5 m earlier: at the Chaser's sweep entry x = 1.38 the
            // trajectory is only h = 1.32, inside the Chaser's column — it eats the
            // round and the Gunner behind it is never reached.
            var w = SpawnPair(2f, GunnerX, out SimConfig cfg);
            TestWorlds.FireAimed3D(w, float2.zero, MuzzleH, new float2(GunnerX, 0f), AimH);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(2, w.MobCount); // nobody died
            Assert.AreEqual(cfg.Chaser.MaxHp - cfg.Weapon.Damage * cfg.Chaser.BodyDamageMult,
                w.Mobs[0].Hp, 1e-4f);       // the Chaser took the body hit
            Assert.AreEqual(cfg.Gunner.MaxHp, w.Mobs[1].Hp, 1e-4f); // the Gunner is untouched
        }

        [Test]
        public void Graze_AtHeadTopPlusRadius_HitsAsHead()
        {
            var cfg = TestConfigs.Open();
            cfg.Chaser.MaxSpeed = 0f;
            float column = cfg.Chaser.HeadTop + cfg.Weapon.ProjectileRadius;

            var grazing = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(grazing, (MobType.Chaser, new float2(5f, 0f)));
            TestWorlds.FireAimed3D(grazing, float2.zero, column - 1e-4f,
                new float2(5f, 0f), column - 1e-4f);
            TestWorlds.RunUntilProjectilesDie(grazing);
            Assert.IsTrue(TestEvents.TryFirstOf(grazing, SimEventKind.ProjectileHit,
                out SimEvent graze), "the grazing shot did not register");
            Assert.AreEqual(HitZone.Head, graze.Zone); // clamped down onto the crown

            // one projectile radius higher and the column is genuinely cleared
            var over = new SimulationWorld(1, cfg);
            TestWorlds.SpawnMobsAt(over, (MobType.Chaser, new float2(5f, 0f)));
            TestWorlds.FireAimed3D(over, float2.zero, column + 1e-3f,
                new float2(5f, 0f), column + 1e-3f);
            TestWorlds.RunUntilProjectilesDie(over);
            Assert.AreEqual(0, TestEvents.CountOf(over, SimEventKind.ProjectileHit));
            Assert.AreEqual(1, over.MobCount);
            Assert.AreEqual(cfg.Chaser.MaxHp, over.Mobs[0].Hp, 1e-4f);
        }

        [Test]
        public void EqualT_TieBreaksLowerIndex()
        {
            var cfg = TestConfigs.Open();
            cfg.Chaser.MaxSpeed = 0f;
            var w = new SimulationWorld(1, cfg);
            // mirrored across the firing line (so both circles are entered at a
            // bit-identical t) and further apart than SeparationRadius (so the
            // pair never nudges itself out of that symmetry)
            float halfGap = 0.5f * cfg.Chaser.SeparationRadius + 0.1f;
            TestWorlds.SpawnMobsAt(w,
                (MobType.Chaser, new float2(5f, halfGap)),
                (MobType.Chaser, new float2(5f, -halfGap)));
            int lowSlotId = w.Mobs[0].Id;

            // one fat, overkill round wide enough to reach both circles at once
            w.SpawnProjectileForTest(ProjectileOwner.Player, float2.zero,
                new float2(cfg.Weapon.ProjectileSpeed, 0f), MuzzleH, 0f,
                cfg.Chaser.MaxHp * 10f, 0.6f, cfg.Weapon.ProjectileLifetime);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, w.MobCount); // single-target: no piercing
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.MobDied, out SimEvent died));
            Assert.AreEqual(lowSlotId, died.EntityId);
        }
    }
}
