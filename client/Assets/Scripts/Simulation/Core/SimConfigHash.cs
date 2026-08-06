using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Balance-parity hash for the network handshake (Stage 2 Task 23, spec
    /// §3.8, Р52/Р139): FNV-1a 64 over EVERY balance number of SimConfig, in
    /// declaration order — used by Task 39 to detect a client/server
    /// balance mismatch before a match starts. Distinct from StateHash64's
    /// own use in SimulationWorld.StateHash(): that hash covers per-tick
    /// WORLD state (players, mobs, projectiles...) and moves every tick;
    /// this one covers the CONFIG that state evolves under, and only
    /// changes when the owner retunes a balance number. Composed entirely
    /// from StateHash64.Begin/Add (rule 2 — reuse, no separate hashing
    /// arithmetic of its own).
    ///
    /// Arrays (ObstaclePos/ObstacleRadius/WallA/WallB/WallHalfWidth) are
    /// hashed as LENGTH + EVERY ELEMENT, not by the paired ObstacleCount/
    /// WallCount fields — this is a cross-process balance check, not a
    /// bounded read of live world state, so hashing "up to the count"
    /// would leave any tail past it invisible and falsify the "every
    /// number changes the hash" contract SimConfigHashTests pins.
    /// ObstacleCount/WallCount are ALSO hashed, as ordinary scalar fields
    /// at their own declared position — the length-prefix step above is
    /// independent of them and does not substitute for hashing them too.
    ///
    /// A null array hashes as a length marker of -1, distinguishable from
    /// an empty array's length of 0, and never throws: validating that a
    /// balance array is non-null is SimConfigBuilder.Validate's job, not
    /// this class's — hand-built SimConfig fixtures in tests never reach
    /// the builder at all.
    ///
    /// Field order below mirrors SimConfig's own declaration order (Hero,
    /// Weapon, Chaser, Gunner, Wave, Arena, Visibility) and, within each
    /// section, that struct's own declared field order — same
    /// canonical-order convention as SimulationWorld.StateHash()'s
    /// Hash*/ helpers.
    public static class SimConfigHash
    {
        public static ulong Compute(in SimConfig cfg)
        {
            ulong h = StateHash64.Begin();
            h = HashHero(h, in cfg.Hero);
            h = HashWeapon(h, in cfg.Weapon);
            h = HashMob(h, in cfg.Chaser);
            h = HashMob(h, in cfg.Gunner);
            h = HashWave(h, in cfg.Wave);
            h = HashArena(h, in cfg.Arena);
            h = HashVisibility(h, in cfg.Visibility);
            return h;
        }

        static ulong HashHero(ulong h, in HeroSimConfig c)
        {
            h = StateHash64.Add(h, c.MaxSpeed); h = StateHash64.Add(h, c.Accel);
            h = StateHash64.Add(h, c.Friction); h = StateHash64.Add(h, c.Radius);
            h = StateHash64.Add(h, c.MaxHp); h = StateHash64.Add(h, c.DashSpeed);
            h = StateHash64.Add(h, c.DashDuration); h = StateHash64.Add(h, c.DashCooldown);
            h = StateHash64.Add(h, c.DashIframes); h = StateHash64.Add(h, c.DashBufferWindow);
            h = StateHash64.Add(h, c.LegsTop); h = StateHash64.Add(h, c.BodyTop);
            h = StateHash64.Add(h, c.HeadTop); h = StateHash64.Add(h, c.LegsDamageMult);
            h = StateHash64.Add(h, c.BodyDamageMult); h = StateHash64.Add(h, c.HeadDamageMult);
            h = StateHash64.Add(h, c.SlideProfileTop); h = StateHash64.Add(h, c.MuzzleHeight);
            h = StateHash64.Add(h, c.SlideMuzzleHeight); h = StateHash64.Add(h, c.MaxAimHeight);
            h = StateHash64.Add(h, c.StaminaMax); h = StateHash64.Add(h, c.DashStaminaCost);
            h = StateHash64.Add(h, c.SlideStaminaCost); h = StateHash64.Add(h, c.StaminaRegenPerSec);
            h = StateHash64.Add(h, c.StaminaRegenDelay); h = StateHash64.Add(h, c.LinkRefund);
            h = StateHash64.Add(h, c.SlideSpeed); h = StateHash64.Add(h, c.SlideDuration);
            h = StateHash64.Add(h, c.SlideSteerRadPerSec); h = StateHash64.Add(h, c.SlideMinSpeedFrac);
            h = StateHash64.Add(h, c.RunUpSeconds); h = StateHash64.Add(h, c.RunUpDecayMult);
            h = StateHash64.Add(h, c.SlideBufferWindow); h = StateHash64.Add(h, c.LinkWindowSeconds);
            h = StateHash64.Add(h, c.PostDashSlideWindow); h = StateHash64.Add(h, c.SlideWallStopDot);
            h = StateHash64.Add(h, c.RicochetRetention);
            h = StateHash64.Add(h, c.AimMoveSpeedFrac); h = StateHash64.Add(h, c.AimSlideSpeedMult);
            h = StateHash64.Add(h, c.AimSettleSeconds);
            h = StateHash64.Add(h, c.EdgeRequestMinTicks);
            return h;
        }

        static ulong HashWeapon(ulong h, in WeaponSimConfig c)
        {
            h = StateHash64.Add(h, c.FireInterval); h = StateHash64.Add(h, c.ProjectileSpeed);
            h = StateHash64.Add(h, c.ProjectileRadius); h = StateHash64.Add(h, c.ProjectileLifetime);
            h = StateHash64.Add(h, c.Damage); h = StateHash64.Add(h, c.SpreadRad);
            h = StateHash64.Add(h, c.RecoilPerShotRad); h = StateHash64.Add(h, c.RecoilRecoveryRadPerSec);
            h = StateHash64.Add(h, c.RecoilMaxRad); h = StateHash64.Add(h, c.MuzzleOffset);
            h = StateHash64.Add(h, c.CanFireWhileDash);
            h = StateHash64.Add(h, c.CanFireWhileSlide);
            h = StateHash64.Add(h, c.SpreadRunMult); h = StateHash64.Add(h, c.SpreadSlideMult);
            h = StateHash64.Add(h, c.RunSpreadSpeedFrac);
            return h;
        }

        /// Shared shape of both MobSimConfig instances (Chaser/Gunner) —
        /// SimConfig declares them as one comma-joined field pair, so this
        /// hashes each in turn, at its own call site in Compute.
        static ulong HashMob(ulong h, in MobSimConfig c)
        {
            h = StateHash64.Add(h, c.MaxSpeed); h = StateHash64.Add(h, c.Accel);
            h = StateHash64.Add(h, c.Radius); h = StateHash64.Add(h, c.MaxHp);
            h = StateHash64.Add(h, c.ContactDamage); h = StateHash64.Add(h, c.AttackRange);
            h = StateHash64.Add(h, c.TelegraphSeconds); h = StateHash64.Add(h, c.AttackCooldown);
            h = StateHash64.Add(h, c.PreferredRange); h = StateHash64.Add(h, c.RangeTolerance);
            h = StateHash64.Add(h, c.StrafeSpeed); h = StateHash64.Add(h, c.FireInterval);
            h = StateHash64.Add(h, c.ProjectileSpeed); h = StateHash64.Add(h, c.ProjectileRadius);
            h = StateHash64.Add(h, c.ProjectileLifetime); h = StateHash64.Add(h, c.ProjectileDamage);
            h = StateHash64.Add(h, c.LeadFactor); h = StateHash64.Add(h, c.SeparationRadius);
            h = StateHash64.Add(h, c.SeparationStrength); h = StateHash64.Add(h, c.AvoidLookahead);
            h = StateHash64.Add(h, c.LegsTop); h = StateHash64.Add(h, c.BodyTop);
            h = StateHash64.Add(h, c.HeadTop); h = StateHash64.Add(h, c.LegsDamageMult);
            h = StateHash64.Add(h, c.BodyDamageMult); h = StateHash64.Add(h, c.HeadDamageMult);
            h = StateHash64.Add(h, c.MuzzleHeight);
            h = StateHash64.Add(h, c.SwingLeadFactor); h = StateHash64.Add(h, c.SwingLeadMaxMeters);
            h = StateHash64.Add(h, c.AvoidMargin);
            return h;
        }

        static ulong HashWave(ulong h, in WaveSimConfig c)
        {
            h = StateHash64.Add(h, c.FirstWaveDelay); h = StateHash64.Add(h, c.WavePause);
            h = StateHash64.Add(h, c.SpawnRingInset); h = StateHash64.Add(h, c.MinSpawnDistanceToPlayer);
            h = StateHash64.Add(h, c.BaseCount); h = StateHash64.Add(h, c.CountGrowth);
            h = StateHash64.Add(h, c.MaxMobsPerWave); h = StateHash64.Add(h, c.MaxSpawnAttempts);
            h = StateHash64.Add(h, c.FallbackSlots);
            h = StateHash64.Add(h, c.GunnerShareBase); h = StateHash64.Add(h, c.GunnerShareGrowth);
            h = StateHash64.Add(h, c.PerPlayerCountFrac);
            return h;
        }

        static ulong HashArena(ulong h, in ArenaSimConfig c)
        {
            h = StateHash64.Add(h, c.Radius);
            h = StateHash64.Add(h, c.ObstacleCount);
            h = HashFloat2Array(h, c.ObstaclePos);
            h = HashFloatArray(h, c.ObstacleRadius);
            h = StateHash64.Add(h, c.MaxMobs); h = StateHash64.Add(h, c.MaxProjectiles);
            h = StateHash64.Add(h, c.MaxEventsPerFrame);
            h = StateHash64.Add(h, c.MaxPlayers); h = StateHash64.Add(h, c.PlayerSpawnRingFrac);
            h = StateHash64.Add(h, c.WallCount);
            h = HashFloat2Array(h, c.WallA);
            h = HashFloat2Array(h, c.WallB);
            h = HashFloatArray(h, c.WallHalfWidth);
            return h;
        }

        static ulong HashVisibility(ulong h, in VisibilitySimConfig c)
        {
            h = StateHash64.Add(h, c.SightRadius); h = StateHash64.Add(h, c.HearRadius);
            h = StateHash64.Add(h, c.ExitHysteresis); h = StateHash64.Add(h, c.LingerTicks);
            h = StateHash64.Add(h, c.HearPositionGridMeters);
            return h;
        }

        /// A null array hashes as length marker -1 (never throws — see
        /// class doc); a real array hashes as its length followed by every
        /// element, so an added/removed/changed tail element always moves
        /// the hash — reading "up to a paired count field" instead would
        /// not.
        static ulong HashFloatArray(ulong h, float[] a)
        {
            h = StateHash64.Add(h, a == null ? -1 : a.Length);
            if (a == null) return h;
            for (int i = 0; i < a.Length; i++) h = StateHash64.Add(h, a[i]);
            return h;
        }

        static ulong HashFloat2Array(ulong h, float2[] a)
        {
            h = StateHash64.Add(h, a == null ? -1 : a.Length);
            if (a == null) return h;
            for (int i = 0; i < a.Length; i++) h = StateHash64.Add(h, a[i]);
            return h;
        }
    }
}
