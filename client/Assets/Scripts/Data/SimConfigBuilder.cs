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
        /// Stage 3 Task 10 (spec §3.13, ledger R-28): `elite`/`director` are
        /// two NEW TRAILING OPTIONAL parameters (Global Constraints: a new
        /// parameter on an existing helper is tail-only, with a default) —
        /// every one of the 46+ existing 7-argument call sites (ConfigTests.
        /// MakeDefaults et al., ServerBootstrap, the Editor scene
        /// bootstraps, ...) keeps compiling unchanged, silently defaulting
        /// both new SimConfig sections to an all-zero MobSimConfig. `null`
        /// on either one is "no override," not "an error": neither
        /// MobEliteConfig nor MobDirectorConfig is a new SO CLASS (spec
        /// §3.13 corrects that reading — they are new ASSETS of this same
        /// MobConfig class), and their real `.asset` instances plus the
        /// scene-bootstrap wiring that feeds them here are Т12's job
        /// (errata E-6 I5), not this one. This task only needs
        /// MaxBodyRadius (below) to have something to read when a caller
        /// DOES supply one — which is exactly what
        /// ZoneConfigTests.Validate_RejectsDoorNarrowerThanDirector does,
        /// driving a door-width violation off a real Director radius
        /// in-memory, without waiting on Т12's asset delivery.
        /// Stage 3 Task 13 (owner decision R-84): `items`/`loot` are two MORE
        /// trailing optional parameters, same contract as `elite`/`director`/
        /// `flow` above — every one of the 82 real existing call sites (this
        /// task's own recount, coordinator ledger) keeps compiling unchanged,
        /// silently getting an empty catalog and an all-zero LootSimConfig.
        public static SimConfig Build(HeroConfig hero, WeaponConfig weapon, MobConfig chaser,
            MobConfig gunner, WaveConfig wave, ArenaConfig arena, VisibilityConfig visibility,
            MobConfig elite = null, MobConfig director = null, MatchFlowConfig flow = null,
            ItemCatalog items = null, LootConfig loot = null)
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
                    EdgeRequestMinTicks = hero.EdgeRequestMinTicks,
                    // Stage 3 Task 3: auto-pickup collection radius.
                    PickupRadius = hero.PickupRadius,
                    // Stage 3 Task 4: the backpack's two capacity numbers.
                    InventoryCapacity = hero.InventoryCapacity,
                    MaxInventoryItems = hero.MaxInventoryItems
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
                    RunSpreadSpeedFrac = weapon.RunSpreadSpeedFrac,
                    // Stage 3 Task 2: the ammo economy.
                    ShotsPerCell = weapon.ShotsPerCell,
                    AmmoStart = weapon.AmmoStart,
                    AmmoMax = weapon.AmmoMax,
                    EmergencyFireInterval = weapon.EmergencyFireInterval
                },
                Chaser = ToMobSimConfig(chaser),
                Gunner = ToMobSimConfig(gunner),
                // Stage 3 Task 10: see this method's own doc above for why
                // `null` (every EXISTING call site) means "no override"
                // rather than a crash.
                Elite = elite != null ? ToMobSimConfig(elite) : default,
                Director = director != null ? ToMobSimConfig(director) : default,
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
                    GunnerShareGrowth = wave.GunnerShareGrowth,
                    PerPlayerCountFrac = wave.PerPlayerCountFrac,
                    // Stage 3 Task 11 (spec §3.3 Р211/Р212/Р298).
                    ZoneWeights = wave.ZoneWeights,
                    EliteShareMiddle = wave.EliteShareMiddle,
                    EliteShareOuterGrowth = wave.EliteShareOuterGrowth,
                    EliteShareOuterCap = wave.EliteShareOuterCap
                },
                Arena = ToArenaSimConfig(arena),
                // Stage 3 Task 12 (errata E-2): the match-flow pacing block.
                // `null` means "no override" exactly as it does for
                // elite/director above — every call site that predates this
                // parameter keeps getting an all-zero Flow, which is what
                // ConfigTests.Build_MatchFlowConfigOmitted_LeavesFlowAtZero
                // states outright.
                Flow = flow != null
                    ? new MatchFlowSimConfig
                    {
                        GateDelaySeconds = flow.GateDelaySeconds,
                        ExtractChannelSeconds = flow.ExtractChannelSeconds,
                        RetinueCount = flow.RetinueCount,
                        RetinueRespawnSeconds = flow.RetinueRespawnSeconds,
                        DirectorReserveSlots = flow.DirectorReserveSlots
                    }
                    : default,
                // Stage 2 Task 22 (spec §3.15): seventh Build() parameter —
                // field names mirror VisibilitySimConfig one to one.
                Visibility = new VisibilitySimConfig
                {
                    SightRadius = visibility.SightRadius,
                    HearRadius = visibility.HearRadius,
                    ExitHysteresis = visibility.ExitHysteresis,
                    LingerTicks = visibility.LingerTicks,
                    HearPositionGridMeters = visibility.HearPositionGridMeters,
                    // Stage 3 Task 13 (spec §3.9): pickup/container visibility radii.
                    PickupRadiusForVisibility = visibility.PickupRadiusForVisibility,
                    ContainerRadiusForVisibility = visibility.ContainerRadiusForVisibility
                },
                // Stage 3 Task 13: `items` CLONES into SimConfig.Items — not
                // an alias — because the catalog is TOPOLOGY (spec §3.7
                // Р264): ArenaTopologyMatches needs `_config.Items` to stay
                // a stable snapshot of what the running match was built
                // with, immune to a live Inspector edit on the SO mutating
                // the SAME array object mid-match, ahead of any hot-tweak
                // gate ever running (ItemCatalogTests.
                // CatalogIsCopiedIntoSimConfig is the test side of this).
                // `loot`'s own array fields (DropChance/CellsPerMob/
                // TransferSeconds) are NOT cloned — same direct-alias
                // convention Wave.ZoneWeights above already follows, since
                // Loot is balance data, not topology (nothing in
                // SimulationWorld's constructor sizes an array off it).
                // `null` on either parameter means "no override," same
                // contract as elite/director/flow.
                Items = items != null ? (ItemDef[])items.Items.Clone() : System.Array.Empty<ItemDef>(),
                Loot = loot != null
                    ? new LootSimConfig
                    {
                        DropChance = loot.DropChance,
                        CrateCount = loot.CrateCount,
                        CacheCountMiddle = loot.CacheCountMiddle,
                        CacheCountCore = loot.CacheCountCore,
                        RepairKitChance = loot.RepairKitChance,
                        CellsPerMob = loot.CellsPerMob,
                        CorpseCellFraction = loot.CorpseCellFraction,
                        RepairKitHealAmount = loot.RepairKitHealAmount,
                        RepairKitChannelSeconds = loot.RepairKitChannelSeconds,
                        TransferSeconds = loot.TransferSeconds,
                        LootSpawnAttempts = loot.LootSpawnAttempts,
                        LootFallbackSlots = loot.LootFallbackSlots,
                        PickupTtlSeconds = loot.PickupTtlSeconds,
                        ContainerTtlSeconds = loot.ContainerTtlSeconds,
                        LootRadius = loot.LootRadius
                    }
                    // Stage 3 Task 13 (coordinator R-96): NOT `default` —
                    // `default(LootSimConfig)` leaves CellsPerMob/DropChance/
                    // TransferSeconds NULL, and LootDrops.MobDeathCells
                    // indexes CellsPerMob by MobType with no bounds guard
                    // (its own doc's premise, "always exactly four long," is
                    // what ValidateLoot below now enforces WHEN a real
                    // `loot` is supplied — but every one of the 82 call
                    // sites that omit it entirely would still have handed a
                    // null array straight to that index, crashing the FIRST
                    // mob death with a bare NullReferenceException, R-37).
                    // Correctly-SIZED all-zero arrays close that hole for
                    // all three siblings alike (DropChance 4 archetypes x 3
                    // zones, CellsPerMob 4 archetypes, TransferSeconds 4
                    // tiers) — "no override" now means "the same
                    // all-zero/no-drop behavior the golden-safety fixture
                    // already relies on," not "a landmine for the first
                    // reader."
                    : new LootSimConfig
                    {
                        DropChance = new float[12],
                        CellsPerMob = new int[4],
                        TransferSeconds = new float[4]
                    }
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

            // Stage 2 Task 16 (spec §3.15): interior walls, same shape as the
            // obstacle triple above — count plus three parallel arrays.
            int wn = a.Walls?.Length ?? 0;
            var wallA = new float2[wn];
            var wallB = new float2[wn];
            var wallHalfWidth = new float[wn];
            for (int i = 0; i < wn; i++)
            {
                wallA[i] = new float2(a.Walls[i].A.x, a.Walls[i].A.y);
                wallB[i] = new float2(a.Walls[i].B.x, a.Walls[i].B.y);
                wallHalfWidth[i] = a.Walls[i].HalfWidth;
            }

            // Stage 3 Task 8 (spec §3.15): ExtractPos, same Vector2 -> float2
            // conversion shape as the obstacle/wall loops above.
            int en = a.ExtractPos?.Length ?? 0;
            var extractPos = new float2[en];
            for (int i = 0; i < en; i++)
                extractPos[i] = new float2(a.ExtractPos[i].x, a.ExtractPos[i].y);

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
                PlayerSpawnRingFrac = a.PlayerSpawnRingFrac,
                WallCount = wn,
                WallA = wallA,
                WallB = wallB,
                WallHalfWidth = wallHalfWidth,
                // Stage 2 Task 46 (bd app-r8x): the one height every interior
                // barrier — circles and walls alike — is simulated and drawn at.
                BarrierTop = a.BarrierTop,
                // Stage 3 Task 3: per-match pickup cap.
                MaxPickups = a.MaxPickups,
                // Stage 3 Task 8 (spec §3.2/§3.15): zone boundaries, zone-wall
                // arc barriers, doors, portals and container caps — straight
                // field-for-field plumbing, same convention as every prior
                // section above (no business logic here; that is Validate's
                // job below, once it exists).
                ZoneRadius = a.ZoneRadius ?? System.Array.Empty<float>(),
                ZoneWallCount = a.ZoneWallCount,
                ZoneWallRadius = a.ZoneWallRadius ?? System.Array.Empty<float>(),
                ZoneWallHalfWidth = a.ZoneWallHalfWidth ?? System.Array.Empty<float>(),
                ZoneWallDoorStart = a.ZoneWallDoorStart ?? System.Array.Empty<int>(),
                ZoneWallDoorCount = a.ZoneWallDoorCount ?? System.Array.Empty<int>(),
                DoorCenterRad = a.DoorCenterRad ?? System.Array.Empty<float>(),
                DoorFreeWidth = a.DoorFreeWidth ?? System.Array.Empty<float>(),
                DoorClearance = a.DoorClearance,
                ExtractPos = extractPos,
                ExtractZone = a.ExtractZone ?? System.Array.Empty<byte>(),
                ExtractKind = a.ExtractKind ?? System.Array.Empty<byte>(),
                ExtractRadius = a.ExtractRadius,
                MaxContainers = a.MaxContainers,
                MaxContainerSlots = a.MaxContainerSlots
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

            // Stage 3 Task 2 (spec Р261, errata E-6 D-I8): the ammo economy —
            // home of the world's ammo validations (Р72), no second home.
            ReqPositive(errors, "Weapon.ShotsPerCell", cfg.Weapon.ShotsPerCell);
            if (cfg.Weapon.AmmoStart > cfg.Weapon.AmmoMax)
            {
                errors.Add("Weapon.AmmoStart must be <= Weapon.AmmoMax " +
                    $"(got AmmoStart={cfg.Weapon.AmmoStart}, AmmoMax={cfg.Weapon.AmmoMax}).");
            }
            if (cfg.Weapon.EmergencyFireInterval <= cfg.Weapon.FireInterval)
            {
                errors.Add("Weapon.EmergencyFireInterval must be > Weapon.FireInterval " +
                    $"(got EmergencyFireInterval={cfg.Weapon.EmergencyFireInterval:F3}, " +
                    $"FireInterval={cfg.Weapon.FireInterval:F3}).");
            }

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
            // app-zx8 (spec §6e, decision "a"): [Range(0,15)] on HeroConfig is an
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
            // Stage 2 Task 16 (spec §3.4/§3.15): the per-extra-player wave
            // scale. Spec's own rule is ">= 0"; the upper end mirrors
            // WaveConfig's [Range(0f, 2f)] Inspector hint, which is never
            // enforced on a value reaching the builder from code/JSON/a test
            // fixture — same precedent as Arena.MaxPlayers (Task 4) and
            // Hero.EdgeRequestMinTicks (app-zx8).
            ReqInRange(errors, "Wave.PerPlayerCountFrac", cfg.Wave.PerPlayerCountFrac, 0f, 2f);
            // Stage 3 Task 11 (spec §3.3 Р211, coordinator R-56): the zone
            // budget weights — exactly three (Outer/Middle/Core, Zone's own
            // declared order), summing to 1 within float epsilon (same
            // допуск form as every other share-of-a-whole rule in this
            // file).
            if (cfg.Wave.ZoneWeights == null || cfg.Wave.ZoneWeights.Length != 3)
            {
                errors.Add("Wave.ZoneWeights must have exactly 3 elements (Outer, Middle, Core) " +
                    $"(got {cfg.Wave.ZoneWeights?.Length ?? 0}).");
            }
            else
            {
                float zoneWeightSum = cfg.Wave.ZoneWeights[0] + cfg.Wave.ZoneWeights[1]
                    + cfg.Wave.ZoneWeights[2];
                if (math.abs(zoneWeightSum - 1f) > 1e-4f)
                {
                    errors.Add("Wave.ZoneWeights must sum to 1 " +
                        $"(got {zoneWeightSum:F5}: [{cfg.Wave.ZoneWeights[0]:F3}, " +
                        $"{cfg.Wave.ZoneWeights[1]:F3}, {cfg.Wave.ZoneWeights[2]:F3}]).");
                }
                // Ф2 review A-1 = B-I2.2 (both reviewers, independently): the
                // SUM alone is not the rule. {-0.5, 1.4, 0.1} sums to 1 and
                // sails through — and SplitByZones then hands WaveSystem a
                // NEGATIVE per-zone budget, which PendingTotal can never
                // discharge, so the wave phase hangs for the rest of the match
                // with no error anywhere. Per element, not just in aggregate.
                for (int i = 0; i < cfg.Wave.ZoneWeights.Length; i++)
                    ReqNonNegative(errors, $"Wave.ZoneWeights[{i}]", cfg.Wave.ZoneWeights[i]);
            }

            // Ф2 review A-1 = B-I2.2: the three elite shares. Until R-60 turned
            // the periphery cap into a config field, "the share is in [0,1]" was
            // guaranteed by the constant 0.25 in code; after it, by nobody —
            // and WaveSystem.EliteShareFor does not saturate its result, unlike
            // the GunnerShare formula beside it. A share above 1 makes the
            // chaser remainder negative, a share below 0 makes the elite debt
            // negative, and either one stops the wave exactly as the weights do
            // above. Same ReqInRange form (and same Р115 reasoning) as
            // Wave.PerPlayerCountFrac right beneath the [Range] hints.
            ReqInRange(errors, "Wave.EliteShareMiddle", cfg.Wave.EliteShareMiddle, 0f, 1f);
            ReqInRange(errors, "Wave.EliteShareOuterGrowth", cfg.Wave.EliteShareOuterGrowth, 0f, 1f);
            ReqInRange(errors, "Wave.EliteShareOuterCap", cfg.Wave.EliteShareOuterCap, 0f, 1f);

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
            // Stage 2 Task 46: ReqInRange rather than ReqPositive — ZERO is a
            // legal authoring choice here, it is the "no modelled top" reading
            // the field defaults to in every hand-built fixture, exactly like
            // MobSimConfig.AvoidMargin's own 0 spends a guarantee instead of
            // breaking a rule. A NEGATIVE height is not a quieter way of saying
            // the same thing — it is a number with no meaning — and the bounds
            // mirror ArenaConfig's own [Range(0, 20)] hint, which is never
            // enforced on a value reaching the builder from code or a test.
            ReqInRange(errors, "Arena.BarrierTop", cfg.Arena.BarrierTop, 0f, 20f);
            // Stage 3 Task 3 (spec §3.6): the pickup economy's own home
            // for its two production-wired numbers (Р72, no second home).
            // Coordinator fix-round (Ф3 review A-9/m4): this doc used to say
            // CellsOnDeath/CorpseCellFraction were "R-3's temporary
            // code-only fields, no SO source yet, Т13's job" — Т13 shipped;
            // both now live in LootSimConfig (CellsPerMob/
            // CorpseCellFraction) and are validated by ValidateLoot, not
            // here.
            ReqPositive(errors, "Arena.MaxPickups", cfg.Arena.MaxPickups);
            ReqPositive(errors, "Hero.PickupRadius", cfg.Hero.PickupRadius);
            // Stage 3 Task 4 (errata E-6 D-I8): the backpack's own home for
            // its two capacity numbers (Р72, no second home) — same
            // ReqPositive convention as every other per-match capacity
            // number above. MaxInventoryItems is validated too, not just
            // InventoryCapacity: it sizes Loot.Inventory's backing array
            // directly (`new byte[maxItems]`), so a non-positive value here
            // would either construct a permanently zero-capacity backpack
            // (0) or throw an opaque exception straight out of the array
            // allocation (negative) instead of this clean, named error.
            ReqPositive(errors, "Hero.InventoryCapacity", cfg.Hero.InventoryCapacity);
            ReqPositive(errors, "Hero.MaxInventoryItems", cfg.Hero.MaxInventoryItems);

            // Stage 3 Task 8 (spec §3.13, errata E-6 D-I8's Т8 share):
            // ExtractRadius must exceed the body it extracts, and
            // MaxContainerSlots must hold a full backpack transfer.
            if (cfg.Arena.ExtractRadius <= cfg.Hero.Radius)
            {
                errors.Add("Arena.ExtractRadius must be > Hero.Radius " +
                    $"(got ExtractRadius={cfg.Arena.ExtractRadius:F3}, " +
                    $"Hero.Radius={cfg.Hero.Radius:F3}).");
            }
            // Stage 3 Task 13 (coordinator R-95): SHAPE before VALUES, same
            // discipline as ValidateZoneArrayShapes below (Ф2 review
            // B-I2.3) — MinCatalogSlotCost is a READER that divides by
            // every item's own SlotCost, so a zero must be refused BEFORE
            // that division runs, not accumulated into `errors` for a
            // report the division never lives to reach
            // (Validate_RejectsZeroSlotCost's own RED was a bare
            // DivideByZeroException, not the named ArgumentException this
            // call now throws).
            ValidateItemSlotCosts(cfg.Items);
            // Stage 3 Task 13 (spec §3.7 Р264, owner decision R-92): the
            // catalog's remaining shape rules — duplicate id and the
            // 255-record wire cap — neither one has a reader that crashes
            // on violation, so both stay in the accumulating report (see
            // ValidateItems's own doc).
            ValidateItems(errors, cfg.Items);
            // minSlotCost now comes from the real catalog (owner decision
            // R-5) — MinCatalogSlotCost's own doc names the one case
            // (empty catalog) it still assumes rather than enforces, same
            // "silent default with a named addressee" shape MaxBodyRadius
            // below already uses for elite/director.
            int minSlotCost = MinCatalogSlotCost(cfg.Items);
            if (cfg.Arena.MaxContainerSlots < cfg.Hero.InventoryCapacity / minSlotCost)
            {
                errors.Add("Arena.MaxContainerSlots must be >= Hero.InventoryCapacity / " +
                    "min(SlotCost) " +
                    $"(got MaxContainerSlots={cfg.Arena.MaxContainerSlots}, " +
                    $"InventoryCapacity={cfg.Hero.InventoryCapacity}, min(SlotCost)={minSlotCost}).");
            }
            // Stage 3 Task 13 (coordinator R-96 finding): cfg.Loot carried
            // NO rule at all before this — ValidateLoot's own doc has the
            // full account of why exactly one of its three array fields
            // earns one. Accumulating, not eager: unlike ValidateItemSlotCosts
            // above, nothing later IN THIS METHOD reads Loot.CellsPerMob —
            // its one reader (LootDrops.MobDeathCells) only runs during a
            // live match, long after Validate returns.
            ValidateLoot(errors, in cfg.Loot, in cfg.Arena);

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

            // Ф2 fix-round (review B-I2.3): SHAPE before VALUES. This throws on
            // the spot rather than adding to `errors`, and it must run before
            // ValidateWalls too — that method reaches the zone arrays through
            // RingSlotBlocked's arc loop.
            ValidateZoneArrayShapes(in cfg);
            ValidateWalls(errors, in cfg, spawnClearance);
            ValidateZoneWalls(errors, in cfg, spawnClearance);

            // Stage 2 Task 22 (spec §3.15/Р72; carryover-t22 §1): server-side
            // visibility filter invariants. Upper bounds mirror
            // VisibilityConfig's own [Range] Inspector hints via ReqInRange —
            // same Р115 precedent as Hero.EdgeRequestMinTicks/Arena.MaxPlayers
            // above ([Range] is an Editor-only hint, never enforced on a value
            // reaching the builder from code/JSON/a test fixture).
            // NetConfig's LingerTicks >= InterpBufferTicks + 2 cross-check is
            // NOT here — NetConfig is not part of SimConfig (Р72); it lands in
            // Task 41's NetInvariants instead.
            ReqInRange(errors, "Visibility.SightRadius", cfg.Visibility.SightRadius, 0f, 150f,
                minExclusive: true);
            if (cfg.Visibility.HearRadius < cfg.Visibility.SightRadius)
            {
                errors.Add("Visibility.HearRadius must be >= Visibility.SightRadius " +
                    $"(got HearRadius={cfg.Visibility.HearRadius:F3}, " +
                    $"SightRadius={cfg.Visibility.SightRadius:F3}).");
            }
            ReqInRange(errors, "Visibility.HearRadius", cfg.Visibility.HearRadius, 0f, 200f);
            ReqInRange(errors, "Visibility.ExitHysteresis", cfg.Visibility.ExitHysteresis, 0f, 20f);
            ReqInRange(errors, "Visibility.HearPositionGridMeters",
                cfg.Visibility.HearPositionGridMeters, 0f, 10f);
            ReqInRange(errors, "Visibility.LingerTicks", cfg.Visibility.LingerTicks, 0, 30);
            // Stage 3 Task 13: a non-positive radius here means
            // VisibilitySystem's future distance check (Т26) can never find
            // a pickup/container visible — same consequence class Hero.
            // PickupRadius's own ReqPositive already states for the auto-
            // pickup radius right below.
            ReqPositive(errors, "Visibility.PickupRadiusForVisibility", cfg.Visibility.PickupRadiusForVisibility);
            ReqPositive(errors, "Visibility.ContainerRadiusForVisibility",
                cfg.Visibility.ContainerRadiusForVisibility);

            if (errors.Count > 0)
                throw new ArgumentException("SimConfig validation failed:\n- " + string.Join("\n- ", errors));
        }

        /// Stage 3 Task 8 (owner decision R-28): the single home for "the
        /// largest body that must fit through a door / clear of a zone
        /// wall" — used by ValidateWalls' own rim rule above (refactored
        /// onto this method, no behavior change) and ValidateZoneWalls
        /// below (door-width rule Р247, R-37's interior-passability rule).
        /// Stage 3 Task 10 extends it with Elite.Radius/Director.Radius —
        /// THE SAME HOME, not a second copy (rule 2), exactly what R-28
        /// named this method's own future addressee for (lesson 272). Both
        /// new terms are harmless for every fixture built through
        /// SimConfigBuilder.Build BEFORE a caller supplies a non-null
        /// `elite`/`director` argument (that method's own doc): `cfg.Elite`/
        /// `cfg.Director` stay `default(MobSimConfig)` — Radius 0f — so
        /// `math.max(..., 0f)` never changes the pre-Task-10 answer.
        /// ZoneConfigTests.Validate_RejectsDoorNarrowerThanDirector is the
        /// one fixture that DOES supply a real Director radius (2.2, spec
        /// §3.13) and is what this extension exists to satisfy.
        /// Stage 3 Task 11 (coordinator R-63): parametrized with
        /// `waveArchetypesOnly` — the door-width rule (Р247) and the
        /// zone-wall body-passability rule (R-37) need every archetype
        /// including Hero and Director; the NEW wave spawn-ring clearance
        /// rule below (R-55) needs ONLY the three archetypes a wave can
        /// actually spawn (Chaser/Gunner/Elite) — Hero has its own,
        /// separate player-spawn rule (Т8, CheckZoneWallSpawnClearance
        /// below) and Director never spawns through a wave at all (Р248/
        /// §3.4: the match-flow state machine drops it at the core's
        /// center). One home, one shared computation (rule 2) — the
        /// wave-only three-way max is reused as-is by the full five-way one
        /// below, not a second math.max chain.
        /// ⚠ ASSUMPTION THIS METHOD CANNOT ENFORCE, WITH ITS ADDRESSEE NAMED
        /// (Stage 3 Task 12, coordinator finding on mutation M8): its answer —
        /// and therefore the STRENGTH of every rule built on it — depends on
        /// whether the caller handed Build the `elite`/`director` assets at
        /// all. Both parameters are trailing and optional, so a call that
        /// omits them gets `default(MobSimConfig)`, Radius 0, and a max() that
        /// silently drops 2.2 m of Director and 0.8 m of Elite: the door-width
        /// rule falls from 5.402 to 2.002, and the wave spawn-ring band
        /// narrows from (63.2, 66.8) to (63.5, 66.5) — enough for a spawn ring
        /// at 63.5 to read as legal on one path and illegal on another.
        /// Measured, not imagined: mutation M8 moved the Core ring exactly
        /// there, and the rule fired only where the assets were supplied.
        ///
        /// Making them REQUIRED is the fix and it cannot happen here: 70+ call
        /// sites pass neither, and every one of them would start throwing on
        /// numbers that are legitimately absent today. ADDRESSEE — **Т22**,
        /// where the Director first spawns and the same task already owes
        /// ValidateMob/ValidateZones coverage for these two sections (Т12
        /// report's own debt list). Until then, a caller that means "the
        /// shipped configuration" must pass them, which is what
        /// ConfigTests.BuildShipped exists to guarantee on the test side.
        static float MaxBodyRadius(in SimConfig cfg, bool waveArchetypesOnly = false)
        {
            float waveMax = math.max(cfg.Chaser.Radius, math.max(cfg.Gunner.Radius, cfg.Elite.Radius));
            return waveArchetypesOnly ? waveMax : math.max(cfg.Hero.Radius, math.max(waveMax, cfg.Director.Radius));
        }

        /// Stage 3 Task 13 (coordinator R-95): SHAPE-before-values guard for
        /// the ONE catalog rule a live reader depends on — MinCatalogSlotCost
        /// divides InventoryCapacity by every item's own SlotCost, so a zero
        /// (or negative) entry must be refused HERE, before that division
        /// ever runs, not accumulated into a report the division doesn't
        /// live to reach. Same "own local list, throw on the spot" shape as
        /// ValidateZoneArrayShapes below (Ф2 review B-I2.3) — a rule about
        /// what makes a READER safe cannot wait for Validate's own final
        /// report, because the next reader crashes first.
        static void ValidateItemSlotCosts(ItemDef[] items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].SlotCost <= 0)
                {
                    var errors = new List<string> { $"Items[{i}].SlotCost must be > 0 " +
                        $"(got Id={items[i].Id}, SlotCost={items[i].SlotCost})." };
                    throw new ArgumentException("SimConfig validation failed:\n- "
                        + string.Join("\n- ", errors));
                }
            }
        }

        /// Stage 3 Task 13 (spec §3.7 Р264, owner decision R-92): the
        /// catalog's remaining shape rules — the ones with a NAMED
        /// consequence that no READER crashes on if left unenforced, so
        /// they can accumulate here instead of throwing on the spot
        /// (ValidateItemSlotCosts above is the one exception, R-95):
        /// - a duplicate Id means two records answer the same
        ///   ItemCatalogLookup.Find call — the SECOND one is simply
        ///   unreachable, and a hand-tuned entry the owner believes is live
        ///   silently never resolves;
        /// - a catalog past 255 records cannot round-trip the wire's own
        ///   byte Id (spec §3.7: "каталог ограничен 255 позициями").
        static void ValidateItems(List<string> errors, ItemDef[] items)
        {
            if (items == null) return;
            if (items.Length > 255)
            {
                errors.Add($"Items must have at most 255 records (wire Id is a byte) " +
                    $"(got {items.Length}).");
            }
            // Coordinator fix-round (Ф3 review C1): 0 is the container
            // slot's own "empty" sentinel (SimulationWorld.
            // TryTakeFromContainer treats a 0 byte as unset) — a catalog
            // record claiming Id 0 would be permanently unreachable through
            // the one take shim in the codebase, indistinguishable from an
            // already-emptied slot even in StateHash. Named refusal, same
            // home as the rules right below it.
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Id == 0)
                {
                    errors.Add($"Items[{i}].Id must not be 0 — 0 is reserved as the container " +
                        "slot's own \"empty\" sentinel (SimulationWorld.TryTakeFromContainer treats " +
                        "a 0 byte as unset).");
                }
            }
            for (int i = 0; i < items.Length; i++)
            {
                for (int j = i + 1; j < items.Length; j++)
                {
                    if (items[i].Id == items[j].Id)
                    {
                        errors.Add($"Items[{i}] and Items[{j}] share Id={items[i].Id} — " +
                            "every catalog entry must have a unique id.");
                    }
                    // Stage 3 Task 16 (coordinator R-124/R-130): among
                    // Kind == Trophy records, no two may share a Tier —
                    // ItemCatalogLookup.FindByTier (the "tier -> item"
                    // mapping both the archetype death-roll and the
                    // crate/cache content roll resolve through) would
                    // otherwise silently pick "whichever comes first",
                    // letting the .asset's own record order decide a game
                    // outcome (the owner's open question, R-91, about a
                    // future second tier-1 item).
                    if (items[i].Kind == ItemKind.Trophy && items[j].Kind == ItemKind.Trophy &&
                        items[i].Tier == items[j].Tier)
                    {
                        errors.Add($"Items[{i}] and Items[{j}] are both Trophy records sharing " +
                            $"Tier={items[i].Tier} — ItemCatalogLookup.FindByTier must resolve to " +
                            "exactly one record per tier.");
                    }
                }
            }
        }

        /// Stage 3 Task 13 (coordinator R-96): cfg.Loot's own shape rules —
        /// R-92 restricts this to checks with a NAMED consequence.
        /// - CellsPerMob is read by LootDrops.MobDeathCells, indexed by
        ///   MobType (Chaser/Gunner/Elite/Director — Core/SimStates.cs'
        ///   own four-value domain) with NO bounds guard of its own; a
        ///   shorter array crashes the FIRST mob death with a bare
        ///   IndexOutOfRangeException, naming nothing. Rule enforced here.
        /// - DropChance gained a live reader in Stage 3 Task 16
        ///   (LootDrops.TryRollMobItemTier, indexed by
        ///   `[archetype * 3 + zone]` with no bounds guard of its own) — its
        ///   own two shape rules (R-121a/R-121b) are enforced below,
        ///   replacing the ASSUMPTION+ADDRESSEE doc this field carried
        ///   before a reader existed.
        /// - TransferSeconds gained its live reader in Stage 3 Task 17
        ///   (Core.LootTransferTimes.ForTier, indexed by the target item's
        ///   own tier as `tier - 1` with no bounds guard of its own, called
        ///   from Loot.LootOps.Begin and from SimulationWorld.ApplyConfig's
        ///   clamp) — so the ASSUMPTION + ADDRESSEE doc it used to carry is
        ///   replaced by the rule below, exactly as DropChance's was in Т16.
        ///   R-92 in its plain form: a rule is earned by the reader it
        ///   protects, and arrives with it.
        /// Build's own omitted-`loot` branch (coordinator R-96) already
        /// guarantees CellsPerMob/DropChance are never null — these rules
        /// instead catch a SUPPLIED-but-malformed LootConfig.asset (an
        /// Inspector edit that shortens an array).
        static void ValidateLoot(List<string> errors, in LootSimConfig loot, in ArenaSimConfig arena)
        {
            if (loot.CellsPerMob == null || loot.CellsPerMob.Length != 4)
            {
                errors.Add("Loot.CellsPerMob must have exactly 4 elements (one per MobType — " +
                    "Chaser/Gunner/Elite/Director), read by LootDrops.MobDeathCells with no " +
                    $"bounds guard of its own (got {loot.CellsPerMob?.Length ?? 0}).");
            }

            // Stage 3 Task 17 (coordinator decision D-4): the same shape as
            // CellsPerMob's rule above, for the same reason and against a
            // DIFFERENT index convention — CellsPerMob is indexed DIRECTLY by
            // MobType, TransferSeconds by `tier - 1` (tiers run 1..4). Four
            // elements, one per tier; the repair kit is outside the ladder and
            // borrows tier one's time (LootTransferTimes.ForTier's own doc).
            if (loot.TransferSeconds == null || loot.TransferSeconds.Length != 4)
            {
                errors.Add("Loot.TransferSeconds must have exactly 4 elements (one per item tier " +
                    "1..4), read by Core.LootTransferTimes.ForTier as [tier - 1] with no bounds " +
                    $"guard of its own (got {loot.TransferSeconds?.Length ?? 0}).");
            }

            // Stage 3 Task 15 (coordinator R-109): same F4 shape family as
            // ValidateZoneWalls' own two rules (ZoneRadius.Length in {0,2};
            // ZoneWallCount > 0 requires Length == 2) — one more
            // independent fact that must agree with "zones exist": Loot
            // wanting a Middle/Core cache. Without this rule the
            // disagreement surfaces four stack frames deeper and much
            // later than Build() — inside SimulationWorld's OWN
            // constructor, at Geometry.ZoneSpawnRingRadius's own named
            // refusal (R-64) — the very failure mode this rule exists to
            // catch earlier, same reasoning as ValidateZoneWalls' own doc.
            if ((loot.CacheCountMiddle > 0 || loot.CacheCountCore > 0) && arena.ZoneRadius.Length != 2)
            {
                errors.Add("Loot.CacheCountMiddle/CacheCountCore > 0 requires Arena.ZoneRadius.Length == " +
                    "2 -- Loot.ContainerStore.PlaceStartingContainers calls Geometry.ZoneSpawnRingRadius" +
                    "(Zone.Middle/Core, ...), which throws a named refusal on a zoneless arena " +
                    $"(got CacheCountMiddle={loot.CacheCountMiddle}, CacheCountCore={loot.CacheCountCore}, " +
                    $"ZoneRadius.Length={arena.ZoneRadius.Length}).");
            }

            // Stage 3 Task 16 (coordinator R-121a): DropChance gained a
            // live reader this task (LootDrops.TryRollMobItemTier, no
            // bounds guard of its own) — the shape rule finally earns a
            // witness (R-92).
            if (loot.DropChance == null || loot.DropChance.Length != 12)
            {
                errors.Add("Loot.DropChance must have exactly 12 elements (4 archetypes x 3 zones), " +
                    "read by LootDrops.TryRollMobItemTier with no bounds guard of its own " +
                    $"(got {loot.DropChance?.Length ?? 0}).");
            }

            // Stage 3 Task 16 (coordinator R-121b): same F4 family as the
            // CacheCountMiddle/CacheCountCore rule above — a nonzero
            // DropChance element means SOME archetype's own death routes
            // through Geometry.ZoneOf (LootDrops.TryRollMobItemTier's own
            // row guard only skips an ALL-zero row), and ZoneOf's own
            // ZoneRadius[0]/[1] reads carry no bounds guard of their own
            // (Geometry.cs:297) — a bare IndexOutOfRangeException on a
            // zoneless arena, the same failure mode caught one stack frame
            // earlier here.
            if (loot.DropChance != null)
            {
                bool anyNonzero = false;
                for (int i = 0; i < loot.DropChance.Length; i++)
                {
                    if (loot.DropChance[i] > 0f) { anyNonzero = true; break; }
                }
                if (anyNonzero && arena.ZoneRadius.Length != 2)
                {
                    errors.Add("Loot.DropChance has a nonzero element, which requires " +
                        "Arena.ZoneRadius.Length == 2 -- LootDrops.TryRollMobItemTier calls " +
                        "Geometry.ZoneOf, which reads ZoneRadius[0]/[1] with no bounds guard of " +
                        $"its own on a zoneless arena (got ZoneRadius.Length={arena.ZoneRadius.Length}).");
                }
            }
        }

        /// Stage 3 Task 13 (owner decision R-5/R-84): the catalog's own
        /// minimum SlotCost — real per-item data as of this task, replacing
        /// the HARDCODED 1 this rule used before ItemCatalog existed.
        /// ⚠ ASSUMPTION THIS METHOD CANNOT ENFORCE, WITH ITS ADDRESSEE NAMED
        /// (same MaxBodyRadius precedent right above): an EMPTY catalog —
        /// every one of the 82 call sites that predate `items`/`loot`, or a
        /// caller that genuinely omits them — falls back to 1, the same
        /// silent default this rule has always used, rather than refusing
        /// outright. Making `items` REQUIRED is the fix and it cannot happen
        /// here, for the same reason MaxBodyRadius's own doc gives for
        /// `elite`/`director`: dozens of call sites pass neither.
        /// ADDRESSEE — Т22, same as MaxBodyRadius's own debt. Until then, a
        /// caller that means "the shipped configuration" must pass a real
        /// catalog, which is what ConfigTests.BuildShipped exists to
        /// guarantee on the test side.
        static int MinCatalogSlotCost(ItemDef[] items)
        {
            if (items == null || items.Length == 0) return 1;
            int min = items[0].SlotCost;
            for (int i = 1; i < items.Length; i++)
                if (items[i].SlotCost < min) min = items[i].SlotCost;
            return min;
        }

        /// Stage 3 Task 11: the three zones in their own declared order
        /// (Zone.Outer=0/Middle=1/Core=2) — shared by the wave spawn-ring
        /// check inside ValidateZoneWalls below, one array, not a fresh
        /// one per wall per call.
        static readonly Zone[] AllZones = { Zone.Outer, Zone.Middle, Zone.Core };


        /// Ф2 fix-round, second half of review B-I2.3. The rule itself was
        /// right and its FIRST placement was wrong: it sat inside
        /// ValidateZoneWalls, and ValidateWalls — which runs one line earlier —
        /// reaches the very same arrays through RingSlotBlocked (Ф2 review A-6
        /// gave that helper its arc loop in this same round). So a short array
        /// still crashed, one frame earlier than before, with exactly the
        /// nameless IndexOutOfRange/ArgumentOutOfRange this rule exists to
        /// replace — caught by the rule's own two tests, which were right all
        /// along.
        ///
        /// Hence: FIRST, before every consumer, and THROWING rather than
        /// accumulating. Validate collects errors and reports them together,
        /// which is the right shape for rules about VALUES; a rule about the
        /// SHAPE of the data cannot do that, because every later rule would
        /// then run against the same malformed arrays and crash before the
        /// collected report is ever built. Same discipline as R-64 in
        /// Geometry.ZoneSpawnRingRadius: the guard stands in front of the read,
        /// not behind it.
        static void ValidateZoneArrayShapes(in SimConfig cfg)
        {
            var errors = new List<string>();
            // Ф2 review B-I2.3: R-64 applied in this file's OWN house. Every
            // loop below indexes four parallel arrays by ZoneWallCount and
            // slices DoorCenterRad/DoorFreeWidth by DoorStart+DoorCount; a
            // config where those disagree does not fail a rule, it CRASHES —
            // a bare ArgumentOutOfRangeException out of the ReadOnlySpan
            // constructor, naming nothing and addressing nobody. That is the
            // exact defect R-64 named in Geometry.ZoneSpawnRingRadius and the
            // exact remedy: say which array is short, and say it first, before
            // any loop can reach it.
            if (cfg.Arena.ZoneWallRadius.Length < cfg.Arena.ZoneWallCount
                || cfg.Arena.ZoneWallHalfWidth.Length < cfg.Arena.ZoneWallCount
                || cfg.Arena.ZoneWallDoorStart.Length < cfg.Arena.ZoneWallCount
                || cfg.Arena.ZoneWallDoorCount.Length < cfg.Arena.ZoneWallCount)
            {
                errors.Add($"Arena.ZoneWallCount is {cfg.Arena.ZoneWallCount} but its parallel " +
                    $"arrays are shorter (ZoneWallRadius={cfg.Arena.ZoneWallRadius.Length}, " +
                    $"ZoneWallHalfWidth={cfg.Arena.ZoneWallHalfWidth.Length}, " +
                    $"ZoneWallDoorStart={cfg.Arena.ZoneWallDoorStart.Length}, " +
                    $"ZoneWallDoorCount={cfg.Arena.ZoneWallDoorCount.Length}).");
                throw new ArgumentException("SimConfig validation failed:\n- "
                    + string.Join("\n- ", errors));
            }
            if (cfg.Arena.DoorFreeWidth.Length != cfg.Arena.DoorCenterRad.Length)
            {
                errors.Add("Arena.DoorCenterRad and Arena.DoorFreeWidth must be the same length " +
                    $"(got DoorCenterRad={cfg.Arena.DoorCenterRad.Length}, " +
                    $"DoorFreeWidth={cfg.Arena.DoorFreeWidth.Length}).");
                throw new ArgumentException("SimConfig validation failed:\n- "
                    + string.Join("\n- ", errors));
            }
            for (int i = 0; i < cfg.Arena.ZoneWallCount; i++)
            {
                int start = cfg.Arena.ZoneWallDoorStart[i];
                int count = cfg.Arena.ZoneWallDoorCount[i];
                if (start < 0 || count < 0 || start + count > cfg.Arena.DoorCenterRad.Length)
                {
                    errors.Add($"Arena zone wall [{i}] slices the door arrays out of bounds " +
                        $"(DoorStart={start}, DoorCount={count}, " +
                        $"DoorCenterRad.Length={cfg.Arena.DoorCenterRad.Length}).");
                    throw new ArgumentException("SimConfig validation failed:\n- "
                        + string.Join("\n- ", errors));
                }
            }
        }

        /// Stage 3 Task 8 (spec §3.2's own Validate paragraph; ledger
        /// R-27/R-28/R-37): the zone-wall arc barriers and their doors.
        /// ZoneWallCount == 0 means zones are off (Stage 2 arena
        /// literally) — every loop below is then a no-op, which is what
        /// keeps every fixture before Т12 (this task's own
        /// TestConfigs.DefaultArena() included) clear of every rule here.
        static void ValidateZoneWalls(List<string> errors, in SimConfig cfg, float spawnClearance)
        {
            // Stage 3 Task 11 (coordinator F4): "zones exist" (ZoneRadius)
            // and "walls exist" (ZoneWallCount) became two INDEPENDENT
            // facts this task — StartWave routes the whole wave budget by
            // ZoneRadius.Length < 2 (R-53), the wave spawn-ring rule below
            // self-gates on the same length, and walls live entirely by
            // ZoneWallCount — nothing enforced the two agree. A config with
            // walls but no ZoneRadius passed validation before this check
            // and got, all at once: the whole wave budget routed to Outer,
            // the new wave spawn-ring rule skipped outright (its own guard
            // reads as "nothing to check"), and a crash at Geometry.ZoneOf's
            // first caller (Т13's loot-tier lookup) — the same "code
            // references a guarantee nobody gives" class as R-37.
            // ZoneRadius is a fixed "two boundaries" shape (Geometry.ZoneOf
            // reads index 0/1 directly) OR legitimately EMPTY (zoneless
            // arena, R-53) — no third length is meaningful.
            if (cfg.Arena.ZoneRadius.Length != 0 && cfg.Arena.ZoneRadius.Length != 2)
            {
                errors.Add("Arena.ZoneRadius must have exactly 0 (zoneless) or 2 (Core/Middle " +
                    $"boundary) elements (got {cfg.Arena.ZoneRadius.Length}).");
            }
            if (cfg.Arena.ZoneWallCount > 0 && cfg.Arena.ZoneRadius.Length != 2)
            {
                errors.Add("Arena.ZoneWallCount > 0 requires Arena.ZoneRadius.Length == 2 -- " +
                    $"walls imply zones (got ZoneWallCount={cfg.Arena.ZoneWallCount}, " +
                    $"ZoneRadius.Length={cfg.Arena.ZoneRadius.Length}).");
            }

            float maxBodyRadius = MaxBodyRadius(in cfg);

            // ZoneRadius is a fixed "two boundaries" shape (Geometry.ZoneOf
            // reads index 0/1 directly), but the pairwise loop below states
            // the rule generally rather than hard-coding index 0 vs 1 —
            // this file's own convention (ValidateWalls/the obstacle loop
            // above do the same for their own variable-length arrays).
            // Ф2 review B-I2.1: plan Т8 asked for "и меньше Radius" and only
            // ZoneWallRadius got it. A boundary outside the arena is not an
            // abstract worry — Geometry.ZoneSpawnRingRadius derives the wave
            // ring from it, so ZoneRadius {65, 200} on a 113 m arena puts the
            // Middle ring at 198, outside the world, where no candidate can
            // ever be valid and the zone's debt never discharges.
            for (int i = 0; i < cfg.Arena.ZoneRadius.Length; i++)
            {
                if (cfg.Arena.ZoneRadius[i] >= cfg.Arena.Radius)
                {
                    errors.Add($"Arena.ZoneRadius[{i}] must be < Arena.Radius " +
                        $"(got ZoneRadius[{i}]={cfg.Arena.ZoneRadius[i]:F3}, " +
                        $"Arena.Radius={cfg.Arena.Radius:F3}).");
                }
            }

            for (int i = 1; i < cfg.Arena.ZoneRadius.Length; i++)
            {
                if (cfg.Arena.ZoneRadius[i] <= cfg.Arena.ZoneRadius[i - 1])
                {
                    errors.Add($"Arena.ZoneRadius[{i}] must be > Arena.ZoneRadius[{i - 1}] " +
                        $"(got ZoneRadius[{i}]={cfg.Arena.ZoneRadius[i]:F3}, " +
                        $"ZoneRadius[{i - 1}]={cfg.Arena.ZoneRadius[i - 1]:F3}).");
                }
            }

            // Coordinator ledger (plan lines 790-795 + spec §3.2's own
            // Validate paragraph): the const below is this method's one
            // number for "half the ring, in radians" (the ring is a full
            // 2*pi around) — named so every reader of the sum-of-doors rule
            // sees the same quantity spelled out once, not recomputed twice.
            const float HalfRingRad = math.PI;
            float spawnClearanceNeeded = cfg.Hero.Radius + spawnClearance;

            for (int i = 0; i < cfg.Arena.ZoneWallCount; i++)
            {
                string tag = $"Arena zone wall [{i}]";

                // Plan Т8 ("…и меньше Radius") + spec §3.2 ("0 <
                // ZoneWallRadius[i] < Radius"): two independent branches —
                // R-25 non-overlapping kill sets, ledger-accepted split.
                if (cfg.Arena.ZoneWallRadius[i] <= 0f)
                {
                    errors.Add($"Arena.ZoneWallRadius[{i}] must be > 0 " +
                        $"(got {cfg.Arena.ZoneWallRadius[i]:F3}).");
                }
                if (cfg.Arena.ZoneWallRadius[i] >= cfg.Arena.Radius)
                {
                    errors.Add($"Arena.ZoneWallRadius[{i}] must be < Arena.Radius " +
                        $"(got ZoneWallRadius[{i}]={cfg.Arena.ZoneWallRadius[i]:F3}, " +
                        $"Arena.Radius={cfg.Arena.Radius:F3}).");
                }

                // Spec §3.2's own Validate paragraph: "HalfWidth > 0" — the
                // door-width formula and R-37 below both consume this field
                // but neither one validates it standing alone.
                if (cfg.Arena.ZoneWallHalfWidth[i] <= 0f)
                {
                    errors.Add($"Arena.ZoneWallHalfWidth[{i}] must be > 0 " +
                        $"(got {cfg.Arena.ZoneWallHalfWidth[i]:F3}).");
                }

                if (cfg.Arena.ZoneWallDoorCount[i] < 1)
                {
                    errors.Add($"{tag} has no door — every zone wall must have at least " +
                        "one door for its zone to stay reachable.");
                }

                // R-37 (debt from Task 7's PushOutOfArc doc): a body of the
                // largest current radius must still find room INSIDE the
                // wall's own hole, or PushOutOfArc's documented assumption
                // ("radius + halfW < ringR") is violated by config, not by
                // a bug — the hole would not exist at all.
                if (maxBodyRadius + cfg.Arena.ZoneWallHalfWidth[i] >= cfg.Arena.ZoneWallRadius[i])
                {
                    errors.Add($"{tag} leaves the ring's interior impassable " +
                        $"(maxBodyRadius+HalfWidth=" +
                        $"{maxBodyRadius + cfg.Arena.ZoneWallHalfWidth[i]:F3} >= " +
                        $"ZoneWallRadius={cfg.Arena.ZoneWallRadius[i]:F3}) — no body could " +
                        "ever reach it.");
                }

                // Plan Т8 ("двери не перекрываются") + spec §3.2 ("двери …
                // суммарно занимают меньше половины кольца"): both rules
                // read this wall's own door slice, by FULL angular cutout
                // (Geometry.DoorHalfCutout, ledger-mandated reuse) — not by
                // free width alone, since jamb-to-jamb overlap is a
                // geometry defect regardless of what free width remains.
                int start = cfg.Arena.ZoneWallDoorStart[i];
                int count = cfg.Arena.ZoneWallDoorCount[i];
                float totalAngularWidth = 0f;
                for (int di = 0; di < count; di++)
                {
                    int doorI = start + di;
                    float halfCutoutI = Geometry.DoorHalfCutout(cfg.Arena.DoorFreeWidth[doorI],
                        cfg.Arena.ZoneWallRadius[i], cfg.Arena.ZoneWallHalfWidth[i]);
                    totalAngularWidth += 2f * halfCutoutI;

                    for (int dj = di + 1; dj < count; dj++)
                    {
                        int doorJ = start + dj;
                        float halfCutoutJ = Geometry.DoorHalfCutout(cfg.Arena.DoorFreeWidth[doorJ],
                            cfg.Arena.ZoneWallRadius[i], cfg.Arena.ZoneWallHalfWidth[i]);
                        float delta = Geometry.WrapAngle(
                            cfg.Arena.DoorCenterRad[doorI] - cfg.Arena.DoorCenterRad[doorJ]);
                        if (math.abs(delta) < halfCutoutI + halfCutoutJ)
                        {
                            errors.Add($"{tag} doors [{doorI}] and [{doorJ}] overlap " +
                                $"(angular gap={math.abs(delta):F3} rad < sum of half-cutouts=" +
                                $"{halfCutoutI + halfCutoutJ:F3} rad).");
                        }
                    }
                }
                if (totalAngularWidth >= HalfRingRad)
                {
                    errors.Add($"{tag} doors together span {totalAngularWidth:F3} rad, " +
                        $"which is >= half the ring ({HalfRingRad:F3} rad) — a zone wall's " +
                        "doors must leave at least half the ring solid.");
                }

                // Spec §3.2's own Validate paragraph, last clause: "кольцо
                // спавна игроков … не лежат в теле дуги" — SAME form as
                // ValidateWalls' own CheckWallSpawnClearance loop just below
                // (solo center + every ring size up to MaxPlayers via
                // Geometry.SpawnPosFor), Geometry.OverlapsArc swapped in for
                // the arc shape instead of OverlapsStadium. No second policy.
                var doorCenter = new ReadOnlySpan<float>(cfg.Arena.DoorCenterRad, start, count);
                var doorFreeWidth = new ReadOnlySpan<float>(cfg.Arena.DoorFreeWidth, start, count);
                CheckZoneWallSpawnClearance(errors, tag, cfg.Arena.ZoneWallRadius[i],
                    cfg.Arena.ZoneWallHalfWidth[i], doorCenter, doorFreeWidth,
                    spawnClearanceNeeded, float2.zero, "solo center");
                for (int n = 2; n <= cfg.Arena.MaxPlayers; n++)
                {
                    for (int s = 0; s < n; s++)
                    {
                        float2 spawnPos = Geometry.SpawnPosFor(s, n, in cfg.Arena);
                        CheckZoneWallSpawnClearance(errors, tag, cfg.Arena.ZoneWallRadius[i],
                            cfg.Arena.ZoneWallHalfWidth[i], doorCenter, doorFreeWidth,
                            spawnClearanceNeeded, spawnPos, $"ring {n}/point {s}");
                    }
                }

                // Stage 3 Task 11 (spec §3.2's rule extended to wave spawn
                // rings, coordinator R-55/R-63): a zone's WAVE spawn ring is
                // a full circle drawn at an ARBITRARY angle
                // (WaveSystem.TryFindSpawnPos), unlike the discrete player
                // spawn points just above — no door can ever save it, so
                // this uses Geometry.InArcBand alone (radial-only, no door
                // exception), not OverlapsArc. Threshold is
                // halfW + max(Chaser,Gunner,Elite).Radius — NO SpawnClearance
                // term (R-63's own arithmetic against the §3.15 starting
                // layout: R=65/92, HalfWidth=1.0, SpawnRingInset=2 give a
                // spawn ring exactly 2 m short of its own wall on both the
                // core and middle rings; 1 + 0.8 = 1.8 clears with 0.2 m to
                // spare, while adding SpawnClearance, 1.0, would raise the
                // threshold to 2.8 and fail BOTH zones on delivery day).
                // Director is excluded (never spawns through a wave, Р248/
                // §3.4) and Hero is excluded (that is the check right above
                // this one, a different policy, Т8) — MaxBodyRadius's
                // `waveArchetypesOnly` flag carries exactly that subset.
                // Checked once per zone, against every wall (not just the
                // wall nominally "at" that zone's boundary) — same
                // "check the ring against every wall" breadth the player
                // spawn-ring loop right above already uses.
                // ⚠ This rule is only as strong as the bodies it is given —
                // see MaxBodyRadius's own doc: without the Elite asset the
                // band it draws is 0.3 m narrower on each side, and a spawn
                // ring parked in that 0.3 m reads as legal. Addressee of the
                // fix is Т22, named there.
                if (cfg.Arena.ZoneRadius.Length >= 2)
                {
                    float maxWaveBodyRadius = MaxBodyRadius(in cfg, waveArchetypesOnly: true);
                    foreach (Zone waveZone in AllZones)
                    {
                        float ringRadius = Geometry.ZoneSpawnRingRadius(waveZone, in cfg.Arena,
                            cfg.Wave.SpawnRingInset);
                        // Ф2 review B-I2 (adjacent finding): SpawnRingInset has
                        // no upper bound of its own, and it is subtracted from
                        // EVERY zone boundary — so an inset chosen against the
                        // arena's own radius quietly drives the inner zones'
                        // rings negative. Observed, not hypothesised:
                        // WaveScalingTests' own arc fixture ran at inset 93 with
                        // rings of -1 and -28 m, and every rule below read that
                        // geometry as "outside the band" and said nothing. A
                        // ring is a circle; a circle of negative radius is not a
                        // stricter case, it is a meaningless one.
                        if (ringRadius <= 0f)
                        {
                            errors.Add($"Wave.SpawnRingInset ({cfg.Wave.SpawnRingInset:F3}) leaves " +
                                $"the {waveZone} zone's wave spawn ring at radius " +
                                $"{ringRadius:F3} — a spawn ring must have positive radius.");
                            continue;
                        }
                        if (Geometry.InArcBand(new float2(ringRadius, 0f), maxWaveBodyRadius,
                                cfg.Arena.ZoneWallRadius[i], cfg.Arena.ZoneWallHalfWidth[i]))
                        {
                            errors.Add($"{tag} covers the {waveZone} zone's wave spawn ring " +
                                $"(radius={ringRadius:F3}) — no door can save a full ring drawn " +
                                "at an arbitrary angle.");
                        }
                    }
                }
            }

            // Ledger R-27: DoorFreeWidth >= 2*(bodyRadius+Skin)+DoorClearance
            // — one flat loop over the shared door arrays (not per-wall),
            // since the rule itself does not depend on which wall a door
            // belongs to.
            float requiredDoorWidth = 2f * (maxBodyRadius + Geometry.Skin) + cfg.Arena.DoorClearance;
            for (int i = 0; i < cfg.Arena.DoorFreeWidth.Length; i++)
            {
                if (cfg.Arena.DoorFreeWidth[i] < requiredDoorWidth)
                {
                    errors.Add($"Arena.DoorFreeWidth[{i}] must be >= " +
                        "2*(bodyRadius+Skin)+DoorClearance " +
                        $"(got DoorFreeWidth[{i}]={cfg.Arena.DoorFreeWidth[i]:F3}, " +
                        $"required={requiredDoorWidth:F3}, bodyRadius={maxBodyRadius:F3}).");
                }
            }

            // Ledger note (Task 7): Geometry.OverlapsArc is the primitive —
            // no new arithmetic here, same discipline as ValidateWalls' own
            // reuse of Geometry.OverlapsStadium above.
            for (int p = 0; p < cfg.Arena.ExtractPos.Length; p++)
            {
                for (int w = 0; w < cfg.Arena.ZoneWallCount; w++)
                {
                    int start = cfg.Arena.ZoneWallDoorStart[w];
                    int count = cfg.Arena.ZoneWallDoorCount[w];
                    var doorCenter = new ReadOnlySpan<float>(cfg.Arena.DoorCenterRad, start, count);
                    var doorFreeWidth = new ReadOnlySpan<float>(cfg.Arena.DoorFreeWidth, start, count);
                    if (Geometry.OverlapsArc(cfg.Arena.ExtractPos[p], cfg.Arena.ExtractRadius,
                            cfg.Arena.ZoneWallRadius[w], cfg.Arena.ZoneWallHalfWidth[w],
                            doorCenter, doorFreeWidth))
                    {
                        errors.Add($"Arena.ExtractPos[{p}] lies inside zone wall [{w}]'s arc " +
                            "body — a portal must not overlap the wall it sits next to.");
                    }
                }

                // Ф2 review A-2: spec §3.13 reads "порталы не в теле дуги И НЕ
                // В СТЕНЕ", and only the first half shipped in Т8. The interior
                // walls and the obstacle circles are exactly the geometry an
                // owner moves at milestone В1, so "the layout happens to be
                // clear today" is not the guarantee the rule is for. Same two
                // primitives the checks above and below already use — no fresh
                // arithmetic (the ledger note on Geometry.OverlapsArc).
                for (int o = 0; o < cfg.Arena.ObstacleCount; o++)
                {
                    if (Geometry.CircleOverlap(cfg.Arena.ExtractPos[p], cfg.Arena.ExtractRadius,
                            cfg.Arena.ObstaclePos[o], cfg.Arena.ObstacleRadius[o]))
                    {
                        errors.Add($"Arena.ExtractPos[{p}] overlaps Arena.Obstacles[{o}] — " +
                            "a portal must stand clear of the barriers around it.");
                    }
                }
                for (int wi = 0; wi < cfg.Arena.WallCount; wi++)
                {
                    if (Geometry.OverlapsStadium(cfg.Arena.ExtractPos[p], cfg.Arena.ExtractRadius,
                            cfg.Arena.WallA[wi], cfg.Arena.WallB[wi], cfg.Arena.WallHalfWidth[wi]))
                    {
                        errors.Add($"Arena.ExtractPos[{p}] overlaps Arena.Walls[{wi}] — " +
                            "a portal must stand clear of the barriers around it.");
                    }
                }

                // Stage 3 Task 12 (owner decision R-79): ExtractZone[p] and
                // ExtractPos[p] state the same fact — which zone this exit
                // stands in — and Geometry.ZoneOf is the only arbiter of it.
                // Т21/Т23 gate portal availability off the declared BYTE, so
                // a disagreement would open a "middle" portal standing in the
                // outer band with nothing anywhere to notice. Self-gated on
                // zones existing at all: ZoneOf reads ZoneRadius[0]/[1]
                // directly, and a zoneless arena (R-53) has no zone to name.
                if (cfg.Arena.ZoneRadius.Length == 2 && p < cfg.Arena.ExtractZone.Length)
                {
                    float2 extractPos = cfg.Arena.ExtractPos[p];
                    Zone actual = Geometry.ZoneOf(extractPos, in cfg.Arena);
                    if (cfg.Arena.ExtractZone[p] != (byte)actual)
                    {
                        errors.Add($"Arena.ExtractZone[{p}] says {(Zone)cfg.Arena.ExtractZone[p]} " +
                            $"but Arena.ExtractPos[{p}] ({extractPos.x:F3}, {extractPos.y:F3}) " +
                            $"lies in {actual} — the declared zone must match the geometry.");
                    }
                }
            }

            // Stage 3 Task 12: the three portal arrays are parallel, same
            // convention as ObstaclePos/ObstacleRadius — a short one would
            // make the rule above silently skip entries instead of failing.
            if (cfg.Arena.ExtractZone.Length != cfg.Arena.ExtractPos.Length
                || cfg.Arena.ExtractKind.Length != cfg.Arena.ExtractPos.Length)
            {
                errors.Add("Arena.ExtractZone and Arena.ExtractKind must be the same length as " +
                    $"Arena.ExtractPos (got ExtractPos={cfg.Arena.ExtractPos.Length}, " +
                    $"ExtractZone={cfg.Arena.ExtractZone.Length}, " +
                    $"ExtractKind={cfg.Arena.ExtractKind.Length}).");
            }
        }

        /// One zone-wall-vs-one-candidate-spawn-point check — mirrors
        /// CheckWallSpawnClearance's exact form (same message shape, same
        /// caller pattern), Geometry.OverlapsArc swapped in for the arc
        /// shape (Stage 3 Task 8, coordinator ledger: "reuse the existing
        /// form and home, do not stand up a second policy").
        static void CheckZoneWallSpawnClearance(List<string> errors, string tag, float ringR,
            float halfW, ReadOnlySpan<float> doorCenter, ReadOnlySpan<float> doorFreeWidth,
            float clearanceNeeded, float2 spawnPos, string pointTag)
        {
            if (Geometry.OverlapsArc(spawnPos, clearanceNeeded, ringR, halfW,
                    doorCenter, doorFreeWidth))
            {
                errors.Add($"{tag} covers the player spawn point ({pointTag}) " +
                    $"(needs Hero.Radius+SpawnClearance={clearanceNeeded:F3} of clearance).");
            }
        }

        /// Stage 2 Task 16 (spec §3.3/§3.15, carryover-t16 items 7b/7c): the
        /// interior-wall rules. Before this task the builder carried NO wall
        /// check at all, while Task 11/12 docstrings already referred to one.
        ///
        /// The "inside the arena" rule is deliberately STRONGER than spec §3.3's
        /// original wording ("inside Radius - HalfWidth"). A wall whose end sits
        /// that close to the rim leaves a pocket where the two depenetration
        /// steps fight forever: PushOutOfStadium shoves a body outward, then
        /// ClampInsideRing pulls it back in, and at `iterations: 1` (what all
        /// four Depenetrate callers pass) the pair never converges; SweepArena
        /// from such a position returns t == 0 on all three MoveWithCollisions
        /// iterations, i.e. no tangential motion either — a soft-locked player or
        /// mob. The working rule keeps a full body diameter of slack:
        ///   max(|A|,|B|) + HalfWidth + bodyRadius + Skin &lt;= Radius - bodyRadius,
        /// where bodyRadius is the LARGEST body that can end up wedged there —
        /// max(Hero, Chaser, Gunner), not the hero alone. A mob is 0.5 against the
        /// hero's 0.45, so hero-only slack leaves a ~0.1 m band in which a wall
        /// passes validation and a mob driven into it by separation still
        /// soft-locks — exactly the failure this rule exists to prevent (review
        /// of Stage 2 Task 16, I-1). The wave-spawn-ring check below already
        /// derives its own body radius the same way.
        ///
        /// Spawn coverage reuses Geometry.SpawnPosFor over the same candidate set
        /// the obstacle loop walks (solo center + every ring size up to
        /// MaxPlayers), and the "spawn ring is not locked" rule mirrors
        /// WaveSystem.TryFindSpawnPos' RNG-free FallbackSlots grid.
        static void ValidateWalls(List<string> errors, in SimConfig cfg, float spawnClearance)
        {
            float wallBodyRadius = MaxBodyRadius(in cfg);
            float rimLimit = cfg.Arena.Radius - wallBodyRadius;
            float spawnClearanceNeeded = cfg.Hero.Radius + spawnClearance;

            for (int i = 0; i < cfg.Arena.WallCount; i++)
            {
                float2 a = cfg.Arena.WallA[i];
                float2 b = cfg.Arena.WallB[i];
                float halfWidth = cfg.Arena.WallHalfWidth[i];
                string tag = $"Arena.Walls[{i}]";

                ReqFinite(errors, $"{tag}.A.x", a.x);
                ReqFinite(errors, $"{tag}.A.y", a.y);
                ReqFinite(errors, $"{tag}.B.x", b.x);
                ReqFinite(errors, $"{tag}.B.y", b.y);
                ReqPositive(errors, $"{tag}.HalfWidth", halfWidth);

                // Fixwave Ф3 item 5: mirrors Geometry's OWN degenerate-axis
                // threshold (ClosestPointOnSegment/SegmentStadium's
                // `math.lengthsq(axis) < 1e-12f`) instead of a bare
                // `length <= 0f`. A wall shorter than that (e.g. 1e-7 m) used
                // to clear this check yet still trip Geometry's degenerate
                // branch at runtime everywhere it consumes the wall:
                // SegmentStadium/SweepArena fall back to treating it as a
                // CIRCLE, while ClosestPointOnSegment (PushOutOfStadium,
                // SteerAround's wall waypoint) collapses its projected axis
                // to `dir` instead of the wall's own direction — a silent
                // mismatch between "validated as a wall" and "behaves like
                // one" this rule exists to reject outright.
                if (math.lengthsq(b - a) < 1e-12f)
                {
                    errors.Add($"{tag} has zero length (|A-B| below Geometry's own 1e-6 " +
                        "degenerate-axis threshold) — a wall must be a real segment, not a " +
                        $"point (A=({a.x:F3}, {a.y:F3})).");
                }

                float farEnd = math.max(math.length(a), math.length(b));
                float reach = farEnd + halfWidth + wallBodyRadius + Geometry.Skin;
                if (reach > rimLimit)
                {
                    errors.Add($"{tag} sits too close to the arena rim " +
                        $"(max(|A|,|B|)+HalfWidth+bodyRadius+Skin={reach:F3} > " +
                        $"Arena.Radius-bodyRadius={rimLimit:F3}, bodyRadius=" +
                        $"{wallBodyRadius:F3}) — a body wedged between the " +
                        "wall and the ring can neither be pushed out nor slide along it.");
                }

                CheckWallSpawnClearance(errors, tag, a, b, halfWidth, spawnClearanceNeeded,
                    float2.zero, "solo center");
                for (int n = 2; n <= cfg.Arena.MaxPlayers; n++)
                {
                    for (int s = 0; s < n; s++)
                    {
                        float2 spawnPos = Geometry.SpawnPosFor(s, n, in cfg.Arena);
                        CheckWallSpawnClearance(errors, tag, a, b, halfWidth, spawnClearanceNeeded,
                            spawnPos, $"ring {n}/point {s}");
                    }
                }
            }

            float ringRadius = cfg.Arena.Radius - cfg.Wave.SpawnRingInset;
            int slots = cfg.Wave.FallbackSlots;
            if (ringRadius <= 0f || slots <= 0) return;

            float bodyRadius = math.max(cfg.Chaser.Radius, cfg.Gunner.Radius);
            // Coordinator R-115: the slot ANGLE formula is the one shared
            // SpawnPlacement.FallbackSlotPos home now — this loop only still
            // owns "which one of the fallbackSlots angles" (the search
            // aspect, RingSlotBlocked, stays a private mirror per that
            // method's own doc: it depends on world-less builder state).
            for (int i = 0; i < slots; i++)
            {
                float2 candidate = SpawnPlacement.FallbackSlotPos(ringRadius, i, slots);
                if (!RingSlotBlocked(in cfg.Arena, candidate, bodyRadius)) return; // a free slot exists
            }

            errors.Add("Arena geometry locks the whole wave spawn ring: none of the " +
                $"{slots} fallback slots at radius {ringRadius:F3} can hold a mob of radius " +
                $"{bodyRadius:F3} — no wave could ever spawn.");
        }

        /// One wall-vs-one-candidate-spawn-point check, factored out for the same
        /// reason CheckSpawnClearance above is: the loop runs it once per
        /// candidate point without repeating the message.
        static void CheckWallSpawnClearance(List<string> errors, string tag, float2 a, float2 b,
            float halfWidth, float clearanceNeeded, float2 spawnPos, string pointTag)
        {
            if (Geometry.OverlapsStadium(spawnPos, clearanceNeeded, a, b, halfWidth))
            {
                errors.Add($"{tag} covers the player spawn point ({pointTag}) " +
                    $"(needs Hero.Radius+SpawnClearance={clearanceNeeded:F3} of clearance).");
            }
        }

        /// Mirrors WaveSystem.IsValidSpawn's geometry half (circles then walls) —
        /// the player-distance and live-mob halves depend on world state the
        /// builder has none of.
        ///
        /// Stage 3 Task 15 (coordinator R-102/R-111): pure delegation to
        /// Ring.Simulation.Core.SpawnPlacement.GeometryBlocked
        /// (doorsPassable: true — same door policy as WaveSystem.IsValidSpawn:
        /// a spawn candidate inside a door cutout is forgiven, correct for a
        /// mob) — the three loops below (obstacles, walls, zone-wall arcs
        /// with the Ф2 review A-6 fix already folded in) were already a
        /// byte-for-byte copy of that method's own geometry half; this is
        /// the second of the three copies R-111's own ledger names collapsing
        /// onto the one shared home, not a rewrite (no arithmetic line
        /// changed).
        static bool RingSlotBlocked(in ArenaSimConfig arena, float2 pos, float bodyRadius)
            => SpawnPlacement.GeometryBlocked(in arena, pos, bodyRadius, doorsPassable: true);

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
