using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Aim-point marker plus honest spread cone (spec §3.5/§3.8/§3.11): a small
    /// emissive quad tracks the current aim point every frame, and a ring around it
    /// shows the ACTUAL radius the next shot's spread could land within — not a
    /// fixed decorative reticle. Hides the OS cursor while active.
    ///
    /// П-3 (this task's resolution): `AimProvider.CurrentAimSimPos` is the sole
    /// per-frame aim source both the marker and the cone's CENTER read — no tick
    /// quantization. The cone's RADIUS is the one place this class reads the
    /// simulation snapshot at all, and only for `RenderCurr.Player.RecoilOffset`
    /// (the hitstop-consistent half of the render pair, the same snapshot
    /// `PlayerView`/`CameraRig` already read) — `SpreadRad` itself comes from
    /// `SimulationRunner.World.Config.Weapon` (hot-tweakable via `WeaponConfig`,
    /// never hardcoded here).
    public sealed class CrosshairView : MonoBehaviour
    {
        /// Ring segment count — also the `LineRenderer.positionCount` the
        /// bootstrap sets at creation, so both sides of that contract share one
        /// source of truth instead of two copies of the same magic number.
        public const int ConeSegments = 32;

        static readonly Vector3 GroundOffset = Vector3.up * 0.05f;

        [SerializeField] Transform _marker;
        [SerializeField] LineRenderer _cone;
        [SerializeField] AimProvider _aimProvider;
        [SerializeField] SimulationRunner _runner;

        readonly Vector3[] _conePoints = new Vector3[ConeSegments];

        void OnEnable() => Cursor.visible = false;

        void OnDisable() => Cursor.visible = true;

        void LateUpdate()
        {
            float2 aimSim = _aimProvider.CurrentAimSimPos;
            Vector3 aimWorld = SimSpace.ToWorld(aimSim);
            _marker.position = aimWorld + GroundOffset;

            UpdateCone(aimSim, aimWorld);
        }

        /// Radius = `tan(SpreadRad + RecoilOffset) * distanceToAimPoint` — the
        /// half-angle a shot fired right now could land within, projected out to
        /// the player's current aim distance (spec §3.5/§3.11: the player sees
        /// the weapon's REAL current spread, recoil inflation included, not a
        /// static decorative prop). Distance is measured in sim space
        /// (`Unity.Mathematics.math.distance`) between `RenderCurr.Player.Pos`
        /// and the same `aimSim` the marker just used above — `SimSpace.ToWorld`
        /// is a plain axis remap with no scale factor, so this equals the
        /// world-space distance too, just without a second `Vector3` round-trip.
        /// `_cone` is a world-space `LineRenderer` ring — points are regenerated
        /// every frame directly in world space rather than baked once and scaled
        /// via `transform.localScale`, so ring width never distorts with radius.
        void UpdateCone(float2 aimSim, Vector3 aimWorld)
        {
            float spreadRad = _runner.World.Config.Weapon.SpreadRad;
            float recoil = _runner.RenderCurr.Player.RecoilOffset;
            float2 playerSim = _runner.RenderCurr.Player.Pos;
            float distance = math.distance(playerSim, aimSim);
            float radius = Mathf.Tan(spreadRad + recoil) * distance;

            Vector3 center = aimWorld + GroundOffset;
            for (int i = 0; i < ConeSegments; i++)
            {
                float angle = i / (float)ConeSegments * (Mathf.PI * 2f);
                _conePoints[i] = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            }
            _cone.SetPositions(_conePoints);
        }
    }
}
