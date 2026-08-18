using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 4 (spec §3.6 "Рюкзак"): the backpack's own storage —
    /// slot-point capacity, add/remove, and per-player isolation. The
    /// backpack lives outside PlayerState (owner decision Р232 — see
    /// SimulationWorld's own Inventory field doc), so every test here
    /// exercises SimulationWorld's own seams (InventoryCountOf/
    /// InventoryItemAt/InventoryUsedSlots/TryAddItem/TryRemoveItemAt/
    /// SetInventoryForTest) directly — no Loot.Inventory instance is ever
    /// touched by a test. Stage 3 Task 13: item ids used below are REAL
    /// catalog entries out of TestConfigs.Default().Items (five records,
    /// Id 0..4, SlotCost 1/2/3/4/1) — Loot.Inventory.SlotCostOf now resolves
    /// through the real catalog (ItemCatalogLookup.Find, R-89), so an id
    /// outside it throws rather than defaulting to 1 the way the removed
    /// T4 -> T13 stub silently did.
    public class InventoryTests
    {
        [Test]
        public void EmptyInventory_HasZeroUsedSlots()
        {
            var w = new SimulationWorld(1, TestConfigs.Default());
            Assert.AreEqual(0, w.InventoryUsedSlots(0), "a fresh backpack must carry no slot points");
            Assert.AreEqual(0, w.InventoryCountOf(0), "…and no items either");
        }

        [Test]
        public void AddingItems_AccumulatesUsedSlots()
        {
            // Id 0 costs 1, Id 1 costs 2 (spec §3.7 table: Т1/Т2) — chosen
            // deliberately UNEQUAL so this test is the mutation witness for
            // "SlotCostOf reads the real catalog" (coordinator §4 point 6):
            // a stub returning a flat 1 per item would total 2, not 3.
            var w = new SimulationWorld(1, TestConfigs.Default());
            Assert.IsTrue(w.TryAddItem(0, 0), "premise: the first add (Id 0) must succeed");
            Assert.IsTrue(w.TryAddItem(0, 1), "premise: the second add (Id 1) must succeed");

            Assert.AreEqual(3, w.InventoryUsedSlots(0),
                "Id 0 (cost 1) plus Id 1 (cost 2) must total 3 slot points");
            Assert.AreEqual(2, w.InventoryCountOf(0), "…and the item count must be two");
            Assert.AreEqual((byte)0, w.InventoryItemAt(0, 0), "slot 0 must hold the first item added");
            Assert.AreEqual((byte)1, w.InventoryItemAt(0, 1), "slot 1 must hold the second item added");
        }

        [Test]
        public void AddBeyondCapacity_Refused_AndInventoryUnchanged()
        {
            // Id 0 and Id 4 (the repair kit) both cost 1 (spec §3.7 table),
            // so two of them exactly fill a capacity of 2.
            var cfg = TestConfigs.Default();
            cfg.Hero.InventoryCapacity = 2;
            var w = new SimulationWorld(1, cfg);
            Assert.IsTrue(w.TryAddItem(0, 0), "premise: the first item (Id 0, cost 1) must fit");
            Assert.IsTrue(w.TryAddItem(0, 4), "premise: the second item (Id 4, cost 1) must exactly fill capacity");

            bool addedThird = w.TryAddItem(0, 0);

            Assert.IsFalse(addedThird, "a third item must be refused once the two slot points are spent");
            Assert.AreEqual(2, w.InventoryUsedSlots(0), "the refused add must not have spent a slot point");
            Assert.AreEqual(2, w.InventoryCountOf(0), "…and must not have grown the item count either");
        }

        [Test]
        public void AddBeyondMaxItems_Refused_AndInventoryUnchanged()
        {
            // Coordinator finding (mutation branch 3): under every OTHER
            // fixture in this file (InventoryCapacity<=8), the slot-point
            // guard always trips before the array's own MaxInventoryItems
            // ceiling ever could — the ceiling guard is unreachable, hence
            // unproven, by construction. This fixture inverts that:
            // MaxInventoryItems is deliberately far BELOW InventoryCapacity,
            // so the array ceiling is the FIRST — and only — guard within
            // reach. Same item id (0) added three times on purpose: this
            // test is about the COUNT ceiling, not slot cost.
            var cfg = TestConfigs.Default();
            cfg.Hero.MaxInventoryItems = 2;
            cfg.Hero.InventoryCapacity = 100; // generous on purpose — slot points must stay nowhere near spent
            var w = new SimulationWorld(1, cfg);
            Assert.IsTrue(w.TryAddItem(0, 0), "premise: the first item must fit under the array ceiling");
            Assert.IsTrue(w.TryAddItem(0, 0), "premise: the second item must exactly fill the array ceiling");

            bool addedThird = w.TryAddItem(0, 0);

            Assert.IsFalse(addedThird,
                "a third item must be refused once MaxInventoryItems's array ceiling is full, " +
                "even though InventoryCapacity's slot points are nowhere near spent (2 of 100)");
            Assert.AreEqual(2, w.InventoryUsedSlots(0), "the refused add must not have spent a slot point");
            Assert.AreEqual(2, w.InventoryCountOf(0), "…and must not have grown the item count either");
        }

        [Test]
        public void RemoveAt_ReturnsItem_AndFreesSlots()
        {
            // Id 0 and Id 4 both cost 1 (same pair AddBeyondCapacity above
            // uses) — keeps this test's numbers (one item, one slot point
            // remaining) unchanged from before the real catalog landed.
            var w = new SimulationWorld(1, TestConfigs.Default());
            w.SetInventoryForTest(0, 0, 4);

            bool removed = w.TryRemoveItemAt(0, 0, out byte itemId);

            Assert.IsTrue(removed, "slot 0 holds an item and must be removable");
            Assert.AreEqual((byte)0, itemId, "the removed slot must hand back the item that was there");
            Assert.AreEqual(1, w.InventoryCountOf(0), "one item must remain");
            Assert.AreEqual(1, w.InventoryUsedSlots(0), "…and the remaining item's (Id 4, cost 1) slot point");
        }

        [Test]
        public void RemoveFromEmptySlot_ReturnsFalse()
        {
            var w = new SimulationWorld(1, TestConfigs.Default());

            bool removed = w.TryRemoveItemAt(0, 0, out byte itemId);

            Assert.IsFalse(removed, "an empty backpack has no slot 0 to remove");
            Assert.AreEqual(default(byte), itemId, "a refused removal must not hand back a real item id");
        }

        [Test]
        public void InventoriesOfPlayers_DoNotMix()
        {
            // Lesson 227: the subject is the SECOND player (index 1), not
            // player 0.
            var w = new SimulationWorld(1, TestConfigs.Default(), playerCount: 2);

            Assert.IsTrue(w.TryAddItem(1, 0), "premise: the add to player 1's backpack must succeed");

            Assert.AreEqual(0, w.InventoryCountOf(0), "player 0's backpack must stay untouched");
            Assert.AreEqual(1, w.InventoryCountOf(1), "player 1's own item must land in player 1's backpack");
            Assert.AreEqual((byte)0, w.InventoryItemAt(1, 0), "…and be the exact item that was added");
        }
    }
}
