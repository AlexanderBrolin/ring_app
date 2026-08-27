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
        /// resolves the ground contact at the round's CENTER height, i.e. when
        /// the sphere's underside touches the floor at `projectileRadius`, not
        /// at zero. A shot aimed at the floor therefore stops one radius' worth
        /// of the descent early — and since that is a share of the drop, not a
        /// fixed distance, the miss on the ground grows with range: at the
        /// shipped balance (muzzle 1.0 m, radius 0.08 m) the round covers 0.92
        /// of the line, which is 0.8 m short at 10 m and 1.6 m short at 20 m.
        ///
        /// Straight line, no gravity: `ProjectileState.VelZ` is set once at
        /// spawn (`SimulationWorld.SpawnProjectile`), so height falls at a
        /// constant rate against horizontal travel and the share of the line is
        /// a ratio of two heights — the DISTANCE downrange never enters it,
        /// which is why this method does not take one.
        ///
        /// THAT PREMISE HOLDS ONLY UNTIL THE FIRST CONTACT, and it is written
        /// narrowly on purpose (app-88jb Т19, spec §3.4). Until this task the
        /// sentence above ended "and no system changes it", which was true of
        /// the whole flight; a round that ricochets off static geometry has its
        /// VelZ damped by `WeaponSimConfig.RicochetRetention` at that moment,
        /// so the constant-rate descent is a fact about the LEG the round is
        /// on, not about the flight. Nothing here breaks, because every caller
        /// of this method asks about the FIRST leg — the picture wants the end
        /// of the shot as it leaves the muzzle, before any geometry has been
        /// met — but the closed form stops being a description of the whole
        /// flight, and a reader who took the old wording literally would build
        /// a tracer that disagrees with the server on the second leg.
        ///
        /// THREE ANSWERS, AND EACH ONE MATCHES A GATE THE SIMULATION ITSELF
        /// HAS:
        ///  - a level or climbing shot returns 1. `ProjectileSystem` gathers a
        ///    floor candidate only while `VelZ` is strictly negative, so such a
        ///    round has no ground contact to report at all;
        ///  - a muzzle EXACTLY at the contact height returns 0, and only that
        ///    exact case: `tFloor` there is `-0.0f`, which passes the gate's own
        ///    `>= 0f`, so the round genuinely is retired on the tick it was
        ///    fired;
        ///  - a muzzle STRICTLY BELOW the contact height returns 1, which reads
        ///    backwards until the gate is read with it (Т45c fix-round 1, G-2).
        ///    The numerator `Radius - Height` is then positive while `VelZ` is
        ///    negative, so `tFloor` is negative on every tick, the [0,1] gate
        ///    rejects the floor candidate every time, and the round is never
        ///    taken by the ground at all — it flies PAST the aimed point.
        ///    Returning 0 here (which this method did until that round) claimed
        ///    the exact opposite: the round snapped onto the shooter's own feet
        ///    while the real one kept going. Not reachable at the shipped
        ///    balance and reachable by DATA ALONE — `WeaponConfig.
        ///    ProjectileRadius` is declared over [0.01, 2] against a 0.45 m
        ///    sliding muzzle, so one balance pass on the slider gets there with
        ///    no code change. `TrajectoryTests.RadiusAboveTheMuzzle_TheFloor
        ///    NeverTakesTheRound` measures it off a real round rather than
        ///    arguing it;
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
            if (toContact < 0f) return 1f;

            // `toContact == 0f` needs no branch of its own: the ratio is 0 there,
            // which is the "retired at the muzzle" answer the gate's `-0.0f`
            // actually produces.
            return math.min(toContact / drop, 1f);
        }
    }
}
