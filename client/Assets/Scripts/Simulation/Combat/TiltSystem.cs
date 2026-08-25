using Ring.Simulation.Core;

namespace Ring.Simulation.Combat
{
    /// The tick-side half of body tilt (app-88jb Т5, spec §3.2, owner
    /// correction Н10): every live body walks its tilt spring one step per
    /// tick. The impulse half lives where the blow is resolved -- DamageMob
    /// adds Impact.AngularImpulse into MobState.TiltVel -- and this system
    /// only ever integrates what that left behind.
    ///
    /// THE ARITHMETIC OF THE STEP IS NOT HERE, DELIBERATELY. It is the public
    /// Impact.SpringStep (Т1), which owns the spring, the integration and the
    /// rest snap as one thing. THREE callers need exactly that step and one of
    /// them is outside this assembly: the mob pass below, the collector pass
    /// Т7 adds beside it, and Presentation's MobVisual (Т31), which rebuilds a
    /// mob's tilt from the hit event because tilt never rides the wire (Р383)
    /// -- and MobVisual cannot see an `internal`. One line per body here, not
    /// a re-typed formula.
    ///
    /// PLACEMENT IN TickAll: immediately after ProjectileSystem.Update and
    /// before WaveSystem.Update. By that point this tick's hits are resolved,
    /// so the tilt integrates from THIS tick's impulse rather than one tick
    /// later -- unlike the Vel shove, whose one-tick lag is inherited from
    /// MoveWithCollisions having already run (see SeparationSystem's own doc).
    internal static class TiltSystem
    {
        public static void Apply(SimulationWorld w)
        {
            MobState[] mobs = w.Mobs;
            int count = w.MobCount;
            float dt = SimulationWorld.TickDt;

            for (int i = 0; i < count; i++)
            {
                // BY REFERENCE INTO THE ARRAY SLOT, never through a copy: the
                // step mutates both fields in place, and a `var m = mobs[i]`
                // read would integrate a body that is then thrown away. This
                // is the same `ref MobState` shape MobAiSystem's own loop uses
                // (MobAiSystem.cs:37).
                ref MobState m = ref mobs[i];
                MobSimConfig cfg = w.MobConfigFor(m.Type);
                Impact.SpringStep(ref m.Tilt, ref m.TiltVel,
                    cfg.TiltDampingRatio, cfg.TiltSettleSeconds, dt);
            }
        }
    }
}
