using Ring.Networking.Protocol;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// Stage 2 Task 29 (spec §3.7 Р58/Р31, Р60/Р82): the CLIENT half of event
    /// redundancy — the memory that lets the same event arrive four times and
    /// be acted on once, and the anti-stale gate that keeps a reordered frame
    /// from applying an old world over a newer one.
    ///
    /// NAMESPACE, NOT ASSEMBLY. `Ring.Networking.Client` is a folder inside the
    /// `Ring.Networking` assembly, the mirror of `Ring.Networking.Server` where
    /// the assembler lives. Nothing here touches UnityEngine or FishNet: this
    /// is a pure decision structure, testable in EditMode, and Task 32 is the
    /// component that reads a frame, calls it, and owns the consequences.
    ///
    /// TWO QUESTIONS, DELIBERATELY SEPARATE (task-29-brief §2.5). The caller
    /// asks them independently, and the second is NOT gated on the first:
    ///   * `TryAcceptState(epoch, tick)` — "may this frame's blocks be applied?"
    ///     Р31, refined by spec §3.7: a frame of the same epoch whose tick is
    ///     `<= lastAppliedTick` does not apply its state. The boundary is `<=`,
    ///     not `<`: a duplicated datagram carries the tick that was just
    ///     applied, and re-applying it would rewind interpolation for nothing.
    ///   * `TryAcceptEvent(epoch, frameTick, record)` — "is this the first sight
    ///     of this event?" A frame whose STATE was refused as stale still has
    ///     its unseen events handled, because a packet that merely overtook
    ///     another would otherwise swallow a death that was never shown.
    /// Counting the refusals is the CALLER's job: `NetStats.StaleSnapshots` and
    /// `DuplicateSnapshots` are per-FRAME counters and this class answers per
    /// record, so it increments nothing (task-29-brief §2.7).
    ///
    /// THE KEY IS `(epoch, ORIGINAL tick, seq)`, NEVER THE FRAME'S OWN TICK.
    /// `EventRecord.TickDelta` counts back from the frame header's tick, so the
    /// original is `header.Tick - record.TickDelta` — and that pair is the one
    /// thing every copy of an event shares. The server assigns `seq` once per
    /// tick, in `BeginTick`, for every connection and every future resend
    /// (SnapshotAssembler's own doc), which is what makes the key stable at
    /// all. Keying on the frame's tick instead would give each resend its own
    /// identity and redundancy would double every death instead of insuring it.
    ///
    /// ONE EPOCH IS TRACKED, AND ONLY `Reset` CHANGES IT. The epoch arrives
    /// over the Reliable lifecycle channel (Р60) and the owner (Task 32) calls
    /// `Reset` with it; until then nothing is trusted, because a snapshot that
    /// outran the handshake belongs to a match this client has not been
    /// admitted to. A frame of ANY other epoch is refused on BOTH questions and
    /// changes nothing: letting a stray datagram switch the tracked epoch would
    /// let one wandering packet erase the dedup memory of the match in
    /// progress, and every event since would be replayed.
    ///
    /// MEMORY: A RING OF PER-TICK SEQ BITMASKS. One bit per (tick, seq), which
    /// is the smallest honest representation of "have I seen this" and needs no
    /// allocation, no hashing and no eviction pass. Everything is sized in the
    /// constructor; nothing below allocates.
    ///
    /// THE RING HAS A HORIZON, AND BOTH OF ITS EDGES ARE DECIDED:
    ///   * a key that has fallen out of the window is refused CONSERVATIVELY —
    ///     "assume seen". A frame that old has been in flight for more than
    ///     eight seconds and Task 32/37 discards it anyway
    ///     (`NetConfig.InterpMaxStaleTicks` is 3), so a false refusal costs a
    ///     cosmetic nobody would have seen, while a false acceptance replays a
    ///     death that already played;
    ///   * the window advances on the newest ORIGINAL tick seen, and the slots
    ///     it passes over are wiped as it goes. A reused slot that kept the old
    ///     tick's bits would silently swallow real events of a new tick, which
    ///     is the failure mode that looks exactly like a delivery bug.
    ///
    /// KNOWN LIMIT, DEFERRED TO THE CALLER — NOT resolved here (fix round 1,
    /// reviewer F2). The window advances on whatever original tick the tracked
    /// epoch's frames name, so a single well-formed frame naming a tick far in
    /// the future would drag the window — and the state gate's floor — forward
    /// until `Reset`, conservatively eating every real event behind it. This
    /// class has no notion of "the present" to bound that with; the receiving
    /// side does (Phase Ф7 builds its render clock), so Task 32 — the one
    /// caller feeding this class — must discard frames beyond a sane future
    /// horizon before this class sees them. Recorded here so that obligation
    /// is inherited explicitly rather than by archaeology.
    ///
    /// HOSTILE INPUT IS REFUSED, NEVER THROWN (Р82, and Р82 is BOTH halves —
    /// do not throw AND do not hand back rubbish). `seq` is a raw `ushort` off
    /// the wire and the mask is indexed by it, so a value at or above the
    /// server's own per-tick cap is refused before anything is indexed. A
    /// `TickDelta` larger than the frame's own tick names an event from before
    /// the match began and is refused for the same reason.
    public sealed class EventDedup
    {
        /// The largest `EventRecord.TickDelta` the one-byte field can carry, and
        /// therefore the furthest back a single frame can reach.
        const int MaxTickDelta = byte.MaxValue;

        /// The ceiling of `NetConfig.EventRedundancyTicks`'s own `[Range(0, 15)]`
        /// — a STRUCTURAL bound, not a balance number: this class never sees a
        /// NetConfig (it is per-match server tuning), so the window is sized for
        /// the widest setting the field can hold.
        const int MaxRedundancyTicks = 15;

        /// How many distinct ORIGINAL ticks the ring remembers, and the whole
        /// arithmetic behind the number:
        ///   * a frame at tick F can name an original tick as old as F - 255
        ///     (`MaxTickDelta`);
        ///   * that record may still be resent for `EventRedundancyTicks - 1`
        ///     further frames, at most 14 of them, so the last frame that can
        ///     carry it sits at F + 14;
        ///   * by that frame the newest original tick seen may be F + 14 itself
        ///     (an event of the current tick rides at delta 0).
        /// The span between the oldest key still arriving and the newest one
        /// seen is therefore (F + 14) - (F - 255) = 269 ticks, i.e. 270 distinct
        /// tick values — 255 + 15, spelled out.
        public const int WindowTicks = MaxTickDelta + MaxRedundancyTicks;

        const int SeqBitsPerWord = 64;

        /// Highest `seq` this class can remember, exclusive. The server's hard
        /// per-tick cap is `2 * Arena.MaxEventsPerFrame` — one wire event per
        /// SimEvent plus the `ProjectileFired` split into spawn and report
        /// (SnapshotAssembler's own doc, where the same expression sizes the
        /// wire buffer) — which is 1024 at the shipped caps, comfortably inside
        /// the `ushort` the field rides in.
        public int SeqCapacity { get; }

        readonly int _wordsPerTick;
        readonly ulong[] _seen;

        bool _hasEpoch;
        ushort _epoch;

        bool _hasWindow;
        uint _newestTick;

        bool _hasApplied;
        uint _lastAppliedTick;

        public EventDedup(in SimConfig cfg)
        {
            SeqCapacity = math.max(1, cfg.Arena.MaxEventsPerFrame * 2);
            _wordsPerTick = (SeqCapacity + SeqBitsPerWord - 1) / SeqBitsPerWord;
            _seen = new ulong[WindowTicks * _wordsPerTick];
        }

        /// Starts tracking `epoch` and forgets everything else — every seq mask,
        /// the window, and the last applied tick. Called by the owner (Task 32)
        /// on the Reliable lifecycle message that names the match's epoch, which
        /// includes a restart (Р60): a restarted match replays its own ticks
        /// from zero, so a client that remembered the previous epoch's keys
        /// would eat the opening seconds of the new one.
        public void Reset(ushort epoch)
        {
            _epoch = epoch;
            _hasEpoch = true;
            _hasWindow = false;
            _newestTick = 0;
            _hasApplied = false;
            _lastAppliedTick = 0;
            System.Array.Clear(_seen, 0, _seen.Length);
        }

        /// May the STATE blocks of the frame `(epoch, tick)` be applied? `true`
        /// also RECORDS the frame as applied, so the caller must only ask when
        /// it means to act on the answer. Р31: a frame of the tracked epoch
        /// applies only if its tick is strictly newer than the last one that
        /// did. Anything else — a foreign epoch, an overtaken frame, a
        /// duplicated datagram — is refused, and the caller decides which of
        /// `NetStats.StaleSnapshots`/`DuplicateSnapshots` that was.
        public bool TryAcceptState(ushort epoch, uint tick)
        {
            if (!_hasEpoch || epoch != _epoch) return false;
            if (_hasApplied && tick <= _lastAppliedTick) return false;

            _hasApplied = true;
            _lastAppliedTick = tick;
            return true;
        }

        /// Is this the FIRST sight of the event `record` carried by the frame
        /// `(epoch, frameTick)`? `true` also marks it seen. Deliberately
        /// independent of `TryAcceptState`: an event out of a frame whose state
        /// was refused as stale is still handled if its key is new (spec §3.7's
        /// refinement of Р31).
        ///
        /// The key is built HERE rather than by the caller, because the
        /// subtraction that produces it — the frame's tick minus the record's
        /// delta — is the one step in the whole scheme that must never be got
        /// wrong, and a contract every call site restates is a contract every
        /// call site can break.
        public bool TryAcceptEvent(ushort epoch, uint frameTick, in SnapshotBlocks.EventRecord record)
        {
            if (!_hasEpoch || epoch != _epoch) return false;

            // Both guards are on untrusted wire bytes (Р82) and both come BEFORE
            // any indexing or any state change: a seq the mask has no bit for,
            // and a delta reaching back past the start of the match.
            if (record.Seq >= SeqCapacity) return false;
            if (record.TickDelta > frameTick) return false;

            uint originTick = frameTick - record.TickDelta;

            if (!_hasWindow)
            {
                _hasWindow = true;
                _newestTick = originTick;
            }
            else if (originTick > _newestTick)
            {
                AdvanceWindow(originTick);
            }
            else if (_newestTick - originTick >= WindowTicks)
            {
                // Older than the ring can answer for. Conservative refusal —
                // see the class doc for why a false "seen" is the cheap error
                // here and a false "new" is not.
                return false;
            }

            int word = SlotOf(originTick) + (record.Seq >> 6);
            ulong bit = 1UL << (record.Seq & (SeqBitsPerWord - 1));
            if ((_seen[word] & bit) != 0) return false;

            _seen[word] |= bit;
            return true;
        }

        /// Moves the newest edge of the ring to `tick`, wiping every slot it
        /// passes so a reused one cannot answer with a previous occupant's bits.
        /// The cost is bounded by the ring itself — a jump of a whole window or
        /// more wipes everything once instead of walking a gap that could be
        /// hours wide — so no input can make this loop long.
        void AdvanceWindow(uint tick)
        {
            uint jump = tick - _newestTick;
            if (jump >= WindowTicks)
            {
                System.Array.Clear(_seen, 0, _seen.Length);
            }
            else
            {
                // Walk the STEP COUNT, never absolute tick values (fix round 1,
                // reviewer F1): `for (t = _newestTick + 1; t <= tick; t++)`
                // holds for EVERY uint when tick == uint.MaxValue — the
                // increment wraps to zero and the loop never leaves, which two
                // well-formed frames of the tracked epoch can reach. The step
                // count is bounded by `jump`, itself under WindowTicks on this
                // branch, and no input stretches that.
                for (uint step = 1; step <= jump; step++)
                    System.Array.Clear(_seen, SlotOf(_newestTick + step), _wordsPerTick);
            }
            _newestTick = tick;
        }

        int SlotOf(uint tick) => (int)(tick % WindowTicks) * _wordsPerTick;
    }
}
