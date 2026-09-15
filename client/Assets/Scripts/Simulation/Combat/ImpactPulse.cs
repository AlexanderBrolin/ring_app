using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// One tick's worth of authoritative impulse against the local collector
    /// (app-88jb Т7, spec §3.8, owner decision Н18) — the shove a predicting
    /// client is allowed to apply to its own copy of PlayerState, and the
    /// ONLY thing about a hit it may apply at all (CRITICAL RULE 3: the
    /// server owns the damage, the death and the hit itself; the client owns
    /// where its own body ends up).
    ///
    /// PUBLIC, for the same reason Impact is (its own doc gives the full
    /// account): the client assembles this in Ring.Networking — from the
    /// PlayerDamaged event, through Impact.VelocityDelta and
    /// Impact.AngularImpulse (Т9) — and Ring.Networking is not in
    /// Simulation/AssemblyInfo.cs's single InternalsVisibleTo.
    ///
    /// SUMMABLE, AND THAT IS NOT A NICETY (finding D2-C4): at the round
    /// counts this arsenal reaches, two hits landing on one collector in one
    /// tick is ordinary rather than exotic, and a (direction, speed) PAIR
    /// cannot express two blows at once — it can only express the last one,
    /// or a mean nobody asked for. BOTH MEMBERS ADD, and since app-94sk T5a
    /// both are vectors (this sentence read "a vector delta and a scalar
    /// moment" until the moment gained its direction) — so two blows landing
    /// from opposite sides now cancel the lean as well as the shove, which a
    /// signed magnitude could only have done for shots on one axis.
    ///
    /// NO `Any` FLAG (finding B2-M5): `Delta` and `TiltImpulse` both at zero
    /// already means "no shove happened", unambiguously and with nothing to
    /// keep in step. A third field that MUST agree with the other two is a
    /// third field that CAN disagree with them — the precedent is
    /// TracerProjectiles' `NoEnd`, which is a sentinel VALUE of the field it
    /// qualifies rather than a companion flag beside it.
    ///
    /// TICK SEMANTICS, STATED HERE BECAUSE BOTH SIDES HAVE TO OBEY IT
    /// (finding A2-C5): the server resolves a hit in ProjectileSystem, AFTER
    /// movement and the weapon, so an impulse it grants on tick T lands in
    /// Vel at the END of tick T and only moves the body from T+1. The client
    /// therefore applies this at the END of its own Step for T, never at the
    /// start of T+1 — a semantics that slips by a single tick diverges the
    /// two copies for good.
    public readonly struct ImpactPulse
    {
        /// Velocity to add to PlayerState.Vel, in meters per second, already
        /// summed over every blow this tick and already through
        /// Impact.VelocityDelta (ceiling first, cocoon damping second — the
        /// order is load-bearing, see that function's own doc).
        public readonly float2 Delta;

        /// Angular velocity to add to PlayerState.TiltVel, in radians per
        /// second, already summed the same way and already through
        /// Impact.AngularImpulse. Signed: a hit above the center of mass
        /// tips the body along the shot, one below undercuts it.
        ///
        /// A VECTOR SINCE app-94sk T5a, exactly as PlayerState.TiltVel is --
        /// LENGTH is the moment, DIRECTION is the blow's own heading, and the
        /// sign that used to carry "along or against the shot" is now the
        /// direction pointing one way or the other. IT HAD TO MOVE WITH THE
        /// FIELD IT IS ADDED INTO (PlayerPrediction.Step: `TiltVel +=
        /// pulse.TiltImpulse`); a scalar here would have made the predicting
        /// client rebuild a lean with no direction while the server's had one,
        /// which is a divergence rather than a cosmetic gap.
        ///
        /// AND THE SUMMING SURVIVED THE CHANGE UNTOUCHED (ImpactPulseLog.Add):
        /// two blows in one tick still add, and adding vectors is what makes
        /// two hits from OPPOSITE sides cancel the way the bodies they hit
        /// would -- which a pair of magnitudes could not express at all.
        public readonly float2 TiltImpulse;

        public ImpactPulse(float2 delta, float2 tiltImpulse)
        {
            Delta = delta;
            TiltImpulse = tiltImpulse;
        }

        /// "Nothing hit this collector on this tick" — the value every caller
        /// with no event to report passes, rather than each of them writing
        /// out a default of its own.
        public static readonly ImpactPulse None = default;
    }
}
