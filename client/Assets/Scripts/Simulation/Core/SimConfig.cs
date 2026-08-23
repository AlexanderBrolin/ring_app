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
        /// distance of a live, un-extracted player's Pos. Stage 3 Task 13
        /// (owner decision R-17): part of SimConfigHash.Compute as of this
        /// task.
        public float PickupRadius;

        /// Stage 3 Task 4 (spec §3.6 "Рюкзак", errata E-6 D-I8): the
        /// backpack's two capacity numbers. InventoryCapacity is measured
        /// in SLOT POINTS (Loot.Inventory.TryAdd sums Loot.Inventory.
        /// SlotCostOf across the carried items and refuses an add that
        /// would push the total past it), NOT item count.
        /// MaxInventoryItems is the hard ceiling on item COUNT that sizes
        /// SimulationWorld's per-player Loot.Inventory backing array at
        /// construction (independent of slot points, so a future catalog
        /// of very cheap items still cannot outgrow it). Stage 3 Task 13
        /// (owner decision R-17): part of SimConfigHash.Compute as of this
        /// task, same move as PickupRadius/MaxPickups above.
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
        /// (the pickup behavior itself has since landed — Loot.PickupSystem
        /// .Collect calls SimulationWorld.AddAmmo, the shared conversion
        /// point named here). AmmoStart seeds
        /// PlayerState.Ammo at match start (SimulationWorld's constructor);
        /// AmmoMax is the magazine ceiling SimulationWorld.ApplyConfig clamps
        /// Ammo down to on a hot-tweak. EmergencyFireInterval is the slower
        /// cooldown WeaponSystem.IntervalFor selects once Ammo reaches 0 — the
        /// "emergency synthesis" keeps the weapon firing rather than going
        /// silent. Stage 3 Task 13 (owner decision R-17): part of
        /// SimConfigHash.Compute as of this task — R-17 lifted the whole
        /// deferred-wiring skip-set in one move (SimConfigHashTests no longer
        /// carries a PendingHashFields entry for any of the four).
        public int ShotsPerCell;
        public int AmmoStart;
        public int AmmoMax;
        public float EmergencyFireInterval;
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
        /// elite-composition numbers. Stage 3 Task 13 (owner decision R-17):
        /// EliteShareMiddle/EliteShareOuterGrowth/EliteShareOuterCap and
        /// ZoneWeights are part of SimConfigHash.Compute as of this task —
        /// R-17 lifted the whole deferred-wiring skip-set in one move, arrays
        /// included. ZoneWeights sums to 1 across exactly three elements
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
    /// ArenaSimConfig.ZoneRadius (Geometry.ZoneOf) — nothing in PlayerState
    /// stores "current zone": a stored duplicate would drift from position
    /// and would enter the state hash for nothing (Р206). Computed wherever
    /// it is needed instead — wave spawn, loot tier, portal gate.
    ///
    /// Wave-cadence-per-zone amendment (bd app-ggvz Т1): MobState.SpawnZone
    /// is a DIFFERENT thing and does not contradict the rule above. It does not
    /// hold a mob's current zone either — that stays uncomputed and
    /// unstored, exactly like PlayerState's. It holds the ring the mob was
    /// PUT INTO by whoever spawned it, which position cannot answer once
    /// the mob has walked away from its spawn point — see the field's own
    /// doc (SimStates.cs) for why that one number is the exception this
    /// enum's "compute, don't store" rule was never meant to cover.
    public enum Zone : byte { Outer = 0, Middle = 1, Core = 2 }

    /// Wave-cadence-per-zone (bd app-ggvz Т1): the ONE home for "how many
    /// zones the arena has," replacing two independent
    /// `const int ZoneCount = 3` copies (WaveSystem, LootDrops — rule 2, two
    /// homes of one number). A STATIC CLASS, not a const on a config struct,
    /// on purpose: the reflective sweep SimConfigHashTests walks every
    /// section with plain GetFields() and would hand a `const` sitting
    /// inside a hashable struct straight to Bump(), which throws on
    /// anything it does not know how to bump — the same reason
    /// ItemCatalogLookup and LootTransferTimes, both further down this
    /// file, are static classes rather than fields on a config struct.
    public static class Zones
    {
        public const int Count = 3;
    }

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
        /// caps above. Stage 3 Task 13 (owner decision R-17): part of
        /// SimConfigHash.Compute as of this task.
        public int MaxPickups;

        /// Stage 3 Task 8 (spec §3.2, Р206): the two zone-boundary radii —
        /// {65, 92} at the shipped layout (delivery is Т12's "перепин №2",
        /// not this task). ALWAYS exactly two elements ("two boundaries,
        /// three zones") — unlike WallCount/ObstacleCount this is not a
        /// variable-length "0 disables" array, so Geometry.ZoneOf reads
        /// index 0/1 directly rather than looping. Empty (never null) before
        /// Т12 wires real numbers — same never-null convention as WallA/
        /// WallB. Stage 3 Task 13 (owner decision R-17): part of
        /// SimConfigHash.Compute as of this task — the skip-set lifted
        /// whole, arrays included.
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

        /// Stage 3 Task 13 (spec §3.9, errata Р268 finding 3): the radius
        /// term VisibilitySystem.Compute needs for a pickup/container target
        /// — that method reads a mob's own radius off
        /// `w.MobConfigFor(m.Type).Radius`, and neither of these two entity
        /// classes has a MobConfig to read from. Both 0.4 m (spec's own
        /// numbers). Consumer: Т26 (the visibility filter's own extension to
        /// the two new entity classes) — this task only delivers the data.
        public float PickupRadiusForVisibility;
        public float ContainerRadiusForVisibility;
    }

    /// Match-flow pacing config (Stage 3 Task 1 Interfaces, errata E-2):
    /// declared here (not in Assets/Data yet) so Т21 (the phase state
    /// machine, Ф4) can already read it. Its ScriptableObject home
    /// (Data/MatchFlowConfig.cs), the `.asset` through
    /// ApplyStageThreeBalance, and the SimConfigBuilder wiring are Т12's job
    /// — the one task that delivers SO-backed data (errata E-2's full
    /// account of why the two are split). Т22 only uses what is already
    /// here. Stage 3 Task 13 (owner decision R-17): part of
    /// SimConfigHash.Compute as of this task — errata E-6 I9 named
    /// "Т8/Т10/Т13/Т22" for the whole deferred set, and R-17 collapsed the
    /// four addressees into ONE, this task, which lifts the skip-set whole,
    /// these five numbers included.
    public struct MatchFlowSimConfig
    {
        public float GateDelaySeconds;
        public float ExtractChannelSeconds;
        public int RetinueCount;
        public float RetinueRespawnSeconds;
        public int DirectorReserveSlots;
    }

    /// Stage 3 Task 13 (spec §3.7): what one catalog entry IS — the ONLY
    /// two kinds that exist today (the two branches Loot.LootConfig's own
    /// Use-vs-everything-else split needs). A `const byte` pair would also
    /// compile, but SimConfigHashTests walks every config section with
    /// plain GetFields() and hands each value to a switch that understands
    /// float/int/bool — a `const` field there is handed straight to that
    /// switch and throws NotSupportedException (same reasoning as
    /// ExitKind's own doc, right above this struct's sibling).
    public enum ItemKind : byte { Trophy = 0, RepairKit = 1 }

    /// Stage 3 Task 13 (spec §3.7): one catalog entry. `Id` is the wire
    /// byte a container slot / backpack slot actually stores — everything
    /// else here is metadata a reader resolves through
    /// ItemCatalogLookup.Find, never carried alongside the id itself.
    /// [System.Serializable] (plain BCL — not UnityEngine, so this stays
    /// legal in an asmdef with `noEngineReferences`, CRITICAL RULE 1) lets
    /// Ring.Data.ItemCatalog hold `ItemDef[]` directly as its own Inspector
    /// array — no parallel Data-side DTO to keep in sync (rule 2).
    [System.Serializable]
    public struct ItemDef
    {
        public byte Id;
        /// 1..4 for a tiered trophy (spec §3.7's own table); 0 for the
        /// repair kit, which sits outside the tier ladder on purpose (spec:
        /// "ремкомплект — вне тиров").
        public byte Tier;
        public byte SlotCost;
        public ushort CreditValue;
        public ItemKind Kind;
    }

    /// Stage 3 Task 13 (owner decision R-89): the ONE home for "item id ->
    /// full catalog entry." Ids are NOT guaranteed to equal their index
    /// (SimConfigBuilder.Validate rejects a duplicate id but not a gap —
    /// R-89's own text), so every reader that needs a record back from an
    /// id — Loot.Inventory's SlotCostOf/UsedSlots/TryAdd today,
    /// SimConfigBuilder.Validate's min(SlotCost) rule, the future
    /// tier-of-drop (Т16) and looting (Т17) — resolves it here, never with
    /// its own copy of the search. Two homes of one lookup already cost
    /// this project three passes on a single test (WaveSystem.PendingRef /
    /// ProjectileSystem.MobRadiusFor's own docs record the lesson); this is
    /// the same discipline applied before a second home has a chance to
    /// grow.
    public static class ItemCatalogLookup
    {
        /// Named refusal (coordinator ledger, R-64 precedent): the message
        /// carries both the id that failed to resolve and the catalog's own
        /// size, not a bare exception — a diagnostic a caller can act on
        /// instead of a stack trace pointing at a search loop.
        public static ItemDef Find(byte id, ItemDef[] catalog)
        {
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i].Id == id) return catalog[i];
            throw new System.ArgumentException(
                $"item id {id} is not in the catalog ({catalog.Length} entries)");
        }

        /// Whether the catalog holds this id at all (Stage 3 Task 25) — the
        /// SAME search as Find above, minus the throw. It exists because the
        /// wire decoders (SnapshotBlocks' Self and ContainerSlots blocks)
        /// have to ask the question on UNTRUSTED bytes, where Find's named
        /// refusal is exactly the wrong answer: a decoder of hostile input
        /// must report, never throw (Р82). Delegating here rather than
        /// walking the array a second time is this class's own rule — it is
        /// declared the ONE home of "item id -> entry", and a private copy of
        /// the loop inside a decoder would be the second home that rule names
        /// the price of.
        public static bool Contains(byte id, ItemDef[] catalog)
        {
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i].Id == id) return true;
            return false;
        }

        /// Stage 3 Task 16 (coordinator R-124): the second mapping this
        /// class holds — "tier -> item", the ONE Trophy record whose Tier
        /// equals `tier`. SimConfigBuilder's own "one trophy per tier" rule
        /// (ValidateItems) is what makes this a FUNCTION rather than a
        /// search needing a tie-break; this method does not assume that
        /// rule holds, it just returns the first match and names the tier
        /// on failure, same named-refusal shape as Find above.
        public static ItemDef FindByTier(byte tier, ItemDef[] catalog)
        {
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i].Kind == ItemKind.Trophy && catalog[i].Tier == tier) return catalog[i];
            throw new System.ArgumentException(
                $"no Trophy item maps to tier {tier} ({catalog.Length} catalog entries)");
        }

        /// Stage 3 Task 16: the ONE RepairKit record — same shape as
        /// FindByTier above, keyed by Kind instead of Tier.
        public static ItemDef FindRepairKit(ItemDef[] catalog)
        {
            for (int i = 0; i < catalog.Length; i++)
                if (catalog[i].Kind == ItemKind.RepairKit) return catalog[i];
            throw new System.ArgumentException(
                $"no RepairKit item in the catalog ({catalog.Length} entries)");
        }
    }

    /// Stage 3 Task 13 (spec §3.7/§3.8): loot-system balance numbers — drop
    /// chances, container counts, the repair kit's own numbers, per-tier
    /// transfer time, and the two entity TTLs. Field order below is the
    /// canonical order SimConfigHash.Compute's HashLoot mirrors (class doc
    /// convention, same as every other section in this file).
    public struct LootSimConfig
    {
        /// [archetype * Zones.Count + zone] -> chance, flat 4x3 (owner
        /// decision, errata E-6 A-I11). Zones.Count is 3 (Zone's own
        /// Outer/Middle/Core order); the archetype axis is 4, indexed
        /// exactly like MobType (Chaser/Gunner/Elite/Director), even though
        /// the spec's own per-archetype/zone table (§3.7) has no row for
        /// the Director — the Director's drop is the fixed "three
        /// containers + one memory core" rule, not a chance roll — so every
        /// reader indexes this array by MobType directly, with no
        /// MobType-to-archetype remap anywhere (rule 2), and the Director's
        /// own row simply stays unread.
        /// Stage 3 Task 16: gained its live reader (LootDrops.
        /// TryRollMobItemTier) and its own two shape rules — length == 12
        /// (R-121a) and a nonzero element requires Arena.ZoneRadius.Length
        /// == 2 (R-121b) — both enforced by SimConfigBuilder.ValidateLoot,
        /// replacing the ASSUMPTION+ADDRESSEE doc this field carried before
        /// a reader existed (coordinator R-96 precedent). Build's own
        /// omitted-`loot` branch still seeds a correctly-sized all-zero
        /// array (coordinator R-96), so the pre-Т16 history of "no rule"
        /// was never the same as "no safe default".
        public float[] DropChance;
        public int CrateCount, CacheCountMiddle, CacheCountCore;
        public float RepairKitChance;
        /// Indexed by MobType (Chaser/Gunner/Elite/Director) — same
        /// archetype-axis convention as DropChance above. Replaces the
        /// TEMPORARY per-archetype MobSimConfig.CellsOnDeath field (R-3,
        /// removed this task) with one flat array. Stage 3 Task 13
        /// (coordinator R-96): the ONE Loot array with a REAL rule —
        /// SimConfigBuilder.Validate's ValidateLoot requires exactly 4
        /// elements, because LootDrops.MobDeathCells already indexes this
        /// array by MobType with no bounds guard of its own (a live reader,
        /// unlike DropChance/TransferSeconds below).
        public int[] CellsPerMob;
        public float CorpseCellFraction;
        public float RepairKitHealAmount;
        public float RepairKitChannelSeconds;
        /// Indexed by tier - 1 (tier 1..4 -> index 0..3) — {0.3, 0.6, 0.9, 1.2}.
        /// ⚠ NOT indexed the way CellsPerMob above is: that one is a DIRECT
        /// MobType index (Chaser 0 .. Director 3), this one carries a SHIFT,
        /// because tiers are numbered from one in spec §3.7's own table while
        /// arrays are numbered from zero. Two conventions in one struct have
        /// to be named or the next reader picks the wrong one — which is
        /// exactly why neither call site does the arithmetic itself:
        /// LootTransferTimes below is the ONE home of the shift.
        ///
        /// Stage 3 Task 17: this field HAS a live reader now
        /// (LootTransferTimes.ForTier, called by Loot.LootOps.Begin and by
        /// SimulationWorld.ApplyConfig's own clamp), so the ASSUMPTION +
        /// ADDRESSEE doc it used to carry is replaced by a real rule in
        /// SimConfigBuilder.ValidateLoot — R-92 in its plain form: a rule is
        /// earned by the reader it protects, and it arrives with that reader.
        /// Same history CellsPerMob had in Т13 and DropChance in Т16.
        public float[] TransferSeconds;
        public int LootSpawnAttempts, LootFallbackSlots;
        public float PickupTtlSeconds, ContainerTtlSeconds;
        /// Stage 3 Task 13 (owner decision R-9, errata E-6 A-I8): spec
        /// §3.13's own data table puts this on HeroConfig; the errata and
        /// the coordinator ledger override that reading — one home next to
        /// every other loot number rather than splitting the loot balance
        /// sheet across two SOs. Consumer: Т17. Declared LAST (not at its
        /// spec-table position) because it entered this struct after every
        /// other field — same "append, don't reshuffle a hash-order
        /// contract" convention the rest of this file already follows.
        public float LootRadius;
    }

    /// Stage 3 Task 17 (spec §3.8 "таймер переноса", coordinator decision
    /// D-3): the ONE home of "how long does moving THIS item take", and of
    /// the only aggregate over that table anyone needs. Two readers, one
    /// mapping — Loot.LootOps.Begin asks for a specific tier's time,
    /// SimulationWorld.ApplyConfig asks for the ceiling — and neither writes
    /// the `tier - 1` shift itself. Same "one lookup, never a second copy of
    /// the search" discipline ItemCatalogLookup above already states, and for
    /// the same recorded reason: two homes of one mapping have already cost
    /// this project three passes on a single test.
    public static class LootTransferTimes
    {
        /// The transfer time for an item of `tier`, in seconds.
        ///
        /// THE SHIFT LIVES HERE AND NOWHERE ELSE: tiers run 1..4 (spec §3.7),
        /// the array runs 0..3, so the index is `tier - 1`. Contrast
        /// LootSimConfig.CellsPerMob, indexed DIRECTLY by MobType with no
        /// shift at all — the two arrays sit in the same struct and are read
        /// differently, which is a trap unless one function owns the
        /// difference.
        ///
        /// OWNER DECISION, SETTLED (R-158, 2026-08-19 — supersedes the
        /// coordinator's D-1 simplification, which was recorded as an open
        /// question for milestone В1): the repair kit is deliberately OUTSIDE
        /// the tier ladder — ItemDef.Tier is 0 for it (spec §3.7: "ремкомплект
        /// — вне тиров") — yet taking one out of a container is a perfectly
        /// legal Take that needs a duration. The owner ruled it takes the SAME
        /// time as the cheapest tier, so the clamp below is now the intended
        /// rule rather than a stand-in: the tier is CLAMPED into [1, 4] and the
        /// kit borrows tier one's time. No entry of its own is coming, and no
        /// balance number is owed here — which also keeps CR 6 satisfied
        /// (a duration invented in code would violate it) without depending on
        /// the phase's spent data-delivery gate.
        ///
        /// No bounds guard on the array itself: SimConfigBuilder.ValidateLoot
        /// enforces exactly four elements (Stage 3 Task 17), which is the rule
        /// this reader is what earned.
        public static float ForTier(byte tier, in LootSimConfig loot)
            => loot.TransferSeconds[math.clamp((int)tier, 1, 4) - 1];

        /// The longest transfer any tier can ask for — the only honest ceiling
        /// a hot-tweak can clamp a running channel against, since the channel's
        /// own target tier is not recoverable at ApplyConfig time (the
        /// container may already be gone).
        ///
        /// A MAX OVER THE TABLE, deliberately not `TransferSeconds[3]`:
        /// nothing anywhere guarantees the table is monotonic, and a ceiling
        /// resting on "the last one is the largest" would go silently wrong
        /// the first time the owner reorders these numbers on a balance pass.
        /// Null-safe for the same reason ValidateLoot exists at all — a
        /// malformed config must produce a named refusal from the builder, not
        /// a NullReferenceException from a clamp.
        public static float Longest(in LootSimConfig loot)
        {
            float[] table = loot.TransferSeconds;
            if (table == null) return 0f;
            float longest = 0f;
            for (int i = 0; i < table.Length; i++) longest = math.max(longest, table[i]);
            return longest;
        }
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
        /// switch). Wired into SimConfigBuilder.Build's SO pipeline since
        /// Т12 (the two trailing optional `elite`/`director` parameters).
        /// Stage 3 Task 13 (owner decision R-17): part of
        /// SimConfigHash.Compute as of this task — MobSimConfig's field
        /// NAMES are shared 1:1 with Chaser/Gunner's own already-hashed
        /// section, so EveryConfigNumberAffectsHash sweeps this section by
        /// its own dedicated AssertSectionAffectsHash("Elite"/"Director")
        /// calls rather than the flat, name-only PendingHashFields set (that
        /// set cannot express "Elite.MaxSpeed is exempt but Chaser.MaxSpeed
        /// is not"). See SimConfig_CarriesExactlyTwelveFields for the
        /// field-count guard.
        public MobSimConfig Elite, Director;
        /// Stage 3 Task 13 (spec §3.7/§3.8): loot-system balance numbers.
        public LootSimConfig Loot;
        /// Stage 3 Task 13 (spec §3.7, owner decision R-82): a copy of the
        /// item catalog, at most 255 records (the wire's byte Id caps it).
        /// `ItemKind`/`ItemDef` live in Simulation/Core rather than Data
        /// (CRITICAL RULE 1 — Simulation never references Ring.Data), so
        /// this field IS the catalog as far as the sim is concerned; the
        /// SO-side original (Ring.Data.ItemCatalog) never reaches
        /// Simulation. Never null after SimConfigBuilder.Build (empty
        /// instead — same never-null convention as every other array field
        /// in this file); a hand-built test fixture may still leave it
        /// null, same as ArenaSimConfig's arrays.
        public ItemDef[] Items;
    }
}
