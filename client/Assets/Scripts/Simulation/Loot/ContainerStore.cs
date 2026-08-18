using Ring.Simulation.Core;

namespace Ring.Simulation.Loot
{
    /// Stage 3 Task 14 (spec §3.7, Р229): the container's own policy — how
    /// long a freshly spawned container lives before it's the one shared
    /// home of "how long does a container of THIS kind live" (mirrors
    /// LootDrops' role for pickup-amount policy) and the per-tick TTL-decay
    /// pass (mirrors PickupSystem.AdvanceTtl's shape) — the two live
    /// together in one file because Т14 is a single task standing up both,
    /// unlike pickups' own history where LootDrops (Т3) and PickupSystem
    /// (also Т3, but a separate concern from day one) were already two
    /// files. A future task is free to split this one the same way if it
    /// grows a third responsibility (rule 4 — split when outgrown, not
    /// pre-emptively).
    public static class ContainerStore
    {
        /// The Ttl a freshly spawned container of `kind` starts at (spec
        /// Interfaces: "0 = не истекает (ящик, тайник, труп сборщика)").
        /// Coordinator R-100: this is the ONLY place in the whole codebase
        /// that reads `Kind` to decide anything — SpawnContainer stores it
        /// as inert data, ContainerSlotAt/TryTakeFromContainer never look
        /// at it at all. Spec §3.7 is explicit that `Kind` distinguishes
        /// skin/spawn-table only ("три механизма вместо одного дали бы три
        /// state-машины и три набора гонок") — a second branch on `Kind`
        /// anywhere else is reopening that decision, not extending this
        /// one.
        public static float InitialTtlFor(ContainerKind kind, in LootSimConfig cfg)
        {
            switch (kind)
            {
                case ContainerKind.Crate:
                case ContainerKind.Cache:
                case ContainerKind.PlayerCorpse:
                    return 0f;
                default: // Ground, MobCorpse
                    return cfg.ContainerTtlSeconds;
            }
        }

        /// Advances every live container's Ttl by one tick and removes the
        /// ones that cross zero — same back-to-front, in-place `ref`
        /// idiom as PickupSystem.AdvanceTtl (Ф1 fix-round B-I-5: production
        /// TTL decay writes through the live array directly, never through
        /// a `*ForTest` seam). A container whose Ttl is already `<= 0`
        /// (Crate/Cache/PlayerCorpse, permanent per InitialTtlFor above) is
        /// skipped before any decrement — it must stay at exactly 0
        /// forever, not drift negative and get swept on the very next
        /// pass. Wired into SimulationWorld.TickAll BEFORE PickupSystem.Update
        /// (coordinator R-101 — see that call site's own doc).
        public static void Update(SimulationWorld w)
        {
            for (int i = w.ContainerCount - 1; i >= 0; i--)
            {
                ref ContainerState c = ref w.Containers[i];
                if (c.Ttl <= 0f) continue; // permanent kind — never decays
                c.Ttl -= SimulationWorld.TickDt;
                if (c.Ttl <= 0f) w.RemoveContainerAt(i);
            }
        }
    }
}
