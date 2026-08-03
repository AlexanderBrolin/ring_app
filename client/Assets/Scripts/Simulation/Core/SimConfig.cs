using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Balance numbers for the player hero (movement, dash, HP).
    public struct HeroSimConfig
    {
        public float MaxSpeed, Accel, Friction, Radius, MaxHp,
            DashSpeed, DashDuration, DashCooldown, DashIframes, DashBufferWindow;

        /// Vertical hit-zone bounds (metres above ground) and per-zone damage
        /// multipliers for the raycast aim system (Т4+).
        public float LegsTop, BodyTop, HeadTop,
            LegsDamageMult, BodyDamageMult, HeadDamageMult;

        /// Slide stamina-movement profile height, hero muzzle heights (standing /
        /// mid-slide), and the arena-wide aim-ray height cap.
        public float SlideProfileTop, MuzzleHeight, SlideMuzzleHeight, MaxAimHeight;
    }

    /// Balance numbers for the player's weapon (fire rate, spread/recoil, projectiles).
    public struct WeaponSimConfig
    {
        public float FireInterval, ProjectileSpeed, ProjectileRadius,
            ProjectileLifetime, Damage, SpreadRad, RecoilPerShotRad, RecoilRecoveryRadPerSec,
            RecoilMaxRad, MuzzleOffset;
        public bool CanFireWhileDash;
    }

    /// Balance numbers shared by all mob archetypes (chaser/gunner use the same shape).
    public struct MobSimConfig
    {
        public float MaxSpeed, Accel, Radius, MaxHp, ContactDamage,
            AttackRange, TelegraphSeconds, AttackCooldown, PreferredRange, RangeTolerance,
            StrafeSpeed, FireInterval, ProjectileSpeed, ProjectileRadius, ProjectileLifetime,
            ProjectileDamage, LeadFactor, SeparationRadius, SeparationStrength, AvoidLookahead;

        /// Vertical hit-zone bounds (metres above ground) and per-zone damage
        /// multipliers for the raycast aim system (Т4+); MuzzleHeight is read for the
        /// Gunner archetype only.
        public float LegsTop, BodyTop, HeadTop,
            LegsDamageMult, BodyDamageMult, HeadDamageMult, MuzzleHeight;

        /// Melee swing-attack target lead (Chaser archetype, Т15+).
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
    }

    /// Arena geometry and per-match entity caps.
    public struct ArenaSimConfig
    {
        public float Radius;
        public int ObstacleCount;
        public float2[] ObstaclePos;
        public float[] ObstacleRadius;
        public int MaxMobs, MaxProjectiles, MaxEventsPerFrame;
    }

    /// Full balance snapshot for one match — plain data, no ScriptableObjects.
    public struct SimConfig
    {
        public HeroSimConfig Hero;
        public WeaponSimConfig Weapon;
        public MobSimConfig Chaser, Gunner;
        public WaveSimConfig Wave;
        public ArenaSimConfig Arena;
    }
}
