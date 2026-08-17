using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Loot
{
    /// Stage 3 Task 3 (spec §3.6): TTL decay and automatic pickup collection
    /// — the LAST system SimulationWorld.TickAll calls today (owner decision
    /// R-2; see that call site's own comment for the canonical Ф1 tail this
    /// system will eventually sit inside, once LootOps/ExtractionSystem/
    /// MatchFlowSystem exist).
    public static class PickupSystem
    {
        /// Advances every live pickup by one tick: TTL first (spec §3.6 draws
        /// no distinction for a pickup that expires and could also have been
        /// collected on the very same tick — TTL wins, it is simply gone
        /// before collection ever looks at it), then automatic collection for
        /// every live, un-extracted player.
        public static void Update(SimulationWorld w)
        {
            AdvanceTtl(w);
            Collect(w);
        }

        /// Iterates back-to-front so RemovePickupAt's swap-remove never skips
        /// or re-visits a slot within this same pass — same idiom
        /// ProjectileSystem.Update uses for the identical reason (its own doc
        /// comment). TTL expiry order has no observable effect on the result
        /// (every expired slot is removed unconditionally, none of them
        /// interact with each other), so back-to-front is simply the
        /// established, already-proven-safe shape for a capped array this
        /// codebase swap-removes from mid-sweep.
        static void AdvanceTtl(SimulationWorld w)
        {
            for (int i = w.PickupCount - 1; i >= 0; i--)
            {
                PickupState p = w.Pickups[i];
                p.Ttl -= SimulationWorld.TickDt;
                if (p.Ttl <= 0f)
                {
                    // Spec §3.6: swap-remove WITHOUT an event — a pickup
                    // quietly aging out is not a VFX/SFX-relevant occurrence
                    // the way PickupTaken (a later task) is.
                    w.RemovePickupAt(i);
                    continue;
                }
                w.SetPickupForTest(i, in p);
            }
        }

        /// Every live, un-extracted player collects every pickup within
        /// Hero.PickupRadius of its own Pos (spec §3.6), in ascending player
        /// index — so a pickup contested by two players always goes to the
        /// lower index (Р259) — then, within one player's own turn, ascending
        /// pickup slot. `!player.Alive || player.Extracted` (Р259, finding
        /// D-16) reads the state as PickupSystem itself finds it: this is the
        /// LAST step of TickAll, after this tick's own combat/movement have
        /// already settled, so a player who died or extracted THIS SAME tick
        /// already reads that way here — there is no separate "this tick"
        /// flag to consult.
        static void Collect(SimulationWorld w)
        {
            // Read once, same idiom ProjectileSystem.Update uses (`SimConfig
            // config = w.Config;` at its own top) — Config's own getter
            // returns SimConfig BY VALUE, so reading it inside either loop
            // below would re-copy the whole struct once per player instead
            // of once per tick.
            SimConfig cfg = w.Config;
            float radius = cfg.Hero.PickupRadius;
            int shotsPerCell = cfg.Weapon.ShotsPerCell;

            for (int playerIndex = 0; playerIndex < w.PlayerCount; playerIndex++)
            {
                PlayerState player = w.PlayerAt(playerIndex);
                if (!player.Alive || player.Extracted) continue;

                // Forward sweep with a conditional advance (not
                // AdvanceTtl's back-to-front): a removal swaps the array's
                // LAST slot into `i`, so `i` is deliberately NOT advanced —
                // the next loop turn re-examines whatever just landed there
                // instead of skipping it. Order has no effect on which
                // pickups THIS player ends up collecting (every pickup
                // within radius is collected regardless of visit order,
                // and WeaponSystem.AddAmmo's clamped addition is
                // order-independent — see AddAmmoForTest's own doc), so this
                // is purely about matching spec §3.6's stated "ascending
                // slot" contract as literally as a swap-remove sweep allows.
                int i = 0;
                while (i < w.PickupCount)
                {
                    PickupState pickup = w.Pickups[i];
                    if (math.distance(pickup.Pos, player.Pos) <= radius)
                    {
                        // Shared conversion/clamp seam (Global Constraints):
                        // AddAmmoForTest is the ONE accounting for ammo, so
                        // auto-pickup calls into it rather than reimplementing
                        // the AmmoMax cap / emergency-cooldown clamp here.
                        w.AddAmmoForTest(playerIndex, pickup.Amount * shotsPerCell);
                        w.RemovePickupAt(i);
                    }
                    else
                    {
                        i++;
                    }
                }
            }
        }
    }
}
