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
    ///
    /// bd `app-g21` GIVES THE DASH THE SAME TREATMENT, ON A GATE OF A
    /// DIFFERENT SHAPE. `PredictDash` below plays `_dashClip` in the frame
    /// this client's own dash starts, ahead of the `PlayerDashed` event that
    /// confirms it — which on a networked client is ~170 ms of interpolation
    /// buffer plus round trip behind a dash that lasts 90 ms, the owner's В1
    /// complaint. `SimulationRunner.DashingThisFrame` is the shared source of
    /// truth (`PersistentPropsDirector`'s floor mark is the other reader, and
    /// the two must not disagree about when a dash began), and it is a LEVEL
    /// over the predicted `PlayerState.DashTimer` rather than a guess at the
    /// next tick's outcome: that property's own doc carries the whole
    /// difference, including the one it makes to the latch — a dash's gate can
    /// rise AFTER its own event rather than before it, always so on the local
    /// backend, which is what `ImmediatePredictionLatch.NoteShownFromEvent`
    /// answers and why `HandleEvent` below takes out that credit wherever a
    /// dash of this client's own was actually heard from its event.
    /// A SECOND `ImmediatePredictionLatch` INSTANCE holds it — one rule, one
    /// instance per predicted THING. A shot's outstanding prediction and a
    /// dash's have nothing to say to each other, and sharing one counter
    /// between them would be two rules in one field: a dash would silence the
    /// next round's predicted shot and the reverse.
    ///
    /// NO `GameFeelConfig` FLAG GATES THE DASH PAIR, and the asymmetry with
    /// `ImmediateMuzzleFeedback` above is a decision rather than an oversight
    /// (bd `app-g21`). Reusing that field would put a cosmetic it does not name
    /// behind it — the owner may well want one without the other — and adding a
    /// field would mean editing the balance asset for a switch nobody has asked
    /// for. This task fixes a defect; it does not ship an option.
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
        // predictions), exactly as before — and, since bd app-g21, per predicted
        // THING as well: the dash below holds its own instance of the same class
        // rather than sharing this counter.
        readonly ImmediatePredictionLatch _latch = new ImmediatePredictionLatch();

        // bd app-g21: the dash's own latch — see the class doc for why a second
        // instance rather than a second user of the one above. It holds both
        // directions of this component's "one dash, one sound" rule: a
        // predicted sound waiting for its event, and the credit an event that
        // was heard first leaves for the edge still to come. `MinSfxInterval`
        // is no substitute for the second of those — it drops a repeat within
        // 0.03 s at the shipped balance, which a hitstop freeze outlasts, and
        // it is a feel knob the owner may turn down to zero, which is no place
        // to hang "is this dash heard twice" on.
        readonly ImmediatePredictionLatch _dashLatch = new ImmediatePredictionLatch();

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

        /// The two predicted sounds this component owns, each with its own gate,
        /// its own latch and — deliberately — its own configuration rule (class
        /// doc): the shot's sits behind `GameFeelConfig.ImmediateMuzzleFeedback`,
        /// the dash's behind nothing at all.
        ///
        /// TWO NAMED METHODS RATHER THAN ONE BODY, because the flag test used to
        /// be the first line of this method: appending the dash under it would
        /// have put a dash's sound behind a toggle whose name promises the
        /// muzzle, which is precisely the reuse bd `app-g21` refused.
        void Update()
        {
            PredictShot();
            PredictDash();
        }

        /// Task 28: per-frame prediction of the SHOT — see the class doc above
        /// and `MuzzleFlashView.PredictBurst`'s doc for the shared heuristic's
        /// full rationale. (Stage 2 Task 45c: this used to cite a
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
        void PredictShot()
        {
            if (!_gameFeel.ImmediateMuzzleFeedback) return;
            // Stage 2 Task 45b: one predicted sound per SHOT — the rising edge
            // of the shared gate AND nothing already waiting for its event (see
            // `ImmediatePredictionLatch`). Evaluated every frame this method
            // reaches, because the edge is a function of the previous frame.
            if (!_latch.ShouldPredict(_runner.WouldFireThisFrame, Time.unscaledTime,
                    _runner.FirePredictionMinGapSeconds)) return;

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

        /// bd `app-g21`: the dash sound, in the frame this client's own dash
        /// starts rather than the frame its event finishes crossing the wire.
        /// `PersistentPropsDirector.PredictDashGlow` is the same rule for the
        /// floor mark and reads the same gate — the two are fixed together on
        /// purpose, since a mark on time beside a sound 170 ms late is the
        /// original defect with an extra seam in it.
        ///
        /// THE LEVEL IS THE GATE AND THE LATCH IS THE EDGE, so `ShouldPredict`
        /// is called on every frame this method reaches, sound or no sound —
        /// the edge is a function of the previous frame's answer. That gate's
        /// own doc explains why a dash needs no `WouldDashThisTick` to guess at
        /// (the simulation states the fact in `DashTimer`), why a
        /// reconciliation's second rising edge for one dash is harmless (the
        /// latch's second fact), and why an event can beat the edge to the same
        /// dash (the latch's third).
        ///
        /// IN SOLO THE SOUND STILL COMES FROM THE EVENT, and that is the point
        /// of the third fact rather than a shortcoming: the local backend hands
        /// `HandleEvent` this dash first, the voice starts there, and the credit
        /// taken out there refuses the edge that follows — this frame's, or the
        /// one a hitstop freeze delayed it into. The one solo case that DOES
        /// reach `PlayClip` below is a dash whose event was dropped by the SFX
        /// gates, which leaves no credit on purpose (`HandleEvent`'s own
        /// comment): the attempt is then a sound arriving on time rather than a
        /// duplicate — and it meets the same `MinSfxInterval`/`VoicesPerSfx`
        /// state that just dropped the event, in the same frame, so it will
        /// ordinarily be refused again.
        ///
        /// THE POSITION IS THE ONE THE MARK USES — `RenderCurr.Player.Pos`, the
        /// point `PersistentPropsDirector.PredictDashGlow` puts its mark on, so
        /// a dash is heard where it is seen; that method's own doc has how
        /// closely it tracks the position the authoritative event carries, which
        /// is "the same quantity one source earlier", not "the same number". Not
        /// the muzzle: a dash comes off the body, and the `RenderMuzzleSimPos`
        /// above answers a question about where a ROUND leaves from.
        ///
        /// `Arm` ONLY ON `true`, the G-4 rule of `PredictShot` above. The gates
        /// inside `PlayClip` can refuse this attempt for a reason that has
        /// nothing to do with this dash — another player's dash sounding within
        /// `MinSfxInterval`, or this kind already at the `VoicesPerSfx` cap —
        /// and an armed-but-silent latch would then swallow this dash's own
        /// event on arrival, losing the sound outright instead of playing it
        /// late.
        void PredictDash()
        {
            if (!_dashLatch.ShouldPredict(_runner.DashingThisFrame, Time.unscaledTime)) return;

            if (PlayClip(_dashClip, SimEventKind.PlayerDashed, _runner.RenderCurr.Player.Pos))
                _dashLatch.Arm(Time.unscaledTime, _runner.ImmediatePredictionWindowSeconds);
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
                // Already played this shot's sound ahead of time (PredictShot
                // above) — consume the prediction instead of a duplicate
                // PlayOneShot (Task 28).
                return;
            }

            // bd app-g21, the dash's half of the same rule, on its own latch and
            // its own seat test. `SimEvent.PlayerIndex` is the ACTOR for this
            // kind (that struct's own doc: the five "own-action" kinds), so this
            // asks exactly "was this MY dash" — a stranger's dash must never
            // consume my prediction, or their sound would be swallowed and mine
            // would play twice (the app-ai2 shape, one kind over).
            if (e.Kind == SimEventKind.PlayerDashed
                && e.PlayerIndex == _runner.RenderCurr.LocalPlayerIndex
                && _dashLatch.TryConsume(Time.unscaledTime))
            {
                // Already heard this dash ahead of its event (PredictDash above).
                return;
            }

            // Task 22 (spec brief): zone-biased pitch — Head reads higher, Legs
            // lower, Body/None (and every non-blow kind, whose Zone defaults to
            // HitZone.None) stay at the plain PitchRange jitter.
            bool played = PlayClip(ClipFor(e.Kind), e.Kind, e.Pos, ZonePitchOffset(e.Zone));

            // bd app-g21: this client's own dash has now been HEARD from its
            // event, so the rising edge still to come for that same dash — this
            // frame's, or the one a hitstop freeze delayed it into — has to be
            // refused rather than double it (`ImmediatePredictionLatch.
            // NoteShownFromEvent`). Conditioned on `played` and not on the call:
            // a dash whose event was dropped by `MinSfxInterval`/`VoicesPerSfx`
            // has not been heard at all, so a predicted attempt later in the
            // same dash is a sound arriving late rather than a duplicate, and
            // must not be refused (the G-4 rule `Arm` follows one method up).
            if (played && e.Kind == SimEventKind.PlayerDashed
                && e.PlayerIndex == _runner.RenderCurr.LocalPlayerIndex)
                _dashLatch.NoteShownFromEvent(
                    Time.unscaledTime, _runner.ImmediatePredictionWindowSeconds);
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

        /// Shared by the event-driven `HandleEvent` and the two predicted paths
        /// (Task 28, and bd `app-g21` for the dash):
        /// `MinSfxInterval`/`VoicesPerSfx` drop-only gates, then the round-robin
        /// voice pick. Returns whether a voice actually started playing — a
        /// predicted path only arms its suppression latch on `true`, and
        /// `HandleEvent` only takes out the reverse credit on `true`
        /// (`PredictShot`/`PredictDash` above carry the reason; the doc that
        /// used to be cited here was `Update`'s, before this task split its body
        /// into those two). `pitchOffset` (Task 22)
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
