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
        WaveCleared,
        /// Task 9: a dash attempt was gated by the stamina pool (Stamina <
        /// DashStaminaCost) — Amount carries the missing cost, Pos the player.
        StaminaDenied
    }

    public struct SimEvent
    {
        public SimEventKind Kind;
        public int Tick;
        public float2 Pos;
        public int EntityId;
        public MobType MobType;
        /// Per-`Kind` payload: damage dealt for ProjectileHit/PlayerDamaged (and
        /// for MobDied, the killing blow's amount) — since Task 6 that is the
        /// damage AFTER the hit-zone multiplier, i.e. exactly what was subtracted
        /// from the victim's Hp, not the projectile's base Damage; the shot's
        /// sim-plane velocity angle (`atan2(vel.y, vel.x)` radians,
        /// `SimulationWorld.SpawnProjectile`) for ProjectileFired — Presentation
        /// needs a tick-exact fire direction, and `Curr.Player.Pos` at
        /// `TicksFlushed` time is wrong for this during a multi-tick catch-up
        /// flush; unused (0) for every other kind.
        public float Amount;
        /// F-3 fix-round: who fired the shot behind a ProjectileFired event
        /// (`SimulationWorld.SpawnProjectile`) — without this, Presentation had no
        /// way to tell a mob's gunfire from the player's own, so a Gunner's shot
        /// spawned the player's own shell casing, played the player's `_shotClip`
        /// (eating into its `MinSfxInterval`/`VoicesPerSfx` budget), and could
        /// wrongly consume `MuzzleFlashView`/`AudioDirector`'s predicted-shot latch
        /// (bd app-ai2). Defaults to `ProjectileOwner.Player` (its zero value) and
        /// is meaningless for every other `Kind`, same "unused for every other
        /// kind" contract as `Amount` above.
        public ProjectileOwner Owner;
        /// Task 6: the vertical hit-zone the blow landed in — meaningful for
        /// ProjectileHit, MobDied, PlayerDamaged and PlayerDied (for the two
        /// death kinds it is the killing blow's zone), so Presentation can pick
        /// zone-specific feedback (headshot ping, leg stagger) without
        /// re-deriving any geometry. Same "unused for every other kind" contract
        /// as `Amount`/`Owner` above, and its unused value is the enum's zero
        /// (`HitZone.None`).
        public HitZone Zone;
        /// Task 6: unit impact direction in the sim plane — the projectile's
        /// direction of travel at contact, or attacker→victim for a melee
        /// strike. Drives directional feedback (blood spray, hit flash, knock
        /// reaction) that would otherwise need the attacker's position, which
        /// the event does not carry. Zero for every kind that has no blow behind
        /// it; paired with `Zone` above and never read without it.
        public float2 HitDir;
    }
}
