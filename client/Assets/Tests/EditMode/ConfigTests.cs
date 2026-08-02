using NUnit.Framework;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    public class ConfigTests
    {
        const float Eps = 1e-5f;

        static (HeroConfig, WeaponConfig, MobConfig, MobConfig, WaveConfig, ArenaConfig) MakeDefaults()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            var weapon = ScriptableObject.CreateInstance<WeaponConfig>();
            var chaser = ScriptableObject.CreateInstance<MobConfig>();
            var gunner = ScriptableObject.CreateInstance<MobConfig>();
            var wave = ScriptableObject.CreateInstance<WaveConfig>();
            var arena = ScriptableObject.CreateInstance<ArenaConfig>();
            return (hero, weapon, chaser, gunner, wave, arena);
        }

        [Test]
        public void Build_DefaultAssets_ProducesValidConfig()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            SimConfig cfg = SimConfigBuilder.Build(h, w, c, g, wv, a);
            Assert.Greater(cfg.Hero.MaxSpeed, 0f);
            Assert.AreEqual(a.Obstacles.Length, cfg.Arena.ObstacleCount);
        }

        [Test]
        public void Build_ObstacleOutsideArena_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Obstacles = new[] { new ArenaConfig.Obstacle
                { Pos = new Vector2(100f, 0f), Radius = 2f } };
            Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
        }

        [Test]
        public void Build_NegativeSpeed_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            h.MaxSpeed = -1f;
            Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
        }

        [Test]
        public void Build_ObstacleOverSpawnPoint_Throws()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            a.Obstacles = new[] { new ArenaConfig.Obstacle
                { Pos = Vector2.zero, Radius = 2f } };
            Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a));
        }

        // P-4.5: the SO defaults are the single source of truth for starting balance —
        // they must reproduce TestConfigs.Default() exactly (once the gunner asset's
        // fields are set to the real gunner numbers, since Task 6 only ships the
        // Chaser-shaped MobConfig default; the actual Gunner .asset arrives in Task 7).
        [Test]
        public void Build_DefaultAssets_MatchesTestConfigsBaseline()
        {
            var (h, w, c, g, wv, a) = MakeDefaults();
            var expected = TestConfigs.Default();

            g.MaxSpeed = expected.Gunner.MaxSpeed;
            g.Accel = expected.Gunner.Accel;
            g.Radius = expected.Gunner.Radius;
            g.MaxHp = expected.Gunner.MaxHp;
            g.ContactDamage = expected.Gunner.ContactDamage;
            g.AttackRange = expected.Gunner.AttackRange;
            g.TelegraphSeconds = expected.Gunner.TelegraphSeconds;
            g.AttackCooldown = expected.Gunner.AttackCooldown;
            g.PreferredRange = expected.Gunner.PreferredRange;
            g.RangeTolerance = expected.Gunner.RangeTolerance;
            g.StrafeSpeed = expected.Gunner.StrafeSpeed;
            g.FireInterval = expected.Gunner.FireInterval;
            g.ProjectileSpeed = expected.Gunner.ProjectileSpeed;
            g.ProjectileRadius = expected.Gunner.ProjectileRadius;
            g.ProjectileLifetime = expected.Gunner.ProjectileLifetime;
            g.ProjectileDamage = expected.Gunner.ProjectileDamage;
            g.LeadFactor = expected.Gunner.LeadFactor;
            g.SeparationRadius = expected.Gunner.SeparationRadius;
            g.SeparationStrength = expected.Gunner.SeparationStrength;
            g.AvoidLookahead = expected.Gunner.AvoidLookahead;
            g.AvoidMargin = expected.Gunner.AvoidMargin;

            SimConfig cfg = SimConfigBuilder.Build(h, w, c, g, wv, a);

            AssertHeroEqual(expected.Hero, cfg.Hero);
            AssertWeaponEqual(expected.Weapon, cfg.Weapon);
            AssertMobEqual(expected.Chaser, cfg.Chaser);
            AssertMobEqual(expected.Gunner, cfg.Gunner);
            AssertWaveEqual(expected.Wave, cfg.Wave);
            AssertArenaEqual(expected.Arena, cfg.Arena);

            // The chaser/gunner archetypes must land in the matching SimConfig slot,
            // not get swapped by the builder's mapping.
            Assert.AreEqual(expected.Chaser.FireInterval, cfg.Chaser.FireInterval, Eps);
            Assert.AreEqual(expected.Gunner.FireInterval, cfg.Gunner.FireInterval, Eps);
            Assert.AreNotEqual(cfg.Chaser.FireInterval, cfg.Gunner.FireInterval);
        }

        static void AssertHeroEqual(HeroSimConfig e, HeroSimConfig a)
        {
            Assert.AreEqual(e.MaxSpeed, a.MaxSpeed, Eps);
            Assert.AreEqual(e.Accel, a.Accel, Eps);
            Assert.AreEqual(e.Friction, a.Friction, Eps);
            Assert.AreEqual(e.Radius, a.Radius, Eps);
            Assert.AreEqual(e.MaxHp, a.MaxHp, Eps);
            Assert.AreEqual(e.DashSpeed, a.DashSpeed, Eps);
            Assert.AreEqual(e.DashDuration, a.DashDuration, Eps);
            Assert.AreEqual(e.DashCooldown, a.DashCooldown, Eps);
            Assert.AreEqual(e.DashIframes, a.DashIframes, Eps);
            Assert.AreEqual(e.DashBufferWindow, a.DashBufferWindow, Eps);
        }

        static void AssertWeaponEqual(WeaponSimConfig e, WeaponSimConfig a)
        {
            Assert.AreEqual(e.FireInterval, a.FireInterval, Eps);
            Assert.AreEqual(e.ProjectileSpeed, a.ProjectileSpeed, Eps);
            Assert.AreEqual(e.ProjectileRadius, a.ProjectileRadius, Eps);
            Assert.AreEqual(e.ProjectileLifetime, a.ProjectileLifetime, Eps);
            Assert.AreEqual(e.Damage, a.Damage, Eps);
            Assert.AreEqual(e.SpreadRad, a.SpreadRad, Eps);
            Assert.AreEqual(e.RecoilPerShotRad, a.RecoilPerShotRad, Eps);
            Assert.AreEqual(e.RecoilRecoveryRadPerSec, a.RecoilRecoveryRadPerSec, Eps);
            Assert.AreEqual(e.RecoilMaxRad, a.RecoilMaxRad, Eps);
            Assert.AreEqual(e.MuzzleOffset, a.MuzzleOffset, Eps);
            Assert.AreEqual(e.CanFireWhileDash, a.CanFireWhileDash);
        }

        static void AssertMobEqual(MobSimConfig e, MobSimConfig a)
        {
            Assert.AreEqual(e.MaxSpeed, a.MaxSpeed, Eps);
            Assert.AreEqual(e.Accel, a.Accel, Eps);
            Assert.AreEqual(e.Radius, a.Radius, Eps);
            Assert.AreEqual(e.MaxHp, a.MaxHp, Eps);
            Assert.AreEqual(e.ContactDamage, a.ContactDamage, Eps);
            Assert.AreEqual(e.AttackRange, a.AttackRange, Eps);
            Assert.AreEqual(e.TelegraphSeconds, a.TelegraphSeconds, Eps);
            Assert.AreEqual(e.AttackCooldown, a.AttackCooldown, Eps);
            Assert.AreEqual(e.PreferredRange, a.PreferredRange, Eps);
            Assert.AreEqual(e.RangeTolerance, a.RangeTolerance, Eps);
            Assert.AreEqual(e.StrafeSpeed, a.StrafeSpeed, Eps);
            Assert.AreEqual(e.FireInterval, a.FireInterval, Eps);
            Assert.AreEqual(e.ProjectileSpeed, a.ProjectileSpeed, Eps);
            Assert.AreEqual(e.ProjectileRadius, a.ProjectileRadius, Eps);
            Assert.AreEqual(e.ProjectileLifetime, a.ProjectileLifetime, Eps);
            Assert.AreEqual(e.ProjectileDamage, a.ProjectileDamage, Eps);
            Assert.AreEqual(e.LeadFactor, a.LeadFactor, Eps);
            Assert.AreEqual(e.SeparationRadius, a.SeparationRadius, Eps);
            Assert.AreEqual(e.SeparationStrength, a.SeparationStrength, Eps);
            Assert.AreEqual(e.AvoidLookahead, a.AvoidLookahead, Eps);
            Assert.AreEqual(e.AvoidMargin, a.AvoidMargin, Eps);
        }

        static void AssertWaveEqual(WaveSimConfig e, WaveSimConfig a)
        {
            Assert.AreEqual(e.FirstWaveDelay, a.FirstWaveDelay, Eps);
            Assert.AreEqual(e.WavePause, a.WavePause, Eps);
            Assert.AreEqual(e.SpawnRingInset, a.SpawnRingInset, Eps);
            Assert.AreEqual(e.MinSpawnDistanceToPlayer, a.MinSpawnDistanceToPlayer, Eps);
            Assert.AreEqual(e.BaseCount, a.BaseCount);
            Assert.AreEqual(e.CountGrowth, a.CountGrowth);
            Assert.AreEqual(e.MaxMobsPerWave, a.MaxMobsPerWave);
            Assert.AreEqual(e.MaxSpawnAttempts, a.MaxSpawnAttempts);
            Assert.AreEqual(e.FallbackSlots, a.FallbackSlots);
            Assert.AreEqual(e.GunnerShareBase, a.GunnerShareBase, Eps);
            Assert.AreEqual(e.GunnerShareGrowth, a.GunnerShareGrowth, Eps);
        }

        static void AssertArenaEqual(ArenaSimConfig e, ArenaSimConfig a)
        {
            Assert.AreEqual(e.Radius, a.Radius, Eps);
            Assert.AreEqual(e.ObstacleCount, a.ObstacleCount);
            Assert.AreEqual(e.MaxMobs, a.MaxMobs);
            Assert.AreEqual(e.MaxProjectiles, a.MaxProjectiles);
            Assert.AreEqual(e.MaxEventsPerFrame, a.MaxEventsPerFrame);
            for (int i = 0; i < e.ObstacleCount; i++)
            {
                Assert.AreEqual(e.ObstaclePos[i].x, a.ObstaclePos[i].x, Eps);
                Assert.AreEqual(e.ObstaclePos[i].y, a.ObstaclePos[i].y, Eps);
                Assert.AreEqual(e.ObstacleRadius[i], a.ObstacleRadius[i], Eps);
            }
        }
    }
}
