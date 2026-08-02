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
    /// Budget (spec Interfaces): a trailing 1-second window tracks every
    /// ACCEPTED trigger's timestamp; a new `ProjectileHit` is only granted
    /// hitstop if the window's already-spent seconds plus this trigger's
    /// `HitstopSeconds` stay under `MaxHitstopRatio` (a fraction of that 1s
    /// window — e.g. 0.35 == 350ms of hitstop per real second) — otherwise the
    /// freeze/target-freeze step is skipped outright, so hold-fire through a
    /// wave doesn't turn into a slideshow. The hit-flash and trauma bump still
    /// fire on every hit regardless of budget — only the freeze itself is
    /// rate-limited. Per-trigger cost is approximated as the window's accepted-
    /// trigger COUNT times the CURRENT `HitstopSeconds` value rather than
    /// storing each trigger's own duration — simpler, and correct for the
    /// overwhelming common case (the value doesn't hot-tweak mid-window); a
    /// tweak landing exactly inside an active window very slightly over/under-
    /// counts for that one window only, never a hard bug.
    ///
    /// `AddTrauma`: implemented now (max-pulse + linear decay, the standard
    /// "trauma" shape — Squirrel Eiserloh's screen-shake talk) so every event
    /// that should eventually drive camera shake already feeds it, but nothing
    /// CONSUMES `Trauma` yet — `CameraRig.ExternalOffset` stays untouched here.
    /// Wiring the actual noise-driven shake is Task 26's job; this class only
    /// keeps the accumulator honest in the meantime, the same "reserved hook,
    /// not wired yet" shape `CameraRig.ExternalOffset` itself already had before
    /// this task existed.
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

        readonly Queue<float> _hitstopBudget = new Queue<float>(BudgetHistoryCapacity);

        float _hitstopTimer;
        // The scope captured at TriggerHitstop time, not re-read from `_gameFeel`
        // at end time — a hot-tweak of `HitstopScope` mid-hitstop must not strand
        // `SimulationRunner` frozen forever (EndHitstop would otherwise decide
        // "this wasn't FullFrame" and skip the matching `UnfreezeRender` call).
        GameFeelConfig.HitstopScopeMode _activeScope;
        MobView _hitstopTargetView;
        float _vignetteAlpha;

        public bool HitstopActive { get; private set; }

        /// Reserved for Task 26 (camera shake) — see class doc. Public so a
        /// future `CameraRig`/shake consumer can read it without this class
        /// needing to know about that consumer.
        public float Trauma { get; private set; }

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

            if (TryConsumeHitstopBudget(_gameFeel.HitstopSeconds))
            {
                TriggerHitstop(_gameFeel.HitstopSeconds);
                if (hasView && _activeScope == GameFeelConfig.HitstopScopeMode.TargetOnly)
                {
                    targetView.FreezePosition(_gameFeel.HitstopSeconds);
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
            while (_hitstopBudget.Count > 0 && _hitstopBudget.Peek() < windowStart)
                _hitstopBudget.Dequeue();

            float used = _hitstopBudget.Count * seconds;
            if (used + seconds > _gameFeel.MaxHitstopRatio) return false;

            _hitstopBudget.Enqueue(now);
            return true;
        }

        void AddTrauma(float amount) => Trauma = Mathf.Clamp01(Mathf.Max(Trauma, amount));

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
