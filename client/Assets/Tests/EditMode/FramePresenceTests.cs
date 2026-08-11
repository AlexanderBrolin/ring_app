using NUnit.Framework;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 47a (bd `app-2rf`): the render frame carries two per-slot
    /// facts beside `Players` — whether this frame KNOWS the slot's state at
    /// all, and whether the slot is alive in the MATCH rather than merely in
    /// sight. Together they let a consumer tell the three cases a single
    /// `Alive` bit collapses into one: a live player, a body, and a slot this
    /// frame says nothing about.
    ///
    /// EVERY FIXTURE HERE IS A LOCAL WORLD, and that is not a shortcut — it is
    /// where the two flags can be tested at all. The networked backend lives in
    /// `Ring.Presentation.Net`, an assembly this test assembly does not
    /// reference (its own `.asmdef`), so what is pinned below is the contract
    /// both backends fill: a local world knows its whole roster, and a slot
    /// that is KNOWN AND NOT ALIVE is a corpse rather than an absence.
    public class FramePresenceTests
    {
        /// Three seats in an open arena — the shape a real match has, and the
        /// only one where "this slot, not that one" can be asserted at all.
        static SimulationWorld ThreeSeatWorld(out SimConfig cfg)
        {
            cfg = TestConfigs.Open();
            return new SimulationWorld(1, cfg, playerCount: 3);
        }

        [Test]
        public void NewSnapshot_SizesBothFlagArraysToTheWholeRoster()
        {
            SimConfig cfg = TestConfigs.Open();
            var snap = new RenderSnapshot(cfg.Arena);

            // The array index IS the player's slot, so a backend scattering
            // records by their own index must be able to write the last seat of
            // the match even on a frame that carried one record.
            Assert.AreEqual(cfg.Arena.MaxPlayers, snap.PlayerKnown.Length,
                "PlayerKnown must be indexable by any seat of the roster");
            Assert.AreEqual(cfg.Arena.MaxPlayers, snap.PlayerAliveInMatch.Length,
                "PlayerAliveInMatch must be indexable by any seat of the roster");
        }

        [Test]
        public void CaptureSnapshot_KnowsEverySlotOfTheRoster()
        {
            SimulationWorld w = ThreeSeatWorld(out SimConfig cfg);
            var snap = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(snap);

            Assert.AreEqual(3, snap.PlayerCount);
            for (int i = 0; i < snap.PlayerCount; i++)
            {
                Assert.IsTrue(snap.PlayerKnown[i],
                    $"slot {i}: a local world has no fog — every seat's state is in hand");
            }
        }

        [Test]
        public void CaptureSnapshot_AliveInMatchMirrorsTheWorldsOwnRoster()
        {
            SimulationWorld w = ThreeSeatWorld(out SimConfig cfg);
            w.KillPlayerForTest(); // seat 0 only — the seam's own victim
            var snap = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(snap);

            // Asserted per seat rather than as "somebody died": a constant
            // false would satisfy the first line and a constant true the other
            // two, and only the pattern refuses both.
            Assert.IsFalse(snap.PlayerAliveInMatch[0], "seat 0 was killed");
            Assert.IsTrue(snap.PlayerAliveInMatch[1], "seat 1 never took a blow");
            Assert.IsTrue(snap.PlayerAliveInMatch[2], "seat 2 never took a blow");
        }

        [Test]
        public void CaptureSnapshot_ADeadSeatStaysKnown_WhichIsWhatMakesItACorpse()
        {
            SimulationWorld w = ThreeSeatWorld(out SimConfig cfg);
            w.KillPlayerForTest();
            var snap = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(snap);

            // The whole point of the pair: this is the reading that means
            // "a body lies here", and it must not be reachable by a seat the
            // frame knows nothing about.
            Assert.IsTrue(snap.PlayerKnown[0], "the frame carries seat 0's state");
            Assert.IsFalse(snap.Players[0].Alive, "and that state says the player is down");
            Assert.IsFalse(snap.PlayerAliveInMatch[0], "the roster agrees");
        }

        [Test]
        public void CaptureSnapshot_ADeadSeatKeepsThePositionItFellAt()
        {
            SimulationWorld w = ThreeSeatWorld(out SimConfig cfg);
            Unity.Mathematics.float2 standing = w.PlayerAt(0).Pos;
            w.KillPlayerForTest();
            var snap = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(snap);

            // Where it fell, there it lies (owner, 2026-08-10): the frame is
            // what a corpse's position comes from, so a death that moved the
            // body would put every corpse somewhere it never stood.
            Assert.AreEqual(standing.x, snap.Players[0].Pos.x, 1e-4f);
            Assert.AreEqual(standing.y, snap.Players[0].Pos.y, 1e-4f);
        }

        [Test]
        public void CopyFrom_CarriesTheKnownFlagOfEverySlot()
        {
            SimConfig cfg = TestConfigs.Open();
            var from = new RenderSnapshot(cfg.Arena);
            var into = new RenderSnapshot(cfg.Arena);
            from.PlayerCount = 3;
            from.PlayerKnown[0] = true;
            from.PlayerKnown[1] = false;
            from.PlayerKnown[2] = true;
            // The destination starts on the OPPOSITE pattern, so a CopyFrom
            // that forgets the field entirely fails here instead of passing on
            // whatever the recycled buffer happened to hold.
            into.PlayerKnown[0] = false;
            into.PlayerKnown[1] = true;
            into.PlayerKnown[2] = false;

            into.CopyFrom(from);

            Assert.IsTrue(into.PlayerKnown[0]);
            Assert.IsFalse(into.PlayerKnown[1], "a slot the source knew nothing about must stay unknown");
            Assert.IsTrue(into.PlayerKnown[2]);
        }

        [Test]
        public void CopyFrom_CarriesTheRosterLivenessOfEverySlot()
        {
            SimConfig cfg = TestConfigs.Open();
            var from = new RenderSnapshot(cfg.Arena);
            var into = new RenderSnapshot(cfg.Arena);
            from.PlayerCount = 3;
            from.PlayerAliveInMatch[0] = false;
            from.PlayerAliveInMatch[1] = true;
            from.PlayerAliveInMatch[2] = false;
            into.PlayerAliveInMatch[0] = true;
            into.PlayerAliveInMatch[1] = false;
            into.PlayerAliveInMatch[2] = true;

            into.CopyFrom(from);

            Assert.IsFalse(into.PlayerAliveInMatch[0]);
            Assert.IsTrue(into.PlayerAliveInMatch[1]);
            Assert.IsFalse(into.PlayerAliveInMatch[2]);
        }

        [Test]
        public void CopyFrom_TheFrozenPairKeepsACorpseACorpse()
        {
            // `SimulationRunner.FreezeRender` deep-copies the live pair through
            // this one routine on every hitstop. A flag it forgot would read
            // "this frame knows nothing about the slot" for the length of the
            // freeze — which is the reading that retires a doll — so a body
            // would blink out of existence on the very hit that killed it.
            SimulationWorld w = ThreeSeatWorld(out SimConfig cfg);
            w.KillPlayerForTest();
            var live = new RenderSnapshot(cfg.Arena);
            var frozen = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(live);

            frozen.CopyFrom(live);

            Assert.IsTrue(frozen.PlayerKnown[0], "the frozen half still knows seat 0");
            Assert.IsFalse(frozen.Players[0].Alive, "and still says it is down — a corpse, not an absence");
            Assert.IsFalse(frozen.PlayerAliveInMatch[0], "the roster fact survives the freeze too");
            Assert.IsTrue(frozen.PlayerKnown[1], "a living neighbour is unaffected");
            Assert.IsTrue(frozen.PlayerAliveInMatch[1]);
        }
    }
}
