using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Visibility
{
    /// Server-side visibility filter core (spec §3.5, Р18-Р21): per-observer
    /// set of entities currently visible or still lingering after a recent
    /// LoS/range break. Pure function of world state + config — no `new`
    /// anywhere below, so a Compute() call allocates nothing beyond what its
    /// two VisibilitySet arguments already own (Task 19 report).
    public static class VisibilitySystem
    {
        public static void Compute(SimulationWorld w, int observerIndex,
            in VisibilitySimConfig cfg, VisibilitySet previous, VisibilitySet result)
        {
            result.Clear();
            // Hoisted out of the per-entity loop below (same discipline as
            // MobAiSystem.Update's own `ArenaSimConfig arena = w.Config.Arena;`)
            // so a per-tick Compute() call copies these two structs out of
            // SimConfig exactly once, not once per player/mob visited.
            ArenaSimConfig arena = w.Config.Arena;
            float heroRadius = w.Config.Hero.Radius;
            float2 observerPos = w.PlayerAt(observerIndex).Pos;

            for (int i = 0; i < w.PlayerCount; i++)
            {
                int id = VisibilityIds.ForPlayer(i);
                if (i == observerIndex)
                {
                    // Own player is always visible to self (spec §3.5) — no
                    // distance/LoS gate at all, and no linger to fade from
                    // either (there is no "loss" state for one's own body).
                    result.Add(id, 0);
                    continue;
                }
                Evaluate(observerPos, w.PlayerAt(i).Pos, heroRadius, id, in cfg, in arena, previous, result);
            }

            MobState[] mobs = w.Mobs;
            int mobCount = w.MobCount;
            for (int i = 0; i < mobCount; i++)
            {
                MobState m = mobs[i];
                float radius = w.MobConfigFor(m.Type).Radius;
                // Keyed by m.Id, NEVER by the loop index i (Р20): _mobs uses
                // swap-remove, so a slot's occupant this tick may be a
                // completely different entity from the one `previous` last
                // saw at this same index — VisibilitySet's own doc, and the
                // reason SwapRemove_DoesNotTransferState pins exactly this line.
                Evaluate(observerPos, m.Pos, radius, m.Id, in cfg, in arena, previous, result);
            }
        }

        /// One entity's visibility/linger transition for this tick (spec
        /// §3.5, Р18-Р20). `id` is the entity's OWN identity in the
        /// synthetic/real id space VisibilitySet keys on — never a slot index.
        static void Evaluate(float2 observerPos, float2 targetPos, float targetRadius, int id,
            in VisibilitySimConfig cfg, in ArenaSimConfig arena, VisibilitySet previous, VisibilitySet result)
        {
            // Exit hysteresis (Р18): an entity already tracked (visible or
            // still lingering) last tick gets a WIDER radius this tick, so it
            // does not flicker in and out right at the plain SightRadius
            // boundary. The LoS gate below is never relaxed by this — only
            // the distance budget is.
            bool wasTracked = previous.Contains(id);
            float radius = wasTracked ? cfg.SightRadius + cfg.ExitHysteresis : cfg.SightRadius;
            float dist = math.distance(observerPos, targetPos);

            // Conservative LoS (Р18, Task 13's Р64 clamp): padding the ray by
            // the TARGET's own radius (negative — shrinks what counts as
            // blocking) means a target is visible as soon as any part of its
            // body clears an obstacle's edge, not only once its exact centre
            // does. HasLineOfFire clamps this per-obstacle/per-wall
            // internally (Targeting.cs) — no second clamp belongs here.
            bool visibleNow = dist <= radius &&
                Targeting.HasLineOfFire(observerPos, targetPos, -targetRadius, in arena);

            if (visibleNow)
            {
                result.Add(id, 0);
                return;
            }

            if (!wasTracked) return; // never seen and still not seen — nothing to linger from

            // Linger (Р19): decrements from LingerTicks down to zero across
            // consecutive invisible ticks; the tick that would decrement past
            // zero drops the entity instead of re-adding it at 0 (0 is
            // reserved for "visible now", never for "just expired").
            int prevLinger = previous.LingerOf(id);
            int remaining = prevLinger == 0 ? cfg.LingerTicks : prevLinger - 1;
            if (remaining > 0) result.Add(id, remaining);
        }
    }
}
