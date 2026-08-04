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
        /// tracer fade time and width scale from `GameFeelConfig` (hot-tweak,
        /// spec §3.9; Task 21 adds `tracerScale`, `GameFeelConfig.TracerScale`)
        /// and clears any leftover trail geometry from a previous life in the
        /// pool. `widthMultiplier` scales the baked `startWidth`/`endWidth`
        /// curve (`StageOneSceneBootstrap.GetOrCreateProjectilePrefab`) rather
        /// than overwriting it, so the prefab's tapered shape survives a
        /// hot-tweak of the overall scale.
        /// CALL ORDER MATTERS (fix-round, app-2pl): the caller (`ViewRegistry`)
        /// must set `transform.position` to the new spawn point BEFORE calling
        /// `Bind` — `TrailRenderer.Clear()` seeds the trail's first point at
        /// whatever position the transform currently has, so clearing before the
        /// teleport draws a spurious segment from the pooled view's old position
        /// (wherever the previous projectile died) to the new spawn point on
        /// every rent from the pool.
        public void Bind(float tracerFadeSeconds, float tracerScale)
        {
            _trail.time = tracerFadeSeconds;
            _trail.widthMultiplier = tracerScale;
            _trail.Clear();
        }
    }
}
