using NUnit.Framework;
using Ring.Simulation.Core;
using Ring.Simulation.Loot;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Task 17 (spec §3.8, Р234/Р239/Р265/Р266): the server side of
    /// looting — `LootOps.Validate`'s checks with their twelve refusal
    /// codes (eleven at Т17; ItemNotUsable joined them in Т19), `LootOps.Begin`'s transfer channel, and `LootOps.Update`'s tick.
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

        /// A two-player world for the per-slot race (Р267): MakeWorld above is
        /// single-player, and a tie-break has nothing to break without a
        /// second collector. Both stand well inside Loot.LootRadius of the
        /// origin, on opposite sides of it, so nothing but the loop order in
        /// LootOps.Update separates them.
        static SimulationWorld MakeDuo(out SimConfig cfg)
        {
            cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(1f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(-1f, 0f));
            return w;
        }

        /// The per-player input array LootOps.Update takes since Т18 (R-162) —
        /// the completion re-check reads THIS tick's window flag, so the input
        /// is a parameter rather than something the system reaches for.
        static SimInput[] OpenInputs(int playerCount)
        {
            var inputs = new SimInput[playerCount];
            for (int i = 0; i < playerCount; i++) inputs[i] = WindowOpen();
            return inputs;
        }

        /// The same array with every window shut. `default(SimInput)` already
        /// is exactly that; it is spelled out so the fixtures below read as an
        /// intention rather than as a default nobody chose.
        static SimInput[] ClosedInputs(int playerCount) => new SimInput[playerCount];

        static void AdvanceChannel(SimulationWorld w, SimInput[] inputs, int ticks)
        {
            for (int t = 0; t < ticks; t++) LootOps.Update(w, inputs);
        }

        /// Runs player 0's channel until it stops running and returns how many
        /// ticks that took — or -1 if it never stopped inside a generous cap,
        /// so a broken implementation fails an assert instead of hanging the
        /// suite.
        static int TicksToComplete(SimulationWorld w)
        {
            SimInput[] inputs = OpenInputs(w.PlayerCount);
            for (int t = 1; t <= 200; t++)
            {
                LootOps.Update(w, inputs);
                if (w.PlayerAt(0).LootTimer <= 0f) return t;
            }
            return -1;
        }

        /// What every abort has in common: nothing reached the backpack, and
        /// the channel closed anyway — it closes in BOTH outcomes, success and
        /// refusal alike. What the CONTAINER looks like differs per abort, so
        /// each test states that half for itself.
        static void AssertNothingWasCollected(SimulationWorld w, string why)
        {
            Assert.AreEqual(0, w.InventoryCountOf(0), why);
            Assert.AreEqual(0f, w.PlayerAt(0).LootTimer, 0f, "the channel closes on an abort too");
            Assert.AreEqual(0, w.PlayerAt(0).LootTargetContainerId);
            Assert.AreEqual(0, w.PlayerAt(0).LootTargetSlot);
        }

        /// How many LootOps.Update ticks a transfer of each tier REALLY takes.
        /// Computed independently and deliberately NOT derived from
        /// Loot.TransferSeconds or LootTransferTimes.ForTier (lesson 324): a
        /// mutation of that table, or of the `tier - 1` shift, has to move ONE
        /// side of the comparisons below — never both at once.
        ///
        /// The channel counts down by repeated float32 subtraction of TickDt
        /// (1f/30f = 0.03333333507…, a hair ABOVE 1/30), and the literals
        /// {0.3, 0.6, 0.9, 1.2} are themselves inexact in binary32, so
        /// ceil(seconds / TickDt) is NOT the answer for tier one: after NINE
        /// subtractions 0.3f still holds +2.235e-8 s, and it is the TENTH tick
        /// that crosses zero. Tiers 2/3/4 do agree with the naive count
        /// (18 / 27 / 36) — tier one is the trap, which is exactly why the
        /// pair used below is {tier 1, tier 2}.
        const int Tier1Ticks = 10;
        const int Tier2Ticks = 18;

        /// How many LootOps.Update ticks the repair kit's own 2-second
        /// channel REALLY takes — computed independently, deliberately NOT
        /// derived from Loot.RepairKitChannelSeconds (lesson 324, same
        /// discipline Tier1Ticks/Tier2Ticks above already follow): a
        /// mutation of the config number, or of ApplyConfig's own clamp, has
        /// to move ONE side of the comparisons below, never both at once.
        ///
        /// The channel counts down by repeated float32 subtraction of TickDt
        /// (1f/30f = 0.03333333507…), and 2f is itself inexact in binary32
        /// under that arithmetic: after SIXTY subtractions the remainder is
        /// still +5.588e-07 > 0 — one more than the naive ceil(2 / TickDt) =
        /// 60 — and it is the SIXTY-FIRST subtraction that crosses zero,
        /// landing at -3.333e-02. Independently verified (numpy float32,
        /// same IEEE-754 RNE C# uses): 59 -> +3.333389e-02, 60 -> +5.5879e-07,
        /// 61 -> -3.333278e-02.
        const int RepairKitTicks = 61;

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

        // ------------------------------------------- 2. the twelve refusals

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

        /// Stage 3 Т32б: the ONE home of Р238's reach test, asked directly.
        /// `Validate_RefusesWhenTooFar` below asks it through check 7 and the
        /// frame builder asks it over a distance it already measured; this is
        /// the witness for the method itself, and for the one property
        /// neither caller could ever have exposed.
        ///
        /// THE DIAGONAL IS THE POINT OF THE THIRD PAIR. A reach test written
        /// per axis — `abs(dx) <= r && abs(dy) <= r` — accepts a collector
        /// standing at (0.75r, 0.75r), who is 1.06r away and out of reach.
        /// Both call sites happened to measure a true distance, so no existing
        /// test could tell a circle from a square; extracting the comparison
        /// into a method that could be rewritten makes that worth pinning.
        [Test]
        public void WithinLootReach_IsInclusive_AndMeasuresTrueDistance()
        {
            var cfg = TestConfigs.Open();
            float radius = cfg.Loot.LootRadius;

            Assert.IsTrue(LootOps.WithinLootReach(radius, in cfg.Loot),
                "exactly at LootRadius counts as in reach — the spec says <=, not <");
            Assert.IsFalse(LootOps.WithinLootReach(radius + 0.01f, in cfg.Loot),
                "and a hair beyond it does not");

            Assert.IsTrue(
                LootOps.WithinLootReach(float2.zero, new float2(radius, 0f), in cfg.Loot),
                "the position overload measures the same boundary…");
            Assert.IsFalse(
                LootOps.WithinLootReach(float2.zero, new float2(radius + 0.01f, 0f), in cfg.Loot),
                "…on both of its sides");

            // 0.75r on each axis is 1.0607r away: inside the square, outside
            // the circle.
            Assert.IsFalse(
                LootOps.WithinLootReach(float2.zero, new float2(radius * 0.75f, radius * 0.75f),
                    in cfg.Loot),
                "reach is a RADIUS, not a bounding box — each axis is within LootRadius here and "
                + "the collector still is not");
        }

        /// Stage 3 Т32б: the other half of the wire/frame shared arithmetic —
        /// which slots of a box hold something, one bit apiece.
        ///
        /// ASCENDING SLOT ORDER IS THE CONTRACT, and the fixture is chosen so
        /// a mask built in the other direction cannot pass: slots 0 and 3 are
        /// occupied and slots 1, 2 are not, so a reversed build would answer
        /// 0b0001_0010 where 0b0000_1001 is required. Empty means the byte 0
        /// (spec Р229), which is why a zero in the middle is the interesting
        /// case rather than a short array.
        [Test]
        public void OccupancyMaskOf_SetsABitPerOccupiedSlot_Ascending()
        {
            Assert.AreEqual(0b0000_1001,
                LootOps.OccupancyMaskOf(new byte[] { 3, 0, 0, 1 }),
                "bit i means slot i holds something, counting from slot 0 up");
            Assert.AreEqual(0, LootOps.OccupancyMaskOf(new byte[] { 0, 0 }),
                "an emptied box answers zero — which is what 'nothing to take' looks like");
            Assert.AreEqual(0b0000_0001, LootOps.OccupancyMaskOf(new byte[] { 5 }),
                "a one-slot box uses one bit, not a whole byte's worth");
        }

        /// The boundary is INCLUSIVE — spec §3.8 check 7 says `<= LootRadius`.
        /// Both sides of it are asserted, so a `<` written where `<=` belongs
        /// has somewhere to show. Since Т32б the comparison itself lives in
        /// `LootOps.WithinLootReach` (pinned directly above); this stays the
        /// witness that check 7 ASKS it.
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

        /// OWNER DECISION, SETTLED (R-158, 2026-08-19 — supersedes the
        /// coordinator's D-1 simplification, which had recorded this as an
        /// open question for milestone В1; see SimConfig's LootTransferTimes
        /// .ForTier for the same ruling at the production home). The repair
        /// kit sits outside the tier ladder (ItemDef.Tier == 0, spec §3.7)
        /// and spec §3.8 gives transfer times for tiers 1..4 only. The owner
        /// ruled the kit takes the SAME time as the cheapest tier, so the
        /// clamp into [1, 4] is the intended rule rather than a stand-in:
        /// no TransferSeconds entry of its own is coming, and no balance
        /// number is owed here (which is also what keeps CR 6 satisfied
        /// without spending the phase's exhausted data-delivery gate).
        [Test]
        public void Begin_RepairKitUsesTheFirstTierTransferTime()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 5 });

            LootOps.Begin(w, 0, LootOp.Take, id, 0);

            Assert.AreEqual(cfg.Loot.TransferSeconds[0], w.PlayerAt(0).LootTimer, 1e-6f);
        }

        /// Stage 3 Task 19 (coordinator review round 1, mandatory fix 1):
        /// RENAMED and RETARGETED, not deleted. `Use` now arms `RepairTimer`
        /// (§2.3 of the plan) and no longer reaches this method's own
        /// `throw` — the old name and body described behavior that no longer
        /// exists, but simply deleting the test would leave the `throw`
        /// branch itself WITHOUT A SINGLE WITNESS: it still guards a
        /// genuinely unrecognized `LootOp` (nothing in production ever
        /// passes one before Т28, but `Begin` does not itself verify that —
        /// same ASSUMES-Validate-already-checked contract every other branch
        /// here documents), and a mutation removing it would pass every test
        /// in this file untouched. `(LootOp)7` is the same sentinel
        /// `Validate_RefusesUnknownOp` already uses for its own check 3.
        [Test]
        public void Begin_ThrowsOnUnrecognizedOp()
        {
            var w = MakeWorld(out SimConfig cfg);

            Assert.Throws<System.ArgumentException>(() => LootOps.Begin(w, 0, (LootOp)7, 0, 0));
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

            LootOps.Update(w, OpenInputs(1));

            PlayerState after = w.PlayerAt(0);
            Assert.AreEqual(2f * SimulationWorld.TickDt, after.LootTimer, 1e-6f);
            Assert.AreEqual(4, after.LootTargetContainerId, "an unfinished channel keeps its target");
            Assert.AreEqual(2, after.LootTargetSlot);
        }

        /// ⚠ Stage 3 Task 18: THIS TEST PINS "THE CHANNEL CLOSES IN BOTH
        /// OUTCOMES", NOT A SUCCESSFUL TRANSFER. The target below is
        /// `LootTargetContainerId = 4`, an id no container in this world
        /// carries, so the completion re-check Т18 added answers
        /// LootRefusal.NoSuchContainer, the operation is canceled SILENTLY —
        /// and the three fields are zeroed all the same. That "all the same"
        /// is the whole subject here: a refusal must not leave a collector
        /// stuck in a channel that can never finish. The successful half lives
        /// in ItemStaysInContainer_UntilTransferCompletes and
        /// TransferCompletes_AfterTierSpecificDelay below; read this one as a
        /// success test and both halves end up unwitnessed.
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

            LootOps.Update(w, OpenInputs(1));

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

            LootOps.Update(w, OpenInputs(1));

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

        // ------------------------------------- 6. completing the transfer

        /// Spec §3.8: while the timer runs the item STAYS in the container —
        /// otherwise two collectors starting at the same moment would each
        /// have "reserved" it, and the race С18/Р236 wants would be settled by
        /// whoever pressed first instead of by who finishes.
        ///
        /// The SECOND slot is the subject (lesson 227): an implementation that
        /// always addressed offset 0 would pass a slot-0 fixture by
        /// coincidence. This test owns the CONTAINER half of the move — the
        /// backpack half belongs to TransferCompletes_AfterTierSpecificDelay
        /// below, so that "does not empty the slot" and "does not fill the
        /// backpack" can never be covered by one witness.
        [Test]
        public void ItemStaysInContainer_UntilTransferCompletes()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            Assert.AreEqual(LootRefusal.None,
                LootOps.Validate(w, 0, LootOp.Take, id, 1, WindowOpen()),
                "premise: the Take this test completes must be legal to begin with");
            LootOps.Begin(w, 0, LootOp.Take, id, 1);
            SimInput[] inputs = OpenInputs(1);

            AdvanceChannel(w, inputs, Tier2Ticks - 1);

            int index = w.IndexOfContainer(id);
            Assert.AreEqual(2, w.ContainerSlotAt(index, 1),
                "the item is still in the container on the LAST tick before the transfer lands");
            Assert.AreEqual(0, w.InventoryCountOf(0), "and it is not in the backpack yet");
            Assert.Greater(w.PlayerAt(0).LootTimer, 0f,
                "premise: the channel must still be running here, or the assert above is vacuous");

            LootOps.Update(w, inputs);

            Assert.AreEqual(0, w.ContainerSlotAt(index, 1),
                "the slot empties ON the completion tick — not before it, and not never");
            Assert.AreEqual(0f, w.PlayerAt(0).LootTimer, 0f,
                "and the timer lands on an EXACT zero: tier two's own residue after 18 subtractions "
                + "is -7.45e-9, so nothing but the explicit reset can produce this");
        }

        /// Spec §3.8 (С20/Р235): the transfer takes the TARGET ITEM's own
        /// tier's time. Two tiers, not one — "the delay depends on the tier" is
        /// not provable from a single duration.
        ///
        /// Tier two is the subject (lesson 227, and the only tier whose count
        /// the naive ceil() happens to get right); tier one is the contrast,
        /// and its count is what proves the arithmetic is real rather than
        /// copied — see Tier1Ticks' own doc. This test owns the BACKPACK half
        /// of the move.
        [Test]
        public void TransferCompletes_AfterTierSpecificDelay()
        {
            var tierTwo = MakeWorld(out SimConfig cfg);
            int twoId = tierTwo.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            LootOps.Begin(tierTwo, 0, LootOp.Take, twoId, 1);

            Assert.AreEqual(Tier2Ticks, TicksToComplete(tierTwo),
                "a tier-2 item takes 0.6 s, which is 18 ticks of TickDt");
            Assert.AreEqual(1, tierTwo.InventoryCountOf(0),
                "and on that tick the item arrives in the backpack");
            Assert.AreEqual(2, tierTwo.InventoryItemAt(0, 0),
                "the item that arrives is the one the target SLOT addressed, not the container's first");

            var tierOne = MakeWorld(out SimConfig _);
            int oneId = tierOne.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            LootOps.Begin(tierOne, 0, LootOp.Take, oneId, 0);

            Assert.AreEqual(Tier1Ticks, TicksToComplete(tierOne),
                "a tier-1 item takes 0.3 s, which is TEN ticks — not the naive ceil(0.3 / TickDt) = 9");
            Assert.AreEqual(1, tierOne.InventoryCountOf(0));
            Assert.AreEqual(1, tierOne.InventoryItemAt(0, 0));

            Assert.AreNotEqual(Tier1Ticks, Tier2Ticks,
                "premise: the two tiers must really differ in tick count, or the pair above says "
                + "nothing at all about the delay depending on the tier");
        }

        /// Spec §3.8 Р267 with С18/Р236: the race for one slot is deliberately
        /// NOT blocked (an exclusive lock is a ready-made griefing move and
        /// breaks ADR-001 §4.1), and two completions landing on the same tick
        /// are resolved BY ASCENDING PLAYER INDEX — which is the loop order in
        /// LootOps.Update itself, not a mechanism of its own. The loser is not
        /// refused at the door: it runs its whole channel and only then finds
        /// the slot empty.
        [Test]
        public void TwoTakesOnSameSlot_LowerIndexWins_OtherGetsSlotEmpty()
        {
            var w = MakeDuo(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, float2.zero, new byte[] { 1, 2 });
            Assert.AreEqual(LootRefusal.None, LootOps.Validate(w, 0, LootOp.Take, id, 1, WindowOpen()),
                "premise: player 0's Take must be legal");
            Assert.AreEqual(LootRefusal.None, LootOps.Validate(w, 1, LootOp.Take, id, 1, WindowOpen()),
                "premise: player 1's Take must be legal too — the race is not blocked up front");

            LootOps.Begin(w, 0, LootOp.Take, id, 1);
            LootOps.Begin(w, 1, LootOp.Take, id, 1);
            SimInput[] inputs = OpenInputs(2);

            AdvanceChannel(w, inputs, Tier2Ticks - 1);

            Assert.Greater(w.PlayerAt(0).LootTimer, 0f,
                "premise: BOTH channels must still be running on the tick before completion, or the "
                + "tie-break is proven vacuously");
            Assert.Greater(w.PlayerAt(1).LootTimer, 0f, "premise: player 1's channel too");
            Assert.AreEqual(w.PlayerAt(0).LootTimer, w.PlayerAt(1).LootTimer, 0f,
                "premise: same item, same tier, so both channels expire on the SAME tick — which is "
                + "the only situation a tie-break has anything to say about");

            LootOps.Update(w, inputs);

            Assert.AreEqual(1, w.InventoryCountOf(0), "the LOWER player index wins the slot (Р267)");
            Assert.AreEqual(2, w.InventoryItemAt(0, 0));
            Assert.AreEqual(0, w.InventoryCountOf(1), "and the loser walks away with nothing");
            Assert.AreEqual(0, w.ContainerSlotAt(w.IndexOfContainer(id), 1),
                "the item now exists exactly once: the container no longer holds it");
            Assert.AreEqual(0f, w.PlayerAt(0).LootTimer, 0f, "both channels close, winner and loser alike");
            Assert.AreEqual(0f, w.PlayerAt(1).LootTimer, 0f);
            Assert.AreEqual(0, w.PlayerAt(1).LootTargetContainerId,
                "the loser's channel leaves no stale target behind either");
        }

        /// Spec §3.8 lists walking out of the radius among the things that
        /// interrupt a transfer. Owner ruling R-160 reads that as NOT
        /// COMPLETING rather than an immediate break: the nine checks run
        /// again on the expiry tick, and a collector who has walked away
        /// fails check 7 (TooFar) there. The spec's own justification for
        /// having a re-check at all — "the collector may have walked away" —
        /// is only meaningful under that reading.
        [Test]
        public void TransferAborts_WhenPlayerWalksOutOfRange()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            LootOps.Begin(w, 0, LootOp.Take, id, 1);
            SimInput[] inputs = OpenInputs(1);

            AdvanceChannel(w, inputs, Tier2Ticks - 1);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(1f + cfg.Loot.LootRadius + 1f, 0f));
            Assert.AreEqual(LootRefusal.TooFar,
                LootOps.Validate(w, 0, LootOp.Take, id, 1, WindowOpen()),
                "premise: from here the very same Take is out of reach");

            LootOps.Update(w, inputs);

            Assert.AreEqual(2, w.ContainerSlotAt(w.IndexOfContainer(id), 1),
                "a collector who walked out of reach leaves the item exactly where it was");
            AssertNothingWasCollected(w, "walking away collects nothing");
        }

        /// Spec §3.8 check 2, read on the EXPIRY tick. The window flag rides
        /// the INPUT (Р239), so the completion re-check has to be looking at
        /// this tick's input — which is why LootOps.Update takes one
        /// (coordinator R-162). This is the only test in the file that can
        /// tell whether that parameter is read at all.
        [Test]
        public void TransferAborts_WhenWindowCloses()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            LootOps.Begin(w, 0, LootOp.Take, id, 1);

            AdvanceChannel(w, OpenInputs(1), Tier2Ticks - 1);
            Assert.AreEqual(LootRefusal.WindowClosed,
                LootOps.Validate(w, 0, LootOp.Take, id, 1, new SimInput()),
                "premise: with the window down the same Take is refused");

            LootOps.Update(w, ClosedInputs(1));

            Assert.AreEqual(2, w.ContainerSlotAt(w.IndexOfContainer(id), 1),
                "a collector who shut the window mid-transfer leaves the item where it was");
            AssertNothingWasCollected(w, "a shut window collects nothing");
        }

        /// Spec §3.8, in DELIBERATE contrast with the extraction channel:
        /// damage does NOT interrupt a transfer, because looting under a wave
        /// would otherwise be physically impossible, where the design wants it
        /// possible but expensively greedy. DEATH still does interrupt it
        /// (Death_ClearsTheLootChannel above), and pinning that asymmetry is
        /// the whole point: without a test, the next reviewer "fixes" it into
        /// symmetry with the extraction channel and nothing objects.
        ///
        /// NON-VACUOUS BY CONSTRUCTION (coordinator requirement, errata class
        /// D-I2): the blow is asserted to have actually landed and to have
        /// been survivable. A fixture where the damage silently did not apply
        /// — i-frames up, victim already dead, a zero amount — would pass this
        /// test for entirely the wrong reason.
        [Test]
        public void DamageDoesNotAbortTransfer()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            LootOps.Begin(w, 0, LootOp.Take, id, 1);
            SimInput[] inputs = OpenInputs(1);

            AdvanceChannel(w, inputs, Tier2Ticks / 2);
            float timerBeforeBlow = w.PlayerAt(0).LootTimer;
            float hpBeforeBlow = w.PlayerAt(0).Hp;
            float blow = cfg.Hero.MaxHp * 0.1f; // a fixture expression, not a balance literal
            w.DamagePlayer(0, ProjectileIds.NoOwner, blow, w.PlayerAt(0).Pos,
                HitZone.Body, new float2(1f, 0f), hitHeight: 0f);

            Assert.AreEqual(hpBeforeBlow - blow, w.PlayerAt(0).Hp, 1e-4f,
                "premise: the blow must actually have landed, or this test proves nothing");
            Assert.IsTrue(w.PlayerAt(0).Alive,
                "premise: and it must not be lethal — death DOES abort the channel, by design");
            Assert.AreEqual(timerBeforeBlow, w.PlayerAt(0).LootTimer, 0f,
                "damage moves the loot channel's timer by exactly nothing");
            Assert.AreEqual(id, w.PlayerAt(0).LootTargetContainerId, "nor its target");

            AdvanceChannel(w, inputs, Tier2Ticks - Tier2Ticks / 2);

            Assert.AreEqual(1, w.InventoryCountOf(0), "and the transfer runs to completion under fire");
            Assert.AreEqual(2, w.InventoryItemAt(0, 0));
        }

        /// The spec's FIRST stated reason for re-checking at all: "the
        /// container may have emptied". Someone else emptied the targeted slot
        /// while this channel was running, so check 4b answers SlotEmpty on the
        /// expiry tick and the operation is canceled SILENTLY: no exception,
        /// no event, the client sees it by the next snapshot (spec §3.8).
        [Test]
        public void ContainerEmptiedMidTransfer_AbortsWithSlotEmpty()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            LootOps.Begin(w, 0, LootOp.Take, id, 1);
            SimInput[] inputs = OpenInputs(1);

            AdvanceChannel(w, inputs, Tier2Ticks - 1);
            Assert.IsTrue(w.TryTakeFromContainer(id, 1, out byte stolen),
                "premise: a third party empties the targeted slot while the channel is still running");
            Assert.AreEqual(2, stolen, "premise: and what they took is the targeted item");
            Assert.AreEqual(LootRefusal.SlotEmpty,
                LootOps.Validate(w, 0, LootOp.Take, id, 1, WindowOpen()),
                "premise: the same Take now answers SlotEmpty");

            LootOps.Update(w, inputs);

            Assert.AreEqual(0, w.ContainerSlotAt(w.IndexOfContainer(id), 1),
                "the emptied slot stays empty — an abort puts nothing back");
            AssertNothingWasCollected(w, "an emptied container yields nothing");
        }

        /// Owner ruling R-160, which nothing else in the suite pins: the nine
        /// checks run ONLY on the tick the timer reaches zero, never every
        /// tick. A collector who steps out of reach mid-channel and is back
        /// inside it by the expiry tick completes the transfer: "interruption"
        /// in spec §3.8 means "does not complete", not an immediate break.
        /// Death is the one exception and it lives in
        /// SimulationWorld.KillPlayer, not here.
        ///
        /// Without this test R-160 is held up by a doc alone, and the poll-
        /// every-tick reading would only cost two Т17 tests that happen to use
        /// unreachable target ids — i.e. it would break by FIXTURE rather than
        /// by defect, which is not coverage.
        [Test]
        public void WalkingOutAndBackMidTransfer_StillCompletes()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            float2 home = w.PlayerAt(0).Pos;
            LootOps.Begin(w, 0, LootOp.Take, id, 1);
            SimInput[] inputs = OpenInputs(1);

            AdvanceChannel(w, inputs, 4);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(1f + cfg.Loot.LootRadius + 1f, 0f));
            Assert.AreEqual(LootRefusal.TooFar,
                LootOps.Validate(w, 0, LootOp.Take, id, 1, WindowOpen()),
                "premise: mid-channel the collector really is out of reach");
            AdvanceChannel(w, inputs, 8);
            TestWorlds.RelocatePlayerForTest(w, 0, home);
            AdvanceChannel(w, inputs, Tier2Ticks - 12);

            Assert.AreEqual(1, w.InventoryCountOf(0),
                "back in reach by the expiry tick, so the transfer completes: the re-check happens "
                + "THERE and only there (R-160)");
            Assert.AreEqual(2, w.InventoryItemAt(0, 0));
            Assert.AreEqual(0, w.ContainerSlotAt(w.IndexOfContainer(id), 1));
        }

        // ------------------------------------------ 6a. ContainerEmptied

        /// Stage 3 Т29: the box is announced EMPTY, and only when it
        /// actually is. The kind's name is a claim about the container —
        /// one fired per take would make "emptied" mean "touched", and the
        /// surface listening for it would flash a box that still holds three
        /// things.
        ///
        /// The SECOND slot is the one taken (lesson 227), so an
        /// implementation that only ever looked at slot 0 cannot pass by
        /// coincidence.
        [Test]
        public void TransferTakingTheLastItem_EmitsContainerEmptied()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 0, 1 });
            LootOps.Begin(w, 0, LootOp.Take, id, 1);
            w.ClearEvents();

            Assert.AreNotEqual(-1, TicksToComplete(w), "premise: the transfer must actually finish");
            Assert.AreEqual(1, w.InventoryCountOf(0), "premise: the item really moved");

            int emptied = 0;
            for (int i = 0; i < w.EventCount; i++)
            {
                SimEvent ev = w.GetEvent(i);
                if (ev.Kind != SimEventKind.ContainerEmptied) continue;
                emptied++;
                Assert.AreEqual(id, ev.EntityId, "the event names the box that was emptied");
            }
            Assert.AreEqual(1, emptied, "exactly one ContainerEmptied for the box that just ran dry");
        }

        /// The negative half, and it is the one that gives the kind its
        /// meaning: a take that leaves something behind announces nothing.
        [Test]
        public void TransferLeavingSomethingBehind_EmitsNoContainerEmptied()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1, 2 });
            LootOps.Begin(w, 0, LootOp.Take, id, 0);
            w.ClearEvents();

            Assert.AreNotEqual(-1, TicksToComplete(w), "premise: the transfer must actually finish");
            Assert.AreEqual(1, w.InventoryCountOf(0), "premise: one item moved");

            for (int i = 0; i < w.EventCount; i++)
            {
                Assert.AreNotEqual(SimEventKind.ContainerEmptied, w.GetEvent(i).Kind,
                    "a box that still holds something has not been emptied");
            }
        }

        /// Stage 3 Т29 (review M-4): the predicate's own refusal for an id
        /// no live container carries. UNREACHABLE from its one production
        /// caller — `LootOps.Update` asks about a box it took from one line
        /// earlier — so it is a contract for the NEXT caller rather than a
        /// guard against today, and this file's own rule is that such a
        /// branch is untested code until somebody writes the witness.
        ///
        /// `false`, not `true`: a box that is not there is not an empty box,
        /// and answering otherwise would make the emit above announce the
        /// emptying of a container nobody can point at.
        [Test]
        public void ContainerIsEmpty_AnswersFalseForAnIdNoContainerCarries()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });

            Assert.IsFalse(w.ContainerIsEmpty(id + 1000),
                "an id no live container carries is not an empty container");
            Assert.IsFalse(w.ContainerIsEmpty(id),
                "the positive witness that the fixture could have said true: this one holds an item");
        }

        // --------------------------------------------------------- 7. Drop

        /// Owner ruling R-161: `Drop` opens NO channel — it begins and ends in
        /// the same tick — because Loot.TransferSeconds is indexed by the tier
        /// of an item taken FROM A CONTAINER, while LootTargetContainerId/
        /// LootTargetSlot address a container a discard does not have. Spec
        /// §3.17 keeps the discard as the cheap valve it is: moving items
        /// INTO containers is explicitly not built, a Drop onto the ground
        /// being deemed enough.
        ///
        /// The SECOND carried item is the subject (lesson 227). This test owns
        /// the CONTAINER half; the backpack half is the next test's, so the
        /// two ablations ("no spawn", "no removal") cannot share one witness.
        [Test]
        public void Drop_SpawnsGroundContainerWithItem()
        {
            var w = MakeWorld(out SimConfig cfg);
            var here = new float2(2f, -1f);
            TestWorlds.RelocatePlayerForTest(w, 0, here);
            w.SetInventoryForTest(0, 1, 2);
            Assert.AreEqual(LootRefusal.None, LootOps.Validate(w, 0, LootOp.Drop, 0, 1, WindowOpen()),
                "premise: dropping the second carried item must be legal");

            LootOps.Begin(w, 0, LootOp.Drop, 0, 1);

            Assert.AreEqual(1, w.ContainerCount, "the discard becomes a container on the ground");
            Assert.AreEqual(ContainerKind.Ground, w.Containers[0].Kind);
            Assert.AreEqual(here, w.Containers[0].Pos, "at the collector's own feet");
            Assert.AreEqual(1, w.Containers[0].SlotCount,
                "one slot — spec §3.7: the ground container has exactly one, and is born of a discard");
            Assert.AreEqual(2, w.ContainerSlotAt(0, 0),
                "holding the item the backpack index addressed, not the first one carried");
            Assert.AreEqual(0f, w.PlayerAt(0).LootTimer, 0f,
                "R-161: Drop opens no transfer channel — it is finished inside this one tick");
            Assert.AreEqual(0, w.PlayerAt(0).LootTargetContainerId);
        }

        /// The other half of the same operation, kept apart from the container
        /// half on purpose (coordinator F-2): with one test covering both, the
        /// "no spawn" and "no removal" ablations would share a single witness
        /// and neither branch would be separately proven — the same rule that
        /// splits the transfer's own two halves above.
        ///
        /// Removal is a SWAP-remove (Loot.Inventory.TryRemoveAt), so the item
        /// that was NOT dropped is asserted by value, not merely by count.
        [Test]
        public void Drop_TakesTheItemOutOfTheBackpack()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 1, 2);

            LootOps.Begin(w, 0, LootOp.Drop, 0, 1);

            Assert.AreEqual(1, w.InventoryCountOf(0),
                "the dropped item leaves the backpack — it must not exist in two places at once");
            Assert.AreEqual(1, w.InventoryItemAt(0, 0), "and the item that was NOT dropped stays");
        }

        // ------------------------------------------------ 8. RepairKit (Use)

        [Test]
        public void Validate_RefusesUse_OnNonRepairKitItem()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 1); // a tier-1 Trophy, not a repair kit

            Assert.AreEqual(LootRefusal.ItemNotUsable,
                LootOps.Validate(w, 0, LootOp.Use, 0, 0, WindowOpen()),
                "coordinator D-1, the twelfth check: Use on a non-repair-kit item is refused");
        }

        [Test]
        public void Validate_RefusesTake_WhileRepairKitChannelRuns()
        {
            var w = MakeWorld(out SimConfig cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(1f, 0f), new byte[] { 1 });
            PlayerState p = w.PlayerAt(0);
            p.RepairTimer = cfg.Loot.RepairKitChannelSeconds;
            w.SetPlayerForTest(0, p);

            Assert.AreEqual(LootRefusal.Busy,
                LootOps.Validate(w, 0, LootOp.Take, id, 0, WindowOpen()),
                "coordinator D-3: one channel per collector — a running repair also blocks a new Take");
        }

        [Test]
        public void Begin_ForUse_ArmsRepairTimerFromConfig()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);

            LootOps.Begin(w, 0, LootOp.Use, 0, 0);

            Assert.AreEqual(cfg.Loot.RepairKitChannelSeconds, w.PlayerAt(0).RepairTimer, 1e-6f,
                "Use arms RepairTimer from the config, not LootTimer");
            Assert.AreEqual(0f, w.PlayerAt(0).LootTimer, 0f, "and leaves the transfer channel untouched");
        }

        /// All positive (healing) fixtures below except DoesNotOverheal set
        /// Hp to exactly this expression — a fixed 10-unit headroom ABOVE the
        /// post-heal value, so the overheal clamp never actually fires here
        /// (that branch is DoesNotOverheal's own, single witness). Without a
        /// shared rule every positive test would start at the default
        /// (MaxHp, full health) and silently exercise the clamp instead of
        /// the heal, conflating two facts one test is supposed to own alone.
        static float HealableHp(in SimConfig cfg) => cfg.Hero.MaxHp - cfg.Loot.RepairKitHealAmount - 10f;

        [Test]
        public void RepairKit_HealsAndIsConsumed()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);
            PlayerState before = w.PlayerAt(0);
            before.Hp = HealableHp(cfg);
            w.SetPlayerForTest(0, before);
            Assert.Less(before.Hp + cfg.Loot.RepairKitHealAmount, cfg.Hero.MaxHp,
                "premise: healing must not hit the ceiling, or this test cannot tell heal from clamp");

            LootOps.Begin(w, 0, LootOp.Use, 0, 0);
            SimInput[] inputs = OpenInputs(1);
            AdvanceChannel(w, inputs, RepairKitTicks - 1);
            Assert.Greater(w.PlayerAt(0).RepairTimer, 0f, "premise: channel must still be running here");

            LootOps.Update(w, inputs);

            Assert.AreEqual(before.Hp + cfg.Loot.RepairKitHealAmount, w.PlayerAt(0).Hp, 1e-4f,
                "the repair kit heals exactly RepairKitHealAmount");
            Assert.AreEqual(0, w.InventoryCountOf(0), "and is consumed");
            Assert.AreEqual(0f, w.PlayerAt(0).RepairTimer, 0f, "the channel closes on completion");
        }

        /// Spec §3.8 check 2 on the REPAIR half of LootOps.Update, mirroring
        /// TransferAborts_WhenWindowCloses above — phase review Ф4, finding
        /// B-1. Update takes THIS tick's sanitized input for BOTH completion
        /// loops (coordinator R-162), but the repair loop's own read had no
        /// witness: every other repair fixture finishes its channel under
        /// OpenInputs, so swapping `in inputs[i]` there for a hardcoded open
        /// window — or dropping check 2 from that path into a lighter copy of
        /// Validate — stayed green across the entire suite.
        /// RepairKit_RefusedWhileDashing does NOT cover it: that one fails
        /// completion on DashingOrSliding, with the window still up.
        [Test]
        public void RepairKit_AbortsWhenWindowCloses()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);
            PlayerState before = w.PlayerAt(0);
            before.Hp = HealableHp(cfg);
            w.SetPlayerForTest(0, before);

            LootOps.Begin(w, 0, LootOp.Use, 0, 0);
            AdvanceChannel(w, OpenInputs(1), RepairKitTicks - 1);
            Assert.Greater(w.PlayerAt(0).RepairTimer, 0f, "premise: the channel must still be running here");
            Assert.AreEqual(LootRefusal.WindowClosed,
                LootOps.Validate(w, 0, LootOp.Use, 0, 0, new SimInput()),
                "premise: with the window down the same Use is refused");

            LootOps.Update(w, ClosedInputs(1));

            Assert.AreEqual(before.Hp, w.PlayerAt(0).Hp, 1e-4f,
                "a collector who shut the window mid-repair is not healed");
            Assert.AreEqual(1, w.InventoryCountOf(0), "and the kit is not consumed");
            Assert.AreEqual(0f, w.PlayerAt(0).RepairTimer, 0f,
                "the channel closes in both outcomes, refusal included");
        }

        /// Spec §3.7, in DELIBERATE contrast with the loot transfer channel
        /// (LootOpsTests.DamageDoesNotAbortTransfer, Т18): a REAL, unabsorbed
        /// hit resets the repair channel immediately — the extraction
        /// channel's own rule (Р222), not the transfer's.
        [Test]
        public void RepairKit_ChannelResetByDamage()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);
            LootOps.Begin(w, 0, LootOp.Use, 0, 0);
            SimInput[] inputs = OpenInputs(1);
            AdvanceChannel(w, inputs, RepairKitTicks / 2);
            Assert.Greater(w.PlayerAt(0).RepairTimer, 0f, "premise: channel must still be running here");
            float hpBeforeBlow = w.PlayerAt(0).Hp;
            float blow = cfg.Hero.MaxHp * 0.1f; // a fixture expression, not a balance literal

            w.DamagePlayer(0, ProjectileIds.NoOwner, blow, w.PlayerAt(0).Pos, HitZone.Body,
                new float2(1f, 0f), hitHeight: 0f);

            Assert.AreEqual(hpBeforeBlow - blow, w.PlayerAt(0).Hp, 1e-4f,
                "premise: the blow must actually have landed, or this test proves nothing");
            Assert.IsTrue(w.PlayerAt(0).Alive, "premise: and it must not be lethal");
            Assert.AreEqual(0f, w.PlayerAt(0).RepairTimer, 0f,
                "damage resets the repair channel immediately — unlike the loot transfer");

            AdvanceChannel(w, inputs, RepairKitTicks);

            Assert.AreEqual(1, w.InventoryCountOf(0), "the repair kit is not consumed by an interrupted channel");
            Assert.AreEqual(hpBeforeBlow - blow, w.PlayerAt(0).Hp, 1e-4f, "nor is the player healed");
        }

        /// Symmetry with Р127 / PvpDamageTests.IframesAbsorbPvpDamage: an
        /// ABSORBED hit (active dash i-frames) must not reach
        /// SimulationWorld.AbortChannels at all — DamagePlayer returns before
        /// that call, exactly as it does before crediting a shooter.
        [Test]
        public void RepairKit_AbsorbedHitDoesNotReset()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);
            // HealableHp, not the default full Hp a fresh world spawns with
            // (SimulationWorld's constructor seeds Hp = Hero.MaxHp): without
            // headroom the completion heal below would clamp at an ALREADY-
            // full Hp, and "heals once it does" would be unfalsifiable —
            // Greater(MaxHp, MaxHp) is false regardless of whether the heal
            // ran at all.
            PlayerState seed = w.PlayerAt(0);
            seed.Hp = HealableHp(cfg);
            w.SetPlayerForTest(0, seed);
            LootOps.Begin(w, 0, LootOp.Use, 0, 0);
            SimInput[] inputs = OpenInputs(1);
            AdvanceChannel(w, inputs, RepairKitTicks / 2);
            float timerBeforeBlow = w.PlayerAt(0).RepairTimer;
            Assert.Greater(timerBeforeBlow, 0f, "premise: channel must still be running here");
            PlayerState p = w.PlayerAt(0);
            p.IframeTimer = cfg.Hero.DashIframes;
            w.SetPlayerForTest(0, p);
            float hpBeforeBlow = w.PlayerAt(0).Hp;

            w.DamagePlayer(0, ProjectileIds.NoOwner, cfg.Hero.MaxHp * 0.1f, w.PlayerAt(0).Pos,
                HitZone.Body, new float2(1f, 0f), hitHeight: 0f);

            Assert.AreEqual(hpBeforeBlow, w.PlayerAt(0).Hp, 1e-4f, "premise: i-frames absorbed the blow");
            Assert.AreEqual(timerBeforeBlow, w.PlayerAt(0).RepairTimer, 0f,
                "an absorbed hit must not reset the channel — same rule as the extraction channel (Р222)");

            AdvanceChannel(w, inputs, RepairKitTicks - RepairKitTicks / 2);

            Assert.AreEqual(0, w.InventoryCountOf(0), "the channel runs to completion under an absorbed hit");
            Assert.AreEqual(hpBeforeBlow + cfg.Loot.RepairKitHealAmount, w.PlayerAt(0).Hp, 1e-4f,
                "and heals exactly RepairKitHealAmount once it does");
        }

        [Test]
        public void RepairKit_DoesNotOverheal()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);
            PlayerState p = w.PlayerAt(0);
            p.Hp = cfg.Hero.MaxHp - cfg.Loot.RepairKitHealAmount * 0.5f; // premise: heal would overshoot
            w.SetPlayerForTest(0, p);
            Assert.Greater(p.Hp + cfg.Loot.RepairKitHealAmount, cfg.Hero.MaxHp,
                "premise: an unclamped heal would exceed MaxHp, or this test cannot tell clamp from coincidence");

            LootOps.Begin(w, 0, LootOp.Use, 0, 0);
            AdvanceChannel(w, OpenInputs(1), RepairKitTicks);

            Assert.AreEqual(cfg.Hero.MaxHp, w.PlayerAt(0).Hp, 1e-4f, "healing clamps at MaxHp, never above it");
        }

        /// The completion tick re-validates through the SAME `Validate` the
        /// Begin-time check used (coordinator §2.4, R-119): starting a dash
        /// BETWEEN Begin and the expiry tick must cancel the heal on
        /// completion, not merely refuse a fresh Begin.
        [Test]
        public void RepairKit_RefusedWhileDashing()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);
            float hpBefore = w.PlayerAt(0).Hp;
            LootOps.Begin(w, 0, LootOp.Use, 0, 0);
            SimInput[] inputs = OpenInputs(1);
            AdvanceChannel(w, inputs, RepairKitTicks - 1);
            Assert.Greater(w.PlayerAt(0).RepairTimer, 0f, "premise: channel must still be running here");
            PlayerState p = w.PlayerAt(0);
            p.DashTimer = cfg.Hero.DashDuration;
            w.SetPlayerForTest(0, p);

            LootOps.Update(w, inputs);

            Assert.AreEqual(hpBefore, w.PlayerAt(0).Hp, 1e-4f, "a dashing collector is not healed on completion");
            Assert.AreEqual(1, w.InventoryCountOf(0), "nor is the repair kit consumed");
            Assert.AreEqual(0f, w.PlayerAt(0).RepairTimer, 0f, "the channel still closes, refusal or not");
        }

        /// Coordinator D-2: `Begin` does not remember WHICH slot armed the
        /// channel (LootTarget* belong to the transfer, not Use) — completion
        /// re-finds a repair kit by KIND. The subject is the SECOND slot
        /// (lesson 227): an implementation that always addressed slot 0
        /// would pass a one-item backpack by coincidence.
        [Test]
        public void RepairKit_FindsTheKitRegardlessOfSlotPosition()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 1, 5); // a Trophy at slot 0, the repair kit at slot 1
            PlayerState p0 = w.PlayerAt(0);
            p0.Hp = HealableHp(cfg);
            w.SetPlayerForTest(0, p0);

            LootOps.Begin(w, 0, LootOp.Use, 0, 1); // the player pointed at slot 1
            AdvanceChannel(w, OpenInputs(1), RepairKitTicks);

            Assert.AreEqual(p0.Hp + cfg.Loot.RepairKitHealAmount, w.PlayerAt(0).Hp, 1e-4f,
                "the kit heals even though completion does not remember slot 1 (coordinator D-2)");
            Assert.AreEqual(1, w.InventoryCountOf(0), "exactly one item remains");
            Assert.AreEqual(1, w.InventoryItemAt(0, 0), "and it is the Trophy — the repair kit is the one consumed");
        }

        /// Same class as LootOpsTests.ContainerEmptiedMidTransfer_AbortsWithSlotEmpty
        /// for the transfer channel: something else consumes the only repair
        /// kit while this channel is running, and completion must cancel
        /// silently rather than crash or heal from nothing.
        [Test]
        public void RepairKit_AbortsIfNoKitRemainsAtCompletion()
        {
            var w = MakeWorld(out SimConfig cfg);
            w.SetInventoryForTest(0, 5);
            float hpBefore = w.PlayerAt(0).Hp;
            LootOps.Begin(w, 0, LootOp.Use, 0, 0);
            Assert.Greater(w.PlayerAt(0).RepairTimer, 0f,
                "premise: the channel must actually be running before it gets robbed of its target");
            SimInput[] inputs = OpenInputs(1);
            AdvanceChannel(w, inputs, RepairKitTicks - 1);
            Assert.IsTrue(w.TryRemoveItemAt(0, 0, out byte removed),
                "premise: something else consumes the only repair kit mid-channel (e.g. a Drop)");
            Assert.AreEqual(5, removed, "premise: and it really was the repair kit");

            LootOps.Update(w, inputs);

            Assert.AreEqual(hpBefore, w.PlayerAt(0).Hp, 1e-4f, "nothing left to consume — no heal");
            Assert.AreEqual(0f, w.PlayerAt(0).RepairTimer, 0f, "the channel still closes");
        }

        /// Symmetry with the existing Death_ClearsTheLootChannel (Т17):
        /// RepairTimer is hashed too (since the Т6 re-pin), so a corpse left
        /// mid-channel would carry stale state into the digest and the save.
        [Test]
        public void Death_ClearsTheRepairChannel()
        {
            var w = MakeWorld(out SimConfig cfg);
            PlayerState p = w.PlayerAt(0);
            p.RepairTimer = cfg.Loot.RepairKitChannelSeconds;
            w.SetPlayerForTest(0, p);

            w.KillPlayerNoDamage(0);

            Assert.IsFalse(w.PlayerAt(0).Alive, "premise: the collector must actually be dead");
            Assert.AreEqual(0f, w.PlayerAt(0).RepairTimer, 0f,
                "a corpse must not carry a running repair channel into the hash/save");
        }

        // ---------------------- 9. Window-flag sanitization (Task 20) ----------------------

        /// A reference PlayerState with none of the four gates armed — Alive,
        /// not Extracted, no dash, no slide — so the flag survives Sanitize
        /// unchanged. Each of the four negative tests below flips exactly ONE
        /// gate off this baseline (rule 227: the baseline itself is never the
        /// subject under test).
        static PlayerState OrdinaryCollector() => new PlayerState { Alive = true };

        /// Spec §3.8/§3.11 (coordinator D-3): the loot window closes the
        /// instant the player is dead, extracted, dashing or sliding —
        /// regardless of what a client claims. Gated in SimInputSanitizer.
        /// Sanitize itself, not only in LootOps.Validate's own checks, because
        /// WeaponSystem.CanFire and PlayerMovementSystem's movement slowdown
        /// both read SimInput.InventoryOpen directly and never go through
        /// Validate at all (that method's own doc explains why). Calls
        /// Sanitize directly, as a pure function — no SimulationWorld needed.
        [Test]
        public void Sanitizer_ClearsWindowFlag_WhileDashing()
        {
            PlayerState reference = OrdinaryCollector();
            reference.DashTimer = 0.1f;
            SimInput result = SimInputSanitizer.Sanitize(WindowOpen(), reference, TestConfigs.Open());
            Assert.IsFalse(result.InventoryOpen, "an active dash must close the loot window");
        }

        [Test]
        public void Sanitizer_ClearsWindowFlag_WhileSliding()
        {
            PlayerState reference = OrdinaryCollector();
            reference.SlideTimer = 0.1f;
            SimInput result = SimInputSanitizer.Sanitize(WindowOpen(), reference, TestConfigs.Open());
            Assert.IsFalse(result.InventoryOpen, "an active slide must close the loot window");
        }

        [Test]
        public void Sanitizer_ClearsWindowFlag_ForADeadPlayer()
        {
            PlayerState reference = OrdinaryCollector();
            reference.Alive = false;
            SimInput result = SimInputSanitizer.Sanitize(WindowOpen(), reference, TestConfigs.Open());
            Assert.IsFalse(result.InventoryOpen, "a dead player cannot hold the loot window open");
        }

        [Test]
        public void Sanitizer_ClearsWindowFlag_ForAnExtractedPlayer()
        {
            PlayerState reference = OrdinaryCollector();
            reference.Extracted = true;
            SimInput result = SimInputSanitizer.Sanitize(WindowOpen(), reference, TestConfigs.Open());
            Assert.IsFalse(result.InventoryOpen, "an extracted player cannot hold the loot window open");
        }

        /// Coordinator D-5 (self-review, task-20-plan §6): the four negative
        /// tests above alone do not distinguish "Sanitize gates the flag
        /// conditionally" from "Sanitize gates the flag unconditionally" —
        /// all four feed a raw window already claimed open AND an active
        /// gate, so an "always clear" stub would pass every one of them
        /// trivially. This is the missing positive: none of the four gates
        /// armed, the flag must survive untouched.
        [Test]
        public void Sanitizer_PreservesWindowFlag_ForAnOrdinaryCollector()
        {
            PlayerState reference = OrdinaryCollector();
            SimInput result = SimInputSanitizer.Sanitize(WindowOpen(), reference, TestConfigs.Open());
            Assert.IsTrue(result.InventoryOpen, "an ordinary collector's open window must survive Sanitize");
        }
    }
}
