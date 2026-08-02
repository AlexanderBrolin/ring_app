#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Dev-only debug spawn stub (Task 21 Interfaces, П-11): a bare IMGUI overlay
    /// with two buttons that drop a live Chaser/Gunner through the same
    /// `SimulationWorld.DevSpawnMob` seam `PracticeTargets` uses, at a point
    /// `SpawnDistance` meters from the player toward the current aim point (falls
    /// back to a fixed +X offset if the mouse/aim ray isn't available yet). This
    /// is intentionally the whole feature set — Task 24 (Phase 7+) grows this into
    /// the full DevOverlay (match stats, wave control, caps observed per spec
    /// §3.15, etc.); this class only proves the spawn wiring end to end. Stripped
    /// from production builds by the compile guard above, same contract as
    /// `PracticeTargets`.
    public sealed class DevOverlay : MonoBehaviour
    {
        const float SpawnDistance = 7f; // midpoint of the brief's "6-8m from player" range

        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;

        void OnGUI()
        {
            if (_runner == null || _runner.World == null) return;

            GUILayout.BeginArea(new Rect(10f, 10f, 160f, 64f));
            if (GUILayout.Button("Spawn Chaser")) Spawn(MobType.Chaser);
            if (GUILayout.Button("Spawn Gunner")) Spawn(MobType.Gunner);
            GUILayout.EndArea();
        }

        void Spawn(MobType type)
        {
            float2 playerPos = _runner.Curr.Player.Pos;
            float2 aimPos = _aimProvider != null
                ? _aimProvider.CurrentAimSimPos
                : playerPos + new float2(1f, 0f);
            float2 dir = math.normalizesafe(aimPos - playerPos, new float2(1f, 0f));
            _runner.World.DevSpawnMob(type, playerPos + dir * SpawnDistance);
        }
    }
}
#endif
