using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Presentation
{
    /// Presentation view for a single pooled mob corpse (Task 27, spec §3.11)
    /// with a cooling emissive glow. Its OWN prefab, spawned by
    /// `PersistentPropsDirector` purely from the `MobDied` event's own
    /// position (owner requirement, веха 3: "партикли/декали/гильзы/трупы от
    /// позиций событий, никаких привязок к мешам вьюх") — `ViewRegistry`
    /// retires/pools the dead mob's `MobView` on the same event
    /// independently; this class never touches or references it. Pooled and
    /// (re)bound purely by `PersistentPropsDirector`'s
    /// `RingBuffer&lt;CorpseView&gt;`.
    /// `Awake` caches `GetComponentsInChildren&lt;Renderer&gt;` rather than a
    /// single `MeshRenderer` (same treatment as `MobView`, same owner
    /// requirement) so a future model swap's whole hierarchy still gets the
    /// glow/fade without this class changing.
    /// Mech corpse (Б4, assets phase B): the prefab carries two Visual
    /// children, `_chaserVisual`/`_gunnerVisual` (each with its own
    /// `Animator`), toggled by `Spawn`'s `type` — the mech falls to the
    /// ground via its own `AnimIds.MechDeath` clip rather than a scripted
    /// lying-on-side rotation. A FIFO-reused pooled slot's animator arrives
    /// disabled (left that way by the previous life's death finishing, see
    /// `Update`) so `Spawn` re-arms it every call (enable + `Rebind` +
    /// re-`Play` the Death state at t=0). The old capsule-primitive path
    /// (`_chaserVisual == null`: rotate flat on its side, no `Animator`) is
    /// kept honest in code for both branches, but after T12 wires the mech
    /// prefab into `PersistentPropsDirector` the capsule prefab is no longer
    /// reachable from the scene (ПБ17).
    public sealed class CorpseView : MonoBehaviour
    {
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly Color ChaserGlow = new Color(2.5f, 0.2f, 0.2f);
        static readonly Color GunnerGlow = new Color(0.2f, 0.6f, 2.5f);

        [SerializeField] GameObject _chaserVisual;
        [SerializeField] GameObject _gunnerVisual;
        [SerializeField] Animator _chaserAnimator;
        [SerializeField] Animator _gunnerAnimator;

        Renderer[] _renderers;
        MaterialPropertyBlock _block;
        Color _baseGlow;
        float _fadeTimer;
        float _fadeDuration;

        Animator _activeAnimator;
        bool _animatorLive;

        void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _block = new MaterialPropertyBlock();
        }

        /// (Re)spawns this pooled instance at `pos`, glowing at full
        /// archetype-accent intensity right after death and cooling to black
        /// over `glowFadeSeconds`. A fresh random yaw each spawn
        /// (cosmetic-only `UnityEngine.Random`) so a FIFO-reused slot doesn't
        /// read as literally the same body landing in the same orientation
        /// every time. `glowFadeSeconds` comes straight from
        /// `GameFeelConfig.CorpseGlowFadeSeconds` every call (review fix-round
        /// — was a local `const`), same hot-tweak contract as `CasingView.
        /// Spawn`'s `settleSeconds` parameter.
        /// Mech vs. capsule (Б4): the mech falls via its own `AnimIds.
        /// MechDeath` clip, upright at spawn (`Quaternion.Euler(0f, yaw, 0f)`)
        /// — the capsule prefab still lies flat on its side
        /// (`Quaternion.Euler(90f, yaw, 0f)`) as before, kept honest but no
        /// longer wired anywhere after T12.
        /// В1/В2 fix-wave 2 (app-n6g item 2, BUG fix): `visualScale` mirrors
        /// `MobVisual.Bind`'s own `_visual.localScale = Vector3.one *
        /// visualScale` write for the LIVE mob (`ViewRegistry.SyncMobs`'
        /// `GameFeelConfig.ChaserVisualScale`/`GunnerVisualScale` read) — the
        /// caller (`PersistentPropsDirector.HandleMobDied`) resolves the same
        /// archetype scale from the `MobDied` event's own `MobType` and passes
        /// it straight through, no new SO field needed. Before this fix the
        /// corpse's `_chaserVisual`/`_gunnerVisual` child kept whatever scale
        /// was baked on the CorpseView prefab (1x) regardless of the dying
        /// mob's own scale, so a Gunner corpse visibly popped to a different
        /// size than the mob had a frame earlier.
        public void Spawn(Vector3 pos, MobType type, float glowFadeSeconds, float visualScale)
        {
            gameObject.SetActive(true);
            float yaw = Random.Range(0f, 360f);
            bool mech = _chaserVisual != null;
            transform.SetPositionAndRotation(pos,
                mech ? Quaternion.Euler(0f, yaw, 0f) : Quaternion.Euler(90f, yaw, 0f));
            if (mech)
            {
                bool chaser = type == MobType.Chaser;
                _chaserVisual.SetActive(chaser);
                _gunnerVisual.SetActive(!chaser);
                GameObject activeVisual = chaser ? _chaserVisual : _gunnerVisual;
                activeVisual.transform.localScale = Vector3.one * visualScale;
                _activeAnimator = chaser ? _chaserAnimator : _gunnerAnimator;
                // Mandatory re-arm: a FIFO-reused slot arrives with the animator
                // disabled by a finished previous death (Б4).
                _activeAnimator.enabled = true;
                _activeAnimator.Rebind();
                if (!_activeAnimator.HasState(0, AnimIds.MechDeath))
                    Debug.LogError("CorpseView: controller has no Death state: " + name);
                _activeAnimator.Play(AnimIds.MechDeath, 0, 0f);
                _activeAnimator.Update(0f);
                _animatorLive = true;
            }
            _baseGlow = type == MobType.Chaser ? ChaserGlow : GunnerGlow;
            _fadeDuration = Mathf.Max(glowFadeSeconds, 1e-4f);
            _fadeTimer = _fadeDuration;
            ApplyEmission(_baseGlow);
        }

        void Update()
        {
            if (_animatorLive && AnimIds.OneShotFinished(_activeAnimator, 0, AnimIds.MechDeath))
            {
                // Controller evaluation off; the SkinnedMeshRenderer keeps skinning —
                // profiled at milestone Б2, not "free" (Б4).
                _activeAnimator.enabled = false;
                _animatorLive = false;
            }
            if (_fadeTimer <= 0f) return;
            _fadeTimer -= Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_fadeTimer / _fadeDuration);
            ApplyEmission(_baseGlow * t);
        }

        void ApplyEmission(Color emission)
        {
            _block.SetColor(EmissionColorId, emission);
            for (int i = 0; i < _renderers.Length; i++) _renderers[i].SetPropertyBlock(_block);
        }
    }
}
