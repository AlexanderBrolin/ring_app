using System.Text;
using Ring.Networking.Protocol;
using Ring.Networking.Server;

namespace Ring.Presentation.Net
{
    /// The end-of-raid scoreboard, turned from a wire message into the lines a
    /// screen draws (Stage 3 Т34, spec §3.10/§3.11).
    ///
    /// IT LIVES ON THIS SIDE OF Р180, AND THAT IS WHY IT EXISTS AT ALL.
    /// `Presentation.asmdef` references `Ring.Simulation` and `Ring.Data` and
    /// deliberately not `Ring.Networking`, so `MatchResultsNet` and
    /// `MatchOutcome` are types the drawing layer cannot name. Handing it a
    /// byte instead would put a second copy of the outcome domain in
    /// `Presentation` — a table that would keep compiling, and keep printing
    /// the wrong word, on the day a sixth outcome is added. So the crossing
    /// happens once, here, in the assembly that is allowed to see both, and
    /// what reaches the screen is text.
    ///
    /// PURE, AND FOR THE USUAL REASON. `NetworkSimBackend` cannot be stood up
    /// in an EditMode test (bd `app-xkir`: it requires a live
    /// `NetworkManager`), so a board formatted inside it would be a screen
    /// nobody could check. This is the decidable part lifted out where a test
    /// reaches it — the same move `ClientEventDecoder` and `ClientFrameDecoder`
    /// made before it.
    public static class MatchResultsBoard
    {
        /// The raid's own words for the five endings (ADR-003 §9). A `switch`
        /// over a domain with a THROWING default (R-237): a sixth outcome must
        /// be given its word here rather than silently printed as whatever the
        /// fifth one says.
        public static string WordFor(MatchOutcome outcome) => outcome switch
        {
            MatchOutcome.Died => "ПОГИБ",
            MatchOutcome.ExtractedEarly => "УШЁЛ ПОРТАЛОМ",
            MatchOutcome.ExtractedCore => "УШЁЛ СТВОРОМ",
            MatchOutcome.Disconnected => "СВЯЗЬ ПОТЕРЯНА",
            MatchOutcome.Stranded => "ОСТАЛСЯ В ЦЕХЕ",
            _ => throw new System.ArgumentOutOfRangeException(nameof(outcome), outcome,
                "MatchResultsBoard.WordFor: every MatchOutcome owns a word on the board."),
        };

        /// One line per seat, this client's own marked.
        ///
        /// THE SEAT IS THE INDEX, and the board says so out loud: the message
        /// carries no slot field precisely so the two cannot disagree
        /// (`MatchResultsNet`'s own doc), and the number a human reads is
        /// one-based because a seat is a place at a table, not an offset.
        ///
        /// A SHORT BOARD IS DRAWN SHORT RATHER THAN REFUSED. The two arrays are
        /// written together by one builder, but they arrive over a wire, and a
        /// decoder that never throws (Р82) can hand this a pair of different
        /// lengths. Drawing the seats BOTH arrays describe is the honest
        /// answer for a message that lost bytes; throwing would take the whole
        /// screen away over a cosmetic disagreement.
        ///
        /// `null` FOR "NO BOARD YET" rather than an empty string, because the
        /// caller has to tell "the raid has not ended" from "the raid ended
        /// with nobody in it".
        public static string Format(in MatchResultsNet results, int localSlot)
        {
            byte[] outcome = results.Outcome;
            int[] credits = results.CreditsTotal;
            if (outcome == null || credits == null) return null;

            int seats = outcome.Length < credits.Length ? outcome.Length : credits.Length;
            var text = new StringBuilder();
            for (int slot = 0; slot < seats; slot++)
            {
                if (text.Length > 0) text.Append('\n');
                bool mine = slot == localSlot;
                // The local row is marked with a character rather than a color,
                // so the mark survives a screenshot, a colorblind reader and a
                // log paste — the same reason the spectate label spells the
                // seat out instead of tinting the HP bar.
                text.Append(mine ? "▶ " : "  ");
                text.Append("СБОРЩИК ").Append(slot + 1).Append(" · ");
                text.Append(WordFor((MatchOutcome)outcome[slot]));
                text.Append(" · ").Append(credits[slot]).Append(" кр");
            }

            return text.ToString();
        }
    }
}
