using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for a single pooled shell casing (Task 27, spec §3.11):
    /// spawned on `ProjectileFired`, tumbles under real PhysX for
    /// `GameFeelConfig.CasingPhysicsSeconds` and then freezes in place
    /// (`isKinematic = true`) for the rest of the match — casings never
    /// explicitly despawn, only get FIFO-reused once `PersistentPropsDirector`'s
    /// `RingBuffer&lt;CasingView&gt;` wraps around (spec: "живут до конца
    /// захода"). Lives on its own dedicated `PersistentPropsDirector.
    /// CasingsLayer` (9, review fix-round — NOT `GreyboxBuilder.
    /// CosmeticsLayer`/8, which the arena's floor/wall/obstacle colliders use:
    /// sharing a layer with the arena would mean disabling that layer's
    /// self-collision also disables casing-vs-arena collision, since
    /// `Physics.IgnoreLayerCollision` toggles a whole layer PAIR, not
    /// specific objects — casings fell through the floor under the original,
    /// single-layer version of this design). `PersistentPropsDirector.Awake`
    /// disables Casings-vs-Casings collision (only that one pair) so up to
    /// `GameFeelConfig.MaxCasings` casings never push each other around,
    /// while Casings-vs-Cosmetics (9×8) stays at Unity's default "collide" —
    /// that's what makes a casing actually bounce off the greybox geometry.
    ///
    /// Freeze condition (app-4qc, Б1 milestone find): a pure "timer expired"
    /// freeze pinned casings in place mid-air whenever the floor's degenerate
    /// collider (see `GreyboxBuilder`'s class doc) had just launched them
    /// upward — the timer ran out before they ever landed. `Update` now also
    /// requires the casing to actually be at rest (low linear velocity)
    /// before freezing, with a hard-cap timer as a structural backstop so a
    /// casing that somehow never settles (e.g. stuck oscillating in a
    /// geometry seam) still stops paying PhysX cost eventually. Task 24
    /// (QC15/PC14): this freeze predicate is now the shared `PropSettle.
    /// ShouldFreeze` helper — `GibView` needs the exact same rule, so it
    /// moved out to a class of its own rather than being copied a second
    /// time (Reuse &gt; duplication, AGENT.md §4); behavior here is
    /// unchanged, only the down-counting `_settleTimer`/`_hardCapTimer` pair
    /// became a single up-counting `_elapsed` that `PropSettle` itself
    /// compares against `settleSeconds`.
    public sealed class CasingView : MonoBehaviour
    {
        Rigidbody _rb;
        float _elapsed;
        float _settleSeconds;

        void Awake() => _rb = GetComponent<Rigidbody>();

        /// (Re)spawns this pooled instance at `pos` with the given ejection
        /// impulse/spin (Presentation-only cosmetic randomness computed by the
        /// caller — `UnityEngine.Random` is fine for VFX, spec allows it).
        /// `settleSeconds` comes straight from `GameFeelConfig.
        /// CasingPhysicsSeconds` every call, so a PlayMode hot-tweak of that
        /// value takes effect on the very next shot, same contract as
        /// `ProjectileView.Bind`'s `tracerFadeSeconds` parameter. Explicitly
        /// resets `isKinematic` to false — a FIFO-reused instance may still be
        /// frozen from its previous life in the ring buffer.
        /// `scale` comes from `GameFeelConfig.CasingScale` every call, same
        /// live hot-tweak contract as `settleSeconds` (Б1 fix-wave 3: owner
        /// playtest feedback — the baked prefab's 5cm casing was unreadable
        /// from the ¾ camera; runtime scale now overrides it every shot).
        public void Spawn(Vector3 pos, Vector3 impulse, Vector3 torque, float settleSeconds, float scale)
        {
            gameObject.SetActive(true);
            transform.localScale = new Vector3(scale, scale * 1.2f, scale);
            transform.SetPositionAndRotation(pos, Quaternion.identity);
            _rb.isKinematic = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            // VelocityChange: the SO numbers are meters-per-second (and radians-per-
            // second for spin) applied directly. ForceMode.Impulse divided by the
            // 0.01 kg mass and multiplied every number by 100 — "up 0.5" launched a
            // 50 m/s rocket and the side scatter tunneled through the arena wall
            // (app-xjz, Э1 bug unmasked at milestone Б1).
            _rb.AddForce(impulse, ForceMode.VelocityChange);
            _rb.AddTorque(torque, ForceMode.VelocityChange);
            _elapsed = 0f;
            _settleSeconds = settleSeconds;
        }

        void Update()
        {
            if (_rb.isKinematic) return;
            _elapsed += Time.unscaledDeltaTime;
            // Freeze only once the casing actually came to rest on the floor —
            // the old pure-timer freeze pinned mid-air casings (app-4qc);
            // PropSettle's own hard cap still guarantees the PhysX cost ends
            // for every casing (class doc).
            if (PropSettle.ShouldFreeze(_rb, _elapsed, _settleSeconds))
                _rb.isKinematic = true;
        }
    }
}
