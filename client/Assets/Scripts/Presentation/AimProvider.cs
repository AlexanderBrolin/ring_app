using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ring.Presentation
{
    /// Maps the mouse cursor to a point on the arena's y=0 ground plane (spec §3.8) —
    /// the Э1 baseline, still the whole story whenever `!AimHeld`. Task 19 (spec
    /// QA7/QD1) adds a second mode for `AimHeld`: a raycast against the `AimProxy`
    /// layer's capsule colliders (bootstrap-placed belts on `MobChaserView`/
    /// `MobGunnerView`/the player doll). On a hit, BOTH `CurrentAimSimPos` AND
    /// `CurrentAimHeight` are read from the SAME `hit.point` of that ONE cast —
    /// never two independent casts — because a plane-XY + proxy-height mismatch
    /// would put the shot's XY behind the target mob (still on the floor) while its
    /// height targets the mob's silhouette: the trajectory systematically
    /// undershoots and a "tower headshot" dies on contact even when the cursor is
    /// dead-on. On a miss, `CurrentAimSimPos` falls back to the Э1 plane cast and
    /// `CurrentAimHeight` reads 0 ("хоть в пол" — a whiffed aimed shot buries
    /// itself, it does not silently reuse the standing muzzle height). The
    /// sim-to-world axis mapping itself lives in `SimSpace` — this class only
    /// consumes `SimSpace.ToSim`, it does not redefine it.
    ///
    /// K15: both values are cached exactly once per render frame, in `LateUpdate`,
    /// AFTER this frame's view-position writes (`ViewRegistry`/`PlayerView`, both
    /// default script order — the proxy colliders live on THEIR objects) plus an
    /// explicit `Physics.SyncTransforms()`, so the proxy raycast sees this frame's
    /// positions, not last frame's stale ones. `[DefaultExecutionOrder]` pins this
    /// class's own `LateUpdate` to run after those default-order writers, which in
    /// turn means every OTHER default-order reader of `CurrentAimSimPos`/
    /// `CurrentAimHeight` (`PlayerVisual`, `CrosshairView`, `CameraRig` — all read
    /// in their own `LateUpdate`) sees the value cached last frame: a deliberate,
    /// one-render-frame-old value, imperceptible at any playable framerate.
    [DefaultExecutionOrder(100)]
    public sealed class AimProvider : MonoBehaviour
    {
        /// User layer 10 — "AimProxy" in `ProjectSettings/TagManager.asset` (Task
        /// 19). Public so `StageOneSceneBootstrap` (proxy-child layer assignment on
        /// the mob prefabs/player doll, plus `EnsureAimProxyLayer`'s TagManager
        /// patch) shares this exact constant instead of redeclaring the literal
        /// `10` (PC15).
        public const int AimProxyLayer = 10;

        [SerializeField] Camera _camera;
        [SerializeField] SimulationRunner _runner;

        float2 _lastValid;
        float2 _cachedAimSimPos;
        float _cachedAimHeight;

        void Awake()
        {
            // Proxy colliders are pure raycast targets, never physics participants
            // (B3 precedent: PersistentPropsDirector.Awake's casing self-collision
            // guard) — but unlike casings, which still need to collide with the
            // arena, a proxy must never collide with ANYTHING, so every layer
            // pairing is disabled here, not just self-collision.
            for (int i = 0; i < 32; i++)
                Physics.IgnoreLayerCollision(AimProxyLayer, i, true);
        }

        void LateUpdate()
        {
            // QA18 guard: no runner/world yet (very first frames before Restart
            // completes, or a scene missing the wiring) — leave the cache exactly
            // where it was rather than throw.
            if (_runner == null || _runner.World == null) return;

            float2 planeAimSimPos = ComputePlaneAimSimPos();
            if (!_runner.LastFrameInput.AimHeld)
            {
                // Э1 unchanged: CurrentAimSimPos still tracks the plane cast every
                // frame; CurrentAimHeight is "not used" while !AimHeld (the sampler
                // sends it, WeaponSystem's hip-fire branch never reads it) — cached
                // at 0 here purely to keep the field finite/inert.
                _cachedAimSimPos = planeAimSimPos;
                _cachedAimHeight = 0f;
                return;
            }

            // This frame's view-position writes (ViewRegistry/PlayerView) already
            // ran this LateUpdate phase before this one (execution-order doc
            // above) — SyncTransforms flushes those Transform writes into PhysX
            // so the cast below sees THIS frame's proxy positions (C15).
            Physics.SyncTransforms();
            if (TryAimProxy(out float2 proxySimPos, out float proxyHeight))
            {
                _cachedAimSimPos = proxySimPos;
                _cachedAimHeight = proxyHeight;
            }
            else
            {
                _cachedAimSimPos = planeAimSimPos; // Э1 plane fallback
                _cachedAimHeight = 0f; // "хоть в пол"
            }
        }

        /// The Э1 plane cast (spec §3.8 invariant): never NaN, never
        /// stale-uninitialized beyond the very first frame — falls back to the last
        /// valid sample whenever the mouse device is missing (focus loss) or the
        /// camera ray is (near-)parallel to the plane / points away from it.
        float2 ComputePlaneAimSimPos()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || _camera == null) return _lastValid;

            Vector2 screenPos = mouse.position.ReadValue();
            Ray ray = _camera.ScreenPointToRay(screenPos);
            if (math.abs(ray.direction.y) <= 1e-4f) return _lastValid;

            float t = -ray.origin.y / ray.direction.y;
            if (t <= 0f) return _lastValid;

            Vector3 world = ray.origin + ray.direction * t;
            _lastValid = SimSpace.ToSim(world);
            return _lastValid;
        }

        /// Task 19 (spec QA7/QD1): a single raycast against the `AimProxy`-layer
        /// capsule colliders — `hit.point` supplies BOTH the XY (via
        /// `SimSpace.ToSim`) and the height, so they can never disagree (class
        /// doc). `maxDistance` mirrors `SimulationWorld.Sanitize`'s own aim-ray cap
        /// (`Arena.Radius * 2f`) — the same "how far can this cursor possibly
        /// reach" bound, kept in exact lockstep rather than redefined here (PC15).
        /// `QueryTriggerInteraction.Collide` is required — every proxy capsule is
        /// `isTrigger = true` (bootstrap), which `Physics.Raycast` ignores by
        /// default.
        bool TryAimProxy(out float2 simPos, out float height)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || _camera == null)
            {
                simPos = default;
                height = default;
                return false;
            }

            Ray ray = _camera.ScreenPointToRay(mouse.position.ReadValue());
            float maxDistance = _runner.World.Config.Arena.Radius * 2f;
            if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance,
                    1 << AimProxyLayer, QueryTriggerInteraction.Collide))
            {
                simPos = default;
                height = default;
                return false;
            }

            simPos = SimSpace.ToSim(hit.point);
            height = hit.point.y;
            return true;
        }

        /// Current aim point in sim space (class doc: Э1 plane cast while
        /// `!AimHeld`, proxy hit — or its plane fallback on a miss — while
        /// `AimHeld`). Cached once per render frame (K15); this property never
        /// itself casts a ray.
        public float2 CurrentAimSimPos => _cachedAimSimPos;

        /// New in Task 19: aim-ray height, meaningful only while `AimHeld` (class
        /// doc / K15) — cached alongside `CurrentAimSimPos` from the exact same
        /// proxy hit when one lands.
        public float CurrentAimHeight => _cachedAimHeight;
    }
}
