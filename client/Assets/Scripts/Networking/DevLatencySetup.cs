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
    /// mirroring FishNet's own dev-only gate around every call site that
    /// touches the simulator: TransportManager.cs:1-3 defines DEVELOPMENT
    /// from that exact condition, and :652-654/:695-699/:770-774 are the
    /// only places AddOutgoing runs, all inside `#if DEVELOPMENT`. In a
    /// release build this type does not exist at all — the guarantee is
    /// structural (dead-code elimination by the compiler), not a runtime
    /// branch that could be left on by mistake (task-33-brief.md §0a).
    ///
    /// Apply is meant to be called from BOTH processes — the server (Task
    /// 36) and the client (Task 44); this task does not wire either caller,
    /// since MatchServer does not exist yet (carry-forward, task-33-brief.md
    /// §1). LatencySimulator.AddOutgoing only delays the OUTGOING side of
    /// whichever process calls it (LatencySimulator.cs:253 AddOutgoing,
    /// :286 where the delay value is computed; TransportManager.cs:697 "to
    /// client", :772 "to server"), so a process that never applies this
    /// gets half the intended RTT and packet loss in one direction only.
    public static class DevLatencySetup
    {
        /// Single entry point (task-33-brief.md §2.1). Reads
        /// net.LatencySimRttMs/LatencySimLossPercent, applies them to
        /// `simulator`, and writes the resulting facts to `stats`.
        /// SetOutOfOrder is deliberately left untouched — NetConfig has no
        /// knob for it (out of scope for this task, task-33-brief.md §1) —
        /// so it stays at FishNet's own default of 0. Does not call
        /// simulator.Initialize (FishNet's own start-up does that) and does
        /// not read a .asset off disk; which NetConfig/NetStats instance
        /// this runs against is entirely the caller's business.
        public static void Apply(LatencySimulator simulator, NetConfig net, NetStats stats)
        {
            int oneWayMs = OneWayLatencyMs(net.LatencySimRttMs);
            double lossFraction = ClampedLossFraction(net.LatencySimLossPercent);

            simulator.SetLatency(oneWayMs);
            simulator.SetPacketLoss(lossFraction);
            simulator.SetEnabled(true); // always on; inertness with zero knobs is CanSimulate's job (LatencySimulator.cs:46)

            // Mirrors CanSimulate's own condition (LatencySimulator.cs:46)
            // at GetEnabled() == true, which SetEnabled(true) above
            // guarantees: an enabled simulator with both knobs at zero is
            // inert. (SetOutOfOrder is never touched here, so its own
            // CanSimulate term stays at FishNet's default of 0 and drops
            // out of this condition entirely.)
            bool active = oneWayMs > 0 || lossFraction > 0d;

            stats.LatencySimActive = active;
            stats.LatencySimRttMs = net.LatencySimRttMs;
            stats.LatencySimOneWayMs = oneWayMs;
            stats.LatencySimLossPercent = net.LatencySimLossPercent;
        }

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
        /// either — task-33-brief.md §0a, LatencySimulator.cs:99). Integer
        /// division truncates toward zero for a non-negative input, so an
        /// odd RTT rounds DOWN (81 -> 40, not 41); that half-a-millisecond
        /// error is documented here rather than special-cased away.
        public static int OneWayLatencyMs(int rttMs)
        {
            if (rttMs <= 0) return 0;
            return rttMs / 2;
        }

        /// Percent-per-direction to the [0,1] fraction FishNet's
        /// SetPacketLoss expects, clamped to 1 AFTER the division — the
        /// packaged setter itself does not clamp (task-33-brief.md §0a,
        /// LatencySimulator.cs:140), so an owner-entered percentage above
        /// 100 would otherwise reach the transport as a fraction above 1.
        static double ClampedLossFraction(float lossPercent)
        {
            if (lossPercent <= 0f) return 0d;
            double fraction = lossPercent / 100.0;
            return fraction > 1d ? 1d : fraction;
        }
    }
}
#endif
