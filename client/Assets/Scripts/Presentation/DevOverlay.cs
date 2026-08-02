#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.IO;
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

        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;

        float _fpsAccum;
        int _fpsFrames;
        float _fps;

        string _forcedSeedText = "";
        bool _logTickHash;
        StreamWriter _logWriter;
        int _lastLoggedTick = -1;

        // WorldRestarted is not a tick event (П-1 only restricts TicksFlushed to
        // its sole SimEventRouter subscriber) — direct subscription, same shape
        // as the deleted PracticeTargets' pattern.
        void OnEnable() => _runner.WorldRestarted += HandleWorldRestarted;

        void OnDisable()
        {
            _runner.WorldRestarted -= HandleWorldRestarted;
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

            // Polled here rather than event-driven — П-1 forbids a new
            // TicksFlushed subscriber, and this only needs to notice "the tick
            // counter moved since last render frame", not react to individual
            // sim events. During a multi-tick catch-up flush this logs only the
            // LAST tick of that batch: `SimulationWorld.StateHash()` reflects
            // the world's CURRENT state only, there is no per-tick history to
            // read back after the fact. Accepted scope for this dev diagnostic
            // (П-9 — "заложи в код", full smoke coverage lands at the milestone
            // playtest) — not a complete per-tick trace.
            if (_logTickHash && _logWriter != null && _runner.World != null)
            {
                int tick = _runner.World.CurrentTick;
                if (tick != _lastLoggedTick)
                {
                    _lastLoggedTick = tick;
                    _logWriter.WriteLine($"{tick}\t{_runner.World.StateHash():X16}");
                }
            }
        }

        void OnGUI()
        {
            if (_runner == null || _runner.World == null) return;

            GUILayout.BeginArea(new Rect(10f, 10f, 300f, 560f), GUI.skin.box);

            GUILayout.Label($"FPS: {_fps:F0}");
            GUILayout.Label($"Tick: {_runner.World.CurrentTick}");
            GUILayout.Label($"Mobs: {_runner.Curr.MobCount}  Projectiles: {_runner.Curr.ProjectileCount}");

            DrawIntCounter("DroppedEvents", _runner.World.DroppedEvents);
            DrawIntCounter("MobSpawnsSkipped", _runner.Curr.Stats.MobSpawnsSkipped);
            DrawIntCounter("ProjectileSpawnsSkipped", _runner.Curr.Stats.ProjectileSpawnsSkipped);
            DrawFloatCounter("DroppedTime", _runner.AccumulatorDroppedTime);

            GUILayout.Label($"Seed: {_runner.Seed}");
            GUILayout.Label($"StateHash: {_runner.World.StateHash():X16}");

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
            if (GUILayout.Button("Spawn Chaser")) Spawn(MobType.Chaser);
            if (GUILayout.Button("Spawn Gunner")) Spawn(MobType.Gunner);

            GUILayout.EndArea();
        }

        static void DrawIntCounter(string label, int value)
        {
            Color prev = GUI.color;
            if (value > 0) GUI.color = Color.red;
            GUILayout.Label($"{label}: {value}");
            GUI.color = prev;
        }

        static void DrawFloatCounter(string label, float value)
        {
            Color prev = GUI.color;
            if (value > 0f) GUI.color = Color.red;
            GUILayout.Label($"{label}: {value:F3}");
            GUI.color = prev;
        }

        void Spawn(MobType type)
        {
            float2 playerPos = _runner.Curr.Player.Pos;
            float2 aimPos = _aimProvider != null
                ? _aimProvider.CurrentAimSimPos
                : playerPos + new float2(1f, 0f);
            float2 dir = math.normalizesafe(aimPos - playerPos, new float2(1f, 0f));
            _runner.World.DevSpawnMob(type, playerPos + dir * SpawnDistance);
        }

        void SetLogTickHash(bool enabled)
        {
            _logTickHash = enabled;
            if (enabled)
            {
                string path = Path.Combine(Application.persistentDataPath, TickHashLogFileName);
                _logWriter = new StreamWriter(path, append: false) { AutoFlush = false };
                _lastLoggedTick = -1;
            }
            else
            {
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
            CloseLogWriter();
            _logTickHash = false;
            _lastLoggedTick = -1;
        }
    }
}
#endif
