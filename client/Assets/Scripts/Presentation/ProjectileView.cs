using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for a single live projectile (spec §3.6/§3.7): an emissive
    /// sphere with a fading `TrailRenderer` tracer. Pooled and (re)bound purely by
    /// `ViewRegistry` — no other class instantiates, destroys, or repositions one.
    public sealed class ProjectileView : MonoBehaviour
    {
        TrailRenderer _trail;

        void Awake() => _trail = GetComponent<TrailRenderer>();

        /// Rebinds this (pooled) view to a freshly spawned projectile: reads the
        /// tracer fade time from `GameFeelConfig` (hot-tweak, spec §3.9) and clears
        /// any leftover trail geometry from a previous life in the pool — without
        /// this, a reused trail draws a spurious line from its last position to the
        /// new spawn point.
        public void Bind(float tracerFadeSeconds)
        {
            _trail.time = tracerFadeSeconds;
            _trail.Clear();
        }
    }
}
