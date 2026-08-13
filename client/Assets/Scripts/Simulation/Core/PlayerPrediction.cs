using Ring.Simulation.Combat;
using Ring.Simulation.Movement;

namespace Ring.Simulation.Core
{
    /// Public prediction seam (spec §3.9, Stage 2 Task 30) — the ONE entry point
    /// a predicting client drives its own copy of PlayerState through, and the
    /// seam Stage 2 Task 34 builds client-side prediction on. It exists because
    /// PlayerMovementSystem and WeaponSystem are `internal` and a client needs
    /// the whole COMPOSITION anyway, not one of the parts: what makes prediction
    /// correct is doing exactly what the world does, in exactly that order.
    /// PredictionParityTests is the proof, bit for bit, tick after tick.
    public static class PlayerPrediction
    {
        /// Exactly what the world does to a LIVE player in one tick (compare
        /// SimulationWorld.TickAll's own movement/weapon phases), minus what the
        /// client must never own: the projectile spawn, the spread RNG draw and
        /// the stats — see WeaponSystem.AdvanceNoSpawn.
        ///
        /// `rawInput` is RAW, exactly like SimulationWorld.Tick's own argument:
        /// sanitizing is part of the step, not a precondition on the caller, so
        /// the client cannot get it subtly different from the server (a
        /// duplicated Sanitize is the one thing SimInputSanitizer exists to
        /// prevent). What the caller DOES owe is that this be the input it
        /// actually sends: prediction must run on the DECODED value
        /// (`InputCodec.Decode(Encode(...))`, Р34), or client and server
        /// permanently disagree about a value neither of them got wrong.
        ///
        /// COMPOSITION ORDER is the world's own, and it is load-bearing.
        /// Sanitize reads the player's state as it stood at the END of the
        /// previous tick (the world computes `_sanitizedInputs[i]` before it
        /// moves anybody); AimPoint is pinned next; movement runs; the weapon
        /// runs LAST, on the post-movement state, because its own "can I fire"
        /// predicate reads the dash/slide timers this tick's movement just set.
        /// The world sequences that as movement-of-everyone then
        /// weapon-of-everyone (two loops, not interleaved) so that a player's
        /// weapon phase sees every player's post-movement position; for the ONE
        /// player a client predicts, that is equivalent to this straight line,
        /// because the weapon reads only its own state and its own input.
        ///
        /// NO SEPARATE GATE STEP. The edge-request rate limit lives INSIDE
        /// PlayerMovementSystem.Update (Stage 2 Task 10) and is therefore already
        /// applied by the call below. A second application here would decrement
        /// DashRequestCooldownTicks/SlideRequestCooldownTicks twice per tick on
        /// the client and desync the gate itself — mispredicting the dash, not
        /// merely the position.
        ///
        /// MovementResult IS DELIBERATELY DISCARDED. Dash/slide events, the
        /// stamina-denied emission and MatchStats are all server business (CR 3);
        /// a client that acted on them would be deciding game outcomes. Cosmetic
        /// client-side reactions belong on the events the server sends back.
        ///
        /// NOT FOR A DEAD PLAYER. The world advances a corpse through
        /// PlayerMovementSystem.UpdateDead instead, and prediction is required to
        /// STOP at death (Р41/Р59) — enforcing that is the caller's job in Stage
        /// 2 Task 34, so this method neither checks Alive nor pretends to.
        public static void Step(ref PlayerState p, in SimInput rawInput, in SimConfig cfg)
        {
            SimInput input = SimInputSanitizer.Sanitize(rawInput, p, cfg);
            p.AimPoint = input.AimPoint;
            PlayerMovementSystem.Update(ref p, in input, in cfg);
            WeaponSystem.AdvanceNoSpawn(ref p, in input, in cfg);
        }
    }
}
