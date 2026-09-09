using Ring.Simulation.Core;

namespace Ring.Simulation.Combat
{
    /// One predicted shot, as the client's own tick recorded it (app-8dv T4,
    /// spec §3.4).
    ///
    /// ⭐ THE GEOMETRY LIES HERE WHOLE, NOT AS A REWRITTEN SET OF FIELDS
    /// (ruling 291): copying `ShotSolution`'s fields across would make this the
    /// THIRD edition of one structure, after `ShotSolution` itself and
    /// `ProjectileState`. The record carries the SOLUTION `ShotGeometry` worked
    /// out, and adds exactly what that solution has no way of knowing: which
    /// shot this was, and on which tick it was written.
    public readonly struct PredictedShot
    {
        /// The shot's identity, and it is the POST-increment `ShotOrdinal` --
        /// the first shot of a match is 1, never 0. ⛔ ZERO IS THE SENTINEL
        /// "no key at all", and it has to stay free: the rings that consume
        /// this key are cleared to `default`, so a first shot keyed 0 would
        /// read as "already shown" and be silently swallowed.
        public readonly int Key;

        /// FishNet's tick domain, stamped by `BeginTick` -- never the world's
        /// own tick counter. The two are unrelated (finding Н-11: the world's
        /// resets to zero per match, FishNet's is monotonic for the process),
        /// and the frame that drains this log converts between them by DELTA
        /// rather than by value.
        public readonly uint LocalTick;

        public readonly ShotSolution Solution;

        public PredictedShot(int key, uint localTick, in ShotSolution solution)
        {
            Key = key;
            LocalTick = localTick;
            Solution = solution;
        }
    }

    /// The client's own record of "I fired", written INSIDE the predicted tick
    /// and drained by the frame (spec §3.4, Р451, ruling 319).
    ///
    /// IT DECIDES NOTHING (CR 3). It records what the predicted simulation has
    /// already done -- the spawn stays in the backend, the damage stays on the
    /// server. That is what makes a client writing it legal at all.
    ///
    /// ⚠ WHY A NEW TYPE RATHER THAN ONE OF THE FIVE RINGS THAT ALREADY EXIST
    /// (rule 2). The nearest neighbors -- `OwnDamageLane` (fixed buffer,
    /// append, cleared per frame, refusal by value), `ClientEventQueue`,
    /// `EventDedup`, `ImpactPulseLog`, `HitFeedbackTrail` -- live in
    /// `Ring.Networking.Client` to the last one. This log has to live in
    /// `Ring.Simulation`, because `WeaponSystem` is what writes it, and
    /// `Simulation.asmdef` references `Unity.Mathematics` and nothing else
    /// (Р180). Reuse is not declined here, it is physically unavailable -- and
    /// the shape is copied from `OwnDamageLane` all the same, refusal-by-value
    /// with a counter included.
    public sealed class PredictedShotLog
    {
        /// Records per frame: the direct tick plus the replay that follows a
        /// state packet, doubled for headroom.
        ///
        /// ⚠ A CHOSEN NUMBER, AND SAYING SO IS HONEST RATHER THAN LAZY: no
        /// quantity called "correction window depth" exists in this tree to
        /// derive it from (`CorrectionWindowSamples` is a median's sample
        /// count, `TracerCatchUpBudget` measures something else). At 60 fps a
        /// ~30/s state rate means at most one replay per frame over a 4-6 tick
        /// window; sixteen is that, twice over. The ceiling is not silent:
        /// past it the log refuses and counts.
        public const int DefaultCapacity = 16;

        readonly PredictedShot[] _pending;
        int _count;
        uint _tick;

        /// Shots refused because the log was full when they were written. The
        /// log's OWN counter, the same shape as
        /// `ClientEventQueue.OverflowDroppedEvents` -- a public field rather
        /// than a property, exactly like that neighbor.
        /// ⛔ `Reset` DOES NOT CLEAR IT, and that is the neighbor's rule word
        /// for word: a per-connection health counter that cleared itself on
        /// every restart would hide precisely the pattern it exists to surface.
        public int OverflowDroppedShots;

        public PredictedShotLog(int capacity = DefaultCapacity)
        {
            if (capacity < 1) capacity = 1;
            _pending = new PredictedShot[capacity];
        }

        /// The FishNet tick the records that follow belong to. Set ONCE per
        /// predicted tick, by the caller that HAS such a tick --
        /// `PlayerPredictionCore.Predict`, off `PerformReplicate`.
        public void BeginTick(uint localTick)
        {
            _tick = localTick;
        }

        /// ⚠ `internal`: only `Ring.Simulation` may state that a shot happened.
        /// A public `Record` would let Presentation FABRICATE one, and
        /// `WeaponSystem`'s own header keeps the opposite discipline about
        /// mutators no outside caller may drive.
        internal void Record(int key, in ShotSolution solution)
        {
            if (_count >= _pending.Length)
            {
                OverflowDroppedShots++;
                return;
            }
            _pending[_count++] = new PredictedShot(key, _tick, in solution);
        }

        /// Everything written since the last drain; a view over the reusable
        /// buffer, invalidated by the next `Drain` or `Reset` -- the same
        /// discipline `GhostProjectiles.Advance` keeps about its own span.
        public System.ReadOnlySpan<PredictedShot> Drain()
        {
            var taken = new System.ReadOnlySpan<PredictedShot>(_pending, 0, _count);
            _count = 0;
            return taken;
        }

        /// Forgets what is waiting, on an epoch change. ⛔ Deliberately does
        /// NOT touch `OverflowDroppedShots` -- see that field's own note.
        public void Reset()
        {
            _count = 0;
        }
    }
}
