using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// One VFX/SFX-relevant occurrence produced during a tick (spec §3.7).
    /// Consumed by the presentation layer and cleared every render frame —
    /// events are not part of StateHash (not authoritative gameplay state).
    public enum SimEventKind : byte
    {
        ProjectileFired,
        ProjectileHit,
        ProjectileBlocked,
        ProjectileExpired,
        MobSpawned,
        MobDied,
        PlayerDamaged,
        PlayerDashed,
        PlayerDied,
        WaveStarted,
        WaveCleared
    }

    public struct SimEvent
    {
        public SimEventKind Kind;
        public int Tick;
        public float2 Pos;
        public int EntityId;
        public MobType MobType;
        /// Per-`Kind` payload: damage dealt for ProjectileHit/PlayerDamaged; the
        /// shot's sim-plane velocity angle (`atan2(vel.y, vel.x)` radians,
        /// `SimulationWorld.SpawnProjectile`) for ProjectileFired — Presentation
        /// needs a tick-exact fire direction, and `Curr.Player.Pos` at
        /// `TicksFlushed` time is wrong for this during a multi-tick catch-up
        /// flush; unused (0) for every other kind.
        public float Amount;
    }
}
