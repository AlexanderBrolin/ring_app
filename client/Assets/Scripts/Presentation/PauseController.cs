using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine.InputSystem;
#endif

namespace Ring.Presentation
{
    /// Escape-triggered pause menu (Task 24 spec Interfaces): flips
    /// `SimulationRunner.Paused`, the project's sole pause gate — `Time.
    /// timeScale` is never touched anywhere (class doc on `SimulationRunner`).
    /// Menu offers resume/restart/quit.
    ///
    /// `Escape` is a fixed dev-controller exception (Приложение П-6) to the
    /// project's "route input through InputActionAsset" rule (spec §3.8), same
    /// as `DeathOverlayController`'s R/Shift+R — direct `Keyboard.current`
    /// polling wrapped in the same `#if UNITY_EDITOR || DEVELOPMENT_BUILD` guard,
    /// stripped from Release builds entirely. The menu's own buttons are
    /// ordinary uGUI, so resume/restart/quit stay reachable (by mouse/gamepad,
    /// through the existing `EventSystem`/`InputSystemUIInputModule`) in every
    /// build even without the keyboard shortcut — Escape just cannot OPEN the
    /// menu outside Editor/dev builds yet.
    public sealed class PauseController : MonoBehaviour
    {
        [SerializeField] SimulationRunner _runner;
        [SerializeField] GameObject _menu;
        [SerializeField] Button _resumeButton;
        [SerializeField] Button _restartButton;
        [SerializeField] Button _quitButton;

        void Awake()
        {
            _menu.SetActive(false);
            _resumeButton.onClick.AddListener(Resume);
            _restartButton.onClick.AddListener(RestartFromMenu);
            _quitButton.onClick.AddListener(Quit);
        }

        // WorldRestarted is not a tick event (П-1 only restricts TicksFlushed to
        // its sole SimEventRouter subscriber) — direct subscription, same shape
        // as the deleted PracticeTargets' pattern. Covers a restart triggered
        // from somewhere other than this menu's own RestartFromMenu (dev-overlay
        // forced-seed restart, or the death overlay's R/Shift+R) while paused.
        void OnEnable() => _runner.WorldRestarted += HandleWorldRestarted;

        void OnDisable() => _runner.WorldRestarted -= HandleWorldRestarted;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            if (kb.escapeKey.wasPressedThisFrame) Toggle();
        }
#endif

        void Toggle()
        {
            if (_runner.Paused) Resume();
            else Open();
        }

        void Open()
        {
            _runner.Paused = true;
            _menu.SetActive(true);
        }

        void Resume()
        {
            _runner.Paused = false;
            _menu.SetActive(false);
        }

        void RestartFromMenu()
        {
            // Runner.Restart already clears Paused itself (Task 24 defensive
            // fix), but the menu still needs to hide — nothing else does that
            // for this specific path.
            _menu.SetActive(false);
            _runner.RestartNewSeed();
        }

        void Quit()
        {
            // EditorApplication.isPlaying = false lives in the UnityEditor
            // namespace, unreachable from this Presentation asmdef (it ships
            // into player builds, where UnityEditor doesn't exist at all).
            // Application.Quit() is a documented no-op in the Editor (just
            // logged, not an error) and does the real thing in a built player.
            Application.Quit();
        }

        void HandleWorldRestarted()
        {
            _runner.Paused = false;
            _menu.SetActive(false);
        }
    }
}
