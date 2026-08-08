using NUnit.Framework;
using Ring.Networking.Client;
using Ring.Simulation.Core;
using Unity.Mathematics;
// AllocatingGCMemory is an extension method (UnityEngine.TestTools.Constraints) —
// a fully-qualified call site doesn't compile (CS1061), so both usings below
// are required by the file, not just convenience imports. The alias shadows
// NUnit's own `Is`, so every other assertion here goes through the classic
// Assert.AreEqual/Greater/Less forms rather than the constraint model.
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 31 (spec §3.9, Р57/Р38): the continuous render clock.
    ///
    /// THE DEFECT THESE TESTS EXIST FOR. Version 2 of the spec ran the clock as
    /// an ANCHOR — `RenderTick = LastAppliedSnapshotTick - InterpBufferTicks`,
    /// recomputed only when a snapshot landed. Time then moved in the arrival
    /// pattern of the network instead of in local time: a lost packet froze the
    /// picture for a whole tick and the next packet advanced it by two, so the
    /// interpolation buffer smoothed away exactly nothing at the 5% loss the
    /// project is required to play well under (Critical Rule 7). Р57 replaced
    /// it with a clock that runs on local delta time and slews towards the
    /// buffered target. `Advance_IsUniformUnderPacketLoss` is the test that
    /// tells the two apart, and its "no freeze-then-leap pair" premise is the
    /// one assertion an anchored implementation cannot satisfy.
    ///
    /// NUMBERS COME FROM FIXTURES, NEVER FROM `.asset` (global constraint Р56):
    /// `Fixture()` below builds `NetTimings` by hand and `Dt` is
    /// `SimulationWorld.TickDt` itself, so no literal `1f / 30f` and no balance
    /// number from an editor asset appears anywhere in this file.
    ///
    /// WHY SEVERAL TESTS MEASURE FROM A CLOCK THAT IS BEHIND ITS TARGET. The
    /// slew is bang-bang by contract — the rate is `1 ± SlewFraction` and
    /// nothing else — so a clock that is AHEAD of its target runs at
    /// `1 - SlewFraction` no matter how far ahead it is. A refusal that was
    /// wrongly accepted (a stale tick, a foreign epoch) would move the target
    /// and still leave the rate unchanged, i.e. be invisible. Measured from
    /// BEHIND the target the same mistake reverses the correction, and shows up
    /// on the very next frame. `CatchingUp` is that fixture.
    public class RenderClockTests
    {
        /// The epoch every fixture tracks unless a test is specifically about
        /// epoch changes.
        const ushort Epoch = 7;

        /// One render frame per world tick — the plan's "a frame is a tick"
        /// reading of the loss test, and the only frame length these tests use.
        /// Expressed through the simulation's own constant so the file carries
        /// no duplicate of the tick rate.
        const float Dt = SimulationWorld.TickDt;

        /// Exactness tolerance for values the clock computes from whole ticks.
        /// Everything the tests below predict is integer arithmetic in double
        /// plus at most a couple of `1 ± SlewFraction` steps, so this is slack
        /// for the float→double widening of `SlewFraction`, not for drift.
        const double Eps = 1e-9;

        /// `Phase` is a float by contract while `RenderTime` is a double, so
        /// the round-trip `RenderTick + Phase` carries a float's worth of
        /// error — a little over 1e-7 in the worst case for a phase just under
        /// one. 1e-6 is that bound with room, and still four orders of
        /// magnitude below the invariant it is checking.
        const double PhaseTolerance = 1e-6;

        /// Slack on the "fraction of steps that are not exactly one tick"
        /// bound of `Advance_IsUniformUnderPacketLoss`. The bound itself is
        /// `SlewFraction`, and it is exact rather than empirical: at
        /// `dt == TickDt` a neutral frame moves `RenderTime` by exactly one and
        /// therefore `RenderTick` by exactly one, so only a slewed frame can
        /// produce a non-unit step, and a run of frames at `1 - SlewFraction`
        /// produces a zero-step on exactly a fraction `SlewFraction` of them.
        /// The epsilon covers the boundary frames of a correction burst.
        const double UniformityEpsilon = 0.01d;

        /// The tick handed to `CatchingUp` — far enough ahead of the armed
        /// clock to demand a correction, far enough inside the snap threshold
        /// that the correction is a slew.
        const uint CatchUpNewest = 108;

        /// The timings the clock is driven with. The three tick counts are the
        /// shipped `NetConfig` defaults restated as test fixtures (Р56: the
        /// asset owns the game's numbers, the fixture owns the test's), and
        /// `SlewFraction` sits in the middle of the 0.05..0.10 band the plan
        /// documents for the field.
        static NetTimings Fixture() => new NetTimings
        {
            InterpBufferTicks = 3,
            InterpMaxStaleTicks = 3,
            RenderClockSnapTicks = 10,
            SlewFraction = 0.08f,
        };

        /// How far one frame moves the clock when nothing is being corrected.
        static double NeutralStep() => Dt / SimulationWorld.TickDt;

        /// ... when the clock is behind its target and catching up.
        static double FastStep(in NetTimings cfg) => NeutralStep() * (1d + cfg.SlewFraction);

        /// ... and when it is ahead of its target and giving time back.
        static double SlowStep(in NetTimings cfg) => NeutralStep() * (1d - cfg.SlewFraction);

        /// A clock tracking `Epoch`, started from ticks 100 and 101, with its
        /// first frame already advanced — i.e. sitting exactly on its target
        /// with no desync at all. Every test that measures a reaction needs a
        /// known starting point, and this asserts that point rather than
        /// assuming it.
        static RenderClock Armed(in NetTimings cfg, out double renderTime)
        {
            var clock = new RenderClock();
            clock.ResetForEpoch(Epoch);
            clock.OnSnapshot(100, Epoch);
            clock.OnSnapshot(101, Epoch);
            Assert.IsTrue(clock.Started, "fixture premise: two distinct ticks start the clock");

            clock.Advance(Dt, in cfg);
            renderTime = clock.RenderTime;
            Assert.AreEqual(101d - cfg.InterpBufferTicks, renderTime, Eps,
                "fixture premise: the first frame after the start places the clock ON its "
                + "target — newest buffered tick minus the interpolation buffer");
            return clock;
        }

        /// An armed clock that has since been handed a target seven ticks ahead
        /// of it and taken one catch-up frame, so it is demonstrably running at
        /// `1 + SlewFraction`. See the class doc for why refusals are measured
        /// from here and not from a clock sitting on its target.
        static RenderClock CatchingUp(in NetTimings cfg, out double renderTime)
        {
            var clock = Armed(cfg, out double start);
            clock.OnSnapshot(CatchUpNewest, Epoch);
            clock.Advance(Dt, in cfg);
            renderTime = clock.RenderTime;
            Assert.AreEqual(start + FastStep(cfg), renderTime, Eps,
                "fixture premise: the clock is behind its target and catching up at "
                + "1 + SlewFraction, so any move of the target is observable next frame");
            return clock;
        }

        /// One frame, with the two invariants that hold on every single frame
        /// of an epoch's life: time never moves backwards, and the tick/phase
        /// split is a faithful decomposition of it.
        static void StepAndAssertInvariants(RenderClock clock, in NetTimings cfg,
            ref double previous, string where)
        {
            clock.Advance(Dt, in cfg);

            Assert.GreaterOrEqual(clock.RenderTime, previous,
                where + ": renderTime is monotonic inside an epoch (spec §3.9)");
            previous = clock.RenderTime;

            Assert.GreaterOrEqual(clock.Phase, 0f, where + ": phase is in [0, 1)");
            Assert.Less(clock.Phase, 1f, where + ": phase is in [0, 1)");
            Assert.AreEqual(clock.RenderTime, clock.RenderTick + (double)clock.Phase,
                PhaseTolerance,
                where + ": RenderTime == RenderTick + Phase — the Р38 decomposition");
        }

        // ---- T31.1. The test the whole task exists for ----

        [Test]
        public void Advance_IsUniformUnderPacketLoss()
        {
            var cfg = Fixture();
            var clock = new RenderClock();
            clock.ResetForEpoch(Epoch);

            // 400 ticks is over thirteen seconds of match at 30 Hz, long enough
            // for roughly twenty losses at the mandated 5% and for any drift
            // the clock accumulates per loss to show up as a trend.
            const int ticks = 400;
            const float lossRate = 0.05f;
            // Unity.Mathematics.Random with a fixed seed, NOT System.Random:
            // the drop pattern must be the same on every machine and every run,
            // or a red build cannot be reproduced.
            var rng = new Unity.Mathematics.Random(0x5EED31u);

            // The clock needs two snapshots to start and a few more frames to
            // settle onto its target; twice the interpolation buffer is the
            // plan's warm-up length.
            int warmup = 2 * cfg.InterpBufferTicks;

            int delivered = 0, dropped = 0;
            int steps = 0, nonUnit = 0, freezeThenLeap = 0;
            int previousDelta = int.MinValue;
            int previousTick = clock.RenderTick;
            double previousTime = clock.RenderTime;

            for (int t = 1; t <= ticks; t++)
            {
                if (rng.NextFloat() < lossRate)
                    dropped++;
                else
                {
                    clock.OnSnapshot((uint)t, Epoch);
                    delivered++;
                }

                clock.Advance(Dt, in cfg);
                Assert.GreaterOrEqual(clock.RenderTime, previousTime,
                    $"tick {t}: renderTime is monotonic inside an epoch");
                previousTime = clock.RenderTime;

                if (t <= warmup)
                {
                    previousTick = clock.RenderTick;
                    continue;
                }

                int delta = clock.RenderTick - previousTick;
                previousTick = clock.RenderTick;
                steps++;
                if (delta != 1) nonUnit++;
                // The anchored clock's signature: a frame that did not move at
                // all because its packet was lost, immediately followed by a
                // frame that moved two ticks because the next packet carried
                // the backlog. A continuous clock cannot produce this pair —
                // the rate would have to cross from below one to above one in a
                // single frame.
                if (previousDelta == 0 && delta == 2) freezeThenLeap++;
                previousDelta = delta;
            }

            // Fixture premises before any conclusion is drawn from the numbers.
            Assert.Greater(dropped, 0,
                "fixture premise: the loss filter must actually drop packets, otherwise "
                + "this test measures a perfect network");
            Assert.Less(dropped, ticks / 5,
                "fixture premise: ~5% loss, not a broken stream");
            Assert.AreEqual(ticks - dropped, delivered, "fixture premise: bookkeeping");
            Assert.AreEqual(ticks - warmup, steps,
                "fixture premise: every post-warm-up frame is measured");
            Assert.GreaterOrEqual(clock.RenderTick, ticks - cfg.InterpBufferTicks - 4,
                "premise: after 400 ticks the clock must be running just behind the newest "
                + "buffered tick — a clock that stalled or never started fails here");
            Assert.LessOrEqual(clock.RenderTick, ticks - cfg.InterpBufferTicks + 1,
                "premise: and it must not have run away from its own target");

            Assert.AreEqual(0, freezeThenLeap,
                "a continuous clock never freezes on a lost packet and never leaps two "
                + "ticks on the next one — that pair IS the v2 anchored clock (Р57)");
            Assert.LessOrEqual((double)nonUnit / steps, cfg.SlewFraction + UniformityEpsilon,
                $"steps that are not exactly one tick ({nonUnit} of {steps}) must stay "
                + "within the slew's own share of frames: at dt == TickDt the only way to "
                + "produce one is to be running at 1 ± SlewFraction");
        }

        // ---- T31.2. Monotonicity, starvation, and the Р38 decomposition ----

        [Test]
        public void Monotonic_WithinEpoch()
        {
            var cfg = Fixture();
            var clock = new RenderClock();
            clock.ResetForEpoch(Epoch);

            double previous = clock.RenderTime;
            uint tick = 1;

            // 1. A healthy stream: one snapshot per frame.
            for (int f = 0; f < 60; f++)
            {
                clock.OnSnapshot(tick++, Epoch);
                StepAndAssertInvariants(clock, in cfg, ref previous, "healthy stream");
            }
            double frozenTarget = (double)(tick - 1) - cfg.InterpBufferTicks;
            Assert.AreEqual(frozenTarget, clock.RenderTime, 1d,
                "premise: a healthy stream leaves the clock within a tick of its target");

            // 2. Global starvation. The server keeps ticking (the `tick`
            //    counter runs on) but nothing arrives, so the clock overruns
            //    its frozen target. 200 frames is long enough for the overrun
            //    to pass the snap threshold even at the slewed-down rate,
            //    which is what makes step 3 a snap rather than a slew.
            const int starvedFrames = 200;
            for (int f = 0; f < starvedFrames; f++)
            {
                tick++;
                StepAndAssertInvariants(clock, in cfg, ref previous, "starvation");
            }
            Assert.Greater(clock.RenderTime - frozenTarget, (double)cfg.RenderClockSnapTicks,
                "premise: the clock really did run more than a snap threshold PAST its "
                + "frozen target — the situation in which a symmetric snap would rewind time");

            // 3. Recovery. The stream resumes at the tick the server has
            //    reached, so the target jumps far ahead in one frame.
            double maxRecoveryStep = 0d;
            for (int f = 0; f < 60; f++)
            {
                double before = clock.RenderTime;
                clock.OnSnapshot(tick++, Epoch);
                StepAndAssertInvariants(clock, in cfg, ref previous, "recovery");
                maxRecoveryStep = math.max(maxRecoveryStep, clock.RenderTime - before);
            }
            Assert.Greater(maxRecoveryStep, 2d,
                "premise: recovery from a starvation this long is a forward SNAP, not a "
                + "multi-second slewed crawl — a slew step cannot exceed 1 + SlewFraction");
            Assert.Greater(clock.RenderTick, 200,
                "positive witness: the clock advanced through the whole scenario instead of "
                + "sitting at zero, where every monotonicity assertion above is free");
        }

        // ---- T31.3. Epoch change is a restart, not a chase ----

        [Test]
        public void Snaps_OnEpochChange()
        {
            var cfg = Fixture();
            const ushort oldEpoch = 9;
            const ushort newEpoch = 10;

            var clock = new RenderClock();
            clock.ResetForEpoch(oldEpoch);
            clock.OnSnapshot(1000, oldEpoch);
            clock.OnSnapshot(1001, oldEpoch);
            clock.Advance(Dt, in cfg);
            for (int f = 0; f < 10; f++)
            {
                clock.OnSnapshot((uint)(1002 + f), oldEpoch);
                clock.Advance(Dt, in cfg);
            }
            Assert.Greater(clock.RenderTime, 1000d,
                "fixture premise: the clock is running deep inside the old epoch");

            // The owner (Task 32) calls this on the Reliable lifecycle message
            // that names the new epoch — a restarted match replays its ticks
            // from zero, so nothing of the old epoch may survive.
            clock.ResetForEpoch(newEpoch);
            Assert.IsFalse(clock.Started,
                "a reset forgets the start too: the new epoch needs its own pair of ticks");
            Assert.AreEqual(0d, clock.RenderTime, Eps,
                "a reset forgets the buffered ticks, so there is no target and no time");
            Assert.AreEqual(0, clock.RenderTick);

            clock.OnSnapshot(4, newEpoch);
            clock.OnSnapshot(5, newEpoch);
            Assert.IsTrue(clock.Started, "two distinct ticks of the NEW epoch start it again");

            clock.Advance(Dt, in cfg);
            Assert.AreEqual(5d - cfg.InterpBufferTicks, clock.RenderTime, Eps,
                "the new epoch starts AT its own target — monotonicity is not required "
                + "across an epoch boundary, and slewing down from tick 1011 to tick 2 "
                + "would take the better part of seven minutes");
        }

        // ---- T31.4. Two DISTINCT snapshots, and a duplicate is not two ----

        [Test]
        public void DoesNotStart_UntilTwoSnapshots()
        {
            var cfg = Fixture();
            var clock = new RenderClock();

            Assert.IsFalse(clock.Started, "a fresh clock has seen nothing");
            clock.Advance(Dt, in cfg);
            Assert.AreEqual(0d, clock.RenderTime, Eps, "Advance before the start is a no-op");

            clock.ResetForEpoch(Epoch);
            Assert.IsFalse(clock.Started, "and a reset is not a start either");

            clock.OnSnapshot(50, Epoch);
            Assert.IsFalse(clock.Started,
                "one snapshot is a position, not the pair interpolation needs");
            clock.Advance(Dt, in cfg);
            Assert.AreEqual(0d, clock.RenderTime, Eps);

            clock.OnSnapshot(50, Epoch);
            Assert.IsFalse(clock.Started,
                "a duplicated datagram carries a tick already counted — two copies of one "
                + "moment are still one moment, and there is nothing to interpolate between");
            clock.Advance(Dt, in cfg);
            Assert.AreEqual(0d, clock.RenderTime, Eps);

            clock.OnSnapshot(51, Epoch);
            Assert.IsTrue(clock.Started,
                "positive witness: a second DISTINCT tick is the pair, and the clock starts");
            clock.Advance(Dt, in cfg);
            Assert.AreEqual(51d - cfg.InterpBufferTicks, clock.RenderTime, Eps,
                "and it starts on its target, not from zero");
        }

        // ---- T31.5. A slew is a rate change, not a teleport ----

        [Test]
        public void Slew_ConvergesToTarget()
        {
            var cfg = Fixture();
            var clock = Armed(cfg, out double start);

            // A gap of two ticks less than the snap threshold: big enough that
            // the clock must correct, small enough that correcting is the
            // slew's job and not the snap's.
            uint newest = (uint)(start + cfg.InterpBufferTicks + cfg.RenderClockSnapTicks - 2);
            double target = newest - cfg.InterpBufferTicks;
            Assert.Less(target - (start + NeutralStep()), (double)cfg.RenderClockSnapTicks,
                "fixture premise: the desync must stay UNDER the snap threshold, or this "
                + "test measures the snap instead of the slew");
            Assert.Greater(target - start, 1d,
                "fixture premise: and it must be big enough to demand a correction at all");
            clock.OnSnapshot(newest, Epoch);

            double maxStep = 0d, previous = clock.RenderTime;
            int frames = 0;
            const int frameCap = 200;
            while (frames < frameCap && math.abs(target - clock.RenderTime) >= 1d)
            {
                clock.Advance(Dt, in cfg);
                Assert.GreaterOrEqual(clock.RenderTime, previous, "a slew never rewinds time");
                maxStep = math.max(maxStep, clock.RenderTime - previous);
                previous = clock.RenderTime;
                frames++;
            }

            Assert.Less(frames, frameCap,
                "the slew converges in a finite number of frames");
            Assert.Greater(frames, 1,
                "and it takes more than one — a single frame that lands on the target is a "
                + "teleport, which is the thing the buffer exists to avoid");
            Assert.Less(math.abs(target - clock.RenderTime), 1d,
                "converged: the residual desync is under one world tick");
            Assert.LessOrEqual(maxStep, FastStep(cfg) + Eps,
                "no frame moved further than the slewed rate allows — the correction speed "
                + "is capped at SlewFraction * dt/TickDt");
        }

        // ---- T31.6. A reordered frame does not drag the target back ----

        [Test]
        public void OutOfOrderSnapshot_DoesNotLowerTarget()
        {
            var cfg = Fixture();
            var clock = CatchingUp(cfg, out double behind);

            // A frame that lost a race with the one behind it, 48 ticks stale.
            clock.OnSnapshot(60, Epoch);
            clock.Advance(Dt, in cfg);
            Assert.AreEqual(behind + FastStep(cfg), clock.RenderTime, Eps,
                "the newest buffered tick is a MAXIMUM: an overtaken frame leaves the target "
                + "where it was, so the clock keeps catching up instead of reversing into a "
                + "slow-down towards a moment it has already shown");

            // Positive witness: a tick that really is newer does move the target.
            clock.OnSnapshot(200, Epoch);
            clock.Advance(Dt, in cfg);
            Assert.AreEqual(200d - cfg.InterpBufferTicks, clock.RenderTime, Eps,
                "positive witness: the target follows a genuinely newer tick");
        }

        // ---- T31.7. One epoch is tracked and only ResetForEpoch changes it ----

        [Test]
        public void ForeignEpochSnapshot_IsRefused()
        {
            var cfg = Fixture();
            const ushort previousEpoch = 4;

            // Before the first ResetForEpoch there is no epoch to compare
            // against, so nothing is trusted — the same discipline EventDedup
            // holds: a snapshot that outran the handshake belongs to a match
            // this client has not been admitted to.
            var unreset = new RenderClock();
            unreset.OnSnapshot(10, Epoch);
            unreset.OnSnapshot(11, Epoch);
            Assert.IsFalse(unreset.Started,
                "snapshots arriving before the lifecycle message start nothing");

            var clock = CatchingUp(cfg, out double behind);

            clock.OnSnapshot(200, previousEpoch);
            clock.Advance(Dt, in cfg);
            Assert.AreEqual(behind + FastStep(cfg), clock.RenderTime, Eps,
                "a wandering packet of another epoch moves nothing and does not switch the "
                + "tracked epoch: one stray datagram must not hand the clock to a match "
                + "this client is not in");

            // Positive witness: the very same tick, in the tracked epoch, does move it.
            clock.OnSnapshot(200, Epoch);
            clock.Advance(Dt, in cfg);
            Assert.AreEqual(200d - cfg.InterpBufferTicks, clock.RenderTime, Eps,
                "positive witness: the refusal was about the epoch, not about the tick");
        }

        // ---- T31.8. The snap threshold, and which side of it the boundary sits ----

        [Test]
        public void SnapForward_OnGapBeyondThreshold()
        {
            var cfg = Fixture();

            // The desync the clock acts on is the error it would have at the
            // END of the frame — where a free-running step would land it
            // against the target it has now. Build a gap of exactly the snap
            // threshold measured that way.
            var atThreshold = Armed(cfg, out double start);
            uint newestAtThreshold = (uint)(start + NeutralStep()
                + cfg.RenderClockSnapTicks + cfg.InterpBufferTicks);
            Assert.AreEqual((double)cfg.RenderClockSnapTicks,
                (newestAtThreshold - cfg.InterpBufferTicks) - (start + NeutralStep()), Eps,
                "fixture premise: this gap is EXACTLY the snap threshold");
            atThreshold.OnSnapshot(newestAtThreshold, Epoch);
            atThreshold.Advance(Dt, in cfg);
            Assert.AreEqual(start + FastStep(cfg), atThreshold.RenderTime, Eps,
                "the boundary is strict: a gap of exactly RenderClockSnapTicks is still "
                + "corrected by slew, and only a LARGER gap snaps");

            var beyond = Armed(cfg, out double start2);
            uint newestBeyond = newestAtThreshold + 1;
            beyond.OnSnapshot(newestBeyond, Epoch);
            beyond.Advance(Dt, in cfg);
            Assert.AreEqual(newestBeyond - cfg.InterpBufferTicks, beyond.RenderTime, Eps,
                "one tick past the threshold the clock jumps onto the target instead of "
                + "spending seconds crawling to it");
            Assert.Greater(beyond.RenderTime, start2 + FastStep(cfg),
                "witness that this really was a snap and not the slew above");
        }

        // ---- T31.9. Ahead of the target: slow down, never rewind ----

        [Test]
        public void NoBackwardSnap_WhenAhead()
        {
            var cfg = Fixture();
            var clock = Armed(cfg, out double start);
            double target = start;

            double previous = clock.RenderTime;
            double lastStep = 0d;
            const int frames = 60;
            for (int f = 0; f < frames; f++)
            {
                clock.Advance(Dt, in cfg);
                Assert.GreaterOrEqual(clock.RenderTime, previous,
                    $"frame {f}: the snap is asymmetric — a clock ahead of its target is "
                    + "slewed down, never snapped back, because renderTime is monotonic "
                    + "inside an epoch");
                lastStep = clock.RenderTime - previous;
                previous = clock.RenderTime;
                // By the fourth frame the overrun is unambiguous and the only
                // correction available to a clock that has run ahead — a slower
                // rate — must be engaged.
                if (f >= 3)
                    Assert.Less(lastStep, NeutralStep(),
                        $"frame {f}: a clock ahead of its target must be running slow");
            }

            Assert.Greater(clock.RenderTime - target, (double)cfg.RenderClockSnapTicks,
                "premise: the clock really is more than a snap threshold ahead of its "
                + "frozen target — a symmetric snap would have rewound it by now");
            Assert.Greater(clock.RenderTime, start,
                "positive witness: it kept running forward the whole time");
            Assert.AreEqual(SlowStep(cfg), lastStep, Eps,
                "and the slowed rate is exactly 1 - SlewFraction");
        }

        // ---- T31.10. A tick the clock cannot represent is refused, never cast ----

        [Test]
        public void TickBeyondClockRange_IsRefused()
        {
            var cfg = Fixture();
            var clock = CatchingUp(cfg, out double behind);

            // `RenderTick` is an int by contract while ticks ride the wire as
            // uint, so the top half of the range has no representation here.
            // Р82: refuse, never throw, and never hand back the rubbish an
            // out-of-range double→int cast produces.
            clock.OnSnapshot(uint.MaxValue, Epoch);
            clock.Advance(Dt, in cfg);
            Assert.AreEqual(behind + FastStep(cfg), clock.RenderTime, Eps,
                "a tick beyond the clock's own representable range moves nothing");
            Assert.GreaterOrEqual(clock.RenderTick, 0,
                "and no cast went out of range on the way");

            // Positive witness: the largest tick that IS representable works.
            clock.OnSnapshot(int.MaxValue, Epoch);
            clock.Advance(Dt, in cfg);
            Assert.AreEqual((double)int.MaxValue - cfg.InterpBufferTicks, clock.RenderTime, Eps,
                "positive witness: the boundary tick itself is ordinary");
            Assert.AreEqual(int.MaxValue - cfg.InterpBufferTicks, clock.RenderTick);
        }

        // ---- T31.11. No configuration can make the clock run backwards ----

        [Test]
        public void HostileSlewFraction_NeverReversesTime()
        {
            // `SlewFraction` is documented 0.05..0.10 and comes from a
            // validated asset, but "renderTime is monotonic inside an epoch" is
            // a CONTRACT of this class, not an outcome of good configuration: a
            // fraction of 1 or more turns `1 - f` into a zero or negative rate
            // and runs the world backwards.
            float[] hostile = { 5f, 1f, -1f, float.NaN };
            foreach (float fraction in hostile)
            {
                var cfg = Fixture();
                cfg.SlewFraction = fraction;

                var clock = Armed(cfg, out double start);
                double previous = clock.RenderTime;
                // No further snapshots: the clock runs ahead of a frozen target
                // and the slow half of the slew is engaged for the whole run.
                for (int f = 0; f < 40; f++)
                {
                    clock.Advance(Dt, in cfg);
                    Assert.GreaterOrEqual(clock.RenderTime, previous,
                        $"SlewFraction {fraction}: renderTime never moves backwards");
                    previous = clock.RenderTime;
                }
                Assert.Greater(clock.RenderTime, start,
                    $"SlewFraction {fraction}: positive witness — the clock still ran "
                    + "forward, it was not merely frozen into passing the assertion above");
            }
        }

        // ---- T31.12. A broken snap threshold breaks neither half of the contract ----

        [Test]
        public void HostileSnapThreshold_NeverReversesTimeAndKeepsTheSlew()
        {
            // Fix round 1, reviewer IMPORTANT. `RenderClockSnapTicks` reaches
            // this class through a plain struct the caller fills in, so
            // `NetConfig`'s [Range(1, 60)] and NetInvariants stand between the
            // OWNER and a bad value — not between a buggy caller and the clock.
            // Both non-positive values break the snap in a different direction
            // and both are covered here:
            //   * negative — a NEGATIVE drift exceeds it, i.e. a target sitting
            //     BEHIND the clock qualifies as a "forward" snap and rewinds
            //     time, which is the exact thing the asymmetric snap exists to
            //     forbid;
            //   * zero — every sub-tick error becomes a per-frame teleport onto
            //     the target and the slew is silently dead.
            int[] hostile = { -5, 0 };
            foreach (int snapTicks in hostile)
            {
                var sane = Fixture();
                var broken = Fixture();
                broken.RenderClockSnapTicks = snapTicks;

                // 1. AHEAD of a frozen target. A few frames under a SANE
                //    threshold first, so a snap onto that target would be a
                //    genuine rewind and not merely a freeze in place.
                var clock = Armed(sane, out double start);
                for (int f = 0; f < 3; f++) clock.Advance(Dt, in sane);
                double ahead = clock.RenderTime;
                Assert.Greater(ahead, start + 2d,
                    $"SnapTicks {snapTicks}: fixture premise — the clock is ticks ahead of "
                    + "its frozen target, so a snap onto that target would rewind it");

                double previous = clock.RenderTime;
                for (int f = 0; f < 40; f++)
                {
                    clock.Advance(Dt, in broken);
                    Assert.GreaterOrEqual(clock.RenderTime, previous,
                        $"SnapTicks {snapTicks}: renderTime never moves backwards — a "
                        + "threshold the asset's own Range cannot express must not turn "
                        + "the forward-only snap into a rewind");
                    previous = clock.RenderTime;
                }
                Assert.Greater(clock.RenderTime, ahead,
                    $"SnapTicks {snapTicks}: positive witness — the clock kept running "
                    + "forward instead of being pinned onto its target");

                // 2. BEHIND the target. The correction must stay a SLEW: a
                //    threshold that names no gap disables the jump, it does not
                //    turn every drift into one.
                var catching = Armed(broken, out double start2);
                catching.OnSnapshot(CatchUpNewest, Epoch);
                double target = CatchUpNewest - broken.InterpBufferTicks;
                Assert.Greater(target - start2, 1d,
                    $"SnapTicks {snapTicks}: fixture premise — the clock is behind a target "
                    + "far enough away to demand a correction");

                double previous2 = catching.RenderTime, maxStep = 0d;
                for (int f = 0; f < 20; f++)
                {
                    catching.Advance(Dt, in broken);
                    maxStep = math.max(maxStep, catching.RenderTime - previous2);
                    previous2 = catching.RenderTime;
                }
                Assert.LessOrEqual(maxStep, FastStep(broken) + Eps,
                    $"SnapTicks {snapTicks}: the correction stayed a slew — no frame "
                    + "teleported onto the target");
                Assert.Greater(catching.RenderTime, start2,
                    $"SnapTicks {snapTicks}: positive witness — and the clock really was "
                    + "correcting, not standing still");
            }
        }

        // ---- T31.13. The data path allocates nothing ----

        [Test]
        public void Clock_DoesNotAllocateGCMemory()
        {
            var cfg = Fixture();
            var clock = new RenderClock();
            clock.ResetForEpoch(Epoch);
            clock.OnSnapshot(1, Epoch);
            clock.OnSnapshot(2, Epoch);
            clock.Advance(Dt, in cfg);

            // Stub-defeating premise before anything is measured (Task 26
            // finding F-D): a class that answers a constant allocates nothing
            // either, so the measurement only means something once the thing
            // being measured is shown to work.
            Assert.IsTrue(clock.Started, "fixture premise: the clock is running");
            double warm = clock.RenderTime;
            clock.Advance(Dt, in cfg);
            Assert.Greater(clock.RenderTime, warm,
                "fixture premise: and it really advances when told to");

            Assert.That(() =>
            {
                for (int i = 0; i < 500; i++)
                {
                    clock.OnSnapshot((uint)(3 + i), Epoch);
                    clock.Advance(SimulationWorld.TickDt, in cfg);
                }
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
