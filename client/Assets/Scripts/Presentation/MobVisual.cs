using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Drives a mech's Animator from MobState (assets phase B spec §3.3):
    /// locomotion from the SCREEN-SPACE displacement of the root the registry
    /// just positioned (hitstop freezes/pause read as Idle by construction,
    /// Б7), one-shot Punch/Shoot on Ai transitions with a code-driven return
    /// (the Phase A robot controllers have no transitions), hysteresis + hold
    /// against threshold flicker (Б12). Pooled: Bind is the mandatory reset
    /// (SetActive(false) rewinds the state machine — the cache must follow,
    /// Б5); one-shot triggers land their state the same frame via Update(0f)
    /// (ПБ1 — a same-frame state check would otherwise cancel them).
    public sealed class MobVisual : MonoBehaviour
    {
        [SerializeField] Animator _animator;
        [SerializeField] Transform _visual;

        /// Which pack this prefab's controller came out of (Stage 3 Task 31).
        /// Serialized rather than derived from `MobState.Type` at `Bind` time
        /// for one reason: the model and its controller are chosen together at
        /// bootstrap time, and a runtime lookup keyed off the archetype would
        /// be a SECOND place that has to agree with that choice — the shape
        /// that let three archetypes quietly share the gunner's prefab in the
        /// first place. Defaults to `Mech`, which is what every prefab that
        /// predates this task carries.
        [SerializeField] AnimIds.MobClipFamily _clipFamily = AnimIds.MobClipFamily.Mech;

        AnimIds.MobClipSet _clips;

        enum Locomotion { Idle, Walk, Run }

        Locomotion _loco;
        float _holdTimer;
        MobAiState _lastAi;
        bool _inOneShot;
        Vector3 _prevPos;
        bool _hasPrevPos;
        bool _statesChecked;

        public void Bind(in MobState m, float visualScale)
        {
            if (_visual.localScale != Vector3.one * visualScale)
                _visual.localScale = Vector3.one * visualScale;
            // Pool-rebind hygiene: the previous life's facing must not leak
            // into a fresh spawn (audit fix ПБ19).
            _visual.localRotation = Quaternion.identity;
            _loco = Locomotion.Idle;
            _holdTimer = 0f;
            _lastAi = m.Ai;
            _inOneShot = false;
            _hasPrevPos = false;
            _animator.Rebind();
            // Resolved on every bind, not once: it costs one switch, and a
            // field read by every frame of Sync must not depend on whether
            // this instance happens to have passed the drift gate already.
            _clips = AnimIds.ClipsFor(_clipFamily);
            if (!_statesChecked)
            {
                // Full drift gate, once per pooled instance (ПБ14): a renamed
                // pack take would otherwise no-op silently at CrossFade time.
                // Task 31: the six checked states are this prefab's own family's
                // now, so a Sci-Fi model is measured against Attack/TurnOff
                // rather than against the mech pack's Punch/Shoot/Death.
                // Six explicit calls rather than a loop over a temporary array:
                // this class is on the pooled-spawn path the allocation tests
                // watch, and an array literal here would allocate once per
                // pooled instance for nothing.
                RequireState(_clips.Idle);
                RequireState(_clips.Walk);
                RequireState(_clips.Run);
                RequireState(_clips.Melee);
                RequireState(_clips.Ranged);
                RequireState(_clips.Death);
                _statesChecked = true;
            }
            _animator.Play(_clips.Idle, 0, 0f);
            _animator.Update(0f);
            // A mob can become visible mid-Telegraph/Fire (spawn into view).
            if (m.Ai == MobAiState.Telegraph) TriggerOneShot(_clips.Melee);
            else if (m.Ai == MobAiState.Fire) TriggerOneShot(_clips.Ranged);
        }

        public void Sync(in MobState m, in MobVisualParams p)
        {
            _animator.speed = p.Paused ? 0f : 1f;

            Vector3 pos = transform.position;
            Vector3 moveDelta = _hasPrevPos ? pos - _prevPos : Vector3.zero;
            _prevPos = pos;
            _hasPrevPos = true;
            float speed = p.DeltaTime > 1e-6f ? moveDelta.magnitude / p.DeltaTime : 0f;

            // Facing: a mob fighting at RANGE squares up to the player while
            // repositioning/firing (side strafe is honest, spec §3.3);
            // movement otherwise.
            //
            // THE ARCHETYPE TEST IS GONE, AND THAT IS A NARROWING, NOT A
            // WIDENING (Task 31, one of spec Р251's fourteen two-way branches
            // — removed instead of made four-way). `Reposition` and `Fire` are
            // set in `MobAiSystem.UpdateGunner` and NOWHERE else
            // (`MobAiSystem.cs:255,266`; `UpdateChaser` only ever sets Chase/
            // Telegraph/Recover), so the state alone already means "this mob is
            // fighting at range right now". Elite and the Director reuse both
            // procedures wholesale, picked by distance — so keying on the type
            // would have left a kiting Elite staring at its own path.
            bool faceTarget = m.Ai == MobAiState.Reposition || m.Ai == MobAiState.Fire;
            Vector3 faceDir = faceTarget ? p.PlayerPos - pos : moveDelta;
            faceDir.y = 0f;
            if (faceDir.sqrMagnitude > 1e-8f
                && (faceTarget || speed > p.WalkExitSpeed))
            {
                Quaternion target = Quaternion.LookRotation(faceDir.normalized, Vector3.up)
                    * Quaternion.AngleAxis(p.YawOffsetDeg, Vector3.up);
                _visual.rotation = Quaternion.RotateTowards(
                    _visual.rotation, target, p.TurnDegPerSec * p.DeltaTime);
            }

            // One-shot triggers on Ai transitions (Б9: ProjectileFired carries
            // the projectile's id — entry to Fire is the only reliable hook).
            if (m.Ai != _lastAi)
            {
                if (m.Ai == MobAiState.Telegraph) TriggerOneShot(_clips.Melee);
                else if (m.Ai == MobAiState.Fire) TriggerOneShot(_clips.Ranged);
                _lastAi = m.Ai;
            }

            if (_inOneShot)
            {
                AnimatorStateInfo st = _animator.GetCurrentAnimatorStateInfo(0);
                // One test, not two, when a family maps both one-shots onto the
                // same take (the Sci-Fi kit's `Attack`) — the hashes are equal
                // there and the || collapses on its own.
                bool oneShotState = st.shortNameHash == _clips.Melee
                    || st.shortNameHash == _clips.Ranged;
                bool finished = oneShotState && st.normalizedTime >= 1f
                    && !_animator.IsInTransition(0);
                if (!oneShotState || finished)
                {
                    _inOneShot = false;
                    CrossFadeLocomotion(in p, force: true);
                }
                else
                {
                    return; // let the one-shot play out
                }
            }

            UpdateLocomotion(speed, in p);
        }

        void RequireState(int stateHash)
        {
            if (!_animator.HasState(0, stateHash))
                Debug.LogError("MobVisual: controller is missing a state: " + name);
        }

        void TriggerOneShot(int stateHash)
        {
            _animator.Play(stateHash, 0, 0f);
            _animator.Update(0f); // land the state NOW — the same-frame check
                                  // below would otherwise cancel it (ПБ1)
            _inOneShot = true;
        }

        void UpdateLocomotion(float speed, in MobVisualParams p)
        {
            _holdTimer -= p.DeltaTime;
            Locomotion next = _loco;
            switch (_loco) // hysteresis: separate enter/exit thresholds (Б12)
            {
                case Locomotion.Idle:
                    if (speed > p.WalkEnterSpeed) next = Locomotion.Walk;
                    break;
                case Locomotion.Walk:
                    if (speed > p.RunEnterSpeed) next = Locomotion.Run;
                    else if (speed < p.WalkExitSpeed) next = Locomotion.Idle;
                    break;
                case Locomotion.Run:
                    if (speed < p.RunExitSpeed) next = Locomotion.Walk;
                    break;
            }
            if (next != _loco && _holdTimer <= 0f)
            {
                _loco = next;
                _holdTimer = p.HoldSeconds;
                CrossFadeLocomotion(in p, force: false);
            }
        }

        void CrossFadeLocomotion(in MobVisualParams p, bool force)
        {
            int state = _loco == Locomotion.Idle ? _clips.Idle
                : _loco == Locomotion.Walk ? _clips.Walk : _clips.Run;
            float duration = force
                ? p.OneShotCrossFadeSeconds : p.LocomotionCrossFadeSeconds;
            _animator.CrossFadeInFixedTime(state, duration, 0, 0f);
        }
    }
}
