using NUnit.Framework;
using Ring.Presentation;
using Picture = Ring.Presentation.SimulationRunner.RequestedPicture;

namespace Ring.Simulation.Tests
{
    /// bd `app-sfi` — the spectate request's state machine, as a table.
    ///
    /// WHAT THE DEFECT WAS. `Broken` had exactly one way out (`Proven`), so a
    /// switch the server had already ACCEPTED was thrown away in silence
    /// whenever the target crossed the corpse's field of view while the request
    /// was still climbing the wire: the picture never moved, and once the target
    /// walked off, the frame carried neither it nor this client's own seat —
    /// camera and HP bar frozen, recoverable only by pressing again.
    ///
    /// The variant chosen (the task's (a)) lets a RETURNING target restart the
    /// run, which costs exactly what owner decision 1b already accepted and
    /// leaves the protocol untouched.
    public class SpectateRequestPictureTests
    {
        static Picture Step(Picture from, bool targetKnown, bool ownKnown)
            => SimulationRunner.NextRequestedPicture(from, targetKnown, ownKnown);

        [Test]
        public void AFrameWithTheTargetAndWithoutOwnSeat_ProvesTheSwitch()
        {
            Assert.AreEqual(Picture.Proven, Step(Picture.NeverArrived, true, false));
            Assert.AreEqual(Picture.Proven, Step(Picture.Holding, true, false));
            Assert.AreEqual(Picture.Proven, Step(Picture.Broken, true, false),
                "proof outranks a run broken earlier — that is what the state is for");
        }

        [Test]
        public void ProofIsSticky()
        {
            Assert.AreEqual(Picture.Proven, Step(Picture.Proven, false, true),
                "a frame that already arrived cannot be unsaid by a later one");
            Assert.AreEqual(Picture.Proven, Step(Picture.Proven, false, false));
            Assert.AreEqual(Picture.Proven, Step(Picture.Proven, true, true));
        }

        [Test]
        public void TheFirstFrameCarryingTheTargetStartsTheRun()
        {
            Assert.AreEqual(Picture.Holding, Step(Picture.NeverArrived, true, true));
        }

        [Test]
        public void ARunThatLosesTheTargetBreaks()
        {
            Assert.AreEqual(Picture.Broken, Step(Picture.Holding, false, true));
        }

        [Test]
        public void ARequestNoFrameHasAnsweredStaysUnanswered()
        {
            Assert.AreEqual(Picture.NeverArrived, Step(Picture.NeverArrived, false, true),
                "silence is not a refusal and not an acceptance");
        }

        /// The defect itself.
        [Test]
        public void ABrokenRunStartsAgainWhenTheTargetComesBack()
        {
            Assert.AreEqual(Picture.NeverArrived, Step(Picture.Broken, true, true),
                "the request is still in flight; the gap said nothing about the switch");
        }

        /// And the sequence the owner would live through, end to end.
        [Test]
        public void TheAcceptedSwitchSurvivesATargetThatHidAndReturned()
        {
            var p = Picture.NeverArrived;

            p = Step(p, targetKnown: true, ownKnown: true);    // seen from the corpse
            Assert.AreEqual(Picture.Holding, p);

            p = Step(p, targetKnown: false, ownKnown: true);   // hides past the linger
            Assert.AreEqual(Picture.Broken, p);

            p = Step(p, targetKnown: true, ownKnown: true);    // and comes back
            Assert.AreEqual(Picture.NeverArrived, p);

            p = Step(p, targetKnown: true, ownKnown: true);    // and stays
            Assert.AreEqual(Picture.Holding, p,
                "so the window closes on Holding and the accepted switch is applied — "
                + "before this fix the same sequence ended on Broken and was dropped in "
                + "silence");
        }

        [Test]
        public void ABrokenRunThatNeverRecoversIsStillDropped()
        {
            var p = Step(Step(Picture.NeverArrived, true, true), false, true);
            Assert.AreEqual(Picture.Broken, p);
            Assert.AreEqual(Picture.Broken, Step(p, false, true),
                "nothing here invents an acceptance the frames never showed");
        }
    }
}
