using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Ring.Presentation
{
    /// Pooled glowing floor mark at a dash start point (app-9av, Б1 milestone
    /// owner request): a downward DecalProjector whose fadeFactor cools to
    /// zero over DashGlowSeconds, then deactivates. FIFO-reused by
    /// PersistentPropsDirector's RingBuffer, same shape as CorpseView.
    public sealed class DashGlowView : MonoBehaviour
    {
        const float ProjectionLift = 0.5f;  // hover above the floor, project down
        const float ProjectionDepth = 1.5f; // z-size: reaches the floor with margin

        [SerializeField] DecalProjector _projector;

        float _timer;
        float _duration;

        public void Spawn(Vector3 pos, float seconds, float size)
        {
            gameObject.SetActive(true);
            transform.SetPositionAndRotation(pos + Vector3.up * ProjectionLift,
                Quaternion.LookRotation(Vector3.down, Vector3.forward));
            _projector.size = new Vector3(size, size, ProjectionDepth);
            _duration = Mathf.Max(seconds, 1e-3f);
            _timer = _duration;
            _projector.fadeFactor = 1f;
        }

        void Update()
        {
            if (_timer <= 0f) return;
            _timer -= Time.unscaledDeltaTime;
            _projector.fadeFactor = Mathf.Clamp01(_timer / _duration);
            if (_timer <= 0f) gameObject.SetActive(false);
        }
    }
}
