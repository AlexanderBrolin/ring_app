using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// The silhouette height gate, shared by every damageable body (Task 6) and
    /// by the barrier: a column grown by the round's own radius at both ends.
    /// It takes the bare heights rather than a config struct — one body, every
    /// caller.
    ///
    /// ⚠ THE CLASS IS NO LONGER THE ARITHMETIC OF A THREE-BAND COLUMN
    /// (app-88jb Т15). `Classify` and `MultFor` — the pair that read a zone and
    /// a multiplier off six scalars — were deleted together with those scalars;
    /// what a shot lands on is decided by HitVolumes.Resolve over the capsules,
    /// and the survivor here is the gate below, which knows nothing of parts.
    ///
    /// ⚠ THE CLASS IS ALSO NO LONGER THE HOME OF A BODY'S CROWN (app-94sk T2):
    /// `StackTop` moved to HitParts.RestCrown with the rest of the questions
    /// asked about the parts array. ⛔ AND `Resolve` IS GONE (app-94sk T3): the
    /// capsules of HitVolumes answer that question now, and this class is down
    /// to the one thing that was never about parts at all -- the height gate
    /// over a COLUMN, which the barrier, the floor and HitVolumes itself share.
    ///
    /// Internal on purpose: nothing outside the simulation assembly needs to
    /// re-derive a zone. Presentation reads the resolved `SimEvent.Zone`.
    internal static class HitZones
    {
        /// Does the projectile's height span over its chord through the target
        /// overlap the target's column? The column is [0, top] grown by the
        /// projectile's own `radius` at both ends, so a round is a sphere against
        /// a capsule-ish body rather than a dimensionless point: it connects while
        /// any part of it is inside, and clears the target only once all of it is
        /// past the crown (or under the feet).
        ///
        /// `hEnter`/`hExit` come from the clipped sweep interval, so this is an
        /// interval-vs-interval test — a descending shot that is above the crown
        /// on entry but inside the body on exit still counts as a hit.
        public static bool Overlaps(float hEnter, float hExit, float radius, float top)
        {
            float lo = math.min(hEnter, hExit);
            float hi = math.max(hEnter, hExit);
            return hi >= -radius && lo <= top + radius;
        }
    }
}
