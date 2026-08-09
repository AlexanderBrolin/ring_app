namespace Ring.Server
{
    /// Stage 2 Task 40 (spec §3.10 Р44/Р60, §6k Р163): the process's epoch
    /// counter — the one place a match's `MatchEpoch` number comes from.
    ///
    /// A PURE COUNTER, WITH NO UnityEngine AND NO FishNet, in `Ring.Server`
    /// beside `MatchRoster`/`MatchConfigLoader` — the same split every other
    /// decision of this stage uses (`InputStarvation` beside `MatchServer`,
    /// `HandshakeDecision` beside `MatchHandshake`): the rule lives in a type
    /// that can be driven in EditMode with no scene, and the wiring only calls
    /// it.
    ///
    /// THE INSTANCE BELONGS TO THE BOOTSTRAP, NOT TO `MatchServer` (Р163б).
    /// Both consumers of an epoch — `MatchServer.StartMatch` and
    /// `MatchHandshake`'s constructor — take it BY VALUE, i.e. both are
    /// RECIPIENTS; the only node above the two is the headless bootstrap (Task
    /// 41), so that is where the single instance lives and from where both are
    /// fed. A counter kept inside `MatchServer` would be a SECOND source of
    /// the very number `StartMatch` already takes as a parameter — the defect
    /// Р151 and the Task 39 ruling were both written against.
    ///
    /// ZERO IS RESERVED AND IS NEVER MINTED. The first `Mint` hands out 1,
    /// every later one the previous value plus one, and the wrap goes
    /// `65535 -> 1`, never `65535 -> 0`. `Current` reads 0 until the first
    /// mint, and that is the whole point of the reservation: `default(ushort)`
    /// — the value an unfilled wire field, a default-constructed struct or a
    /// never-reset client seam all carry — must stay distinguishable from a
    /// live epoch.
    ///
    /// THE WRAP IS SAFE BECAUSE THE CLIENT RULE IS "DIFFERENT", NOT "GREATER"
    /// (Р163). Every epoch-aware client seam compares for INEQUALITY
    /// (`RenderClock.ResetForEpoch`'s own `epoch != _epoch`, `SnapshotQueue.
    /// Admit`'s `ForeignEpoch` branch), so an epoch number that has come back
    /// around after 65535 matches is as good as any other. Nothing anywhere
    /// orders two epochs against each other.
    ///
    /// MINT BEFORE THE PORT OPENS (Р163в) — a caller obligation this class
    /// cannot check for itself. `MatchHandshake`'s constructor freezes the
    /// epoch it is given for the lifetime of that instance, and it is
    /// constructed before the first `ClientHelloNet` can arrive; so the mint
    /// has to come first, or the first accepted client is welcomed into an
    /// epoch nobody minted.
    public sealed class MatchEpochCounter
    {
        ushort _current;

        /// The epoch most recently minted — 0 before the first `Mint`, which
        /// reads as "there is no epoch yet" and is never a value this class
        /// hands out.
        public ushort Current => _current;

        /// Hands out the next epoch and remembers it as `Current`.
        public ushort Mint()
        {
            _current = _current == ushort.MaxValue ? (ushort)1 : (ushort)(_current + 1);
            return _current;
        }
    }
}
