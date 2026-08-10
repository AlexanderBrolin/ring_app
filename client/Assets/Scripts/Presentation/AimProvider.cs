using Ring.Simulation.Core;
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
    /// AFTER this frame's view-position writes (`ViewRegistry`, default script
    /// order — every proxy collider in the arena, the player dolls' own included
    /// since Stage 2 Task 45a, lives on an object THAT class positions) plus an
    /// explicit `Physics.SyncTransforms()`, so the proxy raycast sees this frame's
    /// positions, not last frame's stale ones. `[DefaultExecutionOrder(100)]` pins
    /// this class's own `LateUpdate` behind every one of those writers — `ViewRegistry`
    /// moved to −10 in Stage 2 Task 45b's fix-round 1 and is still far ahead of this
    /// number — which in turn means every OTHER reader of `CurrentAimSimPos`/
    /// `CurrentAimHeight` (`ViewRegistry`, `CrosshairView`, `CameraRig`, `AimRayView`
    /// — all read in their own `LateUpdate`, none of them pinned this late) sees the
    /// value cached last frame: a deliberate, one-render-frame-old value.
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
        // В1/В2 fix-wave 2 (app-n6g item 3, headshot readability): cached
        // alongside the height above, from the exact same proxy hit — same
        // K15 "one cast, one frame-old value" contract.
        HitZone _cachedAimZone;
        MobView _cachedHoveredMob;
        // В3 fix-wave 1 (app-n6g item 3a, owner playtest feedback: the
        // crosshair marker read as "a 2D dot on the floor" even while aiming
        // up a mob's body). The genuine 3D `hit.point` of the SAME proxy
        // cast that already supplies `_cachedAimSimPos`/`_cachedAimHeight`
        // above — was computed internally by `TryAimProxy` all along
        // (`hit.point`, class doc) but never kept past that call. Cached
        // here so `CrosshairView`'s marker can sit at the REAL struck point
        // on the mob's silhouette instead of a floor-projected XY plus a
        // flat height guess.
        Vector3 _cachedAimWorldPoint;

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
            // QA18 guard: no runner, or a backend with nothing to show yet (very
            // first frames before Restart completes, or a scene missing the
            // wiring) — leave the cache exactly where it was rather than throw.
            // Task 43: `Ready` replaced the old `World == null` test, which a
            // networked backend could never answer with anything but "no".
            if (_runner == null || !_runner.Ready) return;

            float2 planeAimSimPos = ComputePlaneAimSimPos();
            if (!_runner.LastFrameInput.AimHeld)
            {
                // Э1 unchanged: CurrentAimSimPos still tracks the plane cast every
                // frame; CurrentAimHeight is "not used" while !AimHeld (the sampler
                // sends it, WeaponSystem's hip-fire branch never reads it) — cached
                // at 0 here purely to keep the field finite/inert.
                _cachedAimSimPos = planeAimSimPos;
                _cachedAimHeight = 0f;
                _cachedAimZone = HitZone.None;
                _cachedHoveredMob = null;
                _cachedAimWorldPoint = SimSpace.ToWorld(planeAimSimPos); // floor point, y=0
                return;
            }

            // This frame's view-position writes (ViewRegistry — dolls, mobs and
            // projectiles alike since Task 45a) already ran this LateUpdate
            // phase before this one (execution-order doc above) — SyncTransforms
            // flushes those Transform writes into PhysX so the cast below sees
            // THIS frame's proxy positions (C15).
            Physics.SyncTransforms();
            if (TryAimProxy(out float2 proxySimPos, out float proxyHeight,
                    out HitZone zone, out MobView hoveredMob, out Vector3 worldPoint))
            {
                _cachedAimSimPos = proxySimPos;
                _cachedAimHeight = proxyHeight;
                _cachedAimZone = zone;
                _cachedHoveredMob = hoveredMob;
                _cachedAimWorldPoint = worldPoint; // the proxy's own real hit.point
            }
            else
            {
                _cachedAimSimPos = planeAimSimPos; // Э1 plane fallback
                _cachedAimHeight = 0f; // "хоть в пол"
                _cachedAimZone = HitZone.None;
                _cachedHoveredMob = null;
                _cachedAimWorldPoint = SimSpace.ToWorld(planeAimSimPos); // same floor fallback
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
        ///
        /// В1/В2 fix-wave 2 (app-n6g item 3): also resolves WHICH belt was hit
        /// (`zone`, via the collider's own bootstrap-assigned name —
        /// `StageOneSceneBootstrap.EnsureAimProxyCapsule` names the three
        /// GameObjects "AimProxy_Legs"/"AimProxy_Body"/"AimProxy_Head"
        /// literally, no extra marker component needed) and, if the hit
        /// collider belongs to a live mob, that mob's own `MobView`
        /// (`GetComponentInParent` — the proxy is a direct child of the same
        /// root `MobView` sits on, `EnsureAimProxyChildren`'s own call sites).
        ///
        /// I1 (final review wave, app-n6g): a hit on the PLAYER's own proxy
        /// (also on this layer, Task 19 — `StageOneSceneBootstrap` wires the
        /// SAME `EnsureAimProxyChildren` onto the player doll as onto every
        /// mob archetype) is excluded outright, treated exactly like a cast
        /// miss. It used to fall through and still populate `simPos`/
        /// `height`/`zone` from the self-hit (`hoveredMob` alone came back
        /// null, since no `MobView` sits on the player) — aiming near your
        /// own feet/torso/head read as a false head-hover cue on YOURSELF
        /// (pulse/tick/red ray, every consumer below reads `CurrentAimZone`
        /// directly, not `CurrentHoveredMob`: `CrosshairView`/`AimRayView`)
        /// and collapsed `CurrentAimSimPos` onto the player's own position
        /// (the aimed shot then degenerates to `WeaponSystem`'s zero-length
        /// fallback direction).
        ///
        /// THE CHECK ASKS WHICH DOLL, NOT WHETHER IT IS A DOLL (Stage 2 Task
        /// 45a). `GetComponentInParent&lt;PlayerView&gt;() != null` used to be a
        /// pure self-hit test only because the local player was the only player
        /// with a doll at all; every player is pooled off one prefab now, so the
        /// bare presence test would have excluded EVERY player from being aimed
        /// at — a PvP arena in which nobody can aim at anybody. `IsLocal`
        /// (`ViewRegistry` sets it from `RenderSnapshot.LocalPlayerIndex` at
        /// bind time) is what makes it "mine" again. A hit on somebody ELSE's
        /// doll now falls through like any other proxy hit: `simPos`/`height`/
        /// `zone` come off it, and `hoveredMob` stays null because no `MobView`
        /// sits on a player — the same null every non-mob proxy hit produces.
        /// (Before Task 45a that null was reachable only through this very
        /// self-hit branch, because the player's own doll carried the only
        /// proxy in the arena that was not a mob's.)
        bool TryAimProxy(out float2 simPos, out float height, out HitZone zone,
            out MobView hoveredMob, out Vector3 worldPoint)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || _camera == null)
            {
                simPos = default;
                height = default;
                zone = HitZone.None;
                hoveredMob = null;
                worldPoint = default;
                return false;
            }

            Ray ray = _camera.ScreenPointToRay(mouse.position.ReadValue());
            float maxDistance = _runner.Config.Arena.Radius * 2f;
            if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance,
                    1 << AimProxyLayer, QueryTriggerInteraction.Collide))
            {
                simPos = default;
                height = default;
                zone = HitZone.None;
                hoveredMob = null;
                worldPoint = default;
                return false;
            }

            // I1: self-hit — behave exactly like the miss branch above (the
            // caller (LateUpdate) falls through to the Э1 plane cast). Task 45a:
            // MY doll, not any doll — see the method doc.
            PlayerView struckDoll = hit.collider.GetComponentInParent<PlayerView>();
            if (struckDoll != null && struckDoll.IsLocal)
            {
                simPos = default;
                height = default;
                zone = HitZone.None;
                hoveredMob = null;
                worldPoint = default;
                return false;
            }

            simPos = SimSpace.ToSim(hit.point);
            height = hit.point.y;
            zone = ClassifyProxyZone(hit.collider);
            hoveredMob = hit.collider.GetComponentInParent<MobView>();
            // В3 fix-wave 1 (app-n6g item 3a): the exact same hit.point this
            // method already computes simPos/height from — kept verbatim
            // rather than reconstructed from simPos/height, so it can never
            // drift from the real cast result (class doc, K15 "one cast"
            // contract).
            worldPoint = hit.point;
            return true;
        }

        static HitZone ClassifyProxyZone(Collider proxyCollider)
        {
            string colliderName = proxyCollider.name;
            if (colliderName == "AimProxy_Legs") return HitZone.Legs;
            if (colliderName == "AimProxy_Body") return HitZone.Body;
            if (colliderName == "AimProxy_Head") return HitZone.Head;
            return HitZone.None;
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

        /// В1/В2 fix-wave 2 (app-n6g item 3a): the zone belt the current proxy
        /// hit landed on — `HitZone.None` while `!AimHeld` or on a miss (class
        /// doc above / `TryAimProxy`'s own doc).
        public HitZone CurrentAimZone => _cachedAimZone;

        /// В1/В2 fix-wave 2 (app-n6g item 3b): the live mob the current proxy
        /// hit belongs to, or null while `!AimHeld`, on a miss, or when the hit
        /// proxy is the player's own (`TryAimProxy`'s own doc).
        public MobView CurrentHoveredMob => _cachedHoveredMob;

        /// В3 fix-wave 1 (app-n6g item 3a): the genuine 3D world point the
        /// aim currently targets — the proxy's own `hit.point` while
        /// `AimHeld` and a proxy is actually hit (real height on the mob's
        /// silhouette, not a floor projection), or the Э1 floor-plane point
        /// (y=0) otherwise (`!AimHeld`, or a miss — class doc above). Cached
        /// once per render frame alongside every other field here (K15).
        public Vector3 CurrentAimWorldPoint => _cachedAimWorldPoint;
    }
}
