using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Shared fixture builders for tests that need a "loaded" world instead of a
    /// bare `new SimulationWorld(seed, TestConfigs.Default())` — introduced by
    /// Task 29 (golden hash / allocation tests) so those two don't each carry
    /// their own copy of the saturation setup. Existing tests are untouched:
    /// only new Task 29 tests consume this.
    public static class TestWorlds
    {
        /// A world with every mob slot filled (via the SpawnMobForTest seam,
        /// half Chaser/half Gunner so both fire/movement/AI paths are live) and
        /// warmed up under sustained player fire for ~100 ticks, so its
        /// projectile/event population has already reached steady state before
        /// a caller starts measuring it (allocations, a "busy" golden tick,
        /// ...). Returns the config used so callers can read Arena caps etc.
        /// without reconstructing it.
        public static SimulationWorld Saturated(out SimConfig config)
        {
            config = TestConfigs.Default();
            var world = new SimulationWorld(1, config);

            int cap = config.Arena.MaxMobs;
            const float goldenAngleRad = 2.399963f; // even angular spread, no periodicity
            for (int i = 0; i < cap; i++)
            {
                float radius = 4f + (i % 24) * 1.2f; // well inside Arena.Radius (35)
                float angle = i * goldenAngleRad;
                float2 pos = radius * new float2(math.cos(angle), math.sin(angle));
                world.SpawnMobForTest((i & 1) == 0 ? MobType.Chaser : MobType.Gunner, pos);
            }

            var holdFire = new SimInput { FireHeld = true, AimPoint = new float2(30f, 0f) };
            for (int i = 0; i < 100; i++) world.Tick(holdFire);

            return world;
        }

        /// Places a batch of mobs in one call (Task 6) — the tuple form keeps a
        /// multi-mob fixture readable as a single statement instead of a column
        /// of SpawnMobForTest calls. Slot order equals argument order, which the
        /// candidate tie-break tests depend on.
        public static void SpawnMobsAt(SimulationWorld world,
            params (MobType type, float2 pos)[] mobs)
        {
            for (int i = 0; i < mobs.Length; i++)
                world.SpawnMobForTest(mobs[i].type, mobs[i].pos);
        }

        /// Test-only 3D-aimed shot (Task 6): builds the velocity from the unit
        /// 3D direction (origin, muzzleH) → (targetXY, targetH) scaled by
        /// Weapon.ProjectileSpeed, so the projectile's height at horizontal
        /// distance d is exactly muzzleH + d·(targetH − muzzleH)/|targetXY −
        /// origin| — the straight line a height-gating fixture reasons about.
        /// Weapon damage/radius/lifetime come from the world's own config, so a
        /// caller only ever states geometry.
        public static int FireAimed3D(SimulationWorld world, float2 origin, float muzzleH,
            float2 targetXY, float targetH)
        {
            WeaponSimConfig weapon = world.Config.Weapon;
            float2 flat = targetXY - origin;
            float dz = targetH - muzzleH;
            float len = math.sqrt(math.lengthsq(flat) + dz * dz);
            float2 dir = len > 1e-6f ? flat / len : new float2(1f, 0f);
            float velZ = len > 1e-6f ? dz / len * weapon.ProjectileSpeed : 0f;
            return world.SpawnProjectileForTest(ProjectileOwner.Player, origin,
                dir * weapon.ProjectileSpeed, muzzleH, velZ,
                weapon.Damage, weapon.ProjectileRadius, weapon.ProjectileLifetime);
        }

        /// Ticks with idle input until no projectile is left in flight (or the
        /// tick budget runs out) and returns how many ticks that took. `maxTicks`
        /// is a stall guard, not an expectation: a fixture whose mobs shoot back
        /// legitimately runs to the cap, so callers assert on world state, never
        /// on the return value being below it.
        public static int RunUntilProjectilesDie(SimulationWorld world, int maxTicks = 120)
        {
            int ticks = 0;
            while (ticks < maxTicks && world.ProjectileCount > 0)
            {
                world.Tick(default);
                ticks++;
            }
            return ticks;
        }

        /// Ticks `world` with idle input until its first wave has folded into
        /// `WorldStats.WavesCleared` (Stage 2 Task 5) — every mob that spawns is
        /// killed outright via the DamageMob seam (same swap-remove path a real
        /// kill takes) the instant it appears, so the wave's own debt-then-
        /// MobCount==0 clear check (WaveSystem.Update) fires without needing
        /// weapon fire to land. `maxTicks` covers TestConfigs.Default()'s
        /// FirstWaveDelay (75 ticks) plus spawn/clear settling with generous
        /// headroom; it is a stall guard, not an expectation — callers assert on
        /// WorldStats.WavesCleared itself.
        public static void ClearFirstWave(SimulationWorld world, int maxTicks = 300)
        {
            var inputs = new SimInput[world.PlayerCount];
            for (int t = 0; t < maxTicks && world.WorldStats.WavesCleared == 0; t++)
            {
                world.TickAll(inputs);
                while (world.MobCount > 0)
                    world.DamageMob(0, 1e9f, world.Mobs[0].Pos, HitZone.Body, default);
            }
        }
    }
}
