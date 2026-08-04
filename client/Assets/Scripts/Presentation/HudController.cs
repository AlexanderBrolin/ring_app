using Ring.Data;
using Ring.Simulation.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ring.Presentation
{
    /// HUD skeleton (Stage 1 Task 14, spec §3.10): HP bar, dash cooldown bar, current
    /// wave number. Reads exclusively from the runner's `Curr` snapshot every frame —
    /// `World.Config` is the one exception, used only for the HP/dash-cooldown maxima
    /// needed to normalize the bars, never for live per-tick state. This keeps
    /// Presentation a pure reader: it never computes game outcomes, only renders what
    /// the snapshot already decided.
    ///
    /// Т22 (combat-depth Г6, spec brief): a third bar, stamina — same `GetOrCreateBar`
    /// shape as HP/dash (Background+Fill Image pair, no text label, QD7: bars are
    /// color+position coded, "Буст" stays a docs/settings term). Filled from
    /// `Curr.Player.Stamina`, NOT `RenderCurr` — same source as the HP/dash bars
    /// above, so the bar doesn't freeze during a hitstop (QC10).
    public sealed class HudController : MonoBehaviour
    {
        // Guards the dash-cooldown division against a zero HeroConfig.DashCooldown —
        // never hit in practice ([Range(0.1f, 10f)] on the SO), but cheap insurance
        // against a NaN fill on the bar during hot-tweak. Reused below for the
        // stamina-max/threshold divisions (Т22), same rationale.
        const float CooldownEps = 1e-4f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] Image _hpFill;
        [SerializeField] Image _dashFill;
        [SerializeField] Image _staminaFill;
        [SerializeField] TMP_Text _waveText;

        // Т22: StaminaDenied pulse — armed by HandleEvent (SimEventRouter's
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
            // World is null only for a single frame ordering edge case before the
            // runner's own Awake has run; skip rendering rather than throw.
            if (_runner.World == null) return;

            var player = _runner.Curr.Player;
            var hero = _runner.World.Config.Hero;

            _hpFill.fillAmount = player.Hp / hero.MaxHp;
            _dashFill.fillAmount = 1f - player.DashCooldown / Mathf.Max(hero.DashCooldown, CooldownEps);
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

        /// Т22 (spec brief): fill fraction, plus a color lerp from
        /// `StaminaBarFullColor` (at/above `StaminaBarLowThreshold`) towards
        /// `StaminaBarLowColor` as the remaining fraction drops through the
        /// threshold to empty — overridden by a flat `StaminaBarLowColor` while
        /// a `StaminaDenied` pulse is active.
        void UpdateStaminaBar(float stamina, float staminaMax)
        {
            float frac = stamina / Mathf.Max(staminaMax, CooldownEps);
            _staminaFill.fillAmount = frac;

            if (_staminaDeniedTimer > 0f)
            {
                _staminaDeniedTimer -= Time.unscaledDeltaTime;
                _staminaFill.color = _gameFeel.StaminaBarLowColor;
                return;
            }

            float threshold = Mathf.Max(_gameFeel.StaminaBarLowThreshold, CooldownEps);
            float t = Mathf.Clamp01(1f - frac / threshold);
            _staminaFill.color = Color.Lerp(_gameFeel.StaminaBarFullColor, _gameFeel.StaminaBarLowColor, t);
        }

        /// A match restart (direct `WorldRestarted` subscription, same shape as
        /// every other class in this namespace) must not leave a pulse bleeding
        /// visibly into the fresh run's first frame.
        void HandleWorldRestarted() => _staminaDeniedTimer = 0f;
    }
}
