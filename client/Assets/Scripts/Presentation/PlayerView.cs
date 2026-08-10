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
    /// already does for mobs). The root does not rotate and carries no renderer
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

        Renderer[] _renderers;
        MaterialPropertyBlock _block;

        /// Whether the slot this doll is bound to is this client's own
        /// (`RenderSnapshot.LocalPlayerIndex` — the registry decides, this class
        /// records). `AimProvider` reads it to tell "my own doll" from
        /// "somebody else's" when a proxy raycast lands on a `PlayerView`:
        /// before pooling, the mere PRESENCE of this component meant "mine",
        /// because only the local player ever had a doll at all.
        public bool IsLocal { get; private set; }

        /// Cached in `Awake` — the pose half of this pair (see the class doc).
        public PlayerVisual Visual { get; private set; }

        void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _block = new MaterialPropertyBlock();
            Visual = GetComponent<PlayerVisual>();
            if (Visual == null)
                Debug.LogError("PlayerView: no PlayerVisual on the doll — it will not animate.");
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
        public void DetachAsCorpse() => ApplyEmission(Color.black);

        void ApplyEmission(Color emission)
        {
            _block.SetColor(EmissionColorId, emission);
            for (int i = 0; i < _renderers.Length; i++) _renderers[i].SetPropertyBlock(_block);
        }
    }
}
