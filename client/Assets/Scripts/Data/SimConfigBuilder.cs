using System;
using System.Collections.Generic;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Data
{
    /// Converts balance ScriptableObjects into the plain-data SimConfig consumed by
    /// Ring.Simulation, validating the result before handing it to the sim. Hot-tweak
    /// migration of a running SimulationWorld is NOT here — see SimulationWorld.ApplyConfig.
    public static class SimConfigBuilder
    {
        public static SimConfig Build(HeroConfig hero, WeaponConfig weapon, MobConfig chaser,
            MobConfig gunner, WaveConfig wave, ArenaConfig arena)
        {
            var cfg = new SimConfig
            {
                Hero = new HeroSimConfig
                {
                    MaxSpeed = hero.MaxSpeed,
                    Accel = hero.Accel,
                    Friction = hero.Friction,
                    Radius = hero.Radius,
                    MaxHp = hero.MaxHp,
                    DashSpeed = hero.DashSpeed,
                    DashDuration = hero.DashDuration,
                    DashCooldown = hero.DashCooldown,
                    DashIframes = hero.DashIframes,
                    DashBufferWindow = hero.DashBufferWindow,
                    LegsTop = hero.LegsTop,
                    BodyTop = hero.BodyTop,
                    HeadTop = hero.HeadTop,
                    LegsDamageMult = hero.LegsDamageMult,
                    BodyDamageMult = hero.BodyDamageMult,
                    HeadDamageMult = hero.HeadDamageMult,
                    SlideProfileTop = hero.SlideProfileTop,
                    MuzzleHeight = hero.MuzzleHeight,
                    SlideMuzzleHeight = hero.SlideMuzzleHeight,
                    MaxAimHeight = hero.MaxAimHeight
                },
                Weapon = new WeaponSimConfig
                {
                    FireInterval = weapon.FireInterval,
                    ProjectileSpeed = weapon.ProjectileSpeed,
                    ProjectileRadius = weapon.ProjectileRadius,
                    ProjectileLifetime = weapon.ProjectileLifetime,
                    Damage = weapon.Damage,
                    SpreadRad = weapon.SpreadRad,
                    RecoilPerShotRad = weapon.RecoilPerShotRad,
                    RecoilRecoveryRadPerSec = weapon.RecoilRecoveryRadPerSec,
                    RecoilMaxRad = weapon.RecoilMaxRad,
                    MuzzleOffset = weapon.MuzzleOffset,
                    CanFireWhileDash = weapon.CanFireWhileDash
                },
                Chaser = ToMobSimConfig(chaser),
                Gunner = ToMobSimConfig(gunner),
                Wave = new WaveSimConfig
                {
                    FirstWaveDelay = wave.FirstWaveDelay,
                    WavePause = wave.WavePause,
                    SpawnRingInset = wave.SpawnRingInset,
                    MinSpawnDistanceToPlayer = wave.MinSpawnDistanceToPlayer,
                    BaseCount = wave.BaseCount,
                    CountGrowth = wave.CountGrowth,
                    MaxMobsPerWave = wave.MaxMobsPerWave,
                    MaxSpawnAttempts = wave.MaxSpawnAttempts,
                    FallbackSlots = wave.FallbackSlots,
                    GunnerShareBase = wave.GunnerShareBase,
                    GunnerShareGrowth = wave.GunnerShareGrowth
                },
                Arena = ToArenaSimConfig(arena)
            };

            Validate(in cfg, arena.SpawnClearance);
            return cfg;
        }

        static MobSimConfig ToMobSimConfig(MobConfig m) => new MobSimConfig
        {
            MaxSpeed = m.MaxSpeed,
            Accel = m.Accel,
            Radius = m.Radius,
            MaxHp = m.MaxHp,
            ContactDamage = m.ContactDamage,
            AttackRange = m.AttackRange,
            TelegraphSeconds = m.TelegraphSeconds,
            AttackCooldown = m.AttackCooldown,
            PreferredRange = m.PreferredRange,
            RangeTolerance = m.RangeTolerance,
            StrafeSpeed = m.StrafeSpeed,
            FireInterval = m.FireInterval,
            ProjectileSpeed = m.ProjectileSpeed,
            ProjectileRadius = m.ProjectileRadius,
            ProjectileLifetime = m.ProjectileLifetime,
            ProjectileDamage = m.ProjectileDamage,
            LeadFactor = m.LeadFactor,
            SeparationRadius = m.SeparationRadius,
            SeparationStrength = m.SeparationStrength,
            AvoidLookahead = m.AvoidLookahead,
            AvoidMargin = m.AvoidMargin,
            LegsTop = m.LegsTop,
            BodyTop = m.BodyTop,
            HeadTop = m.HeadTop,
            LegsDamageMult = m.LegsDamageMult,
            BodyDamageMult = m.BodyDamageMult,
            HeadDamageMult = m.HeadDamageMult,
            MuzzleHeight = m.MuzzleHeight,
            SwingLeadFactor = m.SwingLeadFactor,
            SwingLeadMaxMeters = m.SwingLeadMaxMeters
        };

        static ArenaSimConfig ToArenaSimConfig(ArenaConfig a)
        {
            int n = a.Obstacles?.Length ?? 0;
            var pos = new float2[n];
            var radius = new float[n];
            for (int i = 0; i < n; i++)
            {
                pos[i] = new float2(a.Obstacles[i].Pos.x, a.Obstacles[i].Pos.y);
                radius[i] = a.Obstacles[i].Radius;
            }

            return new ArenaSimConfig
            {
                Radius = a.Radius,
                ObstacleCount = n,
                ObstaclePos = pos,
                ObstacleRadius = radius,
                MaxMobs = a.MaxMobs,
                MaxProjectiles = a.MaxProjectiles,
                MaxEventsPerFrame = a.MaxEventsPerFrame
            };
        }

        /// Collects every violation instead of failing on the first one, then throws a
        /// single ArgumentException listing all of them. spawnClearance comes from
        /// ArenaConfig directly — it is not part of ArenaSimConfig (Simulation-side
        /// struct stays unchanged).
        static void Validate(in SimConfig cfg, float spawnClearance)
        {
            var errors = new List<string>();

            ReqPositive(errors, "Hero.MaxSpeed", cfg.Hero.MaxSpeed);
            ReqPositive(errors, "Hero.Accel", cfg.Hero.Accel);
            ReqPositive(errors, "Hero.Friction", cfg.Hero.Friction);
            ReqPositive(errors, "Hero.Radius", cfg.Hero.Radius);
            ReqPositive(errors, "Hero.MaxHp", cfg.Hero.MaxHp);
            ReqPositive(errors, "Hero.DashSpeed", cfg.Hero.DashSpeed);
            ReqPositive(errors, "Hero.DashDuration", cfg.Hero.DashDuration);
            ReqPositive(errors, "Hero.DashCooldown", cfg.Hero.DashCooldown);
            ReqNonNegative(errors, "Hero.DashIframes", cfg.Hero.DashIframes);
            ReqNonNegative(errors, "Hero.DashBufferWindow", cfg.Hero.DashBufferWindow);

            ReqPositive(errors, "Weapon.FireInterval", cfg.Weapon.FireInterval);
            ReqPositive(errors, "Weapon.ProjectileSpeed", cfg.Weapon.ProjectileSpeed);
            ReqPositive(errors, "Weapon.ProjectileRadius", cfg.Weapon.ProjectileRadius);
            ReqPositive(errors, "Weapon.ProjectileLifetime", cfg.Weapon.ProjectileLifetime);
            ReqPositive(errors, "Weapon.Damage", cfg.Weapon.Damage);
            ReqNonNegative(errors, "Weapon.SpreadRad", cfg.Weapon.SpreadRad);
            ReqNonNegative(errors, "Weapon.RecoilPerShotRad", cfg.Weapon.RecoilPerShotRad);
            ReqNonNegative(errors, "Weapon.RecoilRecoveryRadPerSec", cfg.Weapon.RecoilRecoveryRadPerSec);
            ReqNonNegative(errors, "Weapon.RecoilMaxRad", cfg.Weapon.RecoilMaxRad);
            ReqNonNegative(errors, "Weapon.MuzzleOffset", cfg.Weapon.MuzzleOffset);

            ValidateMob(errors, "Chaser", cfg.Chaser);
            ValidateMob(errors, "Gunner", cfg.Gunner);

            ValidateZones(errors, "Hero", cfg.Hero.LegsTop, cfg.Hero.BodyTop, cfg.Hero.HeadTop,
                cfg.Hero.LegsDamageMult, cfg.Hero.BodyDamageMult, cfg.Hero.HeadDamageMult);
            ValidateZones(errors, "Chaser", cfg.Chaser.LegsTop, cfg.Chaser.BodyTop, cfg.Chaser.HeadTop,
                cfg.Chaser.LegsDamageMult, cfg.Chaser.BodyDamageMult, cfg.Chaser.HeadDamageMult);
            ValidateZones(errors, "Gunner", cfg.Gunner.LegsTop, cfg.Gunner.BodyTop, cfg.Gunner.HeadTop,
                cfg.Gunner.LegsDamageMult, cfg.Gunner.BodyDamageMult, cfg.Gunner.HeadDamageMult);

            ReqPositive(errors, "Hero.SlideProfileTop", cfg.Hero.SlideProfileTop);
            if (cfg.Hero.SlideProfileTop > cfg.Hero.BodyTop)
            {
                errors.Add("Hero.SlideProfileTop must be <= Hero.BodyTop " +
                    $"(got SlideProfileTop={cfg.Hero.SlideProfileTop:F3}, BodyTop={cfg.Hero.BodyTop:F3}).");
            }
            if (cfg.Hero.LegsTop > cfg.Hero.SlideProfileTop)
            {
                errors.Add("Hero.SlideProfileTop must be >= Hero.LegsTop " +
                    $"(got SlideProfileTop={cfg.Hero.SlideProfileTop:F3}, LegsTop={cfg.Hero.LegsTop:F3}).");
            }
            if (cfg.Hero.SlideProfileTop + cfg.Gunner.ProjectileRadius >= cfg.Gunner.MuzzleHeight)
            {
                errors.Add("Hero.SlideProfileTop + Gunner.ProjectileRadius must be < Gunner.MuzzleHeight " +
                    $"(got SlideProfileTop={cfg.Hero.SlideProfileTop:F3}, " +
                    $"Gunner.ProjectileRadius={cfg.Gunner.ProjectileRadius:F3}, " +
                    $"Gunner.MuzzleHeight={cfg.Gunner.MuzzleHeight:F3}).");
            }

            if (cfg.Hero.MuzzleHeight > cfg.Hero.HeadTop)
            {
                errors.Add("Hero.MuzzleHeight must be <= Hero.HeadTop " +
                    $"(got MuzzleHeight={cfg.Hero.MuzzleHeight:F3}, HeadTop={cfg.Hero.HeadTop:F3}).");
            }
            if (cfg.Hero.SlideMuzzleHeight > cfg.Hero.SlideProfileTop)
            {
                errors.Add("Hero.SlideMuzzleHeight must be <= Hero.SlideProfileTop " +
                    $"(got SlideMuzzleHeight={cfg.Hero.SlideMuzzleHeight:F3}, " +
                    $"SlideProfileTop={cfg.Hero.SlideProfileTop:F3}).");
            }

            float maxHeadTop = math.max(cfg.Hero.HeadTop, math.max(cfg.Chaser.HeadTop, cfg.Gunner.HeadTop));
            if (cfg.Hero.MaxAimHeight < maxHeadTop)
            {
                errors.Add("Hero.MaxAimHeight must be >= max(Hero.HeadTop, Chaser.HeadTop, Gunner.HeadTop) " +
                    $"(got MaxAimHeight={cfg.Hero.MaxAimHeight:F3}, max HeadTop={maxHeadTop:F3}).");
            }

            ReqNonNegative(errors, "Wave.FirstWaveDelay", cfg.Wave.FirstWaveDelay);
            ReqPositive(errors, "Wave.WavePause", cfg.Wave.WavePause);
            ReqNonNegative(errors, "Wave.SpawnRingInset", cfg.Wave.SpawnRingInset);
            ReqNonNegative(errors, "Wave.MinSpawnDistanceToPlayer", cfg.Wave.MinSpawnDistanceToPlayer);
            ReqPositive(errors, "Wave.BaseCount", cfg.Wave.BaseCount);
            ReqNonNegative(errors, "Wave.CountGrowth", cfg.Wave.CountGrowth);
            ReqPositive(errors, "Wave.MaxMobsPerWave", cfg.Wave.MaxMobsPerWave);
            ReqPositive(errors, "Wave.MaxSpawnAttempts", cfg.Wave.MaxSpawnAttempts);
            ReqNonNegative(errors, "Wave.FallbackSlots", cfg.Wave.FallbackSlots);
            ReqNonNegative(errors, "Wave.GunnerShareBase", cfg.Wave.GunnerShareBase);
            ReqNonNegative(errors, "Wave.GunnerShareGrowth", cfg.Wave.GunnerShareGrowth);

            ReqPositive(errors, "Arena.Radius", cfg.Arena.Radius);
            ReqPositive(errors, "Arena.MaxMobs", cfg.Arena.MaxMobs);
            ReqPositive(errors, "Arena.MaxProjectiles", cfg.Arena.MaxProjectiles);
            ReqPositive(errors, "Arena.MaxEventsPerFrame", cfg.Arena.MaxEventsPerFrame);
            ReqPositive(errors, "Arena.SpawnClearance", spawnClearance);

            for (int i = 0; i < cfg.Arena.ObstacleCount; i++)
            {
                var pos = cfg.Arena.ObstaclePos[i];
                var r = cfg.Arena.ObstacleRadius[i];
                string tag = $"Arena.Obstacles[{i}]";

                ReqFinite(errors, $"{tag}.Pos.x", pos.x);
                ReqFinite(errors, $"{tag}.Pos.y", pos.y);
                ReqPositive(errors, $"{tag}.Radius", r);

                float dist = math.length(pos);
                if (dist + r > cfg.Arena.Radius)
                {
                    errors.Add($"{tag} lies outside the arena " +
                        $"(|pos|+r={dist + r:F3} > Arena.Radius={cfg.Arena.Radius:F3}).");
                }

                float clearanceNeeded = r + cfg.Hero.Radius + spawnClearance;
                if (dist <= clearanceNeeded)
                {
                    errors.Add($"{tag} covers the player spawn point " +
                        $"(|pos|={dist:F3} <= r+Hero.Radius+SpawnClearance={clearanceNeeded:F3}).");
                }
            }

            if (errors.Count > 0)
                throw new ArgumentException("SimConfig validation failed:\n- " + string.Join("\n- ", errors));
        }

        /// Shared hit-zone body validated for Hero, Chaser and Gunner alike (PC5):
        /// the three vertical zone tops must be strictly increasing and the per-zone
        /// damage multipliers must be non-negative.
        static void ValidateZones(List<string> errors, string who, float legs, float body, float head,
            float legsMult, float bodyMult, float headMult)
        {
            ReqPositive(errors, $"{who}.LegsTop", legs);
            if (legs >= body)
            {
                errors.Add($"{who}.LegsTop must be < {who}.BodyTop " +
                    $"(got LegsTop={legs:F3}, BodyTop={body:F3}).");
            }
            if (body >= head)
            {
                errors.Add($"{who}.BodyTop must be < {who}.HeadTop " +
                    $"(got BodyTop={body:F3}, HeadTop={head:F3}).");
            }
            ReqNonNegative(errors, $"{who}.LegsDamageMult", legsMult);
            ReqNonNegative(errors, $"{who}.BodyDamageMult", bodyMult);
            ReqNonNegative(errors, $"{who}.HeadDamageMult", headMult);
        }

        static void ValidateMob(List<string> errors, string name, MobSimConfig m)
        {
            ReqPositive(errors, $"{name}.MaxSpeed", m.MaxSpeed);
            ReqPositive(errors, $"{name}.Accel", m.Accel);
            ReqPositive(errors, $"{name}.Radius", m.Radius);
            ReqPositive(errors, $"{name}.MaxHp", m.MaxHp);
            ReqNonNegative(errors, $"{name}.ContactDamage", m.ContactDamage);
            ReqNonNegative(errors, $"{name}.AttackRange", m.AttackRange);
            ReqNonNegative(errors, $"{name}.TelegraphSeconds", m.TelegraphSeconds);
            ReqNonNegative(errors, $"{name}.AttackCooldown", m.AttackCooldown);
            ReqNonNegative(errors, $"{name}.PreferredRange", m.PreferredRange);
            ReqNonNegative(errors, $"{name}.RangeTolerance", m.RangeTolerance);
            ReqNonNegative(errors, $"{name}.StrafeSpeed", m.StrafeSpeed);
            ReqNonNegative(errors, $"{name}.FireInterval", m.FireInterval);
            ReqNonNegative(errors, $"{name}.ProjectileSpeed", m.ProjectileSpeed);
            ReqNonNegative(errors, $"{name}.ProjectileRadius", m.ProjectileRadius);
            ReqNonNegative(errors, $"{name}.ProjectileLifetime", m.ProjectileLifetime);
            ReqNonNegative(errors, $"{name}.ProjectileDamage", m.ProjectileDamage);
            ReqNonNegative(errors, $"{name}.LeadFactor", m.LeadFactor);
            ReqNonNegative(errors, $"{name}.SeparationRadius", m.SeparationRadius);
            ReqNonNegative(errors, $"{name}.SeparationStrength", m.SeparationStrength);
            ReqNonNegative(errors, $"{name}.AvoidLookahead", m.AvoidLookahead);
            ReqNonNegative(errors, $"{name}.AvoidMargin", m.AvoidMargin);
        }

        static void ReqFinite(List<string> errors, string name, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                errors.Add($"{name} must be finite (got {value}).");
        }

        static void ReqPositive(List<string> errors, string name, float value)
        {
            ReqFinite(errors, name, value);
            if (value <= 0f)
                errors.Add($"{name} must be > 0 (got {value}).");
        }

        static void ReqNonNegative(List<string> errors, string name, float value)
        {
            ReqFinite(errors, name, value);
            if (value < 0f)
                errors.Add($"{name} must be >= 0 (got {value}).");
        }

        static void ReqPositive(List<string> errors, string name, int value)
        {
            if (value <= 0)
                errors.Add($"{name} must be > 0 (got {value}).");
        }

        static void ReqNonNegative(List<string> errors, string name, int value)
        {
            if (value < 0)
                errors.Add($"{name} must be >= 0 (got {value}).");
        }
    }
}
