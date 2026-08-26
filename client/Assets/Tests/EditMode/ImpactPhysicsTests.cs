using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;   // SimulationWorld.TickDt, SimConfig, MobState (Т5/Т6)
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// The impact formula itself (app-88jb Т2, spec §4.3 tests 2-5b). Numbers
    /// here are an EXPLICIT in-test fixture, not TestConfigs: the arithmetic
    /// is the subject, so it has to be readable in the same screen as the
    /// assertion (precedent DashRicochetTests.Fixture()).
    ///
    /// SEVEN TESTS GO THROUGH THE WORLD, and they break that sentence on
    /// purpose: Т5's three (spec §4.3 tests 6/7/9, from
    /// HitAboveCenterOfMass_TipsAlongTheShot_BelowUndercutsIt on), Т6's two
    /// tilt-threshold witnesses (spec tests 8/10) and Т13's two playtest debts
    /// (app-hoe6 and app-mhw3, the last two tests in this file). What they witness is that the
    /// moment reaches MobState.TiltVel and that TickAll steps the spring --
    /// properties of the WIRING, which no pure function can show. They still
    /// state no literal of their own: every number they need is READ OFF the
    /// fixture (cfg.Chaser.CenterOfMassHeight, cfg.Weapon.ProjectileMass,
    /// cfg.Weapon.ProjectileSpeed), so the two-sources-of-numbers rule holds
    /// and no value from the shipped .asset appears here.
    ///
    /// THE VERY LAST TEST, ProtocolVersion_IsPinnedToFour, GOES THROUGH NEITHER
    /// (Т6). It is a wire-domain sentinel, not impact arithmetic: a new
    /// MobAiState enlarges the Mobs block's nibble domain, and a peer speaking
    /// the older version refuses the WHOLE block as MalformedContent
    /// (SnapshotBlocks.cs:510). It lives here because Т6 is the task that grows
    /// that domain, and a sentinel filed away from its own cause is a sentinel
    /// nobody re-reads.
    ///
    /// `Ai = MobAiState.Idle` IN EVERY TEST HERE THAT SETS IT IS A STATED
    /// STARTING STATE, NOT A FREEZE (finding Н-5, caught by a run in Т4 and not
    /// by reading). It freezes nothing twice over: SimulationWorld.SpawnMob
    /// already writes that very value into every fresh MobState, and
    /// MobAiSystem's UpdateChaser Idle branch overwrites it with Chase on its
    /// first line (MobAiSystem.cs:180-183), because
    /// Targeting.NearestAlivePlayer (Targeting.cs:122) has no aggro radius at
    /// all and the collector standing on the spawn ring is a live target from
    /// tick one. FOR A GUNNER IT IS WEAKER STILL: UpdateGunner never READS
    /// m.Ai at all and rewrites it every tick from distance alone -- Reposition
    /// (MobAiSystem.cs:338) or Fire (:349).
    ///
    /// THE MOB THEREFORE WALKS, AND NOTHING HERE ZEROES ITS LOCOMOTION -- the
    /// opposite of ImpactKnockbackTests, which measures Vel and therefore must
    /// freeze it. The reason is about tilt, not about motion: TiltVel is
    /// written in exactly two places, the impulse in DamageMob and the spring
    /// step in TiltSystem, and a walking chaser earns neither (its own melee
    /// leaves through DamagePlayer, which touches no mob). It also never
    /// arrives: TestConfigs.Open() puts the collector 159.16 m out
    /// (Arena.Radius 173 x PlayerSpawnRingFrac 0.92), the mob starts at 6 m,
    /// and at MaxSpeed 5.2 the longest test here (300 ticks = 10 s) covers
    /// ~52 m of that 153 m gap. Zeroing MaxSpeed/Accel would be an edit
    /// without a cause (coordinator R-Т5-1).
    ///
    /// Т13'S TWO ARE THE SECOND DELIBERATE EXCEPTION TO THAT PARAGRAPH: they
    /// DO zero MaxSpeed/Accel, and they have the cause the sentence above asks
    /// for. Both stand a chaser at a fixed distance and hit the SAME body twice
    /// at heights read off its own parts, so the two blows must be told apart
    /// by the arm alone; a body that walked between them would be answering a
    /// different question each time. Both also replace the fixture's 35 m/s
    /// with the game's 52.5 (each states why in its own doc) — an explicit
    /// in-test fixture, which is what the first paragraph of this doc asks of
    /// any test whose arithmetic is the subject.
    ///
    /// Т6's TiltAboveTheThreshold_PutsTheMobDown_AndItGetsUpOnItsOwn IS THE
    /// DELIBERATE EXCEPTION TO BOTH SENTENCES ABOVE: it takes OpenField()
    /// (collector at the origin, no spawn ring) and stands the gunner EXACTLY
    /// at PreferredRange, so the mob engages and fires from tick one. That is
    /// the whole point -- "a downed mob did not fire" is a witness only if a
    /// standing one would have fired.
    public class ImpactPhysicsTests
    {
        const float Eps = 1e-4f;

        [Test]
        public void VelocityDelta_IsProportionalToProjectileSpeed()
        {
            // Test 2: twice as fast is twice as hard. The ceiling is
            // deliberately high so the proportion stays visible instead of
            // being clipped (otherwise the test would prove that the ceiling
            // works, not that the shove is proportional).
            float slow = Impact.VelocityDelta(2.6f, 20f, 90f, 100f, 1f);
            float fast = Impact.VelocityDelta(2.6f, 40f, 90f, 100f, 1f);
            Assert.AreEqual(2.6f * 20f / 90f, slow, Eps);
            Assert.AreEqual(2f * slow, fast, Eps, "скорость снаряда не пропорциональна толчку");
        }

        [Test]
        public void VelocityDelta_IsInverselyProportionalToTargetMass()
        {
            // Test 3. A witness SEPARATE from test 2: the "return a constant"
            // mutation kills both, but the "do not divide by the mass"
            // mutation kills only this one.
            float light = Impact.VelocityDelta(2.6f, 35f, 70f, 100f, 1f);
            float heavy = Impact.VelocityDelta(2.6f, 35f, 140f, 100f, 1f);
            Assert.AreEqual(2f * heavy, light, Eps, "толчок не обратно пропорционален массе цели");
        }

        [Test]
        public void VelocityDelta_IsCappedByTheTargetsOwnCeiling()
        {
            // Test 4. Without the ceiling this would come out at
            // 2.6 * 300 / 70 = 11.14 m/s -- twice the Gunner's own top speed.
            float uncapped = 2.6f * 300f / 70f;
            Assert.Greater(uncapped, 6f, "фикстура не упирается в потолок — тест ничего не проверяет");
            Assert.AreEqual(6f, Impact.VelocityDelta(2.6f, 300f, 70f, 6f, 1f), Eps);
        }

        [Test]
        public void VelocityDelta_CocoonDividesExactly()
        {
            // Test 5: the cocoon damps by EXACTLY CocoonDamping, not "roughly".
            float bare = Impact.VelocityDelta(3.0f, 14f, 120f, 100f, 1f);
            float damped = Impact.VelocityDelta(3.0f, 14f, 120f, 100f, 3f);
            Assert.AreEqual(bare / 3f, damped, Eps, "кокон гасит не в CocoonDamping раз");
        }

        [Test]
        public void VelocityDelta_CeilingAppliesBeforeTheCocoon()
        {
            // Test 5b -- the witness of the ORDER (Р393). The raw value
            // 2.6*300/120 = 6.5 is above the ceiling of 6; the ceiling BEFORE
            // the cocoon gives 6/3 = 2, the ceiling AFTER the cocoon would
            // give min(6.5/3, 6) = 2.1667. Told apart BY THE NUMBER.
            float raw = 2.6f * 300f / 120f;
            Assert.Greater(raw, 6f, "фикстура не упирается в потолок — порядок неразличим");
            Assert.AreEqual(2f, Impact.VelocityDelta(2.6f, 300f, 120f, 6f, 3f), Eps,
                "потолок применён ПОСЛЕ кокона: эффективный потолок сборщика уехал");
        }

        [Test]
        public void SpringFromSettle_MatchesTheShippedNumbers()
        {
            // The numbers are derived FROM THE FORMULA, not copied out of a
            // table (lesson 475): wn = 4/(0.55*0.9) = 8.0808, k = wn^2 =
            // 65.2995, c = 2*0.55*wn = 8.8889.
            // The peak coefficient of a unit impulse on these constants, stated
            // as the THREE numbers that are genuinely different (Ruling 9,
            // round-3 finding C-C1 -- naming only one of them is what hid the
            // bug):
            //   0.047951 -- the INTEGRATOR: PeakTilt(1f, 0.55f, 0.9f, 1/30f),
            //               the very semi-implicit Euler walk the game runs.
            //               This is the ONLY one a game rule may be written on;
            //   0.064543 -- the CORRECTED continuous form,
            //               exp(-zeta*wn*phi/wd) * sin(phi) / wd. The test
            //               below checks it as a strict upper bound
            //               (PeakTilt_IsLinearInTheImpulse_And...);
            //   0.743    -- their ratio, i.e. the amplitude the discrete damping
            //               c*dt = 0.2963 clips away.
            // The 0.077282 that stood on this line until Ruling 9 was the
            // continuous form WITHOUT sin(phi) -- 1.19737x too high, and that
            // overstatement is exactly why milestone В1's "a headshot puts the
            // chaser down" was unreachable at TiltGain 6.5 while both witnesses
            // still read green. It is not a fourth measurement of anything; it
            // is a dropped factor, and it does not belong in this file.
            Impact.SpringFromSettle(0.55f, 0.9f, out float k, out float c);
            float wn = 4f / (0.55f * 0.9f);
            Assert.AreEqual(wn * wn, k, 1e-3f);
            Assert.AreEqual(2f * 0.55f * wn, c, 1e-3f);
            // And no zeta^2 factor was lost or gained along the way (A2-C1):
            Assert.AreEqual(65.2995f, k, 1e-2f, "формула пружины уехала от 65.30");
        }

        [Test]
        public void PeakTilt_IsLinearInTheImpulse_AndStaysBelowTheContinuousForm()
        {
            // ⚠⚠ REWRITTEN BY ROUND 3 (finding C-C1). v2 checked `PeakTilt`
            // against THE VERY closed form it was implementing itself -- a
            // tautology of class 428, and that is exactly what hid the loss
            // of the sin(phi) factor. Two assertions here, and neither one
            // repeats the code under test:
            //  (1) LINEARITY in the impulse -- a property of the recurrence,
            //      not of the formula;
            //  (2) an INEQUALITY against the CONTINUOUS form WITH sin(phi):
            //      the discrete damping c*dt = 0.296 is bound to clip the
            //      amplitude, so the integrator's peak is strictly below the
            //      continuous one and at the same time above half of it
            //      (fact: the ratio is 0.743 at zeta 0.55 / T 0.9 / dt 1/30).
            const float Zeta = 0.55f, Settle = 0.9f;
            float dt = SimulationWorld.TickDt;
            float one = Impact.PeakTilt(1f, Zeta, Settle, dt);
            Assert.Greater(one, 0f, "пик нулевой — интегратор не крутится");
            Assert.AreEqual(3f * one, Impact.PeakTilt(3f, Zeta, Settle, dt), 1e-5f,
                "пик крена нелинеен по импульсу");

            Impact.SpringFromSettle(Zeta, Settle, out float k, out _);
            float wn = math.sqrt(k);
            float wd = wn * math.sqrt(1f - Zeta * Zeta);
            float phi = math.atan(wd / (Zeta * wn));
            // ⚠ sin(phi) == wd/wn AT THE POINT OF THE MAXIMUM -- exactly the
            // factor v2 dropped, overstating the number by 1.19737x.
            float continuous = math.exp(-Zeta * wn * phi / wd) * math.sin(phi) / wd;
            Assert.Less(one, continuous,
                "дискретный интегратор не срезал амплитуду — PeakTilt снова считает "
                + "замкнутую форму вместо прогона");
            Assert.Greater(one, 0.5f * continuous,
                "пик упал больше чем вдвое — dt или пружина уехали");
        }

        [Test]
        public void PeakTilt_HeadshotKnocksTheChaserDown_BodyDoesNot()
        {
            // ⭐ A RULE OF THE GAME, NOT A TABLE (spec §3.2): a precise shot
            // to the head knocks a light mob off its feet, a hit to the body
            // does not.
            // ⚠⚠ THE TiltGain NUMBER WAS RECALIBRATED BY ROUND 3 (6.5 ->
            // 10.5). On the real integrator, at 6.5 a headshot gave the
            // Chaser 0.586 rad (33.6 deg) against the threshold of 0.9 (51.6
            // deg) -- that is, the milestone В1 criterion was NOT reached AT
            // ALL, and v2 did not see it because it computed the peak with
            // the overstated closed form (0.945). At 10.5 the rule is
            // restored in full: head 0.947, body 0.252, Elite 0.513,
            // Director 0.033 -- numbers run through python over the very
            // step SpringStep executes. ⚠ The number itself is the owner's
            // taste and sits in milestone В1's tuning list.
            // ⚠⚠ app-88jb Т13 (coordinator Ruling 60): THE ARM HERE IS HANDED
            // IN BY HAND, and that is now stated instead of implied. 1.24 m
            // over a ZERO center of mass is a number this test chooses;
            // whether the game's own geometry ever produces such an arm is a
            // DIFFERENT question, and playtest В1 answered it with "no" — on
            // today's column the chaser's head sits at [1.45, 1.85] against a
            // center of mass of 1.17, i.e. an arm of 0.48. This test keeps the
            // half it is good for, the FORMULA; the world half is held by
            // WorldHeadshot_OnTheChasersOwnHeadPart_KnocksItDown_BodyDoesNot
            // below, which reads the contact height off the chaser's own head
            // part and watches for MobAiState.Downed.
            const float Gain = 10.5f;
            float dt = SimulationWorld.TickDt;
            float dv = Impact.VelocityDelta(2.6f, 52.5f, 90f, 6f, 1f);
            // The arm is expressed as the contact height above a ZERO center
            // of mass -- that way the arm's number reads off directly, and
            // AngularImpulse takes part in a witness instead of staying
            // without a single call.
            float head = Impact.PeakTilt(
                Impact.AngularImpulse(hitHeight: 1.24f, centerOfMassHeight: 0f,
                    dv: dv, gain: Gain), 0.55f, 0.9f, dt);
            float body = Impact.PeakTilt(
                Impact.AngularImpulse(hitHeight: 0.33f, centerOfMassHeight: 0f,
                    dv: dv, gain: Gain), 0.55f, 0.9f, dt);
            Assert.Greater(head, 0.9f, "хедшот не валит чейзера — критерий вехи В1 не наблюдается");
            Assert.Less(body, 0.9f, "попадание в корпус валит — хедшот перестал быть особенным");
        }

        [Test]
        public void PeakTilt_NothingInTodaysArsenalKnocksTheHeavyOnesDown()
        {
            // The opposite half of the same rule, and it is NOT a tautology
            // against the previous test: that one is about the light
            // archetype, this one about the heavy ones, and it is precisely
            // this assertion that catches a spurious zeta^2 factor (under
            // mutant M4a the Elite gives 1.075 rad against the threshold of
            // 0.9 -- computed through the integrator).
            const float Gain = 10.5f;
            float dt = SimulationWorld.TickDt;
            float elite = Impact.PeakTilt(
                Impact.AngularImpulse(1.94f, 0f,
                    Impact.VelocityDelta(2.6f, 52.5f, 260f, 6f, 1f), Gain), 0.55f, 0.9f, dt);
            float director = Impact.PeakTilt(
                Impact.AngularImpulse(1.94f, 0f,
                    Impact.VelocityDelta(2.6f, 52.5f, 4000f, 6f, 1f), Gain), 0.55f, 0.9f, dt);
            Assert.Less(elite, 0.9f, "элиту валит сегодняшнее оружие");
            Assert.Less(director, 0.9f, "Директора валит сегодняшнее оружие");
        }

        [Test]
        public void HitAboveCenterOfMass_TipsAlongTheShot_BelowUndercutsIt()
        {
            // Tests 6 and 7 under ONE witness -- the two signs are told apart
            // BY THE NUMBER, and the fixture puts the center of mass STRICTLY
            // BETWEEN the two contact heights (otherwise both heights would
            // carry the same sign and the test would be true under any
            // implementation at all).
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            var m = new MobState { Id = 1, Type = MobType.Chaser, Pos = new float2(6f, 0f),
                Hp = 1e6f, Ai = MobAiState.Idle };
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            w.SetMobForTest(0, m);

            float com = cfg.Chaser.CenterOfMassHeight;
            w.DamageMob(0, 1f, new float2(6f, 0f), HitZone.Head, new float2(1f, 0f),
                ownerIndex: 0, hitHeight: com + 0.5f,
                projectileMass: cfg.Weapon.ProjectileMass,
                projectileSpeed3D: cfg.Weapon.ProjectileSpeed);
            float high = w.Mobs[0].TiltVel;

            var reset = w.Mobs[0]; reset.TiltVel = 0f; reset.Tilt = 0f;
            w.SetMobForTest(0, reset);
            w.DamageMob(0, 1f, new float2(6f, 0f), HitZone.Legs, new float2(1f, 0f),
                ownerIndex: 0, hitHeight: com - 0.5f,
                projectileMass: cfg.Weapon.ProjectileMass,
                projectileSpeed3D: cfg.Weapon.ProjectileSpeed);
            float low = w.Mobs[0].TiltVel;

            Assert.Greater(high, 0f, "попадание ВЫШЕ центра масс не валит тело по ходу");
            Assert.Less(low, 0f, "попадание НИЖЕ центра масс не подсекает тело");
            Assert.AreEqual(-high, low, 1e-4f,
                "плечо считается не от центра масс: симметричные высоты дали несимметричный момент");
        }

        [Test]
        public void Tilt_ReturnsToExactlyZero_InAFiniteNumberOfTicks()
        {
            // Test 9: an EXACT zero, not "approximately". Without the rest
            // snap the exponential walks off into the denormal range and the
            // digest becomes platform-dependent (FTZ/DAZ differ between the
            // Linux server and the Windows client).
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            var m = w.Mobs[0];
            m.Ai = MobAiState.Idle; m.Hp = 1e6f; m.Tilt = 0.3f; m.TiltVel = 0f;
            w.SetMobForTest(0, m);

            for (int i = 0; i < 300; i++) w.Tick(default);

            Assert.AreEqual(0f, w.Mobs[0].Tilt, 0f, "крен не пришёл в ТОЧНЫЙ ноль за 10 секунд");
            Assert.AreEqual(0f, w.Mobs[0].TiltVel, 0f, "угловая скорость не пришла в ТОЧНЫЙ ноль");
        }

        [Test]
        public void Tilt_Oscillates_BeforeItSettles()
        {
            // A witness of the REGIME: at zeta 0.55 the system is underdamped,
            // so the tilt is obliged to cross zero at least once. An aperiodic
            // integrator (zeta >= 1) would not pass this -- and spec v1 called
            // the regime exactly that, in the same sentence as the other one
            // (finding A-M1).
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            var m = w.Mobs[0];
            m.Ai = MobAiState.Idle; m.Hp = 1e6f; m.Tilt = 0.3f; m.TiltVel = 0f;
            w.SetMobForTest(0, m);

            bool crossed = false;
            for (int i = 0; i < 90 && !crossed; i++)
            {
                w.Tick(default);
                if (w.Mobs[0].Tilt < 0f) crossed = true;
            }
            Assert.IsTrue(crossed, "крен не качнулся через ноль — режим не колебательный");
        }

        [Test]
        public void TiltAboveTheThreshold_PutsTheMobDown_AndItGetsUpOnItsOwn()
        {
            // Spec tests 8 and 10. TWO witnesses of the exit, not one: "does
            // not fire while it is down" and "fires again once it is up" --
            // a "Downed forever" mutation would pass the first half alone.
            // THREE round-3 corrections (findings Г-C3 / D-I5), without which
            // both witnesses were EMPTY:
            // (1) OpenField(), and the gunner EXACTLY at PreferredRange:
            //     MobAiSystem.cs:336-347 sends the mob into Reposition and
            //     returns whenever `dist` falls outside [PreferredRange -
            //     RangeTolerance, PreferredRange + RangeTolerance] = [7.5,
            //     10.5] m. Under Open() the collector stands on the spawn ring
            //     159 m out and the gunner used to stand at 6 m -- it would not
            //     have fired STANDING either, so "the downed mob did not fire"
            //     was true under every implementation.
            // (2) FireInterval pinned by an EXPLICIT fixture to 0.2 s (6 ticks)
            //     against the 1.2 s (36 ticks) Downed window: at the stock
            //     1.6 s a standing gunner would not fire ONCE inside that
            //     window and the witness would be empty again. At 0.2 s a
            //     standing one fires about six times -- a structural
            //     difference, not a marginal one.
            // (3) The count is taken over ProjectileFired EVENTS, not over
            //     ProjectileCount: a gunner's round covers the 9 m in ~19 ticks
            //     and disappears, so the live counter would move with no new
            //     shot fired at all.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Gunner.FireInterval = 0.2f;                 // explicit fixture, see (2)
            var w = new SimulationWorld(7, cfg);
            var hero = w.Player; hero.Hp = 1e6f; w.SetPlayerForTest(hero);
            w.SpawnMobForTest(MobType.Gunner, new float2(cfg.Gunner.PreferredRange, 0f));
            var m = w.Mobs[0];
            m.Hp = 1e6f; m.Ai = MobAiState.Fire; m.FireCooldown = 0f;
            // THE TIMER IS DELIBERATELY LARGE (review finding D-I3): a mob
            // entering Downed with a fresh StateTimer of about one dt would
            // stand up just one tick early under the "do not reset the timer"
            // mutation, and the checkpoint below cannot tell that apart. Five
            // seconds make the difference structural: with no reset the mob
            // stands up IMMEDIATELY.
            m.StateTimer = 5f;
            m.Tilt = cfg.Gunner.TiltFallAngle + 0.05f; m.TiltVel = 0f;
            w.SetMobForTest(0, m);

            w.Tick(default);
            Assert.AreEqual(MobAiState.Downed, w.Mobs[0].Ai, "моб за порогом крена не упал");
            // The mob got its shot off BEFORE the fall (the AI phase runs ahead
            // of TiltSystem in the tick, SimulationWorld.cs:388-397) -- that is
            // legal, so the count runs from this mark rather than from zero.
            // TickAll never clears the event buffer (ClearEvents is called
            // explicitly and nowhere else, SimulationWorld.cs:2154), so
            // TestEvents.CountOf is a CUMULATIVE count across the ticks below.
            int firedBeforeDown = TestEvents.CountOf(w, SimEventKind.ProjectileFired);

            int downedTicks = SimulationWorld.TicksFromSeconds(cfg.Gunner.DownedSeconds);
            for (int i = 0; i < downedTicks - 2; i++) w.Tick(default);
            Assert.AreEqual(MobAiState.Downed, w.Mobs[0].Ai, "моб встал раньше DownedSeconds");
            Assert.AreEqual(firedBeforeDown, TestEvents.CountOf(w, SimEventKind.ProjectileFired),
                "лежачий моб стрелял");

            int budget = SimulationWorld.TicksFromSeconds(4f * cfg.Gunner.FireInterval);
            for (int i = 0; i < budget && w.Mobs[0].Ai == MobAiState.Downed; i++) w.Tick(default);
            Assert.AreNotEqual(MobAiState.Downed, w.Mobs[0].Ai, "моб не встал после DownedSeconds");

            // SECOND witness of the exit: back on its feet, the mob shoots again.
            for (int i = 0; i < budget
                 && TestEvents.CountOf(w, SimEventKind.ProjectileFired) == firedBeforeDown; i++)
                w.Tick(default);
            Assert.Greater(TestEvents.CountOf(w, SimEventKind.ProjectileFired), firedBeforeDown,
                "встав, моб не возобновил огонь — второго свидетеля выхода нет");
        }

        [Test]
        public void TiltExactlyAtTheThreshold_DoesNotKnockDown()
        {
            // The boundary is STRICT (`>`), and this is the witness for the
            // `>` -> `>=` mutation.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            w.SpawnMobForTest(MobType.Gunner, new float2(6f, 0f));
            var m = w.Mobs[0];
            // `Ai = MobAiState.Idle` HERE IS A STATED STARTING STATE, NOT A
            // FREEZE (finding Н-5, and see this class's own header): under
            // Open() the collector stands 159.16 m out, far outside the
            // gunner's [7.5, 10.5] m band, so UpdateGunner overwrites Idle
            // with Reposition inside this very tick (MobAiSystem.cs:338). The
            // assertion below is NEGATIVE, so that changes nothing about what
            // it witnesses -- but the seam must not be read as a freeze.
            m.Hp = 1e6f; m.Ai = MobAiState.Idle;
            m.Tilt = cfg.Gunner.TiltFallAngle; m.TiltVel = 0f;   // EXACTLY the threshold
            w.SetMobForTest(0, m);
            w.Tick(default);
            // THE WITNESS IS ALIVE ONLY UNDER THE "CHECK, THEN STEP" ORDER
            // (round-3 correction D-C1): under the old "step, then check" a
            // tilt of exactly 0.9 had already become 0.8347 by the time of the
            // comparison, and `>` was indistinguishable from `>=` in principle.
            Assert.AreNotEqual(MobAiState.Downed, w.Mobs[0].Ai, "порог опрокидывания замкнут");
        }

        [Test]
        public void ProtocolVersion_IsPinnedToFour()
        {
            // Domain sentinel: a new AI state IS A CHANGE OF THE WIRE DOMAIN,
            // and an older peer would refuse the whole Mobs block as
            // MalformedContent.
            Assert.AreEqual(4, Ring.Networking.Protocol.ProtocolVersion.Current);
        }

        [Test]
        public void WorldHeadshot_OnTheChasersOwnHeadPart_KnocksItDown_BodyDoesNot()
        {
            // ⭐ THE MILESTONE В1 CRITERION, THIS TIME THROUGH THE WORLD (debt
            // app-hoe6, coordinator Rulings 60/61). The formula witness above
            // hands the arm in by hand and therefore cannot see whether the
            // game's geometry can produce it -- playtest В1 found it could not.
            // Here the contact height is READ OFF the chaser's own head part
            // and the fall is OBSERVED as MobAiState.Downed through TiltSystem,
            // so what is under test is the body's proportions, not the spring.
            //
            // ⚠ THIS IS THE FIRST OF TWO HALVES (coordinator Ruling 65). A
            // witness that puts a ROUND in flight cannot exist yet: the hit
            // gate still reads the old column (ProjectileSystem's
            // AcceptCandidate takes `overlapTop` from cfg.HeadTop, 1.85 for the
            // chaser), which Т13 does not move — a shot aimed at 2.41 m simply
            // passes over the body. Т14 repoints that gate at these very parts
            // and lands the flying half of the same criterion; debt app-hoe6
            // closes there, not here.
            //
            // TWO EXPLICIT FIXTURES, both load-bearing (Global Constraints: a
            // test whose arithmetic is the subject builds its own fixture --
            // precedent DashRicochetTests.Fixture):
            //  1. ProjectileSpeed 52.5, THE GAME's number, not the shared
            //     fixture's 35. At 35 m/s dv = 2.6*35/90 = 1.0111 and an arm of
            //     1.24 peaks at 0.6313 rad (36.2 deg) against the 0.9 rad
            //     (51.6 deg) threshold -- the criterion would read RED on
            //     entirely correct code. At 52.5 dv = 1.5167 and the peak is
            //     0.9469 rad (54.25 deg).
            //  2. MaxSpeed/Accel zeroed: the mob is FROZEN, so its own
            //     locomotion cannot mix into the measurement (finding Н-5's
            //     lesson, applied on purpose here). OpenField(), not Open(),
            //     puts the collector at the origin instead of 159.16 m out on
            //     the spawn ring.
            //
            // ⚠ THE MARGIN IS THIN AND IS THEREFORE NAMED (Ruling 61). The
            // threshold is reached at an arm of 1.1786 m, i.e. at a contact
            // height of 2.349 m. Once this task's data step lands, the head
            // belt is [2.12, 2.70] and only its UPPER 60 % knocks the chaser
            // over; the middle of the belt, 2.41 m, clears that line by 0.061 m
            // and that is the whole of the margin. A hit to the LOWER third of
            // the head leaves the chaser standing -- arithmetic of the arm, not
            // a defect, and it belongs on milestone В2's tuning list.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Weapon.ProjectileSpeed = 52.5f;   // explicit fixture, see (1)
            cfg.Chaser.MaxSpeed = 0f;             // explicit fixture, see (2)
            cfg.Chaser.Accel = 0f;
            // The premise pins the GAME's number, not "differs from the shared
            // fixture" (review finding B-6): the shared fixture is free to
            // change, and what this test needs is the 52.5 m/s the shipped
            // WeaponConfig fires at -- at the fixture's 35 the peak is 36.2 deg
            // against a 51.6 deg threshold and the criterion reads red on
            // entirely correct code.
            Assert.AreEqual(52.5f, cfg.Weapon.ProjectileSpeed, 1e-4f,
                "фикстура: скорость снаряда обязана быть ИГРОВОЙ (52.5), иначе тест красен на верном коде");

            var w = new SimulationWorld(7, cfg);
            PlayerState hero = w.Player; hero.Hp = 1e6f; w.SetPlayerForTest(hero);
            var mobPos = new float2(6f, 0f);
            w.SpawnMobForTest(MobType.Chaser, mobPos);

            // One blow at the MIDDLE of the named part, then as many ticks as
            // the spring needs to reach its peak (it peaks on step 4; the
            // budget is one settle time, and the Downed window of 1.2 s is
            // longer than that, so a body that fell is still down when the loop
            // ends). The threshold is tested BEFORE the step, so the fall lands
            // one tick after the peak -- TiltSystem's own documented order.
            bool KnocksDown(HitPart part)
            {
                MobState m = w.Mobs[0];
                m.Hp = 1e6f; m.Ai = MobAiState.Idle;
                m.Tilt = 0f; m.TiltVel = 0f; m.StateTimer = 0f;
                w.SetMobForTest(0, m);
                w.DamageMob(0, 1f, mobPos, part.Zone, new float2(1f, 0f), ownerIndex: 0,
                    hitHeight: 0.5f * (part.Bottom + part.Top),
                    projectileMass: cfg.Weapon.ProjectileMass,
                    projectileSpeed3D: cfg.Weapon.ProjectileSpeed);
                int budget = SimulationWorld.TicksFromSeconds(cfg.Chaser.TiltSettleSeconds);
                for (int i = 0; i < budget && w.Mobs[0].Ai != MobAiState.Downed; i++)
                    w.Tick(default);
                return w.Mobs[0].Ai == MobAiState.Downed;
            }

            HitPart[] parts = cfg.Chaser.Parts;
            Assert.IsTrue(KnocksDown(parts[parts.Length - 1]),
                "выстрел в середину головы не валит чейзера — критерий вехи В1 миром не наблюдается");
            Assert.IsFalse(KnocksDown(parts[parts.Length - 2]),
                "попадание в корпус валит — хедшот перестал быть особенным");
        }

        [Test]
        public void TiltImpulse_TheHeadRocksTheChaserHarderThanTheLegs()
        {
            // ⭐ THE SECOND PLAYTEST DEBT (app-mhw3, coordinator Ruling 62), and
            // today the proportion is INVERTED. The chaser's center of mass sits
            // at 1.17 m of a 1.85 m column -- 63 % of the way up -- so the legs
            // (middle 0.30, arm 0.87) out-rock the head (middle 1.65, arm 0.48):
            // 38.1 deg against 21.0 deg at the game's projectile speed. After
            // this task's data step the column is [0, 0.88) / [0.88, 2.12) /
            // [2.12, 2.70], the same center of mass sits at 43 % of it, and the
            // head's arm of 1.24 beats the legs' 0.73 -- the head rocks 1.70x
            // HARDER, which is what a body is supposed to do.
            //
            // ⚠ IT GUARDS THE NUMBERS, NOT THE FORMULA: roll the column back to
            // 1.85 and the ratio flips, so this dies with the DATA. The formula
            // half is already held by HitAboveCenterOfMass_TipsAlongTheShot_
            // BelowUndercutsIt, which places its two heights SYMMETRICALLY
            // around the center of mass on purpose -- and symmetry is exactly
            // what cannot show a body whose proportions are wrong.
            //
            // |TiltVel| is read IMMEDIATELY after the blow, with no tick in
            // between, so the two readings are the two angular impulses and
            // nothing else; the tilt and the angular velocity are zeroed
            // through the seam between them.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Weapon.ProjectileSpeed = 52.5f;   // the game's number, as above
            cfg.Chaser.MaxSpeed = 0f;
            cfg.Chaser.Accel = 0f;
            var w = new SimulationWorld(7, cfg);
            var mobPos = new float2(6f, 0f);
            w.SpawnMobForTest(MobType.Chaser, mobPos);

            float RockOf(HitPart part)
            {
                MobState m = w.Mobs[0];
                m.Hp = 1e6f; m.Ai = MobAiState.Idle;
                m.Tilt = 0f; m.TiltVel = 0f; m.StateTimer = 0f;
                w.SetMobForTest(0, m);
                w.DamageMob(0, 1f, mobPos, part.Zone, new float2(1f, 0f), ownerIndex: 0,
                    hitHeight: 0.5f * (part.Bottom + part.Top),
                    projectileMass: cfg.Weapon.ProjectileMass,
                    projectileSpeed3D: cfg.Weapon.ProjectileSpeed);
                return math.abs(w.Mobs[0].TiltVel);
            }

            HitPart[] parts = cfg.Chaser.Parts;
            float legs = RockOf(parts[0]);
            float head = RockOf(parts[parts.Length - 1]);

            // Premise: without it a mutation that zeroes the legs' impulse would
            // satisfy the comparison below while proving nothing at all.
            Assert.Greater(legs, 0f,
                "фикстура: попадание в ноги не качнуло тело вовсе — сравнивать нечего");
            Assert.Greater(head, legs,
                "выстрел в ноги качает сильнее, чем в голову — пропорция тела не починена");
        }
    }
}
