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
    /// its one public member (`CanFire`, task-35-brief §0a/§2.1).
    ///
    /// NO FLIGHT MATH LIVES HERE, ON PURPOSE. Task 35's whole job is
    /// deciding WHICH tracer exists and for how long, never WHERE it flies —
    /// geometry (position, velocity, the aim/spread draw `WeaponSystem.
    /// SpawnShot` owns) is Ф9's job (the renderer), and this class never
    /// reads a single field of `PlayerState`/`SimInput`/`WeaponSimConfig`
    /// beyond what `CanFire` itself consumes. The one "trajectory" parameter
    /// this class stores is the predicted BIRTH TICK (brief §2.2: opaque
    /// storage, at minimum the birth tick) — everything a ghost's own
    /// lifecycle (`Advance` below) needs, and nothing a renderer would need
    /// to relitigate.
    ///
    /// THE SPAWN GATE IS EXACTLY `WeaponSystem.CanFire`, NO MORE AND NO LESS
    /// (owner decision 0a, 2026-08-08). `CanFire` is coarser than "a shot
    /// actually leaves the barrel this tick" — it never reads
    /// `PlayerState.FireCooldown`, which gates the separate fire-loop inside
    /// `WeaponSystem`'s private `Advance` — so this class does not attempt to
    /// replicate "the exact tick a round fires" itself. That timing decision
    /// belongs to whichever caller invokes `TrySpawnFromPrediction` (Task
    /// 44): `CanFire` here is the single shared eligibility gate three
    /// consumers agree on bit for bit (`WeaponSystem.Advance` itself, the
    /// Presentation copy at `SimulationRunner:127-146` pending Task 43, and
    /// this class) — see `WeaponSystem.CanFire`'s own doc.
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
    /// apart.
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
    /// STORAGE: FIXED SLOTS, A CIRCULAR FIFO OF SLOT INDICES, A REUSED
    /// SCRATCH BUFFER — nothing allocates after the constructor returns
    /// (same discipline as `EventDedup`/`SnapshotQueue`). `capacity` bounds
    /// the total number of ghost RECORDS alive at once (confirmed and
    /// unconfirmed together) — a confirmed ghost keeps its slot until
    /// `TryTranslateEnd` (its own end event) frees it, it is never aged out
    /// by `Advance`. A serverId's "already confirmed" bookkeeping doubles as
    /// its own duplicate-guard: an occupied slot's `_serverId` is the
    /// sentinel `NoServerId` (-1) until `Confirm` assigns it, so scanning for
    /// a match answers both "is this a duplicate Confirm" and "which slot
    /// does this end event belong to" with the same O(capacity) scan
    /// `SnapshotQueue`'s own class doc argues for at this scale (a match's
    /// worth of simultaneous own-shot ghosts is small — dozens, not
    /// thousands).
    ///
    /// A SPAWN THAT FINDS NO FREE SLOT REFUSES SILENTLY (Р82), exactly like a
    /// `CanFire` refusal — `TrySpawnFromPrediction` returns `false` either
    /// way, and the caller cannot and need not distinguish "the weapon
    /// couldn't fire" from "every record slot is already in use". In
    /// production `capacity` is sized off the arena's own projectile cap
    /// (task-35-brief §2.2), so this is a defensive floor, not an expected
    /// path.
    ///
    /// HOSTILE INPUT IS REFUSED, NEVER THROWN (Р82). `Confirm`/
    /// `TryTranslateEnd` reject a negative `serverId` outright, before it
    /// can ever collide with the `NoServerId` sentinel an unconfirmed slot
    /// carries — every other branch below answers with a bool/no-op rather
    /// than an exception, on any input.
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
        /// single `Advance` call can expire more ghosts than the class can
        /// hold at once. This is the "out-buffer without allocations" the
        /// brief leaves to the implementer's own form (§2.2).
        readonly int[] _expiredScratch;
        int _expiredCount;

        int _nextGhostId;

        public GhostProjectiles(int capacity, int ghostConfirmTicks, NetStats stats)
        {
            _capacity = math.max(1, capacity);
            _ghostConfirmTicks = math.max(0, ghostConfirmTicks);
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

        /// Spawns a ghost for a predicted shot, gated EXACTLY by
        /// `WeaponSystem.CanFire` — `false` on refusal (gate closed or no
        /// free slot, Р82) with `ghostId` left at 0. No flight math, no
        /// spread draw: the class doc explains why `predicted`/`input`/
        /// `weapon` exist only to feed the gate.
        public bool TrySpawnFromPrediction(in PlayerState predicted, in SimInput input,
            in WeaponSimConfig weapon, uint predictedTick, out int ghostId)
        {
            ghostId = 0;
            if (!WeaponSystem.CanFire(in predicted, in input, in weapon)) return false;

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
        /// id and birth tick never change. A duplicate `serverId` (already
        /// confirmed) or an empty queue (nothing waiting — including a late
        /// confirmation for a ghost that already expired, class doc) is a
        /// silent no-op, same as any other Р82 refusal.
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
        /// translated, or negative) refuses without throwing.
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

        /// Ages every still-unconfirmed ghost against the PREDICTED tick
        /// base (Р67 — never `RenderTick`). A ghost exactly
        /// `ghostConfirmTicks` old is still alive; one tick past that, it
        /// gasps: its record is freed, its id lands in the returned view,
        /// and `NetStats.UnconfirmedGhosts` counts it once. Confirmed
        /// ghosts never age here — the FIFO this walks holds only
        /// unconfirmed slots, so a confirmed record survives `Advance`
        /// unconditionally until `TryTranslateEnd` retires it.
        public System.ReadOnlySpan<int> Advance(uint predictedTick)
        {
            _expiredCount = 0;

            while (_queueCount > 0)
            {
                int slot = _unconfirmedQueue[_queueHead];
                uint age = predictedTick - _birthTick[slot];
                if (age <= (uint)_ghostConfirmTicks) break;

                DequeueUnconfirmedFront();
                _expiredScratch[_expiredCount] = _ghostId[slot];
                _expiredCount++;
                FreeSlot(slot);
                _stats.UnconfirmedGhosts++;
            }

            return new System.ReadOnlySpan<int>(_expiredScratch, 0, _expiredCount);
        }

        /// Forgets every record, the unconfirmed queue, and restarts the id
        /// counter at `FirstGhostId` — a new match is a new life (brief
        /// §2.2). `NetStats` is NOT touched: it is a per-connection instance
        /// Task 44 owns across matches, the same discipline `SnapshotQueue.
        /// Reset` documents for `OverflowDroppedSnapshots`.
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
