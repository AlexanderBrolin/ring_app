namespace Ring.Networking
{
    /// Per-connection network counters (Stage 2 Task 23, spec §3.15):
    /// rejected edge requests, snapshot arrival health, dropped
    /// entities/events, input-starvation health and ghost-confirmation
    /// health, plus raw byte counters. Incremented from several systems
    /// across a single tick — the snapshot assembler (Task 28), the
    /// transport layer (Task 33/Task 36) and the ghost projectile
    /// system (Task 35) — so this is a sealed CLASS, not a struct: a
    /// struct would force `ref` plumbing through every one of those call
    /// sites, or worse, silently drop increments applied to a copy —
    /// exactly the class of bug phase Ф5 hit four separate times. One
    /// instance is allocated per connection per match.
    ///
    /// NOT part of StateHash/WorldSave (lives in Ring.Networking, outside
    /// Ring.Simulation entirely) and NOT part of MatchStats: MatchStats is
    /// inside StateHash, so folding a network counter into it would make
    /// the server non-deterministic relative to the golden hashes (spec
    /// §3.7 Р26) — a packet dropped on one run and not another would then
    /// change world state. SimConfigHashTests.
    /// NetStatsCounters_DoNotOverlapMatchStatsOrWorldStats pins this as an
    /// invariant Task 58 can otherwise only check by eye.
    ///
    /// BytesDown/BytesUp are `long`: at ~34 KB/s over a half-hour match an
    /// `int` still has headroom, but the extra width is free and an
    /// overflowed traffic counter at milestone В2 would cost a whole
    /// debugging session to notice.
    ///
    /// Nothing beyond the plan's own field list — no Reset(), no
    /// properties, no methods: none has a consumer before Task 40, and
    /// AGENT.md rule 3 forbids features without one. The field set closed
    /// by Task 23 above is extended, not reopened, by the four latency-
    /// simulator fields below (Stage 2 Task 33, plan :1603-1606): they are
    /// DATA written by DevLatencySetup.Apply, not new behavior, and the
    /// class-vs-struct/no-hash reasoning above applies to them identically.
    ///
    /// LatencySimActive/RttMs/OneWayMs/LossPercent carry NO #if guard of
    /// their own even though the writer (DevLatencySetup) lives entirely
    /// under #if UNITY_EDITOR || DEVELOPMENT_BUILD: they are plain data, a
    /// release build simply never calls Apply and the fields stay at their
    /// C# defaults (false/0/0/0f) for free, so NetStats keeps ONE ABI
    /// across both configurations instead of growing a conditional shape.
    /// LatencySimActive mirrors the transport's own truth, read BACK from
    /// the simulator after applying (LatencySimulator.CanSimulate,
    /// LatencySimulator.cs:46 — fix-round 1, M-2) rather than the mere fact
    /// that Apply ran: it reads false whenever both knobs are zero, even
    /// though the simulator was still enabled.
    ///
    /// RttMs/OneWayMs/LossPercent are the APPLIED facts, not a copy of
    /// NetConfig's raw knobs (fix-round 1, I-3, coordinator decision variant
    /// a): RttMs = OneWayMs * 2 and LossPercent = the fraction actually
    /// handed to SetPacketLoss, times 100. "OneWayMs == RttMs / 2" is
    /// therefore true across the WHOLE input domain, including hostile
    /// ones — a negative NetConfig.LatencySimRttMs never leaks through as
    /// -80, it reads back as 0/0/0 with LatencySimActive false. For every
    /// well-behaved input (CR 7's own 80/5) the applied facts and the raw
    /// knobs are numerically identical, so this is invisible at the
    /// defaults.
    public sealed class NetStats
    {
        public int EdgeRequestsRejected, StaleSnapshots, DuplicateSnapshots,
            DroppedEntities, DroppedEvents, InputStarved, InputOverwritten,
            UnconfirmedGhosts;
        public long BytesDown, BytesUp;

        // Stage 2 Task 33 (spec §3.14, Р107): dev latency-simulator APPLIED
        // FACTS for the Task 48 overlay (fix-round 1, I-3 — not a copy of
        // the raw NetConfig knobs, see the class doc above). RttMs is
        // derived back from OneWayMs (= OneWayMs * 2) so the overlay never
        // has to divide in its head and the invariant holds even on
        // hostile input. LossPercent is per-direction, same as the
        // NetConfig field it mirrors (5 => ~9.75% round-trip).
        public bool LatencySimActive;
        public int LatencySimRttMs;
        public int LatencySimOneWayMs;
        public float LatencySimLossPercent;
    }
}
