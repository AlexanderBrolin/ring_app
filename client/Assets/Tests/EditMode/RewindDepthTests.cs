using NUnit.Framework;
using Ring.Networking.Protocol;
using Ring.Networking.Server;
using Ring.Presentation.Net;
using Ring.Simulation.Core;

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
    ///
    /// SINCE app-88jb Т7 THE FILE HOLDS A SECOND SUBJECT, and it is stated
    /// here so the two are not read as one: `DrawDepth` and `DrawTickFor`, the
    /// depth the PICTURE is drawn on. The measured claim leaves this process
    /// unclamped; the picture is trimmed to `Arena.RewindCapTicks` so that it
    /// stands on the tick the server judges by. The cases for it hand over
    /// their own cap, and one of them compares the answer against
    /// `MatchServer.SanitizedRewindDepth` — the only place in the project
    /// where the two formulas meet inside one assertion.
    public class RewindDepthTests
    {
        /// THE ARENA CAP THE `DrawDepth` CASES BELOW HAND OVER, AND WHY IT IS
        /// A LOCAL NUMBER RATHER THAN A CONFIG READ. `Arena.RewindCapTicks` is
        /// a balance value whose single home is the shipped tuning; here it is
        /// simply a PARAMETER of the function under test, so the cases stay
        /// what the rest of this file is — arithmetic over given numbers. The
        /// value mirrors the shipped tuning only so the sums below read like
        /// the ones a real match produces; nothing here breaks if it moves.
        ///
        /// ⚠ AND BECAUSE IT DOES MIRROR THE SHIPPED NUMBER, IT CANNOT BE THE
        /// ONLY CAP THIS FILE EVER HANDS OVER — see
        /// `TheArenaCapComesFromItsParameter_NotAShippedLiteral` at the foot of
        /// the file, which stands on a DIFFERENT cap for exactly that reason.
        const int ArenaCapTicks = 5;

        /// The cap that off-shipped case hands over. Legal tuning rather than
        /// an invented number: the invariant behind the clamp asks only that
        /// the rewind picture plus the sanity tolerance reach the cap, and the
        /// shipped 3 + 2 reaches four as comfortably as it reaches five.
        const int OffShippedCapTicks = 4;

        /// The render tick the draw-tick cases stand on, and a SECOND one for
        /// the same reason the cap has a second value: a sum that ignored its
        /// first argument and answered `10 + depth` would satisfy every case
        /// that hands over ten, and nothing would say so. The two numbers are
        /// arbitrary — what matters is that they differ.
        const int SampleRenderTick = 10;
        const int OtherRenderTick = 23;

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

        [Test]
        public void ADepthDeeperThanTheArenaCap_IsClampedForTheDrawing()
        {
            // WHY THIS CASE EXISTS. The picture was drawn on the MEASURED
            // depth, whose only ceiling belongs to the wire, while the server
            // judges the shot against the arena cap. Two ticks of disagreement
            // is about three and a half meters of round travel: the effect
            // showed up farther along the trajectory than the server ever
            // looked. `DrawDepth` is the one place the arena cap reaches the
            // drawing, and this case pins that it actually bites.
            byte measured = InputCodec.MaxRewindTicksOnWire;
            Assert.Greater((int)measured, ArenaCapTicks,
                "премисса теста: измеренная глубина обязана стоять глубже капа арены, иначе клампить нечего");

            Assert.AreEqual((byte)ArenaCapTicks, RewindDepthMeter.DrawDepth(measured, ArenaCapTicks),
                "картинка обязана рисоваться по капу арены, а не по необрезанной измеренной глубине");
        }

        [Test]
        public void TheWireCeilingCoversEveryLegalArenaCap()
        {
            // A GUARD (ruling 427), AND THE PREMISE THE WHOLE CLAMP RESTS ON.
            // `DrawDepth` trims the MEASURED depth; the server trims the
            // CLAIMED one, and the claim is that same measurement saturated at
            // the wire's ceiling. The two therefore answer the same number only
            // while the arena cap stays at or below that ceiling — otherwise
            // the wire would swallow depth the picture still draws, and the
            // clamp's "tick for tick" claim would quietly become an
            // approximation.
            //
            // WHAT HOLDS IT TODAY IS A THIRD PARTY, WHICH IS WHY THIS IS
            // WORTH A LINE: `SimConfigBuilder` refuses an
            // `Arena.RewindCapTicks` above the 200 ms of CRITICAL RULE 5, and
            // that ceiling is SimulationWorld.TicksFromSeconds(0.2f) — six,
            // one below the seven three bits carry. Nothing states the
            // relation between the two ceilings anywhere else, and neither
            // number is derived from the other: they could be retuned apart by
            // two unrelated hands.
            //
            // ⚠ THIS REPLACES AN EARLIER CASE THAT COMPARED THE SAME CLAIM
            // AGAINST A TEST-LOCAL CAP. That one restated its neighbor
            // `AnAbsurdDepth_SaturatesAtTheWireMaximum` — same arguments, a
            // weaker assertion — and could not notice the retune it claimed to
            // guard, because a number written in this file moves with the file.
            Assert.GreaterOrEqual((int)InputCodec.MaxRewindTicksOnWire,
                SimulationWorld.TicksFromSeconds(0.2f),
                "проводной потолок обязан покрывать любой законный кап арены: иначе заявка " +
                "теряет глубину, которую картинка ещё рисует, и кламп перестаёт совпадать с судьёй");
        }

        [Test]
        public void TheDrawnDepthIsTheJudgesOwnAnswer_WhileTheInvariantHolds()
        {
            // ⭐⭐ THE CLAIM THIS WHOLE TASK RESTS ON, AND UNTIL THIS CASE IT
            // LIVED ONLY IN PROSE. Both docs say the clamp is EXACT rather than
            // approximate — that `DrawDepth` answers the very number
            // `MatchServer.SanitizedRewindDepth` will answer — and the two
            // formulas sat in two files with nothing comparing them. An extra
            // term quietly added to the judge's estimate would leave every
            // other case in this file and in `RewindSanityTests` green.
            //
            // AND IT IS ALSO THE WITNESS THAT `NetInvariants` RULE #13 CARRIES
            // ITS WEIGHT. The equality holds exactly while the picture and the
            // tolerance reach the cap between them, which is what that rule
            // demands; the second half below breaks the rule by ONE tick and
            // shows the two answers coming apart. Without that half the rule
            // would be a sentence nothing tests.
            int pictureTicks = ArenaCapTicks - 2;
            int sanityTicks = ArenaCapTicks - pictureTicks;
            Assert.GreaterOrEqual(pictureTicks + sanityTicks, ArenaCapTicks,
                "премисса теста: фикстура обязана удовлетворять правилу #13, иначе первая половина " +
                "проверяет не то, что заявлено");

            // Three claims, one per branch of the judge's inner minimum: well
            // inside the cap, exactly on it, and saturated on the wire.
            foreach (byte claimed in new byte[]
                     { (byte)(ArenaCapTicks - 3), (byte)ArenaCapTicks, InputCodec.MaxRewindTicksOnWire })
            {
                Assert.AreEqual(
                    MatchServer.SanitizedRewindDepth(claimed, roundTripMs: 0f,
                        sanityTicks: sanityTicks, capTicks: ArenaCapTicks, pictureTicks: pictureTicks),
                    RewindDepthMeter.DrawDepth(claimed, ArenaCapTicks),
                    $"картинка обязана совпадать с судьёй тик в тик на заявке {claimed}");

                // A live round trip only lifts the estimate, so the agreement
                // is not a property of the zero a dedicated server reads.
                Assert.AreEqual(
                    MatchServer.SanitizedRewindDepth(claimed, roundTripMs: 80f,
                        sanityTicks: sanityTicks, capTicks: ArenaCapTicks, pictureTicks: pictureTicks),
                    RewindDepthMeter.DrawDepth(claimed, ArenaCapTicks),
                    $"и под живым RTT тоже — оценка судьи от задержки только растёт (заявка {claimed})");
            }

            // THE OTHER SIDE OF RULE #13, one tick below the floor: the judge's
            // estimate now falls short of the cap, so it — and not the cap —
            // becomes the binding term, while the picture goes on being drawn
            // at the cap. The expectation is the estimate written out here,
            // not a second call to the judge.
            int shortTolerance = sanityTicks - 1;
            Assert.Less(pictureTicks + shortTolerance, ArenaCapTicks,
                "премисса теста: вторая половина обязана НАРУШАТЬ правило #13, иначе она повторяет первую");

            byte deep = InputCodec.MaxRewindTicksOnWire;
            Assert.AreEqual((byte)(pictureTicks + shortTolerance),
                MatchServer.SanitizedRewindDepth(deep, roundTripMs: 0f,
                    sanityTicks: shortTolerance, capTicks: ArenaCapTicks, pictureTicks: pictureTicks),
                "под полом правила #13 судья обязан судить по своей оценке, а не по капу");
            Assert.AreEqual((byte)ArenaCapTicks,
                RewindDepthMeter.DrawDepth(deep, ArenaCapTicks),
                "а картинка по-прежнему рисуется по капу — вот цена нарушенного инварианта");
        }

        [Test]
        public void TheDrawTick_IsTheRenderTickPlusTheClampedDepth()
        {
            // ONE HOME FOR BOTH CONSUMERS. The tracer and the impact — its
            // spark, its sound, its recoil — are halves of one event, and a
            // clamp applied to only one of them would split those halves by
            // two ticks: the consequence would be shown before the round got
            // there. Neither consumer is reachable from EditMode (both are
            // private lines of a class whose constructor wants a live
            // `NetworkManager`), so the expression they share is pinned here.
            // It takes the MEASURED depth and the cap rather than a ready-made
            // draw depth, which is what makes an unclamped number impossible
            // to pass in.
            byte measured = InputCodec.MaxRewindTicksOnWire;
            Assert.Greater((int)measured, ArenaCapTicks,
                "премисса теста: измеренная глубина обязана стоять глубже капа, иначе кламп в сумме не виден");

            // The expectation is arithmetic performed here, not a second call
            // to the function under test: render tick plus the depth the cap
            // allows. Ticks are whole numbers and the sum is exact in `int`,
            // so it is computed in `int` — a `double` here would buy nothing
            // and the cast back could only lose something.
            int expectedDrawTick = SampleRenderTick + ArenaCapTicks;
            Assert.AreEqual(expectedDrawTick,
                RewindDepthMeter.DrawTickFor(SampleRenderTick, measured, ArenaCapTicks),
                "тик отрисовки обязан складывать рендер-тик с капнутой глубиной, а не с необрезанной");
        }

        [Test]
        public void ADepthInsideTheArenaCap_IsDrawnUnchanged()
        {
            // THE OTHER BRANCH, AND THE ONLY WITNESS AGAINST A CLAMP THAT
            // ALWAYS ANSWERS WITH THE CAP. Ordinary shots are measured well
            // inside the arena's number, and a `DrawDepth` that returned the
            // cap regardless would push every one of them deeper than the
            // server looked — the same defect this task removes, mirrored.
            // The depth is derived from the cap rather than written as its own
            // number so that the case keeps its meaning if the cap moves.
            byte measured = (byte)(ArenaCapTicks - 2);
            Assert.Less((int)measured, ArenaCapTicks,
                "премисса теста: глубина обязана стоять внутри капа, иначе тест проверял бы кламп, а не проход");

            Assert.AreEqual(measured, RewindDepthMeter.DrawDepth(measured, ArenaCapTicks),
                "глубина внутри капа обязана доезжать до картинки без изменения");
        }

        [Test]
        public void TheArenaCapComesFromItsParameter_NotAShippedLiteral()
        {
            // ⭐ WHY THIS CASE EXISTS: WITHOUT IT THE CAP COULD BE A SHIPPED
            // LITERAL AND EVERY CASE ABOVE WOULD STAY GREEN. All of them hand
            // over the shipped five, so the substitution "clamp to five and
            // ignore the argument" survives each one — checked case by case
            // rather than assumed: their answers stay 5, 15 and 3. A parameter
            // made mandatory to keep a caller from forgetting it is still a
            // parameter nothing observes until some case moves it (the lesson
            // the predicted-shot log paid for one task ago), and the same
            // guard stands beside the server's own rewind estimate for the
            // same reason.
            //
            // BOTH ENTRY POINTS ARE PINNED, because the clamp has two doors:
            // a `DrawTickFor` that passed a literal of its own would clamp
            // correctly and still answer the wrong tick.
            //
            // ⚠ AND THE RENDER TICK MOVES HERE FOR THE SAME REASON THE CAP
            // DOES. Every other draw-tick case hands over the same ten, so a
            // sum that ignored its first argument would answer correctly for
            // all of them; one case standing on a different frame is what
            // makes that substitution visible. The argument is the one above,
            // applied to the other parameter — a mandatory argument is not an
            // observed one.
            byte measured = InputCodec.MaxRewindTicksOnWire;
            Assert.AreNotEqual(ArenaCapTicks, OffShippedCapTicks,
                "премисса теста: этот случай обязан стоять на ДРУГОМ капе, иначе он повторяет соседей");
            Assert.AreNotEqual(SampleRenderTick, OtherRenderTick,
                "премисса теста: и на другом рендер-тике — иначе первый аргумент суммы никто не наблюдает");
            Assert.Greater((int)measured, OffShippedCapTicks,
                "премисса теста: измеренная глубина обязана стоять глубже этого капа, иначе клампить нечего");

            Assert.AreEqual((byte)OffShippedCapTicks,
                RewindDepthMeter.DrawDepth(measured, OffShippedCapTicks),
                "кламп обязан читать кап из параметра, а не из отгруженного числа");

            int expectedDrawTick = OtherRenderTick + OffShippedCapTicks;
            Assert.AreEqual(expectedDrawTick,
                RewindDepthMeter.DrawTickFor(OtherRenderTick, measured, OffShippedCapTicks),
                "тик отрисовки обязан читать оба своих аргумента, а не собственные литералы");
        }
    }
}
