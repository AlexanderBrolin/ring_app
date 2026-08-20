using Ring.Simulation.Core;

namespace Ring.Simulation.Loot
{
    /// The one home of the ground-entity TTL rule (plan errata E-6 C-I5):
    /// "spend one tick of this entity's life and say whether it just ran
    /// out". Pickups and containers both decay this way and used to write
    /// the arithmetic out twice — `PickupSystem.AdvanceTtl` and
    /// `ContainerStore.Update` — which is the duplication rule 2 rules out
    /// and which a third ground entity would have copied a third time.
    ///
    /// THE RULE IS SHARED, THE LOOP IS NOT, and that split is deliberate.
    /// Each caller keeps its own back-to-front sweep because each owns a
    /// different array and a different swap-remove (`RemovePickupAt` vs
    /// `RemoveContainerAt`), and a shared loop would have to reach both
    /// through an interface — boxing a state struct on a per-tick path, for
    /// nothing. What genuinely lived twice was the arithmetic below, and
    /// that is what moved here.
    ///
    /// `zeroIsPermanent` IS THE ONE PLACE THE TWO CALLERS DIFFER, so it is
    /// an argument rather than a policy this file picks. A container reads
    /// `Ttl <= 0` as "never decays" — Crate, Cache and PlayerCorpse are
    /// seeded to exactly 0 by `ContainerStore.InitialTtlFor`, and a
    /// decrement would drift them negative and sweep them on the very next
    /// pass. A pickup has no such reading: `SimulationWorld.SpawnPickup`
    /// seeds `Loot.PickupTtlSeconds` and a pickup that reaches 0 is simply
    /// over. Handing pickups the containers' policy would make them
    /// immortal — silently, since nothing else in the simulation would
    /// complain — which is why `PickupTests.
    /// ZeroTtl_Expires_WhereAContainerWouldBePermanent` pins the difference
    /// rather than leaving it to this doc.
    ///
    /// THE PROJECTILE'S OWN TTL IS DELIBERATELY NOT A CALLER (coordinator
    /// R-202, plan rule "a fix travels by the finding's consequences, not by
    /// its address"). `ProjectileSystem.Update` decrements at the top of the
    /// movement step and tests the result at the BOTTOM, only in the branch
    /// where the round hit nothing, and that branch emits
    /// `ProjectileExpired` — the whole collision resolution sits between the
    /// two halves. "Step and report the crossing" does not describe it, and
    /// folding it in would reorder a hot path for a shape it does not have.
    public static class TtlDecay
    {
        /// Spends one tick of `ttl` in place and returns true exactly when
        /// this tick is the one that ran it out — the caller then removes
        /// the entity. Returns false without touching `ttl` when the entity
        /// is permanent (see `zeroIsPermanent` in the type's doc).
        public static bool Step(ref float ttl, bool zeroIsPermanent)
        {
            if (zeroIsPermanent && ttl <= 0f) return false;
            ttl -= SimulationWorld.TickDt;
            return ttl <= 0f;
        }
    }
}
