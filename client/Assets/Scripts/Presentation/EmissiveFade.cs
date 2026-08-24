using UnityEngine;

namespace Ring.Presentation
{
    /// One view's "dim me by this much" mechanism (Stage 3 Т33d, bd
    /// `app-tut2`): the renderers it owns, the colors they were authored with,
    /// and a `MaterialPropertyBlock` to scale those colors through.
    ///
    /// A PLAIN CLASS, NOT A COMPONENT, so a view owns one the way it owns a
    /// timer — no extra object on the prefab, nothing for a bootstrap to wire,
    /// and no second lifetime to keep in step with the view's own.
    ///
    /// WHY IT IS SHARED AND `MobView`/`PlayerView` ARE NOT REWRITTEN ONTO IT.
    /// Those two COMPOSE their emission every frame out of state — the hit
    /// flash, the AI-state accent, the archetype's base — and hold the composed
    /// color precisely so a fade can re-apply it dimmed. A cell and a box have
    /// no state to compose: they are one authored color that never changes, so
    /// what they need is not the composer but only the dimmer. Lifting the
    /// dimmer here and leaving the composers where they are keeps one home per
    /// job rather than one home for two jobs.
    ///
    /// BOTH COLOR PROPERTIES, BECAUSE THIS PROJECT PAINTS WITH BOTH. The energy
    /// cell and the corpse marker are URP **Unlit** materials carrying an HDR
    /// `_BaseColor` (`StageOneSceneBootstrap.GetOrCreateUnlitMaterial`, the same
    /// family `DashGlowView` and `TracerTrail` belong to); the crate, cache and
    /// bundle are pack models on **Lit** materials whose glow is
    /// `_EmissionColor`. Writing a property a shader does not declare is a
    /// no-op in a property block, so asking for both costs nothing and removes
    /// the one thing this class would otherwise have to be told.
    ///
    /// AND THE AUTHORED COLOR IS READ PER RENDERER. A prop is several
    /// materials, not one, so a single remembered color would repaint the whole
    /// model in whichever one happened to be first.
    public sealed class EmissiveFade
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        Renderer[] _renderers;
        Color[] _baseColor;
        Color[] _emissionColor;
        bool[] _hasBaseColor;
        bool[] _hasEmissionColor;
        MaterialPropertyBlock _block;

        /// Reads what the prefab was authored with, once. Called from the
        /// view's `Awake` — the shared materials are what the bootstrap put
        /// there and nothing writes them at runtime, so one read is the whole
        /// truth for the life of the pool.
        public void Capture(GameObject root)
        {
            _renderers = root.GetComponentsInChildren<Renderer>(true);
            _baseColor = new Color[_renderers.Length];
            _emissionColor = new Color[_renderers.Length];
            _hasBaseColor = new bool[_renderers.Length];
            _hasEmissionColor = new bool[_renderers.Length];
            _block = new MaterialPropertyBlock();

            for (int i = 0; i < _renderers.Length; i++)
            {
                Material material = _renderers[i].sharedMaterial;
                if (material == null) continue;
                _hasBaseColor[i] = material.HasProperty(BaseColorId);
                _hasEmissionColor[i] = material.HasProperty(EmissionColorId);
                if (_hasBaseColor[i]) _baseColor[i] = material.GetColor(BaseColorId);
                if (_hasEmissionColor[i]) _emissionColor[i] = material.GetColor(EmissionColorId);
            }
        }

        /// Re-applies the authored colors scaled by how much of the fade is
        /// LEFT: 1 is the prefab as authored, 0 is black.
        ///
        /// SCALING RATHER THAN LERPING TO A TARGET, the same arithmetic
        /// `MobView.FadeEmission` and `DashGlowView` already cool by — on the
        /// dark greybox floor a color falling to black reads as the thing
        /// leaving, and it needs no second color to be told what to leave
        /// toward.
        public void Apply(float remaining)
        {
            if (_renderers == null) return;
            float k = Mathf.Clamp01(remaining);
            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] == null) continue;
                // Cleared per renderer rather than reused, because the two
                // entries below differ between renderers of one model and a
                // block carries whatever was last written into it.
                _block.Clear();
                if (_hasBaseColor[i]) _block.SetColor(BaseColorId, _baseColor[i] * k);
                if (_hasEmissionColor[i]) _block.SetColor(EmissionColorId, _emissionColor[i] * k);
                _renderers[i].SetPropertyBlock(_block);
            }
        }
    }
}
