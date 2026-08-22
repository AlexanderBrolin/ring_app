using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for one live loot container (spec §3.7/§3.11, Stage 3
    /// Task 31) — the crate, cache, dropped bundle or corpse marker standing
    /// where the simulation says a container is. Pooled and (re)bound purely by
    /// `ViewRegistry`, same contract as `MobView`/`PickupView`.
    ///
    /// WHY THE KIND IS DRAWN AT ALL, given `ContainerKind`'s own doc says it is
    /// read exactly once in the whole codebase and warns that a second branch
    /// on it reopens a spec decision (coordinator R-100). That warning is about
    /// BEHAVIOR — the same doc opens by calling `Kind` "the container's SKIN
    /// and spawn table only", and choosing a model IS the skin. Nothing here
    /// decides an outcome, a timer or a slot: those stay the one state machine
    /// spec §3.7 insisted on.
    ///
    /// TWO OF THE FIVE KINDS DRAW A MARKER INSTEAD OF A PROP. A `MobCorpse` or
    /// a `PlayerCorpse` container is a body that is ALREADY on the floor —
    /// `CorpseView` puts the mech there, `ViewRegistry` puts the collector's
    /// doll there — so standing a crate on top of it would draw the same death
    /// twice. What those two need is the marker spec §3.11 asks for — the tell
    /// that says there is something on this body worth taking: a small emissive
    /// sphere at its feet, which is what `ViewRegistry` hands them as their
    /// prefab. The distinction lives in the
    /// prefab table, not in this class — a view never asks what it is.
    ///
    /// `Kind` is recorded on `Bind` for exactly one reader, `ViewRegistry`'s
    /// retire path, which has to know which pool this instance came from — the
    /// same reason `MobView.Type` exists.
    public sealed class ContainerView : MonoBehaviour
    {
        [SerializeField] Transform _visual;

        /// Set first thing in `Bind`, from the bound container's own
        /// `ContainerState.Kind`.
        public ContainerKind Kind { get; private set; }

        /// Rebinds this (pooled) view. `scale` is read fresh from
        /// `GameFeelConfig` by the caller on every bind (hot-tweak contract,
        /// `PickupView.Bind`'s own doc) rather than baked on the prefab.
        ///
        /// A FRESH RANDOM YAW EACH BIND, cosmetic-only `UnityEngine.Random`,
        /// same reason and the same idiom `CorpseView.Spawn` uses: a pooled
        /// slot reused a dozen times over a match would otherwise put down a
        /// row of crates all facing exactly the same way, which reads as
        /// tiling rather than as loot.
        public void Bind(ContainerKind kind, float scale)
        {
            Kind = kind;
            Vector3 localScale = Vector3.one * scale;
            if (_visual.localScale != localScale) _visual.localScale = localScale;
            _visual.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            // Restored on rent, because the instance being rented may be the
            // one that just faded to black — see `PickupView.Bind`.
            _fade.Apply(1f);
        }

        /// Re-applies the authored colors scaled by how much fade is left
        /// (Stage 3 Т33d, bd `app-tut2`) — the box's twin of
        /// `PickupView.FadeEmission`.
        ///
        /// A LIT PROP DIMS RATHER THAN DISAPPEARS, and that is the accepted
        /// shape rather than an oversight: the crate, cache and bundle are pack
        /// models whose glow is emission over a lit body, so what the fade
        /// takes away is the tell that says "loot here" — exactly what
        /// `MobView` does to a mech, and the language the owner chose for all
        /// three classes. The corpse marker, being an unlit HDR sphere, goes
        /// the whole way to black.
        public void FadeEmission(float fadeRemaining) => _fade.Apply(fadeRemaining);

        void Awake() => _fade.Capture(gameObject);

        readonly EmissiveFade _fade = new EmissiveFade();
    }
}
