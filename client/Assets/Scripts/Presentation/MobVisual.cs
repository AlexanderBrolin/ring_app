using Ring.Simulation.Core;
using Unity.Mathematics;
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
    /// `m.Tilt` is the AUTHORITATIVE signed magnitude in radians
    /// (`MobState.Tilt`'s own doc, Core/SimStates.cs) — real offline, where
    /// `RenderSnapshot.Mobs` copies `MobState` whole; always zero over the
    /// wire until Т31 gives the network path its own integrator
    /// (`NetworkSimBackend.cs:2176-2183` sends `Id/Type/Ai/Pos/Hp` only, no
    /// `Tilt`, Р383's 9-byte `MobRecord`). The AXIS the scalar has no room
    /// for arrives separately, from the hit event:
    /// `ViewRegistry.HandleEvent`'s `ProjectileHit` branch calls `SetHitDir`
    /// with `SimEvent.HitDir` the instant a blow lands (Ruling 48), and this
    /// class turns that into the horizontal perpendicular a body tips
    /// around (see `SetHitDir`'s own doc for the arithmetic). Facing is kept
    /// in its own field, `_facing`, rather than read back off
    /// `_visual.rotation` (Ruling 47) — the transform now holds
    /// `tilt * _facing`, and reading a composed value back out as if it
    /// were pure facing would fold the tilt into every subsequent turn. A
    /// mob with no axis yet (`_tiltAxis` still `Vector3.zero`, `Bind`'s
    /// reset) composes to `Quaternion.identity` through an explicit guard
    /// in `Sync`, not through any assumed `Quaternion.AngleAxis` behavior on
    /// a degenerate axis — Unity's own docs say nothing about that case
    /// either way (Ruling 49, coordinator finding via Context7).
    ///
    /// `Downed` gets no clip of its own (Ruling 45 — no pack ships a
    /// fall/get-up take, and `Death`/`TurnOff` are `CorpseView`'s alone,
    /// `CorpseView.cs:133`): the physical fall IS the tilt spring above.
    /// The one thing this class does for the state is cut short whatever
    /// one-shot Melee/Ranged take was still mid-flight when the body went
    /// over, so a downed mob does not keep swinging lying down (`Sync`'s
    /// one-shot block, the added `|| m.Ai == MobAiState.Downed`).
    ///
    /// ⚠ NAMED HONESTLY: a networked client sees `Ai == Downed` (Т6 rides
    /// the wire) but never a nonzero `Tilt`, so until Т31 it shows a
    /// downed mob only through the swing-cancel above and the stopped
    /// locomotion Downed already implies — no visible lean. Offline shows
    /// the whole thing. Same boundary the class doc's own paragraph above
    /// already draws for the scalar.
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

        /// Horizontal perpendicular to the last hit's `HitDir`, set by
        /// `SetHitDir` from `ViewRegistry.HandleEvent`'s `ProjectileHit`
        /// branch (Ruling 48). Default `Vector3.zero` reads as "no axis
        /// yet" — a freshly pooled instance genuinely has none until its
        /// first hit, and `Sync` (Ruling 49) tests this field explicitly
        /// before ever calling `Quaternion.AngleAxis`, rather than handing
        /// that call a possibly zero-length axis and hoping: Unity's own
        /// API documentation is silent on what it does with one (checked
        /// directly against the docs, not assumed — neither a
        /// normalize-to-zero nor a NaN is documented either way), and this
        /// line runs for every live mob every frame. An undocumented
        /// degenerate case is not a bet worth taking on a hot path this epic
        /// has already paid for guessing on three times (lessons 512/530).
        Vector3 _tiltAxis;

        public void Bind(in MobState m, float visualScale)
        {
            if (_visual.localScale != Vector3.one * visualScale)
                _visual.localScale = Vector3.one * visualScale;
            // Pool-rebind hygiene: the previous life's facing, tilt axis and
            // composed rotation must not leak into a fresh spawn (audit fix
            // ПБ19, extended by Ruling 47/48's two new fields — the same
            // hygiene the comment already named, twice the state to reset).
            // The direct transform reset stays defensive rather than relied
            // upon: the Bind/Sync contract (Task 21) guarantees a same-frame
            // Sync always follows this call and would overwrite it anyway
            // (`ViewRegistry.SyncMobs`' rent branch, `:1253`/`:1259`), but
            // nothing enforces that contract at compile time.
            _visual.localRotation = Quaternion.identity;
            _facing = Quaternion.identity;
            _tiltAxis = Vector3.zero;
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
            // not wait for its next turn. `m.Tilt` is radians (`MobState.
            // Tilt`'s own doc); `Quaternion.AngleAxis` wants degrees, hence
            // the one `Mathf.Rad2Deg` in this class — Presentation's first
            // read of `Tilt` at all (class doc). Composition order is
            // `tilt * _facing`, tilt OUTERMOST: `_tiltAxis` is a fixed WORLD
            // axis (`SetHitDir` builds it through `SimSpace.ToWorld`), not
            // one relative to whichever way the model currently faces, so it
            // has to apply in world space on top of the facing rotation
            // rather than compose inside it.
            //
            // THE GUARD BELOW IS CONTENT, NOT DEFENSE (Ruling 49): a mob
            // that has not been hit yet genuinely has no axis —
            // `_tiltAxis` reads `Vector3.zero` straight out of `Bind`'s
            // reset — and `Quaternion.AngleAxis`'s own documentation says
            // nothing about a zero-length axis in either direction (no
            // normalize-to-zero, no NaN; checked against the API docs, not
            // assumed). This line runs for every live mob every frame, so
            // the explicit branch is the honest answer rather than a guess
            // dressed as a fact.
            Quaternion tilt = _tiltAxis.sqrMagnitude > 0f
                ? Quaternion.AngleAxis(m.Tilt * Mathf.Rad2Deg, _tiltAxis)
                : Quaternion.identity;
            _visual.rotation = tilt * _facing;

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

        /// Hands over the horizontal axis a positive `m.Tilt` rotates around
        /// in the next `Sync` (Ruling 46/47/48, app-88jb Т11). Called by
        /// `ViewRegistry.HandleEvent`'s `ProjectileHit` branch with the
        /// event's own `HitDir` the instant a blow lands, because
        /// `MobState.Tilt` is a signed SCALAR with no direction of its own
        /// (its own doc, Core/SimStates.cs) and this is the only source of
        /// one on this side of the wire (offline only until Т31 — see the
        /// class doc's own network-boundary paragraph).
        ///
        /// THE AXIS, NOT THE DIRECTION ITSELF: `hitDir` is the shot's unit
        /// direction of travel in the sim plane (`SimEvent.HitDir`'s own
        /// doc, Core/SimEvents.cs:198); the body does not spin around that
        /// vector, it tips OVER it, around the horizontal line
        /// perpendicular to it. `Vector3.Cross(Vector3.up, worldDir)` is
        /// that perpendicular, and its sign is not arbitrary: it is the one
        /// that makes a positive `m.Tilt` rotate `_visual`'s top ALONG
        /// `worldDir` — the same "along the shot" arm `MobState.Tilt`'s own
        /// doc names for a hit above `MobSimConfig.CenterOfMassHeight`
        /// (`Impact.AngularImpulse`'s formula, `SimulationWorld.cs:1671`).
        /// `SimSpace.ToWorld` is the sole sim→world seam (class doc,
        /// `SimSpace.cs:12`) — no inline `new Vector3(x, 0f, y)` here.
        ///
        /// `Cross(up, worldDir)` is already unit length without an explicit
        /// `.normalized`: `hitDir` is a unit vector by construction
        /// (`ProjectileSystem.cs:260`'s `math.normalizesafe`), `ToWorld`
        /// preserves that length (a lossless axis swap, no scaling), and
        /// `Vector3.up` is always perpendicular to a vector confined to the
        /// horizontal plane it maps into — so the cross product's magnitude
        /// is `1 * 1 * sin(90°) = 1` by construction, every time.
        public void SetHitDir(float2 hitDir)
        {
            Vector3 worldDir = SimSpace.ToWorld(hitDir);
            _tiltAxis = Vector3.Cross(Vector3.up, worldDir);
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
