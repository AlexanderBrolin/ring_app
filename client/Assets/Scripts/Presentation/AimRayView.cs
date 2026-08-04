using Ring.Data;
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
    /// Muzzle height: reads the exact ternary `WeaponSystem.Update` itself
    /// uses for the authoritative shot's own muzzle height
    /// (`SlideTimer > 0 ? SlideMuzzleHeight : MuzzleHeight`) so the ray's
    /// visible origin never disagrees with where the server actually spawns
    /// the round. `Task 21 switches this to RenderMuzzleHeight` once that
    /// canonical accessor exists — this is the sim-correct equivalent
    /// available today, not a placeholder guess.
    [RequireComponent(typeof(LineRenderer))]
    public sealed class AimRayView : MonoBehaviour
    {
        // A cool cyan tint, distinct from the cone's warm-neon orange
        // (SpreadConeEmissive) and the tracer's near-white cyan (TracerTrail)
        // — HDR-overbright like every other emissive placeholder in the
        // project, so it still blooms once dimmed by AimRayAlpha below.
        static readonly Color BaseColor = new Color(0.6f, 2.4f, 3.2f);

        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] Material _rayMaterial;

        LineRenderer _line;
        MaterialPropertyBlock _block;

        void Awake()
        {
            _line = GetComponent<LineRenderer>();
            _block = new MaterialPropertyBlock();
            if (_rayMaterial != null) _line.sharedMaterial = _rayMaterial;
        }

        void LateUpdate()
        {
            bool aimHeld = _runner.LastFrameInput.AimHeld;
            _line.enabled = aimHeld;
            if (!aimHeld) return;

            var player = _runner.RenderCurr.Player;
            var hero = _runner.World.Config.Hero;
            // Task 21 switches this to RenderMuzzleHeight (class doc above).
            float muzzleHeight = player.SlideTimer > 0f ? hero.SlideMuzzleHeight : hero.MuzzleHeight;
            Vector3 muzzle = SimSpace.ToWorld(player.Pos) + Vector3.up * muzzleHeight;
            Vector3 aimPoint = SimSpace.ToWorld(_aimProvider.CurrentAimSimPos)
                + Vector3.up * _aimProvider.CurrentAimHeight;

            _line.SetPosition(0, muzzle);
            _line.SetPosition(1, aimPoint);
            _line.startWidth = _line.endWidth = _gameFeel.AimRayWidth;

            Color dimmed = BaseColor * _gameFeel.AimRayAlpha;
            _block.SetColor("_BaseColor", new Color(dimmed.r, dimmed.g, dimmed.b, 1f));
            _line.SetPropertyBlock(_block);
        }
    }
}
