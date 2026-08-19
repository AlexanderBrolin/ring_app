using NUnit.Framework;
using Ring.Simulation.Core;
using Ring.Simulation.Loot;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 17 (spec §3.8, Р234/Р239/Р265/Р266): the server side of
    /// looting — `LootOps.Validate`'s nine checks with their eleven refusal
    /// codes, `LootOps.Begin`'s transfer channel, and `LootOps.Update`'s tick.
    ///
    /// A file of its own rather than more of LootContainerTests (coordinator
    /// decision D-5): spec §4 lists the loot checks under LootContainerTests,
    /// but that list was written when there were FIVE checks and no LootOps —
    /// Р265 grew them to nine, and that file's own twenty tests are about the
    /// container as an ENTITY (spawn, TTL, placement, content). Splitting by
    /// subject is this project's own convention (ConfigTests/ZoneConfigTests/
    /// ItemCatalogTests are separated exactly this way); the divergence from
    /// §4's wording is recorded in the task's decision log.
    ///
    /// TestConfigs.Open() throughout, same reasoning as LootContainerTests
    /// and PickupTests: no obstacles and no waves to interfere. Every
    /// expectation is a FIXTURE EXPRESSION (cfg.Loot.LootRadius,
    /// cfg.Loot.TransferSeconds[i], cfg.Hero.InventoryCapacity), never a
    /// literal copied out of an .asset.
    public class LootOpsTests
    {
        /// A one-player world with the collector standing at the origin, so
        /// every distance in this file is read straight off the container's
        /// own position.
        static SimulationWorld MakeWorld(out SimConfig cfg)
        {
            cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            TestWorlds.RelocatePlayerForTest(w, 0, float2.zero);
            return w;
        }

        /// The one input state every legal request needs (spec §3.8 check 2).
        /// Nothing else on SimInput is read by Validate.
        static SimInput WindowOpen() => new SimInput { InventoryOpen = true };

        // ------------------------------------------------------- 1. positives

        [Test]
        public void Validate_Ok_ForLegalTake()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });

            Assert.AreEqual(LootRefusal.None,
                LootOps.Validate(w, 0, LootOp.Take, id, 0, WindowOpen()),
                "a live collector, a stocked container in reach, an empty backpack — a legal Take");
        }

        /// The Drop/Use branch needs a positive of its own: an implementation
        /// that ran the Take-only checks (container, distance, slot points)
        /// for every op would refuse this while every negative test still
        /// passed.
        [Test]
        public void Validate_Ok_ForLegalDrop()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 1);

            Assert.AreEqual(LootRefusal.None,
                LootOps.Validate(w, 0, LootOp.Drop, 0, 0, WindowOpen()),
                "Drop is addressed by the backpack: containerId is not read, and the Take-only checks must not fire");
        }

        [Test]
        public void Validate_Ok_ForLegalUse()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5); // the repair kit

            Assert.AreEqual(LootRefusal.None,
                LootOps.Validate(w, 0, LootOp.Use, 0, 0, WindowOpen()),
                "a collector standing still may Use a repair kit — the positive side of the DashingOrSliding branch");
        }

        // ------------------------------------------- 2. the eleven refusals

        [Test]
        public void Validate_RefusesDeadPlayer()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });
            w.KillPlayerNoDamage(0);
            Assert.IsFalse(w.PlayerAt(0).Alive, "premise: the collector must actually be dead");

            Assert.AreEqual(LootRefusal.DeadOrExtracted,
                LootOps.Validate(w, 0, LootOp.Take, id, 0, WindowOpen()));
        }

        /// The OTHER half of check 1's `!Alive || Extracted`. Alive stays
        /// TRUE here on purpose — an extracted collector is not a corpse, and
        /// an implementation that only tested Alive would pass the test above
        /// and fail this one.
        [Test]
        public void Validate_RefusesExtractedPlayer()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });
            PlayerState p = w.PlayerAt(0);
            p.Extracted = true;
            w.SetPlayerForTest(0, p);
            Assert.IsTrue(w.PlayerAt(0).Alive, "premise: extracted, but NOT dead — the other half of check 1");

            Assert.AreEqual(LootRefusal.DeadOrExtracted,
                LootOps.Validate(w, 0, LootOp.Take, id, 0, WindowOpen()));
        }

        /// Р265 item 2 — the check that makes the PRICE of looting (slowdown,
        /// no weapon) server-enforced instead of honor-system. Without it a
        /// modified client loots at full speed while shooting, which is a
        /// CR 3 violation, not a cosmetic one.
        [Test]
        public void Validate_RefusesWhenWindowFlagIsDown()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });

            Assert.AreEqual(LootRefusal.WindowClosed,
                LootOps.Validate(w, 0, LootOp.Take, id, 0, new SimInput()),
                "the window is down in this tick's input — refused even though everything else is legal");
        }

        [Test]
        public void Validate_RefusesUnknownOp()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });
            // A backpack with one item, so that this fixture violates ONLY the
            // op check: an unknown op falling through to the Drop/Use branch
            // would find a legal inventory index and answer None, which is
            // exactly what the mutation on this check must be able to show.
            w.SetInventoryForTest(0, 1);

            Assert.AreEqual(LootRefusal.UnknownOp,
                LootOps.Validate(w, 0, (LootOp)7, id, 0, WindowOpen()),
                "an unknown Op is refused out loud, not ignored (spec §3.8 check 3)");
        }

        [Test]
        public void Validate_RefusesMissingContainer()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });
            Assert.AreNotEqual(999, id, "premise: 999 must not collide with a live container id");

            Assert.AreEqual(LootRefusal.NoSuchContainer,
                LootOps.Validate(w, 0, LootOp.Take, 999, 0, WindowOpen()));
        }

        /// Spec §3.8 check 5 and the ASSUMPTION+ADDRESSEE that
        /// SimulationWorld.TryTakeFromContainer's own doc addresses to THIS
        /// task: `Slot` arrives as an untrusted byte and a value past the
        /// container's block reads (and on a successful take would zero) a
        /// NEIGHBORING container's slot.
        ///
        /// Coordinator R-151: the bound is checked BEFORE the content, so the
        /// answer must be SlotOutOfRange, never SlotEmpty — the two codes stay
        /// distinct exactly as the spec lists them. The two ends of the range
        /// are separate assert lines on purpose: one witness covering both
        /// would hide a mutation that drops only the lower bound.
        [Test]
        public void Validate_RefusesSlotOutOfRange()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            Assert.AreEqual(2, w.Containers[0].SlotCount, "premise: this container owns exactly two slots");

            Assert.AreEqual(LootRefusal.SlotOutOfRange,
                LootOps.Validate(w, 0, LootOp.Take, id, 255, WindowOpen()),
                "upper bound: 255 addresses past this container's own block — that is out of range, not empty");
            Assert.AreEqual(LootRefusal.SlotOutOfRange,
                LootOps.Validate(w, 0, LootOp.Take, id, -1, WindowOpen()),
                "lower bound: a negative slot would read BEFORE the block");
        }

        /// The SECOND slot is the subject (lesson 227): emptying slot 0 would
        /// leave an implementation that always reads offset 0 passing.
        [Test]
        public void Validate_RefusesEmptySlot()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            Assert.IsTrue(w.TryTakeFromContainer(id, 1, out byte taken),
                "premise: slot 1 must start non-empty, so this test empties it rather than assuming it");
            Assert.AreEqual(2, taken, "premise: slot 1 held the second item");

            Assert.AreEqual(LootRefusal.SlotEmpty,
                LootOps.Validate(w, 0, LootOp.Take, id, 1, WindowOpen()));
        }

        [Test]
        public void Validate_RefusesInventoryIndexOutOfRange()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 1, 2);
            Assert.AreEqual(2, w.InventoryCountOf(0), "premise: two items carried, so indices 0 and 1 are legal");

            Assert.AreEqual(LootRefusal.InventoryIndexOutOfRange,
                LootOps.Validate(w, 0, LootOp.Drop, 0, 2, WindowOpen()),
                "upper bound: index == Count is already past the end of the backpack");
            Assert.AreEqual(LootRefusal.InventoryIndexOutOfRange,
                LootOps.Validate(w, 0, LootOp.Drop, 0, -1, WindowOpen()),
                "lower bound: a negative backpack index");
        }

        /// The boundary is INCLUSIVE — spec §3.8 check 7 says `<= LootRadius`.
        /// Both sides of it are asserted, so a `<` written where `<=` belongs
        /// has somewhere to show.
        [Test]
        public void Validate_RefusesWhenTooFar()
        {
            var w = MakeWorld(out SimConfig cfg);
            float radius = cfg.Loot.LootRadius;
            int onEdge = w.SpawnContainer(ContainerKind.Crate, new float2(radius, 0f), new byte[] { 1 });
            int beyond = w.SpawnContainer(ContainerKind.Crate, new float2(radius + 0.01f, 0f), new byte[] { 1 });

            Assert.AreEqual(LootRefusal.None,
                LootOps.Validate(w, 0, LootOp.Take, onEdge, 0, WindowOpen()),
                "exactly at LootRadius counts as in reach — the spec says <=, not <");
            Assert.AreEqual(LootRefusal.TooFar,
                LootOps.Validate(w, 0, LootOp.Take, beyond, 0, WindowOpen()));
        }

        [Test]
        public void Validate_RefusesWhenNotEnoughSlots()
        {
            var w = MakeWorld(out SimConfig cfg);
            // Two tier-4 trophies cost 4 slot points each — exactly the
            // backpack's whole capacity, stated as a fixture expression.
            w.SetInventoryForTest(0, 4, 4);
            Assert.AreEqual(cfg.Hero.InventoryCapacity, w.InventoryUsedSlots(0),
                "premise: the backpack must be exactly full before the refusal is meaningful");
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });

            Assert.AreEqual(LootRefusal.NotEnoughSlots,
                LootOps.Validate(w, 0, LootOp.Take, id, 0, WindowOpen()));
        }

        [Test]
        public void Validate_RefusesWhenTransferAlreadyRunning()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });
            PlayerState p = w.PlayerAt(0);
            p.LootTimer = cfg.Loot.TransferSeconds[0];
            w.SetPlayerForTest(0, p);

            Assert.AreEqual(LootRefusal.Busy,
                LootOps.Validate(w, 0, LootOp.Take, id, 0, WindowOpen()),
                "a transfer is already running — a second one cannot start (spec §3.8 check 9)");
        }

        /// Spec §3.8: "`Use` additionally requires not dashing AND not
        /// sliding — both close the window, and an asymmetry would be a
        /// hole." Two tests, because the condition is two branches.
        [Test]
        public void Validate_RefusesUse_WhileDashing()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);
            PlayerState p = w.PlayerAt(0);
            p.DashTimer = cfg.Hero.DashDuration;
            w.SetPlayerForTest(0, p);

            Assert.AreEqual(LootRefusal.DashingOrSliding,
                LootOps.Validate(w, 0, LootOp.Use, 0, 0, WindowOpen()));
        }

        [Test]
        public void Validate_RefusesUse_WhileSliding()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);
            PlayerState p = w.PlayerAt(0);
            p.SlideTimer = cfg.Hero.SlideDuration;
            w.SetPlayerForTest(0, p);

            Assert.AreEqual(LootRefusal.DashingOrSliding,
                LootOps.Validate(w, 0, LootOp.Use, 0, 0, WindowOpen()));
        }

        // ------------------------------------------------------- 3. Begin

        /// Tier THREE is the subject, not tier one (lesson 227): an
        /// implementation that always read TransferSeconds[0] would pass a
        /// tier-one fixture by coincidence.
        [Test]
        public void Begin_SetsTheChannelFromTheItemTier()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 3 });

            LootOps.Begin(w, 0, LootOp.Take, id, 1);

            PlayerState p = w.PlayerAt(0);
            Assert.AreEqual(cfg.Loot.TransferSeconds[2], p.LootTimer, 1e-6f,
                "a tier-3 item takes TransferSeconds[tier - 1], not the first entry");
            Assert.AreEqual(id, p.LootTargetContainerId);
            Assert.AreEqual(1, p.LootTargetSlot, "the target records the slot too, not just the container");
        }

        /// Р266 (finding D-17): containers are swap-removed, so an INDEX
        /// stored at Begin would point at a different container by the time
        /// the transfer finishes — and all nine checks would honestly pass on
        /// that stranger. The fixture makes index and id disagree on purpose.
        [Test]
        public void Begin_RecordsTheContainerIdNotItsArrayIndex()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });
            int second = w.SpawnContainer(ContainerKind.Crate, new float2(-1f, 0f), new byte[] { 1 });
            w.RemoveContainerAt(0); // the second container swaps into position 0
            Assert.AreEqual(second, w.Containers[0].Id,
                "premise: the surviving container now lives at INDEX 0 while keeping its own Id");

            LootOps.Begin(w, 0, LootOp.Take, second, 0);

            Assert.AreEqual(second, w.PlayerAt(0).LootTargetContainerId,
                "Р266: the channel stores the container Id, never its array position");
        }

        /// Coordinator decision D-1, RECORDED SIMPLIFICATION: the repair kit
        /// sits outside the tier ladder (ItemDef.Tier == 0, spec §3.7), and
        /// spec §3.8 only gives transfer times for tiers 1..4. A transfer time
        /// of its own cannot be introduced here — that would be a balance
        /// number living in code (CR 6) and the phase's data-delivery gate
        /// (Т13) is spent — so the tier is clamped into [1, 4] and the kit
        /// borrows tier one's, the cheapest. Whether the repair kit deserves
        /// its own TransferSeconds entry is an OPEN OWNER QUESTION for
        /// milestone В1, recorded rather than quietly decided here.
        [Test]
        public void Begin_RepairKitUsesTheFirstTierTransferTime()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 5 });

            LootOps.Begin(w, 0, LootOp.Take, id, 0);

            Assert.AreEqual(cfg.Loot.TransferSeconds[0], w.PlayerAt(0).LootTimer, 1e-6f);
        }

        /// R-152: only `Take` owns a transfer channel in this task. Dropping
        /// and using are Т18's and Т19's own behavior, and a silent no-op here
        /// would read as an executed operation — the named refusal says whose
        /// work is missing, the same shape SimulationWorld.SpawnContainer
        /// (R-99) and Geometry.ZoneSpawnRingRadius (R-64) already use.
        [Test]
        public void Begin_RefusesAnOpWithNoTransferChannel()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 1);

            Assert.Throws<System.ArgumentException>(() => LootOps.Begin(w, 0, LootOp.Drop, 0, 0));
            Assert.Throws<System.ArgumentException>(() => LootOps.Begin(w, 0, LootOp.Use, 0, 0));
        }

        // ------------------------------------------------------ 4. Update

        [Test]
        public void Update_TicksTheChannelDownByOneTick()
        {
            var w = MakeWorld(out SimConfig cfg);
            PlayerState p = w.PlayerAt(0);
            p.LootTimer = 3f * SimulationWorld.TickDt;
            p.LootTargetContainerId = 4;
            p.LootTargetSlot = 2;
            w.SetPlayerForTest(0, p);

            LootOps.Update(w);

            PlayerState after = w.PlayerAt(0);
            Assert.AreEqual(2f * SimulationWorld.TickDt, after.LootTimer, 1e-6f);
            Assert.AreEqual(4, after.LootTargetContainerId, "an unfinished channel keeps its target");
            Assert.AreEqual(2, after.LootTargetSlot);
        }

        [Test]
        public void Update_ClosesTheChannelOnTheTickItExpires()
        {
            var w = MakeWorld(out SimConfig cfg);
            PlayerState p = w.PlayerAt(0);
            // Exactly one tick left, so the countdown lands on a bit-exact
            // zero rather than a residue this assert would have to tolerate.
            p.LootTimer = SimulationWorld.TickDt;
            p.LootTargetContainerId = 4;
            p.LootTargetSlot = 2;
            w.SetPlayerForTest(0, p);

            LootOps.Update(w);

            PlayerState after = w.PlayerAt(0);
            Assert.AreEqual(0f, after.LootTimer, 0f, "the timer lands on zero rather than drifting negative");
            Assert.AreEqual(0, after.LootTargetContainerId, "a closed channel holds no target");
            Assert.AreEqual(0, after.LootTargetSlot);
        }

        /// ⚠ THE GOLDEN GUARD (coordinator R-150, guard class R-120). This
        /// system runs on EVERY tick of both golden scenarios, where nobody
        /// loots: the early exit at `LootTimer <= 0f` is what keeps it inert,
        /// and it must be a NAMED exit taken before any container, catalog or
        /// distance is read — not an accident of "the code happens not to get
        /// there". The stale target here is the witness: without the guard,
        /// the completion branch runs for an idle player every tick and wipes
        /// it.
        [Test]
        public void Update_LeavesAnIdleChannelUntouched()
        {
            var w = MakeWorld(out SimConfig cfg);
            PlayerState p = w.PlayerAt(0);
            p.LootTimer = 0f;
            p.LootTargetContainerId = 7;
            p.LootTargetSlot = 3;
            w.SetPlayerForTest(0, p);

            LootOps.Update(w);

            PlayerState after = w.PlayerAt(0);
            Assert.AreEqual(0f, after.LootTimer, 0f, "an idle timer must not drift negative");
            Assert.AreEqual(7, after.LootTargetContainerId,
                "the early exit must fire BEFORE the completion branch, or that branch wipes the target");
            Assert.AreEqual(3, after.LootTargetSlot);
        }

        // ------------------------------------------------- 5. world wiring

        /// The witness that the system is actually called from TickAll
        /// (R-149: between WaveSystem.Update and ContainerStore.Update) — the
        /// solo Tick overload forwards to TickAll, so this exercises the real
        /// call site rather than LootOps.Update directly.
        [Test]
        public void TickAll_AdvancesAnActiveLootChannel()
        {
            var w = MakeWorld(out SimConfig cfg);
            PlayerState p = w.PlayerAt(0);
            p.LootTimer = 3f * SimulationWorld.TickDt;
            w.SetPlayerForTest(0, p);

            w.Tick(default);

            Assert.AreEqual(2f * SimulationWorld.TickDt, w.PlayerAt(0).LootTimer, 1e-6f,
                "LootOps.Update must be wired into TickAll, not merely callable from tests");
        }

        /// Spec §3.8: death interrupts the transfer. Same "clean corpse read"
        /// contract every other timer in SimulationWorld.KillPlayer follows —
        /// and it matters more here than for a movement timer, because
        /// LootTimer/LootTargetContainerId/LootTargetSlot are HASHED (since
        /// the Т6 re-pin), so a corpse left mid-channel would carry stale
        /// state into the digest and the save.
        [Test]
        public void Death_ClearsTheLootChannel()
        {
            var w = MakeWorld(out SimConfig cfg);
            PlayerState p = w.PlayerAt(0);
            p.LootTimer = cfg.Loot.TransferSeconds[0];
            p.LootTargetContainerId = 4;
            p.LootTargetSlot = 2;
            w.SetPlayerForTest(0, p);

            w.KillPlayerNoDamage(0);

            PlayerState corpse = w.PlayerAt(0);
            Assert.IsFalse(corpse.Alive, "premise: the collector must actually be dead");
            Assert.AreEqual(0f, corpse.LootTimer, 0f);
            Assert.AreEqual(0, corpse.LootTargetContainerId);
            Assert.AreEqual(0, corpse.LootTargetSlot);
        }
    }
}
