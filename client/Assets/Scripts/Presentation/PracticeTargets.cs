#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Milestone-2 target dummies (spec Interfaces, Task 17): spawns a ring of mob
    /// placeholders through the dev-only `SimulationWorld.DevSpawnMob` seam so
    /// there is something to shoot at before the real `WaveSystem` exists
    /// (Phase 7). Entirely stripped from production builds by the compile guard
    /// above; the whole class is deleted once Phase 7 wires real wave spawning.
    public sealed class PracticeTargets : MonoBehaviour
    {
        const int TargetCount = 6;
        const float MinRadius = 8f;
        const float MaxRadius = 12f;

        [SerializeField] SimulationRunner _runner;

        // Which `SimulationWorld` instance the ring has already been spawned for —
        // not a bool, so it needs no explicit reset on `WorldRestarted` (a restart
        // swaps in a brand-new `SimulationWorld`, so the reference comparison in
        // `SpawnTargets` fails open on its own). Unity does not guarantee Awake/
        // OnEnable ordering across different GameObjects, so `OnEnable`'s
        // `WorldRestarted` subscription and `Start`'s explicit call below can race
        // (either one, or both, may fire for the same world depending on whether
        // this object happens to initialize before or after `SimulationRunner`);
        // this field makes `SpawnTargets` idempotent per world so the outcome is
        // exactly one ring regardless of that order.
        SimulationWorld _spawnedForWorld;

        void OnEnable() => _runner.WorldRestarted += SpawnTargets;

        void OnDisable() => _runner.WorldRestarted -= SpawnTargets;

        // Start, not Awake: guaranteed to run only after every object's Awake and
        // OnEnable have completed, so `_runner.World` is never null here. Covers
        // the case where this object's own `OnEnable` — and so its `WorldRestarted`
        // subscription — happens to run only after `SimulationRunner.Awake()` has
        // already fired the very first `WorldRestarted` (the common case, since
        // that first invocation happens from inside another object's Awake). If
        // instead this object initializes first and does catch that first event,
        // `_spawnedForWorld` below makes this call a no-op.
        void Start() => SpawnTargets();

        void SpawnTargets()
        {
            SimulationWorld world = _runner.World;
            if (world == null || world == _spawnedForWorld) return;
            _spawnedForWorld = world;

            for (int i = 0; i < TargetCount; i++)
            {
                float t = i / (float)TargetCount;
                float angle = t * math.PI * 2f;
                float radius = math.lerp(MinRadius, MaxRadius, t);
                float2 pos = new float2(math.cos(angle), math.sin(angle)) * radius;
                MobType type = (i % 2 == 0) ? MobType.Chaser : MobType.Gunner;
                world.DevSpawnMob(type, pos);
            }
        }
    }
}
#endif
