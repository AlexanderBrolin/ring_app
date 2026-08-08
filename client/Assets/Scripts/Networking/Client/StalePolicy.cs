using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// Stage 2 Task 37 (spec §3.9 Р39/Р77, §6i Р149): the client's stale-entity
    /// and global-starvation policy — the decision that tells Presentation
    /// (Phase Ф9, out of scope here) whether an entity that stopped updating
    /// is still alive off-screen or genuinely gone, and whether the WHOLE
    /// picture should freeze because the connection itself went quiet.
    ///
    /// NAMESPACE, NOT ASSEMBLY. `Ring.Networking.Client` is a folder inside
    /// the `Ring.Networking` assembly, next to `RenderClock`/`SnapshotQueue`/
    /// `EventDedup`/`GhostProjectiles`. Nothing here touches UnityEngine or
    /// FishNet: a pure decision object, testable in EditMode with no scene.
    /// It never reads `NetConfig` (the numbers below are fixture/proxy
    /// arguments — the asset read lives with the wiring, Task 44/Ф9) and it
    /// never subscribes to snapshots itself (the caller feeds it facts).
    ///
    /// TWO TICK DOMAINS, DELIBERATELY MIXED BY SUBTRACTION.
    /// `OnEntitySeen`/`OnFrameApplied` take the WIRE frame's own simulation
    /// tick (`uint`, the same domain `EventDedup`/`SnapshotQueue`/
    /// `RenderClock.OnSnapshot` all read off the header). `Advance` takes the
    /// CLIENT's own continuous render tick (`int`, `RenderClock.RenderTick`'s
    /// own contract). Both are world-tick units — `RenderTick` is defined as
    /// "how many world ticks of continuous local time have elapsed" — just
    /// offset from each other by however far the render clock trails the
    /// newest applied frame (normally `InterpBufferTicks`, so a freshly-seen
    /// entity's age reads NEGATIVE in ordinary play). Subtracting one from
    /// the other is therefore a meaningful "how long has this been true"
    /// measurement, not a domain error.
    ///
    /// BOUNDARY CHOICE, PINNED (task-37-brief §2.2 test 1's own demand): "not
    /// updated for `staleTicks` render ticks" reads `>=`. This matches the
    /// coordinator's own wording for `GlobalStarvation` — "no applied frames
    /// for `>= staleTicks` render ticks in a row" — so the one field reads
    /// the same way on both sides it gates: AT EXACTLY `staleTicks` ticks
    /// unseen an entity (or the world) is already Stale, not for one tick
    /// longer.
    ///
    /// THE EFFECTIVE WALL-CLOCK THRESHOLD IS `staleTicks + InterpBufferTicks`,
    /// NOT `staleTicks` (fix round 1, finding I-2 — confirmed CORRECT
    /// behavior, doc-only fix). `renderTick` trails the newest APPLIED frame
    /// by the interpolation buffer's own depth (see "TWO TICK DOMAINS"
    /// above), so `GlobalStarvation`'s `renderTick - lastAppliedTick >=
    /// staleTicks` test only crosses once that many render ticks of LOCAL
    /// time AND that many buffered ticks of silence have both elapsed. At
    /// the shipped defaults (`staleTicks` 3, `InterpBufferTicks` 3) the
    /// connection indicator trips after roughly 6 render ticks — about
    /// 200 ms at 30 Hz — of genuine silence, not 100 ms. This is intended,
    /// not a defect: the buffer is supposed to absorb a hole up to its own
    /// depth without raising any indicator at all, and the identical offset
    /// applies to every per-entity `age` computation in this class (both
    /// sides subtract against the SAME `renderTick`), so the two thresholds
    /// stay self-consistent with each other rather than one silently running
    /// ahead of the other.
    ///
    /// FADE PROGRESS IS AN EXPLICIT, BLOCKABLE COUNTER, NOT A SUBTRACTION.
    /// Eligibility ("has this gone stale") is read straight off `renderTick -
    /// lastSeenTick`, because that quantity is SUPPOSED to keep climbing
    /// while nothing updates the entity — `Advance` runs every render frame
    /// regardless of network activity, so age never pauses. Fade progress
    /// cannot use the same arithmetic: Р39/Р77 requires it to freeze in place
    /// (not merely read differently) during global starvation, and Р149
    /// requires the same freeze while an absence is proven only by truncated
    /// frames, and `renderTick` keeps climbing through both. So each id
    /// carries one `int` counter that `Advance` increments by AT MOST ONE per
    /// call, and ONLY when neither block applies — the one piece of state a
    /// pure subtraction could not represent. It resets to zero the instant a
    /// fresh sighting arrives (Р39/Р77: "a fresh fact beats aging"), and caps
    /// at `fadeTicks` (terminal, permanent until reappearance).
    ///
    /// THE GRACE TICK. At the exact render tick an entity crosses into
    /// not-live (`age == staleTicks`), the fade counter is left untouched —
    /// incrementing starts only from `age > staleTicks` onward. This is what
    /// gives `Stale` (frozen, zero fade spent) an observable render tick of
    /// its own before `Fading` begins, matching the plan's own two-phase
    /// wording ("freezes... AND [then] fades") rather than skipping straight
    /// from `Live` to `Fading(1)` on the threshold tick. A caller that jumps
    /// `renderTick` by more than one between two `Advance` calls (a render
    /// clock hard snap, Task 31's own `RenderClockSnapTicks` mechanism) can
    /// legitimately skip over this single tick — a known, accepted cosmetic
    /// limit, not a correctness bug.
    ///
    /// Р149, TRUNCATION: ABSENCE MUST BE CONFIRMED BY A NON-TRUNCATED FRAME
    /// AFTER THE LAST SIGHTING. `SnapshotHeaderFlags.Truncated` (bit 0, Task
    /// 28) means "this frame dropped at least one entity to stay inside the
    /// byte budget" — an entity's absence from such a frame proves nothing.
    /// The gate is `lastNonTruncatedAppliedTick > lastSeenTick[id]`: a LATER
    /// non-truncated frame arrived, and (by construction — if the entity had
    /// been in it, `OnEntitySeen` would have advanced `lastSeenTick` past
    /// it) it did not carry this entity. No per-id bookkeeping is needed
    /// beyond the two ticks already stored for other reasons; the fact falls
    /// out of the same subtraction discipline as everything else here. An
    /// entity can freeze (become `Stale`) on age alone, truncated frames or
    /// not — only the FADE start is gated (task-37-brief §2.1's own
    /// invariant: budget truncation must never make a live entity vanish
    /// from the screen).
    ///
    /// "WHOLE WORLD STALE": GLOBAL STARVATION OVERRIDES THE READING, NOT THE
    /// COUNTER. While `GlobalStarvation` is true, `StateOf` reports `Stale`
    /// for every not-yet-`Gone` entity regardless of its own fade progress —
    /// a network hiccup must not let the world visibly keep dying while the
    /// connection itself is the thing in trouble. An entity that had already
    /// reached the terminal `Gone` state before starvation began is NOT
    /// resurrected into `Stale` by it (task-37-brief §2 leaves this corner
    /// open; `Gone` is documented elsewhere as permanent until a fresh
    /// sighting, and un-gone-ing a settled fact because the network stalled
    /// would be a stranger behavior than the one line of code it would take
    /// to avoid). The underlying fade counter itself is untouched by the
    /// override — `FadeProgress` keeps returning the real accumulated value
    /// the whole time, so a caller can tell "world-frozen Stale" from
    /// "genuinely fresh Stale" if it ever needs to.
    ///
    /// CALLER CONTRACT, SPELLED OUT (fix round 1, finding I-3 — doc-only;
    /// the override above already behaves this way, this paragraph exists so
    /// Presentation does not misread it). Apply `FadeProgress(id)` whenever
    /// it is `> 0`, REGARDLESS of what `StateOf(id)` currently reads.
    /// `StateOf` answers "is this entity alive, frozen, or gone" — never
    /// "should the alpha fade be applied this frame." A caller that instead
    /// applies alpha only while `StateOf == Fading` would see a visible POP:
    /// an entity 34% faded snaps back to fully opaque the instant
    /// `GlobalStarvation` forces its reading to `Stale`, then snaps again
    /// once recovery lets `Fading` show through — exactly the artifact the
    /// frozen counter above exists to prevent, not cause.
    ///
    /// MONOTONIC FACTS, NEVER REGRESSED (same discipline as `RenderClock.
    /// OnSnapshot`/`SnapshotQueue.NewestTick`/`EventDedup`'s own newest-tick
    /// bookkeeping). `OnEntitySeen` only advances `lastSeenTick[id]` on a
    /// STRICTLY newer tick than the one already stored; `OnFrameApplied`
    /// advances `lastAppliedTick`/`lastNonTruncatedTick` the same way, each
    /// on its own axis. An out-of-order or duplicated call — traffic the
    /// caller's own dedup/staleness gates (Tasks 29/32) are supposed to have
    /// already filtered, but this class does not trust that — costs nothing
    /// beyond being ignored; it can never make an entity look FRESHER than
    /// the newest fact already on record.
    ///
    /// `Reset()` TAKES NO EPOCH — AND THAT MAKES IT LOAD-BEARING, NOT MERELY
    /// GOOD HYGIENE (fix round 1, finding M-4 corrects an earlier, WRONG
    /// safety claim this paragraph used to make). Unlike `EventDedup`/
    /// `RenderClock`/`SnapshotQueue`, this class never reads or checks an
    /// epoch — the plan gives none of its three entry points one
    /// (task-37-brief §2.1). The owner (Task 44/Ф9) MUST call `Reset()` on
    /// every epoch change, a restart included, the same trigger as its
    /// neighbors — but UNLIKE its neighbors, a forgotten call here is not
    /// merely "stale data quietly ignored until a legitimate fact arrives":
    /// `lastAppliedTick`/`lastNonTruncatedTick` are GLOBAL clocks, not keyed
    /// per id, and a restarted match's `renderTick` starts back near zero
    /// (`RenderClock` resets on the very same epoch change) while these two
    /// would still hold the PREVIOUS match's high tick values. Both failures
    /// this causes are SILENT, not a crash: `GlobalStarvation` (`renderTick -
    /// lastAppliedTick >= staleTicks`) reads `false` for as long as the new
    /// match's `renderTick` stays behind the stale `lastAppliedTick` —
    /// potentially the whole match — so the connection indicator never trips
    /// even during genuine silence; and `ConfirmedAbsent`
    /// (`lastNonTruncatedTick > lastSeenTick[id]`) reads `true` for every
    /// entity from the new match's very first tick, because the stale,
    /// enormous `lastNonTruncatedTick` outranks every fresh, small
    /// `lastSeenTick` — Р149's own gate, propped open from the first frame,
    /// exactly the failure it exists to prevent. Neither failure shows up as
    /// a test failure at wiring time; both surface only as "fades started
    /// too early" or "the indicator never lit," days later. This is the one
    /// call in this class's contract where a missed caller obligation
    /// corrupts behavior across an ENTIRE match, not just a single stale
    /// fact — see task-37-report.md §6 for the contract line this demands of
    /// Task 44/Ф9.
    ///
    /// HOSTILE INPUT IS REFUSED, NEVER THROWN (Р82). `id` is a DENSE index
    /// into the CLIENT's own view registry, `[0, capacity)` — NOT the wire
    /// `EntityId` (`u16`, sparse, and free to exceed `capacity`): mapping
    /// the wire id down to this dense one is Task 44's job (fix-round 2,
    /// W16 — an earlier draft of this doc left the distinction unstated).
    /// `id` is nonetheless refused outside
    /// `[0, capacity)` — `OnEntitySeen` no-ops, `StateOf`/`FadeProgress`
    /// answer `Gone`/`0f`. `frameTick` arguments beyond `int.MaxValue` are
    /// refused (same bound `RenderClock` uses for its own wire tick, since
    /// this class stores and subtracts against an `int`-typed `renderTick`
    /// too). A negative `renderTick` — `RenderClock.RenderTick` never
    /// produces one, so this is a caller bug, not real wire traffic — is a
    /// no-op `Advance` rather than corrupting every id's bookkeeping.
    /// `capacity`/`staleTicks`/`fadeTicks` clamp defensively in the
    /// constructor, same discipline as `GhostProjectiles`: `capacity` floors
    /// at 1 (a zero-or-negative capacity has no representation an
    /// array-backed store can use); `staleTicks` floors at 0 (a zero
    /// `staleTicks` is a legal, if extreme, tuning — "no grace at all").
    /// `fadeTicks` FLOORS AT 1, NOT 0 (fix round 1, finding I-1 — the same
    /// relational-floor precedent `GhostProjectiles.cs:209` uses for
    /// `maxTrackTicks >= ghostConfirmTicks`). At the OLD floor of 0,
    /// `StateOf`'s terminal check (`_fadeProgress[id] >= _fadeTicks`) was
    /// unconditionally true for any not-live entity, and it ran BEFORE the
    /// starvation and truncation-confirmation guards below it — so a
    /// caller-side `fadeTicks` left at its C# default of 0 (a plausible
    /// "forgot to wire the field" bug, and task-37-brief §2.1's own open end
    /// for where this number eventually comes from) made an entity vanish
    /// OUTRIGHT during a starvation window instead of freezing with the rest
    /// of the world, and made Р149's truncation gate meaningless. Flooring
    /// at 1 forces every `Gone` transition through `Advance`, which honors
    /// both guards unconditionally — `Gone` becomes reachable only the tick
    /// after the entity was actually eligible to fade AND unblocked, never
    /// merely because `fadeTicks` was left at zero.
    ///
    /// CALLER OBLIGATION: `OnFrameApplied` MUST BE CALLED FOR EVERY APPLIED
    /// FRAME (fix-round 2, W16). `GlobalStarvation` is computed purely from
    /// `renderTick - lastAppliedTick` (see the BOUNDARY CHOICE paragraph
    /// above) — it has no independent notion of "a frame arrived", only of
    /// "the clock this method maintains says one recently did". A caller
    /// (Task 44/Ф9) that applies a frame without also calling this method
    /// for it starves this class's OWN bookkeeping, not the connection: the
    /// indicator arms itself on a perfectly healthy connection —
    /// indistinguishable from a genuine stall — the moment `renderTick`
    /// outpaces the last call this method actually received.
    ///
    /// NOTHING HERE ALLOCATES. Every array is sized once in the constructor;
    /// `Advance`/`StateOf`/`FadeProgress` are plain array reads/writes and an
    /// O(`capacity`) scan, the same shape `GhostProjectiles.Advance` and
    /// `EventDedup`'s own window walk already use at this scale.
    public sealed class StalePolicy
    {
        /// The four states Presentation reads per entity. `Fading` carries no
        /// progress of its own — see `FadeProgress`, a separate query so the
        /// enum itself stays a plain state name (task-37-brief §2.1 left the
        /// form to the implementer).
        public enum StaleState : byte
        {
            Live,
            Stale,
            Fading,
            Gone,
        }

        /// Highest wire tick this class can store, because every stored tick
        /// is later subtracted against `renderTick`, an `int` by contract —
        /// the same bound `RenderClock.MaxRepresentableTick` uses for the
        /// same reason.
        const uint MaxRepresentableTick = int.MaxValue;

        readonly int _capacity;
        readonly int _staleTicks;
        readonly int _fadeTicks;

        readonly bool[] _everSeen;
        readonly uint[] _lastSeenTick;
        readonly int[] _fadeProgress;

        bool _hasApplied;
        uint _lastAppliedTick;

        bool _hasNonTruncated;
        uint _lastNonTruncatedTick;

        int _currentRenderTick;
        bool _globalStarvation;

        /// The entity-id capacity this instance was built for, post-clamp.
        public int Capacity => _capacity;

        /// Whether no frame has been applied for `staleTicks` render ticks or
        /// more, as of the last `Advance` call. `false` before the first
        /// `OnFrameApplied` ever arrives — there is nothing to call
        /// "starved" yet when nothing has been expected to arrive (the same
        /// premise `RenderClock.Started`/`SnapshotQueue`'s pre-`Reset` state
        /// both rest on).
        public bool GlobalStarvation => _globalStarvation;

        public StalePolicy(int capacity, int staleTicks, int fadeTicks)
        {
            _capacity = math.max(1, capacity);
            _staleTicks = math.max(0, staleTicks);
            _fadeTicks = math.max(1, fadeTicks); // fix round 1, finding I-1 — floors at 1, not 0 (class doc).

            _everSeen = new bool[_capacity];
            _lastSeenTick = new uint[_capacity];
            _fadeProgress = new int[_capacity];
        }

        /// Records that entity `id` was present in a frame the caller just
        /// APPLIED, at that frame's own tick. Р82: an out-of-range/negative
        /// `id` or a `frameTick` beyond what `renderTick`'s `int` can
        /// represent is refused in silence. Monotonic: a `frameTick` at or
        /// behind the one already stored for `id` is ignored (class doc) —
        /// this is also what makes reappearance work "from any state": the
        /// very next accepted call resets the fade counter to zero, and the
        /// next `Advance`/`StateOf` reads `Live` the moment `age` falls
        /// below `staleTicks` again.
        public void OnEntitySeen(int id, uint frameTick)
        {
            if (id < 0 || id >= _capacity) return;
            if (frameTick > MaxRepresentableTick) return;
            if (_everSeen[id] && frameTick <= _lastSeenTick[id]) return;

            _everSeen[id] = true;
            _lastSeenTick[id] = frameTick;
            _fadeProgress[id] = 0;
        }

        /// Records that a frame at `frameTick` was applied — feeding TWO
        /// independent clocks, both maxima (class doc): the global-starvation
        /// clock (every applied frame, truncated or not) and, only when
        /// `!truncated`, the Р149 absence-confirmation clock. An out-of-order
        /// or duplicated call changes neither. Р82: a `frameTick` beyond
        /// representable range is refused.
        public void OnFrameApplied(uint frameTick, bool truncated)
        {
            if (frameTick > MaxRepresentableTick) return;

            if (!_hasApplied || frameTick > _lastAppliedTick)
            {
                _hasApplied = true;
                _lastAppliedTick = frameTick;
            }

            if (!truncated && (!_hasNonTruncated || frameTick > _lastNonTruncatedTick))
            {
                _hasNonTruncated = true;
                _lastNonTruncatedTick = frameTick;
            }
        }

        /// Advances the policy by one render tick: recomputes
        /// `GlobalStarvation` from the current position, then gives every
        /// tracked, not-live entity at most one tick of fade progress if
        /// neither block (starvation, unconfirmed absence) applies. Called
        /// every render tick regardless of network activity (class doc). Р82:
        /// a negative `renderTick` — never produced by `RenderClock` itself —
        /// is a no-op rather than corrupting every id's bookkeeping.
        public void Advance(int renderTick)
        {
            if (renderTick < 0) return;

            _currentRenderTick = renderTick;
            _globalStarvation = _hasApplied
                && (renderTick - (int)_lastAppliedTick) >= _staleTicks;

            for (int id = 0; id < _capacity; id++)
            {
                if (!_everSeen[id]) continue;

                int age = renderTick - (int)_lastSeenTick[id];
                // Live, or the single grace tick at the boundary itself
                // (class doc): fade progress must not move on either.
                if (age <= _staleTicks) continue;

                if (_globalStarvation) continue;
                if (!ConfirmedAbsent(id)) continue;

                if (_fadeProgress[id] < _fadeTicks) _fadeProgress[id]++;
            }
        }

        /// The state Presentation should read for `id`, as of the last
        /// `Advance` call. An invalid id or one never seen answers `Gone`
        /// (class doc: the safe "nothing to show" default) rather than
        /// throwing or defaulting to `Live`.
        public StaleState StateOf(int id)
        {
            if (id < 0 || id >= _capacity || !_everSeen[id]) return StaleState.Gone;

            int age = _currentRenderTick - (int)_lastSeenTick[id];
            if (age < _staleTicks) return StaleState.Live;

            // Terminal: once the fade budget is spent, Gone is permanent
            // until a fresh OnEntitySeen (class doc — starvation does not
            // resurrect it).
            if (_fadeProgress[id] >= _fadeTicks) return StaleState.Gone;

            if (_globalStarvation) return StaleState.Stale;
            if (!ConfirmedAbsent(id)) return StaleState.Stale;

            return _fadeProgress[id] > 0 ? StaleState.Fading : StaleState.Stale;
        }

        /// Fraction of the fade budget spent, in `[0, 1]` — `0` while `Live`
        /// or never seen, `1` once `Gone`. Returns the RAW accumulator even
        /// while `StateOf` reports `Stale` because of the global-starvation
        /// override (class doc's CALLER CONTRACT paragraph, fix round 1
        /// finding I-3): the two answer different questions, and Presentation
        /// must apply this value whenever it is `> 0` regardless of what
        /// `StateOf` currently reads. `fadeTicks` is always `>= 1` post-clamp
        /// (fix round 1, finding I-1), so the division below never sees a
        /// zero denominator — no separate zero-duration branch exists.
        public float FadeProgress(int id)
        {
            if (id < 0 || id >= _capacity || !_everSeen[id]) return 0f;

            int age = _currentRenderTick - (int)_lastSeenTick[id];
            if (age < _staleTicks) return 0f;

            return math.min(1f, _fadeProgress[id] / (float)_fadeTicks);
        }

        /// Forgets every entity fact and both applied-frame clocks — a full
        /// reset (class doc: no epoch to track, so nothing else to check).
        public void Reset()
        {
            System.Array.Clear(_everSeen, 0, _capacity);
            System.Array.Clear(_lastSeenTick, 0, _capacity);
            System.Array.Clear(_fadeProgress, 0, _capacity);

            _hasApplied = false;
            _lastAppliedTick = 0;

            _hasNonTruncated = false;
            _lastNonTruncatedTick = 0;

            _currentRenderTick = 0;
            _globalStarvation = false;
        }

        /// Р149: has a NON-truncated applied frame arrived strictly after
        /// `id`'s last sighting? If it had, and `id` were in it,
        /// `OnEntitySeen` would have advanced `lastSeenTick[id]` past it —
        /// so a later non-truncated frame existing at all is proof this
        /// entity was looked for and not found, not merely cut for budget.
        /// CONFIRMATION IS MONOTONIC (fix round 1, finding M-3): a later
        /// truncated frame never re-blocks a fade already unblocked —
        /// `lastNonTruncatedTick` only ever advances forward (class doc,
        /// MONOTONIC FACTS), so once it has passed `id`'s last sighting it
        /// stays passed it no matter how many truncated frames arrive
        /// afterward.
        bool ConfirmedAbsent(int id)
            => _hasNonTruncated && _lastNonTruncatedTick > _lastSeenTick[id];
    }
}
