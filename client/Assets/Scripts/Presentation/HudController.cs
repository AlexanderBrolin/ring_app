using Ring.Data;
using Ring.Simulation.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ring.Presentation
{
    /// HUD skeleton (Stage 1 Task 14, spec §3.10): HP bar, current wave number.
    /// Reads exclusively from the runner's `Curr` snapshot every frame —
    /// `World.Config` is the one exception, used only for the HP maximum
    /// needed to normalize the bar, never for live per-tick state. This keeps
    /// Presentation a pure reader: it never computes game outcomes, only renders what
    /// the snapshot already decided.
    ///
    /// Task 22 (spec Г6, combat-depth): a third bar, stamina — same `GetOrCreateBar`
    /// shape as HP/dash (Background+Fill Image pair, no text label, QD7: bars are
    /// color+position coded, "Буст" stays a docs/settings term). Filled from
    /// `Curr.Player.Stamina`, NOT `RenderCurr` — same source as the HP bar
    /// above, so the bar doesn't freeze during a hitstop (QC10).
    ///
    /// В1 fix-wave 1 (owner playtest feedback, item 1 "две полоски"): the
    /// dash-cooldown bar is retired — two bars only (HP, Stamina). Dash
    /// readiness is already legible from Stamina (dash/slide both spend from
    /// the same pool) and the doll's own dash-lean/glow feedback, so a
    /// dedicated third bar was redundant screen clutter. `_dashFill` and its
    /// per-frame update below are gone; `StageOneSceneBootstrap` self-heals
    /// an already-committed scene's stale `DashBar` object out and slides
    /// `StaminaBar` up into the freed slot (that bootstrap section's own doc).
    public sealed class HudController : MonoBehaviour
    {
        // Guards the stamina-max/threshold divisions against a zero
        // HeroConfig.StaminaMax/GameFeelConfig.StaminaBarLowThreshold — never
        // hit in practice ([Range] floors on both SOs), but cheap insurance
        // against a NaN fill on the bar during hot-tweak.
        const float DivEps = 1e-4f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] Image _hpFill;
        [SerializeField] Image _staminaFill;
        [SerializeField] TMP_Text _waveText;

        // Task 22: StaminaDenied pulse — armed by HandleEvent (SimEventRouter's
        // fan-out, П-1; this is the one per-event reaction this class needs, so
        // it joins the router rather than subscribing to TicksFlushed itself,
        // same rule every other Presentation class already follows), counted
        // down here in LateUpdate on Time.unscaledDeltaTime — same
        // hitstop-independent timer contract GameFeelDirector's own short
        // feel-timers (hitstop, vignette) use.
        float _staminaDeniedTimer;

        void OnEnable() => _runner.WorldRestarted += HandleWorldRestarted;

        void OnDisable() => _runner.WorldRestarted -= HandleWorldRestarted;

        void LateUpdate()
        {
            // The backend has nothing to show only for a single frame ordering
            // edge case before the runner's own Awake has run; skip rendering
            // rather than throw. Task 43 renamed the question (`World == null`
            // -> `!Ready`), not the reason for asking it — and a networked
            // backend widens that window from one frame to "until the first
            // snapshot lands".
            if (!_runner.Ready) return;

            var player = _runner.Curr.Player;
            var hero = _runner.Config.Hero;

            _hpFill.fillAmount = player.Hp / hero.MaxHp;
            // F-8 fix: user-facing strings are Russian (ADR-003 §9 word list) — the
            // old "WAVE " placeholder predates the settled world vocabulary.
            _waveText.text = "ВОЛНА " + _runner.Curr.Wave.WaveIndex;

            UpdateStaminaBar(player.Stamina, hero.StaminaMax);
        }

        /// `SimEventRouter`'s fan-out (П-1). A `StaminaDenied` attempt (dash or
        /// slide gated by an insufficient stamina pool, `SimEvent.Kind` doc)
        /// arms a short pulse, rendered by `UpdateStaminaBar` below in place of
        /// the ordinary threshold color for `StaminaDeniedPulseSeconds`.
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind == SimEventKind.StaminaDenied)
                _staminaDeniedTimer = _gameFeel.StaminaDeniedPulseSeconds;
        }

        /// Task 22 (spec brief): fill fraction, plus a color lerp from
        /// `StaminaBarFullColor` (at/above `StaminaBarLowThreshold`) towards
        /// `StaminaBarLowColor` as the remaining fraction drops through the
        /// threshold to empty — overridden by a flat `StaminaBarLowColor` while
        /// a `StaminaDenied` pulse is active.
        void UpdateStaminaBar(float stamina, float staminaMax)
        {
            float frac = stamina / Mathf.Max(staminaMax, DivEps);
            _staminaFill.fillAmount = frac;

            if (_staminaDeniedTimer > 0f)
            {
                _staminaDeniedTimer -= Time.unscaledDeltaTime;
                _staminaFill.color = _gameFeel.StaminaBarLowColor;
                return;
            }

            float threshold = Mathf.Max(_gameFeel.StaminaBarLowThreshold, DivEps);
            float t = Mathf.Clamp01(1f - frac / threshold);
            _staminaFill.color = Color.Lerp(_gameFeel.StaminaBarFullColor, _gameFeel.StaminaBarLowColor, t);
        }

        /// A match restart (direct `WorldRestarted` subscription, same shape as
        /// every other class in this namespace) must not leave a pulse bleeding
        /// visibly into the fresh run's first frame.
        void HandleWorldRestarted() => _staminaDeniedTimer = 0f;
    }
}
