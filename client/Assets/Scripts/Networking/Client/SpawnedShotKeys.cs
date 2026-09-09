namespace Ring.Networking.Client
{
    /// What has ALREADY been born (app-8dv T4, spec §3.4): the client's memory
    /// of which predicted shots already grew a trail, so that FishNet's replay
    /// of the same ticks does not grow a second one.
    ///
    /// ⛔ NOT THE SAME BOOKKEEPING THE LATCH KEEPS (spec §3.5, finding C2₃).
    /// This one answers "has a TRAIL been born" and is written by the backend
    /// AFTER the tick; the latch's answers "has the ACT been shown" and is
    /// written in the frame BEFORE it. One home would mean a muzzle grant marks
    /// a shot before the log has recorded it -- and the predicted trail would
    /// then never appear at all.
    ///
    /// ⛔⛔ THE KEY IS THE FISHNET TICK, NOT `ShotOrdinal`, AND THAT CANCELS THE
    /// `BeginReconcile` SEAM THE SPEC ASKED FOR (deviation 9 of the plan;
    /// checked against the code rather than reasoned). The spec's mechanism --
    /// key by `ShotOrdinal`, plus a sweep of everything above the authoritative
    /// ordinal on reconcile -- DUPLICATES a trail on every reconcile, i.e.
    /// roughly thirty times a second:
    ///   * `[Reconcile]` arrives on EVERY state packet, not only on a
    ///     misprediction;
    ///   * the authoritative `ShotOrdinal` is the ordinal at the SERVER's tick,
    ///     which lags the predicted one by the depth of prediction, so
    ///     "clear everything above it" routinely drops the bar below keys whose
    ///     trails are already on screen;
    ///   * the owning client replays into `Predict` again and rewrites those
    ///     same keys into the log;
    ///   * the tracer's own duplicate guard misses, because every new ghost is
    ///     handed a NEW ghost id.
    /// The spec conflated "born authoritatively" with "already drawn": a key
    /// the server has not issued yet is indeed not born authoritatively -- but
    /// it is already DRAWN by prediction, and that is the only thing that
    /// matters here.
    ///
    /// ⭐ THE TICK FITS EXACTLY, AND THE SHAPE IS THE NEIGHBOR'S.
    /// `TimeManager.LocalTick` is monotonic for the process, unlike
    /// `ShotOrdinal`, which `BeginReconcile` steps BACKWARD by assigning the
    /// authoritative state whole; and a replay re-runs the SAME ticks, so a
    /// repeat is refused by construction while a shot that a correction moved
    /// onto a different tick is born as a new one. The form is
    /// `EventDedup.TryAcceptState`'s high-water mark, widened to a PAIR because
    /// `Advance`'s loop can fire twice in one tick (`FireInterval` shorter than
    /// `TickDt`), and compared lexicographically.
    ///
    /// ⚠ THE `_hasApplied` FLAG ITS NEIGHBOR IS FORCED TO KEEP IS NOT NEEDED
    /// HERE: there tick 0 is a legal key, here a record stamped 0 is
    /// unreachable -- the log only ever writes under the tick `Predict`
    /// received from `PerformReplicate`.
    public sealed class SpawnedShotKeys
    {
        uint _tick;
        int _seq;

        /// `false` = this record has already grown a trail (a replay's repeat).
        /// `seqInTick` is the record's 0-based position among those sharing the
        /// tick.
        public bool TryClaim(uint localTick, int seqInTick)
        {
            if (localTick < _tick) return false;
            if (localTick == _tick && seqInTick <= _seq) return false;

            _tick = localTick;
            _seq = seqInTick;
            return true;
        }

        /// A per-match seam: the tick domain does not reset between matches,
        /// but the world the trails are drawn into does.
        public void Reset()
        {
            _tick = 0;
            _seq = 0;
        }
    }
}
