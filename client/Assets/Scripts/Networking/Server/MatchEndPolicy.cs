using System;
using Ring.Simulation.Core;

namespace Ring.Networking.Server
{
    /// Why a match stopped (Stage 2 Task 40, spec §3.10/§3.11). `None` is not
    /// an outcome — it is the reading of a match that is still running, and it
    /// is what `MatchServer.Outcome` answers until one of the other members
    /// happens.
    ///
    /// VALUES ARE PINNED, AND THAT IS A WIRE CONTRACT, NOT A STYLE RULE:
    /// `MatchEndedNet.Reason` carries this as a single byte, so reordering the
    /// members would silently change the meaning of a reason already in flight
    /// between a client build and a server build compiled from different
    /// sources. `MatchLifecycleTests.MatchEndReason_ValuesAreStableOnTheWire`
    /// pins every value that existed before Stage 3, the same discipline
    /// `HandshakeRefusal` carries; `MatchFlowTests.
    /// EndReasonValues_AreStableOnTheWire` (Stage 3 Task 1) pins the full set
    /// 0-3, including the member that task appended, `AllPlayersResolved`.
    public enum MatchEndReason : byte
    {
        None = 0,
        AllPlayersDead = 1,
        MaxDurationReached = 2,
        /// Stage 3 Task 1 (spec §3.10, errata E-1/E-6 D-I2): a run every
        /// player left by dying, extracting, or some mix of the two — added
        /// at the END of this pinned enumeration (Interfaces), same
        /// append-only discipline the rest of this doc requires, so no value
        /// already in flight changes meaning.
        AllPlayersResolved = 3,
    }

    /// How the raid ended for ONE collector (Stage 3 Т24, spec §3.10) — the
    /// per-player half of the result record, carried on MatchEndedNet as a
    /// single byte.
    ///
    /// VALUES ARE PINNED, AND THAT IS A WIRE CONTRACT, exactly as for
    /// MatchEndReason above: reordering the members would silently change the
    /// meaning of a result already in flight between a client build and a
    /// server build compiled from different sources.
    /// ResultsTests.MatchOutcome_ValuesAreStableOnTheWire pins all five.
    ///
    /// Died IS ZERO ON PURPOSE, and it is the only member that can be reached
    /// by a zeroed array: "the collector did not come back" is the safe
    /// direction for a default to point in — the opposite mistake would credit
    /// somebody with an extraction that never happened.
    public enum MatchOutcome : byte
    {
        Died = 0,
        ExtractedEarly = 1,
        ExtractedCore = 2,
        Disconnected = 3,
        Stranded = 4,
    }

    /// Stage 2 Task 40 (spec §3.10 "end of match" and Р43, §3.11's exit
    /// codes): the decision of WHEN a match is over and WHAT the process
    /// should exit with — a pure core beside `MatchServer`, the same split
    /// `InputStarvation`/`EffectiveInputBatch` already occupy in this folder
    /// and `HandshakeDecision` occupies beside `MatchHandshake`.
    ///
    /// EVERYTHING THAT DECIDES IS HERE; THE WIRING ONLY CALLS. `MatchServer`
    /// gathers the two inputs (the world's own tick, and how many players are
    /// still alive after this tick stepped), asks once, and executes the
    /// answer. This is not an aesthetic split: a decision left inline in the
    /// FishNet-touching class cannot be reached by an EditMode test at all,
    /// and a rule no test can reach is a rule no mutation can be caught in.
    /// `ShouldKillOnDisconnect` below is the extreme case of the same
    /// principle — a one-line conjunction that lives here precisely BECAUSE
    /// it would be untestable anywhere else.
    ///
    /// THE LIMIT IS COUNTED IN WORLD TICKS, NOT IN WALL-CLOCK SECONDS. Three
    /// reasons, in order of weight: the world tick is the domain the match
    /// actually lives in, and the same domain the other half of this decision
    /// (`alivePlayers` after `TickAll`) is measured in; a tick count is
    /// exactly testable without a clock; and `MatchServer` already owns one
    /// wall-clock axis for a different purpose (its `Stopwatch`, which
    /// measures what a tick COST), so a second one inside the same object
    /// would be two answers to "what time is it". The conversion
    /// `MatchMaxDurationSeconds * TickRate` is the CALLER's arithmetic (the
    /// Task 41 bootstrap, which is the one node holding `NetConfig`) and a
    /// finished number arrives here.
    ///
    /// SO THE LIMIT IS SIMULATED TIME, NOT WALL TIME, AND UNDER A SUSTAINED
    /// TICK DROP THE TWO DIVERGE — said plainly rather than left for a reader
    /// to discover: a server that cannot keep 30 Hz reaches
    /// `maxDurationTicks` later than `MatchMaxDurationSeconds` seconds after
    /// the match began, in proportion to how far behind it fell. For the
    /// arena's purpose (a bound on how long one container may live) that is
    /// the more meaningful of the two — the limit exists to cap the MATCH,
    /// and a match's length is its ticks.
    public sealed class MatchEndPolicy
    {
        readonly int _maxDurationTicks;

        /// `maxDurationTicks` must be at least 1: a limit of zero would end
        /// every match on the tick it started, and a negative one names
        /// nothing at all. Both are bugs in the caller's own
        /// seconds-to-ticks conversion, and a bug in a conversion done once
        /// at startup should fail at startup, not silently truncate the first
        /// match to nothing.
        public MatchEndPolicy(int maxDurationTicks)
        {
            if (maxDurationTicks < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDurationTicks), maxDurationTicks,
                    "MatchEndPolicy: a match's duration limit must be at least one tick "
                    + "(MatchMaxDurationSeconds * TickRate, converted by the caller).");
            }
            _maxDurationTicks = maxDurationTicks;
        }

        /// The verdict for the tick that just finished. `worldTick` is the
        /// POST-`TickAll` reading of `SimulationWorld.CurrentTick` ("the tick
        /// this call just finished"); `alivePlayers`/`activePlayers`/
        /// `anyExtracted` are all counted AFTER that same step, disconnect-
        /// kills included — every one of them is a fact about the world as it
        /// now stands, not as it stood coming into the tick.
        ///
        /// Stage 3 Task 1 (spec §3.10, errata E-1/E-6 D-I2) adds the two
        /// EXTRACTION-aware parameters. `activePlayers` is "alive AND not yet
        /// extracted" (spec's own definition of "active"; `MatchServer`'s own
        /// count); `anyExtracted` is whether at least one player left through
        /// a portal or the gate this match. Both are inert on every
        /// production path until later Ф1 tasks give `PlayerState.Extracted`
        /// a writer — until then `activePlayers == alivePlayers` and
        /// `anyExtracted` is always false, so this method's observable
        /// behavior on a real match is unchanged from before this task.
        ///
        /// PRIORITY IS FIXED, AND `AllPlayersResolved` IS CHECKED FIRST NOW
        /// (Stage 3 Task 1 — the priority flip errata E-1 mandates over the
        /// plan's original ordering). A run where the last active player just
        /// extracted, on the same tick some other player happened to die,
        /// ended in success, not in a wipe — `Resolved_OutranksAllDead_
        /// WhenSomeoneExtracted` (`MatchFlowTests`) pins it, the same way
        /// `AllDeadWinsOverMaxDuration` (`MatchLifecycleTests`) pins the
        /// UNCHANGED second priority: `AllPlayersDead` still beats
        /// `MaxDurationReached` when both are true on the same tick, because
        /// the two carry different exit codes (0 against 4, §3.11) and a
        /// match that ended in substance must report that, not the timer.
        ///
        /// The duration boundary is `>=`: AT `maxDurationTicks` completed
        /// ticks the match is over, not one tick later.
        public MatchEndReason Evaluate(int worldTick, int alivePlayers, int activePlayers, bool anyExtracted)
        {
            // Resolved beats everything else (see the priority paragraph
            // above): nobody is left to act, and at least one of the players
            // who left did so by extracting rather than dying.
            if (activePlayers <= 0 && anyExtracted) return MatchEndReason.AllPlayersResolved;
            // `<= 0` rather than `== 0`: a count can only reach this method
            // from a loop over the world's own players, so a negative value is
            // a caller bug — and ending the match is the safe direction to be
            // wrong in, where continuing forever is not.
            if (alivePlayers <= 0) return MatchEndReason.AllPlayersDead;
            if (worldTick >= _maxDurationTicks) return MatchEndReason.MaxDurationReached;
            return MatchEndReason.None;
        }

        /// The process exit code spec §3.11 attaches to each outcome: 0 for a
        /// match that was played out — a wipe or a fully resolved run both
        /// count as "played out" (Stage 3 Task 1 adds `AllPlayersResolved`
        /// beside `AllPlayersDead` on the same code, spec §3.11) — and 4 for
        /// one that exhausted `MatchMaxDurationSeconds`.
        ///
        /// `None` THROWS RATHER THAN ANSWERING. Asking a RUNNING match for its
        /// exit code is a bug in the caller — there is no code that means
        /// "not finished", and the plausible-looking 0 would report a match
        /// that never ended as a match played to its end.
        public static int ExitCodeFor(MatchEndReason reason)
        {
            switch (reason)
            {
                case MatchEndReason.AllPlayersDead: return 0;
                case MatchEndReason.AllPlayersResolved: return 0;
                case MatchEndReason.MaxDurationReached: return 4;
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason), reason,
                        "MatchEndPolicy.ExitCodeFor: a match that has not ended (None) has no "
                        + "exit code — ask MatchServer.Outcome first.");
            }
        }

        /// Spec §3.10, "player disconnect -> `KillPlayerNoDamage`": whether
        /// this tick must kill the player behind a connection that is gone.
        /// `connectionActive` is FishNet's own `NetworkConnection.IsActive`
        /// (its "not disconnected, not disconnecting" predicate) and
        /// `playerAlive` is that slot's own `PlayerState.Alive`.
        ///
        /// The `playerAlive` half is what makes the caller's loop idempotent:
        /// a connection stays gone for the rest of the match, so without it
        /// the kill would be re-issued every tick. (`KillPlayerNoDamage` is
        /// itself guarded, so the second call would be a no-op — but a
        /// predicate that says "kill" about a corpse, every tick, forever, is
        /// a predicate that means nothing.)
        public static bool ShouldKillOnDisconnect(bool connectionActive, bool playerAlive)
            => !connectionActive && playerAlive;

        /// Spec §3.10: how the raid ended for the collector in one slot.
        /// Beside ShouldKillOnDisconnect for the same reason that predicate
        /// lives here rather than inline in MatchServer — it is a decision,
        /// it is pure, and a decision no EditMode test can reach is a decision
        /// no mutation can be caught in.
        ///
        /// EXTRACTION IS CHECKED FIRST, AND THE ORDER IS THE MEANING. A
        /// collector who walked out is not Alive either — ExtractionSystem
        /// clears the body deliberately, because leaving is NOT dying (Р223)
        /// and there must be no corpse to loot — so any test of Alive ahead
        /// of Extracted would report every man who got out as a casualty.
        /// `extractKind` is PlayerState.ExtractKind's own encoding, compared
        /// against the constants declared with that field rather than against
        /// a literal, so the two ends of the wire cannot drift apart.
        ///
        /// THE DISCONNECT IS THE SERVER'S MEMORY, NOT THE WORLD'S (Р271). A
        /// dropped connection kills through KillPlayerNoDamage, which is the
        /// same seam and leaves the same corpse as any other death: the world
        /// genuinely cannot tell the two apart, and should not be taught to.
        /// `disconnectKilled` is MatchServer's own per-slot record of having
        /// done it, and what it buys is diagnostic — a line in the log and a
        /// column in the future meta, not different loot on the ground.
        ///
        /// STRANDED IS THE OUTCOME THE PLAN DID NOT NAME (owner decision
        /// R-193). MaxDurationReached is the only end that can leave
        /// collectors ALIVE in the arena — a raid nobody walks into the core
        /// is legitimate (spec §3.5) and simply runs its 900 s out — and the
        /// plan's four members had nothing to say about that man. In the
        /// world it is not an edge case but the AI winning: the factory
        /// closes the communication corridor the operator's link to his shell
        /// runs through, which is what its waves have been buying time for
        /// since it detected the intrusion, and the shell is lost where it
        /// stands with everything in its backpack. Calling him Died would be
        /// a lie about a living body; crediting him with loot he never
        /// carried out would be the opposite lie. Naming the state closes
        /// both — see MatchServer.CreditsCarriedOut for the second half.
        public static MatchOutcome OutcomeFor(bool alive, bool extracted, byte extractKind,
            bool disconnectKilled)
        {
            if (extracted)
            {
                return extractKind == ExtractKinds.Gate
                    ? MatchOutcome.ExtractedCore
                    : MatchOutcome.ExtractedEarly;
            }
            if (disconnectKilled) return MatchOutcome.Disconnected;
            if (!alive) return MatchOutcome.Died;
            return MatchOutcome.Stranded;
        }
    }
}
