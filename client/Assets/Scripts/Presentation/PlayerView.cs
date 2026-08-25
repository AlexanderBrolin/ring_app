using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for one player slot's doll (spec §3.7/§3.11/§3.12) —
    /// pure presentation, П-7. Pooled and (re)bound purely by `ViewRegistry`
    /// since Stage 2 Task 45a: no other class instantiates, destroys or
    /// repositions a `PlayerView`, and this component holds no scene or SO
    /// reference of its own (the `SimulationRunner` it used to read
    /// `RenderPlayerWorldPos` from went with the move — the registry
    /// interpolates every slot's own `PlayerState.Pos` instead, the same way it
    /// already does for mobs). Stage 2 Task 45b's two socket fields do not
    /// break that: they point INSIDE this same prefab, at children of the doll's
    /// own gun, which travel with a pooled instance the way `Visual` does and
    /// the way a scene or an asset reference never could. The root does not
    /// rotate and carries no renderer
    /// of its own: the doll lives on the "Visual" child and `PlayerVisual` owns
    /// facing/animation (spec §3.2). Root pivot sits on the ground.
    ///
    /// EMISSION LIVES HERE, POSE LIVES ON `PlayerVisual` — the exact
    /// `MobView`/`MobVisual` split this pair mirrors. `Bind` sets the
    /// pool-rebind baseline; `Sync` composes every per-frame accent and is the
    /// only place that COMPOSES one, so on the steady per-frame path there is
    /// exactly one property-block write per doll per frame. Four methods call
    /// `ApplyEmission` and the other three write a value rather than compose
    /// it: `Bind` blacks the block out on a pool rebind (`ViewRegistry` calls
    /// `Sync` right after, so a rebind frame carries two writes),
    /// `FadeEmission` re-applies what `Sync` last composed for a slot the
    /// frame has stopped carrying — `ViewRegistry` calls that one INSTEAD of
    /// `Sync`, never beside it — and `DetachAsCorpse` writes black once and
    /// for good. Three accents compose here:
    ///  - the Dash↔Slide combo-window pulse (В1 fix-wave 1, owner playtest item
    ///    3 "мерцание сборщика"): a sine at `GameFeelConfig.LinkWindowFlashHz`
    ///    on unscaled time (no time-manipulation feature ever touches it —
    ///    `Time.timeScale` is never used by this project, see
    ///    `SimulationRunner`) while
    ///    `PlayerState.PostDashSlideTimer`/`LinkWindowTimer` — either > 0f,
    ///    `PlayerMovementSystem`'s own doc — is open. `SimulationWorld.KillPlayer`
    ///    zeroes both, so a death mid-pulse lands back at black by itself on the
    ///    very next frame instead of needing a clear of its own;
    ///  - the remote-player rim (`GameFeelConfig.RemotePlayerEmission`, Stage 2
    ///    Task 45a): a steady tint every doll but this client's own wears, so a
    ///    stranger reads as a stranger at a glance. The registry decides which
    ///    doll is "own" and passes black for it — this class never asks;
    ///  - the hit flash (`Flash`, Stage 2 Task 45c): the decaying white a struck
    ///    body wears, the same one `MobView` gives a struck mech, layered last
    ///    so it reads on top of both accents above rather than instead of them.
    /// `_renderers` is cached once here the same way `MobView`/`CorpseView`
    /// cache theirs (`GetComponentsInChildren&lt;Renderer&gt;(true)`, one shared
    /// `MaterialPropertyBlock`, never a material instance, П-2).
    /// `LinkWindowFlashAccent` reuses `PlayerEmissive`/`DashGlowView`'s own
    /// established player-signature cyan (Э1) rather than inventing a new accent
    /// color; `LinkWindowFlashBoost` is a separate hot-tweak multiplier on the
    /// pulse's peak intensity, same split every other Presentation
    /// accent-color-vs-SO-number pair already makes.
    public sealed class PlayerView : MonoBehaviour
    {
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        // = PlayerEmissive/DashGlowView's own accent (Э1) — reused, not reinvented.
        static readonly Color LinkWindowFlashAccent = new Color(0f, 2.5f, 3f);
        // = MobView.FlashAccent, the project's one hit-flash white (Stage 2 Task
        // 45c): a round landing on a collector has to read as the same event as
        // a round landing on a mech, or the player learns two vocabularies for
        // one thing. A shared constant would have to live in a third type that
        // neither view owns; the pair of literals is the smaller cost, and this
        // comment is what keeps them tied.
        static readonly Color FlashAccent = new Color(4f, 4f, 4f);

        // Stage 2 Task 45b: the two empty children `StageOneSceneBootstrap`
        // parents under this doll's `Gun`, posed from `GameFeelConfig`. They are
        // SERIALIZED references rather than a runtime name lookup because the
        // gun hangs off a humanoid bone whose path inside the doll rig belongs
        // to the asset pack, not to us — the same reason the bootstrap reaches
        // the gun itself with a depth-first search instead of a fixed path, and
        // a search this class would otherwise have to repeat on every pooled
        // rebind.
        //
        // `PlayerGunTuner` cannot stand in for them: its whole body, the `_gun`
        // field included, lives under `#if UNITY_EDITOR`, so in a player build
        // that component holds nothing at all. The runtime path is this one.
        [SerializeField] Transform _muzzleSocket;
        [SerializeField] Transform _ejectSocket;

        Renderer[] _renderers;
        MaterialPropertyBlock _block;
        // Stage 2 Task 45b: this doll's own `AimProxy_*` trigger colliders,
        // switched off when it becomes a corpse — see `DetachAsCorpse`.
        // Collected by LAYER, not by component type: the doll also carries the
        // gun model, and a model's own collider (should an import ever bring
        // one) is not an aim proxy and must not be switched with them.
        Collider[] _aimProxies;
        int _aimProxyCount;
        // Stage 2 Task 45c: the hit-flash pair, same shape and same unscaled
        // clock as `MobView`'s own — `Update` counts the timer down, `Sync`
        // composes the resulting color on top of whatever accent the frame
        // already has, so a flash never races the other accents for the last
        // property-block write.
        float _flashTimer;
        float _flashDuration;
        // Stage 2 Task 47c: what `Sync` last COMPOSED, before the fade
        // multiplier was applied to it — the one thing `FadeEmission` needs in
        // order to dim a doll it cannot recompose, because the frame carries no
        // state for that slot any more (see `FadeEmission`).
        //
        // EXACTLY TWO WRITERS, `Bind` AND `Sync`. `DetachAsCorpse` deliberately
        // is not a third: it writes black straight to the property block and a
        // corpse never receives `Sync` or `FadeEmission` again (its own doc), so
        // a third write here would be a line whose effect nothing can observe.
        // The next `Bind` — the only way that doll comes back — resets it.
        Color _composedEmission = Color.black;

        /// Whether the slot this doll is bound to is this client's own
        /// (`RenderSnapshot.LocalPlayerIndex` — the registry decides, this class
        /// records). `AimProvider` reads it to tell "my own doll" from
        /// "somebody else's" when a proxy raycast lands on a `PlayerView`:
        /// before pooling, the mere PRESENCE of this component meant "mine",
        /// because only the local player ever had a doll at all.
        public bool IsLocal { get; private set; }

        /// Cached in `Awake` — the pose half of this pair (see the class doc).
        public PlayerVisual Visual { get; private set; }

        /// Where this doll's weapon actually points from (Stage 2 Task 45b):
        /// the mouth of the barrel, riding the hand bone through the gun. The
        /// muzzle flash bursts here and the aim ray starts here, so that both
        /// come off the WEAPON THE PLAYER SEES rather than off a point the
        /// simulation computes in front of the hero (`WeaponConfig.MuzzleOffset`
        /// along the aim), which is where they used to sit and which is visibly
        /// not the gun once the doll carries a real model.
        ///
        /// A CONSUMER MUST TREAT NULL AS "NO MUZZLE", not as a reason to fall
        /// back on the event's position. That fallback is exactly the F-3 defect
        /// (`app-aq9`): a `ShotHeard` — a shot from someone this client cannot
        /// see — arrives as an ordinary `ProjectileFired` at a position the
        /// server deliberately coarsened, and drawing anything there hands the
        /// shooter's location to a player who was never told it.
        public Transform MuzzleSocket => _muzzleSocket;

        /// Where this doll's weapon throws its brass (Stage 2 Task 45b) —
        /// position AND direction, unlike `MuzzleSocket` above: the casing's
        /// impulse is this transform's own forward (bd `app-e2n`: "импульс
        /// вбок-назад от ориентации оружия"), so the port's rotation is a real
        /// consumer of the owner's gizmo tuning, not decoration. Same null
        /// contract as `MuzzleSocket`.
        public Transform EjectSocket => _ejectSocket;

        void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _block = new MaterialPropertyBlock();
            Visual = GetComponent<PlayerVisual>();
            if (Visual == null)
                Debug.LogError("PlayerView: no PlayerVisual on the doll — it will not animate.");
            CacheAimProxies();
        }

        /// The doll's aim-proxy colliders, found once (Stage 2 Task 45b). They
        /// are bootstrap-created children of this root and nothing adds or
        /// removes one during a match, so one pass in `Awake` is the whole
        /// story — same "cache the children once" rule `_renderers` above
        /// already follows. `includeInactive: true` for the same reason that
        /// call does: a pooled doll spends its time between lives disabled.
        void CacheAimProxies()
        {
            Collider[] all = GetComponentsInChildren<Collider>(true);
            _aimProxies = all;
            _aimProxyCount = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.layer != AimProvider.AimProxyLayer) continue;
                _aimProxies[_aimProxyCount++] = all[i];
            }
        }

        /// Turns this doll's aim proxies on or off (Stage 2 Task 45b). `enabled`
        /// on the collider, not `SetActive` on its GameObject: the proxy child
        /// carries nothing else, and leaving the object itself alive keeps the
        /// cached array valid and the hierarchy readable in the Inspector.
        void SetAimProxiesEnabled(bool value)
        {
            for (int i = 0; i < _aimProxyCount; i++) _aimProxies[i].enabled = value;
        }

        /// Whether this doll may be AIMED AT (Stage 2 Task 47c fix-round 1) —
        /// the same switch `Bind` and `DetachAsCorpse` already throw, exposed
        /// for the third state a doll can be in: HELD while its slot fades out
        /// (`ViewRegistry.HoldFadingDoll`). A held doll is neither rented nor
        /// retired, so neither of those two ever runs for it, and its proxies
        /// would otherwise stay live for the whole fade.
        ///
        /// THE READER THIS CLOSES DOES NOT GO THROUGH `ViewRegistry` AT ALL,
        /// which is why the registry's three refusals (`TryGetPlayerView`,
        /// `DispatchToDoll`, `EnsureCorpse`) do not cover it: it is a raycast.
        /// `AimProvider.TryAimProxy` walks up from whatever collider it struck
        /// and drops only THIS client's own doll (`struckDoll.IsLocal`), so a
        /// stranger's held doll passes that guard and fills `CurrentAimSimPos`/
        /// `CurrentAimZone` — which `InputSampler.SampleFrame` puts on the wire
        /// as `SimInput.AimPoint`, `CameraRig` leads the camera by, and
        /// `CrosshairView`/`AimRayView` paint the zone tint and the head-hover
        /// cue from. The doll is still drawn, so nothing the server withheld
        /// leaks; what the aim would promise is a target the server has stopped
        /// confirming, which is the same reason `DetachAsCorpse`'s own doc
        /// gives for a corpse.
        ///
        /// Narrow for the same reason `FadeEmission` is: a slot the frame is
        /// silent about has no state, so `Sync` cannot be called for it, and
        /// members like these two are the only way its doll can be reached.
        public void SetAimable(bool value) => SetAimProxiesEnabled(value);

        /// Rebinds this (pooled) doll to a player slot: records whose slot it is
        /// and clears any emission left over from a previous life in the pool.
        /// Only sets the resting baseline — `ViewRegistry` calls `Sync` right
        /// after this (same frame) to layer in this tick's accents.
        ///
        /// `MobView.Bind` takes the bound entity's `MobState` because a mob's
        /// ARCHETYPE is a field of it; a player's identity is the array index,
        /// which `PlayerState` does not carry, and nothing else about the
        /// resting baseline depends on state — so the one argument here is that
        /// identity instead.
        public void Bind(bool isLocal)
        {
            IsLocal = isLocal;
            // Pool-rebind hygiene, same line `MobView.Bind` carries: a doll must
            // not open its new life mid-flash from the previous slot's last hit.
            _flashTimer = 0f;
            // Stage 2 Task 47c: and not mid-fade from the previous slot's last
            // moments either — a doll that went into the pool half dimmed must
            // not hand its remainder to whoever rents it next.
            _composedEmission = Color.black;
            ApplyEmission(_composedEmission);
            // Stage 2 Task 45b: the other half of `DetachAsCorpse`'s proxy
            // switch-off. A doll only ever reaches this method by being rented
            // from the pool, and the pool is fed both by a slot leaving the
            // frame and — via `ViewRegistry.Clear` — by corpses at a match
            // restart, so the previous life's last act can be "proxies off".
            // Renting one back without this line would put a live player behind
            // a silhouette nothing can aim at. Stage 2 Task 47c fix-round 1
            // added a second way for a life to end that way — a doll retired
            // mid-hold, switched off by `SetAimable` (its own doc) — and this
            // line covers it for exactly the same reason.
            SetAimProxiesEnabled(true);
        }

        /// Per-frame accent pass, called once per render frame by
        /// `ViewRegistry.SyncPlayers` for every live doll (new AND continuing).
        /// The three parameters are the registry's own once-per-frame reads of
        /// `GameFeelConfig` — passed as plain values rather than handing this
        /// class a config reference of its own, the same "caller pre-reads the
        /// config, callee takes scalars" shape `MobView.Sync` already follows.
        /// `remoteAccent` is `Color.black` for this client's own doll.
        ///
        /// `fadeRemaining` (Stage 2 Task 47c) IS A MULTIPLIER ON THE FINISHED
        /// COMPOSITION, NOT A FOURTH ACCENT: `1` leaves the three accents above
        /// exactly as they were, `0` puts the doll out. It is the last thing
        /// applied because a fade is not something the doll is doing — it is how
        /// much of whatever it is doing still reaches the screen. In ordinary
        /// play a slot the frame carries has a remainder of exactly `1` (the
        /// policy resets its counter on every sighting, and the frame on screen
        /// is one the policy has already been told about), so the live path is
        /// unchanged for it; the value that arrives here below `1` is the
        /// reordered-frame corner, where a frame carrying a slot lands behind a
        /// sighting the policy already has and the doll is visible while its
        /// fade has begun. `StalePolicy`'s CALLER CONTRACT is explicit that the
        /// remainder is applied whenever it says so, whatever else is true.
        public void Sync(in PlayerState m, float linkWindowFlashHz,
            float linkWindowFlashBoost, Color remoteAccent, float fadeRemaining)
        {
            Color emission = remoteAccent;

            if (m.PostDashSlideTimer > 0f || m.LinkWindowTimer > 0f)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(
                    Time.unscaledTime * linkWindowFlashHz * Mathf.PI * 2f);
                emission += LinkWindowFlashAccent * wave * linkWindowFlashBoost;
            }

            // Stage 2 Task 45c: layered last, exactly as `MobView.Sync` layers
            // its own — a hit reads on top of whatever the doll was already
            // doing rather than replacing it.
            if (_flashTimer > 0f) emission += FlashAccent * Mathf.Clamp01(_flashTimer / _flashDuration);

            _composedEmission = emission;
            ApplyEmission(emission * fadeRemaining);
        }

        /// Dims a doll the frame has stopped carrying (Stage 2 Task 47c, bd
        /// `app-wcy`) — `fadeRemaining` from `1` (untouched) down to `0` (out).
        /// The narrow member `ViewRegistry`'s held path needs, and the reason it
        /// is narrow: a slot the frame is silent about has NO state, so `Sync`
        /// cannot be called for it at all (`default(PlayerState)` reads as the
        /// arena origin and as dead — both lies). This re-applies what `Sync`
        /// last composed instead of composing anything, so emission keeps its
        /// single home and this method can never become a second one.
        ///
        /// WHAT ACTUALLY FADES IS THE NEON, AND THE OWNER CHOSE THAT KNOWING
        /// (decision 2a, session 14). Every material in the project is opaque —
        /// there is not one transparent material to fade an alpha on — so
        /// turning a doll translucent would mean a new material contract in the
        /// art track's zone. What goes out here is the emissive rim that marks a
        /// stranger; the gray silhouette underneath stays lit by the scene until
        /// the doll is retired. So the pop is not abolished, it is made quieter
        /// and later: the thing that reads as "a player is there" dims away over
        /// half a second, and what vanishes at the end is an unlit shape the eye
        /// was no longer tracking.
        public void FadeEmission(float fadeRemaining)
            => ApplyEmission(_composedEmission * fadeRemaining);

        /// A blow landed on this player (Stage 2 Task 45c, bd `app-aq9`, ADR-001
        /// §10's per-hit checklist): the same decaying white flash `MobView.Flash`
        /// gives a struck mech, driven by `GameFeelDirector`'s `PlayerDamaged`
        /// handler on the VICTIM's doll — the attacker's doll gets nothing, since
        /// the checklist is about the body that was hit. Reentrant like its twin:
        /// a second hit mid-flash restamps the timer rather than stacking
        /// brightness.
        ///
        /// IT MEANS DAMAGE WAS TAKEN, and that is the point of the kind it rides
        /// (fix-round 1, G-1). `PlayerDamaged` is not emitted when dash i-frames
        /// refuse a blow, so a round absorbed mid-dash lights nothing here — its
        /// spark and its sound still fire, off the round's own ending. The
        /// earlier wiring hung this off that ending instead and could not tell
        /// the two apart at all; worse, the victim's slot is not on the wire
        /// there (`ClientEventDecoder`'s class doc), so on a networked client the
        /// flash landed on whoever sat in slot 0.
        ///
        /// A FIST COUNTS TOO: `MobAiSystem` drives the same `DamagePlayer`, so a
        /// Chaser's strike flashes its victim exactly like a round does.
        public void Flash(float duration)
        {
            _flashDuration = Mathf.Max(duration, 1e-4f);
            _flashTimer = _flashDuration;
        }

        /// Only the flash timer runs here — everything else about this doll is
        /// written by `ViewRegistry` from the snapshot. Unscaled, same as every
        /// other feel timer in this namespace.
        void Update()
        {
            if (_flashTimer > 0f) _flashTimer -= Time.unscaledDeltaTime;
        }

        /// This doll has just become a corpse (Stage 2 Task 45a fix-round 1;
        /// `ViewRegistry.IntoCorpse` does the bookkeeping — leaving the slot map
        /// for `_corpses` — and this is the view's own half of it). THE
        /// BOOKKEEPER MOVED IN STAGE 2 TASK 47a and this line named the old one
        /// until fix-round 1: it used to be `DispatchToDoll`, the `PlayerDied`
        /// fan-out, and that is now only one of `IntoCorpse`'s two callers. The
        /// other is the FRAME (`EnsureCorpse`), which is the one that runs on
        /// the networked backend in the ordinary case — so a reader sent to the
        /// event path would have gone looking for a detach that never happens
        /// there. It stops
        /// glowing, once and for good: `Sync` is never called on a corpse again,
        /// so this single write is the last thing the property block ever
        /// carries. Owner decision: a body that keeps its remote-player rim, or
        /// keeps pulsing a combo window it can no longer be in, misreports who
        /// is still standing at the moment that mistake costs the most.
        ///
        /// `IsLocal` is deliberately NOT cleared. This client's own corpse is
        /// still its own body, and the aim-proxy self-hit guard
        /// (`AimProvider.TryAimProxy`) must keep excluding it — after death the
        /// camera sits on that body, so the cursor is over it constantly, and
        /// letting the proxy cast land there would revive the exact I1 defect
        /// the guard exists for.
        ///
        /// IT ALSO STOPS BEING AIMABLE AT (Stage 2 Task 45b, the Task 45a debt
        /// this closes). The self-hit guard above only ever excluded THIS
        /// client's own doll; a stranger's corpse kept three live `AimProxy_*`
        /// triggers, so the aim ray, the zone tint and the head-hover cue all
        /// went on reporting a target that is already dead — and a mob's corpse
        /// has no such thing by construction (`CorpseMechView.prefab` carries no
        /// proxy at all), which is the asymmetry the owner's "труп игрока = труп
        /// моба" rules out. Switched off here and — since Stage 2 Task 47c
        /// fix-round 1, for a doll held through its fade — in `SetAimable`;
        /// switched back on by `Bind` and by that same member on the way back.
        public void DetachAsCorpse()
        {
            ApplyEmission(Color.black);
            SetAimProxiesEnabled(false);
        }

        void ApplyEmission(Color emission)
        {
            _block.SetColor(EmissionColorId, emission);
            for (int i = 0; i < _renderers.Length; i++) _renderers[i].SetPropertyBlock(_block);
        }
    }
}
