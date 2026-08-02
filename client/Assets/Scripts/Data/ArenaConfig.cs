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

        [Range(5f, 100f)] public float Radius = 35f;

        public Obstacle[] Obstacles =
        {
            new Obstacle { Pos = new Vector2(10f, 4f), Radius = 2.2f },
            new Obstacle { Pos = new Vector2(-8f, 9f), Radius = 1.8f },
            new Obstacle { Pos = new Vector2(2f, -12f), Radius = 2.5f },
            new Obstacle { Pos = new Vector2(-13f, -6f), Radius = 2.0f },
            new Obstacle { Pos = new Vector2(14f, -9f), Radius = 1.6f },
        };

        [Range(1, 200)] public int MaxMobs = 64;
        [Range(1, 1000)] public int MaxProjectiles = 256;
        [Range(1, 1000)] public int MaxEventsPerFrame = 256;

        /// Minimum clear distance an obstacle must keep from the player spawn point
        /// (arena center), on top of its own radius and the hero radius. Used only by
        /// SimConfigBuilder.Validate — it does not exist on ArenaSimConfig, which stays
        /// a plain Simulation-side struct.
        [Range(0.5f, 5f)] public float SpawnClearance = 1f;
    }
}
