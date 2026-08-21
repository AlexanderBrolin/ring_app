using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Visibility
{
    /// Which observers a SimEvent reaches (spec §3.7, Р28). One channel per
    /// SimEventKind, fixed by ChannelFor below — see EventRelevance's own doc
    /// for what each value means and, critically, what `None` does NOT mean.
    public enum DeliveryChannel : byte { None = 0, Owner = 1, Visible = 2, Audible = 3, All = 4 }

    /// Stage 2 Task 21 (spec §3.7, Р28): server-side event DELIVERY rules — for
    /// a given SimEvent and observer, is it delivered at all, and with what
    /// (possibly coarsened) position. Deliberately a PURE function of its
    /// explicit arguments: unlike VisibilitySystem.Compute, this seam never
    /// re-derives visibility from `w` and `cfg` itself — the caller's
    /// `observerSet` is trusted as the single source of truth for "is the
    /// subject visible" (task-21-brief.md rule 1 / Р132: `observerSet.Contains
    /// (id)`, TOGETHER with linger — an entity merely lingering after a recent
    /// LoS break counts as visible here exactly as it does everywhere else
    /// VisibilitySet is read, never re-gated on `LingerOf(id) == 0`).
    ///
    /// `observerSet` is also the seam that carries the "which tick's set"
    /// contract (carryover-t21.md #1, carryover-t28.md §8б) — which is
    /// PER-KIND, not one rule for the whole channel; see ShouldDeliver's own
    /// doc for the table and for why a single blanket rule is wrong in one
    /// direction or the other. This method itself does not care which tick's
    /// set it is handed (it only ever calls `Contains`/`LingerOf`); the CALLER
    /// (Task 28's SnapshotAssembler) owns the choice, and getting it wrong
    /// fails silently — every event of that kind simply stops reaching anyone.
    public static class EventRelevance
    {
        /// Fixed per-kind routing table (spec §3.7, Р28). Every `SimEventKind`
        /// is explicitly listed below; an unhandled value throws rather than
        /// falling through a `default` case, so a future kind added to the
        /// enum without a matching entry here fails LOUDLY the first time
        /// anything calls this (ChannelFor_HandlesEveryKind pins exactly
        /// that) instead of silently defaulting to some channel that happens
        /// to compile — "Урок 86: contract by assertion, not prose".
        public static DeliveryChannel ChannelFor(SimEventKind kind)
        {
            switch (kind)
            {
                case SimEventKind.StaminaDenied:
                // Stage 3 Т29 (spec §3.6, Р281): a collected cell is the
                // COLLECTOR's own business. The other two see the cell
                // disappear from the Pickups block either way; telling them
                // WHO took it, and therefore that somebody was standing on
                // that spot this tick, is exactly the information CR 4 keeps
                // off the wire. The Owner channel addresses its recipient by
                // `ev.PlayerIndex`, which is why PickupSystem.Collect's emit
                // is obliged to set it.
                case SimEventKind.PickupTaken:
                    return DeliveryChannel.Owner;

                case SimEventKind.PlayerDashed:
                case SimEventKind.PlayerSlideStarted:
                case SimEventKind.DashRicocheted:
                    return DeliveryChannel.Audible;

                case SimEventKind.MobSpawned:
                case SimEventKind.MobDied:
                case SimEventKind.PlayerDamaged:
                case SimEventKind.PlayerDied:
                // Stage 3 Т23: an extraction is something the other two SEE
                // happen — the same channel a death rides, for the same reason
                // (it is an event about a body at a place, not an announcement
                // about the raid). What the raid announces to everyone —
                // portals closing, the Director falling — is above.
                case SimEventKind.PlayerExtracted:
                    return DeliveryChannel.Visible;

                case SimEventKind.WaveStarted:
                case SimEventKind.WaveCleared:
                // Stage 3 Т21 (spec §3.4/§3.5, Р299): both of the raid's own
                // turning points reach everyone, and neither carries a
                // position — the first would otherwise leak the location of
                // whichever collector walked into the core, the second the
                // spot the corpse everyone is about to fight over lies on.
                // Their WIRE catalog (SnapshotEventKind, priority, payload
                // size) WAS Т29's and now exists — until that task both kinds
                // were emitted into a catalog with no entry for them and
                // SnapshotAssembler dropped them silently, which is precisely
                // what the routing living here from the moment the kinds
                // exist could not prevent: this switch throws on a kind it
                // does not know and ChannelFor_HandlesEveryKind walks the
                // whole enumeration, but neither of them watches the wire.
                case SimEventKind.DirectorActivated:
                case SimEventKind.DirectorDied:
                    return DeliveryChannel.All;

                // Projectile relevance needs the round's own trajectory
                // (which observers it flew near), not anything this per-kind
                // table can express — that lives in Task 28's
                // SnapshotAssembler. `None` here means "decided elsewhere",
                // never "nobody" — see ShouldDeliver's own guard below.
                case SimEventKind.ProjectileFired:
                case SimEventKind.ProjectileHit:
                case SimEventKind.ProjectileBlocked:
                case SimEventKind.ProjectileExpired:
                // Stage 2 Task 44a: a round ending ON A PLAYER is a round
                // ending, and belongs to this group for the same reason as
                // ProjectileHit next to it — who sees it is a question about
                // the ROUND's trajectory (who received its spawn), not about
                // the victim's visibility, so the per-kind table above cannot
                // express it and must not pretend to.
                case SimEventKind.ProjectileHitPlayer:
                // Stage 3 Т29 (R-236). AN EMPTIED BOX IS DELIVERED BY
                // VISIBILITY — and this table still cannot say so, for the
                // same structural reason the projectile kinds above are here.
                // Since Т26 there are THREE visibility sets, one per class
                // (Р268 п.2), and `ShouldDeliver` is handed exactly ONE: the
                // MOBS set, which is also where players ride on the signed
                // trick. A container's id lives in the CONTAINERS set, which
                // this seam never sees, so answering `Visible` here would send
                // `VisibleSubjectId` looking for a container id among mobs and
                // silently deliver to whoever happened to share the number.
                // `None` is this file's own word for "decided elsewhere, by
                // Task 28's SnapshotAssembler" — and that is exactly where the
                // decision now lives, against `c.ContainersCurrent`.
                case SimEventKind.ContainerEmptied:
                    return DeliveryChannel.None;

                default:
                    throw new System.ArgumentException(
                        $"EventRelevance.ChannelFor: unhandled SimEventKind {kind} — every kind must be " +
                        "explicitly routed to a delivery channel (spec §3.7, Р28); a silently-returned " +
                        "default would let a future kind fall through unnoticed.", nameof(kind));
            }
        }

        /// The Visible-channel subject's identity in VisibilitySet's own id
        /// space (Р20): a mob's REAL id for MobSpawned/MobDied (Р81 routes
        /// these two by the MOB's OWN visibility — MobDied's
        /// ATTACKER-convention PlayerIndex is deliberately never consulted
        /// here, see EventRelevance's own doc and
        /// SimEvent.PlayerIndex's ATTACKER paragraph), or the VICTIM's
        /// synthetic player id for PlayerDamaged/PlayerDied (SimEvent.PlayerIndex's
        /// VICTIM convention). Never called for any other kind — ShouldDeliver's
        /// own switch only reaches this from the Visible-channel branch.
        static int VisibleSubjectId(in SimEvent ev)
        {
            switch (ev.Kind)
            {
                case SimEventKind.MobSpawned:
                case SimEventKind.MobDied:
                    return ev.EntityId;

                case SimEventKind.PlayerDamaged:
                case SimEventKind.PlayerDied:
                // Stage 3 Т29: a collector walking out is the same shape of
                // subject as a collector dying — the VICTIM convention of
                // SimEvent.PlayerIndex, resolved through the signed trick
                // into the mobs set players share (Р268 п.2). The kind has
                // routed to Visible since Т23, but nothing ever reached this
                // method with it: the assembler had no wire entry for it, so
                // the event was dropped before delivery was ever asked about.
                // Т29 gave it that entry, and this line is what keeps the
                // first frame it rides in from throwing inside a server tick.
                case SimEventKind.PlayerExtracted:
                    return VisibilityIds.ForPlayer(ev.PlayerIndex);

                default:
                    throw new System.ArgumentException(
                        $"EventRelevance.VisibleSubjectId: {ev.Kind} has no Visible-channel subject.", nameof(ev));
            }
        }

        /// Decides whether `ev` reaches a connection, and — when it does — the
        /// position to deliver it with (spec §3.7, Р28).
        ///
        /// TWO INDICES, TWO ROLES (Task 42b, carryover-t28.md §5). `identityIndex`
        /// is WHO this connection is — it feeds the `Owner` channel and the
        /// own-death carve-out, both privacy/identity questions that must not
        /// move just because the connection is spectating someone else.
        /// `viewpointIndex` is WHERE it looks from — it feeds the Audible
        /// channel's hearing-distance check (the one place inside this method
        /// that reads a live position rather than trusting `observerSet`). The
        /// two agree while a connection watches its own body and diverge only
        /// under spectating (Р70/Р88, Stage 2 Task 42a). `observerSet` itself
        /// is untouched by this split — it is the caller's responsibility to
        /// have computed it from `viewpointIndex` already (`VisibilitySystem.
        /// Compute`), positional semantics living entirely in the set.
        ///
        /// `observerSet` must be the set from the tick in which the event's
        /// SUBJECT actually exists, and which tick that is depends ON THE KIND
        /// (Р140, carryover-t28.md §8б):
        ///   * MobDied — the PREVIOUS tick's set. SimulationWorld swap-removes
        ///     the corpse's slot in the SAME tick it dies (SimulationWorld.cs,
        ///     `_mobs[index] = _mobs[--_mobCount]`) and Compute only ever
        ///     visits live mobs, so a freshly recomputed CURRENT-tick set can
        ///     never hold the corpse's id
        ///     (MobDied_DeliveredViaPreviousTickSet_NotCurrentTick).
        ///   * MobSpawned, and every other kind — the CURRENT tick's set. A
        ///     mob that spawned THIS tick did not exist in the previous one,
        ///     so the previous tick's set refuses it just as categorically
        ///     (MobSpawned_RequiresCurrentTickSet).
        /// A single blanket rule is therefore wrong in one direction or the
        /// other — "always previous" silently drops every MobSpawned, "always
        /// current" silently drops every MobDied — which is why the choice is
        /// spelled out here and pinned by those two symmetric tests instead of
        /// being left to the caller's instinct. This method never inspects a
        /// tick number itself: it only calls Contains/LingerOf on whatever set
        /// it is handed. `deliveredPos`
        /// is only meaningful when this returns true; it is `default` (zero)
        /// on a `false` return, on every one of the three channels that can
        /// refuse (Owner/Visible/Audible — All never refuses). A caller must
        /// check `ChannelFor(ev.Kind)` for `None` BEFORE calling this method:
        /// a None-channel kind makes this throw rather than silently
        /// deciding anything (see the DeliveryChannel.None case below).
        public static bool ShouldDeliver(in SimEvent ev, int identityIndex, int viewpointIndex,
            SimulationWorld w, VisibilitySet observerSet, in VisibilitySimConfig cfg,
            out float2 deliveredPos)
        {
            switch (ChannelFor(ev.Kind))
            {
                case DeliveryChannel.Owner:
                {
                    // Private feedback (spec Р28: rebroadcasting it would
                    // leak another player's Stamina economy) — exact
                    // position, gated purely on identity, never on
                    // visibility. Task 21 fix-round 1 (I-1): `deliveredPos`
                    // must still honor the `false`-return-means-`default`
                    // contract documented above — assigning `ev.Pos`
                    // unconditionally here (as the pre-fix-round code did)
                    // would hand a caller that trusts the `out` value
                    // without checking the `bool` (a plausible per-connection
                    // assembler pattern, Task 28) another player's EXACT
                    // position on every refusal, which is precisely the
                    // private feedback this channel exists to keep private.
                    bool isOwner = identityIndex == ev.PlayerIndex;
                    deliveredPos = isOwner ? ev.Pos : default;
                    return isOwner;
                }

                case DeliveryChannel.All:
                    // Spec Р28 requires this channel to carry NO position at
                    // all — today's WaveStarted/WaveCleared position comes from
                    // whichever player happens to be nearest the arena center
                    // (WaveSystem.Update) and must never reach the wire, or
                    // every observer would learn that player's location for
                    // free every wave.
                    deliveredPos = float2.zero;
                    return true;

                case DeliveryChannel.Visible:
                {
                    // Own death is delivered to its own owner unconditionally
                    // (spec Р28: a player's OWN PlayerDied always reaches
                    // them, whatever the visibility gate says) — a
                    // player killed from behind a wall by an attacker they
                    // never saw still needs their own death screen. Scoped to
                    // PlayerDied alone: PlayerDamaged/MobSpawned/MobDied all
                    // go through the plain visibility gate below with no
                    // owner carve-out.
                    if (ev.Kind == SimEventKind.PlayerDied && identityIndex == ev.PlayerIndex)
                    {
                        deliveredPos = ev.Pos;
                        return true;
                    }
                    if (observerSet.Contains(VisibleSubjectId(in ev)))
                    {
                        deliveredPos = ev.Pos;
                        return true;
                    }
                    deliveredPos = default;
                    return false;
                }

                case DeliveryChannel.Audible:
                {
                    // The ACTOR is always the subject for this channel's three
                    // kinds (SimEvent.PlayerIndex's ACTOR convention) — even
                    // for DashRicocheted, whose Pos is the wall CONTACT point,
                    // not the actor's own position (SimEvents.cs's own doc):
                    // the visibility check below is about the actor's body,
                    // the position that follows is whatever `ev.Pos` actually
                    // carries for this kind.
                    int actorId = VisibilityIds.ForPlayer(ev.PlayerIndex);
                    if (observerSet.Contains(actorId))
                    {
                        // Visible (Contains alone — Р132, see this class's own
                        // doc): exact position, same replicated-state
                        // reasoning that already covers a merely-lingering
                        // entity.
                        deliveredPos = ev.Pos;
                        return true;
                    }
                    // Not visible: falls back to hearing, over the VIEWPOINT's
                    // own position (Task 42b) — the SAME position
                    // VisibilitySystem.Compute itself reads to build
                    // `observerSet` (a plain PlayerAt — no Alive gate, so a
                    // viewpoint that names a DEAD player resolves exactly the
                    // same way, spec rule Р70). Two cases actually reach that,
                    // and spectating a live teammate is neither of them: a
                    // dead player still looking out of its own body (the
                    // default, where the two indices agree), and a spectated
                    // target that dies while being watched — nothing returns
                    // the watcher to its own body automatically, by design. Before Task 42b
                    // this read `identityIndex` instead, which agreed with
                    // `viewpointIndex` for every caller that existed at the
                    // time (spectating did not yet split them) and silently
                    // stopped agreeing the moment it could — see this class's
                    // own two-indices paragraph above.
                    float2 observerPos = w.PlayerAt(viewpointIndex).Pos;
                    if (VisibilitySystem.IsAudible(observerPos, ev.Pos, in cfg))
                    {
                        deliveredPos = VisibilitySystem.QuantizeAudiblePos(ev.Pos, in cfg);
                        return true;
                    }
                    deliveredPos = default;
                    return false;
                }

                case DeliveryChannel.None:
                    // TWO DIFFERENT REASONS LAND HERE, and neither is
                    // "nobody" (gate Ф6, own finding): a projectile kind needs
                    // the round's trajectory this seam does not have, and
                    // ContainerEmptied needs the CONTAINERS visibility set
                    // this seam is never handed (R-236). Both are decided by
                    // Stage 2 Task 28's SnapshotAssembler. A silent `false`
                    // here would be indistinguishable from a correct "nobody
                    // nearby" answer and would make a future caller that
                    // reaches this by oversight quietly drop every such event
                    // instead of failing its own tests.
                    throw new System.ArgumentException(
                        $"EventRelevance.ShouldDeliver: {ev.Kind} delivery is decided elsewhere " +
                        "(the SnapshotAssembler — by projectile trajectory relevance for the " +
                        "projectile kinds, against the containers visibility set for " +
                        "ContainerEmptied) — this seam must not be called for a None-channel kind.",
                        nameof(ev));

                default:
                    throw new System.ArgumentException(
                        $"EventRelevance.ShouldDeliver: unhandled DeliveryChannel for {ev.Kind}.", nameof(ev));
            }
        }
    }
}
