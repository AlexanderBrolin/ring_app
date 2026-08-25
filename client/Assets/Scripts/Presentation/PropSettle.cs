using UnityEngine;

namespace Ring.Presentation
{
    /// Shared freeze-condition helper for pooled PhysX cosmetics (Task 24,
    /// QC15/PC14 — extracted out of `CasingView`, which owned this logic
    /// alone since Task 27, so `GibView` can reuse it instead of copying it):
    /// a prop should freeze (`Rigidbody.isKinematic = true`, stop paying
    /// PhysX cost) once it has actually come to rest, NOT on a bare
    /// elapsed-time check. `CasingView`'s own class doc records the exact bug
    /// a pure timer caused (app-4qc, Б1 milestone find): the floor's
    /// degenerate collider (see `GreyboxBuilder`'s class doc) sometimes
    /// launches a fresh spawn upward, and a plain "timer expired" freeze fired
    /// before the prop ever landed, pinning it mid-air. A structural hard cap
    /// (settleSeconds × HardCapMultiplier) still guarantees the PhysX
    /// cost ends even for a prop that somehow never settles (e.g. stuck
    /// oscillating in a geometry seam).
    ///
    /// One rule, two callers (`CasingView`/`GibView`) — not two copies of the
    /// same logic (Reuse &gt; duplication, AGENT.md §4). This class owns no
    /// state of its own: `elapsed` is the caller's own up-counting timer
    /// (reset to 0 in the caller's `Spawn`, advanced every `Update` by
    /// `Time.unscaledDeltaTime` same as the rest of this namespace's
    /// slow-mo-immune cosmetics — there is none, `Time.timeScale` is never
    /// touched, see `SimulationRunner`), `settleSeconds` is whatever
    /// config-sourced value the caller's own archetype uses
    /// (`GameFeelConfig.CasingPhysicsSeconds`/`GibPhysicsSeconds`) — this is a
    /// pure predicate, not a timer of its own.
    public static class PropSettle
    {
        public const float HardCapMultiplier = 4f; // structural, not feel — CasingView's pre-refactor value
        const float SettleSpeedSqr = 0.01f; // (0.1 m/s)^2 — "stopped rolling"

        /// False until `elapsed &gt;= settleSeconds` (a prop is never even
        /// checked for rest before its own minimum flight time has passed);
        /// from there on, true once the rigidbody is actually slow
        /// (`SettleSpeedSqr`) OR the hard cap (settleSeconds × HardCapMultiplier)
        /// has been reached regardless of velocity.
        public static bool ShouldFreeze(Rigidbody rb, float elapsed, float settleSeconds)
        {
            if (elapsed < settleSeconds) return false;
            return rb.linearVelocity.sqrMagnitude < SettleSpeedSqr
                || elapsed >= settleSeconds * HardCapMultiplier;
        }
    }
}
