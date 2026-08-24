using FishNet.Broadcast;

namespace Ring.Networking.Protocol
{
    /// Server -> client, Channel.Reliable (spec §3.7 table Р27, "Lifecycle"):
    /// THIS collector's raid is over — here are his numbers (bd `app-qw01`).
    ///
    /// WHY IT EXISTS AT ALL. A raid ends for one collector long before the
    /// MATCH ends for everyone: he dies, or he walks out through a portal,
    /// and then he waits for the others. Until this message his own counters
    /// travelled nowhere — `MatchEndedNet` is sent at the END OF THE MATCH,
    /// and the per-frame snapshot deliberately carries no stats block at all
    /// (`NetworkSimBackend.HasMatchStats`' own doc: these are not per-frame
    /// quantities and the per-frame budget, Р146, is spent on the ones that
    /// are). So the end-of-raid screen printed seven dashes at a collector
    /// who had just finished his raid — measured on the В2 playtest at
    /// nineteen seconds for one who walked out and over four minutes for one
    /// who died early.
    ///
    /// A SIXTH LIFE-CYCLE MESSAGE RATHER THAN A NEW MEANING FOR AN OLD ONE.
    /// Sending `MatchEndedNet` early would have cost no new type, but it
    /// would have told the client's link that the MATCH had ended — the phase
    /// that shuts spectating down and refuses every later message as
    /// `AlreadyEnded`. Two different facts, two different messages, the same
    /// split `MatchResultsNet` already made against this one.
    ///
    /// IT CARRIES THE TALLY WHOLE, NOT A COPY OF ITS FIELDS. `MatchEndedNet`
    /// spells out eighteen numbers and the reasoning for each; restating them
    /// here would be a second place for a swapped pair to hide (the very
    /// defect `EndedNetFor`'s own doc lifts that function out of the wiring
    /// to prevent) and a second thing to keep in step for every future field.
    /// The server fills it through the SAME `MatchServer.EndedNetFor`, and
    /// the client reads it through the SAME `FinalStats` converters.
    ///
    /// ⚠ `Tally.Reason` IS `MatchEndReason.None`, AND THAT IS THE CONTRACT:
    /// the match has NOT ended when this is sent. `None` is the documented
    /// zero of that enum and is otherwise never put on the wire — `EndMatch`
    /// always has a real reason — so it is free to mean exactly this here.
    /// `Tally.FinalTick` is likewise the tick THIS collector's raid ended on,
    /// not the match's.
    ///
    /// STRUCT IS MANDATORY, NOT STYLISTIC — same reasoning as
    /// `MatchEndedNet`/`SnapshotBroadcast`/`HandshakeNet`: every FishNet
    /// broadcast API is constrained to `where T : struct, IBroadcast`, and
    /// `IBroadcast` is an empty marker, so a `class` here compiles fine and
    /// breaks only at the generic `Broadcast<T>` call site inside
    /// `MatchServer`. `MatchLifecycleTests.LifecycleStructs_AreStructsImplementingIBroadcast`
    /// moves that failure back here.
    public struct RaidEndedNet : IBroadcast
    {
        /// This collector's own numbers, in the shape the end-of-match
        /// message already defines. See the type doc above for why the tally
        /// is embedded rather than restated, and what `Reason`/`FinalTick`
        /// mean inside it here.
        public MatchEndedNet Tally;
    }
}
