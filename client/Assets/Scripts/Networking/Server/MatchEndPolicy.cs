using System;

namespace Ring.Networking.Server
{
    /// Why a match stopped (Stage 2 Task 40, spec §3.10/§3.11). `None` is not
    /// an outcome — it is the reading of a match that is still running, and it
    /// is what `MatchServer.Outcome` answers until one of the other two
    /// happens.
    ///
    /// VALUES ARE PINNED, AND THAT IS A WIRE CONTRACT, NOT A STYLE RULE:
    /// `MatchEndedNet.Reason` carries this as a single byte, so reordering the
    /// members would silently change the meaning of a reason already in flight
    /// between a client build and a server build compiled from different
    /// sources. `MatchLifecycleTests.MatchEndReason_ValuesAreStableOnTheWire`
    /// pins every value, the same discipline `HandshakeRefusal` carries.
    public enum MatchEndReason : byte
    {
        None = 0,
        AllPlayersDead = 1,
        MaxDurationReached = 2,
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
        /// this call just finished") and `alivePlayers` is counted AFTER that
        /// same step, disconnect-kills included — both are facts about the
        /// world as it now stands, not as it stood coming into the tick.
        ///
        /// PRIORITY IS FIXED: `AllPlayersDead` IS CHECKED FIRST. When both
        /// conditions come true on the same tick the match ended in substance,
        /// not on the timer — and the difference is observable outside the
        /// process, because the two reasons carry different exit codes (0
        /// against 4, §3.11). `AllDeadWinsOverMaxDuration` pins it.
        ///
        /// The duration boundary is `>=`: AT `maxDurationTicks` completed
        /// ticks the match is over, not one tick later.
        public MatchEndReason Evaluate(int worldTick, int alivePlayers)
        {
            // `<= 0` rather than `== 0`: a count can only reach this method
            // from a loop over the world's own players, so a negative value is
            // a caller bug — and ending the match is the safe direction to be
            // wrong in, where continuing forever is not.
            if (alivePlayers <= 0) return MatchEndReason.AllPlayersDead;
            if (worldTick >= _maxDurationTicks) return MatchEndReason.MaxDurationReached;
            return MatchEndReason.None;
        }

        /// The process exit code spec §3.11 attaches to each outcome: 0 for a
        /// match that was played out, 4 for one that exhausted
        /// `MatchMaxDurationSeconds`.
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
    }
}
