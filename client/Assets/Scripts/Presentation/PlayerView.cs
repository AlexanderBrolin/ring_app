using UnityEngine;

namespace Ring.Presentation
{
    /// Positions the player root from the runner's interpolated snapshots
    /// (spec §3.7/§3.11) — pure presentation, П-7. Since assets phase B the
    /// root no longer rotates and carries no renderer: the doll lives on the
    /// "Visual" child, PlayerVisual owns facing/animation (spec §3.2). Root
    /// pivot sits on the ground — the E1 capsule offset went with the capsule.
    public sealed class PlayerView : MonoBehaviour
    {
        [SerializeField] SimulationRunner _runner;

        void LateUpdate()
        {
            transform.position = _runner.RenderPlayerWorldPos;
        }
    }
}
