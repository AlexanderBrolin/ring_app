using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for a single live mob (spec §3.6/§3.7). Pooled and
    /// (re)bound purely by `ViewRegistry` — no other class instantiates, destroys,
    /// or repositions a `MobView`. The capsule's material is one shared asset
    /// (`MobEmissive`) across every instance; any accent tint comes only from a
    /// `MaterialPropertyBlock` override applied in `Bind`/`Sync`, never a material
    /// instance (П-2: no per-instance materials). Base emission is black now
    /// (9a) — the capsule is a fallback for when no mech model is bound; a real
    /// model's archetype identity lives in `Visual`/`MobVisual`, not a color tint.
    /// Task 21 (spec §3.6, resolution "Bind contract"): `Bind` now takes the full
    /// `MobState` (not just `MobType`) and only sets up the pool-rebind baseline
    /// (base accent color, cleared flash). Every per-frame accent — the Chaser's
    /// telegraph pulse and the Gunner's Fire-state glint — is computed by `Sync`,
    /// called once per render frame from `ViewRegistry`'s existing `LateUpdate`
    /// diff (П-1: no new `TicksFlushed` subscriber). All timing here rides
    /// `Time.unscaledTime`/`unscaledDeltaTime` — `Time.timeScale` is never used by
    /// this project (see `SimulationRunner`), so hitstop/slow-mo never touches it.
    /// Pulse/glint literal numbers below are a Presentation-only placeholder pass
    /// (game feel proper is Phase 8) — `GameFeelConfig` already carries the
    /// project's exact-value fields (П-4); moving these in is a T25 candidate, not
    /// done here to avoid growing that SO for a still-provisional feel.
    public sealed class MobView : MonoBehaviour
    {
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly Color FlashAccent = new Color(4f, 4f, 4f);

        // Telegraph pulse (Chaser windup — spec §3.6, "base for dodging"): a
        // warm amber accent, distinct from the white `FlashAccent` hit-flash, that
        // ramps in linearly over the Chaser's actual `MobConfig.TelegraphSeconds`
        // (passed into `Sync` by `ViewRegistry`, L-13 fix-round — see that call
        // site's doc; previously a local `TelegraphRampSeconds` const duplicated
        // this number, so a balance re-tune of `MobConfig.TelegraphSeconds` would
        // silently desync the ramp from the real windup) so the ramp reads as
        // "fully charged" right as the strike lands, while oscillating at
        // `TelegraphPulseHz` so it reads as a pulse, not a flat fade-in.
        // `TelegraphPulseFloor` keeps the wave from dipping to fully-off once
        // ramped, so the tell stays legible even at a trough.
        static readonly Color TelegraphAccent = new Color(3f, 2.4f, 0.3f);
        const float TelegraphPulseHz = 6f;
        const float TelegraphPulseFloor = 0.35f;

        // Gunner "aiming" glint (Fire state): a light, low-amplitude cool-white
        // shimmer — deliberately subtler than the telegraph pulse, since a Gunner's
        // shot itself (muzzle flash / tracer) is the actual attack tell, this is
        // just an ambient "I'm aiming" read.
        static readonly Color GunnerGlintAccent = new Color(0.6f, 0.6f, 1.3f);
        const float GunnerGlintHz = 3f;

        // Task 25 (owner requirement, веха 3): the hit-flash/accent read must be
        // independent of the placeholder capsule mesh — a future model swap
        // brings its own renderer hierarchy (body + attachments, possibly
        // `SkinnedMeshRenderer`), not a single top-level `MeshRenderer`.
        // `GetComponentsInChildren<Renderer>(true)` is cached once here (Awake,
        // not per-frame) and every entry gets the SAME `MaterialPropertyBlock`
        // applied in `ApplyEmission` — this is the one place both the
        // hit-flash (`Flash`) and the telegraph-pulse/Fire-glint accents (`Sync`)
        // ultimately write through, so both automatically cover the whole model
        // once one exists, with no further change needed here.
        Renderer[] _renderers;
        MaterialPropertyBlock _block;
        Color _baseEmission;
        float _flashTimer;
        float _flashDuration;

        // Task 25 (Приложение П-7, `HitstopScope.TargetOnly`): while this timer
        // is running, `ViewRegistry.SyncMobs` skips writing `transform.position`
        // for this view — it holds exactly where it was instead of continuing to
        // interpolate, while every other mob/projectile/the player/the camera
        // keep moving normally off the live pair. Ticks unscaled, same as
        // `_flashTimer`, so hitstop/slow-mo (there is none — `Time.timeScale` is
        // never touched, see `SimulationRunner`) never affects the timer itself.
        float _freezePositionTimer;

        /// Set first thing in `Bind`, from the bound entity's `MobState.Type`
        /// (T9 Interfaces — consumed by T10).
        public MobType Type { get; private set; }

        /// Cached in `Awake`; null when this instance is the capsule fallback
        /// (no `MobVisual` sibling component) — T10 checks this before driving
        /// mech-specific animation.
        public MobVisual Visual { get; private set; }

        public bool IsPositionFrozen => _freezePositionTimer > 0f;

        void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _block = new MaterialPropertyBlock();
            Visual = GetComponent<MobVisual>();
        }

        /// Rebinds this (pooled) view to a freshly assigned entity: records the
        /// archetype (`Type`), resets the base emission to black (9a), and
        /// clears any leftover flash state from a previous life in the pool.
        /// Only sets the resting baseline — `ViewRegistry` calls `Sync` right
        /// after this (same frame) to layer in the state-driven accent for the
        /// entity's current tick.
        public void Bind(in MobState m)
        {
            Type = m.Type;
            _baseEmission = Color.black;
            _flashTimer = 0f;
            _freezePositionTimer = 0f; // pool-rebind hygiene, same as the flash timer above
            ApplyEmission(_baseEmission);
        }

        /// Per-frame accent pass (Task 21 Interfaces): layers the Chaser telegraph
        /// pulse or the Gunner Fire-state glint on top of the base accent, then the
        /// hit-flash decay on top of that. Called once per render frame by
        /// `ViewRegistry.SyncMobs` for every live view (new AND continuing) — the
        /// sole place `ApplyEmission` is invoked from, so there is exactly one
        /// write to the property block per view per frame. `telegraphSeconds`
        /// (L-13 fix-round) is `ViewRegistry`'s own read of
        /// `_runner.World.Config.Chaser.TelegraphSeconds` — the single source of
        /// truth the ramp now tracks instead of a locally-duplicated constant.
        public void Sync(in MobState m, float telegraphSeconds)
        {
            Color emission = _baseEmission;

            if (m.Ai == MobAiState.Telegraph)
            {
                float ramp = Mathf.Clamp01(m.StateTimer / telegraphSeconds);
                float wave = 0.5f + 0.5f * Mathf.Sin(m.StateTimer * TelegraphPulseHz * Mathf.PI * 2f);
                float intensity = ramp * Mathf.Lerp(TelegraphPulseFloor, 1f, wave);
                emission += TelegraphAccent * intensity;
            }
            else if (m.Type == MobType.Gunner && m.Ai == MobAiState.Fire)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * GunnerGlintHz * Mathf.PI * 2f);
                emission += GunnerGlintAccent * wave;
            }

            if (_flashTimer > 0f)
            {
                float t = Mathf.Clamp01(_flashTimer / _flashDuration);
                emission += FlashAccent * t;
            }

            ApplyEmission(emission);
        }

        /// Full hit-flash implementation (spec Interfaces, Task 17): decays the
        /// emission from `FlashAccent` back to the archetype's base color over
        /// `duration`, driven by unscaled time so hitstop/slow-mo never affects it.
        /// Task 25 only has to wire the call to ProjectileHit events — the method
        /// itself already works end to end. `Update` here only counts the timer
        /// down; `Sync` (above) is what actually applies the resulting color, so a
        /// flash always composes correctly with whatever state accent is active
        /// that frame instead of racing it for the last `ApplyEmission` call.
        public void Flash(float duration)
        {
            _flashDuration = Mathf.Max(duration, 1e-4f);
            _flashTimer = _flashDuration;
        }

        /// `GameFeelDirector`'s `HitstopScope.TargetOnly` hook (Task 25 Interfaces)
        /// — see `_freezePositionTimer`'s doc above. Reentrant the same way
        /// `Flash` is: a later call while already frozen simply restamps the
        /// timer, it doesn't stack.
        public void FreezePosition(float seconds) => _freezePositionTimer = Mathf.Max(seconds, 1e-4f);

        /// `GameFeelDirector.ForceEndHitstop`'s early-out (e.g. `PlayerDied`) —
        /// clears the freeze immediately instead of waiting for the timer to run
        /// out on its own.
        public void ClearPositionFreeze() => _freezePositionTimer = 0f;

        void Update()
        {
            if (_flashTimer > 0f) _flashTimer -= Time.unscaledDeltaTime;
            if (_freezePositionTimer > 0f) _freezePositionTimer -= Time.unscaledDeltaTime;
        }

        void ApplyEmission(Color emission)
        {
            _block.SetColor(EmissionColorId, emission);
            for (int i = 0; i < _renderers.Length; i++) _renderers[i].SetPropertyBlock(_block);
        }
    }
}
