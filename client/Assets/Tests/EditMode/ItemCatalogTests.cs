using NUnit.Framework;
using Ring.Data;
using Ring.Simulation.Core;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 13 (spec §3.7): the item catalog — SO -> SimConfig
    /// copy semantics, catalog-shape validation, and the one id -> record
    /// lookup (ItemCatalogLookup.Find, owner decision R-89) every reader
    /// shares. Hot-tweak behavior (catalog is topology) lives in
    /// HotTweakTests.CatalogChange_ThrowsOnApplyConfig instead (coordinator
    /// R-87 — same file every other "…Change_ThrowsOnApplyConfig" test in
    /// this codebase already lives in).
    public class ItemCatalogTests
    {
        [Test]
        public void CatalogIsCopiedIntoSimConfig()
        {
            // Proves a CLONE, not an alias — SimConfigBuilder.Build must not
            // hand SimConfig.Items the SO's own live array (same
            // "ArrayContentsNotIdentity_DecideTheHash" discipline every
            // other array-shaped section already follows, e.g. Arena.
            // ObstaclePos). A build that aliased the source would let a
            // later Inspector edit of the SO silently reach into an
            // already-built SimConfig.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var items = ScriptableObject.CreateInstance<ItemCatalog>();
            byte originalFirstId = items.Items[0].Id;

            SimConfig cfg = SimConfigBuilder.Build(h, w, c, g, wv, a, vis, items: items);

            // Coordinator R-94: the length premise comes FIRST, as an
            // assertion — a test that indexes cfg.Items[0] without it dies
            // by IndexOutOfRangeException while the copy is still a stub,
            // which diagnoses nothing and, on RED, is indistinguishable
            // from a failed compile (two of those already cost this stage a
            // stop). Build_ItemCatalogAndLootConfig_ReachSimConfig (Config
            // Tests.cs) is the sibling this form is borrowed from.
            Assert.AreEqual(items.Items.Length, cfg.Items.Length,
                "premise: SimConfig.Items must carry every record before this test can say " +
                "anything about whether they were cloned");
            Assert.AreNotSame(items.Items, cfg.Items,
                "SimConfig.Items must be an independent array, not the SO's own live one");
            items.Items[0].Id = (byte)(originalFirstId + 1);
            Assert.AreEqual(originalFirstId, cfg.Items[0].Id,
                "mutating the SO's array after Build must not reach the already-built SimConfig");
        }

        [Test]
        public void SlotCostComesFromCatalog_NotFromStub()
        {
            // TestConfigs.Default().Items carries five records with
            // DIFFERENT SlotCost (1, 2, 3, 4, 1) — coordinator R-85's own
            // requirement: a catalog of all-1s could never tell a real
            // per-item lookup apart from the T4 -> T13 stub that always
            // returned 1. Id 1 costs 1, Id 2 costs 2 (spec §3.7 table, Т1/Т2;
            // coordinator fix-round Ф3 review C1 shifted ids up by one, 0 is
            // now reserved as the container slot's own "empty" sentinel).
            var w = new SimulationWorld(1, TestConfigs.Default());

            Assert.IsTrue(w.TryAddItem(0, 1), "premise: the first add (Id 1, cost 1) must fit");
            Assert.IsTrue(w.TryAddItem(0, 2), "premise: the second add (Id 2, cost 2) must fit");

            Assert.AreEqual(3, w.InventoryUsedSlots(0),
                "two items costing 1 and 2 slot points must total 3 — a stub returning a flat " +
                "1 per item would total 2 instead");
        }

        [Test]
        public void Validate_RejectsDuplicateItemId()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var items = ScriptableObject.CreateInstance<ItemCatalog>();
            items.Items = new[]
            {
                new ItemDef { Id = 5, Tier = 1, SlotCost = 1, CreditValue = 15, Kind = ItemKind.Trophy },
                new ItemDef { Id = 5, Tier = 2, SlotCost = 2, CreditValue = 60, Kind = ItemKind.Trophy },
            };

            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a, vis, items: items));
            StringAssert.Contains("share Id", ex.Message);
        }

        [Test]
        public void Validate_RejectsZeroSlotCost()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var items = ScriptableObject.CreateInstance<ItemCatalog>();
            items.Items = new[]
            {
                new ItemDef { Id = 0, Tier = 1, SlotCost = 0, CreditValue = 15, Kind = ItemKind.Trophy },
            };

            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a, vis, items: items));
            StringAssert.Contains("SlotCost must be > 0", ex.Message);
        }

        [Test]
        public void Validate_RejectsCatalogOver255Entries()
        {
            // Spec §3.7: "каталог ограничен 255 позициями" — the wire's own
            // byte Id. 256 unique ids (0..255) is one past the cap.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var items = ScriptableObject.CreateInstance<ItemCatalog>();
            var overCap = new ItemDef[256];
            for (int i = 0; i < overCap.Length; i++)
                overCap[i] = new ItemDef { Id = (byte)i, Tier = 1, SlotCost = 1, CreditValue = 1, Kind = ItemKind.Trophy };
            items.Items = overCap;

            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a, vis, items: items));
            StringAssert.Contains("at most 255", ex.Message);
        }

        [Test]
        public void Find_UnknownId_ThrowsNamingIdAndCatalogSize()
        {
            // Coordinator ledger (R-64 precedent, fork on R-89): the message
            // names BOTH the id that failed to resolve and the catalog's
            // own size, not a bare exception.
            var catalog = new[]
            {
                new ItemDef { Id = 0, Tier = 1, SlotCost = 1, CreditValue = 15, Kind = ItemKind.Trophy },
            };

            var ex = Assert.Throws<System.ArgumentException>(() => ItemCatalogLookup.Find(7, catalog));
            StringAssert.Contains("7", ex.Message);
            StringAssert.Contains("1", ex.Message); // catalog's own size (one entry)
        }

        // --- Stage 3 Task 16 (spec §3.7, coordinator R-124/R-130) ---

        /// R-130: the "one trophy per tier" rule lives in ValidateItems
        /// (not ValidateLoot) — without it FindByTier's "first match" is
        /// an unstated ordering rule, and the .asset's own record order
        /// would silently decide a game outcome (R-91's own open
        /// question about a second tier-1 trophy).
        [Test]
        public void Validate_RejectsDuplicateTrophyTier()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var items = ScriptableObject.CreateInstance<ItemCatalog>();
            // Coordinator fix-round (Ф3 review C1, hygiene renumber —
            // mechanism unchanged): ids 1/2, not 0/1 — no new code should
            // use the now-reserved Id 0, even in a fixture Build() would
            // reject for an unrelated reason first if it did.
            items.Items = new[]
            {
                new ItemDef { Id = 1, Tier = 1, SlotCost = 1, CreditValue = 15, Kind = ItemKind.Trophy },
                new ItemDef { Id = 2, Tier = 1, SlotCost = 2, CreditValue = 60, Kind = ItemKind.Trophy },
            };

            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a, vis, items: items));
            StringAssert.Contains("Tier", ex.Message);
        }

        [Test]
        public void FindByTier_ReturnsTheTiersOwnRecord()
        {
            var catalog = TestConfigs.Default().Items;
            ItemDef found = ItemCatalogLookup.FindByTier(2, catalog);
            Assert.AreEqual(2, found.Id, "tier 2 must resolve to TestConfigs' own Id=2 record");
        }

        [Test]
        public void FindByTier_UnknownTier_ThrowsNamingTierAndCatalogSize()
        {
            var catalog = new[]
            {
                new ItemDef { Id = 0, Tier = 1, SlotCost = 1, CreditValue = 15, Kind = ItemKind.Trophy },
            };

            var ex = Assert.Throws<System.ArgumentException>(() => ItemCatalogLookup.FindByTier(9, catalog));
            StringAssert.Contains("9", ex.Message);
            StringAssert.Contains("1", ex.Message); // catalog's own size (one entry)
        }

        [Test]
        public void FindRepairKit_ReturnsTheRepairKitRecord()
        {
            var catalog = TestConfigs.Default().Items;
            ItemDef found = ItemCatalogLookup.FindRepairKit(catalog);
            Assert.AreEqual(5, found.Id, "the repair kit must resolve to TestConfigs' own Id=5 record");
        }

        /// Coordinator fix-round (Ф3 review C1): 0 is the container slot's
        /// own "empty" sentinel (SimulationWorld.TryTakeFromContainer) — a
        /// catalog record claiming Id 0 would be permanently unreachable
        /// through the one take shim in the codebase. Same ValidateItems
        /// home as the duplicate-id/duplicate-tier/255-cap rules.
        [Test]
        public void Validate_RejectsZeroId()
        {
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            var items = ScriptableObject.CreateInstance<ItemCatalog>();
            items.Items = new[]
            {
                new ItemDef { Id = 0, Tier = 1, SlotCost = 1, CreditValue = 15, Kind = ItemKind.Trophy },
            };

            var ex = Assert.Throws<System.ArgumentException>(
                () => SimConfigBuilder.Build(h, w, c, g, wv, a, vis, items: items));
            StringAssert.Contains("must not be 0", ex.Message);
        }
    }
}
