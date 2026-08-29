using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// Balance-parity hash for the network handshake (Stage 2 Task 23, spec
    /// §3.8, Р52): FNV-1a 64 over EVERY balance number of SimConfig, in
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
    /// Weapon, Chaser, Gunner, Wave, Arena, Visibility, Flow, Elite,
    /// Director, Loot, Items) and, within each section, that struct's own
    /// declared field order — same canonical-order convention as
    /// SimulationWorld.StateHash()'s Hash*/ helpers.
    ///
    /// Stage 3 Task 13 (owner decision R-17): `Compute` covers all twelve
    /// fields — Flow/Elite/Director/Loot/Items joined this task, lifting
    /// the deferred-wiring skip-set (SimConfigHashTests' former
    /// PendingHashFields) in one move, arrays included.
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
            h = HashFlow(h, in cfg.Flow);
            h = HashMob(h, in cfg.Elite);
            h = HashMob(h, in cfg.Director);
            h = HashLoot(h, in cfg.Loot);
            h = HashItemArray(h, cfg.Items);
            return h;
        }

        static ulong HashHero(ulong h, in HeroSimConfig c)
        {
            h = StateHash64.Add(h, c.MaxSpeed); h = StateHash64.Add(h, c.Accel);
            h = StateHash64.Add(h, c.Friction); h = StateHash64.Add(h, c.Radius);
            h = StateHash64.Add(h, c.MaxHp); h = StateHash64.Add(h, c.DashSpeed);
            h = StateHash64.Add(h, c.DashDuration); h = StateHash64.Add(h, c.DashCooldown);
            h = StateHash64.Add(h, c.DashIframes); h = StateHash64.Add(h, c.DashBufferWindow);
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
            h = StateHash64.Add(h, c.PickupRadius);
            h = StateHash64.Add(h, c.InventoryCapacity); h = StateHash64.Add(h, c.MaxInventoryItems);
            // app-88jb Т1 (spec §3.2): impact physics — same field order as
            // HeroSimConfig's own declaration.
            h = StateHash64.Add(h, c.Mass); h = StateHash64.Add(h, c.ImpactSpeedCap);
            h = StateHash64.Add(h, c.CocoonDamping);
            h = StateHash64.Add(h, c.CenterOfMassHeight); h = StateHash64.Add(h, c.TiltDampingRatio);
            h = StateHash64.Add(h, c.TiltSettleSeconds); h = StateHash64.Add(h, c.TiltGain);
            // app-88jb Т22 (spec §3.5): body-collision numbers.
            h = StateHash64.Add(h, c.MaxDepenetrationPerTick);
            h = StateHash64.Add(h, c.PushRecoilFraction);
            h = StateHash64.Add(h, c.SlideThrustRecovery);
            // app-88jb Т13 (spec §3.3): the collector's hit parts.
            h = HashHitPartArray(h, c.Parts);
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
            h = StateHash64.Add(h, c.ShotsPerCell); h = StateHash64.Add(h, c.AmmoStart);
            h = StateHash64.Add(h, c.AmmoMax); h = StateHash64.Add(h, c.EmergencyFireInterval);
            // app-88jb Т1 (spec §3.2): impact physics.
            h = StateHash64.Add(h, c.ProjectileMass);
            // app-88jb Т19 (spec §3.4): the ricochet numbers, in the section's
            // own declaration order. They join the digest IN THE STEP THAT
            // DECLARES THEM, not later, and that is a rule rather than a
            // preference: Stage 3 Task 13 (owner decision R-17) deleted the
            // flat PENDING skip-set SimConfigHashTests used to consult, so
            // there is no third case left besides "wired" and "an array the
            // caller listed" -- a scalar declared here and left out of this
            // fold is red in EveryConfigNumberAffectsHash_Weapon on sight
            // (SimConfigHashTests.cs:304-316, its own doc says so).
            h = StateHash64.Add(h, c.MaxRicochets);
            h = StateHash64.Add(h, c.RicochetRetention);
            h = StateHash64.Add(h, c.RicochetMinSpeed);
            // app-88jb Т20 (spec §3.4): the two piercing numbers, folded in the
            // step that DECLARES them for the reason the note above gives.
            h = StateHash64.Add(h, c.PierceMassRatio);
            h = StateHash64.Add(h, c.PierceDamageLoss);
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
            h = StateHash64.Add(h, c.MuzzleHeight);
            h = StateHash64.Add(h, c.SwingLeadFactor); h = StateHash64.Add(h, c.SwingLeadMaxMeters);
            h = StateHash64.Add(h, c.AvoidMargin);
            // app-88jb Т1 (spec §3.2): impact physics — same field order as
            // MobSimConfig's own declaration.
            h = StateHash64.Add(h, c.Mass); h = StateHash64.Add(h, c.ImpactSpeedCap);
            h = StateHash64.Add(h, c.ProjectileMass);
            h = StateHash64.Add(h, c.CenterOfMassHeight); h = StateHash64.Add(h, c.TiltDampingRatio);
            h = StateHash64.Add(h, c.TiltSettleSeconds); h = StateHash64.Add(h, c.TiltGain);
            h = StateHash64.Add(h, c.TiltFallAngle); h = StateHash64.Add(h, c.DownedSeconds);
            // app-88jb Т13 (spec §3.3): this archetype's hit parts.
            h = HashHitPartArray(h, c.Parts);
            // app-88jb Т19 (spec §3.4): this archetype's ricochet numbers,
            // wired in the same step that declares them for the reason
            // HashWeapon's own note above gives (R-17 left no skip-set).
            h = StateHash64.Add(h, c.MaxRicochets);
            h = StateHash64.Add(h, c.RicochetRetention);
            h = StateHash64.Add(h, c.RicochetMinSpeed);
            // app-88jb Т20 (spec §3.4): this archetype's piercing numbers,
            // wired in the same step that declares them, same reason again.
            h = StateHash64.Add(h, c.PierceMassRatio);
            h = StateHash64.Add(h, c.PierceDamageLoss);
            // app-88jb Т22 (spec §3.5, decision Р442): this archetype's reaction
            // share, wired in the same step that declares it, same reason again.
            h = StateHash64.Add(h, c.PushRecoilFraction);
            return h;
        }

        static ulong HashWave(ulong h, in WaveSimConfig c)
        {
            h = StateHash64.Add(h, c.FirstWaveDelay);
            h = StateHash64.Add(h, c.SpawnRingInset); h = StateHash64.Add(h, c.MinSpawnDistanceToPlayer);
            h = StateHash64.Add(h, c.BaseCount); h = StateHash64.Add(h, c.CountGrowth);
            h = StateHash64.Add(h, c.MaxMobsPerWave); h = StateHash64.Add(h, c.MaxSpawnAttempts);
            h = StateHash64.Add(h, c.FallbackSlots);
            h = StateHash64.Add(h, c.GunnerShareBase); h = StateHash64.Add(h, c.GunnerShareGrowth);
            h = StateHash64.Add(h, c.PerPlayerCountFrac);
            h = StateHash64.Add(h, c.EliteShareMiddle); h = StateHash64.Add(h, c.EliteShareOuterGrowth);
            h = StateHash64.Add(h, c.EliteShareOuterCap);
            // Task Т2 (app-ggvz, spec §3.8): the four per-zone wave cadence
            // numbers, on the same "two arrays via the existing element-wise
            // helpers, two scalars via StateHash64.Add" shape as everything
            // else in this method.
            h = HashFloatArray(h, c.WavePauseByZone);
            h = HashInt32Array(h, c.MaxAliveByZone);
            h = StateHash64.Add(h, c.MaxSpawnsPerZonePerTick);
            h = StateHash64.Add(h, c.DifficultyStepSeconds);
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
            h = StateHash64.Add(h, c.BarrierTop);
            // Stage 3 Task 13 (owner decision R-17): the zone/door/portal/
            // container fields this task lifts out of the deferred-wiring
            // skip-set — last, because ArenaSimConfig declares them last;
            // this method's contract is the struct's own field order.
            h = StateHash64.Add(h, c.MaxPickups);
            h = HashFloatArray(h, c.ZoneRadius);
            h = StateHash64.Add(h, c.ZoneWallCount);
            h = HashFloatArray(h, c.ZoneWallRadius);
            h = HashFloatArray(h, c.ZoneWallHalfWidth);
            h = HashInt32Array(h, c.ZoneWallDoorStart);
            h = HashInt32Array(h, c.ZoneWallDoorCount);
            h = HashFloatArray(h, c.DoorCenterRad);
            h = HashFloatArray(h, c.DoorFreeWidth);
            h = StateHash64.Add(h, c.DoorClearance);
            h = HashFloat2Array(h, c.ExtractPos);
            h = HashByteArray(h, c.ExtractZone);
            h = HashByteArray(h, c.ExtractKind);
            h = StateHash64.Add(h, c.ExtractRadius);
            h = StateHash64.Add(h, c.MaxContainers);
            h = StateHash64.Add(h, c.MaxContainerSlots);
            // app-88jb Т22 (spec §3.5, decision Р413).
            h = StateHash64.Add(h, c.RelaxIterations);
            // app-88jb Т24 (spec §3.6, decision Н24/Р407): the rewind cap and
            // the picture half of it — last, because ArenaSimConfig declares
            // them last (this method's contract is the struct's own field
            // order, stated above). Both change what a shot HITS, so two
            // peers running different values must not be able to agree on a
            // config digest.
            h = StateHash64.Add(h, c.RewindCapTicks);
            h = StateHash64.Add(h, c.RewindPictureTicks);
            return h;
        }

        static ulong HashVisibility(ulong h, in VisibilitySimConfig c)
        {
            h = StateHash64.Add(h, c.SightRadius); h = StateHash64.Add(h, c.HearRadius);
            h = StateHash64.Add(h, c.ExitHysteresis); h = StateHash64.Add(h, c.LingerTicks);
            h = StateHash64.Add(h, c.HearPositionGridMeters);
            h = StateHash64.Add(h, c.PickupRadiusForVisibility);
            h = StateHash64.Add(h, c.ContainerRadiusForVisibility);
            return h;
        }

        static ulong HashFlow(ulong h, in MatchFlowSimConfig c)
        {
            h = StateHash64.Add(h, c.GateDelaySeconds); h = StateHash64.Add(h, c.ExtractChannelSeconds);
            h = StateHash64.Add(h, c.RetinueCount); h = StateHash64.Add(h, c.RetinueRespawnSeconds);
            h = StateHash64.Add(h, c.DirectorReserveSlots);
            return h;
        }

        /// Field order mirrors LootSimConfig's own declaration order.
        static ulong HashLoot(ulong h, in LootSimConfig c)
        {
            h = HashFloatArray(h, c.DropChance);
            h = StateHash64.Add(h, c.CrateCount); h = StateHash64.Add(h, c.CacheCountMiddle);
            h = StateHash64.Add(h, c.CacheCountCore);
            h = StateHash64.Add(h, c.RepairKitChance);
            h = HashInt32Array(h, c.CellsPerMob);
            h = StateHash64.Add(h, c.CorpseCellFraction);
            h = StateHash64.Add(h, c.RepairKitHealAmount);
            h = StateHash64.Add(h, c.RepairKitChannelSeconds);
            h = HashFloatArray(h, c.TransferSeconds);
            h = StateHash64.Add(h, c.LootSpawnAttempts); h = StateHash64.Add(h, c.LootFallbackSlots);
            h = StateHash64.Add(h, c.PickupTtlSeconds); h = StateHash64.Add(h, c.ContainerTtlSeconds);
            h = StateHash64.Add(h, c.LootRadius);
            return h;
        }

        /// One entry's worth of ItemDef, in the struct's own declared field
        /// order — same "length + every element" shape as HashFloatArray/
        /// HashFloat2Array, so a catalog that only grows a tail record still
        /// moves the hash.
        static ulong HashItemArray(ulong h, ItemDef[] a)
        {
            h = StateHash64.Add(h, a == null ? -1 : a.Length);
            if (a == null) return h;
            for (int i = 0; i < a.Length; i++)
            {
                h = StateHash64.Add(h, a[i].Id);
                h = StateHash64.Add(h, a[i].Tier);
                h = StateHash64.Add(h, a[i].SlotCost);
                h = StateHash64.Add(h, a[i].CreditValue);
                h = StateHash64.Add(h, (int)a[i].Kind);
            }
            return h;
        }

        /// app-88jb Т13: one HitPart's worth per element, in the struct's own
        /// declared field order — the same "length + every element" shape as
        /// HashItemArray above, so a body that grows only a TAIL part still
        /// moves the hash.
        ///
        /// app-88jb Т15: the six column scalars that used to be hashed beside
        /// this array — three zone tops and three per-zone multipliers — left
        /// HeroSimConfig/MobSimConfig and this digest IN THE SAME STEP, and the
        /// order was not a matter of taste. Dropping them from the digest while
        /// they were still fields would have reddened SimConfigHashTests'
        /// reflective sweep on every one of them at once: a field present in
        /// the struct and absent from the digest is exactly what that sweep
        /// exists to catch.
        static ulong HashHitPartArray(ulong h, HitPart[] a)
        {
            h = StateHash64.Add(h, a == null ? -1 : a.Length);
            if (a == null) return h;
            for (int i = 0; i < a.Length; i++)
            {
                h = StateHash64.Add(h, a[i].Radius);
                h = StateHash64.Add(h, a[i].Bottom);
                h = StateHash64.Add(h, a[i].Top);
                h = StateHash64.Add(h, (int)a[i].Zone);
                h = StateHash64.Add(h, a[i].DamageMult);
            }
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

        /// Same shape as HashFloatArray, for int[] fields — Arena's
        /// ZoneWallDoorStart/ZoneWallDoorCount and LootSimConfig.CellsPerMob.
        static ulong HashInt32Array(ulong h, int[] a)
        {
            h = StateHash64.Add(h, a == null ? -1 : a.Length);
            if (a == null) return h;
            for (int i = 0; i < a.Length; i++) h = StateHash64.Add(h, a[i]);
            return h;
        }

        /// Same shape as HashFloatArray, for byte[] fields — Arena's
        /// ExtractZone/ExtractKind.
        static ulong HashByteArray(ulong h, byte[] a)
        {
            h = StateHash64.Add(h, a == null ? -1 : a.Length);
            if (a == null) return h;
            for (int i = 0; i < a.Length; i++) h = StateHash64.Add(h, a[i]);
            return h;
        }
    }
}
