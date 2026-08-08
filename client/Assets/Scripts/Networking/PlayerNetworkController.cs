using FishNet.Managing.Predicting;
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
    /// `SnapshotQueue` already use for the client's other moving parts. Even
    /// the two BRANCHES this class contains are pure functions on the core
    /// (`RouteReplicate`, `ShouldSendReconcile`), so the routing itself is
    /// pinned by a table rather than by a runtime nobody can start in a test.
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
    /// EVERY MUTATOR IS `internal` (fix-round 1). The world outside this
    /// assembly may READ what the controller believes; it may not push a
    /// `SimInput` into prediction, hand it a state or fake a reconcile. That is
    /// what keeps Р34 structural: the only route from a raw sample to
    /// `PlayerPrediction.Step` runs through `ReplicateData`, which quantizes.
    /// The test assembly sees them through `InternalsVisibleTo`
    /// (Networking/AssemblyInfo.cs, same form Ring.Simulation already uses).
    ///
    /// RECONCILIATION SMOOTHING (Р78/Р106) — the contract, not the wiring.
    /// A correction farther than `NetConfig.ReconcileSnapMeters` (default 1.0)
    /// reads as a teleport and must SNAP; anything smaller must be smoothed
    /// over the graphical transform. The measurement belongs here and only
    /// here, and WHEN it is taken is the whole of its correctness — see
    /// `PlayerPredictionCore.FinishReconcile`.
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
    ///   2. QUANTITY. It is not a correction threshold at all. What it is
    ///      compared against is the transform delta BETWEEN TWO TICKS
    ///      (`UniversalTickSmoother.cs:829-831`: `prevValues` -> the next queued
    ///      properties over `duration = _tickDelta`), and legitimate movement
    ///      and a reconciliation correction enter that one number
    ///      indistinguishably. No value of it can mean "snap when the
    ///      CORRECTION exceeds x", because the quantity it gates is not the
    ///      correction.
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
        bool _hasAuthoritativeState;

        /// The runtime-free half of this component. Public because Task 36 and
        /// Task 44 READ it — the server loop takes `LastServerInput`, the
        /// presentation side takes `Predicted` and `LastCorrectionMeters`.
        /// Everything that CHANGES it is internal.
        public PlayerPredictionCore Core => _core;

        /// Balance numbers for this match. Until it is called the component is
        /// inert: no replicate is built and no reconcile is created, because
        /// every codec mapping and every step of prediction is a function of
        /// this config and a zeroed one would encode garbage. Called by the
        /// match bootstrap (Task 41/44) once the config is resolved.
        internal void Configure(in SimConfig cfg)
        {
            _cfg = cfg;
            _configured = true;
        }

        /// The latest sampled frame, whole. Called by the backend every frame
        /// (Р35: `SampleFrame` before the send, `ClearLatches` after the input
        /// is consumed). See `PlayerPredictionCore.SetPendingInput` for why
        /// nothing is coalesced here.
        internal void SetPendingInput(in SimInput input) => _core.SetPendingInput(in input);

        /// Our own `PlayerDied` arrived (Р41/Р59). Task 44 owns the
        /// subscription — this is the seam it calls.
        internal void NotifyOwnDeath() => _core.NotifyOwnDeath();

        /// The world's authoritative state for this player at `tick` — what
        /// `CreateReconcile` sends back (spec §3.9: from the WORLD, not from a
        /// server-side predicted copy). Task 36 calls this after the world has
        /// ticked. Until the first call the server sends NO reconcile at all
        /// (see `CreateReconcile`).
        internal void SetAuthoritativeState(uint tick, in PlayerState state)
        {
            _authoritativeTick = tick;
            _authoritativeState = state;
            _hasAuthoritativeState = true;
        }

        /// The correction measurement is closed HERE, not in `[Reconcile]`.
        /// `PredictionManager` runs one cycle per state packet: `OnReconcile`
        /// (PredictionManager.cs:667-668), which is what ultimately invokes our
        /// `[Reconcile]` method (`Reconcile_Client`'s last line,
        /// NetworkBehaviour.Prediction.cs:1435), and only THEN the replay loop
        /// up to `lastLocalTickCompleted` (:702-721). `OnPostReconcile`
        /// (declared :183, invoked :723-724) is the first moment the predicted
        /// copy is back on the newest tick — which is the only moment at which
        /// "how far did the picture jump" is a real quantity.
        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            PredictionManager pm = PredictionManager;
            if (pm != null) pm.OnPostReconcile += PredictionManager_OnPostReconcile;
        }

        public override void OnStopNetwork()
        {
            PredictionManager pm = PredictionManager;
            if (pm != null) pm.OnPostReconcile -= PredictionManager_OnPostReconcile;
            base.OnStopNetwork();
        }

        void PredictionManager_OnPostReconcile(uint clientTick, uint serverTick)
            => _core.FinishReconcile();

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
        /// On the server the data is the WORLD's — and there is NO reconcile at
        /// all until the world has produced one (fix-round 1). Before that,
        /// `_authoritativeState` is `default(PlayerState)`, whose `Alive` is
        /// false and whose position is the arena centre: sending it would tell
        /// every client it is dead at the origin, on every tick between spawn
        /// and the first world update.
        ///
        /// On a client the data is the client's own predicted copy, which is
        /// the package's documented fallback role for a locally built
        /// reconcile: it fills the gap when the server's state packet is lost,
        /// and is discarded the moment a real one arrives.
        public override void CreateReconcile()
        {
            bool authoritative = IsServerStarted;
            if (!PlayerPredictionCore.ShouldSendReconcile(authoritative, _hasAuthoritativeState))
                return;

            uint tick = authoritative ? _authoritativeTick : TimeManager.LocalTick;
            PlayerState state = authoritative ? _authoritativeState : _core.Predicted;
            PerformReconcile(new ReconcileData(tick, in state));
        }

        /// Runs on the server for the input it received, on the owner for the
        /// input it is about to send, and — this is the part that is easy to
        /// get wrong — on OTHER clients too whenever the object has
        /// `EnableStateForwarding` on (NetworkBehaviour.Prediction.cs:611 exits
        /// early only when state forwarding is OFF and we are not the server).
        /// Hence the routing below rather than a plain `if server else`: a
        /// non-owner client predicting somebody else's player would be a client
        /// deciding a game outcome (CR 3), and it would do it from
        /// `default(ReplicateData)` (:620-624 -> `ReplicateDefaultData`), which
        /// decodes to an aim point in the far corner of the arena.
        ///
        /// `state` is a BIT MASK, not a sequence of states
        /// (ReplicateState.cs:9-33): `Created` means real data was there for
        /// this tick, and its ABSENCE on a ticked entry means FishNet
        /// SUBSTITUTED `default(T)` (:698-712, `ReplicateDataContainer<T>
        /// .GetDefault`). It is not a repeat of the last input — see
        /// `PlayerPredictionCore.RecordServerInput` for why the difference is
        /// load-bearing.
        [Replicate]
        void PerformReplicate(ReplicateData data, ReplicateState state = ReplicateState.Invalid,
            Channel channel = Channel.Unreliable)
        {
            ReplicateRoute route = PlayerPredictionCore.RouteReplicate(
                IsServerStarted, IsOwner, state.ContainsCreated());
            if (route == ReplicateRoute.Ignore) return;

            // TryDecode, not Decode: on the server these bytes came off the
            // wire from a client (Р82, app-ltw). A refusal means there is no
            // input for this tick — the world's own starvation handling (Task
            // 36) covers that case, and inventing a default input here would
            // hand it a deliberate standstill it could not tell from a real one.
            if (!data.TryToInput(in _cfg, out SimInput input)) return;

            if (route == ReplicateRoute.RecordForServer)
                _core.RecordServerInput(data.GetTick(), in input);
            else
                _core.Predict(in input, in _cfg);
        }

        [Reconcile]
        void PerformReconcile(ReconcileData data, Channel channel = Channel.Unreliable)
        {
            _core.BeginReconcile(data.GetTick(), in data.State);
        }
    }

    /// What `PerformReplicate` must do with one entry, as a value rather than a
    /// branch (Stage 2 Task 34 fix-round 1) — so the routing is pinned by a
    /// truth table in `ReconcileCodecTests` instead of by a network runtime.
    public enum ReplicateRoute : byte
    {
        /// Not ours to act on: another client's object under state forwarding,
        /// or a tick the server has no real data for.
        Ignore = 0,
        /// The server: publish the decoded input for the world (Task 36).
        RecordForServer = 1,
        /// The owning client: advance our own predicted copy.
        PredictLocally = 2,
    }

    /// The last input the server actually RECEIVED for this player, and the
    /// tick it was for (Stage 2 Task 34; consumed by Task 36).
    ///
    /// There is no "is this fresh" flag, because nothing unfresh is ever
    /// published — see `PlayerPredictionCore.RecordServerInput`. Starvation is
    /// therefore a question about TICKS, not about a flag: the world compares
    /// this `Tick` against the tick it is running and declares the player
    /// starved past `NetConfig.InputStarveTicks` (Р25), which is exactly the
    /// quantity that rule is written in.
    public readonly struct ServerTickInput
    {
        public readonly uint Tick;
        public readonly SimInput Input;

        public ServerTickInput(uint tick, in SimInput input)
        {
            Tick = tick;
            Input = input;
        }
    }

    /// Everything `PlayerNetworkController` decides, with no FishNet in it
    /// (Stage 2 Task 34). Nothing here needs a network runtime, which is why
    /// all of it is under test. Readers are public; every mutator is internal
    /// and reachable only through the controller (fix-round 1).
    public sealed class PlayerPredictionCore
    {
        SimInput _pending;
        PlayerState _predicted;
        uint _lastReconciledTick;
        bool _ownDeathReported;
        float _lastCorrectionMeters;
        float2 _preReconcilePos;
        bool _reconcileOpen;
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

        /// How far the PICTURE jumped at the last reconciliation, in metres —
        /// see `FinishReconcile` for why that is a different number from "how
        /// far the server disagreed". Drives the snap-versus-smooth decision
        /// (Р78) and is the quantity the lag gate's own median is written in
        /// (§3.14 item 7).
        public float LastCorrectionMeters => _lastCorrectionMeters;

        /// The last input the server actually received, and its tick (Task 36's
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
        /// Strictly above: the config reads "above this a correction is a
        /// teleport", so the boundary value is still a correction.
        public static bool ShouldSnapCorrection(float correctionMeters, float threshold)
            => correctionMeters > threshold;

        /// What one `[Replicate]` entry means for us. Three inputs, and each
        /// one of them earns its place:
        ///   * `isServerStarted` — the server consumes input, it never predicts.
        ///   * `isOwner` — WITHOUT it, a client running somebody else's
        ///     forwarded replicate would predict a player it does not own
        ///     (NetworkBehaviour.Prediction.cs:611 lets that call through
        ///     whenever `EnableStateForwarding` is on), which is a client
        ///     deciding a game outcome — CR 3.
        ///   * `dataIsFresh` — `ReplicateState.Created`. On the server, a tick
        ///     without it carries `default(T)`, not the previous input, so
        ///     there is nothing to publish. On the OWNER it is deliberately
        ///     ignored: prediction must keep advancing through its own
        ///     replays, which is what prediction IS.
        public static ReplicateRoute RouteReplicate(bool isServerStarted, bool isOwner, bool dataIsFresh)
        {
            if (isServerStarted)
                return dataIsFresh ? ReplicateRoute.RecordForServer : ReplicateRoute.Ignore;

            return isOwner ? ReplicateRoute.PredictLocally : ReplicateRoute.Ignore;
        }

        /// Whether a reconcile may be built at all. A client always may (its
        /// own predicted copy is a legitimate local fallback); the server may
        /// only once the world has actually given it a state, or it would
        /// broadcast `default(PlayerState)` — dead, at the arena origin — to
        /// everybody.
        public static bool ShouldSendReconcile(bool isServerStarted, bool hasAuthoritativeState)
            => !isServerStarted || hasAuthoritativeState;

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
        internal void SetPendingInput(in SimInput input) => _pending = input;

        /// One-way for the lifetime of this player. There is no respawn inside
        /// a match (ADR-001), so nothing legitimately un-kills the local
        /// player; clearing the latch on a later "alive" reconcile would let a
        /// state packet older than the death event resurrect prediction for
        /// exactly the window the double trigger exists to cover. A new match
        /// means a new object — and an OBJECT POOL would mean the same object
        /// with the latch still down, which is why Task 41/44 must reset the
        /// core if it ever turns pooling on.
        internal void NotifyOwnDeath() => _ownDeathReported = true;

        /// Accepts the world's answer: the predicted copy becomes the
        /// authoritative state outright. Everything `PlayerPrediction.Step`
        /// advances is corrected together — a partial copy would leave timers,
        /// stamina and the gate counters permanently adrift.
        ///
        /// This is the OPENING half of a reconciliation. It records where the
        /// picture stood, and `FinishReconcile` closes the measurement once the
        /// replay has caught back up.
        internal void BeginReconcile(uint tick, in PlayerState authoritativeState)
        {
            _preReconcilePos = _predicted.Pos;
            _reconcileOpen = true;
            _predicted = authoritativeState;
            _lastReconciledTick = tick;
        }

        /// Closes the correction measurement, AFTER the replay.
        ///
        /// THIS IS THE WHOLE POINT OF SPLITTING THE PAIR. When `[Reconcile]`
        /// runs, the predicted copy stands on the NEWEST local tick while the
        /// authoritative state is the world as of `ClientStateTick` — roughly
        /// `RTT/2 + StateInterpolation` ticks in the past. Measuring the
        /// distance between those two is measuring how far the player has
        /// MOVED since, not how wrong the client was: at 30 Hz on the run that
        /// is around a metre of "correction" with prediction working
        /// perfectly, and several metres in a dash, which would snap the
        /// graphic on nearly every reconcile and would report the lag gate's
        /// own median (§3.14 item 7, threshold 0.25 m) at four times its limit
        /// on flawless code.
        ///
        /// After FishNet has replayed the queue up to the newest tick
        /// (PredictionManager.cs:702-721) the predicted copy is back where it
        /// belongs, and the distance from `_preReconcilePos` is the honest
        /// quantity: how far the picture jumped. With prediction perfect the
        /// replay reproduces the same states bit for bit and it is EXACTLY
        /// zero.
        ///
        /// Idempotent and safe to call spuriously: `OnPostReconcile` fires once
        /// per state packet for the whole `PredictionManager`, including cycles
        /// in which this behaviour reconciled nothing.
        internal void FinishReconcile()
        {
            if (!_reconcileOpen) return;
            _reconcileOpen = false;
            _lastCorrectionMeters = math.distance(_preReconcilePos, _predicted.Pos);
        }

        /// One predicted tick, over the DECODED input (Р34 — and structurally
        /// so: the only way to reach here from a raw sample is through
        /// `ReplicateData`, which quantizes, and this method is internal so no
        /// other assembly can hand it a raw one).
        ///
        /// Refusing to run for a player who is not alive is this method's job,
        /// not `PlayerPrediction.Step`'s: the world advances a corpse through a
        /// different path entirely (`PlayerMovementSystem.UpdateDead`), and
        /// `Step`'s own doc names the caller as the one who must enforce it.
        internal void Predict(in SimInput decodedInput, in SimConfig cfg)
        {
            if (!IsPredicting) return;
            PlayerPrediction.Step(ref _predicted, in decodedInput, in cfg);
        }

        /// Server side: publish the input the world must consume, and the tick
        /// it belongs to.
        ///
        /// ONLY EVER CALLED FOR DATA THAT REALLY ARRIVED (`RouteReplicate`
        /// gates it on `ReplicateState.Created`). FishNet does NOT repeat the
        /// last input when a client's packet is missing — it substitutes
        /// `default(T)` (NetworkBehaviour.Prediction.cs:698-712), and for this
        /// wire format `default` is not a neutral standstill: all-zero bytes
        /// decode to an aim point at `(-3R, -3R)`, the far corner of the aim
        /// domain. Publishing that would feed the world a deliberate-looking
        /// input that no player ever sent. So nothing unfresh is published at
        /// all, and the world detects starvation from the gap between this tick
        /// and the tick it is running (Р25).
        internal void RecordServerInput(uint tick, in SimInput decodedInput)
            => _lastServerInput = new ServerTickInput(tick, in decodedInput);
    }
}
