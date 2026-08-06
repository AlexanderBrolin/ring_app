// TEMPORARY (T3 -> T30): the whole Networking/Spike folder plus the
// Assets/Scenes/NetSpike.unity scene and Assets/Prefabs/SpikePlayer.prefab are
// deleted in T30, when PlayerPrediction.Step + PlayerNetworkController (T34)
// replace them. Corner-cutting is sanctioned HERE ONLY (stage-2 plan, task T3).
using FishNet.Managing;
using Ring.Data;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Networking.Spike
{
    /// Runtime harness of the T3 spike: builds the SimConfig the spike predicts
    /// with, starts the connection, feeds the owner scripted input, and prints the
    /// four observations plan step 3 asks for — each under the role it is actually
    /// readable from (see SpikePlayerController's class comment).
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
    /// The route is a function of TimeManager.LocalTick, not of Time.time, and it
    /// is pushed from OnPreTick — exactly once per tick, whatever the frame rate.
    /// On a Time.time base the same wall-clock second covered a different arc at a
    /// different fps and the dash phases landed on different ticks, so the two
    /// participants of the manual check walked two different routes and the
    /// promised reproducibility did not exist.
    ///
    /// Start mode is the serialized field and nothing else: the scene is
    /// deliberately absent from EditorBuildSettings, so no build ever loads it,
    /// and inside the editor Environment.GetCommandLineArgs() returns the EDITOR's
    /// own arguments — a command-line override here would have been unreachable
    /// code by construction. The second participant is a second Unity editor
    /// opened on a separate checkout with this field set to Client (plan step 3).
    /// The latency simulator is NOT touched here: plan step 3 has the owner enable
    /// it by hand on NetworkManager's TransportManager (the permanent code is T33).
    public sealed class SpikeBootstrap : MonoBehaviour
    {
        public enum StartMode : byte { Host = 1, Client = 2 }

        /// Seconds between scripted dash requests; 0 disables them. A dash is the
        /// harshest prediction case in the game (Hero.DashSpeed is the fastest
        /// thing a player body does), so the rubber band shows up there first.
        /// The value sits just above the sustainable cadence of the Boost economy
        /// — StaminaRegenDelay 0.8 s plus DashStaminaCost / StaminaRegenPerSec =
        /// 40 / 20 = 2 s, i.e. 2.8 s — so every scripted dash actually executes.
        /// Set the field lower by hand and part of the requests come back denied:
        /// that is a legitimate scenario (the denial is symmetric — both copies
        /// run the same movement tick over the same stamina), it simply spends
        /// ticks NOT dashing and makes the harshest case rarer.
        /// SpikeSceneBootstrap pins the serialized field to this number, the same
        /// way it pins the tick rate: a scenario parameter of a measurement, not a
        /// taste knob.
        public const float DefaultDashPeriodSeconds = 3f;

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
        // Stage 2 Task 22 (coordinator decision, task-22-brief.md): seventh
        // SimConfigBuilder.Build() parameter, wired like the six above — the
        // spike is deleted whole in Task 30, but it has to compile and pass
        // Build()'s validation for every task in between, so it cannot pass
        // null here.
        [SerializeField] VisibilityConfig _visibility;

        [Header("Scripted input")]
        // Angular rate of the path below, radians per second of tick time.
        [SerializeField] float _patternRadPerSec = 1.2f;
        [SerializeField] float _dashPeriodSeconds = DefaultDashPeriodSeconds;
        [SerializeField] bool _showOverlay = true;

        static SimConfig _sharedConfig;
        static bool _sharedConfigReady;

        bool _subscribed;
        uint _lastDashTick;
        bool _hasDashed;

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
                || _gunner == null || _wave == null || _arena == null || _visibility == null)
            {
                Debug.LogError("SpikeBootstrap: balance assets are unwired — " +
                    "run Ring/Bootstrap/Net Spike Scene.");
                return;
            }

            // Reuses the one converter/validator the whole project uses — the spike
            // predicts with the REAL numbers, otherwise its correction figures
            // would describe a hero that does not exist.
            _sharedConfig = SimConfigBuilder.Build(_hero, _weapon, _chaser, _gunner, _wave, _arena, _visibility);
            _sharedConfigReady = true;
        }

        void OnDestroy()
        {
            _sharedConfigReady = false;
            if (_subscribed && _networkManager != null && _networkManager.TimeManager != null)
                _networkManager.TimeManager.OnPreTick -= TimeManager_OnPreTick;
            _subscribed = false;
        }

        void Start()
        {
            if (_networkManager == null)
            {
                Debug.LogError("SpikeBootstrap: NetworkManager is unwired.");
                return;
            }

            // OnPreTick, not Update: it is the last step before FishNet reads the
            // incoming packets and calls OnTick, where the controller turns the
            // pending input into replicate data (T2 note §6, TimeManager.cs:726-742).
            _networkManager.TimeManager.OnPreTick += TimeManager_OnPreTick;
            _subscribed = true;

            switch (_startMode)
            {
                case StartMode.Host:
                    _networkManager.ServerManager.StartConnection();
                    _networkManager.ClientManager.StartConnection();
                    break;
                case StartMode.Client:
                    _networkManager.ClientManager.StartConnection(_clientAddress);
                    break;
            }
        }

        void TimeManager_OnPreTick()
        {
            SpikePlayerController owner = SpikePlayerController.FindLocalOwner();
            if (owner == null) return;
            owner.SetPendingInput(BuildScriptedInput(_networkManager.TimeManager.LocalTick));
        }

        SimInput BuildScriptedInput(uint tick)
        {
            double tickDelta = _networkManager.TimeManager.TickDelta;
            float t = (float)(tick * tickDelta) * _patternRadPerSec;

            // A unit heading plus a third of a triple-rate unit heading: the body
            // turns constantly and at a varying rate, so a mispredicted tick opens
            // a LATERAL gap instead of hiding along the travel line, and by the
            // triangle inequality the sum's length never drops below 1 - 0.35 =
            // 0.65 — there is no degenerate tick. The v1 Lissajous
            // (cos t, sin 2t) DID vanish at every t = pi/2 + pi*k (cos t = 0 forces
            // sin 2t = 0) and normalizesafe then snapped the heading to (1, 0),
            // i.e. a 180-degree flip that the measurement blamed on the network.
            const float harmonicWeight = 0.35f;
            float2 move = math.normalize(
                new float2(math.cos(t), math.sin(t))
                + harmonicWeight * new float2(math.cos(3f * t), math.sin(3f * t)));

            bool dash = false;
            if (_dashPeriodSeconds > 0f)
            {
                uint periodTicks = (uint)math.max(1, (int)math.round(_dashPeriodSeconds / tickDelta));
                if (!_hasDashed || tick - _lastDashTick >= periodTicks)
                {
                    _lastDashTick = tick;
                    _hasDashed = true;
                    dash = true;
                }
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
            // GUI.Label with an explicit Rect draws on Repaint only, so every other
            // event would sort the correction ring (CorrectionMedian) for nothing —
            // and OnGUI runs at least twice per frame.
            if (Event.current.type != EventType.Repaint) return;

            float y = 8f;
            Line(ref y, "RING net spike (T3, temporary)");
            if (_networkManager == null) return;

            bool server = _networkManager.ServerManager.Started;
            bool client = _networkManager.ClientManager.Started;
            string process = server && client ? "host" : server ? "server" : client ? "client" : "offline";

            var sim = _networkManager.TransportManager.LatencySimulator;
            string simText;
            if (sim.GetEnabled())
            {
                // BOTH numbers are ONE-WAY: LatencySimulator.AddOutgoing applies
                // GetLatencyAsFloat() and rolls DropPacket() once per direction
                // (LatencySimulator.cs:245-248, :281-306). Latency 40 is what CR 7's
                // "80 ms RTT" means; loss 5% per direction is 9.75% round-trip.
                double loss = sim.GetPacketLost();
                simText = $"{sim.GetLatency()} ms one way (= {sim.GetLatency() * 2} ms RTT) / " +
                    $"{loss * 100d:0.#}% one way (= {(1d - (1d - loss) * (1d - loss)) * 100d:0.##}% round trip)";
            }
            else
            {
                simText = "OFF — enable it on NetworkManager/TransportManager";
            }

            Line(ref y, $"process {process}   localTick {_networkManager.TimeManager.LocalTick}   " +
                $"tickRate {_networkManager.TimeManager.TickRate}   " +
                $"RTT {_networkManager.TimeManager.RoundTripTime} ms   sim: {simText}");

            DrawLocalOwner(ref y);
            DrawServerCopyOfRemote(ref y);
        }

        /// Observations (a) and (d). They exist only on the client-side predicted
        /// copy, and only on a client-only process: on a host FishNet takes the
        /// server path in Reconcile_Current and never runs the reconcile body.
        void DrawLocalOwner(ref float y)
        {
            SpikePlayerController c = SpikePlayerController.FindLocalOwner();
            if (c == null)
            {
                Line(ref y, "[local owner] n/a — no owned player object yet");
                Line(ref y, "  (a) rubber band and (d) reconcile tick need a second participant: " +
                    "run this process as Client against a Host");
                return;
            }

            Line(ref y, $"[local owner] role: {c.RoleLabel}");
            Line(ref y, $"  (a) correction  last {c.CorrectionLast:0.000} m   " +
                $"median {c.CorrectionMedian():0.000} m (last {SpikePlayerController.CorrectionSamples} " +
                "samples ~ 4.3 s at 30 Hz)   " +
                $"max {c.CorrectionMax:0.000} m (all time)   samples {c.CorrectionSampleCount}");
            Line(ref y, $"  (a) dropped from the sample: repeated tick stamp {c.RepeatedReconcileTicks}   " +
                $"no server data {c.ReconcilesWithoutServerData}");
            Line(ref y, $"  (d) reconcile tick {c.LastReconcileTick}   " +
                $"localTick then {c.LastReconcileLocalTick}   " +
                $"delta {(long)c.LastReconcileLocalTick - c.LastReconcileTick}   " +
                $"server-sourced count {c.ServerReconcileCount}");
        }

        /// Observations (b) and (c). They exist only where FishNet keeps an
        /// incoming replicate queue — the SERVER's copy of a REMOTE client's
        /// object (NetworkBehaviour.Prediction.cs:598-731). An owner's own copy
        /// always replicates once with (Ticked | Created) and would answer both
        /// questions with a constant.
        void DrawServerCopyOfRemote(ref float y)
        {
            SpikePlayerController c = SpikePlayerController.FindServerCopyOfRemote();
            if (c == null)
            {
                Line(ref y, "[server-side, non-owner] n/a — needs a second participant " +
                    "(a remote client connected to this process)");
                Line(ref y, "  (b) ReplicateState table and (c) replicates per server tick " +
                    "cannot be read from an owner's own copy");
                return;
            }

            Line(ref y, $"[server-side, non-owner] role: {c.RoleLabel}");
            Line(ref y, $"  (b) ReplicateState  live+data {c.LiveWithData}   " +
                $"live-no-data {c.LiveNoData}   replay+data {c.ReplayWithData}   " +
                $"replay-no-data {c.ReplayNoData}   future {c.FutureStates}   " +
                $"invalid {c.InvalidStates}   other {c.OtherStates} (mask {c.LastOtherState})   " +
                $"last {c.LastState}");
            Line(ref y, $"  (c) replicates per server tick  max {c.MaxReplicatesPerServerTick}   " +
                $">1 on {c.MultiReplicateTicks} ticks   starved ticks {c.ServerStarvedTicks}");
        }

        static void Line(ref float y, string text)
        {
            GUI.Label(new Rect(12f, y, 1100f, 20f), text);
            y += 20f;
        }
    }
}
