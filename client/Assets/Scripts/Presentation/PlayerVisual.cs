using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Drives the collector doll (assets phase B spec §3.2): Speed from the
    /// SCREEN-SPACE displacement of the root `ViewRegistry` just positioned
    /// (П-7 — pinned render pairs during hitstop/pause make the doll idle by
    /// construction), body facing toward movement (slowly toward the aim point
    /// when idle), procedural Spine+Chest world-space yaw toward that aim point
    /// layered over the Aim pose, dash lean composed as an OFFSET over a
    /// separately-tracked facing (never accumulated into the transform — ПБ8),
    /// Death01 on PlayerDied with the Aim layer faded out, Pistol_Shoot
    /// retrigger per own ProjectileFired.
    ///
    /// POOLED SINCE STAGE 2 TASK 45a, AND THAT IS WHY IT HOLDS NO REFERENCES.
    /// Every doll in the match — including this client's own — is an instance of
    /// one prefab, rented per player slot by `ViewRegistry` (spec §3.12). A
    /// serialized `SimulationRunner`/`AimProvider`/`GameFeelConfig` on a prefab
    /// component comes back null on the clone, so the numbers arrive as
    /// `PlayerVisualParams` and the state as `PlayerState`, exactly the contract
    /// `MobVisual` already follows next door. `Bind` is the mandatory pool reset
    /// (SetActive(false) rewinds the state machine — the cache must follow, Б5);
    /// one-shot triggers land their state the same frame via Update(0f) (ПБ1).
    /// `WorldRestarted` is no longer subscribed to here either: a match restart
    /// returns every doll to the pool through `ViewRegistry.Clear`, and the
    /// re-rent's `Bind` IS the reset that used to be a handler.
    ///
    /// THE AIM POINT ARRIVES IN THE STATE, WHICH IS NOT WHERE IT ORIGINALLY
    /// LIVED. This class used to read `AimProvider.CurrentAimSimPos` — the local
    /// cursor — and a remote player has no cursor at all. `ViewRegistry` now
    /// resolves the point per slot and hands it in through `PlayerState.AimPoint`
    /// (its own doc): the cursor for this client's own doll, the snapshot's
    /// synthetic aim point for everyone else, and the doll's own position when a
    /// slot carries no aim at all — which the `aimDir` guards below already read
    /// as "hold the last facing".
    ///
    /// В1 fix-wave 1 (owner playtest feedback, item 3 "мерцание сборщика"):
    /// the combo-window emission pulse moved to `PlayerView` in Task 45a, where
    /// the renderers and the shared `MaterialPropertyBlock` now live — the same
    /// `MobView`/`MobVisual` split this pair mirrors (the root view owns
    /// emission, the visual owns the pose), and the same one emission mechanism
    /// as before, not a second one.
    public sealed class PlayerVisual : MonoBehaviour
    {
        const int BaseLayer = 0;
        const int AimLayer = 1;

        [SerializeField] Animator _animator;
        [SerializeField] Transform _visual;

        // Task 23 (ADR-002 A10 amendment): the doll's slide pose sequence —
        // Start (one-shot) -> Loop (held) -> Exit (one-shot back to
        // locomotion), driven off PlayerState.SlideTimer the same
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
        bool _bonesResolved;
        bool _statesChecked;

        /// Rebinds this (pooled) doll to a player slot. `visualScale` is the
        /// bind-time number `MobVisual.Bind`'s own second parameter is — read
        /// off `GameFeelConfig.PlayerVisualScale` by the caller, never here.
        ///
        /// A DOLL IS ONLY EVER BOUND FOR A LIVE SLOT (`ViewRegistry`'s presence
        /// rule): a corpse is a doll that was bound while standing and then
        /// received `PlayerDied`, so the reset below boots into locomotion
        /// rather than branching on `m.Alive`.
        public void Bind(in PlayerState m, float visualScale)
        {
            if (_visual.localScale != Vector3.one * visualScale)
                _visual.localScale = Vector3.one * visualScale;
            // Pool-rebind hygiene: the previous life's facing must not leak
            // into a fresh spawn (audit fix ПБ19).
            _visual.localRotation = Quaternion.identity;

            if (!_bonesResolved)
            {
                // Bones resolve once per pooled instance; humanoid mapping is
                // pack-name-agnostic (Б8).
                _spine = _animator.GetBoneTransform(HumanBodyBones.Spine);
                _chest = _animator.GetBoneTransform(HumanBodyBones.Chest);
                if (_chest == null)
                {
                    Debug.LogError("PlayerVisual: Chest bone missing — spine-only aim yaw.");
                    _chest = _spine;
                }
                _bonesResolved = true;
            }
            if (!_statesChecked)
            {
                // Full drift gate, once per pooled instance (ПБ14): a renamed
                // pack take would otherwise no-op silently at CrossFade time.
                if (!_animator.HasState(BaseLayer, AnimIds.Locomotion)
                    || !_animator.HasState(BaseLayer, AnimIds.Death)
                    || !_animator.HasState(BaseLayer, AnimIds.SlideStart)
                    || !_animator.HasState(BaseLayer, AnimIds.SlideLoop)
                    || !_animator.HasState(BaseLayer, AnimIds.SlideExit)
                    || !_animator.HasState(AimLayer, AnimIds.PistolShoot)
                    || !_animator.HasState(AimLayer, AnimIds.PistolAimNeutral))
                    Debug.LogError("PlayerVisual: PlayerAnimator is missing a mandatory state.");
                _statesChecked = true;
            }

            _dead = false;
            _aimWeight = 1f;
            _dashLean01 = 0f;
            // Task 23: without this reset a mid-slide death-then-restart would
            // leave _slidePhase at Loop/Exit; the very next Sync would then see
            // `sliding == false` and CrossFade into SlideExit, fighting the
            // explicit Locomotion Play() below.
            _slidePhase = SlidePhase.None;
            _hasPrevPos = false; // a fresh bind teleports the doll — no ghost speed spike

            _animator.Rebind();
            _animator.SetLayerWeight(AimLayer, 1f);
            _animator.Play(AnimIds.Locomotion, BaseLayer, 0f);
            _animator.Play(AnimIds.PistolAimNeutral, AimLayer, 0f);
            // Controller default is 1 (preview shows the doll running) — the
            // gameplay doll must boot idle (Б7).
            _animator.SetFloat(AnimIds.Speed, 0f);
            _animator.Update(0f);
            _facing = _visual.rotation;
        }

        /// Per-frame pose pass, called once per render frame by
        /// `ViewRegistry.SyncPlayers` for every live doll (new AND continuing),
        /// AFTER that method has written this frame's `transform.position` —
        /// the displacement read below is what makes hitstop/pause read as idle
        /// with no branch of its own (Б7), exactly as in `MobVisual.Sync`.
        public void Sync(in PlayerState m, in PlayerVisualParams p)
        {
            float dt = p.DeltaTime;
            _animator.speed = p.Paused ? 0f : 1f;

            Vector3 pos = transform.position;
            Vector3 moveDelta = _hasPrevPos ? pos - _prevPos : Vector3.zero;
            _prevPos = pos;
            _hasPrevPos = true;

            // Aim layer weight rides one place for both the death fade-out
            // and the restart fade-in (Б3).
            float weightTarget = _dead ? 0f : 1f;
            float weightRate = dt / Mathf.Max(p.LocomotionCrossFadeSeconds, 1e-3f);
            _aimWeight = Mathf.MoveTowards(_aimWeight, weightTarget, weightRate);
            _animator.SetLayerWeight(AimLayer, _aimWeight);

            if (_dead) return; // corpse: no speed/facing/yaw/lean writes (Б3)

            float speed01 = 0f;
            if (dt > 1e-6f && p.MaxSpeed > 1e-6f)
                speed01 = Mathf.Clamp01(moveDelta.magnitude / dt / p.MaxSpeed);
            _animator.SetFloat(AnimIds.Speed, speed01, p.SpeedDampTime, dt);

            Vector3 aimW = SimSpace.ToWorld(m.AimPoint);
            Vector3 aimDir = aimW - pos;
            aimDir.y = 0f;

            // Facing tracked in a FIELD; the transform gets facing+lean as a
            // one-shot composition below — lean never accumulates (ПБ8).
            Quaternion yawOffset = Quaternion.AngleAxis(p.YawOffsetDeg, Vector3.up);
            if (speed01 > p.MoveThreshold01 && moveDelta.sqrMagnitude > 1e-10f)
            {
                Quaternion target = Quaternion.LookRotation(moveDelta.normalized, Vector3.up) * yawOffset;
                _facing = Quaternion.RotateTowards(_facing, target, p.VisualTurnDegPerSec * dt);
            }
            else if (aimDir.sqrMagnitude > 1e-8f)
            {
                // Idle turn-in toward the aim (Б8): the doll never stays
                // back-to-cursor while shooting on the spot.
                Quaternion target = Quaternion.LookRotation(aimDir.normalized, Vector3.up) * yawOffset;
                _facing = Quaternion.RotateTowards(_facing, target, p.IdleAimTurnDegPerSec * dt);
            }

            UpdateSlideAnimation(in m, speed01, in p);

            // Dash lean (7a): an offset over _facing, tilted toward DashDir.
            float leanTarget01 = m.DashTimer > 0f ? 1f : 0f;
            _dashLean01 = Mathf.MoveTowards(_dashLean01, leanTarget01,
                dt / Mathf.Max(p.DashLeanInOutSeconds, 1e-3f));
            Quaternion rotation = _facing;
            // Task 23: the dash lean is a rotation OFFSET on top of the
            // slide pose the Animator is already playing — while SlideTimer
            // is open, skip it outright rather than let it fight the pose
            // (DashTimer/SlideTimer are mutually exclusive in the sim, but
            // _dashLean01 itself decays over DashLeanInOutSeconds, so a
            // linked slide starting right after a dash ends can still catch
            // it mid-decay). Г7-review fix: SlideTimer alone isn't enough —
            // it hits 0 the instant the Exit phase begins, so a lean could
            // still layer over the SlideExit stand-up clip for ~1 frame.
            // _slidePhase == None is the true "fully out of the slide
            // sequence" predicate (SlideExit->Locomotion only flips it back
            // to None once Exit itself is done or cut short).
            if (_dashLean01 > 0.001f && m.SlideTimer <= 0f && _slidePhase == SlidePhase.None)
            {
                Vector3 dashW = SimSpace.ToWorld(m.DashDir);
                if (dashW.sqrMagnitude > 1e-6f)
                    rotation = Quaternion.AngleAxis(_dashLean01 * p.DashLeanDeg,
                        Vector3.Cross(Vector3.up, dashW.normalized)) * _facing;
            }
            _visual.rotation = rotation;

            // One-shot return on the Aim layer: no transitions exist in the
            // generated controller — the return is code-driven (Б9).
            if (AnimIds.OneShotFinished(_animator, AimLayer, AnimIds.PistolShoot))
                _animator.CrossFadeInFixedTime(AnimIds.PistolAimNeutral,
                    p.OneShotCrossFadeSeconds, AimLayer, 0f);

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
                    + p.YawOffsetDeg);
                yaw = Mathf.Clamp(yaw, -p.AimYawClampDeg, p.AimYawClampDeg);
                float spineYaw = yaw * p.SpineYawShare;
                float chestYaw = yaw - spineYaw;
                if (_spine != null)
                    _spine.rotation = Quaternion.AngleAxis(spineYaw, Vector3.up) * _spine.rotation;
                if (_chest != null)
                    _chest.rotation = Quaternion.AngleAxis(chestYaw, Vector3.up) * _chest.rotation;
                // Chest fallback (== _spine) receives both shares → full yaw
                // on the single bone, which is exactly the degraded intent.
            }
        }

        /// Task 23 (ADR-002 A10 amendment): steps the slide pose FSM from
        /// SlideTimer alone — SlideTimer > 0 is the sim's own "sliding right
        /// now" predicate (ProjectileSystem/Spread/SimulationRunner already
        /// read the exact same field, class docs of each). Start plays out
        /// once (or is cut short if the slide itself ends first — SlideDuration
        /// is only HeroConfig.SlideDuration=0.52s, shorter than Start can run
        /// long), then Loop holds for the remainder, then Exit plays once and
        /// hands back to Locomotion. Exit is allowed to cut short: a player
        /// still holding move input drops straight into Locomotion instead of
        /// waiting the stand-up clip out ("keep it snappy" — spec).
        void UpdateSlideAnimation(in PlayerState m, float speed01, in PlayerVisualParams p)
        {
            bool sliding = m.SlideTimer > 0f;
            switch (_slidePhase)
            {
                case SlidePhase.None:
                    if (sliding) EnterSlidePhase(SlidePhase.Start, AnimIds.SlideStart, in p);
                    break;
                case SlidePhase.Start:
                    if (!sliding) EnterSlidePhase(SlidePhase.Exit, AnimIds.SlideExit, in p);
                    else if (AnimIds.OneShotFinished(_animator, BaseLayer, AnimIds.SlideStart))
                        EnterSlidePhase(SlidePhase.Loop, AnimIds.SlideLoop, in p);
                    break;
                case SlidePhase.Loop:
                    if (!sliding) EnterSlidePhase(SlidePhase.Exit, AnimIds.SlideExit, in p);
                    break;
                case SlidePhase.Exit:
                    if (sliding)
                    {
                        // A new slide chained in (link window) before the
                        // stand-up finished — restart the sequence from Start.
                        EnterSlidePhase(SlidePhase.Start, AnimIds.SlideStart, in p);
                        break;
                    }
                    bool exitDone = AnimIds.OneShotFinished(_animator, BaseLayer, AnimIds.SlideExit);
                    bool running = speed01 > p.MoveThreshold01;
                    if (exitDone || running)
                    {
                        _animator.CrossFadeInFixedTime(AnimIds.Locomotion,
                            p.LocomotionCrossFadeSeconds, BaseLayer, 0f);
                        _slidePhase = SlidePhase.None;
                    }
                    break;
            }
        }

        void EnterSlidePhase(SlidePhase phase, int stateHash, in PlayerVisualParams p)
        {
            _animator.CrossFadeInFixedTime(stateHash, p.OneShotCrossFadeSeconds, BaseLayer, 0f);
            _slidePhase = phase;
        }

        /// `ViewRegistry.HandlePlayerEvent`'s per-slot fan-out (П-1): death and
        /// own-shot retrigger. The caller has already decided this event belongs
        /// to THIS doll's slot — `PlayerDied` by its VICTIM convention,
        /// `ProjectileFired` by its ACTOR one (`SimEvent.PlayerIndex`'s own doc)
        /// — so no owner/index test is repeated here. The crossfade durations are
        /// the ones this doll last saw in `Sync`, because an event arrives in the
        /// Update phase, before this frame's parameter pack exists.
        public void HandleEvent(in SimEvent e, float oneShotCrossFadeSeconds)
        {
            switch (e.Kind)
            {
                case SimEventKind.PlayerDied:
                    _dead = true;
                    _animator.CrossFadeInFixedTime(AnimIds.Death,
                        oneShotCrossFadeSeconds, BaseLayer, 0f);
                    break;
                case SimEventKind.ProjectileFired:
                    if (!_dead)
                    {
                        _animator.Play(AnimIds.PistolShoot, AimLayer, 0f);
                        _animator.Update(0f); // land the state this frame (ПБ1)
                    }
                    break;
            }
        }
    }
}
