// TEMPORARY (T3 -> T30): the whole Networking/Spike folder plus the
// Assets/Scenes/NetSpike.unity scene and Assets/Prefabs/SpikePlayer.prefab are
// deleted in T30, when PlayerPrediction.Step + PlayerNetworkController (T34)
// replace them. Corner-cutting is sanctioned HERE ONLY (stage-2 plan, task T3).
using FishNet.Managing;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using Ring.Simulation.Core;
using Ring.Simulation.Movement;
using Unity.Mathematics;
using UnityEngine;

namespace Ring.Networking.Spike
{
    /// Vertical slice of spec §3.7/§3.9 on a single player, to retire risk Р-А
    /// before the rest of stage 2 is built on the assumption. What it proves:
    /// "client input -> server records it in the replicate branch -> server
    /// advances the authority once per tick in OnPostTick -> reconcile carries a
    /// whole PlayerState back" survives FishNet's prediction pipeline unchanged.
    ///
    /// Deliberately NOT here (all sanctioned by the plan for Spike/ only, all
    /// arriving as real tasks later): the world (T4+), input sanitization
    /// (SimInputSanitizer, T6), edge-request rate limiting (T10), the weapon tick
    /// (T30), the snapshot protocol (T26–T29), the visibility filter (T19–T22),
    /// more than one player (T4/T36) and quantized input (T25).
    ///
    /// The four numbers plan step 3 asks the owner to read off the running scene
    /// are collected here and printed by SpikeBootstrap's overlay:
    ///   (a) CorrectionLast/Median/Max — how far the position we SHOWED was from
    ///       the authority for the same tick, i.e. the rubber band;
    ///   (b) TickedCreated/TickedNonCreated/ReplayedCreated/Future counters —
    ///       which ReplicateState combinations actually occur, and specifically
    ///       which one marks a lost input (Т2 note §4: the enum is a [Flags]
    ///       mask, so this is a table of combinations, not of single values);
    ///   (c) MaxReplicatesPerServerTick/MultiReplicateTicks — what happens when
    ///       two [Replicate] datas land on one server tick (spec §3.7 expects
    ///       exactly one to be consumed, the second to count as overwritten);
    ///   (d) LastReconcileTick vs LastReconcileLocalTick — which tick the
    ///       ReconcileData carries relative to local time.
    public sealed class SpikePlayerController : NetworkBehaviour
    {
        #region Wire types.

        /// Spike input payload. The production one (T25/T34) is quantized; raw
        /// floats here — bandwidth is not what this task measures.
        public struct SpikeReplicateData : IReplicateData
        {
            public float2 MoveDir, AimPoint;
            public float AimHeight;
            public bool FireHeld, DashRequested, AimHeld, SlideRequested;

            /// Assigned by FishNet, never by us. Codegen requires GetTick() to be
            /// a plain field read (Т2 note §4, PredictionProcessor.cs:383-416).
            uint _tick;

            public SpikeReplicateData(in SimInput input)
            {
                MoveDir = input.MoveDir;
                AimPoint = input.AimPoint;
                AimHeight = input.AimHeight;
                FireHeld = input.FireHeld;
                DashRequested = input.DashRequested;
                AimHeld = input.AimHeld;
                SlideRequested = input.SlideRequested;
                _tick = 0;
            }

            public SimInput ToSimInput() => new SimInput
            {
                MoveDir = MoveDir,
                AimPoint = AimPoint,
                AimHeight = AimHeight,
                FireHeld = FireHeld,
                DashRequested = DashRequested,
                AimHeld = AimHeld,
                SlideRequested = SlideRequested
            };

            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
            public void Dispose() { }
        }

        /// Spec §3.9: ReconcileData is the FULL PlayerState plus a tick, taken
        /// from the authority (here: this spike's single authoritative copy;
        /// from T30 on: from the world).
        public struct SpikeReconcileData : IReconcileData
        {
            public PlayerState Player;

            /// See SpikeReplicateData._tick.
            uint _tick;

            public SpikeReconcileData(in PlayerState player)
            {
                Player = player;
                _tick = 0;
            }

            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
            public void Dispose() { }
        }

        #endregion

        /// Ring length of the "what did we show for tick N" history, in ticks
        /// (~2.1 s at 30 Hz) — longer than any replay window FishNet will ask for.
        const int HistoryLength = 64;

        /// Ring length of the correction sample buffer the overlay medians over.
        const int CorrectionSamples = 128;

        /// Spec Р25: how long the server repeats the last input before it falls
        /// back to "stand still". The production value lives in NetConfig (T23);
        /// this spike has no config asset, and the number is dev-only here.
        const int InputStarveTicks = 10;

        /// Spike shortcut (T3 only): the owner instance SpikeBootstrap feeds input
        /// to and reads the overlay from. The production path is the runner facade
        /// of T43/T44 — nothing outside Spike/ may copy this.
        public static SpikePlayerController LocalOwner { get; private set; }

        SimConfig _config;
        bool _hasConfig;

        /// Client-side predicted copy of the local player (spec §3.9) and the
        /// server-side authority. On a host only the authority is live.
        PlayerState _predicted, _authority;

        /// Owner-side latch. Same contract T34 pins for the real controller
        /// ("input arrives from outside, the controller never samples it"), so the
        /// spike already proves the asmdef direction Presentation -> Networking.
        SimInput _pendingInput;

        /// Last input the server actually consumed, and whether a fresh one landed
        /// on this tick (spec §3.7: the replicate branch only records).
        SimInput _serverInput;
        bool _serverInputFresh;
        int _serverStarvedRun;

        /// Client mirror of the above, used when a replicate runs for a tick whose
        /// data is not known yet (state without Created).
        SimInput _lastClientInput;

        readonly uint[] _shownTick = new uint[HistoryLength];
        readonly float2[] _shownPos = new float2[HistoryLength];
        readonly bool[] _shownValid = new bool[HistoryLength];

        readonly float[] _corrections = new float[CorrectionSamples];
        readonly float[] _correctionScratch = new float[CorrectionSamples];
        int _correctionCount, _correctionCursor;

        int _replicatesThisTick;

        #region Observations (plan step 3).

        /// (a) Rubber band, metres.
        public float CorrectionLast { get; private set; }
        public float CorrectionMax { get; private set; }
        public int CorrectionSampleCount => _correctionCount;

        /// (b) ReplicateState combinations seen (Т2 note §4).
        public int TickedCreated { get; private set; }
        public int TickedNonCreated { get; private set; }
        public int ReplayedCreated { get; private set; }
        public int FutureStates { get; private set; }
        public int InvalidStates { get; private set; }
        /// Any combination the four named buckets above do not cover — kept
        /// separate so an unexpected flag mix shows up instead of being filed
        /// under one of them.
        public int OtherStates { get; private set; }
        public ReplicateState LastState { get; private set; }

        /// (c) Two replicates on one server tick.
        public int MaxReplicatesPerServerTick { get; private set; }
        public int MultiReplicateTicks { get; private set; }
        public int ServerStarvedTicks { get; private set; }

        /// (d) Which tick the reconcile carries.
        public uint LastReconcileTick { get; private set; }
        public uint LastReconcileLocalTick { get; private set; }
        public int ReconcileCount { get; private set; }

        /// Median of the collected corrections, metres. Sorts a copy of the ring —
        /// the overlay is dev-only and runs once per repaint.
        public float CorrectionMedian()
        {
            if (_correctionCount == 0) return 0f;
            System.Array.Copy(_corrections, _correctionScratch, _correctionCount);
            System.Array.Sort(_correctionScratch, 0, _correctionCount);
            return _correctionScratch[_correctionCount / 2];
        }

        #endregion

        /// The seam T34 pins for the production controller: input is pushed in from
        /// outside, edge requests latch until the next replicate consumes them.
        public void SetPendingInput(in SimInput input)
        {
            bool dash = _pendingInput.DashRequested || input.DashRequested;
            bool slide = _pendingInput.SlideRequested || input.SlideRequested;
            _pendingInput = input;
            _pendingInput.DashRequested = dash;
            _pendingInput.SlideRequested = slide;
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            _hasConfig = SpikeBootstrap.TryGetSharedConfig(out _config);
            if (!_hasConfig)
            {
                NetworkManager.LogError("SpikePlayerController: no SimConfig — " +
                    "SpikeBootstrap is missing from the scene or its balance assets are unwired.");
                return;
            }

            // Same starting state SimulationWorld's constructor builds for its own
            // single player; duplicated rather than exposed because the spike has
            // no world at all (T4 gives the world an array of players anyway).
            _authority = new PlayerState
            {
                Hp = _config.Hero.MaxHp, Stamina = _config.Hero.StaminaMax, Alive = true
            };
            _predicted = _authority;

            TimeManager.OnTick += TimeManager_OnTick;
            TimeManager.OnPostTick += TimeManager_OnPostTick;
        }

        public override void OnStopNetwork()
        {
            if (TimeManager != null)
            {
                TimeManager.OnTick -= TimeManager_OnTick;
                TimeManager.OnPostTick -= TimeManager_OnPostTick;
            }
            base.OnStopNetwork();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (IsOwner) LocalOwner = this;
        }

        public override void OnStopClient()
        {
            if (LocalOwner == this) LocalOwner = null;
            base.OnStopClient();
        }

        void TimeManager_OnTick()
        {
            if (!_hasConfig) return;

            // Counted around the replicate call, not inside it: FishNet queues
            // incoming datas in TryIterateData BETWEEN OnPreTick and OnTick
            // (Т2 note §6, TimeManager.cs:726-742), then runs one invocation per
            // queued data when the controller calls the replicate method here.
            _replicatesThisTick = 0;
            MovePlayer(BuildReplicateData());

            if (!IsServerStarted) return;
            if (_replicatesThisTick > MaxReplicatesPerServerTick)
                MaxReplicatesPerServerTick = _replicatesThisTick;
            if (_replicatesThisTick > 1) MultiReplicateTicks++;
            if (_replicatesThisTick == 0) ServerStarvedTicks++;
        }

        void TimeManager_OnPostTick()
        {
            if (!_hasConfig) return;

            // Spec §3.7: the authoritative step runs in OnPostTick, not OnTick.
            // TimeManager's events are plain multicast delegates invoked in
            // subscription order with no priority API (Т2 note §6), so "run after
            // every replicate landed" is bought by the event, not by ordering luck.
            if (IsServerStarted) AdvanceAuthority();
            CreateReconcile();
        }

        SpikeReplicateData BuildReplicateData()
        {
            // Only the controller builds data (FishNet demo
            // CharacterControllerPrediction.cs:205-224); everyone else sends the
            // default, which FishNet replaces with whatever it has queued.
            if (!IsOwner) return default;

            var data = new SpikeReplicateData(_pendingInput);
            // Edge latches are consumed by exactly one tick, like
            // InputSampler.ClearLatches does for the local backend.
            _pendingInput.DashRequested = false;
            _pendingInput.SlideRequested = false;
            return data;
        }

        [Replicate]
        void MovePlayer(SpikeReplicateData data, ReplicateState state = ReplicateState.Invalid,
            Channel channel = Channel.Unreliable)
        {
            if (!_hasConfig) return;
            CountState(state);

            if (IsServerStarted)
            {
                // Spec §3.7: the server branch moves NOTHING. It drops the input
                // into the slot the world step will read in OnPostTick — with an
                // array of players (T4/T36) this becomes _inputs[playerIndex].
                if (!state.ContainsCreated()) return;
                _serverInput = data.ToSimInput();
                _serverInputFresh = true;
                _replicatesThisTick++;
                return;
            }

            // Client: advance the predicted copy. Both the live tick and every
            // reconcile replay come through here — that IS the prediction.
            SimInput input;
            if (state.ContainsCreated())
            {
                input = data.ToSimInput();
                _lastClientInput = input;
            }
            else
            {
                // Unknown tick (state without Created): repeat the last input with
                // edge flags cleared, exactly what the server does when starved
                // (spec Р25) — mismatched starvation policies would show up as a
                // correction on every lost packet and drown observation (a).
                input = RepeatWithoutEdges(_lastClientInput);
            }

            PlayerMovementSpikeSeam.Update(ref _predicted, in input, in _config);
            ApplyToTransform(in _predicted);

            // Record only the FIRST run of a tick — that is the position the
            // player actually saw, and the one a correction is measured against.
            if (state.ContainsTicked() && !state.ContainsReplayed())
                RecordShown(data.GetTick(), _predicted.Pos);
        }

        public override void CreateReconcile()
        {
            if (!_hasConfig) return;

            // The server sends the authority. The client builds the same shape
            // from its own predicted copy: FishNet uses it as the local fallback
            // for ticks whose reconcile packet never arrived (FishNet demo
            // CharacterControllerPrediction.cs:227-253).
            PerformReconcile(new SpikeReconcileData(IsServerStarted ? _authority : _predicted));
        }

        [Reconcile]
        void PerformReconcile(SpikeReconcileData data, Channel channel = Channel.Unreliable)
        {
            if (!_hasConfig) return;

            uint tick = data.GetTick();
            LastReconcileTick = tick;
            LastReconcileLocalTick = TimeManager.LocalTick;
            ReconcileCount++;

            if (TryGetShown(tick, out float2 shown))
                RecordCorrection(math.distance(shown, data.Player.Pos));

            _predicted = data.Player;
            ApplyToTransform(in _predicted);
        }

        void AdvanceAuthority()
        {
            SimInput input;
            if (_serverInputFresh)
            {
                input = _serverInput;
                _serverStarvedRun = 0;
            }
            else
            {
                _serverStarvedRun++;
                // Spec Р25: repeat the last input with edge flags cleared, but not
                // longer than InputStarveTicks; past that the body stops (AimHeld
                // and the aim point survive — a disconnect must not read as a shot
                // fired or as a body walking into a wall).
                input = _serverStarvedRun <= InputStarveTicks
                    ? RepeatWithoutEdges(_serverInput)
                    : StarvedIdle(_serverInput);
            }

            PlayerMovementSpikeSeam.Update(ref _authority, in input, in _config);
            _serverInputFresh = false;
            ApplyToTransform(in _authority);
        }

        /// Sim-space (x, y) -> world (x, 0, z). Presentation.SimSpace is the sole
        /// home of this mapping in production code, but Ring.Networking must not
        /// reference Ring.Presentation (spec §3.1: the dependency runs the other
        /// way), and this line dies with the spike in T30.
        void ApplyToTransform(in PlayerState p)
            => transform.position = new Vector3(p.Pos.x, 0f, p.Pos.y);

        static SimInput RepeatWithoutEdges(in SimInput last)
        {
            SimInput s = last;
            s.DashRequested = false;
            s.SlideRequested = false;
            return s;
        }

        static SimInput StarvedIdle(in SimInput last)
        {
            SimInput s = RepeatWithoutEdges(last);
            s.MoveDir = float2.zero;
            s.FireHeld = false;
            return s;
        }

        void CountState(ReplicateState state)
        {
            LastState = state;
            if (!state.IsValid()) InvalidStates++;
            else if (state.IsTickedCreated()) TickedCreated++;
            else if (state.IsTickedNonCreated()) TickedNonCreated++;
            else if (state.IsReplayedCreated()) ReplayedCreated++;
            else if (state.IsFuture()) FutureStates++;
            else OtherStates++;
        }

        void RecordShown(uint tick, float2 pos)
        {
            int slot = (int)(tick % HistoryLength);
            _shownTick[slot] = tick;
            _shownPos[slot] = pos;
            _shownValid[slot] = true;
        }

        bool TryGetShown(uint tick, out float2 pos)
        {
            int slot = (int)(tick % HistoryLength);
            if (_shownValid[slot] && _shownTick[slot] == tick)
            {
                pos = _shownPos[slot];
                return true;
            }
            pos = default;
            return false;
        }

        void RecordCorrection(float meters)
        {
            CorrectionLast = meters;
            if (meters > CorrectionMax) CorrectionMax = meters;
            _corrections[_correctionCursor] = meters;
            _correctionCursor = (_correctionCursor + 1) % CorrectionSamples;
            if (_correctionCount < CorrectionSamples) _correctionCount++;
        }
    }
}
