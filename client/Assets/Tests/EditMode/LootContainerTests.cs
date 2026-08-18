using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 14 (spec §3.7, Р229): the container as ONE entity type —
    /// spawn/take/read through SimulationWorld, TTL policy through
    /// Loot.ContainerStore. TestConfigs.Open() throughout (same reasoning as
    /// PickupTests' own doc: no obstacle/wave interference); nothing here
    /// touches StateHash directly — that coverage lives in
    /// WorldLifecycleTests (the reflective ContainerState sweep and
    /// ContainerState_IsHashedAndRestoredWithTheSave), next to every other
    /// entity's own hash proof.
    public class LootContainerTests
    {
        [Test]
        public void SpawnedContainer_HoldsGivenItems()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);

            int id = w.SpawnContainer(ContainerKind.Crate, new float2(4f, 0f), new byte[] { 5, 9 });

            Assert.AreNotEqual(-1, id, "premise: the container must actually be created");
            Assert.AreEqual(1, w.ContainerCount);
            ContainerState c = w.Containers[0];
            Assert.AreEqual(ContainerKind.Crate, c.Kind);
            Assert.AreEqual(new float2(4f, 0f), c.Pos);
            Assert.AreEqual(2, c.SlotCount);
            // Reads by the container's own Id (1, since a fresh world's
            // _nextEntityId starts at 1) while the container itself lives
            // at array POSITION 0 — an implementation that (incorrectly)
            // used containerId as the slot-block index directly could not
            // pass this by coincidence.
            Assert.IsTrue(w.TryTakeFromContainer(id, 0, out byte item0));
            Assert.AreEqual(5, item0);
            Assert.IsTrue(w.TryTakeFromContainer(id, 1, out byte item1));
            Assert.AreEqual(9, item1);
        }

        /// Р229: swap-remove must carry the slot BLOCK along with the
        /// struct. Lesson 227 — the SECOND container is the subject: it is
        /// the one that gets relocated into the removed slot's position,
        /// so it is the one whose content can be silently swapped for its
        /// predecessor's stale leftover if the block copy is missing.
        [Test]
        public void SwapRemove_DoesNotTransferSlotsToNeighbour()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);

            w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 5 });
            int secondId = w.SpawnContainer(ContainerKind.Crate, new float2(2f, 0f), new byte[] { 9 });
            Assert.AreEqual(2, w.ContainerCount, "premise: both containers must exist before the removal");

            w.RemoveContainerAt(0); // removes the FIRST container; the second swaps into slot 0

            Assert.AreEqual(1, w.ContainerCount);
            Assert.AreEqual(secondId, w.Containers[0].Id,
                "premise: the second container now occupies array position 0");
            Assert.IsTrue(w.TryTakeFromContainer(secondId, 0, out byte item),
                "the second container's own slot block must have moved WITH it");
            Assert.AreEqual(9, item,
                "…and must still hold ITS OWN item, not the first container's stale leftover");
        }

        [Test]
        public void TakeFromEmptySlot_ReturnsFalse()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 5 });

            Assert.IsTrue(w.TryTakeFromContainer(id, 0, out byte first), "premise: the first take must succeed");
            Assert.AreEqual(5, first);

            bool ok = w.TryTakeFromContainer(id, 0, out byte second);

            Assert.IsFalse(ok, "a slot already taken must read as empty on a second take");
            Assert.AreEqual(0, second, "an empty slot's item must be reported as 0 (спека: 0 = пусто)");
        }

        [Test]
        public void CapReached_SkipsAndCounts()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.MaxContainers = 1; // lesson 227: the SECOND container is the subject, not the first
            var w = new SimulationWorld(1, cfg);

            int firstId = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 5 });
            Assert.AreNotEqual(-1, firstId, "premise: the first container must actually fill the one slot");
            Assert.AreEqual(0, w.WorldStats.ContainerSpawnsSkipped);

            int secondId = w.SpawnContainer(ContainerKind.Crate, new float2(2f, 0f), new byte[] { 9 });

            Assert.AreEqual(-1, secondId, "the second container must be refused once the cap is full");
            Assert.AreEqual(1, w.WorldStats.ContainerSpawnsSkipped);
            Assert.AreEqual(1, w.ContainerCount, "the FIRST container must not be evicted to make room");
            Assert.AreEqual(5, w.ContainerSlotAt(0, 0), "…and it must still be the original one, untouched");
        }

        /// Interfaces doc, dословно: "0 = не истекает (ящик, тайник, труп
        /// сборщика)" — Crate and PlayerCorpse are two of the three
        /// permanent kinds (Cache is the third, sharing the same branch,
        /// no test of its own needed — same code path).
        [Test]
        public void TtlZero_NeverExpires_ForCrateAndPlayerCorpse()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.SpawnContainer(ContainerKind.Crate, new float2(50f, 50f), new byte[] { 5 });
            w.SpawnContainer(ContainerKind.PlayerCorpse, new float2(60f, 60f), new byte[] { 9 });
            Assert.AreEqual(2, w.ContainerCount, "premise: both containers must exist");

            // Well past Loot.ContainerTtlSeconds — proves "permanent" rather
            // than merely "hasn't expired yet".
            int ticks = (int)math.ceil(cfg.Loot.ContainerTtlSeconds / SimulationWorld.TickDt) + 5;
            for (int i = 0; i < ticks; i++) w.Tick(default);

            Assert.AreEqual(2, w.ContainerCount, "Crate/PlayerCorpse containers must never expire");
        }

        [Test]
        public void TtlExpiry_RemovesGroundContainer()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            w.SpawnContainer(ContainerKind.Ground, new float2(50f, 50f), new byte[] { 5 });
            Assert.AreEqual(1, w.ContainerCount, "premise: the container must actually exist");
            ContainerState c = w.Containers[0];
            c.Ttl = SimulationWorld.TickDt * 1.5f; // expires on the SECOND tick, same idiom as PickupTests
            w.SetContainerForTest(0, in c);

            w.Tick(default);
            Assert.AreEqual(1, w.ContainerCount, "premise: must not have expired on the first tick yet");

            w.Tick(default);
            Assert.AreEqual(0, w.ContainerCount, "the container must be gone once its TTL crosses zero");
        }

        /// Coordinator R-99: a block-overflowing spawn must refuse by name,
        /// not write past its own reserved slot block — an unguarded write
        /// would corrupt the NEXT container's own slots on a flat array,
        /// with neither an exception nor any other observable sign at the
        /// call site (the mirror-image of the defect swap-remove's own
        /// block copy exists to prevent, Р229).
        [Test]
        public void ItemsExceedSlotCapacity_ThrowsNamedRefusal()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            var tooMany = new byte[cfg.Arena.MaxContainerSlots + 1];
            for (int i = 0; i < tooMany.Length; i++) tooMany[i] = (byte)(i + 1);

            var ex = Assert.Throws<System.ArgumentException>(
                () => w.SpawnContainer(ContainerKind.Crate, float2.zero, tooMany));

            Assert.That(ex.Message, Does.Contain("MaxContainerSlots"));
        }
    }
}
