using Ring.Simulation.AI;
using Ring.Simulation.Combat;
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
    /// `MobGunnerView`/the player doll) — joined, since Stage 2 Task 46, by the
    /// arena's own greybox geometry on `GreyboxBuilder.CosmeticsLayer`, which is
    /// in the cast only to SCREEN: a hit on it is reported as a miss, so a mob
    /// standing behind a wall can no longer be hovered through it (bd `app-1ru`,
    /// `TryAimProxy`'s own doc). That task's fix-round finished the screen along
    /// the shot's own line rather than the camera's, so a proxy the camera can
    /// see past a barrier but the round cannot get to is a miss as well — same
    /// doc, which also names the flat-plan limit of the gate doing it. On a
    /// surviving hit, BOTH `CurrentAimSimPos` AND
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
        // Stage 2 Task 45c (bd app-bej): where the round fired at the current
        // aim would actually come down — `_cachedAimWorldPoint` pulled back
        // along the shot's own line by `Trajectory.FloorCutFraction`. Cached
        // here, alongside every other per-frame aim value, because the marker
        // and the ray END have to name the SAME point (PC8: the marker doubles
        // as the ray's dot) and two independent restatements of one flight
        // would eventually disagree.
        Vector3 _cachedImpactWorldPoint;

        void Awake()
        {
            // Proxy colliders are pure raycast targets, never physics participants
            // (B3 precedent: PersistentPropsDirector.Awake's casing self-collision
            // guard) — but unlike casings, which still need to collide with the
            // arena, a proxy must never collide with ANYTHING, so every layer
            // pairing is disabled here, not just self-collision. This does not
            // touch the cast below, including the arena-geometry layer Stage 2
            // Task 46 added to its mask: IgnoreLayerCollision suppresses CONTACT
            // generation between colliders, and scene queries answer off the
            // layer mask they are handed.
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
                // Hip fire is flat (WeaponSystem's own `!AimHeld` branch spawns
                // the round with no climb rate at all), so it never descends
                // and the ground never cuts it — there is no correction to make
                // and the two points coincide.
                _cachedImpactWorldPoint = _cachedAimWorldPoint;
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

            _cachedImpactWorldPoint = ResolveImpactWorldPoint();
        }

        /// Stage 2 Task 45c (bd `app-bej`, owner smoke test #1): the aimed point
        /// pulled back to where the round actually ends up. The share of the
        /// muzzle→aim line the round covers is `Ring.Simulation.Combat.
        /// Trajectory.FloorCutFraction`'s answer and nothing is re-derived here
        /// — a shot aimed at the floor stops one projectile radius' worth of
        /// the descent early, because `ProjectileSystem` resolves the ground
        /// contact at the round's CENTER height (that method's own doc has the
        /// measured numbers).
        ///
        /// THE LINE IS THE SIMULATION'S, NOT THE PICTURE'S. Both endpoints come
        /// off the authoritative shot — `SimulationRunner.RenderMuzzleSimPos`/
        /// `RenderMuzzleHeight` are the ground point and the height
        /// `WeaponSystem`'s aimed branch fires from — rather than off the doll's
        /// muzzle socket, which is where Stage 2 Task 45b moved the DRAWN origin
        /// of the ray. So `AimRayView` draws from the barrel of the model to a
        /// point measured from the simulation's own muzzle: the two ends answer
        /// different questions on purpose, "where does the player see the gun"
        /// and "where does the round land".
        ///
        /// Aim at anything at or above the contact height — which is every mob
        /// body, and the player dolls, and nothing else — and the fraction is 1,
        /// so this returns `CurrentAimWorldPoint` unchanged. A WALL IS NOT IN
        /// THAT LIST (fix-round 1, G-5 item 8, correcting this paragraph's own
        /// earlier claim): a cursor over a wall is a cast MISS, reads height 0
        /// through the plane fallback, and gets the floor's own fraction. That
        /// is the honest answer for it too — the round really does come down
        /// short of the wall's foot — but it is not the "nothing moves" case.
        /// Stage 2 Task 46 changed WHY that is a miss without changing THAT it
        /// is one: arena geometry used to be outside the cast's mask entirely,
        /// and is now inside it and deliberately reported as a miss.
        ///
        /// ONE HEIGHT, READ ONCE. The aim height handed to the helper and the
        /// `y` of the far endpoint it interpolates toward must be the same
        /// number, or the cut is measured along one line and applied to another.
        /// Both come off `_cachedAimWorldPoint` here rather than one off it and
        /// one off `_cachedAimHeight` — the two are equal in every branch of
        /// `LateUpdate` above, and taking them from one place is what keeps them
        /// equal without a comment having to promise it.
        Vector3 ResolveImpactWorldPoint()
        {
            float muzzleHeight = _runner.RenderMuzzleHeight;
            float cut = Trajectory.FloorCutFraction(muzzleHeight, _cachedAimWorldPoint.y,
                _runner.Config.Weapon.ProjectileRadius);
            if (cut >= 1f) return _cachedAimWorldPoint;

            Vector3 muzzleWorld = SimSpace.ToWorld(_runner.RenderMuzzleSimPos(_cachedAimSimPos))
                + Vector3.up * muzzleHeight;
            // `Lerp`, not `LerpUnclamped`: the helper's answer is in [0, 1] by
            // construction (its own doc's three branches), and the clamped call
            // is what says so at the call site.
            return Vector3.Lerp(muzzleWorld, _cachedAimWorldPoint, cut);
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
        /// ARENA GEOMETRY IS IN THE CAST, AND EVERY HIT ON IT IS A MISS (Stage
        /// 2 Task 46, bd `app-1ru`). The mask covers `GreyboxBuilder.
        /// CosmeticsLayer` as well as the proxies, and `Physics.Raycast`
        /// returns the NEAREST hit, so the three cases fall out of that one
        /// fact: cursor over bare floor — the floor is the nearest hit, miss,
        /// Э1 plane fallback, exactly as before this task, since a miss was
        /// already the answer there; cursor on a mob — its proxy sits above the
        /// floor and therefore nearer along a descending camera ray, hover;
        /// cursor on a mob standing BEHIND a wall — the wall is nearer, miss,
        /// and the head pulse and red ray stop promising a target the round
        /// cannot reach. The bug this closes was never that the cast hit walls
        /// (it never did — that is what `app-1ru`'s own description got wrong);
        /// it was that walls were invisible to it while the mob behind them was
        /// not. Layer 8 carries the arena greybox and nothing else — casings
        /// have their own layer 9 precisely so a layer-pair toggle could tell
        /// them apart (`PersistentPropsDirector`), and corpses/decals carry no
        /// collider on it — so no cosmetic prop can flicker the aim by drifting
        /// through the cursor line.
        ///
        /// AND THE SCREEN IS FINISHED ALONG THE LINE OF FIRE, NOT THE CAMERA'S
        /// (fix-round 1, Ф-2, the remainder of `app-1ru`'s second half). The
        /// cast above screens what the CAMERA cannot see past, and this camera
        /// is a rigid frame — `CameraRig` builds its offset from `CameraConfig`
        /// alone (pitch 55°, distance 18, no yaw), so a 3 m barrier throws a
        /// screen shadow of only 3/tan(55°) ≈ 2.1 m at frame center. A mob
        /// standing further behind a barrier than that is unshadowed on screen,
        /// the cast reaches its proxy, and the head pulse, the zone tint and the
        /// marker would go back to promising a target a shot from a 1.0 m muzzle
        /// cannot reach through a 3 m barrier. So a proxy hit is accepted only
        /// when `Ring.Simulation.AI.Targeting.HasLineOfFire` — the simulation's
        /// own "can a round get from here to there" gate, already the one the
        /// gunner AI fires off and the server's fog of war sees by — agrees,
        /// measured from the SIMULATION's muzzle (`SimulationRunner.
        /// RenderMuzzleSimPos`, the point `WeaponSystem`'s aimed branch really
        /// launches from) to the proxy point, padded by the round's own
        /// `ProjectileRadius`, exactly as the projectile's own gather phase pads
        /// its sweep. A refusal takes the same early exit a hit on arena
        /// geometry takes: a miss, the Э1 plane fallback, `CurrentAimZone` back
        /// to `None`, `CurrentHoveredMob` back to null, the marker on the floor.
        /// Nothing else moves — the `!AimHeld` branch, the plane fallback
        /// itself, the local-doll guard and every path that never reaches a
        /// proxy are untouched, and another PLAYER's doll is screened by this
        /// same rule (it is behind the guard, not in front of it), which is the
        /// PvP half of the same bug.
        ///
        /// THAT GATE IS FLAT, AND TODAY THAT COSTS A NAMED BAND, NOT NOTHING.
        /// `HasLineOfFire` knows obstacle circles, interior wall stadiums and —
        /// since Stage 3 Task 9 — zone-wall ARCS in plan only (this sentence
        /// named the first two until Task 30 drew the third: the arcs share
        /// `BarrierTop` with the other interior barriers, so everything the
        /// paragraph says below covers them unchanged); it has no height and no
        /// `Arena.BarrierTop`, and it does
        /// not consult the outer ring boundary at all (its own doc says so) —
        /// harmless here, because a target beyond the rim is not a target. For
        /// every aim point at or below the barrier column the round's own radius
        /// grows `BarrierTop` into, the flat answer is not merely conservative
        /// but exact: the round flies a straight line from the muzzle to that
        /// point (`ProjectileSystem` adds no gravity), so its whole height stays
        /// between the two, inside the column, and any barrier on the plan line
        /// really does stop it. Above that column the flat answer starts
        /// refusing shots that would land — with the shipped numbers that band
        /// is the top 0.42 m of a Gunner's head belt (`MobGunnerConfig` head top
        /// 3.5 against a grown column of 3.08), and only when the barrier also
        /// sits in the last sixth or so of the muzzle→aim line, which is what it
        /// takes for a climbing round to be over the crown by the time it gets
        /// there. The refusal is the deliberate direction of that error, the
        /// same one `ProjectileSystem`'s own gate chose: this bug is about the
        /// picture promising what the round cannot do, so the picture stays on
        /// the cautious side of it. When the owner's post-MVP low cover lands
        /// (0.8–1.0 m, with the crouch), the flat answer stops being cautious
        /// and starts being wrong, and height has to arrive here as well.
        ///
        /// WHAT THE OWNER WILL SEE FROM THE RIGID FRAME, both halves of it: near
        /// the arena's southern rim the camera itself sits BEHIND the ring wall,
        /// the cast lands on its outer face, and the cursor stops hovering
        /// anything in that strip; and any barrier within ~2.1 m south of a
        /// target takes its hover away too. Both agree with what is drawn (the
        /// barrier is between the eye and the target), but "the cursor stopped
        /// picking things up at the south edge" is a bug report waiting to be
        /// filed, so it is named here first.
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

            // One copy of the built config for the whole method — the cast's
            // reach, the round's radius and the arena the line-of-fire gate
            // walks all come off it, and `Config` is a by-value property over a
            // struct this size.
            SimConfig config = _runner.Config;
            Ray ray = _camera.ScreenPointToRay(mouse.position.ReadValue());
            float maxDistance = config.Arena.Radius * 2f;
            // Stage 2 Task 46: proxies AND arena geometry, one cast. Adding the
            // geometry layer is what lets a wall SCREEN a mob behind it; the
            // proxies still answer everything else, and QueryTriggerInteraction
            // .Collide stays required for them alone (every proxy capsule is a
            // trigger, the greybox primitives are not).
            const int geometryMask = (1 << AimProxyLayer) | (1 << GreyboxBuilder.CosmeticsLayer);
            if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance,
                    geometryMask, QueryTriggerInteraction.Collide))
            {
                simPos = default;
                height = default;
                zone = HitZone.None;
                hoveredMob = null;
                worldPoint = default;
                return false;
            }

            // Arena geometry screens whatever stands behind it: report a miss,
            // which sends the caller to the Э1 plane fallback — the same answer
            // the cursor already got over bare floor, and the honest one for a
            // point the round cannot reach (method doc).
            if (hit.collider.gameObject.layer == GreyboxBuilder.CosmeticsLayer)
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

            // Fix-round 1 (Ф-2): the camera screened this hit, now the shot
            // does. Both ends are the SIMULATION's — the muzzle the aimed
            // branch of `WeaponSystem` launches from, and the point the round
            // is being sent to — so this asks the authoritative question rather
            // than a picture-shaped approximation of it (method doc for the
            // gate's flat-plan limit and for the band it costs).
            float2 aimSimPos = SimSpace.ToSim(hit.point);
            if (!Targeting.HasLineOfFire(_runner.RenderMuzzleSimPos(aimSimPos), aimSimPos,
                    config.Weapon.ProjectileRadius, in config.Arena))
            {
                simPos = default;
                height = default;
                zone = HitZone.None;
                hoveredMob = null;
                worldPoint = default;
                return false;
            }

            simPos = aimSimPos;
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
        ///
        /// WHERE THE CURSOR POINTS, WHICH IS NOT WHERE THE ROUND LANDS (Stage 2
        /// Task 45c). `CurrentImpactWorldPoint` below is what a view showing
        /// the player a promise must draw; this one stayed because it is the
        /// input that promise is built from, and because "what is under the
        /// cursor" is a question of its own that the observation work (Т47) will
        /// have to ask again.
        public Vector3 CurrentAimWorldPoint => _cachedAimWorldPoint;

        /// Stage 2 Task 45c (bd `app-bej`): where a round fired at the current
        /// aim actually comes down — `CurrentAimWorldPoint` above, cut short by
        /// the ground when the aim is low enough for the ground to be in the
        /// way (`ResolveImpactWorldPoint`'s own doc for the full rule and for
        /// which line it is measured along). The aim ray's far end and the
        /// crosshair marker both read THIS, so the picture stops promising a
        /// point the round cannot reach; the two coincide for every aim at or
        /// above the round's ground-contact height, which is every shot at a
        /// body. Cached once per render frame like everything else here (K15).
        public Vector3 CurrentImpactWorldPoint => _cachedImpactWorldPoint;
    }
}
