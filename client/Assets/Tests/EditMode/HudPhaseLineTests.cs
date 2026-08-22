using System;
using NUnit.Framework;
using Ring.Presentation;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т33 (spec §3.11, bd `app-j4oj`): the two things the HUD's phase
    /// line and the extraction rings SAY, tested where they can be — as pure
    /// functions of the frame, with the drawing left to the playtest.
    ///
    /// THE PICTURE'S RULE AND THE WORLD'S RULE ARE ONE FUNCTION, and that is
    /// the half of `app-j4oj` a test can hold. `ExtractionSystem` enforces
    /// which exits take a collector; the ring on the floor reports it; both now
    /// read `ExitRules.IsOpen`, so the only way for the picture to lie about an
    /// exit is for the world to lie the same way.
    public class HudPhaseLineTests
    {
        static MatchState In(MatchPhase phase) => new MatchState { Phase = phase };

        [Test]
        public void EveryPhaseOwnsAWord()
        {
            // Reflective over the domain rather than four hand-written cases: a
            // phase added later has no word, and the `default: throw` says so
            // HERE instead of in front of a player.
            foreach (MatchPhase phase in Enum.GetValues(typeof(MatchPhase)))
            {
                Assert.IsNotEmpty(HudController.PhaseWord(phase, directorAlive: true),
                    $"{phase} has no word on the line");
            }
        }

        [Test]
        public void UnknownPhase_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => HudController.PhaseWord((MatchPhase)99, directorAlive: true),
                "a phase with no word must say so, not print an empty line");
        }

        [Test]
        public void DirectorActive_ReadsDifferentlyOnceHeIsDead()
        {
            // The phase covers the whole endgame including the sharing window
            // AFTER he falls, so one word for both halves would name a danger
            // that is already over. This is what `RenderSnapshot.DirectorAlive`
            // is carried for (R-257).
            Assert.AreNotEqual(
                HudController.PhaseWord(MatchPhase.DirectorActive, directorAlive: true),
                HudController.PhaseWord(MatchPhase.DirectorActive, directorAlive: false),
                "the line must distinguish a living Director from a fallen one");
        }

        [Test]
        public void OtherPhases_DoNotDependOnTheDirectorBit()
        {
            // The mirror of the test above, and the reason it is here: without
            // it, "always report the bit" would pass the one above and still be
            // wrong — the raid does not farm differently because a Director who
            // has not woken yet is alive.
            foreach (MatchPhase phase in Enum.GetValues(typeof(MatchPhase)))
            {
                if (phase == MatchPhase.DirectorActive) continue;
                Assert.AreEqual(HudController.PhaseWord(phase, directorAlive: true),
                    HudController.PhaseWord(phase, directorAlive: false),
                    $"{phase} must read the same either way");
            }
        }

        [Test]
        public void Clock_ReadsAsMinutesAndPaddedSeconds()
        {
            Assert.AreEqual("0:00", HudController.Clock(0));
            Assert.AreEqual("0:09", HudController.Clock(9), "seconds are padded, or 0:9 reads as 0:90");
            Assert.AreEqual("1:00", HudController.Clock(60));
            Assert.AreEqual("12:34", HudController.Clock(754));
        }

        [Test]
        public void Clock_FloorsAtZero_RatherThanPrintingASign()
        {
            // The sentinel `MatchCountdown.None` is -1 and never reaches here
            // (`UpdatePhaseLine` drops the clock instead), but a countdown that
            // overran by a tick must read as spent, not as "-0:01".
            Assert.AreEqual("0:00", HudController.Clock(-1));
        }

        [Test]
        public void PortalsAreOpenWhileTheRaidFarms_AndTheGateIsNot()
        {
            Assert.IsTrue(ExitRules.IsOpen(In(MatchPhase.Farm), (byte)ExitKind.Portal),
                "the early portals are the way out during the farm");
            Assert.IsFalse(ExitRules.IsOpen(In(MatchPhase.Farm), (byte)ExitKind.Gate),
                "the gate is shut until the Director falls");
        }

        [Test]
        public void TheDirectorWaking_ShutsThePortalsForGood()
        {
            Assert.IsFalse(ExitRules.IsOpen(In(MatchPhase.DirectorActive), (byte)ExitKind.Portal));
            Assert.IsFalse(ExitRules.IsOpen(In(MatchPhase.DirectorActive), (byte)ExitKind.Gate));
        }

        [Test]
        public void GateOpen_TakesTheGateAndNotThePortals()
        {
            Assert.IsTrue(ExitRules.IsOpen(In(MatchPhase.GateOpen), (byte)ExitKind.Gate));
            Assert.IsFalse(ExitRules.IsOpen(In(MatchPhase.GateOpen), (byte)ExitKind.Portal),
                "the portals do not reopen — the raid leaves by the gate or not at all");
        }

        [Test]
        public void AnEndedRaid_HasNoWayOutAtAll()
        {
            Assert.IsFalse(ExitRules.IsOpen(In(MatchPhase.Ended), (byte)ExitKind.Portal));
            Assert.IsFalse(ExitRules.IsOpen(In(MatchPhase.Ended), (byte)ExitKind.Gate));
        }

        [Test]
        public void UnknownExitKind_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ExitRules.IsOpen(In(MatchPhase.Farm), 99),
                "an exit kind the table does not know must say so, not default to open");
        }
    }
}
