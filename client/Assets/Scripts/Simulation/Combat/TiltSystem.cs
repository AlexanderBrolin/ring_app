using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Combat
{
    /// The tick-side half of body tilt (app-88jb Т5, spec §3.2, owner
    /// correction Н10): every body walks its tilt spring one step per tick.
    /// TWO PASSES SINCE Т7 -- the mobs, then the collectors -- and neither
    /// owns an impulse: those live where their blow is resolved (DamageMob
    /// adds Impact.AngularImpulse into MobState.TiltVel, DamagePlayer into
    /// PlayerState.TiltVel), and this system only ever integrates what they
    /// left behind.
    ///
    /// SINCE Т6 IT ALSO OWNS ONE DECISION, not just the integration, AND THAT
    /// DECISION IS THE MOBS' ALONE: a mob whose |Tilt| passes
    /// MobSimConfig.TiltFallAngle enters MobAiState.Downed (spec §3.2), while
    /// a collector has no such threshold and never will (Р377 -- see the
    /// collector pass's own note). That decision lives here rather than in
    /// the AI because this is the one place that owns the angle it is made
    /// from; its
    /// counterpart, the exit back to Idle after DownedSeconds, lives in
    /// MobAiSystem.Update ahead of the dispatch by MobType, because Downed is
    /// precisely the state that cancels an archetype's FSM (coordinator
    /// Ruling 20). Nothing else about the step changed: the spring still walks
    /// for every body, downed ones included (Ruling 21, and see the loop).
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

                // app-88jb Т6 (spec §3.2): THE ENTRY into Downed lives here,
                // beside the tilt it is decided from, so "the body went over"
                // is settled in the one place that owns the angle. The EXIT
                // lives in MobAiSystem.Update, ahead of the dispatch by
                // MobType (coordinator Ruling 20) -- an archetype's FSM is
                // exactly what this state cancels, so it cannot be that FSM's
                // own business.
                //
                // THE THRESHOLD IS TESTED BEFORE THE SPRING STEPS, and the
                // order is load-bearing (round-3 finding D-C1, which reversed
                // round 2's M-d). Under "step, then test" a body handed
                // exactly TiltFallAngle is already down to 0.8347 by the time
                // of the comparison, so `>` and `>=` become indistinguishable
                // in principle and the boundary has no witness that can tell
                // them apart. The cost is a one-tick lag between the impulse
                // and the fall -- the same lag SeparationSystem's own doc
                // already documents and accepts.
                //
                // STRICTLY `>`: exactly TiltFallAngle stands
                // (TiltExactlyAtTheThreshold_DoesNotKnockDown is the witness,
                // and that pair of asserts is what mutation M6 kills).
                //
                // `m.Ai != Downed` GUARDS RE-ENTRY, and it guards the timer,
                // not the state: without it a body still past the angle would
                // re-zero StateTimer every tick and never stand up again.
                // StateTimer is the EXISTING generic FSM timer, not a new
                // field (findings B-I3/A-I13) -- nothing else owns it while
                // the archetype FSM is canceled.
                if (m.Ai != MobAiState.Downed && math.abs(m.Tilt) > cfg.TiltFallAngle)
                {
                    m.Ai = MobAiState.Downed;
                    m.StateTimer = 0f;
                }

                // THE SPRING STEPS UNCONDITIONALLY, DOWNED BODIES INCLUDED
                // (coordinator Ruling 21). Skipping it for a downed body
                // would be the symmetric-looking mistake: the mob would stand
                // up carrying the very tilt that felled it, cross the
                // threshold on that same tick and fall again -- a loop with
                // no exit. With the step running, a body that fell from 0.95
                // rad is at |Tilt| ~ 2.6e-4 by the time DownedSeconds is up
                // (36 ticks at zeta 0.55 / T 0.9 s), which is what makes
                // standing up stick.
                Impact.SpringStep(ref m.Tilt, ref m.TiltVel,
                    cfg.TiltDampingRatio, cfg.TiltSettleSeconds, dt);
            }

            // THE COLLECTOR PASS (app-88jb Т7, spec §3.2, owner decision Р377)
            // -- the same spring, the same epsilon snap, one line, through the
            // same public Impact.SpringStep the loop above uses. The impulse
            // half lives where that blow is resolved too (SimulationWorld.
            // DamagePlayer adds Impact.AngularImpulse into PlayerState.TiltVel),
            // so this pass, like the mob's, only ever integrates what somebody
            // else left behind.
            //
            // NO THRESHOLD, NO Downed, NOTHING TO COMPARE (Р377). A mob past
            // MobSimConfig.TiltFallAngle stops acting for DownedSeconds;
            // HeroSimConfig carries no such angle and is not to be given one,
            // because taking control away from a player because a round landed
            // contradicts ADR-001 §9, where evasion is the skill the fight is
            // asking for. That is why this pass is a step and not a decision:
            // it has no branch whose ordering could be got wrong, unlike the
            // mob's, where the comparison has to happen BEFORE the step
            // (round-3 finding D-C1, see the loop above).
            //
            // EVERY PLAYER, CORPSES INCLUDED, and that mirrors two rules this
            // project already keeps rather than inventing a third. Ruling 21:
            // the mob spring steps for downed bodies too, because a body that
            // stopped integrating keeps the tilt that felled it forever.
            // PlayerMovementSystem.UpdateDead: "the corpse still decelerates
            // under friction and resolves collisions like a live body" -- Vel
            // is not frozen and not zeroed on death, it is allowed to settle,
            // and Tilt is the same kind of quantity. That is also why
            // SimulationWorld.ClearCombatTimers does NOT clear this pair: it
            // clears TIMERS a body leaving the fight must drop, and explicitly
            // leaves physical state alone (its own note on DashDir, "a heading,
            // not a timer"). A corpse's tilt is hashed and must therefore reach
            // a definite value -- which it does, exactly, through
            // Impact.SpringStep's own RestEpsilon snap.
            //
            // The config is read ONCE, outside the loop: every collector shares
            // one HeroSimConfig, unlike the mobs above, whose numbers are
            // per-archetype (SimulationWorld.Config returns a copy of the whole
            // struct, so a per-player read would copy it PlayerCount times).
            HeroSimConfig hero = w.Config.Hero;
            for (int i = 0; i < w.PlayerCount; i++)
            {
                // Through PlayerRef, the systems' seam into live player
                // storage, never SetPlayerForTest: a battle path calling a
                // method named "ForTest" is the exact defect
                // Loot.PickupSystem.AdvanceTtl's own doc records being fixed.
                ref PlayerState p = ref w.PlayerRef(i);
                Impact.SpringStep(ref p.Tilt, ref p.TiltVel,
                    hero.TiltDampingRatio, hero.TiltSettleSeconds, dt);
            }
        }
    }
}
