using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// Stage 2 Task 32 (spec §3.9 Р37/Р38/Р83, §6i Р150е): the ring buffer
    /// between the network and interpolation — the one thing that decides, for
    /// every arriving frame, whether its blocks get a slot to be decoded into
    /// at all.
    ///
    /// NAMESPACE, NOT ASSEMBLY. `Ring.Networking.Client` is a folder inside the
    /// `Ring.Networking` assembly, next to `EventDedup` and `RenderClock`.
    /// Nothing here touches UnityEngine or FishNet: this is a pure decision
    /// object, testable in EditMode with no scene. The component that decodes
    /// wire bytes into the slot this class hands out is Task 44; the receiver
    /// that owns this queue and calls `Reset` on the Reliable lifecycle message
    /// is also Task 44 (mirroring `EventDedup`/`RenderClock`, both reset by the
    /// same caller on the same message).
    ///
    /// THE STORED ELEMENT IS A `RenderSnapshot`, PREALLOCATED IN THE
    /// CONSTRUCTOR. The whole ring — `Depth` instances, each built off the SAME
    /// `ArenaSimConfig` — is sized once and never grows; `Admit` on an
    /// `Accepted` verdict hands back one of those preallocated instances for
    /// the caller to decode wire bytes into (Task 44's job, explicitly out of
    /// this task's scope — see the class the plan created this alongside,
    /// `RenderSnapshot.CopyFrom`, Task 32's OTHER half — NOTE: this queue never
    /// calls `CopyFrom`; the caller decodes wire bytes directly into the slot
    /// `Admit` hands back, fix-round 1 correction). Nothing below allocates
    /// after the constructor returns.
    ///
    /// STORAGE IS A SCAN, NOT A MODULO RING — fix-round 1, IMPORTANT #1. The
    /// original design addressed a slot by `tick % Depth`, reasoning that two
    /// ticks within the `Depth`-wide BACKWARD admission window can never
    /// collide. That reasoning had a hole: the FORWARD admission window is
    /// `FutureHorizonTicks` (270), not `Depth` (5) — a gap of exactly `Depth`
    /// between the newest resident and an incoming tick (a lost batch of five
    /// packets, an everyday event at 5% loss) maps to the SAME slot as the
    /// newest resident itself, evicting it while strictly older residents sit
    /// untouched. `Depth` is small (3–12 at the asset's `[Range(1, 10)]`), so
    /// every admission/lookup here is a plain O(`Depth`) scan of the slot
    /// arrays instead — `_occupied`/`_slotTick` indexed by physical slot, not
    /// by any function of the tick. The invariant Р83 actually needs —
    /// "evicting to make room for a new tick always evicts the tick with the
    /// SMALLEST value among current residents" — is computed explicitly
    /// (`OldestCommittedIndex`) rather than assumed from arithmetic that
    /// doesn't hold once the window is asymmetric.
    ///
    /// ADMISSION IS TWO-PHASE: `Admit` RESERVES, `Commit` PUBLISHES — fix-round
    /// 1, IMPORTANT #2. `Admit` on `Accepted` hands back a `RenderSnapshot` to
    /// decode into, but the slot is NOT yet visible to `TryGet`, does not count
    /// toward `Count`, and does not move `NewestTick`. A frame Task 44 gives up
    /// decoding half-way through (a malformed block, Р82's territory) must
    /// never let `TryGet` hand a consumer a buffer whose fields belong to
    /// whatever tick used to live there, under the identity of a tick that was
    /// never actually filled. Only `Commit(tick)` — called once decoding
    /// finished — makes the data resident: `_occupied`/`_slotTick`/`Count`/
    /// `NewestTick` all update there, nowhere else. AT MOST ONE RESERVATION IS
    /// EVER PENDING: the START of every `Admit` call (regardless of what it
    /// goes on to decide) silently drops any reservation the caller never
    /// committed — a caller that abandons a decode costs at most the one slot
    /// that reservation would have occupied, reclaimed on the very next
    /// `Admit`, never a permanent leak. `Commit` itself is equally defensive:
    /// a `tick` that does not match the pending reservation (none pending, a
    /// stale tick from an old reservation already reclaimed, a double commit)
    /// is a silent no-op (Р82).
    ///
    /// WHAT "EVICT" MEANS DURING THE RESERVATION WINDOW. When a reservation
    /// needs the ring's last free capacity, the SLOT that will hold the new
    /// tick is chosen and — if it currently holds a committed resident — that
    /// resident's bookkeeping is cleared IMMEDIATELY, as part of `Admit`
    /// (`OverflowDroppedSnapshots` increments right there), not deferred to
    /// `Commit`. This is deliberate: the physical `RenderSnapshot` buffer
    /// handed back is the SAME object the evicted resident used, and the
    /// caller starts overwriting its fields the moment `Admit` returns — if
    /// the old resident's identity were left "resident" in bookkeeping during
    /// that window, a `TryGet` for it would confidently hand back a buffer
    /// that is, by then, a half-decoded frame of an entirely different tick.
    /// Losing that resident's SLOT even if the incoming decode is later
    /// abandoned is the accepted cost — it was the oldest, least valuable
    /// resident by construction (see `OldestCommittedIndex`), and the
    /// alternative (rolling the eviction back) would require restoring data
    /// that was never actually preserved anywhere once the buffer started
    /// being overwritten.
    ///
    /// WHY THE RING IS SIZED `InterpBufferTicks + 2`, AND WHY THAT SIZE IS ALSO
    /// THE BACKWARD ADMISSION WINDOW. `RenderClock` (Task 31) targets
    /// `newestBufferedTick - InterpBufferTicks`, and Р38 owns the render PAIR
    /// at that target and the tick after it — so a consumer reading the pair
    /// needs the buffer to hold at minimum `InterpBufferTicks + 1` ticks behind
    /// the newest just to have both halves of the pair on hand; the plan's
    /// `+ 2` (Р37) is the one extra tick of headroom that keeps an ordinary
    /// reordering from starving that pair the instant it happens. A tick more
    /// than `Depth` behind the newest ACCEPTED tick is judged `Stale`
    /// regardless of whether a free slot physically exists — Р37's window is a
    /// statement about what interpolation can still use, not merely about what
    /// the ring can physically hold.
    ///
    /// FOUR WAYS TO BE REFUSED, AND WHY THEY ARE FOUR AND NOT ONE.
    ///   * `ForeignEpoch` — no epoch tracked yet, or a frame naming a different
    ///     one. Same discipline as `EventDedup.Reset`/`RenderClock.ResetForEpoch`:
    ///     a frame that outran the handshake, or survived from a match this
    ///     client has left, must not be trusted with anything.
    ///   * `Duplicate` — the exact tick is STILL a COMMITTED resident (an
    ///     occupied slot whose stored tick equals this one). This is
    ///     deliberately narrower than "already seen" in general: a tick that was
    ///     seen once and has since been discharged (`DiscardBelow`) or evicted
    ///     (overflow) no longer has a resident copy to be a duplicate OF, and
    ///     re-arriving is answered by the next bullet instead. The distinction
    ///     matters to the caller only in one way — neither counts as
    ///     `Accepted` — so collapsing them would cost nothing observable EXCEPT
    ///     the one case the task brief pins explicitly: a tick that fell below
    ///     the discharge floor and comes back is `Stale`, not `Duplicate` (task
    ///     brief §2.5, test 11), because "duplicate" is a fact about the RING's
    ///     current contents and a discharged tick has none. A tick with a
    ///     PENDING, uncommitted reservation is likewise not a duplicate of
    ///     anything — see the two-phase note above.
    ///   * `Stale` — either the tick has fallen `Depth` ticks or more behind
    ///     the newest ACCEPTED tick (interpolation has no use for it even if
    ///     the ring had room), or it sits below the floor `DiscardBelow` has
    ///     already advanced past — REGARDLESS of whether that exact tick was
    ///     ever itself accepted. A reordered frame that is still INSIDE the
    ///     window and has no resident slot yet is the opposite of this — it is
    ///     `Accepted`, because filling that hole is the entire reason the ring
    ///     exists (Р37).
    ///   * `FutureRejected` — Р150е, spelled out below.
    ///
    /// Р150е: THE FutureRejected GATE, AND WHY IT LIVES HERE. `EventDedup`'s own
    /// class doc records a KNOWN LIMIT (fix round 1, reviewer F2): its
    /// dedup window advances on whatever original tick the frames it is fed
    /// name, and a single frame naming a tick far in the future would drag that
    /// window — and the anti-stale floor beside it — forward until `Reset`,
    /// eating every real event behind it for the rest of the match.
    /// `EventDedup` has no notion of "the present" to bound that with; THIS
    /// class is the one caller that feeds it (Task 44 reads the verdict below
    /// and only calls `EventDedup.TryAcceptEvent` when told to), so this is
    /// where the obligation lands. `FutureHorizonTicks` is `EventDedup.
    /// WindowTicks` itself, not a second, independently-tuned number: a frame
    /// further ahead than the dedup memory can ever answer for is not a fast
    /// player and not a burst of buffered datagrams, it is either corruption or
    /// a stall long enough (270 ticks, 9 seconds at 30 Hz) that the connection
    /// life cycle (Р60) should already have restarted it — a legitimate stall
    /// that long is Task 36/44's problem, not something this class should ever
    /// paper over by quietly accepting it. A `FutureRejected` frame is refused
    /// WHOLESALE: it does not move `NewestTick` (so the very next ordinary
    /// frame is judged against the floor it already had, not against the
    /// poisoned one), and the caller is told, by the verdict alone, never to
    /// hand its events to `EventDedup` at all — not "hand them and let the
    /// dedup gate reject them", which is exactly the path that would have
    /// dragged the window in the first place.
    ///
    /// KNOWN LIMIT, DELIBERATE AND SYMMETRIC WITH EventDedup'S OWN. The very
    /// FIRST frame accepted after a `Reset` skips the horizon check entirely —
    /// there is no `NewestTick` yet to measure "how far ahead" against. This
    /// mirrors `EventDedup`'s "established-connection premise": both classes
    /// trust that `Reset`/the epoch itself only ever arrives over the Reliable
    /// lifecycle channel, so the very first tick a freshly-reset queue is
    /// handed is presumed to be an honest opening tick of the new match, not
    /// adversarial input a client chose to send before ever being admitted. A
    /// hostile first tick could, in principle, plant an absurd `NewestTick` and
    /// make every following honest frame read as `Stale`-by-window until the
    /// next `Reset` — the same shape of risk `EventDedup`'s own limit accepts,
    /// carried by the same trust boundary, and recorded here rather than by
    /// archaeology.
    ///
    /// Р83, OVERFLOW. A burst of delayed datagrams landing faster than the
    /// consumer's own `DiscardBelow` can keep up empties the ring's spare
    /// capacity; the NEXT tick admitted past that point evicts the TRUE oldest
    /// committed resident (`OldestCommittedIndex`, never a modulo coincidence —
    /// see the storage note above) — counted in `OverflowDroppedSnapshots`, the
    /// queue's OWN counter (task brief §2.3: `NetStats`' composition is closed
    /// as of Task 23 and this task's Files do not touch it; `StaleSnapshots`/
    /// `DuplicateSnapshots` are fed by the CALLER off the verdicts above, the
    /// same discipline `EventDedup` set — this class answers per admission, the
    /// caller owns the match-wide counters). The counter increments ONLY when
    /// the ring is genuinely full of committed residents (no free slot
    /// exists) — never merely because a physical slot happened to be reused,
    /// which is precisely the bug fix-round 1 found in the modulo scheme.
    /// THAT IS NECESSARY AND NO LONGER SUFFICIENT (bd `app-0wm`): the evicted
    /// resident must also be a tick the render clock has NOT walked past yet.
    /// A full ring is an ordinary working state here rather than a symptom —
    /// capacity is `InterpBufferTicks + 2`, the floor trails the RENDER tick by
    /// one, and the render clock trails the newest tick by `InterpBufferTicks`
    /// whenever it is on target, which leaves the residents spanning the whole
    /// ring and an admission with no free slot to take. Counting those
    /// evictions measured the ring's geometry rather than the connection.
    /// ⚠ IT IS NOT EVERY ADMISSION, and the measurement says so: 1371 in 83
    /// seconds is 16.5/s against a ~28.5/s stream at 30 Hz and 5% loss, because
    /// the clock's distance from the newest tick breathes between 2 and 4 —
    /// one tick nearer and the residents are one short of the ring, so the
    /// admission finds a free slot and evicts nothing. `EvictionWasNeverShown`
    /// carries the added test and its own doc carries the rest.
    /// `Reset` does NOT clear this counter: the task brief's own list of what
    /// `Reset` clears — the ring, the floor, the newest tick, the epoch — does
    /// not name it, and a per-connection health counter that reset itself on
    /// every restart would hide exactly the pattern (a client whose consumer
    /// keeps falling behind, match after match) it exists to surface.
    ///
    /// `Reset` IS A FULL RESET EVEN AT THE SAME EPOCH (contract note carried
    /// over from Task 31's review: a duplicated lifecycle message must not be
    /// answered with a second, redundant reset by THIS class — deduplicating
    /// the lifecycle message itself is the CALLER's job, Task 44). This class
    /// has no way to tell "the same epoch, sent twice" from "the same epoch,
    /// legitimately reused after a dedicated restart protocol" apart by
    /// argument alone, so it does not try: every call clears the ring, the
    /// floor, the newest tick and any pending reservation, unconditionally.
    ///
    /// `DiscardBelow` NEVER MOVES BACKWARDS, AND WORKS NO MATTER WHAT ELSE IS
    /// GOING ON. The consumer calls it every render frame with `RenderTick - 1`
    /// — spec §3.9 justified the unconditional call against the on-hit render
    /// pin Task Т10 (app-88jb) later removed whole: a chain of those pins used
    /// to fill the ring in a few hundred milliseconds and start dropping
    /// snapshots ALONG WITH their events if discharge ever skipped a frame.
    /// That caller-side obligation belongs to Phase Ф9's `SimulationRunner`,
    /// not to this class — what belongs here is that the method itself has no
    /// dependency on anything else being called first, in order, or at all,
    /// and a floor argument at or below the current floor is silently ignored
    /// (Р82: refuse, never throw, never regress) rather than trusted at face
    /// value from a caller that might repeat the same `RenderTick - 1` many
    /// frames in a row.
    ///
    /// `TryGet` IS THE SEPARATE, READ-ONLY DOOR for the render-pair consumer
    /// (Task 44/Ф9) asking "what do you have for tick T" after the fact — the
    /// same underlying scan `Admit`'s duplicate check uses, exposed to a
    /// different caller answering a different question, and answering `false`
    /// for anything not currently a COMMITTED resident (never admitted, still
    /// only reserved, discharged, or evicted).
    ///
    /// HOSTILE INPUT IS REFUSED, NEVER THROWN (Р82). Every branch above answers
    /// with a verdict or a no-op; nothing on this class's data path throws for
    /// any tick or epoch value, wire-sourced or not. A tick beyond
    /// `MaxRepresentableTick` (`int.MaxValue`) is refused as `FutureRejected`
    /// before it can occupy a slot or move `NewestTick` (fix-round 2, W1) —
    /// the same bound `RenderClock.OnSnapshot`/`StalePolicy`'s own guards
    /// already refuse at; this queue is the one OTHER consumer of the
    /// identical wire tick that, until now, had no guard of its own.
    public sealed class SnapshotQueue
    {
        /// The future horizon a frame's tick may not cross past the newest one
        /// already accepted (Р150е). Named FROM `EventDedup.WindowTicks` rather
        /// than restated as its own literal — see the class doc's Р150е
        /// paragraph for why 270 is not a second, independently-chosen number.
        public const int FutureHorizonTicks = EventDedup.WindowTicks;

        /// Highest wire tick this class will ever store as a resident or
        /// pending value (fix-round 2, W1) — a stored tick can end up handed
        /// to a caller that eventually subtracts it against an `int`-typed
        /// clock elsewhere in the client (`RenderClock.RenderTick`,
        /// `StalePolicy`'s own render-tick domain), the same bound
        /// `RenderClock.MaxRepresentableTick`/`StalePolicy.
        /// MaxRepresentableTick` use for exactly that reason. `Admit` refuses
        /// anything past it outright — see the class doc's HOSTILE INPUT
        /// paragraph and `Admit`'s own doc.
        const uint MaxRepresentableTick = int.MaxValue;

        /// The outcome of one `Admit` call. See the class doc for what
        /// distinguishes each refusal from the others.
        public enum AdmitVerdict : byte
        {
            Accepted,
            Stale,
            Duplicate,
            FutureRejected,
            ForeignEpoch,
        }

        readonly RenderSnapshot[] _ring;
        readonly bool[] _occupied;
        readonly uint[] _slotTick;
        readonly int _depth;

        bool _hasEpoch;
        ushort _epoch;

        bool _hasFloor;
        uint _floor;

        bool _hasNewestAccepted;
        uint _newestAccepted;

        int _count;

        bool _hasPending;
        int _pendingIdx;
        uint _pendingTick;

        /// Р83: snapshots evicted because the ring was full of committed
        /// residents when a newer, otherwise-valid tick needed a slot one of
        /// them occupied — AND the evicted tick had not been shown yet
        /// (`EvictionWasNeverShown`, bd `app-0wm`). The eviction that frees a
        /// tick the render clock has already walked past is the ring doing its
        /// job, not a loss, and counting it made this number a function of the
        /// ring's geometry rather than of the connection: 1371 "losses" in 83
        /// seconds of a healthy match. The queue's OWN counter, not `NetStats`
        /// — see the class doc.
        public int OverflowDroppedSnapshots;

        /// The ring's physical capacity, `InterpBufferTicks + 2` from the
        /// `NetTimings` the constructor was built with (5 at the shipped
        /// defaults) — also the width of the BACKWARD admission window every
        /// `Stale`/`Accepted` decision above is measured against.
        public int Depth => _depth;

        /// How many slots currently hold a COMMITTED, undischarged snapshot.
        /// Never exceeds `Depth`. A pending, uncommitted reservation is not
        /// counted.
        public int Count => _count;

        /// Whether ANY tick has been COMMITTED since the last `Reset`. Before
        /// this is true, `NewestTick` is meaningless (and reads zero, which is
        /// otherwise an ordinary tick value).
        public bool HasNewestTick => _hasNewestAccepted;

        /// The newest tick ever COMMITTED since the last `Reset` — a maximum,
        /// the same discipline as `RenderClock.OnSnapshot`'s own bookkeeping:
        /// never pulled back by a reordered, stale, or `FutureRejected` frame,
        /// and never advanced by a reservation that was never committed.
        public uint NewestTick => _newestAccepted;

        /// TAKES THE WHOLE `SimConfig` SINCE Т32б, for the reason
        /// `RenderSnapshot`'s own constructor gives: the ring preallocates the
        /// frames, and a frame is no longer sized from the arena alone.
        public SnapshotQueue(in SimConfig cfg, in NetTimings timings)
        {
            // A non-positive InterpBufferTicks has no representation a ring
            // can use (a zero-or-negative-width ring holds nothing); NetTimings
            // is a plain struct any caller can build by hand or leave at
            // default (Task 31's own fix-round lesson), so [Range(1, 10)] on
            // the asset stands between the OWNER and a bad value, not between
            // a caller bug and this constructor.
            _depth = math.max(1, timings.InterpBufferTicks + 2);

            _ring = new RenderSnapshot[_depth];
            for (int i = 0; i < _depth; i++) _ring[i] = new RenderSnapshot(in cfg);
            _occupied = new bool[_depth];
            _slotTick = new uint[_depth];
        }

        /// Starts tracking `epoch` and forgets everything else — every occupied
        /// slot, any pending reservation, the discharge floor and the newest
        /// committed tick. Called by the owner (Task 44) on the Reliable
        /// lifecycle message that names the match's epoch, a restart included
        /// (Р60). ALWAYS a full reset, even when `epoch` names the CURRENTLY
        /// tracked epoch — deduplicating a repeated lifecycle message is the
        /// caller's job (see the class doc); this method has no way to tell
        /// that case apart from a deliberate same-epoch reset and does not
        /// try. `OverflowDroppedSnapshots` is NOT cleared — see the class
        /// doc's Р83 paragraph.
        public void Reset(ushort epoch)
        {
            _epoch = epoch;
            _hasEpoch = true;

            System.Array.Clear(_occupied, 0, _depth);
            System.Array.Clear(_slotTick, 0, _depth);
            _count = 0;

            _hasPending = false;

            _hasFloor = false;
            _floor = 0;

            _hasNewestAccepted = false;
            _newestAccepted = 0;
        }

        /// Decides whether the frame `(epoch, tick)` may reserve a slot at
        /// all. `slot` is the preallocated `RenderSnapshot` to decode into on
        /// `Accepted`, and `null` for every other verdict. The slot is NOT yet
        /// visible to `TryGet`/`Count`/`NewestTick` — call `Commit(tick)` once
        /// decoding into it actually finished (two-phase admission, fix-round
        /// 1 — see the class doc). See the class doc for the full reasoning
        /// behind each branch; the order below matters only where two
        /// refusals could otherwise both apply (an epoch check always wins,
        /// and a tick that is a COMMITTED resident is `Duplicate` even where
        /// it would also read as `Stale`-by-window against a newer tick
        /// already committed). A tick beyond `MaxRepresentableTick` is
        /// refused as `FutureRejected` immediately after the epoch check
        /// (fix-round 2, W1) — mirroring `RenderClock.OnSnapshot`'s own
        /// order — since an unrepresentable tick can never legitimately be a
        /// floor hit or a duplicate of anything already resident.
        public AdmitVerdict Admit(ushort epoch, uint tick, out RenderSnapshot slot)
        {
            slot = null;

            // AT MOST ONE PENDING RESERVATION (class doc): whatever the
            // PREVIOUS Admit reserved and the caller never committed is
            // reclaimed right here, unconditionally, before this call decides
            // anything of its own.
            _hasPending = false;

            if (!_hasEpoch || epoch != _epoch) return AdmitVerdict.ForeignEpoch;
            if (tick > MaxRepresentableTick) return AdmitVerdict.FutureRejected;
            if (_hasFloor && tick < _floor) return AdmitVerdict.Stale;
            if (IndexOfCommitted(tick) >= 0) return AdmitVerdict.Duplicate;

            if (_hasNewestAccepted)
            {
                if (tick > _newestAccepted)
                {
                    // Р150е: a prospective advance past the horizon is refused
                    // WHOLESALE, before it can move NewestTick at all.
                    if (tick - _newestAccepted > (uint)FutureHorizonTicks)
                        return AdmitVerdict.FutureRejected;
                }
                else if (_newestAccepted - tick >= (uint)_depth)
                {
                    // Older than interpolation can still use, and never
                    // itself resident (the Duplicate check above already
                    // ruled that out) — Р37's hole-filling exception does not
                    // reach this far back.
                    return AdmitVerdict.Stale;
                }
            }
            // else: the very first tick since Reset has no window or horizon
            // to be measured against — see the class doc's KNOWN LIMIT note.

            int idx = FreeSlotIndex();
            if (idx < 0)
            {
                // The ring is genuinely full of COMMITTED residents (fix-round
                // 1: never a physical-slot coincidence). Evict the TRUE oldest.
                // A full ring is the NECESSARY condition for the counter below
                // and no longer the sufficient one — bd `app-0wm` added the
                // second test, `EvictionWasNeverShown`, four lines down.
                idx = OldestCommittedIndex();
                uint evictedTick = _slotTick[idx];
                _occupied[idx] = false;
                _count--;
                if (EvictionWasNeverShown(evictedTick)) OverflowDroppedSnapshots++;
            }

            _hasPending = true;
            _pendingIdx = idx;
            _pendingTick = tick;

            slot = _ring[idx];
            return AdmitVerdict.Accepted;
        }

        /// Publishes the slot reserved by the most recent `Accepted` `Admit`
        /// call as a committed resident — the second half of the two-phase
        /// admission the class doc describes. `tick` must match the pending
        /// reservation exactly; anything else (nothing pending, a stale tick
        /// from a reservation `Admit` already reclaimed, a repeated commit) is
        /// a silent no-op (Р82) rather than an assumption that the caller
        /// meant something else.
        public void Commit(uint tick)
        {
            if (!_hasPending || _pendingTick != tick) return;

            int idx = _pendingIdx;
            _hasPending = false;

            _occupied[idx] = true;
            _slotTick[idx] = tick;
            _count++;

            if (!_hasNewestAccepted || tick > _newestAccepted)
            {
                _hasNewestAccepted = true;
                _newestAccepted = tick;
            }
        }

        /// Read-only lookup for the render-pair consumer (Task 44/Ф9): does
        /// the ring currently hold `tick`'s data as a COMMITTED resident?
        /// `false` for anything else — a tick never admitted, one that is
        /// only pending (reserved, not yet committed), one that has been
        /// discharged, or one evicted by overflow.
        public bool TryGet(uint tick, out RenderSnapshot snapshot)
        {
            int idx = IndexOfCommitted(tick);
            if (idx >= 0)
            {
                snapshot = _ring[idx];
                return true;
            }
            snapshot = null;
            return false;
        }

        /// Advances the discharge floor to `tick` and frees every COMMITTED
        /// slot older than it. A `tick` at or below the current floor is a
        /// silent no-op (Р82) — see the class doc for why this method trusts
        /// nothing about call order or frequency.
        public void DiscardBelow(uint tick)
        {
            if (_hasFloor && tick <= _floor) return;

            _floor = tick;
            _hasFloor = true;

            for (int i = 0; i < _depth; i++)
            {
                if (_occupied[i] && _slotTick[i] < tick)
                {
                    _occupied[i] = false;
                    _count--;
                }
            }
        }

        /// The slot index of the tick `t` if it is a COMMITTED resident,
        /// `-1` otherwise. O(`Depth`) — see the class doc's storage note for
        /// why this is a scan rather than an O(1) address computation.
        int IndexOfCommitted(uint t)
        {
            for (int i = 0; i < _depth; i++)
                if (_occupied[i] && _slotTick[i] == t) return i;
            return -1;
        }

        /// The first FREE slot, `-1` if the ring is completely full of
        /// committed residents.
        int FreeSlotIndex()
        {
            for (int i = 0; i < _depth; i++)
                if (!_occupied[i]) return i;
            return -1;
        }

        /// The slot holding the SMALLEST tick among committed residents — the
        /// TRUE oldest, computed explicitly rather than assumed from address
        /// arithmetic (fix-round 1, IMPORTANT #1). Only ever called when the
        /// ring is full (`FreeSlotIndex` found nothing), so at least one
        /// committed resident is guaranteed to exist.
        int OldestCommittedIndex()
        {
            int oldest = -1;
            uint oldestTick = 0;
            for (int i = 0; i < _depth; i++)
            {
                if (!_occupied[i]) continue;
                if (oldest < 0 || _slotTick[i] < oldestTick)
                {
                    oldest = i;
                    oldestTick = _slotTick[i];
                }
            }
            return oldest;
        }

        /// Whether the frame just evicted from `evictedTick`'s slot was thrown
        /// away UNSHOWN — the only kind of eviction
        /// `OverflowDroppedSnapshots` is meant to count (bd `app-0wm`).
        ///
        /// THE FLOOR IS WHAT "ALREADY SHOWN" MEANS, and the ring already holds
        /// it. `DiscardBelow(renderTick - 1)` is the consumer telling this
        /// queue how far the render clock has walked
        /// (`NetworkSimBackend.Advance`), so a resident at or below `_floor` is
        /// a tick the picture has been through: evicting it frees a slot and
        /// costs nothing. Above the floor is a frame that arrived intact and
        /// was thrown away before it could be shown — the loss
        /// `NetDiagnostics.DroppedSnapshots` has always promised in as many
        /// words, and the only kind worth a red counter.
        ///
        /// WITHOUT THIS TEST THE COUNTER MEASURED THE RING'S GEOMETRY, NOT THE
        /// CONNECTION. Capacity is `InterpBufferTicks + 2` and the floor sits
        /// one tick below the render tick, so whenever the render clock is the
        /// full `InterpBufferTicks` behind the newest tick, the residents span
        /// the entire ring and the next admission has no free slot: it evicts a
        /// tick the picture went through a frame ago and charged the diagnostic
        /// for it.
        ///
        /// HOW OFTEN THAT HAPPENS IS A MEASUREMENT, NOT A DERIVATION, and the
        /// arithmetic is worth keeping because it is what tells the two states
        /// apart. Measured: 1371 "losses" in 83 seconds of a healthy match
        /// under 80 ms / 5%, `stale` and `dup` both zero, red the whole time.
        /// That is 16.5/s against a stream of ~28.5 admissions/s (30 Hz less 5%
        /// loss) — not every frame, because the clock's distance from the
        /// newest tick breathes between 2 and 4 ticks and one tick nearer
        /// leaves a slot free. Both the owner and the coordinator believed the
        /// number and went looking for a network fault that was not there.
        ///
        /// NO FLOOR YET MEANS EVERY EVICTION COUNTS. Before the first
        /// `DiscardBelow` nothing has been shown, so nothing evicted can have
        /// been shown either; the honest answer is the conservative one. It is
        /// also what keeps this change confined to the accounting: the two
        /// pre-existing asserts that reach this branch run with no floor set,
        /// and they still read what they always read.
        ///
        /// THE EVICTION ITSELF IS UNTOUCHED — which slot, which resident, which
        /// verdict, in what order — exactly as `app-c3m` left the `dt` clamp
        /// alone and moved only the accounting off it. The test named
        /// `NewAccounting_EvictsTheSameTicks_InTheSameOrder_WithTheSameVerdicts`
        /// is that promise, checked rather than asserted.
        bool EvictionWasNeverShown(uint evictedTick) => !_hasFloor || evictedTick > _floor;
    }
}
