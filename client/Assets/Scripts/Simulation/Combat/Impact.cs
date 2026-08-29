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

        /// Momentum-conserving shove between two bodies in contact (app-88jb Т22,
        /// owner decision Р442). The target gains the pusher's momentum share;
        /// the pusher loses that share mirrored, scaled by how much of the
        /// reaction its own footing FAILS to absorb -- a self-propelled body
        /// pushes against the ground and the ground takes the rest.
        /// `pusherRecoilFraction` IS that leftover: 0.25 for the collector, 1.0
        /// for a mob, which makes mob-vs-mob conserve momentum EXACTLY and
        /// leaves the collector's deviation ONE named number in a
        /// ScriptableObject instead of a special case in the caller.
        ///
        /// THIS IS NOT VelocityDelta ABOVE, AND THE TWO ARE KEPT APART ON
        /// PURPOSE. A projectile EMBEDS itself: its whole momentum is absorbed,
        /// the target's own reaction lives in CocoonDamping, and `m*v/M` has no
        /// built-in bound, which is why that formula needs ImpactSpeedCap.
        /// Two bodies BOTH move, so the share below is under one by
        /// construction. The bullet form is the small-mass limit of this one
        /// (m_src/(m_src+m_tgt) -> m_src/m_tgt as m_src shrinks), and reusing it
        /// verbatim was measured and rejected: at the collector's 120 kg it
        /// gives 40 / 18 / 10 m/s for dash / slide / run, and a ceiling of 6
        /// flattens all three to ONE number -- erasing exactly the difference
        /// between a dash and a walk that Р442 exists to create.
        ///
        /// NO CEILING HERE, AND THAT IS THE DECISION (ruling 114). Three bounds
        /// already stand: the share is below one, so a body can never leave
        /// faster than whatever ran into it; mobs move through
        /// MoveWithCollisions, which SWEEPS, so no speed tunnels through a wall;
        /// and MobAiSystem bleeds Vel back toward its own MaxSpeed at Accel*dt
        /// every tick (1.0 m/s per tick for a chaser). A fourth, invented
        /// ceiling would buy none of that and would cost the thing the owner
        /// asked for: a faster dash must shove harder, without end.
        ///
        /// THE GUARD IS THE THRESHOLD. `approachSpeed <= 0` covers a body
        /// standing still, one already leaving, and -- the interesting case --
        /// the tick after a shove has landed, because the target now outruns the
        /// pusher and the closing speed goes negative on its own. The impulse is
        /// therefore self-limiting, which is why no minimum-speed constant
        /// exists: it would be a second answer to a question the sign already
        /// answers, and a standing collector would still not jitter the crowd.
        ///
        /// `approachSpeed` is the closing speed ALONG THE CONTACT NORMAL, never
        /// a full-vector magnitude: a collector running PAST a body must not
        /// hurl it the way one running INTO it does.
        public static bool ResolveBodyPush(float pusherMass, float targetMass,
            float approachSpeed, float pusherRecoilFraction,
            out float targetDelta, out float pusherDelta)
        {
            targetDelta = 0f;
            pusherDelta = 0f;
            if (approachSpeed <= 0f) return false;
            float share = pusherMass / (pusherMass + targetMass);
            targetDelta = share * approachSpeed;
            pusherDelta = pusherRecoilFraction * (1f - share) * approachSpeed;
            return true;
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

        /// The two numbers one PIERCE is judged by, answered together (app-88jb
        /// Т20, coordinator Ruling 101). A struct rather than two `…For` helpers
        /// for exactly the reason RicochetNumbers above gives about three: two
        /// helpers would be the SAME owner branch written twice, and a fork
        /// written out twice is a fork that drifts. readonly struct, never a
        /// class: this is read on the projectile path, where allocations are
        /// forbidden (AllocationTests.Tick_DoesNotAllocateGC).
        public readonly struct PierceNumbers
        {
            /// The DIRECT ratio of round mass to target mass a round must BEAT.
            /// Direct rather than its reciprocal, and that is the decision
            /// (spec §3.4, finding C-I10): v1 wrote the same rule as
            /// `TargetMass / ProjectileMass < 1 / PierceMassRatio`, a double
            /// inversion whose value 0 divided by zero and pierced EVERYTHING,
            /// the Director included. Written this way round, 0 is refused by
            /// validation rule 10 instead of being the most dangerous number in
            /// the block.
            public readonly float MassRatio;
            /// The share of its damage a piercing round gives up, in [0, 1).
            public readonly float DamageLoss;

            public PierceNumbers(float massRatio, float damageLoss)
            {
                MassRatio = massRatio;
                DamageLoss = damageLoss;
            }
        }

        /// The ONE home of the "who fired it" fork over the piercing numbers,
        /// keyed exactly the way ProjectileMassFor and RicochetNumbersFor above
        /// are keyed and for exactly their reasons (app-88jb Т20, Ruling 101).
        ///
        /// A MOB-OWNED ROUND READS THE GUNNER ARCHETYPE'S NUMBERS, not its own
        /// shooter's, and that is this family's existing rule rather than a new
        /// simplification: ProjectileMassFor and RicochetNumbersFor above both
        /// answer `cfg.Gunner` for every mob-owned round. The other three
        /// archetypes carry the fields (MobConfig is one class behind four
        /// assets) and nothing reads them today — the gunner is the only
        /// archetype that shoots.
        public static PierceNumbers PierceNumbersFor(byte ownerIndex, in SimConfig cfg)
            => ownerIndex == ProjectileIds.NoOwner
                ? new PierceNumbers(cfg.Gunner.PierceMassRatio, cfg.Gunner.PierceDamageLoss)
                : new PierceNumbers(cfg.Weapon.PierceMassRatio, cfg.Weapon.PierceDamageLoss);

        /// THE PIERCING RULE ITSELF, and its ONE home (app-88jb Т20, spec §3.4,
        /// owner decision Н13, Ruling 101):
        ///
        ///   projectileMass / targetMass > MassRatio  &&  damageDealt > targetHp
        ///
        /// Public and written once because TWO callers ask it — ProjectileSystem's
        /// `case HitMob` and its `case HitPlayer`, through
        /// ProjectileFlight.TryPierce — and a RULE written out twice drifts for
        /// the same reason a FORK written out twice does, which is what
        /// ProjectileMassFor's own doc says one level down. Scalars only, no
        /// owner key: the caller has already resolved the numbers, so this is
        /// the VelocityDelta/AngularImpulse family rather than the `…For` one.
        ///
        /// ⚠ THE SECOND CLAUSE IS STRICT OVERKILL, NOT "THE TARGET DIES"
        /// (coordinator Ruling 102). Death in this project is `Hp -= dmg`
        /// followed by `Hp <= 0` — SimulationWorld.DamageMob and .DamagePlayer,
        /// both of them — i.e. `damageDealt >= targetHp`; this rule asks for
        /// STRICTLY more. So at exact equality the target dies and the round is
        /// consumed anyway. That is the spec's own formula rather than an
        /// off-by-one, it errs toward NOT piercing, and
        /// ProjectileFlightTests.ExactlyLethalRound_DoesNotPierce_ButOnePoint
        /// OfOverkillDoes is what keeps it a decision instead of an accident.
        ///
        /// ⚠ WHAT THIS DOES NOT ASK is whether the blow will LAND at all. A
        /// collector with dash i-frames up takes no damage (DamagePlayer's own
        /// second guard) — and that question belongs to the CALL SITE, beside
        /// the method that owns the guard, because it is about the blow
        /// ARRIVING and not about the round PIERCING (Ruling 101's own
        /// boundary). Mobs have no such guard, which is why only one of the two
        /// call sites carries the extra condition.
        ///
        /// `targetMass` is never zero, and that holds on BOTH sides of the
        /// two-sources split: validation rule 1 requires a positive Mass on
        /// every body for anything that goes through SimConfigBuilder
        /// (ImpactConfigTests.Validate_ZeroMobMass_Throws), and the fixtures
        /// that construct a SimConfig directly and skip that rule were swept —
        /// TestConfigs states all five masses (120 / 90 / 70 / 260 / 4000), and
        /// no other literal SimConfig in the suite is ever handed to a
        /// SimulationWorld at all. So the division has no degenerate case to
        /// defend against here, and a guard would be a branch without a
        /// reachable input.
        public static bool Pierces(in PierceNumbers n, float projectileMass, float targetMass,
            float damageDealt, float targetHp)
            => projectileMass / targetMass > n.MassRatio && damageDealt > targetHp;
    }
}
