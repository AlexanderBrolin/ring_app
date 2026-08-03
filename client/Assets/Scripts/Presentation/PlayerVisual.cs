using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Drives the collector doll (assets phase B spec §3.2): Speed from the
    /// SCREEN-SPACE displacement of the interpolated snapshot position (П-7 —
    /// pinned render pairs during hitstop/pause make the doll idle by
    /// construction; the root transform is never read), body facing toward
    /// movement (slowly toward aim when idle), procedural Spine+Chest
    /// world-space yaw toward the aim point layered over the Aim pose, dash
    /// lean composed as an OFFSET over a separately-tracked facing (never
    /// accumulated into the transform — ПБ8), Death01 on PlayerDied with the
    /// Aim layer faded out, Pistol_Shoot retrigger per own ProjectileFired.
    /// Events arrive via SimEventRouter's fan-out (П-1); WorldRestarted — by
    /// direct subscription (ViewRegistry's pattern).
    public sealed class PlayerVisual : MonoBehaviour
    {
        const int BaseLayer = 0;
        const int AimLayer = 1;

        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] Animator _animator;
        [SerializeField] Transform _visual;

        Transform _spine;
        Transform _chest;
        Quaternion _facing = Quaternion.identity;
        Vector3 _prevPos;
        bool _hasPrevPos;
        float _dashLean;
        float _aimWeight = 1f;
        bool _dead;

        void OnEnable() => _runner.WorldRestarted += HandleWorldRestarted;

        void OnDisable() => _runner.WorldRestarted -= HandleWorldRestarted;

        void Start()
        {
            // Bones resolve once; humanoid mapping is pack-name-agnostic (Б8).
            _spine = _animator.GetBoneTransform(HumanBodyBones.Spine);
            _chest = _animator.GetBoneTransform(HumanBodyBones.Chest);
            if (_chest == null)
            {
                Debug.LogError("PlayerVisual: Chest bone missing — spine-only aim yaw.");
                _chest = _spine;
            }
            if (!_animator.HasState(BaseLayer, AnimIds.Locomotion)
                || !_animator.HasState(BaseLayer, AnimIds.Death)
                || !_animator.HasState(AimLayer, AnimIds.PistolShoot)
                || !_animator.HasState(AimLayer, AnimIds.PistolAimNeutral))
                Debug.LogError("PlayerVisual: PlayerAnimator is missing a mandatory state.");
            // Controller default is 1 (preview shows the doll running) — the
            // gameplay doll must boot idle (Б7).
            _animator.SetFloat(AnimIds.Speed, 0f);
            _facing = _visual.rotation;
        }

        void LateUpdate()
        {
            if (_runner.World == null) return;
            float dt = Time.unscaledDeltaTime;
            _animator.speed = _runner.Paused ? 0f : 1f;

            Vector3 pos = _runner.RenderPlayerWorldPos;
            Vector3 moveDelta = _hasPrevPos ? pos - _prevPos : Vector3.zero;
            _prevPos = pos;
            _hasPrevPos = true;

            // Aim layer weight rides one place for both the death fade-out
            // and the restart fade-in (Б3).
            float weightTarget = _dead ? 0f : 1f;
            float weightRate = dt / Mathf.Max(_gameFeel.LocomotionCrossFadeSeconds, 1e-3f);
            _aimWeight = Mathf.MoveTowards(_aimWeight, weightTarget, weightRate);
            _animator.SetLayerWeight(AimLayer, _aimWeight);

            if (_dead) return; // corpse: no speed/facing/yaw/lean writes (Б3)

            float speed01 = 0f;
            if (dt > 1e-6f)
                speed01 = Mathf.Clamp01(
                    moveDelta.magnitude / dt / _runner.World.Config.Hero.MaxSpeed);
            _animator.SetFloat(AnimIds.Speed, speed01, _gameFeel.SpeedDampTime, dt);

            Vector3 aimW = SimSpace.ToWorld(_aimProvider.CurrentAimSimPos);
            Vector3 aimDir = aimW - pos;
            aimDir.y = 0f;

            // Facing tracked in a FIELD; the transform gets facing+lean as a
            // one-shot composition below — lean never accumulates (ПБ8).
            Quaternion yawOffset = Quaternion.AngleAxis(_gameFeel.PlayerYawOffsetDeg, Vector3.up);
            if (speed01 > _gameFeel.PlayerMoveThreshold01 && moveDelta.sqrMagnitude > 1e-10f)
            {
                Quaternion target = Quaternion.LookRotation(moveDelta.normalized, Vector3.up) * yawOffset;
                _facing = Quaternion.RotateTowards(_facing, target, _gameFeel.VisualTurnDegPerSec * dt);
            }
            else if (aimDir.sqrMagnitude > 1e-8f)
            {
                // Idle turn-in toward the aim (Б8): the doll never stays
                // back-to-cursor while shooting on the spot.
                Quaternion target = Quaternion.LookRotation(aimDir.normalized, Vector3.up) * yawOffset;
                _facing = Quaternion.RotateTowards(_facing, target, _gameFeel.IdleAimTurnDegPerSec * dt);
            }

            // Dash lean (7a): an offset over _facing, tilted toward DashDir.
            PlayerState player = _runner.RenderCurr.Player;
            float leanTarget = player.DashTimer > 0f ? _gameFeel.DashLeanDeg : 0f;
            _dashLean = Mathf.MoveTowards(_dashLean, leanTarget,
                _gameFeel.DashLeanDeg * dt / Mathf.Max(_gameFeel.DashLeanInOutSeconds, 1e-3f));
            Quaternion rotation = _facing;
            if (_dashLean > 0.01f)
            {
                Vector3 dashW = SimSpace.ToWorld(player.DashDir);
                if (dashW.sqrMagnitude > 1e-6f)
                    rotation = Quaternion.AngleAxis(_dashLean,
                        Vector3.Cross(Vector3.up, dashW.normalized)) * _facing;
            }
            _visual.rotation = rotation;

            // One-shot return on the Aim layer: no transitions exist in the
            // generated controller — the return is code-driven (Б9).
            if (AnimIds.OneShotFinished(_animator, AimLayer, AnimIds.PistolShoot))
                _animator.CrossFadeInFixedTime(AnimIds.PistolAimNeutral,
                    _gameFeel.OneShotCrossFadeSeconds, AimLayer, 0f);

            // Spine+Chest world-space yaw toward the aim point, applied LAST —
            // after facing/lean settle the Visual's frame (Б8). The Animator
            // wrote this frame's pose in PreLateUpdate; next frame it rewrites
            // the bones, so the offset never accumulates.
            if (aimDir.sqrMagnitude > 1e-8f)
            {
                // _visual.forward carries the model yaw offset — compensate,
                // or a non-zero PlayerYawOffsetDeg skews the aim by itself
                // and pins the spine against the clamp (audit fix ПБ19).
                // DeltaAngle keeps the offset-compensated sum in [-180;180] — a
                // 180° model offset would otherwise pin the clamp (Б1-веха fix).
                float yaw = Mathf.DeltaAngle(0f,
                    Vector3.SignedAngle(_visual.forward, aimDir.normalized, Vector3.up)
                    + _gameFeel.PlayerYawOffsetDeg);
                yaw = Mathf.Clamp(yaw, -_gameFeel.AimYawClampDeg, _gameFeel.AimYawClampDeg);
                float spineYaw = yaw * _gameFeel.SpineYawShare;
                float chestYaw = yaw - spineYaw;
                if (_spine != null)
                    _spine.rotation = Quaternion.AngleAxis(spineYaw, Vector3.up) * _spine.rotation;
                if (_chest != null)
                    _chest.rotation = Quaternion.AngleAxis(chestYaw, Vector3.up) * _chest.rotation;
                // Chest fallback (== _spine) receives both shares → full yaw
                // on the single bone, which is exactly the degraded intent.
            }
        }

        /// SimEventRouter fan-out slot (П-1): death and own-shot retrigger.
        public void HandleEvent(in SimEvent e)
        {
            switch (e.Kind)
            {
                case SimEventKind.PlayerDied:
                    _dead = true;
                    _animator.CrossFadeInFixedTime(AnimIds.Death,
                        _gameFeel.OneShotCrossFadeSeconds, BaseLayer, 0f);
                    break;
                case SimEventKind.ProjectileFired:
                    if (!_dead && e.Owner == ProjectileOwner.Player)
                    {
                        _animator.Play(AnimIds.PistolShoot, AimLayer, 0f);
                        _animator.Update(0f); // land the state this frame (ПБ1)
                    }
                    break;
            }
        }

        void HandleWorldRestarted()
        {
            _dead = false;
            _aimWeight = 1f;
            _animator.SetLayerWeight(AimLayer, 1f);
            _animator.Play(AnimIds.Locomotion, BaseLayer, 0f);
            _animator.Play(AnimIds.PistolAimNeutral, AimLayer, 0f);
            _animator.SetFloat(AnimIds.Speed, 0f);
            _dashLean = 0f;
            _hasPrevPos = false; // restart teleports the player — no ghost speed spike
        }
    }
}
