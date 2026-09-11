#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.IO;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Presentation
{
    /// Dev-only debug overlay: started as a bare spawn-buttons stub (Task 21),
    /// grown here (Task 24 spec Interfaces + Приложение П-6/П-9) into the full
    /// overlay — fps, match/tick counters, every "silent loss" counter spec §3.7
    /// forbids dropping quietly (`DroppedEvents`, `MobSpawnsSkipped`/
    /// `ProjectileSpawnsSkipped`, the fixed-step accumulator's `DroppedTime`),
    /// all highlighted red once nonzero; the current `StateHash()` in hex plus an
    /// optional buffered tick→hash file log (П-9 — a diagnostic for determinism
    /// divergence, spec §3.3); a forced-seed restart field; and the original
    /// Task 21 spawn buttons. All IMGUI, stripped from production builds by the
    /// compile guard above (same contract as `PracticeTargets`/the two other
    /// Task 24 controllers' keyboard-shortcut branches).
    public sealed class DevOverlay : MonoBehaviour
    {
        const float SpawnDistance = 7f; // midpoint of the brief's "6-8m from player" range
        const float FpsUpdateInterval = 0.5f;
        const string TickHashLogFileName = "tick_hash.log";

        /// The lag gate's own threshold for the median reconciliation
        /// correction (Stage 2, spec §3.14 item 7, milestone В3): above this
        /// the correction line goes red. THE ONE NEW RED THRESHOLD THAT IS NOT
        /// "ABOVE ZERO", and it is not invented here — it is the number the
        /// gate is written in, quoted in `PlayerPredictionCore.
        /// FinishReconcile`'s own doc. A structural constant, not balance: the
        /// game plays no differently for it (CR 6).
        const float LagGateMedianMeters = 0.25f;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;
        /// app-461s T4 (plan deviation 1): the aim-ray gauge's `Δ` reading is
        /// the gap between the doll's muzzle socket and the simulated muzzle,
        /// and that fact lives only here — `AimProvider` carries no
        /// `ViewRegistry` reference to learn it from (`MuzzleGapMeters`'s own
        /// doc). A third reference, on top of the two above.
        [SerializeField] AimRayView _aimRayView;

        float _fpsAccum;
        int _fpsFrames;
        float _fps;

        /// This frame's network panel, taken ONCE in `Update` (Stage 2 Task
        /// 48). `OnGUI` runs several times per rendered frame — once per GUI
        /// event, per Unity's own execution-order page — so reading the seam
        /// from there would describe several different moments inside one
        /// picture, and would pay for the snapshot as many times.
        NetDiagnostics _net;

        /// Whether there is a network to describe at all. False in solo, where
        /// the whole section is omitted rather than drawn out of zeros: a zero
        /// is indistinguishable from a measurement, and six of the lines
        /// below are red above zero — a permanent all-clear on a diagnostic
        /// nobody is feeding is exactly what the dashes elsewhere on this
        /// panel exist to prevent.
        bool _hasNet;

        string _forcedSeedText = "";
        bool _logTickHash;
        StreamWriter _logWriter;

        // WorldRestarted is not a tick event (П-1 only restricts TicksFlushed to
        // its sole SimEventRouter subscriber) — direct subscription, same shape
        // as the deleted PracticeTargets' pattern.
        void OnEnable()
        {
            _runner.WorldRestarted += HandleWorldRestarted;
            // F-2 fix: OnDisable below unsubscribes TickAdvanced and closes the log
            // writer whenever the toggle was on, but never flips `_logTickHash` back
            // off — so a disable/enable cycle left the GUI toggle reading ON while
            // silently logging nothing (the exact "no silent loss" violation the
            // toggle's own OFF-branch doc already calls out for
            // HandleWorldRestarted). SetLogTickHash(true) both resubscribes and
            // reopens a fresh writer (the old one was already closed/disposed in
            // OnDisable, so plain resubscription alone would NRE on the first
            // LogTick call) — same call the GUI's own toggle-flip path uses.
            if (_logTickHash) SetLogTickHash(true);
        }

        void OnDisable()
        {
            _runner.WorldRestarted -= HandleWorldRestarted;
            if (_logTickHash) _runner.TickAdvanced -= LogTick;
            CloseLogWriter();
        }

        void OnApplicationQuit() => CloseLogWriter();

        void Update()
        {
            _fpsAccum += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsAccum >= FpsUpdateInterval)
            {
                _fps = _fpsFrames / _fpsAccum;
                _fpsAccum = 0f;
                _fpsFrames = 0;
            }

            // ONE SNAPSHOT PER RENDERED FRAME (Task 48) — see `_net`. Taken
            // whether or not the panel is `Ready`, because the answer is
            // answerable at any time by contract and a section that appeared
            // one frame late would be one frame of zeros.
            _hasNet = _runner != null && _runner.TryGetNetDiagnostics(out _net);
        }

        /// `SimulationRunner.TickAdvanced` subscriber (review round: replaces an
        /// earlier per-render-frame poll that only ever saw a catch-up batch's
        /// LAST tick — exactly the kind of hitch most likely to hide a
        /// determinism divergence). Subscribed only while the toggle is on
        /// (`SetLogTickHash`), so `TickAdvanced`'s `StateHash()` call stays
        /// guarded/free the rest of the time.
        void LogTick(int tick, ulong hash) => _logWriter.WriteLine($"{tick}\t{hash:X16}");

        void OnGUI()
        {
            // Task 43: `Ready` is the successor to the old `World == null` test.
            if (_runner == null || !_runner.Ready) return;

            // GROWN FOR THE NETWORK SECTION (Task 48), and the numbers are
            // counted rather than guessed. WIDTH: the longest line the section
            // prints is the tick trio at four-digit ticks (~46 characters) and
            // the two halves of the server-side line (~44 each), so at the
            // default skin's ~7 px per character 380 holds them where 300 cut
            // them. HEIGHT: the section adds a 6 px spacer, a header, and
            // fourteen rows occupying fifteen text lines (the server-side row
            // is deliberately wrapped in two; fix-round 1 corrected this row
            // count from thirteen to fourteen — six int counters below RTT/
            // Tick/Bytes/Corrections, not five) — sixteen text lines at the
            // default skin's ~20 px per laid-out line, i.e. 320 px exactly,
            // the same 320 this budget already carried before the count was
            // corrected — the row-count fix changes the prose, not the
            // number — so the slack the original 560 already carried for the
            // sixteen elements above it survives.
            // app-461s T4 (spec §3.8, fix-round 1): the aim-ray gauge adds
            // TWO rows, not one. WIDTH: the gauge's six readings measure
            // 72-74 characters on a single line at realistic worst-case
            // values (`cone 11.00°  hw 15.31  stop body(Head)`) — 504-518 px
            // at this panel's own ~7 px per character, against ~364 px of
            // usable width after padding (~52 characters), the same "300
            // cuts it" failure this comment already measured once for the
            // tick trio. Split in two by manual newline, exactly like the
            // server-side row below: `on/h/d/Δ` on the first line, `cone/hw/
            // stop` on the second — the wider half measures 40 characters,
            // comfortably inside the budget. HEIGHT: two rows at ~20 px per
            // laid-out line add 40 px, not 20 — 880 becomes 920. A clipped
            // panel is a panel that cannot be read, which is the whole point
            // of the thing, and doubly so for a gauge whose entire job is to
            // make a number readable that the picture alone does not carry
            // (owner lesson 716).
            // app-461s Н47: the gauge gained a CONDITIONAL seventh reading,
            // `cap` — the drawn length on the frames the screen ceiling
            // shortens the ray — and it lands on the FIRST of those two rows,
            // which changes the width arithmetic and not the height. WIDTH,
            // re-counted: ` cap 16.7` is 9 characters, taking the worst-case
            // first row from `AimRay: on  h 1.20  d 78.8  Δ 0.15` (34) to
            // `AimRay: on  h 1.20  d 78.8 cap 16.7  Δ 0.15` (43), or 44 if a
            // muzzle height ever reaches two digits — 301-308 px at this
            // panel's own ~7 px per character, against the same ~364 px of
            // usable width. So the FIRST row is the wider of the two now (43
            // against the second row's 40), and it still clears the budget by
            // roughly eight characters; the split into two rows stays
            // mandatory, since 43 + 40 back on one line is 83 characters.
            // HEIGHT: unchanged at 920. `cap` adds characters to an existing
            // row, never a row — and `GUILayout.Label` lays the text out
            // itself, so the only way it could cost a line is by wrapping,
            // which the width count above is exactly what rules out.
            GUILayout.BeginArea(new Rect(10f, 10f, 380f, 920f), GUI.skin.box);

            GUILayout.Label($"FPS: {_fps:F0}");
            GUILayout.Label($"Tick: {_runner.CurrentTick}");
            GUILayout.Label($"Mobs: {_runner.Curr.MobCount}  Projectiles: {_runner.Curr.ProjectileCount}");
            // Task 44d: DroppedEvents is NOT one of the match statistics below
            // and is drawn either way, HasMatchStats or not — on a networked
            // backend it reports what THIS client lost out of its own receive
            // queue, which is a real measurement of this process.
            DrawIntCounter("DroppedEvents", _runner.DroppedEvents);
            DrawFloatCounter("DroppedTime", _runner.AccumulatorDroppedTime);

            // Task 44d: a backend whose world counts these on another machine
            // reports HasMatchStats false, and the protocol carries no block
            // for them — so print a dash for the same reason StateHash below
            // prints one. A zero here is otherwise indistinguishable from a
            // count, and for the two red-above-zero counters it reads as a
            // permanent all-clear on a diagnostic nobody is feeding.
            if (_runner.HasMatchStats)
            {
                // Task 22 (A16): match counters, not "silent loss" — no red highlight.
                GUILayout.Label($"Slides: {_runner.Curr.Stats.SlidesUsed}  Headshots: {_runner.Curr.Stats.HeadshotKills}");
                // Stage 2 Task 5: these two moved to WorldStats (world-scoped, not
                // per-player) — Curr.Stats stays the local player's personal counters.
                DrawIntCounter("MobSpawnsSkipped", _runner.Curr.WorldStats.MobSpawnsSkipped);
                DrawIntCounter("ProjectileSpawnsSkipped", _runner.Curr.WorldStats.ProjectileSpawnsSkipped);
            }
            else
            {
                GUILayout.Label("Slides: —  Headshots: —");
                GUILayout.Label("MobSpawnsSkipped: —");
                GUILayout.Label("ProjectileSpawnsSkipped: —");
            }

            GUILayout.Label($"Seed: {_runner.Seed}");
            // Task 43: a backend for which the hash is a server-side quantity
            // (Task 44) reports HasStateHash false — print a dash rather than a
            // number that would look computed and be wrong.
            GUILayout.Label(_runner.HasStateHash
                ? $"StateHash: {_runner.StateHash:X16}"
                : "StateHash: —");

            GUILayout.Label(AimRayLine());

            DrawNetworkSection();

            GUILayout.Space(6f);
            GUILayout.Label("Forced seed:");
            GUILayout.BeginHorizontal();
            _forcedSeedText = GUILayout.TextField(_forcedSeedText, GUILayout.Width(180f));
            if (GUILayout.Button("Restart") && long.TryParse(_forcedSeedText, out long forcedSeed))
                _runner.Restart(forcedSeed);
            GUILayout.EndHorizontal();

            bool newLogTickHash = GUILayout.Toggle(_logTickHash, "Log tick→hash to file");
            if (newLogTickHash != _logTickHash) SetLogTickHash(newLogTickHash);

            GUILayout.Space(6f);
            // Task 43 (CR 3): hidden where the backend cannot spawn into an
            // authoritative world it does not own — asked before drawing rather
            // than drawing a button whose only possible answer is a refusal.
            if (_runner.CanDevSpawnMob)
            {
                if (GUILayout.Button("Spawn Chaser")) Spawn(MobType.Chaser);
                if (GUILayout.Button("Spawn Gunner")) Spawn(MobType.Gunner);
            }

            GUILayout.EndArea();
        }

        /// THE NETWORK INSTRUMENT PANEL (Stage 2 Task 48, plan Ф9
        /// :2100-2107) — the thing milestone В3's lag gate is read off and
        /// milestone В1's numbers are taken from. Thirteen lines, drawn from
        /// the one snapshot `Update` took, and drawn only where there is a
        /// network: `_hasNet` is false in solo and the whole section is
        /// absent, not empty.
        ///
        /// RED MEANS ILL HEALTH AND NOTHING ELSE, which is why most of these
        /// lines are never red. SEVEN of the thirteen can go red. Six of them
        /// are counters red above zero, each something that should not happen
        /// on a healthy connection at Critical Rule 7's own 80 ms / 5% — none
        /// of them is caused by packet LOSS, which is ordinary and which the
        /// interpolation buffer exists to absorb: they are reordering,
        /// duplication, a frame thrown away unshown, a frame that arrived with
        /// entities missing, a predicted round the server never confirmed, and
        /// the render clock's own snap count. That last one shares the `Clock:`
        /// line with the slew, which is the clock working as designed and never
        /// reddens — a snap is a visible jump in the moment being shown, so
        /// only the count carries the verdict. The seventh red is the
        /// correction median past the gate's own 0.25 m — the one threshold
        /// that is not "above zero". Everything else — RTT, the tick trio,
        /// the byte rate, the queue occupancies, the simulator's settings —
        /// is a reading rather than a verdict, and coloring it would be the
        /// disease this task was opened to cure (`DroppedTime`, red from the
        /// first minute of every session and therefore meaningless).
        ///
        /// `DroppedSnapshots` HAD THE SAME DISEASE AND KEPT THE SAME CURE (bd
        /// `app-0wm`), WHICH IS WHY THE LIST ABOVE NOW SAYS "a frame thrown
        /// away unshown" WHERE IT USED TO SAY "a ring that overflowed". Those
        /// are not the same event: a full ring is ordinary here — capacity is
        /// `InterpBufferTicks + 2` and the residents span it whenever the render
        /// clock sits its full `InterpBufferTicks` behind the newest tick — so
        /// counting every eviction made the number a reading of the ring's
        /// geometry, red on a healthy link at 16.5 a second (1371 in 83
        /// seconds), believed by both the owner and the coordinator. The cure
        /// went where `app-c3m`'s went, into the class that owns the counter
        /// (`SnapshotQueue.EvictionWasNeverShown`), and nothing in this file
        /// changed but this wording: the line is still red above zero, and
        /// above zero now names a frame that was actually lost.
        void DrawNetworkSection()
        {
            if (!_hasNet) return;

            GUILayout.Space(6f);
            GUILayout.Label("— Network —");

            // FishNet's own caveat, printed beside its own number: the package
            // documents RoundTripTime as INCLUDING the latency of the tick
            // rate (TimeManager.cs:104), so this is not a wire ping and the
            // label must not let it be read as one.
            GUILayout.Label($"RTT: {_net.RoundTripMs} ms (incl tick rate)");

            // A DASH PER FIELD, NOT ONE FLAG FOR THE TRIO (fix-round 1, F-2).
            // The two halves of this line start at different moments: the ring
            // has a newest tick from the first frame it commits, while the
            // render clock does not run until it has seen a SECOND DISTINCT
            // tick — so between the two, `RenderTick` is a zero and the
            // difference printed beside it was the size of the server's tick
            // counter rather than of any lag. Each number is printed only
            // where it is a measurement, and `behind` only where BOTH are, the
            // same discipline `StateHash` and `HasMatchStats` above already
            // use.
            string renderTick = _net.HasRenderTick ? _net.RenderTick.ToString() : "—";
            string serverTick = _net.HasNewestServerTick ? _net.NewestServerTick.ToString() : "—";
            string behind = _net.HasRenderTick && _net.HasNewestServerTick
                ? (_net.NewestServerTick - _net.RenderTick).ToString()
                : "—";
            GUILayout.Label($"Tick: render {renderTick}  server {serverTick}  behind {behind}");

            // Up is a DASH, not a zero, and permanently: nothing on this side
            // of the wire measures outgoing bytes (see
            // `NetworkSimBackend.TryGetNetDiagnostics` for the whole finding —
            // FishNet's own traffic statistics are behind a serialized field
            // with no setter). A zero here would read as a silent client.
            GUILayout.Label($"Bytes: down {_net.BytesDownPerSecond / 1024f:F1} KB/s  up —");

            // The lag gate's own line. A dash while nothing has reconciled
            // yet, because zero is a legitimate median — a perfect prediction
            // corrects by exactly nothing — and cannot double as "no samples".
            if (_net.CorrectionCount > 0)
            {
                DrawLine($"Corrections: {_net.CorrectionCount}  "
                    + $"median {_net.CorrectionMedianMeters:F3} m",
                    _net.CorrectionMedianMeters > LagGateMedianMeters);
            }
            else
            {
                GUILayout.Label("Corrections: 0  median —");
            }

            DrawIntCounter("StaleSnapshots", _net.StaleSnapshots);
            DrawIntCounter("DuplicateSnapshots", _net.DuplicateSnapshots);
            DrawIntCounter("DroppedSnapshots", _net.DroppedSnapshots);
            DrawIntCounter("FramesMissingEntities", _net.FramesMissingEntities);
            DrawIntCounter("UnconfirmedGhosts", _net.UnconfirmedGhosts);
            DrawIntCounter("DroppedPredictedShots", _net.DroppedPredictedShots);

            // Occupancy, not loss — never red. A snapshot ring sitting at zero
            // is a buffer absorbing nothing, which is the unhealthy reading of
            // this line, and a full one is the buffer doing its job.
            GUILayout.Label($"Queues: snap {_net.SnapshotQueueCount}/{_net.SnapshotQueueDepth}  "
                + $"events {_net.EventQueueCount}/{_net.EventQueueCapacity}");

            // A snap is a visible jump in the moment being shown, so the count
            // is red above zero; the slew is the clock working as designed and
            // is never red.
            string slew = _net.ClockSlewSign > 0 ? "catching up"
                : _net.ClockSlewSign < 0 ? "easing back"
                : "steady";
            DrawLine($"Clock: {slew}  snaps {_net.ClockSnaps}", _net.ClockSnaps > 0);

            // Not a verdict either way — but the first thing a measurement
            // taken at milestone В1 has to state, because Critical Rule 7's
            // "80 ms RTT / 5% loss" is what the numbers above are supposed to
            // have been read under. The loss figure is PER DIRECTION, as the
            // asset states it (5 => ~9.75% round trip), and the label says so
            // rather than leaving the reader to halve or double it.
            GUILayout.Label(_net.LatencySimActive
                ? $"LatencySim: {_net.LatencySimRttMs} ms RTT  "
                    + $"{_net.LatencySimLossPercent:F1}% loss/dir"
                : "LatencySim: off");

            // NAMED RATHER THAN DRAWN AS FOUR ZEROS. All four are the
            // SERVER's per-connection counters; this process holds a NetStats
            // on which nothing ever writes them, so a line each would be four
            // permanent all-clears on diagnostics nobody feeds — the exact
            // failure `HasMatchStats` was added to prevent. The ones this side
            // can honestly answer are above: FramesMissingEntities is what a
            // client sees of DroppedEntities.
            GUILayout.Label("Server-side (not on client): InputStarved,\n"
                + "  InputOverwritten, DroppedEntities, EdgeRejected");
        }

        /// app-461s T4 (spec §3.8, owner lesson 716): the aim-ray gauge —
        /// height, reach, doll/muzzle gap, cone angle, notch half-width and
        /// stop, none of which the picture alone lets the owner measure. Six
        /// readings off the hip line `AimProvider` caches every `Ready` frame
        /// regardless of `AimHeld` (`CurrentHipLine`'s own doc), plus the one
        /// fact only the view knows (`Δ`, `AimRayView.MuzzleGapMeters`'s own
        /// doc — plan deviation 1: no reference to `ViewRegistry` exists on
        /// `AimProvider` to learn it from otherwise).
        /// ⚠ TWO LINES, MANUAL `\n`, SAME SHAPE AS THE SERVER-SIDE ROW BELOW
        /// (fix-round 1): at realistic worst-case values the six readings run
        /// 72-74 characters on one line, wider than this panel's own budget
        /// (see the width arithmetic on `GUILayout.BeginArea` above) —
        /// `on/h/d/Δ` first, `cone/hw/stop` second.
        /// ⚠ SEVEN READINGS SINCE app-461s Н47, NOT SIX, AND THE SEVENTH IS
        /// CONDITIONAL: `cap` joins the first line only on the frames the
        /// screen ceiling actually shortens the drawn ray. It is the only
        /// witness this task has — `AimRayView` is a `MonoBehaviour`, so
        /// EditMode cannot reach the arithmetic at all, and the picture alone
        /// cannot tell a ray cut by the screen from a ray that simply ran into
        /// something. The width arithmetic above was re-counted for it.
        string AimRayLine()
        {
            // A DIFFERENT REASON THAN THE ONE BELOW (fix-round 1, Minor 1):
            // an unwired scene (no `Apply` since this task) would otherwise
            // print the exact same dash as aiming down sights, which reads
            // as "aiming" on a scene that has never fired a shot.
            if (_aimProvider == null || _aimRayView == null) return "AimRay: not wired";
            // THE GAUGE IS A HIP-FIRE INSTRUMENT (spec §3.8). While the right
            // button is held, the drawn ray answers the AIMED question
            // instead — far end and color both switch in
            // `AimRayView.LateUpdate` — while `Δ` keeps measuring the HIP
            // muzzle regardless, the wrong pair, off by up to 0.18 m at a
            // high cover next to a collector (app-461s T2 review round). And
            // it is not `Δ` alone: the notches go dark too (`AimRayView`'s
            // own `drawNotches` predicate), so every one of the six readings
            // would describe a line that is not the one on screen. A single
            // dash for the whole reading is the honest answer here, the same
            // shape this file already uses for `StateHash: —` on a networked
            // backend.
            if (_runner.LastFrameInput.AimHeld) return "AimRay: —";

            AimLineSolution line = _aimProvider.CurrentHipLine;
            // READ OFF THE VIEW, NOT RECOMPUTED: `AimActive && !AimHeld`
            // would say "on" on frames with no doll at all, where the view
            // itself has already gated the ray dark — `MuzzleGapMeters`
            // already resets to NaN on every one of those exits (its own
            // doc), so testing it here says exactly what is on screen.
            bool on = !float.IsNaN(_aimRayView.MuzzleGapMeters);
            // A DASH FOR `Δ` ALONE HERE, NOT FOR THE WHOLE LINE (fix-round 1,
            // Important): unlike the `AimHeld` case above, the other five
            // readings stay valid on an `off` frame — `AimProvider` writes
            // its cache every `Ready` frame regardless of whether the view
            // found a doll to draw from — so only the one field the view
            // itself reports as NaN gets the dash, per `MuzzleGapMeters`'s
            // own doc ("the dev readout prints a dash for it").
            string gap = on ? $"{_aimRayView.MuzzleGapMeters:F2}" : "—";
            // GUARD MANDATORY: `NotchDistance` is legitimately zero (the
            // muzzle sits inside a body, or the cursor sits on the muzzle
            // itself), and `hw / 0` is NaN right on the one readout this
            // task exists to give.
            string cone = line.NotchDistance <= 1e-4f
                ? "—"
                : $"{math.degrees(math.atan(line.NotchHalfWidth / line.NotchDistance)):F2}°";
            // Lowercase `body(...)`, never `Body(...)` or `hit(...)`:
            // `AimStop.Body` ("stopped by a body") and `HitZone.Body`
            // ("torso") are different ideas sharing a name, and `hit(...)`
            // would borrow the authoritative-hit vocabulary (A1/A7) for a
            // line that hits nothing.
            string stop = line.Stop == AimStop.Body ? $"body({line.Zone})" : line.Stop.ToString();
            // app-461s (owner decision Н47): `d` is still the SIMULATION's
            // answer — the reach the line was solved to — and the screen
            // ceiling never touches it (`AimLine` knows nothing of a camera).
            // What the ceiling changes is how much of that is DRAWN, so the
            // fact gets its own reading, taken off the view the same way `Δ`
            // is (`AimRayView.DrawnLengthMeters`).
            // ⚠ PRINTED ONLY WHILE IT ACTUALLY BITES, which is what makes it
            // readable as an event rather than as decoration: an unwired
            // camera, an ortho projection, a cursor closer than two thirds of
            // the way to the screen edge — all of them leave the drawn length
            // equal to `d`, and all of them print nothing here. The NaN of a
            // frame that draws no hip ray fails this comparison too, so the
            // `off` line stays exactly as wide as it was.
            // ⚠ THE 1 cm SLACK IS NOT COSMETIC: the drawn length is rebuilt
            // through a world-space round trip, so an uncapped frame can miss
            // `line.Length` by an ulp or two and would otherwise flicker `cap`
            // on and off at a distance where nothing is being cut at all.
            float drawn = _aimRayView.DrawnLengthMeters;
            string cap = drawn < line.Length - 0.01f ? $" cap {drawn:F1}" : string.Empty;

            return $"AimRay: {(on ? "on" : "off")}  h {line.Height:F2}  d {line.Length:F1}{cap}  Δ {gap}\n" +
                $"  cone {cone}  hw {line.NotchHalfWidth:F2}  stop {stop}";
        }

        static void DrawIntCounter(string label, int value)
            => DrawLine($"{label}: {value}", value > 0);

        static void DrawFloatCounter(string label, float value)
            => DrawLine($"{label}: {value:F3}", value > 0f);

        /// One line of the panel, red when it is reporting ill health. The
        /// single place `GUI.color` is touched, so no line can leave the tint
        /// on for the line after it — the failure the two counter helpers used
        /// to each be one copy of.
        static void DrawLine(string text, bool unhealthy)
        {
            Color prev = GUI.color;
            if (unhealthy) GUI.color = Color.red;
            GUILayout.Label(text);
            GUI.color = prev;
        }

        void Spawn(MobType type)
        {
            float2 playerPos = _runner.Curr.Player.Pos;
            float2 aimPos = _aimProvider != null
                ? _aimProvider.CurrentAimSimPos
                : playerPos + new float2(1f, 0f);
            float2 dir = math.normalizesafe(aimPos - playerPos, new float2(1f, 0f));
            _runner.DevSpawnMob(type, playerPos + dir * SpawnDistance);
        }

        void SetLogTickHash(bool enabled)
        {
            _logTickHash = enabled;
            if (enabled)
            {
                string path = Path.Combine(Application.persistentDataPath, TickHashLogFileName);
                _logWriter = new StreamWriter(path, append: false) { AutoFlush = false };
                _runner.TickAdvanced += LogTick;
            }
            else
            {
                _runner.TickAdvanced -= LogTick;
                CloseLogWriter();
            }
        }

        void CloseLogWriter()
        {
            if (_logWriter == null) return;
            _logWriter.Flush();
            _logWriter.Close();
            _logWriter = null;
        }

        void HandleWorldRestarted()
        {
            // Turned off (not just the writer closed) rather than silently left
            // "on" with a null writer doing nothing — spec §3.7's "no silent
            // loss" principle applies here too: a toggle that reads ON but stops
            // logging would be exactly that kind of silent, invisible failure.
            if (_logTickHash) _runner.TickAdvanced -= LogTick;
            CloseLogWriter();
            _logTickHash = false;
        }
    }
}
#endif
