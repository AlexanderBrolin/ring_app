using Ring.Data;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// The aim-assist ray (Task 20, spec Г5): a two-point world-space
    /// `LineRenderer` from the weapon's muzzle to the current aim point.
    ///
    /// VISIBLE ALWAYS, NOT ONLY WHILE `AimHeld` (app-461s T2, owner decisions
    /// Н34/Н41 — superseding this class's own PD15 reading: "visible ONLY
    /// while `AimHeld` — hip fire's honest picture is `CrosshairView`'s
    /// spread cone, aimed fire's honest picture is this ray"). Hip fire's
    /// honest picture is now this ray itself, growing out of the muzzle to
    /// wherever the shot would actually stop, plus the two notches it carries
    /// (`_notchLeft`/`_notchRight`, drawn since app-461s T3) standing where
    /// the round's own spread puts them; aimed fire's honest picture is
    /// unchanged — this ray alone.
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
    /// Muzzle height: Task 21 read `SimulationRunner.RenderMuzzleHeight` (PC7's
    /// single home of the `SlideTimer > 0 ? SlideMuzzleHeight : MuzzleHeight`
    /// ternary `WeaponSystem.Update` itself uses for the authoritative shot), so
    /// that the ray's visible origin did not disagree with where the server
    /// spawns the round. This class read neither that property nor any other
    /// muzzle height directly until app-461s T2's hip branch, whose far end
    /// climbs by the very same height, reached through
    /// `AimProvider.CurrentHipLine.Height` rather than a second direct read —
    /// see the next paragraph for where the ORIGIN (as opposed to this, the
    /// far end) came from instead.
    ///
    /// STAGE 2 TASK 45b MOVED THE ORIGIN ONTO THE MODEL (bd `app-60c`). The ray
    /// started at the hero's own center lifted to that height — a point inside
    /// the collector's chest, which reads as a laser growing out of his sternum
    /// once the doll carries a real pistol. It now starts at the muzzle socket
    /// of the LOCAL player's doll (`ViewRegistry.TryGetPlayerView` on
    /// `RenderSnapshot.LocalPlayerIndex`), so the ray leaves the barrel the
    /// player is looking at, at whatever height the animated hand is holding it
    /// — including mid-slide, which the ternary above approximated with a second
    /// number. No doll (the opening frames, or after this player dies) means no
    /// ray, switched off through `SetDrawn` exactly like the other two gates
    /// this view keeps (app-461s T2 fix round 1: `!AimActive`, and the
    /// first-frame guard against a restart's own stale cache — `!AimHeld` is
    /// no longer one of the three since that task made the ray hip-visible).
    /// The ray's far END was untouched by that task and is Stage 2 Task
    /// 45c's own subject (`app-bej`).
    ///
    /// THE TWO ENDS ANSWER DIFFERENT QUESTIONS, AND THAT IS DELIBERATE (Stage 2
    /// Task 45c). The start is the barrel the player is looking at — a point on
    /// the model, put there by Task 45b. Aimed, the end is where the
    /// simulation's round comes down, measured from the simulation's own
    /// muzzle (`AimProvider.CurrentImpactWorldPoint`). The line between them is
    /// therefore not the round's own line: the two origins sit a fraction of a
    /// meter apart, so the drawn ray is a hair off parallel to the shot. What it
    /// gets right is the thing the player actually reads off it — the point at
    /// the far end.
    ///
    /// FROM THE HIP THE END IS A DIFFERENT READ AGAIN (app-461s T2):
    /// `AimProvider.CurrentHipLine.End`, lifted by that same line's own
    /// `Height`, never `CurrentImpactWorldPoint` — that point is cut short by
    /// the floor for whatever the CURSOR points at
    /// (`AimProvider.ResolveImpactWorldPoint`'s own doc), a different question
    /// from where a hip-fired round, launched along the cursor's own plane
    /// direction from the muzzle, actually stops. Taking it from the hip would
    /// draw a ray diving out of the barrel and into the ground exactly where
    /// this task set out to stop lying.
    ///
    /// `[DefaultExecutionOrder(10)]` IS WHAT MAKES THAT ORIGIN THIS FRAME'S
    /// (fix-round 1, G-1). The socket rides a hand bone the Animator writes in
    /// `PreLateUpdate`, on a doll root `ViewRegistry` (pinned at −10) moves in
    /// its own `LateUpdate`; this class reads it in `LateUpdate` too, and Unity
    /// orders equal-order `LateUpdate`s arbitrarily — with no
    /// `ProjectSettings/MonoManager.asset` in the project, the ray's start could
    /// otherwise come from this frame or the previous one, and which of the two
    /// could differ between runs.
    [DefaultExecutionOrder(10)]
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
        // app-461s T2: the two notch strokes drawn by T3, declared here so
        // `SetDrawn` below has something to dereference (circle 2's finding —
        // otherwise T3 would still owe this class a compile-clean state). A
        // scene bootstrapped before T3 runs has no children under `AimRay` at
        // all, so both stay null until then; every use of them is null-guarded
        // the same way `TryGetMuzzle` already guards the doll's own socket.
        [SerializeField] LineRenderer _notchLeft;
        [SerializeField] LineRenderer _notchRight;

        LineRenderer _line;
        MaterialPropertyBlock _block;
        // Г5 review (Minor): read from the bootstrap-created `_rayMaterial`
        // itself instead of a second hardcoded literal here duplicating
        // `StageOneSceneBootstrap`'s `AimRayEmissive` color — one number, one
        // owner. `Color.white` is the harmless fallback for the (never
        // expected in practice) case the reference isn't wired yet.
        Color _baseColor;

        /// app-461s T2: the gap, IN PLAN, between the doll's muzzle socket and
        /// the simulation's own muzzle — the cost of owner decision Н40,
        /// named at the time it was made and never measured until now.
        /// ⛔ ITS HOME IS HERE, NOT `AimProvider`, AND THAT IS A FACT ABOUT THE
        /// CODE, NOT A TASTE CALL: the socket's position lives in
        /// `ViewRegistry.TryGetPlayerView(...).MuzzleSocket`, and the only two
        /// places in the project that ever read it are `TryGetMuzzle` below
        /// and `MuzzleFlashView.TryGetMuzzle` — both in `Presentation`.
        /// `AimProvider` carries no reference to `ViewRegistry` at all, and
        /// giving it one for the sake of a single readout would teach the
        /// provider about the doll, when the provider's whole job is to
        /// answer a question about the SIMULATION.
        /// ⚠ NaN WHILE THE RAY IS NOT DRAWN (no doll, paused, the death
        /// screen) — the dev readout prints a dash for it, the same way the
        /// overlay already prints a dash instead of a `StateHash` on the
        /// networked backend.
        /// ⛔ THIS COMPARES TWO PLANE POSITIONS, NOT TWO POINTS IN SPACE: the
        /// socket sits at the height of an animated hand while the printed
        /// height is the simulation's flat `h`, so this never measures the
        /// vertical half of decision Н40's cost.
        public float MuzzleGapMeters { get; private set; } = float.NaN;

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

        /// The single switch for everything this view draws: the ray plus
        /// both notches. app-461s T2 (circle 2's finding): called on EVERY
        /// path out of `LateUpdate`, including every one of its early
        /// returns — a child object carries its own `enabled` (lesson 726),
        /// so turning the ray off here never touches the notches on its own.
        void SetDrawn(bool ray, bool notches)
        {
            _line.enabled = ray;
            if (_notchLeft != null) _notchLeft.enabled = notches;
            if (_notchRight != null) _notchRight.enabled = notches;
        }

        void LateUpdate()
        {
            // Г5 review (Important): cold-start guard, same shape as
            // AimProvider's own QA18 pattern — the backend has nothing to show
            // on the very first frame(s) before SimulationRunner.Awake's
            // RestartNewSeed completes (or a scene missing the wiring).
            // Task 43: was `World == null`, then `Ready`.
            // Task 45b fix-round 1 (G-6): the guard's REASON changed with that
            // task and this comment kept the old one — `RenderMuzzleHeight` is
            // read nowhere below any more. What needs the guard is
            // `TryGetMuzzle`, which reads the render pair for the local slot.
            // Stage 2 Task 45c fix-round 1 (G-4): and the test is `AimActive`
            // now — `Ready` plus "not paused" plus "my player is standing", the
            // SAME signal that decides the OS cursor and the ground marker
            // (`SimulationRunner.AimActive`, `CrosshairView.UpdateCursor`).
            // Without it the ray outlived the pause menu: `Update` stops
            // sampling input while paused, so a right button held when Escape
            // was pressed leaves `AimHeld` true and the check below passes
            // forever.
            // app-461s T2: this early return, and the two below it, all go
            // through `SetDrawn` now rather than writing `_line.enabled`
            // directly, and all three reset `MuzzleGapMeters` to NaN — without
            // that reset the readout would print a stuck number from whatever
            // frame last drew successfully, on exactly the frames it exists to
            // report (paused, the death screen, no doll).
            if (_runner == null || !_runner.AimActive)
            {
                SetDrawn(false, false);
                MuzzleGapMeters = float.NaN;
                return;
            }

            // app-461s T2: no early return on `!aimHeld` any more — the ray is
            // drawn from the hip too now, so this flag only PICKS which far
            // end and which color the rest of the method uses.
            bool aimHeld = _runner.LastFrameInput.AimHeld;

            // Stage 2 Task 45b: a ray with no barrel to leave is not drawn from
            // somewhere else — same rule the flash and the casing follow (class
            // doc). `SetDrawn` is called on every path so a doll that
            // disappears mid-aim takes the ray (and the notches) with it.
            if (!TryGetMuzzle(out Vector3 muzzle))
            {
                SetDrawn(false, false);
                MuzzleGapMeters = float.NaN;
                return;
            }

            AimLineSolution line = _aimProvider.CurrentHipLine;
            // ⛔⛔ FIRST-FRAME GUARD (app-461s T2, circle 2's finding): this
            // class runs at `[DefaultExecutionOrder(10)]`, `AimProvider` at
            // `(100)` — this view runs BEFORE the provider. On the very first
            // frame `AimActive` ever turns true in a match, the provider has
            // not run its own `LateUpdate` yet, so its cache still reads
            // `default`: `End` is the origin, `Height` is zero, and the
            // notches would collapse onto that same point. Before this task
            // the case was unreachable — `AimHeld` required a held right
            // button, which the very first frame never has — but "the ray is
            // always on" makes it reachable in every single match.
            if (math.lengthsq(line.Dir) < 1e-8f)
            {
                SetDrawn(false, false);
                MuzzleGapMeters = float.NaN;
                return;
            }
            // app-461s T3 (⛔ THE VIEW DECIDES NO NUMBER, plan deviation 8):
            // the stroke's length is `AimLine`'s own pure function, handed the
            // two dials out of the ScriptableObject. Keeping the formula here
            // would put a numeric decision back into a MonoBehaviour, where
            // EditMode cannot reach it.
            float strokeLength = AimLine.NotchStroke(line.NotchHalfWidth,
                _gameFeel.AimRayNotchFrac, _gameFeel.AimRayNotchMinLength);
            // app-461s T3: the notches answer the HIP question — how wide the
            // cone is at the cursor — so they stand down while the right
            // button is held, where the ray alone is the honest picture
            // (class doc). ⭐ Switched OFF rather than drawn at zero length: a
            // `LineRenderer` whose two points coincide still draws a
            // degenerate quad, not nothing.
            SetDrawn(ray: true, notches: !aimHeld && strokeLength > 0f);

            // Stage 2 Task 45c (bd app-bej): aimed, the far end is where the
            // round COMES DOWN, not where the cursor points — the two part
            // company whenever the aim is low enough for the ground to take
            // the round first (`AimProvider.CurrentImpactWorldPoint`).
            // app-461s T2: from the hip the far end is a different read again
            // — `line.End`, lifted by `line.Height` — and NEVER
            // `CurrentImpactWorldPoint`: that point answers the aimed
            // question (cut short by the floor under the cursor), and reusing
            // it here would draw a ray diving out of the barrel into the
            // ground, exactly the lie this task exists to stop telling.
            Vector3 aimPoint = aimHeld
                ? _aimProvider.CurrentImpactWorldPoint
                : SimSpace.ToWorld(line.End) + Vector3.up * line.Height;

            _line.SetPosition(0, muzzle);
            _line.SetPosition(1, aimPoint);
            _line.startWidth = _line.endWidth = _gameFeel.AimRayWidth;

            // app-461s T2 (plan deviation 1): the plan-view gap between the
            // doll's muzzle socket and the simulation's own muzzle — see
            // `MuzzleGapMeters`'s own doc for why this class is its home.
            MuzzleGapMeters = math.distance(SimSpace.ToSim(muzzle), line.Start);

            // В1/В2 fix-wave 2 (app-n6g item 3a): same zone tint CrosshairView's
            // marker applies while AIMED, via the shared AimZoneColors lookup —
            // falls back to the ray's own baked cyan (_baseColor) on
            // HitZone.None, same as before this fix. Applied uniformly to the
            // WHOLE LineRenderer via MaterialPropertyBlock (no per-vertex/
            // gradient color anywhere on this component) — item 3b verified
            // this already colors the entire ray, not just its tip.
            // app-461s T2 (spec §3.4, owner decision Н39): from the hip there
            // is no zone to show at all — `AimZoneColors.Resolve` is not
            // called there, one flat yellow out of `GameFeelConfig` stands in
            // for it instead (ADR-002 A29(b): a client-side zone read never
            // reaches the picture, only the dev readout).
            Color rayColor = aimHeld
                ? AimZoneColors.Resolve(_aimProvider.CurrentAimZone, _baseColor, _gameFeel)
                : _gameFeel.AimRayHipColor;
            // В3 fix-wave 1 (app-n6g item 3b): headshot alignment gets an
            // extra brightness boost on top of the base AimRayAlpha dimming —
            // GameFeelConfig's own class doc has the "unmistakable, not a
            // faint dim-red tinge" rationale. Hip fire never qualifies: there
            // is no zone from the hip, so the boost is aimed-only too.
            float alphaBoost = aimHeld && _aimProvider.CurrentAimZone == HitZone.Head
                ? _gameFeel.AimRayHeadAlphaBoost : 1f;
            Color dimmed = rayColor * (_gameFeel.AimRayAlpha * alphaBoost);
            _block.SetColor("_BaseColor", new Color(dimmed.r, dimmed.g, dimmed.b, 1f));
            _line.SetPropertyBlock(_block);

            // app-461s T3: the notches, in ONE guarded block — color, width and
            // positions together.
            // ⛔ THE GUARD COVERS ALL THREE WRITES, NOT JUST THE POSITIONS: a
            // scene bootstrapped before this task has no children under
            // `AimRay` at all, and `SetPropertyBlock` on an unwired reference
            // would throw every single frame on exactly the scene the guard
            // exists for (`TryGetMuzzle`'s own socket check is the precedent).
            // The ray's own writes stay outside it — `[RequireComponent]`
            // makes `_line` unmissable, and hiding the ray behind a missing
            // child would be a worse failure than missing notches.
            // ⛔ AND COLOR AND WIDTH ARE PUSHED EXPLICITLY RATHER THAN INHERITED
            // FROM THE SHARED MATERIAL: the ray's color lives in the
            // MaterialPropertyBlock above, never in the material asset, and its
            // width lives in this renderer's own `startWidth`/`endWidth` — the
            // shared `aimRayMat` alone would leave the notches at the baked
            // cyan and at the default thickness.
            if (_notchLeft != null && _notchRight != null)
            {
                _notchLeft.SetPropertyBlock(_block);
                _notchRight.SetPropertyBlock(_block);
                _notchLeft.startWidth = _notchLeft.endWidth = _gameFeel.AimRayWidth;
                _notchRight.startWidth = _notchRight.endWidth = _gameFeel.AimRayWidth;
                AimLine.Notches(in line, strokeLength, out float2 a0, out float2 a1,
                    out float2 b0, out float2 b1);
                // The strokes ride the LINE, not the floor: their height is the
                // line's own, the muzzle height it was solved at.
                // ⚠ Point by point rather than `SetPositions(new Vector3[2])` —
                // the array form would allocate on every frame of every match.
                Vector3 up = Vector3.up * line.Height;
                _notchLeft.SetPosition(0, SimSpace.ToWorld(a0) + up);
                _notchLeft.SetPosition(1, SimSpace.ToWorld(a1) + up);
                _notchRight.SetPosition(0, SimSpace.ToWorld(b0) + up);
                _notchRight.SetPosition(1, SimSpace.ToWorld(b1) + up);
            }
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
