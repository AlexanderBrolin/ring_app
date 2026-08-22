using FishNet.Broadcast;

namespace Ring.Networking.Protocol
{
    /// Server -> every client, Channel.Reliable: the raid's PUBLIC scoreboard
    /// (Stage 3 Т34, spec §3.10/§3.11, Р270).
    ///
    /// THE SECOND MESSAGE, AND DELIBERATELY NOT THE FIRST ONE BROADCAST.
    /// `MatchEndedNet` is personal: it carries a collector's accuracy, his
    /// damage taken, his shots and his kills, and `MatchServer` builds one per
    /// connection out of that connection's own slot. Sending THAT to everybody
    /// would hand each client the others' shooting accuracy for nothing, which
    /// is the exact trade Р270 refuses; sending nothing at all would leave the
    /// results screen unable to say who else got out. So the raid learns three
    /// things about each of its members and no more: which seat, how his raid
    /// ended, and what he carried out of the factory.
    ///
    /// THE SEAT IS THE INDEX, NOT A FIELD. Both arrays are one entry per seat
    /// in seat order, so "which player is this row about" has exactly one
    /// answer and cannot disagree with itself. A parallel `Slot[]` would be a
    /// second statement of the same fact, and the failure mode of two
    /// statements is that they drift.
    ///
    /// WHAT IS NOT HERE, and the reason each one is not:
    ///   * accuracy, kills, shots, damage — Р270, private to their collector;
    ///   * the LOOT ITEMS — the board says what a raid was WORTH, not what was
    ///     in somebody's pack: an item list is a shopping list for whoever
    ///     survived, and the credits already answer "did he do well";
    ///   * a player id or name — the meta owns identity (Э5), and a slot is
    ///     what this protocol has always addressed players by.
    ///
    /// STRUCT IS MANDATORY, NOT STYLISTIC — `IBroadcast` is an empty marker
    /// and every FishNet broadcast API is constrained to `where T : struct`,
    /// so a class here compiles and breaks only at the send.
    /// `ResultsTests.ResultsNet_IsABroadcastStruct` moves that failure back
    /// here.
    ///
    /// NO QUANTIZATION, DELIBERATELY, for the reason `MatchEndedNet`'s own doc
    /// gives: this travels once per match on the Reliable channel, and the
    /// snapshot byte budget has nothing to say about a lifecycle message.
    public struct MatchResultsNet : IBroadcast
    {
        /// `MatchEndReason` as a byte — the same reason the personal message
        /// carries it: the board says how the RAID ended as well as how each
        /// collector did.
        public byte Reason;

        /// The epoch of the match that just ended (`ushort`, the one width
        /// this protocol uses for the concept, Р163а), so a client can discard
        /// a board belonging to a match it is no longer in.
        public ushort MatchEpoch;

        /// The world tick the match ended on.
        public uint FinalTick;

        /// `MatchOutcome` as a byte, one entry per seat in seat order.
        public byte[] Outcome;

        /// What each seat carried out of the factory, in credits, in the same
        /// seat order.
        public int[] CreditsTotal;
    }
}
