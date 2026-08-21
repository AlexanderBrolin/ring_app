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
        ///
        /// The decay itself is written through `ref w.Pickups[i]`, the SAME
        /// in-place idiom ProjectileSystem.Update uses on its own TTL
        /// (`ref ProjectileState proj = ref w.Projectiles[i]`) — Ф1 fix-round,
        /// review B-I-5. It used to copy the struct out, edit the copy and
        /// write it back through a `*ForTest` seam: two extra copies per
        /// pickup per tick, and a battle-path call whose NAME said "test"
        /// (rule 4 — one policy, not two).
        static void AdvanceTtl(SimulationWorld w)
        {
            for (int i = w.PickupCount - 1; i >= 0; i--)
            {
                ref PickupState p = ref w.Pickups[i];
                // The arithmetic itself lives in TtlDecay.Step (errata E-6
                // C-I5), shared with ContainerStore.Update.
                // `zeroIsPermanent: false` is this system's half of that
                // home's one difference: 0 is a container's "never decays"
                // sentinel and never a pickup's — a pickup at 0 is over,
                // which PickupTests.
                // ZeroTtl_Expires_WhereAContainerWouldBePermanent pins.
                if (TtlDecay.Step(ref p.Ttl, zeroIsPermanent: false))
                {
                    // Spec §3.6: swap-remove WITHOUT an event — a pickup
                    // quietly aging out is not a VFX/SFX-relevant occurrence
                    // the way PickupTaken is. That kind now EXISTS (Т29 gave
                    // every Stage 3 event its enum entry, channel and emitter
                    // together) and is emitted by Collect below — the
                    // contrast this comment draws is with a real neighbor
                    // now, not with a promised one. Safe under the
                    // `ref` above for the same reason the back-to-front sweep
                    // is: the slot this overwrites is never read again, in
                    // this turn or any later one.
                    w.RemovePickupAt(i);
                }
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
                // order-independent — see SimulationWorld.AddAmmo's own doc),
                // so this is purely about matching spec §3.6's stated
                // "ascending slot" contract as literally as a swap-remove
                // sweep allows.
                int i = 0;
                while (i < w.PickupCount)
                {
                    PickupState pickup = w.Pickups[i];
                    if (math.distance(pickup.Pos, player.Pos) <= radius)
                    {
                        // Shared conversion/clamp seam (Global Constraints):
                        // SimulationWorld.AddAmmo is the ONE accounting for
                        // ammo, so auto-pickup calls into it rather than
                        // reimplementing the AmmoMax cap / emergency-cooldown
                        // clamp here.
                        w.AddAmmo(playerIndex, pickup.Amount * shotsPerCell);
                        // Ф1 fix-round (review C1 / B-I-1, owner decision
                        // R-24): the collector's own tally of what it picked
                        // up, in CELLS — `Amount` is denominated in cells at
                        // every producer (LootDrops.MobDeathCells and
                        // CorpseCells both return cells, SpawnPickup stores
                        // them unconverted), and MatchStats.CellsPicked's own
                        // doc says "cells picked up this match". Deliberately
                        // NOT the shots those cells bought (that is AmmoSpent's
                        // unit on the other side of the ledger, spec §3.10
                        // lists the two side by side) and NOT the number of
                        // piles walked over, which would undercount a stack.
                        // Credited AFTER the refill purely for reading order —
                        // the two touch different memory, neither reads the
                        // other — and it is credited whether or not the refill
                        // itself hit the AmmoMax ceiling: the cells were picked
                        // up either way.
                        w.StatsRef(playerIndex).CellsPicked += pickup.Amount;
                        // Stage 3 Т29 (spec §3.6/§3.12 Р281): the ADDRESSEE
                        // named at AdvanceTtl above has paid. Emitted HERE,
                        // where a cell is really collected — never where one
                        // ages out, which that method's own doc keeps silent
                        // on purpose.
                        //
                        // BEFORE THE REMOVAL, and that is not style: swap-
                        // remove moves the array's LAST slot into `i`, so
                        // after the call `pickup` names a different cell and
                        // the id in the event would belong to a bystander.
                        // (`pickup` is a COPY taken at the top of the turn,
                        // so reading it after the removal would be legal C#
                        // and wrong anyway — the local still holds the right
                        // values; what the order really protects is any
                        // future reader who reaches for `w.Pickups[i]`.)
                        //
                        // `playerIndex` IS LOAD-BEARING: this kind rides the
                        // Owner channel, which addresses its recipient by
                        // exactly this field (EventRelevance.ShouldDeliver) —
                        // an emit without it delivers to nobody at all.
                        w.Emit(SimEventKind.PickupTaken, pickup.Pos, pickup.Id, default, 0f,
                            playerIndex: (byte)playerIndex);
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
