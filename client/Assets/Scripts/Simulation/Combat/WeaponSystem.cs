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
        public static void Update(SimulationWorld w, ref PlayerState p, in SimInput input)
        {
            float dt = SimulationWorld.TickDt;
            var cfg = w.Config.Weapon;

            p.FireCooldown -= dt;
            p.RecoilOffset = math.max(0f, p.RecoilOffset - cfg.RecoilRecoveryRadPerSec * dt);

            // p.Alive is redundant today — SimulationWorld.Tick (Task 23) only calls
            // this from its Alive branch — kept as defense-in-depth so the system
            // stays safe on a direct/future call site, not just its current caller.
            bool canFire = input.FireHeld && p.Alive
                && (cfg.CanFireWhileDash || p.DashTimer <= 0f);
            if (!canFire)
            {
                // Clamp the floor so releasing-and-holding fire again doesn't cash in
                // a backlog of overshoot ticks as a burst — release means "reset to idle".
                p.FireCooldown = math.max(0f, p.FireCooldown);
                return;
            }

            // Safety net against an infinite loop if FireInterval is misconfigured to 0.
            float interval = math.max(cfg.FireInterval, 1e-3f);
            ref MatchStats stats = ref w.StatsRef;
            while (p.FireCooldown <= 0f)
            {
                float overshoot = math.min(-p.FireCooldown, dt);
                float2 baseDir = math.normalizesafe(input.AimPoint - p.Pos, new float2(1f, 0f));
                float a = cfg.SpreadRad + p.RecoilOffset;
                float angle = w.SpreadRng.NextFloat(-a, a);
                float2 dir = Geometry.Rotate(baseDir, angle);
                float2 spawnPos = p.Pos + dir * (cfg.MuzzleOffset + overshoot * cfg.ProjectileSpeed);
                float2 vel = dir * cfg.ProjectileSpeed;
                w.SpawnProjectile(ProjectileOwner.Player, spawnPos, vel,
                    cfg.Damage, cfg.ProjectileRadius, cfg.ProjectileLifetime);

                p.RecoilOffset = math.min(cfg.RecoilMaxRad, p.RecoilOffset + cfg.RecoilPerShotRad);
                stats.ShotsFired++;
                p.FireCooldown += interval;
            }
        }
    }
}
