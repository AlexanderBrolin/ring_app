using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Single home of the aimed shot's FLIGHT geometry as a whole-flight
    /// question, next to `Spread`'s single home of the cone (Stage 2 Task 45c,
    /// bd `app-bej`). `ProjectileSystem` answers the same geometry one tick at
    /// a time, because that is what an authoritative sweep needs; the picture
    /// needs the end of the flight before the flight happens, and re-deriving
    /// it in `Ring.Presentation` would put the round's descent in two places
    /// that drift apart at the first balance change.
    ///
    /// NOTHING IN THE SIMULATION CALLS THIS. `ProjectileSystem`'s own floor
    /// arithmetic is untouched by this class, down to the byte — the two are
    /// the per-tick and the closed form of one relation, and only the second
    /// is new. That is what keeps the replay hash where it was: no line the
    /// simulation executes changed. `TrajectoryTests.MatchesTheSimulationsOwn
    /// FloorCut` is what keeps the two forms honest with each other, by firing
    /// a real round and measuring where it ended rather than by re-checking
    /// the algebra.
    public static class Trajectory
    {
        /// How far along the muzzle→aim line a round fired at `aimHeight` from
        /// `muzzleHeight` gets before the ground takes it: 1 when it reaches
        /// the aimed point, less when the ground cuts it short. The caller
        /// interpolates its own two endpoints by the answer.
        ///
        /// WHY THE AIMED POINT IS NOT THE ANSWER, which is the whole defect
        /// (`app-bej`, owner smoke test #1 — "при ПКМ попадание в пол
        /// происходит не по мушке, а ближе к игроку"). `ProjectileSystem`
        /// resolves the ground contact at the round's CENTRE height, i.e. when
        /// the sphere's underside touches the floor at `projectileRadius`, not
        /// at zero. A shot aimed at the floor therefore stops one radius' worth
        /// of the descent early — and since that is a share of the drop, not a
        /// fixed distance, the miss on the ground grows with range: at the
        /// shipped balance (muzzle 1.0 m, radius 0.08 m) the round covers 0.92
        /// of the line, which is 0.8 m short at 10 m and 1.6 m short at 20 m.
        ///
        /// Straight line, no gravity: `ProjectileState.VelZ` is set once at
        /// spawn (`SimulationWorld.SpawnProjectile`) and no system changes it,
        /// so height falls at a constant rate against horizontal travel and the
        /// share of the line is a ratio of two heights — the DISTANCE downrange
        /// never enters it, which is why this method does not take one.
        ///
        /// THREE ANSWERS, AND EACH ONE MATCHES A GATE THE SIMULATION ITSELF
        /// HAS:
        ///  - a level or climbing shot returns 1. `ProjectileSystem` gathers a
        ///    floor candidate only while `VelZ` is strictly negative, so such a
        ///    round has no ground contact to report at all;
        ///  - a muzzle already at or under the contact height returns 0: there
        ///    is no line left to cover, and the round is retired where it was
        ///    fired. Unreachable at the shipped balance (both the standing and
        ///    the sliding muzzle sit well above the radius) and answered
        ///    explicitly anyway, because the alternative is a ratio that walks
        ///    backwards past the shooter;
        ///  - an aimed point at or above the contact height returns 1 — the
        ///    round passes through it before the ground is in question. This is
        ///    the ordinary case for a shot at a mob, which is why aiming at a
        ///    body moves nothing on screen.
        public static float FloorCutFraction(float muzzleHeight, float aimHeight,
            float projectileRadius)
        {
            float drop = muzzleHeight - aimHeight;
            if (drop <= 0f) return 1f;

            float toContact = muzzleHeight - projectileRadius;
            if (toContact <= 0f) return 0f;

            return math.min(toContact / drop, 1f);
        }
    }
}
