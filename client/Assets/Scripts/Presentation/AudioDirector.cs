using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Minimal placeholder SFX layer (П-2, Task 17 milestone): a small round-robin
    /// pool of `AudioSource` voices, one clip per event kind, pitch randomized by
    /// `GameFeelConfig.PitchRange`. Driven exclusively by `SimEventRouter`'s
    /// `HandleEvent` fan-out (П-1) — never subscribes to `TicksFlushed` itself.
    /// Voice-count limits, `MinSfxInterval` throttling and `StopAll` are Phase 8
    /// (T27), intentionally not implemented here.
    public sealed class AudioDirector : MonoBehaviour
    {
        const int VoiceCount = 8;

        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] AudioClip _shotClip;
        [SerializeField] AudioClip _hitClip;
        [SerializeField] AudioClip _mobDeathClip;
        [SerializeField] AudioClip _dashClip;
        [SerializeField] AudioClip _playerHitClip;

        AudioSource[] _voices;
        int _nextVoice;

        void Awake()
        {
            _voices = new AudioSource[VoiceCount];
            for (int i = 0; i < VoiceCount; i++)
            {
                var go = new GameObject($"Voice_{i:00}");
                go.transform.SetParent(transform, false);
                AudioSource source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                _voices[i] = source;
            }
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out).
        public void HandleEvent(in SimEvent e)
        {
            AudioClip clip = ClipFor(e.Kind);
            if (clip == null) return;

            AudioSource source = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            source.transform.position = SimSpace.ToWorld(e.Pos);
            source.pitch = 1f + Random.Range(-_gameFeel.PitchRange, _gameFeel.PitchRange);
            source.PlayOneShot(clip);
        }

        AudioClip ClipFor(SimEventKind kind)
        {
            switch (kind)
            {
                case SimEventKind.ProjectileFired: return _shotClip;
                case SimEventKind.ProjectileHit: return _hitClip;
                case SimEventKind.MobDied: return _mobDeathClip;
                case SimEventKind.PlayerDashed: return _dashClip;
                case SimEventKind.PlayerDamaged: return _playerHitClip;
                default: return null;
            }
        }
    }
}
