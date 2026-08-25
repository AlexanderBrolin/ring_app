using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// The one home of impact arithmetic (app-88jb, spec §3.2). PUBLIC, and
    /// deliberately so (findings A-C1/B-C1/C-I3/D-C7): the client's tracer
    /// and the client's own knockback prediction live in Ring.Networking,
    /// which references Ring.Simulation but is NOT in
    /// Simulation/AssemblyInfo.cs's single InternalsVisibleTo (that names
    /// Ring.Simulation.Tests and nothing else). Precedents for a public
    /// static class here: PlayerPrediction, Trajectory, WeaponSystem.
    public static class Impact
    {
        /// Spring constants from the DAMPING RATIO and the SETTLE TIME, which
        /// is the only pair a human can tune (finding C-I2 — the spec got raw
        /// k/c wrong twice before this existed).
        ///
        /// k = (4 / (zeta * T))^2,  c = 2 * zeta * sqrt(k)
        ///
        /// NO EXTRA zeta^2 FACTOR. Spec v2 wrote one and it was wrong by a
        /// factor of 3.3 (k = 19.75 instead of 65.30, finding A2-C1): the
        /// peak-response coefficient would have grown to 0.1405, and the
        /// Elite would have started falling over from a headshot -- breaking
        /// the stated rule that nothing in today's arsenal knocks the heavy
        /// archetypes down.
        public static void SpringFromSettle(float dampingRatio, float settleSeconds,
            out float k, out float c)
        {
            float wn = 4f / (dampingRatio * settleSeconds);
            k = wn * wn;
            c = 2f * dampingRatio * math.sqrt(k);
        }

        /// Named constant, not two bare 1e-4f literals (finding B2-I6; the only named
        /// tolerance precedent in this project is Geometry.Skin = 1e-3f).
        public const float RestEpsilon = 1e-4f;

        /// One explicit-integrator step of the tilt spring, snap included (Т5).
        /// PUBLIC and pure, because THREE callers need exactly this arithmetic and
        /// one of them lives outside the simulation assembly: TiltSystem's mob pass,
        /// TiltSystem's collector pass, and Presentation's MobVisual. Written once
        /// here rather than three times there.
        ///
        /// THE SNAP IS PART OF THE STEP, not a caller's afterthought: an exponential
        /// never reaches zero, so after ~25 s the tilt drifts into the DENORMAL range
        /// — and FTZ/DAZ differ between the Linux server and the Windows client,
        /// which would make the golden digest platform-dependent. It also makes "the
        /// tilt returns to zero in a finite number of ticks" literally executable.
        public static void SpringStep(ref float tilt, ref float tiltVel,
            float dampingRatio, float settleSeconds, float dt)
        {
            SpringFromSettle(dampingRatio, settleSeconds, out float k, out float c);
            tiltVel += (-k * tilt - c * tiltVel) * dt;
            tilt += tiltVel * dt;
            if (math.abs(tilt) < RestEpsilon && math.abs(tiltVel) < RestEpsilon)
            {
                tilt = 0f;
                tiltVel = 0f;
            }
        }
    }
}
