using Ring.Data;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Aim-point marker plus honest HIP-FIRE spread cone (spec §3.5/§3.8/§3.11):
    /// a small emissive round disc tracks the current aim point every frame, and
    /// — while hip-firing (`!AimHeld`) — a ring around it shows the ACTUAL radius
    /// the next shot's spread could land within, via
    /// `Ring.Simulation.Combat.Spread.HipRadians` — the EXACT SAME function
    /// `WeaponSystem.Update`'s own hip-fire branch calls (Task 20, PC6) — never a
    /// private re-derivation and never a fixed decorative reticle. While
    /// `AimHeld` the cone is hidden (PD15: a spread CONE only ever describes hip
    /// fire — aimed fire draws a genuine 3D ray instead, `AimRayView`) and this
    /// same marker doubles as the aim-point dot, scaled by `GameFeelConfig.
    /// AimDotScale` — no second marker is ever created for that (PC8). Hides the
    /// OS cursor while active.
    ///
    /// П-3 (Task 19's resolution): `AimProvider.CurrentAimSimPos` is the sole
    /// per-frame aim source both the marker and the cone's CENTER read — no tick
    /// quantization. The cone's RADIUS is the one place this class reads the
    /// simulation snapshot at all (`RenderCurr.Player`, the hitstop-consistent
    /// half of the render pair, the same snapshot `PlayerView`/`CameraRig`
    /// already read), fed into `Spread.HipRadians` alongside `World.Config.
    /// Weapon`/`World.Config.Hero` (hot-tweakable via their SOs, never hardcoded
    /// here) — `settleFactor` is deliberately NOT applied (PD15 above): that
    /// shrink exists only on `WeaponSystem`'s AIMED-fire branch, which this cone
    /// never represents.
    public sealed class CrosshairView : MonoBehaviour
    {
        /// Ring segment count — also the `LineRenderer.positionCount` the
        /// bootstrap sets at creation, so both sides of that contract share one
        /// source of truth instead of two copies of the same magic number.
        public const int ConeSegments = 32;

        static readonly Vector3 GroundOffset = Vector3.up * 0.05f;
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        [SerializeField] Transform _marker;
        [SerializeField] LineRenderer _cone;
        [SerializeField] AimProvider _aimProvider;
        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        // В3 fix-wave 1 (app-n6g item 3a): billboards the marker toward the
        // camera while AimHeld — same MainCamera reference AimProvider's own
        // `_camera` field is wired to (StageOneSceneBootstrap's `mainCamera`
        // local var), a second slot rather than reaching through AimProvider
        // since that field is private there (encapsulation, not a shared
        // singleton).
        [SerializeField] Camera _camera;

        readonly Vector3[] _conePoints = new Vector3[ConeSegments];
        Vector3 _markerBaseScale;
        // В1/В2 fix-wave 2 (app-n6g item 3a): the marker doubles as the
        // zone-colored aim dot while AimHeld — same MaterialPropertyBlock/
        // _EmissionColor idiom every other accent in this project uses
        // (PlayerVisual/MobView/CorpseView), never a material instance.
        Renderer _markerRenderer;
        MaterialPropertyBlock _block;
        Color _markerBaseEmission;

        void Awake()
        {
            _markerBaseScale = _marker.localScale;
            _markerRenderer = _marker.GetComponent<Renderer>();
            _block = new MaterialPropertyBlock();
            // The marker's own baked "CrosshairEmissive" color (hip-fire /
            // HitZone.None fallback) — read once, same "cache the material's
            // own base" idiom AimRayView's Awake already uses for _baseColor.
            _markerBaseEmission = _markerRenderer.sharedMaterial.GetColor(EmissionColorId);
        }

        void OnEnable() => Cursor.visible = false;

        void OnDisable() => Cursor.visible = true;

        void LateUpdate()
        {
            // Г5 review (Minor, same lens as AimRayView's Important — QA18
            // pattern): UpdateCone below dereferences _runner.World.Config,
            // same as RenderMuzzleHeight does for AimRayView — hide the cone
            // and skip it until both exist, rather than crash on the cold
            // start. Once running, behavior below is unchanged.
            if (_runner == null || _runner.World == null)
            {
                _cone.enabled = false;
                return;
            }

            bool aimHeld = _runner.LastFrameInput.AimHeld;

            float2 aimSim = _aimProvider.CurrentAimSimPos;
            Vector3 aimWorld;
            if (aimHeld)
            {
                // В3 fix-wave 1 (app-n6g item 3a, owner playtest feedback:
                // aiming "feels like a 2D crosshair with a ray"): the marker
                // sits at the aim proxy's REAL 3D world point
                // (AimProvider.CurrentAimWorldPoint — the proxy's own
                // hit.point, real height on the mob's silhouette) instead of
                // a floor-projected XY plus a flat ground offset — moving
                // the cursor up a mob's body now visibly slides the marker
                // UP the model. Billboarded to the camera every frame (the
                // marker's local Y axis is its flat disc's cap normal) so it
                // always reads as a coin facing the viewer, not a decal
                // lying on whatever surface it currently touches.
                aimWorld = _aimProvider.CurrentAimWorldPoint;
                _marker.position = aimWorld + GroundOffset;
                Vector3 toCamera = _camera != null
                    ? _camera.transform.position - _marker.position : Vector3.up;
                if (toCamera.sqrMagnitude < 1e-6f) toCamera = Vector3.up;
                _marker.up = toCamera.normalized;
            }
            else
            {
                // Э1 unchanged: hip-fire keeps the marker flat on the floor,
                // paired with the spread cone ring below (UpdateCone) — no
                // billboard, matching this fix-wave's scope (headshot
                // aiming only, not the hip-fire reticle).
                aimWorld = SimSpace.ToWorld(aimSim);
                _marker.position = aimWorld + GroundOffset;
                _marker.rotation = Quaternion.identity;
            }
            // Task 20 (PC8): the SAME marker serves as the aim dot while
            // AimHeld — shrunk by AimDotScale, never a second renderer.
            // В3 fix-wave 1 (item 3a): head zone scales it back up a touch
            // on top of that shrink — GameFeelConfig's own class doc has the
            // "unmistakable, not just recolored" rationale.
            float headBoost = _aimProvider.CurrentAimZone == HitZone.Head
                ? _gameFeel.AimMarkerHeadScaleBoost : 1f;
            _marker.localScale =
                (aimHeld ? _markerBaseScale * _gameFeel.AimDotScale : _markerBaseScale) * headBoost;

            // В1/В2 fix-wave 2 (app-n6g item 3a): zone tint — `CurrentAimZone`
            // is already `HitZone.None` whenever `!aimHeld` (AimProvider's own
            // class doc), so `AimZoneColors.Resolve` falls back to the
            // marker's baked color and hip-fire mode is visually unchanged.
            Color zoneColor = AimZoneColors.Resolve(
                _aimProvider.CurrentAimZone, _markerBaseEmission, _gameFeel);
            _block.SetColor(EmissionColorId, zoneColor);
            _markerRenderer.SetPropertyBlock(_block);

            // Task 20 (PD15): the cone only ever means "hip fire" — hidden the
            // instant AimHeld switches the player to aimed fire, and its
            // positions aren't even recomputed that frame (nothing reads them
            // while disabled).
            _cone.enabled = !aimHeld;
            if (!aimHeld) UpdateCone(aimSim, aimWorld);
        }

        /// Radius = `tan(Spread.HipRadians(...)) * distanceToAimPoint` — the
        /// half-angle the HIP-FIRE branch's next shot could land within, read via
        /// the single shared `Ring.Simulation.Combat.Spread.HipRadians` formula
        /// (class doc — PC6) off `World.Config.Weapon`/`RenderCurr.Player`/
        /// `World.Config.Hero`, never a private copy of the math, projected out
        /// to the player's current aim distance (spec §3.5/§3.11: the player
        /// sees the weapon's REAL current hip-fire spread — recoil AND
        /// movement-widening both included, since `Spread.HipRadians` folds in
        /// the slide/run multiplier `WeaponSystem` itself applies — not a static
        /// decorative prop). Distance is measured in sim space (`Unity.
        /// Mathematics.math.distance`) between `RenderCurr.Player.Pos` and the
        /// same `aimSim` the marker just used above — `SimSpace.ToWorld` is a
        /// plain axis remap with no scale factor, so this equals the world-space
        /// distance too, just without a second `Vector3` round-trip. Only ever
        /// called while `!AimHeld` (`LateUpdate` above) — this cone has no
        /// aimed-fire meaning (PD15, class doc). `_cone` is a world-space
        /// `LineRenderer` ring — points are regenerated every frame directly in
        /// world space rather than baked once and scaled via
        /// `transform.localScale`, so ring width never distorts with radius.
        void UpdateCone(float2 aimSim, Vector3 aimWorld)
        {
            var cfg = _runner.World.Config;
            var player = _runner.RenderCurr.Player;
            float halfAngle = Spread.HipRadians(cfg.Weapon, player, cfg.Hero);
            float distance = math.distance(player.Pos, aimSim);
            float radius = Mathf.Tan(halfAngle) * distance;

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
