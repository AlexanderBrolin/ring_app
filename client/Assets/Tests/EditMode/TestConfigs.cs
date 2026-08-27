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
                    SlideProfileTop = 0.55f, MuzzleHeight = 1.0f, SlideMuzzleHeight = 0.45f,
                    // app-88jb Т13: mirrors HeroConfig's own C# default, which
                    // moved 3.8 -> 4.9 in this task so validation rule 14 can
                    // reach the Director's crown (HeroConfig.MaxAimHeight's own
                    // comment carries the ordering argument).
                    MaxAimHeight = 4.9f,
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
                    EdgeRequestMinTicks = 3,
                    // Stage 3 Task 3: mirrors HeroConfig's C# default (two-sources-
                    // of-numbers discipline — test/code-default side).
                    PickupRadius = 2f,
                    // Stage 3 Task 4: mirrors HeroConfig's C# defaults (two-sources-
                    // of-numbers discipline — test/code-default side).
                    InventoryCapacity = 8, MaxInventoryItems = 16,
                    // app-88jb Т1 (spec §3.2): mirrors HeroConfig's C# defaults
                    // (two-sources-of-numbers discipline — test/code-default side).
                    Mass = 120f, ImpactSpeedCap = 6f, CocoonDamping = 3f,
                    CenterOfMassHeight = 0.95f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    // app-88jb Т13 (spec §3.3): the collector's hit parts —
                    // mirrors HeroConfig's C# default array (two-sources-of-
                    // numbers discipline — test/code-default side). His scale
                    // factor is 1.0, so his heights are the only ones that did
                    // not move; Parts[0].Top is SlideProfileTop, which
                    // validation rule 5 requires.
                    Parts = new[]
                    {
                        new HitPart { Radius = 0.32f, Bottom = 0f, Top = 0.55f,
                            Zone = HitZone.Legs, DamageMult = 0.75f },
                        new HitPart { Radius = 0.45f, Bottom = 0.55f, Top = 1.35f,
                            Zone = HitZone.Body, DamageMult = 1.0f },
                        new HitPart { Radius = 0.16f, Bottom = 1.35f, Top = 1.75f,
                            Zone = HitZone.Head, DamageMult = 1.7f },
                    } },
                Weapon = new WeaponSimConfig { FireInterval = 0.12f, ProjectileSpeed = 35f,
                    ProjectileRadius = 0.12f, ProjectileLifetime = 1.5f, Damage = 12f,
                    SpreadRad = 0.026f, RecoilPerShotRad = 0.006f,
                    // recovery MUST be below RecoilPerShotRad / FireInterval (0.05),
                    // otherwise recoil never accumulates and the cone is dead
                    RecoilRecoveryRadPerSec = 0.03f, RecoilMaxRad = 0.07f,
                    MuzzleOffset = 0.6f, CanFireWhileDash = false,
                    CanFireWhileSlide = true, SpreadRunMult = 1.5f, SpreadSlideMult = 2.0f,
                    RunSpreadSpeedFrac = 0.5f,
                    // Stage 3 Task 2: ShotsPerCell/AmmoMax/EmergencyFireInterval
                    // mirror WeaponConfig's C# defaults like every field above
                    // (two-sources-of-numbers discipline — test/code-default
                    // side). AmmoStart does NOT (errata E-4/A-C3) — this is
                    // test numbers, not a mirror of the C# default (400, not
                    // WeaponConfig's real starting magazine of 120), same
                    // deliberate-mirror-break category as DefaultArena()'s own
                    // BarrierTop below. The solo/multiplayer golden scenarios
                    // (DeterminismTests) hold the trigger at p = 0.7/tick and
                    // fire 245-277 rounds over 1000 ticks; at the real 120 the
                    // magazine would run dry mid-replay, the emergency interval
                    // would cut in, and the golden hash would shift outside
                    // either sanctioned re-pin (Т6/Т12). 400 is comfortably
                    // above the 245-277 range.
                    ShotsPerCell = 10, AmmoStart = 400, AmmoMax = 400,
                    EmergencyFireInterval = 1.25f,
                    // app-88jb Т1 (spec §3.2): mirrors WeaponConfig's C# default.
                    ProjectileMass = 2.6f,
                    // app-88jb Т19 (spec §3.4). Retention and the speed floor
                    // MIRROR WeaponConfig's C# defaults field for field, like
                    // every line above.
                    //
                    // ⚠ MaxRicochets DOES NOT, AND THE SPEC IS WHAT SAYS SO
                    // (§4.3, spec line 1633, R-173/351/355): the fixture
                    // numbers are the MODEST ones, and that line names this
                    // field by name -- an 18 000-tick extraction golden must
                    // not turn into a load test of ricochets. The GAME number
                    // is 2 (spec's own starting-
                    // numbers table, line 297) and WeaponConfig.cs carries it;
                    // the fixture deliberately carries ONE. Same
                    // documented-deviation category as AmmoStart above and
                    // ArenaConfig.BarrierTop below — and, like those two, it is
                    // guarded rather than merely written down:
                    // ConfigTests.Build_DefaultAssets_MatchesTestConfigsBaseline
                    // asserts the divergence itself, so the day the two sources
                    // agree again, something moved and this reason is stale.
                    //
                    // Not zero, either. A fixture that silently disabled the
                    // mechanic would leave the golden scenarios never
                    // exercising it, and Т34's coverage guard is written
                    // against exactly that loss. A test whose SUBJECT is "the
                    // round dies on a barrier" states MaxRicochets = 0 in its
                    // own fixture instead, and three of them now do
                    // (coordinator Ruling 94).
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f },
                Chaser = new MobSimConfig { MaxSpeed = 5.2f, Accel = 30f, Radius = 0.5f,
                    MaxHp = 30f, ContactDamage = 15f, AttackRange = 1.1f,
                    TelegraphSeconds = 0.35f, AttackCooldown = 0.9f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f,
                    // app-88jb Т1 (spec §3.2): impact physics — chaser numbers,
                    // mirrors MobConfig's C# defaults (two-sources-of-numbers
                    // discipline — test/code-default side).
                    Mass = 90f, ImpactSpeedCap = 6f, ProjectileMass = 3.0f,
                    CenterOfMassHeight = 1.17f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    TiltFallAngle = 0.9f, DownedSeconds = 1.2f,
                    // app-88jb Т19 (spec §3.4): Retention and the speed floor
                    // mirror MobConfig's C# defaults; MaxRicochets is the
                    // fixture's MODEST 1 against the game's 2, for the reason
                    // the Weapon block above states in full (spec line 1633,
                    // R-173). Same three numbers on every archetype because
                    // MobConfig is one class behind four assets. Only the
                    // gunner's are ever read today (Impact.RicochetNumbersFor
                    // answers cfg.Gunner for every mob-owned round, the same
                    // way ProjectileMass above is read) — the other three carry
                    // them for the same reason they carry ProjectileMass.
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f,
                    // app-88jb Т13 (spec §3.3): the chaser's hit parts —
                    // mirrors MobConfig's C# default array (two-sources
                    // discipline). Heights are the old column x 1.46, the
                    // measured ratio of this model's crown (2.6996) to it.
                    Parts = new[]
                    {
                        new HitPart { Radius = 0.35f, Bottom = 0f, Top = 0.88f,
                            Zone = HitZone.Legs, DamageMult = 0.75f },
                        new HitPart { Radius = 0.50f, Bottom = 0.88f, Top = 2.12f,
                            Zone = HitZone.Body, DamageMult = 1.0f },
                        new HitPart { Radius = 0.17f, Bottom = 2.12f, Top = 2.70f,
                            Zone = HitZone.Head, DamageMult = 1.7f },
                    } },
                // Gunner's Parts already carry the taller ranged-mech tower
                // (Т16 ships the same numbers into the real .asset via the marker
                // mechanism; ahead of that this baseline is the source of truth,
                // QA4). SwingLeadFactor/SwingLeadMaxMeters are melee-only (Chaser) and
                // simply keep the MobConfig class default here, unused by Gunner.
                Gunner = new MobSimConfig { MaxSpeed = 4f, Accel = 25f, Radius = 0.5f,
                    MaxHp = 20f, PreferredRange = 9f, RangeTolerance = 1.5f, StrafeSpeed = 3f,
                    FireInterval = 1.6f, ProjectileSpeed = 14f, ProjectileRadius = 0.15f,
                    ProjectileLifetime = 3f, ProjectileDamage = 8f, LeadFactor = 0.8f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f,
                    // app-88jb Т1 (spec §3.2): impact physics — gunner numbers
                    // (owner Ruling 3: differentiated per-archetype numbers
                    // live here and reach the shipped .asset only via Т11a,
                    // owner decision Р432 — not Т16, which stays part geometry
                    // and MaxAimHeight).
                    Mass = 70f, ImpactSpeedCap = 6f, ProjectileMass = 3.0f,
                    CenterOfMassHeight = 1.78f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    TiltFallAngle = 0.9f, DownedSeconds = 1.2f,
                    // app-88jb Т19 (spec §3.4): Retention and the speed floor
                    // mirror MobConfig's C# defaults; MaxRicochets is the
                    // fixture's MODEST 1 against the game's 2, for the reason
                    // the Weapon block above states in full (spec line 1633,
                    // R-173). Same three numbers on every archetype because
                    // MobConfig is one class behind four assets. Only the
                    // gunner's are ever read today (Impact.RicochetNumbersFor
                    // answers cfg.Gunner for every mob-owned round, the same
                    // way ProjectileMass above is read) — the other three carry
                    // them for the same reason they carry ProjectileMass.
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f,
                    // app-88jb Т13 (spec §3.3): the gunner's hit parts.
                    // Heights are the old zone column x 1.20 (crown 4.2063
                    // against 3.50). The shipped .asset gets them through the
                    // marker mechanism in Т16, exactly as that column itself
                    // did in Task 17 — ahead of that, this is the source of
                    // truth (same note as the tower above).
                    Parts = new[]
                    {
                        new HitPart { Radius = 0.35f, Bottom = 0f, Top = 1.32f,
                            Zone = HitZone.Legs, DamageMult = 0.75f },
                        new HitPart { Radius = 0.50f, Bottom = 1.32f, Top = 3.24f,
                            Zone = HitZone.Body, DamageMult = 1.0f },
                        new HitPart { Radius = 0.17f, Bottom = 3.24f, Top = 4.20f,
                            Zone = HitZone.Head, DamageMult = 1.7f },
                    } },
                Wave = new WaveSimConfig { FirstWaveDelay = 2.5f,
                    SpawnRingInset = 2f, MinSpawnDistanceToPlayer = 8f,
                    // Task Т6 (app-ggvz, owner decision К5/spec Р325):
                    // BaseCount is a DELIBERATE MIRROR BREAK, same category as
                    // WeaponConfig.AmmoStart above and DefaultArena's own
                    // BarrierTop. WaveConfig's C# default moved to 16 (the
                    // game's starting wave, x4 by owner decision); the fixture
                    // deliberately stays at 4, because the golden scenarios in
                    // DeterminismTests run 3000-18000 ticks and a fixture wave
                    // of round(16 * 2.4) = 38 per ring would turn a
                    // determinism check into a load test. The divergence is
                    // pinned in triple form by
                    // ConfigTests.Build_DefaultAssets_MatchesTestConfigsBaseline,
                    // NOT by AssertWaveEqual, which excludes the field.
                    BaseCount = 4,
                    CountGrowth = 2, MaxMobsPerWave = 72, MaxSpawnAttempts = 16,
                    FallbackSlots = 24, GunnerShareBase = 0.2f, GunnerShareGrowth = 0.05f,
                    // Stage 2 Task 16: mirrors WaveConfig's C# default
                    // (two-sources-of-numbers discipline — test/code-default side).
                    PerPlayerCountFrac = 0.7f,
                    // Stage 3 Task 11: mirrors WaveConfig's C# defaults (two-
                    // sources-of-numbers discipline — test/code-default side).
                    //
                    // ⚠ AMENDED IN Т6 (app-ggvz, decision Р311): the "mirrors"
                    // claim above no longer covers EliteShareOuterGrowth. The
                    // game's C# default moved to 0.007f so the outer ring's
                    // elite share saturates on minute 12 rather than minute
                    // 4.5 (ADR-001 §3.1); the fixture stays at 0.02f, and
                    // NOTE THE DIRECTION -- here the fixture is the LARGER of
                    // the two, so this is not a "scaled-down fixture" case.
                    // EliteShareMiddle and EliteShareOuterCap still mirror.
                    // The break is pinned in triple form by
                    // ConfigTests.Build_DefaultAssets_MatchesTestConfigsBaseline.
                    //
                    // ⚠ REWRITTEN IN Т12 (Ф2 review B-I4): the Т11 text here
                    // said this arena was ZONELESS and that
                    // EliteShareOuterGrowth/Cap were "exactly what moves both
                    // golden scenarios". Both halves are false since Т12, and
                    // the second half was never true: DefaultArena() below
                    // ships zone boundaries, so EliteShareMiddle — not the
                    // Outer growth — is what puts Elites into both golden
                    // runs; and Т11's own mutations M4/M12 doubled the Outer
                    // growth rate and moved NEITHER golden, because both
                    // scenarios lived at WaveIndex = 1 where the
                    // (WaveIndex - 1) factor is identically zero. Leaving it
                    // stood was worse than a stale number: DeterminismTests'
                    // pinned re-pin justification says the opposite in as many
                    // words, so the repository held two contradictory
                    // statements about its most guarded constant.
                    //
                    // ⚠ AMENDED AGAIN IN Т4 (app-ggvz): ZoneWeights stood here
                    // and is gone with the single shared wave budget it
                    // weighted (owner decision К3) — every ring now draws a
                    // WHOLE wave. WaveIndex is no longer any ring's wave
                    // ordinal either: it is the raid's DIFFICULTY STEP (Р315),
                    // so the "(WaveIndex - 1) is identically zero" sentence
                    // above holds only for the first DifficultyStepSeconds of
                    // a run, not for a whole scenario.
                    EliteShareMiddle = 0.35f, EliteShareOuterGrowth = 0.02f,
                    EliteShareOuterCap = 0.25f,
                    // Task Т2 (app-ggvz, spec §3.8 Р325): the fixture's OWN
                    // scaled-down cadence numbers, deliberately NOT a mirror
                    // of WaveConfig's real {20,30,30}s / {150,110,10} /
                    // 20s — the golden scenarios in DeterminismTests run
                    // 3000-18000 ticks, and the shipped ceilings would turn
                    // a determinism check into a load test. Set only here;
                    // the six other TestConfigs variants below are all
                    // derived from Default() and never redeclare WaveSimConfig.
                    WavePauseByZone = new[] { 2f, 3f, 3f },
                    MaxAliveByZone = new[] { 24, 16, 8 },
                    // MaxSpawnsPerZonePerTick agrees with the shipped number
                    // on purpose (2 = 2, ConfigTests.AssertWaveEqual covers
                    // it with ordinary equality) — nothing here needs to be
                    // scaled down for a smoothing cap.
                    MaxSpawnsPerZonePerTick = 2,
                    DifficultyStepSeconds = 2f },
                // Stage 3 Task 12 (spec §3.13/§3.4, owner decision R-75): the
                // third and fourth archetypes, delivered as assets of the
                // existing MobConfig class. Numbers and their sources, one
                // by one — everything the spec names verbatim is marked so,
                // everything else is derived from an already-shipped number
                // rather than invented:
                //   Elite: MaxHp 120, Radius 0.8, ContactDamage 25,
                //     MaxSpeed 4.2 — spec §3.13 verbatim. Hit-zone belts,
                //     multipliers, muzzle and the whole ranged block are the
                //     Gunner's ("по образцу ганнера", §3.13 verbatim).
                //     Accel/Telegraph/Cooldown/separation/swing-lead are the
                //     Chaser's (Р214: "усиленный чейзер"). AttackRange 1.4 is
                //     DERIVED: the Chaser reaches 1.1 - 0.5 = 0.6 m past its
                //     own hull, and 0.8 + 0.6 keeps that same reach — the
                //     field is center-to-center (MobAiSystem's Chase entry),
                //     so a wider body needs a wider number for the same
                //     fight. PreferredRange 2.5 is DERIVED for the same
                //     reason: MobAiSystem dispatches Elite/Director by
                //     distance (chaser inside AttackRange, gunner outside),
                //     so at the Gunner's own 9 the Elite would park at 9 m
                //     and never close — a Gunner with more HP, not Р214's
                //     "enhanced chaser". 2.5 puts the hold band just outside
                //     melee (1.0..4.0 with the Gunner's tolerance), so it
                //     closes, fires while closing, and finishes in melee.
                //   Director: MaxHp 2500, Radius 2.2, ContactDamage 45,
                //     MaxSpeed 3.0, TelegraphSeconds 1.1 — spec §3.13
                //     verbatim; everything else is the ELITE profile (spec
                //     §3.4: "Elite-профиль с числами Директора"), except
                //     AttackRange 2.8 by the same surface-reach derivation
                //     (2.2 + 0.6) and PreferredRange back at the Gunner's 9,
                //     because §3.4 gives the Director the ranged stance
                //     outright ("дистанционный залп на Reposition/Fire") and
                //     Р248 keeps it in the core anyway.
                // Both are В1 tuning knobs. Radius 0.8 and 2.2 are NOT free
                // knobs: 0.8 carries the wave spawn ring's 0.2 m margin and
                // 2.2 carries the door width's 0.598 m.
                // Energy-cell drop-on-death (formerly this struct's own
                // CellsOnDeath field, moved to Loot.CellsPerMob this task,
                // R-3) stays 0 for the same golden-safety reason
                // Chaser/Gunner's does (owner decision R-18) — and it
                // matters from Т12 on, because the zones that task turned on
                // route 45% of every wave into the middle zone, where
                // EliteShareMiddle 0.35 makes Elites spawn in both golden
                // scenarios.
                Elite = new MobSimConfig { MaxSpeed = 4.2f, Accel = 30f, Radius = 0.8f,
                    MaxHp = 120f, ContactDamage = 25f, AttackRange = 1.4f,
                    TelegraphSeconds = 0.35f, AttackCooldown = 0.9f,
                    PreferredRange = 2.5f, RangeTolerance = 1.5f, StrafeSpeed = 3f,
                    FireInterval = 1.6f, ProjectileSpeed = 14f, ProjectileRadius = 0.15f,
                    ProjectileLifetime = 3f, ProjectileDamage = 8f, LeadFactor = 0.8f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f,
                    // app-88jb Т1 (spec §3.2): impact physics — Elite numbers
                    // (owner Ruling 3: reach the shipped .asset only via
                    // Т11a, owner decision Р432 — not Т16).
                    Mass = 260f, ImpactSpeedCap = 6f, ProjectileMass = 3.0f,
                    CenterOfMassHeight = 1.78f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    TiltFallAngle = 0.9f, DownedSeconds = 1.2f,
                    // app-88jb Т19 (spec §3.4): Retention and the speed floor
                    // mirror MobConfig's C# defaults; MaxRicochets is the
                    // fixture's MODEST 1 against the game's 2, for the reason
                    // the Weapon block above states in full (spec line 1633,
                    // R-173). Same three numbers on every archetype because
                    // MobConfig is one class behind four assets. Only the
                    // gunner's are ever read today (Impact.RicochetNumbersFor
                    // answers cfg.Gunner for every mob-owned round, the same
                    // way ProjectileMass above is read) — the other three carry
                    // them for the same reason they carry ProjectileMass.
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f,
                    // app-88jb Т13 (spec §3.3, evidence Т12): the elite's hit
                    // parts. ⚠ HER FACTOR IS 1.0216, NOT THE GUNNER'S 1.20 the
                    // spec's own table assumed — she is the ONE archetype whose
                    // model scale was already fitted to her column (app-oxyo
                    // took EliteVisualScale 0.75 -> 1.5, crown 3.5756 against
                    // 3.50). At 1.20 her head belt would have been [3.24, 4.20]
                    // with 0.62 m of it hanging in open air above the model —
                    // a partial rerun of the very defect app-oxyo closed.
                    Parts = new[]
                    {
                        new HitPart { Radius = 0.56f, Bottom = 0f, Top = 1.12f,
                            Zone = HitZone.Legs, DamageMult = 0.75f },
                        new HitPart { Radius = 0.80f, Bottom = 1.12f, Top = 2.76f,
                            Zone = HitZone.Body, DamageMult = 1.0f },
                        new HitPart { Radius = 0.28f, Bottom = 2.76f, Top = 3.58f,
                            Zone = HitZone.Head, DamageMult = 1.7f },
                    } },
                Director = new MobSimConfig { MaxSpeed = 3.0f, Accel = 30f, Radius = 2.2f,
                    MaxHp = 2500f, ContactDamage = 45f, AttackRange = 2.8f,
                    TelegraphSeconds = 1.1f, AttackCooldown = 0.9f,
                    PreferredRange = 9f, RangeTolerance = 1.5f, StrafeSpeed = 3f,
                    FireInterval = 1.6f, ProjectileSpeed = 14f, ProjectileRadius = 0.15f,
                    ProjectileLifetime = 3f, ProjectileDamage = 8f, LeadFactor = 0.8f,
                    SeparationRadius = 1.2f, SeparationStrength = 6f, AvoidLookahead = 3f,
                    AvoidMargin = 1f,
                    MuzzleHeight = 0.95f, SwingLeadFactor = 1.0f, SwingLeadMaxMeters = 2.0f,
                    // app-88jb Т1 (spec §3.2): impact physics — Director
                    // numbers (owner Ruling 3: reach the shipped .asset only
                    // via Т11a, owner decision Р432 — not Т16).
                    Mass = 4000f, ImpactSpeedCap = 6f, ProjectileMass = 3.0f,
                    CenterOfMassHeight = 2.31f, TiltDampingRatio = 0.55f,
                    TiltSettleSeconds = 0.9f, TiltGain = 10.5f,
                    TiltFallAngle = 0.9f, DownedSeconds = 1.2f,
                    // app-88jb Т19 (spec §3.4): Retention and the speed floor
                    // mirror MobConfig's C# defaults; MaxRicochets is the
                    // fixture's MODEST 1 against the game's 2, for the reason
                    // the Weapon block above states in full (spec line 1633,
                    // R-173). Same three numbers on every archetype because
                    // MobConfig is one class behind four assets. Only the
                    // gunner's are ever read today (Impact.RicochetNumbersFor
                    // answers cfg.Gunner for every mob-owned round, the same
                    // way ProjectileMass above is read) — the other three carry
                    // them for the same reason they carry ProjectileMass.
                    MaxRicochets = 1, RicochetRetention = 0.8f, RicochetMinSpeed = 6f,
                    // app-88jb Т13 (spec §3.3): the Director's hit parts.
                    // Heights are the old column x 1.37 (crown 4.7903 against
                    // 3.50), and his 4.80 crown is what sets Hero.MaxAimHeight
                    // 4.9 (validation rule 14). ⚠ Legs stay [0, 1.51): the
                    // owner decided in bd app-50db that a slide does NOT open a
                    // passage under him, so this height is a DECISION, not a
                    // number to round to something more convenient.
                    Parts = new[]
                    {
                        new HitPart { Radius = 1.54f, Bottom = 0f, Top = 1.51f,
                            Zone = HitZone.Legs, DamageMult = 0.75f },
                        new HitPart { Radius = 2.20f, Bottom = 1.51f, Top = 3.70f,
                            Zone = HitZone.Body, DamageMult = 1.0f },
                        new HitPart { Radius = 0.77f, Bottom = 3.70f, Top = 4.80f,
                            Zone = HitZone.Head, DamageMult = 1.7f },
                    } },
                // Stage 3 Task 12 (errata E-2): mirrors MatchFlowConfig's C#
                // defaults, same two-sources discipline as every section
                // above. Inert in both golden scenarios — nothing reads Flow
                // until Т21/Т22 build the phase machine.
                Flow = new MatchFlowSimConfig { GateDelaySeconds = 90f,
                    ExtractChannelSeconds = 20f, RetinueCount = 2,
                    RetinueRespawnSeconds = 25f, DirectorReserveSlots = 3 },
                // Stage 3 Task 13 (spec §3.7, owner decision R-91): mirrors
                // Ring.Data.ItemCatalog's own C# defaults field for field —
                // same two-sources-of-numbers discipline as DefaultArena()
                // below, not five literals lifted from a shipped .asset
                // (spec §0/Р56). Five records, not six — R-91's own account
                // of why the spec's prose overstates its own table. SlotCost
                // is already different across entries (1, 2, 3, 4, 1) by
                // virtue of mirroring the real tier ladder, which is exactly
                // what R-85 requires: without at least one pair of differing
                // costs, the SlotCostOf stub-removal mutation (Т4 -> Т13)
                // could not be caught by anything.
                // Coordinator fix-round (Ф3 review C1): Id 1..5, not
                // 0..4 — 0 is reserved as the container slot's own "empty"
                // sentinel (SimulationWorld.TryTakeFromContainer); a Tier-1
                // record at Id 0 was unrecoverable through the one take
                // shim in the codebase. Mirrors ItemCatalog.cs's own shift.
                Items = new[]
                {
                    new ItemDef { Id = 1, Tier = 1, SlotCost = 1, CreditValue = 15, Kind = ItemKind.Trophy },
                    new ItemDef { Id = 2, Tier = 2, SlotCost = 2, CreditValue = 60, Kind = ItemKind.Trophy },
                    new ItemDef { Id = 3, Tier = 3, SlotCost = 3, CreditValue = 200, Kind = ItemKind.Trophy },
                    new ItemDef { Id = 4, Tier = 4, SlotCost = 4, CreditValue = 1000, Kind = ItemKind.Trophy },
                    new ItemDef { Id = 5, Tier = 0, SlotCost = 1, CreditValue = 0, Kind = ItemKind.RepairKit },
                },
                // Stage 3 Task 13 (spec §3.7/§3.8): loot-system numbers.
                // Golden-safety zeros (owner decision R-18, same discipline
                // Weapon.CorpseCellFraction/Chaser·Gunner·Elite·Director's
                // former CellsOnDeath already followed): every field that
                // can put a NEW pickup or container into a golden scenario —
                // drop chances, crate/cache counts, the repair kit's own
                // chance, and CellsPerMob — stays at exactly zero, so
                // `_nextEntityId`/`_lootRng` never move and neither golden
                // digest shifts outside its sanctioned re-pin. Every OTHER
                // field mirrors LootConfig's real C# default, same
                // two-sources discipline as Flow above — PickupTtlSeconds
                // 120 in particular is a MIRROR of the just-removed
                // SimulationWorld.PickupTtlSeconds constant (R-3): without
                // this line the existing TTL fixtures in PickupTests would
                // drift silently the moment Т13 moved the number (lesson
                // class 296).
                Loot = new LootSimConfig
                {
                    DropChance = new float[12], // 4 archetypes x 3 zones, all zero
                    CrateCount = 0, CacheCountMiddle = 0, CacheCountCore = 0,
                    RepairKitChance = 0f,
                    CellsPerMob = new[] { 0, 0, 0, 0 },
                    CorpseCellFraction = 0f,
                    RepairKitHealAmount = 40f,
                    RepairKitChannelSeconds = 2f,
                    TransferSeconds = new[] { 0.3f, 0.6f, 0.9f, 1.2f },
                    LootSpawnAttempts = 16, LootFallbackSlots = 24,
                    PickupTtlSeconds = 120f, ContainerTtlSeconds = 180f,
                    LootRadius = 3f
                },
                Arena = DefaultArena(),
                // Stage 2 Task 19: mirrors VisibilityConfig's C# defaults
                // (two-sources-of-numbers discipline — test/code-default side).
                // Stage 3 Task 13: PickupRadiusForVisibility/
                // ContainerRadiusForVisibility mirror the same SO's C#
                // defaults too — inert until Т26 wires a consumer.
                Visibility = new VisibilitySimConfig { SightRadius = 45f, HearRadius = 60f,
                    ExitHysteresis = 3f, LingerTicks = 5, HearPositionGridMeters = 3f,
                    PickupRadiusForVisibility = 0.4f, ContainerRadiusForVisibility = 0.4f }
            };
        }

        public static ArenaSimConfig DefaultArena()
        {
            return new ArenaSimConfig
            {
                // Stage 2 Task 16: the whole block below mirrors ArenaConfig's C#
                // defaults field for field — the golden scenarios run off THIS
                // struct, so an arena change that skipped it would leave the
                // golden hash pinned to the old layout while the game moved.
                // Stage 3 Task 12 (sanctioned re-pin #2): the whole block below
                // moves to the three-zone arena, field for field with
                // ArenaConfig's own C# defaults — Radius 113, twelve more
                // circles and eight more walls for the middle and outer
                // zones, the raised caps, the 0.92 spawn ring, the zone
                // walls with their doors and the four exits. THIS is the
                // mechanism of the re-pin: the golden scenarios run off this
                // struct, so the arena moving here is what moves both
                // digests.
                // bd app-3cph: the mirror follows ArenaConfig's own C#
                // defaults again — rim 113 -> 173, the two rings tripled in
                // area around an UNCHANGED core, twelve circles and eight
                // walls moved outward with their rings, the caps raised for
                // the doubled mob density, the three portals re-radiused.
                // The SO field docs carry every derivation; this side only
                // mirrors. THIS is again the mechanism of the re-pin: both
                // golden scenarios run off this struct.
                Radius = 173f, ObstacleCount = 20,
                ObstaclePos = new[] { new float2(10f, 4f), new float2(-8f, 9f),
                    new float2(2f, -12f), new float2(-13f, -6f), new float2(14f, -9f),
                    new float2(-40f, 8f), new float2(30f, 22f), new float2(-6f, -30f),
                    new float2(97f, 0f), new float2(48.5f, 84.00446f),
                    new float2(-48.5f, 84.00446f), new float2(-97f, 0f),
                    new float2(-48.5f, -84.00446f), new float2(48.5f, -84.00446f),
                    new float2(115.67271f, 97.06093f), new float2(26.22087f, 148.70597f),
                    new float2(-141.89359f, 51.64504f), new float2(-141.89359f, -51.64504f),
                    new float2(26.22087f, -148.70597f), new float2(115.67271f, -97.06093f) },
                ObstacleRadius = new[] { 2.2f, 1.8f, 2.5f, 2.0f, 1.6f, 3.0f, 2.8f, 3.2f,
                    3.0f, 2.5f, 4.0f, 3.5f, 2.5f, 3.0f,
                    3.0f, 2.5f, 4.0f, 3.5f, 2.5f, 3.0f },
                MaxMobs = 1350, MaxProjectiles = 4096, MaxEventsPerFrame = 4096,
                // Stage 3 Task 3: same mirror discipline as the three caps
                // above — ArenaConfig's own C# default.
                MaxPickups = 1200,
                // Stage 3 Task 8: same mirror discipline — ArenaConfig's own
                // C# defaults. Zone/door/portal arrays stay EMPTY (ZoneWallCount
                // 0 gives the Stage 2 arena literally, same convention as
                // WallCount == 0 for Open()) — TestConfigs gets zones in Т12,
                // not here, and both golden scenarios depend on that: neither
                // Geometry.SweepArena/Depenetrate nor any wave/loot system
                // reads these fields yet, so leaving them off changes nothing
                // observable, but shipping non-empty data now (while Radius is
                // still 65, not the real layout's 113) would be inventing a
                // geometry the spec ties to the Т12 re-pin, not this task.
                // ExtractRadius/MaxContainers/MaxContainerSlots/DoorClearance
                // are independent of the zone layout (only Hero.Radius/
                // InventoryCapacity bound them), so they mirror the SO's real
                // numbers like every other scalar in this method.
                ZoneRadius = new[] { 65f, 130f },
                ZoneWallCount = 2,
                ZoneWallRadius = new[] { 65f, 130f },
                ZoneWallHalfWidth = new[] { 1f, 1f },
                ZoneWallDoorStart = new[] { 0, 3 },
                ZoneWallDoorCount = new[] { 3, 3 },
                DoorCenterRad = new[] { math.radians(90f), math.radians(210f), math.radians(330f),
                    math.radians(30f), math.radians(150f), math.radians(270f) },
                DoorFreeWidth = new[] { 6f, 6f, 6f, 6f, 6f, 6f },
                DoorClearance = 1.0f,
                // Owner decisions R-65 (radius 100 -> 102) and R-72 (angle
                // 180 -> 300 deg) — ArenaConfig's own field carries the full
                // arithmetic for both.
                ExtractPos = new[] { new float2(75f, 129.90381f), new float2(75f, -129.90381f),
                    new float2(0f, 97f), float2.zero },
                ExtractZone = new byte[] { 0, 0, 1, 2 },
                ExtractKind = new byte[] { 0, 0, 0, 1 },
                ExtractRadius = 8f,
                MaxContainers = 300,
                MaxContainerSlots = 8,
                // Stage 2 Task 4: same values as ArenaConfig's C# defaults
                // (two-sources-of-numbers discipline — this is the test/code-default side).
                MaxPlayers = 3, PlayerSpawnRingFrac = 0.92f,
                // Stage 2 Task 16: interior walls, same mirror discipline.
                WallCount = 14,
                WallA = new[] { new float2(-28f, 10f), new float2(-28f, 17.6f),
                    new float2(12f, -6f), new float2(12f, -13.6f),
                    new float2(2f, 24f), new float2(-34f, -20f),
                    new float2(94f, -10f), new float2(101.6f, -10f),
                    new float2(-94f, -10f), new float2(-101.6f, -10f),
                    new float2(-10f, 148f), new float2(-10f, 155.6f),
                    new float2(-10f, -148f), new float2(-10f, -155.6f) },
                WallB = new[] { new float2(-8f, 10f), new float2(-8f, 17.6f),
                    new float2(34f, -6f), new float2(34f, -13.6f),
                    new float2(2f, 44f), new float2(-16f, -34f),
                    new float2(94f, 10f), new float2(101.6f, 10f),
                    new float2(-94f, 10f), new float2(-101.6f, 10f),
                    new float2(10f, 148f), new float2(10f, 155.6f),
                    new float2(10f, -148f), new float2(10f, -155.6f) },
                WallHalfWidth = new[] { 0.8f, 0.8f, 0.8f, 0.8f, 0.6f, 0.6f,
                    0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f },
                // Stage 2 Task 46 — THE ONE FIELD WHERE THIS MIRROR IS BROKEN
                // ON PURPOSE. ArenaConfig's own C# default is 3 m (the height
                // the game plays at); this baseline stays at 0, "no modelled
                // top", which is what every barrier did before Task 46.
                // The reason is the golden scenarios above this comment: they
                // run off THIS struct, a projectile's Height and VelZ both feed
                // StateHash, and any climbing shot that passed over a barrier
                // would change its own trajectory and with it the pinned hash.
                // Keeping the test side on the pre-Task-46 branch means the new
                // branch is not merely expected to leave the goldens alone — it
                // is never executed by them at all. Tests that need a real
                // height state it themselves, in the fixture, and say so.
                BarrierTop = 0f
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
        /// Stage 2 Task 16: "without obstacles" now also means without the
        /// interior WALLS DefaultArena() ships, for exactly the same reason it
        /// has always dropped the circles — an open-field fixture must not have
        /// its geometry silently rewritten by an arena-layout tuning pass. This
        /// keeps every Open()-based fixture at the WallCount == 0 it has had
        /// since Task 11, so the layout change moves the golden scenarios
        /// (Default()) and nothing else.
        public static SimConfig Open()
        {
            var c = Quiet();
            c.Arena.ObstacleCount = 0;
            c.Arena.ObstaclePos = System.Array.Empty<float2>();
            c.Arena.ObstacleRadius = System.Array.Empty<float>();
            c.Arena.WallCount = 0;
            c.Arena.WallA = System.Array.Empty<float2>();
            c.Arena.WallB = System.Array.Empty<float2>();
            c.Arena.WallHalfWidth = System.Array.Empty<float>();
            // Stage 3 Task 12 (owner decision R-76): "without obstacles" now
            // also means without the ZONE WALLS DefaultArena() ships, for the
            // very same reason it has always dropped the circles and (since
            // Stage 2 Task 16) the interior walls — an open-field fixture must
            // not have its geometry silently rewritten by an arena-layout
            // pass. Without this, every Open()-based movement/combat fixture
            // would suddenly be fenced in by two rings.
            //
            // ZoneRadius and the portals deliberately STAY. They are not
            // barriers: nothing collides with a zone boundary or an exit, and
            // Geometry.ZoneOf reads ZoneRadius[0]/[1] directly — zeroing them
            // would hand every Т13+ consumer a zoneless arena nobody asked
            // for, and crash ZoneSpawnRingRadius (R-53's own invariant) in the
            // bargain. ZoneGeometryTests.OpenFixture_HasNoBarrierThroughCenter
            // is what proves "open" is still literally true.
            c.Arena.ZoneWallCount = 0;
            c.Arena.ZoneWallRadius = System.Array.Empty<float>();
            c.Arena.ZoneWallHalfWidth = System.Array.Empty<float>();
            c.Arena.ZoneWallDoorStart = System.Array.Empty<int>();
            c.Arena.ZoneWallDoorCount = System.Array.Empty<int>();
            c.Arena.DoorCenterRad = System.Array.Empty<float>();
            c.Arena.DoorFreeWidth = System.Array.Empty<float>();
            return c;
        }

        /// Open() stripped down to a FEATURELESS DISC with the player standing
        /// at its center — the fixture for movement, combat, visibility and AI
        /// tests that state their geometry in absolute coordinates around the
        /// origin ("a mob 30 m to the right", "an obstacle at (12, 0)").
        ///
        /// TWO DIFFERENCES FROM Open(), BOTH SERVING ONE PURPOSE (Stage 3 Ф5-0,
        /// owner decision R-173).
        ///
        /// NO ZONE BOUNDARIES. From Т21 on, a live collector standing in the
        /// CORE is what activates the Director (Р299) — and from Т22 on that
        /// spawns the Director and his retinue on top of whatever the fixture
        /// was measuring. The owner's own rule for fixtures the core gets in
        /// the way of is a zoneless arena, which is legal by construction
        /// (R-53) and which Geometry.ZoneOf's callers must guard for anyway
        /// (lesson 315). Open() itself KEEPS its two boundaries — that is
        /// owner decision R-76, pinned by ZoneGeometryTests.
        /// OpenFixture_HasNoBarrierThroughCenter, and the loot-tier tests that
        /// read them (PickupTests.EliteInMiddle_DropsTierTwo and its family)
        /// stay on Open() for exactly that reason.
        ///
        /// NO SPAWN RING. Since Ф5-0 a solo world spawns on the ring like any
        /// other lobby size (Geometry.SpawnPosFor's own account), so a fixture
        /// that wants the player at the origin has to SAY so rather than lean
        /// on a special case that no longer exists. PlayerSpawnRingFrac = 0
        /// says it once, here, instead of 76 tests each repeating the same
        /// relocation line — the same reason Quiet() exists so that no test
        /// has to write FirstWaveDelay = 1e6f itself. It is a number of the
        /// TESTS, not a mirror of any .asset (spec §0/Р56): a real match never
        /// stacks its lobby on one point, and a multiplayer world built on
        /// this fixture must place its players itself
        /// (TestWorlds.RelocatePlayerForTest), exactly as the PvP and
        /// event-delivery fixtures already do.
        public static SimConfig OpenField()
        {
            var c = Open();
            c.Arena.ZoneRadius = System.Array.Empty<float>();
            c.Arena.PlayerSpawnRingFrac = 0f;
            return c;
        }

        /// Ф2 fix-round (review B-m6, and the sweep it prompted): shrink the
        /// arena AND the zone boundaries together.
        ///
        /// Several fixtures narrow the world to 20-35 m so a projectile or a
        /// dash can reach its far wall inside the test's own tick budget. Since
        /// Т12 the shared arena also carries ZoneRadius {65, 92}, and those
        /// fixtures kept them — leaving boundaries wider than the world they
        /// bound. Harmless while nothing reads them (Geometry.ZoneOf simply
        /// answers Core everywhere inside 20 m), and NOT harmless from Т13 on,
        /// where the loot tier is read off exactly that answer: every drop in
        /// such a fixture would silently become a Core-tier drop. The builder's
        /// own new rule (ZoneRadius[i] < Arena.Radius, Ф2 review B-I2.1) states
        /// the same invariant for configs that go through it; these fixtures
        /// construct SimulationWorld directly, so they need it stated here.
        ///
        /// The 0.5/0.8 split keeps three non-empty zones in proportion rather
        /// than inventing a layout: no fixture using this cares WHERE the
        /// boundaries are, only that they are inside the world.
        public static void ShrinkArena(ref SimConfig c, float radius)
        {
            c.Arena.Radius = radius;
            if (c.Arena.ZoneRadius.Length == 2)
                c.Arena.ZoneRadius = new[] { radius * 0.5f, radius * 0.8f };
        }

        /// OpenField() with an extended slide (Task 10 — M16): SlideDuration 0.9s
        /// (vs the 0.52s default) and a shortened StaminaRegenDelay of 0.3s so
        /// slide-adjacent stamina-regen timing tests (regen frozen for the
        /// whole slide even once the post-action delay alone would have
        /// elapsed; buffer-window regen catch-up) have enough headroom to
        /// observe the behavior deterministically instead of racing it.
        public static SimConfig RegenFixture()
        {
            var c = OpenField();
            c.Hero.SlideDuration = 0.9f;
            c.Hero.StaminaRegenDelay = 0.3f;
            return c;
        }

        /// Stage 3 Task 15 (spec §4 Р296 smoke-test fixture, coordinator §4):
        /// Default() with non-zero container counts — the cheapest fixture
        /// that actually exercises Loot.ContainerStore.PlaceStartingContainers
        /// (every other fixture in this file keeps the golden-safety zeros).
        /// Named for what it adds over Default() (containers), NOT
        /// `Extraction()` — that name belongs to Т36's own third-golden
        /// fixture (spec §4), a different scenario entirely.
        public static SimConfig Populated()
        {
            var c = Default();
            c.Loot.CrateCount = 3;
            c.Loot.CacheCountMiddle = 2;
            c.Loot.CacheCountCore = 1;
            // Stage 3 Task 16 (spec §4 Р296, coordinator R-110): non-zero
            // shares so the smoke test's own containers carry real items
            // and the archetype drop/repair-kit rolls run at least once
            // over the 1000-tick window (mobs die to friendly fire even
            // with idle input, Т5) — mirrors LootConfig's own C# defaults,
            // not fixture-only numbers (two-sources discipline).
            c.Loot.DropChance = new[]
            {
                0.10f, 0.10f, 0.00f,
                0.10f, 0.10f, 0.00f,
                0.00f, 0.35f, 0.50f,
                0.00f, 0.00f, 0.00f,
            };
            c.Loot.RepairKitChance = 0.25f;
            return c;
        }

        /// Stage 3 Т36 (plan Т36, spec §4): the THIRD golden's own fixture —
        /// the full arena the game ships, not a reduced one. Т36 is the only
        /// scenario in this file that pins the extraction LOOP end to end
        /// (Director activation, his death, the gate's delay, the walk out),
        /// so every number it runs on has to be the shipped number: three
        /// zones with their walls and doors, the real container counts, the
        /// real drop chances.
        ///
        /// IT BUILDS ON Populated() RATHER THAN RESTATING IT (rule 2). That
        /// fixture already carries the drop-chance and repair-kit shares
        /// mirrored from LootConfig's own C# defaults; the only thing it
        /// deliberately keeps cheap is the container COUNT (three crates, for
        /// a smoke test that just needs placement to run at all). This
        /// replaces exactly those three numbers with the shipped ones —
        /// Populated()'s own doc says its name belongs to a different job, and
        /// that stays true: it is the base, not the fixture.
        ///
        /// ⚠ THIS IS THE ONE FIXTURE IN THE FILE EXEMPT FROM THE OPEN-FIELD
        /// RULE (R-173/351/355). A fixture that TICKS the world is normally
        /// owed a zoneless arena, precisely so an arena-layout pass cannot
        /// rewrite its geometry underneath it. Here the zoned, fully populated
        /// arena IS the subject: a golden that pinned the extraction loop on
        /// an open field would pin a loop with no core to enter, no doors to
        /// pass and no gate to open.
        public static SimConfig Extraction()
        {
            var c = Populated();
            c.Loot.CrateCount = 24;
            c.Loot.CacheCountMiddle = 15;
            c.Loot.CacheCountCore = 2;
            return c;
        }
    }
}
