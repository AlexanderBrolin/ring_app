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
    /// above, deliberately chosen (QC10) so the bar would never stall behind
    /// the on-hit render pin Task Т10 (app-88jb) has since removed whole;
    /// `RenderCurr` is `Curr` on every frame now (`SimulationRunner`'s own
    /// doc), so the two reads are equivalent either way today.
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

        // ---------------------------------------------------------------------
        // Stage 3 Т33 (spec §3.11): the ammunition count, the phase line and
        // the extraction channel's ring.
        //
        // TWO OF THE THREE ARE PERSONAL, AND THE WIRE SAYS SO. Another
        // collector's `PlayerRecord` carries `Index`, `Pos`, `Dir`, `Hp` and
        // `Flags` and nothing else — no `Ammo`, no `ExtractTimer` — so while
        // this client is watching somebody else those two surfaces are HIDDEN
        // rather than drawn empty, exactly the rule and exactly the reason the
        // stamina bar already follows (Task 47b, owner decision 4a): a painted
        // zero is a claim about their magazine, not an absence of one. The
        // phase line is the third and it is match-wide, so it stays up for a
        // spectator too — it is the same raid either way.
        // ---------------------------------------------------------------------

        /// The magazine's ROOT, hidden while spectating — see the block above.
        [SerializeField] GameObject _ammoBar;
        [SerializeField] Image _ammoFill;
        [SerializeField] TMP_Text _ammoText;
        /// Т33's own separate mark for the emergency synthesis mode: the
        /// weapon keeps firing at `Ammo == 0`, slower
        /// (`WeaponSimConfig.EmergencyFireInterval`), and a player who cannot
        /// tell that from a jam will read a working gun as a broken one.
        [SerializeField] GameObject _emergencyLabel;
        /// Farm / Director / gate, plus what is left of the raid.
        [SerializeField] TMP_Text _phaseText;
        /// The extraction channel, drawn as a ring around the collector's own
        /// figure (spec §3.11). A radial `Image` moved to his screen point —
        /// this is the one HUD surface anchored to the world rather than to a
        /// corner, because what it reports is happening TO HIM and a corner
        /// meter would make the player look away from the thing that can
        /// interrupt it.
        [SerializeField] Image _extractRing;
        /// For the projection the ring above needs. The same `Main Camera`
        /// `AimProvider` casts from — one camera, wired by the same bootstrap.
        [SerializeField] Camera _camera;

        /// Masks the emergency mark for a moment after a cell is picked up
        /// (Р275). Collection is NOT predicted (CR 3), so the frame that says
        /// `Ammo > 0` is one round trip behind the event that says the cell was
        /// taken — and in that gap the mark would sit on screen announcing an
        /// emergency the world has already ended. The event is the earliest
        /// honest news there is, so it silences the mark; nothing else about
        /// the magazine is drawn from it, and the NUMBER keeps telling the
        /// server's truth throughout.
        float _pickupMaskTimer;

        /// How long that mask may last. A ceiling rather than a duration: the
        /// authoritative frame normally arrives well inside it and clears the
        /// mark on its own, and this is only what stops a `PickupTaken` whose
        /// frame never came from hiding the mark forever. One second is several
        /// times the 80 ms round trip Critical Rule 7 tests under, and it is
        /// display masking rather than a number a match is decided by — the
        /// same category as this file's bar colors.
        const float PickupMaskSeconds = 1f;

        // Task 22: StaminaDenied pulse — armed by HandleEvent (SimEventRouter's
        // fan-out, П-1; this is the one per-event reaction this class needs, so
        // it joins the router rather than subscribing to TicksFlushed itself,
        // same rule every other Presentation class already follows), counted
        // down here in LateUpdate on Time.unscaledDeltaTime — same
        // unscaled-time-driven timer contract GameFeelDirector's own short
        // feel-timers (trauma, shake, vignette) use.
        float _staminaDeniedTimer;

        /// The wave line's flash timer (Task Т7, bd `app-ggvz`, spec §3.10).
        /// Counted down by `WaveAnnounceTimerAfter` ITSELF, called from
        /// `LateUpdate` on `Time.unscaledDeltaTime` — same
        /// unscaled-time-driven contract `_staminaDeniedTimer` above already
        /// uses. The decay lives INSIDE the tested seam rather than in a
        /// decrement line of its own, so this class gains a third feel timer
        /// without gaining a third hand-written countdown to keep in step
        /// (review M-1: three timers do read the clock here — this one, the
        /// pickup mask and the stamina pulse — and that is the point being
        /// made, not a claim that only two do).
        float _waveAnnounceTimer;

        /// The wave number `WaveAnnounceTimerAfter` last compared against, so
        /// it can tell GROWTH from "held" next frame. -1 is the unseen
        /// sentinel: no real `WaveState.WaveIndex` reaches it (`WaveSystem`'s
        /// own doc — it is 1-based once assigned), so `LateUpdate` reads
        /// unseen as "no growth yet" rather than comparing against it
        /// literally. That is what keeps this class's actual first rendered
        /// frame, and the first frame after `HandleWorldRestarted`, from
        /// flashing over a transition nobody watched.
        int _previousWaveNumber = -1;

        /// `_waveText`'s own color before any flash ever touches it,
        /// snapshotted once in `Awake` — the one Unity lifecycle callback
        /// guaranteed to run exactly once regardless of later enable/disable
        /// cycles, unlike `OnEnable` just below. No other color in this class
        /// needs a memory like this: `UpdateStaminaBar`'s fill color is
        /// always a fresh formula off `GameFeelConfig`, never a return to
        /// something remembered.
        Color _waveTextBaseColor;

        void Awake() => _waveTextBaseColor = _waveText.color;

        void OnEnable() => _runner.WorldRestarted += HandleWorldRestarted;

        void OnDisable() => _runner.WorldRestarted -= HandleWorldRestarted;

        void LateUpdate()
        {
            // Stage 2 Task 47b: the bars belong to whoever is being WATCHED,
            // which is this client's own player for the whole of solo and for
            // as long as that player is standing (`SimulationRunner.
            // ObservedIndex`). The index is resolved against the RENDER pair
            // while the numbers still come off `Curr` — deliberately (QC10:
            // the bar must not stall behind the render pair), though the two
            // are the same pair on every frame now (Task Т10, app-88jb,
            // removed the on-hit render pin that used to be able to move one
            // without the other).
            int observed = _runner.ObservedIndex;
            bool spectating = _runner.IsSpectating;

            // WHAT SPECTATING HIDES AND SHOWS IS DECIDED ABOVE THE PICTURE
            // GUARD (fix-round 1, Ф-2), and that is the whole of the fix: the
            // guard below returns for the entire connect and handshake of a
            // networked client, so a surface switched off only underneath it is
            // a surface that ships in whatever state the scene was saved in.
            // The spectate label was saved enabled, and the word "НАБЛЮДЕНИЕ"
            // therefore hung over an empty screen until the first snapshot
            // landed. THE RULE ITSELF IS UNCHANGED AND STILL THE ONLY ONE — the
            // label is up exactly while `SimulationRunner.IsSpectating`, which
            // is false before that runner has run at all and false again on the
            // first frame its `UpdateObservation` finds no picture. (A client
            // PAUSED at the moment the picture goes away keeps the previous
            // answer until it unpauses, the facade having stopped calling that
            // method — which is what every other HUD surface does in a pause
            // too: it holds the screen it was drawn on.) Nothing here reads the
            // snapshot or `Config`, which is what makes the two lines safe this
            // side of the guard. `StageOneSceneBootstrap` ships the object
            // disabled as well; one half without the other lets the defect back
            // in, from either side.
            //
            // THE STAMINA BAR IS HIDDEN, NOT EMPTIED, WHILE SPECTATING (the
            // owner's decision 4a). Stamina is not on the wire in any form — no
            // block carries it and no flag proxies it — so a bar drawn for
            // somebody else could only ever be a painted zero, and a painted
            // zero is a claim about their Буст rather than an absence of one.
            // It rides above the guard with the label for symmetry rather than
            // for a defect of its own: `!spectating` is exactly the state it is
            // shipped in, so a picture-less frame asks for what it already has.
            if (_staminaBar != null) _staminaBar.SetActive(!spectating);
            // Т33: the magazine is personal for the same reason and by the same
            // fact — it is not in another collector's wire record at all.
            if (_ammoBar != null) _ammoBar.SetActive(!spectating);
            if (spectating)
            {
                if (_emergencyLabel != null) _emergencyLabel.SetActive(false);
                if (_extractRing != null) _extractRing.gameObject.SetActive(false);
            }
            if (_spectateLabel != null)
            {
                _spectateLabel.gameObject.SetActive(spectating);
                // The world's own word for a player (ADR-003 §9: Игрок →
                // Сборщик) and a HUMAN seat number, one-based: this is a label,
                // not an index.
                if (spectating) _spectateLabel.text = "НАБЛЮДЕНИЕ · СБОРЩИК " + (observed + 1);
            }

            // The backend has nothing to show only for a single frame ordering
            // edge case before the runner's own Awake has run; skip rendering
            // rather than throw. Task 43 renamed the question (`World == null`
            // -> `!Ready`), not the reason for asking it — and a networked
            // backend widens that window from one frame to "until the first
            // snapshot lands".
            if (!_runner.Ready) return;

            RenderSnapshot curr = _runner.Curr;
            var hero = _runner.Config.Hero;

            // F-8 fix: user-facing strings are Russian (ADR-003 §9 word list) — the
            // old "WAVE " placeholder predates the settled world vocabulary.
            int waveNumber = curr.Wave.WaveIndex;
            _waveText.text = "ВОЛНА " + waveNumber;

            // Task Т7 (bd `app-ggvz`, spec §3.10): rearm/decay the flash
            // timer off THIS frame's own number before folding
            // _previousWaveNumber forward — the seam needs the OLD value to
            // tell growth from "held", and turning it over a line early
            // would make every frame read as "held". The unseen sentinel
            // (-1, see the field's own doc) is read as "no growth yet" so
            // neither this class's first rendered frame nor the first frame
            // after a restart opens with a flash.
            int previousForCompare = _previousWaveNumber < 0 ? waveNumber : _previousWaveNumber;
            _waveAnnounceTimer = WaveAnnounceTimerAfter(previousForCompare, waveNumber,
                _waveAnnounceTimer, _gameFeel.WaveAnnounceSeconds, Time.unscaledDeltaTime);
            _previousWaveNumber = waveNumber;
            _waveText.color = _waveAnnounceTimer > 0f
                ? _gameFeel.WaveAnnounceFlashColor
                : _waveTextBaseColor;

            // A frame that says nothing about the watched seat moves nothing:
            // its `PlayerState` would be `default` — full-health-less zero at
            // the arena origin — and drawing that would report a death that
            // this frame never witnessed. The bar holds what it had, the same
            // answer `SimulationRunner.RenderObservedWorldPos` gives the camera.
            if (observed < 0 || observed >= curr.PlayerCount || !curr.PlayerKnown[observed]) return;

            PlayerState player = curr.Players[observed];
            _hpFill.fillAmount = player.Hp / hero.MaxHp;
            if (!spectating) UpdateStaminaBar(player.Stamina, hero.StaminaMax);

            // Т33. The phase line is match-wide and runs for a spectator too;
            // the other two are this collector's own and were switched off
            // above if he is not the one being watched.
            UpdatePhaseLine(in curr);
            if (!spectating)
            {
                // One copy of the built config for both, the same shape
                // `AimProvider.TryAimProxy` takes it in: `Config` is a
                // by-value property over a struct this size.
                SimConfig cfg = _runner.Config;
                UpdateAmmo(in player, in cfg);
                UpdateExtractRing(in player, in cfg);
            }
        }

        /// The magazine: how many rounds, how full the strip is, and whether
        /// the weapon has fallen back to emergency synthesis.
        ///
        /// EMERGENCY IS `Ammo == 0` AND NOT A FLAG, because that is the whole
        /// of the rule on the authoritative side too: `WeaponSystem.
        /// IntervalFor` reads `p.Ammo > 0 ? FireInterval : EmergencyFireInterval`
        /// and nothing else decides it. A separate bit here would be a second
        /// opinion about the same number.
        void UpdateAmmo(in PlayerState player, in SimConfig cfg)
        {
            int max = cfg.Weapon.AmmoMax;
            if (_ammoFill != null)
                _ammoFill.fillAmount = Mathf.Clamp01(player.Ammo / Mathf.Max(max, DivEps));
            if (_ammoText != null) _ammoText.text = $"БОЕЗАПАС {player.Ammo}/{max}";

            if (_pickupMaskTimer > 0f) _pickupMaskTimer -= Time.unscaledDeltaTime;
            bool emergency = player.Ammo == 0 && _pickupMaskTimer <= 0f;
            if (_emergencyLabel != null && _emergencyLabel.activeSelf != emergency)
                _emergencyLabel.SetActive(emergency);
        }

        /// Where the raid stands and how long it has — one of the three phase
        /// words spec §3.11 names, plus the countdown.
        ///
        /// THE COUNTDOWN IS ABSENT RATHER THAN ZERO WHEN NOBODY IS COUNTING.
        /// `MatchSecondsRemaining` carries the sentinel `MatchCountdown.None`
        /// on a world that has no countdown by construction — solo, where the
        /// duration lives in `NetConfig` and only `MatchServer` ever ends a
        /// match — and zero is a legal reading meaning "time is up". So the
        /// line drops the clock instead of printing 0:00 at a raid with all
        /// the time in the world.
        void UpdatePhaseLine(in RenderSnapshot curr)
        {
            if (_phaseText == null) return;

            string phase = PhaseWord(curr.Match.Phase, curr.DirectorAlive);
            _phaseText.text = curr.MatchSecondsRemaining == MatchCountdown.None
                ? phase
                : $"{phase} · {Clock(curr.MatchSecondsRemaining)}";
        }

        /// The world's own words (ADR-003 §9, spec §3.11).
        ///
        /// `DirectorAlive` SEPARATES THE TWO HALVES OF ONE PHASE. The raid
        /// stays in `DirectorActive` from the moment he wakes until the gate
        /// opens — which includes the stretch AFTER he is dead, while the
        /// sharing window runs — and naming the Director over his own corpse
        /// would announce the wrong danger. The bit is in the frame for exactly
        /// this reason (R-257: the gate bit is derived from the phase, this one
        /// cannot be).
        public static string PhaseWord(MatchPhase phase, bool directorAlive) => phase switch
        {
            MatchPhase.Farm => "ФАРМ",
            MatchPhase.DirectorActive => directorAlive ? "ДИРЕКТОР" : "ДИРЕКТОР ПАЛ",
            MatchPhase.GateOpen => "СТВОР ОТКРЫТ",
            MatchPhase.Ended => "ЗАХОД ОКОНЧЕН",
            _ => throw new System.ArgumentOutOfRangeException(nameof(phase), phase,
                "HudController.PhaseWord: every MatchPhase owns a word on the line."),
        };

        /// `m:ss`, floored — the reading a player checks against a wall clock.
        public static string Clock(int seconds)
        {
            int safe = Mathf.Max(seconds, 0);
            return $"{safe / 60}:{safe % 60:00}";
        }

        /// The wave line's flash timer, one tick forward (Task Т7, bd
        /// `app-ggvz`, spec §3.10 — the canon's "announce -> wave -> quiet
        /// window" cadence).
        /// `waveNumber` is world-wide and monotonic — a raid-wide difficulty
        /// step, not a per-ring counter — so GROWTH over `previousWaveNumber`
        /// is the only event this seam reacts to; a raid with more rings does
        /// not flash more often.
        ///
        /// On growth the timer is REARMED to `announceSeconds` outright, NOT
        /// QUEUED: a fresh flash always starts at the full value no matter
        /// what was already counting down, so back-to-back growth never
        /// stacks or accumulates. Otherwise it DECAYS by one frame,
        /// `Mathf.Max(0f, timerNow - deltaSeconds)` — never negative, so a
        /// spent flash reads as done rather than as a debt.
        public static float WaveAnnounceTimerAfter(int previousWaveNumber, int waveNumber,
            float timerNow, float announceSeconds, float deltaSeconds)
        {
            return waveNumber > previousWaveNumber
                ? announceSeconds
                : Mathf.Max(0f, timerNow - deltaSeconds);
        }

        /// The extraction channel, as a ring around the collector himself.
        ///
        /// `ExtractTimer` GROWS, unlike every other timer this project draws:
        /// `ExtractionSystem` adds `TickDt` to it each tick a body stands in an
        /// open exit and zeroes it the moment he steps out, so progress is
        /// elapsed-over-total rather than one minus remaining-over-total. Read
        /// off the timer's own home rather than assumed from the family
        /// (`LootTimer` counts the other way, and drawing this one like that
        /// would run the ring backwards).
        void UpdateExtractRing(in PlayerState player, in SimConfig cfg)
        {
            if (_extractRing == null) return;

            bool channeling = player.ExtractTimer > 0f;
            if (_extractRing.gameObject.activeSelf != channeling)
                _extractRing.gameObject.SetActive(channeling);
            if (!channeling) return;

            float total = Mathf.Max(cfg.Flow.ExtractChannelSeconds, DivEps);
            _extractRing.fillAmount = Mathf.Clamp01(player.ExtractTimer / total);
            if (_camera != null)
                _extractRing.rectTransform.position =
                    _camera.WorldToScreenPoint(_runner.RenderPlayerWorldPos);
        }

        /// `SimEventRouter`'s fan-out (П-1). A `StaminaDenied` attempt (dash or
        /// slide gated by an insufficient stamina pool, `SimEvent.Kind` doc)
        /// arms a short pulse, rendered by `UpdateStaminaBar` below in place of
        /// the ordinary threshold color for `StaminaDeniedPulseSeconds`.
        public void HandleEvent(in SimEvent e)
        {
            if (e.Kind == SimEventKind.StaminaDenied)
                _staminaDeniedTimer = _gameFeel.StaminaDeniedPulseSeconds;
            // Р275: the earliest honest news that the magazine is no longer
            // empty. Addressed to its collector by `PlayerIndex` — the field
            // that kind's own doc calls load-bearing — so a cell somebody else
            // picked up does not silence this client's mark.
            else if (e.Kind == SimEventKind.PickupTaken
                     && e.PlayerIndex == _runner.Curr.LocalPlayerIndex)
                _pickupMaskTimer = PickupMaskSeconds;
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
        void HandleWorldRestarted()
        {
            _staminaDeniedTimer = 0f;
            // Т33: and the pickup mask with it — a mask armed in the last
            // match would hide the fresh one's opening emergency.
            _pickupMaskTimer = 0f;
            // Т7 (bd `app-ggvz`): and the wave-announce flash with it — a
            // pulse charged in the raid that just ended must not bleed into
            // the new one's first frame. _previousWaveNumber resets to the
            // -1 unseen sentinel rather than 0 (a real WaveIndex under this
            // world's own numbering, see the field's own doc) alongside it,
            // so the new raid's first rendered frame reads as "no growth
            // yet" — the same protection this class's actual first frame
            // ever gets — instead of misreading the drop from the old raid's
            // wave N down to the new raid's opening number as GROWTH.
            _waveAnnounceTimer = 0f;
            _previousWaveNumber = -1;
            // AND THE COLOR WITH IT, not just the timer (Т7 review I-1). The
            // repaint below lives under the `!_runner.Ready` guard, and on the
            // networked backend `Ready` DROPS at the epoch change
            // (NetworkSimBackend.SyncMatchEpoch) and only comes back with the
            // first pair of the new match — so a flash still burning when a
            // raid ends would leave the dead raid's wave line sitting there in
            // flash color for that whole window, with no frame running to
            // repaint it. Same reason this method already blanks _phaseText
            // and _ammoText below rather than trusting the next frame.
            _waveText.color = _waveTextBaseColor;
            // Т35 (spec Р291, the restart reset list): the three surfaces Т33
            // added are taken down with it. THE RING IS THE ONE THAT MATTERS —
            // it is the only one that stays ON by itself: a collector who was
            // mid-extraction when the raid ended leaves an `ExtractTimer` on
            // screen that the next raid's first frames have no reason to
            // clear, so the new match would open with a stranger's channel
            // filling around the figure. The other two are overwritten every
            // frame from the new frame's own numbers and are cleared here for
            // the single frame before that happens.
            if (_extractRing != null) _extractRing.gameObject.SetActive(false);
            if (_emergencyLabel != null) _emergencyLabel.SetActive(false);
            if (_phaseText != null) _phaseText.text = string.Empty;
            if (_ammoText != null) _ammoText.text = string.Empty;
            if (_ammoFill != null) _ammoFill.fillAmount = 0f;
        }
    }
}
