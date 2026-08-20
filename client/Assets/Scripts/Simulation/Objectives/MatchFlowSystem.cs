using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Objectives
{
    /// The raid's own phase machine (spec §3.5, Р219/Р256/Р299, Stage 3 Т21):
    /// Farm -> DirectorActive -> GateOpen, plus Ended, which outranks all
    /// three. It owns every write to MatchState and nothing else — the phase
    /// and the Director's death tick are the whole of that state (Р219a), and
    /// both are already part of StateHash/WorldSave/CaptureSnapshot since the
    /// sanctioned re-pin of Т6.
    ///
    /// LAST STEP OF TickAll, AND THAT IS A RULE, NOT A PLACEMENT (Р256, R-2).
    /// It runs after movement, after combat, after the loot channel and after
    /// pickups, so every position and every mob it reads is this tick's
    /// SETTLED one. Two consequences the spec spells out and this file
    /// therefore must not reorder: a collector who crosses into the core
    /// during a tick activates the Director on THAT tick (the portal closing
    /// takes effect from the next one), and — once Т23 lands the extraction
    /// channel ahead of this call — a collector who finishes his channel on
    /// the activation tick still gets out.
    ///
    /// NOTHING HERE IS A TIMER. Р299 replaced the old
    /// DirectorActivationSeconds with "a live collector stood in the core",
    /// so a raid nobody walks into the core in simply never leaves Farm — a
    /// legitimate raid, by that decision, with no fallback clock behind it.
    /// The one countdown that does exist, GateDelaySeconds, is measured off
    /// the Director's own death tick rather than kept as a ticking field, for
    /// the same reason: a stored countdown would be a derived value inside
    /// the state hash.
    ///
    /// WHO WRITES Ended: NOT THIS FILE (coordinator R-172). The end of a raid
    /// is MatchEndPolicy's decision, and that class lives in
    /// Ring.Networking.Server — the simulation neither sees it nor can (the
    /// assembly reference runs one way), and the duration limit it reads lives
    /// in NetConfig, which is not part of SimConfig at all (Р72). So this file
    /// only ever READS Ended, first thing, and refuses to move a raid that is
    /// over; the writer arrives with Т24, the task that owns the end of the
    /// raid and already touches MatchServer.
    internal static class MatchFlowSystem
    {
        public static void Update(SimulationWorld w)
        {
            ref MatchState m = ref w.MatchRef;

            // Ended is checked FIRST (Р256 п.2/п.3): a raid that has ended
            // does not start its endgame and does not open its gate, whatever
            // the arena looks like on the tick it ended.
            if (m.Phase == MatchPhase.Ended) return;

            switch (m.Phase)
            {
                case MatchPhase.Farm:
                    if (AnyLiveCollectorInCore(w)) Activate(w, ref m);
                    break;

                case MatchPhase.DirectorActive:
                    AdvanceTowardTheGate(w, ref m);
                    break;

                // GateOpen is terminal short of Ended: "открывается и больше
                // не закрывается до конца захода" (spec §3.5). No case, and
                // deliberately no `default` either — MatchPhase has four
                // members, three of them are named above, and a fifth would
                // be a new phase whose handling is a decision, not a fall-
                // through.
            }
        }

        /// Spec §3.5/Р299: the activation condition, and the whole of it — a
        /// LIVE, NOT-YET-EXTRACTED collector whose position is in the core.
        /// Dead bodies and collectors who already left the raid are excluded
        /// deliberately: a corpse lying in the core is not a decision anybody
        /// made, and an extracted collector is not in the raid to make one.
        ///
        /// ZONELESS ARENAS ARE A LEGAL INPUT AND ARE GUARDED HERE (lesson 315,
        /// R-53). Geometry.ZoneOf reads ZoneRadius[0]/[1] with no bounds check
        /// of its own and throws a bare IndexOutOfRangeException on an arena
        /// that has neither — and this method is ZoneOf's first battle reader
        /// in Objectives. An arena without zones has no core, so it has
        /// nothing to walk into: the guard answers "no", it does not invent a
        /// substitute boundary.
        static bool AnyLiveCollectorInCore(SimulationWorld w)
        {
            ArenaSimConfig arena = w.Config.Arena;
            if (arena.ZoneRadius.Length < 2) return false;

            for (int i = 0; i < w.PlayerCount; i++)
            {
                PlayerState p = w.PlayerAt(i);
                if (!p.Alive || p.Extracted) continue;
                if (Geometry.ZoneOf(p.Pos, in arena) == Zone.Core) return true;
            }
            return false;
        }

        /// The latch, fired ON THE TRANSITION and never on the standing
        /// condition: the event announces that the early portals have just
        /// closed, which happens once per raid. It reaches everyone and
        /// carries NO position (spec §3.4, Р28's All channel — the position of
        /// whoever walked in is exactly what must not ride along).
        static void Activate(SimulationWorld w, ref MatchState m)
        {
            m.Phase = MatchPhase.DirectorActive;
            w.Emit(SimEventKind.DirectorActivated, float2.zero, 0, default, 0f);
        }

        /// DirectorActive -> GateOpen (spec §3.5): the Director is gone AND
        /// GateDelaySeconds have passed since he fell.
        ///
        /// "IS THE DIRECTOR ALIVE" IS A SCAN, NOT A FIELD (Р218) — he lives in
        /// _mobs as an ordinary MobState, and the only thing about him the
        /// match state stores is the tick he died on, because that is the only
        /// thing that cannot be derived from the world afterwards.
        ///
        /// DirectorDeathTick == 0 MEANS "NOT DEAD YET", and that sentinel is
        /// safe rather than merely conventional: TickAll increments the tick
        /// counter BEFORE any system runs, so the earliest tick this method
        /// can ever observe is 1.
        ///
        /// UNTIL Т22 SPAWNS HIM, AN ACTIVATED RAID READS AS A DIRECTOR WHO HAS
        /// ALREADY DIED, and that is by construction, not by oversight: with
        /// liveness defined as a scan, "never born" and "already dead" are the
        /// same reading, and Т22 — which spawns him on this very transition
        /// and holds the slot reserve that guarantees his birth (Р254) — is
        /// what makes the scan mean what it says.
        static void AdvanceTowardTheGate(SimulationWorld w, ref MatchState m)
        {
            if (m.DirectorDeathTick == 0)
            {
                if (DirectorAlive(w)) return;
                m.DirectorDeathTick = w.CurrentTick;
                w.Emit(SimEventKind.DirectorDied, float2.zero, 0, default, 0f);
                // Falls through into the countdown below rather than spending
                // a tick on the transition alone — same "no wasted tick"
                // shape WaveSystem's Waiting -> Active handover already has,
                // and the only shape under which a GateDelaySeconds of zero
                // means what it says.
            }

            // THE BOUNDARY IS COMPARED IN WHOLE TICKS, AND THAT IS A
            // DETERMINISM RULE, NOT A STYLE ONE (Т21, measured — see below).
            // Spec §3.5 states it as CurrentTick - DirectorDeathTick >=
            // GateDelaySeconds * TickRate, and the obvious transcription —
            // `elapsed * TickDt >= GateDelaySeconds`, one float multiply
            // against a config float — was written first and then caught by
            // its own mutation: `>` and `>=` behaved IDENTICALLY at the exact
            // boundary. The instrumented run says why. With both sides stored
            // into float locals the two are bit-equal (0x3EAAAAAB) and `>` is
            // false; written INLINE inside the `if`, the product keeps more
            // than float precision and `>` is true. Same expression, two
            // evaluation forms, two answers — and an expression whose value
            // depends on whether an intermediate was spilled to a local has no
            // place in a simulation that must reproduce bit-for-bit
            // (CRITICAL RULE 1/2, and the hash this phase feeds).
            //
            // Integers have no such freedom. The conversion happens ONCE, with
            // an explicit rounding rule — nearest whole tick, which is exact
            // for every number that matters here (the shipped 90 s is 2700
            // ticks; a fixture that states its delay as N * TickDt is N) and
            // stable against the ±1 ulp the division itself may carry, where
            // ceil would turn that same ulp into a whole extra tick.
            int delayTicks = (int)math.round(w.Config.Flow.GateDelaySeconds / SimulationWorld.TickDt);
            int elapsed = w.CurrentTick - m.DirectorDeathTick;
            if (elapsed >= delayTicks)
                m.Phase = MatchPhase.GateOpen;
        }

        static bool DirectorAlive(SimulationWorld w)
        {
            for (int i = 0; i < w.MobCount; i++)
                if (w.Mobs[i].Type == MobType.Director) return true;
            return false;
        }
    }
}
