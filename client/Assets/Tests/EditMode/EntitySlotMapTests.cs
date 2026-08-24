using NUnit.Framework;
using Ring.Networking.Client;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т32б (bd `app-dut`): the sparse-id to dense-slot table the three
    /// non-player `StalePolicy` instances are indexed through.
    public class EntitySlotMapTests
    {
        [Test]
        public void Claim_IsStableForTheSameId_AndDistinctForOthers()
        {
            var map = new EntitySlotMap(4);

            int a = map.Claim(101);
            int b = map.Claim(102);

            Assert.GreaterOrEqual(a, 0);
            Assert.GreaterOrEqual(b, 0);
            Assert.AreNotEqual(a, b, "two ids may not share a slot — that would be one fade timer "
                + "for two entities");
            Assert.AreEqual(a, map.Claim(101), "and the same id keeps the slot it already has");
            Assert.AreEqual(a, map.Find(101));
        }

        /// The complete scan is the point: a claim that took the first FREE slot
        /// without finishing the search would hand a returning id a second slot,
        /// and the entity would carry two fade timers, one of them always about
        /// to expire.
        ///
        /// THE FIXTURE PUTS A HOLE BEFORE THE ENTRY. Slot 0 is released while
        /// the id under test sits at slot 1, so a first-free-wins claim answers
        /// 0 where 1 is required.
        [Test]
        public void Claim_FindsAnExistingEntryPastAFreeSlot()
        {
            var map = new EntitySlotMap(4);
            int first = map.Claim(101);
            int second = map.Claim(102);
            Assert.AreEqual(0, first, "premise: the first claim takes slot 0");
            Assert.AreEqual(1, second, "premise: and the second takes slot 1");

            map.Release(first);

            Assert.AreEqual(second, map.Claim(102),
                "the existing entry wins over the hole in front of it");
        }

        [Test]
        public void Release_FreesTheSlotForANewId()
        {
            var map = new EntitySlotMap(1);
            int slot = map.Claim(101);
            Assert.AreEqual(-1, map.Claim(102), "premise: a one-slot table is full after one claim");

            map.Release(slot);

            Assert.AreEqual(slot, map.Claim(102), "the freed slot is handed to the next id");
            Assert.AreEqual(-1, map.Find(101), "and the old id is no longer known");
        }

        /// A full table refuses rather than evicting. Evicting would move the
        /// pop from the entity that could not be remembered onto one that
        /// already was — a worse trade, and a silent one.
        [Test]
        public void Claim_WhenFull_RefusesInsteadOfEvicting()
        {
            var map = new EntitySlotMap(2);
            int a = map.Claim(101);
            int b = map.Claim(102);

            Assert.AreEqual(-1, map.Claim(103));
            Assert.AreEqual(a, map.Find(101), "the sitting tenants keep their slots");
            Assert.AreEqual(b, map.Find(102));
        }

        /// Zero is the free marker and cannot be an entity: `SimulationWorld`
        /// mints ids from 1, so nothing legal is refused here.
        [Test]
        public void Claim_RefusesTheFreeMarker()
        {
            var map = new EntitySlotMap(2);
            Assert.AreEqual(-1, map.Claim(EntitySlotMap.NoEntity));
            Assert.AreEqual(-1, map.Find(EntitySlotMap.NoEntity));
            Assert.AreEqual(0, map.Claim(1), "…and a real id still gets slot 0, unspent");
        }

        [Test]
        public void Reset_ForgetsEveryEntry()
        {
            var map = new EntitySlotMap(3);
            map.Claim(101);
            map.Claim(102);

            map.Reset();

            Assert.AreEqual(-1, map.Find(101),
                "a new epoch mints ids from 1 again — a survivor would answer for the match before");
            Assert.AreEqual(0, map.Claim(999), "and every slot is free again");
        }
    }
}
