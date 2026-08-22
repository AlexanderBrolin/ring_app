using NUnit.Framework;
using Ring.Presentation;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т33a (bd `app-17gj`, found by the owner's В1 playtest): WHEN the
    /// loot window shuts itself, and — the whole point of the task — when it
    /// must not.
    ///
    /// THE DEFECT THIS FILE EXISTS FOR. `InventoryWindowController.Update` used
    /// to close the window whenever the frame described no box interior, which
    /// folded two different statements into one: "the SOURCE panel has nothing
    /// to show" and "the WINDOW must close". The frame's interior pool lists
    /// only the boxes within `LootOps.WithinLootReach` — on the wire by Р238
    /// and locally by `SimulationWorld.CaptureOwnerView`'s copy of the same
    /// rule — so in open ground that count is zero, and Tab lowered the flag it
    /// had just raised before a panel was ever drawn. A collector must be able
    /// to look in his own backpack anywhere.
    ///
    /// NOT A MONOBEHAVIOUR TEST, and that is what the split bought. The
    /// decision moved out of `Update` into a static function of the frame, so
    /// it can be asked directly here; what remains in `Update` is drawing.
    /// Lesson 399 in one file: a condition that only ever fails in front of a
    /// player is a condition no test was looking at.
    public class InventoryWindowTests
    {
        /// A frame carrying one live collector and NO box in reach — open
        /// ground, the exact situation the owner pressed Tab in.
        static RenderSnapshot OpenGroundFrame()
        {
            SimConfig cfg = TestConfigs.Open();
            var frame = new RenderSnapshot(cfg);
            frame.PlayerCount = 1;
            frame.LocalPlayerIndex = 0;
            frame.Players[0] = new PlayerState { Alive = true, Extracted = false };
            frame.ContainerInteriorCount = 0;
            return frame;
        }

        [Test]
        public void NoBoxInReach_DoesNotCloseTheWindow()
        {
            RenderSnapshot frame = OpenGroundFrame();

            // The regression witness for app-17gj. An empty source pool is a
            // statement about the SOURCE panel, and the window is two panels.
            Assert.IsFalse(InventoryWindowController.WindowMustClose(in frame),
                "a live collector in open ground must keep his backpack open");
        }

        [Test]
        public void DeadCollector_ClosesTheWindow()
        {
            RenderSnapshot frame = OpenGroundFrame();
            frame.Players[0].Alive = false;

            // The mirror of `SimInputSanitizer`, which already forces the
            // SERVER's flag down for a dead player. Without this line the
            // client would keep drawing a window the world stopped honouring.
            Assert.IsTrue(InventoryWindowController.WindowMustClose(in frame),
                "death closes the window");
        }

        [Test]
        public void ExtractedCollector_ClosesTheWindow()
        {
            RenderSnapshot frame = OpenGroundFrame();
            frame.Players[0].Extracted = true;

            Assert.IsTrue(InventoryWindowController.WindowMustClose(in frame),
                "a collector who has left the arena closes the window with him");
        }

        [Test]
        public void BoxInReach_DoesNotCloseTheWindow_Either()
        {
            RenderSnapshot frame = OpenGroundFrame();
            frame.ContainerInteriorCount = 1;

            // The other half of the split, stated so the pool cannot creep back
            // into the decision from the opposite side: the answer is the same
            // with a box as without one, because the pool is not consulted.
            Assert.IsFalse(InventoryWindowController.WindowMustClose(in frame),
                "standing over a box changes nothing about whether the window shuts");
        }
    }
}
