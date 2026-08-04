// TEMPORARY (T3 -> T30): the whole Networking/Spike folder plus the
// Assets/Scenes/NetSpike.unity scene and Assets/Prefabs/SpikePlayer.prefab are
// deleted in T30, when PlayerPrediction.Step + PlayerNetworkController (T34)
// replace them. Corner-cutting is sanctioned HERE ONLY (stage-2 plan, task T3).
using System.Collections.Generic;
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
    /// Vertical slice of spec §3.7/§3.9 on a single player, to retire risk P-A
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
    /// WHICH OBSERVATION IS READABLE FROM WHICH ROLE — the single most important
    /// fact about this instrument, and the reason every overlay line names its
    /// role. FishNet routes a replicate through one of two branches
    /// (NetworkBehaviour.Prediction.cs:507-510, by NetworkObject.IsController):
    ///   * Replicate_Authoritative — the OWNER's own copy. It always invokes the
    ///     user body exactly once with (Ticked | Created) and nothing else
    ///     (:590-591, comment "Owner always replicates with new data"). No queue,
    ///     no starvation, never two datas on one tick.
    ///   * Replicate_NonAuthoritative — the SERVER's copy of a remote client's
    ///     object (and a client's copy of somebody else's object). Only here does
    ///     the incoming queue exist, so only here can a tick run with no data
    ///     (:711, bare Ticked) or consume a second data (:684).
    /// Therefore:
    ///   (a) rubber band and (d) reconcile tick  -> LOCAL OWNER, and only on a
    ///       client-only build: on a host Reconcile_Current takes the server
    ///       path (:1304-1311) and the user reconcile body never runs at all;
    ///   (b) ReplicateState table and (c) two replicates per server tick
    ///       -> the SERVER-SIDE copy of a REMOTE client's object.
    /// One process can never show all four: the manual check needs two
    /// participants (see SpikeBootstrap's overlay and the plan's step 3).
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
            /// a plain field read (T2 note §4, PredictionProcessor.cs:383-416).
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
            /// The client builds this struct too — CreateReconcile runs on both
            /// sides — and FishNet substitutes that local copy whenever the state
            /// it received carries no data for this behaviour
            /// (NetworkBehaviour.Prediction.cs:1356-1361 reads it back through
            /// Reconcile_Reader_Local, :1494-1501), then invokes the user body
            /// anyway. Such a run is a rollback to the client's own snapshot, so
            /// the distance from "what we showed" comes out EXACTLY 0.000 m: left
            /// in the sample it would drag the median down hardest precisely when
            /// the network is worst. FishNet's own discriminator
            /// (`IsReconcileRemote`, :180) is `internal` and unreadable from this
            /// assembly, so the source travels in the payload — one byte on a
            /// temporary dev struct.
            public const byte SourceLocalFallback = 0; // = struct default
            public const byte SourceServer = 1;

            public PlayerState Player;
            public byte Source;

            /// See SpikeReplicateData._tick.
            uint _tick;

            public SpikeReconcileData(in PlayerState player, byte source)
            {
                Player = player;
                Source = source;
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

        /// Ring length of the correction sample buffer the overlay medians over
        /// (~4.3 s at 30 Hz — the median is a RECENT number, CorrectionMax is not).
        public const int CorrectionSamples = 128;

        /// Spec P25: how long the server repeats the last input before it falls
        /// back to "stand still". The production value lives in NetConfig (T23);
        /// this spike has no config asset, and the number is dev-only here.
        const int InputStarveTicks = 10;

        /// Spike shortcut (T3 only): every live instance, so the overlay can pick
        /// the one instance each observation is actually readable from (see the
        /// class comment). The production path is the runner facade of T43/T44 —
        /// nothing outside Spike/ may copy this.
        static readonly List<SpikePlayerController> Instances = new();

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
        int _clientStarvedRun;

        readonly uint[] _shownTick = new uint[HistoryLength];
        readonly float2[] _shownPos = new float2[HistoryLength];
        readonly bool[] _shownValid = new bool[HistoryLength];

        readonly float[] _corrections = new float[CorrectionSamples];
        readonly float[] _correctionScratch = new float[CorrectionSamples];
        int _correctionCount, _correctionCursor;

        int _replicatesThisTick;

        uint _lastSampledReconcileTick;
        bool _hasSampledReconcile;

        #region Roles (which observation this instance can answer).

        /// The client-side predicted copy — the only place (a) and (d) exist.
        public bool IsLocalOwner => IsOwner;

        /// The server's copy of a REMOTE client's object — the only place the
        /// incoming replicate queue exists, i.e. the only source of (b) and (c).
        public bool IsServerCopyOfRemote => IsServerStarted && !IsOwner && Owner.IsValid;

        /// Printed verbatim on every overlay line so a number can never be read
        /// off a role it does not describe.
        public string RoleLabel
        {
            get
            {
                if (IsOwner)
                    return IsServerStarted
                        ? "host owner — authoritative, FishNet runs no reconcile body here"
                        : "client owner — predicted copy";
                if (!IsServerStarted) return "client copy of a remote player";
                return Owner.IsValid ? "server copy of a remote client" : "server copy, unowned";
            }
        }

        /// The instance the scripted input is pushed into, and the source of
        /// observations (a) and (d). Null until a second participant connects on a
        /// server-only/host process — that is a fact to print, not a zero.
        public static SpikePlayerController FindLocalOwner() => Find(c => c.IsLocalOwner);

        /// The source of observations (b) and (c); null on a client-only process
        /// and on a host until a remote client connects.
        public static SpikePlayerController FindServerCopyOfRemote() => Find(c => c.IsServerCopyOfRemote);

        static SpikePlayerController Find(System.Func<SpikePlayerController, bool> predicate)
        {
            for (int i = 0; i < Instances.Count; i++)
            {
                SpikePlayerController c = Instances[i];
                // Destroyed objects can survive in the list when the editor
                // reloads the domain mid-play; Unity's fake-null catches them.
                if (c != null && predicate(c)) return c;
            }
            return null;
        }

        #endregion

        #region Observations (plan step 3).

        /// (a) Rubber band, meters — LOCAL OWNER only.
        public float CorrectionLast { get; private set; }
        /// All-time worst, never windowed (unlike CorrectionMedian()).
        public float CorrectionMax { get; private set; }
        public int CorrectionSampleCount => _correctionCount;
        /// Reconciles thrown out of the sample instead of biasing it, see
        /// PerformReconcile: a repeated tick stamp (starved server tick) and a
        /// reconcile FishNet filled in from the client's own history.
        public int RepeatedReconcileTicks { get; private set; }
        public int ReconcilesWithoutServerData { get; private set; }

        /// (b) ReplicateState combinations seen (T2 note §4) — SERVER COPY OF A
        /// REMOTE CLIENT only; on an owner every single one lands in LiveWithData.
        public int LiveWithData { get; private set; }
        public int LiveNoData { get; private set; }
        public int ReplayWithData { get; private set; }
        public int ReplayNoData { get; private set; }
        public int FutureStates { get; private set; }
        public int InvalidStates { get; private set; }
        /// Any combination the buckets above do not cover — kept separate, with
        /// the raw mask, so an unexpected flag mix shows up instead of being
        /// filed under one of them.
        public int OtherStates { get; private set; }
        public ReplicateState LastOtherState { get; private set; }
        public ReplicateState LastState { get; private set; }

        /// (c) Two replicates on one server tick — SERVER COPY OF A REMOTE CLIENT.
        public int MaxReplicatesPerServerTick { get; private set; }
        public int MultiReplicateTicks { get; private set; }
        public int ServerStarvedTicks { get; private set; }

        /// (d) Which tick the reconcile carries — LOCAL OWNER, server-sourced
        /// reconciles only.
        public uint LastReconcileTick { get; private set; }
        public uint LastReconcileLocalTick { get; private set; }
        public int ServerReconcileCount { get; private set; }

        /// Median of the collected corrections, meters. Sorts a copy of the ring,
        /// so the overlay calls it on EventType.Repaint only (SpikeBootstrap).
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

            Instances.Add(this);
            TimeManager.OnTick += TimeManager_OnTick;
            TimeManager.OnPostTick += TimeManager_OnPostTick;
        }

        public override void OnStopNetwork()
        {
            Instances.Remove(this);
            if (TimeManager != null)
            {
                TimeManager.OnTick -= TimeManager_OnTick;
                TimeManager.OnPostTick -= TimeManager_OnPostTick;
            }
            base.OnStopNetwork();
        }

        void TimeManager_OnTick()
        {
            if (!_hasConfig) return;

            // Counted around the replicate call, not inside it: FishNet queues
            // incoming datas in TryIterateData BETWEEN OnPreTick and OnTick
            // (T2 note §6, TimeManager.cs:726-742), then runs one invocation per
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
            // subscription order with no priority API (T2 note §6), so "run after
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
                if (!state.ContainsReplayed()) _clientStarvedRun = 0;
            }
            else
            {
                // Unknown tick (state without Created). The starvation policy is
                // ONE function shared with the server step below — asymmetric
                // policies would show up as a correction on every lost packet and
                // drown observation (a). The run length only advances on live
                // ticks: a replay re-runs the same starved ticks and would
                // otherwise inflate the counter past the cap on every reconcile.
                if (!state.ContainsReplayed()) _clientStarvedRun++;
                input = StarvedInput(_lastClientInput, _clientStarvedRun);
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
            // CharacterControllerPrediction.cs:227-253) — hence the source tag,
            // see SpikeReconcileData.
            PerformReconcile(IsServerStarted
                ? new SpikeReconcileData(_authority, SpikeReconcileData.SourceServer)
                : new SpikeReconcileData(_predicted, SpikeReconcileData.SourceLocalFallback));
        }

        [Reconcile]
        void PerformReconcile(SpikeReconcileData data, Channel channel = Channel.Unreliable)
        {
            if (!_hasConfig) return;

            uint tick = data.GetTick();
            bool fromServer = data.Source == SpikeReconcileData.SourceServer;

            // FILTER 1 — repeated tick stamp. The tick a reconcile carries is the
            // one written into the state-update header, and that is the owner's
            // ReplicateTick (PredictionManager.cs:750-754, :783), which FishNet
            // advances only for CREATED replicates
            // (NetworkObject.Prediction.cs:660-665). This spike, though, steps the
            // authority on EVERY server post-tick, starved ones included, and
            // FishNet drops only STRICTLY older states
            // (NetworkBehaviour.Prediction.cs:1475). So a starved tick ships a
            // second reconcile carrying the SAME tick with the authority one step
            // further along, and subtracting it from what we showed for that tick
            // yields exactly one tick of travel (0.25 m at MaxSpeed, 1.0 m in a
            // dash) — a measurement artifact, not a rubber band.
            //   Chosen over the alternative (carry an authority-step counter in
            // SpikeReconcileData and compare only matching pairs): the second
            // sample is not merely mispaired, it describes a position the client
            // was never asked to show, so there is no pair to salvage — the only
            // correct handling is to drop it. The tick stamp is FishNet's own and
            // needs no extra wire field, and the number of drops is itself worth
            // reporting: it is the server's starvation seen from the client.
            bool repeatedTick = _hasSampledReconcile && tick <= _lastSampledReconcileTick;

            if (fromServer)
            {
                LastReconcileTick = tick;
                LastReconcileLocalTick = TimeManager.LocalTick;
                ServerReconcileCount++;
                if (repeatedTick) RepeatedReconcileTicks++;
                else
                {
                    _lastSampledReconcileTick = tick;
                    _hasSampledReconcile = true;
                    // FILTER 2 (see SpikeReconcileData.Source) has already passed:
                    // this state really came off the wire.
                    if (TryGetShown(tick, out float2 shown))
                        RecordCorrection(math.distance(shown, data.Player.Pos));
                }
            }
            else
            {
                ReconcilesWithoutServerData++;
            }

            // Applied in both cases: even the local fallback is the rollback point
            // FishNet expects the body to install before it replays the inputs.
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
                input = StarvedInput(_serverInput, _serverStarvedRun);
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

        /// Spec P25, the SINGLE home of the starvation policy: the server
        /// authority step and the client prediction branch both call this, with
        /// the same threshold and the same fall-back, so a lost packet cannot show
        /// up as a correction that the network did not cause.
        /// Repeat the last input with the edge latches cleared, but not longer
        /// than InputStarveTicks; past that the body stops (AimHeld and the aim
        /// point survive — a disconnect must not read as a body walking into a
        /// wall). P25 also clears FireHeld there; the spike never runs the weapon
        /// tick (WeaponSystem is the only reader of SimInput.FireHeld), so writing
        /// it here would be a dead store — the production policy of T36 owns it.
        static SimInput StarvedInput(in SimInput last, int starvedRun)
        {
            // "Held values without the edge latches" is exactly what the world
            // already means by any sub-tick after the first (spec §3.2), so the
            // existing SimInputFrame.ForTick is the definition, not a copy of it.
            SimInput input = SimInputFrame.ForTick(last, tickIndex: 1);
            if (starvedRun > InputStarveTicks) input.MoveDir = float2.zero;
            return input;
        }

        /// ReplicateState is a [Flags] mask (T2 note §4), so buckets are tested
        /// with ContainsTicked/ContainsCreated/ContainsReplayed
        /// (ReplicateState.cs:46-56). The Is* helpers are EXACT equality
        /// (:64-79): IsReplayedCreated compares against (Replayed | Created)
        /// while a real replay carries (Replayed | Ticked | Created)
        /// (NetworkBehaviour.Prediction.cs:770), so an Is*-based table would file
        /// every replay under "other" and never fire at all.
        /// The five combinations FishNet can actually emit each get their own
        /// bucket, because "live tick with no data" (a lost input, :711) and
        /// "replayed tick with no data" (:796-806) are the two halves of
        /// observation (b) and must not be added together.
        void CountState(ReplicateState state)
        {
            LastState = state;
            if (!state.IsValid())
            {
                InvalidStates++;
                return;
            }

            bool created = state.ContainsCreated();
            bool ticked = state.ContainsTicked();
            bool replayed = state.ContainsReplayed();

            if (replayed)
            {
                if (created) ReplayWithData++;
                else if (ticked) ReplayNoData++;
                else FutureStates++;
                return;
            }

            if (ticked)
            {
                if (created) LiveWithData++;
                else LiveNoData++;
                return;
            }

            OtherStates++;
            LastOtherState = state;
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
