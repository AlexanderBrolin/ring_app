using Ring.Networking.Protocol;
using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// Stage 2 Task 44b (spec §3.10's full-reset list, which names "the
    /// receive queue of events" as its own item beside the set of seen
    /// `(epoch, tick, seq)`): the place an accepted event WAITS between the
    /// frame that carried it and the render tick it belongs to — the SIXTH
    /// per-match seam, and the one that did not exist until this task.
    ///
    /// WHY IT HAD TO EXIST AT ALL. The two neighbours it sits between each
    /// answer a different question and neither holds an event:
    ///   * `EventDedup.TryAcceptEvent` returns a VERDICT — "is this the first
    ///     sight of this event" — and stores one bit, never the record;
    ///   * `SnapshotQueue` stores `RenderSnapshot`, which carries the world's
    ///     STATE (players, mobs, projectiles, wave, stats) and no events at
    ///     all.
    /// An event accepted by the dedup while the render clock is still
    /// `InterpBufferTicks` behind the frame it rode in on therefore had
    /// nowhere to be until now: either it was acted on immediately — showing
    /// a death several ticks before the body it belongs to reaches that
    /// moment on screen — or it was dropped.
    ///
    /// NAMESPACE, NOT ASSEMBLY, AND NOTHING OF FISHNET OR UnityEngine HERE.
    /// `Ring.Networking.Client` is a folder inside the `Ring.Networking`
    /// assembly, next to `EventDedup`, `SnapshotQueue` and `RenderClock`;
    /// this is a pure decision object, testable in EditMode with no scene.
    /// The caller that decodes a frame, asks `EventDedup` and enqueues what
    /// it approved is Task 44c; the caller that drains it against
    /// `RenderClock.RenderTick` is the same one.
    ///
    /// THE TICK IS ABSOLUTE, AND THE CALLER COMPUTES IT THE ONE WAY THERE IS.
    /// `SnapshotBlocks.EventRecord.TickDelta` counts ticks BACK FROM THE
    /// FRAME HEADER's tick (the field's own doc; `SnapshotAssembler` writes
    /// it as `(byte)(_tick - <the record's own tick>)`), so the absolute tick
    /// is `frameTick - record.TickDelta` — the same subtraction
    /// `EventDedup.TryAcceptEvent` performs internally to build its key. This
    /// class takes the RESULT rather than the pair, because it has no
    /// business re-deriving a number the dedup has already derived and
    /// answered for: two derivations of one key are two chances to disagree.
    ///
    /// DELIVERY ORDER IS BY TICK FIRST, ARRIVAL SECOND. Frames arrive
    /// reordered as a matter of course at the 5% loss Critical Rule 7
    /// requires, and redundancy re-sends an event for several frames after
    /// its own, so arrival order alone would show a death before the shot
    /// that caused it. Within ONE tick the arrival order is kept exactly:
    /// the server assigns `seq` once per tick in `BeginTick`, in order, and
    /// nothing on this side may shuffle what that ordering means.
    ///
    /// AN EVENT IS NEVER HANDED OUT EARLY. `TryDequeue` answers only for
    /// records whose absolute tick the render clock has actually reached.
    /// That is the whole point of the wait: the interpolated world on screen
    /// is `InterpBufferTicks` behind the newest frame buffered, and an event
    /// shown at arrival time would land on a world that has not got there.
    ///
    /// OVERFLOW IS COUNTED, NEVER SILENT (spec §3.7), AND IT REFUSES THE
    /// NEWCOMER RATHER THAN EVICTING A RESIDENT — the opposite of
    /// `SnapshotQueue`'s own overflow rule, deliberately. `SnapshotQueue`
    /// evicts its OLDEST resident because the oldest snapshot is the least
    /// valuable one it holds; here the oldest pending event is the one CLOSEST
    /// to being shown, and the newcomer is the furthest from it, so the cheap
    /// loss is the newcomer. The loss is PERMANENT either way, and that is
    /// exactly why it is counted: by the time an event reaches this class the
    /// dedup has already marked its key seen, so none of the redundant
    /// re-sends the server will keep making for `EventRedundancyTicks` frames
    /// can bring it back.
    ///
    /// `Reset` DOES NOT CLEAR `OverflowDroppedEvents`, for the same reason
    /// `SnapshotQueue.Reset` does not clear `OverflowDroppedSnapshots`: a
    /// per-connection health counter that cleared itself on every restart
    /// would hide precisely the pattern (a client whose consumer keeps
    /// falling behind, match after match) it exists to surface.
    ///
    /// NO EPOCH IS TRACKED, AND THAT IS CORRECT — the same shape as
    /// `GhostProjectiles`/`StalePolicy`. Three of the six seams
    /// `ClientMatchReset` clears know an epoch and three do not; this one
    /// never sees a frame header, only records its caller has already had
    /// admitted by the three that do.
    ///
    /// HOSTILE AND ABSURD INPUT IS REFUSED, NEVER THROWN (Р82). A render
    /// tick below zero names no moment and delivers nothing; a full queue
    /// answers `false` instead of growing; nothing below allocates after the
    /// constructor returns.
    public sealed class ClientEventQueue
    {
        /// One accepted event and the ABSOLUTE tick it belongs to. Held by
        /// value: `EventRecord` is a plain struct with no reference fields
        /// (`SnapshotBlocks`' own doc), so the ring costs one allocation in
        /// the constructor and none afterwards.
        public struct PendingEvent
        {
            /// The absolute world tick this event happened on — NOT the tick
            /// of the frame that carried it. See the class doc.
            public uint Tick;

            public SnapshotBlocks.EventRecord Record;
        }

        readonly PendingEvent[] _pending;
        readonly int _capacity;
        int _count;

        /// Events refused because the queue was full when they arrived. The
        /// queue's OWN counter, the same shape as
        /// `SnapshotQueue.OverflowDroppedSnapshots` — not a `NetStats` field:
        /// `NetStats`' composition is closed and this is a fact about this
        /// object, which the dev overlay reads from here.
        public int OverflowDroppedEvents;

        /// How many events can wait at once. See the constructor for the
        /// arithmetic behind the number.
        public int Capacity => _capacity;

        /// How many events are waiting right now.
        public int Count => _count;

        /// The capacity is DERIVED from the two numbers that actually bound
        /// the wait, not chosen:
        ///   * at most `snapshotEventBudget` event records ride ONE frame —
        ///     that is what `NetConfig.SnapshotEventBudget` means, and
        ///     `SnapshotAssembler` enforces it per frame;
        ///   * an event waits from the frame that carried it until the render
        ///     clock reaches its tick, and the clock targets
        ///     `newestBufferedTick - InterpBufferTicks` — so the ticks whose
        ///     events can be pending at once span the same window
        ///     `SnapshotQueue` sizes its ring for: `InterpBufferTicks + 1`
        ///     ticks to hold the render PAIR behind the newest (Р38/Р37),
        ///     plus the one tick of headroom that keeps an ordinary
        ///     reordering from starving it, i.e. `InterpBufferTicks + 2`.
        /// The product is the bound. The `+ 2` is deliberately the SAME
        /// expression `SnapshotQueue`'s `Depth` is built from rather than an
        /// independently-tuned second number — `ClientLinkTests` pins the two
        /// against each other so they cannot drift apart in silence.
        ///
        /// Both arguments are floored at 1 rather than refused: `NetTimings`
        /// is a plain struct any caller can leave at `default` (Task 31's own
        /// fix-round lesson), and a zero-width queue holds nothing at all,
        /// which is a worse answer than a small one.
        public ClientEventQueue(in NetTimings timings, int snapshotEventBudget)
        {
            int perFrame = math.max(1, snapshotEventBudget);
            int ticksPending = math.max(1, timings.InterpBufferTicks + 2);
            _capacity = perFrame * ticksPending;
            _pending = new PendingEvent[_capacity];
        }

        /// Takes an event the dedup has ALREADY approved, together with its
        /// absolute tick, and holds it until that tick is on screen. `false`
        /// means the queue was full and this record was refused — counted in
        /// `OverflowDroppedEvents` and gone for good (see the class doc);
        /// the residents are left exactly as they were.
        ///
        /// Insertion is a stable, ordered insert rather than an append plus a
        /// sort at delivery time: the queue is short by construction, the
        /// walk is bounded by `Capacity`, and keeping the array ordered is
        /// what makes "is anything due" a single comparison in `TryDequeue`
        /// instead of a scan on every render frame.
        public bool Enqueue(uint tick, in SnapshotBlocks.EventRecord record)
        {
            if (_count == _capacity)
            {
                OverflowDroppedEvents++;
                return false;
            }

            // Strictly `>`: an existing record of the SAME tick is left ahead
            // of the newcomer, which is what keeps arrival order inside a
            // tick.
            int i = _count;
            while (i > 0 && _pending[i - 1].Tick > tick)
            {
                _pending[i] = _pending[i - 1];
                i--;
            }

            _pending[i].Tick = tick;
            _pending[i].Record = record;
            _count++;
            return true;
        }

        /// Hands back the oldest event whose tick `renderTick` has reached,
        /// removing it. `false` means nothing is due — either the queue is
        /// empty or its earliest record still belongs to the future. Call it
        /// in a loop until it answers `false`; every call that answers `true`
        /// has consumed exactly one record.
        ///
        /// `renderTick` is an `int` because `RenderClock.RenderTick` is one
        /// by contract, while an event's tick is the `uint` the wire carries;
        /// a negative value names no moment of any match and delivers
        /// nothing rather than wrapping into an enormous unsigned one (Р82).
        public bool TryDequeue(int renderTick, out PendingEvent pending)
        {
            pending = default;

            if (_count == 0) return false;
            if (renderTick < 0) return false;
            if (_pending[0].Tick > (uint)renderTick) return false;

            pending = _pending[0];

            _count--;
            for (int i = 0; i < _count; i++) _pending[i] = _pending[i + 1];
            _pending[_count] = default;
            return true;
        }

        /// Forgets every waiting event — a full reset, as
        /// `GhostProjectiles.Reset`/`StalePolicy.Reset` are, and for the same
        /// reason: this class tracks no epoch, so there is nothing to check
        /// an argument against. Called by `ClientMatchReset` on the Reliable
        /// life-cycle message that names a new epoch; a restarted match
        /// replays its ticks from zero, and an event of the previous match
        /// still waiting here would be handed out over the opening seconds of
        /// the new one, on a tick number that means something else entirely.
        /// `OverflowDroppedEvents` deliberately survives — see the class doc.
        public void Reset()
        {
            System.Array.Clear(_pending, 0, _capacity);
            _count = 0;
        }
    }
}
