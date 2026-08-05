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
                    MaxAimHeight = hero.MaxAimHeight,
                    StaminaMax = hero.StaminaMax,
                    DashStaminaCost = hero.DashStaminaCost,
                    SlideStaminaCost = hero.SlideStaminaCost,
                    StaminaRegenPerSec = hero.StaminaRegenPerSec,
                    StaminaRegenDelay = hero.StaminaRegenDelay,
                    LinkRefund = hero.LinkRefund,
                    SlideSpeed = hero.SlideSpeed,
                    SlideDuration = hero.SlideDuration,
                    SlideSteerRadPerSec = hero.SlideSteerRadPerSec,
                    SlideMinSpeedFrac = hero.SlideMinSpeedFrac,
                    RunUpSeconds = hero.RunUpSeconds,
                    RunUpDecayMult = hero.RunUpDecayMult,
                    SlideBufferWindow = hero.SlideBufferWindow,
                    LinkWindowSeconds = hero.LinkWindowSeconds,
                    PostDashSlideWindow = hero.PostDashSlideWindow,
                    SlideWallStopDot = hero.SlideWallStopDot,
                    RicochetRetention = hero.RicochetRetention,
                    AimMoveSpeedFrac = hero.AimMoveSpeedFrac,
                    AimSlideSpeedMult = hero.AimSlideSpeedMult,
                    AimSettleSeconds = hero.AimSettleSeconds,
                    EdgeRequestMinTicks = hero.EdgeRequestMinTicks
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
                    CanFireWhileDash = weapon.CanFireWhileDash,
                    CanFireWhileSlide = weapon.CanFireWhileSlide,
                    SpreadRunMult = weapon.SpreadRunMult,
                    SpreadSlideMult = weapon.SpreadSlideMult,
                    RunSpreadSpeedFrac = weapon.RunSpreadSpeedFrac
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
                MaxEventsPerFrame = a.MaxEventsPerFrame,
                MaxPlayers = a.MaxPlayers,
                PlayerSpawnRingFrac = a.PlayerSpawnRingFrac
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

            // Task 2 (spec stamina/slide/aim): stamina pool + action costs/regen.
            ReqPositive(errors, "Hero.StaminaMax", cfg.Hero.StaminaMax);
            ReqPositive(errors, "Hero.StaminaRegenPerSec", cfg.Hero.StaminaRegenPerSec);
            ReqNonNegative(errors, "Hero.StaminaRegenDelay", cfg.Hero.StaminaRegenDelay);
            ReqCostWithinStamina(errors, "Hero.DashStaminaCost", cfg.Hero.DashStaminaCost, cfg.Hero.StaminaMax);
            ReqCostWithinStamina(errors, "Hero.SlideStaminaCost", cfg.Hero.SlideStaminaCost, cfg.Hero.StaminaMax);
            // В1 fix-wave 3 (owner economy rework): LinkRefund must stay
            // non-negative and strictly below the cheaper of the two costs it
            // discounts — equal-or-above either one would let a chain of
            // linked moves net-gain stamina forever (perpetual motion).
            ReqNonNegative(errors, "Hero.LinkRefund", cfg.Hero.LinkRefund);
            float minLinkCost = math.min(cfg.Hero.DashStaminaCost, cfg.Hero.SlideStaminaCost);
            if (cfg.Hero.LinkRefund >= minLinkCost)
            {
                errors.Add("Hero.LinkRefund must be < min(Hero.DashStaminaCost, Hero.SlideStaminaCost) " +
                    $"(got LinkRefund={cfg.Hero.LinkRefund:F3}, min cost={minLinkCost:F3}).");
            }

            // Task 2: slide kinematics + buffered-input windows.
            ReqPositive(errors, "Hero.SlideSpeed", cfg.Hero.SlideSpeed);
            ReqPositive(errors, "Hero.SlideDuration", cfg.Hero.SlideDuration);
            ReqPositive(errors, "Hero.RunUpSeconds", cfg.Hero.RunUpSeconds);
            ReqNonNegative(errors, "Hero.RunUpDecayMult", cfg.Hero.RunUpDecayMult);
            ReqNonNegative(errors, "Hero.SlideBufferWindow", cfg.Hero.SlideBufferWindow);
            ReqNonNegative(errors, "Hero.LinkWindowSeconds", cfg.Hero.LinkWindowSeconds);
            ReqNonNegative(errors, "Hero.PostDashSlideWindow", cfg.Hero.PostDashSlideWindow);
            ReqInRange(errors, "Hero.SlideMinSpeedFrac", cfg.Hero.SlideMinSpeedFrac, 0f, 1f, minExclusive: true);
            ReqInRange(errors, "Hero.SlideWallStopDot", cfg.Hero.SlideWallStopDot, -1f, 1f);
            ReqInRange(errors, "Hero.RicochetRetention", cfg.Hero.RicochetRetention, 0f, 1f);

            // Task 2: aim-down-sights movement/settle profile. AimMoveSpeedFrac must
            // stay strictly above SlideMinSpeedFrac (D15) — checked separately from the
            // (0,1] range so its message names AimMoveSpeedFrac specifically.
            ReqInRange(errors, "Hero.AimMoveSpeedFrac", cfg.Hero.AimMoveSpeedFrac, 0f, 1f, minExclusive: true);
            if (cfg.Hero.AimMoveSpeedFrac <= cfg.Hero.SlideMinSpeedFrac)
            {
                errors.Add("Hero.AimMoveSpeedFrac must be > Hero.SlideMinSpeedFrac (D15) " +
                    $"(got AimMoveSpeedFrac={cfg.Hero.AimMoveSpeedFrac:F3}, " +
                    $"SlideMinSpeedFrac={cfg.Hero.SlideMinSpeedFrac:F3}).");
            }
            ReqInRange(errors, "Hero.AimSlideSpeedMult", cfg.Hero.AimSlideSpeedMult, 0f, 1f, minExclusive: true);
            ReqPositive(errors, "Hero.AimSettleSeconds", cfg.Hero.AimSettleSeconds);

            // Stage 2 Task 8 (spec Interfaces): minimum tick gap the
            // edge-request gate requires between two ACCEPTED
            // DashRequested/SlideRequested edges of the same kind. Declared in
            // Task 8; consumed since Stage 2 Task 10, where the gate itself
            // landed (PlayerMovementSystem.Update's rate limit at the top of
            // the method).
            // app-zx8 (spec §6e решение "а"): [Range(0,15)] on HeroConfig is an
            // Editor-only Inspector hint, never enforced on a value that
            // reaches the builder from code/JSON/a test fixture — mirror the
            // upper bound here, same precedent as Arena.MaxPlayers (Task 4).
            ReqInRange(errors, "Hero.EdgeRequestMinTicks", cfg.Hero.EdgeRequestMinTicks, 0, 15);
            // app-zx8: even a value inside [0,15] can be tall enough in real
            // time to swallow the dash<->slide link windows whole — at the
            // shipped .asset numbers (LinkWindowSeconds 0.25s = 7.5 ticks,
            // PostDashSlideWindow 0.32s = 9.6 ticks) EdgeRequestMinTicks >= 8
            // eats the legal link entirely, making the ADR-001 mechanic
            // unreachable while still passing every check above. The error
            // names both numbers (gate window in seconds, narrower of the two
            // link windows) so an owner tuning the number knows which one to
            // move.
            float edgeGateWindowSeconds = cfg.Hero.EdgeRequestMinTicks * SimulationWorld.TickDt;
            float minLinkWindowSeconds = math.min(cfg.Hero.LinkWindowSeconds, cfg.Hero.PostDashSlideWindow);
            if (edgeGateWindowSeconds >= minLinkWindowSeconds)
            {
                errors.Add("Hero.EdgeRequestMinTicks * TickDt must be < min(Hero.LinkWindowSeconds, " +
                    "Hero.PostDashSlideWindow) " +
                    $"(got gate window={edgeGateWindowSeconds:F4}s, min link window={minLinkWindowSeconds:F4}s).");
            }

            // Task 2: movement-driven spread widening while running/sliding.
            ReqAtLeast(errors, "Weapon.SpreadRunMult", cfg.Weapon.SpreadRunMult, 1f);
            ReqAtLeast(errors, "Weapon.SpreadSlideMult", cfg.Weapon.SpreadSlideMult, 1f);
            ReqInRange(errors, "Weapon.RunSpreadSpeedFrac", cfg.Weapon.RunSpreadSpeedFrac, 0f, 1f);

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
            // Stage 2 Task 4 (spec §3.2): must stay in lockstep with ArenaConfig's own
            // [Range(...)] Inspector hints — those are never enforced outside
            // the Editor UI, so the builder rejects an out-of-range value
            // reaching it programmatically too (same I3 rationale as
            // ValidateMob's SwingLeadFactor check below).
            ReqInRange(errors, "Arena.MaxPlayers", cfg.Arena.MaxPlayers, 1, 3);
            ReqInRange(errors, "Arena.PlayerSpawnRingFrac", cfg.Arena.PlayerSpawnRingFrac, 0.1f, 0.95f);

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

                // Stage 2 Task 4 (spec §3.2, fix-round 1 I-1): the builder
                // doesn't know the match's actual playerCount (it arrives
                // later, from MatchConfig), and rings for different player
                // counts are NOT nested — e.g. the n=2 ring's point (-28,0)
                // is never a point on the n=3 ring — so every potential spawn
                // point must be checked: the solo center, plus every point on
                // every ring size from n=2 up to Arena.MaxPlayers (at
                // MaxPlayers=3 that's 5 ring points: the n=2 ring's 2 points
                // plus the n=3 ring's 3). Formula reused from
                // Geometry.SpawnPosFor, not duplicated — reuse > duplication.
                // MaxPlayers < 2 (M-3): the ring loop below doesn't run (n
                // starts at 2), so only the center is checked once — no
                // duplicate "covers the spawn point" message.
                float clearanceNeeded = r + cfg.Hero.Radius + spawnClearance;
                CheckSpawnClearance(errors, tag, pos, clearanceNeeded, float2.zero, "solo center");
                for (int n = 2; n <= cfg.Arena.MaxPlayers; n++)
                {
                    for (int s = 0; s < n; s++)
                    {
                        float2 spawnPos = Geometry.SpawnPosFor(s, n, in cfg.Arena);
                        CheckSpawnClearance(errors, tag, pos, clearanceNeeded, spawnPos, $"ring {n}/point {s}");
                    }
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
            // I3 (final review wave, app-n6g): spec-mandated per-archetype
            // range — MobConfig's own [Range(0f, 2f)] Inspector hint is never
            // enforced outside the Editor UI, so the builder must reject an
            // out-of-range value reaching it programmatically too.
            ReqInRange(errors, $"{name}.SwingLeadFactor", m.SwingLeadFactor, 0f, 2f);
            ReqNonNegative(errors, $"{name}.SwingLeadMaxMeters", m.SwingLeadMaxMeters);
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

        /// Stage 2 Task 4: a bounded integer field (e.g. Arena.MaxPlayers) — both ends
        /// inclusive, matching the [Range(min, max)] Inspector hint it mirrors.
        static void ReqInRange(List<string> errors, string name, int value, int min, int max)
        {
            if (value < min || value > max)
                errors.Add($"{name} must be in [{min}, {max}] (got {value}).");
        }

        /// Stage 2 Task 4 (spec §3.2): one obstacle-vs-one-candidate-spawn-point
        /// check, factored out so the loop above can run it once for the solo
        /// center and once per point on every ring size up to MaxPlayers
        /// (fix-round 1 I-1) without repeating the distance/message logic.
        static void CheckSpawnClearance(List<string> errors, string tag, float2 obstaclePos,
            float clearanceNeeded, float2 spawnPos, string pointTag)
        {
            float dist = math.distance(obstaclePos, spawnPos);
            if (dist <= clearanceNeeded)
            {
                errors.Add($"{tag} covers the player spawn point ({pointTag}) " +
                    $"(dist={dist:F3} <= r+Hero.Radius+SpawnClearance={clearanceNeeded:F3}).");
            }
        }

        /// Task 2: a stamina-cost field must be positive and not exceed the pool it
        /// draws from (Hero.StaminaMax).
        static void ReqCostWithinStamina(List<string> errors, string name, float value, float staminaMax)
        {
            ReqPositive(errors, name, value);
            if (value > staminaMax)
            {
                errors.Add($"{name} must be <= Hero.StaminaMax " +
                    $"(got {name}={value:F3}, StaminaMax={staminaMax:F3}).");
            }
        }

        /// Task 2: bounded fractions/dot-products (min optionally exclusive, max always
        /// inclusive — matches every (0,1]/[0,1]/[-1,1] shape this task's fields need).
        static void ReqInRange(List<string> errors, string name, float value, float min, float max,
            bool minExclusive = false)
        {
            bool finite = !(float.IsNaN(value) || float.IsInfinity(value));
            ReqFinite(errors, name, value);
            if (!finite)
                return;

            bool minOk = minExclusive ? value > min : value >= min;
            if (!minOk || value > max)
            {
                string minBrace = minExclusive ? "(" : "[";
                errors.Add($"{name} must be in {minBrace}{min}, {max}] (got {value:F3}).");
            }
        }

        /// Task 2: a lower-bounded multiplier (e.g. spread multipliers must be >= 1 —
        /// they only ever widen the cone, never narrow it).
        static void ReqAtLeast(List<string> errors, string name, float value, float min)
        {
            ReqFinite(errors, name, value);
            if (value < min)
                errors.Add($"{name} must be >= {min} (got {value:F3}).");
        }
    }
}
