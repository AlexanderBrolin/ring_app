using NUnit.Framework;
using Ring.Networking.Server;
using Ring.Simulation.Core;

namespace Ring.Simulation.Tests
{
    /// THE REWIND DEPTH A CLIENT CLAIMS, WEIGHED AGAINST WHAT ITS SOCKET SAYS
    /// IS POSSIBLE (app-88jb Т29, spec §3.6, Р374/Р404). The depth arrives in
    /// `SimInput.RewindTicks`, because the client is the only party that knows
    /// which picture it was shooting at; the server therefore takes the number
    /// but does not take it on trust. `MatchServer.SanitizeRewindDepthForTest`
    /// is the arithmetic of that second opinion — it builds an estimate out of
    /// the socket's round trip time and trims a claim that runs past it.
    ///
    /// ONE FORMULA, AND ALL FOUR EXPECTATIONS BELOW ARE COMPUTED FROM IT
    /// rather than chosen (review finding A-C1: an earlier draft carried three
    /// expectations descended from three different formulas, and no single
    /// implementation could have satisfied all three at once):
    ///
    ///     min(claimed,
    ///         TicksFromSeconds(roundTripMs * 0.001f * 0.5f)
    ///             + pictureTicks + sanityTicks,
    ///         capTicks)
    ///
    /// The middle term is the estimate. Half the round trip is the one-way
    /// delay; `pictureTicks` is the interpolation buffer the client's picture
    /// deliberately trails by, and it belongs in the estimate because the
    /// client states its depth as "predicted tick minus rendered tick", so an
    /// estimate without the buffer would punish an honest client with no ping
    /// at all; `sanityTicks` is the tolerance on top — 2 by Р404, which is
    /// 20 % of a cap of 6 rather than the 67 % a tolerance of 4 would be.
    ///
    /// TRIMMING IS NOT A REFUSAL. The input is legal and is simulated like any
    /// other; the one thing the server withholds is belief in the single
    /// number the client cannot prove.
    ///
    /// ⚠ WHAT THIS FILE DOES NOT WITNESS, SAID PLAINLY. Every case hands the
    /// function its five numbers, so nothing here can tell whether the CALLER
    /// hands it live ones — whether the round trip time reaching it is the
    /// socket's current reading rather than a stale or identically-zero field.
    /// Liveness is a property of the WIRING, not of the formula, and its only
    /// witness is the Ф4 lag gate under 80 ms RTT and 5 % loss (CR 7). A green
    /// run of this file says nothing about the depth the server actually
    /// rewinds its targets by.
    public class RewindSanityTests
    {
        [Test]
        public void ClaimedDepthFarAboveTheSocketEstimate_IsTrimmed()
        {
            // A client whose socket reports no delay at all claims the whole
            // cap. THE 5 IS COMPUTED, NOT PICKED: the one-way term is
            // TicksFromSeconds(0f * 0.001f * 0.5f) = 0, the interpolation
            // buffer adds 3, the tolerance adds 2 — an estimate of 5, and the
            // claimed 6 is cut down to it.
            //
            // AND THE CUT IS NOT A REFUSAL OF THE CONNECTION. Nothing is
            // dropped, nothing is disconnected: the input is legal and runs
            // this tick like any other, only its depth stops being believed.
            Assert.AreEqual((byte)5, MatchServer.SanitizeRewindDepthForTest(
                    claimed: 6, roundTripMs: 0f, sanityTicks: 2, capTicks: 6,
                    pictureTicks: 3),
                "глубина, заявленная выше оценки по сокету, обязана быть урезана до 5");
        }

        [Test]
        public void ClaimedDepthWithinTheEstimate_IsKept()
        {
            // The other half of the rule, and the half that keeps the check
            // from being a punishment: an honest client must not pay for being
            // honest. Same formula, at 80 ms — the one-way term is
            // TicksFromSeconds(0.04f) = 1, the buffer adds 3, the tolerance
            // adds 2, so the estimate is 6. The claimed 5 sits inside it and
            // is obliged to come back untouched.
            Assert.AreEqual((byte)5, MatchServer.SanitizeRewindDepthForTest(
                    claimed: 5, roundTripMs: 80f, sanityTicks: 2, capTicks: 6,
                    pictureTicks: 3),
                "честная глубина внутри допуска обязана пройти нетронутой");
        }

        [Test]
        public void RoundTripTime_IsReadAsMilliseconds_NotTicks()
        {
            // ⭐ THE WITNESS THAT SEPARATES TWO READINGS OF THE SAME FIELD BY A
            // NUMBER RATHER THAN BY A NAME. FishNet's
            // `TimeManager.RoundTripTime` is MILLISECONDS — verified against
            // the pinned package and against our own backend, where the same
            // reading is named `RoundTripMs` — and a handoff of ours once
            // asserted it was ticks. The tolerance is 0 here so that the two
            // readings cannot meet in the same answer:
            //   as milliseconds — one-way = TicksFromSeconds(0.04f) = 1, plus
            //     the buffer of 3, so the estimate is 4 and the claimed 6 is
            //     trimmed to 4, which is what this case asserts;
            //   as ticks — one-way = 80 * 0.5 = 40, plus the buffer of 3, so
            //     the estimate is 43, nothing is trimmed and the answer would
            //     be the claimed 6;
            //   with the millisecond conversion dropped but the conversion to
            //     ticks kept — one-way = TicksFromSeconds(80f * 0.5f) = 1200,
            //     and again nothing is trimmed.
            // Every misreading answers 6 and only the right one answers 4, so
            // this fixture cannot come out green by accident.
            Assert.AreEqual((byte)(SimulationWorld.TicksFromSeconds(0.04f) + 3),
                MatchServer.SanitizeRewindDepthForTest(
                    claimed: 6, roundTripMs: 80f, sanityTicks: 0, capTicks: 6,
                    pictureTicks: 3),
                "RoundTripTime прочитан не как миллисекунды: 80 мс — это один тик в одну сторону");
        }

        [Test]
        public void AnEstimateAboveTheCap_IsStillBoundedByIt()
        {
            // ⭐ THE CAP IS THE CEILING OF THE ANSWER, AND WITHOUT THIS CASE
            // NOTHING SAYS SO. In the three fixtures above the estimate is
            // already below the cap, so an implementation that dropped
            // `capTicks` out of the minimum would leave every one of them
            // green. Here 400 ms lifts the estimate over it: one-way =
            // TicksFromSeconds(0.2f) = 6, plus the buffer of 3, plus the
            // tolerance of 2 = 11, and the answer is still 6 — the 200 ms of
            // compensation CRITICAL RULE 5 allows, and not a tick more.
            //
            // ⚠ `claimed: 7` CANNOT ARRIVE FROM THE WIRE, so this case pins
            // the contract of a pure function and NOT a battle path. The depth
            // travels in three bits of `InputCodec`: the encoder saturates at
            // `MaxRewindTicksOnWire` (7) instead of masking, and the decoder
            // clamps what it reads down to `RewindTicksWireCap` (6), so a
            // client that sends the eighth value is understood as 6 before any
            // server code sees it — and `Arena.RewindCapTicks` is 6 as well,
            // in the shipped asset and in the test fixtures alike. Read the
            // number below as "the function is bounded for every input it can
            // be given", never as "a claim of 7 happens".
            Assert.AreEqual((byte)6, MatchServer.SanitizeRewindDepthForTest(
                    claimed: 7, roundTripMs: 400f, sanityTicks: 2, capTicks: 6,
                    pictureTicks: 3),
                "оценка выше капа обязана остаться ограниченной капом — ждём 6");
        }
    }
}
