using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// The AIMED-fire marker and the OS cursor -- since app-461s T5 that is the
    /// whole of this class, and the list has exactly two entries. A small
    /// emissive round disc rides the round's own landing point while `AimHeld`,
    /// tinted by the hit zone under the cursor, pulsing on a head and asking
    /// `AudioDirector` for a tick on the edge onto one; the same disc IS the aim
    /// dot, shrunk by `GameFeelConfig.AimDotScale` -- no second marker is ever
    /// created for that (PC8). And this class is the sole owner of the OS
    /// cursor's visibility (`UpdateCursor` below, Stage 2 Task 45c).
    ///
    /// FROM THE HIP IT DRAWS NOTHING AT ALL (owner decisions Н37/Н41/Н45).
    /// Two things used to stand on the floor there and app-461s T5 retires both:
    /// the honest hip-fire spread ring -- a world-space `LineRenderer` loop
    /// whose radius came out of `Ring.Simulation.Combat.Spread.HipRadians`, the
    /// very function `ShotGeometry.Solve`'s own hip branch calls -- and the marker
    /// disc underneath it. The line of fire out of the muzzle now says the same
    /// thing where the player is already looking (`AimRayView`, whose notches
    /// stand on that SAME cone through `AimLine`/`Spread.HipHalfWidth`), and one
    /// cone drawn twice would be two drawings to keep true -- moot now anyway,
    /// because no cone is drawn here any more.
    ///
    /// AND THE SWITCH SITS ABOVE THE BLOCK RATHER THAN INSIDE IT: while
    /// `!AimHeld` the renderer goes off and the DRAWING below it is skipped for
    /// that frame — no position, no orientation, no scale, no color and no read
    /// of the aim zone. An invisible disc taking all of those once per FRAME is
    /// the exact cost this removal is for, and hiding the disc while still
    /// feeding it would have paid that cost for nothing. (Per frame, note, not
    /// per tick: `LateUpdate` runs at the display's rate, so the bill was 60 and
    /// up on an ordinary client rather than the simulation's 30 a second.)
    /// ⚠ EXACTLY ONE LINE BELOW THE SWITCH STILL RUNS ON THAT PATH, and it is
    /// not the marker's: `_prevHoverZone` is cleared so the head-hover tick
    /// keeps an honest edge across a release of the aim button. Its own comment
    /// down there carries the why and the one frame in which the new code and
    /// the old differ.
    /// ⚠ AND THE `AimActive` PATH ABOVE CLEARS NOTHING, which is inherited
    /// rather than chosen: the pause menu, the death screen and the loot window
    /// all return before that switch is reached, so aiming at a head, pressing
    /// Escape and coming back to the same head gives no tick. The retired code
    /// returned there without touching the detector too, so this is the shape
    /// of the gap rather than a new one.
    ///
    /// П-3 (Task 19's resolution): `AimProvider` is this class's sole per-frame
    /// aim source -- no tick quantization. Since T5 it reads NEITHER the
    /// simulation snapshot NOR `SimulationRunner.Config`: the retired ring's
    /// radius was the one place it ever reached for either of them, and all
    /// that is left of `_runner` here is `AimActive` plus
    /// `LastFrameInput.AimHeld`.
    public sealed class CrosshairView : MonoBehaviour
    {
        static readonly Vector3 GroundOffset = Vector3.up * 0.05f;
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        [SerializeField] Transform _marker;
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
            // The marker's own baked "CrosshairEmissive" color (HitZone.None
            // fallback) — read once, same "cache the material's own base" idiom
            // AimRayView's Awake already uses for _baseColor.
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
        /// matters at this call site is that the pointer, the aimed-fire marker
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
            // pattern): everything below reads the aim cache and the runner's
            // last input — hide the marker and skip the lot until the backend
            // has something to show, rather than crash on the cold start. Once
            // running, behavior below is unchanged. Task 43: was `World == null`,
            // then `Ready`.
            //
            // Fix-round 1 (G-4): the guard is `AimActive` now, not `Ready` — a
            // strictly narrower condition that also covers the pause menu and
            // the death screen. The marker used to keep tracking the mouse
            // across the menu buttons the cursor was finally being shown for.
            // app-461s T5: the spread ring that used to go down here beside it
            // is retired outright, so this branch has ONE renderer to switch
            // off rather than two.
            if (_runner == null || !_runner.AimActive)
            {
                _markerRenderer.enabled = false;
                return;
            }

            // app-461s T5 (owner decisions Н37/Н41/Н45): the declaration comes
            // FIRST now. The enable line used to stand above it and write a
            // flat `true`, because there was always something on the floor to
            // show; from the hip there is not, and the two lines have to be in
            // this order for the switch to read the state it reports.
            bool aimHeld = _runner.LastFrameInput.AimHeld;
            _markerRenderer.enabled = aimHeld;
            if (!aimHeld)
            {
                // The ONE write the hip path keeps, and it is not the marker's:
                // without it a release-and-re-aim onto a head the cursor never
                // left would swallow the tick.
                // ⚠ NOT BIT FOR BIT WHAT THE BLOCK BELOW USED TO WRITE HERE,
                // and the difference is one frame wide. `AimProvider` is pinned
                // to `[DefaultExecutionOrder(100)]` and this class is not pinned
                // at all, so the zone read below is the cache LAST frame's
                // `AimProvider.LateUpdate` left: the retired code's FIRST hip
                // frame therefore stored the previous AIMED frame's zone and
                // only reached `HitZone.None` on the second. From the second hip
                // frame on the two agree — `AimProvider` writes `None` for every
                // `!AimHeld` frame of its own — and the tick lands on the same
                // frame either way. What changed is the value the detector holds
                // during that first frame, not when it fires.
                _prevHoverZone = HitZone.None;
                return;
            }

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
            Vector3 aimWorld = _aimProvider.CurrentImpactWorldPoint;
            _marker.position = aimWorld + GroundOffset;
            Vector3 toCamera = _camera != null
                ? _camera.transform.position - _marker.position : Vector3.up;
            if (toCamera.sqrMagnitude < 1e-6f) toCamera = Vector3.up;
            _marker.up = toCamera.normalized;

            // Task 20 (PC8): the SAME marker serves as the aim dot while
            // AimHeld — shrunk by AimDotScale, never a second renderer.
            // app-461s T5: the shrink is unconditional here, since the block
            // itself is now the AimHeld branch — the ternary that used to pick
            // between the shrunk dot and the full-size hip reticle has no
            // second case left to pick.
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
            _marker.localScale = _markerBaseScale * _gameFeel.AimDotScale * headBoost;

            // В3 fix-wave 2 (item 3c): fire the audio tick exactly on the
            // None/Legs/Body → Head EDGE. app-461s T5: the edge itself is
            // unchanged by the gate above — the hip branch clears
            // `_prevHoverZone` to the very `HitZone.None` this read used to
            // return there, so hip-fire cursor movement still never counts as
            // "entering Head".
            float2 aimSim = _aimProvider.CurrentAimSimPos;
            if (hoverZone == HitZone.Head && _prevHoverZone != HitZone.Head && _audio != null)
                _audio.PlayHeadHoverTick(aimSim);
            _prevHoverZone = hoverZone;

            // В1/В2 fix-wave 2 (app-n6g item 3a): zone tint — `AimZoneColors.
            // Resolve` falls back to the marker's own baked color on
            // `HitZone.None`, which is what a miss and the Э1 plane fallback
            // both answer. `hoverZone` is the same `CurrentAimZone` read once
            // above, reused here rather than a second property read.
            Color zoneColor = AimZoneColors.Resolve(hoverZone, _markerBaseEmission, _gameFeel);
            _block.SetColor(EmissionColorId, zoneColor);
            _markerRenderer.SetPropertyBlock(_block);
        }
    }
}
