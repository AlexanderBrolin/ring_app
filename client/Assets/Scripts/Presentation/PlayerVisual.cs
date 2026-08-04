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
    ///
    /// В1 fix-wave 1 (owner playtest feedback, item 3 "мерцание сборщика"):
    /// pulses the doll's emission while a Dash↔Slide combo window is open
    /// (`PlayerState.PostDashSlideTimer`/`LinkWindowTimer` — either > 0f,
    /// `PlayerMovementSystem`'s own doc). Same `MaterialPropertyBlock`/
    /// `_EmissionColor` idiom `MobView`'s Gunner "aiming" glint uses (a sine
    /// wave at `GameFeelConfig.LinkWindowFlashHz`, unscaled time so hitstop/
    /// slow-mo never touches it) — `_renderers` is cached once here the same
    /// way `MobView`/`CorpseView` cache theirs (`GetComponentsInChildren
    /// &lt;Renderer&gt;(true)`, one shared `MaterialPropertyBlock`, never a
    /// material instance, П-2). `LinkWindowFlashAccent` reuses
    /// `PlayerEmissive`/`DashGlowView`'s own established player-signature
    /// cyan (Э1) rather than inventing a new accent color; `LinkWindowFlashBoost`
    /// is a separate hot-tweak multiplier on the pulse's peak intensity, same
    /// split every other Presentation accent-color-vs-SO-number pair already
    /// makes (`MobView`'s `GunnerGlintAccent` constant vs. its `Hz` consts).
    public sealed class PlayerVisual : MonoBehaviour
    {
        const int BaseLayer = 0;
        const int AimLayer = 1;

        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        // = PlayerEmissive/DashGlowView's own accent (Э1) — reused, not reinvented.
        static readonly Color LinkWindowFlashAccent = new Color(0f, 2.5f, 3f);

        [SerializeField] SimulationRunner _runner;
        [SerializeField] AimProvider _aimProvider;
        [SerializeField] GameFeelConfig _gameFeel;
        [SerializeField] Animator _animator;
        [SerializeField] Transform _visual;
        [SerializeField] Transform _gun;

        // Task Т23 (ADR-002 A10 amendment): the doll's slide pose sequence —
        // Start (one-shot) -> Loop (held) -> Exit (one-shot back to
        // locomotion), driven off RenderCurr.Player.SlideTimer the same
        // "code-drives-the-hash, no controller transitions" way the Aim
        // layer's own one-shot return already works (AnimIds.OneShotFinished
        // + CrossFadeInFixedTime).
        enum SlidePhase { None, Start, Loop, Exit }

        Transform _spine;
        Transform _chest;
        Quaternion _facing = Quaternion.identity;
        Vector3 _prevPos;
        bool _hasPrevPos;
        float _dashLean01;
        float _aimWeight = 1f;
        bool _dead;
        SlidePhase _slidePhase = SlidePhase.None;

        Renderer[] _renderers;
        MaterialPropertyBlock _block;

#if UNITY_EDITOR
        Vector3 _appliedGunPosition;
        Vector3 _appliedGunEuler;
        bool _gunApplied;
#endif

        void OnEnable() => _runner.WorldRestarted += HandleWorldRestarted;

        void OnDisable() => _runner.WorldRestarted -= HandleWorldRestarted;

        void Start()
        {
            // Renderers resolve once too (В1 fix-wave 1, same "resolve once"
            // rule as the bones below) — GetComponentsInChildren covers the
            // whole doll hierarchy under this component's own GameObject,
            // same as MobView/CorpseView's Awake-time cache.
            _renderers = GetComponentsInChildren<Renderer>(true);
            _block = new MaterialPropertyBlock();

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
                || !_animator.HasState(BaseLayer, AnimIds.SlideStart)
                || !_animator.HasState(BaseLayer, AnimIds.SlideLoop)
                || !_animator.HasState(BaseLayer, AnimIds.SlideExit)
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

#if UNITY_EDITOR
            // Editor-only: builds carry the baked scene values; this live push exists for the owner's PlayMode tuning loop.
            // Gun tuning is gizmo-friendly (Б1 wave 4): config values are pushed to
            // the transform ONLY when they change, so the owner can also drag the
            // Gun with the scene gizmo in PlayMode and then persist the result via
            // the CaptureGunTransform context menu below.
            if (_gun != null
                && (!_gunApplied
                    || _appliedGunPosition != _gameFeel.GunLocalPosition
                    || _appliedGunEuler != _gameFeel.GunLocalEuler))
            {
                _gun.localPosition = _gameFeel.GunLocalPosition;
                _gun.localEulerAngles = _gameFeel.GunLocalEuler;
                _appliedGunPosition = _gameFeel.GunLocalPosition;
                _appliedGunEuler = _gameFeel.GunLocalEuler;
                _gunApplied = true;
            }
#endif

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

            UpdateLinkWindowFlash(in player);
            UpdateSlideAnimation(in player, speed01);

            float leanTarget01 = player.DashTimer > 0f ? 1f : 0f;
            _dashLean01 = Mathf.MoveTowards(_dashLean01, leanTarget01,
                dt / Mathf.Max(_gameFeel.DashLeanInOutSeconds, 1e-3f));
            Quaternion rotation = _facing;
            // Task Т23: the dash lean is a rotation OFFSET on top of the
            // slide pose the Animator is already playing — while SlideTimer
            // is open, skip it outright rather than let it fight the pose
            // (DashTimer/SlideTimer are mutually exclusive in the sim, but
            // _dashLean01 itself decays over DashLeanInOutSeconds, so a
            // linked slide starting right after a dash ends can still catch
            // it mid-decay).
            if (_dashLean01 > 0.001f && player.SlideTimer <= 0f)
            {
                Vector3 dashW = SimSpace.ToWorld(player.DashDir);
                if (dashW.sqrMagnitude > 1e-6f)
                    rotation = Quaternion.AngleAxis(_dashLean01 * _gameFeel.DashLeanDeg,
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

        /// В1 fix-wave 1 (class doc): the combo-window pulse, computed fresh
        /// every render frame from the interpolated snapshot's own timers —
        /// same "read RenderCurr, never branch on hitstop" contract every
        /// other per-frame accent in this class already follows. Unconditional
        /// `ApplyLinkWindowEmission` call every frame (black when the window
        /// is closed) mirrors `MobView.Sync`'s own shape, so a window closing
        /// mid-pulse always lands back at black the very next frame instead
        /// of needing a separate "clear" path.
        void UpdateLinkWindowFlash(in PlayerState player)
        {
            bool open = player.PostDashSlideTimer > 0f || player.LinkWindowTimer > 0f;
            if (!open)
            {
                ApplyLinkWindowEmission(Color.black);
                return;
            }
            float wave = 0.5f + 0.5f * Mathf.Sin(
                Time.unscaledTime * _gameFeel.LinkWindowFlashHz * Mathf.PI * 2f);
            ApplyLinkWindowEmission(LinkWindowFlashAccent * wave * _gameFeel.LinkWindowFlashBoost);
        }

        /// Task Т23 (ADR-002 A10 amendment): steps the slide pose FSM from
        /// SlideTimer alone — SlideTimer > 0 is the sim's own "sliding right
        /// now" predicate (ProjectileSystem/Spread/SimulationRunner already
        /// read the exact same field, class docs of each). Start plays out
        /// once (or is cut short if the slide itself ends first — SlideDuration
        /// is only HeroConfig.SlideDuration=0.52s, shorter than Start can run
        /// long), then Loop holds for the remainder, then Exit plays once and
        /// hands back to Locomotion. Exit is allowed to cut short: a player
        /// still holding move input drops straight into Locomotion instead of
        /// waiting the stand-up clip out ("keep it snappy" — spec).
        void UpdateSlideAnimation(in PlayerState player, float speed01)
        {
            bool sliding = player.SlideTimer > 0f;
            switch (_slidePhase)
            {
                case SlidePhase.None:
                    if (sliding) EnterSlidePhase(SlidePhase.Start, AnimIds.SlideStart);
                    break;
                case SlidePhase.Start:
                    if (!sliding) EnterSlidePhase(SlidePhase.Exit, AnimIds.SlideExit);
                    else if (AnimIds.OneShotFinished(_animator, BaseLayer, AnimIds.SlideStart))
                        EnterSlidePhase(SlidePhase.Loop, AnimIds.SlideLoop);
                    break;
                case SlidePhase.Loop:
                    if (!sliding) EnterSlidePhase(SlidePhase.Exit, AnimIds.SlideExit);
                    break;
                case SlidePhase.Exit:
                    if (sliding)
                    {
                        // A new slide chained in (link window) before the
                        // stand-up finished — restart the sequence from Start.
                        EnterSlidePhase(SlidePhase.Start, AnimIds.SlideStart);
                        break;
                    }
                    bool exitDone = AnimIds.OneShotFinished(_animator, BaseLayer, AnimIds.SlideExit);
                    bool running = speed01 > _gameFeel.PlayerMoveThreshold01;
                    if (exitDone || running)
                    {
                        _animator.CrossFadeInFixedTime(AnimIds.Locomotion,
                            _gameFeel.LocomotionCrossFadeSeconds, BaseLayer, 0f);
                        _slidePhase = SlidePhase.None;
                    }
                    break;
            }
        }

        void EnterSlidePhase(SlidePhase phase, int stateHash)
        {
            _animator.CrossFadeInFixedTime(stateHash, _gameFeel.OneShotCrossFadeSeconds, BaseLayer, 0f);
            _slidePhase = phase;
        }

        void ApplyLinkWindowEmission(Color emission)
        {
            _block.SetColor(EmissionColorId, emission);
            for (int i = 0; i < _renderers.Length; i++) _renderers[i].SetPropertyBlock(_block);
        }

        /// SimEventRouter fan-out slot (П-1): death and own-shot retrigger.
        public void HandleEvent(in SimEvent e)
        {
            switch (e.Kind)
            {
                case SimEventKind.PlayerDied:
                    _dead = true;
                    // В1 fix-wave 1: a death mid-pulse must not freeze the
                    // corpse doll glowing — LateUpdate's own `if (_dead)
                    // return;` guard above skips UpdateLinkWindowFlash from
                    // here on, so the clear has to happen once, right here.
                    ApplyLinkWindowEmission(Color.black);
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
            _dashLean01 = 0f;
            // Task Т23: without this reset a mid-slide death-then-restart
            // would leave _slidePhase at Loop/Exit; the very next LateUpdate
            // would then see `sliding == false` and CrossFade into SlideExit,
            // fighting the explicit Locomotion Play() two lines above.
            _slidePhase = SlidePhase.None;
            _hasPrevPos = false; // restart teleports the player — no ghost speed spike
            ApplyLinkWindowEmission(Color.black); // В1 fix-wave 1: no pulse bleeding into the fresh run
        }

#if UNITY_EDITOR
        /// Owner workflow (Б1 wave 4): drag the Gun with the gizmo in
        /// PlayMode until the grip looks right, then right-click this
        /// component → Capture Gun Transform To Config. SO edits made in
        /// PlayMode persist, so the captured numbers survive exiting play.
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
            Debug.Log("PlayerVisual: gun transform captured to GameFeelConfig: "
                + _gun.localPosition + " / " + _gun.localEulerAngles);
        }
#endif
    }
}
