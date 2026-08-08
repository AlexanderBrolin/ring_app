#if UNITY_EDITOR || DEVELOPMENT_BUILD
using FishNet.Managing.Transporting;
using Ring.Data;

namespace Ring.Networking
{
    /// Stage 2 Task 33 (spec §3.14, plan Task 33, Р107, CR 7 "80 ms RTT /
    /// 5% loss"): applies NetConfig's dev latency-simulator knobs to a live
    /// FishNet LatencySimulator and records the outcome in NetStats for the
    /// dev overlay (Task 48).
    ///
    /// The whole file lives under #if UNITY_EDITOR || DEVELOPMENT_BUILD,
    /// mirroring FishNet's own dev-only gate. TransportManager.cs:1-3
    /// defines DEVELOPMENT from that exact condition, and the simulator's
    /// entire call path is compiled only inside `#if DEVELOPMENT`: :219
    /// Initialize, :653 the CanSimulate read, :697/:772 — the ONLY two
    /// AddOutgoing calls ("to client"/"to server"), :791 IterateOutgoing
    /// (fix-round 1, I-1: :653 and :791 are not AddOutgoing calls
    /// themselves, and TransportManager.cs:810's SetEnabled sits under a
    /// SEPARATE `#if UNITY_EDITOR` block, :799 — Editor-only OnValidate
    /// housekeeping, not part of the DEVELOPMENT path this class mirrors).
    /// In a release build this type is excluded by the PREPROCESSOR before
    /// the compiler ever sees it (fix-round 1, M-6) — there is no dead code
    /// for the compiler to eliminate, because there is no code reaching the
    /// compiler in the first place.
    ///
    /// Apply is meant to be called from BOTH processes — the server (Task
    /// 36) and the client (Task 44); this task does not wire either caller,
    /// since MatchServer does not exist yet (carry-forward, task-33-brief.md
    /// §1). LatencySimulator.AddOutgoing only delays the OUTGOING side of
    /// whichever process calls it (LatencySimulator.cs:253 AddOutgoing,
    /// :286 where the delay value is computed; TransportManager.cs:697 "to
    /// client", :772 "to server"), so a CONNECTION whose two ends don't both
    /// apply this gets half the intended RTT and packet loss in one
    /// direction only (fix-round 1, M-6: the shortfall belongs to the
    /// connection/link between the two ends, not to either process taken in
    /// isolation).
    public static class DevLatencySetup
    {
        /// Single entry point (task-33-brief.md §2.1). Reads
        /// net.LatencySimRttMs/LatencySimLossPercent, applies them to
        /// `simulator`, and writes the resulting APPLIED facts to `stats`
        /// (fix-round 1, I-3 — see the field docs on NetStats itself for
        /// why these are the applied numbers, not the raw NetConfig knobs).
        /// SetOutOfOrder is deliberately left untouched — NetConfig has no
        /// knob for it (out of scope for this task, task-33-brief.md §1) —
        /// so it stays at whatever the caller (or FishNet's own default of
        /// 0) already set it to. Does not call simulator.Initialize
        /// (FishNet's own start-up does that) and does not read a .asset
        /// off disk; which NetConfig/NetStats instance this runs against is
        /// entirely the caller's business.
        ///
        /// All three arguments are required; a null is a wiring bug of the
        /// caller (Task 36/44), deliberately not swallowed by a runtime
        /// guard here (fix-round 1, M-7) — it throws immediately at the
        /// call site instead of failing silently later.
        public static void Apply(LatencySimulator simulator, NetConfig net, NetStats stats)
        {
            int oneWayMs = OneWayLatencyMs(net.LatencySimRttMs);
            double lossFraction = ClampedLossFraction(net.LatencySimLossPercent);

            simulator.SetLatency(oneWayMs);
            simulator.SetPacketLoss(lossFraction);
            simulator.SetEnabled(true); // always on; inertness with zero knobs is CanSimulate's job (LatencySimulator.cs:46)

            // Fix-round 1, M-2: read the verdict BACK from the simulator —
            // the literal CanSimulate form (LatencySimulator.cs:46) —
            // rather than recomputing it from the two local variables
            // above. A caller that already set SetOutOfOrder > 0 on this
            // same instance before calling Apply (that knob is out of THIS
            // task's scope, but not out of the simulator's) is then
            // reported correctly instead of silently read as inactive.
            bool active = simulator.GetEnabled()
                && (simulator.GetLatency() > 0 || simulator.GetPacketLost() > 0 || simulator.GetOutOfOrder() > 0);

            // Fix-round 1, I-3 (coordinator decision, variant a): NetStats
            // stores the APPLIED facts, not a copy of the raw NetConfig
            // knobs. This keeps "OneWayMs == RttMs / 2" true across the
            // WHOLE input domain, including hostile ones — a negative
            // NetConfig.LatencySimRttMs now reads back as
            // stats.LatencySimRttMs == 0, it never leaks through as -80.
            // For every well-behaved input the two are indistinguishable
            // from the raw knob (RttMs = oneWayMs * 2 collapses back to the
            // original even RTT), so this changes nothing observable at
            // CR 7's own numbers (80 -> 40 -> 80).
            stats.LatencySimActive = active;
            stats.LatencySimRttMs = oneWayMs * 2;
            stats.LatencySimOneWayMs = oneWayMs;
            stats.LatencySimLossPercent = (float)(lossFraction * 100.0);
        }

        // FishNet's own Inspector ceiling on _latency (fix-round 1, M-1;
        // LatencySimulator.cs:85-87, [Range(0, 60000)]).
        const int MaxOneWayMs = 60000;

        /// Converts a round-trip milliseconds figure into the one-way value
        /// FishNet's LatencySimulator.SetLatency expects (Р107): the
        /// simulator adds `_latency` once per direction
        /// (LatencySimulator.cs:245-248 GetLatencyAsFloat, :286 AddOutgoing
        /// where it is applied), so RTT = 2 x one-way and Apply must hand
        /// the transport HALF of NetConfig.LatencySimRttMs, not the whole
        /// figure — at the shipped default of 80 that is 40.
        ///
        /// A hostile negative input clamps to 0 rather than throwing (Р82:
        /// the [Range] attribute on NetConfig.LatencySimRttMs is an
        /// Inspector hint only, and FishNet's own SetLatency does not clamp
        /// either — task-33-brief.md §0a, LatencySimulator.cs:99). A
        /// hostile huge input clamps to 60000 (fix-round 1, M-1) — FishNet's
        /// OWN Inspector ceiling on `_latency`, since `SetLatency` itself
        /// will happily accept anything a `long` holds and this is the only
        /// place that ceiling is enforced at all. Integer division
        /// truncates toward zero for a non-negative input, so an odd RTT
        /// rounds DOWN (81 -> 40, not 41), and a positive RTT under 2 ms
        /// rounds all the way down to 0 (1 -> 0) — both documented here
        /// rather than special-cased away.
        public static int OneWayLatencyMs(int rttMs)
        {
            if (rttMs <= 0) return 0;
            int oneWayMs = rttMs / 2;
            return oneWayMs > MaxOneWayMs ? MaxOneWayMs : oneWayMs;
        }

        /// Percent-per-direction to the [0,1] fraction FishNet's
        /// SetPacketLoss expects, clamped to 1 AFTER the division — the
        /// packaged setter itself does not clamp (task-33-brief.md §0a,
        /// LatencySimulator.cs:140), so an owner-entered percentage above
        /// 100 would otherwise reach the transport as a fraction above 1.
        /// The guard is written as `!(lossPercent > 0f)`, not
        /// `lossPercent <= 0f` (fix-round 1, I-2): the two are NOT
        /// equivalent for NaN. `NaN <= 0f` is false, so the naive guard
        /// would let NaN fall through to `NaN / 100.0` and then to
        /// `NaN > 1d ? 1d : NaN` — ALSO false, since every comparison
        /// against NaN is false — so NaN would reach SetPacketLoss and
        /// NetStats unclamped. `!(lossPercent > 0f)` is true for NaN
        /// (`NaN > 0f` is false), so NaN is caught by the same branch as an
        /// ordinary non-positive input (same form as
        /// RenderClock.SlewFractionOf, RenderClock.cs:375-381).
        static double ClampedLossFraction(float lossPercent)
        {
            if (!(lossPercent > 0f)) return 0d;
            double fraction = lossPercent / 100.0;
            return fraction > 1d ? 1d : fraction;
        }
    }
}
#endif
