using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;
// Type alias, not `using Unity.Mathematics;` — a full namespace import would make
// `Random` ambiguous between `UnityEngine.Random` (used below, `PlayClip`'s pitch
// jitter) and `Unity.Mathematics.Random`, the same trap PersistentPropsDirector's
// class doc (Task 27) already documents and routes around.
using float2 = Unity.Mathematics.float2;

namespace Ring.Presentation
{
    /// SFX layer (П-2): a round-robin pool of `AudioSource` voices, one clip
    /// per event kind, pitch randomized by `GameFeelConfig.PitchRange`. Driven
    /// exclusively by `SimEventRouter`'s `HandleEvent` fan-out (П-1) for the
    /// authoritative per-event playback — never subscribes to `TicksFlushed`
    /// itself.
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
    ///
    /// Task 28 (spec §3.11, ImmediateMuzzleFeedback) adds a SEPARATE per-frame
    /// `Update` path alongside the event-driven one: plays `_shotClip` in the
    /// frame the player presses Fire, ahead of the authoritative tick's
    /// `ProjectileFired` event, which can land up to one 30Hz tick later (spec
    /// §3.2). See `MuzzleFlashView`'s class doc — same
    /// `SimulationRunner.WouldFireThisFrame` heuristic, and — since Stage 2 Task
    /// 45b — literally the same suppression mechanism (`ImmediatePredictionLatch`),
    /// held as an independent instance here so the two never interfere with each
    /// other's bookkeeping. `PlayClip` below is shared by both paths so
    /// `MinSfxInterval`/`VoicesPerSfx` gate the predicted attempt exactly like
    /// a real one — and its `bool` return means the latch is only armed when a
    /// voice actually played, never when the predicted attempt itself got
    /// gated out (armed-but-silent would wrongly consume the real event too,
    /// losing the shot's sound entirely instead of gating it once).
    public sealed class AudioDirector : MonoBehaviour
    {
        const int VoiceCount = 16;
        // В3 fix-wave 2 (item 3c): sentinel `_voiceKind` value for a physical
        // voice slot borrowed by `PlayHeadHoverTick` — see that method's own doc.
        const SimEventKind NotAnEventKind = (SimEventKind)255;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] AudioClip _shotClip;
        [SerializeField] AudioClip _hitClip;
        [SerializeField] AudioClip _mobDeathClip;
        [SerializeField] AudioClip _dashClip;
        [SerializeField] AudioClip _playerHitClip;
        [SerializeField] AudioClip _staminaDeniedClip; // Task 22
        [SerializeField] AudioClip _ricochetClip; // Task 22
        [SerializeField] AudioClip _headHoverTickClip; // В3 fix-wave 2, item 3c

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

        // Task 28 (ImmediateMuzzleFeedback): holds a predicted shot-sound play
        // until either the matching real ProjectileFired event consumes it
        // (HandleEvent) or SimulationRunner.ImmediatePredictionWindowSeconds
        // elapses unconfirmed — see the class doc above and MuzzleFlashView's
        // for the full rationale. Stage 2 Task 45b (bd app-id9) replaced the
        // `bool`/`float` pair here and its twin in MuzzleFlashView with ONE
        // shared class: the two had to agree, and two copies of a rule are two
        // rules. Still an independent INSTANCE per component (each owns its own
        // predictions), exactly as before.
        readonly ImmediatePredictionLatch _latch = new ImmediatePredictionLatch();

        // В3 fix-wave 2 (item 3c): last-play timestamp for the head-hover tick,
        // parallel to `_lastPlayTime` above but NOT indexed by `SimEventKind` —
        // see `PlayHeadHoverTick`'s own doc for why.
        float _lastHeadHoverTickTime = float.NegativeInfinity;

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

        /// В3 fix-wave 2 (app-n6g item 3c): a subtle one-shot the instant the
        /// aim-proxy hover zone ENTERS Head — edge-triggered by the caller
        /// (`CrosshairView` tracks its own previous-frame zone and calls this
        /// exactly once per entry, never every frame the cursor stays over Head),
        /// reinforcing the marker pulse (`HeadHoverPulseHz`/`Amp`) and mob glow
        /// boost (`ViewRegistry.SyncMobs`) above with a matching sound. Rate-limited
        /// by the SAME `GameFeelConfig.MinSfxInterval` anti-spam knob `PlayClip`'s
        /// per-kind gate already reads, via its own dedicated timestamp rather than
        /// `PlayClip`'s `_lastPlayTime[(int)kind]` bucket: a hover transition is a
        /// Presentation-only signal (`AimProvider`'s raycast, never emitted by the
        /// sim) with no `SimEventKind` of its own, and inventing a fake one just to
        /// share that array would touch Simulation code for a purely cosmetic
        /// reason — out of scope this fix-wave (Simulation changes are sanctioned
        /// ONLY by a proven bug, item 4's investigation). Shares the same physical
        /// round-robin voice pool as every other clip (`_voices`/`_nextVoice`); no
        /// `VoicesPerSfx` cap here (nothing to bucket concurrent voices by), the
        /// same trade-off `MinSfxInterval` alone already accepts for this one
        /// low-frequency cue. `NotAnEventKind` marks the stolen physical slot in
        /// `_voiceKind` so `CountActiveVoices(realKind)` can never mistake this
        /// clip for that real kind's own voice while both happen to be sounding
        /// at once (the array only ever holds genuine `SimEventKind` values
        /// otherwise, all < 14 — `byte 255` can never collide with one).
        public void PlayHeadHoverTick(float2 simPos)
        {
            if (_headHoverTickClip == null) return;
            float now = Time.unscaledTime;
            if (now - _lastHeadHoverTickTime < _gameFeel.MinSfxInterval) return;

            AudioSource source = _voices[_nextVoice];
            _voiceKind[_nextVoice] = NotAnEventKind;
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            source.transform.position = SimSpace.ToWorld(simPos);
            source.pitch = 1f + Random.Range(-_gameFeel.PitchRange, _gameFeel.PitchRange);
            source.PlayOneShot(_headHoverTickClip);
            _lastHeadHoverTickTime = now;
        }

        /// Task 28: per-frame prediction — see the class doc above and
        /// `MuzzleFlashView.PredictBurst`'s doc for the shared heuristic's full
        /// rationale. (Stage 2 Task 45c: this used to cite a
        /// `MuzzleFlashView.Update`; that class has had no `Update` since Task
        /// 45b's fix-round 1 moved its whole per-frame path into `LateUpdate`,
        /// behind `ViewRegistry`'s doll placement.) Fix-round (review #1,
        /// Medium): positioned at the MUZZLE, same as `MuzzleFlashView`'s fix —
        /// the authoritative `ProjectileHit`/`PlayClip` position for a real shot
        /// is `WeaponSystem`'s spawn point (`p.Pos + dir * cfg.MuzzleOffset`),
        /// not the hero's center.
        ///
        /// Stage 2 Task 45c: that spawn point is `SimulationRunner.
        /// RenderMuzzleSimPos` now — this method held the only restatement of it
        /// in Presentation, and `AimProvider` needed the same point to work out
        /// where a round comes down (`app-bej`). One formula, asked with this
        /// path's own aim: the last complete tick's `PlayerState.AimPoint`,
        /// exactly the value the hand-written version read.
        void Update()
        {
            if (!_gameFeel.ImmediateMuzzleFeedback) return;
            // Stage 2 Task 45b: one predicted sound per SHOT — the rising edge
            // of the shared gate AND nothing already waiting for its event (see
            // `ImmediatePredictionLatch`). Evaluated every frame this method
            // reaches, because the edge is a function of the previous frame.
            if (!_latch.ShouldPredict(_runner.WouldFireThisFrame, Time.unscaledTime)) return;

            float2 muzzlePos = _runner.RenderMuzzleSimPos(_runner.RenderCurr.Player.AimPoint);

            if (PlayClip(_shotClip, SimEventKind.ProjectileFired, muzzlePos))
                _latch.Arm(Time.unscaledTime, _runner.ImmediatePredictionWindowSeconds);
            // PlayClip returning false (MinSfxInterval/VoicesPerSfx gated the
            // predicted attempt out) leaves the latch unarmed — the real event
            // still gets its own ordinary chance at HandleEvent below instead of
            // being wrongly suppressed for a sound that never actually played.
            // Fix-round 1 (G-4): the EDGE is spent either way, and with a single
            // outstanding prediction that costs nothing — an unarmed latch has
            // no record for the arriving event to consume, so the shot is heard
            // once, from the event. It could only have lost a sound while the
            // latch held a QUEUE of predictions, where one shot's event could
            // consume a record another shot had left behind.
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out). Task 27 (Приложение П-2): two drop-only gates ahead
        /// of the existing round-robin voice pick — `MinSfxInterval` first
        /// (cheapest check, and independent of how many voices happen to be
        /// free right now), then the per-kind `VoicesPerSfx` cap; both now live
        /// in `PlayClip` below (Task 28), shared with the predicted path above.
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind == SimEventKind.ProjectileFired && e.Owner != ProjectileOwner.Player)
            {
                // F-3 fix-round: a mob's shot has no clip of its own yet — until one
                // exists, skip audio entirely rather than borrowing the player's
                // `_shotClip` (which also ate into the player's own
                // MinSfxInterval/VoicesPerSfx budget under the old owner-blind
                // event). Returning here BEFORE the predicted-latch check below is
                // also what keeps a mob's shot from ever wrongly consuming the
                // player's own predicted-shot latch (the audio side of bd app-ai2 —
                // MuzzleFlashView.HandleEvent gets the matching fix).
                return;
            }
            // Stage 2 Task 45b: only the LOCAL player's own shot may consume a
            // prediction. `Owner == Player` was the whole test while it meant
            // "mine" — on a networked client every other player's round decodes
            // to that same owner (`ClientEventDecoder`), so a stranger's gunfire
            // would swallow my predicted shot's confirmation and then lose its
            // own sound on the way out: the app-ai2 defect one participant
            // further out. The matching fix is in `MuzzleFlashView.HandleEvent`.
            if (e.Kind == SimEventKind.ProjectileFired
                && e.PlayerIndex == _runner.RenderCurr.LocalPlayerIndex
                && _latch.TryConsume(Time.unscaledTime))
            {
                // Already played this shot's sound ahead of time (Update above)
                // — consume the prediction instead of a duplicate PlayOneShot
                // (Task 28).
                return;
            }

            // Task 22 (spec brief): zone-biased pitch — Head reads higher, Legs
            // lower, Body/None (and every non-blow kind, whose Zone defaults to
            // HitZone.None) stay at the plain PitchRange jitter.
            PlayClip(ClipFor(e.Kind), e.Kind, e.Pos, ZonePitchOffset(e.Zone));
        }

        /// Task 22: additive pitch bias layered on top of `PlayClip`'s ordinary
        /// `PitchRange` jitter — never applied standalone, always summed with
        /// the random jitter so the zone bias reads as "this hit's pitch, plus
        /// its usual small randomization" rather than replacing it.
        float ZonePitchOffset(HitZone zone)
        {
            switch (zone)
            {
                case HitZone.Head: return _gameFeel.ZoneHitPitchOffset;
                case HitZone.Legs: return -_gameFeel.ZoneHitPitchOffset;
                default: return 0f;
            }
        }

        /// Shared by the event-driven `HandleEvent` and the predicted `Update`
        /// path (Task 28): `MinSfxInterval`/`VoicesPerSfx` drop-only gates, then
        /// the round-robin voice pick. Returns whether a voice actually started
        /// playing — the predicted path only arms its suppression latch on
        /// `true` (see `Update`'s doc above for why). `pitchOffset` (Task 22)
        /// defaults to 0 for every call site that has no zone to bias by (the
        /// predicted-shot path above never passes one — a predicted shot has no
        /// SimEvent, hence no Zone, to read yet).
        bool PlayClip(AudioClip clip, SimEventKind kind, float2 simPos, float pitchOffset = 0f)
        {
            if (clip == null) return false;

            float now = Time.unscaledTime;
            if (now - _lastPlayTime[(int)kind] < _gameFeel.MinSfxInterval) return false;
            if (CountActiveVoices(kind) >= _gameFeel.VoicesPerSfx) return false;

            AudioSource source = _voices[_nextVoice];
            _voiceKind[_nextVoice] = kind;
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            source.transform.position = SimSpace.ToWorld(simPos);
            source.pitch = 1f + Random.Range(-_gameFeel.PitchRange, _gameFeel.PitchRange) + pitchOffset;
            source.PlayOneShot(clip);
            _lastPlayTime[(int)kind] = now;
            return true;
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

        /// Stage 2 Task 45c: `ProjectileHitPlayer` shares `_hitClip` with
        /// `ProjectileHit` rather than getting a clip of its own. It IS the same
        /// event acoustically — a round ending on a body — and the two are told
        /// apart by what else fires around them: a landed blow adds the victim's
        /// own `_playerHitClip` on `PlayerDamaged`, and a blow refused by dash
        /// i-frames does not, which is exactly the difference worth hearing.
        /// Sharing the clip does NOT share the anti-spam budget: `PlayClip` keys
        /// `MinSfxInterval`/`VoicesPerSfx` on the KIND, so a PvP hit can never
        /// silence a PvE one or the reverse.
        AudioClip ClipFor(SimEventKind kind)
        {
            switch (kind)
            {
                case SimEventKind.ProjectileFired: return _shotClip;
                case SimEventKind.ProjectileHit: return _hitClip;
                case SimEventKind.ProjectileHitPlayer: return _hitClip;
                case SimEventKind.MobDied: return _mobDeathClip;
                case SimEventKind.PlayerDashed: return _dashClip;
                case SimEventKind.PlayerDamaged: return _playerHitClip;
                case SimEventKind.StaminaDenied: return _staminaDeniedClip; // Task 22
                case SimEventKind.DashRicocheted: return _ricochetClip; // Task 22
                default: return null;
            }
        }
    }
}
