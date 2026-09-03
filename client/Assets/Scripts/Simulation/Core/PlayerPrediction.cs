using Ring.Simulation.Combat;
using Ring.Simulation.Movement;
using Unity.Mathematics;

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
        ///
        /// `pulse` IS THE ONE THING ABOUT A HIT A CLIENT MAY APPLY (app-88jb
        /// Т7, spec §3.8): the shove the server already resolved, handed back
        /// so the predicted copy ends the tick where the world ended it. The
        /// hit itself, the damage and the death stay server business (CR 3) —
        /// this parameter carries no decision, only its consequence.
        ///
        /// NO DEFAULT VALUE, DELIBERATELY. Every call site is patched
        /// in the task that adds it, exactly as `DamageMob.ownerIndex` was:
        /// a defaulted `ImpactPulse.None` would silently mean "nothing hit
        /// this collector" on a live combat path, and the one caller that
        /// forgot to pass a real pulse would look identical to the callers
        /// that legitimately have none.
        ///
        /// ⚠ Т22 grew this signature by one more parameter
        /// (`ReadOnlySpan&lt;PushableBody&gt; visibleBodies`, finding Н20), and
        /// every call site got the same treatment a second time.
        /// ⚠ AN EARLIER WORDING COUNTED "THREE CALL SITES" AND NO ARRANGEMENT
        /// OF THE TREE EVER MATCHED IT (review of bd `app-njmi`, finding 6):
        /// there is exactly ONE production caller — `PlayerPredictionCore.
        /// Predict` — and six in tests. The count is dropped rather than
        /// corrected, because a number like this rots on the next task that
        /// adds a fixture, and what the paragraph is really saying does not
        /// need it. ⛔ AND THE ONE PRODUCTION CALLER IS WHY THE RULE MATTERS:
        /// it passed `ReadOnlySpan&lt;PushableBody&gt;.Empty` for three sessions
        /// while every fixture passed real bodies, so the client separated off
        /// nothing in a live match and no test could see it (bd `app-njmi`).
        public static void Step(ref PlayerState p, in SimInput rawInput, in SimConfig cfg,
            in ImpactPulse pulse, System.ReadOnlySpan<PushableBody> visibleBodies)
        {
            SimInput input = SimInputSanitizer.Sanitize(rawInput, p, cfg);
            p.AimPoint = input.AimPoint;
            PlayerMovementSystem.Update(ref p, in input, in cfg);
            WeaponSystem.AdvanceNoSpawn(ref p, in input, in cfg);
            // app-88jb Т22 (finding Н20/D-C11, owner decision Р442): the client's
            // half of the body separation, run through the SAME
            // BodySeparation.Accumulate the server calls — see that type's doc
            // for why one copy rather than two is the whole design.
            //
            // ⚠ AFTER THE WEAPON, BEFORE THE PULSE, because that is exactly
            // where the server puts it: SimulationWorld.Tick runs movement →
            // weapon → mobs → SeparationSystem → projectiles, and the pulse
            // below stands in for that projectile phase. A separation applied
            // before the weapon would sit in the wrong place of the same
            // line-up this method already reproduces field by field.
            //
            // AN EMPTY SET IS A LEGAL INPUT, not a degenerate one: a collector
            // that can see no body resolves against nothing, which is exactly
            // what the server does for it.
            SeparateFromBodies(ref p, in cfg, visibleBodies);
            // LAST, AFTER THE WEAPON, and the position in this line-up is the
            // whole contract (app-88jb Т7, finding A2-C5). The server resolves
            // a hit in ProjectileSystem, which runs AFTER both the movement
            // and the weapon phases of the same tick, so an impulse it grants
            // on tick T sits in Vel at the END of T and moves the body from
            // T+1. Applying it here reproduces that exactly; applying it
            // before the movement would give the client one tick of travel the
            // world never had, and no reconcile can argue that back.
            //
            // AN ADDITION, NEVER AN ASSIGNMENT, for the reason ImpactPulse
            // itself is summable: the pulse is already the SUM of every blow
            // the server resolved against this collector on this tick, and it
            // adds into the same Vel the movement above just wrote rather than
            // replacing it.
            //
            // THE SPRING IS NOT STEPPED HERE, and that is deliberate rather
            // than forgotten: TiltSystem.Apply owns the integration on both
            // sides, PredictedKnockback_MatchesTheServer_TickForTick pins this
            // method to the impulse alone, and PlayerState.Tilt is classified
            // Server in PredictionParityTests.RoleByField precisely because
            // this method never writes it.
            p.Vel += pulse.Delta;
            p.TiltVel += pulse.TiltImpulse;
        }

        /// The client's body separation (app-88jb Т22, owner decision Р442) —
        /// the same three steps the server runs for a collector, in the same
        /// order: bodies, then the arena, then bodies once more.
        ///
        /// WHAT IS ABSENT IS AS DELIBERATE AS WHAT IS HERE. There is no
        /// relaxation loop, because relaxation belongs to the mob CROWD and the
        /// client simulates no mobs; the collector's own pass is single by
        /// design on both sides, precisely so the client — which cannot move the
        /// bodies in its span — reaches the same answer as a server that can.
        /// And the reciprocals are dropped on the floor (two empty spans),
        /// because a client has no mobs to move and CRITICAL RULE 3 puts their
        /// fate on the server regardless.
        ///
        /// `moved` is threaded through BOTH passes because
        /// Hero.MaxDepenetrationPerTick is a per-tick ceiling — the server keeps
        /// the identical running total in SimulationWorld.SepPlayerMoved.
        static void SeparateFromBodies(ref PlayerState p, in SimConfig cfg,
            System.ReadOnlySpan<PushableBody> visibleBodies)
        {
            HeroSimConfig hero = cfg.Hero;
            float2 moved = float2.zero;
            CollectorPass(ref p, in hero, visibleBodies, ref moved, withShove: true);
            // Same guard the server applies (SeparationSystem.ResolveArena): this
            // pass answers "did the body push put me inside geometry?", and where
            // no body pushed there is no question — running it anyway re-clips a
            // velocity MoveWithCollisions deliberately left tangential.
            if (moved.x != 0f || moved.y != 0f)
                Geometry.Depenetrate(ref p.Pos, ref p.Vel, hero.Radius, in cfg.Arena, 1);
            CollectorPass(ref p, in hero, visibleBodies, ref moved, withShove: false);
        }

        static void CollectorPass(ref PlayerState p, in HeroSimConfig hero,
            System.ReadOnlySpan<PushableBody> bodies, ref float2 moved, bool withShove)
        {
            if (bodies.IsEmpty) return;
            float2 disp = float2.zero, push = float2.zero;
            BodySeparation.Accumulate(p.Pos, p.Vel, hero.Radius, hero.Mass,
                hero.PushRecoilFraction, bodies, ref disp, ref push,
                System.Span<float2>.Empty, System.Span<float2>.Empty);
            BodySeparation.ApplyToCollector(ref p, in hero, disp,
                withShove ? push : float2.zero, ref moved);
        }
    }
}
