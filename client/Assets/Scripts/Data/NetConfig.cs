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
    /// LatencySimRttMs/LatencySimLossPercent are stored ONE-DIRECTIONAL at
    /// the transport but ROUND-TRIP/per-direction here: FishNet's own
    /// LatencySimulator tooltip talks about the host doubling loss, but
    /// that doubling is a transport-side concern, not something this class
    /// reflects. This class stores the RTT and the per-direction loss
    /// percentage exactly as the owner tunes them; halving the RTT into the
    /// transport's one-way setting is Task 33's job (Р107), not this
    /// class's.
    ///
    /// SnapshotMaxBytes defaults to 1000, below the 1282-byte Inspector
    /// ceiling (Tugboat.GetMTU() 1350 minus a 68-byte header allowance,
    /// Р101). The gap is deliberate: FishNet silently upgrades an oversized
    /// Unreliable send to Reliable instead of dropping the packet, which
    /// would quietly break the "state travels unreliably" model this
    /// project relies on. The cap enforced here is OURS, not the
    /// transport's, so Task 28's snapshot truncation has real headroom to
    /// operate in before that silent channel upgrade could ever trigger.
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

        // Integer literal by design (Р76), not derived at runtime via
        // ceil(0.1f / (1f / 30f)) — that float division rounds a hair over
        // 3, so ceil pushes it to 4, a classic float-precision trap. > 0
        // requires NetInvariants (Task 41) to hold.
        [Range(1, 10)] public int InterpBufferTicks = 3;

        // One second of staleness tolerance at 30 Hz before a ghost is
        // considered stalled.
        [Range(1, 30)] public int InterpMaxStaleTicks = 3;

        // Two seconds of render-clock drift at 30 Hz before a hard snap.
        [Range(1, 60)] public int RenderClockSnapTicks = 10;

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

        [Range(60, 7200)] public int MatchMaxDurationSeconds = 1800; // sync-marker key — keep LAST

#if UNITY_EDITOR
        void OnValidate() => RingDataChanged.Raise();
#endif
    }
}
