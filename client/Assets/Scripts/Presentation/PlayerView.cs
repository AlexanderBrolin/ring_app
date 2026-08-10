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
    /// sole caller of `ApplyEmission`, so there is exactly one property-block
    /// write per doll per frame. Two accents compose here:
    ///  - the Dash↔Slide combo-window pulse (В1 fix-wave 1, owner playtest item
    ///    3 "мерцание сборщика"): a sine at `GameFeelConfig.LinkWindowFlashHz`
    ///    on unscaled time (hitstop/slow-mo never touch it) while
    ///    `PlayerState.PostDashSlideTimer`/`LinkWindowTimer` — either > 0f,
    ///    `PlayerMovementSystem`'s own doc — is open. `SimulationWorld.KillPlayer`
    ///    zeroes both, so a death mid-pulse lands back at black by itself on the
    ///    very next frame instead of needing a clear of its own;
    ///  - the remote-player rim (`GameFeelConfig.RemotePlayerEmission`, Stage 2
    ///    Task 45a): a steady tint every doll but this client's own wears, so a
    ///    stranger reads as a stranger at a glance. The registry decides which
    ///    doll is "own" and passes black for it — this class never asks.
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
            ApplyEmission(Color.black);
            // Stage 2 Task 45b: the other half of `DetachAsCorpse`'s proxy
            // switch-off. A doll only ever reaches this method by being rented
            // from the pool, and the pool is fed both by a slot leaving the
            // frame and — via `ViewRegistry.Clear` — by corpses at a match
            // restart, so the previous life's last act can be "proxies off".
            // Renting one back without this line would put a live player behind
            // a silhouette nothing can aim at.
            SetAimProxiesEnabled(true);
        }

        /// Per-frame accent pass, called once per render frame by
        /// `ViewRegistry.SyncPlayers` for every live doll (new AND continuing).
        /// The three parameters are the registry's own once-per-frame reads of
        /// `GameFeelConfig` — passed as plain values rather than handing this
        /// class a config reference of its own, the same "caller pre-reads the
        /// config, callee takes scalars" shape `MobView.Sync` already follows.
        /// `remoteAccent` is `Color.black` for this client's own doll.
        public void Sync(in PlayerState m, float linkWindowFlashHz,
            float linkWindowFlashBoost, Color remoteAccent)
        {
            Color emission = remoteAccent;

            if (m.PostDashSlideTimer > 0f || m.LinkWindowTimer > 0f)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(
                    Time.unscaledTime * linkWindowFlashHz * Mathf.PI * 2f);
                emission += LinkWindowFlashAccent * wave * linkWindowFlashBoost;
            }

            ApplyEmission(emission);
        }

        /// This doll has just become a corpse (Stage 2 Task 45a fix-round 1;
        /// `ViewRegistry.DispatchToDoll` does the bookkeeping — leaving the slot
        /// map for `_corpses` — and this is the view's own half of it). It stops
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
        /// моба" rules out. Switched off here, switched back on by `Bind`.
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
