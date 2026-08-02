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
    public sealed class HudController : MonoBehaviour
    {
        // Guards the dash-cooldown division against a zero HeroConfig.DashCooldown —
        // never hit in practice ([Range(0.1f, 10f)] on the SO), but cheap insurance
        // against a NaN fill on the bar during hot-tweak.
        const float CooldownEps = 1e-4f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] Image _hpFill;
        [SerializeField] Image _dashFill;
        [SerializeField] TMP_Text _waveText;

        void LateUpdate()
        {
            // World is null only for a single frame ordering edge case before the
            // runner's own Awake has run; skip rendering rather than throw.
            if (_runner.World == null) return;

            var player = _runner.Curr.Player;
            var hero = _runner.World.Config.Hero;

            _hpFill.fillAmount = player.Hp / hero.MaxHp;
            _dashFill.fillAmount = 1f - player.DashCooldown / Mathf.Max(hero.DashCooldown, CooldownEps);
            // Technical placeholder string, not world-dictionary text — real UI copy
            // for the wave counter arrives with Task 24.
            _waveText.text = "WAVE " + _runner.Curr.Wave.WaveIndex;
        }
    }
}
