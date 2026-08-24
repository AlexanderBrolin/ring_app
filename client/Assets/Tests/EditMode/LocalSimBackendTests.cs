using NUnit.Framework;
using Ring.Presentation;
using Ring.Simulation.Core;
using Ring.Simulation.Loot;

namespace Ring.Simulation.Tests
{
    /// Stage 3 Т32б: the loot surface `ISimBackend` grew, seen from the LOCAL
    /// backend — the half R-229 said could only ever be stubs.
    ///
    /// WHY THE FIXTURE STOPS WHERE IT DOES. This backend builds its own
    /// `SimulationWorld` inside `Restart` and hands it to nobody, so a test
    /// cannot put a box within reach of the player or seed a backpack; those
    /// are `SimulationWorld`'s own tests' subject (`LootOpsTests`,
    /// `LootContainerTests`) and the world reached here is the same one they
    /// exercise. What IS this class's own — the wiring, the remembered
    /// address, the answers before a world exists, and the reset on a new
    /// match — is what is asserted below.
    public class LocalSimBackendTests
    {
        [Test]
        public void LootRequest_BeforeRestart_IsRefusedAsAValue()
        {
            var backend = new LocalSimBackend();

            Assert.IsFalse(backend.Ready, "premise: no world until Restart");
            Assert.IsFalse(backend.TryRequestLoot(LootOp.Take, 1, 0),
                "no world to ask — a refusal, never a throw (Р82's discipline, one layer up)");
            Assert.AreEqual(LootRefusal.None, backend.LastLootRefusal,
                "and nothing was answered, so there is no verdict to show");
        }

        /// The address is remembered WHATEVER the verdict, because the refusal
        /// is drawn on the slot that was pressed.
        ///
        /// AN ID NO BOX CARRIES IS THE CASE THIS FIXTURE CAN REACH, and it is
        /// not a degenerate one: it is what a client sends when the box aged
        /// out (TTL) between the frame that drew it and the click on it. The
        /// world answers `NoSuchContainer` rather than throwing, and the
        /// backend keeps the address so the window knows which slot to color.
        [Test]
        public void LootRequest_RemembersTheAddress_AndTheWorldsVerdict()
        {
            var backend = new LocalSimBackend();
            SimConfig cfg = TestConfigs.Open();
            Assert.IsTrue(backend.Restart(1, cfg));

            // FIRST, WITH THE WINDOW SHUT. `LootOps.Validate` runs its checks
            // in the spec's order and check 2 is the window, so a world whose
            // player never opened one answers WindowClosed and never reaches
            // check 4 — which is itself the proof that the verdict comes from
            // the real validator rather than from a constant.
            Assert.IsTrue(backend.TryRequestLoot(LootOp.Take, 4242, 3),
                "the world was asked — which is all `true` claims on this backend");
            Assert.AreEqual(4242, backend.LootRequestContainerId);
            Assert.AreEqual(3, backend.LootRequestSlot);
            Assert.AreEqual(LootRefusal.WindowClosed, backend.LastLootRefusal,
                "check 2 before check 4: the ordered validator, not a placeholder");
            Assert.IsFalse(backend.LootRequestInFlight,
                "nothing is outstanding: a local world answers inside the call, so there is no "
                + "round trip for the window to dim a slot through");

            // THEN WITH IT OPEN, WHICH IS THE END-TO-END HALF. The flag reaches
            // the world only by riding a tick of this backend's own `Advance`,
            // and `LootOps.Validate` reads the SANITIZED input of that tick —
            // so a backend that dropped `SimInput.InventoryOpen` on the way in
            // would still answer WindowClosed here.
            var open = new SimInput { InventoryOpen = true };
            backend.Advance(open, 0.5f, null);
            Assert.IsTrue(backend.TryRequestLoot(LootOp.Take, 4242, 3));
            Assert.AreEqual(LootRefusal.NoSuchContainer, backend.LastLootRefusal,
                "past check 2 now, and the next thing wrong is the id nothing alive carries");
        }

        /// A new match mints entity ids from 1 again, so an address remembered
        /// across a restart would name a box from the match before — a wrong
        /// answer rather than a missing one, the same reasoning
        /// `MobTypeMemory.Reset` states.
        [Test]
        public void Restart_ForgetsTheLastLootAddressAndVerdict()
        {
            var backend = new LocalSimBackend();
            SimConfig cfg = TestConfigs.Open();
            Assert.IsTrue(backend.Restart(1, cfg));
            Assert.IsTrue(backend.TryRequestLoot(LootOp.Take, 4242, 3));
            Assert.AreEqual(LootRefusal.WindowClosed, backend.LastLootRefusal,
                "premise: there is something to forget");

            Assert.IsTrue(backend.Restart(2, cfg));

            Assert.AreEqual(0, backend.LootRequestContainerId);
            Assert.AreEqual(0, backend.LootRequestSlot);
            Assert.AreEqual(LootRefusal.None, backend.LastLootRefusal);
        }
    }
}
