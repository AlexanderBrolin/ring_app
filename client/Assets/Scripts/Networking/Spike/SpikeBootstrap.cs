// TEMPORARY (T3 -> T30): the whole Networking/Spike folder plus the
// Assets/Scenes/NetSpike.unity scene and Assets/Prefabs/SpikePlayer.prefab are
// deleted in T30, when PlayerPrediction.Step + PlayerNetworkController (T34)
// replace them. Corner-cutting is sanctioned HERE ONLY (stage-2 plan, task T3).
using System;
using FishNet.Managing;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Networking.Spike
{
    /// Runtime harness of the T3 spike: builds the SimConfig the spike predicts
    /// with, starts the connection, feeds the owner scripted input, and prints the
    /// four observations plan step 3 asks for.
    ///
    /// Input is SCRIPTED, not sampled: Ring.Networking's asmdef references are
    /// pinned by the plan to Ring.Simulation / Ring.Data / FishNet.Runtime /
    /// Unity.Mathematics, the project is Input-System-only (ProjectSettings
    /// activeInputHandler: 1, so UnityEngine.Input throws), and Presentation must
    /// never be referenced from here (spec §3.1 — the dependency runs the other
    /// way; T34 pins the same rule with SetPendingInput). A deterministic path is
    /// also the better instrument: the rubber band is measured against a route the
    /// observer already knows.
    ///
    /// Start mode: the serialized field, overridable from the command line so one
    /// build can play either side —
    ///   -ringSpikeHost | -ringSpikeServer | -ringSpikeClient [address]
    /// The latency simulator is NOT touched here: plan step 3 has the owner enable
    /// it by hand on NetworkManager's TransportManager (the permanent code is T33).
    public sealed class SpikeBootstrap : MonoBehaviour
    {
        public enum StartMode : byte { Manual = 0, Host = 1, Client = 2, ServerOnly = 3 }

        [SerializeField] NetworkManager _networkManager;
        [SerializeField] StartMode _startMode = StartMode.Host;
        [SerializeField] string _clientAddress = "127.0.0.1";

        [Header("Balance (same assets Main.unity feeds SimulationRunner)")]
        [SerializeField] HeroConfig _hero;
        [SerializeField] WeaponConfig _weapon;
        [SerializeField] MobConfig _chaser;
        [SerializeField] MobConfig _gunner;
        [SerializeField] WaveConfig _wave;
        [SerializeField] ArenaConfig _arena;

        [Header("Scripted input")]
        // Angular rate of the Lissajous path below, radians per second.
        [SerializeField] float _patternRadPerSec = 1.2f;
        // Seconds between scripted dash requests; 0 disables them. A dash is the
        // harshest prediction case in the game (Hero.DashSpeed is the fastest thing
        // a player body does), so the rubber band shows up there first.
        [SerializeField] float _dashPeriodSeconds = 2f;
        [SerializeField] bool _showOverlay = true;

        static SimConfig _sharedConfig;
        static bool _sharedConfigReady;

        float _lastDashTime;

        /// Spike shortcut (T3 only): SpikePlayerController is spawned by FishNet's
        /// own PlayerSpawner, so it cannot be handed the config through the
        /// inspector. Production gets its config through MatchConfig (T38).
        public static bool TryGetSharedConfig(out SimConfig config)
        {
            config = _sharedConfig;
            return _sharedConfigReady;
        }

        void Awake()
        {
            if (_hero == null || _weapon == null || _chaser == null
                || _gunner == null || _wave == null || _arena == null)
            {
                Debug.LogError("SpikeBootstrap: balance assets are unwired — " +
                    "run Ring/Bootstrap/Net Spike Scene.");
                return;
            }

            // Reuses the one converter/validator the whole project uses — the spike
            // predicts with the REAL numbers, otherwise its correction figures
            // would describe a hero that does not exist.
            _sharedConfig = SimConfigBuilder.Build(_hero, _weapon, _chaser, _gunner, _wave, _arena);
            _sharedConfigReady = true;
            _lastDashTime = -_dashPeriodSeconds;
        }

        void OnDestroy()
        {
            _sharedConfigReady = false;
        }

        void Start()
        {
            if (_networkManager == null)
            {
                Debug.LogError("SpikeBootstrap: NetworkManager is unwired.");
                return;
            }

            switch (ResolveStartMode())
            {
                case StartMode.Host:
                    _networkManager.ServerManager.StartConnection();
                    _networkManager.ClientManager.StartConnection();
                    break;
                case StartMode.ServerOnly:
                    _networkManager.ServerManager.StartConnection();
                    break;
                case StartMode.Client:
                    _networkManager.ClientManager.StartConnection(_clientAddress);
                    break;
                case StartMode.Manual:
                    break;
            }
        }

        void Update()
        {
            SpikePlayerController owner = SpikePlayerController.LocalOwner;
            if (owner == null) return;
            owner.SetPendingInput(BuildScriptedInput());
        }

        StartMode ResolveStartMode()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-ringSpikeHost":
                        return StartMode.Host;
                    case "-ringSpikeServer":
                        return StartMode.ServerOnly;
                    case "-ringSpikeClient":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                            _clientAddress = args[i + 1];
                        return StartMode.Client;
                }
            }
            return _startMode;
        }

        SimInput BuildScriptedInput()
        {
            // Lissajous 2:1, normalized to unit length: the body turns constantly,
            // so a mispredicted tick opens a LATERAL gap instead of hiding along
            // the travel line. Normalized because the spike skips sanitization
            // (SimInputSanitizer is T6) and the world would clamp |MoveDir| to 1.
            float t = Time.time * _patternRadPerSec;
            float2 move = math.normalizesafe(
                new float2(math.cos(t), math.sin(2f * t)), new float2(1f, 0f));

            bool dash = false;
            if (_dashPeriodSeconds > 0f && Time.time - _lastDashTime >= _dashPeriodSeconds)
            {
                _lastDashTime = Time.time;
                dash = true;
            }

            return new SimInput
            {
                MoveDir = move,
                // Any finite point ahead of the body: the movement tick reads
                // AimPoint only as the dash-direction fallback when MoveDir is zero.
                AimPoint = move * 5f,
                AimHeight = _sharedConfig.Hero.MuzzleHeight,
                DashRequested = dash
            };
        }

        void OnGUI()
        {
            if (!_showOverlay) return;

            GUI.Label(new Rect(12f, 8f, 900f, 20f), "RING net spike (T3, temporary)");
            if (_networkManager == null) return;

            bool server = _networkManager.ServerManager.Started;
            bool client = _networkManager.ClientManager.Started;
            string role = server && client ? "host" : server ? "server" : client ? "client" : "offline";

            var sim = _networkManager.TransportManager.LatencySimulator;
            string simText = sim.GetEnabled()
                ? $"{sim.GetLatency()} ms one way / {sim.GetPacketLost() * 100d:0.#}% loss"
                : "OFF — enable it on NetworkManager/TransportManager";

            GUI.Label(new Rect(12f, 28f, 900f, 20f),
                $"role {role}   localTick {_networkManager.TimeManager.LocalTick}   " +
                $"tickRate {_networkManager.TimeManager.TickRate}   " +
                $"RTT {_networkManager.TimeManager.RoundTripTime} ms   sim: {simText}");

            SpikePlayerController c = SpikePlayerController.LocalOwner;
            if (c == null)
            {
                GUI.Label(new Rect(12f, 48f, 900f, 20f), "waiting for the owned player object...");
                return;
            }

            GUI.Label(new Rect(12f, 48f, 900f, 20f),
                $"(a) correction  last {c.CorrectionLast:0.000} m   median {c.CorrectionMedian():0.000} m   " +
                $"max {c.CorrectionMax:0.000} m   samples {c.CorrectionSampleCount}");
            GUI.Label(new Rect(12f, 68f, 900f, 20f),
                $"(b) ReplicateState  Ticked|Created {c.TickedCreated}   Ticked(no data) {c.TickedNonCreated}   " +
                $"Replayed|Created {c.ReplayedCreated}   Future {c.FutureStates}   " +
                $"Invalid {c.InvalidStates}   other {c.OtherStates}   last {c.LastState}");
            GUI.Label(new Rect(12f, 88f, 900f, 20f),
                $"(c) replicates per server tick  max {c.MaxReplicatesPerServerTick}   " +
                $">1 on {c.MultiReplicateTicks} ticks   starved ticks {c.ServerStarvedTicks}");
            GUI.Label(new Rect(12f, 108f, 900f, 20f),
                $"(d) reconcile tick {c.LastReconcileTick}   localTick then {c.LastReconcileLocalTick}   " +
                $"delta {(long)c.LastReconcileLocalTick - c.LastReconcileTick}   count {c.ReconcileCount}");
        }
    }
}
