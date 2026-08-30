using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Presentation.Net;

namespace Ring.Simulation.Tests
{
    /// THE REWIND DEPTH A SHOOTING CLIENT CLAIMS, AS ARITHMETIC (app-88jb Т26
    /// fix-round A, spec §3.6/§3.7). `RewindDepthMeter.Measure` answers how far
    /// into the past the server has to look at its targets so that it judges
    /// this frame's shot against the picture that was on this screen — and it
    /// answers with two differences, each taken inside its own tick counter:
    ///
    ///     (localTick - lastReconciledTick) + (newestTick - renderTick)
    ///      \_____ FishNet's counter _____/   \____ world counter ____/
    ///
    /// Only tick COUNTS cross the seam between the two, never tick ADDRESSES,
    /// and a count means the same thing on either side because both counters
    /// advance by `SimulationWorld.TickDt`. Three readiness gates — no
    /// reconcile yet, no snapshot yet, no render tick placed yet — each
    /// degenerate their own bracket to zero, which is an honest "no rewind"
    /// rather than a guess.
    ///
    /// WHY THE FUNCTION IS PURE, AND WHY THAT IS A PRECEDENT RATHER THAN AN
    /// INVENTION. `PlayerPredictionCore` lives in `PlayerNetworkController.cs`
    /// beside the `NetworkBehaviour` that feeds it and holds every decision
    /// that class makes with no FishNet in it — which is exactly why
    /// `ReconcileCodecTests` needs no network runtime. `RewindDepthMeter` is
    /// the same move applied to one formula: the numbers come from a live
    /// `NetworkManager`, the arithmetic over them does not, so the arithmetic
    /// is lifted to where a test can reach it and the caller keeps the wiring.
    ///
    /// ⚠ WHAT THIS FILE DOES NOT WITNESS, SAID PLAINLY BECAUSE THE PROJECT HAS
    /// PAID FOR THE OMISSION ONCE ALREADY. Every case below hands `Measure` its
    /// numbers, so nothing here can tell whether the CALLER hands it live ones.
    /// That is precisely how the defect this fix-round exists for survived: the
    /// source of the first bracket was a property FishNet clears before the
    /// handler that read it ever runs, so the bracket was identically zero
    /// while the formula read as correct. Liveness is a property of the WIRING,
    /// not of the formula, and its only witness is the Ф4 lag gate under 80 ms
    /// RTT (CR 7). A green run of this file says nothing about the depth that
    /// actually leaves the process.
    public class RewindDepthTests
    {
        [Test]
        public void NoReconcileYet_LeavesOnlyTheInterpolationLag()
        {
            // The gate on the first bracket, and the reason it is written as
            // "unset" and not as "zero": FishNet's marker for "no tick" IS 0
            // (`TimeManager.UNSET_TICK`), so an ungated subtraction would
            // report THE AGE OF THE PROCESS — 5000 ticks here — and saturate
            // every early shot. Only the world's own bracket may speak.
            Assert.AreEqual((byte)3, RewindDepthMeter.Measure(
                    localTick: 5000, lastReconciledTick: 0,
                    hasNewestTick: true, newestTick: 100,
                    clockPlaced: true, renderTick: 97),
                "до первой реконсиляции глубина обязана быть только лагом интерполяции — 3 тика");
        }

        [Test]
        public void PredictionLead_AndInterpolationLag_AddUp()
        {
            // Ruling 157 justified the SHAPE of this sum by arithmetic: about
            // two ticks of prediction lead at 80 ms RTT plus the
            // `InterpBufferTicks` = 3 the render clock deliberately trails by,
            // which is the "working depth of 5 ticks" spec §3.6 asks for. Until
            // this line that number lived only in prose inside a document; here
            // a machine performs it.
            Assert.AreEqual((byte)5, RewindDepthMeter.Measure(
                    localTick: 1002, lastReconciledTick: 1000,
                    hasNewestTick: true, newestTick: 100,
                    clockPlaced: true, renderTick: 97),
                "две скобки, 2 и 3, обязаны сложиться в рабочую глубину 5 тиков");
        }

        [Test]
        public void AStalledSnapshotStream_DoesNotProduceNegativeLag()
        {
            // The render clock keeps advancing while snapshots stop arriving,
            // so it can pass the newest accepted tick and the second bracket
            // goes negative. Clamping each bracket rather than only the sum is
            // what keeps the first one's two ticks intact: a negative lag would
            // SUBTRACT from a prediction lead that is perfectly alive.
            Assert.AreEqual((byte)2, RewindDepthMeter.Measure(
                    localTick: 1002, lastReconciledTick: 1000,
                    hasNewestTick: true, newestTick: 100,
                    clockPlaced: true, renderTick: 103),
                "вставший поток снапшотов не имеет права вычитать из опережения предсказания — ждём 2");
        }

        [Test]
        public void AReconcileAheadOfLocalTick_DoesNotProduceNegativeLead()
        {
            // The same rule on the other bracket. The tick a reconcile carries
            // is FishNet's own, and nothing forbids it from standing ahead of
            // the local tick this frame samples; a negative lead would eat the
            // interpolation lag exactly as a negative lag would eat the lead.
            Assert.AreEqual((byte)3, RewindDepthMeter.Measure(
                    localTick: 1000, lastReconciledTick: 1003,
                    hasNewestTick: true, newestTick: 100,
                    clockPlaced: true, renderTick: 97),
                "реконсиляция впереди локального тика не имеет права вычитать из лага интерполяции — ждём 3");
        }

        [Test]
        public void WithoutANewestTick_TheSecondBracketIsZero()
        {
            // `newestTick` reads 0 before the first accepted frame, and 0 is an
            // ordinary tick value rather than a sentinel — hence a flag of its
            // own. THE NUMBERS BELOW ARE DELIBERATE GARBAGE: 9999 against a
            // render tick of 0 would contribute 9999 if the flag were ignored,
            // so this case can tell "the gate held" from "the term happened to
            // come out zero".
            Assert.AreEqual((byte)2, RewindDepthMeter.Measure(
                    localTick: 1002, lastReconciledTick: 1000,
                    hasNewestTick: false, newestTick: 9999,
                    clockPlaced: true, renderTick: 0),
                "без принятого снапшота вторая скобка обязана молчать, оставив опережение — 2");
        }

        [Test]
        public void BeforeTheClockIsPlaced_TheSecondBracketIsZero()
        {
            // The clock is STARTED in `OnSnapshot` and PLACED one call later,
            // in `Advance`; in between it reports a render tick that has never
            // been on any screen. The gate this parameter carries is therefore
            // placement, not start. Garbage numbers again, and for the reason
            // the case above states.
            Assert.AreEqual((byte)2, RewindDepthMeter.Measure(
                    localTick: 1002, lastReconciledTick: 1000,
                    hasNewestTick: true, newestTick: 9999,
                    clockPlaced: false, renderTick: 0),
                "до первой постановки часов вторая скобка обязана молчать, оставив опережение — 2");
        }

        [Test]
        public void AnAbsurdDepth_SaturatesAtTheWireMaximum()
        {
            // The ceiling belongs to the WIRE — three bits — and not to the
            // arena. `Arena.RewindCapTicks` is a balance number whose single
            // home is `SimInputSanitizer`, and a client that pre-clamped to it
            // could never show the server's own sanity check an inflated claim.
            // So the ceiling is read from `InputCodec` instead of being written
            // here as a literal seven.
            Assert.AreEqual(InputCodec.MaxRewindTicksOnWire, RewindDepthMeter.Measure(
                    localTick: 10000, lastReconciledTick: 1,
                    hasNewestTick: true, newestTick: 100,
                    clockPlaced: true, renderTick: 97),
                "нелепая глубина обязана насыщаться проводным максимумом, а не капом арены");
        }

        [Test]
        public void AClientThatHasSeenNothing_MeasuresZero()
        {
            // All three gates shut at once — the state of every client for the
            // first moments of a match, and the one case where zero is the
            // measurement rather than the absence of one. The server then
            // judges the shot in its own present, which is what it did before
            // this number existed.
            Assert.AreEqual((byte)0, RewindDepthMeter.Measure(
                    localTick: 1, lastReconciledTick: 0,
                    hasNewestTick: false, newestTick: 0,
                    clockPlaced: false, renderTick: 0),
                "клиент, не видевший ни снапшота, ни реконсиляции, обязан заявлять нулевую глубину");
        }
    }
}
