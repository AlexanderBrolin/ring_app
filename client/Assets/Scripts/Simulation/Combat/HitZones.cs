using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Vertical hit-volume maths shared by every damageable body (Task 6): the
    /// hero and both mob archetypes carry the same LegsTop/BodyTop/HeadTop +
    /// per-zone multiplier shape, so these are scalar helpers taking the four
    /// numbers directly rather than an overload per config struct — one body,
    /// every caller.
    ///
    /// Internal on purpose: nothing outside the simulation assembly needs to
    /// re-derive a zone. Presentation reads the resolved `SimEvent.Zone`.
    internal static class HitZones
    {
        /// Zone of a hit landing at height `h`, in half-open bands
        /// [0, legsTop) → Legs, [legsTop, bodyTop) → Body, [bodyTop, headTop] → Head.
        ///
        /// `h` is CLAMPED into [0, headTop] first, which is what makes the edges
        /// forgiving: a round that only grazes the crown (h slightly above
        /// headTop, still inside the projectile's own radius — see Overlaps)
        /// reads as Head rather than falling off the table, and one that cuts in
        /// just under the floor line reads as Legs. Clamping and hit/no-hit are
        /// deliberately separate decisions: Overlaps below decides IF the shot
        /// connects at all, this only decides WHERE.
        public static HitZone Classify(float h, float legsTop, float bodyTop, float headTop)
        {
            float clamped = math.clamp(h, 0f, headTop);
            if (clamped < legsTop) return HitZone.Legs;
            if (clamped < bodyTop) return HitZone.Body;
            return HitZone.Head;
        }

        /// Damage multiplier for a zone. HitZone.None is neutral (1) — a blow
        /// with no zone behind it (melee, any future zone-less source) deals
        /// exactly what it says.
        public static float MultFor(HitZone zone, float legsMult, float bodyMult, float headMult)
        {
            switch (zone)
            {
                case HitZone.Legs: return legsMult;
                case HitZone.Body: return bodyMult;
                case HitZone.Head: return headMult;
                default: return 1f;
            }
        }

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
