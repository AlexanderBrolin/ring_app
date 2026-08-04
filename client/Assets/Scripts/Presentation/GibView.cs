using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for a single pooled mech gib chunk (Task 24, revised
    /// per `app-1zf`'s investigation: George/Leela are monolithic skinned
    /// meshes — one mesh, one skin, `Head` exists only as a bone, not a
    /// separable sub-mesh — so gibs are PRIMITIVES ONLY, no `_Ring/Gibs/` FBX
    /// assets, no LFS). Two colliderless primitive children,
    /// `_boxVisual`/`_capsuleVisual` (`StageOneSceneBootstrap.
    /// GetOrCreateGibPrefab`), toggled randomly by `Spawn` for a little shape
    /// variety across a burst — same dual-visual-child shape `CorpseView`'s
    /// `_chaserVisual`/`_gunnerVisual` toggle already uses, just picked by
    /// `UnityEngine.Random` instead of `MobType` (cosmetic randomness is
    /// legal — casings precedent, `PersistentPropsDirector.SpawnCasing`).
    /// The Rigidbody/Collider live on the ROOT (a single small
    /// `SphereCollider`, shape-agnostic so either visual child can be active
    /// without touching the collider), on `PersistentPropsDirector.
    /// CasingsLayer` — physics is already isolated there (self-collision
    /// disabled in `PersistentPropsDirector.Awake`), no second dedicated
    /// layer/TagManager edit needed for gibs.
    ///
    /// Pooled and (re)spawned purely by `PersistentPropsDirector`'s
    /// `RingBuffer&lt;GibView&gt;` (fifth kind, `GameFeelConfig.
    /// GibPartsFifoLimit`), from the triggering `MobDied` event's own `Pos`
    /// plus a config-derived height (owner requirement, веха 3 — never from
    /// view/mesh/bone state; `PersistentPropsDirector.HandleMobDied`'s own
    /// doc). `Spawn`'s signature is deliberately just `(Vector3 pos, Vector3
    /// impulse)` — narrower than `CasingView.Spawn`'s `(pos, impulse, torque,
    /// settleSeconds, scale)` — so the settle window
    /// (`GameFeelConfig.GibPhysicsSeconds`) is threaded through the separate
    /// `SettleSeconds` property instead: the caller (`PersistentPropsDirector`,
    /// which already reads `_gameFeel` every event) sets it immediately
    /// before calling `Spawn`, same "caller pre-reads the config, value takes
    /// effect the very next spawn" hot-tweak contract `CasingView.Spawn`'s own
    /// `settleSeconds` parameter follows — just carried on a property instead
    /// of a sixth positional argument.
    public sealed class GibView : MonoBehaviour
    {
        // Cosmetic-only spin (UnityEngine.Random legal for VFX, casings
        // precedent) — structural, not a GameFeelConfig feel number, same
        // "positioning epsilon stays a code const" split
        // PersistentPropsDirector's own class doc already draws.
        const float SpinTorqueScale = 5f; // rad/s via VelocityChange, same convention as CasingView's torque

        [SerializeField] GameObject _boxVisual;
        [SerializeField] GameObject _capsuleVisual;

        Rigidbody _rb;
        float _elapsed;

        /// Settle window read fresh by the caller from
        /// `GameFeelConfig.GibPhysicsSeconds` every spawn (class doc) —
        /// defaults to a sane non-zero value so a slot Rent()ed without ever
        /// having `Spawn` called on it (shouldn't happen, defensive only)
        /// doesn't sit at `elapsed &gt;= 0 == settleSeconds` and freeze
        /// instantly.
        public float SettleSeconds { get; set; } = 3f;

        void Awake() => _rb = GetComponent<Rigidbody>();

        /// (Re)spawns this pooled instance at `pos` with the given launch
        /// impulse (`ForceMode.VelocityChange` — урок 28, same
        /// meters-per-second-direct contract `CasingView.Spawn`'s own doc
        /// explains: `ForceMode.Impulse` divides by the rigidbody's small
        /// mass and multiplies every number, which is how a casing once
        /// rocketed through the arena wall, app-xjz). Explicitly resets
        /// `isKinematic`/velocity — a FIFO-reused instance may still be
        /// frozen from its previous life in the ring buffer.
        public void Spawn(Vector3 pos, Vector3 impulse)
        {
            gameObject.SetActive(true);
            bool box = Random.value < 0.5f;
            _boxVisual.SetActive(box);
            _capsuleVisual.SetActive(!box);
            transform.SetPositionAndRotation(pos, Random.rotation);
            _rb.isKinematic = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.AddForce(impulse, ForceMode.VelocityChange);
            _rb.AddTorque(Random.insideUnitSphere * SpinTorqueScale, ForceMode.VelocityChange);
            _elapsed = 0f;
        }

        void Update()
        {
            if (_rb.isKinematic) return;
            _elapsed += Time.unscaledDeltaTime;
            // Shared freeze rule with CasingView — see PropSettle's class doc
            // (Task 24, QC15/PC14: one rule, not a second copy of it).
            if (PropSettle.ShouldFreeze(_rb, _elapsed, SettleSeconds))
                _rb.isKinematic = true;
        }
    }
}
