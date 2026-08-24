using Ring.Networking.Protocol;
using Ring.Simulation.Core;

namespace Ring.Presentation.Net
{
    /// The end-of-raid counters, turned from the wire's flat fields back into
    /// the two structs the results screen already knows how to print (fix
    /// round of gate Ф7, review A-2).
    ///
    /// WHY THIS EXISTS AT ALL. `MatchEndedNet` has carried a collector's own
    /// kills, accuracy, dashes and damage since Stage 2 Task 40, and Т24 added
    /// his loot and credits to it — and nothing ever read it. The results
    /// screen printed dashes instead, on the honest reasoning of its day: the
    /// per-frame protocol carries no stats block, so `RenderSnapshot.Stats` is
    /// `BeginSlot`'s cleared zeros and printing them would be "a complete,
    /// plausible and permanent lie". That reasoning stopped covering the case
    /// the moment the numbers began ARRIVING by another route: with Т34's board
    /// beside them, six dashes over a working scoreboard is the absurdity, not
    /// the caution.
    ///
    /// SIMULATION STRUCTS OUT, WIRE STRUCT IN, and that is what keeps Р180
    /// intact: `MatchStats` and `WorldStats` live in `Ring.Simulation.Core`,
    /// which `Presentation` references; `MatchEndedNet` does not, which is why
    /// the crossing happens HERE, in the one assembly allowed to see both —
    /// the same route `MatchResultsBoard` takes for the board.
    ///
    /// PURE, SO IT CAN BE CHECKED. Fourteen same-typed assignments in a row are
    /// exactly the shape where a swapped pair compiles, runs and misreports a
    /// raid forever — the reason `MatchServer.EndedNetFor` was made `internal`
    /// on the sending side, and the reason this is a static function rather
    /// than a few lines inside `NetworkSimBackend`, which no EditMode test can
    /// stand up at all (bd `app-xkir`).
    public static class FinalStats
    {
        /// WHICH TALLY ANSWERS THE END-OF-RAID SCREEN (bd `app-qw01`).
        ///
        /// Two messages can carry this collector's numbers and they arrive at
        /// different moments: `RaidEndedNet` when HIS raid ends, `MatchEndedNet`
        /// when the MATCH does. The match's own message outranks it when both
        /// are in hand — it is the authoritative final tally and it is the only
        /// one carrying the match's end reason — but between the two the raid
        /// message is all there is, and that gap is the whole defect: it was
        /// measured at nineteen seconds for a collector who walked out and over
        /// four minutes for one who died early.
        ///
        /// THE ORDER IS THE RULE, not an implementation detail: the match's own
        /// message is asked FIRST because when it has arrived it is the
        /// authoritative final tally and the only one carrying an end reason.
        /// The raid tally answers in the gap before it — and that gap is the
        /// whole defect.
        public static bool TryTally(bool matchEnded, in MatchEndedNet ended,
            bool raidEnded, in RaidEndedNet raid, out MatchEndedNet tally)
        {
            if (matchEnded) { tally = ended; return true; }
            if (raidEnded) { tally = raid.Tally; return true; }
            tally = default;
            return false;
        }

        /// This collector's own half of the message.
        public static MatchStats PersonalFrom(in MatchEndedNet ended) => new MatchStats
        {
            Kills = ended.Kills,
            HeadshotKills = ended.HeadshotKills,
            ShotsFired = ended.ShotsFired,
            ShotsHit = ended.ShotsHit,
            DashesUsed = ended.DashesUsed,
            SlidesUsed = ended.SlidesUsed,
            DeathTick = ended.DeathTick,
            DamageTaken = ended.DamageTaken,
            AmmoSpent = ended.AmmoSpent,
            CellsPicked = ended.CellsPicked,
        };

        /// The world-scoped half, identical in every copy of the message.
        public static WorldStats WorldFrom(in MatchEndedNet ended) => new WorldStats
        {
            WavesCleared = ended.WavesCleared,
            MobSpawnsSkipped = ended.MobSpawnsSkipped,
            ProjectileSpawnsSkipped = ended.ProjectileSpawnsSkipped,
        };
    }
}
