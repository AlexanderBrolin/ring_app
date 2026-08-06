using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Balance numbers for the player hero (movement, dash, HP).
    public struct HeroSimConfig
    {
        public float MaxSpeed, Accel, Friction, Radius, MaxHp,
            DashSpeed, DashDuration, DashCooldown, DashIframes, DashBufferWindow;

        /// Vertical hit-zone bounds (metres above ground) and per-zone damage
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
    }

    /// Balance numbers shared by all mob archetypes (chaser/gunner use the same shape).
    public struct MobSimConfig
    {
        public float MaxSpeed, Accel, Radius, MaxHp, ContactDamage,
            AttackRange, TelegraphSeconds, AttackCooldown, PreferredRange, RangeTolerance,
            StrafeSpeed, FireInterval, ProjectileSpeed, ProjectileRadius, ProjectileLifetime,
            ProjectileDamage, LeadFactor, SeparationRadius, SeparationStrength, AvoidLookahead;

        /// Vertical hit-zone bounds (metres above ground) and per-zone damage
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
    }

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

    /// Full balance snapshot for one match — plain data, no ScriptableObjects.
    public struct SimConfig
    {
        public HeroSimConfig Hero;
        public WeaponSimConfig Weapon;
        public MobSimConfig Chaser, Gunner;
        public WaveSimConfig Wave;
        public ArenaSimConfig Arena;
        public VisibilitySimConfig Visibility;
    }
}
