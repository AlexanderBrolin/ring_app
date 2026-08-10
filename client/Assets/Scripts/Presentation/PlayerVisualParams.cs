namespace Ring.Presentation
{
    /// Per-frame parameter pack for PlayerVisual.Sync (Stage 2 Task 45a) —
    /// built ONCE per frame by ViewRegistry from GameFeelConfig and the
    /// runner's own Config/Paused, so a pooled doll holds no scene or SO
    /// reference of its own (spec §3.12). Same shape and the same reason as
    /// MobVisualParams next door; the two are deliberately separate structs
    /// because the doll and the mech read different feel numbers, and one
    /// merged pack would hand each half fields it must never act on.
    ///
    /// THE AIM POINT IS NOT HERE, and that is the point. It differs per doll —
    /// the local player aims with a cursor no remote player has — so it rides
    /// `PlayerState.AimPoint`, which ViewRegistry resolves per slot before it
    /// calls Sync (see `ViewRegistry.SyncPlayers`). A field here would be one
    /// value for every doll in the frame, which is exactly the mistake.
    public struct PlayerVisualParams
    {
        public float MaxSpeed, SpeedDampTime, MoveThreshold01;
        public float YawOffsetDeg, VisualTurnDegPerSec, IdleAimTurnDegPerSec;
        public float AimYawClampDeg, SpineYawShare;
        public float DashLeanDeg, DashLeanInOutSeconds;
        public float LocomotionCrossFadeSeconds, OneShotCrossFadeSeconds;
        public float DeltaTime;
        public bool Paused;
    }
}
