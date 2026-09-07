using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// Single home of the spray PATTERN -- the shot's angle INSIDE the cone
    /// `Spread` sizes (app-8dv, spec §3.2/§3.8, owner decisions Н27/Н29/Н32/Н33).
    /// The angle is a pure function of state both sides already have -- the
    /// shot's number in the burst, the shot's number in the match, and the aim
    /// point -- so a predicting client reaches the SAME angle the server does.
    /// The world RNG draw this replaces made that impossible by construction:
    /// `SpreadRng` lives in the world and is advanced by shooters the client
    /// cannot see.
    ///
    /// ⚠ EXACTLY ONE MEMBER IS PUBLIC -- `Draw` -- and that is the neighbor's
    /// rule word for word. `Spread`'s own doc earns its publicity by naming a
    /// consumer ("public ahead of that need because CrosshairView will read the
    /// very same formula"), and `Spread` has exactly one public member. The
    /// salts and `Hash01` have no consumer outside this class, and this same
    /// plan demotes `TrySpawnFromPrediction` to `internal` on the very same
    /// argument (public, it would be a loaded gun). So they carry no modifier;
    /// a test, should it ever need them, is served by `internal` --
    /// `Simulation/AssemblyInfo.cs` is already open to `Ring.Simulation.Tests`.
    ///
    /// THERE IS NO AIM QUANTIZATION OF ITS OWN HERE, and that is a decision
    /// rather than an omission: the wire quantizes the aim pair once, when
    /// `ReplicateData.FromInput` builds the structure, after which both sides
    /// decode the SAME bytes. A second grid on top of the wire's would buy
    /// nothing -- its step (1/64 m = 1.5625 cm) practically coincides with the
    /// wire's (1.58 cm at the shipped arena radius), so it would not even make
    /// the seed coarser. The aim point goes into the hash whole.
    /// ⚠ AND THE RISK IT CLAIMED TO SOFTEN IS NOT SOFTENED BY IT EITHER
    /// (Р-F, "walking the mouse through sub-centimeter shifts"), for the same
    /// reason. The price of Р-F is accepted by the spec as it stands, and
    /// picturing a defense that does not exist would be worse than naming it.
    public static class SprayPattern
    {
        /// The salts are taken from the STYLE of the ones already living in
        /// this simulation (`SimulationWorld` seeds three streams with
        /// 0xB5297A4D / 0x68E31DA4 / 0x1B56C4E9) and deliberately do NOT
        /// collide with any of them by value, nor with the 0x9E3779B9
        /// zero-guard of `SimulationWorld.Fold`: a salt that matched somebody
        /// else's constant would read as a link that does not exist.
        const uint SaltYaw = 0x2545F491u;
        const uint SaltPitch = 0x94D049BBu;

        /// Both angles in ONE call: yaw in .x, pitch in .y, radians, already
        /// multiplied by the half-width of the cone.
        public static float2 Draw(int burstShots, int shotOrdinal, float2 aimPoint,
            float coneRadians, in WeaponSimConfig weapon)
        {
            // The floor under the divisor is the SOLE safety net against a
            // fixture that never passed the validator (the precedent is
            // WeaponSystem.IntervalFor, whose own 1e-3f is called exactly
            // that there): the validator catches a config, this floor catches
            // a test's partial initializer.
            int n = math.max(1, weapon.SprayPatternShots);
            int k = burstShots + 1;                                    // 1-based, see test 4c
            float amp = math.min(k, n) / (float)n;                     // saturates
            float phase = weapon.SprayYawTurns * 2f * math.PI * k / n; // grows ALWAYS
            float yawBase = weapon.SprayYawAmplitude * math.sin(phase) * amp;
            float pitchBase = weapon.SprayPitchAmplitude * amp;

            float u = Hash01(shotOrdinal, aimPoint, SaltYaw);
            float v = Hash01(shotOrdinal, aimPoint, SaltPitch);

            return new float2(
                coneRadians * math.lerp(yawBase, 2f * u - 1f, weapon.SprayVariance),
                coneRadians * math.lerp(pitchBase, (2f * v - 1f) * weapon.SprayPitchAmplitude,
                    weapon.SprayVariance));
        }

        /// [0..1), a pure function of the shot's number, the aim point and the
        /// salt.
        ///
        /// ⭐ BUILT ON StateHash64 RATHER THAN ON A MIXER OF ITS OWN (rule 2):
        /// this project's FNV-1a is already deterministic, already free of
        /// allocations and already guarded by the golden hashes; there is no
        /// other deterministic mixer in Ring.Simulation
        /// (Unity.Mathematics.Random is a STREAM generator, and a stream is
        /// exactly what this task walks away from). The top 24 bits are used
        /// because FNV-1a avalanches better in the high bits than in the low
        /// ones, and because 2^24 steps cut the widest cone of this task
        /// (11 degrees in a slide) into 6.6e-7 of a degree -- six orders of
        /// magnitude finer than anything visible.
        /// ⚠ THE PRICE OF THE COUPLING IS NAMED: from this task on, editing
        /// StateHash64 moves not only the golden constant but the TRAJECTORIES
        /// OF ROUNDS.
        static float Hash01(int ordinal, float2 aimPoint, uint salt)
        {
            ulong h = StateHash64.Begin();
            h = StateHash64.Add(h, ordinal);
            // ⭐ THE POINT GOES INTO THE HASH WHOLE, WITH NO GRID OF ITS OWN,
            // and the float2 overload canonicalizes the sign of zero
            // (-0.0 -> +0.0) inside its Add(float) -- that is, it already does
            // the one thing a grid of our own was going to be introduced for.
            h = StateHash64.Add(h, aimPoint);
            h = StateHash64.Add(h, unchecked((int)salt));
            return (float)(h >> 40) * (1f / 16777216f);
        }
    }
}
