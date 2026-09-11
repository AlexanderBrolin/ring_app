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
    /// ternary `ShotGeometry.Solve` itself uses for the authoritative shot), so
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
    /// AND SINCE OWNER DECISION Н47 THE HIP END CARRIES A CEILING ON TOP OF
    /// THAT (app-461s, playtest of T2/T3: "the ray is nice, the spread marker
    /// is nice... at most 2/3 of the visible radius; if there is an obstacle
    /// the ray runs into it. Everything as it is now, the ray just has to be
    /// shorter"). A CEILING, NOT A REPLACEMENT FOR THE STOP: the body, the
    /// barrier and the rim all still end the line where the simulation says
    /// they do and they all still bite first — this only shortens what is
    /// left over, which in an empty field was the round's whole 78.75 m of
    /// range across a visible band of ground about 25 m wide, i.e. a ray that
    /// left the screen in every single match.
    /// ⛔ THE CEILING IS MEASURED OFF THE CAMERA AT RUNTIME AND CANNOT BE A
    /// NUMBER IN `GameFeelConfig`, which is the owner's own reading of it:
    /// "the visible radius is what the player sees on the monitor with the
    /// client running; depending on resolution and monitor the visibility
    /// differs a little". Only the FRACTION is a dial
    /// (`GameFeelConfig.AimRayScreenReachFrac`); the meters come from this
    /// camera's own view frustum, per frame, in the DIRECTION OF THE RAY —
    /// the rig looks down at a pitch, so the ground runs much further away up
    /// the screen than down it, and one averaged radius would be wrong for
    /// both (`TryFrustumExit`).
    /// ⛔ THE CEILING DOES NOT OVERRULE THE SPREAD MARKS, AND SINCE FIX
    /// ROUND 1 IT DOES NOT LOSE TO THEM BLINDLY EITHER: the floor that
    /// holds the ray out to the notches applies only while the notches are
    /// being drawn, and is itself clipped to the edge of what the camera
    /// shows. `DrawnLength`'s own doc carries both halves and why each one
    /// is needed.
    /// ⛔ AND `Ring.Simulation` IS NOT TOLD ABOUT ANY OF IT (Critical Rule 1):
    /// `AimLine` knows nothing of a camera or a resolution and must not, so
    /// `line.Length` stays whatever the simulation answered — the dev readout,
    /// the notches and the cone angle are all still measured off THAT. What
    /// this class shortens is the drawing alone.
    ///
    /// `[DefaultExecutionOrder(10)]` IS WHAT MAKES THAT ORIGIN THIS FRAME'S
    /// (fix-round 1, G-1). The socket rides a hand bone the Animator writes in
    /// `PreLateUpdate`, on a doll root `ViewRegistry` (pinned at −10) moves in
    /// its own `LateUpdate`; this class reads it in `LateUpdate` too, and Unity
    /// orders equal-order `LateUpdate`s arbitrarily — with no
    /// `ProjectSettings/MonoManager.asset` in the project, the ray's start could
    /// otherwise come from this frame or the previous one, and which of the two
    /// could differ between runs.
    /// WHAT ENDED THE HIP RAY THIS FRAME (app-461s Н47, fix round 1) — one
    /// answer out of `AimRayView.DrawnLength`, and the dev readout's only way
    /// to tell the three rules apart. Round one shipped a single "shorter than
    /// the simulation said" flag, which in an empty field was true on almost
    /// every frame and told the owner nothing about WHICH rule was doing it.
    /// ⚠ Declared beside the view rather than nested in it, same shape
    /// `PlayerSlotPicture` keeps next to `ViewRegistry` — and for the same
    /// reason: the enum is half of a pure function's answer, so it belongs to
    /// the rule rather than to the component.
    public enum AimRayLimit : byte
    {
        /// Nothing shortened the ray: it is as long as the simulation's own
        /// answer, and `AimLineSolution.Stop` already says what ended it
        /// there (a body, a barrier, the rim, or plain range). The screen is
        /// not in the picture at all, which is also the value every frame
        /// that draws no hip ray carries.
        Stop,

        /// The screen ceiling cut it — `GameFeelConfig.AimRayScreenReachFrac`
        /// of the visible reach in this direction. THE ONE STATE OWNER
        /// DECISION Н47 EXISTS TO PRODUCE.
        Ceiling,

        /// The notch floor held it out PAST that ceiling, because the cursor
        /// is further away than the ceiling and the two spread marks stand on
        /// the cursor. Not a failure: the marks must not hang past the ray's
        /// end.
        Notch,

        /// The notch floor was itself clipped to the edge of the visible
        /// area: the cursor is out past what the camera shows AT THE RAY'S
        /// HEIGHT, so the marks are off-screen anyway and following them
        /// would only put the ray's end off-screen with them.
        Edge,
    }

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
        // app-461s (owner decision Н47): the camera whose view frustum decides
        // how far out the hip ray may be drawn. Same slot shape and same
        // `mainCamera` the neighbor already keeps for its own screen-space
        // question (`CrosshairView._camera`, which billboards the marker) —
        // a second serialized reference rather than a reach through
        // `AimProvider`, whose own camera field is private for exactly the
        // encapsulation reason that comment gives.
        // ⚠ MUST SURVIVE BEING UNWIRED: a scene bootstrapped before this task
        // carries no value here at all, and that has to read as "no ceiling"
        // (the pre-Н47 picture), never as a ray of length zero — see
        // `ScreenReach`'s first line, the same null-guard shape
        // `_notchLeft`/`_notchRight` above already rely on.
        [SerializeField] Camera _camera;

        LineRenderer _line;
        MaterialPropertyBlock _block;
        // Г5 review (Minor): read from the bootstrap-created `_rayMaterial`
        // itself instead of a second hardcoded literal here duplicating
        // `StageOneSceneBootstrap`'s `AimRayEmissive` color — one number, one
        // owner. `Color.white` is the harmless fallback for the (never
        // expected in practice) case the reference isn't wired yet.
        Color _baseColor;
        /// app-461s (Н47): scratch for the NON-ALLOCATING overload of
        /// `GeometryUtility.CalculateFrustumPlanes` — the parameterless one
        /// returns a fresh `Plane[6]` per call, which on a per-frame path is a
        /// garbage-collected array per rendered frame of every match. Held as
        /// a readonly field, the same shape the neighbors' own per-frame
        /// scratch already takes (`MuzzleFlashView._pending`,
        /// `PersistentPropsDirector._pendingCasingSlots`).
        /// ⛔ EXACTLY SIX: the overload documents "an array of 6 Planes that
        /// will be overwritten", and any other length is a runtime error.
        readonly Plane[] _frustumPlanes = new Plane[6];

        /// Below this the ray counts as running PARALLEL to a frustum plane —
        /// both vectors are unit length there, so this is a cosine, not a
        /// distance. A parallel ray never leaves through that plane and the
        /// division that would find where it does is the one that produces an
        /// infinity.
        const float ParallelEpsilon = 1e-6f;

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

        /// app-461s (Н47): how long the HIP ray was ACTUALLY drawn this frame,
        /// in the same plane meters `AimLineSolution.Length` speaks. Read
        /// together with `DrawnLimit` below and never alone — the number is
        /// meaningless until you know which of the three rules produced it.
        /// ⚠ NaN ON EVERY FRAME THE HIP RAY IS NOT THE THING BEING DRAWN —
        /// the three early returns below (paused, the death screen, no doll,
        /// a cold aim cache) and the AIMED branch alike, since aimed the far
        /// end is a world point rather than a length along this line. Same
        /// contract, same dash in the readout, as `MuzzleGapMeters` above.
        public float DrawnLengthMeters { get; private set; } = float.NaN;

        /// app-461s Н47 fix round 1: WHICH rule produced `DrawnLengthMeters`.
        /// ⛔ THE PAIR IS THE INSTRUMENT, AND THE ROUND-ONE READOUT WAS NOT.
        /// That one printed the drawn length whenever it fell short of
        /// `line.Length` — but in an empty field `line.Length` is the round's
        /// whole 78.75 m of range while the notches stand at the cursor, so
        /// the condition was true on virtually every hip frame whether the
        /// ceiling had bitten or not, and when the cursor sat past the ceiling
        /// the number printed was the CURSOR's distance under a label that
        /// said "ceiling". An instrument that cannot tell its two states apart
        /// cannot witness the one change this task made, and this view has no
        /// other witness on the screen.
        public AimRayLimit DrawnLimit { get; private set; } = AimRayLimit.Stop;

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
        ///
        /// ⚠ THE PER-RENDERER NULL GUARDS BELOW ARE FOR SWITCHING OFF, AND
        /// THE ASYMMETRY IS DELIBERATE (app-461s T3 fix round 1). Darkening
        /// one notch while the other is missing is always safe; LIGHTING one
        /// while the other is missing is not, because the caller's write
        /// block needs the pair. So the decision to pass `notches: true` is
        /// the caller's, and it requires BOTH references — see `LateUpdate`'s
        /// `drawNotches`.
        void SetDrawn(bool ray, bool notches)
        {
            _line.enabled = ray;
            if (_notchLeft != null) _notchLeft.enabled = notches;
            if (_notchRight != null) _notchRight.enabled = notches;
        }

        /// All three dev readouts at once (app-461s Н47), for the reason
        /// `SetDrawn` above exists: they are reset together on every path out
        /// of `LateUpdate` that draws no hip ray, and a second
        /// caller-remembered line is a second thing to forget. Each one's own
        /// doc says what its blank value means to its reader.
        /// ⚠ `AimRayLimit.Stop` IS THE BLANK VALUE FOR THE THIRD, and it has
        /// to pair with the NaN rather than stand on its own: `DevOverlay`
        /// prints the length only for the three limits that are not `Stop`, so
        /// a limit left behind from a drawn frame would print a stale NaN.
        void ClearReadouts()
        {
            MuzzleGapMeters = float.NaN;
            DrawnLengthMeters = float.NaN;
            DrawnLimit = AimRayLimit.Stop;
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
                ClearReadouts();
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
                ClearReadouts();
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
                ClearReadouts();
                return;
            }
            // app-461s T3 (⛔ THE VIEW DECIDES NO NUMBER, plan deviation 8):
            // the stroke's length is `AimLine`'s own pure function, handed the
            // two dials out of the ScriptableObject. Keeping the formula here
            // would put a numeric decision back into a MonoBehaviour, where
            // EditMode cannot reach it.
            float strokeLength = AimLine.NotchStroke(line.NotchHalfWidth,
                _gameFeel.AimRayNotchFrac, _gameFeel.AimRayNotchMinLength);
            // app-461s T3: ONE predicate decides both whether the notches are
            // switched on and whether the block at the tail writes to them —
            // fix round 1's finding was that those two had drifted apart.
            // ⛔ BOTH REFERENCES, NOT EITHER: switching a renderer ON while
            // its writes are skipped is worse than drawing nothing at all. A
            // scene carrying exactly one of the two children (one deleted by
            // hand in the Inspector, or a merge of the scene file, with no
            // `Apply` since) would light the survivor up on whatever its YAML
            // holds — the default (0,0,0)→(0,0,1) segment at the default
            // width in the material's baked cyan, an emissive slab parked at
            // the middle of the arena for the whole match. Switching them OFF
            // stays safe one at a time, which is why `SetDrawn` keeps a guard
            // per renderer rather than one over the pair.
            // ⛔ AND THE CONE'S HALF-WIDTH HAS TO BE NON-ZERO: at
            // `NotchHalfWidth` zero the two strokes land point for point on
            // top of each other, and two renderers drawing the same quad in
            // the same place out of the same material z-fight. It is
            // reachable, not theoretical — a body up against the barrel
            // drives `Length`, hence `NotchDistance`, hence the half-width to
            // zero. `strokeLength > 0f` cannot catch that one: `NotchStroke`
            // takes a floor, so at the shipped `AimRayNotchMinLength` it is
            // never zero. It stays in the predicate anyway, because zeroing
            // BOTH dials is the documented way to silence the notches with no
            // code change (`GameFeelConfig`'s own doc).
            // ⛔ Hip only: the notches answer the HIP question — how wide the
            // cone is at the cursor — so they stand down while the right
            // button is held, where the ray alone is the honest picture
            // (class doc).
            // ⭐ Switched OFF rather than drawn at zero length: a
            // `LineRenderer` whose two points coincide still draws a
            // degenerate quad, not nothing.
            bool drawNotches = _notchLeft != null && _notchRight != null
                && !aimHeld && strokeLength > 0f && line.NotchHalfWidth > 0f;
            SetDrawn(ray: true, notches: drawNotches);

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
            // app-461s Н47: and that hip end is now `line.End` only while the
            // screen agrees — the length is `DrawnLength`'s, so the
            // point is rebuilt from the origin and the direction rather than
            // read off the `End` property. Same point whenever the ceiling
            // does not bite.
            // Hoisted out of the notch block at the tail (app-461s Н47): the
            // hip far end and both notch strokes ride the SAME plane — the
            // height the line was solved at — and the vector was being built
            // twice for the one number.
            Vector3 lineUp = Vector3.up * line.Height;

            Vector3 aimPoint;
            if (aimHeld)
            {
                aimPoint = _aimProvider.CurrentImpactWorldPoint;
                // ⛔ NO CEILING ON THE AIMED END, AND IT IS NOT AN OVERSIGHT:
                // that point is where the round comes down along the line to
                // the CURSOR, and the cursor is on the screen by definition —
                // so the aimed ray cannot run off the edge the way the hip
                // ray did, and Н47's complaint does not reach it. Capping it
                // anyway would only be able to pull the ray SHORT of
                // `CrosshairView`'s marker disc, which stands on that very
                // point: the same "a mark left hanging past the end of the
                // ray" failure the notch floor exists to prevent, for no gain
                // at all.
                DrawnLengthMeters = float.NaN;
                DrawnLimit = AimRayLimit.Stop;
            }
            else
            {
                // ⚠ MEASURED FROM THE SIMULATION'S MUZZLE, NOT THE DOLL'S
                // SOCKET: every length in play here — `line.Length`,
                // `line.NotchDistance`, the ceiling — is a distance along THIS
                // line from THIS origin, and mixing in the socket (a few
                // centimeters off, `MuzzleGapMeters`) would compare two
                // different rulers. The drawn segment still starts at the
                // socket, exactly as the class doc's "the two ends answer
                // different questions" paragraph already describes.
                // ⚠ `ToWorld` ON A DIRECTION rather than a position: the
                // mapping is the same linear one either way and `SimSpace` is
                // this project's only home for it (its own doc bans an inline
                // `new Vector3(p.x, 0f, p.y)` anywhere else). `line.Dir` is
                // unit length, which is what makes the frustum distance below
                // come out in meters.
                Vector3 lineOrigin = SimSpace.ToWorld(line.Start) + lineUp;
                Vector3 lineDir = SimSpace.ToWorld(line.Dir);
                // ⛔ `drawNotches` IS PASSED, NOT ASSUMED (fix round 1): the
                // floor that holds the ray out to the spread marks must not
                // apply on the frames nobody is drawing marks — see
                // `DrawnLength`'s own doc for what that costs otherwise.
                float drawnLength = DrawnLength(line.Length, line.NotchDistance,
                    ScreenReach(lineOrigin, lineDir), _gameFeel.AimRayScreenReachFrac,
                    drawNotches, out AimRayLimit limit);
                DrawnLengthMeters = drawnLength;
                DrawnLimit = limit;
                aimPoint = lineOrigin + lineDir * drawnLength;
            }

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
            // ⛔ THE GUARD IS THE SWITCH'S OWN PREDICATE, AND THAT IS FIX ROUND
            // 1's FINDING: the two used to be written separately, so a scene
            // holding exactly one of the two children switched the survivor on
            // and then skipped every write to it. One boolean, one answer —
            // they cannot drift apart again.
            // ⇒ `drawNotches` already carries the null check, and a scene
            // bootstrapped before this task (no children under `AimRay` at
            // all) therefore neither lights them nor dereferences them, the
            // same shape `TryGetMuzzle`'s own socket check keeps.
            // The ray's own writes stay OUTSIDE it — `[RequireComponent]`
            // makes `_line` unmissable, and hiding the ray behind a missing
            // child would be a worse failure than missing notches.
            // ⚠ NOT RUN WHILE THE NOTCHES ARE DARK, AND NOT FOR THE FRAME AFTER
            // EITHER: the switch above sits in this same `LateUpdate`, above
            // this block, so the very frame that turns them back on falls
            // through to here and writes fresh positions before anything is
            // rendered. Ten managed-to-native calls per aimed frame bought
            // nothing at all.
            // ⛔ COLOR AND WIDTH ARE PUSHED EXPLICITLY RATHER THAN INHERITED
            // FROM THE SHARED MATERIAL: the ray's color lives in the
            // MaterialPropertyBlock above, never in the material asset, and its
            // width lives in this renderer's own `startWidth`/`endWidth` — the
            // shared `aimRayMat` alone would leave the notches at the baked
            // cyan and at the default thickness.
            if (drawNotches)
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
                _notchLeft.SetPosition(0, SimSpace.ToWorld(a0) + lineUp);
                _notchLeft.SetPosition(1, SimSpace.ToWorld(a1) + lineUp);
                _notchRight.SetPosition(0, SimSpace.ToWorld(b0) + lineUp);
                _notchRight.SetPosition(1, SimSpace.ToWorld(b1) + lineUp);
            }
        }

        /// WHICH OF THREE RULES ENDS THE HIP RAY, AND HOW LONG IT COMES OUT
        /// (app-461s, owner decision Н47; fix round 1 made it a pure static
        /// and gave it the `limit` half). Pure and static SO THAT IT HAS A
        /// WITNESS: `Ring.Simulation.Tests` references `Ring.Presentation`,
        /// and the precedents for a view's numeric decision living out here
        /// where EditMode can reach it are `ViewRegistry.PictureFor` and
        /// `HudController.WaveAnnounceTimerAfter`. Round one left the
        /// arithmetic inside `LateUpdate` on the belief that a
        /// `MonoBehaviour`'s rule cannot be tested at all, which was simply
        /// wrong about this project.
        ///
        /// THE THREE RULES, IN THE ORDER THEY APPLY:
        /// 1. THE SIMULATION'S OWN STOP (`length`) is the starting point and
        ///    the longest the ray can ever be. A body, a barrier or the rim
        ///    still ends it wherever the simulation says — Н47 asked for a
        ///    CEILING, never for a replacement ("everything as it is now, the
        ///    ray just has to be shorter").
        /// 2. THE SCREEN CEILING (`reach`) cuts it. This is the
        ///    whole of decision Н47: in an empty field the ray used to run the
        ///    round's full 78.75 m of range across a visible band of ground
        ///    about 25 m wide, so it left the screen in every match.
        /// 3. THE NOTCH FLOOR holds it back OUT to the notches — but only
        ///    while the notches are actually drawn, and never past the edge of
        ///    what the camera shows. Both halves of that sentence are fix
        ///    round 1's, and both are explained below.
        ///
        /// ⛔⛔ THE FLOOR IS CONDITIONAL ON `drawNotches`, WHICH ROUND ONE
        /// MISSED: the two spread marks can be silenced with no code change
        /// (`AimRayNotchFrac` and `AimRayNotchMinLength` both at zero,
        /// `GameFeelConfig`'s own documented way), and a scene bootstrapped
        /// before the task that added them carries no notch renderers at all.
        /// An unconditional floor would then hold the ray out to a cursor
        /// whose marks nobody is drawing — which switches decision Н47 off
        /// entirely for every cursor past two thirds, in the exact
        /// configurations where the ray is the only thing on screen.
        ///
        /// ⛔⛔ AND THE FLOOR IS ITSELF CAPPED AT `reach`, THE FULL VISIBLE
        /// DISTANCE WITH NO FRACTION APPLIED. The cursor is a point on the
        /// FLOOR while the ray and its marks ride the line's own height, so
        /// they project HIGHER up the screen than the cursor does: for a
        /// cursor in the top few percent of the frame the ray's end and both
        /// notches are already past the top edge. There the floor would be
        /// protecting an anchor that is not on screen to anchor anything, and
        /// would reproduce the very defect Н47 exists to remove. Capped at
        /// `reach` the rule reads: cursor within the frame, the ray reaches
        /// its marks; cursor past what the camera shows, the ray ends at the
        /// edge instead of running off it.
        ///
        /// ⚠ `reach` AT OR BELOW ZERO IS THE DOCUMENTED "THE SCREEN HAD NO
        /// ANSWER" VALUE (`TryFrustumExit` writes zero on every one of its
        /// false paths), and it returns `length` unchanged — the pre-Н47
        /// picture, never a collapsed ray. NaN takes the same path, because
        /// `!(reach > 0f)` is false for it.
        /// ⚠ AND `length` ITSELF IS NEVER TOUCHED (Critical Rule 1): the dev
        /// readout's `d`, the cone angle and the notch positions are all still
        /// the simulation's own answer. What comes back here is a drawing
        /// length and nothing else.
        public static float DrawnLength(float length, float notchDistance, float reach,
            float screenFrac, bool drawNotches, out AimRayLimit limit)
        {
            limit = AimRayLimit.Stop;
            if (!(reach > 0f)) return length;

            float drawn = length;
            float ceiling = reach * screenFrac;
            if (ceiling < drawn)
            {
                drawn = ceiling;
                limit = AimRayLimit.Ceiling;
            }
            if (!drawNotches) return drawn;

            // ⚠ THE ANCHOR IS CLAMPED TO `length` FIRST, AND THAT IS NOT THE
            // SAME KIND OF DEAD BRANCH AS A CONFIGURATION NOBODY USES: today
            // `AimLine` builds `notchDistance` as `min(length, toCursor)`, so
            // the clamp changes no shipped frame — but this is a public pure
            // function, and without it a caller that does not hold that
            // invariant gets a ray drawn straight THROUGH the body that
            // stopped it. One `min` against a paragraph asking every future
            // caller to remember.
            // ⚠ AND IT IS WHAT KEEPS THE READOUT'S TWO LABELS HONEST: with
            // the anchor clamped, `floor < anchor` can only mean the SCREEN
            // clipped it, which is exactly what `Edge` claims.
            float anchor = math.min(notchDistance, length);
            float floor = math.min(anchor, reach);
            if (floor > drawn)
            {
                drawn = floor;
                limit = floor < anchor ? AimRayLimit.Edge : AimRayLimit.Notch;
            }
            return drawn;
        }

        /// How far a ray leaving `origin` along unit `dir` travels before it
        /// crosses out of the frustum `planes` bound — the runtime "visible
        /// radius" owner decision Н47 asks for a fraction of, measured IN THE
        /// DIRECTION OF THE RAY and AT THE RAY'S OWN HEIGHT, both of which the
        /// caller bakes into the two vectors. False, with `distance` at zero,
        /// means "no usable answer", which `DrawnLength` reads as "no
        /// ceiling". Pure and static for the same reason `DrawnLength` above
        /// is: EditMode can build six `Plane`s and ask.
        ///
        /// ⭐ FRUSTUM PLANES RATHER THAN A SEARCH OVER `WorldToScreenPoint`:
        /// the question "where does this segment leave the screen" has a
        /// closed-form answer — six planes, one dot product and one divide
        /// each — while a bisection over projected points would cost dozens of
        /// matrix transforms per rendered frame to land on the same number
        /// approximately.
        /// ⚠ ALL SIX PLANES, NOT THE FOUR SIDES — decided, not assumed. Taking
        /// only the sides would mean hardcoding Unity's documented index order
        /// (left, right, down, up, near, far) into this loop, and fix round
        /// 1's review swept 48 600 frames across the whole range of camera
        /// pitch and aspect ratio this project can produce: a side plane won
        /// every single time, near and far never once. The dependency buys
        /// nothing, so it is not taken.
        /// ⚠ THE SIGN TEST IS WHAT PICKS THE RIGHT CROSSINGS: the planes face
        /// INWARD, so a negative `closing` is the ray heading OUT through that
        /// plane and a positive one is it running deeper inside. Only the
        /// outbound ones can end the visible part of the ray.
        ///
        /// ⛔⛔ AN ORIGIN OUTSIDE ANY PLANE ANSWERS FALSE, AND THAT TEST IS
        /// LOAD-BEARING RATHER THAN DEFENSIVE (fix round 1's finding). Without
        /// it the loop still answers: the planes the origin is INSIDE of go on
        /// producing positive crossings, so a ray that is nowhere near the
        /// screen comes back with a confident ceiling measured off a frustum
        /// it is not in — the review's own case put the origin 12 m below the
        /// bottom plane and got a 17.43 m ceiling out of the NEAR plane. The
        /// case is reachable, not theoretical: the rig damps toward the doll
        /// (`CameraRig`), so a teleport or a respawn can leave the collector
        /// outside the frame for a frame or two, and a ceiling invented there
        /// would visibly snap the ray short.
        ///
        /// THE OTHER DEGENERATE CASES, ONE BY ONE — every one answers false,
        /// i.e. leaves the ray at its simulated length:
        /// - NO CAMERA WIRED. Caught by the caller (`ScreenReach`), on a scene
        ///   bootstrapped before this task; the ray keeps the picture it had
        ///   then.
        /// - THE RAY PARALLEL TO A PLANE. `ParallelEpsilon` drops that plane
        ///   rather than dividing by ~0; a ray parallel to every plane it
        ///   could leave by finds no crossing at all and answers false.
        /// - A ZERO OR NEGATIVE DISTANCE. Rejected by the `t > 0f` test, which
        ///   is also what keeps a NaN out of `nearest` — a NaN fails every
        ///   comparison, so it can never be stored.
        public static bool TryFrustumExit(Plane[] planes, Vector3 origin, Vector3 dir,
            out float distance)
        {
            distance = 0f;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < planes.Length; i++)
            {
                Plane plane = planes[i];
                // `GetDistanceToPoint` is the plane equation itself — positive
                // on the inward side, since the normals face in.
                float side = plane.GetDistanceToPoint(origin);
                if (side < 0f) return false;
                float closing = Vector3.Dot(plane.normal, dir);
                if (closing >= -ParallelEpsilon) continue;
                // The ordinary ray/plane solve, in meters because `dir` is
                // unit length.
                float t = -side / closing;
                if (t > 0f && t < nearest) nearest = t;
            }
            if (float.IsPositiveInfinity(nearest)) return false;
            distance = nearest;
            return true;
        }

        /// This frame's visible reach along the ray, or zero when the camera
        /// cannot give one — the value `DrawnLength` reads as "no ceiling".
        /// The arithmetic is `TryFrustumExit` above; what is left here is the
        /// one thing a static cannot do, which is ask the camera.
        /// ⚠ THE UNWIRED CAMERA MUST READ AS "NO CEILING", NEVER AS A RAY OF
        /// LENGTH ZERO: a scene bootstrapped before this task carries no value
        /// in `_camera` at all, and the pre-Н47 picture is the right answer
        /// there.
        float ScreenReach(Vector3 origin, Vector3 dir)
        {
            if (_camera == null) return 0f;
            GeometryUtility.CalculateFrustumPlanes(_camera, _frustumPlanes);
            return TryFrustumExit(_frustumPlanes, origin, dir, out float reach) ? reach : 0f;
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
