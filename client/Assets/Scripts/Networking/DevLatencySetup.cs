#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Globalization;
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
    /// Apply is called from BOTH processes — the server (Task 36,
    /// MatchServer.StartMatch) and the client (Task 44,
    /// ClientMatchLink.OnClientConnectionState). As of app-ck7 both go
    /// through the FOUR-argument form with DevLatencyLaunch.Options, so one
    /// `-ring-latency` on a launch line answers for a whole process, and both
    /// print what came out of it (see Describe below).
    /// LatencySimulator.AddOutgoing only delays the OUTGOING side of
    /// whichever process calls it (LatencySimulator.cs:253 AddOutgoing,
    /// :286 where the delay value is computed; TransportManager.cs:697 "to
    /// client", :772 "to server"), so a CONNECTION whose two ends don't both
    /// apply this gets half the intended RTT and packet loss in one
    /// direction only (fix-round 1, M-6: the shortfall belongs to the
    /// connection/link between the two ends, not to either process taken in
    /// isolation).
    public static class DevLatencySetup
    {
        /// THE ASSET'S OWN NUMBERS — the single entry point of Task 33
        /// (task-33-brief.md §2.1), and since app-ck7 what the command-line
        /// form's `UseConfig` branch means rather than what the two callers
        /// call directly. Reads
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
        ///
        /// THE BODY IS TWO WORDS LONG BECAUSE THE WORK IS SHARED (app-ck7).
        /// This form's only opinion is WHERE THE NUMBERS COME FROM — the
        /// asset's two knobs; the halving, the clamps, the enable and the
        /// NetStats write live in ApplyNumbers below, which the command-line
        /// form goes through as well. Two copies of that block would be two
        /// homes for one rule (project rule 2), and the day either moved,
        /// `-ring-latency 80` and an unflagged launch would stop meaning the
        /// same thing.
        public static void Apply(LatencySimulator simulator, NetConfig net, NetStats stats)
        {
            ApplyNumbers(simulator, stats, net.LatencySimRttMs, net.LatencySimLossPercent);
        }

        /// The same apply, told what the COMMAND LINE asked for (Stage 2
        /// app-ck7, task-ck7-wire-brief.md §2.1). `DevLatencyOptions.Parse`
        /// reads the `-ring-latency` switch; this decides what the transport
        /// ends up running, and it is the only overload the two dev callers
        /// (MatchServer.StartMatch and ClientMatchLink's connection handler)
        /// use:
        ///
        ///     UseConfig  NetConfig's numbers — the three-argument form
        ///                above, unchanged. Every REFUSAL arrives here too
        ///                (DevLatencyOptions.Refuse), which is what keeps
        ///                Critical Rule 7 from becoming opt-out by typo.
        ///     Off        the simulator is not applied at all — only
        ///                SetEnabled(false); the transport's own numbers are
        ///                left exactly as FishNet's start-up left them, and
        ///                NetStats says plainly that nothing is simulated.
        ///     Override   the round trip the operator named; the loss he
        ///                named if he named one, NetConfig's if he did not
        ///                (DevLatencyOptions.HasLossPercent, never RttMs or
        ///                LossPercent read as their own presence flag).
        ///
        /// `Complaint` IS DELIBERATELY NOT READ HERE. Printing it belongs to
        /// the caller, the only side that knows which console it owns (both
        /// callers log through UnityEngine.Debug, each for its own reason,
        /// stated at its own call site) — the same split DevLatencyOptions
        /// keeps with its own doc, and the reason a refusal reaches this
        /// method already flattened into `UseConfig`: what a refusal changes
        /// is the console, never the simulator.
        ///
        /// `Override` GOES THROUGH THE SAME OneWayLatencyMs AND THE SAME
        /// LOSS CLAMP as the configured path. The parser clamps nothing on
        /// purpose, so this is the one place an RTT is halved and a
        /// percentage is bounded, for the command line's numbers exactly as
        /// for the asset's (project rule 2: a second home for that rule
        /// would disagree with the first the day either moved).
        ///
        /// THREE BRANCHES AND NO FOURTH. `Off` is the only one that does not
        /// go through ApplyNumbers, because it is the only one that applies
        /// nothing; the other two differ solely in WHERE THE TWO NUMBERS COME
        /// FROM, which is why neither of them owns a clamp, an enable or a
        /// line of the NetStats write. A caller that wants to PRINT what came
        /// out of this hands the same `options` and the same `stats` to
        /// Describe below — one sentence, one home, two consoles that cannot
        /// drift apart.
        public static void Apply(LatencySimulator simulator, NetConfig net, NetStats stats,
            DevLatencyOptions options)
        {
            switch (options.Mode)
            {
                case DevLatencyMode.Off:
                    // NOT APPLIED MEANS NOT WRITTEN. Only the bit FishNet's
                    // own CanSimulate reads first is cleared; `_latency` and
                    // `_packetLoss` keep whatever the transport's start-up
                    // left in them, since re-applying them as zeros would be
                    // applying the simulator — the one thing this mode says
                    // not to do. (SetEnabled(false) is safe with no transport
                    // attached: LatencySimulator.Reset reads the enabled flag
                    // AFTER the assignment, so the false path touches nothing
                    // — LatencySimulator.cs:180-209.)
                    simulator.SetEnabled(false);
                    Record(stats, active: false, oneWayMs: 0, lossFraction: 0d);
                    return;

                case DevLatencyMode.Override:
                    // The switch overrides what it NAMES and nothing else:
                    // `HasLossPercent` is the presence flag, never LossPercent
                    // read as its own (zero is a legal loss), so
                    // `-ring-latency 120` keeps the asset's loss exactly as an
                    // unflagged launch would.
                    ApplyNumbers(simulator, stats, options.RttMs,
                        options.HasLossPercent ? options.LossPercent : net.LatencySimLossPercent);
                    return;

                default:
                    // `UseConfig`, EVERY refusal (DevLatencyOptions.Refuse)
                    // and any answer this enum may grow later — the
                    // three-argument form verbatim, so "an unflagged launch
                    // behaves exactly as it did before the switch existed" is
                    // true by CONSTRUCTION rather than by two code paths
                    // agreeing. Written as `default` rather than as
                    // `case DevLatencyMode.UseConfig` on purpose: the safe
                    // half of the decision is where an answer nobody
                    // anticipated belongs, because Critical Rule 7 stays on
                    // for it.
                    Apply(simulator, net, stats);
                    return;
            }
        }

        /// What the call above actually did, as a fragment for the CALLER's
        /// own line (app-ck7, task-ck7-wire-brief.md §2.3). Returns a string
        /// and prints nothing: which console this process owns is the
        /// caller's knowledge — both write through UnityEngine.Debug, for two
        /// different reasons given where they write — while WHAT WAS APPLIED is
        /// this class's, and the two consoles a milestone is measured across
        /// are read side by side, so they must not word it differently.
        ///
        /// IT REPORTS THE APPLIED FACTS, NOT THE PARSED ONES. Every number
        /// below comes off `stats`, which Apply filled in from what reached
        /// the transport, so a clamped 250% prints as the 100% actually
        /// running and an operator's `5000` prints as the round trip he got.
        /// Only the MODE is read from `options` — the applied facts cannot
        /// say who asked for them, and "the asset's 80" and "the switch's 80"
        /// are the two readings an operator most needs told apart.
        ///
        /// A REFUSAL PRINTS AS "NetConfig's numbers" AND SAYS NOTHING ABOUT
        /// THE SWITCH, which is not an omission: `Complaint` is a separate
        /// line the caller prints first (and must — lesson 195), and this one
        /// would be lying if it claimed no switch had been passed.
        ///
        /// THE NUMBERS ARE FORMATTED IN THE INVARIANT CULTURE and NOT rounded
        /// — the same rule DevLatencyOptions reads them under, and the same
        /// one ServerBootstrap writes every diagnostic under. This line is
        /// what an operator compares against what he typed, so `2.25` must
        /// come back as `2.25` and not as the overlay's fixed-width `2.2`
        /// (DevOverlay rounds because it draws a row of a fixed width; a
        /// console has no such excuse).
        public static string Describe(DevLatencyOptions options, NetStats stats)
        {
            if (options.Mode == DevLatencyMode.Off)
            {
                return "not applied at all (" + DevLatencyOptions.LatencyArgument + " "
                    + DevLatencyOptions.OffValue + "): this process delays nothing and drops "
                    + "nothing, so what a measurement taken now contains is the link's own "
                    + "latency and loss";
            }

            string source;
            if (options.Mode != DevLatencyMode.Override)
                source = "NetConfig's numbers";
            else if (options.HasLossPercent)
                source = "the numbers " + DevLatencyOptions.LatencyArgument + " named";
            else
                source = "the round trip " + DevLatencyOptions.LatencyArgument
                    + " named, with NetConfig's loss";

            string applied = string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} ms RTT ({2} ms one-way), {3}% loss per direction",
                source, stats.LatencySimRttMs, stats.LatencySimOneWayMs,
                stats.LatencySimLossPercent);

            // An applied simulator with both knobs at zero is ENABLED and
            // inert (FishNet's own CanSimulate, LatencySimulator.cs:46). That
            // is a different state from `off` above — the transport's enabled
            // bit is where they differ — and saying so here is what keeps the
            // console able to tell them apart, since the overlay prints both
            // as "off" (DevOverlay.cs:301-304).
            return stats.LatencySimActive
                ? applied
                : applied + " — applied and inert, so nothing is being simulated";
        }

        /// The apply itself, told the two numbers instead of where to read
        /// them (app-ck7). THE ONE PLACE AN RTT IS HALVED, A PERCENTAGE IS
        /// BOUNDED AND THE SIMULATOR IS ENABLED — the asset's numbers and the
        /// command line's both arrive here, which is what makes
        /// `-ring-latency 5000/250` clamp exactly as an owner-entered 5000/250
        /// in the .asset does (project rule 2, and the reason
        /// DevLatencyOptions deliberately clamps nothing on its side).
        ///
        /// `rttMs` is ROUND-TRIP milliseconds and `lossPercent` is percent per
        /// direction — the units NetConfig, the switch, the overlay and
        /// Critical Rule 7 all state, so nothing on the way in has to be
        /// converted by a caller and no caller can convert twice.
        static void ApplyNumbers(LatencySimulator simulator, NetStats stats, int rttMs,
            float lossPercent)
        {
            int oneWayMs = OneWayLatencyMs(rttMs);
            double lossFraction = ClampedLossFraction(lossPercent);

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

            Record(stats, active, oneWayMs, lossFraction);
        }

        /// The four applied facts, written in ONE place (app-ck7) — both the
        /// applied path above and the `Off` branch that applies nothing come
        /// through here, so "RttMs == OneWayMs * 2" is an invariant of the
        /// type rather than of two call sites remembering to agree.
        ///
        /// Fix-round 1, I-3 (coordinator decision, variant a): NetStats
        /// stores the APPLIED facts, not a copy of the raw NetConfig knobs.
        /// This keeps "OneWayMs == RttMs / 2" true across the WHOLE input
        /// domain, including hostile ones — a negative NetConfig.
        /// LatencySimRttMs reads back as stats.LatencySimRttMs == 0, it never
        /// leaks through as -80. For every well-behaved input the two are
        /// indistinguishable from the raw knob (RttMs = oneWayMs * 2
        /// collapses back to the original even RTT), so this changes nothing
        /// observable at CR 7's own numbers (80 -> 40 -> 80). A simulator
        /// that was never applied records zeros for the same reason, and not
        /// the numbers an earlier launch or an earlier match left behind:
        /// what these fields answer is "what is on the wire", and the Task 48
        /// overlay is the one asking.
        static void Record(NetStats stats, bool active, int oneWayMs, double lossFraction)
        {
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
        /// RenderClock.SlewFractionOf, RenderClock.cs:414-420).
        static double ClampedLossFraction(float lossPercent)
        {
            if (!(lossPercent > 0f)) return 0d;
            double fraction = lossPercent / 100.0;
            return fraction > 1d ? 1d : fraction;
        }
    }
}
#endif
