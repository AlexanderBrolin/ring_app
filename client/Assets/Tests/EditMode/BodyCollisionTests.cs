using System.Collections.Generic;
using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// app-88jb Т22 (spec §3.5, owner decisions Н15/Н20/Н21, Р441/Р442): bodies
    /// stop being ghosts. Until this task a collector walked THROUGH every mob
    /// in the arena and mobs stacked on one point, because the only push-apart
    /// in the game was SeparationSystem's SOFT one -- a force into Vel that a
    /// dense wave outruns.
    ///
    /// THE FILE COVERS TWO SUBJECTS THAT SHARE ONE PAIR SCAN, and keeps them
    /// apart by name:
    ///   * POSITION -- overlapping bodies are separated (Geometry.ResolveBodyPair,
    ///     accumulated into a double buffer and applied after the full scan, so
    ///     the outcome cannot depend on array order or on the death history that
    ///     shuffles it);
    ///   * MOMENTUM -- a body that is run INTO is shoved (Impact.ResolveBodyPush,
    ///     owner decision Р442). The shove reads masses and the closing speed
    ///     along the contact normal, and nothing else: there is no branch for
    ///     dash, slide or run, and none for archetype.
    ///
    /// ⚠ EVERY FIXTURE HERE USES OpenField(), NOT Open(). Open() spawns the
    /// collector on the spawn ring at x ≈ 159 m, where it never reaches a body
    /// placed at x = 2 and every "did not pass through" assert would be true on
    /// a stub. OpenField() sets PlayerSpawnRingFrac = 0, which is the only
    /// reason these tests can fail at all.
    public class BodyCollisionTests
    {
        /// Test 25 -- the task's headline RED.
        ///
        /// ⚠⚠ THE FINAL DISTANCE IS NOT A WITNESS, and the plan's first form of
        /// this test proved it by going GREEN on today's ghost behavior (finding
        /// Н-41, session 72). A collector that walks straight THROUGH the chaser
        /// keeps going: 60 ticks at MaxSpeed 7 put it near x = 14, twelve metres
        /// PAST a body standing at x = 2, and "distance >= contact width" is
        /// true on the far side just as it is in front.
        ///
        /// ⚠ NOR IS "the collector is stopped in front of it", which was the
        /// first repair and is ALSO wrong -- for the opposite reason. Under
        /// Р442 a 120 kg collector walking into a 90 kg chaser SHOVES IT ALONG:
        /// the pair separates fully every tick, the chaser is pushed ahead, and
        /// the collector keeps advancing. Being blocked is not the promise; not
        /// interpenetrating is.
        ///
        /// So the invariant is checked EVERY TICK (bodies never overlap) and the
        /// far side is checked once at the end (the collector never comes out
        /// behind the body it was pushing). Together those two are true only of
        /// a world where bodies are solid, and false in today's.
        [Test]
        public void CollectorDoesNotWalkThroughAChaser()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(2f, 0f)));
            var m = w.Mobs[0]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; w.SetMobForTest(0, m);

            var input = new SimInput { MoveDir = new float2(1f, 0f) };
            float contact = cfg.Hero.Radius + cfg.Chaser.Radius;
            for (int i = 0; i < 60; i++)
            {
                w.Tick(input);
                Assert.GreaterOrEqual(math.distance(w.Player.Pos, w.Mobs[0].Pos), contact - 0.05f,
                    $"тик {i}: сборщик и чейзер перекрылись");
            }

            Assert.Less(w.Player.Pos.x, w.Mobs[0].Pos.x,
                "сборщик оказался ПО ТУ СТОРОНУ тела — он прошёл сквозь него");
        }

        /// Test 27 (Н21): the soft separation is a FORCE, and a force takes ticks
        /// the wave does not give it. Two chasers spawned 2 cm apart are still
        /// 2 cm apart one tick later, because SeparationSystem's Vel addition
        /// only becomes motion on the FOLLOWING tick's MoveWithCollisions.
        [Test]
        public void TwoMobsNeverStandOnTheSamePoint()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)),
                (MobType.Chaser, new float2(6.02f, 0f)));
            for (int i = 0; i < 2; i++)
            {
                var mi = w.Mobs[i]; mi.Ai = MobAiState.Idle; mi.Hp = 1e6f; w.SetMobForTest(i, mi);
            }

            w.Tick(default);

            Assert.Greater(math.distance(w.Mobs[0].Pos, w.Mobs[1].Pos), 0.5f,
                "два моба остались в одной точке после жёсткого разведения");
        }

        /// Test 30 (finding D-C3) -- a NEGATIVE test, and it is green on the stub
        /// BY DESIGN: where nothing pushes the collector out, the displacement is
        /// zero and LessOrEqual(0, 0.55) holds. It exists to pin the CEILING once
        /// the push arrives, not to drive the red phase.
        ///
        /// The case is real rather than hypothetical: the dash covers 2.7 m and
        /// the Director is 4.4 m across, so a dash CAN end inside that body.
        ///
        /// ⚠ WITHOUT MaxDepenetrationPerTick THE THROW IS 2.48 m, not the
        /// 0.97 m an earlier wording gave (review round of Т22, finding M-2).
        /// 0.97 is the collector's SHARE of the overlap — 4000/4120 — which the
        /// plan wrote as a fraction and this comment turned into metres. The
        /// fixture's own overlap is (0.45 + 2.2) - 0.1 = 2.55 m, and 0.971 of
        /// that is 2.48: five times the ceiling this test pins, rather than
        /// twice it.
        [Test]
        public void DashEndingInsideTheDirector_DoesNotFlingTheCollectorFourMeters()
        {
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.MaxDepenetrationPerTick = 0.5f;
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Director, new float2(0f, 0f)));
            var d = w.Mobs[0]; d.Ai = MobAiState.Idle; d.Hp = 1e6f; w.SetMobForTest(0, d);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(0.1f, 0f));

            float2 before = w.Player.Pos;
            w.Tick(default);

            Assert.LessOrEqual(math.distance(before, w.Player.Pos), 0.55f,
                "выталкивание из тела превысило MaxDepenetrationPerTick");
        }

        /// Witness for Р413: ONE Jacobi iteration does not separate a chain of
        /// three. The middle body is pushed both ways in the same scan, the two
        /// contributions very nearly cancel, and the chain stays a chain -- which
        /// is why the iteration count is a config field and not a literal 1.
        [Test]
        public void ThreeBodiesInAChain_AreSeparated_ByRelaxation()
        {
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Arena.RelaxIterations = 4;
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)),
                (MobType.Chaser, new float2(6.05f, 0f)), (MobType.Chaser, new float2(6.1f, 0f)));
            for (int i = 0; i < 3; i++)
            {
                var mi = w.Mobs[i]; mi.Ai = MobAiState.Idle; mi.Hp = 1e6f; w.SetMobForTest(i, mi);
            }

            w.Tick(default);

            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    Assert.Greater(math.distance(w.Mobs[i].Pos, w.Mobs[j].Pos), 0.5f,
                        $"пара ({i},{j}) не разведена — одной итерации цепочке мало");
        }

        // ── The push law (Р442) — six witnesses ──────────────────────────────
        //
        // ⚠ FIXTURE NUMBERS, NOT THE SHIPPED .asset ONES: TestConfigs.Default()
        // gives the collector MaxSpeed 7, DashSpeed 22 and DashDuration 0.15,
        // while the slide matches what ships (13.5 / 0.52). The collector's
        // momentum share is 120/210 = 0.5714 against a chaser and
        // 120/4120 = 0.0291 against the Director.

        /// ⭐ THE HEADLINE WITNESS of the owner's correction: the shove must READ
        /// THE SPEED. Without it, "push by a constant" survives the entire suite.
        ///
        /// Three runs of ONE fixture are compared against each other rather than
        /// against absolute numbers, so the test survives the balance pass at
        /// milestone В2 and dies only if the law stops reading speed.
        ///
        /// ⚠⚠ BOTH THRESHOLDS ARE THE PLAN'S OWN 1.5 AGAIN, and the second one
        /// spent this round at 1.3 (review round of Т22, finding I-5). It was
        /// lowered under a measurement rather than under an argument: session
        /// 72 predicted 12.57 / 7.71 = 1.63, measured less, and moved the
        /// literal instead of asking why. The answer was in the fixture, not in
        /// the law — the mob it measured was never frozen (finding I-4), so the
        /// RUN channel was reading the chaser's own 5.2 m/s legs. With the body
        /// actually immobilized the three channels come out 2.86 / 5.15 / 10.44
        /// m/s, i.e. ratios of 1.80 and 2.03, and the plan's threshold clears
        /// on both.
        ///
        /// ⚠ THE ABSOLUTE NUMBERS MOVED TOO, AND DOWNWARD, which is the honest
        /// consequence of the same freeze rather than a regression: a chaser
        /// that walks INTO the blow feeds the contact more interpenetration per
        /// tick, so ruling 117's cap (`overlap/dt`) stops biting and the slide
        /// landed the full 0.5714 · 13.5 = 7.71. Against a body that only ever
        /// gets pushed, the cap governs — which is precisely the quantity the
        /// law promises and the one a fixture should be measuring.
        [Test]
        public void Push_GrowsWithApproachSpeed()
        {
            TestWorlds.RunIntoBody(MobType.Chaser, TestWorlds.MoveMode.Run, 0f,
                out float run, out _);
            TestWorlds.RunIntoBody(MobType.Chaser, TestWorlds.MoveMode.Slide, 0f,
                out float slide, out _);
            TestWorlds.RunIntoBody(MobType.Chaser, TestWorlds.MoveMode.Dash, 0f,
                out float dash, out _);

            // ⭐ THE RUN CHANNEL NEEDS A FLOOR OF ITS OWN, and without it the
            // comparisons above are blind to the one mutation Р442 forbids by
            // name — "no branch for dash, slide or run". Delete the shove for
            // everything but the slide and the dash and `run` becomes 0, at
            // which point `slide > run * 1.5` is 5.15 > 0 and passes. Measured
            // 2.86 m/s against a floor of 1.0.
            Assert.Greater(run, 1f, "бег не толкнул вовсе — закон ветвится по режиму движения");
            Assert.Greater(slide, run * 1.5f, "подкат толкнул не сильнее бега");
            Assert.Greater(dash, slide * 1.5f, "дэш толкнул не сильнее подката");
        }

        /// The second load-bearing factor: MASS. The Director outweighs the
        /// collector 33 to 1 and must barely register the same slide that throws
        /// a chaser faster than it can run.
        [Test]
        public void Push_ScalesWithMass_DirectorBarelyMoves()
        {
            TestWorlds.RunIntoBody(MobType.Chaser, TestWorlds.MoveMode.Slide, 0f,
                out float chaser, out _);
            TestWorlds.RunIntoBody(MobType.Director, TestWorlds.MoveMode.Slide, 0f,
                out float director, out _);

            Assert.Greater(chaser, director * 10f, "толчок не зависит от массы тела");
            Assert.Less(director, 1f, "Директора сдвинуло как лёгкое тело");
        }

        /// Witness for the PROJECTION: the closing speed is taken along the
        /// contact normal, never as the speed's magnitude. Otherwise a collector
        /// sliding PAST a body would hurl it exactly as hard as one sliding INTO
        /// it, and a crowd would explode sideways off a near miss.
        ///
        /// ⚠⚠ THE FIXTURE IS DEEP OVERLAP PLUS PERPENDICULAR MOTION, not a
        /// grazing pass, and the difference is a mutation that survived
        /// (session 72, M32). In a graze the fresh overlap is tiny, so ruling
        /// 117's cap holds the blow down on its own and the projection has
        /// nothing left to prove — the earlier fixture measured the CAP and
        /// called it the projection. Here the cap is wide open (the bodies are
        /// 0.45 m into each other, worth 13.5 m/s of closing speed) while the
        /// projection is zero, so the two answers are as far apart as the rule
        /// can put them.
        [Test]
        public void SidewaysMotion_DoesNotShoveAnOverlappedBody()
        {
            float sideways = TestWorlds.SidewaysPushOnOverlappedChaser();
            TestWorlds.RunIntoBody(MobType.Chaser, TestWorlds.MoveMode.Slide, 0f,
                out float headOn, out _);

            // ⚠⚠ MEASURED: EXACTLY 0 sideways against 5.15 head-on — and the
            // sideways figure used to read 1.99, WHICH WAS NOT THE PROJECTION'S
            // RESIDUE (review round of Т22, finding M-10). An earlier wording
            // here explained the 1.99 as the contact normal rotating and
            // picking up a component along the travel. The chaser in this
            // fixture was simply never frozen: it accelerates at 30 m/s², a
            // round 1.0 m/s per tick, and the fixture measures three ticks.
            // 1.99 was the mob's own two ticks of acceleration to the second
            // decimal, and the explanation was a story fitted to a number.
            //
            // Zero is what the law actually promises, and the sign is why: `n`
            // points from the body to the collector, so a collector travelling
            // sideways has a POSITIVE component along `n` from the first tick
            // on — it is separating, not closing — and ResolveBodyPush declines
            // an approach that is not positive. The cap is wide open the whole
            // time (the bodies are 0.45 m into each other, worth 13.5 m/s), so
            // nothing but the projection is holding the blow down.
            //
            // Under the mutation that reads the speed's MAGNITUDE the sideways
            // figure becomes 0.5714 · 13.5 = 7.71, which the first assert kills
            // with two orders of magnitude to spare. The second is the premise:
            // the same fixture DOES shove when the collector closes, so a world
            // where the push was deleted outright cannot pass by scoring zero
            // twice.
            Assert.Less(sideways, 0.5f,
                "тело отброшено движением ВДОЛЬ него — скорость взята не по нормали");
            Assert.Greater(headOn, 3f,
                "премисса: лобовой подкат не толкнул вовсе — фикстура ничего не мерила");
        }

        /// Witness for PushRecoilFraction = 0.25. At full recoil a slide would
        /// drop 13.5 -> 7.71, i.e. slower than running, and "the combo cuts
        /// through a crowd" would be false; at zero it would cost nothing at all
        /// and the number would be decoration.
        [Test]
        public void Slide_LosesOnlyItsRecoilShare_NotTheWholeCollision()
        {
            float after = TestWorlds.SlideThroughChasers(1, out float dip);

            // The DIP is the recoil share, CAPPED BY THE ACTUAL OVERLAP
            // (ruling 117): a full-speed blow would cost 0.25 · (90/210) · 13.5
            // = 1.446 and dip to 12.05, but on the tick of contact the bodies
            // converge by only a fraction of that travel, and the measured dip
            // is 12.54. The threshold is set to die under the "no recoil"
            // mutation (which leaves exactly 13.5) without clinging to the
            // third digit of a balance number.
            Assert.Less(dip, 13.0f, "подкат не потерял НИЧЕГО — отдача не применяется");
            Assert.Greater(dip, 11f, "подкат потерял больше своей доли отдачи");
            // ⭐ WITNESS FOR THE THRUSTER ITSELF (Р443): the engine wins the dip
            // back. Without this assert SlideThrustRecovery has no victim at all.
            Assert.Greater(after, dip + 0.5f,
                "движок не отыграл потерю — подкат катится, а не идёт под тягой");
        }

        /// ⭐⭐ THE OWNER'S INTENT AS AN EXECUTABLE TEST (Р442): "the movement
        /// combo must cut through a crowd of light enemies". Three chasers, and
        /// the slide has to come out the far side still faster than running.
        [Test]
        public void SlideThroughThreeChasers_StaysFasterThanRunning()
        {
            float after = TestWorlds.SlideThroughChasers(3);

            Assert.Greater(after, TestConfigs.OpenField().Hero.MaxSpeed,
                "подкат сквозь троих стал медленнее бега — комбо толпу не прорезает");
        }

        /// Witness for the THIRD pair kind (Р442 removed the "no impulse between
        /// mobs" boundary): a chaser knocked flying carries the blow into whoever
        /// stands behind it. Without it a crowd parts one body at a time and
        /// "cutting through" is really "squeezing past".
        [Test]
        public void PushedMob_KnocksBackTheMobBehindIt()
        {
            float behind = TestWorlds.SecondRowSpeedAfterDash();

            // Positive x only: the chasers' own AI drives them the other way
            // (the collector is behind them), so this cannot be their own legs.
            Assert.Greater(behind, 3f,
                "второй ряд не получил импульса — закон не применён к паре моб↔моб");
        }

        // ── Only visible bodies (Н20) ────────────────────────────────────────
        //
        // ⚠⚠ AN HONEST CAVEAT INHERITED FROM THE PLAN AND RE-CHECKED HERE: under
        // today's line-of-sight visibility the rule "separate only from what you
        // can see" is a NO-OP, and that is proved by geometry rather than taste.
        // An overlap needs less than `Hero.Radius + Chaser.Radius` = 0.95 m;
        // invisibility comes either from distance (> SightRadius 45 m) or from a
        // blocked line of sight, i.e. a blocker BETWEEN the bodies — and every
        // body is pushed out of a blocker by `radius + halfWidth`, so two bodies
        // on opposite sides of any blocker are at least 2·0.6 + 0.95 = 2.15 m
        // apart. ⇒ A BODY IN CONTACT IS ALWAYS VISIBLE.
        //
        // ⚠ AND ONE FACT THE PLAN DID NOT KNOW (session 72): the world has no
        // per-player VisibilitySet at all — visibility is computed by
        // SnapshotAssembler in the networking layer, which Simulation may not
        // read (CRITICAL RULE 1). Standing up a second line-of-sight pass inside
        // the simulation, for a rule that provably changes nothing, is a cost
        // with nothing bought. So the server separates from every body (at
        // contact the two sets are the same), and the rule has its teeth where
        // they are real: in the set the CLIENT assembles.
        //
        // The witnesses below check what is checkable: parity on a VISIBLE body,
        // and an empty set being a legal input.

        [Test]
        public void PredictionAndServerAgree_WhenNoBodyIsVisible_Guard()
        {
            // ⚠ A GUARD, NOT A WITNESS (lesson 427), and named so honestly:
            // both sides do NOTHING here, so what this pins is the wiring of the
            // fifth parameter and the fact that an empty set is a legal input
            // rather than an exception. No behavioral witness for the "only
            // visible" rule exists — see the analysis above.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            // The fixture has to place its players itself (TestConfigs.OpenField
            // sets PlayerSpawnRingFrac = 0, which puts both on the same point) —
            // otherwise they would separate FROM EACH OTHER and the test would
            // measure something other than its subject.
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, 60f));
            PlayerState predicted = w.PlayerAt(0);
            var input = new SimInput { MoveDir = new float2(1f, 0f) };

            for (int i = 0; i < 30; i++)
            {
                PlayerPrediction.Step(ref predicted, in input, in cfg,
                    Ring.Simulation.Combat.ImpactPulse.None,
                    System.ReadOnlySpan<PushableBody>.Empty);
                w.TickAll(new[] { input, default });
            }

            Assert.AreEqual(0f, math.distance(predicted.Pos, w.PlayerAt(0).Pos), 0.01f,
                "предсказание с пустым набором тел разошлось с сервером");
        }

        /// ⭐ THE HALF THAT MATTERS (finding D-C11): without it the guard above
        /// passes trivially, because both sides do nothing. Here bodies are
        /// there, both sides must separate the collector IDENTICALLY, and the
        /// last assert states the premise — the collector really was stopped by
        /// something — so a world where nothing separated could not pass by
        /// agreeing on doing nothing.
        ///
        /// ⚠⚠ TWO PIECES OF FIXTURE CRAFT, BOTH PAID FOR BY A FAILING RUN
        /// (session 72), and neither is convenience:
        ///
        ///   1. THE SPAN CARRIES EVERY BODY IN THE WORLD, not just the chaser
        ///      this test spawns. That is the correct SHAPE — the server
        ///      separates from every body it has, so a span that carried a
        ///      hand-picked subset would be testing the fixture rather than the
        ///      rule — and it was paid for by a run: an earlier version
        ///      disagreed by 0.198 m against a 4000 kg body it knew nothing of.
        ///      ⚠ THAT BODY CANNOT APPEAR HERE ANY MORE, and an earlier wording
        ///      of this paragraph said in the present tense that it does
        ///      (review round of Т22, finding M-1). MatchFlowSystem wakes the
        ///      Director when a live collector stands in the CORE, and
        ///      OpenField() is ZONELESS (owner decision R-173), so
        ///      AnyLiveCollectorInCore returns false outright at
        ///      ZoneRadius.Length < 2. The shape stays because it is right, not
        ///      because a Director is expected.
        ///   2. THE BODIES ARE FROZEN back onto their snapshot after every tick.
        ///      The client's span necessarily holds LAST tick's positions — it
        ///      has no mobs to simulate — while the server separates against the
        ///      positions bodies have NOW, after MobAiSystem has moved them and
        ///      after the collector's own shove has thrown them. A body that
        ///      moves therefore guarantees a mismatch that is, again, not the
        ///      rule. Freezing removes the one difference that is not under
        ///      test; what remains under the assert is the rule alone.
        ///
        /// The real, un-freezable version of that gap is named honestly
        /// elsewhere and belongs to the lag gate: a networked client separates
        /// against snapshot positions ~140 ms old, worth up to 0.73 m per tick —
        /// risk Р-F, point 7 of the Ф4 gate, and issue app-njmi.
        [Test]
        public void PredictionAndServerAgree_WhenTheBodyIsVisible()
        {
            SimConfig cfg = TestConfigs.OpenField();
            // ⚠ THE BODY IS IMMOBILIZED BY CONFIG, not only frozen after the
            // tick. MobAiSystem runs BEFORE the separation within the same tick,
            // so a chaser with a nonzero Accel travels Accel·dt² before the
            // server starts separating — and the server then separates against a
            // position the client's span cannot contain. Zeroing the archetype's
            // own speed removes that difference at the source.
            cfg.Chaser.MaxSpeed = 0f;
            cfg.Chaser.Accel = 0f;
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0f, 60f));
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(2f, 0f)));
            var m = w.Mobs[0]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; w.SetMobForTest(0, m);

            var input = new SimInput { MoveDir = new float2(1f, 0f) };

            // ⚠ FIVE WARM-UP TICKS, AND THEIR STATED REASON NO LONGER HOLDS
            // (review round of Т22, finding M-1). They were written against a
            // Director born on a phase transition inside the FIRST tick, whom
            // the client's span — taken BEFORE that tick — could not know
            // about; on a ZONELESS fixture he is never born at all. What the
            // ticks still do is let the collector cover the ground to the body
            // before the comparison starts, and the premise below is what
            // proves they did. They stay because removing them would move the
            // fixture without buying anything.
            for (int i = 0; i < 5; i++) w.TickAll(new[] { input, default });

            PlayerState predicted = w.PlayerAt(0);
            var frozen = new System.Collections.Generic.List<float2>();
            var bodies = new System.Collections.Generic.List<PushableBody>();

            for (int i = 0; i < 25; i++)
            {
                // A snapshot of EVERY body in the world — the chaser, and the
                // Director with his retinue that MatchFlowSystem puts at the
                // arena center.
                frozen.Clear(); bodies.Clear();
                for (int k = 0; k < w.MobCount; k++)
                {
                    frozen.Add(w.Mobs[k].Pos);
                    var c = w.MobConfigFor(w.Mobs[k].Type);
                    bodies.Add(new PushableBody(w.Mobs[k].Pos, c.Radius, c.Mass));
                }

                PlayerPrediction.Step(ref predicted, in input, in cfg,
                    Ring.Simulation.Combat.ImpactPulse.None,
                    System.MemoryExtensions.AsSpan(bodies.ToArray()));
                w.TickAll(new[] { input, default });

                // Bodies go back onto the snapshot — see the doc above.
                for (int k = 0; k < w.MobCount && k < frozen.Count; k++)
                {
                    var mob = w.Mobs[k];
                    mob.Pos = frozen[k];
                    mob.Vel = float2.zero;
                    w.SetMobForTest(k, mob);
                }
            }

            Assert.Less(math.distance(predicted.Pos, w.PlayerAt(0).Pos), 0.01f,
                "предсказание разошлось с сервером на ВИДИМОМ теле");
            Assert.Less(w.PlayerAt(0).Pos.x, 2f - cfg.Chaser.Radius,
                "премисса: сборщик прошёл сквозь тело — фикстура ничего не разводила");
        }

        /// Victim of mutation M22a (apply displacements INSIDE the scan) and the
        /// reason the double buffer is a contract rather than a preference.
        ///
        /// SeparationSystem's own doc states it verbatim: "a single
        /// resolve-as-you-go pass would let position updates from the first pairs
        /// BIAS THE PAIRS SCANNED AFTERWARD". The array order is not stable --
        /// SimulationWorld retires a mob by swapping the tail into its slot -- so
        /// a scan that reads its own writes makes the outcome a function of the
        /// DEATH HISTORY, which the client watched in a different order. That is
        /// unreproducible by construction, and "the chain is separated" cannot
        /// see it: Gauss-Seidel satisfies that assert just as well as Jacobi.
        [Test]
        public void SeparationOutcome_DoesNotDependOnArrayOrder()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var a = new SimulationWorld(7, cfg);
            var b = new SimulationWorld(7, cfg);
            var pts = new[] { new float2(6f, 0f), new float2(6.05f, 0f), new float2(6.1f, 0f) };
            TestWorlds.SpawnMobsAt(a, (MobType.Chaser, pts[0]), (MobType.Chaser, pts[1]),
                (MobType.Chaser, pts[2]));
            TestWorlds.SpawnMobsAt(b, (MobType.Chaser, pts[2]), (MobType.Chaser, pts[1]),
                (MobType.Chaser, pts[0]));           // SAME set, REVERSED order
            foreach (var w in new[] { a, b })
                for (int i = 0; i < 3; i++)
                {
                    var m = w.Mobs[i]; m.Ai = MobAiState.Idle; m.Hp = 1e6f; w.SetMobForTest(i, m);
                }

            a.Tick(default); b.Tick(default);

            // SETS of positions are compared, not slots: the two worlds fill
            // their slots in opposite order, which is the whole point.
            var setA = new List<float> { a.Mobs[0].Pos.x, a.Mobs[1].Pos.x, a.Mobs[2].Pos.x };
            var setB = new List<float> { b.Mobs[0].Pos.x, b.Mobs[1].Pos.x, b.Mobs[2].Pos.x };
            setA.Sort(); setB.Sort();
            for (int i = 0; i < 3; i++)
                Assert.AreEqual(setA[i], setB[i], 1e-5f,
                    "исход разведения зависит от порядка тел в массиве");
        }

        // ── The THIRD pair kind: collector ↔ collector (spec §3.5, review
        //    round of Т22, finding C-1) ────────────────────────────────────
        //
        // ⚠⚠ THIS PAIR HAD ZERO WITNESSES UNTIL THIS ROUND, and the gap was
        // not an oversight that went unnoticed — it was WRITTEN DOWN. The
        // guard fixture below moves its second collector 60 m away with the
        // words "otherwise they would separate FROM EACH OTHER", i.e. the
        // interaction was known and stepped around rather than checked. Every
        // instrument Т22 did run (fifteen mutations, the diff self-review, the
        // sweeps) was blind to it by construction: a mutation is measured
        // THROUGH the tests, and no test observed this pair.
        //
        // What the four tests below pin, and why each is a separate one:
        //   * the SHARE   -- each collector takes its own mass-weighted half of
        //     the overlap, never the whole of it;
        //   * RULING 113  -- a collector's velocity change never reads another
        //     body's motion, which is the invariant that makes the client's
        //     half reproducible at all;
        //   * PARITY      -- the server's answer for a collector standing in
        //     another collector equals the client's own, to the centimetre;
        //   * the DEGENERATE PAIR -- two collectors on one point are left
        //     alone, because nothing in the shared data can tell them apart.

        /// ⭐ THE HEADLINE WITNESS of finding C-1: the pair is resolved ONCE
        /// PER SIDE, and each side keeps only its OWN half.
        ///
        /// Before this round CollectorPass spilled Accumulate's reciprocals
        /// into EVERY slot, collectors included — so the pair was processed in
        /// collector p's pass and again in collector q's, and by
        /// ResolveBodyPair's own symmetry (dA of one pass IS dB of the other)
        /// each slot received its share TWICE. At equal mass that is the whole
        /// overlap where half was owed.
        ///
        /// THE NUMBER IS DERIVED, NOT MEASURED-AND-PINNED (lesson 428). The
        /// two stand 0.6 m apart inside a 0.9 m contact width, so the overlap
        /// is 0.3 m and each owes itself 0.3 · 120/240 = 0.15 m. A tick runs
        /// the collector pass TWICE against ONE frozen snapshot, and the
        /// second pass sees the pair 0.15 m less overlapped from the first —
        /// 0.075 m more each — so the tick's total is 0.225 m per collector
        /// and the gap ends at 1.05 m. Under the doubling it was 0.30 m and
        /// 1.20 m: the tolerance below is an order of magnitude tighter than
        /// the difference between the two answers.
        [Test]
        public void TwoCollectors_EachTakeTheirOwnHalfOfTheOverlap()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(-0.3f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(0.3f, 0f));

            w.TickAll(new SimInput[2]);

            Assert.AreEqual(-0.525f, w.PlayerAt(0).Pos.x, 0.01f,
                "первый сборщик взял не свою долю перекрытия");
            Assert.AreEqual(0.525f, w.PlayerAt(1).Pos.x, 0.01f,
                "второй сборщик взял не свою долю перекрытия");
        }

        /// ⭐ WITNESS FOR RULING 113, and the sharpest of the four: a collector
        /// standing still is DISPLACED by the one that runs into it — that is
        /// the positional half doing its job — but its VELOCITY must not move
        /// a millimetre per second, because the blow would have to be derived
        /// from somebody else's speed.
        ///
        /// The ruling called that "impossible to break rather than merely
        /// documented -- the data is not there", and the data really is not:
        /// PushableBody carries no velocity. The spill made it available
        /// anyway, by the back door of the OTHER collector's pass — and the
        /// client, whose CollectorPass hands Accumulate two empty reciprocal
        /// spans, cannot reproduce a single metre per second of it.
        [Test]
        public void StandingCollector_IsNotShovedByAnotherCollectorsMotion()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(0f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(2.5f, 0f));

            PlayerState p = w.PlayerAt(0);
            p.SlideTimer = cfg.Hero.SlideDuration;
            p.SlideDir = new float2(1f, 0f);
            w.SetPlayerForTest(0, p);

            float worst = 0f;
            float2 stoodAt = w.PlayerAt(1).Pos;
            var inputs = new SimInput[2];
            for (int i = 0; i < 20; i++)
            {
                w.TickAll(inputs);
                worst = math.max(worst, math.length(w.PlayerAt(1).Vel));
            }

            Assert.AreEqual(0f, worst, 1e-4f,
                "стоящий сборщик получил скорость от чужого движения");
            // The premise: the slide really did arrive. Without it a fixture
            // where nothing ever touched would pass the assert above.
            Assert.Greater(math.distance(w.PlayerAt(1).Pos, stoodAt), 0.1f,
                "премисса: подкат не дошёл — стоящего никто не сдвинул");
        }

        /// ⭐ PARITY ON THE THIRD PAIR, the half of finding C-1 that reaches
        /// the wire. PredictionAndServerAgree_WhenTheBodyIsVisible pins the
        /// collector↔mob pair; this one pins the pair where BOTH bodies run a
        /// pass of their own on the server and only ONE of them exists on the
        /// client.
        ///
        /// The same two pieces of fixture craft the mob version documents
        /// apply and for the same reasons: the body is frozen back onto its
        /// snapshot after every tick (the client's span necessarily holds last
        /// tick's position), and it is a body that never moves under its own
        /// power anyway — a collector with no input.
        [Test]
        public void PredictionAndServerAgree_WhenTheBodyIsAnotherCollector()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(0f, 0f));
            var stands = new float2(2f, 0f);
            TestWorlds.RelocatePlayerForTest(w, 1, stands);

            var input = new SimInput { MoveDir = new float2(1f, 0f) };
            PlayerState predicted = w.PlayerAt(0);
            var bodies = new PushableBody[1];

            for (int i = 0; i < 25; i++)
            {
                bodies[0] = new PushableBody(w.PlayerAt(1).Pos, cfg.Hero.Radius, cfg.Hero.Mass);
                PlayerPrediction.Step(ref predicted, in input, in cfg,
                    Ring.Simulation.Combat.ImpactPulse.None,
                    new System.ReadOnlySpan<PushableBody>(bodies));
                w.TickAll(new[] { input, default(SimInput) });

                PlayerState q = w.PlayerAt(1);
                q.Pos = stands; q.Vel = float2.zero;
                w.SetPlayerForTest(1, q);
            }

            Assert.Less(math.distance(predicted.Pos, w.PlayerAt(0).Pos), 0.01f,
                "предсказание разошлось с сервером на ВТОРОМ СБОРЩИКЕ");
            Assert.Less(w.PlayerAt(0).Pos.x, 2f - cfg.Hero.Radius,
                "премисса: сборщик прошёл сквозь сборщика — фикстура ничего не разводила");
        }

        /// A GUARD, GREEN BEFORE AND AFTER (lesson 427), and it earns its place
        /// against the mutation that deletes the equal-key guard in
        /// ResolveBodyPair's degenerate branch.
        ///
        /// ⚠ TWO COLLECTORS ON ONE POINT CANNOT BE SEPARATED, and that is a
        /// property of the DATA rather than of the algorithm (ruling 121). The
        /// degenerate direction is derived from the pair's tie-break keys; a
        /// collector's key is its MASS BITS (PushableBody carries no id, ruling
        /// 116), and two collectors weigh the same by construction. Equal keys
        /// do not flip on an argument swap, so both sides would compute the
        /// SAME direction and the pair would travel across the arena together
        /// at MaxDepenetrationPerTick — a runaway, not a separation. Declining
        /// the pair is the one answer both sides reach identically.
        ///
        /// It was green before this round too, but by ACCIDENT rather than by
        /// rule: the doubled spill made the two halves cancel to exactly zero.
        [Test]
        public void TwoCollectorsOnTheSamePoint_AreLeftAlone_Guard()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            // The premise IS the fixture: OpenField sets PlayerSpawnRingFrac = 0,
            // so Geometry.SpawnPosFor puts every player on the origin.
            Assert.AreEqual(0f, math.distance(w.PlayerAt(0).Pos, w.PlayerAt(1).Pos), 1e-6f,
                "премисса: фикстура не поставила двух сборщиков в одну точку");

            w.TickAll(new SimInput[2]);

            Assert.AreEqual(0f, math.length(w.PlayerAt(0).Pos), 1e-4f,
                "первый сборщик уехал из вырожденной пары");
            Assert.AreEqual(0f, math.length(w.PlayerAt(1).Pos), 1e-4f,
                "второй сборщик уехал из вырожденной пары");
        }

        /// Witness for finding I-1: a CORPSE IS NOT AN OBSTACLE, and until this
        /// round it was one.
        ///
        /// SnapshotBodies keeps a dead collector's slot (the reciprocals are
        /// indexed by slot and a map would be a second answer) and gives it
        /// ZERO RADIUS, justifying that in as many words: "a body of radius 0
        /// can never overlap, so ResolveBodyPair returns false for every pair
        /// it is in". The arithmetic says otherwise — the gate is
        /// `(rA + rB) - dist > 0`, so a corpse of radius 0 overlaps a LIVING
        /// body of radius 0.45 at any distance under 0.45 m. Zero guarantees
        /// `false` only against a second zero.
        ///
        /// The corpse is offset in y so the wrong behavior is visible as a
        /// DEFLECTION rather than as a slow-down: nothing else in this fixture
        /// can move the collector off the x axis.
        [Test]
        public void DeadCollector_DoesNotSeparateTheLiving()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 0, new float2(0f, 0f));
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(2f, 0.3f));

            PlayerState corpse = w.PlayerAt(1);
            corpse.Hp = 0f;
            corpse.Alive = false;
            w.SetPlayerForTest(1, corpse);

            var input = new SimInput { MoveDir = new float2(1f, 0f) };
            for (int i = 0; i < 30; i++) w.TickAll(new[] { input, default(SimInput) });

            Assert.AreEqual(0f, w.PlayerAt(0).Pos.y, 1e-4f,
                "труп с нулевым радиусом столкнул живого сборщика с прямой");
            // The premise: the living collector really did walk over the spot.
            Assert.Greater(w.PlayerAt(0).Pos.x, 2f,
                "премисса: сборщик не дошёл до трупа — тест ничего не проверил");
        }

    }
}
