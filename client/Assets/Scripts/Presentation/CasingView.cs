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
    public sealed class CasingView : MonoBehaviour
    {
        Rigidbody _rb;
        float _settleTimer;

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
        public void Spawn(Vector3 pos, Vector3 impulse, Vector3 torque, float settleSeconds)
        {
            gameObject.SetActive(true);
            transform.SetPositionAndRotation(pos, Quaternion.identity);
            _rb.isKinematic = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.AddForce(impulse, ForceMode.Impulse);
            _rb.AddTorque(torque, ForceMode.Impulse);
            _settleTimer = settleSeconds;
        }

        void Update()
        {
            if (_settleTimer <= 0f) return;
            _settleTimer -= Time.unscaledDeltaTime;
            if (_settleTimer <= 0f) _rb.isKinematic = true;
        }
    }
}
