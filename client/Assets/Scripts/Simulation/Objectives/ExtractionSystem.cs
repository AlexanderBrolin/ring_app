using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Objectives
{
    /// The way out of a raid (spec §3.5, Р221/Р222/Р223, Stage 3 Т23): the
    /// exits are STATIC GEOMETRY of the config, not entities of the state —
    /// ArenaSimConfig carries their positions, radius and kinds, the client
    /// draws them from its own verified copy, and the only things that ever
    /// travel are whether an exit is open (derived from the phase) and how far
    /// a collector's own channel has run.
    ///
    /// RUNS BETWEEN THE LOOT CHANNEL AND ContainerStore/PickupSystem, and that
    /// is a rule (Р256 п.1, R-2's canonical tail): after combat, so a blow
    /// landed this tick has already cancelled what it should; before
    /// MatchFlowSystem, so a collector who completes his channel on the very
    /// tick a companion steps into the core still gets out — the portals close
    /// from the NEXT tick. Reversed, that same collector would be caught by a
    /// door that shut retroactively.
    ///
    /// NO NEW STATE ARRIVES WITH THIS TASK (errata E-1): ExtractTimer and
    /// ExtractKind were declared in Т1 and entered StateHash at the sanctioned
    /// Т6 re-pin, precisely so the behavior could land later without moving a
    /// golden. What this file adds is behavior, and the ONE write it makes to
    /// anything else goes through the same seams death already uses.
    internal static class ExtractionSystem
    {
        public static void Update(SimulationWorld w)
        {
            ArenaSimConfig arena = w.Config.Arena;
            // An arena with no exits is a legal input — every fixture that
            // predates Т12 is one — and it simply has no way out (same shape
            // as MatchFlowSystem's zoneless guard, stated before anything
            // indexes the arrays).
            if (arena.ExtractPos == null || arena.ExtractPos.Length == 0) return;

            MatchState match = w.Match;
            float channelSeconds = w.Config.Flow.ExtractChannelSeconds;

            for (int i = 0; i < w.PlayerCount; i++)
            {
                ref PlayerState p = ref w.PlayerRef(i);
                // A corpse and a man already gone are both out of the raid.
                // Neither is written to: death cleared the timer through
                // AbortChannels, extraction through ClearCombatTimers, and
                // writing a zero over a zero would only be noise in a field
                // that feeds the state hash.
                if (!p.Alive || p.Extracted) continue;

                int exit = OpenExitUnderfoot(in arena, in match, p.Pos);
                if (exit < 0)
                {
                    // Spec §3.5 Р222: outside an OPEN exit the channel is
                    // ZEROED, not paused — progress is never banked, and that
                    // includes the case where the exit itself shut under a
                    // collector standing still (the first man into the core
                    // locks the door on the other two, Р299).
                    p.ExtractTimer = 0f;
                    continue;
                }

                p.ExtractTimer += SimulationWorld.TickDt;

                // THE COMPLETION BOUNDARY IS COMPARED IN WHOLE TICKS, AND THAT
                // IS THE SAME DETERMINISM RULE Т21 PAID FOR (R-178/lesson 348),
                // in its second form — caught here by a test rather than by a
                // mutation. Spec §3.5 states the channel as a SUM ("taймер
                // растёт на TickDt… при ExtractTimer >= ExtractChannelSeconds"),
                // and the obvious transcription `p.ExtractTimer >=
                // channelSeconds` is wrong for a measured reason: a sum of six
                // TickDt is 0.2f while six times TickDt is 0.20000002f, so the
                // channel would finish a whole tick LATE — and at the shipped
                // 20 s (600 ticks) which way it lands is not predictable by
                // reading the code. The neighbouring channels (LootOps'
                // transfer, the repair kit) dodge this by counting DOWN, where
                // subtraction of near-equal floats is exact; this one cannot
                // count down, because it must reset to zero the instant a
                // collector steps out or takes a hit.
                //
                // Converting BOTH sides to whole ticks once removes the freedom
                // entirely: the division is far more accurate than half a tick
                // at any length that matters, so rounding is exact for the
                // shipped number and for every fixture stated as N * TickDt.
                int elapsedTicks = (int)math.round(p.ExtractTimer / SimulationWorld.TickDt);
                int channelTicks = (int)math.round(channelSeconds / SimulationWorld.TickDt);
                if (elapsedTicks < channelTicks) continue;

                Extract(w, i, ref p, (ExitKind)arena.ExtractKind[exit], arena.ExtractPos[exit]);
            }
        }

        /// Which exit a collector is standing in, or -1. Spec §3.5 Р222: the
        /// exit must be OPEN and the body within ExtractRadius of it. The
        /// search is over exits rather than over zones — an exit's own kind is
        /// what decides whether it is open (below), and its zone is data the
        /// client draws with.
        static int OpenExitUnderfoot(in ArenaSimConfig arena, in MatchState match, float2 pos)
        {
            float radiusSq = arena.ExtractRadius * arena.ExtractRadius;
            for (int e = 0; e < arena.ExtractPos.Length; e++)
            {
                if (!IsExitOpen(in match, arena.ExtractKind[e])) continue;
                if (math.lengthsq(pos - arena.ExtractPos[e]) <= radiusSq) return e;
            }
            return -1;
        }

        /// THE WHOLE OPENNESS RULE, IN ONE PLACE (spec §3.5's own table):
        /// the early portals are open while the raid still farms and shut for
        /// good the moment the Director wakes; the gate is shut until he falls
        /// and the window of sharing elapses, and then stays open to the end.
        /// A raid that has ENDED has no way out of it at all — both readings
        /// fall out of the two comparisons below rather than needing a third
        /// rule.
        public static bool IsExitOpen(in MatchState match, byte exitKind) => (ExitKind)exitKind switch
        {
            ExitKind.Portal => match.Phase == MatchPhase.Farm,
            ExitKind.Gate => match.Phase == MatchPhase.GateOpen,
            _ => throw new System.ArgumentOutOfRangeException(nameof(exitKind), exitKind,
                "IsExitOpen: unknown exit kind"),
        };

        /// He is out (spec §3.5 Р222/Р223): NOT a death — no corpse, no
        /// backpack on the ground, nothing for anyone to loot, and
        /// MatchEndPolicy will count him as resolved rather than as a wipe.
        /// The timers are cleared through the SAME home KillPlayer uses
        /// (errata E-6/C-I9), so "a body that has left the fight reads clean"
        /// is stated once for both ways of leaving it.
        static void Extract(SimulationWorld w, int index, ref PlayerState p, ExitKind kind, float2 exitPos)
        {
            SimulationWorld.ClearCombatTimers(ref p);
            p.Alive = false;
            p.Extracted = true;
            // PlayerState.ExtractKind is its OWN encoding (Т1's doc: 0 = not
            // extracted, 1 = early portal, 2 = gate) — deliberately not the
            // raw ArenaSimConfig.ExtractKind byte, which starts at 0 for a
            // portal and would make "not extracted" and "left by portal" the
            // same value. Mapped explicitly rather than by arithmetic, so the
            // two encodings can never drift into each other silently.
            p.ExtractKind = kind == ExitKind.Gate ? (byte)2 : (byte)1;
            w.Emit(SimEventKind.PlayerExtracted, exitPos, 0, default, 0f, playerIndex: (byte)index);
        }
    }
}
