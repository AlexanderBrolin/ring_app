using Ring.Simulation.AI;
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
    /// takes effect from the next one), and — since Т23 landed the extraction
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
                {
                    // ONE liveness scan per tick, shared by both readers below
                    // (rule 2): the top-up runs only while he stands, and the
                    // countdown starts only once he does not.
                    bool directorAlive = DirectorAlive(w);
                    if (directorAlive) TopUpRetinueOnItsPeriod(w);
                    AdvanceTowardTheGate(w, ref m, directorAlive);
                    break;
                }

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

            // Stage 3 Т22 (spec §3.4): THE DIRECTOR IS BORN ON THIS VERY
            // TRANSITION, and the spawn is UNCONDITIONAL — the arena center,
            // no placement search, no rejection. A boss who failed to appear
            // "because the middle was crowded" is precisely the outcome Р254
            // forbids: the phase would sit in DirectorActive, the liveness
            // scan would answer "dead", and the gate would open off a fight
            // nobody had. His slot is guaranteed by WaveSystem's reserve, not
            // by luck (coordinator R-181's own validator rule ties the two
            // numbers together: DirectorReserveSlots >= 1 + RetinueCount).
            w.SpawnMob(MobType.Director, float2.zero);
            TopUpRetinue(w);

            w.Emit(SimEventKind.DirectorActivated, float2.zero, 0, default, 0f);
        }

        /// Spec §3.3 Р215: the retinue is topped back up to RetinueCount ON ITS
        /// OWN PERIOD, RetinueRespawnSeconds, for as long as the Director
        /// stands.
        ///
        /// WHY A MODULO OF THE RAID'S OWN TICK AND NOT A STORED TIMER
        /// (coordinator R-180). MatchState carries exactly two fields and may
        /// not carry a third: Р219a bars derived values from the phase state,
        /// and errata E-1 bars a new hashable field outright — a single extra
        /// StateHash64.Add moves both goldens even at value zero, and all three
        /// sanctioned re-pins are spent. So "is it time" has to be a function
        /// of the world, and the only clock the world keeps is CurrentTick.
        /// The period is exact; what it is NOT is phase-aligned to the
        /// activation — a fallen retinue slot is refilled somewhere within one
        /// period rather than exactly one period later. That is the accepted
        /// price, and it is the small half of the trade: the alternative that
        /// keeps perfect phase costs a fourth re-pin of the goldens, and the
        /// alternative that needs no clock at all (refill the moment a slot
        /// opens) would make the fight endless and delete a shipped number.
        ///
        /// A period of zero or less cannot be divided by, and this is where a
        /// hand-built fixture that skipped SimConfigBuilder can land: the
        /// validator refuses RetinueRespawnSeconds <= 0 for the real game
        /// (coordinator R-181), and a world assembled around that refusal
        /// simply gets no top-up rather than a DivideByZeroException.
        static void TopUpRetinueOnItsPeriod(SimulationWorld w)
        {
            // WHOLE TICKS, converted ONCE (R-178/lesson 348): a seconds-side
            // comparison here would be the same defect Т21's own gate boundary
            // was caught carrying — an answer that depends on whether an
            // intermediate spilled to a float local has no place in state that
            // feeds StateHash.
            int periodTicks = SimulationWorld.TicksFromSeconds(w.Config.Flow.RetinueRespawnSeconds);
            if (periodTicks <= 0) return;
            if (w.CurrentTick % periodTicks != 0) return;
            TopUpRetinue(w);
        }

        /// Fills the retinue back up to Flow.RetinueCount. THE SHORTFALL IS THE
        /// DEBT, and it is derived, never stored (Р218's own shape): "retinue"
        /// is not a flag on a mob — Р215 refuses one outright — it is the live
        /// elites standing in the core, which is exactly what this counts.
        ///
        /// A failed placement therefore needs no bookkeeping either: the
        /// shortfall is still a shortfall on the next period. The cap branch
        /// that Р254 asks to be retried "next tick, exactly like wave debt"
        /// is unreachable — but the arithmetic alone was never what made it so,
        /// and saying that it was is the correction this gate had to make
        /// (Ф5 gate, review A-5). R-181's sum — wave ceiling (MaxMobs −
        /// reserve) + the Director + a full retinue = MaxMobs — only closes
        /// while the number of elites in the core is BOUNDED by RetinueCount.
        /// Until the retinue was leashed, nothing bounded it: this method
        /// counts elites STANDING in the core, a collector could walk them
        /// out, and the next period bred replacements without limit — elites
        /// past the cap, wave slots eaten and this very branch reached. What
        /// holds the sum now is MobAiSystem.LeashesToCore (owner decision
        /// R-200), which keeps the core's elite in the core once the endgame
        /// begins, so the count this method takes and the count the validator
        /// reasons about are finally the same number.
        ///
        /// THE BRANCH THEREFORE HAS NO WITNESS AND NO MUTATION CAN KILL IT —
        /// said out loud rather than left for a reviewer: it is a defensive
        /// return whose premise the leash and the validator jointly forbid.
        static void TopUpRetinue(SimulationWorld w)
        {
            ArenaSimConfig arena = w.Config.Arena;
            // Zoneless arenas are a legal input (lesson 315) and have no core
            // to guard — the same guard AnyLiveCollectorInCore states above,
            // for the same reason and in the same form.
            if (arena.ZoneRadius.Length < 2) return;

            int want = w.Config.Flow.RetinueCount;
            // One copy of the wave section per call (the same "SimulationWorld.
            // Config is a property, not a field" rule every other caller here
            // obeys) — the placement home takes it by `in`.
            WaveSimConfig wave = w.Config.Wave;
            for (int have = LiveRetinueCount(w, in arena); have < want; have++)
            {
                // Coordinator R-183: the retinue is placed by the SAME home a
                // wave places its own mobs through — same spawn ring, same
                // rejection rules (distance to the nearest player, live-mob
                // overlap, obstacles, walls, arcs). Elites, because a retinue
                // member IS an elite (Р215).
                if (!WaveSystem.TryFindMobSpawnPos(w, in wave, Zone.Core, MobType.Elite,
                        out float2 pos))
                    return;
                if (w.SpawnMob(MobType.Elite, pos) < 0) return;
            }
        }

        /// The live retinue: elites standing in the core (Р215 — no stored
        /// mark exists or may exist, so this is the only reading there is).
        /// Callers guard the zoneless case before this runs.
        static int LiveRetinueCount(SimulationWorld w, in ArenaSimConfig arena)
        {
            int n = 0;
            for (int i = 0; i < w.MobCount; i++)
            {
                if (w.Mobs[i].Type != MobType.Elite) continue;
                if (Geometry.ZoneOf(w.Mobs[i].Pos, in arena) == Zone.Core) n++;
            }
            return n;
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
        /// SINCE Т22 THE SCAN MEANS WHAT IT SAYS: the Director is spawned on
        /// the activating transition itself and his slot is guaranteed by
        /// WaveSystem's standing reserve (Р254), so "no Director in _mobs"
        /// during DirectorActive can only mean he fell. Before that task an
        /// activated raid read as a Director who had already died — with
        /// liveness defined as a scan, "never born" and "already dead" are the
        /// same reading, which is why the two had to arrive in one phase.
        static void AdvanceTowardTheGate(SimulationWorld w, ref MatchState m, bool directorAlive)
        {
            if (m.DirectorDeathTick == 0)
            {
                if (directorAlive) return;
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
            int delayTicks = SimulationWorld.TicksFromSeconds(w.Config.Flow.GateDelaySeconds);
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
