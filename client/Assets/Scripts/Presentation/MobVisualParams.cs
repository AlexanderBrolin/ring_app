using UnityEngine;

namespace Ring.Presentation
{
    /// Parameter pack for MobVisual.Sync — built by ViewRegistry from
    /// GameFeelConfig AND the runner's config: all of it once per frame except
    /// the one field named below, which is per body (pooled prefab components
    /// hold no scene/SO references of their own, spec Б5).
    ///
    /// ⚠ THAT ONE FIELD, AND WHY IT IS NOT FRAME-WIDE (app-94sk T5b):
    /// `TurnDegPerSec` moved from GameFeelConfig, where it was ONE number for
    /// every mob alive, into `MobSimConfig`, where it is PER ARCHETYPE — so
    /// ViewRegistry writes it per body inside its own loop, off the archetype
    /// config it already looks up there, and the rest of the pack is still
    /// built once. The alternative was a second Sync parameter, which would
    /// have moved a client-zone signature for no gain.
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
