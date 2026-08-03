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

        /// Builds a SimConfig from a caller-supplied hero and default everything else —
        /// for zone-validation tests (Task 1) that only need to vary Hero fields.
        static SimConfig BuildWith(HeroConfig hero)
        {
            var (_, w, c, g, wv, a) = MakeDefaults();
            return SimConfigBuilder.Build(hero, w, c, g, wv, a);
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
            g.LegsTop = expected.Gunner.LegsTop;
            g.BodyTop = expected.Gunner.BodyTop;
            g.HeadTop = expected.Gunner.HeadTop;
            g.LegsDamageMult = expected.Gunner.LegsDamageMult;
            g.BodyDamageMult = expected.Gunner.BodyDamageMult;
            g.HeadDamageMult = expected.Gunner.HeadDamageMult;
            g.MuzzleHeight = expected.Gunner.MuzzleHeight;

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

        [Test]
        public void Validate_ZoneOrderViolated_Throws()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.LegsTop = 1.0f; hero.BodyTop = 0.5f; // zone order violated
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("LegsTop"));
        }

        [Test]
        public void Validate_SlideProfileAboveGunnerMuzzle_Throws()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            // NB (QA2/QD3): a fresh MobConfig has ProjectileRadius = 0 (chaser
            // defaults), so 1.0 is used: 1.0 + 0 >= MuzzleHeight(0.95) — rule D5
            // violated, while 1.0 <= Hero.BodyTop (1.35) — the other rules stay quiet.
            hero.SlideProfileTop = 1.0f;
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("SlideProfileTop"));
        }

        [Test]
        public void Validate_ZeroStaminaRegen_Throws()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.StaminaRegenPerSec = 0f;
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("StaminaRegenPerSec"));
        }

        [Test]
        public void Validate_AimFracNotAboveSlideFrac_Throws()
        {
            var hero = ScriptableObject.CreateInstance<HeroConfig>();
            hero.AimMoveSpeedFrac = hero.SlideMinSpeedFrac; // equality is also a violation (strict >)
            var ex = Assert.Throws<System.ArgumentException>(() => BuildWith(hero));
            Assert.That(ex.Message, Does.Contain("AimMoveSpeedFrac"));
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
            Assert.AreEqual(e.LegsTop, a.LegsTop, Eps);
            Assert.AreEqual(e.BodyTop, a.BodyTop, Eps);
            Assert.AreEqual(e.HeadTop, a.HeadTop, Eps);
            Assert.AreEqual(e.LegsDamageMult, a.LegsDamageMult, Eps);
            Assert.AreEqual(e.BodyDamageMult, a.BodyDamageMult, Eps);
            Assert.AreEqual(e.HeadDamageMult, a.HeadDamageMult, Eps);
            Assert.AreEqual(e.SlideProfileTop, a.SlideProfileTop, Eps);
            Assert.AreEqual(e.MuzzleHeight, a.MuzzleHeight, Eps);
            Assert.AreEqual(e.SlideMuzzleHeight, a.SlideMuzzleHeight, Eps);
            Assert.AreEqual(e.MaxAimHeight, a.MaxAimHeight, Eps);
            Assert.AreEqual(e.StaminaMax, a.StaminaMax, Eps);
            Assert.AreEqual(e.DashStaminaCost, a.DashStaminaCost, Eps);
            Assert.AreEqual(e.SlideStaminaCost, a.SlideStaminaCost, Eps);
            Assert.AreEqual(e.LinkedDashStaminaCost, a.LinkedDashStaminaCost, Eps);
            Assert.AreEqual(e.StaminaRegenPerSec, a.StaminaRegenPerSec, Eps);
            Assert.AreEqual(e.StaminaRegenDelay, a.StaminaRegenDelay, Eps);
            Assert.AreEqual(e.SlideSpeed, a.SlideSpeed, Eps);
            Assert.AreEqual(e.SlideDuration, a.SlideDuration, Eps);
            Assert.AreEqual(e.SlideSteerRadPerSec, a.SlideSteerRadPerSec, Eps);
            Assert.AreEqual(e.SlideMinSpeedFrac, a.SlideMinSpeedFrac, Eps);
            Assert.AreEqual(e.RunUpSeconds, a.RunUpSeconds, Eps);
            Assert.AreEqual(e.RunUpDecayMult, a.RunUpDecayMult, Eps);
            Assert.AreEqual(e.SlideBufferWindow, a.SlideBufferWindow, Eps);
            Assert.AreEqual(e.LinkWindowSeconds, a.LinkWindowSeconds, Eps);
            Assert.AreEqual(e.PostDashSlideWindow, a.PostDashSlideWindow, Eps);
            Assert.AreEqual(e.SlideWallStopDot, a.SlideWallStopDot, Eps);
            Assert.AreEqual(e.RicochetRetention, a.RicochetRetention, Eps);
            Assert.AreEqual(e.AimMoveSpeedFrac, a.AimMoveSpeedFrac, Eps);
            Assert.AreEqual(e.AimSlideSpeedMult, a.AimSlideSpeedMult, Eps);
            Assert.AreEqual(e.AimSettleSeconds, a.AimSettleSeconds, Eps);
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
            Assert.AreEqual(e.CanFireWhileSlide, a.CanFireWhileSlide);
            Assert.AreEqual(e.SpreadRunMult, a.SpreadRunMult, Eps);
            Assert.AreEqual(e.SpreadSlideMult, a.SpreadSlideMult, Eps);
            Assert.AreEqual(e.RunSpreadSpeedFrac, a.RunSpreadSpeedFrac, Eps);
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
            Assert.AreEqual(e.LegsTop, a.LegsTop, Eps);
            Assert.AreEqual(e.BodyTop, a.BodyTop, Eps);
            Assert.AreEqual(e.HeadTop, a.HeadTop, Eps);
            Assert.AreEqual(e.LegsDamageMult, a.LegsDamageMult, Eps);
            Assert.AreEqual(e.BodyDamageMult, a.BodyDamageMult, Eps);
            Assert.AreEqual(e.HeadDamageMult, a.HeadDamageMult, Eps);
            Assert.AreEqual(e.MuzzleHeight, a.MuzzleHeight, Eps);
            Assert.AreEqual(e.SwingLeadFactor, a.SwingLeadFactor, Eps);
            Assert.AreEqual(e.SwingLeadMaxMeters, a.SwingLeadMaxMeters, Eps);
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
