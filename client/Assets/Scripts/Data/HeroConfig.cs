using UnityEngine;

namespace Ring.Data
{
    /// Balance numbers for the player hero (movement, dash, HP).
    /// Field defaults mirror Ring.Simulation.Tests.TestConfigs.Default().Hero.
    [CreateAssetMenu(menuName = "Ring/Hero Config", fileName = "HeroConfig")]
    public sealed class HeroConfig : ScriptableObject
    {
        [Range(0.1f, 30f)] public float MaxSpeed = 7f;
        [Range(1f, 200f)] public float Accel = 40f;
        [Range(1f, 200f)] public float Friction = 30f;
        [Range(0.1f, 2f)] public float Radius = 0.45f;
        [Range(1f, 1000f)] public float MaxHp = 100f;
        [Range(1f, 60f)] public float DashSpeed = 22f;
        [Range(0.05f, 1f)] public float DashDuration = 0.15f;
        [Range(0.1f, 10f)] public float DashCooldown = 1.2f;
        [Range(0f, 1f)] public float DashIframes = 0.2f;
        [Range(0f, 0.5f)] public float DashBufferWindow = 0.15f;
    }
}
