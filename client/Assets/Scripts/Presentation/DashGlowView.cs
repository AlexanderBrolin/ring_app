using UnityEngine;

namespace Ring.Presentation
{
    /// Pooled glowing floor mark at a dash start point (app-9av, Б1 milestone
    /// owner request): a flat unlit quad whose `_BaseColor` cools from HDR
    /// `GlowColor` to black over `DashGlowSeconds`, then deactivates. FIFO-reused
    /// by PersistentPropsDirector's RingBuffer, same shape as CorpseView.
    ///
    /// Review fix-round: the original implementation used a `DecalProjector`
    /// with `fadeFactor`, mirroring `PersistentPropsDirector`'s scorch decal.
    /// That was functionally dead — URP's shipped `Decal.shadergraph` has no
    /// Emission block at all (confirmed by reading the shader graph source),
    /// so `EnableKeyword("_EMISSION")`/`SetColor("_EmissionColor", …)` on a
    /// decal material is a no-op (the keyword lands in `m_InvalidKeywords`);
    /// the mark would have rendered as a barely-visible `_BaseColor` smudge,
    /// not a glow. This class instead follows the `TracerTrail`/`SpreadCone`
    /// family: an HDR color written straight to `_BaseColor` on an
    /// `Universal Render Pipeline/Unlit` material via `MaterialPropertyBlock`
    /// (same `MobView`-style pattern — one shared material, per-instance color
    /// only through the property block, никогда per-instance material
    /// instances) — that shader's `_BaseColor` IS what drives HDR bloom on an
    /// unlit surface, unlike a decal's.
    public sealed class DashGlowView : MonoBehaviour
    {
        const float FloorLift = 0.01f; // avoid z-fighting with the floor mesh

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly Color GlowColor = new Color(0f, 2.5f, 3f); // HDR, = PlayerEmissive (Э1)

        [SerializeField] Renderer _renderer;

        MaterialPropertyBlock _block;
        float _timer;
        float _duration;

        void Awake() => _block = new MaterialPropertyBlock();

        public void Spawn(Vector3 pos, float seconds, float size)
        {
            gameObject.SetActive(true);
            transform.SetPositionAndRotation(pos + Vector3.up * FloorLift, Quaternion.Euler(90f, 0f, 0f));
            transform.localScale = Vector3.one * size;
            _duration = Mathf.Max(seconds, 1e-3f);
            _timer = _duration;
            ApplyColor(GlowColor);
        }

        void Update()
        {
            if (_timer <= 0f) return;
            _timer -= Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_timer / _duration);
            // Cooling to black on the dark greybox floor reads as the mark
            // fading out — same trick CorpseView's glow uses.
            ApplyColor(GlowColor * t);
            if (_timer <= 0f) gameObject.SetActive(false);
        }

        void ApplyColor(Color c)
        {
            _block.SetColor(BaseColorId, c);
            _renderer.SetPropertyBlock(_block);
        }
    }
}
