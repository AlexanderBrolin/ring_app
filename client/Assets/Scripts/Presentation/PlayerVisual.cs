using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Drives the collector doll (assets phase B spec §3.2): Speed from the
    /// SCREEN-SPACE displacement of the root `ViewRegistry` just positioned
    /// (П-7 — a paused render pair makes the doll idle by construction), body
    /// facing toward movement (slowly toward the aim point
    /// when idle), procedural Spine+Chest world-space yaw toward that aim point
    /// layered over the Aim pose, dash lean composed as an OFFSET over a
    /// separately-tracked facing (never accumulated into the transform — ПБ8),
    /// Death01 on PlayerDied with the Aim layer faded out, Pistol_Shoot
    /// retrigger per own ProjectileFired.
    ///
    /// POOLED SINCE STAGE 2 TASK 45a, AND THAT IS WHY IT HOLDS NO REFERENCES.
    /// Every doll in the match — including this client's own — is an instance of
    /// one prefab, rented per player slot by `ViewRegistry` (spec §3.12), so
    /// the numbers arrive as `PlayerVisualParams` and the state as
    /// `PlayerState`, exactly the contract `MobVisual` already follows next
    /// door. TWO DIFFERENT REASONS SIT BEHIND THAT, and they are worth keeping
    /// apart. `SimulationRunner`/`AimProvider` are SCENE components: a prefab
    /// asset cannot reference one at all, so the field would simply be null on
    /// every clone. `GameFeelConfig` is an ASSET and a prefab holds one
    /// perfectly well — `PlayerGunTuner` on this very doll does — so what
    /// keeps it out is the rule, not the mechanism: spec §3.12 has the pooled
    /// views take their numbers from the caller, which is what keeps one
    /// frame's feel numbers the same for every doll in it.
    ///
    /// `Bind` is the mandatory pool reset (SetActive(false) rewinds the state
    /// machine — the cache must follow, Б5); one-shot triggers land their state
    /// the same frame via Update(0f) (ПБ1). `WorldRestarted` is no longer
    /// subscribed to here either: a match restart returns every doll to the pool
    /// through `ViewRegistry.Clear`, and the re-rent's `Bind` takes over from
    /// what used to be a handler — see that method's own doc for the one
    /// behavior delta that came with the swap.
    ///
    /// THE AIM POINT ARRIVES IN THE STATE, WHICH IS NOT WHERE IT ORIGINALLY
    /// LIVED. This class used to read `AimProvider.CurrentAimSimPos` — the local
    /// cursor — and a remote player has no cursor at all. `ViewRegistry` now
    /// resolves the point per slot and hands it in through `PlayerState.AimPoint`
    /// (its own doc): the cursor for this client's own doll, the snapshot's
    /// synthetic aim point for everyone else, and the doll's own position when a
    /// slot carries no aim at all — which collapses `aimDir` below, so a
    /// standing doll holds its last facing and a moving one keeps turning along
    /// its own displacement.
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
        SlidePhase _slidePhase = SlidePhase.None;
        bool _bonesResolved;
        bool _statesChecked;

        /// Rebinds this (pooled) doll to a player slot. `visualScale` is the
        /// bind-time number `MobVisual.Bind`'s own second parameter is — read
        /// off `GameFeelConfig.PlayerVisualScale` by the caller, never here.
        ///
        /// THE RESET BOOTS INTO LOCOMOTION AND DOES NOT BRANCH ON `m.Alive`,
        /// which used to be justified by "a doll is only ever bound for a live
        /// slot". That stopped being true in Stage 2 Task 47a: a doll is also
        /// rented for a slot this client first meets as a BODY
        /// (`ViewRegistry.EnsureCorpse`, someone else's corpse walked up to
        /// after the fact). The reset is still right for both, and for the same
        /// reason it was right before — it is pool-rebind hygiene, not a pose
        /// choice — because the caller states the pose immediately afterwards:
        /// `Sync` for a live slot, `PlayDeath` for a body. Branching here would
        /// put the decision in two places.
        ///
        /// BEHAVIOR DELTA VS THE PRE-POOL RESTART PATH (Stage 2 Task 45a, named
        /// so it can be looked at rather than discovered): the component this
        /// replaced reset its animation state on `WorldRestarted` but kept the
        /// doll's accumulated facing and its Animator's accumulated state. This
        /// resets both — `_animator.Rebind()`, `_visual.localRotation` back to
        /// identity and `_facing` re-read from it — which is `MobVisual.Bind`'s
        /// own pool-rebind hygiene (Б5/ПБ19) and is required for a doll that
        /// really does come out of a pool. Visible consequence: after a restart
        /// the doll faces the model's rest direction for the fraction of a
        /// second `VisualTurnDegPerSec`/`IdleAimTurnDegPerSec` needs to turn it
        /// back, instead of continuing to face wherever it stood before.
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

        /// Forget where this doll last stood (Stage 2 Task 47c fix-round 1) —
        /// AN ENTRY INTO `Bind`'S OWN RESET, NOT A SECOND HOME FOR THE SPEED
        /// RULE. It sets the one flag the line above already sets
        /// (`_hasPrevPos = false`) and states nothing else about speed: the
        /// displacement is still read in exactly one place, `Sync` below, which
        /// then sees no previous point and reports zero for that frame.
        ///
        /// THE ONE CALLER IS A RETURN THAT IS NOT A BIND. A slot whose records
        /// stopped keeps its doll — held, frozen and dimming
        /// (`ViewRegistry.HoldFadingDoll`) — and `Sync` is not called for it at
        /// all while that lasts, so `_prevPos` stays at the last point the
        /// picture ever showed. When the frame carries the slot again the doll
        /// is still in `_activePlayers`, so the registry takes its CONTINUING
        /// path: no `Bind`, a transform snapped to wherever that player is now,
        /// and then `Sync`. Without this call that first `Sync` reads everything
        /// the player walked behind the fog — up to the whole stale-plus-fade
        /// budget of running — as one frame's displacement: `speed01` pegs at
        /// `1`, the doll sprints on the spot, and `_facing` turns along the
        /// teleport vector instead of along any real movement. The first frame
        /// back is a teleport, and this is what says so.
        public void ForgetPrevPos() => _hasPrevPos = false;

        /// Per-frame pose pass, called once per render frame by
        /// `ViewRegistry.SyncPlayers` for every LIVE doll (new AND continuing),
        /// AFTER that method has written this frame's `transform.position` —
        /// the displacement read below is what makes a paused frame read as
        /// idle with no branch of its own (Б7), exactly as in `MobVisual.Sync`.
        /// A corpse gets `SyncCorpse` below instead; there is no "am I dead"
        /// branch in here, because which method the registry calls IS that fact.
        public void Sync(in PlayerState m, in PlayerVisualParams p)
        {
            float dt = p.DeltaTime;

            Vector3 pos = transform.position;
            Vector3 moveDelta = _hasPrevPos ? pos - _prevPos : Vector3.zero;
            _prevPos = pos;
            _hasPrevPos = true;

            SyncAlways(1f, in p);

            float speed01 = 0f;
            if (dt > 1e-6f && p.MaxSpeed > 1e-6f)
                speed01 = Mathf.Clamp01(moveDelta.magnitude / dt / p.MaxSpeed);
            _animator.SetFloat(AnimIds.Speed, speed01, p.SpeedDampTime, dt);

            Vector3 aimW = SimSpace.ToWorld(m.AimPoint);
            Vector3 aimDir = aimW - pos;
            aimDir.y = 0f;

            // Facing tracked in a FIELD; the transform gets facing+lean as a
            // one-shot composition below — lean never accumulates (ПБ8).
            if (speed01 > p.MoveThreshold01 && moveDelta.sqrMagnitude > 1e-10f)
            {
                Quaternion target = FacingAlong(moveDelta.normalized, p.YawOffsetDeg);
                _facing = Quaternion.RotateTowards(_facing, target, p.VisualTurnDegPerSec * dt);
            }
            else if (aimDir.sqrMagnitude > 1e-8f)
            {
                // Idle turn-in toward the aim (Б8): the doll never stays
                // back-to-cursor while shooting on the spot.
                Quaternion target = FacingAlong(aimDir.normalized, p.YawOffsetDeg);
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

        /// A corpse's entire per-frame budget (Stage 2 Task 45a fix-round 1).
        /// A detached doll is never `Sync`ed and never repositioned again — that
        /// is what makes it a corpse — but two things still have to happen to it
        /// every frame, and neither needs a `PlayerState`:
        ///  - the Aim layer has to fade OUT (Б3), or the pistol-aim pose stays
        ///    layered over Death01 and the body dies still holding its gun up;
        ///  - `_animator.speed` has to keep tracking the pause gate, or a body
        ///    goes on collapsing while the rest of the frame is frozen.
        /// Same numbers as the live path, from the same pack the registry
        /// already built once for this frame.
        public void SyncCorpse(in PlayerVisualParams p) => SyncAlways(0f, in p);

        /// The part of the frame that is the same for a body and a corpse — the
        /// pause gate and the Aim-layer weight ride ONE place for the death
        /// fade-out and the restart fade-in alike (Б3). `aimWeightTarget` is the
        /// only thing that differs, and the caller is the one that knows it.
        void SyncAlways(float aimWeightTarget, in PlayerVisualParams p)
        {
            _animator.speed = p.Paused ? 0f : 1f;
            float weightRate = p.DeltaTime / Mathf.Max(p.LocomotionCrossFadeSeconds, 1e-3f);
            _aimWeight = Mathf.MoveTowards(_aimWeight, aimWeightTarget, weightRate);
            _animator.SetLayerWeight(AimLayer, _aimWeight);
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
        /// — so no owner/index test is repeated here.
        ///
        /// `oneShotCrossFadeSeconds` is a PARAMETER because an event arrives in
        /// the `Update` phase, before this frame's `PlayerVisualParams` has been
        /// built: `ViewRegistry.DispatchToDoll` reads
        /// `GameFeelConfig.OneShotCrossFadeSeconds` off the config right there
        /// and hands it in. Nothing is cached from the last `Sync` — the value
        /// is this instant's, which is what a PlayMode hot-tweak needs.
        ///
        /// `PlayerDied` does not set an "am I dead" flag here: the registry
        /// detaches this doll into its corpse list on the same event, and from
        /// then on the only method it calls is `SyncCorpse`. That is the single
        /// home of the fact, and it is why no branch below tests for it.
        public void HandleEvent(in SimEvent e, float oneShotCrossFadeSeconds)
        {
            switch (e.Kind)
            {
                case SimEventKind.PlayerDied:
                    PlayDeath(fromStanding: true, oneShotCrossFadeSeconds);
                    break;
                case SimEventKind.ProjectileFired:
                    _animator.Play(AnimIds.PistolShoot, AimLayer, 0f);
                    _animator.Update(0f); // land the state this frame (ПБ1)
                    break;
            }
        }

        /// The pose half of becoming a corpse (Stage 2 Task 47a) — ONE home for
        /// the Death01 transition, called by `ViewRegistry` at the moment a doll
        /// leaves its slot for the corpse list, whichever of the two facts got
        /// there first: the `PlayerDied` event above, or the frame itself when
        /// it reports the slot known and not alive. Exactly one of them runs it,
        /// because the registry files the body under its slot and the loser
        /// finds it already there.
        ///
        /// `fromStanding` IS THE DIFFERENCE BETWEEN A DEATH AND A BODY, and it
        /// is the caller's fact rather than a guess made here. A doll that was
        /// on its feet in the previous frame is falling NOW, so it crossfades
        /// and the collapse is seen. A doll rented for a slot this client only
        /// ever met as a corpse — someone else's body, found after the fact
        /// (`ViewRegistry.EnsureCorpse`) — lands on the LAST frame of the same
        /// clip instead: it fell before this client was looking, and playing the
        /// fall would state that it happened just now, in front of a player who
        /// would then look for a killer who left long ago.
        ///
        /// The Aim layer is dropped outright in that second case rather than
        /// faded. `SyncCorpse` eases it out over `LocomotionCrossFadeSeconds`
        /// because a fresh death still has a weapon up to lower; a body already
        /// on the ground has none, and easing from full weight would raise its
        /// pistol first.
        public void PlayDeath(bool fromStanding, float oneShotCrossFadeSeconds)
        {
            if (fromStanding)
            {
                _animator.CrossFadeInFixedTime(AnimIds.Death,
                    oneShotCrossFadeSeconds, BaseLayer, 0f);
                return;
            }

            _animator.Play(AnimIds.Death, BaseLayer, 1f);
            _aimWeight = 0f;
            _animator.SetLayerWeight(AimLayer, 0f);
            _animator.Update(0f); // land the pose this frame (ПБ1)
        }

        /// The FACING half of a body found after the fact (Stage 2 Task 47a
        /// fix-round 1), and the only caller is the one branch that has no
        /// facing to keep: `ViewRegistry.EnsureCorpse` renting a doll for a
        /// slot this client first meets as a corpse. A doll that died on its
        /// feet already carries the facing its last `Sync` integrated and must
        /// not be turned; a rented one has just been through `Bind`, which
        /// resets `_visual.localRotation` to identity as pool hygiene, so
        /// without this it would lie along the model's rest direction — and so
        /// would every other body on the arena, which is what made the two
        /// clients disagree about the pose of one body.
        ///
        /// IT SNAPS RATHER THAN TURNS, which is the difference from `Sync`'s
        /// two turn-in branches above and not an omission. Those rate-limit the
        /// facing because a live doll is WATCHED as it turns; this body fell
        /// before the finder was looking, so there is no turn to show and any
        /// rate at all would be a body pivoting on the ground.
        ///
        /// THE AIM POINT IS THE HEADING, the same quantity the idle branch of
        /// `Sync` turns toward, taken off the same field of the same record
        /// this body's POSITION came from — for a networked corpse the
        /// border put the wire's `Dir` there (`NetworkSimBackend.ReadPlayers`),
        /// for a local one it is the world's own aim point, pinned at the value
        /// it had at death (`SimulationWorld.TickMovement`). A degenerate pair
        /// (an aim point exactly on the body) leaves the rest facing alone
        /// rather than inventing one: it is the same `1e-8` refusal `Sync`
        /// makes, and the honest answer to "no heading was carried".
        public void FaceAimInstantly(in PlayerState m, float yawOffsetDeg)
        {
            Vector3 aimDir = SimSpace.ToWorld(m.AimPoint) - transform.position;
            aimDir.y = 0f;
            if (aimDir.sqrMagnitude <= 1e-8f) return;
            _facing = FacingAlong(aimDir.normalized, yawOffsetDeg);
            _visual.rotation = _facing;
        }

        /// What a flat world direction MEANS as this model's facing — the model
        /// yaw offset is part of that answer, and this is its one home (Stage 2
        /// Task 47a fix-round 1: `Sync`'s movement and idle-aim branches and
        /// `FaceAimInstantly` all compose it here, so a re-tune of
        /// `GameFeelConfig.PlayerYawOffsetDeg` cannot reach two of the three
        /// and miss the last). `dir` must be flat and normalized; the callers
        /// are what guarantee it, since each already has its own reason to
        /// refuse a near-zero one.
        static Quaternion FacingAlong(Vector3 dir, float yawOffsetDeg)
            => Quaternion.LookRotation(dir, Vector3.up) * Quaternion.AngleAxis(yawOffsetDeg, Vector3.up);
    }
}
