using NUnit.Framework;
using Ring.Presentation;

namespace Ring.Simulation.Tests
{
    /// Playtest В1, round two (bd `app-1kei`, found by the owner): WHAT a frame
    /// says to draw for one player slot.
    ///
    /// THE DEFECT THIS FILE EXISTS FOR. `ViewRegistry`'s per-frame loop asked
    /// `!state.Alive` and made a corpse of whatever answered — so a collector
    /// who walked out of the gate, having just killed the Director, lay down
    /// and played the death clip. Extraction sets `Alive = false` and
    /// `Extracted = true` in the same tick (`ExtractionSystem`), and the
    /// simulation has obeyed the difference since Т23:
    /// `ExtractionTests.Completing_MarksExtracted_LeavesNoCorpse_AndAnnouncesIt`
    /// pins "no corpse, nothing to loot". Only the picture was told one bit and
    /// drew one answer.
    ///
    /// NOT A MONOBEHAVIOUR TEST, the same split `InventoryWindowTests` bought
    /// for the same reason: the decision is a pure function of the three facts
    /// a frame states, so it can be asked here, and what remains in the loop is
    /// renting, positioning and syncing. Lesson 399/401 — two conditions folded
    /// into one break in front of a player, not in a suite.
    public class PlayerSlotPictureTests
    {
        [Test]
        public void ALiveSeat_IsADoll()
        {
            Assert.AreEqual(PlayerSlotPicture.Doll,
                ViewRegistry.PictureFor(known: true, alive: true, extracted: false));
        }

        [Test]
        public void AKnownSeatThatIsNeitherAliveNorOut_IsABody()
        {
            Assert.AreEqual(PlayerSlotPicture.Body,
                ViewRegistry.PictureFor(known: true, alive: false, extracted: false));
        }

        /// The finding itself.
        [Test]
        public void ASeatThatWalkedOut_IsGone_NotABody()
        {
            Assert.AreEqual(PlayerSlotPicture.Gone,
                ViewRegistry.PictureFor(known: true, alive: false, extracted: true),
                "extraction is not a death — the spec takes the body away (§3.5), so there is "
                + "nothing here to draw and certainly no death clip to play");
        }

        /// A slot the frame says nothing about is not a body either — that half
        /// predates this fix (Stage 2 Task 47a) and is pinned here so the new
        /// rule cannot be written in a way that loses it.
        [Test]
        public void AnUnknownSeat_IsGone_WhateverTheOtherBitsSay()
        {
            Assert.AreEqual(PlayerSlotPicture.Gone,
                ViewRegistry.PictureFor(known: false, alive: false, extracted: false),
                "an unknown slot may be reading default(PlayerState) — none of its other "
                + "bits mean anything");
            Assert.AreEqual(PlayerSlotPicture.Gone,
                ViewRegistry.PictureFor(known: false, alive: true, extracted: false),
                "…including a live-looking one");
        }

        /// The pair carries the invariant `!(Alive && Extracted)`, but the two
        /// masks arrive as independent bytes and a decoder that never throws
        /// (Р82) will hand up whatever was sent. Of the two readings, the one
        /// that draws nothing is the safe one.
        [Test]
        public void IfBothBitsAreSet_TheSeatIsStillGone()
        {
            Assert.AreEqual(PlayerSlotPicture.Gone,
                ViewRegistry.PictureFor(known: true, alive: true, extracted: true),
                "a hostile or out-of-sync sender must not be able to make this layer rent a "
                + "doll for a seat that is out of the raid");
        }
    }
}
