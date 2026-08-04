using UnityEngine;

namespace Ring.Presentation
{
    /// Which mech body part a gib mesh represents (T24-2, owner-approved
    /// Blender split — see `GibView`'s class doc for the full story). Drives
    /// per-part spawn height in `PersistentPropsDirector.SpawnFullExplodeGibs`
    /// (head/torso/arms/legs read different belts off the dying mob's own
    /// `MobSimConfig`) and lets `SpawnHeadGib` pick the one array element that
    /// matters for a headshot kill without hardcoding an index.
    public enum GibPartKind { Head, Torso, ArmL, ArmR, LegL, LegR }

    /// Presentation view for a single pooled mech gib chunk.
    ///
    /// T24-2 (owner-approved Blender split, spec item — supersedes Task 24's
    /// `app-1zf` finding): George/Leela WERE monolithic skinned meshes with no
    /// separable sub-mesh, so the original Task 24 shipped two toggleable
    /// PRIMITIVE children (`_boxVisual`/`_capsuleVisual`) instead of real
    /// parts. The owner has since Blender-split both mechs into real,
    /// capped-cut part meshes (`George_Parts.fbx`: Head/ArmL/ArmR/LegL/LegR/
    /// Torso; `Leela_Parts.fbx`: Head/LegL/LegR/Torso — `_Ring/Gibs/`,
    /// `ThirdPartyImportBootstrap`), so this class now swaps in the ACTUAL
    /// part mesh at `Spawn` instead of picking between two primitive shapes.
    /// The two primitive children and their random-pick logic are gone; the
    /// prefab now carries a single `MeshFilter`/`MeshRenderer` pair
    /// (`StageOneSceneBootstrap.GetOrCreateGibPrefab`) whose `sharedMesh`/
    /// `sharedMaterial` `Spawn` overwrites every call, same "reset like
    /// fresh" contract every other pooled cosmetic in this namespace follows.
    ///
    /// The Rigidbody/Collider still live on the ROOT, alongside the
    /// MeshFilter/MeshRenderer now (one GameObject total — the old
    /// shape-agnostic-SphereCollider-plus-two-child-visuals split existed
    /// only to let either primitive toggle without touching the collider;
    /// with a single real mesh there is nothing left to toggle). No
    /// `MeshCollider` (task brief — gibs are cosmetic-only, a per-part mesh
    /// collider would be real, unbounded PhysX cost for zero gameplay
    /// benefit): `Spawn` instead approximates the `SphereCollider`'s
    /// radius/center from the swapped-in mesh's own `bounds` — good enough
    /// for a tumbling chunk that only needs to look like it's colliding with
    /// the floor/other gibs, on `PersistentPropsDirector.CasingsLayer`
    /// (physics already isolated there, self-collision disabled in
    /// `PersistentPropsDirector.Awake` — unchanged from Task 24).
    ///
    /// Pooled and (re)spawned purely by `PersistentPropsDirector`'s
    /// `RingBuffer&lt;GibView&gt;` (`GameFeelConfig.GibPartsFifoLimit`), from
    /// the triggering `MobDied` event's own `Pos` plus a config/belt-derived
    /// height (owner requirement, веха 3 — never from view/mesh/bone state;
    /// `PersistentPropsDirector.HandleMobDied`'s own doc) — the mesh/material
    /// swapped in at `Spawn` is presentation-only content, it changes nothing
    /// about where a gib is allowed to appear.
    public sealed class GibView : MonoBehaviour
    {
        // Cosmetic-only spin (UnityEngine.Random legal for VFX, casings
        // precedent) — structural, not a GameFeelConfig feel number, same
        // "positioning epsilon stays a code const" split
        // PersistentPropsDirector's own class doc already draws.
        const float SpinTorqueScale = 5f; // rad/s via VelocityChange, same convention as CasingView's torque

        // T24-2: sanity floor for the mesh.bounds-derived collider radius —
        // an empty/degenerate mesh (shouldn't happen, defensive only) must
        // never collapse the SphereCollider to zero.
        const float MinColliderRadius = 0.02f;

        [SerializeField] MeshFilter _meshFilter;
        [SerializeField] MeshRenderer _meshRenderer;

        Rigidbody _rb;
        SphereCollider _collider;
        float _elapsed;

        /// Settle window read fresh by the caller from
        /// `GameFeelConfig.GibPhysicsSeconds` every spawn (class doc) —
        /// defaults to a sane non-zero value so a slot Rent()ed without ever
        /// having `Spawn` called on it (shouldn't happen, defensive only)
        /// doesn't sit at `elapsed &gt;= 0 == settleSeconds` and freeze
        /// instantly.
        public float SettleSeconds { get; set; } = 3f;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<SphereCollider>();
        }

        /// (Re)spawns this pooled instance at `pos` with the given launch
        /// impulse (`ForceMode.VelocityChange` — урок 28, same
        /// meters-per-second-direct contract `CasingView.Spawn`'s own doc
        /// explains). Explicitly resets `isKinematic`/velocity — a
        /// FIFO-reused instance may still be frozen from its previous life in
        /// the ring buffer.
        ///
        /// T24-2: `mesh`/`material` are the actual part content (one of
        /// `PersistentPropsDirector`'s per-archetype `_chaserParts`/
        /// `_gunnerParts` entries, plus that archetype's single remap
        /// material — every mech in this pack uses exactly one material
        /// slot, verified against the source FBX, so no per-part material
        /// array is needed); `scale` mirrors the dying mob's own
        /// `ChaserVisualScale`/`GunnerVisualScale` (same number
        /// `CorpseView.Spawn`'s own `visualScale` parameter already applies
        /// to the whole corpse — a gib chunk cut from the same mesh has to
        /// agree with it or a Gunner's parts would visibly mismatch its own
        /// corpse/live-mob size). The `SphereCollider` is resized/recentred
        /// from `mesh.bounds` in the mesh's own LOCAL (unscaled) space — the
        /// `transform.localScale` write below is what the physics engine
        /// then applies on top (Unity scales a collider's local
        /// radius/center by the transform's own lossyScale automatically),
        /// so this method must NOT premultiply by `scale` itself.
        public void Spawn(Vector3 pos, Vector3 impulse, Mesh mesh, Material material, float scale)
        {
            gameObject.SetActive(true);
            _meshFilter.sharedMesh = mesh;
            _meshRenderer.sharedMaterial = material;
            transform.SetPositionAndRotation(pos, Random.rotation);
            transform.localScale = Vector3.one * scale;
            _collider.center = mesh.bounds.center;
            _collider.radius = Mathf.Max(mesh.bounds.extents.magnitude, MinColliderRadius);
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

        /// T24-2: classifies a gib part `Mesh` asset by its own `name` —
        /// Unity names an FBX's mesh sub-assets after the source file's
        /// geometry-node names, which for this Blender split are
        /// "George_Head"/"Leela_LegL"/etc., often with Blender's own
        /// "_mesh" data-block suffix still attached (verified against the
        /// source FBX's own embedded strings — both forms are handled the
        /// same way here since this only checks for a `Contains`, never an
        /// exact match). Ordinal, case-sensitive: the six part-kind tokens
        /// are fixed English identifiers baked into the export, not
        /// user-facing text that would ever need culture-aware comparison.
        /// Falls back to `Torso` — the one part name that shares no
        /// substring with any other kind's token, so every mesh that isn't
        /// explicitly Head/Arm*/Leg* really is the torso.
        public static GibPartKind ClassifyPart(string meshName)
        {
            if (meshName.IndexOf("Head", System.StringComparison.Ordinal) >= 0) return GibPartKind.Head;
            if (meshName.IndexOf("ArmL", System.StringComparison.Ordinal) >= 0) return GibPartKind.ArmL;
            if (meshName.IndexOf("ArmR", System.StringComparison.Ordinal) >= 0) return GibPartKind.ArmR;
            if (meshName.IndexOf("LegL", System.StringComparison.Ordinal) >= 0) return GibPartKind.LegL;
            if (meshName.IndexOf("LegR", System.StringComparison.Ordinal) >= 0) return GibPartKind.LegR;
            return GibPartKind.Torso;
        }
    }
}
