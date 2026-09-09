using NUnit.Framework;
using Ring.Networking.Client;

namespace Ring.Simulation.Tests
{
    /// app-8dv T5 (spec §3.4, plan Step 5): the two decisions the predicted
    /// trail makes, taken out of `NetworkSimBackend` and made VALUES.
    ///
    /// WHY THEY LIVE HERE AT ALL, SAID PLAINLY. Both answers are consumed by
    /// private lines of a class whose constructor takes a live
    /// `NetworkManager`, so no EditMode fixture can reach them where they are
    /// spent. Left as `if`s at the spend site they would carry three of this
    /// task's eight mutations with no witness at all -- the same hole Ruling
    /// 306 closed by moving the seed arithmetic out of `RouteToTracers` and
    /// into `TracerProjectiles.TrySpawn`.
    ///
    /// ⛔ AND THEY ARE ANSWERS, NOT PREDICATES. An earlier draft of the plan
    /// carried four booleans, three of which returned their own argument --
    /// `f(x) == f(x)`, which is not a witness (lesson 428) and leaves the real
    /// decision in the `if` the mutation aims at. The shape used instead is
    /// the repository's own: `MatchEndPolicy.Evaluate` and
    /// `SpectatePolicy.ShouldLogRefusal` answer with a value the caller
    /// switches on.
    public class OwnShotPolicyTests
    {
        /// The whole truth table in one fixture, and the reason it is one
        /// fixture is that BOTH parameters need a witness rather than only
        /// the one a mutation was noticed at (ruling 346, lesson 705): a
        /// mutant that ignored `isOwnShot` and one that ignored
        /// `ghostConfirmed` both survive any three of these four rows.
        [Test]
        public void RouteOwnSpawn_AnswersOneRouteForEachCornerOfTheTable()
        {
            Assert.AreEqual(OwnShotRoute.AdoptGhost,
                OwnShotPolicy.RouteOwnSpawn(isOwnShot: true, ghostConfirmed: true),
                "a confirmed ghost is adopted -- the trail is already on screen, and a "
                + "second one would draw the same round twice");

            Assert.AreEqual(OwnShotRoute.SpawnPlainly,
                OwnShotPolicy.RouteOwnSpawn(isOwnShot: true, ghostConfirmed: false),
                "the ghost expired or was never born, so the shot takes the ordinary path "
                + "-- otherwise this client's own round would have no trail at all");

            Assert.AreEqual(OwnShotRoute.Ignore,
                OwnShotPolicy.RouteOwnSpawn(isOwnShot: false, ghostConfirmed: true),
                "somebody else's round is none of the ghost registry's business, and a "
                + "positional FIFO has no identity to refuse it by");

            Assert.AreEqual(OwnShotRoute.Ignore,
                OwnShotPolicy.RouteOwnSpawn(isOwnShot: false, ghostConfirmed: false),
                "and that stays true whatever the ghost queue happens to hold");
        }

        /// The enum's own zero, pinned because the SAFE direction is what a
        /// forgotten branch has to fall into -- the reason `MatchOutcome`
        /// states for its own zero. A route that defaulted to `AdoptGhost`
        /// would pair a stranger's round with this client's oldest
        /// unconfirmed ghost, which is exactly the wrong-identity match
        /// `GhostProjectiles`' own KNOWN LIMIT paragraph describes.
        [Test]
        public void TheDefaultRouteIsIgnore_SoAForgottenBranchIsHarmless()
        {
            Assert.AreEqual(OwnShotRoute.Ignore, default(OwnShotRoute));
            Assert.AreEqual(0, (int)OwnShotRoute.Ignore);
        }

        /// M247's witness. The budget is `NetConfig.TracerCatchUpBudget`
        /// (Р468): `renderTick` can jump, and `FireInterval` is hot-tweakable
        /// down to 0.01 s, so a frame can find far more records waiting than
        /// a frame should ever spend trails on.
        ///
        /// ⚠ THE BUDGET ITSELF MOVES BETWEEN THE ROWS, and that is the point
        /// rather than variety (ruling 346, lesson 705): with every row on
        /// one budget, `=> pending > 8 ? 8 : pending` -- a shipped literal
        /// instead of the parameter -- would survive the whole fixture.
        [Test]
        public void SpawnBudgetFor_SpendsWhatTheFrameAllows_AndNeverMoreThanIsWaiting()
        {
            Assert.AreEqual(3, OwnShotPolicy.SpawnBudgetFor(pending: 3, budget: 8),
                "fewer records than the budget: every one of them is born");
            Assert.AreEqual(8, OwnShotPolicy.SpawnBudgetFor(pending: 20, budget: 8),
                "more records than the budget: the frame spends exactly the budget");
            Assert.AreEqual(2, OwnShotPolicy.SpawnBudgetFor(pending: 20, budget: 2),
                "and the ceiling is the PARAMETER, not the number this project ships");
            Assert.AreEqual(20, OwnShotPolicy.SpawnBudgetFor(pending: 20, budget: 40),
                "a budget above what is waiting cannot invent records");
        }

        /// The overflow the caller counts is what this answer leaves behind,
        /// and it is measured as `pending - granted` rather than restated as
        /// a second formula (lesson 428: an expectation computed by the
        /// function under test is not an expectation).
        ///
        /// The floor at zero is not decoration: `pending` is a count the
        /// drain hands over, `budget` comes from a hand-editable
        /// `NetConfig`, and a negative answer would be handed to a loop
        /// bound. Refusal by value, like every other gate on this path
        /// (Р82).
        [Test]
        public void SpawnBudgetFor_LeavesTheRestForTheOverflowCounter_AndNeverGoesNegative()
        {
            const int pending = 11;
            const int budget = 4;

            int granted = OwnShotPolicy.SpawnBudgetFor(pending, budget);
            Assert.AreEqual(4, granted);
            Assert.AreEqual(7, pending - granted,
                "the seven records this frame refused are what the log's own "
                + "OverflowDroppedShots counts");

            Assert.AreEqual(0, OwnShotPolicy.SpawnBudgetFor(pending: 0, budget: 8),
                "an empty drain grants nothing");
            Assert.AreEqual(0, OwnShotPolicy.SpawnBudgetFor(pending: 5, budget: 0),
                "a budget of zero is a legal tuning and grants nothing");
            Assert.AreEqual(0, OwnShotPolicy.SpawnBudgetFor(pending: 5, budget: -3),
                "and a hand-edited negative budget is refused as a value, never handed "
                + "on as a negative loop bound");
        }
    }
}
