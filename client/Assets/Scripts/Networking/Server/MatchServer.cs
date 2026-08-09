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
    /// instance rather than an in-place reset. This class's OWN per-player
    /// freshness scratch (`_lastSeenInputTick`/`_lastFreshWorldTick`, fix-round
    /// 1 C1) gets exactly the same treatment for exactly the same reason — see
    /// `StartMatch`'s own comment on it.
    ///
    /// STARTMATCH IS RE-ENTRANT BY DESIGN: calling it while a match is already
    /// running implicitly stops the previous one first (see `StartMatch`'s own
    /// body) rather than throwing. "Restart (Р60) = a new `StartMatch`" (§2.2)
    /// reads most naturally as the caller (Ф8's Task 40) simply calling
    /// `StartMatch` again with the next seed/epoch — requiring a separate
    /// `StopMatch` first would be an easy contract to forget. `StopMatch`
    /// remains the ONLY way to end a match without immediately starting another
    /// (server shutdown, "all players disconnected", spec §3.11's exit codes
    /// 0/3/4).
    ///
    /// SUBSCRIPTION TIMING — FIXED FOR THE INSTANCE'S WHOLE LIFETIME
    /// (fix-round 1, I1). `OnPostTick` is subscribed exactly ONCE, in the
    /// CONSTRUCTOR — never in `StartMatch`/`StopMatch`. FishNet's tick events
    /// have no subscriber-priority mechanism (task-36-brief §0a): order is
    /// strictly FIFO by subscription time. `PlayerNetworkController`
    /// (`TickNetworkBehaviour`) subscribes to the SAME event at
    /// `OnStartNetwork` — i.e. at SPAWN
    /// (`FishNet.Utility.Template.TickNetworkBehaviour.OnStartNetwork_Internal`,
    /// `:43-49`) — and Ф8's own contract requires already-spawned controllers
    /// to be handed INTO `StartMatch` as an array, which means every player's
    /// spawn (the very first one, and any Task-40 restart's fresh replacement
    /// object) happens strictly AFTER this constructor has already run.
    /// Subscribing here, once, therefore GUARANTEES this handler's position in
    /// the FIFO chain is always before every controller's own — which is what
    /// makes `SetAuthoritativeState` (step 5) land before any controller's own
    /// `OnPostTick`-driven `CreateReconcile` reads it (see that step's own
    /// comment). Subscribing per-`StartMatch` instead would re-insert this
    /// handler at a DIFFERENT FIFO position on every restart relative to
    /// whatever new controllers had already spawned by then — silently
    /// re-introducing the very race this fixes. `StopMatch` therefore never
    /// touches the subscription either: it only flips `_running` and releases
    /// state, and `OnPostTick`'s own first line (`if (!_running) return;`)
    /// is what makes a stopped instance inert without an unsubscribe (this
    /// doubles as the fix for a mid-tick `StopMatch` called by some OTHER
    /// `OnPostTick` subscriber ahead of this one in the same FIFO chain —
    /// fix-round 1, M2 — this handler would otherwise run past `StopMatch`'s
    /// null-outs and NRE).
    ///
    /// ONE `MatchServer` PER PROCESS, DECLARED EXPLICITLY (fix-round 2, W9).
    /// The `OnPostTick` subscription above lives for the rest of the
    /// PROCESS's lifetime, not merely this instance's — there is no
    /// unsubscribe anywhere in this class, on purpose (the FIFO guarantee
    /// I1 above rests on exactly that permanence). A `MatchServer` that
    /// falls out of scope is therefore NOT garbage-collected: `_nm.
    /// TimeManager`'s own event still holds a live delegate reference into
    /// it for as long as the `NetworkManager` itself exists. Ф8's bootstrap
    /// must construct exactly ONE instance for the whole process and reuse
    /// it across every `StartMatch`/`StopMatch` cycle (see the re-entrancy
    /// paragraph above) — constructing a second one would leave the first's
    /// subscription still firing (inert while its own `_running` is false,
    /// per the SUBSCRIPTION TIMING paragraph, but never released) alongside
    /// the new one's.
    ///
    /// TWO READINGS OF `SimulationWorld.CurrentTick` PER CALL, NOT ONE
    /// (fix-round 1, M1). `CurrentTick` counts ticks the world has FINISHED —
    /// `SimulationWorld.TickAll`'s own length guards run first (fix-round 2,
    /// W7 — precise: a throw must never leave `_tick` half-bumped), and
    /// `_tick++` is the first MUTATION of state after them
    /// (`SimulationWorld.cs:196`), so the property still reads 0 before the
    /// match's first tick has run at all. `OnPostTick` reads it TWICE, on
    /// purpose, once on each side of `TickAll`, and the two readings mean
    /// different things: `preTickWorldTick` (before) is "how many ticks were
    /// complete coming into this call" — the domain freshness/starvation is
    /// measured in (step 1), because an input's staleness is a question about
    /// the tick it is ABOUT to drive, not the one just finished.
    /// `postTickWorldTick` (after, steps 4-5) is "the tick THIS call just
    /// finished" — the domain the outgoing snapshot's `Tick` field and
    /// `SetAuthoritativeState` use, because that is the tick whose STATE they
    /// are reporting. The two differ by exactly one for the whole match, which
    /// is correct and not an off-by-one to "fix" — each reading is the value
    /// the operation that consumes it actually needs.
    ///
    /// TICK-DOMAIN AGNOSTICISM (fix-round 1, C1 — the critical fix). Freshness
    /// is NEVER computed by subtracting a `ServerTickInput.Tick` from a world
    /// tick — the two are DIFFERENT COUNTERS with no fixed offset (see
    /// `EffectiveInputBatch.Gather`'s own doc for the full account and package
    /// citations). `_lastSeenInputTick`/`_lastFreshWorldTick` below are this
    /// class's per-player memory of "the last raw replicate tick observed" and
    /// "the world tick at which it was last seen to CHANGE" — allocated fresh
    /// every `StartMatch`, for the same Р151 reason `SnapshotAssembler` is: a
    /// restart's fresh world starts back at tick 0, and stale freshness memory
    /// from the previous match would misreport every player as having gone
    /// silent for however long the previous match ran.
    ///
    /// WHAT Ф8 MUST HAND IN (task-36-brief §5's contract, extended by
    /// fix-round 1 I1/I3.1): `connections[i]` and `controllers[i]` are assumed
    /// to be the SAME player, by index — `identityIndex`/`viewpointIndex` in
    /// every `SnapshotAssembler.BuildFor` call below are both `i`, matching
    /// that class's own doc ("the two are equal until spectating lands", Task
    /// 42). Both arrays must be non-empty and the same length; `playerCount`
    /// is derived from them rather than taken as a separate parameter, so the
    /// two can never disagree by a caller's mistake. `StartMatch` itself calls
    /// `Configure` on every controller (I3.1) — see that method's own comment
    /// — so Ф8 must NOT call it a second time (harmless if it does; the last
    /// call wins). **This `MatchServer` instance must be CONSTRUCTED BEFORE
    /// any `PlayerNetworkController` spawns** (I1, above) — Ф8's bootstrap
    /// order is therefore `NetworkManager` up → `new MatchServer(...)` →
    /// (later) spawn players → `StartMatch(..., connections, controllers)`.
    /// Assigning connections to player slots, roster/join handling and
    /// spawning `PlayerNetworkController` objects themselves belong to Ф8
    /// (Task 38/39/41) — entirely outside this task's scope (task-36-brief
    /// §1's scope boundary).
    ///
    /// OBSERVABILITY IS PARTIAL, ON PURPOSE (I3.2 — open end recorded here
    /// rather than guessed at). `CurrentWorldTick`/`StatsFor`/`TickTime`/
    /// `DevStats` are the raw NUMBERS spec §3.11's per-match log line needs;
    /// ASSEMBLING that structured line (matchId, seed, playerCount, duration,
    /// `WorldStats`, `DroppedEvents`, per-entity/event drop counts — none of
    /// which this class owns or should) is explicitly Т40/Т41's job, which the
    /// plan already routes through `MatchServer.cs` modifications. This class
    /// does not reach for `WorldStats`/`DroppedEvents` itself because it has
    /// no reason to hold a second opinion on numbers `SimulationWorld` already
    /// owns — Ф8 reads them off `_world`/`WorldStats` directly once it has a
    /// reason to (a public `World` accessor, if warranted, is that task's call
    /// to make, not this one's to add speculatively — AGENT.md rule 3).
    ///
    /// NETSTATS OWNERSHIP — HONEST CORRECTION (fix-round 1, M3). Task-36-brief
    /// §2.2, in paraphrase, states that `MatchServer` creates one `NetStats`
    /// instance per connection per match. That is imprecise: the per-connection `NetStats` instances are
    /// allocated by `SnapshotAssembler`'s OWN constructor (`SnapshotAssembler.
    /// cs`'s `Connection` type, `Stats = new NetStats()`), never by this class
    /// directly — `MatchServer` only WRITES into them (`InputStarved`, via
    /// `StatsFor`) and, by constructing a fresh assembler every `StartMatch`,
    /// is the reason a fresh set exists per match. The original report's §2.2
    /// citation restated the brief without noticing the distinction; this is
    /// the honest correction, not a new decision — `NetStats.cs` is closed
    /// (Task 23, do not touch) and the type genuinely has no public
    /// constructor seam for MatchServer to call other than the one
    /// `SnapshotAssembler` already uses. `_devStats` below is DELIBERATELY
    /// NOT one of those instances and is NOT "per connection per match" the
    /// way `NetStats`'s own class doc describes the rest of its fields — see
    /// its own field doc for why.
    ///
    /// HOW A MATCH ENDS (Stage 2 Task 40, spec §3.10/§3.11). The decision is
    /// not made here — `MatchEndPolicy` makes it, this class gathers its two
    /// inputs and executes the answer, in a fixed order: build the
    /// `MatchSummary` FIRST (`StopMatch` releases the world and the assembler
    /// the numbers come from), then send every live connection its OWN
    /// `MatchEndedNet` over `Reliable`, then `StopMatch`, then record the
    /// outcome. The whole pass sits between step 2 (`TickAll`) and step 3
    /// (`BeginTick`) of `OnPostTick`, so the last tick a match plays is a
    /// complete tick that simply sends no snapshot — and it leaves through the
    /// same `finally` every other path does, because the event buffer still
    /// has to be cleared and this tick's own cost still has to be counted.
    ///
    /// A DISCONNECT KILLS THE PLAYER, ON THE SAME TICK (spec §3.10). The pass
    /// above is preceded by one over `MatchEndPolicy.ShouldKillOnDisconnect`,
    /// for the same placement reason: a player whose connection died during
    /// this tick's own `Broadcast` must die in THIS frame's world, not the
    /// next one, or the frame the other players just received shows a body
    /// that is about to disappear for no visible reason. The `Alive` half of
    /// that predicate is what makes the pass idempotent across the rest of the
    /// match.
    ///
    /// `Outcome` IS POLLED, NOT EVENTED — the bootstrap asks. An event fired
    /// from the middle of `OnPostTick` would invite exactly the nested
    /// re-entrancy two paragraphs of this contract are already written against
    /// (a subscriber calling `StopMatch`/`StartMatch` from inside this
    /// handler's own stack), and the bootstrap has a tick of its own to ask
    /// on: it is a second subscriber to the SAME `OnPostTick` (§6k Р161).
    /// `StartMatch` puts `Outcome` back to `None`.
    ///
    /// THE SUMMARY IS THE ONE SOURCE OF THE MATCH'S NUMBERS. Every
    /// `MatchEndedNet` this class sends, and the structured per-match log line
    /// of spec §3.11 that Task 41 assembles (it owns `matchId`, which this
    /// assembly never sees), are both built from ONE `MatchSummary` captured
    /// once. Reading the same counters a second time — off a world that
    /// `StopMatch` has by then released — is the "two copies of one number"
    /// defect Р151 and the Task 39 ruling were both written against.
    ///
    /// RESTART IS A PROCEDURE WITH A MANDATORY ORDER (§6k Р164) — see
    /// `RestartMatch`'s own doc. The one part worth stating at the class level
    /// is what a restart does NOT touch: the roster and the handshake are
    /// objects of the JOIN PHASE and survive an epoch untouched, so nothing
    /// here recreates them, and the connections of the finished match are the
    /// connections of the next one.
    public sealed class MatchServer
    {
        readonly NetworkManager _nm;
        readonly NetConfig _netConfig;

        // Stage 2 Task 40: the end-of-match decision, handed in ready-made.
        // A finished instance rather than the number it was built from,
        // because the number is a CONVERSION (MatchMaxDurationSeconds *
        // TickRate) and this class has no business holding an opinion about
        // it — the bootstrap that reads the asset does the arithmetic once
        // and this class only asks the question.
        readonly MatchEndPolicy _endPolicy;

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
        // configuration as if it were that one connection's own health. This is
        // why it is allocated ONCE, here, for the object's whole lifetime,
        // rather than per `StartMatch` alongside the assembler's own instances
        // (fix-round 1, M3): it is not match-scoped state, it is process-scoped
        // configuration echo.
        readonly NetStats _devStats = new NetStats();

        SimulationWorld _world;
        SnapshotAssembler _assembler;
        NetworkConnection[] _connections;
        PlayerNetworkController[] _controllers;
        ServerTickInput[] _lastInputsScratch;
        SimInput[] _effectiveInputsScratch;
        bool[] _starvedScratch;

        // Fix-round 1, C1: per-player change-detection memory for
        // EffectiveInputBatch.Gather — see the class doc's "TICK-DOMAIN
        // AGNOSTICISM" paragraph and Gather's own doc for the full account.
        uint[] _lastSeenInputTick;
        int[] _lastFreshWorldTick;

        ushort _epoch;
        bool _running;

        // Stage 2 Task 40. `_outcome` is `None` for as long as a match is
        // running and for as long as none has run yet; `_lastSummary` is
        // meaningful only while `_outcome` is not `None` (StartMatch clears
        // both, so a summary can never outlive the outcome that vouches for
        // it).
        MatchEndReason _outcome;
        MatchSummary _lastSummary;

        /// Count/average/max of `OnPostTick`'s own wall-clock cost (spec §3.11)
        /// since the last `StartMatch` — Ф8 reads this to assemble the per-match
        /// log line; this class only keeps the numbers.
        public TickTimeAccumulator TickTime => _tickTime;

        /// The applied dev latency-simulator facts for THIS process (see the
        /// field's own doc for why this is a dedicated instance).
        public NetStats DevStats => _devStats;

        /// The world's own tick right now (I3.2) — 0 when no match is running.
        /// This is the POST-TickAll reading (see the class doc's "two
        /// readings" paragraph): between ticks, "how many ticks this match has
        /// completed". `OnPostTick` itself also reads a PRE-TickAll value for
        /// freshness, which this accessor deliberately does not expose — there
        /// is no external consumer for it, and adding one speculatively would
        /// be a public API nobody asked for (AGENT.md rule 3).
        public int CurrentWorldTick => _world?.CurrentTick ?? 0;

        /// This connection slot's counters (I3.2) — delegates to the
        /// assembler, which is where they are actually allocated (see the
        /// class doc's honest NetStats-ownership correction, M3). Throws when
        /// no match is running: there is no valid slot range to answer for,
        /// and a silent default would be indistinguishable from "connection 0,
        /// zero drops so far".
        public NetStats StatsFor(int connectionSlot)
        {
            if (_assembler == null)
                throw new InvalidOperationException("MatchServer.StatsFor: no match is running.");
            return _assembler.StatsFor(connectionSlot);
        }

        /// Why a match stopped — `None` while one is running (and before the
        /// first has ever started). The bootstrap (Task 41) polls this to
        /// learn a match is over and turns it into the process's exit code
        /// through `MatchEndPolicy.ExitCodeFor`; see the class doc for why
        /// this is a property and not an event.
        public MatchEndReason Outcome => _outcome;

        /// The numbers of the match named by `Outcome` — the ONE capture every
        /// `MatchEndedNet` was built from, and the same one spec §3.11's log
        /// line must be built from (see the class doc). Meaningful only while
        /// `Outcome` is not `None`: `StartMatch` clears it together with the
        /// outcome, so a caller that checks `Outcome` first can never read a
        /// previous match's numbers.
        public MatchSummary LastSummary => _lastSummary;

        public MatchServer(NetworkManager networkManager, NetConfig netConfig, MatchEndPolicy endPolicy)
        {
            _nm = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _netConfig = netConfig ?? throw new ArgumentNullException(nameof(netConfig));
            _endPolicy = endPolicy ?? throw new ArgumentNullException(nameof(endPolicy));

            // Fix-round 1, I1 — see the class doc's "SUBSCRIPTION TIMING"
            // paragraph for why this happens exactly once, here, and never
            // again in StartMatch/StopMatch.
            _nm.TimeManager.OnPostTick += OnPostTick;
        }

        /// Starts a match — or restarts one, if this instance is already
        /// running one (see the class doc's re-entrancy paragraph).
        /// `connections[i]`/`controllers[i]` must name the same player; both
        /// arrays are required, non-empty and the same length. Calls
        /// `Configure` on every controller (I3.1) — a match this method
        /// returns from is therefore never silently inert.
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

            // Fix-round 1, C1: fresh freshness-memory every match (Р151's own
            // reasoning applies here too — see the class doc). `uint.MaxValue`
            // can never collide with a real FishNet tick, so every player
            // starts at that sentinel — but fix-round 2 (W6) corrects two
            // false claims an earlier draft of this comment made about what
            // happens next.
            //
            // THE SENTINEL DOES NOT SURVIVE "FOREVER" FOR A SILENT PLAYER: it
            // lives only until this player's very FIRST `Gather` call, win or
            // lose. `default(ServerTickInput).Tick` is `0`, not
            // `uint.MaxValue` — so a player who has never sent a single input
            // reads `Tick == 0` on that first call, which already differs
            // from the sentinel and is therefore detected as "a change" right
            // there, exactly like a genuine first input whose own raw tick
            // happens to be `0` would be. The two are LATENTLY
            // indistinguishable this way (a real ambiguity, though it does
            // not bite in practice — a connected client's own `TimeManager.
            // LocalTick` is never actually `0` by the time it can replicate
            // anything — named here rather than left implicit). Either way
            // `lastFreshWorldTick[i]` lands at the world tick of that first
            // `Gather` call — `0` at match start, since `SimulationWorld`
            // always begins at `CurrentTick 0` (its own constructor) — so a
            // player who never sends anything still starves exactly
            // `starveTicks` ticks after match start: the SAME outcome the
            // earlier wording described, reached by the OPPOSITE mechanism
            // (the sentinel evaporating on tick one, not surviving it).
            var lastSeenInputTick = new uint[playerCount];
            var lastFreshWorldTick = new int[playerCount];
            for (int i = 0; i < playerCount; i++) lastSeenInputTick[i] = uint.MaxValue;

            _world = world;
            _assembler = assembler;
            _connections = connections;
            _controllers = controllers;
            _epoch = epoch;
            _lastInputsScratch = lastInputsScratch;
            _effectiveInputsScratch = effectiveInputsScratch;
            _starvedScratch = starvedScratch;
            _lastSeenInputTick = lastSeenInputTick;
            _lastFreshWorldTick = lastFreshWorldTick;
            _tickTime.Reset();

            // Stage 2 Task 40: a running match has no outcome, and no summary
            // to answer for one. Cleared together so the pair can never
            // disagree (see the fields' own note).
            _outcome = MatchEndReason.None;
            _lastSummary = default;

            // Fix-round 1, I3.1: without this, a match's controllers stay
            // `!_configured` forever (PlayerNetworkController.TimeManager_
            // OnTick/CreateReconcile both early-return on that flag) —
            // structurally silent, not a loud failure. MatchServer is the one
            // caller that always holds both the config and the controller
            // array at the same time, so it is the natural (and now the only)
            // place this gets called.
            for (int i = 0; i < controllers.Length; i++)
                controllers[i].Configure(in simConfig);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Stage 2 Task 33 carry-forward (task-33-report §7.1): the
            // simulator lives in EVERY process, so both the server (here) and
            // the client (Task 44) must call Apply on their OWN
            // TransportManager.LatencySimulator — DevLatencySetup.cs's own doc:
            // AddOutgoing only delays the OUTGOING side of whichever process
            // calls it.
            DevLatencySetup.Apply(_nm.TransportManager.LatencySimulator, _netConfig, _devStats);
#endif

            _running = true;
        }

        /// Restarts the RUNNING match under a new epoch (Stage 2 Task 40,
        /// spec §3.10 Р44/Р60, §6k Р164) — the host-mode restart's single
        /// entry point. The trigger itself does not exist in Phase Ф8: this
        /// task ships the mechanism, and the handle that pulls it is Ф9's,
        /// the same shape of deferred wiring the client half of the handshake
        /// already carries.
        ///
        /// THE ORDER INSIDE IS MANDATORY, AND EVERY STEP OF IT IS LOAD-BEARING:
        ///   * the connections are captured BEFORE anything else, because
        ///     `StartMatch` stops the previous match first and `StopMatch`
        ///     nulls the field they live in. They are the SAME connections:
        ///     §6k Р164 keeps the roster and the handshake untouched across a
        ///     restart (there is no join phase to a restart at all — spec
        ///     §3.10 refuses joining a match in progress, and `MatchRoster`'s
        ///     started flag is one-way), so there is nothing to rebuild them
        ///     from and no reason to.
        ///   * `MatchRestartedNet` goes out BEFORE `StartMatch`, not after.
        ///     The right to reset the client's per-match state travels on
        ///     THIS message and on no other: every epoch-aware client seam
        ///     refuses a frame of an epoch it does not track
        ///     (`SnapshotQueue.Admit` answers `ForeignEpoch`) and only
        ///     `Reset`/`ResetForEpoch` switches the tracked epoch over. A
        ///     snapshot of the new epoch that overtakes this message is
        ///     therefore discarded — and since snapshots are `Unreliable`
        ///     while this is `Reliable`, a few of the opening frames can
        ///     still lose that race. That residual loss is the accepted cost
        ///     Р164 names (the interpolation buffer absorbs it); sending the
        ///     message afterwards instead would lose them ALL.
        ///
        /// FRESH CONTROLLER OBJECTS ARE THE CALLER'S OBLIGATION, and this
        /// class cannot check it. `PlayerNetworkController`'s own doc spells
        /// out why they must be new OBJECTS rather than reused ones ("A new
        /// match means a new object — and an OBJECT POOL would mean the same
        /// object with the latch still down"): `PlayerPredictionCore` has no
        /// reset, and the `NotifyOwnDeath` latch is one-way. Handing the same
        /// array back in compiles, runs, and silently starts the new match
        /// with the previous one's prediction state — so this is recorded as
        /// a contract, honestly, rather than dressed up as something the
        /// signature enforces.
        ///
        /// Throws when no match is running: restarting nothing is a bug in
        /// the caller, and `StartMatch` is the method for starting a first
        /// match.
        public void RestartMatch(long seed, in SimConfig simConfig, ushort newEpoch,
            PlayerNetworkController[] freshControllers)
        {
            if (!_running)
            {
                throw new InvalidOperationException(
                    "MatchServer.RestartMatch: no match is running — a restart needs a match to "
                    + "restart; call StartMatch for the first one.");
            }

            // Captured first: StartMatch below stops the running match, and
            // StopMatch releases this array's field (see this method's doc).
            NetworkConnection[] connections = _connections;

            var restarted = new MatchRestartedNet
            {
                MatchEpoch = newEpoch,
                Seed = seed,
            };
            for (int i = 0; i < connections.Length; i++)
            {
                // Same guard, and the same reason, as the per-tick snapshot
                // send: broadcasting to a dead connection is a LogWarning
                // FishNet emits itself, not a silent no-op.
                if (!connections[i].IsActive) continue;
                _nm.ServerManager.Broadcast(connections[i], restarted, channel: Channel.Reliable);
            }

            StartMatch(seed, in simConfig, newEpoch, connections, freshControllers);
        }

        /// Ends the match without starting another — releases this match's
        /// world/assembler/scratch (a process may sit between matches for a
        /// while, e.g. a join window; there is no reason to hold the finished
        /// match's arrays for GC over that whole span) and flips `_running`
        /// off, which is what makes `OnPostTick` inert from here on (see the
        /// class doc — the `OnPostTick` SUBSCRIPTION itself is never touched,
        /// fix-round 1 I1). Idempotent: stopping an already-stopped instance
        /// is a no-op, not an error.
        public void StopMatch()
        {
            if (!_running) return;

            _running = false;

            _world = null;
            _assembler = null;
            _connections = null;
            _controllers = null;
            _lastInputsScratch = null;
            _effectiveInputsScratch = null;
            _starvedScratch = null;
            _lastSeenInputTick = null;
            _lastFreshWorldTick = null;
        }

        /// The whole per-tick pipeline (spec §3.7, Р22 — order is load-bearing,
        /// see task-36-brief §0a: `OnPostTick` is where the world steps because
        /// FishNet's own tick order has no subscriber-priority mechanism, and
        /// `OnPostTick` structurally runs after `[Replicate]` delivery). Always
        /// subscribed (see the class doc's "SUBSCRIPTION TIMING" paragraph) —
        /// `_running` is what gates whether a call actually does anything.
        void OnPostTick()
        {
            // Fix-round 1, I1/M2: a stopped instance is inert, and a
            // `StopMatch` called mid-tick by some OTHER OnPostTick subscriber
            // ahead of this one in FIFO order must not let this handler run
            // past it into null fields.
            if (!_running) return;

            _stopwatch.Restart();

            // See the class doc's "TWO READINGS" paragraph: this is the
            // PRE-TickAll value, the domain step 1's freshness math is done
            // in. The POST-TickAll reading for steps 4-5 is taken separately,
            // below, after TickAll has actually run.
            int preTickWorldTick = _world.CurrentTick;
            int starveTicks = _netConfig.InputStarveTicks;

            // 1. Effective inputs (Р22 step 1, Р25). `LastServerInput` is
            // Task 34's contract (§8.1): only ever a REAL, freshly-arrived
            // input, never a repeat. Its `.Tick` is FishNet's own tick for
            // that replicate (client-stamped, never re-stamped by the server —
            // see EffectiveInputBatch.Gather's own doc, fix-round 1 C1) — it
            // is used ONLY as a change-identity here, never subtracted from a
            // world tick directly.
            for (int i = 0; i < _controllers.Length; i++)
                _lastInputsScratch[i] = _controllers[i].Core.LastServerInput;

            EffectiveInputBatch.Gather(_lastInputsScratch, preTickWorldTick, starveTicks,
                _lastSeenInputTick, _lastFreshWorldTick, _effectiveInputsScratch, _starvedScratch);

            for (int i = 0; i < _starvedScratch.Length; i++)
                if (_starvedScratch[i]) _assembler.StatsFor(i).InputStarved++;

            // 2. The world steps exactly once, on the effective inputs — never
            // the raw ones (a stale/absent input must never reach TickAll
            // unmodified, see InputStarvation's own doc).
            _world.TickAll(_effectiveInputsScratch);

            // Fix-round 2, W8: captured BEFORE the try and used in `finally`
            // below instead of the field — see the `finally` block's own
            // note for why.
            var world = _world;
            try
            {
                // The POST-TickAll reading — "the tick just completed" — is
                // what the end policy, the outgoing snapshot and the reconcile
                // all report (steps 2b, 4-5); see the class doc's "TWO
                // READINGS" paragraph. Read ONCE, here, so those three can
                // never answer about different ticks.
                int postTickWorldTick = _world.CurrentTick;

                // 2a. Stage 2 Task 40 (spec §3.10): a player whose connection
                // is gone dies in THIS frame's world, not the next one — see
                // the class doc's own paragraph on why the placement matters.
                // `connections[i]`/`controllers[i]`/player `i` are the same
                // player by index (the class doc's "what Ф8 must hand in"),
                // so this loop's bound is also the world's player count.
                for (int i = 0; i < _connections.Length; i++)
                {
                    if (MatchEndPolicy.ShouldKillOnDisconnect(
                            _connections[i].IsActive, _world.PlayerAt(i).Alive))
                    {
                        _world.KillPlayerNoDamage(i);
                    }
                }

                // 2b. Stage 2 Task 40 (spec §3.10/§3.11): is the match over?
                // The decision belongs to MatchEndPolicy — this only gathers
                // its two inputs, both read AFTER step 2a so a disconnect that
                // emptied the arena ends the match on the very tick it
                // happened.
                int alivePlayers = 0;
                for (int i = 0; i < _world.PlayerCount; i++)
                    if (_world.PlayerAt(i).Alive) alivePlayers++;

                MatchEndReason reason = _endPolicy.Evaluate(postTickWorldTick, alivePlayers);
                if (reason != MatchEndReason.None)
                {
                    EndMatch(reason);
                    // Leaves through the `finally` below, on purpose: this
                    // tick's events still have to be cleared and its own cost
                    // still has to be counted, exactly as on every other path
                    // out of this handler.
                    return;
                }

                // 3. One capture + wire-event expansion shared by every
                // connection this tick (SnapshotAssembler.BeginTick's own doc).
                _assembler.BeginTick(_world);

                // 4. Per-connection frame, Unreliable (spec §3.7 table Р27:
                // state travels unreliably). `identityIndex`/`viewpointIndex`
                // are both `i` — see the class doc's "what Ф8 must hand in"
                // paragraph.
                for (int i = 0; i < _connections.Length; i++)
                {
                    // Fix-round 1, I3.3: a disconnected/disconnecting
                    // connection gets no frame — BuildFor would still pay the
                    // full visibility/routing cost for nothing, and
                    // Broadcasting to a dead connection is a per-tick
                    // LogWarning FishNet itself emits (stdout spam on a
                    // headless server that runs for the whole match) rather
                    // than a silent no-op. `IsActive` is the package's own
                    // "not disconnected/disconnecting" predicate
                    // (`NetworkConnection.cs:123`,
                    // `ClientId >= 0 && !Disconnecting`).
                    if (!_connections[i].IsActive) continue;

                    int len = _assembler.BuildFor(i, i, i, _epoch);
                    var broadcast = new SnapshotBroadcast
                    {
                        Tick = (uint)postTickWorldTick,
                        MatchEpoch = _epoch,
                        Payload = new ArraySegment<byte>(_assembler.BufferFor(i), 0, len),
                    };
                    _nm.ServerManager.Broadcast(_connections[i], broadcast, channel: Channel.Unreliable);
                }

                // 5. Reconciliation source — the WORLD's own tick, not
                // FishNet's (task-34-report §8.1's warning, resolved here as
                // task-36-brief §2.2 directs): FishNet re-stamps the wire tick
                // of a reconcile regardless of what is passed in
                // (Reconcile_* internals), so this value only ever feeds
                // `PlayerPredictionCore.LastReconciledTick` — our own
                // bookkeeping, never the packet. Runs AFTER the snapshot send
                // (step 4) and BEFORE `ClearEvents` (step 6), matching Р22's
                // fixed order and letting `SendStateUpdate` (§0a) pick it up
                // the same tick it was set — and, per fix-round 1 I1, BEFORE
                // every controller's own `OnPostTick`-driven `CreateReconcile`
                // runs, because this whole handler is guaranteed to
                // fire before theirs.
                uint postTickWorldTickU = (uint)postTickWorldTick;
                for (int i = 0; i < _controllers.Length; i++)
                    _controllers[i].SetAuthoritativeState(postTickWorldTickU, _world.PlayerAt(i));
            }
            finally
            {
                // 6. LAST, ALWAYS (Р22) — in `finally` (fix-round 1, M7) so a
                // broken step 3-5 (an exception out of BuildFor, Broadcast,
                // etc.) still clears the event buffer: a headless server has
                // no render frame to otherwise drain it, and a buffer that
                // survives a bad tick either overflows the next one or hands
                // a later tick's clients events that already went stale.
                //
                // `world` (the LOCAL captured above, fix-round 2 W8) — not
                // `_world` — because a `StopMatch` running inside this same
                // try block nulls the FIELD as part of its own cleanup;
                // reading the field here would then throw an NRE that MASKS
                // whatever exception the steps were actually propagating,
                // instead of letting that real exception surface. Two paths
                // reach that state: a nested `StopMatch` triggered
                // synchronously from steps 3-5 (e.g. a disconnect handler
                // reacting to step 4's own `Broadcast` call — the fix-round 2
                // case), and, since Stage 2 Task 40, the ORDINARY end of a
                // match at step 2b, which stops it deliberately.
                world.ClearEvents();

                // Fix-round 2, W8: moved into `finally`, AFTER `ClearEvents`,
                // so this tick's own timing is never silently lost — the two
                // statements used to sit AFTER the whole try/finally, which
                // an exception out of steps 3-5 would skip entirely (nothing
                // past a propagating throw runs), under-counting `TickTime`
                // on exactly the ticks most worth knowing were slow or broken.
                _stopwatch.Stop();
                _tickTime.Record(_stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        /// Ends the running match for `reason` (Stage 2 Task 40) — the order
        /// below is the class doc's, and it is the order because `StopMatch`
        /// releases the world and the assembler every number here comes from.
        void EndMatch(MatchEndReason reason)
        {
            MatchSummary summary = BuildSummary(reason);

            // Captured for the same reason `RestartMatch` captures it:
            // `StopMatch` below nulls the field.
            NetworkConnection[] connections = _connections;
            for (int i = 0; i < connections.Length; i++)
            {
                // A connection that already left gets nothing — the same
                // guard, for the same reason, as the per-tick snapshot send.
                if (!connections[i].IsActive) continue;
                _nm.ServerManager.Broadcast(connections[i], EndedNetFor(in summary, i),
                    channel: Channel.Reliable);
            }

            StopMatch();

            _lastSummary = summary;
            _outcome = reason;
        }

        /// The match's numbers, captured ONCE (see the class doc). Called
        /// while the world is still alive, from `EndMatch` only.
        MatchSummary BuildSummary(MatchEndReason reason)
        {
            int playerCount = _world.PlayerCount;
            var playerStats = new MatchStats[playerCount];
            for (int i = 0; i < playerCount; i++) playerStats[i] = _world.StatsAt(i);

            return new MatchSummary(reason, _epoch, (uint)_world.CurrentTick,
                _world.WorldStats, _world.DroppedEvents, playerStats);
        }

        /// Player `slot`'s own copy of the end-of-match message: the world's
        /// numbers, identical in every copy, plus that one player's own. The
        /// counters are copied across field by field rather than by embedding
        /// the simulation's own structs — see `MatchEndedNet`'s own doc for
        /// why the protocol spells its surface out.
        static MatchEndedNet EndedNetFor(in MatchSummary summary, int slot)
        {
            MatchStats stats = summary.PlayerStats[slot];
            WorldStats world = summary.World;

            return new MatchEndedNet
            {
                Reason = (byte)summary.Reason,
                MatchEpoch = summary.Epoch,
                FinalTick = summary.FinalTick,

                Kills = stats.Kills,
                HeadshotKills = stats.HeadshotKills,
                ShotsFired = stats.ShotsFired,
                ShotsHit = stats.ShotsHit,
                DashesUsed = stats.DashesUsed,
                SlidesUsed = stats.SlidesUsed,
                DeathTick = stats.DeathTick,
                DamageTaken = stats.DamageTaken,

                WavesCleared = world.WavesCleared,
                MobSpawnsSkipped = world.MobSpawnsSkipped,
                ProjectileSpawnsSkipped = world.ProjectileSpawnsSkipped,
            };
        }
    }

    /// Everything a finished match is worth knowing about, captured in ONE
    /// place at the ONE moment it is all still readable (Stage 2 Task 40, spec
    /// §3.10/§3.11) — see `MatchServer`'s own class doc for why a second
    /// reading of the same counters is the defect this type exists to prevent.
    ///
    /// TWO CONSUMERS, ONE CAPTURE: the per-connection `MatchEndedNet` that
    /// `MatchServer` sends, and the structured per-match log line of spec
    /// §3.11 that the Task 41 bootstrap assembles — the bootstrap owns
    /// `matchId` and the process's own wall clock, neither of which this
    /// assembly can see, which is exactly why the line is built there and the
    /// numbers are captured here.
    ///
    /// `PlayerStats` IS INDEXED BY PLAYER SLOT — the same index `connections`
    /// and `controllers` use throughout `MatchServer`, and the same one a
    /// client was given in its `MatchWelcomeNet`. It is a fresh array per
    /// match end (one allocation at the end of a match is not a per-tick cost)
    /// and is never handed to anything that mutates it.
    public readonly struct MatchSummary
    {
        public readonly MatchEndReason Reason;
        public readonly ushort Epoch;

        /// The world tick the match ended on.
        public readonly uint FinalTick;

        public readonly WorldStats World;

        /// `SimulationWorld.DroppedEvents` — events the world's own buffer
        /// could not hold, cumulative for the match.
        public readonly int DroppedEvents;

        /// One entry per player slot; never null for a summary a real match
        /// end produced.
        public readonly MatchStats[] PlayerStats;

        public MatchSummary(MatchEndReason reason, ushort epoch, uint finalTick,
            in WorldStats world, int droppedEvents, MatchStats[] playerStats)
        {
            Reason = reason;
            Epoch = epoch;
            FinalTick = finalTick;
            World = world;
            DroppedEvents = droppedEvents;
            PlayerStats = playerStats;
        }
    }

    /// Pure batch form of the per-tick starvation pass (Stage 2 Task 36 §2.2
    /// point 1) — no FishNet, tested directly (`InputStarvationTests`). Mirrors
    /// the split `PlayerNetworkController`/`PlayerPredictionCore` already uses
    /// (Stage 2 Task 34): the FishNet-touching class stays thin, the decision
    /// lives beside it in a runtime-free type.
    ///
    /// TICK-DOMAIN AGNOSTICISM (fix-round 1, C1 — CRITICAL fix, verified
    /// personally against the package). `ServerTickInput.Tick` — the value
    /// `PlayerPredictionCore.RecordServerInput` publishes and this method
    /// reads from `lastInputs[i].Tick` — is stamped by the OWNING CLIENT'S OWN
    /// `TimeManager.LocalTick` (`NetworkBehaviour.Prediction.cs:531-532`,
    /// `Replicate_Authoritative`: `uint dataTick = TimeManager.LocalTick;`)
    /// and the SERVER NEVER RE-STAMPS IT: the one line that would
    /// (`data.Data.SetTick(tm.LocalTick)`) is commented out
    /// (`NetworkBehaviour.Prediction.cs:716-717`, inside
    /// `Replicate_NonAuthoritative`'s local `ReplicateData` function — the
    /// server-side path a client-owned object's replicate actually runs
    /// through). That FishNet-tick domain is UNRELATED to
    /// `SimulationWorld.CurrentTick` — the world domain resets to 0 on every
    /// match restart (Р60) while `TimeManager.LocalTick`/`Tick` is monotonic
    /// for the whole PROCESS (task-2 note §6) — so subtracting one from the
    /// other directly (the ORIGINAL, buggy form of this method) computes
    /// GARBAGE: whenever a client's raw tick number is larger than the
    /// world's (the ordinary case — a match's world tick count is small,
    /// FishNet's process-lifetime tick count is not), the subtraction is
    /// deeply negative, Р82's own clamp reads that as "fresh", and
    /// `InputStarvation`'s hold/starve regimes NEVER ENGAGE — Р25's whole
    /// point silently dies, and `NetStats.InputStarved` stays 0 forever
    /// regardless of what actually happens on the wire.
    ///
    /// THE FIX measures freshness by CHANGE, not by MAGNITUDE. `lastSeenInputTick`/
    /// `lastFreshWorldTick` are `MatchServer`'s own per-player memory (see its
    /// class doc): a raw replicate tick is an OPAQUE IDENTITY here, never an
    /// operand of subtraction. When it differs from what was last observed,
    /// THIS tick's `worldTick` becomes "the world tick this player was last
    /// seen fresh at" — and `ticksSinceLast` is the gap between that WORLD
    /// tick and the current one, entirely inside the world's own domain. This
    /// is immune to the FishNet/world tick offset by construction (there is
    /// no cross-domain arithmetic left to be wrong), to a client's prediction
    /// lead, and to any future change in whether/how the server stamps the
    /// tick field.
    public static class EffectiveInputBatch
    {
        /// `lastInputs[i]` is player i's `PlayerPredictionCore.LastServerInput`
        /// snapshot; `worldTick` is the PRE-`TickAll` reading of
        /// `SimulationWorld.CurrentTick` (`MatchServer.OnPostTick`'s own doc:
        /// "how many ticks were complete coming into this call"). Fills
        /// `effectiveInputs`/`starved` in place and MUTATES
        /// `lastSeenInputTick`/`lastFreshWorldTick` in place too — all five
        /// spans must be the same length as `lastInputs`, and the two state
        /// spans are the caller's PERSISTENT per-match scratch (not rebuilt
        /// per call): see the class doc for what each remembers and why.
        /// Returns the count of players found starved, the value `MatchServer`
        /// feeds nowhere else — PER-CONNECTION `NetStats.InputStarved` needs
        /// the `starved` span itself, which is why both are out parameters
        /// rather than just the count.
        public static int Gather(
            ReadOnlySpan<ServerTickInput> lastInputs,
            int worldTick,
            int starveTicks,
            Span<uint> lastSeenInputTick,
            Span<int> lastFreshWorldTick,
            Span<SimInput> effectiveInputs,
            Span<bool> starved)
        {
            if (lastSeenInputTick.Length != lastInputs.Length)
            {
                throw new ArgumentException(
                    $"EffectiveInputBatch.Gather: lastSeenInputTick.Length ({lastSeenInputTick.Length}) must "
                    + $"equal lastInputs.Length ({lastInputs.Length}).", nameof(lastSeenInputTick));
            }
            if (lastFreshWorldTick.Length != lastInputs.Length)
            {
                throw new ArgumentException(
                    $"EffectiveInputBatch.Gather: lastFreshWorldTick.Length ({lastFreshWorldTick.Length}) must "
                    + $"equal lastInputs.Length ({lastInputs.Length}).", nameof(lastFreshWorldTick));
            }
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
                // Identity comparison ONLY — never arithmetic. A raw
                // replicate tick that differs from what this player was last
                // observed carrying means a NEW packet was consumed since the
                // previous call, regardless of what either number's magnitude
                // is or which domain it lives in.
                if (lastInputs[i].Tick != lastSeenInputTick[i])
                {
                    lastSeenInputTick[i] = lastInputs[i].Tick;
                    lastFreshWorldTick[i] = worldTick;
                }

                int ticksSinceLast = worldTick - lastFreshWorldTick[i];
                effectiveInputs[i] = InputStarvation.Effective(
                    in lastInputs[i].Input, ticksSinceLast, starveTicks, out bool isStarved);
                starved[i] = isStarved;
                if (isStarved) starvedCount++;
            }
            return starvedCount;
        }
    }

    /// Tiny pure accumulator for the server tick's own wall-clock cost (spec
    /// §3.11: the per-match log line's average and maximum tick time —
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
