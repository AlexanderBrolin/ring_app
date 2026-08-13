using UnityEngine;

namespace Ring.Data
{
    /// Network protocol tuning numbers (Stage 2 Task 23, spec §3.8/§3.15,
    /// Р52): tick rate, interpolation/reconciliation windows, the snapshot
    /// and event budget, join/match timers and the dev latency simulator.
    /// NetConfig NEVER enters SimConfig, SimConfigBuilder.Build or
    /// SimConfigHash — Р52. Reason: SimConfigHash is the balance-parity
    /// handshake (Task 39); if NetConfig rode inside it, retuning
    /// LatencySimRttMs (a dev/deploy knob, not a balance number) would break
    /// a match on a hash mismatch even though nothing about gameplay
    /// balance changed. Tasks 24-29 read these fields as their own
    /// defaults, which is why this class ships first in phase Ф6.
    ///
    /// LatencySimRttMs stores the ROUND-TRIP time; the transport's own knob
    /// is ONE-WAY. FishNet's LatencySimulator applies its _latency once per
    /// direction (LatencySimulator.cs:245-248/:286, called from
    /// TransportManager.cs:697 "to client" and :772 "to server"), so
    /// RTT = 2 x Latency and Task 33 must hand the transport HALF of this
    /// field (Р107). The tooltip on that field claiming "when acting as
    /// host this value will be doubled" (LatencySimulator.cs:84) is FALSE —
    /// no code path multiplies it, and _simulateHost is a boolean "simulate
    /// clientHost at all" switch, not a multiplier (Task 2 note §8). Loss is
    /// one-way for the same reason: LatencySimLossPercent is the
    /// PER-DIRECTION percentage the owner tunes, and 5% here compounds to
    /// 1 - 0.95^2 = 9.75% round-trip.
    ///
    /// SnapshotMaxBytes defaults to 1000, below the 1282-byte Inspector
    /// ceiling. Those are two different transport numbers and must not be
    /// conflated (Р101, Task 2 note §3): Tugboat.GetMTU() returns
    /// 1282 = MAXIMUM_UDP_MTU (1350) - NetConstants.MaxUdpHeaderSize (68)
    /// for every channel, while the socket's own size check runs against
    /// the raw 1350. Task 41's NetInvariants asks the transport via
    /// GetMTU() at run time rather than trusting either literal. The gap
    /// below our cap is deliberate: FishNet silently upgrades an oversized
    /// Unreliable send to Reliable instead of dropping the packet, which
    /// would quietly break the "state travels unreliably" model this
    /// project relies on. The cap is OURS, not the transport's, and what it
    /// actually squeezes at the shipped numbers is the EVENT budget, never
    /// the entity list: Task 28 measured the worst case at 1180 B (spec §6i
    /// Р146) with mobs fitting the remainder outright (956 B of record room
    /// after the 44 B fixed part, 864 B of mob records needed), so entity
    /// truncation is unreachable at the defaults — events
    /// defer and re-ride instead (Р61), and the truncation branch stays
    /// tested through a fixture cap (§6i Р147). Either way the frame never
    /// reaches the transport oversized, which is the point of the gap.
    ///
    /// [Range] attributes below are Inspector hints only. The real
    /// cross-config checks (e.g. GhostConfirmTicks > InterpBufferTicks) live
    /// in NetInvariants (Task 41, Р72) — the one place that sees both
    /// NetConfig and SimConfig at once; SimConfigBuilder never sees
    /// NetConfig, so no cross-check belongs here.
    [CreateAssetMenu(menuName = "Ring/Net Config", fileName = "NetConfig")]
    public sealed class NetConfig : ScriptableObject
    {
        // Mirrors TimeManager._tickRate (Task 2 note §6) — FishNet's own
        // tick rate, not a separate simulation clock.
        [Range(1, 240)] public int TickRate = 30;

        // Stored as an integer literal by design (Р76) instead of being
        // derived from a float expression at run time. FACT CORRECTION
        // (Stage 2 Task 23 fix-round): Р76, spec §3.7 and the plan all
        // justify this with "ceil(0.1f / (1f / 30f)) yields 4 in float" —
        // that arithmetic is wrong. 0.1f / (1f / 30f) rounds to exactly
        // 3.0f (the exact quotient is 3 - 1.12e-7, inside half a ULP of 3),
        // so ceil gives 3, not 4; the 4 only appears for the DIFFERENT
        // expression 0.1f * 30 evaluated in double (3.0000000447). The
        // decision itself is unaffected — an interpolation window is a tick
        // count the owner tunes, not a quantity to re-derive at run time.
        // > 0 is required by NetInvariants (Task 41).
        [Range(1, 10)] public int InterpBufferTicks = 3;

        // Ticks an entity may go without a fresh snapshot before it freezes
        // and then fades (spec §3.9, Р39/Р77 — consumed by StalePolicy,
        // Task 37). Default 3 = 100 ms at 30 Hz; the Range ceiling of 30 is
        // one full second.
        [Range(1, 30)] public int InterpMaxStaleTicks = 3;

        // Render-clock drift tolerated before a hard snap instead of a slew
        // (spec §3.9, Р57 — consumed by RenderClock, Task 31). Default 10 =
        // 333 ms at 30 Hz; the Range ceiling of 60 is two seconds.
        [Range(1, 60)] public int RenderClockSnapTicks = 10;

        // Stage 2 Task 41 (spec §3.9, Р154): how far the render clock's rate
        // may deviate from real time while it works off a desync — it runs at
        // 1 ± SlewFraction instead of jumping. Sits next to
        // RenderClockSnapTicks because the two are the same clock's two
        // correction modes, and it is a FIELD rather than a code constant for
        // the same reason its three neighbors are: NetTimings' other numbers
        // all come off this asset, and a fourth one living in code would be an
        // exception without a cause. It is also a taste knob, and taste is
        // tuned by playtest in the .asset, not by recompiling.
        //
        // 0.05 IS THE LOW EDGE OF SPEC §3.9's BAND, NOT ITS MIDDLE. §3.9 asks
        // for "slew of +/-5-10% of clock speed", i.e. 0.05..0.10, whose middle
        // would be 0.075. Shipping the low edge is the choice: it is the
        // gentlest legal correction, and a correction that is too soft merely
        // takes longer to work off a desync, whereas one that is too strong
        // shows up as a visible change of pace in animation. Raising it is a
        // matter of taste and belongs to a playtest (milestone В1), which is
        // exactly why this is an asset field.
        //
        // Do not confuse §3.9's TUNING band (0.05..0.10) with the band
        // NetInvariants enforces ([0, 0.10]) — the invariant additionally
        // admits 0, and 0.05 IS the midpoint of that wider band, which is
        // where an earlier draft of this comment got "middle" from.
        //
        // THE DEFAULT MATTERS MORE THAN USUAL: default(float) is 0, and
        // RenderClock.SlewFractionOf reads anything at or below zero as "do
        // not correct at all", so shipping the C# zero would install the
        // feature switched off. Zero stays LEGAL (NetInvariants accepts it)
        // because switching slew off deliberately is a mode, not a mistake —
        // it is only the DEFAULT that must not be 0.
        [Range(0f, 0.10f)] public float SlewFraction = 0.05f;

        // Ticks an event stays redundantly re-sent before being dropped.
        // 0 disables redundancy entirely (an event is sent exactly once).
        [Range(0, 15)] public int EventRedundancyTicks = 4;

        // Max events packed into one snapshot; > 0 requires NetInvariants.
        [Range(1, 128)] public int SnapshotEventBudget = 16;

        // See the class doc above for why 1000, not the 1282 MTU ceiling.
        [Range(256, 1282)] public int SnapshotMaxBytes = 1000;

        // > InterpBufferTicks requires NetInvariants (Task 41).
        [Range(1, 60)] public int GhostConfirmTicks = 12;

        // Above this, a reconciliation snap reads as a teleport rather than
        // a correction.
        [Range(0.1f, 10f)] public float ReconcileSnapMeters = 1.0f;

        // ~330 ms at 30 Hz (spec §3.7 Р25): ticks without input before a
        // player's input is considered starved.
        [Range(1, 60)] public int InputStarveTicks = 10;

        // Round-trip time, milliseconds — see class doc above: this is
        // RTT, the transport gets half of it (Task 33, Р107).
        [Range(0, 1000)] public int LatencySimRttMs = 80;

        // Percent PER DIRECTION — 5% here compounds to ~9.75% round-trip
        // (Р107), not 10%.
        [Range(0f, 100f)] public float LatencySimLossPercent = 5f;

        [Range(10, 600)] public int JoinTimeoutSeconds = 120;

        [Range(0, 60)] public int MatchEndLingerSeconds = 10;

        [Range(60, 7200)] public int MatchMaxDurationSeconds = 1800;

        // Stage 2 Task 41 (spec §3.10; Task 40 brief §2.7): once EVERY client
        // has disconnected, the server process waits this long before exiting
        // with code 0. A process watchdog, not a match rule — the code that
        // reads it is ServerBootstrap — but the number belongs beside
        // JoinTimeoutSeconds and MatchEndLingerSeconds in the asset rather
        // than as a literal in the bootstrap, by the same rule that put those
        // two here (Critical Rule 6).
        [Range(5, 300)] public int MatchAbandonGraceSeconds = 30;

        // Stage 2 Task 42a (spec §3.10 :673-678, Р70): the minimum interval,
        // in seconds, between two ACCEPTED spectate-target switches by the
        // SAME dead player. Server-side by rule, not merely by convenience —
        // the visibility set a spectator receives is computed from the
        // TARGET's own position, so a client cycling through targets with no
        // limit would sample one living player's visibility set after
        // another in a matter of seconds and reconstruct the whole map from
        // their union, defeating the entire reason fog of war exists.
        // `SpectatePolicy` (Ring.Networking.Server) is the class that
        // actually enforces this; `ServerBootstrap` converts the seconds
        // below into world ticks (`Ceiling(seconds * TickRate)`, rounded UP
        // so the enforced interval is never SHORTER than configured) because
        // the policy itself works in ticks — the same domain
        // `SimulationWorld.CurrentTick` does, and the one
        // `MatchEndPolicy`'s own constructor already reads its limit in.
        //
        [Range(0.05f, 2f)] public float SpectatorSwitchCooldownSeconds = 0.35f;

        // Stage 2 Task 47c (spec §3.9, Р39/Р77): how many render ticks a
        // stranger's doll takes to fade out once it is eligible — StalePolicy's
        // own `fadeTicks`, the budget its FadeProgress reports the spent
        // fraction of. In TICKS, like InterpMaxStaleTicks above and for the same
        // reason: the policy counts in ticks, and a second seconds-to-ticks
        // conversion would be a second answer to a question that already has
        // one. Default 15 = 500 ms at 30 Hz — long enough to read as a fade
        // rather than a blink, short enough that a departed stranger does not
        // linger past the moment the player stops believing in them; the Range
        // ceiling of 60 is two seconds. > 0 is required by NetInvariants
        // (#9) — see there for why the floor is enforced and no ceiling is.
        //
        // IT BECAME A FIELD ONLY WHEN A READER FOR IT DID. Task 37 wrote the
        // policy and Task 44 fed it, but nothing read StateOf/FadeProgress, so
        // the number lived as a NetworkSimBackend constant whose own doc said
        // an asset field would be "a number nobody could see the effect of
        // tuning" (CR 6 is about numbers the game plays by). Task 47c is the
        // reader — ViewRegistry holds a doll the frame has gone silent about and
        // dims it by this budget — so the constant moved here, unchanged at 15.
        //
        // MARKER FIELD. The backfill mechanism is
        // EditorBootstrapUtils.EnsureAssetHasKey(so, path, markerField),
        // which is a text search for the marker's name in the committed YAML:
        // if the marker names a field the .asset already carries, the search
        // succeeds, nothing is dirtied, and NO new key is ever written. So the
        // marker has to be the LAST field added, and the call site has to name
        // THIS field — Task 47c moved it here, in the same commit that added
        // this field. The chain so far: MatchMaxDurationSeconds (Task 23, the
        // first join) -> MatchAbandonGraceSeconds (Task 41b) ->
        // SpectatorSwitchCooldownSeconds (Task 42a) -> here. Each predecessor's
        // own field comment stops mentioning the mechanism once the marker
        // leaves it, so exactly one field in this class ever claims to be it.
        [Range(1, 60)] public int EntityFadeTicks = 15; // sync-marker key — keep LAST

#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
