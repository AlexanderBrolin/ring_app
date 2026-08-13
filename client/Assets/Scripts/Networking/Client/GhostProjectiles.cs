using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Networking.Client
{
    /// Stage 2 Task 35 (spec §3.9 Р40/Р67, ADR-002 §5): the client's own
    /// predicted-shot tracers — spawned the instant a predicted tick would
    /// fire, long before the server's confirmation can arrive (≥ RTT/2 +
    /// buffer ≈ 140 ms), so the shooter's own round leaves the barrel without
    /// waiting on the network (cosmetic prediction, spec §3.14 lag-gate
    /// point 8). Networking.Client is a folder inside the `Ring.Networking`
    /// assembly, same as `EventDedup`/`SnapshotQueue` beside it — nothing
    /// here touches UnityEngine or FishNet, and Simulation.Combat's
    /// `WeaponSystem` is the only cross-assembly dependency, reached through
    /// its two public members (`CanFire`/`WouldFireThisTick`, task-35-brief
    /// §0a/§2.1, decision "Б" — fix-round 1, finding I-1).
    ///
    /// NO FLIGHT MATH LIVES HERE, ON PURPOSE. Task 35's whole job is
    /// deciding WHICH tracer exists and for how long, never WHERE it flies —
    /// geometry (position, velocity, the aim/spread draw `WeaponSystem.
    /// SpawnShot` owns) is Ф9's job (the renderer), and this class never
    /// reads a single field of `PlayerState`/`SimInput`/`WeaponSimConfig`
    /// beyond what the gate itself consumes. The one "trajectory" parameter
    /// this class stores is the predicted BIRTH TICK (brief §2.2: opaque
    /// storage, at minimum the birth tick) — everything a ghost's own
    /// lifecycle (`Advance` below) needs, and nothing a renderer would need
    /// to relitigate.
    ///
    /// THE SPAWN GATE IS `WeaponSystem.WouldFireThisTick` (fix-round 1,
    /// finding I-1 — supersedes the original decision 0a text below).
    /// `CanFire` alone is coarser than "a shot fires this tick": it never
    /// reads `FireCooldown`, so gating a per-tick spawn on it alone fires on
    /// EVERY tick the trigger stays held — measured, a 3.6x over-spawn
    /// against the shipped `FireInterval`, which permanently shifts the FIFO
    /// `Confirm` matches against (fix-round 1 finding I-1's own numbers).
    /// `WouldFireThisTick` composes `CanFire` with the one further term
    /// `Advance`'s own loop decides the shot on — see that method's doc for
    /// the exact formula and its state contract (same as `CanFire`'s own:
    /// AFTER movement, BEFORE this tick's weapon phase).
    ///
    /// GHOST IDS ARE NEGATIVE AND NEVER REMAPPED (Р67, plan finding C-2). The
    /// id handed back by `TrySpawnFromPrediction` is the ONLY id this ghost
    /// is ever known by, from spawn to `TryTranslateEnd`. `Confirm` records
    /// which SERVER id belongs to which ghost internally and never surfaces
    /// it, and never touches the ghost's own id — a consumer keying a view
    /// registry off this id (Ф9, out of scope here) never sees a `Confirm`
    /// as a retire+rent, which is exactly the artifact Р67 exists to avoid
    /// (a confirmed remap would teleport the client's own tracer back
    /// `(RTT/2 + buffer) × ProjectileSpeed ≈ 5-7 m` and cut its trail). The
    /// counter that MINTS these ids starts at -1 and counts DOWN — server ids
    /// arrive as `u16` (0..65535) over the wire, so the two spaces can never
    /// collide by construction, with no runtime check needed to keep them
    /// apart. Freeing a slot (expiry, translation, or the fix-round 1
    /// `maxTrackTicks` ceiling below) never returns its GHOST id to
    /// circulation either — only the physical SLOT is reclaimed — so a stale
    /// visual entity keyed off a forgotten id can never collide with a new
    /// ghost's id later in the same match.
    ///
    /// MATCHING IS POSITIONAL FIFO, NOT IDENTITY (brief §2.2). The server
    /// spawns a shooter's own rounds in the order their predicted ticks fired
    /// and confirms them in that same order (`ProjectileSpawnedNet`, Task
    /// 44's wiring), so `Confirm` always resolves against the OLDEST
    /// still-unconfirmed ghost, never by matching some payload the wire
    /// message doesn't carry. `Confirm`'s own `tick` parameter (task-35-brief
    /// §2.2's signature) is accepted but not read by this task's logic — the
    /// FIFO position is the entire match, and no described behavior in this
    /// task depends on the confirmation's own tick value; a future
    /// consumer (telemetry, latency measurement) is free to start reading it
    /// without a signature change.
    ///
    /// KNOWN LIMIT, DEFERRED TO THE CALLER (fix-round 1, finding I-4 — same
    /// discipline `EventDedup`'s own class doc records for its finding F2).
    /// I-1/I-2 remove the two causes of MASS FIFO desync (over-spawn, and
    /// mass-expiry on a backward replayed tick); what remains is a single
    /// residual RISK SHAPE neither this class nor any code change can close
    /// (fix-round 2, W12 rewrites this paragraph — an earlier draft's
    /// sentence here was unreadable, and it overreached alongside it by
    /// claiming a lost spawn event was FIFO-safe, which is false): a
    /// WRONG-IDENTITY match, whenever a `Confirm` call lands on a ghost
    /// OTHER than the one it actually belongs to, while the queue is
    /// non-empty. Two wire events can trigger it, not one:
    ///   * a server-side SPAWN event is lost in transit (events are
    ///     unreliable, Р58) — the ghost it would have confirmed (say G1, the
    ///     oldest unconfirmed) is never told to `Confirm` and just sits at
    ///     the front of the FIFO. If a LATER projectile's own spawn
    ///     confirmation then arrives — `Confirm` for a different, younger
    ///     ghost's serverId — it matches G1 instead, because matching always
    ///     resolves against the oldest unconfirmed, never by identity. When
    ///     that serverId's own end event arrives, it translates G1, not the
    ///     ghost it actually belongs to (example: spawn #1 is lost, confirm
    ///     #2 matches G1 → #2's end event translates to G1; G2, the true
    ///     owner, is left waiting behind it and eventually gasps
    ///     unconfirmed instead).
    ///   * a confirmation arrives for a ghost that has ALREADY expired via
    ///     `ghostConfirmTicks`, while the queue is NOT empty (a newer ghost
    ///     is waiting) — the stray `Confirm` matches that newer ghost
    ///     instead of no-oping, for the identical reason: this class has no
    ///     wire-carried identity to refuse it by (the class doc's own
    ///     "MATCHING IS POSITIONAL FIFO" paragraph is exactly why — the
    ///     protocol never gives `Confirm` anything else to check).
    /// Both shapes SELF-CORRECT the moment the queue next runs empty — one
    /// bad match costs one mismatched ghost, never a permanent drift — so
    /// the visible symptom either way is a single wrong tracer identity,
    /// not a cascading desync. Closing this fully would require the wire
    /// protocol to carry a client-assigned correlation id the server echoes
    /// back, which is out of this class's — and this fix-round's — scope;
    /// Task 44 inherits the obligation to decide whether that residual risk
    /// is worth a protocol change.
    ///
    /// STORAGE: FIXED SLOTS, A CIRCULAR FIFO OF SLOT INDICES, A REUSED
    /// SCRATCH BUFFER — nothing allocates after the constructor returns
    /// (same discipline as `EventDedup`/`SnapshotQueue`). `capacity` bounds
    /// the total number of ghost RECORDS alive at once (confirmed and
    /// unconfirmed together) — a confirmed ghost keeps its slot until
    /// `TryTranslateEnd` (its own end event) frees it, OR until
    /// `maxTrackTicks` (fix-round 1, finding I-3) frees it as registry
    /// hygiene if no end event ever arrives. A serverId's "already
    /// confirmed" bookkeeping doubles as its own duplicate-guard: an
    /// occupied slot's `_serverId` is the sentinel `NoServerId` (-1) until
    /// `Confirm` assigns it, so scanning for a match answers both "is this a
    /// duplicate Confirm" and "which slot does this end event belong to"
    /// with the same O(capacity) scan `SnapshotQueue`'s own class doc argues
    /// for at this scale (a match's worth of simultaneous own-shot ghosts is
    /// small — dozens, not thousands).
    ///
    /// A SPAWN THAT FINDS NO FREE SLOT REFUSES SILENTLY (Р82), exactly like a
    /// gate refusal — `TrySpawnFromPrediction` returns `false` either way,
    /// and the caller cannot and need not distinguish "the weapon wouldn't
    /// fire" from "every record slot is already in use" (pinned by
    /// `TrySpawnFromPrediction_CapacityExhaustedRefusesSilently`, fix-round
    /// 1 M-4). In production `capacity` is sized off the arena's own
    /// projectile cap (task-35-brief §2.2) and `maxTrackTicks` reclaims any
    /// slot an end event never frees (I-3), so exhaustion is a defensive
    /// floor, not an expected path.
    ///
    /// HOSTILE INPUT (WIRE-SOURCED DATA) IS REFUSED, NEVER THROWN (Р82) —
    /// fix-round 1, finding M-9 narrows this from the original "any input"
    /// phrasing. `Confirm`/`TryTranslateEnd` reject a negative `serverId`
    /// outright, before it can ever collide with the `NoServerId` sentinel
    /// an unconfirmed slot carries — every other branch on the DATA path
    /// (wire-sourced serverIds/ticks) answers with a bool/no-op rather than
    /// an exception, on any such input. The CONSTRUCTOR'S OWN arguments are
    /// a different trust boundary: `stats` is required and a `null` there is
    /// a wiring bug of the caller (Task 44's own bootstrap), deliberately
    /// NOT swallowed — the first `Advance`/expiry call dereferences it and
    /// throws an ordinary `NullReferenceException`, the same discipline Task
    /// 34's fix-round 1 (finding M7) established for a required constructor
    /// dependency: a caller that forgot to wire a dependency should fail
    /// loudly at the seam, not have this class pretend a match can run
    /// without health telemetry.
    public sealed class GhostProjectiles
    {
        /// The value an occupied slot's `_serverId` carries while its ghost
        /// is still unconfirmed — never a legal server id (those are
        /// non-negative `u16`s on the wire), so a scan for a specific
        /// `serverId` can never accidentally match an unconfirmed slot.
        const int NoServerId = -1;

        /// The first id `TrySpawnFromPrediction` ever hands out, from a
        /// fresh instance or immediately after `Reset` — pinned as a named
        /// constant because `Reset_ForgetsEverything` asserts this exact
        /// value, not merely "some negative number".
        const int FirstGhostId = -1;

        readonly int _capacity;
        readonly int _ghostConfirmTicks;
        readonly int _maxTrackTicks;
        readonly NetStats _stats;

        readonly bool[] _occupied;
        readonly int[] _ghostId;
        readonly uint[] _birthTick;
        readonly int[] _serverId;

        /// Circular FIFO of slot indices, oldest-unconfirmed at `_queueHead`.
        /// Birth order and insertion order coincide (a slot enters at the
        /// tail the moment `TrySpawnFromPrediction` creates it and leaves
        /// only via `Confirm` or expiry in `Advance`), so the front is
        /// always both "the oldest still-unconfirmed ghost" (what `Confirm`
        /// needs) and "the next one due to expire" (what `Advance` needs) —
        /// one structure answers both questions.
        readonly int[] _unconfirmedQueue;
        int _queueHead, _queueCount;

        /// Reused scratch `Advance` writes expired ghost ids into before
        /// handing back a view over it — sized to `capacity` because no
        /// single `Advance` call can expire more UNCONFIRMED ghosts than the
        /// class can hold at once. This is the "out-buffer without
        /// allocations" the brief leaves to the implementer's own form
        /// (§2.2). ONLY unconfirmed expiries land here — a confirmed ghost
        /// freed by the `maxTrackTicks` ceiling (I-3) never does, by design
        /// (registry hygiene, not an unfulfilled prediction). THE RETURNED
        /// VIEW IS INVALIDATED BY THE NEXT `Advance` OR `Reset` CALL
        /// (fix-round 1, M-6): it is a window over this SAME reused array,
        /// not a copy, so a caller that needs to hold onto the ids past that
        /// point must finish consuming them (or copy them out) first — the
        /// same discipline any span over a reused buffer demands.
        readonly int[] _expiredScratch;
        int _expiredCount;

        int _nextGhostId;

        /// `capacity`/`ghostConfirmTicks`/`maxTrackTicks` all clamp to
        /// defensive floors — an owner-tuned `NetConfig`/fixture value is
        /// never trusted at face value, the same discipline `SnapshotQueue`'s
        /// own constructor applies to `InterpBufferTicks` (fix-round 1,
        /// M-4): `capacity` floors at 1 (a zero-or-negative capacity has no
        /// representation an array-backed ring can use); `ghostConfirmTicks`
        /// floors at 0 — AT (not fewer than) 0 confirm-ticks a ghost still
        /// survives the tick it was born on (`Advance` never expires a ghost
        /// on its own birth tick, see that method's doc), gasping only on
        /// the very next one, i.e. a "life of one tick" rather than zero;
        /// `maxTrackTicks` floors at `ghostConfirmTicks` itself (post-clamp)
        /// — a ceiling BELOW the confirm window would kill a freshly
        /// confirmed ghost before an unconfirmed one would even expire,
        /// which is never the intent of a "registry hygiene" ceiling (I-3).
        /// WHERE THE NUMBER ITSELF COMES FROM IS AN OPEN END (fix-round 2,
        /// W19 — the same open end `StalePolicy.cs`'s own `fadeTicks` doc
        /// records): wiring a concrete value is Task 44's job, not this
        /// one's — a plausible starting point is `ceil(ProjectileLifetime /
        /// TickDt) + margin`, since a confirmed ghost has no legitimate
        /// reason to outlive its own projectile's flight by much.
        public GhostProjectiles(int capacity, int ghostConfirmTicks, int maxTrackTicks, NetStats stats)
        {
            _capacity = math.max(1, capacity);
            _ghostConfirmTicks = math.max(0, ghostConfirmTicks);
            _maxTrackTicks = math.max(_ghostConfirmTicks, maxTrackTicks);
            _stats = stats;

            _occupied = new bool[_capacity];
            _ghostId = new int[_capacity];
            _birthTick = new uint[_capacity];
            _serverId = new int[_capacity];
            _unconfirmedQueue = new int[_capacity];
            _expiredScratch = new int[_capacity];

            for (int i = 0; i < _capacity; i++) _serverId[i] = NoServerId;
            _nextGhostId = FirstGhostId;
        }

        /// Spawns a ghost for a predicted shot, gated by
        /// `WeaponSystem.WouldFireThisTick` (fix-round 1, finding I-1 —
        /// supersedes the original `CanFire`-only gate) — `false` on refusal
        /// (gate closed or no free slot, Р82) with `ghostId` left at 0 on
        /// EITHER refusal path (fix-round 1, M-8 — the caller never needs to
        /// tell them apart, see the class doc). No flight math, no spread
        /// draw: the class doc explains why `predicted`/`input`/`weapon`
        /// exist only to feed the gate.
        public bool TrySpawnFromPrediction(in PlayerState predicted, in SimInput input,
            in WeaponSimConfig weapon, uint predictedTick, out int ghostId)
        {
            ghostId = 0;
            if (!WeaponSystem.WouldFireThisTick(in predicted, in input, in weapon)) return false;

            int slot = FreeSlotIndex();
            if (slot < 0) return false;

            int id = _nextGhostId;
            _nextGhostId--;

            _occupied[slot] = true;
            _ghostId[slot] = id;
            _birthTick[slot] = predictedTick;
            _serverId[slot] = NoServerId;
            EnqueueUnconfirmed(slot);

            ghostId = id;
            return true;
        }

        /// Matches `serverId` to the OLDEST still-unconfirmed ghost (FIFO,
        /// class doc) and remembers the pairing internally — the ghost's own
        /// id and birth tick never change. Two refusals, BOTH silent
        /// no-ops (Р82), and NEITHER decorative — see
        /// `Confirm_DuplicateServerIdIsSilentNoOp`/
        /// `Confirm_AfterExpiryIsSilentNoOp` (fix-round 1, finding I-5
        /// pinned the first with a dedicated test; it was reachable but
        /// unpinned before):
        ///   * a DUPLICATE `serverId` — an occupied slot already carries it.
        ///     This guard is guaranteed ONLY while that slot is still ALIVE
        ///     (occupied): once `TryTranslateEnd` has freed it, a
        ///     POST-FACTUM duplicate (the same wire event delivered twice
        ///     AFTER this class already forgot the first) is NOT this
        ///     class's to catch — the id space has moved on and nothing
        ///     here remembers a translated serverId anymore. That is the
        ///     caller's (Task 44) precondition to uphold: a repeated
        ///     `ProjectileSpawnedNet` for the same server projectile is
        ///     filtered upstream by `EventDedup`, before it ever reaches
        ///     this method.
        ///   * an EMPTY queue — nothing waiting, including a late
        ///     confirmation for a ghost that already expired (class doc's
        ///     KNOWN LIMIT paragraph covers the one case this does NOT fully
        ///     close: a non-empty queue at the time the stray confirmation
        ///     arrives).
        public void Confirm(int serverId, uint tick)
        {
            if (serverId < 0) return;

            for (int i = 0; i < _capacity; i++)
                if (_occupied[i] && _serverId[i] == serverId) return;

            if (_queueCount == 0) return;

            int slot = DequeueUnconfirmedFront();
            _serverId[slot] = serverId;
        }

        /// For a CONFIRMED `serverId`, hands back the ghost's own id and
        /// frees the record (an end event is terminal — Task 44 routes it
        /// once). An unknown `serverId` (never confirmed, already
        /// translated, freed early by the `maxTrackTicks` ceiling, or
        /// negative) refuses without throwing, `ghostId` left at 0 on that
        /// path too (fix-round 1, M-8 — same discipline as
        /// `TrySpawnFromPrediction`'s refusal).
        public bool TryTranslateEnd(int serverId, out int ghostId)
        {
            ghostId = 0;
            if (serverId < 0) return false;

            for (int i = 0; i < _capacity; i++)
            {
                if (_occupied[i] && _serverId[i] == serverId)
                {
                    ghostId = _ghostId[i];
                    FreeSlot(i);
                    return true;
                }
            }
            return false;
        }

        /// Ages every record against the PREDICTED tick base (Р67 — never
        /// `RenderTick`) and returns a view over the UNCONFIRMED ids that
        /// gasped this call (see `_expiredScratch`'s own doc for the view's
        /// lifetime). Two independent ceilings, in order:
        ///
        /// UNCONFIRMED (the FIFO this walks first) — not confirmed within
        /// `ghostConfirmTicks` of its own birth tick: freed, its id lands in
        /// the returned view, `NetStats.UnconfirmedGhosts` counts it once.
        /// A ghost exactly `ghostConfirmTicks` old is still alive; one tick
        /// past that, it gasps.
        ///
        /// CONFIRMED, NO END EVENT (fix-round 1, finding I-3) — a confirmed
        /// record is never walked by the FIFO above (`Confirm` already
        /// dequeued it) and would otherwise occupy its slot until `Reset`:
        /// a lost `ProjectileEndedNet` (events are unreliable, Р58)
        /// permanently burns capacity, and exhausting `capacity` this way
        /// silently refuses every spawn for the rest of the match. Every
        /// occupied CONFIRMED slot gets the same treatment past
        /// `maxTrackTicks` ticks old, freed SILENTLY: it was confirmed —
        /// this is registry hygiene, not an unfulfilled prediction, so it
        /// touches neither the returned view nor `NetStats`.
        ///
        /// BOTH AGE COMPUTATIONS ARE SIGNED (fix-round 1, finding I-2 —
        /// `AgeOf`'s own doc). FishNet replays the `[Replicate]` queue after
        /// every state packet — the predicted tick this method is called
        /// with runs BACKWARD relative to the previous call roughly as often
        /// as state packets arrive (~30x/s on an ordinary connection), not a
        /// hypothetical. A ghost whose age reads NEGATIVE — "born after the
        /// tick this call is asking about", the replay-tick case — is
        /// treated as too young to judge either ceiling, not as a fully
        /// wrapped-around unsigned age past every threshold at once.
        public System.ReadOnlySpan<int> Advance(uint predictedTick)
        {
            _expiredCount = 0;

            while (_queueCount > 0)
            {
                int slot = _unconfirmedQueue[_queueHead];
                int age = AgeOf(slot, predictedTick);
                if (age <= _ghostConfirmTicks) break;

                DequeueUnconfirmedFront();
                _expiredScratch[_expiredCount] = _ghostId[slot];
                _expiredCount++;
                FreeSlot(slot);
                _stats.UnconfirmedGhosts++;
            }

            for (int i = 0; i < _capacity; i++)
            {
                if (!_occupied[i] || _serverId[i] == NoServerId) continue;
                if (AgeOf(i, predictedTick) > _maxTrackTicks) FreeSlot(i);
            }

            return new System.ReadOnlySpan<int>(_expiredScratch, 0, _expiredCount);
        }

        /// Forgets every record, the unconfirmed queue, and restarts the id
        /// counter at `FirstGhostId` — a new match is a new life (brief
        /// §2.2). `NetStats` is NOT touched by `Reset`: it is a
        /// per-connection instance Task 44 owns and resets (if ever) on its
        /// OWN lifecycle, not this class's per-match one — this class only
        /// ever INCREMENTS into it, sanctioned by `NetStats.cs`'s own class
        /// doc (lines 5-9), which names this task among the several systems
        /// that increment its counters every tick (fix-round 1, M-10
        /// corrects an earlier draft of this doc that miscited
        /// `SnapshotQueue.OverflowDroppedSnapshots` — a field SnapshotQueue
        /// owns OUTRIGHT, not an external sink it writes into like this
        /// class writes `NetStats` — as if it were the same shape of fact).
        public void Reset()
        {
            for (int i = 0; i < _capacity; i++)
            {
                _occupied[i] = false;
                _ghostId[i] = 0;
                _birthTick[i] = 0;
                _serverId[i] = NoServerId;
            }

            _queueHead = 0;
            _queueCount = 0;
            _expiredCount = 0;
            _nextGhostId = FirstGhostId;
        }

        /// Test-only introspection (`internal`, visible to
        /// `Ring.Simulation.Tests` via this assembly's `InternalsVisibleTo` —
        /// same mechanism Task 34's fix-round added). NOT part of the public
        /// Task 44 contract (task-35-brief §2.2 names exactly six public
        /// members; this is not one of them) — it exists solely so
        /// `Ghost_TrajectoryUnchangedOnConfirm` can observe the one opaque
        /// parameter this class stores without widening the production
        /// surface.
        internal bool TryGetBirthTick(int ghostId, out uint birthTick)
        {
            for (int i = 0; i < _capacity; i++)
            {
                if (_occupied[i] && _ghostId[i] == ghostId)
                {
                    birthTick = _birthTick[i];
                    return true;
                }
            }
            birthTick = 0;
            return false;
        }

        /// The SIGNED age of slot `slot`'s record against `predictedTick`
        /// (fix-round 1, finding I-2). `predictedTick - _birthTick[slot]` is
        /// unsigned `uint` arithmetic that WRAPS on a backward tick (a
        /// replayed `[Replicate]` queue, class doc) into a value near
        /// `uint.MaxValue` — reinterpreting those same 32 bits as `int`
        /// (`unchecked`, never throws) recovers the true signed distance for
        /// every gap this class will ever see in one match (ticks measured
        /// in the tens of thousands at most, nowhere near the ~2^31 a signed
        /// distance could misread). A NEGATIVE result reads as "born after
        /// the tick being asked about" — a ghost from the future, relative
        /// to a replay that walked time backward — and both callers below
        /// treat it as too young for their own ceiling, never as expired.
        int AgeOf(int slot, uint predictedTick)
            => unchecked((int)(predictedTick - _birthTick[slot]));

        int FreeSlotIndex()
        {
            for (int i = 0; i < _capacity; i++)
                if (!_occupied[i]) return i;
            return -1;
        }

        void FreeSlot(int slot)
        {
            _occupied[slot] = false;
            _serverId[slot] = NoServerId;
        }

        void EnqueueUnconfirmed(int slot)
        {
            int tail = (_queueHead + _queueCount) % _capacity;
            _unconfirmedQueue[tail] = slot;
            _queueCount++;
        }

        int DequeueUnconfirmedFront()
        {
            int slot = _unconfirmedQueue[_queueHead];
            _queueHead = (_queueHead + 1) % _capacity;
            _queueCount--;
            return slot;
        }
    }
}
