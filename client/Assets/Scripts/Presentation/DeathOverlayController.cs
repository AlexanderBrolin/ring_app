using System.Text;
using Ring.Simulation.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine.InputSystem;
#endif

namespace Ring.Presentation
{
    /// Death screen (Task 24 spec Interfaces): shown on `PlayerDied`, driven
    /// exclusively through `SimEventRouter`'s `HandleEvent` fan-out (П-1 — this
    /// task's amendment explicitly calls this out: no new `TicksFlushed`
    /// subscriber). Metrics are computed once when the panel is shown, straight
    /// off `SimulationRunner.Curr.Stats` — safe because match stats freeze the
    /// tick the player dies (spec §3.12: `DamagePlayer`/`DamageMob`'s guarded
    /// increments, `WaveSystem.Update`'s own `!w.Player.Alive` early-return), so
    /// nothing further mutates them while the panel stays up.
    ///
    /// `R`/`Shift+R` are a fixed dev-controller exception (Приложение П-6) to the
    /// project's "route input through InputActionAsset" rule (spec §3.8): direct
    /// `Keyboard.current` polling, wrapped in the same `#if UNITY_EDITOR ||
    /// DEVELOPMENT_BUILD` guard `PracticeTargets`/`DevOverlay` use elsewhere, so
    /// it compiles out of Release builds entirely — only the on-screen restart
    /// button (ordinary uGUI `Button`, driven by the project-wide `UI/Submit`
    /// action through the existing `EventSystem`/`InputSystemUIInputModule`, no
    /// custom input code needed) ships as a restart surface in Release.
    public sealed class DeathOverlayController : MonoBehaviour
    {
        // Unscaled seconds after Show() before the keyboard shortcuts activate —
        // guards against the same keypress that coincided with death (or a
        // startled reflex tap) immediately consuming the overlay.
        const float InputDelaySeconds = 0.5f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameFeelDirector _gameFeelDirector;
        [SerializeField] GameObject _panel;
        [SerializeField] TMP_Text _metricsText;
        [SerializeField] Button _restartButton;

        float _shownAtUnscaledTime = -1f;

        void Awake()
        {
            _panel.SetActive(false);
            _restartButton.onClick.AddListener(_runner.RestartNewSeed);
        }

        // WorldRestarted is not a tick event (П-1 only restricts TicksFlushed to
        // its sole SimEventRouter subscriber) — direct subscription, same shape
        // as the deleted PracticeTargets' pattern.
        void OnEnable() => _runner.WorldRestarted += HandleWorldRestarted;

        void OnDisable() => _runner.WorldRestarted -= HandleWorldRestarted;

        /// Called by `SimEventRouter` for every event in this tick-flush's buffer
        /// (П-1 fan-out) — last slot in the order (Приложение П-1: GameFeelDirector
        /// → PersistentProps → AudioDirector → ViewRegistry → DeathOverlayController).
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind != SimEventKind.PlayerDied) return;
            Show();
        }

        void Show()
        {
            // Task 25 (this task's amendment, explicitly called out ahead of
            // time in Task 24's brief): a hitstop freeze must never survive into
            // the death screen — `GameFeelDirector`'s own `PlayerDied` handler
            // already forces this off before this method runs (П-1 fan-out:
            // `GameFeelDirector` is the first slot, this controller the last),
            // so this call is ordinarily a no-op; kept explicit/defensive rather
            // than relying solely on that ordering.
            _gameFeelDirector.ForceEndHitstop();

            _shownAtUnscaledTime = Time.unscaledTime;
            _metricsText.text = BuildMetricsText();
            _panel.SetActive(true);
            // Review round (Minor): without an explicit selection, UI/Submit has
            // nothing to fire on until the owner first moves the mouse over the
            // button — gamepad/keyboard Submit would silently do nothing.
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(_restartButton.gameObject);
        }

        void HandleWorldRestarted()
        {
            _shownAtUnscaledTime = -1f;
            _panel.SetActive(false);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void Update()
        {
            if (_shownAtUnscaledTime < 0f) return;
            if (Time.unscaledTime - _shownAtUnscaledTime < InputDelaySeconds) return;

            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            if (!kb.rKey.wasPressedThisFrame) return;

            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            if (shift) _runner.Restart(_runner.Seed);
            else _runner.RestartNewSeed();
        }
#endif

        /// Русские подписи — словарь мира (ADR-003 §9) + Приложение П-6.
        string BuildMetricsText()
        {
            // Stage 2 Task 5: personal counters off Curr.Stats (the local
            // player's own MatchStats), WavesCleared off Curr.WorldStats (a
            // match-wide counter, not something any one player earned).
            MatchStats stats = _runner.Curr.Stats;
            WorldStats worldStats = _runner.Curr.WorldStats;
            float timeSeconds = stats.DeathTick * SimulationWorld.TickDt;
            // Stage 2 Task 7: ShotsHit/Kills/HeadshotKills now route through the
            // projectile's OwnerIndex (SimulationWorld.DamageMob) instead of a
            // hardcoded player 0, so Curr.Stats (the LOCAL player's own
            // MatchStats) reflects only shots THEY landed — this ratio is sound
            // for PvE in multiplayer now. PvP kills (DamagePlayer crediting the
            // attacker) still arrive in Task 17 and don't feed ShotsHit at all.
            float accuracy = stats.ShotsFired > 0 ? (float)stats.ShotsHit / stats.ShotsFired : 0f;

            var sb = new StringBuilder();
            sb.AppendLine("Заход");
            sb.AppendLine($"Утилизировано: {stats.Kills}");
            sb.AppendLine($"Волн отражено: {worldStats.WavesCleared}");
            sb.AppendLine($"Время на объекте: {FormatTime(timeSeconds)}");
            sb.AppendLine($"Точность: {accuracy:P0}");
            sb.AppendLine($"Дэшей: {stats.DashesUsed}");
            sb.AppendLine($"Урона получено: {stats.DamageTaken:F0}");
            sb.Append($"seed: {_runner.Seed}");
            if (_runner.ConfigTweaked) sb.Append(" (прогон с правками)");
            return sb.ToString();
        }

        static string FormatTime(float seconds)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{total / 60:00}:{total % 60:00}";
        }
    }
}
