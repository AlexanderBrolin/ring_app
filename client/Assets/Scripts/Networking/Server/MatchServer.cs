using System;
using System.Diagnostics;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Transporting;
using FishNet.Transporting;
using Ring.Data;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;

namespace Ring.Networking.Server
{
    /// The server's per-match tick loop (Stage 2 Task 36, spec §3.7/§3.11, Р22/
    /// Р60/Р151): an ordinary class — NOT a MonoBehaviour/NetworkBehaviour — built
    /// by the Ф8 bootstrap around an already-running `NetworkManager`, and the
    /// FIRST server-side consumer of FishNet's own tick events and Broadcast API.
    ///
    /// NOT UNIT-TESTED DIRECTLY, ON PURPOSE (same split as `SnapshotAssembler`/
    /// `PlayerNetworkController`): every decision worth pinning by a test is
    /// either already covered where it lives (`InputStarvation`,
    /// `EffectiveInputBatch`, `TickTimeAccumulator` here; `SnapshotAssembler`,
    /// `PlayerPredictionCore` elsewhere) or is FishNet wiring that only a live
    /// `NetworkManager` can exercise — proven by R-COMPILE and, eventually, the
    /// two-process milestone В1 (task-36-brief §0/§3), not by EditMode.
    ///
    /// Р151 — A FRESH `SimulationWorld` AND `SnapshotAssembler` EVERY
    /// `StartMatch`. Both are constructed here, together, every time — never
    /// reused across a restart. `SnapshotAssembler`'s own class doc explains
    /// why: every byte of its per-connection memory (carry queue, resend
    /// history, subscription expiries) ages against `SimulationWorld.
    /// CurrentTick`, and a restart rewinds that clock to zero — an instance
    /// that outlived the restart would compute negative ages against a fresh
    /// world and, worst of all silently, refuse every new delivery (Р58's
    /// redundancy, dead for the whole next match). The symmetry Р151 names is
    /// exact: on the CLIENT a restart calls `EventDedup.Reset` on one surviving
    /// instance; on the SERVER, because the per-connection state here is an
    /// order of magnitude larger and owned entirely by `SnapshotAssembler`
    /// itself (no public reset seam exists, nor should one — Task 28 sized the
    /// class around "one instance, one match"), the equivalent move is a fresh
    /// instance rather than an in-place reset.
    ///
    /// STARTMATCH IS RE-ENTRANT BY DESIGN: calling it while a match is already
    /// running implicitly stops the previous one first (see `StartMatch`'s own
    /// body) rather than throwing. "Restart (Р60) = a new `StartMatch`" (§2.2)
    /// reads most naturally as the caller (Ф8's Task 40) simply calling
    /// `StartMatch` again with the next seed/epoch — requiring a separate
    /// `StopMatch` first would be an easy contract to forget and would double-
    /// subscribe `OnPostTick` if it were. `StopMatch` remains the ONLY way to
    /// end a match without immediately starting another (server shutdown,
    /// "all players disconnected", spec §3.11's exit codes 0/3/4).
    ///
    /// WHAT Ф8 MUST HAND IN (task-36-brief §5's contract): `connections[i]`
    /// and `controllers[i]` are assumed to be the SAME player, by index —
    /// `identityIndex`/`viewpointIndex` in every `SnapshotAssembler.BuildFor`
    /// call below are both `i`, matching that class's own doc ("the two are
    /// equal until spectating lands", Task 42). Both arrays must be non-empty
    /// and the same length; `playerCount` is derived from them rather than
    /// taken as a separate parameter, so the two can never disagree by a
    /// caller's mistake. Assigning connections to player slots, roster/join
    /// handling and re-spawning `PlayerNetworkController` objects belong to
    /// Ф8 (Task 38/39/41) — entirely outside this task's scope
    /// (task-36-brief §1's scope boundary).
    public sealed class MatchServer
    {
        readonly NetworkManager _nm;
        readonly NetConfig _netConfig;
        readonly TickTimeAccumulator _tickTime = new TickTimeAccumulator();
        readonly Stopwatch _stopwatch = new Stopwatch();

        // Stage 2 Task 33 carry-forward (server half, task-36-brief §2.2's last
        // bullet): a DEDICATED sink for DevLatencySetup.Apply's applied-facts
        // output, deliberately NOT one of SnapshotAssembler's per-connection
        // NetStats instances. The simulator it reads back from
        // (`TransportManager.LatencySimulator`) is ONE instance for the whole
        // transport, not per-connection, so the facts it produces describe this
        // SERVER PROCESS, not any single remote connection — writing them into
        // an arbitrary connection's counters would misattribute process-wide
        // configuration as if it were that one connection's own health.
        readonly NetStats _devStats = new NetStats();

        SimulationWorld _world;
        SnapshotAssembler _assembler;
        NetworkConnection[] _connections;
        PlayerNetworkController[] _controllers;
        ServerTickInput[] _lastInputsScratch;
        SimInput[] _effectiveInputsScratch;
        bool[] _starvedScratch;
        ushort _epoch;
        bool _running;

        /// Count/average/max of `OnPostTick`'s own wall-clock cost (spec §3.11)
        /// since the last `StartMatch` — Ф8 reads this to assemble the per-match
        /// log line; this class only keeps the numbers.
        public TickTimeAccumulator TickTime => _tickTime;

        /// The applied dev latency-simulator facts for THIS process (see the
        /// field's own doc for why this is a dedicated instance).
        public NetStats DevStats => _devStats;

        public MatchServer(NetworkManager networkManager, NetConfig netConfig)
        {
            _nm = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _netConfig = netConfig ?? throw new ArgumentNullException(nameof(netConfig));
        }

        /// Starts a match — or restarts one, if this instance is already
        /// running one (see the class doc's re-entrancy paragraph).
        /// `connections[i]`/`controllers[i]` must name the same player; both
        /// arrays are required, non-empty and the same length.
        public void StartMatch(long seed, in SimConfig simConfig, ushort epoch,
            NetworkConnection[] connections, PlayerNetworkController[] controllers)
        {
            if (connections == null) throw new ArgumentNullException(nameof(connections));
            if (controllers == null) throw new ArgumentNullException(nameof(controllers));
            if (connections.Length == 0)
            {
                throw new ArgumentException(
                    "MatchServer.StartMatch: a match needs at least one connection.", nameof(connections));
            }
            if (connections.Length != controllers.Length)
            {
                throw new ArgumentException(
                    $"MatchServer.StartMatch: connections.Length ({connections.Length}) must equal "
                    + $"controllers.Length ({controllers.Length}) — Ф8 must hand in the same player at "
                    + "the same index in both arrays.", nameof(controllers));
            }

            if (_running) StopMatch();

            int playerCount = controllers.Length;

            // Local first, committed together at the end — a throw partway
            // through construction (e.g. SnapshotAssembler's own fixed-part-
            // too-small guard) must never leave this instance half-updated,
            // holding a fresh world next to a stale assembler from the match
            // that just ended.
            var world = new SimulationWorld(seed, in simConfig, playerCount);
            var assembler = new SnapshotAssembler(in simConfig, _netConfig, connections.Length);
            var lastInputsScratch = new ServerTickInput[playerCount];
            var effectiveInputsScratch = new SimInput[playerCount];
            var starvedScratch = new bool[playerCount];

            _world = world;
            _assembler = assembler;
            _connections = connections;
            _controllers = controllers;
            _epoch = epoch;
            _lastInputsScratch = lastInputsScratch;
            _effectiveInputsScratch = effectiveInputsScratch;
            _starvedScratch = starvedScratch;
            _tickTime.Reset();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Stage 2 Task 33 carry-forward (task-33-report §7.1): the
            // simulator lives in EVERY process, so both the server (here) and
            // the client (Task 44) must call Apply on their OWN
            // TransportManager.LatencySimulator — DevLatencySetup.cs's own doc:
            // AddOutgoing only delays the OUTGOING side of whichever process
            // calls it.
            DevLatencySetup.Apply(_nm.TransportManager.LatencySimulator, _netConfig, _devStats);
#endif

            _nm.TimeManager.OnPostTick += OnPostTick;
            _running = true;
        }

        /// Ends the match without starting another — unsubscribes from
        /// `TimeManager.OnPostTick` and releases this match's world/assembler/
        /// scratch (a process may sit between matches for a while, e.g. a join
        /// window; there is no reason to hold the finished match's arrays for
        /// GC over that whole span). Idempotent: stopping an already-stopped
        /// instance is a no-op, not an error.
        public void StopMatch()
        {
            if (!_running) return;

            _nm.TimeManager.OnPostTick -= OnPostTick;
            _running = false;

            _world = null;
            _assembler = null;
            _connections = null;
            _controllers = null;
            _lastInputsScratch = null;
            _effectiveInputsScratch = null;
            _starvedScratch = null;
        }

        /// The whole per-tick pipeline (spec §3.7, Р22 — order is load-bearing,
        /// see task-36-brief §0a: `OnPostTick` is where the world steps because
        /// FishNet's own tick order has no subscriber-priority mechanism, and
        /// `OnPostTick` structurally runs after `[Replicate]` delivery).
        /// Subscribed in `StartMatch`, unsubscribed in `StopMatch` — never runs
        /// outside that span.
        void OnPostTick()
        {
            _stopwatch.Restart();

            int worldTick = _world.CurrentTick;
            int starveTicks = _netConfig.InputStarveTicks;

            // 1. Effective inputs (Р22 step 1, Р25). `LastServerInput` is
            // Task 34's contract (§8.1): only ever a REAL, freshly-arrived
            // input, never a repeat — so the gap between the tick the world is
            // about to run and that tick is exactly InputStarvation.Effective's
            // `ticksSinceLast` (task-34-report §8.1's own wording).
            for (int i = 0; i < _controllers.Length; i++)
                _lastInputsScratch[i] = _controllers[i].Core.LastServerInput;

            EffectiveInputBatch.Gather(_lastInputsScratch, worldTick, starveTicks,
                _effectiveInputsScratch, _starvedScratch);

            for (int i = 0; i < _starvedScratch.Length; i++)
                if (_starvedScratch[i]) _assembler.StatsFor(i).InputStarved++;

            // 2. The world steps exactly once, on the effective inputs — never
            // the raw ones (a stale/absent input must never reach TickAll
            // unmodified, see InputStarvation's own doc).
            _world.TickAll(_effectiveInputsScratch);

            // 3. One capture + wire-event expansion shared by every connection
            // this tick (SnapshotAssembler.BeginTick's own doc).
            _assembler.BeginTick(_world);

            // 4. Per-connection frame, Unreliable (spec §3.7 table Р27: state
            // travels unreliably). `identityIndex`/`viewpointIndex` are both
            // `i` — see the class doc's "what Ф8 must hand in" paragraph.
            for (int i = 0; i < _connections.Length; i++)
            {
                int len = _assembler.BuildFor(i, i, i, _epoch);
                var broadcast = new SnapshotBroadcast
                {
                    Tick = (uint)_world.CurrentTick,
                    MatchEpoch = _epoch,
                    Payload = new ArraySegment<byte>(_assembler.BufferFor(i), 0, len),
                };
                _nm.ServerManager.Broadcast(_connections[i], broadcast, channel: Channel.Unreliable);
            }

            // 5. Reconciliation source — the WORLD's own tick, not FishNet's
            // (task-34-report §8.1's warning, resolved here as task-36-brief
            // §2.2 directs): FishNet re-stamps the wire tick of a reconcile
            // regardless of what is passed in (Reconcile_* internals), so this
            // value only ever feeds `PlayerPredictionCore.LastReconciledTick` —
            // our own bookkeeping, never the packet. Runs AFTER the snapshot
            // send (step 4) and BEFORE `ClearEvents` (step 6), matching Р22's
            // fixed order and letting `SendStateUpdate` (§0a) pick it up the
            // same tick it was set.
            uint worldTickU = (uint)_world.CurrentTick;
            for (int i = 0; i < _controllers.Length; i++)
                _controllers[i].SetAuthoritativeState(worldTickU, _world.PlayerAt(i));

            // 6. LAST, always (Р22) — clearing before steps 3-5 would ship an
            // empty events block on a headless server with no render frame to
            // have drained it first, and every event since the last tick would
            // be lost rather than merely deferred.
            _world.ClearEvents();

            _stopwatch.Stop();
            _tickTime.Record(_stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// Pure batch form of the per-tick starvation pass (Stage 2 Task 36 §2.2
    /// point 1) — no FishNet, tested directly (`InputStarvationTests`). Mirrors
    /// the split `PlayerNetworkController`/`PlayerPredictionCore` already uses
    /// (Stage 2 Task 34): the FishNet-touching class stays thin, the decision
    /// lives beside it in a runtime-free type.
    public static class EffectiveInputBatch
    {
        /// `lastInputs[i]` is player i's `PlayerPredictionCore.LastServerInput`
        /// snapshot; `worldTick` is `SimulationWorld.CurrentTick` — task-34-report
        /// §8.1's own words: "the difference between the tick the world runs and
        /// `LastServerInput.Tick` is exactly `InputStarvation.Effective`'s input."
        /// Fills `effectiveInputs`/`starved` in place (no allocation — `MatchServer`
        /// reuses fixed per-match scratch arrays every tick) and returns the count
        /// of players found starved, the value `MatchServer` feeds nowhere else —
        /// PER-CONNECTION `NetStats.InputStarved` needs the `starved` span itself,
        /// which is why both are out parameters rather than just the count.
        public static int Gather(ReadOnlySpan<ServerTickInput> lastInputs, int worldTick,
            int starveTicks, Span<SimInput> effectiveInputs, Span<bool> starved)
        {
            if (effectiveInputs.Length != lastInputs.Length)
            {
                throw new ArgumentException(
                    $"EffectiveInputBatch.Gather: effectiveInputs.Length ({effectiveInputs.Length}) must equal "
                    + $"lastInputs.Length ({lastInputs.Length}).", nameof(effectiveInputs));
            }
            if (starved.Length != lastInputs.Length)
            {
                throw new ArgumentException(
                    $"EffectiveInputBatch.Gather: starved.Length ({starved.Length}) must equal "
                    + $"lastInputs.Length ({lastInputs.Length}).", nameof(starved));
            }

            int starvedCount = 0;
            for (int i = 0; i < lastInputs.Length; i++)
            {
                int ticksSinceLast = worldTick - (int)lastInputs[i].Tick;
                effectiveInputs[i] = InputStarvation.Effective(
                    in lastInputs[i].Input, ticksSinceLast, starveTicks, out bool isStarved);
                starved[i] = isStarved;
                if (isStarved) starvedCount++;
            }
            return starvedCount;
        }
    }

    /// Tiny pure accumulator for the server tick's own wall-clock cost (spec
    /// §3.11: "среднее и максимум времени тика" in the per-match log line —
    /// Ф8 assembles the log string, this only holds the numbers). `Stopwatch`-
    /// driven by the caller (`MatchServer.OnPostTick`), deliberately NOT Unity
    /// time: this measures the process, not the simulation, and the class
    /// itself stays free of any timing source so it is trivially testable on a
    /// handcrafted series of milliseconds.
    public sealed class TickTimeAccumulator
    {
        int _count;
        double _totalMs;
        double _maxMs;

        public int Count => _count;
        public double AverageMs => _count == 0 ? 0.0 : _totalMs / _count;
        public double MaxMs => _maxMs;

        public void Record(double milliseconds)
        {
            _count++;
            _totalMs += milliseconds;
            if (milliseconds > _maxMs) _maxMs = milliseconds;
        }

        public void Reset()
        {
            _count = 0;
            _totalMs = 0.0;
            _maxMs = 0.0;
        }
    }
}
