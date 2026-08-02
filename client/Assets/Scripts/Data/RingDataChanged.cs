namespace Ring.Data
{
    /// Task 28 (spec §3.9): editor-only hot-tweak signal. Every balance/feel SO in
    /// this namespace calls `Raise()` from its own `OnValidate` (Editor-only,
    /// guarded per-class) whenever the owner edits a field in the Inspector —
    /// `SimulationRunner` is the sole production subscriber (its own subscription
    /// is itself guarded `#if UNITY_EDITOR || DEVELOPMENT_BUILD`, spec §3.9), and
    /// reacts by rebuilding `SimConfig` and calling `SimulationWorld.ApplyConfig`
    /// at the next safe tick boundary instead of requiring a full match restart.
    /// No payload — every listener just re-reads whichever SO references it
    /// already holds, same "diff nothing, just re-pull" shape as
    /// `SimulationRunner.RequestApplyConfig`'s existing pending-flag mechanism
    /// (Task 7) this event ultimately drives.
    public static class RingDataChanged
    {
        public static event System.Action Changed;

        public static void Raise() => Changed?.Invoke();
    }
}
