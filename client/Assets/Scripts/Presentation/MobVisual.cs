using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Drives a mech's Animator from MobState (assets phase B spec §3.3):
    /// locomotion from the SCREEN-SPACE displacement of the root the registry
    /// just positioned (a paused frame reads as Idle by construction, Б7),
    /// one-shot Punch/Shoot on Ai transitions with a code-driven return
    /// (the Phase A robot controllers have no transitions), hysteresis + hold
    /// against threshold flicker (Б12). Pooled: Bind is the mandatory reset
    /// (SetActive(false) rewinds the state machine — the cache must follow,
    /// Б5); one-shot triggers land their state the same frame via Update(0f)
    /// (ПБ1 — a same-frame state check would otherwise cancel them).
    ///
    /// Body tilt (app-88jb Т11, spec §3.2's Presentation half, coordinator
    /// Rulings 45-49) composes on TOP of the facing rotation, on `_visual`
    /// ONLY — never on this component's own root transform, because that
    /// root also parents the three `AimProxy_*` colliders
    /// (`Prefabs/MobChaserView.prefab`: the root's `m_Children` list is
    /// exactly four entries, the three proxies plus this stripped `_visual`)
    /// and Р375 requires them to stay upright regardless of what the model
    /// does (Ruling 46, witness `TiltedMob_KeepsItsUprightParts`, Т14).
    /// `m.Tilt` is the lean itself — a `float2` whose length is the angle in
    /// radians and whose heading is the direction the body leans towards
    /// (`MobState.Tilt`'s own doc, Core/SimStates.cs; it was a signed
    /// magnitude until app-94sk T5a) — and this component reads ONE field on
    /// both paths, which is the whole shape of app-88jb Т31. Offline the number is
    /// AUTHORITATIVE: `RenderSnapshot.Mobs` copies `MobState` whole. Over the
    /// wire it used to be always zero — `MobRecord` is nine bytes and carries
    /// `Id/Type/Ai/Pos/Hp` only (Р383) — and Т31 did not put it on the wire
    /// either: `NetworkSimBackend` REBUILDS it into the published pair
    /// through `Ring.Networking.Client.MobTiltIntegrator`, which walks the
    /// same `Impact.SpringStep` at the same `SimulationWorld.TickDt` the
    /// server walks, from the hit event the wire does carry. So this class
    /// was left untouched by that task on purpose (owner decision 4а, narrowed
    /// to docs): an integrator HERE, fed by an event that reaches this layer
    /// on both paths, would have DOUBLED the offline tilt — the authoritative
    /// scalar plus a reconstructed one. THE DIRECTION IS NO LONGER A
    /// SECOND ARRIVAL: since app-94sk T5a `MobState.Tilt` is a `float2`
    /// whose length is the angle and whose heading is the lean, so this
    /// class derives the axis it needs from the value it already reads.
    /// Until T5a the axis came from the event instead — `ViewRegistry.
    /// HandleEvent`'s `ProjectileHit` branch called `SetHitDir` with
    /// `SimEvent.HitDir` (Ruling 48) — and that was the defect the owner
    /// read as "a lag or a bug": the axis was rewritten on EVERY hit while
    /// the magnitude kept swinging from the previous one, so a body already
    /// leaning snapped onto the new blow's axis. Both the field and the
    /// setter are gone; there is no second piece of state left to disagree.
    /// Facing is kept in its own field, `_facing`, rather than read back off
    /// `_visual.rotation` (Ruling 47) — the transform now holds
    /// `tilt * _facing`, and reading a composed value back out as if it
    /// were pure facing would fold the tilt into every subsequent turn. An
    /// UPRIGHT mob composes to `Quaternion.identity` through an explicit
    /// guard in `Sync`, not through any assumed `Quaternion.AngleAxis`
    /// behavior on a degenerate axis — Unity's own docs say nothing about
    /// that case either way (Ruling 49, coordinator finding via Context7).
    ///
    /// `Downed` gets no clip of its own (Ruling 45 — no pack ships a
    /// fall/get-up take, and `Death`/`TurnOff` are `CorpseView`'s alone,
    /// `CorpseView.cs:133`): the physical fall IS the tilt spring above.
    /// The one thing this class does for the state is cut short whatever
    /// one-shot Melee/Ranged take was still mid-flight when the body went
    /// over, so a downed mob does not keep swinging lying down (`Sync`'s
    /// one-shot block, the added `|| m.Ai == MobAiState.Downed`).
    ///
    /// ⚠ NAMED HONESTLY, AND THE NAME CHANGED WITH app-88jb Т31. A networked
    /// client used to see `Ai == Downed` (Т6 rides the wire) but never a
    /// nonzero `Tilt`, so a body went over showing only the swing-cancel
    /// above and the stopped locomotion Downed already implies — no visible
    /// lean, where offline showed the whole fall. The lean arrives with Т31,
    /// synthesized into the pair by the backend rather than by anything here
    /// (see the class doc's own paragraph on the scalar), so the two paths
    /// now differ in HOW the number was obtained and not in whether there is
    /// one. What is still not identical is exactness: the rebuilt curve is
    /// driven by an event, so a blow whose victim this client could not
    /// identify — or a round that had ricocheted on its way in — is missing
    /// or overstated (the integrator's own doc prices both).
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

        /// Facing alone, WITHOUT the tilt composed on top of it (Ruling 47,
        /// app-88jb Т11). `_visual.rotation` now holds `tilt * _facing`, so
        /// this field is the only thing `RotateTowards` in `Sync` has left
        /// to turn that is not itself already leaning.
        Quaternion _facing;

        public void Bind(in MobState m, float visualScale)
        {
            if (_visual.localScale != Vector3.one * visualScale)
                _visual.localScale = Vector3.one * visualScale;
            // Pool-rebind hygiene: the previous life's facing and composed
            // rotation must not leak into a fresh spawn (audit fix ПБ19,
            // extended by Ruling 47's field).
            //
            // THE TILT AXIS USED TO BE RESET HERE TOO, AND THAT RESET WAS A
            // SECOND, QUIETER DEFECT (closed by app-94sk T5a): a body first
            // SEEN already downed — bound with a tilt it got before this
            // client ever saw it — came up with `_tiltAxis` at zero and so
            // stood bolt upright, because `Bind` has no `Downed`
            // compensation and never had one. There is nothing to reset now:
            // the lean arrives whole, inside `m.Tilt`.
            // The direct transform reset stays defensive rather than relied
            // upon: the Bind/Sync contract (Task 21) guarantees a same-frame
            // Sync always follows this call and would overwrite it anyway
            // (`ViewRegistry.SyncMobs`' rent branch, `:1253`/`:1259`), but
            // nothing enforces that contract at compile time.
            _visual.localRotation = Quaternion.identity;
            _facing = Quaternion.identity;
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
            //
            // WRITES `_facing`, NOT THE TRANSFORM (Ruling 47, app-88jb Т11):
            // the block below composes tilt on top of whatever this settles
            // on, and a facing update landing straight on `_visual.rotation`
            // would overwrite the previous frame's tilt the instant the mob
            // turns — kneeling would read as "tilted only while standing
            // still", exactly the defect Ruling 47 exists to avoid.
            bool faceTarget = m.Ai == MobAiState.Reposition || m.Ai == MobAiState.Fire;
            Vector3 faceDir = faceTarget ? p.PlayerPos - pos : moveDelta;
            faceDir.y = 0f;
            if (faceDir.sqrMagnitude > 1e-8f
                && (faceTarget || speed > p.WalkExitSpeed))
            {
                Quaternion target = Quaternion.LookRotation(faceDir.normalized, Vector3.up)
                    * Quaternion.AngleAxis(p.YawOffsetDeg, Vector3.up);
                _facing = Quaternion.RotateTowards(
                    _facing, target, p.TurnDegPerSec * p.DeltaTime);
            }

            // Body tilt (Ruling 46/47, app-88jb Т11): written EVERY Sync,
            // never gated behind the facing `if` above, because `m.Tilt`
            // walks every tick (Combat/TiltSystem.cs) regardless of whether
            // this mob happens to be turning this frame — a stationary mob
            // that just got knocked down must fall on the tick it happened,
            // not wait for its next turn.
            //
            // THE LEAN AND ITS AXIS ARE ONE FIELD since app-94sk T5a, and the
            // arithmetic that turns it into a rotation lives in ONE home for
            // both dolls -- `SimSpace.TiltRotation` (rule 2; its own doc
            // carries the axis, the sign, the units and the upright guard).
            // Composition order is `tilt * _facing`, tilt OUTERMOST, because
            // that axis is fixed in the WORLD rather than relative to
            // whichever way this model currently faces.
            _visual.rotation = SimSpace.TiltRotation(m.Tilt) * _facing;

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
                // `|| m.Ai == MobAiState.Downed` (Ruling 45, app-88jb Т11):
                // TiltSystem (Combat/TiltSystem.cs:87-91) can flip a mob's Ai
                // to Downed on ANY tick, including one where this mob is
                // still mid-swing — without this extra condition, a downed
                // body keeps playing its Melee/Ranged take out to
                // `normalizedTime >= 1f` while already lying on the ground.
                // Reuses the SAME two-line cancel path the one-shot's own
                // natural completion already performs below (coordinator
                // instruction: one path, not a second) — this OR-clause is
                // the entire change, not a new branch of its own.
                if (!oneShotState || finished || m.Ai == MobAiState.Downed)
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
