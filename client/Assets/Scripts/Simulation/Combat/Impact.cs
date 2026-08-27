using Ring.Simulation.Core;
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

        /// The ONE home of the impact formula (spec §3.2, owner decision Н14):
        ///
        ///   dv = min( projectileMass * |Vel3| / targetMass , targetImpactSpeedCap ) / damping
        ///
        /// |Vel3| is the FULL 3D speed (length(float3(Vel, VelZ))), because
        /// WeaponSimConfig.ProjectileSpeed is itself the length of the 3D vector in
        /// this project (combat-depth spec §3.2) -- a horizontal-only magnitude here
        /// would silently under-shove every angled shot.
        ///
        /// ORDER IS LOAD-BEARING: the CEILING is applied BEFORE the damping divides
        /// (finding C-I9/A-I4, decision Р393). The collector's effective ceiling is
        /// therefore ImpactSpeedCap / CocoonDamping -- 6 / 3 = 2 m/s at the shipped
        /// numbers, not 6.
        public static float VelocityDelta(float projectileMass, float projectileSpeed3D,
            float targetMass, float targetImpactSpeedCap, float damping)
        {
            float raw = projectileMass * projectileSpeed3D / targetMass;
            return math.min(raw, targetImpactSpeedCap) / damping;
        }

        /// Peak tilt of a single impulse -- computed by RUNNING THE ACTUAL INTEGRATOR,
        /// never by a closed form (round-3 finding C-C1, and this is the whole point).
        ///
        /// TWO reasons, both measured, not argued:
        ///   1. The closed form the spec and plan v2 carried DROPPED sin(phi). For an
        ///      impulse response the peak is (w0/wn)*exp(-zeta*wn*phi/wd), because at
        ///      the maximum sin(phi) == wd/wn EXACTLY; writing (w0/wd)*exp(...)
        ///      overstates it by wn/wd = 1/sqrt(1-zeta^2) = 1.19737 at zeta 0.55.
        ///   2. Even the CORRECTED closed form is not what the game does. The game
        ///      integrates with semi-implicit Euler at dt = 1/30, where c*dt = 0.296
        ///      adds discrete damping the continuous solution knows nothing about.
        ///      At zeta 0.55 / T 0.9 s the chaser headshot impulse peaks at 0.586 rad
        ///      through the integrator against 0.789 through the corrected closed form
        ///      and 0.945 through the plan-v2 one. The threshold is 0.9: the milestone
        ///      rule "a headshot puts the chaser down" was UNREACHABLE at TiltGain 6.5,
        ///      and no test could see it because both witnesses sat on the formula.
        ///
        /// The regime is OSCILLATORY on purpose: the body rocks and comes back, and
        /// that rock is what reads as a blow. (Spec v1 claimed both regimes in one
        /// sentence -- finding A-M1.)
        ///
        /// `dt` is a PARAMETER, not SimulationWorld.TickDt read from here: Impact
        /// stays a pure function of its arguments, and the caller that cares about
        /// game feel is the one that owns the tick length.
        public static float PeakTilt(float angularImpulse, float dampingRatio, float settleSeconds,
            float dt)
        {
            float tilt = 0f, tiltVel = angularImpulse, peak = 0f;
            // The window is three settle times -- ceil(3 * 0.9 / (1/30)) = 81 steps
            // at the shipped numbers. It is a BOUND, not a tuned quantity, and the
            // numbers behind that word are measured rather than promised (Ruling 10):
            // a unit impulse PEAKS ON STEP 4 (t ~ 0.133 s), and SpringStep's
            // RestEpsilon snap zeroes the walk on STEP 44, after which the remaining
            // 37 steps of the 81 do nothing at all. The window is therefore an order
            // of magnitude wider than the answer needs, and shrinking it to a handful
            // of steps would not change what this function returns -- so it is NOT a
            // number the answer stands on. What the bound does buy is the only thing
            // asked of it: the loop is FINITE BY CONSTRUCTION, with no convergence
            // test, no early return, and no tail that can run away.
            int steps = (int)math.ceil(3f * settleSeconds / dt);
            for (int i = 0; i < steps; i++)
            {
                SpringStep(ref tilt, ref tiltVel, dampingRatio, settleSeconds, dt);
                peak = math.max(peak, math.abs(tilt));
            }
            return peak;
        }

        /// The ONE home of the moment a hit applies to a body (round-3 finding C-I1).
        ///
        ///   angularImpulse = (hitHeight - centerOfMassHeight) * dv * gain     [rad/s]
        ///
        /// PUBLIC and written once, because FOUR places need exactly this arithmetic
        /// and two of them live outside Ring.Simulation: DamageMob (T5), DamagePlayer
        /// (T7), the client's ClientEventDecoder building an ImpactPulse (T9) and
        /// Presentation's MobVisual rebuilding a mob's tilt (T31). Four hand-written
        /// copies of one signed subtraction is exactly the shape round 2 removed for
        /// the spring step, and the sign of the arm is the half that silently flips.
        public static float AngularImpulse(float hitHeight, float centerOfMassHeight,
            float dv, float gain)
            => (hitHeight - centerOfMassHeight) * dv * gain;

        /// The ONE home of the "who fired it" fork over projectile mass: the player
        /// weapon's for a player-owned round, the Gunner archetype's for a mob's
        /// (round-3 finding C-I2). Written once because BOTH sides need it --
        /// Ring.Simulation.Combat.ProjectileSystem on the server (T4) and the
        /// client's ClientEventDecoder rebuilding an ImpactPulse (T9) -- and a fork
        /// written out twice is a fork that drifts.
        ///
        /// Exact precedent, one namespace over: SnapshotEvents.SpeedCapFor, whose own
        /// note reads "ONE home for the rule, called by both sides -- the same fix
        /// Task 27 applied to SnapshotBlocks.MaxHpFor after that branch had been
        /// written out twice". This is the same rule for the same pair of callers.
        public static float ProjectileMassFor(byte ownerIndex, in SimConfig cfg)
            => ownerIndex == ProjectileIds.NoOwner ? cfg.Gunner.ProjectileMass : cfg.Weapon.ProjectileMass;

        /// The three numbers one ricochet is judged by, answered TOGETHER
        /// (app-88jb Т19, coordinator Ruling 97). A struct rather than three
        /// `…For` helpers because three helpers would be the SAME owner branch
        /// written three times, which is the defect this whole family exists to
        /// prevent -- see ProjectileMassFor's own doc above and
        /// SnapshotEvents.SpeedCapFor's ("ONE home for the rule, called by both
        /// sides"). readonly struct, never a class: this is read on the
        /// projectile path, where allocations are forbidden
        /// (AllocationTests.Tick_DoesNotAllocateGC).
        public readonly struct RicochetNumbers
        {
            /// How many times one round may reflect at all.
            public readonly int Max;
            /// The share of 3D speed a reflection keeps -- applied to `Vel` and
            /// `VelZ` alike, so the direction survives and only the magnitude
            /// falls.
            public readonly float Retention;
            /// The floor the DAMPED speed must still clear for the reflection
            /// to happen; below it the round is extinguished instead.
            public readonly float MinSpeed;

            public RicochetNumbers(int max, float retention, float minSpeed)
            {
                Max = max;
                Retention = retention;
                MinSpeed = minSpeed;
            }
        }

        /// The ONE home of the "who fired it" fork over the ricochet numbers,
        /// keyed exactly the way ProjectileMassFor above is keyed and for
        /// exactly its reasons (app-88jb Т19, Ruling 97). Its readers are
        /// ProjectileFlight.TryRicochet on the server and, from Т32, the
        /// client's tracer through that same method -- a fork written out twice
        /// is a fork that drifts.
        ///
        /// A MOB-OWNED ROUND READS THE GUNNER ARCHETYPE'S NUMBERS, not its own
        /// shooter's, and that is this family's existing rule rather than a new
        /// simplification: ProjectileMassFor and SnapshotEvents.SpeedCapFor
        /// both answer `cfg.Gunner` for every mob-owned round. The other three
        /// archetypes carry the fields (MobConfig is one class behind four
        /// assets) and nothing reads them today, exactly as they carry
        /// ProjectileMass and nothing reads that either -- the gunner is the
        /// only archetype that shoots.
        public static RicochetNumbers RicochetNumbersFor(byte ownerIndex, in SimConfig cfg)
            => ownerIndex == ProjectileIds.NoOwner
                ? new RicochetNumbers(cfg.Gunner.MaxRicochets, cfg.Gunner.RicochetRetention,
                    cfg.Gunner.RicochetMinSpeed)
                : new RicochetNumbers(cfg.Weapon.MaxRicochets, cfg.Weapon.RicochetRetention,
                    cfg.Weapon.RicochetMinSpeed);
    }
}
