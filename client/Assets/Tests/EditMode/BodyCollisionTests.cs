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
        /// the Director is 4.4 m across, so a dash CAN end inside that body, and
        /// without MaxDepenetrationPerTick the collector would be thrown 0.97 m
        /// in a single tick.
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
        [Test]
        public void Push_GrowsWithApproachSpeed()
        {
            TestWorlds.RunIntoBody(MobType.Chaser, TestWorlds.MoveMode.Run, 0f,
                out float run, out _);
            TestWorlds.RunIntoBody(MobType.Chaser, TestWorlds.MoveMode.Slide, 0f,
                out float slide, out _);
            TestWorlds.RunIntoBody(MobType.Chaser, TestWorlds.MoveMode.Dash, 0f,
                out float dash, out _);

            Assert.Greater(slide, run * 1.5f, "подкат толкнул не сильнее бега");
            Assert.Greater(dash, slide * 1.3f, "дэш толкнул не сильнее подката");
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

            // Measured: 1.99 m/s sideways against 7.71 head-on. The sideways
            // figure is not zero and should not be — as the collector slides
            // past, the contact normal ROTATES and picks up a component along
            // the travel, which is the projection doing its job rather than
            // failing it. Under the mutation that reads the speed's magnitude
            // instead, the sideways figure becomes the head-on one (7.71).
            Assert.Less(sideways, 3f,
                "тело отброшено движением ВДОЛЬ него — скорость взята не по нормали");
            Assert.Greater(headOn, sideways * 2f,
                "лобовой толчок не отличается от бокового — проекция не применяется");
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
            // rather than an exception. No behavioural witness for the "only
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
        /// passes trivially, because both sides do nothing. Here the body is
        /// there, both sides must separate the collector IDENTICALLY, and the
        /// last assert states the premise — the fixture really did put a body in
        /// the way — so a world where nothing separated could not pass by
        /// agreeing on doing nothing.
        ///
        /// ⚠⚠ THE BODY IS PINNED IN PLACE EVERY TICK, and that is the fixture's
        /// whole craft rather than a convenience (session 72). The client's span
        /// necessarily holds LAST TICK's body position — it has no mobs to
        /// simulate — while the server separates against the position the body
        /// has NOW. A body that moves therefore guarantees a mismatch, and the
        /// first form of this test measured exactly that instead of the rule:
        /// 0.196 m of divergence, all of it the shove the collector had just
        /// given the chaser. Holding the body still removes the one difference
        /// that is NOT the rule, so what remains under the assert is the rule
        /// alone.
        ///
        /// The real, un-pinnable version of that gap is named honestly elsewhere
        /// and belongs to the lag gate: a networked client separates against
        /// snapshot positions ~140 ms old, worth up to 0.73 m per tick, which is
        /// risk Р-F and point 7 of the Ф4 gate — not something a fixture can
        /// assert away.
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
        ///      this test spawns. MatchFlowSystem puts the DIRECTOR at
        ///      float2.zero the moment a live collector stands in the Core zone,
        ///      plus his retinue — so the server separates the collector from a
        ///      4000 kg body the first version of this span knew nothing about,
        ///      and the two sides disagreed by 0.198 m for a reason that was
        ///      never the rule.
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
            // ⚠ THE BODY IS IMMOBILISED BY CONFIG, not only frozen after the
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

            // ⚠ THE WORLD HAS TO SETTLE before the comparison starts. The
            // Director is not born in the constructor but on a phase transition
            // inside the FIRST tick — and the server separates from him on that
            // same tick, while the client's span, taken BEFORE the tick, does
            // not know he exists yet. Five warm-up ticks retire the question:
            // after them the set of bodies no longer changes.
            for (int i = 0; i < 5; i++) w.TickAll(new[] { input, default });

            PlayerState predicted = w.PlayerAt(0);
            var frozen = new System.Collections.Generic.List<float2>();
            var bodies = new System.Collections.Generic.List<PushableBody>();

            for (int i = 0; i < 25; i++)
            {
                // A snapshot of EVERY body in the world — the chaser, and the
                // Director with his retinue that MatchFlowSystem puts at the
                // arena centre.
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
                (MobType.Chaser, pts[0]));           // ТОТ ЖЕ набор, ОБРАТНЫЙ порядок
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
    }
}
