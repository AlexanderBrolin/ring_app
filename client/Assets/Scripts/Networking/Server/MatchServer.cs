using System;
using System.Diagnostics;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Transporting;
using FishNet.Transporting;
using Ring.Data;
using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Ring.Simulation.Loot;
using Unity.Mathematics;

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
    /// ONE `MatchServer` PER PROCESS, DECLARED EXPLICITLY (fix-round 2, W9;
    /// extended by Task 42a fix-round 1, M-5, and again by Stage 3 Т28 —
    /// THREE permanent roots now, not one). The `OnPostTick` subscription lives for the rest of the
    /// PROCESS's lifetime, not merely this instance's — there is no
    /// unsubscribe anywhere in this class, on purpose (the FIFO guarantee
    /// I1 above rests on exactly that permanence). Task 42a's constructor
    /// ALSO registers `OnSpectateRequest` as this process's one
    /// `SpectateRequestNet` handler, and Stage 3 Т28 `OnLootRequest` as its
    /// one `LootRequestNet` handler, both with the identical never-released
    /// shape — no `UnregisterBroadcast` call exists anywhere in this class.
    /// A `MatchServer` that falls out of scope is therefore NOT
    /// garbage-collected THROUGH ANY OF THE THREE ROOTS: `_nm.TimeManager`'s
    /// own event keeps a live delegate into `OnPostTick`, and
    /// `_nm.ServerManager`'s own broadcast handler list keeps one into
    /// `OnSpectateRequest` and one into `OnLootRequest`, all for as long as
    /// the `NetworkManager` itself exists. Ф8's bootstrap must
    /// construct exactly ONE instance for the whole process and reuse it
    /// across every `StartMatch`/`StopMatch` cycle (see the re-entrancy
    /// paragraph above) — constructing a second one would leave the first's
    /// OWN copies of BOTH subscriptions still firing (inert while its own
    /// `_running` is false, per the SUBSCRIPTION TIMING paragraph, but never
    /// released) alongside the new one's. `MatchHandshake.Unregister`'s own
    /// doc walks the mechanism this second consequence rests on in full
    /// (`AddUnique`/`Delegate.Equals`, `ServerManager.RegisterBroadcast`'s
    /// package internals): two `MatchServer` INSTANCES convert their own
    /// `OnSpectateRequest` to the same METHOD but with two DIFFERENT
    /// invocation targets, so FishNet's own `Delegate.Equals`-based
    /// deduplication correctly treats them as different handlers and
    /// subscribes BOTH — the second construction does not even fail loudly,
    /// it just doubles the handler that answers every future
    /// `SpectateRequestNet`, and since Т28 every future `LootRequestNet`,
    /// for the rest of the process.
    ///
    /// TWO READINGS OF `SimulationWorld.CurrentTick` PER CALL, NOT ONE
    /// (fix-round 1, M1). `CurrentTick` counts ticks the world has FINISHED —
    /// `SimulationWorld.TickAll`'s own length guards run first (fix-round 2,
    /// W7 — precise: a throw must never leave `_tick` half-bumped), and
    /// `_tick++` is the first MUTATION of state after them
    /// (`SimulationWorld.cs:203`), so the property still reads 0 before the
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
    /// to be the SAME player, by index — `identityIndex` in every
    /// `SnapshotAssembler.BuildFor` call below is always `i` (WHO this
    /// connection is never changes). `viewpointIndex` is `_viewpointIndex[i]`
    /// instead (Stage 2 Task 42a, spec §3.10 :673-678): it STARTS at `i`,
    /// same as identity, and only ever moves when this connection's own dead
    /// player sends an accepted `SpectateRequestNet` — see `OnSpectateRequest`
    /// below for the decision and `SnapshotAssembler`'s own class doc for
    /// the split these two parameters feed. Both arrays must be non-empty
    /// and the same length; `playerCount` is derived from them rather than
    /// taken as a separate parameter, so the two can never disagree by a
    /// caller's mistake. `StartMatch` itself calls
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
    /// inputs and executes the answer.
    ///
    /// THE DECISION IS TAKEN EARLY AND EXECUTED LATE, AND THE SPLIT IS THE
    /// WHOLE POINT (fix-round 1, C-1). `reason` is computed at step 2b, right
    /// after the disconnect kills of step 2a, because the count of living
    /// players is only meaningful once those kills have landed. It is ACTED ON
    /// at step 5b — AFTER `BeginTick` (3), the per-connection snapshot (4) and
    /// `SetAuthoritativeState` (5). Ending the match between steps 2 and 3
    /// instead (the shape this task originally shipped) skipped all three and
    /// then let `finally`'s `ClearEvents` erase the final tick's events
    /// unread: `PlayerDied` — the very event the match ended ON — would never
    /// reach any client, because events ride the unreliable snapshot, there is
    /// no resend path outside it, and there is no tick N+1 to carry them. A
    /// client would see the match end with its last death missing, which is
    /// Critical Rule 3 (the server is authoritative over death) failing
    /// silently.
    ///
    /// THE PRICE OF THE LATE EXECUTION, NAMED HONESTLY: `MatchEndedNet` rides
    /// `Reliable` while the final snapshot of the same tick rides
    /// `Unreliable`, and FishNet orders neither against the other, so a client
    /// can receive the results before the last frame they belong to. That is
    /// what `MatchEndLingerSeconds` (spec §3.10, the bootstrap's own wait
    /// before exiting) absorbs — and it is incomparably cheaper than losing
    /// the closing death outright.
    ///
    /// THE END ITSELF IS A FIXED ORDER: build the `MatchSummary` FIRST
    /// (`StopMatch` releases the world and the assembler every number comes
    /// from), then send every live connection its OWN `MatchEndedNet` over
    /// `Reliable`, then `StopMatch`, then record the outcome — the last three
    /// under a `finally`, so a throw out of any single `Broadcast` still ends
    /// the match instead of leaving `_running` true for the next tick to try
    /// the same failing send again, forever (fix-round 1, I-2).
    ///
    /// A DISCONNECT KILLS THE PLAYER, ON THE SAME TICK (spec §3.10). Step 2a
    /// runs `MatchEndPolicy.ShouldKillOnDisconnect` over every slot BEFORE the
    /// world is captured for broadcast (`BeginTick`, step 3), so a death this
    /// tick DISCOVERS is a death this tick's own outgoing frame REPORTS —
    /// rather than one that waits for the next frame while the picture still
    /// shows a living body. (The disconnect itself is discovered by FishNet's
    /// own connection bookkeeping between ticks, not by anything this handler
    /// does.) The `Alive` half of that predicate is what makes the pass
    /// idempotent across the rest of the match.
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
    /// `RestartMatch`'s own doc. Two parts are worth stating at the class
    /// level. First, what a restart does NOT touch: the roster and the
    /// handshake are objects of the JOIN PHASE and survive an epoch untouched,
    /// so nothing here recreates them, and the connections of the finished
    /// match are the connections of the next one. Second, WHO HOLDS THOSE
    /// CONNECTIONS: the bootstrap does (§6k Р164 — it built the slot-to-
    /// connection table from the handshake's `onAccepted` in the first place),
    /// and it hands them back in as an argument. `RestartMatch` therefore
    /// works both on a RUNNING match and on one that has already ended — and
    /// the second case is the ordinary one, since a host-mode "play again"
    /// happens after `Outcome` went non-`None` and `StopMatch` released this
    /// class's own copy of the array (fix-round 1, I-4).
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

        // Stage 2 Task 42a (spec §3.10 :673-678, Р70): the spectate-switch
        // decision, handed in ready-made — the same reason `_endPolicy` is:
        // the seconds-to-ticks conversion (`SpectatorSwitchCooldownSeconds *
        // TickRate`) is `ServerBootstrap`'s arithmetic, not this class's own
        // opinion (see `SpectatePolicy`'s own doc).
        readonly SpectatePolicy _spectatePolicy;

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

        // Stage 2 Task 42a (spec §3.10 :673-678, Р70): per-slot spectate
        // state, the same "fresh scratch every StartMatch, released every
        // StopMatch" treatment as `_lastSeenInputTick`/`_lastFreshWorldTick`
        // above, for the identical reason (Р151) — a restart's world starts
        // back at tick 0, and either array surviving a restart would either
        // apply a stale viewpoint (`_viewpointIndex`) or a stale cooldown
        // origin (`_lastSpectateSwitchTick`) to a match that never earned it.
        // `_viewpointIndex[i]` is WHERE slot `i` currently looks from — `i`
        // itself (its own body) until an accepted `SpectateRequestNet` moves
        // it. `_lastSpectateSwitchTick[i]` is the world tick of slot `i`'s
        // last ACCEPTED switch, or `SpectatePolicy.NoPriorSwitch` if none has
        // happened yet this match — see that constant's own doc for why a
        // sentinel is safer here than `int.MinValue`.
        int[] _viewpointIndex;
        int[] _lastSpectateSwitchTick;

        // Stage 2 Task 42a fix-round 1, M-6 (throttle decision moved into
        // SpectatePolicy.ShouldLogRefusal, fix-round 2, I-C — these two
        // arrays are its memory, the wiring holds none of the decision
        // itself). `_lastLoggedRefusal[slot]` is the last REFUSAL reason
        // logged for this slot, `SpectateRefusal.None` meaning "nothing
        // logged yet since the last accepted switch (or match start)";
        // `_lastLoggedRefusalTick[slot]` is the world tick that entry was
        // written at (meaningless while `_lastLoggedRefusal[slot]` is the
        // sentinel — `ShouldLogRefusal` never reads it in that case). See
        // `OnSpectateRequest`'s own doc for why this exists — same
        // fresh-every-`StartMatch`/released-in-`StopMatch` treatment as the
        // pair above, for the identical Р151 reason.
        SpectateRefusal[] _lastLoggedRefusal;
        int[] _lastLoggedRefusalTick;

        // app-88jb Т29 (spec §3.6), and the pair is the TRIM LOG'S THROTTLE
        // MEMORY: two fields per slot, exactly like `_lastLoggedRefusal` +
        // `_lastLoggedRefusalTick` above, because either one of them alone
        // throttles nothing. `_rewindTrimmed[i]` is the state the LOG LAST
        // REPORTED for slot `i` — not the state of the previous tick, so
        // `false` at match start reads as both "the log has said nothing yet"
        // and the truth it would have said; `_rewindTrimmedLogTick[i]` is the
        // world tick that report was written at, or `NoRewindTrimLogYet` while
        // nothing has been written for this slot at all.
        //
        // WHY A THROTTLE IS NEEDED AT ALL. `SimInput.RewindTicks` is a LEVEL
        // and not an edge (its own doc), so a client whose claim sits above
        // the estimate produces the condition on every single tick of the
        // match, and `OnPostTick` runs on every one of them: thirty lines a
        // second, per slot, into the container's stdout.
        //
        // ⛔ THE PRECEDENT IN THIS FILE IS A MODEL OF THE FORM, NOT A PERMIT
        // FOR "LOG ON CHANGE" (fix-round, A-2/B-2 — an earlier version of this
        // wiring logged on the change alone and cited that precedent as its
        // warrant). `SpectatePolicy.ShouldLogRefusal` states outright that it
        // is "NOT A 'LOG ON CHANGE' GATE — and that is a deliberate departure",
        // and it measured the reason: against a flapping client the "it
        // changed" half is true on every tick, so a change test alone bounds
        // nothing. This condition can flap for the same kind of reason, and
        // the arithmetic is on hand rather than feared — the claim is
        // `predictionLead + interpolationLag` (`RewindDepthMeter.Measure`),
        // both halves move with packet arrival, and with the round trip time
        // reading zero (see `SanitizedRewindDepth`'s own ⛔ paragraph)
        // the estimate is a flat 5 at the shipped picture of 3 and tolerance
        // of 2. "Is this trimmed" therefore degenerates to "is the claim
        // exactly 6", the top of the wire's own domain and one tick above the
        // honest 5 that `RewindSanityTests`' second fixture states for an
        // 80 ms link. Crossing that line and falling back is therefore an
        // ordinary operating condition rather than an exotic one — reasoned
        // from those two sources, not measured on a live link — and every
        // crossing would be TWO lines.
        //   Hence the line needs BOTH halves: the state must differ from the
        // one last REPORTED, and a bond of `_netConfig.TickRate` ticks — one
        // second — must have passed since that report. The read site in
        // `OnPostTick` carries why the bond is derived rather than declared.
        //
        // THE COSTS OF THE BOND, NAMED HERE RATHER THAN LEFT TO BE FOUND, the
        // way the precedent names its own three — and stated per case, because
        // a short episode fares differently depending on where it falls:
        //   * IF THE BOND HAD ALREADY ELAPSED when the episode began, its
        //     start is logged at once and its END waits until a bond after
        //     that line — so the log can read "trimming" for up to a second
        //     after the trimming stopped.
        //   * IF THE BOND HAD NOT ELAPSED, the episode never reaches the log
        //     at all: the start is suppressed, and the return to the state the
        //     log already reported is not a change to report. Lost, not
        //     delayed — the precedent names the same cost for a transient
        //     reason of its own.
        //   * WHAT IS NOT LOST is a state that STAYS: within one bond of the
        //     last line, any state differing from the one reported is written
        //     out, and the line names what is happening NOW rather than the
        //     episode that was swallowed.
        //
        // ⚠ AND THE DIFFERENCE FROM THE PRECEDENT IS NAMED RATHER THAN
        // SMOOTHED OVER: that one got a policy class with a predicate a test
        // can reach, and this one deliberately does not (ruling 224). The
        // decision here carries no subject matter of its own — two bools and a
        // tick difference — so a test over it would measure the comparison
        // rather than a rule; and this class is by construction beyond
        // EditMode's reach anyway (see the class doc). The throttle therefore
        // has NO EditMode witness, on purpose.
        //
        // Same fresh-every-`StartMatch`/released-in-`StopMatch` treatment, and
        // the same Р151 reason, as the arrays above.
        bool[] _rewindTrimmed;
        int[] _rewindTrimmedLogTick;

        /// "No trim line has been written for this slot yet" (app-88jb Т29
        /// fix-round) — the value `StartMatch` fills `_rewindTrimmedLogTick`
        /// with, and what lets a match's very FIRST line through the bond
        /// instead of making it wait a second to be allowed out.
        ///
        /// ⚠ `int.MinValue`, AND NOT THE `-1` THE NEIGHBORING SENTINEL PICKED
        /// — the difference is the point rather than an oversight.
        /// `SpectatePolicy.NoPriorSwitch` is `-1` precisely to keep
        /// `currentTick - lastSwitchTick` a safe `int` subtraction, and it can
        /// afford that because its own predicate SHORT-CIRCUITS on the sentinel
        /// and never subtracts it. This one has to ANSWER on it: at tick 0 a
        /// `-1` would give a difference of 1, below any bond, and would silence
        /// a match's opening line for its whole first second. `int.MinValue`
        /// clears every bond instead — and the underflow the neighbor avoided
        /// is avoided too, by doing the one subtraction that can meet this
        /// value in `long`.
        const int NoRewindTrimLogYet = int.MinValue;

        // app-88jb Т29 fix-round (A-8): the two arena numbers the trim needs,
        // taken ONCE per match instead of once per tick.
        // `SimulationWorld.Config` is a by-VALUE property — `SnapshotAssembler.
        // BeginTick` records the same trap in a note of its own — so reading
        // `_world.Config.Arena` inside `OnPostTick` copied the WHOLE
        // `SimConfig` struct on every tick to reach two ints.
        //
        // ⛔ WHY THE CACHE IS LEGAL AND NOT A STALE COPY WAITING TO BE FOUND,
        // said here so the next session does not read it as a bug: the
        // hot-tweak path cannot reach this world at all.
        // `SimulationWorld.ApplyConfig` is the only thing that moves a running
        // world's numbers, and its only callers are `LocalSimBackend` (which
        // owns its own offline world) and `NetworkSimBackend`, which REFUSES
        // the call with a log rather than migrating anything. The world this
        // class builds in `StartMatch` is private to it and handed to nobody,
        // so nothing can retune it underneath these two copies; a restart goes
        // through `StartMatch` again and re-reads both from that match's own
        // `simConfig`.
        // Value types, so `StopMatch` leaves them alone: there is no reference
        // to release, and `_running` is what makes a stopped instance inert.
        int _rewindCapTicks;
        int _rewindPictureTicks;

        // Stage 3 Т24 (spec §3.10): the two facts about a finished raid that
        // the WORLD does not record, kept here for the summary to read — same
        // "fresh scratch every StartMatch, released every StopMatch"
        // treatment, and the same Р151 reason, as the four arrays above.
        //
        // `_extractedTick[i]` is the world tick slot `i` was first observed
        // extracted, 0 meaning "never left" (MatchProgress.Observe owns the
        // rule and its sentinel). It exists because leaving is NOT dying
        // (Р223): a death stamps MatchStats.DeathTick, an extraction stamps
        // nothing at all, and without this the record would credit a man who
        // went home in the fourth minute with the whole raid's length.
        //
        // `_disconnectKilled[i]` is whether THIS server killed slot `i` for a
        // dropped connection (step 2a). The world cannot answer it —
        // KillPlayerNoDamage leaves the same corpse any other death does — and
        // the distinction is what separates the `Disconnected` outcome from
        // `Died` (Р271). Recorded at the kill rather than re-derived from
        // `IsActive` at the end, because a player who fell fighting in the
        // second minute and closed his window in the tenth died fighting.
        int[] _extractedTick;

        /// bd `app-qw01`: whether slot `i` has already been sent the news that
        /// HIS OWN raid is over. One send per collector per match — the news
        /// is a fact about a moment, not a state to be re-announced every tick.
        /// Allocated and released with `_extractedTick`, which is the same
        /// per-match memory in the same shape.
        bool[] _raidEndNotified;
        bool[] _disconnectKilled;

        ushort _epoch;
        bool _running;

        // Ф8 gate W-13. `-1` means "no match has ever started on this
        // instance yet" — the ONLY state that skips ValidateRoster's new
        // length check below, because a first start has no previous roster
        // to disagree with. Deliberately NOT nulled by `StopMatch`, unlike
        // `_connections` (whose `.Length` this field otherwise duplicates):
        // the whole point is to survive past a `StopMatch` so the NEXT
        // `StartMatch`/`RestartMatch` — the "restart" this class doc means —
        // can still compare against it.
        int _lastPlayerCount = -1;

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

        public MatchServer(NetworkManager networkManager, NetConfig netConfig, MatchEndPolicy endPolicy,
            SpectatePolicy spectatePolicy)
        {
            _nm = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
            _netConfig = netConfig ?? throw new ArgumentNullException(nameof(netConfig));
            _endPolicy = endPolicy ?? throw new ArgumentNullException(nameof(endPolicy));
            _spectatePolicy = spectatePolicy ?? throw new ArgumentNullException(nameof(spectatePolicy));

            // Fix-round 1, I1 — see the class doc's "SUBSCRIPTION TIMING"
            // paragraph for why this happens exactly once, here, and never
            // again in StartMatch/StopMatch.
            _nm.TimeManager.OnPostTick += OnPostTick;

            // Stage 2 Task 42a: the process's one `SpectateRequestNet`
            // handler, registered here for the same reason and with the same
            // permanence as the `OnPostTick` subscription above — this is a
            // DIFFERENT FishNet event chain (broadcast dispatch, not the tick
            // loop), so the FIFO-ordering argument that pins `OnPostTick` to
            // the constructor does not apply here the same way; what does
            // apply is the class doc's "ONE MatchServer PER PROCESS" rule —
            // exactly one registration must ever exist, and the constructor
            // is the one place that runs exactly once per instance.
            _nm.ServerManager.RegisterBroadcast<SpectateRequestNet>(OnSpectateRequest);

            // Stage 3 Т28 (spec §3.8 С17/Р237): the process's one
            // `LootRequestNet` handler, registered here for exactly the
            // reasons the line above states — the "ONE MatchServer PER
            // PROCESS" rule and the constructor being the one place that runs
            // once per instance. THIRD PERMANENT ROOT, not a second: the
            // class doc's garbage-collection paragraph counts them, and this
            // is one more delegate `_nm.ServerManager`'s own handler list
            // keeps into this instance for as long as the `NetworkManager`
            // lives.
            _nm.ServerManager.RegisterBroadcast<LootRequestNet>(OnLootRequest);
        }

        /// Starts a match — or restarts one, if this instance is already
        /// running one (see the class doc's re-entrancy paragraph).
        /// `connections[i]`/`controllers[i]` must name the same player; both
        /// arrays are required, non-empty and the same length. Calls
        /// `Configure` on every controller (I3.1) — a match this method
        /// returns from is therefore never silently inert.
        ///
        /// THE SECOND AND EVERY LATER START OF THIS INSTANCE MUST CARRY THE
        /// SAME ROSTER LENGTH as the first (Ф8 gate, W-13; §6k Р164). The guard
        /// lives in `ValidateRoster` and fires whether the caller came through
        /// `RestartMatch` or called this method again directly: a shorter or
        /// longer roster would silently rename players against the slot
        /// indices the join-phase handshake already promised them.
        public void StartMatch(long seed, in SimConfig simConfig, ushort epoch,
            NetworkConnection[] connections, PlayerNetworkController[] controllers)
        {
            // Fix-round 1, I-1: ONE copy of the roster rule, called first by
            // both entry points. `RestartMatch` sends an irreversible
            // `MatchRestartedNet` before it gets here, so it must be able to
            // reject a bad roster BEFORE that send — which it can only do if
            // the rule lives somewhere both can reach.
            ValidateRoster(connections, controllers, _lastPlayerCount);

            if (_running) StopMatch();

            int playerCount = controllers.Length;

            // Local first, committed together at the end — a throw partway
            // through construction (e.g. SnapshotAssembler's own fixed-part-
            // too-small guard) must never leave this instance half-updated,
            // holding a fresh world next to a stale assembler from the match
            // that just ended.
            // Stage 3 Т24: the raid's own per-slot memory, fresh for this
            // match — a restart's world starts back at tick 0, so either
            // array surviving one would date this raid's extractions by the
            // last raid's clock.
            var extractedTick = new int[playerCount];
            var disconnectKilled = new bool[playerCount];

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

            // Stage 2 Task 42a: fresh every match, same Р151 reasoning as the
            // pair above. `_viewpointIndex[i] = i` — identity and viewpoint
            // start equal for every slot, exactly matching
            // `SnapshotAssembler`'s own doc ("the two are equal until
            // spectating lands"); `_lastSpectateSwitchTick[i]` starts at the
            // sentinel so this match's first spectate request from any slot
            // is never refused for a cooldown inherited from nowhere.
            var viewpointIndex = new int[playerCount];
            var lastSpectateSwitchTick = new int[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                viewpointIndex[i] = i;
                lastSpectateSwitchTick[i] = SpectatePolicy.NoPriorSwitch;
            }

            // Stage 2 Task 42a fix-round 1, M-6: `default(SpectateRefusal)`
            // is `None`, exactly the "nothing logged yet" sentinel this array
            // wants — no explicit fill loop needed, unlike the two above.
            // `lastLoggedRefusalTick` needs no fill either: `ShouldLogRefusal`
            // never reads a slot's tick while that slot's reason is still the
            // sentinel (fix-round 2, I-C).
            var lastLoggedRefusal = new SpectateRefusal[playerCount];
            var lastLoggedRefusalTick = new int[playerCount];

            // app-88jb Т29: `false` — "the log has reported nothing being
            // trimmed" — is `default(bool)` AND the truth at match start, so
            // that half needs no fill loop, exactly like the pair above. The
            // tick half does need one: `NoRewindTrimLogYet` is what lets this
            // match's first line out at once instead of a bond after tick 0
            // (see that constant's own doc).
            // Fresh every match for the Р151 reason the arrays above carry: a
            // restart's world starts back at tick 0, so a `true` inherited from
            // the previous match would swallow this one's opening line, and an
            // inherited TICK STAMP would silence it until the new match's own
            // clock passed a stamp it may never reach at all.
            var rewindTrimmed = new bool[playerCount];
            var rewindTrimmedLogTick = new int[playerCount];
            for (int i = 0; i < playerCount; i++)
                rewindTrimmedLogTick[i] = NoRewindTrimLogYet;

            _world = world;
            _assembler = assembler;
            _connections = connections;
            _controllers = controllers;
            _epoch = epoch;
            // Ф8 gate W-13: committed AFTER ValidateRoster already passed
            // above, so a REJECTED restart never overwrites the length a
            // FUTURE restart must still compare against.
            _lastPlayerCount = playerCount;
            _lastInputsScratch = lastInputsScratch;
            _effectiveInputsScratch = effectiveInputsScratch;
            _starvedScratch = starvedScratch;
            _lastSeenInputTick = lastSeenInputTick;
            _lastFreshWorldTick = lastFreshWorldTick;
            _viewpointIndex = viewpointIndex;
            _lastSpectateSwitchTick = lastSpectateSwitchTick;
            _lastLoggedRefusal = lastLoggedRefusal;
            _lastLoggedRefusalTick = lastLoggedRefusalTick;
            _rewindTrimmed = rewindTrimmed;
            _rewindTrimmedLogTick = rewindTrimmedLogTick;
            // app-88jb Т29 fix-round (A-8): from the config THIS match was
            // built on, not from `_world.Config` a tick at a time — see both
            // fields' own doc for why the copy cannot go stale under a match.
            _rewindCapTicks = simConfig.Arena.RewindCapTicks;
            _rewindPictureTicks = simConfig.Arena.RewindPictureTicks;
            _extractedTick = extractedTick;
            _raidEndNotified = new bool[extractedTick.Length];
            _disconnectKilled = disconnectKilled;
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
            //
            // Stage 2 app-ck7: WHICH numbers is the launch line's to say now
            // (`-ring-latency`, read once per process by DevLatencyLaunch),
            // and both lines printed below are load-bearing rather than
            // decorative (lesson 195). Without the applied line, "the switch
            // worked" and "the switch was ignored" are the same observation on
            // this console; without the complaint, "the switch never arrived"
            // and "the switch was rejected" are.
            //
            // BOTH GO THROUGH UnityEngine.Debug, WHICH IS HOW THE WHOLE SERVER
            // ZONE LOGS (app-aor, owner's decision (a)): no operator-facing
            // line in this file, in MatchHandshake or in ServerBootstrap goes
            // through the NetworkManager's own logger any more, so this pair is
            // the rule rather than an exception anything nearby contradicts.
            // The mechanism is what makes it the rule, and it is written down
            // here because it is not visible from the call site: a
            // dedicated-server build defines UNITY_SERVER, and FishNet's
            // logging configuration answers that define with `_headlessLogging`,
            // whose default is LoggingType.Error (LevelLoggingConfiguration.
            // cs:55, :86-98 — and Server.unity leaves NetworkManager's
            // `_logging` unassigned, so the default is what runs). CanLog
            // admits only levels at or below the highest admitted one, and
            // Common(3)/Warning(2) both sit above Error(1), so anything a
            // headless build hands NetworkManager.Log or NetworkManager.
            // LogWarning is dropped before it reaches stdout.
            //
            // The build THIS PAIR matters for is `linux-server-dev` — NOT the
            // container, whose image is packed from the RELEASE server
            // (`client/docker/build.sh` builds `BuildLinuxServer`), where this
            // whole `#if` is gone before the compiler sees it and there is no
            // switch to operate. The dev server IS headless all the same, so
            // without `Debug` the only report an operator has of what his
            // launch line did would be silence. The lines outside this `#if`
            // are in the container too, which is why they cannot be left on a
            // logger the container silences. ServerBootstrap writes every
            // operator-facing diagnostic through UnityEngine.Debug and reaches
            // that log; `Debug` is spelled out in full because this file
            // imports System.Diagnostics (for Stopwatch) and does NOT import
            // UnityEngine, so a bare `Debug` would bind to the wrong type
            // outright rather than being reported as ambiguous.
            DevLatencyOptions latency = DevLatencyLaunch.Options;
            DevLatencySetup.Apply(_nm.TransportManager.LatencySimulator, _netConfig, _devStats,
                latency);
            if (latency.Complaint != null)
            {
                UnityEngine.Debug.LogWarning($"MatchServer: {latency.Complaint} Running on "
                    + "NetConfig's numbers instead, so Critical Rule 7's simulator stays ON — a "
                    + "value nobody could read must never be able to stand it down.");
            }
            UnityEngine.Debug.Log("MatchServer: dev latency simulator — "
                + DevLatencySetup.Describe(latency, _devStats)
                + ". This is the server's own OUTGOING half of the link; the client applies and "
                + "prints the other.");
#endif

            _running = true;
        }

        /// Starts the next match under a new epoch, telling the clients to
        /// reset first (Stage 2 Task 40, spec §3.10 Р44/Р60, §6k Р164) — the
        /// restart's single entry point. The trigger itself does not exist in
        /// Phase Ф8: this task ships the mechanism, and the handle that pulls
        /// it is Ф9's, the same shape of deferred wiring the client half of
        /// the handshake already carries.
        ///
        /// IT COVERS BOTH RESTART PATHS, AND THE ORDINARY ONE IS THE SECOND
        /// (fix-round 1, I-4): a match still RUNNING (the host cuts it short)
        /// and a match that has already ENDED (`Outcome` went non-`None`,
        /// `StopMatch` already ran, "play again"). There is deliberately no
        /// `_running` precondition — one would reject exactly the common case.
        ///
        /// THE CONNECTIONS COME IN AS AN ARGUMENT, NOT OFF A FIELD. They are
        /// the SAME connections as the finished match's — §6k Р164 keeps the
        /// roster and the handshake untouched across a restart (there is no
        /// join phase to a restart at all: spec §3.10 refuses joining a match
        /// in progress, and `MatchRoster`'s started flag is one-way) — but the
        /// object that OWNS that slot-to-connection table is the bootstrap,
        /// which built it from the handshake's `onAccepted`. Reading them off
        /// this class's own field would work only on the running-match path,
        /// since `StopMatch` releases it on the other.
        ///
        /// DEAD CONNECTIONS ARE NOT FILTERED OUT, ON PURPOSE. Р165's rule
        /// ("a match with a dead seat does not start") governs the FIRST
        /// start, where a compacted array would silently rename players
        /// whose `PlayerIndex` was already promised in the handshake. A
        /// restart has no such freedom either: the indices are the same
        /// indices, so a connection that died between the two matches keeps
        /// its slot, gets no `MatchRestartedNet` (the `IsActive` guard below),
        /// and its player is killed by step 2a of the new match's very first
        /// tick — the ordinary disconnect path, not a special case.
        ///
        /// THE ORDER INSIDE IS MANDATORY, AND EVERY STEP OF IT IS LOAD-BEARING:
        ///   * the roster is validated FIRST (fix-round 1, I-1). Everything
        ///     after the broadcast below is irreversible from the clients'
        ///     point of view: a throw out of `StartMatch`'s own guards, once
        ///     the message is already gone, would leave every client reset
        ///     into epoch N+1 while match N kept running — and they would
        ///     refuse every one of its remaining frames as `ForeignEpoch`,
        ///     silently, for the rest of the match. That is the same
        ///     discipline `StartMatch` already applies to its own
        ///     construction ("a throw partway through must never leave this
        ///     instance half-updated").
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
        public void RestartMatch(long seed, in SimConfig simConfig, ushort newEpoch,
            NetworkConnection[] connections, PlayerNetworkController[] freshControllers)
        {
            // Before the irreversible send — see this method's doc.
            ValidateRoster(connections, freshControllers, _lastPlayerCount);

            var restarted = new MatchRestartedNet
            {
                MatchEpoch = newEpoch,
                Seed = seed,
            };
            for (int i = 0; i < connections.Length; i++)
            {
                // Same guard, and the same reason, as the per-tick snapshot
                // send — see `OnPostTick`'s step 4 for what the guard actually
                // buys.
                if (!connections[i].IsActive) continue;
                _nm.ServerManager.Broadcast(connections[i], restarted, channel: Channel.Reliable);
            }

            StartMatch(seed, in simConfig, newEpoch, connections, freshControllers);
        }

        /// The roster rule both entry points enforce, in one place (fix-round
        /// 1, I-1): `connections[i]` and `controllers[i]` are the same player,
        /// so both arrays are required, non-empty and the same length.
        ///
        /// ONE `ParamName` IS A ROLE, NOT A LITERAL, AND THAT IS SAID RATHER
        /// THAN HIDDEN: `connections` is the name of that parameter in both
        /// public methods, but the controller array is `controllers` on
        /// `StartMatch` and `freshControllers` on `RestartMatch`, so a
        /// rejection from the latter reports `ParamName` "controllers" — the
        /// role the argument plays, not the identifier at that call site. The
        /// exception MESSAGE names the actual lengths either way, which is
        /// what a caller debugs from.
        ///
        /// `lastPlayerCount` (Ф8 gate W-13) IS THE PREVIOUS MATCH'S OWN
        /// `controllers.Length`, OR `-1` WHEN NONE HAS STARTED YET — the
        /// caller's `_lastPlayerCount` field, handed in explicitly rather
        /// than read off an instance so this stays the same kind of pure,
        /// static check the rest of this method already is. A restart that
        /// hands in a DIFFERENT player count than the match it is restarting
        /// silently renames every player past the shorter length against the
        /// slot indices `MatchWelcomeNet.PlayerIndex` already promised those
        /// clients in the join phase — a length MISMATCH is always wrong and
        /// is refused unconditionally; an EQUAL length is not a guarantee the
        /// two rosters actually name the same players in the same order
        /// (this check cannot see identities, only counts), so it closes
        /// only the provable half of the defect.
        /// `internal` rather than `private`, the same reason `EndedNetFor`
        /// is (see that method's own doc): a caller-supplied
        /// `lastPlayerCount` needs no live `NetworkManager` at all, so a test
        /// can drive every branch directly.
        internal static void ValidateRoster(NetworkConnection[] connections,
            PlayerNetworkController[] controllers, int lastPlayerCount)
        {
            if (connections == null) throw new ArgumentNullException(nameof(connections));
            if (controllers == null) throw new ArgumentNullException(nameof(controllers));
            if (connections.Length == 0)
            {
                throw new ArgumentException(
                    "MatchServer: a match needs at least one connection.", nameof(connections));
            }
            if (connections.Length != controllers.Length)
            {
                throw new ArgumentException(
                    $"MatchServer: connections.Length ({connections.Length}) must equal "
                    + $"controllers.Length ({controllers.Length}) — Ф8 must hand in the same player at "
                    + "the same index in both arrays.", nameof(controllers));
            }
            if (lastPlayerCount >= 0 && controllers.Length != lastPlayerCount)
            {
                throw new ArgumentException(
                    $"MatchServer: a restart — or any repeat start of this instance — must "
                    + $"field the same player count as the previous match (got {controllers.Length}, "
                    + $"previous match had {lastPlayerCount}) — a "
                    + "shorter or longer roster would silently rename players against the slot "
                    + "indices already promised to clients in the join-phase handshake.",
                    nameof(controllers));
            }
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
            _viewpointIndex = null;
            _lastSpectateSwitchTick = null;
            _lastLoggedRefusal = null;
            _lastLoggedRefusalTick = null;
            _rewindTrimmed = null;
            _rewindTrimmedLogTick = null;
            _extractedTick = null;
            _raidEndNotified = null;
            _disconnectKilled = null;
        }

        /// The `SpectateRequestNet` handler (Stage 2 Task 42a, spec §3.10
        /// :673-678, Р70) — every decision lives in `SpectatePolicy.Evaluate`;
        /// this method only gathers the facts, calls it once, and either
        /// commits the switch or does nothing.
        ///
        /// MUST NEVER THROW — THIS RUNS INSIDE A FISHNET BROADCAST HANDLER.
        /// In a release headless build (`BuildLinuxServer` never sets
        /// `BuildOptions.Development`) `ServerManager.ParseReceived` wraps
        /// every handler dispatch in a `try/catch` that turns ANY exception
        /// into an immediate `Kick(..., KickReason.MalformedData, ...)` —
        /// see `HandshakeDecision`'s own doc for the full mechanism, verified
        /// there against `BuildCommands.cs`. A bug in this method would
        /// therefore disconnect an innocent client and blame it for
        /// "malformed data". `SpectatePolicy.Evaluate` already answers with a
        /// VALUE for exactly this reason; the guards below exist so this
        /// method never even reaches `PlayerAt` with an index that could
        /// throw on its own.
        ///
        /// `if (!_running) return;` FIRST, THE SAME WAY `OnPostTick`'s OWN
        /// FIRST LINE DOES. Between matches `_world`/`_connections` are `null`
        /// (`StopMatch`), and a `SpectateRequestNet` arriving in that window
        /// — the join phase of the NEXT match, or the linger after this one
        /// ended — must be silently ignored rather than NRE.
        ///
        /// THE REQUESTING SLOT IS FOUND BY REFERENCE EQUALITY, DELIBERATELY
        /// NOT `==`/`Equals`. `NetworkConnection` overloads both of its own
        /// comparers to compare by `ClientId` rather than object identity —
        /// safe for the package's own ordinary use, but this method has no
        /// reason to trust anything looser than "this is literally the
        /// connection `StartMatch` put in this slot" for a lookup that
        /// decides which player's cooldown gets spent.
        ///
        /// THE RANGE CHECK RUNS BEFORE `PlayerAt(target)`, NEVER AFTER, AND
        /// SHARES ITS ONE RULE WITH `SpectatePolicy.Evaluate` (Stage 2 Task
        /// 42a fix-round 1, I-4). `SimulationWorld.PlayerAt` is a bare array
        /// index with no bounds guard of its own, so a `target` outside `[0,
        /// playerCount)` would throw before `SpectatePolicy` ever got a
        /// chance to answer `TargetOutOfRange` — exactly the exception this
        /// method exists to never produce. `targetAlive` is computed with
        /// `SpectatePolicy.IsTargetInRange` inlined into it (`in range AND
        /// PlayerAt(target).Alive`) rather than as two separate reads, so
        /// there is no path on which an invalid `target` reaches `PlayerAt`
        /// at all; the placeholder `false` a short-circuited range check
        /// leaves in `targetAlive` is never actually read by `Evaluate`,
        /// because `TargetOutOfRange` is checked before `TargetDead` in the
        /// policy's own fixed order. `IsTargetInRange` is the SAME static
        /// method `Evaluate` itself calls — before this fix-round the
        /// comparison was two independent copies (the original task-42a
        /// review, I-4): this one, and the one buried in `Evaluate`'s own
        /// body, and only the latter had a test watching it.
        ///
        /// DEATH OF THE CURRENT TARGET IS NOT HANDLED HERE, ON PURPOSE. This
        /// method only ever runs when a `SpectateRequestNet` arrives — it
        /// does not watch `_viewpointIndex[i]`'s target for a death that
        /// happens while nobody asked to switch away from it. That is a
        /// deliberate omission, not a gap: the spec gives no rule for
        /// auto-returning a spectator to their own body when their target
        /// dies, and inventing one would have the server silently decide
        /// something for the player instead of waiting for their own next
        /// request. `VisibilitySystem.Compute`/`EventRelevance` already read
        /// a dead `PlayerAt` without an `Alive` guard (`EventRelevance.cs`'s
        /// own "no Alive gate" comment), so a corpse works as a viewpoint
        /// without any special-casing here — the client simply keeps
        /// watching a body that stopped moving until it asks to look
        /// somewhere else.
        ///
        /// AN ACCEPTED SWITCH RESETS THE ASSEMBLER'S VIEWPOINT MEMORY (Stage
        /// 2 Task 42a fix-round 1, I-1 — CRITICAL finding, coordinator-
        /// verified). Without this, `SnapshotAssembler.BuildFor`'s
        /// hysteresis/linger continuity (`VisibilitySystem`'s own doc) reads
        /// `previous` — computed from the OLD viewpoint — for up to
        /// `VisibilityConfig.LingerTicks` further ticks after the switch,
        /// handing the spectator live current-tick positions from wherever
        /// they used to be looking. `SpectatePolicy`'s cooldown limits how
        /// OFTEN that leak can be triggered but does nothing to stop it from
        /// happening once per accepted switch — and across enough switches
        /// that is the exact map-scan Р70 exists to prevent. Called ONLY on
        /// acceptance, never on refusal: a refusal changes nothing about
        /// this connection's current viewpoint, so there is nothing of its
        /// memory to invalidate. See `ResetViewpointMemory`'s own doc for
        /// what is cleared and what is deliberately left alone.
        ///
        /// REFUSAL LOGGING IS RATE-LIMITED PER SLOT (Stage 2 Task 42a
        /// fix-round 1, M-6; the actual THROTTLE DECISION moved out of this
        /// method in fix-round 2, I-C — see `SpectatePolicy.ShouldLogRefusal`
        /// for the predicate and, importantly, for why it is a plain rate
        /// limit and not a "log on change" gate). This method's own job is
        /// only to gather the two pieces of memory that predicate needs
        /// (`_lastLoggedRefusal[slot]`, `_lastLoggedRefusalTick[slot]`) and
        /// write them back when it says yes — UNLIKE `MatchHandshake.
        /// Refuse`'s "one log per connection" shape, which this handler
        /// cannot copy: a handshake refusal ends the connection (or is the
        /// one-time `DuplicatePlayer` retry case), while a dead client can
        /// legitimately send `SpectateRequestNet` every single tick — a UI
        /// that retries on refusal, or simply spam from a player mashing the
        /// switch key. An ACCEPTED switch resets `_lastLoggedRefusal[slot]`
        /// back to `None`, which is `ShouldLogRefusal`'s own sentinel for
        /// "log the next one unconditionally" — so the first refusal after
        /// a fresh switch is never silently swallowed by a rate-limit window
        /// that started before the switch happened.
        void OnSpectateRequest(NetworkConnection connection, SpectateRequestNet msg, Channel channel)
        {
            if (!_running) return;

            // Not a seated connection (e.g. one `MatchHandshake` refused
            // with `DuplicatePlayer` but left connected, `MatchHandshake.cs`'s
            // own doc on that path) — nothing to act on.
            int slot = SlotOf(connection);
            if (slot < 0) return;

            int target = msg.TargetIndex;
            int playerCount = _world.PlayerCount;
            bool requesterAlive = _world.PlayerAt(slot).Alive;
            // Ф5 gate, review A-1 (spec §3.5 Р257): an extracted collector is
            // NOT a corpse, and the two are indistinguishable by `Alive` alone
            // since Т23 — so the flag has to come along.
            bool requesterExtracted = _world.PlayerAt(slot).Extracted;
            bool targetInRange = SpectatePolicy.IsTargetInRange(target, playerCount);
            bool targetAlive = targetInRange && _world.PlayerAt(target).Alive;
            int currentTick = _world.CurrentTick;

            SpectateRefusal refusal = _spectatePolicy.Evaluate(slot, target, playerCount,
                requesterAlive, requesterExtracted, targetAlive, _lastSpectateSwitchTick[slot],
                currentTick);

            if (refusal == SpectateRefusal.None)
            {
                _viewpointIndex[slot] = target;
                _lastSpectateSwitchTick[slot] = currentTick;
                // I-1: the old viewpoint's hysteresis/linger memory must not
                // survive into the new one — see this method's own doc.
                _assembler.ResetViewpointMemory(slot);
                // M-6/I-C: an accepted switch resets what the throttle
                // remembers, so the next refusal — of any reason, even one
                // this slot already logged before the switch — is fresh.
                _lastLoggedRefusal[slot] = SpectateRefusal.None;
                // UnityEngine.Debug, not _nm.Log (app-aor): see StartMatch's
                // own paragraph on the UNITY_SERVER logging ceiling for the
                // mechanism, and note that THIS line is outside any `#if` —
                // it has to reach the container's stdout, which is precisely
                // where the NetworkManager's logger never carried it.
                UnityEngine.Debug.Log($"MatchServer: spectate switch accepted — slot={slot} "
                    + $"target={target} tick={currentTick}.");
            }
            else if (_spectatePolicy.ShouldLogRefusal(_lastLoggedRefusal[slot],
                _lastLoggedRefusalTick[slot], currentTick))
            {
                _lastLoggedRefusal[slot] = refusal;
                _lastLoggedRefusalTick[slot] = currentTick;
                // Diagnostic wording only, the same discipline `HandshakeLog`'s
                // refusal tails state in their own docs (MatchHandshake.cs):
                // never "exploit"/"illegitimate"/"security" — an unmodified
                // client reaches every one of these reasons through ordinary
                // play (a stale target that just died, a double-tap past the
                // cooldown).
                //
                // UnityEngine.Debug, not _nm.Log (app-aor) — as above. The
                // level stays Log rather than following `MatchHandshake.
                // Refuse` to LogWarning: that one refusal is per CONNECTION and
                // ends it, while this one is per REQUEST on a live connection
                // and is already rate-limited by ShouldLogRefusal precisely
                // because a client may legitimately produce it every tick.
                UnityEngine.Debug.Log($"MatchServer: refusing spectate switch — slot={slot} "
                    + $"target={target} tick={currentTick} — {refusal}.");
            }
        }

        /// One loot operation asked for over the reliable channel (Stage 3
        /// Т28, spec §3.8). THE HANDLER DECIDES NOTHING, on purpose — every
        /// rule it applies belongs to a seam that EditMode can reach:
        /// `LootNet.IsCurrentEpoch` for the epoch, `SimulationWorld.
        /// TryBeginLoot` for the validation and the channel, `LootNet.
        /// ResultFor` for the reply. That split is `SpectateTests`'s own
        /// recorded lesson: FishNet wiring needs a live `NetworkManager` this
        /// project's tests cannot raise, so a decision left inline here is a
        /// decision nothing watches.
        ///
        /// THIS IS NOT A QUEUE, AND THAT IS NOT AN OVERSIGHT (coordinator
        /// R-223). Spec §3.8 asks for the operations to run "on a tick
        /// boundary, in arrival order", and this handler IS that boundary:
        /// FishNet dispatches incoming broadcasts inside
        /// `TimeManager.IncreaseTick`'s own loop, between `OnPreTick` and
        /// `OnPostTick` (TimeManager.cs:726/734/752 of the pinned 4.7.2) and
        /// only once per frame — so this runs after the last tick this world
        /// completed and before the next one, never inside `TickAll`, and
        /// requests arrive in the order they were sent. Deferring them into a
        /// list drained by `OnPostTick` would buy one tick of input freshness
        /// (see `TryBeginLoot`'s own doc on the one-tick lag) at the price of
        /// a capacity, an overflow policy and a loss counter — machinery for
        /// a difference of 33 ms on the tick a window opens.
        ///
        /// THREE ARRIVALS GET NO REPLY AT ALL, and each is a request that
        /// reached no match rather than one that was refused: a stopped
        /// instance, an unseated connection, and a foreign epoch. There is no
        /// match-scoped truth to report about any of them — `LootResultNet`
        /// echoes a match's own verdict, and none of the three has one. The
        /// client's own `LootRequestTracker` covers the silence: its ghost is
        /// dropped when the epoch changes, which is the only way the first and
        /// the third can persist.
        void OnLootRequest(NetworkConnection connection, LootRequestNet msg, Channel channel)
        {
            if (!_running) return;
            if (!LootNet.IsCurrentEpoch(msg.MatchEpoch, _epoch)) return;

            int slot = SlotOf(connection);
            if (slot < 0) return;

            LootRefusal code = _world.TryBeginLoot(slot, (LootOp)msg.Op, msg.ContainerId, msg.Slot);
            _nm.ServerManager.Broadcast(connection, LootNet.ResultFor(in msg, code),
                channel: Channel.Reliable);
        }

        /// This connection's player slot, or -1 when it holds none — the one
        /// home of that lookup, shared by both broadcast handlers above
        /// (rule 2; `OnSpectateRequest` was its only caller until Т28 brought
        /// the second). `connections[i]` names player `i`, which is the
        /// class doc's own "what Ф8 must hand in" contract.
        int SlotOf(NetworkConnection connection)
        {
            for (int i = 0; i < _connections.Length; i++)
            {
                if (ReferenceEquals(_connections[i], connection)) return i;
            }
            return -1;
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
            {
                _lastInputsScratch[i] = _controllers[i].Core.LastServerInput;
                // bd `app-mi4`: taken now, so an input arriving after this line
                // overwrites nothing the world was still going to use — and one
                // that arrives BEFORE the next gather is counted as the loss it
                // is.
                _controllers[i].Core.MarkServerInputTaken();
            }

            EffectiveInputBatch.Gather(_lastInputsScratch, preTickWorldTick, starveTicks,
                _lastSeenInputTick, _lastFreshWorldTick, _effectiveInputsScratch, _starvedScratch);

            for (int i = 0; i < _starvedScratch.Length; i++)
                if (_starvedScratch[i]) _assembler.StatsFor(i).InputStarved++;

            // bd `app-mi4`: two counters that had no writer at all, so the
            // post-match log printed zeros where measurements belonged (the
            // `app-c3m` genre — an instrument that lies). Both are running
            // TOTALS owned by their sources, so they are assigned rather than
            // incremented: the world counts a player's dropped edge requests
            // (`SimulationWorld.RejectedEdgeRequestsFor`) and the prediction
            // core counts inputs a newer replicate overwrote before this loop
            // could take them.
            for (int i = 0; i < _controllers.Length; i++)
            {
                NetStats stats = _assembler.StatsFor(i);
                stats.EdgeRequestsRejected = _world.RejectedEdgeRequestsFor(i);
                stats.InputOverwritten = _controllers[i].Core.OverwrittenServerInputs;
            }

            // 1a. app-88jb Т29 (spec §3.6): the rewind depth each collector
            // claimed this tick, weighed against what THE SERVER'S OWN SOCKET
            // READING says is possible. The whole rule is
            // `SanitizedRewindDepth` and this step is wiring — read that
            // method's doc before this loop, in particular for what the round
            // trip time it is fed actually reads on a dedicated server, and for
            // the single witness of the wiring's liveness (the Ф4 lag gate,
            // never a green EditMode run).
            //
            // ⚠ WHAT A GREEN RUN DOES NOT WITNESS HERE, BEYOND THE LIVENESS OF
            // THAT READING (fix-round, A-3). The one statement below that makes
            // Т29 BEHAVIORAL is the write-back — `_effectiveInputsScratch[i].
            // RewindTicks = allowed` — and no EditMode case executes it:
            // `RewindSanityTests` calls the pure function directly, and no test
            // drives `OnPostTick` at all (it needs a live `NetworkManager`).
            // Delete that one line and the whole suite stays green while the
            // server goes on rewinding by exactly the number the client asked
            // for. The Ф4 lag gate is what has to catch it, and it has to catch
            // it by the OBSERVABLE consequence — a claim above the estimate is
            // answered at the TRIMMED depth, in the line logged below and in
            // the hit it produces — not merely by checking that the server's
            // round trip reading is non-zero.
            //
            // BETWEEN STEP 1 AND STEP 2 BY NECESSITY. `Gather` is what puts a
            // claimed depth into `_effectiveInputsScratch` — a repeat carries
            // the depth the real input was sent with — and `TickAll` is what
            // consumes it, so this is the only window in which the number can
            // still be corrected. Nothing between the two reads the depth.
            //
            // THE READINGS ARE TAKEN ONCE, OUTSIDE THE LOOP, because they are
            // the same for every slot: the round trip time is a PROCESS-wide
            // number and not a per-connection one (the method's own ⛔
            // paragraph measures why FishNet 4.7.2 offers the server no
            // per-connection reading at all), and the tolerance is one asset
            // field. The two arena numbers are not read here at all any more —
            // they were taken once for the whole match in `StartMatch`; see
            // `_rewindCapTicks`' own doc for why that copy cannot go stale, and
            // for the per-tick `SimConfig` copy it removes.
            //
            // The trim is deliberately NOT the same statement as the arena
            // clamp `SimInputSanitizer.Sanitize` applies inside `TickAll`: that
            // one brings any depth into the arena's cap, this one asks whether
            // a depth inside the cap is one this connection could have earned.
            // ⚠ AND THE TWO ARE NOT INDEPENDENT, WHICH AN EARLIER WORDING HERE
            // CLAIMED THEY WERE (fix-round, A-1/B-4). Handing `_rewindCapTicks`
            // in bounds the answer from ABOVE for every input; nothing here
            // bounds it from BELOW, and a negative `NetConfig.RewindSanityTicks`
            // makes this method answer 255 — at which point the arena clamp
            // inside `TickAll` is the only thing standing between that and the
            // world, i.e. load-bearing rather than independent. The method's
            // own ⛔ "NO FLOOR AT ZERO" paragraph carries the measurement and
            // what it looks like to an operator.
            float rttMs = _nm.TimeManager.RoundTripTime;
            int sanityTicks = _netConfig.RewindSanityTicks;
            // THE THROTTLE'S BOND IS ONE SECOND, DERIVED AND NOT DECLARED.
            // `NetConfig.TickRate` is the tick rate itself (its own field
            // comment: "Mirrors TimeManager._tickRate"), so this many ticks is
            // one second at whatever rate the process runs — and it is exactly
            // the interval FishNet keeps for its own timing update
            // (`TimeManager.TimingTickInterval` IS `_tickRate`, and the modulo
            // in `SendTimingAdjustment` carries the comment "Send every
            // second"). No asset field of its own is added for it, on purpose:
            // this is the frequency of a DIAGNOSTIC, not a number the game
            // plays by — CRITICAL RULE 6 governs the second kind — and a knob
            // nobody could see the effect of tuning is precisely what
            // `EntityFadeTicks`' own history warns against. The precedent
            // borrows its bond the same way, from the cooldown the switch
            // itself already uses.
            int logBondTicks = _netConfig.TickRate;
            for (int i = 0; i < _effectiveInputsScratch.Length; i++)
            {
                byte claimed = _effectiveInputsScratch[i].RewindTicks;
                byte allowed = SanitizedRewindDepth(claimed, rttMs, sanityTicks,
                    _rewindCapTicks, _rewindPictureTicks);
                bool trimmed = allowed != claimed;
                // `SimInput` is a STRUCT, so the write has to land in the ARRAY
                // ELEMENT the span handed to `TickAll` reads, never in a copy
                // of it lifted out into a local.
                if (trimmed) _effectiveInputsScratch[i].RewindTicks = allowed;

                // ON A CHANGE, AND NEVER MORE OFTEN THAN ONCE PER BOND — see
                // `_rewindTrimmed`'s own doc for the precedent whose FORM this
                // follows, for why the change test alone would bound nothing
                // here, and for the costs of the bond. BOTH SIDES ARE
                // WRITTEN: an operator who reads that trimming started needs to
                // read that it stopped, or the log reports a match going wrong
                // and never reports it recovering. The line always names the
                // CURRENT state, so the first one out of a quiet window says
                // what is happening now.
                //
                // Diagnostic wording only, the same discipline the refusal
                // tails keep — never "exploit"/"cheat"/"illegitimate". An
                // unmodified collector reaches this branch through ordinary
                // play: his measured depth rises over an estimate the server
                // builds from a round trip time it currently reads as zero.
                //
                // The tick is the PRE-`TickAll` reading, the one step 1's own
                // arithmetic is done in (see the class doc's "TWO READINGS"
                // paragraph) — this line is about the input going INTO the
                // tick, not about the tick that just completed. It is also the
                // domain the bond is measured in, so both sides of that
                // subtraction come from one clock.
                //
                // UnityEngine.Debug, not _nm.Log (app-aor), and outside any
                // `#if`: under UNITY_SERVER the FishNet logger's ceiling is
                // Error, and this line has to reach the container's stdout.
                if (trimmed != _rewindTrimmed[i]
                    && preTickWorldTick - (long)_rewindTrimmedLogTick[i] >= logBondTicks)
                {
                    _rewindTrimmed[i] = trimmed;
                    _rewindTrimmedLogTick[i] = preTickWorldTick;
                    UnityEngine.Debug.Log(trimmed
                        ? $"MatchServer: trimming claimed rewind depth — slot={i} "
                            + $"claimed={claimed} allowed={allowed} tick={preTickWorldTick}."
                        : $"MatchServer: claimed rewind depth is inside the estimate again "
                            + $"— slot={i} claimed={claimed} allowed={allowed} "
                            + $"tick={preTickWorldTick}.");
                }
            }

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
                // is gone dies BEFORE this tick's world is captured for
                // broadcast (step 3), so the death this tick discovers is
                // reported by this tick's own frame — see the class doc.
                // `connections[i]`/`controllers[i]`/player `i` are the same
                // player by index (the class doc's "what Ф8 must hand in"),
                // so this loop's bound is also the world's player count.
                for (int i = 0; i < _connections.Length; i++)
                {
                    if (MatchEndPolicy.ShouldKillOnDisconnect(
                            _connections[i].IsActive, _world.PlayerAt(i).Alive))
                    {
                        _world.KillPlayerNoDamage(i);
                        // Stage 3 Т24 (Р271): the corpse this leaves is
                        // indistinguishable from any other, so the reason is
                        // remembered HERE, at the one moment it is known.
                        // The predicate's own `playerAlive` half makes this
                        // write happen at most once per slot per match.
                        _disconnectKilled[i] = true;
                    }
                }

                // 2b. Stage 2 Task 40 (spec §3.10/§3.11): is the match over?
                // The decision belongs to MatchEndPolicy — this only gathers
                // its two inputs, both read AFTER step 2a so a disconnect that
                // emptied the arena ends the match on the very tick it
                // happened. DECIDED here, EXECUTED at step 5b: this tick still
                // owes its clients a frame, and the events in it (fix-round 1,
                // C-1: `PlayerDied` above all) have no second chance to be
                // sent.
                // Stage 3 Task 1 (spec §3.10, errata E-1/R-13 decision), moved
                // into `MatchProgress.Observe` by Т24. That method counts the
                // two extraction-aware inputs `Evaluate` needs AND stamps the
                // tick a collector left, which is memory the world does not
                // keep. Т1's own note that `PlayerState.Extracted` "has no
                // writer yet" is retired here: Т23 gave it one, so on a real
                // raid `activePlayers` now genuinely differs from
                // `alivePlayers` and `anyExtracted` genuinely goes true.
                MatchProgress.Observe(_world, postTickWorldTick, _extractedTick,
                    out int alivePlayers, out int activePlayers, out bool anyExtracted);

                MatchEndReason reason =
                    _endPolicy.Evaluate(postTickWorldTick, alivePlayers, activePlayers, anyExtracted);

                // 2c. bd `app-qw01`: TELL EACH COLLECTOR THAT HIS OWN RAID IS
                // OVER, once, on the tick it ends. A raid ends for one player
                // long before the match does — he dies, or he walks out — and
                // until this send his own counters travelled nowhere at all,
                // so his end-of-raid screen printed dashes until everyone else
                // had finished too.
                //
                // AFTER `Evaluate`, BECAUSE THE MATCH'S OWN ENDING OUTRANKS
                // THIS ONE: when the same tick also ends the match, step 5b's
                // `EndMatch` sends the authoritative `MatchEndedNet` to
                // everybody, and sending both would hand the screen two
                // tallies of the same raid in one frame.
                //
                // BEFORE THE FRAME (step 4) RATHER THAN AFTER, for the reason
                // fix-round 1 C-1 gives about `PlayerDied`: this tick still
                // owes its clients a frame, and the reliable message may as
                // well be queued alongside it.
                SendRaidEndNotices(reason != MatchEndReason.None);

                // 3. One capture + wire-event expansion shared by every
                // connection this tick (SnapshotAssembler.BeginTick's own doc).
                _assembler.BeginTick(_world);

                // 4. Per-connection frame, Unreliable (spec §3.7 table Р27:
                // state travels unreliably). `identityIndex` is always `i` —
                // WHO this connection is never changes. `viewpointIndex` is
                // `_viewpointIndex[i]` (Stage 2 Task 42a) — WHERE it looks
                // from, `i` by default and only ever moved by that slot's own
                // accepted `SpectateRequestNet` (`OnSpectateRequest` below).
                // See the class doc's "what Ф8 must hand in" paragraph.
                for (int i = 0; i < _connections.Length; i++)
                {
                    // Task 36, I3.3: a disconnected/disconnecting connection
                    // gets no frame, because `BuildFor` would otherwise pay
                    // the full visibility/routing cost of a frame nobody
                    // receives. `IsActive` is the package's own "not
                    // disconnected/disconnecting" predicate
                    // (`NetworkConnection.cs`, `ClientId >= 0 &&
                    // !Disconnecting`).
                    //
                    // Stage 2 Task 40 fix-round 1 (M-4) CORRECTS the second
                    // half of what this comment used to claim. Verified in
                    // `NetworkConnection.Buffer.cs`'s own `SendToClient`: a
                    // `Disconnecting` connection is dropped SILENTLY
                    // (`if (Disconnecting) return;` is the method's first
                    // line), and the `LogWarning` one branch below it is only
                    // reachable for a connection whose `ClientId` is already
                    // negative. So the guard's payoff is the skipped build,
                    // not avoided per-tick stdout spam — the spam claim was
                    // true for only one of the two states `IsActive` covers.
                    if (!_connections[i].IsActive) continue;

                    int len = _assembler.BuildFor(i, i, _viewpointIndex[i], _epoch);
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

                // 5b. Stage 2 Task 40, fix-round 1 (C-1): the end decided at
                // step 2b is EXECUTED here, once this tick has delivered
                // everything it owed — the frame, its events and the
                // reconcile. Ending before step 3 would have let `finally`'s
                // `ClearEvents` discard the closing tick's events unread, and
                // the death the match ended on would never have been sent at
                // all. `EndMatch` stops the match, so nothing below it in this
                // try block may assume `_world`/`_assembler` still exist —
                // which is why it is LAST.
                //
                // `&& _running` (Ф8 gate W-12) RE-CHECKS THE FLAG DECIDED AT
                // 2b IS STILL TRUE HERE, AFTER STEPS 3-5 RAN. `reason` was
                // computed against THIS match's `_world` at step 2b; steps
                // 3-5 then call out through `Broadcast` and
                // `SetAuthoritativeState`, and if any of that somehow ran a
                // nested `StopMatch` synchronously (the same class of
                // re-entrancy `OnPostTick`'s own first line and the
                // `finally` block below both already guard against), this
                // flag would be the one place left to notice before
                // `EndMatch` acted on a decision that no longer describes
                // the match this instance is holding. Practically
                // unreachable today — nothing in this codebase restarts a
                // match synchronously from inside a `Broadcast` callback —
                // and NOT a complete fix even if it happened: a nested
                // `StopMatch` FOLLOWED BY a nested `StartMatch` would leave
                // `_running` back at `true` for the NEW match, which this
                // one check cannot tell apart from the original. What it
                // does close, cheaply, is the simpler half — a nested
                // `StopMatch` with no restart — rather than calling
                // `EndMatch` a second time on an instance already stopped.
                if (reason != MatchEndReason.None && _running)
                {
                    EndMatch(reason);
                }
                else if (reason != MatchEndReason.None)
                {
                    // Ф8 gate, re-review M-6: the `_running` half above turns a
                    // nested StopMatch into a silent no-op, and silence is what
                    // this class refuses everywhere else. The state is
                    // unreachable on any legal path (OnPostTick returns at its
                    // first line when no match is running), so reaching it means
                    // another subscriber stopped the match from inside this very
                    // tick — worth a line no operator can miss rather than a match
                    // that simply stops reporting.
                    // UnityEngine.Debug, not _nm.LogError (app-aor). Error is
                    // the ONE level FishNet's headless ceiling would have let
                    // through, so this line is moved for uniformity rather than
                    // to rescue it: after the sweep no line OF OURS in the
                    // server's own half goes through NetworkManager's logger,
                    // so there is no lone survivor here for the next line to
                    // imitate. FishNet's own diagnostics still use it — see
                    // ServerManager's unhandled-PacketId errors — which is
                    // exactly why the claim has to be about our code and not
                    // about the process.
                    // The sweep stopped at the server's own half on purpose:
                    // Networking/Client/ClientMatchLink.cs still logs through
                    // `_nm.Log` (owner's decision 3a). Those three are a
                    // different question, not a solved one — a client build
                    // does not define UNITY_SERVER, so THIS ceiling never
                    // applied to them, but `_guiLogging` has a ceiling of its
                    // own and an Editor left on the Dedicated Server target
                    // does define UNITY_SERVER. Recorded in the phase-gate
                    // errata basket rather than settled here.
                    UnityEngine.Debug.LogError("MatchServer: the match ended mid-tick for " + reason
                        + ", but something else had already stopped it in this same tick — "
                        + "no MatchEndedNet was sent and Outcome stays None.");
                }
            }
            finally
            {
                // 6. LAST, ALWAYS (Р22) — in `finally` (Task 36 fix-round 1,
                // M7) so a broken step 3-5b (an exception out of BuildFor,
                // Broadcast, etc.) still clears the event buffer: a headless
                // server has no render frame to otherwise drain it, and a
                // buffer that survives a bad tick either overflows the next
                // one or hands a later tick's clients events that already went
                // stale. Step 5b's own send is inside that protection too —
                // and `EndMatch` additionally makes its own bookkeeping
                // unconditional, so a failed send cannot leave the match
                // running (Task 40 fix-round 1, I-2).
                //
                // `world` (the LOCAL captured above, Task 36 fix-round 2 W8) —
                // not `_world` — because a `StopMatch` running inside this
                // same try block nulls the FIELD as part of its own cleanup;
                // reading the field here would then throw an NRE that MASKS
                // whatever exception the steps were actually propagating,
                // instead of letting that real exception surface. Two paths
                // reach that state: a nested `StopMatch` triggered
                // synchronously from steps 3-5 (e.g. a disconnect handler
                // reacting to step 4's own `Broadcast` call — the Task 36
                // case), and, since Stage 2 Task 40, the ORDINARY end of a
                // match at step 5b, which stops it deliberately.
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
        ///
        /// THE END IS UNCONDITIONAL ONCE IT HAS BEGUN (fix-round 1, I-2). The
        /// sends are in a `try` whose `finally` stops the match and records
        /// the outcome, for the same reason step 6 of `OnPostTick` is in one:
        /// a throw out of any single `Broadcast` would otherwise leave
        /// `_running` true and `Outcome` `None`, so the next tick would reach
        /// the same end condition, rebuild the same summary and attempt the
        /// same failing send — every tick, forever, re-sending `MatchEndedNet`
        /// to every connection ahead of the failing one and never letting the
        /// process exit. The exception itself still propagates; only the
        /// match's own state is settled first.
        /// bd `app-qw01`: one personal `RaidEndedNet` per collector whose raid
        /// has just ended, and never a second one.
        ///
        /// WIRING ONLY — the decision is `RaidEndNotice.IsDue`'s, the numbers
        /// are `BuildSummary`/`EndedNetFor`'s, and this method does nothing a
        /// test would want to reach that those two do not already own (the
        /// class doc's "not unit-tested, on purpose" covers exactly this
        /// shape).
        ///
        /// THE SUMMARY IS BUILT ONLY IF SOMEBODY IS OWED ONE. It is a whole-
        /// roster capture — three arrays and a walk over every player — and a
        /// raid ends for a given collector exactly once, so at most three of
        /// these are built in a match. Building it per tick regardless would
        /// be a per-tick allocation on the server's hot path for nothing.
        ///
        /// `MatchEndReason.None` IS THE REASON IT CARRIES, and that is the
        /// contract `RaidEndedNet`'s own doc states: the match has not ended.
        void SendRaidEndNotices(bool matchEnding)
        {
            MatchSummary summary = default;
            bool summaryBuilt = false;

            for (int i = 0; i < _connections.Length; i++)
            {
                if (!RaidEndNotice.IsDue(_world.PlayerAt(i).Alive, _raidEndNotified[i],
                        matchEnding))
                    continue;

                // MARKED EVEN IF THE CONNECTION IS GONE: the news is owed to a
                // raid, not to a socket, and a collector who dropped out will
                // not come back to be told. Leaving the flag clear would make
                // this loop re-ask about him on every remaining tick of the
                // match.
                _raidEndNotified[i] = true;
                if (!_connections[i].IsActive) continue;

                if (!summaryBuilt)
                {
                    summary = BuildSummary(MatchEndReason.None);
                    summaryBuilt = true;
                }

                _nm.ServerManager.Broadcast(_connections[i],
                    new RaidEndedNet { Tally = EndedNetFor(in summary, i) },
                    channel: Channel.Reliable);
            }
        }

        void EndMatch(MatchEndReason reason)
        {
            // Stage 3 Т24 (coordinator R-172): the raid is over IN THE WORLD
            // as well, and this is the one place that says so. It happens
            // BEFORE the summary is built and long before `StopMatch`, so the
            // world this method reads is one whose own phase agrees with the
            // verdict being reported — and so that the invariants standing on
            // Ended (no exit is open on a finished raid; the phase machine
            // does not move one) hold from the instant the decision is
            // executed rather than from the next tick, which never comes.
            _world.MarkMatchEnded();

            MatchSummary summary = BuildSummary(reason);

            // Captured before `StopMatch` nulls the field.
            NetworkConnection[] connections = _connections;
            MatchResultsNet results = ResultsNetFrom(in summary);
            try
            {
                for (int i = 0; i < connections.Length; i++)
                {
                    // A connection that already left gets nothing — the same
                    // guard, for the same reason, as the per-tick snapshot
                    // send (step 4).
                    if (!connections[i].IsActive) continue;
                    _nm.ServerManager.Broadcast(connections[i], EndedNetFor(in summary, i),
                        channel: Channel.Reliable);
                    // Stage 3 Т34: and the public board, the same connection
                    // and the same channel. Built ONCE outside this loop —
                    // every copy is identical by construction, which is the
                    // whole difference between it and the message above.
                    _nm.ServerManager.Broadcast(connections[i], results,
                        channel: Channel.Reliable);
                }
            }
            finally
            {
                StopMatch();

                _lastSummary = summary;
                _outcome = reason;
            }
        }

        /// The match's numbers, captured ONCE (see the class doc). Called
        /// while the world and the assembler are still alive, from `EndMatch`
        /// only — which is the only moment `StatsFor` can still answer
        /// (fix-round 1, I-6: `StopMatch` releases the assembler, so a
        /// bootstrap polling `Outcome` on its own tick is already too late).
        MatchSummary BuildSummary(MatchEndReason reason)
        {
            int playerCount = _world.PlayerCount;
            var playerStats = new MatchStats[playerCount];
            for (int i = 0; i < playerCount; i++) playerStats[i] = _world.StatsAt(i);

            // `NetStats` is a class, so this array holds the assembler's own
            // live instances and keeps them reachable past `StopMatch` — no
            // copy of the counters is made, and none is wanted (Р151: one
            // number, one home).
            var connectionStats = new NetStats[playerCount];
            for (int i = 0; i < playerCount; i++) connectionStats[i] = _assembler.StatsFor(i);

            // Stage 3 Т24 (spec §3.10, errata E-3): THE RESULT, taken here
            // and nowhere else. Every value below needs something `StopMatch`
            // is about to release — the players' own flags, their backpacks,
            // the world's tick — which is exactly why this method exists at
            // all (see its doc, and `ServerBootstrap.cs`'s own note that the
            // world is gone by the time a bootstrap polling `Outcome` asks).
            uint finalTick = (uint)_world.CurrentTick;
            var outcome = new MatchOutcome[playerCount];
            var creditsTotal = new int[playerCount];
            var loot = new byte[playerCount][];
            var survivedSeconds = new int[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                PlayerState p = _world.PlayerAt(i);
                outcome[i] = MatchEndPolicy.OutcomeFor(p.Alive, p.Extracted, p.ExtractKind,
                    _disconnectKilled[i]);
                creditsTotal[i] = CreditsCarriedOut(_world, i);
                loot[i] = LootCarriedOut(_world, i);
                // Ticks to seconds through NetConfig.TickRate — the same
                // number MatchEndPolicy's own limit was converted with
                // (`ServerBootstrap`: MatchMaxDurationSeconds * TickRate), and
                // integer division, so the field reports COMPLETED seconds
                // rather than a rounded-up one nobody lived.
                survivedSeconds[i] = SurvivedTicksFor(p.Alive, p.Extracted, _extractedTick[i],
                    playerStats[i].DeathTick, finalTick) / _netConfig.TickRate;
            }

            return new MatchSummary(reason, _epoch, finalTick,
                _world.WorldStats, _world.DroppedEvents, playerStats, connectionStats,
                outcome, creditsTotal, loot, survivedSeconds);
        }

        /// Player `slot`'s own copy of the end-of-match message: the world's
        /// numbers, identical in every copy, plus that one player's own. The
        /// counters are copied across field by field rather than by embedding
        /// the simulation's own structs — see `MatchEndedNet`'s own doc for
        /// why the protocol spells its surface out.
        ///
        /// `internal` FOR THE TESTS, DELIBERATELY (fix-round 1, I-5). This is
        /// a pure function of a summary and a slot — no FishNet, no world, no
        /// clock — and eleven same-typed assignments in a row are exactly the
        /// shape where a swapped pair compiles, runs and reports the wrong
        /// number for the rest of the project's life. The class doc's
        /// "not unit-tested, on purpose" covers the FishNet WIRING around it,
        /// not arithmetic like this; `EffectiveInputBatch` in this same file
        /// sets the precedent for lifting the decidable part out where a test
        /// can reach it. `MatchLifecycleTests.EndedNetFor_CopiesEveryStat`
        /// pins every field.
        internal static MatchEndedNet EndedNetFor(in MatchSummary summary, int slot)
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

                // Stage 3 Т24 (errata E-3): this player's own result.
                // AmmoSpent/CellsPicked come off `stats`, not off a fifth and
                // sixth summary array — MatchStats is already their home and
                // its own doc names this task as their READER (Р151).
                Outcome = (byte)summary.Outcome[slot],
                CreditsTotal = summary.CreditsTotal[slot],
                Loot = summary.Loot[slot],
                AmmoSpent = stats.AmmoSpent,
                CellsPicked = stats.CellsPicked,
                SurvivedSeconds = summary.SurvivedSeconds[slot],

                WavesCleared = world.WavesCleared,
                MobSpawnsSkipped = world.MobSpawnsSkipped,
                ProjectileSpawnsSkipped = world.ProjectileSpawnsSkipped,
            };
        }

        /// The raid's PUBLIC scoreboard, built once and sent to everyone
        /// (Stage 3 Т34, spec §3.10/§3.11, Р270).
        ///
        /// THE SAME SUMMARY BOTH MESSAGES COME OFF (errata E-3). `EndedNetFor`
        /// above reads one slot of it for one connection; this reads every
        /// slot for everybody. One capture, two readings — so a collector's
        /// credits cannot say one thing on his own screen and another on the
        /// board beside it, which is what two builders reading two places
        /// would eventually produce.
        ///
        /// ONE ALLOCATION PAIR PER MATCH. Both arrays are minted here rather
        /// than borrowed from the summary: `summary.Outcome` is
        /// `MatchOutcome[]`, a simulation-side enum the wire carries as bytes,
        /// and `CreditsTotal` is handed out to FishNet's serializer, which
        /// must not be given a reference the server keeps mutating. This runs
        /// once, on the tick a match ends.
        ///
        /// `internal` FOR THE TESTS, for the reason `EndedNetFor` states: a
        /// pure function of a summary, no FishNet and no world, and a run of
        /// same-typed assignments is exactly where a swapped pair compiles and
        /// misreports forever.
        internal static MatchResultsNet ResultsNetFrom(in MatchSummary summary)
        {
            int seats = summary.Outcome.Length;
            var outcome = new byte[seats];
            var credits = new int[seats];
            for (int slot = 0; slot < seats; slot++)
            {
                outcome[slot] = (byte)summary.Outcome[slot];
                credits[slot] = summary.CreditsTotal[slot];
            }

            return new MatchResultsNet
            {
                Reason = (byte)summary.Reason,
                MatchEpoch = summary.Epoch,
                FinalTick = summary.FinalTick,
                Outcome = outcome,
                CreditsTotal = credits,
            };
        }

        /// What slot `slot` carried OUT of the factory, in credits (Stage 3
        /// Т24, spec §3.10).
        ///
        /// GATED ON Extracted, NOT SIMPLY SUMMED, and the gate is the whole
        /// point: §3.10 counts what was carried out, and only an extraction
        /// carries anything out. The other three endings need no arithmetic
        /// of their own — a corpse's backpack has already moved into the
        /// container KillPlayer spawns from it (so the sum would be zero
        /// anyway, for both Died and Disconnected), while a Stranded
        /// collector is still holding everything and is precisely the case a
        /// bare sum would misreport. One gate, stated once, correct for all
        /// four.
        ///
        /// `internal` FOR THE TESTS, DELIBERATELY — the same reason
        /// EndedNetFor above is: a pure function of a world and a slot, where
        /// the rule is worth a mutation and the FishNet wiring around it is
        /// not reachable by one.
        internal static int CreditsCarriedOut(SimulationWorld world, int slot)
            => world.PlayerAt(slot).Extracted ? world.InventoryCreditsOf(slot) : 0;

        /// The items behind that number, in carry order — the `loot` half of
        /// spec §3.10's record, and empty for exactly the collectors
        /// CreditsCarriedOut pays nothing.
        ///
        /// IDS ONLY. Tier and price are the catalog's answers
        /// (ItemCatalogLookup, R-89), the client verified its own copy of that
        /// catalog at the handshake, and the log line resolves them where it
        /// prints. Copying the resolved values into the record instead would
        /// be three numbers traveling where one identifies them.
        internal static byte[] LootCarriedOut(SimulationWorld world, int slot)
        {
            if (!world.PlayerAt(slot).Extracted) return Array.Empty<byte>();

            int count = world.InventoryCountOf(slot);
            if (count == 0) return Array.Empty<byte>();

            var items = new byte[count];
            for (int i = 0; i < count; i++) items[i] = world.InventoryItemAt(slot, i);
            return items;
        }

        /// How long the raid lasted for one collector, in world ticks (Stage 3
        /// Т24, spec §3.10 `survivedSeconds`; the caller divides by the tick
        /// rate).
        ///
        /// THREE ENDINGS, THREE CLOCKS, AND NONE OF THEM IS A NEW STATE
        /// FIELD. A collector who EXTRACTED stopped when he stepped out —
        /// and extraction, unlike death, stamps nothing in the world at all
        /// (Р223: it is not a death, so there is no DeathTick), which is why
        /// `extractedTick` is MatchServer's own memory and why the raid's own
        /// FinalTick would be wrong here: the other two may keep playing for
        /// another quarter of an hour after he is home. A CORPSE stopped at
        /// MatchStats.DeathTick, already the world's own answer, read rather
        /// than recorded a second time (Р151). Anyone STILL STANDING survived
        /// the whole raid, so FinalTick is his by definition.
        ///
        /// Extracted is tested BEFORE Alive for the reason OutcomeFor's own
        /// doc gives: a man who walked out is not alive either.
        internal static int SurvivedTicksFor(bool alive, bool extracted, int extractedTick,
            int deathTick, uint finalTick)
        {
            if (extracted) return extractedTick;
            if (!alive) return deathTick;
            return (int)finalTick;
        }

        /// THE REWIND DEPTH A CLIENT CLAIMS, WEIGHED AGAINST WHAT THE SERVER'S
        /// OWN SOCKET READING SAYS IS POSSIBLE (app-88jb Т29, spec §3.6,
        /// Р374/Р404). The depth arrives inside `SimInput.RewindTicks` because
        /// the client is the only party that knows which picture it was
        /// shooting at — that field's own doc carries why it travels in the
        /// input rather than off the socket. The server takes the number and
        /// does not take it on trust; this method is the arithmetic of the
        /// second opinion:
        ///
        ///     min(claimed,
        ///         TicksFromSeconds(roundTripMs * 0.001f * 0.5f)
        ///             + pictureTicks + sanityTicks,
        ///         capTicks)
        ///
        /// THE INTERPOLATION BUFFER IS PART OF THE ESTIMATE, NOT AN INDULGENCE.
        /// Half the round trip is the one-way delay, but the client states its
        /// depth as "predicted tick minus rendered tick" (`RewindDepthMeter.
        /// Measure`), so the buffer its picture deliberately trails by sits
        /// inside every honest claim. An estimate built from the delay alone
        /// would punish the collector on a perfect connection hardest, because
        /// his whole depth IS the buffer. `sanityTicks` is the tolerance the
        /// owner tunes on top of that (`NetConfig.RewindSanityTicks`).
        ///
        /// TRIMMING IS NOT A REFUSAL OF THE CONNECTION. Nothing is dropped and
        /// nothing is disconnected: the input is legal and the tick runs on it
        /// like any other. The single thing the server withholds is belief in
        /// the one number the client cannot prove.
        ///
        /// ⚠ AND THE POSSESSIVE IS GONE FROM THIS DOC ON PURPOSE (fix-round,
        /// B-8): "what HIS socket says" promised a per-connection measurement
        /// that the paragraph below then disproves. There is ONE reading, it
        /// belongs to the process, and `OnPostTick` takes it once before the
        /// loop and applies it to every slot alike.
        ///
        /// ⛔ WHAT `roundTripMs`' SOURCE ACTUALLY READS ON A DEDICATED SERVER,
        /// MEASURED AGAINST THE PINNED FishNet 4.7.2 AND NOT ASSUMED:
        /// identically zero. ONE path produces the zero and TWO more close the
        /// ways around it — an earlier wording credited all three with the zero
        /// itself, which only the first of them does (fix-round, B-6).
        ///   THE ZERO: `TimeManager.RoundTripTime` has exactly one writer,
        /// `ModifyPing`, and its only caller is the ping-response parser in
        /// `ClientManager`; the ping itself goes out under a client guard, and
        /// a dedicated server never becomes a client — `ClientManager.
        /// StartConnection` is reached from the client bootstrap alone, and the
        /// server scene carries no client component at all. Nothing ever
        /// assigns the field, so it holds its initial value for the life of the
        /// process.
        ///   THE FIRST WAY AROUND, CLOSED — AND FOR A CORRECTED REASON
        /// (fix-round, B-7). `NetworkConnection.PacketTick` IS a server-side
        /// stamp, its own doc saying "only available on the server", and
        /// FishNet does subtract it: `EstimatedTick.LocalTickDifference` is
        /// `TimeManager.LocalTick` minus the tick that packet landed on. What
        /// that yields is the AGE of this connection's newest packet, not a
        /// round trip — there is no return leg in it at all — and FishNet uses
        /// it for exactly that, as the "ticksBehind" test in
        /// `NetworkConnection.WriteState` that skips a client which has not
        /// "communicated within 5 seconds". For a client replicating every tick
        /// it reads near zero however long the link is. The earlier wording
        /// dismissed this path as "the CLIENT's tick, counted from another
        /// origin", which describes the REMOTE half of those estimates and not
        /// the server-local stamp the subtraction actually uses — a right
        /// conclusion resting on the wrong fact.
        ///   THE SECOND WAY AROUND, CLOSED: `Transport`/`Tugboat` expose no
        /// per-connection delay in their public surface whatsoever.
        ///   THE CONSEQUENCE, STATED AS A NUMBER RATHER THAN AS A RISK: with
        /// the reading at zero the middle term degenerates to
        /// `pictureTicks + sanityTicks`, an effective ceiling of 5 ticks at the
        /// shipped 3 and 2. That is 167 ms, inside the 200 ms CRITICAL RULE 5
        /// allows, and since app-gtj6 EQUAL to the shipped cap of 5 (the
        /// 200 ms ceiling is still 6) rather than looser. The fact
        /// is recorded here, not repaired: a server-side measurement of our own
        /// (a private ping, a tick echo, transport internals) is forbidden by
        /// coordinator ruling 223, because the spec's own input declares that
        /// second path unnecessary — the check may be pure.
        ///
        /// ⚠ WHAT A GREEN RUN OF THIS FUNCTION DOES NOT WITNESS. Every case in
        /// `RewindSanityTests` hands the five numbers in, so the file can speak
        /// for the arithmetic and for nothing about whether the CALLER hands it
        /// a live round trip time rather than a stale or identically-zero one.
        /// Liveness is a property of the WIRING, not of the formula, and its
        /// only witness is the Ф4 lag gate under 80 ms RTT and 5 % loss
        /// (CR 7), where "the server's per-connection RTT is not zero" is
        /// checked by a running process instead of by an assertion.
        ///
        /// ⛔ NO FLOOR AT ZERO IS ADDED HERE, and the boundary is named rather
        /// than left for a reader to find. Drag the sum below zero and the
        /// minimum goes with it, and the `(byte)` cast wraps: at a sum of -1
        /// this method answers 255. TWO of its parameters can take the sum
        /// there, and BOTH are now held UPSTREAM rather than here — an earlier
        /// wording claimed the answer was bounded "for every input the function
        /// can be given", which was false on the second until the owner's round
        /// decision 4б gave it a rule (fix-round, A-1/B-4).
        ///   `pictureTicks` IS HELD, and by a written home: NetInvariants rule
        /// #11 pins `Arena.RewindPictureTicks` to `Net.InterpBufferTicks`, rule
        /// #1 of the same validator holds that above zero, and `ServerBootstrap`
        /// fails the process on any violation. `SimStates`' own note on the
        /// same field refuses a second clamp for the same reason (ruling 139),
        /// and a second answer here would be the same duplication.
        ///   `sanityTicks` IS HELD TOO, SINCE THE OWNER'S ROUND DECISION 4б,
        /// AND BY A HOME OF THE SAME KIND: NetInvariants rule #12 refuses a
        /// negative `NetConfig.RewindSanityTicks`, and `ServerBootstrap` fails
        /// the process on it like any other violation. Before that rule the
        /// field had nothing behind it at all — its `[Range(0, 6)]` is an
        /// Inspector hint, which a hand-edited YAML, a script or a test is not
        /// bound by. THE MEASUREMENT IS KEPT HERE, because it is what the rule
        /// refuses rather than a hazard this method still carries: at -4, with
        /// the shipped picture of 3 and the reading at zero, the estimate is
        /// -1, the minimum is -1, and this method answers 255. The only thing
        /// downstream of that is `SimInputSanitizer.Sanitize` inside `TickAll`,
        /// which clamps the depth to `Arena.RewindCapTicks` — so the
        /// operator-visible result would be the check INVERTED: a tolerance of
        /// -4 or below granting every claim the full cap instead of trimming it
        /// (fix-round, A-1/B-4). Between -1 and -3 the sum stays non-negative
        /// and nothing wraps — those merely make the estimate stricter than the
        /// owner asked for, and rule #12 refuses them for that.
        ///   ⛔ AND STILL NO CLAMP IS ADDED HERE, for the reason the
        /// `pictureTicks` paragraph above gives about its own field: with a
        /// written home upstream, a second answer in this method would be the
        /// duplication ruling 139 refuses. Ruling 226 had refused the RULE for
        /// want of a plan; decision 4б is that plan, and the rule went where
        /// rules live.
        internal static byte SanitizedRewindDepth(
            byte claimed, float roundTripMs, int sanityTicks, int capTicks, int pictureTicks)
        {
            int estimate = SimulationWorld.TicksFromSeconds(roundTripMs * 0.001f * 0.5f)
                + pictureTicks + sanityTicks;
            // `Unity.Mathematics.math.min`, the minimum this whole zone reaches
            // for, and the very idiom `SimInputSanitizer.Sanitize` already
            // applies to this same field. The `(int)` cast on `claimed` is for
            // the reader rather than the compiler — overload resolution picks
            // min(int, int) either way — the same note the sanitizer's own
            // clamp carries beside it.
            return (byte)math.min((int)claimed, math.min(estimate, capTicks));
        }
    }

    /// The post-tick reading of who is still in the raid (Stage 3 Т1, moved
    /// here by Т24) — the three inputs MatchEndPolicy.Evaluate needs, plus the
    /// one fact about a finished raid that nothing in the world records.
    ///
    /// LIFTED OUT OF OnPostTick BECAUSE Т24 GAVE IT ARITHMETIC WORTH TESTING.
    /// As Т1 left it this was a counting loop inline in the FishNet-touching
    /// class; the stamping below is new memory with a rule of its own
    /// (first write wins), and MatchServer's own class doc is explicit that
    /// what lives inline there can be reached by no EditMode test and
    /// therefore caught by no mutation. Same split, and the same file, as
    /// EffectiveInputBatch and InputStarvation already occupy.
    /// WHEN A COLLECTOR IS OWED THE NEWS THAT HIS OWN RAID IS OVER
    /// (bd `app-qw01`) — a pure core beside `MatchEndPolicy`/`MatchProgress`,
    /// the same split `EffectiveInputBatch` and `HandshakeDecision` already
    /// occupy in this folder.
    ///
    /// ONE PREDICATE COVERS BOTH WAYS OUT. A raid ends for a collector when he
    /// dies or when he walks out, and extraction CLEARS `Alive` as well as
    /// setting `Extracted` (Р223 — the body leaves the arena), which
    /// `MatchProgress.Observe`'s own doc measures and states. So `!alive` is
    /// the whole test, and naming `Extracted` beside it would be a second
    /// clause with no witness that could kill a mutation of it — the exact
    /// thing that doc says out loud about its own redundant clause rather than
    /// leaving for a reviewer to find.
    ///
    /// ONCE PER COLLECTOR PER MATCH. The news is a fact about a MOMENT, not a
    /// state to be re-announced: without the second argument this would put a
    /// reliable message on the wire every remaining tick of the match for
    /// every collector who had already finished.
    ///
    /// AND NEVER ON THE TICK THE MATCH ITSELF ENDS. `EndMatch` sends the
    /// authoritative `MatchEndedNet` to everybody on that tick; sending both
    /// would hand the end-of-raid screen two tallies of one raid in a single
    /// frame, and the personal one would be the weaker of the two (no end
    /// reason, no final tick of the match).
    internal static class RaidEndNotice
    {
        internal static bool IsDue(bool alive, bool alreadyNotified, bool matchEnding)
            => !alive && !alreadyNotified && !matchEnding;
    }

    internal static class MatchProgress
    {
        /// `worldTick` is the POST-TickAll reading — "the tick that just
        /// finished" — and every count below is a fact about the world as it
        /// now stands, disconnect-kills included (they run first, step 2a).
        ///
        /// "ACTIVE" IS ALIVE AND NOT YET EXTRACTED — the spec's own definition
        /// (§3.5), kept verbatim from Т1. MEASURED FACT WORTH STATING: the two
        /// counts cannot actually differ today, because extraction clears
        /// Alive (Р223 — the body leaves the arena), so the invariant
        /// `!(Alive && Extracted)` makes the second clause redundant. What
        /// separates a resolved raid from a wipe is `anyExtracted`, not the
        /// gap between these two numbers. The clause stays because "active"
        /// is defined that way and this method must not depend on an
        /// invariant it does not itself enforce — but it has no witness that
        /// could kill a mutation of it, and that is said out loud here rather
        /// than left for a reviewer to find (spec §3.10's own wording implies
        /// the two counts can diverge; recorded for the Ф9 amendment batch).
        ///
        /// THE STAMP IS FIRST-WRITE-WINS, AND ZERO IS THE SENTINEL. A
        /// collector leaves once and stays left, so a later tick must not
        /// move the moment he did it; and no extraction can be observed at
        /// world tick 0, because ExtractionSystem runs inside TickAll and the
        /// first observation any caller makes is of tick 1 or later. That is
        /// what makes 0 safe as "never left" — the same sentinel argument
        /// MatchState.DirectorDeathTick already stands on.
        internal static void Observe(SimulationWorld world, int worldTick, int[] extractedTick,
            out int alivePlayers, out int activePlayers, out bool anyExtracted)
        {
            alivePlayers = 0;
            activePlayers = 0;
            anyExtracted = false;

            for (int i = 0; i < world.PlayerCount; i++)
            {
                PlayerState p = world.PlayerAt(i);
                if (p.Alive) alivePlayers++;
                if (p.Alive && !p.Extracted) activePlayers++;
                if (!p.Extracted) continue;

                anyExtracted = true;
                if (extractedTick[i] == 0) extractedTick[i] = worldTick;
            }
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
    /// BOTH ARRAYS ARE INDEXED BY PLAYER SLOT — the same index `connections`
    /// and `controllers` use throughout `MatchServer`, and the same one a
    /// client was given in its `MatchWelcomeNet`. Each is a fresh array per
    /// match end (two allocations at the end of a match are not a per-tick
    /// cost).
    ///
    /// THE ARRAYS ARE EXPOSED, SO READ-ONLY USE IS A CALLER CONTRACT, NOT A
    /// PROPERTY OF THE TYPE (fix-round 1, M-7). `readonly` on the fields
    /// freezes the references, never their contents, and `MatchServer.
    /// LastSummary` hands both to the bootstrap. `PlayerStats` holds COPIES of
    /// the world's structs, so writing into it corrupts only the caller's own
    /// reading; `ConnectionStats` holds the assembler's `NetStats` OBJECTS
    /// rather than copies. By the time `LastSummary` can be read the
    /// assembler that owned them is already released and the next match
    /// allocates its own set, so in the ordinary flow this array is their
    /// last reader — but a caller that cached a reference from `StatsFor`
    /// DURING the match shares those same objects, and a write through either
    /// alias is visible through the other. The caller must treat both as
    /// read-only.
    ///
    /// `ConnectionStats` EXISTS BECAUSE THERE IS NO OTHER MOMENT TO TAKE IT
    /// (fix-round 1, I-6). Spec §3.11's log line needs the snapshot's dropped
    /// entities and events, which live in the per-connection `NetStats` the
    /// assembler allocates — and `MatchServer.StatsFor` throws once
    /// `StopMatch` has released the assembler, i.e. by the time a bootstrap
    /// polling `Outcome` could ask. Capturing the references here costs one
    /// array and keeps the numbers in their single home (Р151) instead of
    /// making the bootstrap copy them every tick against the day a match ends.
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

        /// One entry per connection slot — the assembler's own live instances
        /// (`NetStats` is a class), not copies. Never null for a summary a
        /// real match end produced.
        public readonly NetStats[] ConnectionStats;

        /// Stage 3 Т24 (spec §3.10, errata E-3) — THE RESULT, four arrays and
        /// no more. `AmmoSpent`/`CellsPicked` are deliberately NOT here even
        /// though the plan's Interfaces block listed them: both are already
        /// fields of `PlayerStats[slot]`, whose own doc names Т24 as their
        /// READER. A second array holding the same number is precisely the
        /// "two copies of one number" defect Р151 and this type's own class
        /// doc are written against, so the log line and `EndedNetFor` read
        /// them off `MatchStats` instead (coordinator R-195).
        ///
        /// The four below have no other home. `Outcome` needs the server's
        /// own memory of who disconnected; `CreditsTotal`/`Loot` need the
        /// per-player `Loot.Inventory`, which `StopMatch` releases with the
        /// world; `SurvivedSeconds` needs the tick a collector EXTRACTED,
        /// which — unlike a death — stamps nothing in the world at all.
        public readonly MatchOutcome[] Outcome;
        public readonly int[] CreditsTotal;

        /// Item ids, in carry order, for the collector who walked out with
        /// them — EMPTY for everyone else, because spec §3.10's record counts
        /// what left the factory, not what a corpse was holding. Tier and
        /// price stay the catalog's to answer (`ItemCatalogLookup`, R-89).
        public readonly byte[][] Loot;

        public readonly int[] SurvivedSeconds;

        public MatchSummary(MatchEndReason reason, ushort epoch, uint finalTick,
            in WorldStats world, int droppedEvents, MatchStats[] playerStats,
            NetStats[] connectionStats, MatchOutcome[] outcome, int[] creditsTotal,
            byte[][] loot, int[] survivedSeconds)
        {
            Reason = reason;
            Epoch = epoch;
            FinalTick = finalTick;
            World = world;
            DroppedEvents = droppedEvents;
            PlayerStats = playerStats;
            ConnectionStats = connectionStats;
            Outcome = outcome;
            CreditsTotal = creditsTotal;
            Loot = loot;
            SurvivedSeconds = survivedSeconds;
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
