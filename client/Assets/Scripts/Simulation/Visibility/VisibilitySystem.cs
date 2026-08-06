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
        /// Computes `observerIndex`'s per-tick visibility set (spec §3.5,
        /// Р18-Р21) into `result`, reading `previous` (last tick's own
        /// result) for hysteresis/linger continuity. `previous` and `result`
        /// MUST be two DISTINCT VisibilitySet instances — the real per-tick
        /// caller ping-pongs a pair of buffers, today's `result` becoming
        /// tomorrow's `previous` (see e.g. VisibilityTests' own ping-pong
        /// fixtures). `result.Clear()` below runs FIRST, before `previous`
        /// is ever read, so aliasing the two would silently turn every
        /// hysteresis/linger decision into "compare an already-emptied set
        /// to itself" — no exception, no crash, just a permanently-empty
        /// linger/hysteresis window (Fix-round 1, I-3) — which is exactly
        /// why this is a guard rather than a documented caller obligation
        /// alone.
        public static void Compute(SimulationWorld w, int observerIndex,
            in VisibilitySimConfig cfg, VisibilitySet previous, VisibilitySet result)
        {
            if (ReferenceEquals(previous, result))
            {
                throw new System.ArgumentException(
                    "VisibilitySystem.Compute: previous and result must be two DISTINCT " +
                    "VisibilitySet instances — result.Clear() runs before previous is ever " +
                    "read, so aliasing the two silently disables hysteresis/linger instead " +
                    "of throwing.", nameof(result));
            }
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

        /// Audibility gate (Stage 2 Task 20, spec §3.5, Р21): plain distance
        /// only — deliberately no LoS check at all. Sound is the diegetic
        /// "open short-range aether" of ADR-003 §8 and travels through walls;
        /// HearRadius is >= SightRadius by construction of the config (the
        /// cross-field validation itself lands in Task 22, not here), so this
        /// gate is always at least as permissive as Compute's own sight gate,
        /// never narrower.
        public static bool IsAudible(float2 observerPos, float2 sourcePos, in VisibilitySimConfig cfg)
        {
            return math.distance(observerPos, sourcePos) <= cfg.HearRadius;
        }

        /// Snaps the position of an event whose source is NOT visible onto a
        /// coarse grid (Р21): exact coordinates of every shot through walls
        /// are an ESP-grade leak. `grid <= 0` is a hard opt-out (identity) —
        /// TestConfigs and any hand-built config that never sets
        /// HearPositionGridMeters must not crash into a divide-by-zero
        /// instead. When enabled, `round(pos / grid) * grid` is a pure,
        /// deterministic function of `pos` and `grid` alone — no RNG, no
        /// world/tick dependency — so the same true position always produces
        /// the same coarse one, which is exactly what makes it idempotent
        /// (re-quantizing an already-quantized position is a no-op) and
        /// symmetric around the arena's centre (math.round forwards to
        /// System.Math.Round(double), whose documented default,
        /// MidpointRounding.ToEven, treats a midpoint the same distance
        /// either side of zero identically).
        public static float2 QuantizeAudiblePos(float2 pos, in VisibilitySimConfig cfg)
        {
            float grid = cfg.HearPositionGridMeters;
            if (grid <= 0f) return pos;
            return math.round(pos / grid) * grid;
        }
    }
}
