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
        // Stage 2 Task 45b: the same two sockets `PlayerView` publishes to the
        // runtime, wired here as well so they join the owner's PlayMode loop.
        // Two references to one pair of objects is not a second home for them —
        // the objects themselves are the single home; this component is the
        // EDITOR conversation with the asset (push every frame, capture on
        // demand) and `PlayerView` is the runtime read, and neither can do the
        // other's job (see the class doc on why this component exists at all).
        [SerializeField] Transform _muzzleSocket;
        [SerializeField] Transform _ejectSocket;

        Vector3 _appliedGunPosition;
        Vector3 _appliedGunEuler;
        bool _gunApplied;
        Vector3 _appliedMuzzlePosition;
        Vector3 _appliedEjectPosition;
        Vector3 _appliedEjectEuler;
        bool _socketsApplied;

        /// Config values are pushed to the transform ONLY when they change
        /// (Б1 wave 4), so the owner can also drag the Gun with the scene gizmo
        /// in PlayMode and then persist the result through the context menu
        /// below — an unconditional push would fight the gizmo every frame.
        /// `LateUpdate`, like the block this replaces: the Animator writes the
        /// hand bone in PreLateUpdate, and the gun rides that bone.
        void LateUpdate()
        {
            if (_gameFeel == null) return;
            PushGunPose();
            PushSocketPoses();
        }

        void PushGunPose()
        {
            if (_gun == null) return;
            if (_gunApplied
                && _appliedGunPosition == _gameFeel.GunLocalPosition
                && _appliedGunEuler == _gameFeel.GunLocalEuler) return;

            _gun.localPosition = _gameFeel.GunLocalPosition;
            _gun.localEulerAngles = _gameFeel.GunLocalEuler;
            _appliedGunPosition = _gameFeel.GunLocalPosition;
            _appliedGunEuler = _gameFeel.GunLocalEuler;
            _gunApplied = true;
        }

        /// Stage 2 Task 45b: the muzzle and ejection-port sockets get the same
        /// change-guarded push the gun itself does, and for the same reason —
        /// an unconditional write would fight the scene gizmo the owner is
        /// dragging them with. One guard for the pair rather than one each:
        /// they are tuned in the same sitting and captured by the same menu
        /// item below, so splitting the bookkeeping would buy nothing.
        void PushSocketPoses()
        {
            if (_muzzleSocket == null || _ejectSocket == null) return;
            if (_socketsApplied
                && _appliedMuzzlePosition == _gameFeel.GunMuzzleLocalPosition
                && _appliedEjectPosition == _gameFeel.GunEjectLocalPosition
                && _appliedEjectEuler == _gameFeel.GunEjectLocalEuler) return;

            _muzzleSocket.localPosition = _gameFeel.GunMuzzleLocalPosition;
            _ejectSocket.localPosition = _gameFeel.GunEjectLocalPosition;
            _ejectSocket.localEulerAngles = _gameFeel.GunEjectLocalEuler;
            _appliedMuzzlePosition = _gameFeel.GunMuzzleLocalPosition;
            _appliedEjectPosition = _gameFeel.GunEjectLocalPosition;
            _appliedEjectEuler = _gameFeel.GunEjectLocalEuler;
            _socketsApplied = true;
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

        /// Stage 2 Task 45b, same workflow one level down: drag the Muzzle and
        /// EjectPort children with the gizmo until the flash leaves the barrel's
        /// mouth and the brass leaves the slide, then capture. A SEPARATE menu
        /// item from the gun's own above, because the two are separate sittings
        /// — re-gripping the pistol and placing its muzzle are different
        /// questions, and a single item would force whoever answers one to
        /// commit the other's current numbers too. The port's ROTATION is
        /// captured and the muzzle's is not: only the port has a consumer for it
        /// (`GameFeelConfig.GunEjectLocalEuler`'s own doc).
        [ContextMenu("Capture Gun Sockets To Config")]
        void CaptureGunSocketsToConfig()
        {
            if (_muzzleSocket == null || _ejectSocket == null || _gameFeel == null) return;
            _gameFeel.GunMuzzleLocalPosition = _muzzleSocket.localPosition;
            _gameFeel.GunEjectLocalPosition = _ejectSocket.localPosition;
            _gameFeel.GunEjectLocalEuler = _ejectSocket.localEulerAngles;
            _appliedMuzzlePosition = _gameFeel.GunMuzzleLocalPosition;
            _appliedEjectPosition = _gameFeel.GunEjectLocalPosition;
            _appliedEjectEuler = _gameFeel.GunEjectLocalEuler;
            _socketsApplied = true;
            UnityEditor.EditorUtility.SetDirty(_gameFeel);
            Debug.Log("PlayerGunTuner: gun sockets captured to GameFeelConfig: muzzle "
                + _muzzleSocket.localPosition + ", eject " + _ejectSocket.localPosition
                + " / " + _ejectSocket.localEulerAngles);
        }
#endif
    }
}
