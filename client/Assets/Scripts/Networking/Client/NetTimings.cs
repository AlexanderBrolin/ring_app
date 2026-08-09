namespace Ring.Networking.Client
{
    /// Stage 2 Task 31 (spec §3.9, Р57/Р76): plain timings snapshot built from
    /// `NetConfig` once per match — the tick counts and the slew rate the
    /// client's interpolation machinery runs on, and nothing else.
    ///
    /// WHY A STRUCT AND NOT THE ASSET ITSELF. `NetConfig` is a
    /// `ScriptableObject`: a UnityEngine type, an editor asset, and a per-match
    /// tuning surface the owner edits by hand. The decision classes of
    /// `Ring.Networking.Client` are pure C# so they can be driven in EditMode
    /// with no scene, no engine loop and no asset database, so the SO itself
    /// never crosses into them (the plan's own wording). The CALLER — Task 32
    /// on the receive side, Task 44 in the runner — reads the asset once when
    /// the match starts and hands the resulting numbers down by value. That is
    /// also why nothing here validates: `NetInvariants` (Task 41) checks the
    /// asset at the one place it is read, and a second, weaker copy of those
    /// rules living down here would be a second answer to the same question.
    ///
    /// ALL FOUR FIELDS NOW COME OFF THAT ASSET (Stage 2 Task 41). This
    /// paragraph previously said three of four, and was correct when Task 31
    /// wrote it: `SlewFraction` had no counterpart in `NetConfig`, and Р154
    /// left "code constant or SO field" explicitly open for Task 41/44 to
    /// settle. Task 41 settled it in favour of a field — `NetConfig.
    /// SlewFraction`, defaulting to 0.05, its band checked by `NetInvariants`
    /// — so `InterpBufferTicks`/`InterpMaxStaleTicks`/`RenderClockSnapTicks`/
    /// `SlewFraction` all map straight onto same-named `NetConfig` fields
    /// (seventeen of them now, `TickRate` through
    /// `MatchAbandonGraceSeconds`).
    ///
    /// THE HAZARD BELOW SURVIVES THAT CHANGE, so it stays written down. Left
    /// at its C# default of `0`, this struct's `SlewFraction` does not merely
    /// mistune the correction — it DISABLES it outright: `RenderClock.
    /// SlewFractionOf` reads any value at or below zero as "do not correct",
    /// and the render clock falls back to the hard snap alone for every
    /// desync, however small. `NetInvariants` guards the ASSET, not this
    /// struct — and it deliberately accepts 0 there, because switching slew
    /// off is a legitimate mode (Р154). So a caller that builds a `NetTimings`
    /// and forgets to copy this fourth field still gets a silently
    /// snap-only clock, with nothing anywhere to catch it.
    ///
    /// NO TICK RATE FIELD, DELIBERATELY. Every number below is already counted
    /// in world ticks, and the one place seconds have to become ticks — the
    /// render clock's delta time — divides by `SimulationWorld.TickDt`, the
    /// single source of the 30 Hz rate (ADR-002 T5). A tick-rate field here
    /// would be a second copy of that constant that could disagree with it.
    ///
    /// THE COUNTS ARE INTEGERS BECAUSE Р76 MADE THEM INTEGERS. An interpolation
    /// window is a whole number of ticks the owner tunes, not a duration to
    /// re-derive from seconds at run time; deriving it would put the depth of
    /// the buffer, the linger of Р19 and the lag gate's own number at the mercy
    /// of a float rounding.
    public struct NetTimings
    {
        /// How far behind the newest buffered tick the render clock aims to
        /// run — the depth of the interpolation buffer, and therefore how much
        /// packet loss can be absorbed before the picture has nothing left to
        /// interpolate between. `NetConfig`'s default is 3, i.e. 100 ms at
        /// 30 Hz.
        public int InterpBufferTicks;

        /// How many ticks an entity may go without a fresh snapshot before it
        /// freezes and then fades (Р39/Р77). Consumed by the stale policy of
        /// Task 37, NOT by the render clock: the clock's job is to decide which
        /// moment is on screen, and what is still visible at that moment is a
        /// different question with a different owner. It travels here because
        /// it comes out of the same asset at the same moment and splitting the
        /// per-match timings across two structs would buy nothing.
        public int InterpMaxStaleTicks;

        /// The desync, in ticks, the render clock tolerates before it stops
        /// slewing and jumps. `NetConfig`'s default is 10 — a third of a second
        /// at 30 Hz, well past anything an ordinary hiccup produces, so the
        /// jump is reserved for the cases where slewing would take seconds.
        public int RenderClockSnapTicks;

        /// How much the render clock's rate may deviate from real time while
        /// it corrects a desync: the clock runs at `1 ± SlewFraction` instead
        /// of jumping. Source is `NetConfig.SlewFraction` (Task 41), whose
        /// default is 0.05. Spec §3.9's tuning band is 0.05..0.10 — below that
        /// a correction takes too long to finish before the next one starts,
        /// above it the change of pace becomes visible in animation — while
        /// exactly 0 is the separate, legal "do not slew at all" mode.
        public float SlewFraction;
    }
}
