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
        static readonly int[] MandatoryStates =
        {
            AnimIds.MechIdle, AnimIds.MechWalk, AnimIds.MechRun,
            AnimIds.MechPunch, AnimIds.MechShoot, AnimIds.MechDeath,
        };

        [SerializeField] Animator _animator;
        [SerializeField] Transform _visual;

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
            if (!_statesChecked)
            {
                // Full drift gate, once per pooled instance (ПБ14): a renamed
                // pack take would otherwise no-op silently at CrossFade time.
                foreach (int state in MandatoryStates)
                {
                    if (!_animator.HasState(0, state))
                        Debug.LogError("MobVisual: controller is missing a state: " + name);
                }
                _statesChecked = true;
            }
            _animator.Play(AnimIds.MechIdle, 0, 0f);
            _animator.Update(0f);
            // A mob can become visible mid-Telegraph/Fire (spawn into view).
            if (m.Ai == MobAiState.Telegraph) TriggerOneShot(AnimIds.MechPunch);
            else if (m.Ai == MobAiState.Fire) TriggerOneShot(AnimIds.MechShoot);
        }

        public void Sync(in MobState m, in MobVisualParams p)
        {
            _animator.speed = p.Paused ? 0f : 1f;

            Vector3 pos = transform.position;
            Vector3 moveDelta = _hasPrevPos ? pos - _prevPos : Vector3.zero;
            _prevPos = pos;
            _hasPrevPos = true;
            float speed = p.DeltaTime > 1e-6f ? moveDelta.magnitude / p.DeltaTime : 0f;

            // Facing: the gunner squares up to the player while repositioning/
            // firing (side strafe is honest, spec §3.3); movement otherwise.
            bool faceTarget = m.Type == MobType.Gunner
                && (m.Ai == MobAiState.Reposition || m.Ai == MobAiState.Fire);
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
                if (m.Ai == MobAiState.Telegraph) TriggerOneShot(AnimIds.MechPunch);
                else if (m.Ai == MobAiState.Fire) TriggerOneShot(AnimIds.MechShoot);
                _lastAi = m.Ai;
            }

            if (_inOneShot)
            {
                AnimatorStateInfo st = _animator.GetCurrentAnimatorStateInfo(0);
                bool oneShotState = st.shortNameHash == AnimIds.MechPunch
                    || st.shortNameHash == AnimIds.MechShoot;
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
            int state = _loco == Locomotion.Idle ? AnimIds.MechIdle
                : _loco == Locomotion.Walk ? AnimIds.MechWalk : AnimIds.MechRun;
            float duration = force
                ? p.OneShotCrossFadeSeconds : p.LocomotionCrossFadeSeconds;
            _animator.CrossFadeInFixedTime(state, duration, 0, 0f);
        }
    }
}
