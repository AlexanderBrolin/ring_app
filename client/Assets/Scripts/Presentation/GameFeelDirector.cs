using System.Collections.Generic;
using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Ring.Presentation
{
    /// Hitstop + hit-flash + damage-vignette (spec §3.11, Task 25 Interfaces,
    /// Приложение П-7). First slot in `SimEventRouter`'s fan-out (П-1) — this
    /// class never subscribes to `TicksFlushed` itself, same rule as every other
    /// class in Presentation.
    ///
    /// Architecture (П-7): `SimulationRunner.RenderPrev`/`RenderCurr`/
    /// `RenderAlpha` are the SOLE point `ViewRegistry`/`PlayerView`/`CameraRig`
    /// read for interpolation — none of them ever check `HitstopActive` or
    /// branch on hitstop at all. This class is the only thing that ever calls
    /// `SimulationRunner.FreezeRender`/`UnfreezeRender`, so "does hitstop affect
    /// rendering" lives in exactly one place instead of three separate
    /// `if (feel.HitstopActive)` checks. The simulation itself is never paused
    /// for hitstop (spec §3.2/§3.11: `SimulationWorld.Tick` keeps advancing every
    /// frame regardless) — only the VISUAL pair driving interpolation gets
    /// pinned.
    ///
    /// Two independent freeze mechanisms, chosen by `GameFeelConfig.
    /// HitstopScope`:
    ///  - `FullFrame`: the whole scene's interpolation freezes via
    ///    `SimulationRunner.FreezeRender()`.
    ///  - `TargetOnly`: only the specific `MobView` that was hit stops updating
    ///    its own transform (`MobView.FreezePosition`/`IsPositionFrozen`,
    ///    checked by `ViewRegistry.SyncMobs` before writing `transform.
    ///    position`) — every other mob, every projectile, the player and the
    ///    camera keep interpolating normally off the live pair the whole time.
    ///
    /// Ending a `FullFrame` freeze doesn't snap straight back to the live pair:
    /// `SimulationRunner.UnfreezeRender` eases `RenderAlpha` from 0→1 over
    /// `GameFeelConfig.HitstopCatchUpSeconds` instead, because several ticks can
    /// have landed "behind the scenes" while the frame was pinned (the sim never
    /// stopped) — an instant snap would read as every mob/projectile popping
    /// forward in a single frame. `ForceEndHitstop` (this class's own
    /// `PlayerDied` handler below, and `DeathOverlayController.Show`'s explicit
    /// hook into it) skips that ease — a hard cut back to live is the right call
    /// the instant a death screen is about to cover the whole frame anyway.
    ///
    /// Budget (spec Interfaces): a trailing 1-second window tracks each
    /// ACCEPTED trigger's own `(timestamp, seconds)` pair; a new
    /// `ProjectileHit` is only granted hitstop if the window's summed seconds
    /// plus this trigger's own duration stay under `MaxHitstopRatio` (a
    /// fraction of that 1s window — e.g. 0.35 == 350ms of hitstop per real
    /// second) — otherwise the freeze/target-freeze step is skipped outright,
    /// so hold-fire through a wave doesn't turn into a slideshow. The
    /// hit-flash and trauma bump still fire on every hit regardless of
    /// budget — only the freeze itself is rate-limited. Task 22 (spec Г6)
    /// fix-round: each entry stores its OWN accepted duration rather than the
    /// window's accepted-trigger COUNT times the CURRENT `HitstopSeconds`
    /// value — that shortcut was only correct while every hit in a window
    /// shared one flat duration; once `HandleProjectileHit` started passing a
    /// zone-scaled duration (`HeadHitstopScale`), a window mixing head and
    /// body hits under the old approximation mis-priced every entry as
    /// whichever duration happened to be current at CHECK time, not the
    /// duration each entry actually spent.
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
    /// run on `Time.unscaledDeltaTime`/`Time.unscaledTime` — never gated by
    /// hitstop or paused by anything — so a shake already in flight when a
    /// hitstop freeze or the death overlay hits keeps reading live instead of
    /// stalling with the rest of the frame.
    ///
    /// `GameFeelConfig.ExtrapolateLocalPlayer` is deliberately left unconsumed
    /// here: no spec text ties it to a concrete mechanic, and inventing one
    /// risks contradicting the one hard rule above (`PlayerView` reads ONLY
    /// `RenderAlpha`, no per-class special casing) — left documented as still
    /// reserved rather than guessed at.
    public sealed class GameFeelDirector : MonoBehaviour
    {
        const float BudgetWindowSeconds = 1f;
        const int BudgetHistoryCapacity = 64; // generous — bounded by realistic fire rate

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] ViewRegistry _viewRegistry;
        [SerializeField] Image _vignette;

        // Task 22 (spec Г6) fix-round: (timestamp, seconds) pairs — see the
        // Budget doc paragraph above — instead of a bare timestamp queue.
        readonly Queue<(float timestamp, float seconds)> _hitstopBudget =
            new Queue<(float timestamp, float seconds)>(BudgetHistoryCapacity);

        float _hitstopTimer;
        // The scope captured at TriggerHitstop time, not re-read from `_gameFeel`
        // at end time — a hot-tweak of `HitstopScope` mid-hitstop must not strand
        // `SimulationRunner` frozen forever (EndHitstop would otherwise decide
        // "this wasn't FullFrame" and skip the matching `UnfreezeRender` call).
        GameFeelConfig.HitstopScopeMode _activeScope;
        MobView _hitstopTargetView;
        float _vignetteAlpha;

        public bool HitstopActive { get; private set; }

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
            if (_hitstopTimer > 0f)
            {
                _hitstopTimer -= Time.unscaledDeltaTime;
                if (_hitstopTimer <= 0f) EndHitstop(instant: false);
            }

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
                case SimEventKind.MobDied:
                    AddTrauma(_gameFeel.TraumaDeath);
                    break;
                case SimEventKind.PlayerDamaged:
                    AddTrauma(_gameFeel.TraumaPlayerHit);
                    // Vignette pulse: jumps up to the hit's severity (never below
                    // whatever's already fading out from a prior hit) and decays
                    // linearly in `UpdateVignette`, reusing `TraumaDecayPerSec` —
                    // no separate `GameFeelConfig` field for this, see class/Task
                    // 25 report: the vignette piggybacks the same trauma numbers
                    // rather than growing the SO for a second, near-identical pulse
                    // curve.
                    _vignetteAlpha = Mathf.Max(_vignetteAlpha, _gameFeel.TraumaPlayerHit);
                    break;
                case SimEventKind.PlayerDied:
                    ForceEndHitstop();
                    break;
            }
        }

        /// `DeathOverlayController.Show` calls this directly (Task 24's brief
        /// referenced this seam ahead of time), on top of this class's own
        /// `PlayerDied` handler above — by the time `DeathOverlayController` sees
        /// the same `PlayerDied` event (П-1 fan-out: `GameFeelDirector` runs
        /// first), hitstop is already forced off, so this second call is
        /// ordinarily a no-op; kept as an explicit, defensive hook rather than
        /// relying solely on fan-out order. Idempotent either way.
        public void ForceEndHitstop()
        {
            if (!HitstopActive) return;
            EndHitstop(instant: true);
        }

        void HandleProjectileHit(in SimEvent e)
        {
            bool hasView = _viewRegistry.TryGetMobView(e.EntityId, out MobView targetView);

            // Hit-flash reads on every hit regardless of budget — only the
            // freeze itself is rate-limited (class doc, Budget).
            if (hasView) targetView.Flash(_gameFeel.FlashDuration);

            // Task 22 (spec brief): a headshot lands harder — HitstopSeconds scaled
            // by HeadHitstopScale BEFORE the budget check/trigger below, so a
            // head hit both costs more of the 1s hitstop budget and freezes
            // longer, same "duration IS the everything" contract TriggerHitstop
            // itself already follows for the flat case.
            float hitstopSeconds = e.Zone == HitZone.Head
                ? _gameFeel.HitstopSeconds * _gameFeel.HeadHitstopScale
                : _gameFeel.HitstopSeconds;

            if (TryConsumeHitstopBudget(hitstopSeconds))
            {
                TriggerHitstop(hitstopSeconds);
                if (hasView && _activeScope == GameFeelConfig.HitstopScopeMode.TargetOnly)
                {
                    targetView.FreezePosition(hitstopSeconds);
                    _hitstopTargetView = targetView;
                }
            }

            AddTrauma(_gameFeel.TraumaHit);
        }

        /// Resets (never sums, spec Interfaces: "таймер переустанавливается, не
        /// суммируется") the hitstop timer and — for `FullFrame` scope — pins the
        /// runner's render pair. Budget-gated by the caller (`HandleProjectileHit`),
        /// not here, so this method always does exactly what it says once called.
        void TriggerHitstop(float seconds)
        {
            _hitstopTimer = seconds;
            _activeScope = _gameFeel.HitstopScope;
            HitstopActive = true;
            if (_activeScope == GameFeelConfig.HitstopScopeMode.FullFrame)
                _runner.FreezeRender();
        }

        void EndHitstop(bool instant)
        {
            _hitstopTimer = 0f;
            HitstopActive = false;
            if (_activeScope == GameFeelConfig.HitstopScopeMode.FullFrame)
                _runner.UnfreezeRender(instant ? 0f : _gameFeel.HitstopCatchUpSeconds);
            if (_hitstopTargetView != null)
            {
                _hitstopTargetView.ClearPositionFreeze();
                _hitstopTargetView = null;
            }
        }

        bool TryConsumeHitstopBudget(float seconds)
        {
            float now = Time.unscaledTime;
            float windowStart = now - BudgetWindowSeconds;
            while (_hitstopBudget.Count > 0 && _hitstopBudget.Peek().timestamp < windowStart)
                _hitstopBudget.Dequeue();

            // Task 22 (spec Г6) fix-round: sum each entry's OWN accepted
            // duration — a plain `foreach` over the concrete `Queue<T>` field
            // (not the `IEnumerable<T>` interface) binds to `Queue<T>`'s own
            // struct enumerator, so this allocates nothing despite the loop
            // (same "zero allocation once warmed up" constraint every other
            // per-event Presentation path already follows).
            float used = 0f;
            foreach (var entry in _hitstopBudget) used += entry.seconds;
            if (used + seconds > _gameFeel.MaxHitstopRatio) return false;

            _hitstopBudget.Enqueue((now, seconds));
            return true;
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
        /// a frozen frame, a stuck target-view freeze, decaying trauma or a
        /// fading vignette bleeding into the fresh run.
        void HandleWorldRestarted()
        {
            ForceEndHitstop();
            _hitstopBudget.Clear();
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
