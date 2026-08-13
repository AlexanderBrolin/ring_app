using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Visibility
{
    /// Server-side visibility filter core (spec §3.5, Р18-Р21): per-observer
    /// set of entities currently visible or still lingering after a recent
    /// LoS/range break. Pure function of world state + config, with NOT ONE
    /// ALLOCATION ON THE SUCCESS PATH, so a Compute() call allocates nothing
    /// beyond what its two VisibilitySet arguments already own
    /// (VisibilitySystem_Compute_DoesNotAllocateGC pins that by measurement).
    /// Deliberately stated that way rather than as "no `new` anywhere below":
    /// the aliasing guard added in Task 19's fix-round does construct an
    /// ArgumentException — on the failure path that ends the call, where an
    /// allocation costs nothing anyone measures.
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
            // Hoisted out of the per-entity loops below (same discipline as
            // MobAiSystem.Update's own `ArenaSimConfig arena = w.Config.Arena;`)
            // so those loops read two locals instead of going back to
            // SimulationWorld.Config once per player/mob visited.
            //
            // What this is NOT (Ф5 phase review, I-7 — the earlier wording
            // claimed it): it is not "the config is copied exactly once".
            // SimulationWorld.Config is a BY-VALUE property, so each of the
            // two reads below copies the WHOLE SimConfig, and the mob loop's
            // own w.MobConfigFor copies a MobSimConfig per mob on top of that
            // (as does `MobState m = mobs[i]`). None of that allocates — every
            // one is a struct copy, which is why
            // VisibilitySystem_Compute_DoesNotAllocateGC is green for the
            // right reason — but the copies are real. A
            // ref-readonly accessor would remove them; it touches
            // SimulationWorld's public API and is therefore a Task 28
            // candidate (carryover-t28.md §8г), not this phase's work.
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
        /// "open short-range aether" of ADR-003 §8 and travels through walls.
        ///
        /// This is a SELF-CONTAINED gate: it assumes NOTHING about Compute's
        /// own sight gate and must not be read as "always at least as
        /// permissive as sight, never narrower" (Ф5 phase review, I-3 — the
        /// earlier wording claimed exactly that, on a guarantee quoted only
        /// half-way; Урок 88). Both halves of the old claim are false:
        /// (1) Task 22's cross-check requires only HearRadius >= SightRadius,
        ///     while Compute widens an ALREADY-TRACKED entity's own budget to
        ///     SightRadius + ExitHysteresis — so a config of Sight 45 /
        ///     ExitHysteresis 20 / Hear 45 passes validation and still leaves
        ///     hearing strictly NARROWER than sight for anything tracked;
        /// (2) "by construction of the config" does not hold at all for a
        ///     hand-built VisibilitySimConfig, which is what actually reaches
        ///     this method most of the time: SimConfigBuilder is the only
        ///     place that cross-check lives, and TestConfigs/fixtures/JSON
        ///     never pass through it (VisibilityTests really does call this
        ///     with HearRadius = 0.5 * SightRadius).
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
