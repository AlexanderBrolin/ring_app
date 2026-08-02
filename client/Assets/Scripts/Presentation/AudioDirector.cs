using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Minimal placeholder SFX layer (П-2, Task 17 milestone): a small round-robin
    /// pool of `AudioSource` voices, one clip per event kind, pitch randomized by
    /// `GameFeelConfig.PitchRange`. Driven exclusively by `SimEventRouter`'s
    /// `HandleEvent` fan-out (П-1) — never subscribes to `TicksFlushed` itself.
    /// Voice-count limits and `MinSfxInterval` throttling are Phase 8 (T27); a
    /// minimal `StopAll` (Task 24 spec Interfaces, restart cleanup — full voice
    /// management is still T27) is implemented below.
    public sealed class AudioDirector : MonoBehaviour
    {
        const int VoiceCount = 8;

        [SerializeField] SimulationRunner _runner;
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

        // WorldRestarted is not a tick event (П-1 only restricts TicksFlushed to
        // its sole SimEventRouter subscriber) — direct subscription, same shape
        // as the deleted PracticeTargets' pattern. This object's own Awake above
        // always runs before its own OnEnable, so `_voices` is never null here.
        void OnEnable() => _runner.WorldRestarted += StopAll;

        void OnDisable() => _runner.WorldRestarted -= StopAll;

        /// Cuts every currently-playing voice short (Task 24 spec Interfaces):
        /// a match restart shouldn't leave the previous run's gunfire/death
        /// stingers still ringing out over the new one. `Stop()` on an idle
        /// `AudioSource` is a harmless no-op, so this is safe to call whether or
        /// not anything was actually playing.
        public void StopAll()
        {
            for (int i = 0; i < _voices.Length; i++) _voices[i].Stop();
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
