using UnityEngine;

namespace Ring.Presentation
{
    /// Renders the local player capsule (spec §3.7/§3.11). Position is interpolated
    /// between the runner's `Prev`/`Curr` snapshots by `Alpha` — this is pure
    /// presentation, it never reads or mutates simulation state directly. Facing is
    /// driven every frame straight from `AimProvider.CurrentAimSimPos`, not from the
    /// snapshot's `AimPoint`: the aim provider is the single per-frame source of
    /// truth for where the player currently looks (orchestrator resolution П-3),
    /// so the capsule turns immediately with the mouse instead of snapping only on
    /// tick boundaries.
    public sealed class PlayerView : MonoBehaviour
    {
        static readonly Vector3 CapsuleOffset = Vector3.up * 1f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;

        void LateUpdate()
        {
            Vector3 prevW = SimSpace.ToWorld(_runner.Prev.Player.Pos);
            Vector3 currW = SimSpace.ToWorld(_runner.Curr.Player.Pos);
            Vector3 groundPos = Vector3.Lerp(prevW, currW, _runner.Alpha);
            transform.position = groundPos + CapsuleOffset;

            Vector3 aimW = SimSpace.ToWorld(_aimProvider.CurrentAimSimPos);
            Vector3 facing = aimW - groundPos;
            // Zero (or near-zero) direction: aim point coincides with the player —
            // leave the previous facing untouched rather than snapping to identity.
            if (facing.sqrMagnitude > 1e-8f)
                transform.rotation = Quaternion.LookRotation(facing, Vector3.up);
        }
    }
}
