namespace Ring.Server
{
    /// bd `app-qrew`: WHETHER THIS PROCESS PLAYS ANOTHER MATCH once the one it
    /// is running has ended and its linger has expired.
    ///
    /// IT LIVES HERE, NOT IN `ServerBootstrap`, AND THAT IS THE POINT. The
    /// restart mechanism (`MatchServer.RestartMatch`) has existed since Stage 2
    /// Task 40 with nothing calling it, so the whole client-side reset list
    /// (Р291) was unreachable on the networked path and provable only by unit
    /// tests and the solo path. The handle that fixes that is FishNet wiring,
    /// and FishNet wiring does not come up in EditMode — so the DECISION it
    /// pulls is separated out to where a test can reach it and the wiring is
    /// left with no opinion of its own (the precedent this project has applied
    /// six times now: a branch that cannot be tested where it is written is in
    /// the wrong place).
    public static class MatchRerunPolicy
    {
        /// The shipped default: one match, then exit — exactly what every
        /// container has done since Stage 2, and what the meta will expect once
        /// it schedules matches itself (Э5). A playtest raises it.
        public const int DefaultMatchesToPlay = 1;

        /// `matchesToPlay` is a COUNT, not a switch, and deliberately so. A
        /// plain "restart forever" flag is a footgun on a remote host: a match
        /// that ends the instant it starts — every collector dead on tick one,
        /// or a roster that never fills — would spin the container at full CPU
        /// with nobody watching. A bounded count cannot do that, and it still
        /// buys the playtest exactly what it needed: repeated raids without an
        /// ssh session and a `docker compose` cycle between them.
        ///
        /// `matchesPlayed` counts matches that have ENDED, so the first call
        /// after the first match sees 1.
        ///
        /// AND A REMAINING RAID IS NOT A REASON ON ITS OWN — SOMEBODY HAS TO
        /// BE THERE TO PLAY IT (bd `app-a8r5`, found on the owner's live
        /// playtest). The first shape of this method asked only about the
        /// count, and when the last client left the server kept obliging:
        /// rerun, empty arena, `AllPlayersDead` on tick one, ten seconds of
        /// linger, rerun again — three of the owner's eight raids gone before
        /// anyone looked. Two different facts ("the raids are not used up" and
        /// "the collectors are still on the line") had been folded into one
        /// question, which is this project's most-repeated defect (lessons
        /// 399/401/410); here the second fact was not asked at all.
        ///
        /// `liveConnections` is how many roster slots still hold an active
        /// connection at the moment the question is asked. Zero — or a
        /// miscount below zero — is an empty arena either way.
        public static bool ShouldRerun(int matchesToPlay, int matchesPlayed, int liveConnections)
            => matchesToPlay > DefaultMatchesToPlay
               && matchesPlayed < matchesToPlay
               && liveConnections > 0;
    }
}
