using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 10: the edge-request rate limit. A per-KIND gate inside
    /// PlayerMovementSystem.Update drops a Dash/Slide request that arrives less
    /// than Hero.EdgeRequestMinTicks ticks after the same kind's last ACCEPTED
    /// request, and — critically — a dropped request never latches the input
    /// buffer (see RejectedRequest_DoesNotRearmBuffer for why that is the whole
    /// mechanism rather than a detail of it).
    public class EdgeRateLimitTests
    {
        /// The fixture MUST clear the dash cooldown, or every test here would be
        /// measuring Hero.DashCooldown instead of the gate (self-review A-C2).
        /// TestConfigs' own numbers: DashCooldown 1.2 s (36 ticks — longer than
        /// any window below), DashDuration 0.15 s, StaminaMax 100 against
        /// DashStaminaCost 40, i.e. a pool covering exactly two dashes.
        static SimConfig Fixture()
        {
            var cfg = TestConfigs.Open();
            cfg.Hero.EdgeRequestMinTicks = 3;
            cfg.Hero.DashCooldown = 0f;   // else 1.2 s, not the gate, blocks the second dash
            // The dash must stay SHORTER than the gate window; otherwise the
            // dash itself — not the gate — is what spaces the accepted requests
            // out, and every assertion below would hold just as well with the
            // gate ripped out entirely.
            //
            // Stage 2 Task 10, deliberate deviation from the plan's literal
            // `2f / 30f` (recorded in task-10-report.md): a dash of D seconds
            // OWNS ceil(D / TickDt) + 1 ticks — the start tick plus the
            // continuations, the last of which clamps DashTimer to 0 while still
            // applying this tick's dash velocity (same arithmetic
            // DashRicochetTests.Fixture's own comment spells out). So 2f/30f
            // owns ticks 0..2 and covers the 3-tick gate window exactly, making
            // the test pass with or without a gate; 1f/30f owns ticks 0..1 and
            // leaves tick 2 free, where only the gate can still hold a request
            // back. The plan's stated intent ("the dash must not overlap the
            // limiter's window") is what this number honours.
            cfg.Hero.DashDuration = 1f / 30f;
            cfg.Hero.StaminaMax = 1000f;  // stamina must not become the limiter either
            return cfg;
        }

        static SimInput Move => new SimInput { MoveDir = new float2(1f, 0f) };
        static SimInput Dash => new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true };
        static SimInput Slide => new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true };

        const int SpamTicks = 6;

        [Test]
        public void SpammedDash_AcceptedOncePerWindow()
        {
            var w = new SimulationWorld(1, Fixture());
            for (int t = 0; t < SpamTicks; t++) w.Tick(Dash);
            // tick 0 accepted (dash owns ticks 0-1); ticks 1-2 dropped by the
            // gate — tick 2 is the load-bearing one, the dash is already over
            // there and only the gate is still refusing; tick 3 accepted;
            // ticks 4-5 dropped again. Without the gate the request on tick 2
            // would start a third dash inside these six ticks.
            Assert.AreEqual(2, w.Stats.DashesUsed);
        }

        [Test]
        public void RejectedRequests_AreCounted()
        {
            var w = new SimulationWorld(1, Fixture());
            for (int t = 0; t < SpamTicks; t++) w.Tick(Dash);
            // Test-only seam (Stage 2 Task 10): deliberately NOT part of
            // StateHash / WorldSave / MatchStats — a dropped request is
            // diagnostics, not world state (the shipped network counter lands in
            // Stage 2 Task 23/28's NetStats). Every tick of this run carries
            // exactly one request, so accepted + rejected must account for all
            // of them — a fixture expression, not a restated literal.
            Assert.AreEqual(SpamTicks - w.Stats.DashesUsed, w.RejectedEdgeRequestsForTest);
            Assert.Greater(w.RejectedEdgeRequestsForTest, 0,
                "the spam run must actually have had requests dropped");
        }

        [Test]
        public void DashThenSlide_BothAccepted()
        {
            // Р26: the timer is per KIND. A single shared counter would cut the
            // LEGAL dash->slide link — the slide request below lands two ticks
            // after the dash was accepted, i.e. INSIDE a shared window, yet the
            // post-dash window (Hero.PostDashSlideWindow) exists precisely so
            // that slide is allowed to follow.
            var w = new SimulationWorld(1, Fixture());
            w.Tick(Dash);   // tick 0: dash accepted, the DASH counter arms
            w.Tick(Move);   // tick 1: the dash's last owned tick
            w.Tick(Slide);  // tick 2: a shared counter would drop this; a per-kind one accepts it
            w.Tick(Move);   // tick 3: buffered slide starts off the post-dash window
            Assert.AreEqual(1, w.Stats.DashesUsed);
            Assert.AreEqual(1, w.Stats.SlidesUsed,
                "a per-kind gate must not cut the legal dash->slide link");
        }

        [Test]
        public void HonestRhythm_NotThrottled()
        {
            // A player pressing no faster than the window itself must never lose
            // a single request — the gate exists against a spamming client, not
            // against honest input.
            var cfg = Fixture();
            var w = new SimulationWorld(1, cfg);
            const int presses = 4;
            for (int i = 0; i < presses; i++)
            {
                w.Tick(Dash);
                // Fixture expression (C14): the gap IS the configured window.
                for (int gap = 0; gap < cfg.Hero.EdgeRequestMinTicks; gap++) w.Tick(Move);
            }
            Assert.AreEqual(presses, w.Stats.DashesUsed);
            Assert.AreEqual(0, w.RejectedEdgeRequestsForTest,
                "an honest rhythm must not lose a single request to the gate");
        }

        [Test]
        public void RejectedRequest_DoesNotRearmBuffer()
        {
            // THE test that separates a working gate from a powerless one.
            // Hero.DashBufferWindow (0.15 s = 4.5 ticks) is WIDER than the gate
            // window (EdgeRequestMinTicks = 3 ticks), so a gate that refuses the
            // request but still lets it latch the buffer changes nothing at all:
            // the latched request simply arrives a tick later through the buffer.
            // Suppressing that latch is what moves the golden hash, which is why
            // this task is the one that re-pins it.
            var cfg = Fixture();
            var w = new SimulationWorld(1, cfg);
            Assert.Greater(cfg.Hero.DashBufferWindow / SimulationWorld.TickDt,
                cfg.Hero.EdgeRequestMinTicks,
                "fixture sanity: the buffer window must be wider than the gate window, " +
                "otherwise this test proves nothing");

            w.Tick(Dash);  // tick 0: accepted — the dash starts and consumes the buffer
            w.Tick(Dash);  // tick 1: dropped by the gate; the dash's last owned tick
            Assert.AreEqual(0f, w.Player.DashBufferTimer, 1e-6f,
                "a dropped request must not re-arm the dash buffer");
            w.Tick(Dash);  // tick 2: dropped; the dash is over, so only a re-armed buffer could fire
            Assert.AreEqual(1, w.Stats.DashesUsed,
                "a dropped request must not reach the dash through the input buffer");
        }
    }
}
