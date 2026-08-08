using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking
{
    /// One player's seat in FishNet's prediction pipeline (Stage 2 Task 34,
    /// spec §3.9). Input goes up as `ReplicateData`, the world's own answer
    /// comes back as `ReconcileData`, and FishNet replays the queue in between.
    ///
    /// DELIBERATELY THIN. Every decision worth arguing about lives in
    /// `PlayerPredictionCore` below, which knows nothing about FishNet and can
    /// therefore be tested without a `NetworkManager`, a spawned object or a
    /// connection (`ReconcileCodecTests`). What is left here is the wiring the
    /// package demands and nothing else — the same split `RenderClock` and
    /// `SnapshotQueue` already use for the client's other moving parts.
    ///
    /// THE PACKAGE'S OWN SHAPE, NOT AN INVENTED ONE. `TickNetworkBehaviour`
    /// (FishNet.Utility.Template) is the base the package's own prediction demo
    /// uses; it subscribes to `TimeManager.OnTick`/`OnPostTick` on start and
    /// unsubscribes on stop, which is the whole of what a hand-rolled base
    /// would do. `[Replicate]`/`[Reconcile]` are private and `CreateReconcile`
    /// is overridden and calls the reconcile method, because the IL
    /// post-processor rejects the class outright otherwise
    /// (PredictionProcessor.cs:237-267 and :273-278) — those are compile-time
    /// contracts, not conventions.
    ///
    /// WHAT DRIVES IT, AND WHAT IT DOES NOT OWN. Input arrives from OUTSIDE
    /// through `SetPendingInput`, because `InputSampler` lives in
    /// `Ring.Presentation` and Presentation already references
    /// `Ring.Networking` — sampling here would close an assembly cycle (Р35).
    /// The world that feeds `SetAuthoritativeState` and consumes
    /// `Core.LastServerInput` is Task 36's; the prefab this component sits on,
    /// the `NetworkTickSmoother` beside it and the death event feeding
    /// `NotifyOwnDeath` are Task 41/44's.
    ///
    /// RECONCILIATION SMOOTHING (Р78/Р106) — the contract, not the wiring.
    /// A correction farther than `NetConfig.ReconcileSnapMeters` (default 1.0)
    /// reads as a teleport and must SNAP; anything smaller must be smoothed
    /// over the graphical transform. The measurement belongs here and only
    /// here: `Core.LastCorrectionMeters` is taken BEFORE the predicted copy is
    /// overwritten, and after that the distance is unrecoverable — no later
    /// consumer can measure what the client used to believe.
    ///
    /// The snap itself must be driven from that number rather than configured
    /// on the smoother, and the reason is in the smoother's own sources.
    /// `MovementSettings.TeleportThreshold` looks like the natural home for it
    /// and is not, twice over:
    ///   1. UNITS. `UniversalTickSmoother.cs:448` caches the threshold SQUARED
    ///      (`TeleportThreshold * TeleportThreshold`) while the value it is
    ///      compared against — `MoveRates.cs:216-218` against `distance` from
    ///      `Vectors.cs:26-30` — is a plain `Vector3.Distance` in metres. The
    ///      inspector number therefore behaves as metres² and coincides with
    ///      metres at exactly 1.0 and nowhere else; the moment an owner retuned
    ///      `ReconcileSnapMeters` to 2, the smoother would snap at 4 m.
    ///   2. QUANTITY. What it measures is how far the object moved BETWEEN
    ///      TWO TICKS, not how far the correction moved it. At 30 Hz a
    ///      legitimate dash covers `DashSpeed / TickRate` metres per tick —
    ///      around one metre at the shipped numbers — so a 1 m threshold would
    ///      teleport the graphic on every dash tick, which is precisely the
    ///      artefact Р78 exists to remove.
    /// Task 41/44 therefore wires `NetworkTickSmoother`
    /// (FishNet.Component.Transforming.Beta — NOT the obsolete internal
    /// `LocalTransformTickSmoother`, Р106) for the ordinary smoothing and
    /// drives the snap from `PlayerPredictionCore.ShouldSnapCorrection`.
    public class PlayerNetworkController : TickNetworkBehaviour
    {
        readonly PlayerPredictionCore _core = new PlayerPredictionCore();

        SimConfig _cfg;
        bool _configured;

        uint _authoritativeTick;
        PlayerState _authoritativeState;

        /// The runtime-free half of this component. Public because Task 36 and
        /// Task 44 talk to it — the server loop reads `LastServerInput`, the
        /// presentation side reads `Predicted` and `LastCorrectionMeters`.
        public PlayerPredictionCore Core => _core;

        /// Balance numbers for this match. Until it is called the component is
        /// inert: no replicate is built and no reconcile is created, because
        /// every codec mapping and every step of prediction is a function of
        /// this config and a zeroed one would encode garbage. Called by the
        /// match bootstrap (Task 41/44) once the config is resolved.
        public void Configure(in SimConfig cfg)
        {
            _cfg = cfg;
            _configured = true;
        }

        /// The latest sampled frame, whole. Called by the backend every frame
        /// (Р35: `SampleFrame` before the send, `ClearLatches` after the input
        /// is consumed). See `PlayerPredictionCore.SetPendingInput` for why
        /// nothing is coalesced here.
        public void SetPendingInput(in SimInput input) => _core.SetPendingInput(in input);

        /// Our own `PlayerDied` arrived (Р41/Р59). Task 44 owns the
        /// subscription — this is the seam it calls.
        public void NotifyOwnDeath() => _core.NotifyOwnDeath();

        /// The world's authoritative state for this player at `tick` — what
        /// `CreateReconcile` sends back (spec §3.9: from the WORLD, not from a
        /// server-side predicted copy). Task 36 calls this after the world has
        /// ticked.
        public void SetAuthoritativeState(uint tick, in PlayerState state)
        {
            _authoritativeTick = tick;
            _authoritativeState = state;
        }

        protected override void TimeManager_OnTick()
        {
            if (!_configured) return;
            PerformReplicate(BuildReplicate());
        }

        protected override void TimeManager_OnPostTick()
        {
            if (!_configured) return;
            CreateReconcile();
        }

        /// Only the controller of the object builds real data; everyone else
        /// hands in `default`, which FishNet expects (the package demo's own
        /// `BuildMoveData` does exactly this).
        ReplicateData BuildReplicate()
        {
            if (!IsOwner) return default;
            SimInput pending = _core.PendingInput;
            return ReplicateData.FromInput(in pending, in _cfg);
        }

        /// Required override (PredictionProcessor.cs:237-267 rejects the class
        /// if it is missing or does not call the reconcile method).
        ///
        /// On the server the data is the WORLD's. On a client it is the
        /// client's own predicted copy, which is the package's documented
        /// fallback role for a locally built reconcile: it fills the gap when
        /// the server's state packet is lost, and is discarded the moment a
        /// real one arrives.
        public override void CreateReconcile()
        {
            bool authoritative = IsServerStarted;
            uint tick = authoritative ? _authoritativeTick : TimeManager.LocalTick;
            PlayerState state = authoritative ? _authoritativeState : _core.Predicted;
            PerformReconcile(new ReconcileData(tick, in state));
        }

        /// Runs on the server for the input it received, and on the owner for
        /// the input it is about to send — including every replay FishNet
        /// drives during a reconciliation, which arrives through this same
        /// method with `Replayed` set.
        ///
        /// `state` is a BIT MASK, not a sequence of states
        /// (ReplicateState.cs:9-33): `Created` means real data was there for
        /// this tick, and its ABSENCE on a ticked entry means FishNet is
        /// filling a gap it never received. That distinction is the freshness
        /// flag the server hands the world (Р25) — it must never be read as an
        /// equality against a single enum value.
        [Replicate]
        void PerformReplicate(ReplicateData data, ReplicateState state = ReplicateState.Invalid,
            Channel channel = Channel.Unreliable)
        {
            // TryDecode, not Decode: on the server these bytes came off the
            // wire from a client (Р82, app-ltw). A refusal means there is no
            // input for this tick — the world's own starvation handling (Task
            // 36) covers that case, and inventing a default input here would
            // hand it a deliberate standstill it could not tell from a real one.
            if (!data.TryToInput(in _cfg, out SimInput input)) return;

            if (IsServerStarted)
                _core.RecordServerInput(data.GetTick(), in input, state.ContainsCreated());
            else
                _core.Predict(in input, in _cfg);
        }

        [Reconcile]
        void PerformReconcile(ReconcileData data, Channel channel = Channel.Unreliable)
        {
            _core.ApplyReconcile(data.GetTick(), in data.State);
        }
    }

    /// What the server must feed the world for one player on one tick (Stage 2
    /// Task 34; consumed by Task 36).
    ///
    /// `IsFresh` is false when FishNet ran the tick with data it never
    /// received — a repeat under packet loss. The world needs the difference:
    /// a held input and a starved one produce the same `SimInput` and mean
    /// opposite things (Р25, `NetConfig.InputStarveTicks`).
    public readonly struct ServerTickInput
    {
        public readonly uint Tick;
        public readonly SimInput Input;
        public readonly bool IsFresh;

        public ServerTickInput(uint tick, in SimInput input, bool isFresh)
        {
            Tick = tick;
            Input = input;
            IsFresh = isFresh;
        }
    }

    /// Everything `PlayerNetworkController` decides, with no FishNet in it
    /// (Stage 2 Task 34). Nothing here needs a network runtime, which is why
    /// all of it is under test.
    public sealed class PlayerPredictionCore
    {
        SimInput _pending;
        PlayerState _predicted;
        uint _lastReconciledTick;
        bool _ownDeathReported;
        float _lastCorrectionMeters;
        ServerTickInput _lastServerInput;

        /// The latest sampled frame, held WHOLE and unmodified.
        public SimInput PendingInput => _pending;

        /// The client's own copy of its player, advanced by prediction and
        /// reset by every reconcile.
        public PlayerState Predicted => _predicted;

        /// The tick of the last reconcile applied — FishNet's tick for it, see
        /// `ReconcileData`'s doc on who really stamps that field.
        public uint LastReconciledTick => _lastReconciledTick;

        /// A `PlayerDied` for our own index has been seen (Р41/Р59).
        public bool OwnDeathReported => _ownDeathReported;

        /// How far the last correction moved the player, in metres — measured
        /// before the predicted copy was overwritten, because afterwards the
        /// distance no longer exists anywhere. Drives the snap-versus-smooth
        /// decision (Р78); see `PlayerNetworkController`'s doc for why it is
        /// not the smoother's own teleport threshold.
        public float LastCorrectionMeters => _lastCorrectionMeters;

        /// The decoded input for the tick the server just ran (Task 36's
        /// contract).
        public ServerTickInput LastServerInput => _lastServerInput;

        /// True while this client may advance its own copy at all.
        public bool IsPredicting => ShouldPredict(_ownDeathReported, _predicted.Alive);

        /// BOTH triggers, never one (Р41/Р59, spec §3.9). The event travels
        /// unreliably (Р58), so waiting only for it would keep predicting a
        /// corpse; the state arrives only with a reconcile, so waiting only for
        /// that would keep predicting for as long as the state packet is late.
        /// A prediction that outlives the player rolls back by up to
        /// `DashSpeed 30 * RTT 0.12` ~ 3.6 m when the truth catches up.
        ///
        /// A player the server has not described yet reads as NOT alive, and
        /// that is the same deliberate answer: `default(PlayerState)` is not
        /// somebody to predict, it is somebody we have not met.
        public static bool ShouldPredict(bool ownDeathReported, bool aliveInAuthoritativeState)
            => !ownDeathReported && aliveInAuthoritativeState;

        /// Whether a correction of `correctionMeters` must snap rather than be
        /// smoothed (Р78; `threshold` is `NetConfig.ReconcileSnapMeters`).
        public static bool ShouldSnapCorrection(float correctionMeters, float threshold)
            => correctionMeters > threshold;

        /// Stores the latest frame, WHOLE, and coalesces NOTHING.
        ///
        /// The Task 3 spike merged edge requests here (`_pending.DashRequested
        /// || input.DashRequested`, carryover-t30). That was a cosmetic tick
        /// shift until Task 10 made the edge-request rate limit part of the
        /// HASHED per-player state: since then, presenting a dash on a
        /// different tick than the server consumes it drives
        /// `DashRequestCooldownTicks` apart on the two copies and mispredicts
        /// the dash itself, not merely where it started. Holding an edge until
        /// it is consumed belongs to the sampler, which knows when the frame
        /// was consumed and can clear the latch (Р35: `SampleFrame` before the
        /// send, `ClearLatches` after) — this class cannot know either.
        public void SetPendingInput(in SimInput input) => _pending = input;

        /// One-way for the lifetime of this player. There is no respawn inside
        /// a match (ADR-001), so nothing legitimately un-kills the local
        /// player; clearing the latch on a later "alive" reconcile would let a
        /// state packet older than the death event resurrect prediction for
        /// exactly the window the double trigger exists to cover. A new match
        /// means a new object (Task 41/44).
        public void NotifyOwnDeath() => _ownDeathReported = true;

        /// Accepts the world's answer: the predicted copy becomes the
        /// authoritative state outright. Everything `PlayerPrediction.Step`
        /// advances is corrected together — a partial copy would leave timers,
        /// stamina and the gate counters permanently adrift.
        public void ApplyReconcile(uint tick, in PlayerState authoritativeState)
        {
            _lastCorrectionMeters = math.distance(_predicted.Pos, authoritativeState.Pos);
            _predicted = authoritativeState;
            _lastReconciledTick = tick;
        }

        /// One predicted tick, over the DECODED input (Р34 — and structurally
        /// so: the only way to reach here from a raw sample is through
        /// `ReplicateData`, which quantizes).
        ///
        /// Refusing to run for a player who is not alive is this method's job,
        /// not `PlayerPrediction.Step`'s: the world advances a corpse through a
        /// different path entirely (`PlayerMovementSystem.UpdateDead`), and
        /// `Step`'s own doc names the caller as the one who must enforce it.
        public void Predict(in SimInput decodedInput, in SimConfig cfg)
        {
            if (!IsPredicting) return;
            PlayerPrediction.Step(ref _predicted, in decodedInput, in cfg);
        }

        /// Server side: publish what the world must consume for this tick.
        public void RecordServerInput(uint tick, in SimInput decodedInput, bool isFresh)
            => _lastServerInput = new ServerTickInput(tick, in decodedInput, isFresh);
    }
}
