using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.AI
{
    /// Pairwise push-apart between overlapping mobs (spec Task 20, Phase 6):
    /// keeps mobs from stacking on the same point when several of them converge
    /// on the player from the same side. Every overlapping pair contributes an
    /// equal-and-opposite impulse into a preallocated buffer first, and only
    /// once every pair has been scanned are those impulses applied to Vel — so
    /// the mob earliest in SimulationWorld.Mobs never gets a different outcome
    /// than one discovered later in the same tick's pair scan (a single
    /// resolve-as-you-go pass would let position updates from the first pairs
    /// bias the pairs scanned afterward; the double buffer removes that order
    /// dependency by construction).
    ///
    /// This never touches Pos as a second movement path — MoveWithCollisions
    /// (already run this tick by MobAiSystem) stays the only way a mob's
    /// position advances. The Vel this adds only shows up as motion on the
    /// FOLLOWING tick's MoveWithCollisions call, which is fine: separation is a
    /// continuous force, not an instant unstick, so a one-tick lag is
    /// deterministic and imperceptible over the many ticks it takes to push
    /// mobs apart. Geometry.Depenetrate afterwards is the same safety net
    /// MoveWithCollisions itself uses against starting overlaps — it only
    /// resolves obstacle/wall penetration, not mob-mob overlap, so it doesn't
    /// duplicate what this system already does for mobs.
    internal static class SeparationSystem
    {
        public static void Apply(SimulationWorld w)
        {
            MobState[] mobs = w.Mobs;
            int count = w.MobCount;
            float2[] forces = w.SepForces;
            ArenaSimConfig arena = w.Config.Arena;

            for (int i = 0; i < count; i++) forces[i] = float2.zero;

            for (int i = 0; i < count; i++)
            {
                MobSimConfig cfgI = w.MobConfigFor(mobs[i].Type);
                for (int j = i + 1; j < count; j++)
                {
                    MobSimConfig cfgJ = w.MobConfigFor(mobs[j].Type);
                    float threshold = cfgI.SeparationRadius + cfgJ.SeparationRadius;
                    if (threshold <= 0f) continue;

                    float2 d = mobs[i].Pos - mobs[j].Pos;
                    float dist = math.length(d);
                    if (dist >= threshold) continue;

                    float strength = (cfgI.SeparationStrength + cfgJ.SeparationStrength) * 0.5f;
                    float2 dir = math.normalizesafe(d, new float2(1f, 0f));
                    float2 force = dir * (1f - dist / threshold) * strength;

                    // Equal and opposite: the pair's midpoint never drifts from
                    // separation alone (spec Interfaces — "no first-in-list skew").
                    forces[i] += force;
                    forces[j] -= force;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (forces[i].x == 0f && forces[i].y == 0f) continue;
                mobs[i].Vel += forces[i];
                float radius = w.MobConfigFor(mobs[i].Type).Radius;
                Geometry.Depenetrate(ref mobs[i].Pos, ref mobs[i].Vel, radius, in arena, 1);
            }
        }
    }
}
