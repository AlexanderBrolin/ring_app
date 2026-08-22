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
    /// The end-of-raid screen (Task 24 spec Interfaces) — "death screen" was its
    /// name while dying was the only way to reach it.
    ///
    /// THREE WAYS IN, AND THEY ARE THREE DIFFERENT FACTS. `PlayerDied` for this
    /// client, delivered through `SimEventRouter`'s `HandleEvent` fan-out (П-1 —
    /// Task 24's amendment explicitly calls this out: no new `TicksFlushed`
    /// subscriber); a BOARD arriving, which says the whole match ended (Т34);
    /// and THIS COLLECTOR WALKING OUT (`LocalCollectorWalkedOut`, bd
    /// `app-rkcu`), which says the raid ended for him alone and is the only one
    /// of the three that solo can ever produce besides dying. The last was
    /// missing until the owner extracted through the gate and got nothing at
    /// all.
    ///
    /// Metrics are computed once when the panel is shown, straight
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
    /// behind it — running. ⚠ UNLESS THERE IS NOTHING LEFT TO WATCH OR NO RIGHT
    /// TO WATCH IT: over a finished raid (Ф7 review A-1) and to a collector who
    /// walked out (bd `app-rkcu`, whom `SpectatePolicy` refuses outright), that
    /// button is not offered either — a control that silently does nothing is
    /// the one thing decision 4b was taken to remove. Where the numbers are
    /// somebody else's
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
        /// The raid's public board (Stage 3 Т34, spec §3.11): one line per
        /// collector — how his raid ended and what he carried out — with this
        /// client's own marked. Hidden while there is no board, which is every
        /// death that is not also the end of the raid.
        [SerializeField] TMP_Text _resultsText;

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

        /// Whether THIS client's collector has walked out of the raid (playtest
        /// В1 round two, bd `app-rkcu`) — the second thing that ends a raid for
        /// one player, beside dying, and the one nothing in this class could
        /// see.
        ///
        /// WHY THE SCREEN NEEDED IT. `Update` below polls for a BOARD, and a
        /// board is a networked object by construction: `MatchServer` builds it
        /// when a whole match ends, so `LocalSimBackend.HasMatchResults` is
        /// `false` for ever and solo — the mode the owner tunes in — had no
        /// second way in at all. A collector who killed the Director and left
        /// through the gate got no screen whatsoever, while my own note for Т34
        /// claimed the opposite (lesson 406/407: a doc explaining an absence has
        /// to be read as a list of what it actually accounted for). "My raid is
        /// over" and "the match's board has arrived" are two facts, and only the
        /// first one belongs to this client.
        ///
        /// OFF `PlayerExtractedInMatch` AND NOT `Players[i].Extracted`, for the
        /// reason that field's own doc gives: the second is only ever true about
        /// oneself, and a rule that reads it happens to work here — this IS the
        /// local slot — but would quietly mean something else the moment it was
        /// reused for anybody else's seat. One fact, one home.
        ///
        /// A NULL OR SHORT FRAME IS "NO", not an exception: this is polled every
        /// frame, including the ones before a backend has a picture at all.
        public static bool LocalCollectorWalkedOut(RenderSnapshot frame)
        {
            if (frame == null) return false;
            int local = frame.LocalPlayerIndex;
            if (local < 0 || local >= frame.PlayerCount) return false;
            return frame.PlayerExtractedInMatch[local];
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
            ShowBoard();

            // THE PANEL OFFERS WHAT THIS BACKEND CAN ACTUALLY DO (Stage 2 Task
            // 47b, the owner's decision 4b) — the same shape `DevOverlay` uses
            // for `CanDevSpawnMob`. On a networked client `Restart` is refused
            // by the backend, so the restart button was a control that silently
            // did nothing; what exists there instead is the match this player is
            // no longer in, and watching it.
            bool canRestart = _runner.CanRestartMatch;
            // ⚠ AND NOTHING IS OFFERED OVER A FINISHED RAID (fix round, Ф7
            // review A-1). "Наблюдать" closes this panel to watch the match go
            // on — but once the board is up the match is OVER, so there is
            // nothing to watch, and worse: `Update` below reopens the panel
            // while a board stands, which turned that button into a control
            // that silently did nothing. That is the exact defect the owner's
            // decision 4b removed from the restart button, and it must not come
            // back through the other one.
            bool raidOver = _runner.HasMatchResults;
            // ⚠ AND NOT TO A COLLECTOR WHO WALKED OUT EITHER (bd `app-rkcu`,
            // the same lens as A-1 above). `SpectatePolicy.Evaluate` refuses an
            // extracted requester outright — `SpectateRefusal.RequesterExtracted`
            // is its second check, and the spec's reason is that he left the
            // object and has no business looking through anyone's eyes (§3.5) —
            // so offering him this button would put the dead control back, this
            // time through the door the board does not cover: on a networked
            // raid his own extraction comes a long time before the match's end.
            // The RESTART button is untouched by that: `CanRestartMatch` is
            // false on the networked backend anyway, and in solo — where it is
            // true — restarting after walking out is exactly what the panel is
            // for.
            bool walkedOut = _runner.Ready && LocalCollectorWalkedOut(_runner.RenderCurr);
            _restartButton.gameObject.SetActive(canRestart && !raidOver);
            if (_hintText != null) _hintText.gameObject.SetActive(canRestart && !raidOver);
            if (_spectateButton != null)
                _spectateButton.gameObject.SetActive(!canRestart && !raidOver && !walkedOut);

            _panel.SetActive(true);
            // Review round (Minor): without an explicit selection, UI/Submit has
            // nothing to fire on until the owner first moves the mouse over the
            // button — gamepad/keyboard Submit would silently do nothing. The
            // selected object is whichever button this panel is actually
            // offering.
            Button offered = raidOver || (!canRestart && walkedOut)
                ? null
                : canRestart ? _restartButton : _spectateButton;
            if (EventSystem.current != null && offered != null)
                EventSystem.current.SetSelectedGameObject(offered.gameObject);
        }

        /// Closes the panel WITHOUT ending anything (Stage 2 Task 47b): the
        /// match carries on and the HUD becomes a spectator's
        /// (`SimulationRunner.ObservedIndex`).
        ///
        /// ⚠ WHAT BRINGS THE PANEL BACK, corrected twice. This paragraph used to
        /// say "nothing" does, which was true while `PlayerDied` was the only
        /// way in. Т34 added a BOARD arriving (fix round, Ф7 review A-1), and
        /// bd `app-rkcu` added this collector's own EXTRACTION — both reopen it,
        /// because a raid can end without this client dying. None of them
        /// collide with this method, because `Show` offers no closing button in
        /// either case: a finished raid has nothing left to spectate, and a
        /// collector who walked out is refused spectating by the server anyway
        /// (`SpectatePolicy.Evaluate`). `HandleWorldRestarted` resets the screen
        /// for the next raid.
        void Hide()
        {
            _shownAtUnscaledTime = -1f;
            _panel.SetActive(false);
            _boardDrawn = false;
        }

        void HandleWorldRestarted() => Hide();

        /// Draws the raid's public board, or takes it off the panel when there
        /// is none (Stage 3 Т34).
        ///
        /// THE BACKEND ANSWERS `null` UNTIL A RAID ENDS, and the two cases the
        /// panel opens in are genuinely different: a collector who died while
        /// the raid goes on has no board to read, and taking the object off
        /// rather than printing an empty line is what keeps the panel from
        /// showing an empty heading over nothing.
        void ShowBoard()
        {
            if (_resultsText == null) return;

            string board = _runner.MatchResultsBoard;
            // `!= null` RATHER THAN `IsNullOrEmpty` (fix round, Ф7 review B-7):
            // `MatchResultsBoard.Format` returns null for "no raid has ended"
            // and a (today unreachable) empty string for "one ended with nobody
            // in it" — collapsing the two would throw away the distinction the
            // formatter's own doc keeps them apart for.
            bool has = board != null;
            if (_resultsText.gameObject.activeSelf != has)
                _resultsText.gameObject.SetActive(has);
            if (has) _resultsText.text = board;
            _boardDrawn = has;
        }

        /// Whether the board on the panel is the CURRENT one, so `Update` can
        /// watch for a board arriving without asking for its text every frame
        /// (`ISimBackend.MatchResultsBoard` builds what it returns). Cleared by
        /// `Hide` and by a restart, which is what lets the next raid's board be
        /// drawn in its turn.
        bool _boardDrawn;

        void Update()
        {
            // Stage 3 Т34: THE RAID CAN END WITHOUT THIS CLIENT DYING. The
            // panel has opened on `PlayerDied` alone since Stage 2, which
            // leaves a collector who extracted — or who was still standing
            // when the clock ran out — with no screen at all. A board
            // appearing is the other way in, and it is polled rather than
            // pushed because the message arrives in `Ring.Networking`, on the
            // far side of Р180's line, with no event this layer may subscribe
            // to. One reference comparison per frame.
            bool ready = _runner != null && _runner.Ready;
            bool hasBoard = ready && _runner.HasMatchResults;
            // bd `app-rkcu`: THE RAID CAN ALSO END FOR THIS CLIENT ALONE, and
            // that is the half a board can never report. A board is built by
            // `MatchServer` when a WHOLE match ends, so in solo it never comes
            // at all and a collector who walked out of the gate — the winning
            // move of the entire stage — got no screen whatsoever. Polled here
            // beside the board rather than pushed, for the same reason the board
            // is: the fact lives in the frame, and a frame is what this layer
            // reads.
            bool walkedOut = ready && LocalCollectorWalkedOut(_runner.RenderCurr);
            if (_shownAtUnscaledTime < 0f)
            {
                if (hasBoard || walkedOut) Show();
            }
            else if (hasBoard && !_boardDrawn)
            {
                // The panel is already up — it opened on a death that came a
                // moment BEFORE the raid's own end, and the board arrived while
                // it was standing. Drawn ONCE, on that edge: `MatchResultsBoard`
                // builds the text it returns.
                ShowBoard();
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
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
#endif
        }

        /// Русские подписи — словарь мира (ADR-003 §9) + Приложение П-6.
        ///
        /// A DASH WHERE THERE IS NO NUMBER (Stage 2 Task 47b, the owner's
        /// decision 4b). `ISimBackend.HasMatchStats` is the test — false on a
        /// networked backend, whose protocol carries no block for either half of
        /// the counters, so `RenderSnapshot.Stats`/`WorldStats` are
        /// `BeginSlot`'s cleared zeros rather than measurements. Printed
        /// straight, those six lines are a complete, plausible and permanent
        /// lie: nothing utilized, no waves held, no damage taken, and an
        /// accuracy of 0% for a player who spent the match shooting. The seed
        /// and the tweak marker are NOT dashed — they are the facade's own
        /// facts and true on either backend.
        string BuildMetricsText()
        {
            // THE END-OF-MATCH MESSAGE OUTRANKS BOTH (fix round Ф7, review
            // A-2). A networked client's per-frame picture carries no counters
            // and never will, which is why this screen printed dashes — but the
            // numbers DO arrive, once, when the raid ends, and printing dashes
            // beside a working scoreboard while holding them is the absurdity
            // the dash was invented to avoid. Asked FIRST, because when it
            // answers it is the authoritative end-of-raid tally rather than a
            // live frame's running one.
            if (!_runner.TryGetFinalStats(out MatchStats stats, out WorldStats worldStats))
            {
                if (!_runner.HasMatchStats) return BuildDashedMetricsText();

                // Stage 2 Task 5: personal counters off Curr.Stats (the local
                // player's own MatchStats), WavesCleared off Curr.WorldStats (a
                // match-wide counter, not something any one player earned).
                stats = _runner.Curr.Stats;
                worldStats = _runner.Curr.WorldStats;
            }
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
