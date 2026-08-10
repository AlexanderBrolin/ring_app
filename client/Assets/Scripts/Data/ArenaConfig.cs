using UnityEngine;

namespace Ring.Data
{
    /// Arena geometry and per-match entity caps.
    /// Field defaults mirror Ring.Simulation.Tests.TestConfigs.DefaultArena().
    [CreateAssetMenu(menuName = "Ring/Arena Config", fileName = "ArenaConfig")]
    public sealed class ArenaConfig : ScriptableObject
    {
        [System.Serializable]
        public struct Obstacle
        {
            public Vector2 Pos;
            public float Radius;
        }

        /// Stage 2 Task 11/16 (spec §3.3/§3.15): an interior wall — the segment
        /// A→B inflated by HalfWidth ("stadium"), mirroring ArenaSimConfig's
        /// WallA/WallB/WallHalfWidth triple. Attribute-free like the Obstacle
        /// struct right above it: both are authored as array elements, where a
        /// [Range] hint buys nothing the builder's own validation doesn't.
        [System.Serializable]
        public struct Wall
        {
            public Vector2 A;
            public Vector2 B;
            public float HalfWidth;
        }

        // Stage 2 Task 16 (spec §3.4): 35 -> 65. At 35 the arena is 70 m across
        // while a round covers ProjectileSpeed 52.5 x ProjectileLifetime 1.5 =
        // 78.75 m (numbers from the .asset — spec §0), i.e. every shot crosses
        // the whole map and no cover or visibility filter can ever matter.
        [Range(5f, 100f)] public float Radius = 65f;

        /// The first five circles are the Stage 1 layout, kept FIRST and in
        /// order: SweepArena walks them by index and that order is part of the
        /// state hash. Stage 2 Task 16 appends three more (spec §3.15, owner
        /// decision F4a) — the Stage 1 five all sit within r ~ 15 of the centre,
        /// so at Radius 65 the whole outer band would otherwise be bare.
        public Obstacle[] Obstacles =
        {
            new Obstacle { Pos = new Vector2(10f, 4f), Radius = 2.2f },
            new Obstacle { Pos = new Vector2(-8f, 9f), Radius = 1.8f },
            new Obstacle { Pos = new Vector2(2f, -12f), Radius = 2.5f },
            new Obstacle { Pos = new Vector2(-13f, -6f), Radius = 2.0f },
            new Obstacle { Pos = new Vector2(14f, -9f), Radius = 1.6f },
            new Obstacle { Pos = new Vector2(-40f, 8f), Radius = 3.0f },
            new Obstacle { Pos = new Vector2(30f, 22f), Radius = 2.8f },
            new Obstacle { Pos = new Vector2(-6f, -30f), Radius = 3.2f },
        };

        /// Stage 2 Task 16 starting layout (spec §3.15 — owner tuning target at
        /// milestone В1): two corridors (walls 1+2 and 3+4, axes 7.6 m apart
        /// minus 2 x 0.8 m of half-width = exactly 6.0 m of free passage, 20 m
        /// and 22 m long), one lone wall breaking line of sight between the P0
        /// and P1 spawn points, and one diagonal for oblique dash ricochets.
        public Wall[] Walls =
        {
            new Wall { A = new Vector2(-28f, 10f), B = new Vector2(-8f, 10f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(-28f, 17.6f), B = new Vector2(-8f, 17.6f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(12f, -6f), B = new Vector2(34f, -6f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(12f, -13.6f), B = new Vector2(34f, -13.6f), HalfWidth = 0.8f },
            new Wall { A = new Vector2(2f, 24f), B = new Vector2(2f, 44f), HalfWidth = 0.6f },
            new Wall { A = new Vector2(-34f, -20f), B = new Vector2(-16f, -34f), HalfWidth = 0.6f },
        };

        // Stage 2 Task 16 (spec §3.4, arithmetic): three players on a 65 m arena.
        [Range(1, 200)] public int MaxMobs = 96;
        [Range(1, 1000)] public int MaxProjectiles = 384;
        [Range(1, 1000)] public int MaxEventsPerFrame = 512;

        /// Minimum clear distance an obstacle must keep from the player spawn point
        /// (arena center), on top of its own radius and the hero radius. Used only by
        /// SimConfigBuilder.Validate — it does not exist on ArenaSimConfig, which stays
        /// a plain Simulation-side struct.
        [Range(0.5f, 5f)] public float SpawnClearance = 1f;

        // Stage 2 Task 4 (spec §3.2): per-match player cap and the multiplayer
        // spawn-ring radius fraction (ring radius = Radius * PlayerSpawnRingFrac).
        // Delivery into SimConfig via the bootstrap marker mechanism is
        // Stage 2 Task 9 — for now this is a plain SO field mirrored by
        // TestConfigs.DefaultArena().
        [Range(1, 3)] public int MaxPlayers = 3;
        [Range(0.1f, 0.95f)] public float PlayerSpawnRingFrac = 0.8f;

        /// Stage 2 Task 46 (bd app-r8x, owner decision 2026-08-11): the height
        /// every INTERIOR barrier is built and simulated at — the eight obstacle
        /// circles and the six walls above share this one number, in metres
        /// above the floor. A round whose whole remaining step sits above it
        /// flies over the barrier; below it, the barrier stops the shot exactly
        /// as it always did.
        ///
        /// 0 means NO MODELLED TOP — the barrier stops a shot at any height,
        /// which is what the arena did before this field existed. The value is
        /// not a synonym for "very low": nothing is drawn at zero height, so the
        /// greybox falls back to the ring wall's own height for a barrier
        /// without a top, because "no top" reads honestly as "up to the ceiling
        /// of the world" (GreyboxBuilder).
        ///
        /// THE OUTER RING WALL IS NOT INCLUDED, and not by omission: it holds
        /// the edge of the world, so a shot flying over it would leave the arena
        /// for good rather than land anywhere.
        ///
        /// 3 m is the height the ring wall is already drawn at, and it sits
        /// above every muzzle in the game (the hero's 1.0 standing / 0.45
        /// sliding, the Gunner's 0.95): a horizontal shot cannot clear a
        /// barrier, so this is honest geometry rather than a new mechanic. The
        /// [Range] ceiling of 20 is the arena's own aim-height scale (the hero's
        /// MaxAimHeight is 3.8) with room for a tall future structure, not a
        /// balance statement.
        [Range(0f, 20f)] public float BarrierTop = 3f; // sync-marker key — keep LAST

        // Task 28 (spec §3.9): hot-tweak signal — see HeroConfig.OnValidate's doc.
        // Arena topology (Radius/Obstacles) is a special case: SimulationRunner's
        // ApplyConfig path catches SimulationWorld's ArgumentException for this
        // one and falls back to a full Restart(Seed) instead of an in-place
        // migration (spec §3.9 forbids migrating topology) — this OnValidate
        // still fires the same generic signal, the RESTART decision is entirely
        // SimulationRunner's, not this asset's.
#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
