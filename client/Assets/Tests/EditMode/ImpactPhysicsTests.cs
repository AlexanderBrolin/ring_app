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
    }
}
