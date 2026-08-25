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
    /// AimDotScale` — no second marker is ever created for that (PC8). Sole
    /// owner of the OS cursor's visibility (`UpdateCursor`, Stage 2 Task 45c).
    ///
    /// П-3 (Task 19's resolution): `AimProvider.CurrentAimSimPos` is the sole
    /// per-frame aim source both the marker and the cone's CENTER read — no tick
    /// quantization. The cone's RADIUS is the one place this class reads the
    /// simulation snapshot at all (`RenderCurr.Player`, the render-pair half
    /// every interpolating view goes through, the same snapshot `ViewRegistry`/`CameraRig`
    /// already read), fed into `Spread.HipRadians` alongside the runner's
    /// `Config.Weapon`/`Config.Hero` (hot-tweakable via their SOs, never hardcoded
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
        // В3 fix-wave 2 (app-n6g item 3c): the head-hover audio tick lives on
        // AudioDirector (same "one AudioSource pool, every clip through it"
        // idiom every other SFX in this project uses) — this class only owns
        // the edge-detection (see `_prevHoverZone` below) that decides WHEN to
        // ask for it.
        [SerializeField] AudioDirector _audio;

        readonly Vector3[] _conePoints = new Vector3[ConeSegments];
        Vector3 _markerBaseScale;
        // В1/В2 fix-wave 2 (app-n6g item 3a): the marker doubles as the
        // zone-colored aim dot while AimHeld — same MaterialPropertyBlock/
        // _EmissionColor idiom every other accent in this project uses
        // (PlayerView/MobView/CorpseView), never a material instance.
        Renderer _markerRenderer;
        MaterialPropertyBlock _block;
        Color _markerBaseEmission;
        // В3 fix-wave 2 (item 3c): last frame's hovered zone, so the audio tick
        // fires once on the None/Legs/Body → Head EDGE, not every frame the
        // cursor happens to still be resting on Head.
        HitZone _prevHoverZone;

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

        /// The safety net, and only that (Stage 2 Task 45c): whoever switches
        /// this component off — leaving Play mode included — gets the OS cursor
        /// back rather than a machine with no pointer. The matching `OnEnable`
        /// that used to hide it is gone: hiding on enable and revealing on
        /// disable made the cursor a function of THIS COMPONENT's lifetime,
        /// which is why it stayed hidden through the pause menu and the death
        /// screen (bd `app-10j`) — `UpdateCursor` below decides per frame
        /// instead, and an enable while paused must not overrule it.
        void OnDisable() => Cursor.visible = true;

        /// SOLE OWNER OF `Cursor.visible` IN THE PROJECT (owner decision 6а,
        /// Stage 2 Task 45c, bd `app-10j`): one class writes it, every frame,
        /// from the state of the game rather than from any event. Written
        /// unconditionally rather than on change — the write is what makes the
        /// ownership real, and a component that only wrote on its own edges
        /// could be overruled by anything else and never notice.
        ///
        /// SHOWN WHENEVER THE GAME IS NOT ASKING FOR AIM, and "asking for aim"
        /// is `SimulationRunner.AimActive` — not a test assembled here. That
        /// property's own doc has the four terms and why each is in it; what
        /// matters at this call site is that the pointer, the ground marker
        /// below and `AimRayView`'s ray all obey the SAME signal, so the cursor
        /// can never appear over a crosshair that is still tracking the mouse
        /// (fix-round 1, G-4).
        void UpdateCursor() => Cursor.visible = !(_runner != null && _runner.AimActive);

        void LateUpdate()
        {
            // Ahead of everything else on purpose: a frame with nothing to show
            // is a frame with nothing to aim at, and the cursor is the player's
            // only way out of it.
            UpdateCursor();

            // Г5 review (Minor, same lens as AimRayView's Important — QA18
            // pattern): UpdateCone below reads _runner.Config and the render
            // pair, the same pair AimRayView's own guard protects — hide the
            // cone and skip it until the backend has something to show, rather
            // than crash on the cold start. Once running, behavior below is
            // unchanged. Task 43: was `World == null`, then `Ready`.
            // (Stage 2 Task 45b fix-round 1, G-6: this comment used to name
            // `RenderMuzzleHeight` as what AimRayView reads. That task moved
            // that view onto the doll's muzzle socket. Stage 2 Task 45c
            // fix-round 1, G-5 item 6: nor is the property unread — `AimProvider`
            // has read it since that task, for the simulation's own muzzle
            // rather than for a drawn one.)
            //
            // Fix-round 1 (G-4): the guard is `AimActive` now, not `Ready` — a
            // strictly narrower condition that also covers the pause menu and
            // the death screen. The MARKER goes down with the cone here: it used
            // to keep tracking the mouse across the menu buttons the cursor was
            // finally being shown for.
            if (_runner == null || !_runner.AimActive)
            {
                _cone.enabled = false;
                _markerRenderer.enabled = false;
                return;
            }
            _markerRenderer.enabled = true;

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
                // Stage 2 Task 45c (bd app-bej): the point is now the round's
                // own landing point rather than the cursor's
                // (`AimProvider.CurrentImpactWorldPoint` vs the
                // `CurrentAimWorldPoint` this line used to read). The two are
                // the same point for any aim at or above the round's
                // ground-contact height — every shot at a mob's body included —
                // and part company on a shot at the FLOOR, where the marker used
                // to stand 8% of the range beyond where the round comes down.
                aimWorld = _aimProvider.CurrentImpactWorldPoint;
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
            // В3 fix-wave 2 (item 3a): a breathing scale PULSE layers on top of
            // that boost while on Head — same `0.5+0.5*sin(...)`-shaped
            // oscillation idiom as PlayerView/MobView's own pulses (class doc
            // above the GameFeelConfig fields), remapped to a signed [-1,1]
            // swing around 1 so the dot visibly grows AND shrinks, not just
            // fades.
            HitZone hoverZone = _aimProvider.CurrentAimZone;
            float headBoost = 1f;
            if (hoverZone == HitZone.Head)
            {
                float pulse = 1f + _gameFeel.HeadHoverPulseAmp * Mathf.Sin(
                    Time.unscaledTime * _gameFeel.HeadHoverPulseHz * Mathf.PI * 2f);
                headBoost = _gameFeel.AimMarkerHeadScaleBoost * pulse;
            }
            _marker.localScale =
                (aimHeld ? _markerBaseScale * _gameFeel.AimDotScale : _markerBaseScale) * headBoost;

            // В3 fix-wave 2 (item 3c): fire the audio tick exactly on the
            // None/Legs/Body → Head EDGE (CurrentAimZone is None whenever
            // !aimHeld, AimProvider's own class doc, so hip-fire cursor
            // movement never spuriously counts as "entering Head").
            if (hoverZone == HitZone.Head && _prevHoverZone != HitZone.Head && _audio != null)
                _audio.PlayHeadHoverTick(aimSim);
            _prevHoverZone = hoverZone;

            // В1/В2 fix-wave 2 (app-n6g item 3a): zone tint — `CurrentAimZone`
            // is already `HitZone.None` whenever `!aimHeld` (AimProvider's own
            // class doc), so `AimZoneColors.Resolve` falls back to the
            // marker's baked color and hip-fire mode is visually unchanged.
            // `hoverZone` (В3 fix-wave 2) is the same `CurrentAimZone` read
            // once above, reused here rather than a second property read.
            Color zoneColor = AimZoneColors.Resolve(hoverZone, _markerBaseEmission, _gameFeel);
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
        /// (class doc — PC6) off `_runner.Config.Weapon`/`RenderCurr.Player`/
        /// `_runner.Config.Hero`, never a private copy of the math, projected out
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
            var cfg = _runner.Config;
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
