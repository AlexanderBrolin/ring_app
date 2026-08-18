using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Balance numbers for the player hero (movement, dash, HP).
    public struct HeroSimConfig
    {
        public float MaxSpeed, Accel, Friction, Radius, MaxHp,
            DashSpeed, DashDuration, DashCooldown, DashIframes, DashBufferWindow;

        /// Vertical hit-zone bounds (meters above ground) and per-zone damage
        /// multipliers for the raycast aim system (Task 4+).
        public float LegsTop, BodyTop, HeadTop,
            LegsDamageMult, BodyDamageMult, HeadDamageMult;

        /// Slide stamina-movement profile height, hero muzzle heights (standing /
        /// mid-slide), and the arena-wide aim-ray height cap.
        public float SlideProfileTop, MuzzleHeight, SlideMuzzleHeight, MaxAimHeight;

        /// Stamina pool and per-action costs/regen (Task 2 — stamina/slide/dash economy).
        /// LinkRefund (В1 fix-wave 3, owner economy rework): stamina credited back
        /// when a slide/dash executes inside its link window — see
        /// PlayerMovementSystem.Update's linked-slide/linked-dash branches.
        public float StaminaMax, DashStaminaCost, SlideStaminaCost,
            StaminaRegenPerSec, StaminaRegenDelay, LinkRefund;

        /// Slide kinematics and buffered-input windows (Task 2).
        public float SlideSpeed, SlideDuration, SlideSteerRadPerSec, SlideMinSpeedFrac,
            RunUpSeconds, RunUpDecayMult, SlideBufferWindow, LinkWindowSeconds,
            PostDashSlideWindow, SlideWallStopDot, RicochetRetention;

        /// Aim-down-sights movement/settle profile (Task 2).
        public float AimMoveSpeedFrac, AimSlideSpeedMult, AimSettleSeconds;

        /// Stage 2 Task 8 (spec Interfaces): minimum tick gap between two
        /// ACCEPTED edge requests of the same kind (Dash/Slide) from the same
        /// player. Consumed by the rate limit at the top of
        /// PlayerMovementSystem.Update since Stage 2 Task 10, which also clamps
        /// PlayerState's two counters against it in SimulationWorld.ApplyConfig.
        /// 0 disables the limit (every request is accepted).
        public int EdgeRequestMinTicks;

        /// Stage 3 Task 3 (spec §3.6, R-4): auto-pickup collection radius —
        /// Loot.PickupSystem.Update gathers energy cells within this
        /// distance of a live, un-extracted player's Pos. NOT part of
        /// SimConfigHash.Compute yet — see SimConfigHashTests.
        /// SimConfig_CarriesExactlyEightSections and its own PendingHashFields
        /// for where that decision is recorded.
        public float PickupRadius;

        /// Stage 3 Task 4 (spec §3.6 "Рюкзак", errata E-6 D-I8): the
        /// backpack's two capacity numbers. InventoryCapacity is measured
        /// in SLOT POINTS (Loot.Inventory.TryAdd sums Loot.Inventory.
        /// SlotCostOf across the carried items and refuses an add that
        /// would push the total past it), NOT item count.
        /// MaxInventoryItems is the hard ceiling on item COUNT that sizes
        /// SimulationWorld's per-player Loot.Inventory backing array at
        /// construction (independent of slot points, so a future catalog
        /// of very cheap items still cannot outgrow it). NOT part of
        /// SimConfigHash.Compute yet — see SimConfigHashTests.
        /// PendingHashFields for where that decision is recorded (same
        /// T4 -> T13 deferral as PickupRadius/MaxPickups above).
        public int InventoryCapacity;
        public int MaxInventoryItems;
    }

    /// Balance numbers for the player's weapon (fire rate, spread/recoil, projectiles).
    public struct WeaponSimConfig
    {
        public float FireInterval, ProjectileSpeed, ProjectileRadius,
            ProjectileLifetime, Damage, SpreadRad, RecoilPerShotRad, RecoilRecoveryRadPerSec,
            RecoilMaxRad, MuzzleOffset;
        public bool CanFireWhileDash;

        /// Movement-driven spread widening while running/sliding, and whether the
        /// weapon can fire at all mid-slide (Task 2).
        public bool CanFireWhileSlide;
        public float SpreadRunMult, SpreadSlideMult, RunSpreadSpeedFrac;

        /// Stage 3 Task 2 (spec Р261/Р225, errata E-6 D-I8): the ammo economy.
        /// ShotsPerCell converts one picked-up energy cell into this many shots
        /// (the pickup behavior itself is a later task — WeaponSystem.AddAmmo is
        /// the shared conversion point it will call). AmmoStart seeds
        /// PlayerState.Ammo at match start (SimulationWorld's constructor);
        /// AmmoMax is the magazine ceiling SimulationWorld.ApplyConfig clamps
        /// Ammo down to on a hot-tweak. EmergencyFireInterval is the slower
        /// cooldown WeaponSystem.IntervalFor selects once Ammo reaches 0 — the
        /// "emergency synthesis" keeps the weapon firing rather than going
        /// silent. NOT part of SimConfigHash.Compute yet (deferred alongside
        /// Flow — see SimConfigHashTests.SimConfig_CarriesExactlyEightSections
        /// and its own PendingHashFields for where that decision is recorded).
        public int ShotsPerCell;
        public int AmmoStart;
        public int AmmoMax;
        public float EmergencyFireInterval;

        /// Stage 3 Task 3 (spec §3.6, R-3): the fraction of a dead player's
        /// remaining Ammo that rasps out as energy cells — Loot.LootDrops.
        /// CorpseCells is the sole reader. TEMPORARY HOME (R-3): LootSimConfig
        /// doesn't exist until Т13, which moves this into LootSimConfig.
        /// CorpseCellFraction in one step (same move CellsOnDeath below makes).
        /// NOT part of SimConfigHash.Compute yet — see
        /// SimConfigHashTests.SimConfig_CarriesExactlyEightSections and its
        /// own PendingHashFields for where that decision is recorded.
        public float CorpseCellFraction;
    }

    /// Balance numbers shared by all mob archetypes (chaser/gunner use the same shape).
    public struct MobSimConfig
    {
        public float MaxSpeed, Accel, Radius, MaxHp, ContactDamage,
            AttackRange, TelegraphSeconds, AttackCooldown, PreferredRange, RangeTolerance,
            StrafeSpeed, FireInterval, ProjectileSpeed, ProjectileRadius, ProjectileLifetime,
            ProjectileDamage, LeadFactor, SeparationRadius, SeparationStrength, AvoidLookahead;

        /// Vertical hit-zone bounds (meters above ground) and per-zone damage
        /// multipliers for the raycast aim system (Task 4+); MuzzleHeight is read for the
        /// Gunner archetype only.
        public float LegsTop, BodyTop, HeadTop,
            LegsDamageMult, BodyDamageMult, HeadDamageMult, MuzzleHeight;

        /// Melee swing-attack target lead (Chaser archetype, Task 15+).
        public float SwingLeadFactor, SwingLeadMaxMeters;

        /// Extra clearance `Ring.Simulation.AI.MobAiSystem.SteerAround` adds on top
        /// of `Radius` when deciding whether an obstacle still blocks the path to a
        /// target (obstruction lookahead only — the physical
        /// `PlayerMovementSystem.MoveWithCollisions` call always uses the bare
        /// `Radius`). A mob steering with zero margin re-acquires direct pursuit the
        /// instant it is barely, physically clear of an obstacle — which snaps it
        /// onto the obstacle's minimal tangent line, i.e. the shallowest possible
        /// final approach angle into the target. That angle is bounded by
        /// `asin((obstacleRadius + Radius + AvoidMargin) / distanceToObstacleCentre)`
        /// regardless of how the tangent itself is computed (a geometric invariant
        /// of "detour then beeline," confirmed empirically while debugging a Task 19
        /// regression: with `AvoidMargin = 0`, a Chaser rounding an obstacle sitting
        /// on the player's fixed firing line — `ProjectileTests.
        /// ObstacleBeforeMob_BlocksShot_NoDamage`, Task 16 — settled onto a
        /// ~23.6-degree final approach, shallow enough to stay inside the player's
        /// shot corridor all the way into `AttackRange`). This margin makes the mob
        /// keep a wider berth while still navigating around the obstacle, which
        /// lifts that bound comfortably clear of the corridor; 1 (a full body-width
        /// beyond the bare radius) is the smallest round value that does so for the
        /// current Chaser/Gunner numbers (empirically, >=0.8 already suffices —
        /// verified in an offline replay of the collision/steering math before
        /// touching Unity, see the Task 19 report).
        /// Fix-round T14: the wall branch of SteerAround carries a second,
        /// independent guarantee scaled by this same field — offset directly
        /// off a wall's face at a mob sitting in exact physical contact with
        /// it, the resulting waypoint clears that face by exactly
        /// `AvoidMargin`. At `AvoidMargin == 0` that guaranteed clearance
        /// itself vanishes — NOT a dead stop against the flat face: the
        /// waypoint's face offset collapses to zero, leaving a heading that
        /// runs strictly TANGENTIAL to the wall (along its axis), and
        /// `Geometry.Slide` only cancels the velocity component pointing
        /// INTO a surface, so a purely tangential heading is untouched by it
        /// — no dead stop arises. This is why `SimConfigBuilder` validates
        /// this field with `ReqNonNegative`, not `ReqPositive`: 0 is a legal
        /// value, it just spends away the clearance guarantee itself — a
        /// config choice, not a validation bug.
        public float AvoidMargin;

        /// Stage 3 Task 3 (spec §3.6 "Дроп ячеек", R-3): this archetype's
        /// fixed energy-cell drop on death — one field per archetype
        /// instance (Chaser's own value, Gunner's own value), read by
        /// Loot.LootDrops.MobDeathCells. TEMPORARY HOME (R-3): LootSimConfig
        /// doesn't exist until Т13, which moves this into LootSimConfig.
        /// CellsPerMob (indexed by MobType) in one step. NOT part of
        /// SimConfigHash.Compute yet — see SimConfigHashTests.
        /// SimConfig_CarriesExactlyEightSections and its own PendingHashFields
        /// for where that decision is recorded.
        public int CellsOnDeath;
    }

    /// Wave-spawning balance numbers (pacing, counts, spawn placement).
    public struct WaveSimConfig
    {
        public float FirstWaveDelay, WavePause, SpawnRingInset,
            MinSpawnDistanceToPlayer;
        public int BaseCount, CountGrowth, MaxMobsPerWave,
            MaxSpawnAttempts, FallbackSlots;
        public float GunnerShareBase, GunnerShareGrowth;

        /// Stage 2 Task 16 (spec §3.4): per-extra-player wave scale. The raw
        /// wave size is multiplied by (1 + (playerCount - 1) *
        /// PerPlayerCountFrac) before the MaxMobsPerWave cap — see
        /// Ring.Simulation.AI.WaveSystem.CountForTest, the single seam that
        /// owns the formula. 0 keeps solo-sized waves at any player count.
        public float PerPlayerCountFrac;

        /// Stage 3 Task 11 (spec §3.3 Р211/Р212/Р298): the zone budget and
        /// elite-composition numbers. NOT yet part of SimConfigHash.Compute
        /// (coordinator R-57/R-60/R-17: EliteShareMiddle/EliteShareOuterGrowth/
        /// EliteShareOuterCap ride PendingHashFields, ZoneWeights rides the
        /// array-shaped "pending" stretch test — SimConfigHashTests, both
        /// addressee Т13) and NOT yet wired from a real `.asset` (Data/
        /// WaveConfig.cs carries the SO-side default; SimConfigBuilder.Build
        /// copies it through starting this task, same as every other Wave
        /// scalar). ZoneWeights sums to 1 across exactly three elements
        /// (Outer/Middle/Core, Zone's own declared order) — validated in
        /// SimConfigBuilder (coordinator R-56), never assumed here.
        public float[] ZoneWeights;

        /// Elite's flat share of the Middle zone's own budget (spec's own
        /// table, Р212) — a constant, does not grow with WaveIndex the way
        /// the Outer share below does (the Core zone needs no field at all:
        /// its share is always 1, spec's own table, Р212).
        public float EliteShareMiddle;

        /// Elite's share of the Outer zone's budget GROWS by this amount per
        /// wave (`EliteShareOuterGrowth * (WaveIndex - 1)`, spec Р298) up to
        /// EliteShareOuterCap below — "the periphery gets harder from the
        /// clock, not from a static split" (ADR-001 §3.1, the exact clause
        /// spec Р298 exists to satisfy).
        public float EliteShareOuterGrowth;

        /// Ceiling on the Outer zone's growing elite share above (spec's own
        /// "потолок 0.25"). Coordinator decision R-60 (overrides an earlier
        /// draft that treated 0.25 as a hardcoded formula constant): CRITICAL
        /// RULE 6 (ADR-002 §4) puts every game balance number — wave numbers
        /// named explicitly — in a ScriptableObject, not in code, precisely
        /// so the owner can retune it on a milestone (В1's own "periphery
        /// difficulty grows with the clock" playtest) without a recompile.
        /// A fourth WaveSimConfig field, not a local const in WaveSystem.
        public float EliteShareOuterCap;
    }

    /// Stage 3 Task 8 (spec §3.2, Р206): which of the arena's three concentric
    /// rings a position falls in. A PURE function of position and
    /// ArenaSimConfig.ZoneRadius (Geometry.ZoneOf) — nothing in PlayerState/
    /// MobState stores "current zone": a stored duplicate would drift from
    /// position and would enter the state hash for nothing (Р206). Computed
    /// wherever it is needed instead — wave spawn, loot tier, portal gate.
    public enum Zone : byte { Outer = 0, Middle = 1, Core = 2 }

    /// Stage 3 Task 12: the two ExtractKind values, named once (rule 2 — the
    /// convention was prose only until this task put real data behind it).
    /// Same shape as Zone above: the enum names the values, ArenaSimConfig
    /// keeps the raw wire-friendly `byte[]`, and call sites cast.
    ///
    /// Named ExitKind, not ExtractKind, on purpose — twice over. It must not
    /// be read as PlayerState.ExtractKind, whose byte means something else
    /// entirely (0 = "not extracted at all", errata E-1); and a type sharing
    /// ArenaSimConfig.ExtractKind's own name would be a lookup trap at every
    /// call site that touches both.
    ///
    /// It is an ENUM rather than two consts on ArenaSimConfig, and that is
    /// not a style choice: SimConfigHashTests walks every config section with
    /// plain GetFields(), which returns STATIC and CONST fields alongside
    /// instance ones — a `const byte` there is handed straight to that
    /// sweep's Bump(), which knows float/int/bool and throws
    /// NotSupportedException on anything else. Config sections hold hashable
    /// instance numbers and nothing else; the registry enforces it.
    public enum ExitKind : byte { Portal = 0, Gate = 1 }

    /// Arena geometry and per-match entity caps.
    public struct ArenaSimConfig
    {
        public float Radius;
        public int ObstacleCount;
        public float2[] ObstaclePos;
        public float[] ObstacleRadius;
        public int MaxMobs, MaxProjectiles, MaxEventsPerFrame;

        /// Stage 2 Task 4 (spec §3.2): per-match player cap and the multiplayer
        /// spawn-ring radius fraction (ring radius = Radius * PlayerSpawnRingFrac).
        /// Read by SimulationWorld's constructor guard, Geometry.SpawnPosFor and
        /// SimConfigBuilder.Validate's spawn-clearance check — all three reuse
        /// the same formula, not a duplicated copy of the trigonometry.
        public int MaxPlayers;
        public float PlayerSpawnRingFrac;

        /// Stage 2 Task 11 (spec §3.3): wall geometry. Each wall is a
        /// "stadium" — segment WallA[i]→WallB[i] inflated by
        /// WallHalfWidth[i] — reusing Geometry's circle-sweep math instead
        /// of an OBB. Shape mirrors the ObstaclePos/ObstacleRadius pair.
        /// Populated by SimConfigBuilder from ArenaConfig.Walls[] since
        /// Stage 2 Task 16 (the shipped default arena now carries WallCount
        /// 6). WallCount is 0 and the arrays EMPTY — never null, a real
        /// Build() always allocates them, empty or not — for a config that
        /// opts out of walls entirely (e.g. TestConfigs.Open()).
        public int WallCount;
        public float2[] WallA;
        public float2[] WallB;
        public float[] WallHalfWidth;

        /// Stage 2 Task 46 (bd app-r8x): height of every INTERIOR barrier —
        /// the obstacle circles and the stadium walls above share this one
        /// number, in meters above the floor (y = 0). A round whose whole
        /// remaining step sits above it passes over the barrier instead of
        /// being stopped by it (ProjectileSystem.AcceptCandidate).
        ///
        /// 0 (or any non-positive value) means NO MODELLED TOP: the barrier
        /// stops a shot at any height, which is what every barrier did before
        /// this field existed. That is the C# default of this struct, so every
        /// hand-built fixture — and with it the golden scenarios — keeps the
        /// pre-Task-46 behavior without stating anything.
        ///
        /// ONE NUMBER, NOT ONE PER BARRIER (owner decision 2026-08-11): with a
        /// shared height "cleared one interior barrier" means "cleared them
        /// all", which is what lets the projectile gather keep a single
        /// candidate slot for the nearest interior barrier instead of one slot
        /// per barrier.
        ///
        /// The arena's outer ring boundary is NOT covered by this: it holds the
        /// edge of the world, and a shot flying over it would leave the arena
        /// altogether — see ProjectileSystem's HitRingWall candidate.
        public float BarrierTop;

        /// Stage 3 Task 3 (spec §3.6, R-4): per-match cap on live pickups
        /// (energy cells today; Task 13's second Kind reuses this same
        /// array/cap) — same swap-remove-capped-array shape as
        /// MaxMobs/MaxProjectiles above. SimulationWorld's constructor sizes
        /// its `Pickups` array off exactly this field, and ArenaTopologyMatches
        /// rejects a hot-tweak that changes it, same contract as the three
        /// caps above. NOT part of SimConfigHash.Compute yet — see
        /// SimConfigHashTests.SimConfig_CarriesExactlyEightSections and its
        /// own PendingHashFields for where that decision is recorded.
        public int MaxPickups;

        /// Stage 3 Task 8 (spec §3.2, Р206): the two zone-boundary radii —
        /// {65, 92} at the shipped layout (delivery is Т12's "перепин №2",
        /// not this task). ALWAYS exactly two elements ("two boundaries,
        /// three zones") — unlike WallCount/ObstacleCount this is not a
        /// variable-length "0 disables" array, so Geometry.ZoneOf reads
        /// index 0/1 directly rather than looping. Empty (never null) before
        /// Т12 wires real numbers — same never-null convention as WallA/
        /// WallB. NOT part of SimConfigHash.Compute yet — see
        /// SimConfigHashTests.PendingHashFields (R-17: skip-set lifts whole,
        /// addressee Т13).
        public float[] ZoneRadius;

        /// Stage 3 Task 8 (spec §3.2, Р207): the zone-boundary ARC BARRIERS —
        /// same parallel-array shape as WallA/WallB/WallHalfWidth above, but
        /// each entry here is a full ring (centered on the arena origin,
        /// radius ZoneWallRadius[i]) with angular door cutouts instead of a
        /// straight stadium segment (Geometry.OverlapsArc/SegmentArc/
        /// PushOutOfArc, Task 7). ZoneWallCount == 0 gives the Stage 2 arena
        /// literally, same convention as WallCount — every fixture before
        /// Т12 (including TestConfigs.Default()) stays on this branch, which
        /// is what keeps both golden scenarios green through this task.
        public int ZoneWallCount;
        public float[] ZoneWallRadius;
        public float[] ZoneWallHalfWidth;

        /// Doors live in one flat pair of arrays SHARED by every wall
        /// (Р246: circular jambs, not an angular pad — see Geometry.cs'
        /// Stage 3 Task 7 section). ZoneWallDoorStart[i]/ZoneWallDoorCount[i]
        /// slice DoorCenterRad/DoorFreeWidth per wall — mirrors
        /// Geometry.SegmentArc/OverlapsArc's own
        /// ReadOnlySpan&lt;float&gt; doorCenter/doorFreeWidth parameters (Task 7).
        /// Ledger R-26: DoorFreeWidth is the canonical name — spec §3.2's own
        /// data table calls it DoorHalfWidthMeters, which is an error in the
        /// spec's text against its own prose (Р246/Р247) and against Task
        /// 7/8's shipped signatures.
        public int[] ZoneWallDoorStart;
        public int[] ZoneWallDoorCount;
        public float[] DoorCenterRad;
        public float[] DoorFreeWidth;

        /// Stage 3 Task 8 (owner decision R-29): the maneuvering room term of
        /// the door-width rule (spec Р247): DoorFreeWidth >= 2*(bodyRadius +
        /// Geometry.Skin) + DoorClearance. .asset-sourced by CR 6 (a real
        /// number belongs in data, not code) — Interfaces plan text omitted
        /// it; this task adds the field, Т12 delivers the real value.
        public float DoorClearance;

        /// Stage 3 Task 8 (spec §3.15): portals and the extraction gate —
        /// one flat triple of parallel arrays, same shape discipline as
        /// ObstaclePos/ObstacleRadius. ExtractZone/ExtractKind are raw byte
        /// (Zone, and Portal=0/Gate=1 respectively) rather than enum-typed,
        /// matching PickupKind's own wire-friendly byte convention.
        public float2[] ExtractPos;
        public byte[] ExtractZone;
        public byte[] ExtractKind;


        /// Stage 3 Task 8 (spec §3.15): 8 at the shipped layout — validated
        /// (Т12+) against Hero.Radius, same ReqPositive-adjacent per-match
        /// geometry convention as the rest of this struct.
        public float ExtractRadius;

        /// Stage 3 Task 8 (spec §3.7/§3.13): per-match container caps, same
        /// per-match-entity-cap convention as MaxPickups above.
        /// MaxContainerSlots is R-5's corrected 8, not the spec table's
        /// stale 4 — Р263 derives it from InventoryCapacity / min(SlotCost)
        /// = 8/1, and §3.12 counts on a one-byte occupancy mask, exact at 8.
        public int MaxContainers;
        public int MaxContainerSlots;
    }

    /// Server-side visibility filter numbers (Stage 2 Task 19, spec §3.5,
    /// Р18-Р21): sight/hearing radii, exit hysteresis, linger grace period and
    /// the audible-position quantization grid (the latter two fields —
    /// HearRadius and HearPositionGridMeters — are read only from Stage 2
    /// Task 20 on, once IsAudible/QuantizeAudiblePos land, but ship together
    /// with the rest of the config here since VisibilityConfig's own SO
    /// carries them as one balance sheet). NOT part of StateHash: a
    /// per-observer fog-of-war filter is a network-facing concern, not world
    /// state — see VisibilitySet's own doc for where the per-connection
    /// result actually lives.
    public struct VisibilitySimConfig
    {
        public float SightRadius, HearRadius, ExitHysteresis;
        public int LingerTicks;
        public float HearPositionGridMeters;
    }

    /// Match-flow pacing config (Stage 3 Task 1 Interfaces, errata E-2):
    /// declared here (not in Assets/Data yet) so Т21 (the phase state
    /// machine, Ф4) can already read it. Its ScriptableObject home
    /// (Data/MatchFlowConfig.cs), the `.asset` through
    /// ApplyStageThreeBalance, and the SimConfigBuilder wiring are Т12's job
    /// — the one task that delivers SO-backed data (errata E-2's full
    /// account of why the two are split). Т22 only uses what is already
    /// here. NOT part of SimConfigHash.Compute yet: errata E-6 I9 named
    /// "Т8/Т10/Т13/Т22" for that whole deferred set, and owner decision R-17
    /// collapsed the four addressees into ONE — Т13 lifts the skip-set whole,
    /// these five numbers included. Recorded, and made executable, in
    /// SimConfigHashTests.PendingHashFields, which asserts each of them is
    /// still outside the hash and goes red the moment Т13 removes the set
    /// without wiring them.
    public struct MatchFlowSimConfig
    {
        public float GateDelaySeconds;
        public float ExtractChannelSeconds;
        public int RetinueCount;
        public float RetinueRespawnSeconds;
        public int DirectorReserveSlots;
    }

    /// Full balance snapshot for one match — plain data, no ScriptableObjects.
    public struct SimConfig
    {
        public HeroSimConfig Hero;
        public WeaponSimConfig Weapon;
        public MobSimConfig Chaser, Gunner;
        public WaveSimConfig Wave;
        public ArenaSimConfig Arena;
        public VisibilitySimConfig Visibility;
        /// Stage 3 Task 1 (errata E-2): match-flow pacing (gate delay,
        /// extract channel length, retinue respawn count/cadence, Director
        /// reserve slots) — SO-backed default arrives in Т12.
        public MatchFlowSimConfig Flow;
        /// Stage 3 Task 10 (spec Р213): the third and fourth mob archetype
        /// — same MobSimConfig shape Chaser/Gunner already use (one asset
        /// of the existing Ring.Data.MobConfig class each, spec §3.13: "не
        /// новые ассеты класса, а ассеты существующего класса"), read
        /// through the exact same seam (SimulationWorld.MobConfigFor's own
        /// switch). `TestConfigs.Default()` deliberately leaves both at
        /// their C# struct default (all-zero) until Т12 — see that
        /// method's own doc — and neither section is wired into
        /// SimConfigBuilder.Build's SO pipeline yet (Т12, errata E-6 I5)
        /// except for the two OPTIONAL trailing parameters
        /// SimConfigBuilder.Build grew this task purely so
        /// ZoneConfigTests' own R-28 door-width test could drive a real
        /// Director body radius through Validate without waiting on that
        /// asset delivery. NOT part of SimConfigHash.Compute yet (owner
        /// decision R-17) — MobSimConfig's field NAMES are shared 1:1 with
        /// Chaser/Gunner's own already-hashed section, so the flat,
        /// name-only SimConfigHashTests.PendingHashFields set cannot record
        /// this deferral without also, incorrectly, exempting Chaser's/
        /// Gunner's real numbers; see SimConfigHashTests.
        /// EliteAndDirectorSections_DoNotAffectHash_UntilT13WiresThem for
        /// the executable record of the decision instead, and
        /// SimConfig_CarriesExactlyTenSections for the section-count guard.
        public MobSimConfig Elite, Director;
    }
}
