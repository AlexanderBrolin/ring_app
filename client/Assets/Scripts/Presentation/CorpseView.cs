using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for a single pooled mob corpse (Task 27, spec §3.11) —
    /// a simple "lying" capsule primitive with a cooling emissive glow. This is
    /// its OWN prefab, spawned by `PersistentPropsDirector` purely from the
    /// `MobDied` event's own position (owner requirement, веха 3: "партикли/
    /// декали/гильзы/трупы от позиций событий, никаких привязок к мешам
    /// вьюх") — `ViewRegistry` retires/pools the dead mob's `MobView` on the
    /// same event independently; this class never touches or references it.
    /// Pooled and (re)bound purely by `PersistentPropsDirector`'s
    /// `RingBuffer&lt;CorpseView&gt;`.
    /// `Awake` caches `GetComponentsInChildren&lt;Renderer&gt;` rather than a
    /// single `MeshRenderer` (same treatment as `MobView`, same owner
    /// requirement) so a future model swap's whole hierarchy still gets the
    /// glow/fade without this class changing.
    public sealed class CorpseView : MonoBehaviour
    {
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly Color ChaserGlow = new Color(2.5f, 0.2f, 0.2f);
        static readonly Color GunnerGlow = new Color(0.2f, 0.6f, 2.5f);
        const float GlowFadeSeconds = 3f;

        Renderer[] _renderers;
        MaterialPropertyBlock _block;
        Color _baseGlow;
        float _fadeTimer;

        void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _block = new MaterialPropertyBlock();
        }

        /// (Re)spawns this pooled instance lying on its side at `pos`, glowing
        /// at full archetype-accent intensity right after death and cooling to
        /// black over `GlowFadeSeconds`. A fresh random yaw each spawn
        /// (cosmetic-only `UnityEngine.Random`) so a FIFO-reused slot doesn't
        /// read as literally the same body landing in the same orientation
        /// every time.
        public void Spawn(Vector3 pos, MobType type)
        {
            gameObject.SetActive(true);
            float yaw = Random.Range(0f, 360f);
            transform.SetPositionAndRotation(pos, Quaternion.Euler(90f, yaw, 0f));
            _baseGlow = type == MobType.Chaser ? ChaserGlow : GunnerGlow;
            _fadeTimer = GlowFadeSeconds;
            ApplyEmission(_baseGlow);
        }

        void Update()
        {
            if (_fadeTimer <= 0f) return;
            _fadeTimer -= Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_fadeTimer / GlowFadeSeconds);
            ApplyEmission(_baseGlow * t);
        }

        void ApplyEmission(Color emission)
        {
            _block.SetColor(EmissionColorId, emission);
            for (int i = 0; i < _renderers.Length; i++) _renderers[i].SetPropertyBlock(_block);
        }
    }
}
