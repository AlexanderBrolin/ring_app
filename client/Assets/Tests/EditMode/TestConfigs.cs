using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public static class TestConfigs
    {
        public static SimConfig Default()
        {
            return new SimConfig
            {
                Hero = new HeroSimConfig { MaxSpeed = 7f, Accel = 40f, Friction = 30f,
                    Radius = 0.45f, MaxHp = 100f, DashSpeed = 22f, DashDuration = 0.15f,
                    DashCooldown = 1.2f, DashIframes = 0.2f, DashBufferWindow = 0.15f,
                    LegsTop = 0.55f, BodyTop = 1.35f, HeadTop = 1.75f,
                    LegsDamageMult = 0.75f, BodyDamageMult = 1.0f, HeadDamageMult = 1.7f,
                    SlideProfileTop = 0.55f, MuzzleHeight = 1.0f, SlideMuzzleHeight = 0.45f,
                    MaxAimHeight = 3.8f,
                    StaminaMax = 100f, DashStaminaCost = 40f, SlideStaminaCost = 30f,
                    StaminaRegenPerSec = 20f, StaminaRegenDelay = 0.8f, LinkRefund = 10f,
                    SlideSpeed = 13.5f, SlideDuration = 0.52f, SlideSteerRadPerSec = 1.2f,
                    SlideMinSpeedFrac = 0.75f, RunUpSeconds = 1.18f, RunUpDecayMult = 3.0f,
                    SlideBufferWindow = 0.15f, LinkWindowSeconds = 0.25f,
                    PostDashSlideWindow = 0.32f, SlideWallStopDot = 0.7f,
                    RicochetRetention = 0.8f,
                    AimMoveSpeedFrac = 0.8f, AimSlideSpeedMult = 0.5f,
                    AimSettleSeconds = 0.5f,
                    // Stage 2 Task 8: mirrors HeroConfig's C# default (two-sources-
                    // of-numbers discipline — this is the test/code-default side).
                    EdgeRequestMinTicks = 3 },
                Weapon = new WeaponSimConfig { FireInterval = 0.12f, ProjectileSpeed = 35f,
                    ProjectileRadius = 0.12f, ProjectileLifetime = 1.5f, Damage = 12f,
                    SpreadRad = 0.026f, RecoilPerShotRad = 0.006f,
                    // recovery MUST be below RecoilPerShotRad / FireInterval (0.05),
                    // otherwise recoil never accumulates and the cone is dead
                    RecoilRecoveryRadPerSec = 0.03f, RecoilMaxRad = 0.07f,
                    MuzzleOffset = 0.6f, CanFireWhileDash = false,
                    CanFireWhileSlide = true, SpreadRunMult = 1.5f, SpreadSlideMult = 2.0f,
                    RunSpreadSpeedFrac = 0.5f },
                Chaser = new MobSimConfig { MaxSpeed = 5.2f, Accel = 30f, Radius = 0.5f,
                    MaxHp = 30f, ContactDamage = 15f, AttackRange = 1.1f,
                    TelegraphSeconds = 0.35f, AttackCooldown = 0.9f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    LegsTop = 0.60f, BodyTop = 1.45f, HeadTop = 1.85f,
                    LegsDamageMult = 0.75f, BodyDamageMult = 1.0f, HeadDamageMult = 1.7f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f },
                // Gunner's LegsTop/BodyTop/HeadTop already carry the taller ranged-mech
                // tower (Task 17 ships the same numbers into the real .asset via the
                // marker mechanism, ahead of that this baseline is the source of truth,
                // QA4). SwingLeadFactor/SwingLeadMaxMeters are melee-only (Chaser) and
                // simply keep the MobConfig class default here, unused by Gunner.
                Gunner = new MobSimConfig { MaxSpeed = 4f, Accel = 25f, Radius = 0.5f,
                    MaxHp = 20f, PreferredRange = 9f, RangeTolerance = 1.5f, StrafeSpeed = 3f,
                    FireInterval = 1.6f, ProjectileSpeed = 14f, ProjectileRadius = 0.15f,
                    ProjectileLifetime = 3f, ProjectileDamage = 8f, LeadFactor = 0.8f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    LegsTop = 1.10f, BodyTop = 2.70f, HeadTop = 3.50f,
                    LegsDamageMult = 0.75f, BodyDamageMult = 1.0f, HeadDamageMult = 1.7f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f },
                Wave = new WaveSimConfig { FirstWaveDelay = 2.5f, WavePause = 4f,
                    SpawnRingInset = 2f, MinSpawnDistanceToPlayer = 8f, BaseCount = 4,
                    CountGrowth = 2, MaxMobsPerWave = 24, MaxSpawnAttempts = 16,
                    FallbackSlots = 24, GunnerShareBase = 0.2f, GunnerShareGrowth = 0.05f },
                Arena = DefaultArena()
            };
        }

        public static ArenaSimConfig DefaultArena()
        {
            return new ArenaSimConfig
            {
                Radius = 35f, ObstacleCount = 5,
                ObstaclePos = new[] { new float2(10f, 4f), new float2(-8f, 9f),
                    new float2(2f, -12f), new float2(-13f, -6f), new float2(14f, -9f) },
                ObstacleRadius = new[] { 2.2f, 1.8f, 2.5f, 2.0f, 1.6f },
                MaxMobs = 64, MaxProjectiles = 256, MaxEventsPerFrame = 256,
                // Stage 2 Task 4: same values as ArenaConfig's C# defaults
                // (two-sources-of-numbers discipline — this is the test/code-default side).
                MaxPlayers = 3, PlayerSpawnRingFrac = 0.8f
            };
        }

        /// Default config with waves pushed out of reach: movement/combat
        /// fixtures must never meet wave mobs (long runs would kill the player).
        /// Wave scenarios use Default() explicitly (WaveTests only).
        public static SimConfig Quiet()
        {
            var c = Default();
            c.Wave.FirstWaveDelay = 1e6f;
            return c;
        }

        /// Quiet arena without obstacles — open-field movement/combat tests.
        public static SimConfig Open()
        {
            var c = Quiet();
            c.Arena.ObstacleCount = 0;
            c.Arena.ObstaclePos = System.Array.Empty<float2>();
            c.Arena.ObstacleRadius = System.Array.Empty<float>();
            return c;
        }

        /// Open() with an extended slide (Task 10 — M16): SlideDuration 0.9s
        /// (vs the 0.52s default) and a shortened StaminaRegenDelay of 0.3s so
        /// slide-adjacent stamina-regen timing tests (regen frozen for the
        /// whole slide even once the post-action delay alone would have
        /// elapsed; buffer-window regen catch-up) have enough headroom to
        /// observe the behaviour deterministically instead of racing it.
        public static SimConfig RegenFixture()
        {
            var c = Open();
            c.Hero.SlideDuration = 0.9f;
            c.Hero.StaminaRegenDelay = 0.3f;
            return c;
        }
    }
}
