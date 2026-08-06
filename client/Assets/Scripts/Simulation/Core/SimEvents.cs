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
        /// Task 10 reuses this same kind for a gated slide attempt (Stamina <
        /// SlideStaminaCost) — same "Amount = missing cost" payload contract.
        StaminaDenied,
        /// Task 10: a slide started — Pos is the player's position for this
        /// tick (spec §3.4 payload convention, same as PlayerDashed), HitDir
        /// carries the slide's travel direction (SlideDir).
        PlayerSlideStarted,
        /// Task 12: an active dash mirrored off a wall/obstacle — Pos is the
        /// contact point (MoveWithCollisions' first-contact `contact` out
        /// param, not the player's post-slide position), HitDir carries the
        /// surface normal at contact (same "unused for every other kind"
        /// convention as Amount/Owner/Zone above).
        DashRicocheted
    }

    public struct SimEvent
    {
        public SimEventKind Kind;
        public int Tick;
        /// World-space position this event concerns — per-Kind meaning varies
        /// (e.g. the projectile's contact point for ProjectileHit, the mob's
        /// own position for MobDied). PlayerDamaged/PlayerDied (Stage 2 Task 8
        /// fix-round 1, I-1): normally the BLOW's own origin — the attacking
        /// mob's or projectile's position `SimulationWorld.DamagePlayer` was
        /// called with, NOT necessarily the victim's — so a paired
        /// PlayerDamaged+PlayerDied from the same hit carry the SAME Pos.
        /// PlayerDied is the one exception: it can also fire with no blow at
        /// all (`KillPlayerNoDamage` — a player exiting the match), in which
        /// case Pos is the victim's OWN last-known position instead, since
        /// there is no blow to place.
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
        /// (`HitZone.None`). Stage 2 Task 8 fix-round 1 (M-6): PlayerDied can
        /// also fire with NO blow behind it at all (`KillPlayerNoDamage` — a
        /// player exiting the match) — in that one case Zone reads its unused
        /// `HitZone.None`, same as every kind that never carries a blow.
        public HitZone Zone;
        /// Task 6: unit impact direction in the sim plane — the projectile's
        /// direction of travel at contact, or attacker→victim for a melee
        /// strike. Drives directional feedback (blood spray, hit flash, knock
        /// reaction) that would otherwise need the attacker's position, which
        /// the event does not carry. Zero for every kind that has no blow behind
        /// it; paired with `Zone` above and never read without it. Same
        /// no-blow PlayerDied exception as `Zone` above (`KillPlayerNoDamage`) —
        /// reads `float2.zero` there.
        public float2 HitDir;
        /// Stage 2 Task 7: which player this event concerns, under three
        /// conventions picked per kind.
        /// ACTOR — the five "own-action" kinds ProjectileFired, PlayerDashed,
        /// PlayerSlideStarted, DashRicocheted, StaminaDenied
        /// (SimulationWorld.TickMovement's own per-player loop index /
        /// SpawnProjectile's ownerIndex).
        /// VICTIM — PlayerDamaged/PlayerDied (mirrors EntityId's convention for
        /// those two kinds, spec §3.2); the attacker is deliberately not
        /// reported, there is only one player slot on the struct and for a
        /// damage/death pair the victim is the one Presentation places the
        /// feedback on.
        /// ATTACKER — ProjectileHit/MobDied, added by Stage 2 Task 17
        /// (carryover-t17.md item 2): the SHOOTER behind the blow, i.e. the
        /// projectile's OwnerIndex (ProjectileIds.NoOwner for a mob's round).
        /// Without it Presentation cannot tell "my hit" from another player's
        /// when placing a hitmarker in a multiplayer match — the victim of those
        /// two kinds is a mob, already identified by EntityId/MobType.
        /// Unused (ProjectileIds.NoOwner) for every other kind, same "unused for
        /// every other kind" contract as `Amount`/`Owner`/`Zone` above. Not part
        /// of StateHash — events are excluded from the hash entirely (spec §3.7,
        /// see this struct's own doc comment).
        public byte PlayerIndex;
    }
}
