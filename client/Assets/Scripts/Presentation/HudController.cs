using Ring.Data;
using Ring.Simulation.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ring.Presentation
{
    /// HUD skeleton (Stage 1 Task 14, spec §3.10): HP bar, current wave number.
    /// Reads exclusively from the runner's `Curr` snapshot every frame — the
    /// runner's `Config` is the one exception, used only for the HP maximum
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
    ///
    /// Stage 2 Task 47b (the owner's decision 4a): the bars describe the seat
    /// this client is WATCHING, which is its own for the whole of solo and for
    /// as long as that player is standing. While it is watching somebody else
    /// the HUD is deliberately SMALLER, not fuller: HP and the wave stay, the
    /// stamina bar is hidden outright because no stamina of anyone else exists
    /// on the wire, and one label says whose health that is. Nothing new appears
    /// while this player is alive.
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
        /// The stamina bar's ROOT object, not its fill — a bar hidden by
        /// emptying its fill still shows its background, which reads as "out of
        /// Буст" rather than "this number is not yours to see" (Stage 2 Task
        /// 47b, the owner's decision 4a).
        [SerializeField] GameObject _staminaBar;
        [SerializeField] TMP_Text _waveText;
        /// Shown only while this client is watching somebody else (Stage 2 Task
        /// 47b) — the one thing on screen that says the HP beside it is not
        /// this player's.
        [SerializeField] TMP_Text _spectateLabel;

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

            // Stage 2 Task 47b: the bars belong to whoever is being WATCHED,
            // which is this client's own player for the whole of solo and for
            // as long as that player is standing (`SimulationRunner.
            // ObservedIndex`). The index is resolved against the RENDER pair
            // while the numbers still come off `Curr` — deliberately, and the
            // two can only disagree inside a hitstop freeze, which moves a pose
            // and never a seat's existence (QC10: the bar must not freeze with
            // the picture).
            RenderSnapshot curr = _runner.Curr;
            int observed = _runner.ObservedIndex;
            var hero = _runner.Config.Hero;
            bool spectating = _runner.IsSpectating;

            // F-8 fix: user-facing strings are Russian (ADR-003 §9 word list) — the
            // old "WAVE " placeholder predates the settled world vocabulary.
            _waveText.text = "ВОЛНА " + curr.Wave.WaveIndex;

            // THE STAMINA BAR IS HIDDEN, NOT EMPTIED, WHILE SPECTATING (the
            // owner's decision 4a). Stamina is not on the wire in any form — no
            // block carries it and no flag proxies it — so a bar drawn for
            // somebody else could only ever be a painted zero, and a painted
            // zero is a claim about their Буст rather than an absence of one.
            if (_staminaBar != null) _staminaBar.SetActive(!spectating);
            if (_spectateLabel != null)
            {
                _spectateLabel.gameObject.SetActive(spectating);
                // The world's own word for a player (ADR-003 §9: Игрок →
                // Сборщик) and a HUMAN seat number, one-based: this is a label,
                // not an index.
                if (spectating) _spectateLabel.text = "НАБЛЮДЕНИЕ · СБОРЩИК " + (observed + 1);
            }

            // A frame that says nothing about the watched seat moves nothing:
            // its `PlayerState` would be `default` — full-health-less zero at
            // the arena origin — and drawing that would report a death that
            // this frame never witnessed. The bar holds what it had, the same
            // answer `SimulationRunner.RenderObservedWorldPos` gives the camera.
            if (observed < 0 || observed >= curr.PlayerCount || !curr.PlayerKnown[observed]) return;

            PlayerState player = curr.Players[observed];
            _hpFill.fillAmount = player.Hp / hero.MaxHp;
            if (!spectating) UpdateStaminaBar(player.Stamina, hero.StaminaMax);
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
