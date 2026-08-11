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
    /// increments, `WaveSystem.Update`'s early return once no player is alive —
    /// `Targeting.NearestAlivePlayer` since Stage 2 Task 8, same guarantee as
    /// the old solo-only `!w.Player.Alive` check it replaced), so nothing
    /// further mutates them while the panel stays up.
    ///
    /// `R`/`Shift+R` are a fixed dev-controller exception (Приложение П-6) to the
    /// project's "route input through InputActionAsset" rule (spec §3.8): direct
    /// `Keyboard.current` polling, wrapped in the same `#if UNITY_EDITOR ||
    /// DEVELOPMENT_BUILD` guard `PracticeTargets`/`DevOverlay` use elsewhere, so
    /// it compiles out of Release builds entirely — only the on-screen restart
    /// button (ordinary uGUI `Button`, driven by the project-wide `UI/Submit`
    /// action through the existing `EventSystem`/`InputSystemUIInputModule`, no
    /// custom input code needed) ships as a restart surface in Release.
    ///
    /// IT IS THIS CLIENT'S OWN DEATH SCREEN, AS OF STAGE 2 TASK 47b (bd
    /// `app-jw0`). The fan-out delivers every `PlayerDied` this client is told
    /// about, which in solo was only ever one player's and on a networked client
    /// is everybody's it can see — so `HandleEvent` now compares the victim
    /// against this client's own seat before showing anything.
    ///
    /// AND IT OFFERS ONLY WHAT THE BACKEND CAN DO (the owner's decision 4b).
    /// Where a match cannot be restarted from this process
    /// (`ISimBackend.CanRestartMatch`), the restart button and its `R`/`Shift+R`
    /// hint are hidden and the keys are inert; what the panel offers there is
    /// "Наблюдать", which closes it and leaves the match — and the spectator HUD
    /// behind it — running. Where the numbers are somebody else's
    /// (`HasMatchStats`), the metrics print dashes instead of the zeros the
    /// render pair holds.
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
        /// The line that advertises `R`/`Shift+R`. It goes wherever the restart
        /// button goes (Stage 2 Task 47b): on a backend that cannot restart, the
        /// two keys do nothing, and a hint for them is a false instruction
        /// rather than a missing one.
        [SerializeField] TMP_Text _hintText;
        /// Closes the panel and leaves the match running (Stage 2 Task 47b, the
        /// owner's decision 4b) — the only thing there IS to do on a networked
        /// client, where the match goes on without this player.
        [SerializeField] Button _spectateButton;

        float _shownAtUnscaledTime = -1f;

        void Awake()
        {
            _panel.SetActive(false);
            _restartButton.onClick.AddListener(_runner.RestartNewSeed);
            if (_spectateButton != null) _spectateButton.onClick.AddListener(Hide);
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
            // bd `app-jw0`, closed by Stage 2 Task 47b: THIS client's death, not
            // anybody's. Solo never noticed — there was one player — but a
            // networked client is delivered every death it can see (and its own
            // unconditionally, `EventRelevance`'s own-death carve-out), so
            // without this the death screen came up over a match this player was
            // still very much alive in.
            //
            // BY `PlayerIndex`, which is this kind's convention for the VICTIM
            // (`SimEvent`'s own doc puts `PlayerDamaged`/`PlayerDied` under it
            // for both id fields), and against `LocalPlayerIndex` rather than
            // `ObservedIndex`: this screen is about one's own death, and a
            // spectator watching somebody else die is watching, not dying.
            if (e.PlayerIndex != _runner.RenderCurr.LocalPlayerIndex) return;
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

            // THE PANEL OFFERS WHAT THIS BACKEND CAN ACTUALLY DO (Stage 2 Task
            // 47b, the owner's decision 4b) — the same shape `DevOverlay` uses
            // for `CanDevSpawnMob`. On a networked client `Restart` is refused
            // by the backend, so the restart button was a control that silently
            // did nothing; what exists there instead is the match this player is
            // no longer in, and watching it.
            bool canRestart = _runner.CanRestartMatch;
            _restartButton.gameObject.SetActive(canRestart);
            if (_hintText != null) _hintText.gameObject.SetActive(canRestart);
            if (_spectateButton != null) _spectateButton.gameObject.SetActive(!canRestart);

            _panel.SetActive(true);
            // Review round (Minor): without an explicit selection, UI/Submit has
            // nothing to fire on until the owner first moves the mouse over the
            // button — gamepad/keyboard Submit would silently do nothing. The
            // selected object is whichever button this panel is actually
            // offering.
            Button offered = canRestart ? _restartButton : _spectateButton;
            if (EventSystem.current != null && offered != null)
                EventSystem.current.SetSelectedGameObject(offered.gameObject);
        }

        /// Closes the panel WITHOUT ending anything (Stage 2 Task 47b): the
        /// match carries on, the HUD becomes a spectator's
        /// (`SimulationRunner.ObservedIndex`), and nothing brings this panel
        /// back — `PlayerDied` for this seat arrives once per match, and
        /// `HandleWorldRestarted` below is what resets the screen for the next
        /// one.
        void Hide()
        {
            _shownAtUnscaledTime = -1f;
            _panel.SetActive(false);
        }

        void HandleWorldRestarted() => Hide();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void Update()
        {
            if (_shownAtUnscaledTime < 0f) return;
            // The same gate the button obeys (Stage 2 Task 47b): these two keys
            // call exactly what it calls, so a backend that refuses a restart
            // must refuse it from every surface, not only from the visible one.
            if (!_runner.CanRestartMatch) return;
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
        ///
        /// A DASH WHERE THERE IS NO NUMBER (Stage 2 Task 47b, the owner's
        /// decision 4b). `ISimBackend.HasMatchStats` is the test — false on a
        /// networked backend, whose protocol carries no block for either half of
        /// the counters, so `RenderSnapshot.Stats`/`WorldStats` are
        /// `BeginSlot`'s cleared zeros rather than measurements. Printed
        /// straight, those six lines are a complete, plausible and permanent
        /// lie: nothing utilised, no waves held, no damage taken, and an
        /// accuracy of 0% for a player who spent the match shooting. The seed
        /// and the tweak marker are NOT dashed — they are the facade's own
        /// facts and true on either backend.
        string BuildMetricsText()
        {
            if (!_runner.HasMatchStats) return BuildDashedMetricsText();

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
            // for PvE in multiplayer now. Stage 2 Task 17 extended the same
            // routing to PvP (SimulationWorld.DamagePlayer takes the attacker's
            // index): a round landed on another PLAYER counts toward ShotsHit
            // and, on the killing blow, Kills/HeadshotKills, so this ratio now
            // covers both halves of the match rather than PvE alone.
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

        /// The same six lines with a dash in place of every number the world
        /// counted, and the two facade facts intact — see `BuildMetricsText`'s
        /// own doc for why a dash rather than the zero the render pair holds.
        /// The LABELS are repeated rather than shared with a formatter: the two
        /// texts differ in every value and in nothing else, and a shared
        /// builder taking six nullable numbers would be longer than both.
        string BuildDashedMetricsText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Заход");
            sb.AppendLine($"Утилизировано: {NoNumber}");
            sb.AppendLine($"Волн отражено: {NoNumber}");
            sb.AppendLine($"Время на объекте: {NoNumber}");
            sb.AppendLine($"Точность: {NoNumber}");
            sb.AppendLine($"Дэшей: {NoNumber}");
            sb.AppendLine($"Урона получено: {NoNumber}");
            sb.Append($"seed: {_runner.Seed}");
            if (_runner.ConfigTweaked) sb.Append(" (прогон с правками)");
            return sb.ToString();
        }

        /// What stands where a number would, when the count is on another
        /// machine. An em dash, the typographic one — not a hyphen, which reads
        /// as a minus in a column of figures.
        const string NoNumber = "—";

        static string FormatTime(float seconds)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{total / 60:00}:{total % 60:00}";
        }
    }
}
