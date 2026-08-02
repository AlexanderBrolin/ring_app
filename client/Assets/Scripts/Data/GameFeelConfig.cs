using UnityEngine;

namespace Ring.Data
{
    /// Presentation-only game-feel numbers (hitstop, screen shake, VFX/SFX pooling).
    /// Never consumed by SimConfigBuilder / Ring.Simulation — purely client feel,
    /// hot-tweakable in PlayMode.
    [CreateAssetMenu(menuName = "Ring/Game Feel Config", fileName = "GameFeelConfig")]
    public sealed class GameFeelConfig : ScriptableObject
    {
        public enum HitstopScopeMode { TargetOnly, FullFrame }

        [Range(0f, 0.2f)] public float HitstopSeconds = 0.04f;
        public HitstopScopeMode HitstopScope = HitstopScopeMode.FullFrame;
        [Range(0f, 1f)] public float MaxHitstopRatio = 0.35f;
        [Range(0f, 0.5f)] public float HitstopCatchUpSeconds = 0.05f;
        [Range(0f, 0.5f)] public float FlashDuration = 0.08f;
        [Range(0f, 1f)] public float TraumaHit = 0.2f;
        [Range(0f, 1f)] public float TraumaDeath = 0.35f;
        [Range(0f, 1f)] public float TraumaPlayerHit = 0.45f;
        [Range(0f, 5f)] public float TraumaDecayPerSec = 1.2f;
        [Range(0f, 2f)] public float ShakeAmplitude = 0.35f;
        [Range(1f, 60f)] public float ShakeFrequency = 22f;
        [Range(0f, 1f)] public float PitchRange = 0.12f;
        [Range(0f, 2f)] public float TracerFadeSeconds = 0.4f;
        [Range(0f, 5f)] public float CasingPhysicsSeconds = 1.5f;
        [Range(1, 4096)] public int MaxCasings = 1024;
        [Range(1, 4096)] public int MaxDecals = 512;
        [Range(1, 512)] public int MaxCorpses = 64;
        [Range(1, 32)] public int VoicesPerSfx = 6;
        [Range(0f, 1f)] public float MinSfxInterval = 0.03f;
        public bool ImmediateMuzzleFeedback = true;
        public bool ExtrapolateLocalPlayer = false;
    }
}
