using NUnit.Framework;
using Ring.Networking;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// bd `app-mi4` — the two server counters that had no writer at all, so the
    /// post-match log printed permanent zeros where measurements belonged (the
    /// `app-c3m` genre: an instrument that lies). This suite is what stops them
    /// from silently going back to zero.
    public class ServerCounterTests
    {
        // ---- EdgeRequestsRejected: per player, not per match ----------------

        static SimConfig TwoPlayerFixture()
        {
            var c = TestConfigs.Open();
            // A dash that costs nothing to attempt, so the ONLY thing refusing
            // the spam is the edge-request rate limit this counter is about.
            c.Hero.DashStaminaCost = 0f;
            return c;
        }

        static SimInput Dash => new SimInput { DashRequested = true };

        [Test]
        public void DroppedEdgeRequestsAreCountedAgainstTheirOwnPlayer()
        {
            var w = new SimulationWorld(1, TwoPlayerFixture(), playerCount: 2);
            // The SECOND player is the one spamming, on purpose: with player 0
            // in that role, an implementation that credited every drop to slot 0
            // would pass this test unchanged (measured — the mutation survived).
            var inputs = new[] { default(SimInput), Dash };

            for (int t = 0; t < 20; t++) w.TickAll(inputs);

            Assert.Greater(w.RejectedEdgeRequestsFor(1), 0,
                "player 1 spammed the request and the rate limit dropped some");
            Assert.AreEqual(0, w.RejectedEdgeRequestsFor(0),
                "player 0 asked for nothing — a per-connection counter that told him about "
                + "somebody else's drops would be worse than the zero it replaced");
            Assert.AreEqual(w.RejectedEdgeRequestsFor(0) + w.RejectedEdgeRequestsFor(1),
                w.RejectedEdgeRequestsForTest,
                "and the whole-match seam the older tests read stays the sum of the parts");
        }

        // ---- InputOverwritten: an arrival the world never got to take -------

        static SimInput Move(float x) => new SimInput { MoveDir = new float2(x, 0f) };

        [Test]
        public void AnInputTheWorldNeverTookAndANewerOneReplaced_IsCounted()
        {
            var core = new PlayerPredictionCore();

            core.RecordServerInput(10u, Move(1f));
            core.RecordServerInput(11u, Move(-1f));

            Assert.AreEqual(1, core.OverwrittenServerInputs,
                "the first input never reached a world tick — that loss is the number the "
                + "server log printed as zero for the whole of Stage 2");
            Assert.AreEqual(11u, core.LastServerInput.Tick, "and the newer one stands");
        }

        [Test]
        public void AnInputTheWorldTookIsNotCountedWhenTheNextArrives()
        {
            var core = new PlayerPredictionCore();

            core.RecordServerInput(10u, Move(1f));
            core.MarkServerInputTaken();
            core.RecordServerInput(11u, Move(-1f));

            Assert.AreEqual(0, core.OverwrittenServerInputs,
                "an input the tick loop consumed is not overwritten by the next arrival — "
                + "that is the ordinary rhythm of every healthy connection");
        }

        [Test]
        public void TheCounterIsATotal_NotAFlag()
        {
            var core = new PlayerPredictionCore();

            core.RecordServerInput(1u, Move(1f));
            core.RecordServerInput(2u, Move(1f));
            core.RecordServerInput(3u, Move(1f));

            Assert.AreEqual(2, core.OverwrittenServerInputs,
                "two arrivals overwrote an untaken input, and both are lost work");
        }

        [Test]
        public void MarkingTwiceDoesNotInventALoss()
        {
            var core = new PlayerPredictionCore();

            core.RecordServerInput(1u, Move(1f));
            core.MarkServerInputTaken();
            core.MarkServerInputTaken();
            core.RecordServerInput(2u, Move(1f));

            Assert.AreEqual(0, core.OverwrittenServerInputs,
                "the server marks every tick, including ticks that took a repeat of what it "
                + "already had — that must never read as a loss");
        }
    }
}
