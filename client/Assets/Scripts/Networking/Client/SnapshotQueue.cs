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
    /// `RenderSnapshot.CopyFrom`, Task 32's OTHER half). Nothing below
    /// allocates after the constructor returns.
    ///
    /// WHY THE RING IS SIZED `InterpBufferTicks + 2`, AND WHY THAT SIZE IS ALSO
    /// THE ADMISSION WINDOW. `RenderClock` (Task 31) targets `newestBufferedTick
    /// - InterpBufferTicks`, and Р38 owns the render PAIR at that target and the
    /// tick after it — so a consumer reading the pair needs the buffer to hold
    /// at minimum `InterpBufferTicks + 1` ticks behind the newest just to have
    /// both halves of the pair on hand; the plan's `+ 2` (Р37) is the one extra
    /// tick of headroom that keeps an ordinary reordering from starving that
    /// pair the instant it happens. Slot assignment is `tick % Depth` — the
    /// classic ring-buffer trick, and it is SOUND here specifically because the
    /// admission window (below) is exactly `Depth` ticks wide: two DISTINCT
    /// ticks that are both within `Depth` of the newest accepted tick can never
    /// collide on the same slot (they would have to differ by a multiple of
    /// `Depth` to do that, and the window has no room for two). That is also
    /// why "evict the oldest on overflow" needs no separate bookkeeping —
    /// admitting a tick whose slot is still occupied by an older one IS the
    /// oldest tick in that slot, by the same arithmetic.
    ///
    /// FOUR WAYS TO BE REFUSED, AND WHY THEY ARE FOUR AND NOT ONE.
    ///   * `ForeignEpoch` — no epoch tracked yet, or a frame naming a different
    ///     one. Same discipline as `EventDedup.Reset`/`RenderClock.ResetForEpoch`:
    ///     a frame that outran the handshake, or survived from a match this
    ///     client has left, must not be trusted with anything.
    ///   * `Duplicate` — the exact tick is STILL RESIDENT in the ring (an
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
    ///     current contents and a discharged tick has none.
    ///   * `Stale` — either the tick has fallen more than `Depth` ticks behind
    ///     the newest ACCEPTED tick (the ring physically cannot hold it without
    ///     colliding with something newer), or it sits below the floor
    ///     `DiscardBelow` has already advanced past — REGARDLESS of whether that
    ///     exact tick was ever itself accepted. A reordered frame that is still
    ///     INSIDE the window and has no resident slot yet is the opposite of
    ///     this — it is `Accepted`, because filling that hole is the entire
    ///     reason the ring exists (Р37).
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
    /// capacity; the NEXT tick admitted past that point collides with a slot
    /// still holding an undischarged snapshot and evicts it — counted in
    /// `OverflowDroppedSnapshots`, the queue's OWN counter (task brief §2.3:
    /// `NetStats`' composition is closed as of Task 23 and this task's Files do
    /// not touch it; `StaleSnapshots`/`DuplicateSnapshots` are fed by the
    /// CALLER off the verdicts above, the same discipline `EventDedup` set —
    /// this class answers per admission, the caller owns the match-wide
    /// counters). `Reset` does NOT clear this counter: the task brief's own
    /// list of what `Reset` clears — the ring, the floor, the newest tick, the
    /// epoch — does not name it, and a per-connection health counter that reset
    /// itself on every restart would hide exactly the pattern (a client whose
    /// consumer keeps falling behind, match after match) it exists to surface.
    ///
    /// `Reset` IS A FULL RESET EVEN AT THE SAME EPOCH (contract note carried
    /// over from Task 31's review: a duplicated lifecycle message must not be
    /// answered with a second, redundant reset by THIS class — deduplicating
    /// the lifecycle message itself is the CALLER's job, Task 44). This class
    /// has no way to tell "the same epoch, sent twice" from "the same epoch,
    /// legitimately reused after a dedicated restart protocol" apart by
    /// argument alone, so it does not try: every call clears the ring, the
    /// floor and the newest tick, unconditionally.
    ///
    /// `DiscardBelow` NEVER MOVES BACKWARDS, AND WORKS NO MATTER WHAT ELSE IS
    /// GOING ON. The consumer calls it every render frame with `RenderTick - 1`
    /// (spec §3.9: "the buffer keeps discharging during `FreezeRender`" —
    /// otherwise a chain of hitstops fills the ring in a few hundred
    /// milliseconds and starts dropping snapshots ALONG WITH their events).
    /// That caller-side obligation belongs to Phase Ф9's `SimulationRunner`,
    /// not to this class — what belongs here is that the method itself has no
    /// dependency on anything else being called first, in order, or at all,
    /// and a floor argument at or below the current floor is silently ignored
    /// (Р82: refuse, never throw, never regress) rather than trusted at face
    /// value from a caller that might repeat the same `RenderTick - 1` many
    /// frames in a row.
    ///
    /// ADMISSION HANDS BACK THE SLOT DIRECTLY, RATHER THAN MAKING THE CALLER
    /// ASK AGAIN. `Admit`'s `out RenderSnapshot slot` is the literal "a slot is
    /// issued" the task brief describes for `Accepted` — one call, one
    /// decision, one answer, instead of a second lookup that would have to
    /// agree with the first by construction rather than by return value.
    /// `TryGet` is the SEPARATE, read-only door for the render-pair consumer
    /// (Task 44/Ф9) asking "what do you have for tick T" after the fact — the
    /// same underlying lookup, exposed twice because admission and consumption
    /// are different callers asking different questions.
    ///
    /// HOSTILE INPUT IS REFUSED, NEVER THROWN (Р82). Every branch above answers
    /// with a verdict or a no-op; nothing on this class's data path throws for
    /// any tick or epoch value, wire-sourced or not.
    public sealed class SnapshotQueue
    {
        /// The future horizon a frame's tick may not cross past the newest one
        /// already accepted (Р150е). Named FROM `EventDedup.WindowTicks` rather
        /// than restated as its own literal — see the class doc's Р150е
        /// paragraph for why 270 is not a second, independently-chosen number.
        public const int FutureHorizonTicks = EventDedup.WindowTicks;

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

        /// Р83: snapshots evicted because the ring was full of undischarged
        /// entries when a newer, otherwise-valid tick needed the slot one of
        /// them occupied. The queue's OWN counter, not `NetStats` — see the
        /// class doc.
        public int OverflowDroppedSnapshots;

        /// The ring's physical capacity, `InterpBufferTicks + 2` from the
        /// `NetTimings` the constructor was built with (5 at the shipped
        /// defaults) — also the width of the admission window every `Stale`/
        /// `Accepted` decision above is measured against.
        public int Depth => _depth;

        /// How many slots currently hold an undischarged, accepted snapshot.
        /// Never exceeds `Depth`.
        public int Count => _count;

        /// Whether ANY tick has been accepted since the last `Reset`. Before
        /// this is true, `NewestTick` is meaningless (and reads zero, which is
        /// otherwise an ordinary tick value).
        public bool HasNewestTick => _hasNewestAccepted;

        /// The newest tick ever ACCEPTED since the last `Reset` — a maximum,
        /// the same discipline as `RenderClock.OnSnapshot`'s own bookkeeping:
        /// never pulled back by a reordered, stale, or `FutureRejected` frame.
        public uint NewestTick => _newestAccepted;

        public SnapshotQueue(in ArenaSimConfig arena, in NetTimings timings)
        {
            // A non-positive InterpBufferTicks has no representation the ring
            // math above can use (a zero- or negative-width window collapses
            // slot uniqueness); NetTimings is a plain struct any caller can
            // build by hand or leave at default (Task 31's own fix-round
            // lesson), so [Range(1, 10)] on the asset stands between the OWNER
            // and a bad value, not between a caller bug and this constructor.
            _depth = math.max(1, timings.InterpBufferTicks + 2);

            _ring = new RenderSnapshot[_depth];
            for (int i = 0; i < _depth; i++) _ring[i] = new RenderSnapshot(in arena);
            _occupied = new bool[_depth];
            _slotTick = new uint[_depth];
        }

        /// Starts tracking `epoch` and forgets everything else — every occupied
        /// slot, the discharge floor and the newest accepted tick. Called by
        /// the owner (Task 44) on the Reliable lifecycle message that names the
        /// match's epoch, a restart included (Р60). ALWAYS a full reset, even
        /// when `epoch` names the CURRENTLY tracked epoch — deduplicating a
        /// repeated lifecycle message is the caller's job (see the class doc);
        /// this method has no way to tell that case apart from a deliberate
        /// same-epoch reset and does not try. `OverflowDroppedSnapshots` is
        /// NOT cleared — see the class doc's Р83 paragraph.
        public void Reset(ushort epoch)
        {
            _epoch = epoch;
            _hasEpoch = true;

            System.Array.Clear(_occupied, 0, _depth);
            System.Array.Clear(_slotTick, 0, _depth);
            _count = 0;

            _hasFloor = false;
            _floor = 0;

            _hasNewestAccepted = false;
            _newestAccepted = 0;
        }

        /// Decides whether the frame `(epoch, tick)` may have a slot at all.
        /// `slot` is the preallocated `RenderSnapshot` to decode into on
        /// `Accepted`, and `null` for every other verdict. See the class doc
        /// for the full reasoning behind each branch; the order below matters
        /// only where two refusals could otherwise both apply (an epoch check
        /// always wins, and a tick still resident in the ring is `Duplicate`
        /// even where it would also read as `Stale`-by-window against a newer
        /// tick already accepted).
        public AdmitVerdict Admit(ushort epoch, uint tick, out RenderSnapshot slot)
        {
            slot = null;

            if (!_hasEpoch || epoch != _epoch) return AdmitVerdict.ForeignEpoch;
            if (_hasFloor && tick < _floor) return AdmitVerdict.Stale;

            int idx = SlotOf(tick);
            if (_occupied[idx] && _slotTick[idx] == tick) return AdmitVerdict.Duplicate;

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
                    // Older than the ring's window can answer for, and never
                    // itself resident (the Duplicate check above already ruled
                    // that out) — Р37's hole-filling exception does not reach
                    // this far back.
                    return AdmitVerdict.Stale;
                }
            }
            // else: the very first tick since Reset has no window or horizon
            // to be measured against — see the class doc's KNOWN LIMIT note.

            if (_occupied[idx])
            {
                // The slot's current occupant is, by the window arithmetic in
                // the class doc, necessarily older than this tick by a whole
                // ring width — the oldest tick that slot could possibly hold.
                OverflowDroppedSnapshots++;
            }
            else
            {
                _count++;
            }

            _occupied[idx] = true;
            _slotTick[idx] = tick;
            if (!_hasNewestAccepted || tick > _newestAccepted)
            {
                _hasNewestAccepted = true;
                _newestAccepted = tick;
            }

            slot = _ring[idx];
            return AdmitVerdict.Accepted;
        }

        /// Read-only lookup for the render-pair consumer (Task 44/Ф9): does the
        /// ring currently hold `tick`'s data, undischarged? `false` for
        /// anything not currently resident — a tick never admitted, one that
        /// has been discharged, or one evicted by overflow.
        public bool TryGet(uint tick, out RenderSnapshot snapshot)
        {
            int idx = SlotOf(tick);
            if (_occupied[idx] && _slotTick[idx] == tick)
            {
                snapshot = _ring[idx];
                return true;
            }
            snapshot = null;
            return false;
        }

        /// Advances the discharge floor to `tick` and frees every slot older
        /// than it. A `tick` at or below the current floor is a silent no-op
        /// (Р82) — see the class doc for why this method trusts nothing about
        /// call order or frequency.
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

        int SlotOf(uint tick) => (int)(tick % (uint)_depth);
    }
}
