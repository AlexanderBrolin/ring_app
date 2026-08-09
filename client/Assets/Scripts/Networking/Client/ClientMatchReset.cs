using System;

namespace Ring.Networking.Client
{
    /// Stage 2 Task 40 (spec §3.10's "full reset of the client's network
    /// state", Р60, §6k Р164): the ONE handler that clears every per-match
    /// seam on this side of the wire, called on the Reliable lifecycle message
    /// that names a new epoch (`MatchRestartedNet`, and the welcome that opens
    /// the first match).
    ///
    /// WHY ONE HANDLER AND NOT FIVE CALL SITES. The five objects below each
    /// carry a piece of "where this client is in the match", and each of them
    /// fails SILENTLY when it is the one that was forgotten — no exception, no
    /// log line, just one guarantee quietly dead for the whole next match:
    ///   * an unreset `EventDedup` still tracks the PREVIOUS epoch, and its
    ///     epoch test opens both of its questions, so every frame of the new
    ///     match is refused for its state AND its events. (Its stale
    ///     `_lastAppliedTick` is a second, independent refusal — but of state
    ///     only, and only in the same-epoch case: `TryAcceptEvent` never reads
    ///     that field at all.)
    ///   * an unreset `SnapshotQueue` refuses the frames themselves as
    ///     `ForeignEpoch`;
    ///   * an unreset `RenderClock` keeps running on the previous match's tick
    ///     numbers, and ignores the new match's snapshots by the same epoch
    ///     test;
    ///   * an unreset `GhostProjectiles` carries the previous match's tracers
    ///     into the new one;
    ///   * an unreset `StalePolicy` — the SECOND seam whose failure is not a
    ///     refusal — keeps the finished match's two global clocks, and both
    ///     of its readings go wrong. `GlobalStarvation` is
    ///     `renderTick - lastAppliedTick >= staleTicks`, and the new match's
    ///     `renderTick` starts back near zero while `lastAppliedTick` still
    ///     holds the old match's high value, so the subtraction stays
    ///     negative and the connection indicator does not light until this
    ///     match's own ticks climb past the finished match's last applied one
    ///     — potentially the whole match, as `StalePolicy`'s own doc puts it.
    ///     `ConfirmedAbsent` is `lastNonTruncatedTick > lastSeenTick[id]`,
    ///     and that stale, enormous left-hand side outranks every fresh
    ///     sighting, so Р149's gate stands propped open from the new match's
    ///     first frame. What an entity then does depends on whether the
    ///     FINISHED match ever saw its id, and the two answers fail in
    ///     OPPOSITE directions: an id that match had seen keeps its high
    ///     `lastSeenTick` — `OnEntitySeen` refuses to lower a sighting tick
    ///     it already holds — so that entity's age reads negative and
    ///     `StateOf` answers `Live` however long it is really absent, a
    ///     freeze rather than a fade; only an id NEW to this match reaches
    ///     the propped-open gate carrying a fresh sighting tick, and that one
    ///     can start fading with no legitimate absence behind it.
    /// Five call sites spread across a receiver would be five chances to
    /// forget one; one call site is one.
    ///
    /// THE SET'S COMPLETENESS IS CONTRACTUAL, NOT SOMETHING THE TYPE SYSTEM
    /// HOLDS UP. Nothing in C# can say "these are all the objects a restart
    /// must clear". What holds it up instead is mechanical: `MatchLifecycleTests`
    /// carries one test per seam, each observing that seam through its own
    /// public behavior, and the task's mutation wave removes one call at a
    /// time to prove each test really catches its own. A SIXTH PER-MATCH SEAM
    /// MUST THEREFORE BE ADDED IN TWO PLACES — here, and in
    /// `MatchLifecycleTests` — or it inherits none of that.
    ///
    /// THIS CLASS DOES NOT OWN THE FIVE AND DOES NOT BUILD THEM. They are
    /// handed in. Their construction parameters have nothing in common (an
    /// arena config, per-match timings, capacities, a `NetStats` sink), and
    /// pulling those in here would make this the sixth home for the client's
    /// network configuration. The owner is the network backend of Task 44,
    /// which builds all five for its own reasons and hands them here once.
    ///
    /// TWO OF THE FIVE TAKE NO EPOCH, AND THAT IS CORRECT.
    /// `GhostProjectiles.Reset`/`StalePolicy.Reset` track no epoch at all —
    /// their reset is total by construction, which is why neither has an
    /// epoch-shaped seam to pass one to.
    ///
    /// A RESET AT THE SAME EPOCH IS A FULL RESET, NOT A NO-OP. That is
    /// `SnapshotQueue`'s own documented contract ("`Reset` IS A FULL RESET
    /// EVEN AT THE SAME EPOCH") and this class does not second-guess it:
    /// deduplicating a lifecycle message that arrived twice belongs to the
    /// caller that receives it, which is the only place that can tell a
    /// repeat from a deliberate same-epoch restart.
    ///
    /// NOBODY CALLS THIS IN PHASE Ф8. The class is declared and tested here
    /// because the restart it serves is built here; subscribing it to a
    /// received `MatchRestartedNet` belongs to the client-side receiver of
    /// Ф9/Task 44 — the same shape of deferred wiring the client half of the
    /// handshake (Task 39) already carries.
    public sealed class ClientMatchReset
    {
        readonly EventDedup _dedup;
        readonly SnapshotQueue _snapshotQueue;
        readonly RenderClock _renderClock;
        readonly GhostProjectiles _ghosts;
        readonly StalePolicy _stalePolicy;

        /// Every seam is required, and each is guarded separately so a wiring
        /// mistake names the argument it was actually made in — five guards
        /// answering "one of them was null" would leave the caller to find out
        /// which.
        public ClientMatchReset(EventDedup dedup, SnapshotQueue snapshotQueue, RenderClock renderClock,
            GhostProjectiles ghosts, StalePolicy stalePolicy)
        {
            _dedup = dedup ?? throw new ArgumentNullException(nameof(dedup));
            _snapshotQueue = snapshotQueue ?? throw new ArgumentNullException(nameof(snapshotQueue));
            _renderClock = renderClock ?? throw new ArgumentNullException(nameof(renderClock));
            _ghosts = ghosts ?? throw new ArgumentNullException(nameof(ghosts));
            _stalePolicy = stalePolicy ?? throw new ArgumentNullException(nameof(stalePolicy));
        }

        /// Clears all five seams and starts tracking `epoch` in the three that
        /// track one. Call this on the Reliable lifecycle message that names
        /// the epoch — never on a snapshot: a snapshot of an unknown epoch is
        /// refused by these very objects and must never be the thing that
        /// switches them over.
        public void ResetForEpoch(ushort epoch)
        {
            _dedup.Reset(epoch);
            _snapshotQueue.Reset(epoch);
            _renderClock.ResetForEpoch(epoch);
            _ghosts.Reset();
            _stalePolicy.Reset();
        }
    }
}
