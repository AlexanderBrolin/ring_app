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
    /// Idempotent by child count: `Build()` runs once from `Awake` and does
    /// nothing if this transform already has children (e.g. a second `Awake` from
    /// a domain reload without a scene reopen). It never rebuilds afterwards —
    /// arena topology is off-limits to hot-tweak while the match is running;
    /// `Object.DestroyImmediate` isn't available outside the Editor either, so
    /// there is no in-place "clear and rebuild" path here. Task 28 owns rebuilding
    /// on match restart.
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
        const int CosmeticsLayer = 8;

        [SerializeField] ArenaConfig _arena;
        [SerializeField] Material _floor;
        [SerializeField] Material _wall;
        [SerializeField] Material _obstacle;

        void Awake()
        {
            Build();
        }

        public void Build()
        {
            if (transform.childCount > 0) return;

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
