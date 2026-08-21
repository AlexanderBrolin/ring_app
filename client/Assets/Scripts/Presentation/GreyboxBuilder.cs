using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Builds a greybox arena purely from `ArenaConfig` numbers: a floor disc
    /// tinted per zone, a ring wall made of tangent-aligned cube segments, one
    /// arc band per configured zone wall (with a cutout and two jambs per
    /// door), one stadium per configured interior wall, one cylinder per
    /// configured obstacle and one floor ring per configured extraction point
    /// — all parented under this transform. Presentation-only:
    /// this never touches Simulation state, it just visualizes the same
    /// `ArenaConfig`/`ArenaSimConfig` numbers Simulation collides against
    /// (`Geometry.cs`), so the visible surface follows sim collision by
    /// construction (same radius, same center at world/sim origin per `SimSpace`)
    /// — down to two approximations worth naming rather than leaving to be
    /// discovered (fix-round 1, Ф-5). A primitive cylinder is a polygonal prism
    /// inscribed in its circle, not a circle: its drawn face lies inside the
    /// simulated one by R·(1 − cos(π/sides)) — centimeters at the radii the
    /// PRIMITIVES here are drawn at, which is obstacles and wall caps and
    /// nothing else. Anything at a zone's own radius is drawn from a mesh this
    /// file generates instead, segmented by `MeshSagMeters` like every arc:
    /// twenty sides costs 0.80 m of error at 65 and 1.13 m at 92
    /// (`BuildTintDisc`'s own doc measures it).
    /// And the ring wall is drawn as 0.5 m thick boxes CENTERED on the radius,
    /// while Simulation stops a round when its center reaches `Radius` minus the
    /// round's own radius — so at the rim a round buries up to ~0.17 m into the
    /// drawn wall before it ends. Both are pre-Task-46 and neither is a defect
    /// — they are what "the picture matches the collision" costs, said out loud
    /// rather than left under an absolute.
    ///
    /// PhysX colliders on the generated primitives are intentionally kept (they
    /// come free with `CreatePrimitive`) and moved to the `Cosmetics` layer
    /// (`TagManager.asset` user layer 8) — EXCEPT on the extraction rings Task
    /// 30 adds, which are pictures with nothing behind them and drop theirs
    /// outright (`DropCollider`); the zone floor tints of the same task are
    /// generated meshes rather than primitives and never had one to drop.
    /// They have TWO consumers, not one:
    /// cosmetic props (Task 27 shell casings) bounce off the arena, and since
    /// Stage 2 Task 46 `AimProvider` casts against this same layer to find out
    /// whether the cursor is standing behind a barrier (bd app-1ru). Simulation
    /// still never queries PhysX — it has its own analytic collision
    /// (`Geometry.SweepArena`/`Depenetrate`) against the same `ArenaConfig`
    /// numbers.
    ///
    /// WHAT THAT SECOND CONSUMER DECIDES, AND WHAT IT DOES NOT (fix-round 1,
    /// Ф-3, replacing this paragraph's earlier "no game outcome is ever decided
    /// by a collider here" — an absolute the same task stopped holding). The
    /// cast shapes the local player's aim INPUT: its result is what `AimProvider`
    /// caches, `InputSampler` copies that into the frame's `SimInput`, and the
    /// authoritative server resolves the shot from that input alone (CR 3
    /// intact — an input is not an outcome). So nothing is decided here; but
    /// moving or resizing one of these colliders is not cosmetic either, because
    /// it silently changes where the player is ABLE to aim, and `Presentation/`
    /// is the client track's own zone by CODEOWNERS. One fact that would
    /// otherwise be guessed the wrong way: these colliders live on the CLIENT
    /// scene only — this component is placed in `Main.unity` and in neither
    /// `Server.unity` nor `AssetPreview.unity` — so the headless server has no
    /// greybox and no PhysX arena at all, and never asks one anything.
    ///
    /// `BuildFloor` swaps its `CreatePrimitive`-default `CapsuleCollider` for a
    /// `BoxCollider` (app-4qc, Б1 milestone find): under the floor's
    /// non-uniform (2R, 0.5, 2R) scale, PhysX degenerates a capsule (height 1 &lt;
    /// 2×radius R) into a plain SPHERE of radius R — a dome whose apex sits
    /// ~R-0.5 above the arena center — so anything cosmetic spawned near
    /// ground level started deep INSIDE it and got depenetrated tens of
    /// meters up and sideways.
    ///
    /// EVERY CYLINDER HERE NOW CARRIES A CONVEX `MeshCollider` INSTEAD OF ITS
    /// DEFAULT CAPSULE (Stage 2 Task 46, replacing this paragraph's earlier
    /// claim). The obstacles used to keep the default capsule on the argument
    /// that its degenerate sphere was "visually indistinguishable from a true
    /// cylinder for casing bounce purposes". That argument was already generous
    /// when it was made, measured against the arena of ITS day (fix-round 1,
    /// Ф-5: this passage used to measure it against today's): obstacles were
    /// drawn a flat 2 m then, which scales the capsule to radius 3.2 / height 2
    /// — height &lt; 2×radius — so PhysX collapsed it to a sphere of radius 3.2
    /// centered at y = 1, reaching 4.2 m, i.e. 2.2 m over a 2 m crown. At this
    /// task's 3 m the same collapse puts the sphere's center at y = 1.5, its top
    /// at 4.7 and its bottom at −1.7: ~1.7 m over the crown and as far under the
    /// floor — a smaller overhang for a taller barrier, and still an overhang
    /// either way. Bounce was the only consumer of it, so it stood. It stopped
    /// standing when
    /// `AimProvider` began casting against this layer to decide whether the
    /// cursor sits behind a barrier: the collider now decides where the player
    /// is ALLOWED to aim, and a bulge over the crown refuses hover on a mob
    /// standing plainly visible above it. A convex `MeshCollider` over the
    /// primitive's own mesh is exactly the shape being drawn, at any scale,
    /// which also removes the silent dependency on whether height happens to
    /// exceed twice the radius.
    ///
    /// Idempotent by child count: `Build()` runs once from `Awake` and does
    /// nothing if this transform already has children (e.g. a second `Awake` from
    /// a domain reload without a scene reopen). Arena topology is off-limits to
    /// hot-tweak while the match is running (`SimulationWorld.ApplyConfig` throws
    /// on a topology change, spec §3.9) — the only place topology legitimately
    /// changes is across a full match restart, which is what `Rebuild()` (Task
    /// 24 spec Interfaces) below is for: destroys whatever primitives currently
    /// exist and reconstructs from the CURRENT `ArenaConfig` values, so a restart
    /// that followed a hot-tweak fallback (`SimulationRunner.Update`'s
    /// `ArgumentException` catch) never leaves the greybox showing stale geometry
    /// that no longer matches what Simulation collides against.
    public sealed class GreyboxBuilder : MonoBehaviour
    {
        const float WallHeight = 3f;
        const float WallY = 1.5f;
        const float WallThickness = 0.5f;

        const float FloorScaleY = 0.5f;
        const float FloorY = -0.5f;

        /// Floor for the derived segment count of any ring (Task 30): three
        /// boxes still make a closed figure, and nothing below three does.
        /// It can only ever bind on a ring so small that `MeshSagMeters`
        /// exceeds it outright — an extraction radius of centimeters — where
        /// the sagitta arithmetic has nothing left to resolve.
        const int MinRingSegments = 3;

        /// Structural geometry of the floor rings drawn around extraction
        /// points (Task 30, spec §3.11) — half-width and height of the band,
        /// and how far each painted layer is lifted off the floor's top face
        /// at y = 0. Code constants rather than `GameFeelConfig` fields on the
        /// same split this file already makes for `WallThickness`/`FloorY`
        /// (and `ViewRegistry` for `MobOffset`): a marker's own thickness is
        /// positioning, not feel — what the owner tunes about an extraction
        /// point is its RADIUS, and that is `ArenaConfig.ExtractRadius`,
        /// read straight from the asset below.
        ///
        /// THE LIFT IS PER LAYER AND IT IS WHY THE TINT DISCS AND THE RINGS
        /// DISAGREE ABOUT IT. Two coplanar surfaces at the same y z-fight;
        /// stacking the zone tints (bigger disc lower) and then the extraction
        /// rings above all of them puts every painted surface on its own
        /// plane, in the order they must occlude each other. 1 cm apart is
        /// under the thickness of a spent casing, so nothing cosmetic resting
        /// on the floor is visibly swallowed by a layer above it.
        const float FloorPaintLift = 0.01f;
        const float ExtractRingHalfWidth = 0.25f;
        const float ExtractRingHeight = 0.06f;

        /// User layer 8 — named "Cosmetics" in `ProjectSettings/TagManager.asset`.
        /// Public (Task 27): `PersistentPropsDirector`/`StageOneSceneBootstrap`
        /// reuse this exact constant for casings/decals/corpses instead of
        /// redeclaring the literal `8` a second/third time (reuse > duplication).
        public const int CosmeticsLayer = 8;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] ArenaConfig _arena;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] Material _floor;
        [SerializeField] Material _wall;
        [SerializeField] Material _obstacle;
        [SerializeField] Material _portalRing;
        [SerializeField] Material _gateRing;

        /// URP's base-color property, written through a `MaterialPropertyBlock`
        /// so the three zone tints share the ONE `Floor` material asset instead
        /// of instancing it three times — the same idiom `PlayerView`/`MobView`/
        /// `CorpseView`/`DashGlowView` already use for their own accents.
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        /// Meshes this component built itself (the zone tint fans). A mesh is
        /// not owned by the renderer that draws it, so `Rebuild` has to free
        /// them explicitly or a match restarted often enough leaks one per
        /// zone per restart.
        readonly System.Collections.Generic.List<Mesh> _generatedMeshes =
            new System.Collections.Generic.List<Mesh>();

        void Awake()
        {
            Build();
        }

        // WorldRestarted is not a tick event (П-1 only restricts TicksFlushed to
        // its sole SimEventRouter subscriber) — direct subscription, same shape
        // as the deleted PracticeTargets' pattern.
        void OnEnable() => _runner.WorldRestarted += Rebuild;

        void OnDisable() => _runner.WorldRestarted -= Rebuild;

        public void Build()
        {
            if (transform.childCount > 0) return;

            BuildContent();
        }

        /// Task 24 spec Interfaces — see class doc. Safe no matter how many times
        /// or in what order this fires relative to `Awake`'s own `Build()` call
        /// (cross-object Awake/OnEnable ordering against `SimulationRunner` isn't
        /// guaranteed, same caveat as the deleted `PracticeTargets`): always ends
        /// with exactly one fresh set of primitives, `Build()`'s own child-count
        /// guard included — worst case (this subscription catching the very
        /// first `WorldRestarted` before this object's own `Awake` runs) is one
        /// harmless extra destroy-and-rebuild pass over zero children.
        public void Rebuild()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
                Destroy(transform.GetChild(i).gameObject);
            ReleaseGeneratedMeshes();

            BuildContent();
        }

        void OnDestroy() => ReleaseGeneratedMeshes();

        void ReleaseGeneratedMeshes()
        {
            for (int i = 0; i < _generatedMeshes.Count; i++) Destroy(_generatedMeshes[i]);
            _generatedMeshes.Clear();
        }

        void BuildContent()
        {
            BuildFloor();
            BuildZoneTint();
            BuildWall();
            BuildZoneWalls();
            BuildWallSegments();
            BuildExtractionRings();
            BuildObstacles();
        }

        /// Height every INTERIOR barrier is drawn at — obstacle cylinders and
        /// wall stadiums alike, from the one `ArenaConfig.BarrierTop` number
        /// Simulation gates shots against (Stage 2 Task 46), so the drawn height
        /// and the gated height are the same number and cannot be tuned apart.
        /// (The class doc names what the two still approximate in PLAN.) The
        /// outer ring wall keeps its own `WallHeight`: it has no modelled top
        /// at all.
        ///
        /// READING SOMEBODY ELSE'S NUMBER, SAID OUT LOUD. A non-positive
        /// `BarrierTop` means "no modelled top" — Simulation stops a shot there
        /// at any height — and geometry of zero height would be invisible, so
        /// those barriers are drawn at the ring wall's height instead. That is
        /// not a clamp papering over bad data: "there is no top" honestly looks
        /// like "as tall as the world" from inside the arena, and the tallest
        /// thing the arena draws is its own boundary.
        float BarrierHeight => _arena.BarrierTop > 0f ? _arena.BarrierTop : WallHeight;

        /// Are the two zone boundaries this arena's Simulation reads actually
        /// there? Task 30 (lesson 315): `Geometry.ZoneOf` indexes
        /// `ZoneRadius[0]`/`[1]` with no guard of its own, so every READER of
        /// zone data gets to check first — and this builder reads the asset
        /// directly from `Awake`, before any `SimConfigBuilder` validation has
        /// necessarily run over it. A zoneless arena is the LEGAL Stage 2
        /// arena (`ZoneWallCount == 0`, `TestConfigs.Open()`), not a broken
        /// one: it simply draws no tint and no arcs, exactly as it did before
        /// this task.
        bool HasZones => _arena.ZoneRadius != null && _arena.ZoneRadius.Length >= 2;

        void BuildFloor()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            floor.name = "Floor";
            floor.layer = CosmeticsLayer;
            floor.transform.SetParent(transform, false);
            floor.transform.localPosition = new Vector3(0f, FloorY, 0f);
            floor.transform.localScale = new Vector3(_arena.Radius * 2f, FloorScaleY, _arena.Radius * 2f);
            floor.GetComponent<MeshRenderer>().sharedMaterial = _floor;
            // The OUTER zone's tint rides the floor disc itself rather than a
            // fourth disc stacked on top of it: the outer zone is the whole
            // floor minus what the two smaller discs cover, so painting the
            // base costs nothing and saves a layer of lift (Task 30).
            if (HasZones) Tint(floor, _gameFeel.ZoneTintOuter);

            // CreatePrimitive(Cylinder) ships a CapsuleCollider, which under this
            // non-uniform scale (2R, 0.5, 2R) degenerates in PhysX to a SPHERE of
            // radius R (height 1 < 2*radius) — a dome whose apex sits ~R-0.5 above
            // the arena center. Anything cosmetic spawned near ground level starts
            // deep INSIDE that collider and gets depenetrated tens of meters up and
            // sideways (app-4qc, Б1 milestone find). A local (1,2,1) box scales to
            // the exact visual disc bounds: top face at world y = 0.
            Collider degenerate = floor.GetComponent<Collider>();
            degenerate.enabled = false; // Destroy is deferred to end-of-frame — kill the dome NOW
            Object.Destroy(degenerate);
            BoxCollider floorBox = floor.AddComponent<BoxCollider>();
            floorBox.size = new Vector3(1f, 2f, 1f);
        }

        /// Paints one already-built primitive through a shared property block —
        /// see `BaseColorId`. A fresh block per call rather than a cached
        /// field: this runs a handful of times per arena build, never per
        /// frame, and a cached block would have to be cleared between two
        /// renderers anyway.
        static void Tint(GameObject go, Color color)
        {
            var block = new MaterialPropertyBlock();
            block.SetColor(BaseColorId, color);
            go.GetComponent<MeshRenderer>().SetPropertyBlock(block);
        }

        /// Kills a primitive's collider outright — for the flat painted
        /// surfaces (zone tints, extraction rings) that are pictures and
        /// nothing else. They must not answer `AimProvider`'s cast: a cast
        /// stopped by a painted ring would report "behind a barrier" over open
        /// floor and refuse the player their aim (Stage 2 Task 46's whole
        /// mechanism, read backwards). Disabled BEFORE `Destroy` for the same
        /// reason `BuildFloor` does it: `Destroy` is deferred to end of frame
        /// and PhysX would answer with the doomed collider until then.
        static void DropCollider(GameObject primitive)
        {
            Collider c = primitive.GetComponent<Collider>();
            c.enabled = false;
            Object.Destroy(c);
        }

        /// Segments a full ring of radius `ringR` needs so that no drawn chord
        /// departs from the true circle by more than `GameFeelConfig.
        /// MeshSagMeters` (Task 30, spec Р273 — that field carries the
        /// arithmetic and the reason it is a field). Every arc in this file
        /// steps by this ring's own angle, so a zone wall and the boundary
        /// wall of the same radius would be segmented identically.
        ///
        /// The clamps are for a hand-edited asset, not for the shipped one:
        /// `[Range]` bounds `MeshSagMeters` in the Inspector but nothing
        /// bounds a value typed into the YAML, and both a non-positive sag
        /// (an infinite count) and a sag past the diameter (an arccos argument
        /// under −1) are arithmetic the formula cannot answer.
        int RingSegments(float ringR)
        {
            float sag = Mathf.Max(_gameFeel.MeshSagMeters, Mathf.Epsilon);
            float cos = Mathf.Clamp(1f - sag / ringR, -1f, 1f);
            float halfAngle = Mathf.Acos(cos);
            if (halfAngle <= 0f) return MinRingSegments;
            return Mathf.Max(MinRingSegments, Mathf.CeilToInt(Mathf.PI / halfAngle));
        }

        /// The one loop every ring band in this file is drawn by (Task 30,
        /// plan errata C-M6, which asks for one loop parameterized rather than a
        /// second policy): the tangent boxes covering the angular span
        /// [`from`, `from + span`] of a band centered on radius `ringR` and
        /// `halfW` thick, `height` tall, its base at `baseY`. The boundary
        /// wall passes a full turn and no doors; a zone wall passes one span
        /// per gap between its doors; an extraction marker passes a full turn
        /// of a flat, collider-less band.
        ///
        /// WIDTH IS DERIVED, NOT PADDED. A box centered on `ringR` presents a
        /// FLAT outer face tangent to the circle of radius `ringR + halfW`, so
        /// two neighbors a step apart meet exactly where their two tangent
        /// planes cross — at `(ringR + halfW)·tan(step/2)` from each tangent
        /// point. Widening by that much and no more closes the notch between
        /// them without piling overlap into the band's inside. (The ring
        /// wall's own former `WallSegmentOverlap` of 1.05 was that number
        /// guessed instead of solved: a 5 % pad happens to cover the notch at
        /// 48 segments on a 113 m radius and covers nothing in particular at
        /// any other pair of numbers.)
        ///
        /// `index` is threaded by reference so one wall's spans number their
        /// children continuously — a door does not restart the count, so
        /// "ZoneWall_00_Segment_31" names the thirty-second box of that wall
        /// rather than the first box of its second span, and the hierarchy
        /// reads as one ring with holes in it instead of three unrelated
        /// stretches.
        void BuildArcSpan(Transform parent, string namePrefix, ref int index, float ringR,
            float halfW, float height, float baseY, float from, float span, Material material,
            bool collides)
        {
            int segments = Mathf.Max(1,
                Mathf.CeilToInt(span / (2f * Mathf.PI) * RingSegments(ringR)));
            float step = span / segments;
            float width = 2f * (ringR + halfW) * Mathf.Tan(step * 0.5f);

            for (int i = 0; i < segments; i++)
            {
                float angle = from + (i + 0.5f) * step;
                // Radial direction at this angle also doubles as the segment's
                // local forward (Z) axis via LookRotation below, which makes the
                // local X axis (the segment's width) fall out tangent to the ring
                // automatically — no separate tangent-vector math needed.
                var radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                GameObject seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                seg.name = $"{namePrefix}_{index++:00}";
                seg.layer = CosmeticsLayer;
                seg.transform.SetParent(parent, false);
                seg.transform.localPosition = radial * ringR + Vector3.up * (baseY + height * 0.5f);
                seg.transform.localRotation = Quaternion.LookRotation(radial, Vector3.up);
                seg.transform.localScale = new Vector3(width, height, halfW * 2f);
                seg.GetComponent<MeshRenderer>().sharedMaterial = material;
                if (!collides) DropCollider(seg);
            }
        }

        void BuildWall()
        {
            var wallRoot = new GameObject("Wall");
            wallRoot.transform.SetParent(transform, false);

            // The arena boundary: a full turn, no doors, and the one band in
            // this file whose height is its own `WallHeight` rather than
            // `BarrierHeight` — it has no modelled top (see `BarrierHeight`).
            int index = 0;
            BuildArcSpan(wallRoot.transform, "WallSegment", ref index, _arena.Radius,
                WallThickness * 0.5f, WallHeight, WallY - WallHeight * 0.5f,
                0f, 2f * Mathf.PI, _wall, true);
        }

        /// The three zones, told apart by the color of the ground (Task 30,
        /// spec §3.11). The outer zone is the floor disc itself
        /// (`BuildFloor`); the two inner zones are thin discs stacked on top
        /// of it, biggest first, each on its own `FloorPaintLift` plane.
        ///
        /// RADII COME FROM THE SAME TWO NUMBERS `Geometry.ZoneOf` DECIDES BY,
        /// so the color under a player's feet cannot disagree with the zone
        /// the server thinks they are in — the whole point of drawing this at
        /// all. `ZoneRadius[0]` bounds the Core, `[1]` the Middle: that is
        /// `ZoneOf`'s own order of tests, not an assumption made here.
        void BuildZoneTint()
        {
            if (!HasZones) return;

            var tintRoot = new GameObject("ZoneTint");
            tintRoot.transform.SetParent(transform, false);

            BuildTintDisc(tintRoot.transform, "ZoneTint_Middle", _arena.ZoneRadius[1],
                FloorPaintLift, _gameFeel.ZoneTintMiddle);
            BuildTintDisc(tintRoot.transform, "ZoneTint_Core", _arena.ZoneRadius[0],
                FloorPaintLift * 2f, _gameFeel.ZoneTintCore);
        }

        /// One painted disc, flat on the floor at `centerY`, no collider and no
        /// thickness — a generated triangle fan rather than a primitive.
        ///
        /// AND THE FAN IS THE WHOLE POINT (fix-round, Ф7 review B-I1). A
        /// `PrimitiveType.Cylinder` is a TWENTY-sided prism inscribed in its
        /// circle — the class doc has said so since Т46 — and twenty sides is
        /// cheap at an obstacle's 2 m radius and ruinous at a zone boundary's:
        /// the drawn edge falls `R·(1 − cos(π/20))` short of the true radius,
        /// which is 0.80 m at the Core's 65 and 1.13 m at the Middle's 92,
        /// sixteen to twenty-two times the 5 cm this same file derives for
        /// every arc it draws, on facets 20 to 29 m long. Two places where
        /// that is not academic: in the six doorways there is no wall to hide
        /// the seam under, so the player crosses a boundary whose paint
        /// disagrees with `Geometry.ZoneOf` by up to 0.8 m at exactly the spot
        /// they cross it; and the Middle disc's own edge would sink to
        /// 92·cos(9°) = 90.87, INSIDE the wall band's own inner face at 91.0,
        /// showing a hand's width of the outer zone's color where the server
        /// says Middle.
        ///
        /// `RingSegments` is the same tolerance the arcs use, so the paint and
        /// the collision agree to within one `MeshSagMeters` by the same
        /// construction rather than by coincidence.
        void BuildTintDisc(Transform parent, string name, float radius, float centerY,
            Color color)
        {
            var disc = new GameObject(name);
            disc.layer = CosmeticsLayer;
            disc.transform.SetParent(parent, false);
            disc.transform.localPosition = new Vector3(0f, centerY, 0f);
            disc.AddComponent<MeshFilter>().sharedMesh = BuildDiscMesh(radius);
            disc.AddComponent<MeshRenderer>().sharedMaterial = _floor;
            Tint(disc, color);
        }

        /// A filled circle in the XZ plane, center first and the rim fanned
        /// around it at this radius's own segment count. Tracked in
        /// `_generatedMeshes` because a mesh built at runtime is not owned by
        /// the GameObject that renders it: destroying the object on `Rebuild`
        /// would leave the mesh behind, and a match restarted often enough
        /// would leak one per restart.
        Mesh BuildDiscMesh(float radius)
        {
            int segments = RingSegments(radius);
            var vertices = new Vector3[segments + 1];
            var triangles = new int[segments * 3];
            vertices[0] = Vector3.zero;
            for (int i = 0; i < segments; i++)
            {
                float angle = i * (2f * Mathf.PI / segments);
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * radius, 0f,
                    Mathf.Sin(angle) * radius);
                // Clockwise seen from +Y, which is the winding that faces the
                // camera: this project looks at the arena from above.
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = 1 + (i + 1) % segments;
                triangles[i * 3 + 2] = 1 + i;
            }
            var mesh = new Mesh { name = $"ZoneTint_{segments}" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _generatedMeshes.Add(mesh);
            return mesh;
        }

        /// The zone-wall arcs — the geometry this whole task exists for
        /// (spec §3.2/§3.11): the barriers `Geometry.SweepArena`/`Depenetrate`/
        /// `HasLineOfFire` have collided against since Т9 with nothing drawn
        /// over them, which is the same "the round stops in mid-air" defect
        /// `BuildWallSegments` removed for the interior stadiums at Т46.
        ///
        /// THE SHAPE IS THE SIMULATION'S, PIECE FOR PIECE. An arc barrier is
        /// the band [`ringR − halfW`, `ringR + halfW`] minus one angular
        /// cutout per door, each cutout's two corners filled back in by a jamb
        /// circle of radius `halfW` (`Geometry.OverlapsArc`). So it is drawn
        /// as tangent boxes over the SOLID spans between cutouts plus one
        /// cylinder per jamb — and the cutout's half-extent is not restated
        /// here but taken from `Geometry.DoorHalfCutout`, the very function
        /// the collision and `SimConfigBuilder.ValidateZoneWalls` both read
        /// (rule 2, and the only way the drawn doorway and the passable one
        /// stay the same doorway when the owner retunes `DoorFreeWidth` at
        /// milestone В1).
        ///
        /// The jambs reuse `BuildWallCap` outright (plan errata C-M6): a
        /// jamb and a stadium's end cap are the same circle of radius
        /// `halfW` extruded to the barrier height, and `Geometry` builds both
        /// out of the same `SegmentCircle`.
        ///
        /// Door centers are NORMALIZED and SORTED before the spans are cut.
        /// Config authors them in whatever winding reads well (Stage 3 ships
        /// 90/210/330° on the inner ring, 30/150/270° on the outer) and
        /// nothing promises they arrive in ascending order — an unsorted pass
        /// would cut spans that run backwards past a neighboring door and
        /// wall a doorway shut.
        void BuildZoneWalls()
        {
            if (_arena.ZoneWallCount <= 0) return;

            var zoneRoot = new GameObject("ZoneWalls");
            zoneRoot.transform.SetParent(transform, false);

            // The counts are clamped to what the arrays actually hold, and the
            // reason is this builder's own reading position rather than
            // distrust of the data: `SimConfigBuilder.ValidateZoneWalls`
            // proves `ZoneWallCount` against every parallel array before a
            // match runs, but `Awake` reads the ASSET, possibly before
            // anything has validated it, and a hand-edited YAML that promises
            // one wall more than it describes would throw halfway through this
            // loop — leaving the player in an arena with some of its barriers
            // drawn and the rest invisible, which is the exact failure this
            // task exists to remove. Drawing the walls that ARE described is
            // the honest answer; the bad field is diagnosed where it is
            // diagnosed.
            float height = BarrierHeight;
            int wallCount = Mathf.Min(_arena.ZoneWallCount,
                Mathf.Min(LengthOf(_arena.ZoneWallRadius), LengthOf(_arena.ZoneWallHalfWidth)));
            wallCount = Mathf.Min(wallCount,
                Mathf.Min(LengthOf(_arena.ZoneWallDoorStart), LengthOf(_arena.ZoneWallDoorCount)));
            int doorsAvailable =
                Mathf.Min(LengthOf(_arena.DoorCenterRad), LengthOf(_arena.DoorFreeWidth));

            for (int z = 0; z < wallCount; z++)
            {
                float ringR = _arena.ZoneWallRadius[z];
                float halfW = _arena.ZoneWallHalfWidth[z];
                int doorStart = Mathf.Clamp(_arena.ZoneWallDoorStart[z], 0, doorsAvailable);
                int doorCount = Mathf.Clamp(_arena.ZoneWallDoorCount[z], 0,
                    doorsAvailable - doorStart);
                string prefix = $"ZoneWall_{z:00}";
                int index = 0;

                if (doorCount <= 0)
                {
                    // A ring with no way through it is legal data (a sealed
                    // zone) and draws as a closed band.
                    BuildArcSpan(zoneRoot.transform, $"{prefix}_Segment", ref index, ringR,
                        halfW, height, 0f, 0f, 2f * Mathf.PI, _wall, true);
                    continue;
                }

                var centers = new float[doorCount];
                var halves = new float[doorCount];
                for (int d = 0; d < doorCount; d++)
                {
                    centers[d] = Mathf.Repeat(_arena.DoorCenterRad[doorStart + d], 2f * Mathf.PI);
                    halves[d] = Geometry.DoorHalfCutout(_arena.DoorFreeWidth[doorStart + d],
                        ringR, halfW);
                }
                SortDoorsByCenter(centers, halves);

                for (int d = 0; d < doorCount; d++)
                {
                    // From this door's counter-clockwise corner to the next
                    // door's clockwise one. ONLY THE LAST SPAN WRAPS PAST 0,
                    // and the distinction is load-bearing (fix-round, Ф7
                    // review B-M1): the doors are sorted, so a non-positive
                    // span anywhere BUT the last pair means two cutouts
                    // overlap, and wrapping that one by 2π would draw a solid
                    // band — with colliders — straight across every other
                    // doorway on the ring, walling off a passage the
                    // simulation still lets bodies through. `SimConfigBuilder.
                    // ValidateZoneWalls` rejects overlapping doors before a
                    // match runs, but this builder reads the unvalidated asset
                    // (see the clamps above), and its honest answer there is
                    // to draw nothing for that pair rather than something
                    // false.
                    int next = (d + 1) % doorCount;
                    float from = centers[d] + halves[d];
                    float span = centers[next] - halves[next] - from;
                    if (d == doorCount - 1) span += 2f * Mathf.PI;
                    if (span <= 0f) continue;
                    BuildArcSpan(zoneRoot.transform, $"{prefix}_Segment", ref index, ringR,
                        halfW, height, 0f, from, span, _wall, true);

                    BuildWallCap($"{prefix}_Jamb_{d * 2:00}", zoneRoot.transform,
                        JambCenter(ringR, centers[d] - halves[d]), halfW, height);
                    BuildWallCap($"{prefix}_Jamb_{d * 2 + 1:00}", zoneRoot.transform,
                        JambCenter(ringR, centers[d] + halves[d]), halfW, height);
                }
            }
        }

        /// Length of a config array that may legitimately be absent — the
        /// zone arrays ship EMPTY, never null (`SimConfig.cs`'s convention),
        /// but an asset authored before those fields existed deserializes them
        /// as null. `System.Array` rather than two overloads: the zone data is
        /// half `float[]` and half `int[]`, and the only thing being asked is
        /// how many entries there are.
        static int LengthOf(System.Array array) => array?.Length ?? 0;

        /// Insertion sort over one wall's doors, centers and half-extents kept
        /// in step. Three doors per wall at the shipped layout — a plain loop
        /// is the honest tool, and it runs once per arena build.
        static void SortDoorsByCenter(float[] centers, float[] halves)
        {
            for (int i = 1; i < centers.Length; i++)
            {
                float center = centers[i];
                float half = halves[i];
                int j = i - 1;
                while (j >= 0 && centers[j] > center)
                {
                    centers[j + 1] = centers[j];
                    halves[j + 1] = halves[j];
                    j--;
                }
                centers[j + 1] = center;
                halves[j + 1] = half;
            }
        }

        /// Where one jamb cylinder stands: on the ring, at a corner of a door's
        /// cutout — the world-space form of `Geometry`'s own private
        /// `JambCenter`, which the collision resolves those corners against.
        static Vector3 JambCenter(float ringR, float angle)
            => new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ringR;

        /// Extraction points, drawn as a painted ring of radius
        /// `ArenaConfig.ExtractRadius` around each configured position (spec
        /// §3.11, plan errata I13). Two materials rather than one tinted one:
        /// an early PORTAL and the core GATE are different promises to the
        /// player — the portals stand open from the start and shut when the
        /// Director wakes, the gate is sealed until he dies (ADR-001 A4) — and
        /// a material is what the owner can retune without a rebuild.
        ///
        /// STATIC HERE, AND DELIBERATELY: the open/closed STATE rides the
        /// snapshot's `Match` block, which no client decodes yet (that is
        /// Т32's debt, and Т33 is where the phase reaches the HUD). Drawing
        /// the ring now is what lets a player find an extraction point at
        /// milestone В1 at all; lighting it by state is the next task's job,
        /// not a second mechanism.
        ///
        /// `ExtractKind` is length-checked rather than trusted: `SimConfigBuilder`
        /// validates that it matches `ExtractPos` one to one, but this builder
        /// reads the asset straight from `Awake` and may run before anything
        /// has validated it — the same defensive posture `BuildWallSegments`
        /// takes with its degenerate-heading guard.
        void BuildExtractionRings()
        {
            if (_arena.ExtractPos == null || _arena.ExtractPos.Length == 0) return;

            var extractionRoot = new GameObject("Extraction");
            extractionRoot.transform.SetParent(transform, false);

            for (int i = 0; i < _arena.ExtractPos.Length; i++)
            {
                bool isGate = _arena.ExtractKind != null
                    && i < _arena.ExtractKind.Length
                    && _arena.ExtractKind[i] != 0;
                Vector2 pos = _arena.ExtractPos[i];

                var marker = new GameObject(isGate ? $"Gate_{i:00}" : $"Portal_{i:00}");
                marker.transform.SetParent(extractionRoot.transform, false);
                marker.transform.localPosition = new Vector3(pos.x, 0f, pos.y);

                int index = 0;
                BuildArcSpan(marker.transform, "Ring", ref index, _arena.ExtractRadius,
                    ExtractRingHalfWidth, ExtractRingHeight, FloorPaintLift * 3f,
                    0f, 2f * Mathf.PI, isGate ? _gateRing : _portalRing, false);
            }
        }

        void BuildObstacles()
        {
            if (_arena.Obstacles == null || _arena.Obstacles.Length == 0) return;

            var obstaclesRoot = new GameObject("Obstacles");
            obstaclesRoot.transform.SetParent(transform, false);

            // Stage 2 Task 46: was a flat 2 m (a unit-scaled primitive cylinder
            // is 2 units tall) regardless of what the simulation thought — the
            // height is BarrierHeight now, the same number the shot gate reads.
            float height = BarrierHeight;
            for (int i = 0; i < _arena.Obstacles.Length; i++)
            {
                ArenaConfig.Obstacle o = _arena.Obstacles[i];

                GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                obstacle.name = $"Obstacle_{i:00}";
                obstacle.layer = CosmeticsLayer;
                obstacle.transform.SetParent(obstaclesRoot.transform, false);
                // ArenaConfig.Obstacle.Pos is a plain UnityEngine.Vector2 (not
                // Unity.Mathematics.float2), so it can't go through
                // SimSpace.ToWorld directly — same (x, 0, y) mapping, spelled out.
                obstacle.transform.localPosition = new Vector3(o.Pos.x, height * 0.5f, o.Pos.y);
                obstacle.transform.localScale =
                    new Vector3(o.Radius * 2f, height * 0.5f, o.Radius * 2f);
                obstacle.GetComponent<MeshRenderer>().sharedMaterial = _obstacle;
                ReplaceWithConvexMeshCollider(obstacle);
            }
        }

        /// Stage 2 Task 46 (bd app-r8x/app-1ru): the interior walls
        /// `ArenaConfig` has carried since Stage 2 Task 16 had no visible
        /// geometry at all — six corridors that stopped bodies and rounds while
        /// the player saw an empty floor. Named apart from `BuildWall` above,
        /// which builds the outer RING.
        ///
        /// THE OBJECTS IT CREATES ARE NAMED "InteriorWall_*" UNDER AN
        /// "InteriorWalls" ROOT, not after this method (fix-round 1, Ф-4). The
        /// METHOD name is the spec's (§3.3) and stays, but "WallSegment" already
        /// belongs to the RING — `BuildWall` names its children that (Task 30:
        /// as many of them as `RingSegments` derives for the arena's radius,
        /// where a literal 48 used to stand) — so naming these objects the same
        /// way would put a hierarchy in front of the owner in which "Wall"
        /// holds the ring and "WallSegments" holds the interior walls, each
        /// reading as the other.
        ///
        /// A wall is a STADIUM in Simulation — the segment A→B inflated by
        /// HalfWidth, so its ends are ROUND (`Geometry.SegmentStadium`) — and it
        /// is drawn as one: a box spanning the two end centers, plus a cylinder
        /// of radius HalfWidth at each end. One box alone would leave HalfWidth
        /// meters of collision past each end with nothing drawn over it (0.8 m
        /// on four of the six shipped walls), which is precisely the "the round
        /// stops in mid-air" mismatch this task exists to remove; three
        /// primitives per wall — 18 objects against the hundred-odd the ring
        /// itself now derives — is the cheap side of that trade.
        void BuildWallSegments()
        {
            if (_arena.Walls == null || _arena.Walls.Length == 0) return;

            var wallsRoot = new GameObject("InteriorWalls");
            wallsRoot.transform.SetParent(transform, false);

            float height = BarrierHeight;
            for (int i = 0; i < _arena.Walls.Length; i++)
            {
                ArenaConfig.Wall wall = _arena.Walls[i];
                var a = new Vector3(wall.A.x, 0f, wall.A.y);
                var b = new Vector3(wall.B.x, 0f, wall.B.y);
                Vector3 axis = b - a;

                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body.name = $"InteriorWall_{i:00}_Body";
                body.layer = CosmeticsLayer;
                body.transform.SetParent(wallsRoot.transform, false);
                body.transform.localPosition = 0.5f * (a + b) + Vector3.up * (height * 0.5f);
                // LookRotation puts the cube's local Z along the wall's own axis,
                // so its local X falls out as the width — the same trick
                // BuildWall uses for the ring segments, no tangent math needed.
                // A zero-length wall is rejected by SimConfigBuilder.ValidateWalls
                // and so can never reach a running match, but this builder reads
                // the asset directly and runs from Awake, possibly before the
                // config is ever built: the substitute heading keeps a bad asset
                // from spraying "look rotation viewing vector is zero" instead of
                // failing where it is actually diagnosed.
                //
                // The threshold is this builder's OWN and deliberately coarser
                // than the validator's (fix-round 1, Ф-5 — the sentence above
                // used to point at that rule as if the number came from it).
                // ValidateWalls rejects |A−B|² under 1e-12 because that is where
                // Geometry's degenerate-axis branch changes what a wall MEANS to
                // Simulation; the question here is only whether LookRotation has
                // a usable forward, and mirroring 1e-12 would let a wall a
                // micrometer long through to a rotation with nothing to derive
                // it from. 1e-8 on the square is a tenth of a millimeter of
                // length: everything under it is invisible at any camera
                // distance, so substituting a heading there costs no picture
                // anyone could have seen.
                Vector3 heading = axis.sqrMagnitude > 1e-8f ? axis : Vector3.forward;
                body.transform.localRotation = Quaternion.LookRotation(heading, Vector3.up);
                body.transform.localScale =
                    new Vector3(wall.HalfWidth * 2f, height, axis.magnitude);
                body.GetComponent<MeshRenderer>().sharedMaterial = _wall;

                BuildWallCap($"InteriorWall_{i:00}_CapA", wallsRoot.transform, a,
                    wall.HalfWidth, height);
                BuildWallCap($"InteriorWall_{i:00}_CapB", wallsRoot.transform, b,
                    wall.HalfWidth, height);
            }
        }

        /// One rounded end of a wall stadium — the shape Simulation's own
        /// SegmentStadium resolves against an end cap (a circle of radius
        /// HalfWidth), extruded to the barrier height.
        void BuildWallCap(string name, Transform parent, Vector3 center, float halfWidth,
            float height)
        {
            GameObject cap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cap.name = name;
            cap.layer = CosmeticsLayer;
            cap.transform.SetParent(parent, false);
            // A primitive cylinder is 2 units tall, hence the half-scale on Y —
            // same convention BuildFloor and BuildObstacles already use.
            cap.transform.localPosition = center + Vector3.up * (height * 0.5f);
            cap.transform.localScale = new Vector3(halfWidth * 2f, height * 0.5f, halfWidth * 2f);
            cap.GetComponent<MeshRenderer>().sharedMaterial = _wall;
            ReplaceWithConvexMeshCollider(cap);
        }

        /// Swaps a primitive cylinder's default `CapsuleCollider` for a convex
        /// `MeshCollider` over the mesh actually being drawn — see the class doc
        /// for the measurement behind it (Stage 2 Task 46). Shared by the
        /// obstacles and the wall caps so the rule has one home rather than two.
        static void ReplaceWithConvexMeshCollider(GameObject primitive)
        {
            Collider degenerate = primitive.GetComponent<Collider>();
            degenerate.enabled = false; // Destroy is deferred to end-of-frame — kill it NOW
            Object.Destroy(degenerate);
            MeshCollider exact = primitive.AddComponent<MeshCollider>();
            exact.sharedMesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            exact.convex = true;
        }
    }
}
