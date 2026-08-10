using Ring.Data;
using UnityEngine;

namespace Ring.Presentation
{
    /// Builds a greybox arena purely from `ArenaConfig` numbers: a floor disc, a
    /// ring wall made of tangent-aligned cube segments, one stadium per
    /// configured interior wall, and one cylinder per configured obstacle — all
    /// parented under this transform. Presentation-only:
    /// this never touches Simulation state, it just visualizes the same
    /// `ArenaConfig`/`ArenaSimConfig` numbers Simulation collides against
    /// (`Geometry.cs`), so the visible surface follows sim collision by
    /// construction (same radius, same center at world/sim origin per `SimSpace`)
    /// — down to two approximations worth naming rather than leaving to be
    /// discovered (fix-round 1, Ф-5). A primitive cylinder is a polygonal prism
    /// inscribed in its circle, not a circle: its drawn face lies inside the
    /// simulated one by R·(1 − cos(π/sides)), centimetres on this arena's radii.
    /// And the ring wall is drawn as 0.5 m thick boxes CENTRED on the radius,
    /// while Simulation stops a round when its centre reaches `Radius` minus the
    /// round's own radius — so at the rim a round buries up to ~0.17 m into the
    /// drawn wall before it ends. Both are pre-Task-46 and neither is a defect
    /// — they are what "the picture matches the collision" costs, said out loud
    /// rather than left under an absolute.
    ///
    /// PhysX colliders on the generated primitives are intentionally kept (they
    /// come free with `CreatePrimitive`) and moved to the `Cosmetics` layer
    /// (`TagManager.asset` user layer 8). They have TWO consumers, not one:
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
    /// centred at y = 1, reaching 4.2 m, i.e. 2.2 m over a 2 m crown. At this
    /// task's 3 m the same collapse puts the sphere's centre at y = 1.5, its top
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
        const int WallSegments = 48;
        const float WallSegmentOverlap = 1.05f;
        const float WallHeight = 3f;
        const float WallY = 1.5f;
        const float WallThickness = 0.5f;

        const float FloorScaleY = 0.5f;
        const float FloorY = -0.5f;

        /// User layer 8 — named "Cosmetics" in `ProjectSettings/TagManager.asset`.
        /// Public (Task 27): `PersistentPropsDirector`/`StageOneSceneBootstrap`
        /// reuse this exact constant for casings/decals/corpses instead of
        /// redeclaring the literal `8` a second/third time (reuse > duplication).
        public const int CosmeticsLayer = 8;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] ArenaConfig _arena;
        [SerializeField] Material _floor;
        [SerializeField] Material _wall;
        [SerializeField] Material _obstacle;

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

            BuildContent();
        }

        void BuildContent()
        {
            BuildFloor();
            BuildWall();
            BuildWallSegments();
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

        void BuildFloor()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            floor.name = "Floor";
            floor.layer = CosmeticsLayer;
            floor.transform.SetParent(transform, false);
            floor.transform.localPosition = new Vector3(0f, FloorY, 0f);
            floor.transform.localScale = new Vector3(_arena.Radius * 2f, FloorScaleY, _arena.Radius * 2f);
            floor.GetComponent<MeshRenderer>().sharedMaterial = _floor;

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

        void BuildWall()
        {
            var wallRoot = new GameObject("Wall");
            wallRoot.transform.SetParent(transform, false);

            float circumference = 2f * Mathf.PI * _arena.Radius;
            float segmentWidth = circumference / WallSegments * WallSegmentOverlap;

            for (int i = 0; i < WallSegments; i++)
            {
                float angle = i * (2f * Mathf.PI / WallSegments);
                // Radial direction at this angle also doubles as the segment's
                // local forward (Z) axis via LookRotation below, which makes the
                // local X axis (the segment's width) fall out tangent to the ring
                // automatically — no separate tangent-vector math needed.
                var radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                GameObject seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                seg.name = $"WallSegment_{i:00}";
                seg.layer = CosmeticsLayer;
                seg.transform.SetParent(wallRoot.transform, false);
                seg.transform.localPosition = radial * _arena.Radius + Vector3.up * WallY;
                seg.transform.localRotation = Quaternion.LookRotation(radial, Vector3.up);
                seg.transform.localScale = new Vector3(segmentWidth, WallHeight, WallThickness);
                seg.GetComponent<MeshRenderer>().sharedMaterial = _wall;
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
        /// belongs to the RING — `BuildWall` names its 48 children that, and the
        /// `WallSegments` constant above counts them — so naming these objects
        /// the same way would put a hierarchy in front of the owner in which
        /// "Wall" holds the ring and "WallSegments" holds the interior walls,
        /// each reading as the other.
        ///
        /// A wall is a STADIUM in Simulation — the segment A→B inflated by
        /// HalfWidth, so its ends are ROUND (`Geometry.SegmentStadium`) — and it
        /// is drawn as one: a box spanning the two end centres, plus a cylinder
        /// of radius HalfWidth at each end. One box alone would leave HalfWidth
        /// metres of collision past each end with nothing drawn over it (0.8 m
        /// on four of the six shipped walls), which is precisely the "the round
        /// stops in mid-air" mismatch this task exists to remove; three
        /// primitives per wall — 18 objects against the ring's own 48 — is the
        /// cheap side of that trade.
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
                // micrometre long through to a rotation with nothing to derive
                // it from. 1e-8 on the square is a tenth of a millimetre of
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
