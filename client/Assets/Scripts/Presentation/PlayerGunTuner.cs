using UnityEngine;

namespace Ring.Presentation
{
    /// The owner's PlayMode gun-tuning loop (Stage 2 Task 45a, spec §3.12 Р97),
    /// moved off `PlayerVisual` in one piece: the live config→transform push AND
    /// the `Capture Gun Transform To Config` context menu AND the three
    /// applied-value fields the two share. Р97 is why it moved WHOLE rather than
    /// as an extracted method — the menu item is private, lives under
    /// `#if UNITY_EDITOR`, and writes state the push reads back, so a public seam
    /// into another class's private editor state would have been worse than the
    /// move.
    ///
    /// IT LIVES HERE BECAUSE THE DOLL IS POOLED NOW. `PlayerVisual` is
    /// instantiated from a prefab, once per player slot, and a pooled view holds
    /// no `ScriptableObject` reference of its own (spec §3.12) — every number it
    /// needs arrives through `PlayerVisualParams`. The gun pose is the one
    /// exception that cannot travel that way, because the workflow is a
    /// two-directional EDITOR conversation with the asset (read it every frame,
    /// write it back on demand), not a per-frame feel number. Keeping that
    /// conversation on its own editor-only component is what lets the doll stay
    /// reference-free without costing the owner the workflow.
    ///
    /// THE CLASS COMPILES IN A PLAYER BUILD, ITS BODY DOES NOT. Every member is
    /// guarded — same guard the block on `PlayerVisual` already carried — while
    /// the type itself stays, so the shipped doll prefab keeps a component whose
    /// script resolves instead of logging "the referenced script is missing" once
    /// per pooled instantiation. A build carries the pose baked into the prefab
    /// by `StageOneSceneBootstrap`, which is exactly what it carried before.
    public sealed class PlayerGunTuner : MonoBehaviour
    {
#if UNITY_EDITOR
        [SerializeField] Ring.Data.GameFeelConfig _gameFeel;
        [SerializeField] Transform _gun;

        Vector3 _appliedGunPosition;
        Vector3 _appliedGunEuler;
        bool _gunApplied;

        /// Config values are pushed to the transform ONLY when they change
        /// (Б1 wave 4), so the owner can also drag the Gun with the scene gizmo
        /// in PlayMode and then persist the result through the context menu
        /// below — an unconditional push would fight the gizmo every frame.
        /// `LateUpdate`, like the block this replaces: the Animator writes the
        /// hand bone in PreLateUpdate, and the gun rides that bone.
        void LateUpdate()
        {
            if (_gun == null || _gameFeel == null) return;
            if (_gunApplied
                && _appliedGunPosition == _gameFeel.GunLocalPosition
                && _appliedGunEuler == _gameFeel.GunLocalEuler) return;

            _gun.localPosition = _gameFeel.GunLocalPosition;
            _gun.localEulerAngles = _gameFeel.GunLocalEuler;
            _appliedGunPosition = _gameFeel.GunLocalPosition;
            _appliedGunEuler = _gameFeel.GunLocalEuler;
            _gunApplied = true;
        }

        /// Owner workflow (Б1 wave 4): drag the Gun with the gizmo in PlayMode
        /// until the grip looks right, then right-click this component →
        /// Capture Gun Transform To Config. SO edits made in PlayMode persist,
        /// so the captured numbers survive exiting play; the next bootstrap
        /// `Apply` bakes them into the doll prefab for builds.
        [ContextMenu("Capture Gun Transform To Config")]
        void CaptureGunTransformToConfig()
        {
            if (_gun == null || _gameFeel == null) return;
            _gameFeel.GunLocalPosition = _gun.localPosition;
            _gameFeel.GunLocalEuler = _gun.localEulerAngles;
            _appliedGunPosition = _gameFeel.GunLocalPosition;
            _appliedGunEuler = _gameFeel.GunLocalEuler;
            _gunApplied = true;
            UnityEditor.EditorUtility.SetDirty(_gameFeel);
            Debug.Log("PlayerGunTuner: gun transform captured to GameFeelConfig: "
                + _gun.localPosition + " / " + _gun.localEulerAngles);
        }
#endif
    }
}
