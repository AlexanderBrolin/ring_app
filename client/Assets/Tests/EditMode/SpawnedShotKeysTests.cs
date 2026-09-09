using NUnit.Framework;
using Ring.Networking.Client;

namespace Ring.Simulation.Tests
{
    /// The client's memory of which predicted shots have already been drawn
    /// (app-8dv T4, spec §3.4). FishNet replays `PerformReplicate` after every
    /// state packet, so the same shot is written into the log again and again;
    /// this is what stops it from growing a second trail each time.
    ///
    /// ⛔ THE KEY IS THE FISHNET TICK, NOT `ShotOrdinal`, AND THAT CANCELS A
    /// SEAM THE SPEC ASKED FOR. The spec's mechanism -- key by `ShotOrdinal`,
    /// plus a `BeginReconcile` sweep of everything above the authoritative one
    /// -- would duplicate a trail on EVERY reconcile, roughly thirty times a
    /// second: `[Reconcile]` arrives on every state packet rather than only on
    /// a misprediction, and the authoritative ordinal lags the predicted one by
    /// the depth of prediction, so "clear everything above it" routinely drops
    /// the bar below keys whose trails are already on screen. The tick has
    /// neither problem: it is monotonic for the process (`ShotOrdinal` steps
    /// BACKWARD whenever `BeginReconcile` assigns the authoritative state
    /// whole), and a replay re-runs the SAME ticks, so a repeat is refused by
    /// construction.
    public class SpawnedShotKeysTests
    {
        [Test]
        public void AFirstClaimIsGranted_AndItsRepeatIsNot()
        {
            var keys = new SpawnedShotKeys();
            Assert.IsTrue(keys.TryClaim(100u, 0), "первое рождение следа отказано");
            Assert.IsFalse(keys.TryClaim(100u, 0),
                "повтор реплея родил второй след на том же выстреле");
        }

        /// ⭐ THE STRESS CASE THE PAIR EXISTS FOR (M272): `Advance`'s `while`
        /// loop can fire more than once in a tick when `FireInterval` is
        /// shorter than `TickDt`, and both shots deserve a trail. A dedup that
        /// compared ticks alone would swallow the second one.
        [Test]
        public void TwoShotsInOneTick_BothClaim()
        {
            var keys = new SpawnedShotKeys();
            Assert.IsTrue(keys.TryClaim(100u, 0), "первый выстрел тика отказан");
            Assert.IsTrue(keys.TryClaim(100u, 1),
                "второй выстрел того же тика проглочен — дедуп смотрит только на тик");
            // ⚠ AND THE OTHER HALF OF THE PAIR'S CONTRACT (review finding): the
            // REPLAY of that second shot must still be refused. Without this
            // line the mutant that never stores `seqInTick` survives -- it lets
            // the first claim of every sequence number through, which is exactly
            // the duplicate-trail-per-reconcile this class exists against.
            Assert.IsFalse(keys.TryClaim(100u, 1),
                "реплей второго выстрела тика родил ещё один след");
        }

        /// A replay re-runs ticks that are not newer than the mark, which is
        /// exactly what makes it a replay.
        [Test]
        public void AnOlderTickIsRefused()
        {
            var keys = new SpawnedShotKeys();
            Assert.IsTrue(keys.TryClaim(100u, 0), "премисса: первая заявка обязана пройти");
            Assert.IsFalse(keys.TryClaim(99u, 0), "тик из прошлого родил след заново");
            Assert.IsFalse(keys.TryClaim(100u, 0), "тот же тик и тот же номер прошли дважды");
            Assert.IsTrue(keys.TryClaim(101u, 0), "следующий тик отказан");
        }

        /// A per-match seam: trails of the previous match must never suppress
        /// the first shots of the next one, and the tick domain does not reset
        /// between matches -- but the world it draws into does.
        [Test]
        public void Reset_ForgetsTheMark()
        {
            var keys = new SpawnedShotKeys();
            Assert.IsTrue(keys.TryClaim(100u, 0), "премисса: первая заявка обязана пройти");
            keys.Reset();
            Assert.IsTrue(keys.TryClaim(100u, 0), "после рестарта матча заявка всё ещё занята");
        }
    }
}
