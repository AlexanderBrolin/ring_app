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

        void OnEnable() => _runner.WorldRestarted += SpawnTargets;

        void OnDisable() => _runner.WorldRestarted -= SpawnTargets;

        // Start, not Awake: guaranteed to run only after every object's Awake and
        // OnEnable have completed, so `_runner.World` is never null here. The very
        // first `WorldRestarted` fires from inside `SimulationRunner.Awake()`,
        // before this object's own `OnEnable` subscription exists, so that first
        // spawn has to be triggered explicitly here rather than relying solely on
        // the event.
        void Start() => SpawnTargets();

        void SpawnTargets()
        {
            SimulationWorld world = _runner.World;
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
