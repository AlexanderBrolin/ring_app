using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for one live ground pickup (spec §3.11, Stage 3 Task
    /// 31) — an emissive sphere on the floor where an energy cell is lying.
    /// Pooled and (re)bound purely by `ViewRegistry`, exactly like `MobView`
    /// and `ProjectileView`: nothing else instantiates, destroys or
    /// repositions one.
    ///
    /// NO STATE OF ITS OWN AND NO PER-FRAME WORK. A pickup does not move, does
    /// not animate and does not react — `PickupState` carries `Pos`, `Kind`,
    /// `Amount` and `Ttl`, and of those only the position is drawn today
    /// (`PickupKind` has exactly one member, `EnergyCell`, so there is nothing
    /// to branch a model on, and `Amount`/`Ttl` are decided by the server and
    /// belong to the HUD's half of the picture, not to a sphere on the floor).
    /// So this class is deliberately thinner than every other view here: a
    /// scale write on `Bind` and nothing else. The moment a second `PickupKind`
    /// arrives it grows a model choice — and that will be a table with a
    /// throwing default, like `ViewRegistry.PoolFor`, not a two-way ternary
    /// (the shape spec Р251 spent a whole stage undoing).
    ///
    /// THE COLLECTION RADIUS IS NOT DRAWN HERE and drawing it would be a lie:
    /// `HeroConfig.PickupRadius` is how far the COLLECTOR reaches, a property
    /// of the player and not of the cell, so a sphere that size would promise
    /// a pickup volume that moves with somebody else.
    public sealed class PickupView : MonoBehaviour
    {
        [SerializeField] Transform _visual;

        /// Rebinds this (pooled) view. `diameter` comes from
        /// `GameFeelConfig.PickupVisualDiameter` on every call rather than
        /// being baked on the prefab — the same hot-tweak contract
        /// `CorpseView.Spawn`'s `glowFadeSeconds` and `ProjectileView.Bind`'s
        /// tracer numbers already follow, so the owner can size the cells on
        /// the В1 playtest without a re-bootstrap.
        public void Bind(float diameter)
        {
            Vector3 scale = Vector3.one * diameter;
            if (_visual.localScale != scale) _visual.localScale = scale;
            // A POOLED VIEW COMES BACK AT WHATEVER BRIGHTNESS IT LEFT AT. The
            // instance that just faded to black is the one the next cell is
            // rented from, so the bind that puts it back on the floor is what
            // has to restore it — the same reason `Bind` rewrites the scale it
            // "already" has.
            _fade.Apply(1f);
        }

        /// Re-applies the authored color scaled by how much fade is left
        /// (Stage 3 Т33d, bd `app-tut2`) — the cell's twin of
        /// `MobView.FadeEmission`, and called INSTEAD of a position write for
        /// the same reason: a frame that stopped mentioning this cell says
        /// nothing about where it is, only that this client can no longer see
        /// it. A cell does not move, so nothing but the brightness has
        /// anywhere to go.
        public void FadeEmission(float fadeRemaining) => _fade.Apply(fadeRemaining);

        void Awake() => _fade.Capture(gameObject);

        readonly EmissiveFade _fade = new EmissiveFade();
    }
}
