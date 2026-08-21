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
        DashRicocheted,
        /// Stage 2 Task 44a: a round ended ON A PLAYER. Deliberately its own
        /// kind rather than a widened `ProjectileHit`, for two reasons that are
        /// both about the CONSUMER, not about taste. First, `EntityId` here is a
        /// PLAYER SLOT while `ProjectileHit`'s is a MOB id, and the two id
        /// spaces overlap freely — a presentation layer that looks the number up
        /// in its mob registry would flash a bystanding mob that happens to
        /// share it, and `SimEvent` carries no field left to discriminate on
        /// (`MobType` has no "not a mob" member, `PlayerIndex` is spent on the
        /// shooter, `Owner` describes the round). Second,
        /// `Ring.Networking.Server.SnapshotAssembler` maps `ProjectileHit` to a
        /// hardcoded `ProjectileEndKind.HitMob`, so reusing it would put a
        /// literal falsehood on the wire.
        ///
        /// PAYLOAD: `Pos` = the contact point; `EntityId` = the VICTIM's player
        /// slot (same victim convention `ProjectileHit`'s `EntityId` follows);
        /// `MobType` unused; `Amount` = the round's post-multiplier damage;
        /// `Zone` = the vertical zone it landed in; `HitDir` = the round's
        /// travel direction at contact; `PlayerIndex` = the SHOOTER (ATTACKER
        /// convention, `ProjectileIds.NoOwner` for a mob's round);
        /// `SecondaryEntityId` = the ROUND's own id, since `EntityId` is spent
        /// on the victim exactly as it is for `ProjectileHit`.
        ///
        /// EMITTED ON EVERY REMOVAL, INCLUDING AN ABSORBED ONE. It reports that
        /// the ROUND ENDED, not that damage landed — dash i-frames make
        /// `SimulationWorld.DamagePlayer` a no-op while the round is still
        /// consumed, and a tracer whose end went unreported would hang until the
        /// client's own confirm timeout. `Amount` is therefore what the round
        /// CARRIED, which is the damage actually dealt in the ordinary case and
        /// strictly more than it when the victim absorbed the hit;
        /// `DamagePlayer` returns nothing, so the applied figure is not
        /// available at the emit site at all. A consumer that needs "damage that
        /// actually landed" must read `PlayerDamaged`, which is not emitted when
        /// the blow is refused.
        ProjectileHitPlayer,
        /// Stage 3 Т21 (spec §3.4/§3.5, Р299): a live collector stood in the
        /// core and the raid's endgame began — the Director is on his way and
        /// the early portals have just closed. Fired ON THE TRANSITION, once
        /// per raid, by MatchFlowSystem.
        ///
        /// PAYLOAD: none. Pos is float2.zero and every other field is unused —
        /// this kind rides the All channel (EventRelevance.ChannelFor), which
        /// carries no position by rule (Р28), and the position it would
        /// otherwise carry is the location of whichever collector walked in.
        /// Same shape as WaveStarted/WaveCleared next to it.
        DirectorActivated,
        /// Stage 3 Т21 (spec §3.4/§3.5): the Director has fallen, and the
        /// window of sharing at his corpse (GateDelaySeconds) starts now.
        /// Fired by MatchFlowSystem on the tick its scan first finds him gone
        /// — liveness is a scan over _mobs by type, never a field (Р218).
        ///
        /// PAYLOAD: none, for the same reason as DirectorActivated above —
        /// where he fell is where everyone is about to fight, and the All
        /// channel does not carry positions.
        DirectorDied,
        /// Stage 3 Т23 (spec §3.5 Р222/Р223): a collector held his channel to
        /// the end and LEFT THE RAID — PlayerIndex names him, Pos is the exit
        /// he walked out of. Deliberately its own kind rather than a
        /// PlayerDied with a flag: the two differ in everything a consumer
        /// cares about (no corpse, nothing to loot, and the man is not dead —
        /// Р223's own reason for Extracted being a separate bit at all).
        PlayerExtracted,

        /// Stage 3 Т29 (spec §3.6/§3.12 Р281): a ground cell was actually
        /// COLLECTED — the moment PickupSystem.Collect folds it into the
        /// collector's ammo. Deliberately NOT emitted when a cell ages out on
        /// its TTL: PickupSystem.AdvanceTtl's own doc records that a pickup
        /// quietly expiring is not a VFX/SFX-relevant occurrence, and this is
        /// the kind it contrasts itself with.
        ///
        /// PAYLOAD: `EntityId` = the cell's own id (it rides the wire as the
        /// same u16 code every long-lived entity does, Р278); `PlayerIndex` =
        /// the COLLECTOR, and it is load-bearing rather than informational —
        /// this kind rides the Owner channel, which addresses its recipient
        /// by exactly that field (EventRelevance.ShouldDeliver), so an emit
        /// that omitted it would deliver to nobody. `Pos` is the cell's, for
        /// the surface that wants to play the pop where it lay.
        PickupTaken,

        /// Stage 3 Т29 (spec §3.16, §3.12 Р281): a container's LAST item just
        /// left it — emitted by Loot.LootOps.Update on the tick a transfer
        /// completes and finds nothing behind it. The state itself already
        /// rides every frame (the Containers block's "already looted" flag);
        /// what this kind carries is the MOMENT, which a per-frame flag
        /// cannot express to a surface that wants to react once.
        ///
        /// PAYLOAD: `EntityId` = the container's own id (u16 on the wire, as
        /// above). `Pos` is the container's — this kind rides the Visible
        /// channel, so the two collectors who can see the box learn the
        /// pile they were racing for is spent.
        ContainerEmptied
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
        /// from the victim's Hp, not the projectile's base Damage.
        /// ProjectileHitPlayer (Stage 2 Task 44a) carries the same
        /// post-multiplier figure but under a WEAKER claim — the round's
        /// carried damage, which the victim's i-frames may have refused
        /// entirely (see that kind's own doc); the shot's
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
        /// ProjectileHit, ProjectileHitPlayer (Stage 2 Task 44a), MobDied,
        /// PlayerDamaged and PlayerDied (for the two
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
        /// (carryover-t17.md item 2), and ProjectileHitPlayer, added by Stage 2
        /// Task 44a: the SHOOTER behind the blow, i.e. the
        /// projectile's OwnerIndex (ProjectileIds.NoOwner for a mob's round).
        /// Without it Presentation cannot tell "my hit" from another player's
        /// when placing a hitmarker in a multiplayer match — the victim of the
        /// first two kinds is a mob, already identified by EntityId/MobType,
        /// and ProjectileHitPlayer's victim is the player slot in EntityId.
        /// Unused (ProjectileIds.NoOwner) for every other kind, same "unused for
        /// every other kind" contract as `Amount`/`Owner`/`Zone` above. Not part
        /// of StateHash — events are excluded from the hash entirely (spec §3.7,
        /// see this struct's own doc comment).
        public byte PlayerIndex;
        /// Stage 2 Task 28: the event's SECOND participant, for the kinds whose
        /// primary `EntityId` is already spent on the victim. Two kinds write
        /// it: ProjectileHit and, since Stage 2 Task 44a, ProjectileHitPlayer —
        /// in both it carries the ROUND's own id while EntityId keeps the
        /// victim's (a mob id, or a player slot). Every other projectile ending
        /// (ProjectileBlocked/ProjectileExpired) already puts the round in
        /// EntityId, so only the two victim-bearing branches needed a second slot.
        ///
        /// 0 MEANS "NONE", and that is safe rather than merely conventional:
        /// SimulationWorld's `_nextEntityId` counter starts at 1 and only grows
        /// (see VisibilityIds' own doc), so no real entity can ever be 0.
        ///
        /// WHY IT EXISTS AT ALL: spec §3.8's `ProjectileEndedNet (id, contact
        /// point, end kind)` and table Р28's "to everyone who received THAT
        /// round's spawn" are both keyed on the round's id, and without this
        /// field a round that ended on a mob could never close the
        /// per-connection spawn subscription it opened. Same "unused for every
        /// other kind" contract as `Amount`/`Owner`/`Zone` above — and MobDied
        /// is deliberately part of "every other kind" (SimulationWorld.DamageMob
        /// has no projectile in scope; the assembler joins death to round
        /// through the tick's event buffer instead, task-28-brief §2.4 item 4).
        /// Not part of StateHash — events are excluded from the hash entirely.
        public int SecondaryEntityId;
    }
}
