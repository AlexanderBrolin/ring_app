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
                    return DeliveryChannel.Owner;

                case SimEventKind.PlayerDashed:
                case SimEventKind.PlayerSlideStarted:
                case SimEventKind.DashRicocheted:
                    return DeliveryChannel.Audible;

                case SimEventKind.MobSpawned:
                case SimEventKind.MobDied:
                case SimEventKind.PlayerDamaged:
                case SimEventKind.PlayerDied:
                    return DeliveryChannel.Visible;

                case SimEventKind.WaveStarted:
                case SimEventKind.WaveCleared:
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
                    return VisibilityIds.ForPlayer(ev.PlayerIndex);

                default:
                    throw new System.ArgumentException(
                        $"EventRelevance.VisibleSubjectId: {ev.Kind} has no Visible-channel subject.", nameof(ev));
            }
        }

        /// Decides whether `ev` reaches `observerIndex`, and — when it does —
        /// the position to deliver it with (spec §3.7, Р28).
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
        public static bool ShouldDeliver(in SimEvent ev, int observerIndex, SimulationWorld w,
            VisibilitySet observerSet, in VisibilitySimConfig cfg, out float2 deliveredPos)
        {
            switch (ChannelFor(ev.Kind))
            {
                case DeliveryChannel.Owner:
                {
                    // Private feedback (spec Р28: rebroadcasting it would
                    // leak another player's Stamina economy) — exact
                    // position, gated purely on identity, never on
                    // visibility. Task 21 fix-round 1 (I-1): `deliveredPos`
                    // must still honour the `false`-return-means-`default`
                    // contract documented above — assigning `ev.Pos`
                    // unconditionally here (as the pre-fix-round code did)
                    // would hand a caller that trusts the `out` value
                    // without checking the `bool` (a plausible per-connection
                    // assembler pattern, Task 28) another player's EXACT
                    // position on every refusal, which is precisely the
                    // private feedback this channel exists to keep private.
                    bool isOwner = observerIndex == ev.PlayerIndex;
                    deliveredPos = isOwner ? ev.Pos : default;
                    return isOwner;
                }

                case DeliveryChannel.All:
                    // Spec Р28 requires this channel to carry NO position at
                    // all — today's WaveStarted/WaveCleared position comes from
                    // whichever player happens to be nearest the arena centre
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
                    if (ev.Kind == SimEventKind.PlayerDied && observerIndex == ev.PlayerIndex)
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
                    // Not visible: falls back to hearing, over the SAME
                    // observer position VisibilitySystem.Compute itself reads
                    // (a plain PlayerAt — no Alive gate, so a dead observer,
                    // e.g. spectating, resolves exactly the same way, spec
                    // rule Р70).
                    float2 observerPos = w.PlayerAt(observerIndex).Pos;
                    if (VisibilitySystem.IsAudible(observerPos, ev.Pos, in cfg))
                    {
                        deliveredPos = VisibilitySystem.QuantizeAudiblePos(ev.Pos, in cfg);
                        return true;
                    }
                    deliveredPos = default;
                    return false;
                }

                case DeliveryChannel.None:
                    // Projectile relevance needs trajectory data this seam
                    // does not have (Task 28's SnapshotAssembler owns it) — a
                    // silent `false` here would be indistinguishable from a
                    // correct "nobody nearby" answer and would make a future
                    // caller that reaches this by oversight quietly drop every
                    // projectile event instead of failing its own tests.
                    throw new System.ArgumentException(
                        $"EventRelevance.ShouldDeliver: {ev.Kind} delivery is decided elsewhere " +
                        "(Task 28's SnapshotAssembler, by projectile trajectory relevance) — this seam " +
                        "must not be called for a None-channel kind.", nameof(ev));

                default:
                    throw new System.ArgumentException(
                        $"EventRelevance.ShouldDeliver: unhandled DeliveryChannel for {ev.Kind}.", nameof(ev));
            }
        }
    }
}
