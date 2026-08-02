using UnityEngine;

namespace Ring.Presentation
{
    /// Minimal aim-point marker (spec §3.8/§3.11): moves a small emissive quad to the
    /// current aim point every frame and hides the OS cursor while active. The spread
    /// cone overlay is Task 26, not here.
    public sealed class CrosshairView : MonoBehaviour
    {
        static readonly Vector3 GroundOffset = Vector3.up * 0.05f;

        [SerializeField] Transform _marker;
        [SerializeField] AimProvider _aimProvider;

        void OnEnable() => Cursor.visible = false;

        void OnDisable() => Cursor.visible = true;

        void LateUpdate()
        {
            _marker.position = SimSpace.ToWorld(_aimProvider.CurrentAimSimPos) + GroundOffset;
        }
    }
}
