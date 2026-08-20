using System;
using System.Collections.Generic;
using Ring.Data;
using Ring.Simulation.Core;

namespace Ring.Networking
{
    /// Stage 2 Task 41 (spec §3.15 validation-homes table, §3.9, Р72/Р154;
    /// plan Т41). The cross-config gate: the ONE place in the project that
    /// sees `NetConfig` and `SimConfig` at the same moment, and therefore the
    /// only place several of the rules below can be stated at all.
    ///
    /// WHY THIS CLASS EXISTS AT ALL — the `LingerTicks` rule. `LingerTicks`
    /// lives in `VisibilityConfig`, which is part of `SimConfig`;
    /// `InterpBufferTicks` lives in `NetConfig`, which by Р52 is deliberately
    /// NOT part of `SimConfig` (it would drag deploy knobs like
    /// `LatencySimRttMs` into the balance-parity hash). `SimConfigBuilder`
    /// therefore cannot see both, and says so in its own Visibility block.
    /// Without this class the rule has no home and simply is not checked.
    ///
    /// A REFUSAL IS A VALUE, NOT AN EXCEPTION (lesson 115, the precedent being
    /// `MatchConfigLoader`). The plan's wording for Т41 is "violation → log
    /// and exit code 2": the caller has to turn the answer into a diagnosable
    /// stdout line and an OS exit code, and a throw from the middle of a
    /// headless boot would hand it a stack trace instead. This is the opposite
    /// choice from the neighboring `SimConfigBuilder.Validate`, which
    /// accumulates into a `List&lt;string&gt;` and then throws — deliberately
    /// so, because that one runs inside an Editor/test call chain where an
    /// exception IS the diagnosis.
    ///
    /// EVERY VIOLATION IS COLLECTED, NEVER JUST THE FIRST. An operator raising
    /// a server whose config has two numbers out of step has to learn about
    /// both from one run; stopping at the first would mean one restart per
    /// mistake.
    ///
    /// `null` `NetConfig` THROWS, and that is not a contradiction of the
    /// paragraph above: a missing asset is broken WIRING, not a bad
    /// configuration. There is no value to report on, so there is nothing the
    /// caller could print and act on.
    ///
    /// FIELD NAMES IN MESSAGES FOLLOW `SimConfigBuilder`'s convention
    /// ("Hero.MaxSpeed", "Visibility.SightRadius"), with `NetConfig`'s own
    /// fields prefixed `Net.`. Every message names the field it is about AND
    /// both numbers being compared — a message that only says "invalid
    /// config" costs the reader the debugging session this class exists to
    /// prevent.
    ///
    /// THESE ARE NOT DUPLICATES OF `[Range]` (Р115). The attributes on
    /// `NetConfig` are Inspector hints and are enforced nowhere: a value
    /// arriving from code, from a test fixture or from a hand-edited YAML has
    /// never passed through a slider. The rules below are also mostly
    /// RELATIONAL — no attribute can express "greater than that other field"
    /// — and the one that looks absolute, `SlewFraction`'s band, is here
    /// because §3.9 states the band and nothing enforces it at run time. Do
    /// not delete any of them as redundant with the attributes.
    public static class NetInvariants
    {
        /// Bytes FishNet spends per snapshot BETWEEN our payload and the MTU
        /// the transport reports, worst case. Derived from the pinned package
        /// (`Library/PackageCache/com.firstgeargames.fishnet@0728292d8339`),
        /// term by term — not estimated:
        ///
        ///   2  `PacketId.Broadcast`, written unpacked by
        ///      `BroadcastsSerializers.WriteBroadcast`
        ///      (Runtime/Serializing/Helping/Broadcasts.cs:18 →
        ///      Writer.cs:192-195 `WriteUInt16Unpacked`); the width is named
        ///      by `TransportManager.PACKETID_LENGTH = 2`
        ///      (Runtime/Managing/Transporting/TransportManager.cs:144).
        ///   2  the broadcast type key, `typeof(T).FullName.GetStableHashU16()`
        ///      (Broadcasts.cs:19 → Writer.cs:371 → :357, unpacked u16).
        ///   2  the length of the serialized broadcast, written packed
        ///      (Broadcasts.cs:24 `WriteInt32` → Writer.cs:399
        ///      `WriteSignedPackedWhole` → :1132 zig-zag → :1142-1153, seven
        ///      bits per byte). Two bytes covers every length below 8192,
        ///      i.e. the whole band `SnapshotMaxBytes` can legally take.
        ///   5  `SnapshotBroadcast.Tick`, a `uint` written packed
        ///      (Writer.cs:427-428 → :1142). Three bytes at any tick this
        ///      project can reach; five is the type's ceiling and is used
        ///      because a bound has to be a bound.
        ///   2  `SnapshotBroadcast.MatchEpoch`, an unpacked `ushort`
        ///      (Writer.cs:370-371 → :357).
        ///   2  the `Payload` length prefix — `ArraySegment&lt;byte&gt;`'s
        ///      default writer is `WriteArraySegmentAndSize`
        ///      (Writer.cs:550-551 → :292/:300), same packed encoding as above.
        ///   4  the tick stamped at the head of every datagram buffer:
        ///      `TransportManager.UNPACKED_TICK_LENGTH = 4`
        ///      (TransportManager.cs:156), reserved in `PacketBundle`'s
        ///      constructor (Runtime/Connection/Buffer.cs:151) and written by
        ///      `ByteBuffer.CopySegment` (Buffer.cs:76-80).
        ///   2  FishNet's own MTU reserve, which `TransportManager.
        ///      GetMTUWithReserve` subtracts from the transport's number
        ///      (TransportManager.cs:346-348) as `MINIMUM_MTU_RESERVE` (= 1,
        ///      :177) plus `_customMtuReserve` (defaulted to the same 1,
        ///      :130). `Tugboat.GetMTU` does NOT apply it, so a caller passing
        ///      the transport's raw number would otherwise overstate the
        ///      budget by exactly these two bytes.
        ///   = 21.
        ///
        /// WHAT THE CAP ACTUALLY BUYS. `TransportManager.SendSplitMessage`
        /// compares the whole serialized broadcast against the channel's MTU
        /// and, if it is larger, splits it and forces the pieces onto
        /// `Channel.Reliable` (TransportManager.cs:576-627, the branch at
        /// :582-584 being the "no split needed" one). An oversized snapshot is
        /// therefore not dropped — it quietly stops being unreliable, which
        /// breaks the "state travels unreliably, events ride redundantly"
        /// model the whole protocol is built on. That silent mode change, not
        /// a packet loss, is what invariant #5 is here to prevent.
        ///
        /// THE CONDITION THIS CHECK IS HONEST UNDER: ONE SNAPSHOT TRAVELS AS
        /// ONE BROADCAST MESSAGE. `SendSplitMessage` decides per MESSAGE, on
        /// the segment handed to it, and it decides BEFORE any bundling; the
        /// bundling underneath never changes that verdict, because
        /// `PacketBundle.Write` responds to a full buffer by opening a NEW
        /// buffer on the SAME channel (Buffer.cs:243-257) and never by
        /// switching channels. So a per-message budget is the right budget —
        /// as long as a message carries one snapshot. A future sender that
        /// coalesced two ticks into a single `SnapshotBroadcast` would put the
        /// SUM in front of the per-message comparison, and this constant would
        /// then be measuring the wrong thing. Treat that as a precondition of
        /// invariant #5, not as a passing observation.
        ///
        /// PRECISION, STATED SO NOBODY "OPTIMIZES" IT LATER. Against a raw
        /// transport MTU of `raw`, the envelope alone is 15 bytes, and the
        /// channel MTU the split check uses is `raw - 2`, so the actual
        /// upgrade-to-Reliable threshold is `N > raw - 17`. This constant is
        /// 21, i.e. deliberately 4 bytes stricter — exactly the datagram tick
        /// — and that difference is the point rather than slack: in the band
        /// `raw - 21 &lt; N &lt;= raw - 17` the message escapes the split, stays
        /// Unreliable, and still produces a datagram longer than the nominal
        /// buffer size (`ByteBuffer.Size` is that same `raw - 2`, and
        /// `PacketBundle.Write` skips its capacity test when the buffer is
        /// empty). `N &lt;= raw - 21` is therefore the exact boundary of "one
        /// snapshot fits one clean datagram", which is the property worth
        /// holding. At the shipped numbers: raw 1282, upgrade at N &gt; 1265,
        /// this cap at N &lt;= 1261, default `SnapshotMaxBytes` 1000.
        ///
        /// WITHOUT THE DEDUCTION THIS CHECK WOULD BE THEATRE:
        /// `Tugboat.GetMTU(byte channel)` ignores its channel argument and
        /// always returns `MAXIMUM_UDP_MTU - NetConstants.MaxUdpHeaderSize`
        /// = 1350 - 68 = 1282 (Tugboat.cs:581-583, NetConstants.cs:49) — which
        /// is exactly the ceiling `NetConfig.SnapshotMaxBytes` already carries
        /// on its `[Range]`. Comparing the two bare numbers could only ever
        /// fail on a value the Inspector cannot even produce.
        public const int SnapshotWireOverheadBytes = 21;

        /// How far `NetConfig.TickRate` may sit from the rate
        /// `SimulationWorld.TickDt` actually denotes. See `Validate` for why
        /// this is a tolerance rather than an equality.
        const double TickRateTolerance = 1e-3;

        /// Spec §3.9 states the render clock's slew as "±5-10% of clock
        /// speed"; this is the upper end. The lower end is 0 and is legal —
        /// see `Validate`.
        const float MaxSlewFraction = 0.10f;

        /// Answers with one message per violated invariant, in a fixed order,
        /// or an empty array when everything holds. Never null.
        ///
        /// `transportMtu` is what the TRANSPORT reports for the snapshot
        /// channel (`Tugboat.GetMTU`), NOT what `TransportManager.
        /// GetLowestMTU` reports: `SnapshotWireOverheadBytes` already contains
        /// FishNet's own MTU reserve, so handing in the already-reduced number
        /// merely makes the check two bytes stricter than it needs to be,
        /// while handing in the raw number keeps it exact. It arrives as a
        /// plain `int` on purpose — this class must stay drivable in EditMode
        /// with no `NetworkManager`, no scene and no engine loop, which is the
        /// same reason `NetTimings` carries values rather than the asset.
        ///
        /// `timeManagerTickRate` (Ф8 gate W-1) is the tick rate FishNet's own
        /// `TimeManager` actually carries on THIS scene (`TimeManager.
        /// TickRate`, a `ushort`, widened to `int` for the same
        /// no-NetworkManager-required reason `transportMtu` is a plain `int`
        /// above) — see invariant #8 below for why a fifth tick-rate home
        /// needed a check of its own.
        public static string[] Validate(NetConfig net, in SimConfig sim, int transportMtu,
            int timeManagerTickRate)
        {
            if (net == null)
                throw new ArgumentNullException(nameof(net));

            var errors = new List<string>();

            // #1 (plan Т41). A zero-tick interpolation buffer is not a
            // "tighter" setting — it removes the buffer, so every lost
            // datagram becomes a visible freeze instead of being absorbed.
            if (net.InterpBufferTicks <= 0)
            {
                errors.Add("Net.InterpBufferTicks must be > 0 " +
                    $"(got InterpBufferTicks={net.InterpBufferTicks}).");
            }

            // #2 (plan Т41). A zero event budget packs no events into any
            // snapshot ever, and the deferral machinery (Р61) would re-defer
            // the same events forever without a single one going out.
            if (net.SnapshotEventBudget <= 0)
            {
                errors.Add("Net.SnapshotEventBudget must be > 0 " +
                    $"(got SnapshotEventBudget={net.SnapshotEventBudget}).");
            }

            // #3 (Р72, plan Т41). A predicted tracer must outlive the wait for
            // its own server confirmation. As long as the client renders
            // InterpBufferTicks behind the newest buffered frame — the
            // contract Т31's render clock and Т32's receive path hold, not
            // something this file can check — the confirmation cannot arrive
            // sooner than that depth, so a ghost expiring at or before it dies
            // before its own confirmation could land. Strictly greater, not >=.
            if (net.GhostConfirmTicks <= net.InterpBufferTicks)
            {
                errors.Add("Net.GhostConfirmTicks must be > Net.InterpBufferTicks " +
                    $"(got GhostConfirmTicks={net.GhostConfirmTicks}, " +
                    $"InterpBufferTicks={net.InterpBufferTicks}).");
            }

            // #4 (Р72 — the reason this class exists, see the type doc). The
            // server must keep an entity on the wire for at least as long as
            // the client is still interpolating through it: the client renders
            // InterpBufferTicks behind the newest frame, and the queue is two
            // ticks deeper than that (Р37). Drop it sooner and entities
            // vanish off a timeline the client has not reached yet.
            if (sim.Visibility.LingerTicks < net.InterpBufferTicks + 2)
            {
                errors.Add("Visibility.LingerTicks must be >= Net.InterpBufferTicks + 2 " +
                    $"(got LingerTicks={sim.Visibility.LingerTicks}, " +
                    $"InterpBufferTicks={net.InterpBufferTicks}).");
            }

            // #5 (plan Т41). See SnapshotWireOverheadBytes for the derivation
            // and for what going over actually costs.
            int snapshotBudget = transportMtu - SnapshotWireOverheadBytes;
            if (net.SnapshotMaxBytes > snapshotBudget)
            {
                errors.Add("Net.SnapshotMaxBytes must be <= transportMtu - " +
                    $"{SnapshotWireOverheadBytes} (got SnapshotMaxBytes={net.SnapshotMaxBytes}, " +
                    $"transportMtu={transportMtu}, budget={snapshotBudget}).");
            }

            // #6. NetConfig.TickRate is FishNet's tick rate; SimulationWorld.
            // TickDt is the world's fixed step, and ADR-002 T5 makes it the
            // single source of the 30 Hz rate. Two numbers, one truth.
            //
            // WHY A TOLERANCE AND NOT AN EQUALITY. `TickDt` is a float, so
            // `TickRate * TickDt == 1f` is not a usable test: 1f/30f is
            // 0.0333333351..., and 1.0 / that is 29.99999843..., not 30. The
            // check below is instead "TickRate names the rate TickDt denotes,
            // to within a thousandth of a tick per second". That threshold is
            // chosen, not guessed, and it is safe at both ends:
            //   * float rounding can move 1.0/(1f/N) away from N by at most
            //     about N * 2^-24, which at NetConfig's own [Range] ceiling of
            //     240 is 1.4e-5 — nearly two orders of magnitude below the
            //     tolerance, so a legitimate 1f/N can never trip it;
            //   * every disagreement worth catching misses by orders of
            //     magnitude more than that. The realistic mistakes are large:
            //     TickRate 29 against 1f/30f misses by ~1.0, TickRate 60 by
            //     ~30. Near misses stay caught down to a disagreement of about
            //     a thousandth of a tick per second, and no closer: anything
            //     inside that is, by construction, not a disagreement this
            //     check makes. A TickDt of 1f/29.6f leaves ~0.400 and 1f/29.9f
            //     only ~0.100 — still a
            //     hundred times the tolerance, but the earlier claim that any
            //     non-whole rate misses "by 0.4 or more" was a false
            //     generalization and is not what makes this safe. What makes
            //     it safe is the gap between 1e-3 and the 1.4e-5 worst-case
            //     float error above.
            // A "nearest whole number" form (round 1.0/TickDt, compare) was
            // considered and is weaker: it would accept TickRate = 30 against
            // a TickDt of 1f/29.6f, which is a real disagreement.
            double worldTicksPerSecond = 1.0 / SimulationWorld.TickDt;
            if (Math.Abs(net.TickRate - worldTicksPerSecond) > TickRateTolerance)
            {
                errors.Add("Net.TickRate must match the rate SimulationWorld.TickDt denotes " +
                    $"(got TickRate={net.TickRate}, TickDt={SimulationWorld.TickDt} " +
                    $"=> {worldTicksPerSecond:F6} ticks/second).");
            }

            // #7 (Р154, spec §3.9). ZERO IS LEGAL and means "do not slew" —
            // RenderClock.SlewFractionOf reads anything at or below zero that
            // way, and switching the correction off deliberately is a mode.
            // Negative is not that mode, it is a value no band admits, and
            // above the band the change of pace becomes visible in animation.
            // `!(x >= 0f)` rather than `x < 0f` so that NaN, which passes both
            // ordinary comparisons, is refused too.
            if (!(net.SlewFraction >= 0f) || net.SlewFraction > MaxSlewFraction)
            {
                errors.Add($"Net.SlewFraction must be in [0, {MaxSlewFraction}] " +
                    $"(got SlewFraction={net.SlewFraction}).");
            }

            // #8 (Ф8 gate W-1). A DIFFERENT agreement from #6 above: #6 pins
            // `NetConfig.TickRate` against `SimulationWorld.TickDt` (the
            // WORLD's own fixed step); this one pins it against the rate
            // FishNet's `TimeManager` is actually configured to tick the
            // SCENE at. Before this invariant existed, the two could disagree
            // with #6 fully satisfied: ticks are produced by `TimeManager`,
            // but every seconds-to-ticks conversion this project makes
            // (`ServerBootstrap`'s match-duration/join/spectate-cooldown
            // timers) reads `NetConfig.TickRate` — so a scene left on the old
            // rate after a retune would silently run every one of those
            // timers at the wrong pace while every existing test stayed
            // green (no test built a scene). Exact equality, not a
            // tolerance: both sides are integers (`TimeManager.TickRate` is a
            // `ushort`), so there is no float-rounding case to admit the way
            // #6 has to.
            if (net.TickRate != timeManagerTickRate)
            {
                errors.Add("Net.TickRate must equal the scene's TimeManager.TickRate " +
                    $"(got Net.TickRate={net.TickRate}, TimeManager.TickRate={timeManagerTickRate}).");
            }

            // #9 (Stage 2 Task 47c). A fade has to last at least one tick to be
            // a fade at all. `StalePolicy` already CLAMPS its own `fadeTicks` up
            // to 1 (its fix-round-1 finding I-1, which floored it there rather
            // than at 0 so that every `Gone` transition goes through `Advance`
            // and honors the starvation and truncation guards) — so a
            // zero-or-negative number here is not refused downstream, it is
            // silently retuned, and the operator who wrote it never learns the
            // asset said something nothing honored. Catching it here is what
            // turns a quiet disagreement between the asset and the policy into
            // a line the reader can act on.
            //
            // NO CEILING, DELIBERATELY, unlike #7's band. `SlewFraction` has an
            // upper rule because spec §3.9 STATES a band and nothing enforces it
            // at run time; there is no spec band for a fade duration and no
            // threshold past which anything breaks — a long fade holds a
            // stranger's doll in the registry for longer, which the pool is
            // sized for either way (`ViewRegistry` allocates twice the roster),
            // and how long a fade should read as is taste, settled by playtest
            // at milestone В1. Adding a ceiling here would be the duplicate of
            // the `[Range]` hint this class's own type doc refuses to become.
            if (net.EntityFadeTicks <= 0)
            {
                errors.Add("Net.EntityFadeTicks must be > 0 " +
                    $"(got EntityFadeTicks={net.EntityFadeTicks}).");
            }

            // #10 (Stage 3, Ф5 gate review A-2; spec §3.5 Р255/Р300). THE ONE
            // CROSS-CHECK Р255 LEFT STANDING after it deleted the second
            // duration number, and the one nothing implemented until this
            // gate. Its home is here because Р72 says only the nodes that see
            // BOTH configs may state a rule spanning them, and this validator
            // is exactly that node — `ServerBootstrap` and `NetworkSimBackend`
            // are its only callers.
            //
            // WHAT IT REFUSES IS A CONFIGURATION NOBODY CAN WIN, NOT A LATE
            // GAMBLE. Р300 is explicit that a collector who enters the core
            // too late is taking a risk the validator must NOT second-guess —
            // that is the loop working. This is the other thing: if the
            // sharing window plus the extraction channel do not FIT inside
            // the raid at all, the Director could fall on tick one and the
            // gate would still open too late to walk through, so the core
            // route is dead for every player in every raid the build ever
            // runs. Strict `<`: at exactly equal there is no tick left to
            // stand in the gate on.
            float endgameSeconds = sim.Flow.GateDelaySeconds + sim.Flow.ExtractChannelSeconds;
            if (endgameSeconds >= net.MatchMaxDurationSeconds)
            {
                errors.Add("Flow.GateDelaySeconds + Flow.ExtractChannelSeconds must be < "
                    + "Net.MatchMaxDurationSeconds — otherwise the gate route cannot be walked "
                    + $"in ANY raid (got {sim.Flow.GateDelaySeconds} + "
                    + $"{sim.Flow.ExtractChannelSeconds} = {endgameSeconds} against "
                    + $"MatchMaxDurationSeconds={net.MatchMaxDurationSeconds}).");
            }

            return errors.ToArray();
        }
    }
}
