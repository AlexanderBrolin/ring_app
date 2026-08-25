using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Ring.Presentation
{
    /// Hit-flash + damage-vignette + trauma-driven camera shake (spec §3.11,
    /// Task 25 Interfaces). First slot in `SimEventRouter`'s fan-out (П-1) —
    /// this class never subscribes to `TicksFlushed` itself, same rule as
    /// every other class in Presentation.
    ///
    /// Task Т10 (app-88jb) removed this class's other half whole: the render
    /// pin it used to trigger on every hit (`SimulationRunner`'s deep-copied
    /// frozen pair, `MobView`'s own per-target position hold) never had a
    /// home in `Simulation` — it was always a Presentation-only device, and
    /// `SimulationWorld.Tick` never paused for it (spec §3.2/§3.11). Per-hit
    /// readability now rests on the push/lean/fall response a landed hit
    /// drives on the victim itself (Т4–Т7 of this same epic) instead of
    /// pinning the world around it. What THIS class still carries is the
    /// rest of ADR-001 §10's checklist: the hit-flash on the struck view
    /// (`Flash`, below — mob or player, every hit, unconditionally), the
    /// trauma-driven camera shake (`AddTrauma`/`ShakeOffset`) and the damage
    /// vignette (`HandlePlayerDamaged`). Sparks, hit sound and the
    /// crosshair's zone-tinted highlight (`AimZoneColors.Resolve`,
    /// `CrosshairView`, `AimRayView`) live elsewhere and are untouched by
    /// this class either way.
    ///
    /// `AddTrauma`: max-pulse + linear decay, the standard "trauma" shape
    /// (Squirrel Eiserloh's screen-shake talk) — every event that should drive
    /// camera shake feeds it, clamped to [0, 1]. Task 26 wires the consumer
    /// side: `ShakeOffset` below turns the decaying `Trauma` scalar into a
    /// per-frame Perlin-noise offset (`trauma²` easing — small trauma barely
    /// shakes, trauma near 1 shakes hard, spec Interfaces), which `CameraRig`
    /// reads directly off this component (a bootstrap-wired reference, not an
    /// event or `SimulationRunner`) and adds on top of its already-damped
    /// position every `LateUpdate`. Both the trauma decay and the shake noise
    /// run on `Time.unscaledDeltaTime`/`Time.unscaledTime` — never paused by
    /// anything — so a shake already in flight when the death overlay hits
    /// keeps reading live instead of stalling with the rest of the frame.
    public sealed class GameFeelDirector : MonoBehaviour
    {
        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] ViewRegistry _viewRegistry;
        [SerializeField] Image _vignette;

        float _vignetteAlpha;

        public float Trauma { get; private set; }

        /// Task 26 (spec Interfaces): `ShakeAmplitude * trauma² *
        /// (perlin(t·Freq) − 0.5, perlin(t·Freq + 17) − 0.5)`, recomputed every
        /// `Update` from the CURRENT (already-decayed-this-frame) `Trauma`. The
        /// two Perlin samples are offset by 17 (an arbitrary decorrelation
        /// constant, not itself a tunable) purely so the X/Z components don't
        /// read as a single diagonal wobble.
        ///
        /// Plane choice (documented per this task's brief — "реши осознанно"):
        /// world XZ, the same ground plane `SimSpace`/`CameraRig`'s focus point
        /// already live in — NOT the camera's local screen-right/up basis.
        /// `CameraRig` never yaws or rolls (only a fixed `PitchDeg` around
        /// world X), so world +X already reads as screen-horizontal, and a
        /// world-Z nudge reads as a blend of screen-vertical and depth under
        /// that fixed pitch — a believable "shake" for this camera without
        /// `CameraRig` having to hand this class its basis vectors.
        /// `CameraRig` adds this on top of `transform.position` AFTER
        /// `SmoothDamp` (its own class doc / this task's brief) — never fed
        /// back into the damp state — so shake reads as instantaneous jitter
        /// instead of being smoothed away like normal camera follow.
        public Vector3 ShakeOffset { get; private set; }

        // WorldRestarted is not a tick event (П-1 only restricts TicksFlushed to
        // its sole SimEventRouter subscriber) — direct subscription, same shape
        // as every other class in this file's sibling scripts.
        void OnEnable() => _runner.WorldRestarted += HandleWorldRestarted;

        void OnDisable() => _runner.WorldRestarted -= HandleWorldRestarted;

        void Update()
        {
            if (Trauma > 0f)
                Trauma = Mathf.Max(0f, Trauma - _gameFeel.TraumaDecayPerSec * Time.unscaledDeltaTime);

            UpdateShake();
            UpdateVignette();
        }

        /// Called by `SimEventRouter` for every event in this tick-flush's
        /// buffer (П-1 fan-out) — first slot, ahead of `AudioDirector`/
        /// `ViewRegistry`/`DeathOverlayController` (Приложение П-1).
        public void HandleEvent(in SimEvent e)
        {
            switch (e.Kind)
            {
                case SimEventKind.ProjectileHit:
                    HandleProjectileHit(in e);
                    break;
                case SimEventKind.PlayerDamaged:
                    HandlePlayerDamaged(in e);
                    break;
                case SimEventKind.MobDied:
                    AddTrauma(_gameFeel.TraumaDeath);
                    break;
            }
        }

        void HandleProjectileHit(in SimEvent e)
        {
            bool hasView = _viewRegistry.TryGetMobView(e.EntityId, out MobView targetView);

            // Hit-flash fires on every hit, unconditionally.
            if (hasView) targetView.Flash(_gameFeel.FlashDuration);

            AddTrauma(_gameFeel.TraumaHit);
        }

        /// A BLOW LANDED ON A PLAYER — the whole victim half of ADR-001 §10's
        /// per-hit checklist (Stage 2 Task 45c fix-round 1, G-1). Until that
        /// round this handler was two lines that shook the camera and lit the
        /// vignette for ANY victim, and the flash hung off
        /// `ProjectileHitPlayer` instead.
        ///
        /// WHY THIS KIND AND NOT `ProjectileHitPlayer`, WHICH IS THE ONE THAT
        /// NAMES THE HIT. Because the victim is not on the wire there.
        /// `ClientEventDecoder`'s `HitPlayer` branch fills exactly `Kind`,
        /// `SecondaryEntityId` and `Zone`; `EntityId` keeps the zero it was
        /// initialized with, and that class's own doc spells out the trap: "it
        /// is not 'no victim', it is 'seat 0', and the victim is simply not on
        /// the wire". Addressing anything by that field would give every hit in
        /// the match to whoever sits in slot 0 and nothing at all to everybody
        /// else — invisible in solo, wrong for every networked match. On THIS
        /// kind the decoder writes `e.EntityId = e.PlayerIndex = p.PlayerIndex`,
        /// the victim, which is also what `SimulationWorld.DamagePlayer` emits
        /// locally: one field, one meaning, both backends.
        ///
        /// WHAT IT COSTS, SAID PLAINLY: a round refused by dash i-frames emits
        /// no `PlayerDamaged` at all, so it draws no flash and no shake. That is
        /// the more honest of the two — the cue now means damage was taken, not
        /// that something flew close. The round's own end still reports itself
        /// through `ProjectileHitPlayer`: spark, sound and tracer retirement,
        /// none of which needs to know who was hit.
        ///
        /// WHAT IT GAINS: a Chaser's fist arrives here too (`MobAiSystem` calls
        /// the same `DamagePlayer`), so melee finally gets the checklist it
        /// never had while this hung off a projectile kind.
        ///
        /// FLASH FOR ANY VICTIM, SHAKE/VIGNETTE ONLY WHEN IT IS ME. A stranger
        /// being shot across the arena is something I watch; shaking my camera
        /// and reddening my screen for it would make somebody else's fight
        /// jerk my aim. That filter is new in fix-round 1 and closes a defect
        /// older than this task: the two lines this method replaced ran for
        /// every `PlayerDamaged` that reached this client, whoever it named.
        /// Which of them reach me is the server's decision, not this class's —
        /// `SnapshotAssembler` delivers this kind on the Visible channel, keyed
        /// on the VICTIM's own visibility (`EventRelevance.VisibleSubjectId`).
        void HandlePlayerDamaged(in SimEvent e)
        {
            if (_viewRegistry.TryGetPlayerView(e.EntityId, out PlayerView victim))
                victim.Flash(_gameFeel.FlashDuration);

            if (e.EntityId != _runner.RenderCurr.LocalPlayerIndex) return;

            // ONE trauma call for a blow taken, not two. `AddTrauma` is a `Max`,
            // so the 0.2 `TraumaHit` this method also used to add through the
            // `ProjectileHitPlayer` path was invisible next to this 0.45 on every
            // landed hit and visible only on a refused one — the exact inverse of
            // what either cue meant (fix-round 1, G-3).
            AddTrauma(_gameFeel.TraumaPlayerHit);
            // Vignette pulse: jumps up to the hit's severity (never below
            // whatever's already fading out from a prior hit) and decays
            // linearly in `UpdateVignette`, reusing `TraumaDecayPerSec` — no
            // separate `GameFeelConfig` field for this, see class/Task 25
            // report: the vignette piggybacks the same trauma numbers rather
            // than growing the SO for a second, near-identical pulse curve.
            _vignetteAlpha = Mathf.Max(_vignetteAlpha, _gameFeel.TraumaPlayerHit);
        }

        void AddTrauma(float amount) => Trauma = Mathf.Clamp01(Mathf.Max(Trauma, amount));

        /// See `ShakeOffset`'s own doc for the formula/plane-choice rationale.
        /// Recomputed unconditionally every frame (even at `Trauma == 0`, where
        /// it collapses to `Vector3.zero` since the amplitude term is zero) —
        /// cheap enough (two `Mathf.PerlinNoise` calls) that branching around
        /// it would only save work in the common "no shake right now" case at
        /// the cost of a second code path to keep in sync with the formula.
        void UpdateShake()
        {
            float trauma = Trauma;
            float t = Time.unscaledTime * _gameFeel.ShakeFrequency;
            float nx = Mathf.PerlinNoise(t, 0f) - 0.5f;
            float nz = Mathf.PerlinNoise(t + 17f, 0f) - 0.5f;
            float magnitude = _gameFeel.ShakeAmplitude * trauma * trauma;
            ShakeOffset = new Vector3(nx, 0f, nz) * magnitude;
        }

        void UpdateVignette()
        {
            if (_vignette == null) return;

            if (_vignetteAlpha > 0f)
                _vignetteAlpha = Mathf.Max(0f, _vignetteAlpha - _gameFeel.TraumaDecayPerSec * Time.unscaledDeltaTime);

            Color c = _vignette.color;
            c.a = _vignetteAlpha;
            _vignette.color = c;
        }

        /// A match restart (Task 24 shape, direct `WorldRestarted` subscription
        /// — not a tick event, П-1 only restricts `TicksFlushed`) must not leave
        /// decaying trauma or a fading vignette bleeding into the fresh run.
        void HandleWorldRestarted()
        {
            Trauma = 0f;
            ShakeOffset = Vector3.zero;
            _vignetteAlpha = 0f;
            if (_vignette != null)
            {
                Color c = _vignette.color;
                c.a = 0f;
                _vignette.color = c;
            }
        }
    }
}
