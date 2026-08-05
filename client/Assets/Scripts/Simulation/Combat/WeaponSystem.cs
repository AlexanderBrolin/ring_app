using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    internal static class WeaponSystem
    {
        /// Advances the weapon by one tick (spec §3.5): cooldown always ticks down,
        /// recoil always decays, and while FireHeld stays true the cooldown's
        /// fractional remainder carries into the next shot (no rounding drift) —
        /// possibly firing more than once per tick if dt outpaces the interval.
        ///
        /// Two fire modes (spec §3.2 v5, Task 15) share every line of that
        /// bookkeeping — including recoil accumulation — and part ways only on the
        /// shot's geometry and its cone: aimed fire (input.AimHeld) sends a genuine
        /// 3D round at (AimPoint, AimHeight) through a cone the aim-settle shrinks,
        /// hip fire keeps the flat horizontal shot through the movement-widened
        /// Spread.HipRadians cone. Both draw from the weapon RNG stream only when
        /// their cone is actually open, so perfectly settled recoil-free aim spends
        /// no randomness at all.
        /// `index` (Stage 2 Task 5) is the firing player's own index — ShotsFired
        /// is a personal counter, so it must land on THAT player's own MatchStats
        /// slot, not always player 0's.
        public static void Update(SimulationWorld w, ref PlayerState p, in SimInput input, int index)
        {
            float dt = SimulationWorld.TickDt;
            var cfg = w.Config.Weapon;
            // Task 15 (QC21): the fire branch reads the hero half of the config too
            // — muzzle heights (standing / mid-slide) and the aim-settle window.
            var hero = w.Config.Hero;

            p.FireCooldown -= dt;
            p.RecoilOffset = math.max(0f, p.RecoilOffset - cfg.RecoilRecoveryRadPerSec * dt);

            // p.Alive is redundant today — SimulationWorld.Tick (Task 23) only calls
            // this from its Alive branch — kept as defense-in-depth so the system
            // stays safe on a direct/future call site, not just its current caller.
            bool canFire = input.FireHeld && p.Alive
                && (cfg.CanFireWhileDash || p.DashTimer <= 0f)
                && (cfg.CanFireWhileSlide || p.SlideTimer <= 0f);
            if (!canFire)
            {
                // Clamp the floor so releasing-and-holding fire again doesn't cash in
                // a backlog of overshoot ticks as a burst — release means "reset to idle".
                p.FireCooldown = math.max(0f, p.FireCooldown);
                return;
            }

            // Safety net against an infinite loop if FireInterval is misconfigured to 0.
            float interval = math.max(cfg.FireInterval, 1e-3f);
            ref MatchStats stats = ref w.StatsRef(index);
            while (p.FireCooldown <= 0f)
            {
                float overshoot = math.min(-p.FireCooldown, dt);
                float muzzleH = p.SlideTimer > 0f ? hero.SlideMuzzleHeight : hero.MuzzleHeight;
                float a; float3 vel3;
                if (input.AimHeld)
                {
                    // Aimed fire (Task 15): the round is a full 3D vector from the
                    // muzzle to the aimed point, and the base cone shrinks as the
                    // aim settles — but recoil never leaves it (D15: a spray is
                    // never a laser, however settled the aim is).
                    float settle = p.AimSettleTimer / hero.AimSettleSeconds;   // [0..1]
                    a = p.RecoilOffset + cfg.SpreadRad * (1f - settle);
                    float2 baseDir2 = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                    float3 target3 = new float3(input.AimPoint, input.AimHeight);
                    float3 muzzle3 = new float3(p.Pos + baseDir2 * cfg.MuzzleOffset, muzzleH);
                    vel3 = math.normalizesafe(target3 - muzzle3, new float3(baseDir2, 0f))
                        * cfg.ProjectileSpeed;
                }
                else
                {
                    // Hip fire: the flat Phase-1 geometry, widened by movement.
                    a = Spread.HipRadians(in cfg, in p, in hero);
                    float2 dir2 = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                    vel3 = new float3(dir2 * cfg.ProjectileSpeed, 0f);
                }
                if (a > 0f)   // both modes draw — and only when there is a cone to draw from
                {
                    float angle = w.SpreadRng.NextFloat(-a, a);
                    // Rotation around the VERTICAL axis only (K10): the horizontal
                    // pair turns, the climb rate rides along untouched, and the
                    // renormalise keeps |vel3| at exactly ProjectileSpeed.
                    float2 rotated = Geometry.Rotate(vel3.xy, angle);
                    vel3 = math.normalizesafe(new float3(rotated, vel3.z), vel3) * cfg.ProjectileSpeed;
                }
                // K9: the fractional-remainder pre-advance walks the round along its
                // OWN line — horizontally by its horizontal speed, vertically by its
                // climb rate — so an aimed shot still passes through the aimed point.
                float2 dir2D = math.normalizesafe(vel3.xy,
                    math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f)));
                float horizSpeed = math.length(vel3.xy);
                float2 spawnPos = p.Pos + dir2D * (cfg.MuzzleOffset + overshoot * horizSpeed);
                float height = muzzleH + overshoot * vel3.z;
                // ownerIndex (Stage 2 Task 7): this firing player's own index —
                // drives per-shooter ShotsHit/Kills credit (SimulationWorld.DamageMob).
                w.SpawnProjectile(ProjectileOwner.Player, (byte)index, spawnPos, vel3.xy, height, vel3.z,
                    cfg.Damage, cfg.ProjectileRadius, cfg.ProjectileLifetime);

                p.RecoilOffset = math.min(cfg.RecoilMaxRad, p.RecoilOffset + cfg.RecoilPerShotRad);
                stats.ShotsFired++;
                p.FireCooldown += interval;
            }
        }
    }
}
