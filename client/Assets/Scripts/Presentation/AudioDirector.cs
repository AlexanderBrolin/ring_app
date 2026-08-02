using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// SFX layer (П-2): a round-robin pool of `AudioSource` voices, one clip
    /// per event kind, pitch randomized by `GameFeelConfig.PitchRange`. Driven
    /// exclusively by `SimEventRouter`'s `HandleEvent` fan-out (П-1) — never
    /// subscribes to `TicksFlushed` itself.
    /// Task 27 (Приложение П-2) extends the Task 17 placeholder with the two
    /// numbers this class always had reserved fields for but never consumed:
    /// `GameFeelConfig.VoicesPerSfx` caps how many voices currently playing
    /// THE SAME event kind are allowed at once — a new trigger for a kind
    /// already at that cap is dropped outright (the physical voice pool below
    /// stays round-robin/shared across every kind, same as before; only the
    /// per-kind ACCOUNTING is new) — and `GameFeelConfig.MinSfxInterval`
    /// drops a new trigger of a kind that fired less than that many seconds
    /// ago, regardless of the voice cap (anti-phasing: many identical hits
    /// landing the same tick/flush no longer buzz as near-simultaneous
    /// duplicate one-shots). Both checks only ever SKIP playback — they never
    /// steal or cut a voice that's already sounding. `VoiceCount` itself grows
    /// 8 → 16 (T27 brief Interfaces) so the per-kind cap of 6 across up to 5
    /// distinct clip kinds has realistic headroom instead of starving on a
    /// physical pool smaller than a single kind's own cap.
    public sealed class AudioDirector : MonoBehaviour
    {
        const int VoiceCount = 16;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] AudioClip _shotClip;
        [SerializeField] AudioClip _hitClip;
        [SerializeField] AudioClip _mobDeathClip;
        [SerializeField] AudioClip _dashClip;
        [SerializeField] AudioClip _playerHitClip;

        AudioSource[] _voices;
        // Which SimEventKind each physical voice is currently sounding for
        // (only meaningful together with `_voices[i].isPlaying` — a finished
        // voice's stale kind here is harmless, `CountActiveVoices` always
        // gates on `isPlaying` too). Indexed in lockstep with `_voices`.
        SimEventKind[] _voiceKind;
        // Last accepted-trigger timestamp per SimEventKind (Time.unscaledTime),
        // indexed by (int)SimEventKind — MinSfxInterval anti-phasing gate.
        float[] _lastPlayTime;
        int _nextVoice;

        void Awake()
        {
            _voices = new AudioSource[VoiceCount];
            _voiceKind = new SimEventKind[VoiceCount];
            for (int i = 0; i < VoiceCount; i++)
            {
                var go = new GameObject($"Voice_{i:00}");
                go.transform.SetParent(transform, false);
                AudioSource source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                _voices[i] = source;
            }
            _lastPlayTime = new float[System.Enum.GetValues(typeof(SimEventKind)).Length];
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
        /// (П-1 fan-out). Task 27 (Приложение П-2): two drop-only gates ahead
        /// of the existing round-robin voice pick — `MinSfxInterval` first
        /// (cheapest check, and independent of how many voices happen to be
        /// free right now), then the per-kind `VoicesPerSfx` cap.
        public void HandleEvent(in SimEvent e)
        {
            AudioClip clip = ClipFor(e.Kind);
            if (clip == null) return;

            float now = Time.unscaledTime;
            if (now - _lastPlayTime[(int)e.Kind] < _gameFeel.MinSfxInterval) return;
            if (CountActiveVoices(e.Kind) >= _gameFeel.VoicesPerSfx) return;

            AudioSource source = _voices[_nextVoice];
            _voiceKind[_nextVoice] = e.Kind;
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            source.transform.position = SimSpace.ToWorld(e.Pos);
            source.pitch = 1f + Random.Range(-_gameFeel.PitchRange, _gameFeel.PitchRange);
            source.PlayOneShot(clip);
            _lastPlayTime[(int)e.Kind] = now;
        }

        /// How many physical voices are RIGHT NOW playing a one-shot for
        /// `kind` — gates `GameFeelConfig.VoicesPerSfx`. `_voiceKind[i]` alone
        /// is not enough (it's only ever overwritten on a new `PlayOneShot`,
        /// never cleared when playback ends) — always paired with
        /// `_voices[i].isPlaying`, which is what actually goes false the
        /// instant a one-shot finishes.
        int CountActiveVoices(SimEventKind kind)
        {
            int count = 0;
            for (int i = 0; i < _voices.Length; i++)
            {
                if (_voiceKind[i] == kind && _voices[i].isPlaying) count++;
            }
            return count;
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
