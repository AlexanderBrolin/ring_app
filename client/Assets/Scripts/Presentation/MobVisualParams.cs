using UnityEngine;

namespace Ring.Presentation
{
    /// Per-frame parameter pack for MobVisual.Sync — built ONCE per frame by
    /// ViewRegistry from GameFeelConfig (pooled prefab components hold no
    /// scene/SO references, spec Б5).
    public struct MobVisualParams
    {
        public float WalkEnterSpeed, WalkExitSpeed, RunEnterSpeed, RunExitSpeed;
        public float HoldSeconds, TurnDegPerSec, YawOffsetDeg;
        public float LocomotionCrossFadeSeconds, OneShotCrossFadeSeconds;
        public float DeltaTime;
        public Vector3 PlayerPos;
        public bool Paused;
    }
}
