using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// The aim-assist ray (Task 20, spec Г5): a two-point world-space
    /// `LineRenderer` from the weapon's muzzle to the current aim point,
    /// visible ONLY while `AimHeld` — hip fire's honest picture is
    /// `CrosshairView`'s spread cone, aimed fire's honest picture is this ray.
    /// Carries no dot/marker of its own (PC8): `CrosshairView`'s existing
    /// `_marker` already doubles as the aim-point dot while `AimHeld` (scaled
    /// by `GameFeelConfig.AimDotScale`), so a second one here would just draw
    /// the same point twice.
    ///
    /// The `LineRenderer` component itself lives on this same GameObject
    /// (`GetComponent`, `MuzzleFlashView`'s `_particles` precedent) — only the
    /// cross-cutting references are bootstrap-wired fields. The material is
    /// created once by `StageOneSceneBootstrap` (`GetOrCreateUnlitMaterial`,
    /// QA10) and pushed in via `_rayMaterial`; this class re-applies it in
    /// `Awake` too (harmless no-op once the bootstrap has already assigned the
    /// same shared material to the `LineRenderer` directly), same
    /// belt-and-braces reference-consuming shape `ViewRegistry`'s prefab slots
    /// use. `AimRayWidth` maps straight onto `LineRenderer.startWidth`/
    /// `endWidth` every frame (hot-tweak, `GameFeelConfig`, Task 17).
    /// `AimRayAlpha` does NOT drive real alpha blending — this project has no
    /// transparent-material path anywhere yet (every existing emissive Unlit,
    /// `SpreadConeEmissive`/`TracerTrail`/`MuzzleFlash`, is Opaque, and URP's
    /// stock Unlit fragment shader never samples a mesh's vertex color, so
    /// `LineRenderer.startColor`/`endColor` alone would be a silent no-op on
    /// it) — instead it scales the ray's emissive RGB brightness, applied via
    /// a `MaterialPropertyBlock` rather than a material instance, the same
    /// "no per-instance materials" rule `CorpseView.Spawn`'s per-death tint
    /// already follows. A lower `AimRayAlpha` therefore reads as a fainter
    /// glow under Bloom rather than literal translucency — a deliberate,
    /// convention-consistent simplification; a real transparent surface is a
    /// separate scope decision for whoever wants it later.
    ///
    /// Muzzle height: read `SimulationRunner.RenderMuzzleHeight` (Task 21,
    /// PC7's single home of the `SlideTimer > 0 ? SlideMuzzleHeight :
    /// MuzzleHeight` ternary `WeaponSystem.Update` itself uses for the
    /// authoritative shot) so the ray's visible origin never disagreed with
    /// where the server actually spawns the round.
    ///
    /// STAGE 2 TASK 45b MOVED THE ORIGIN ONTO THE MODEL (bd `app-60c`). The ray
    /// started at the hero's own centre lifted to that height — a point inside
    /// the collector's chest, which reads as a laser growing out of his sternum
    /// once the doll carries a real pistol. It now starts at the muzzle socket
    /// of the LOCAL player's doll (`ViewRegistry.TryGetPlayerView` on
    /// `RenderSnapshot.LocalPlayerIndex`), so the ray leaves the barrel the
    /// player is looking at, at whatever height the animated hand is holding it
    /// — including mid-slide, which the ternary above approximated with a second
    /// number. No doll (the opening frames, or after this player dies) means no
    /// ray, switched off exactly the way `!Ready`/`!AimHeld` already switch it
    /// off. The ray's far END is untouched by that task (`app-bej`).
    [RequireComponent(typeof(LineRenderer))]
    public sealed class AimRayView : MonoBehaviour
    {
        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] Material _rayMaterial;
        // Stage 2 Task 45b: asked per frame, never cached — the local player's
        // doll is one pooled instance among several (`TryGetPlayerView`'s doc).
        [SerializeField] ViewRegistry _viewRegistry;

        LineRenderer _line;
        MaterialPropertyBlock _block;
        // Г5 review (Minor): read from the bootstrap-created `_rayMaterial`
        // itself instead of a second hardcoded literal here duplicating
        // `StageOneSceneBootstrap`'s `AimRayEmissive` color — one number, one
        // owner. `Color.white` is the harmless fallback for the (never
        // expected in practice) case the reference isn't wired yet.
        Color _baseColor;

        void Awake()
        {
            _line = GetComponent<LineRenderer>();
            _block = new MaterialPropertyBlock();
            if (_rayMaterial != null)
            {
                _line.sharedMaterial = _rayMaterial;
                _baseColor = _rayMaterial.GetColor("_BaseColor");
            }
            else
            {
                _baseColor = Color.white;
            }
        }

        void LateUpdate()
        {
            // Г5 review (Important): cold-start guard, same shape as
            // AimProvider's own QA18 pattern — the backend has nothing to show
            // on the very first frame(s) before SimulationRunner.Awake's
            // RestartNewSeed completes (or a scene missing the wiring), and
            // RenderMuzzleHeight below reads the render pair and Config.
            // Task 43: was `World == null`; `Ready` is the successor test.
            if (_runner == null || !_runner.Ready)
            {
                _line.enabled = false;
                return;
            }

            bool aimHeld = _runner.LastFrameInput.AimHeld;
            if (!aimHeld)
            {
                _line.enabled = false;
                return;
            }

            // Stage 2 Task 45b: a ray with no barrel to leave is not drawn from
            // somewhere else — same rule the flash and the casing follow (class
            // doc). `_line.enabled` is written on both paths so a doll that
            // disappears mid-aim takes the ray with it.
            if (!TryGetMuzzle(out Vector3 muzzle))
            {
                _line.enabled = false;
                return;
            }
            _line.enabled = true;

            Vector3 aimPoint = SimSpace.ToWorld(_aimProvider.CurrentAimSimPos)
                + Vector3.up * _aimProvider.CurrentAimHeight;

            _line.SetPosition(0, muzzle);
            _line.SetPosition(1, aimPoint);
            _line.startWidth = _line.endWidth = _gameFeel.AimRayWidth;

            // В1/В2 fix-wave 2 (app-n6g item 3a): same zone tint CrosshairView's
            // marker applies, via the shared AimZoneColors lookup — falls back
            // to the ray's own baked cyan (_baseColor) on HitZone.None, same as
            // before this fix. Applied uniformly to the WHOLE LineRenderer via
            // MaterialPropertyBlock (no per-vertex/gradient color anywhere on
            // this component) — item 3b verified this already colors the
            // entire ray, not just its tip.
            Color zoneColor = AimZoneColors.Resolve(_aimProvider.CurrentAimZone, _baseColor, _gameFeel);
            // В3 fix-wave 1 (app-n6g item 3b): headshot alignment gets an
            // extra brightness boost on top of the base AimRayAlpha dimming —
            // GameFeelConfig's own class doc has the "unmistakable, not a
            // faint dim-red tinge" rationale.
            float alphaBoost = _aimProvider.CurrentAimZone == HitZone.Head
                ? _gameFeel.AimRayHeadAlphaBoost : 1f;
            Color dimmed = zoneColor * (_gameFeel.AimRayAlpha * alphaBoost);
            _block.SetColor("_BaseColor", new Color(dimmed.r, dimmed.g, dimmed.b, 1f));
            _line.SetPropertyBlock(_block);
        }

        /// The local player's own barrel mouth (Stage 2 Task 45b) — false when
        /// this client has no live doll, which the caller answers by hiding the
        /// ray. The socket's own null check has the same meaning it has in
        /// `MuzzleFlashView`: a doll prefab older than this task carries no
        /// socket, and that must read as "no ray", not as an exception per
        /// frame.
        bool TryGetMuzzle(out Vector3 worldPos)
        {
            worldPos = default;
            int slot = _runner.RenderCurr.LocalPlayerIndex;
            if (!_viewRegistry.TryGetPlayerView(slot, out PlayerView doll)) return false;
            Transform muzzle = doll.MuzzleSocket;
            if (muzzle == null) return false;
            worldPos = muzzle.position;
            return true;
        }
    }
}
