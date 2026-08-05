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
    ///
    /// Both kinds are covered symmetrically (fix-round 1, I-1): the slide half
    /// of the gate is invisible to the golden hash on two of its three lines —
    /// the StaminaDenied suppression and the rejected-request tally are events
    /// and diagnostics, neither of which is hashed — so without the slide tests
    /// below both could be reverted with every other test still green.
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
            // the test pass with or without a gate; ONE TICK owns ticks 0..1 and
            // leaves tick 2 free, where only the gate can still hold a request
            // back. The plan's stated intent ("the dash must not overlap the
            // limiter's window") is what this honours.
            //
            // Fix-round 1 (I-2): written as TickDt itself, not as the literal
            // 1f/30f it happens to equal today. The property this whole fixture
            // rests on is "the dash lasts exactly ONE tick", not the number
            // 1/30 — a future TickDt of 1/60 would silently turn that literal
            // into two ticks, re-covering the gate window and quietly restoring
            // the very defect this deviation exists to remove.
            cfg.Hero.DashDuration = SimulationWorld.TickDt;
            cfg.Hero.StaminaMax = 1000f;  // stamina must not become the limiter either
            return cfg;
        }

        /// Fixture() with the slide made measurable the same way the dash is:
        /// the run-up gate must not be what refuses a slide (RunUpSeconds 1.18 s
        /// = ~36 ticks would space slides far wider than the gate ever could),
        /// and a slide must own fewer ticks than the gate window, for exactly
        /// the reason spelled out in Fixture() above.
        static SimConfig SlideFixture()
        {
            var cfg = Fixture();
            cfg.Hero.RunUpSeconds = 0f;                        // slide is always run-up-eligible
            cfg.Hero.SlideDuration = SimulationWorld.TickDt;   // one tick, same reasoning as the dash
            return cfg;
        }

        /// SlideFixture() with an empty, non-refilling stamina pool: every
        /// ACCEPTED slide request is then refused on cost and emits exactly one
        /// StaminaDenied, which is what RejectedSlide_EmitsNoStaminaDenied
        /// counts the dropped requests against.
        static SimConfig StarvedSlideFixture()
        {
            var cfg = SlideFixture();
            cfg.Hero.StaminaMax = 0f;          // the world starts the player at StaminaMax
            cfg.Hero.StaminaRegenPerSec = 0f;  // and nothing lifts it back over SlideStaminaCost
            return cfg;
        }

        static SimInput Move => new SimInput { MoveDir = new float2(1f, 0f) };
        static SimInput Dash => new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true };
        static SimInput Slide => new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true };

        const int SpamTicks = 6;

        /// ceil(SpamTicks / EdgeRequestMinTicks) — with a request on every tick
        /// the gate accepts ticks 0, W, 2W, … and drops the rest. A fixture
        /// expression over the configured window (C14), never a restated number:
        /// widening EdgeRequestMinTicks must move the expectation with it, not
        /// turn the tests red.
        static int ExpectedAcceptedUnderSpam(in SimConfig cfg)
            => (SpamTicks + cfg.Hero.EdgeRequestMinTicks - 1) / cfg.Hero.EdgeRequestMinTicks;

        [Test]
        public void SpammedDash_AcceptedOncePerWindow()
        {
            var cfg = Fixture();
            var w = new SimulationWorld(1, cfg);
            for (int t = 0; t < SpamTicks; t++) w.Tick(Dash);
            // tick 0 accepted (the dash owns ticks 0-1); ticks 1-2 dropped by the
            // gate — tick 2 is the load-bearing one, the dash is already over
            // there and only the gate is still refusing; tick 3 accepted; ticks
            // 4-5 dropped again. Without the gate the request on tick 2 would
            // start a third dash inside these six ticks.
            Assert.AreEqual(ExpectedAcceptedUnderSpam(cfg), w.Stats.DashesUsed);
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
        public void SpammedSlide_AcceptedOncePerWindow()
        {
            // Fix-round 1 (I-1): the dash half of the gate was covered from the
            // start, the slide half was not. This is the symmetric twin of
            // SpammedDash_AcceptedOncePerWindow, and it also pins the slide's
            // share of the rejected-request tally — the increment in
            // SimulationWorld.TickMovement that nothing else observes.
            var cfg = SlideFixture();
            var w = new SimulationWorld(1, cfg);
            for (int t = 0; t < SpamTicks; t++) w.Tick(Slide);
            int expected = ExpectedAcceptedUnderSpam(cfg);
            Assert.AreEqual(expected, w.Stats.SlidesUsed);
            Assert.AreEqual(SpamTicks - w.Stats.SlidesUsed, w.RejectedEdgeRequestsForTest,
                "every spammed tick is either an accepted slide or a counted drop");
        }

        [Test]
        public void RejectedSlide_EmitsNoStaminaDenied()
        {
            // Fix-round 1 (I-1): events are not hashed, so a slide gate that
            // dropped the request but still let it emit StaminaDenied would move
            // no golden and break no other test — while flooding Presentation
            // with one denial per tick instead of one per accepted request, and
            // costing the future network counter half its drops.
            var cfg = StarvedSlideFixture();
            var w = new SimulationWorld(1, cfg);
            Assert.Less(w.Player.Stamina, cfg.Hero.SlideStaminaCost,
                "fixture premise: the pool must be too small for a slide, so every " +
                "ACCEPTED request is refused on cost and emits its one StaminaDenied");

            for (int t = 0; t < SpamTicks; t++) w.Tick(Slide);

            Assert.AreEqual(0, w.Stats.SlidesUsed, "fixture premise: no slide may actually start");
            // One denial per ACCEPTED request and not one more. Note the buffer
            // is deliberately NOT cleared on a cost-refused slide (C11 — it keeps
            // retrying while a late regen could still cover the cost), so on a
            // gate that read the raw input this would fire on every one of the
            // SpamTicks ticks instead.
            Assert.AreEqual(ExpectedAcceptedUnderSpam(cfg),
                TestEvents.CountOf(w, SimEventKind.StaminaDenied),
                "a dropped request must emit no StaminaDenied at all");
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
            w.Tick(Move);   // tick 1: the dash's last owned tick — post-dash window opens
            // Tick 2: a shared counter would drop this request; a per-kind one
            // accepts it, and since the post-dash window is still open the
            // buffered slide starts on this very tick (fix-round 1, M-1 — the
            // earlier comment here misplaced the start one tick later).
            w.Tick(Slide);
            w.Tick(Move);   // tick 3: the slide is already running, this just rides it
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
                // Fixture expression (C14): EdgeRequestMinTicks - 1 filler ticks
                // after the request tick, so the FULL period between two
                // requests (this tick plus the filler) is exactly
                // EdgeRequestMinTicks — the tightest legal rhythm. A period of
                // EdgeRequestMinTicks + 1 (looping the full window here) would
                // still pass with the gate widened by one tick: the counter's
                // own countdown supplies the missing tick of slack, so that
                // period cannot tell a W-tick gate from a W+1-tick one.
                for (int gap = 0; gap < cfg.Hero.EdgeRequestMinTicks - 1; gap++) w.Tick(Move);
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
