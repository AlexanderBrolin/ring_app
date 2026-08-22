using System;

namespace Ring.Networking.Client
{
    /// Stage 2 Task 40 (spec §3.10's "full reset of the client's network
    /// state", Р60, §6k Р164): the ONE handler that clears every per-match
    /// seam on this side of the wire, called on the Reliable lifecycle message
    /// that names a new epoch (`MatchRestartedNet`, and the welcome that opens
    /// the first match).
    ///
    /// WHY ONE HANDLER AND NOT SEVEN CALL SITES. The seven objects below each
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
    ///   * an unreset `ClientEventQueue` — the SIXTH seam, added in Task 44b
    ///     — still holds the finished match's accepted-but-unshown events,
    ///     keyed by ABSOLUTE tick, and the new match replays its ticks from
    ///     zero. Each leftover record is therefore handed out again the moment
    ///     the NEW match's render clock reaches that same tick NUMBER — the
    ///     previous match's death or hit surfacing minutes into a world it
    ///     never happened in — and until then it sits occupying capacity the
    ///     new match's own events are refused at (`OverflowDroppedEvents`).
    ///     This one does not merely lose a guarantee, it INVENTS events: the
    ///     only seam of the seven that fails by producing something rather than
    ///     by refusing.
    /// Seven call sites spread across a receiver would be seven chances to
    /// forget one; one call site is one.
    ///
    /// THE SET'S COMPLETENESS IS CONTRACTUAL, NOT SOMETHING THE TYPE SYSTEM
    /// HOLDS UP. Nothing in C# can say "these are all the objects a restart
    /// must clear". What holds it up instead is mechanical: `MatchLifecycleTests`
    /// carries one test per seam, each observing that seam through its own
    /// public behavior, and the task's mutation wave removes one call at a
    /// time to prove each test really catches its own. A NEW PER-MATCH SEAM
    /// MUST THEREFORE BE ADDED IN TWO PLACES — here, and in
    /// `MatchLifecycleTests` — or it inherits none of that. That is not a
    /// hypothetical: the sixth seam (`ClientEventQueue`) arrived in Task 44b
    /// and the seventh (`TracerProjectiles`) in bd `app-s0u`
    /// by exactly this route, and the paragraph is left standing, with the
    /// number moved on, for the seventh.
    ///
    /// THIS CLASS DOES NOT OWN THE SEVEN AND DOES NOT BUILD THEM. They are
    /// handed in. Their construction parameters have nothing in common (an
    /// arena config, per-match timings, capacities, an event budget, a
    /// `NetStats` sink), and pulling those in here would make this a second
    /// home for the client's network configuration. The owner is the network
    /// backend of Task 44, which builds all seven for its own reasons and hands
    /// them here once.
    ///
    /// THREE OF THE SEVEN TAKE NO EPOCH, AND THAT IS CORRECT.
    /// `GhostProjectiles.Reset`/`StalePolicy.Reset`/`ClientEventQueue.Reset`
    /// track no epoch at all — their reset is total by construction, which is
    /// why none of them has an epoch-shaped seam to pass one to.
    ///
    /// A RESET AT THE SAME EPOCH IS A FULL RESET, NOT A NO-OP. That is
    /// `SnapshotQueue`'s own documented contract ("`Reset` IS A FULL RESET
    /// EVEN AT THE SAME EPOCH") and this class does not second-guess it:
    /// deduplicating a lifecycle message that arrived twice belongs to the
    /// caller that receives it, which is the only place that can tell a
    /// repeat from a deliberate same-epoch restart.
    ///
    /// THE CALLER ARRIVED IN PHASE Ф9 (Task 44b — this paragraph replaces
    /// "NOBODY CALLS THIS IN PHASE Ф8", which was true when the class was
    /// written and is not any more). `ClientMatchLink` is the one production
    /// caller: it hands the epoch here on the two Reliable messages that
    /// carry the right to reset — the welcome that opens the first match and
    /// `MatchRestartedNet` — and on nothing else. In particular it does NOT
    /// call this on `MatchEndedNet`, for the reason that method's own doc
    /// gives.
    public sealed class ClientMatchReset
    {
        readonly EventDedup _dedup;
        readonly SnapshotQueue _snapshotQueue;
        readonly RenderClock _renderClock;
        readonly GhostProjectiles _ghosts;
        /// bd `app-s0u`: rounds rebuilt from the wire. Without this line the
        /// tracers of the match that just ended keep flying through the new
        /// one — the ids the new match mints would collide with tracks nobody
        /// can retire any more, since their `ProjectileEnded` belonged to the
        /// epoch that is over.
        readonly TracerProjectiles _tracers;
        readonly StalePolicy _stalePolicy;
        /// Stage 3 Т32б (bd `app-dut`): the mobs' fade bookkeeping, which is a
        /// SECOND thing to forget on an epoch change and fails exactly the way
        /// the player policy above does when it is not — an id from the match
        /// before, answered for.
        readonly EntityStaleTracker _mobStale;
        readonly ClientEventQueue _eventQueue;

        /// Every seam is required, and each is guarded separately so a wiring
        /// mistake names the argument it was actually made in — eight guards
        /// answering "one of them was null" would leave the caller to find out
        /// which.
        ///
        /// `eventQueue` was APPENDED, not inserted (Task 44b), and `mobStale`
        /// is appended for the same reason (Т32б), so every existing
        /// positional call site keeps the meaning it already had — the same
        /// discipline `HandshakeRefusal.UnrecognizedRejection` followed when
        /// its own enum grew.
        public ClientMatchReset(EventDedup dedup, SnapshotQueue snapshotQueue, RenderClock renderClock,
            GhostProjectiles ghosts, StalePolicy stalePolicy, ClientEventQueue eventQueue,
            TracerProjectiles tracers, EntityStaleTracker mobStale)
        {
            _dedup = dedup ?? throw new ArgumentNullException(nameof(dedup));
            _snapshotQueue = snapshotQueue ?? throw new ArgumentNullException(nameof(snapshotQueue));
            _renderClock = renderClock ?? throw new ArgumentNullException(nameof(renderClock));
            _ghosts = ghosts ?? throw new ArgumentNullException(nameof(ghosts));
            _stalePolicy = stalePolicy ?? throw new ArgumentNullException(nameof(stalePolicy));
            _eventQueue = eventQueue ?? throw new ArgumentNullException(nameof(eventQueue));
            _tracers = tracers ?? throw new ArgumentNullException(nameof(tracers));
            _mobStale = mobStale ?? throw new ArgumentNullException(nameof(mobStale));
        }

        /// Clears all eight seams and starts tracking `epoch` in the three that
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
            _eventQueue.Reset();
            _tracers.Reset();
            _mobStale.Reset();
        }
    }
}
