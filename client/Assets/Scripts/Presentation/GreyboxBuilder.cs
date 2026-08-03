using Ring.Data;
using UnityEngine;

namespace Ring.Presentation
{
    /// Builds a greybox arena purely from `ArenaConfig` numbers: a floor disc, a
    /// ring wall made of tangent-aligned cube segments, and one cylinder per
    /// configured obstacle — all parented under this transform. Presentation-only:
    /// this never touches Simulation state, it just visualizes the same
    /// `ArenaConfig`/`ArenaSimConfig` numbers Simulation collides against
    /// (`Geometry.cs`), so the visible surface matches sim collision by
    /// construction (same radius, same center at world/sim origin per `SimSpace`).
    ///
    /// PhysX colliders on the generated primitives are intentionally kept (they
    /// come free with `CreatePrimitive`) and moved to the `Cosmetics` layer
    /// (`TagManager.asset` user layer 8) — they exist only so cosmetic props
    /// (Task 27 shell casings) bounce off the arena; Simulation never queries
    /// PhysX, it has its own analytic collision (`Geometry.SweepArena`/
    /// `Depenetrate`) against the same `ArenaConfig` numbers.
    ///
    /// `BuildFloor` swaps its `CreatePrimitive`-default `CapsuleCollider` for a
    /// `BoxCollider` (app-4qc, Б1 milestone find): under the floor's
    /// non-uniform (2R, 0.5, 2R) scale, PhysX degenerates a capsule (height 1 &lt;
    /// 2×radius R) into a plain SPHERE of radius R — a dome whose apex sits
    /// ~R-0.5 above the arena center — so anything cosmetic spawned near
    /// ground level started deep INSIDE it and got depenetrated tens of
    /// meters up and sideways. The obstacle cylinders keep their default
    /// (also-degenerate, also-spherical) capsule colliders on purpose: for a
    /// short cylindrical obstacle that "sphere" is visually indistinguishable
    /// from a true cylinder collider for casing bounce purposes, so it isn't
    /// worth the extra box-collider bookkeeping — a deliberate scope decision,
    /// not an oversight.
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

        const float ObstacleScaleY = 1f;
        const float ObstacleY = 1f;

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
            BuildObstacles();
        }

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
                obstacle.transform.localPosition = new Vector3(o.Pos.x, ObstacleY, o.Pos.y);
                obstacle.transform.localScale = new Vector3(o.Radius * 2f, ObstacleScaleY, o.Radius * 2f);
                obstacle.GetComponent<MeshRenderer>().sharedMaterial = _obstacle;
            }
        }
    }
}
