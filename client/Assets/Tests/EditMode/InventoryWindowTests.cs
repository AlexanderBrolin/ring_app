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
            // client would keep drawing a window the world stopped honoring.
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

        /// Фикс-раунд гейта Ф7, находка ревью B-1 (Critical).
        ///
        /// THE LOSER OF A LOOT RACE MUST NOT CRASH THE WINDOW. The race is
        /// deliberately not blocked (`LootOps.Validate` check 4b: "This is also
        /// the refusal the LOSER of a race gets"), and the item STAYS in the
        /// container while a timer runs (`LootOps.Begin`'s own doc), with
        /// revalidation only at the completion tick. So between the winner's
        /// completion and the loser's there is a real window in which the
        /// loser's `LootTimer` names a slot whose occupancy bit is already
        /// clear — and the panel then had `itemId = 0`, which
        /// `ItemCatalogLookup.Find` REFUSES BY THROWING (0 is the reserved
        /// "empty" sentinel, Р229, and is in no catalog). One exception per
        /// frame, for the length of the transfer, on the client that lost.
        [Test]
        public void TransferProgress_OfAnEmptiedSlot_IsZero_AndDoesNotThrow()
        {
            SimConfig cfg = TestConfigs.Open();

            Assert.DoesNotThrow(() => InventoryWindowController.TransferProgress(in cfg, 0, 0.5f),
                "the slot a running transfer names can be emptied under it by another collector");
            Assert.AreEqual(0f, InventoryWindowController.TransferProgress(in cfg, 0, 0.5f), 1e-6f,
                "an emptied slot has no transfer left to draw");
        }

        [Test]
        public void TransferProgress_OfARealItem_StillMeasuresItsOwnTier()
        {
            SimConfig cfg = TestConfigs.Open();
            byte itemId = cfg.Items[0].Id;
            float total = cfg.Loot.TransferSeconds[cfg.Items[0].Tier];

            // The positive witness beside the guard (lesson 129): without it,
            // "always return 0" would satisfy the test above perfectly and the
            // bar would never move for anybody.
            Assert.AreEqual(0.5f, InventoryWindowController.TransferProgress(in cfg, itemId, total * 0.5f),
                1e-5f, "half the tier's own duration spent is a half-full bar");
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

        // ---- what an OPEN window costs the aim surfaces (bd `app-zg29`) ------
        //
        // The owner found this from the far side: no mouse POINTER appeared in
        // the window, so items had to be picked with the aim marker, while
        // Escape's pause menu had a pointer all along. The cursor is a pure
        // function of `SimulationRunner.AimActive` (`CrosshairView` is its sole
        // owner in the project), pause was one of that property's terms and the
        // window was not — so these pin all four, the new one included.

        [Test]
        public void AimActive_InAnOrdinaryFightingFrame()
        {
            Assert.IsTrue(SimulationRunner.IsAimActive(
                ready: true, paused: false, alive: true, inventoryOpen: false));
        }

        /// The finding itself. `WeaponSystem.CanFire` refuses the shot on
        /// `InventoryOpen` unconditionally — there is no `CanFireWhileWindowOpen`
        /// the way there is for the dash and the slide — so while the window is
        /// up the game is not asking for aim in the strictest sense there is,
        /// and the marker, the cone and the ray were all drawing a shot that
        /// could not happen.
        [Test]
        public void AimActive_IsFalse_WhileTheLootWindowIsOpen()
        {
            Assert.IsFalse(SimulationRunner.IsAimActive(
                ready: true, paused: false, alive: true, inventoryOpen: true),
                "the pointer belongs to the player while he is reading his pack, and the shot "
                + "is refused by the server anyway");
        }

        /// The three terms that were already there, pinned so the fourth cannot
        /// be added by loosening one of them.
        [Test]
        public void AimActive_IsFalse_BeforeTheBackendHasAPicture()
        {
            Assert.IsFalse(SimulationRunner.IsAimActive(
                ready: false, paused: false, alive: true, inventoryOpen: false));
        }

        [Test]
        public void AimActive_IsFalse_WhileThePauseMenuIsUp()
        {
            Assert.IsFalse(SimulationRunner.IsAimActive(
                ready: true, paused: true, alive: true, inventoryOpen: false),
                "input is frozen while paused, so a held right button would aim for ever");
        }

        [Test]
        public void AimActive_IsFalse_WhileThisCollectorIsDown()
        {
            Assert.IsFalse(SimulationRunner.IsAimActive(
                ready: true, paused: false, alive: false, inventoryOpen: false));
        }
    }
}
