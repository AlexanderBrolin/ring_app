using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for a single live mob (spec §3.6/§3.7). Pooled and
    /// (re)bound purely by `ViewRegistry` — no other class instantiates, destroys,
    /// or repositions a `MobView`. The capsule's material is one shared asset
    /// (`MobEmissive`) across every instance; per-archetype color comes only from a
    /// `MaterialPropertyBlock` override applied in `Bind`, never a material
    /// instance (П-2: no per-instance materials).
    public sealed class MobView : MonoBehaviour
    {
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly Color ChaserAccent = new Color(3.5f, 0.25f, 0.25f);
        static readonly Color GunnerAccent = new Color(0.25f, 0.8f, 3.5f);
        static readonly Color FlashAccent = new Color(4f, 4f, 4f);

        MeshRenderer _renderer;
        MaterialPropertyBlock _block;
        Color _baseEmission;
        float _flashTimer;
        float _flashDuration;

        void Awake()
        {
            _renderer = GetComponent<MeshRenderer>();
            _block = new MaterialPropertyBlock();
        }

        /// Rebinds this (pooled) view to a freshly assigned entity: picks the
        /// per-archetype accent color and clears any leftover flash state from a
        /// previous life in the pool.
        public void Bind(MobType type)
        {
            _baseEmission = type == MobType.Chaser ? ChaserAccent : GunnerAccent;
            _flashTimer = 0f;
            ApplyEmission(_baseEmission);
        }

        /// Full hit-flash implementation (spec Interfaces, Task 17): decays the
        /// emission from `FlashAccent` back to the archetype's base color over
        /// `duration`, driven by unscaled time so hitstop/slow-mo never affects it.
        /// Task 25 only has to wire the call to ProjectileHit events — the method
        /// itself already works end to end.
        public void Flash(float duration)
        {
            _flashDuration = Mathf.Max(duration, 1e-4f);
            _flashTimer = _flashDuration;
        }

        void Update()
        {
            if (_flashTimer <= 0f) return;

            _flashTimer -= Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_flashTimer / _flashDuration);
            ApplyEmission(_baseEmission + FlashAccent * t);
        }

        void ApplyEmission(Color emission)
        {
            _block.SetColor(EmissionColorId, emission);
            _renderer.SetPropertyBlock(_block);
        }
    }
}
