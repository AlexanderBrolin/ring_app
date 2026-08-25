using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Knockback of a hit mob (app-88jb Т4, spec §3.2 / §4.3 test 1): the
    /// impulse of a round that lands has to show up in the SAME MobState.Vel
    /// SeparationSystem.Apply already adds into, never as a direct write to
    /// Pos — a body that is shoved keeps traveling under its own velocity for
    /// the ticks that follow, a body whose Pos is written jumps once and stops.
    ///
    /// AND OF A HIT COLLECTOR (app-88jb Т7, spec §3.2 / §4.3 test 49), which
    /// is the same arithmetic through the same Impact.VelocityDelta with two
    /// differences that are decisions rather than accidents: the collector's
    /// figure is DIVIDED BY HeroSimConfig.CocoonDamping — the cocoon is what
    /// makes a hit read as a stagger rather than as a launch — and the
    /// collector has no knockdown threshold at all (Р377, ADR-001 §9). Both
    /// halves of this class live here rather than in two files because the
    /// formula, the fixture rules and the tick semantics they depend on are
    /// one subject (rule 2).
    ///
    /// THE MOBS ARE FROZEN BY ZEROING THEIR OWN LOCOMOTION, AND NOTHING ELSE
    /// FREEZES THEM (coordinator Ruling 17, finding Н-5 — caught by a RUN, not
    /// by reading). Each test states an explicit in-test fixture that zeroes
    /// MaxSpeed and Accel of the archetypes it spawns, because the arithmetic
    /// is the subject here — the same rule ImpactPhysicsTests and
    /// DashRicochetTests.Fixture() already follow.
    ///
    /// Writing MobAiState.Idle does NOT freeze a mob and is deliberately absent
    /// below. Targeting.NearestAlivePlayer (Targeting.cs:122) has no aggro
    /// radius at all, so the collector standing 159.16 m away is a live target
    /// from the first tick; MobAiSystem.UpdateChaser's Idle branch writes
    /// Ai = Chase on its very first line ("spends this tick settling into
    /// Chase"); and UpdateGunner — the branch BOTH Gunner and Elite take while
    /// out of range — never reads Ai in the first place. Left un-frozen, every
    /// mob here accelerates toward that collector along +x, which is the SAME
    /// direction the round travels: on a constant stub the first assertion
    /// below PASSED at Vel.x = 4.0 = Gunner.MaxSpeed, and the magnitude test
    /// was measuring locomotion rather than impact.
    ///
    /// ZERO ACCEL KEEPS THE SHOVE INSTEAD OF SWALLOWING IT:
    /// PlayerMovementSystem.MoveTowards(cur, target, 0) returns `cur` untouched
    /// for any non-zero cur (:409-415), MobAiSystem runs BEFORE ProjectileSystem
    /// in TickAll (:388-390), and TestWorlds.RunUntilProjectilesDie stops on the
    /// tick of the hit — so Vel is read exactly as DamageMob left it.
    ///
    /// THE FIXTURE IS TestConfigs.Open(), AND THAT IS DELIBERATE (coordinator
    /// R-Т4-1, and finding 4 of this epic is what makes it worth writing down).
    /// Open() places the collector on the spawn ring — Arena.Radius 173 ×
    /// PlayerSpawnRingFrac 0.92 = 159.16 m from the origin — and every test in
    /// this class states its geometry in absolute coordinates and fires with an
    /// EXPLICIT segment start (TestWorlds.FireAimed3D(w, float2.zero, …)), so
    /// that ring never enters the arithmetic. It is also what keeps the
    /// Director out of the measurement: MatchFlowSystem.AnyLiveCollectorInCore
    /// reads COLLECTOR positions (w.PlayerAt(i)), not mob positions, and it is
    /// consulted only in MatchPhase.Farm, so a collector left standing in the
    /// outer ring never activates the phase machine and nothing is ever spawned
    /// at float2.zero — the point these tests shoot FROM. None of the four
    /// tests that stay on Open() calls TestWorlds.RelocatePlayerForTest, and
    /// none of them may start doing so while staying on Open(): a collector
    /// moved inside ZoneRadius[0] = 65 m would spawn the Director and his
    /// retinue on top of the shot, which is what TestConfigs.OpenField()
    /// exists for (R-173).
    ///
    /// AND THAT PARAGRAPH IS EXACTLY WHY THE TWO COLLECTOR-KNOCKBACK TESTS
    /// ARE ON OpenField() (app-88jb Т7, coordinator Ruling 14).
    /// HitCollector_IsShoved_ButTheCocoonDividesIt and
    /// ShotEatenByIframes_ShovesNobody both put the VICTIM at (6, 0) and fire
    /// from the origin — the one move the paragraph above forbids on Open() —
    /// so they take the fixture it names instead. The guarantee there is
    /// STRUCTURAL rather than geometric: MatchFlowSystem.
    /// AnyLiveCollectorInCore opens with `if (arena.ZoneRadius.Length &lt; 2)
    /// return false`, and OpenField() empties that array, so the phase
    /// transition that spawns the Director is unreachable by construction
    /// instead of merely unlikely. OpenField() also drops the spawn ring
    /// (PlayerSpawnRingFrac = 0), which stands the SHOOTER on the origin the
    /// shot starts from — harmless, because ProjectileSystem excludes a
    /// round's own owner from its candidate set, the same exclusion the
    /// paragraph above already leans on.
    /// CollectorIsNeverKnockedDown_HoweverHardTheHit stays on Open(): it
    /// fires nothing, moves nobody and never leaves the spawn ring, so no
    /// collector ever enters the core.
    ///
    /// Open() builds on Quiet(), so FirstWaveDelay = 1e6 keeps wave mobs out of
    /// a fixture that indexes w.Mobs[0]/w.Mobs[1] by spawn slot, and no mob
    /// here dies from its single round (chaser 12 of 30 Hp, gunner 9 of 20,
    /// elite 9 of 120) — a death would swap-remove the slot and every index
    /// below would point at a different body.
    public class ImpactKnockbackTests
    {
        [Test]
        public void HitMob_IsShovedAlongTheProjectile_AndDoesNotTeleport()
        {
            SimConfig cfg = TestConfigs.Open();
            cfg.Chaser.MaxSpeed = 0f; cfg.Chaser.Accel = 0f;   // the ONLY freeze — see class doc
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            // The mob starts at rest, stated rather than assumed: what the
            // assertions read has to be the impulse and nothing else.
            var m = w.Mobs[0];
            m.Vel = float2.zero;
            w.SetMobForTest(0, m);
            float2 posBefore = w.Mobs[0].Pos;

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.Greater(w.Mobs[0].Vel.x, 0f,
                "толчок не лёг в Vel: моб не поехал по ходу снаряда");
            Assert.AreEqual(posBefore.x, w.Mobs[0].Pos.x, 0.35f,
                "моб ТЕЛЕПОРТИРОВАН в тик попадания — импульс написан в Pos, а не в Vel");
        }

        [Test]
        public void KnockbackMagnitude_IsTheImpactFormula_NotAConstant()
        {
            // A witness BY NUMBER: the expectation is computed from
            // Impact.VelocityDelta with the same arguments instead of repeating
            // the code under test (lesson 428).
            SimConfig cfg = TestConfigs.Open();
            cfg.Chaser.MaxSpeed = 0f; cfg.Chaser.Accel = 0f;   // the ONLY freeze — see class doc
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            var m = w.Mobs[0];
            m.Vel = float2.zero;
            w.SetMobForTest(0, m);

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);

            float expected = Ring.Simulation.Combat.Impact.VelocityDelta(
                cfg.Weapon.ProjectileMass, cfg.Weapon.ProjectileSpeed,
                cfg.Chaser.Mass, cfg.Chaser.ImpactSpeedCap, 1f);
            Assert.AreEqual(expected, math.length(w.Mobs[0].Vel), 0.02f,
                "толчок не равен формуле импакта");
        }

        [Test]
        public void HeavierArchetype_IsShovedLess_BySameShot()
        {
            // The second witness of the inverse proportion — this one THROUGH
            // THE WORLD rather than on the pure function (lesson 470: a pair
            // "value → outcome" gets two witnesses, and one of them has to be
            // observable in the game).
            SimConfig cfg = TestConfigs.Open();
            cfg.Gunner.MaxSpeed = 0f; cfg.Gunner.Accel = 0f;   // the ONLY freeze — see class doc
            cfg.Elite.MaxSpeed = 0f; cfg.Elite.Accel = 0f;
            var w = new SimulationWorld(7, cfg);
            TestWorlds.SpawnMobsAt(w,
                (MobType.Gunner, new float2(6f, 0f)),
                (MobType.Elite, new float2(6f, 12f)));
            for (int i = 0; i < w.MobCount; i++)
            {
                var mi = w.Mobs[i];
                mi.Vel = float2.zero;
                w.SetMobForTest(i, mi);
            }

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);
            float gunnerPush = math.length(w.Mobs[0].Vel);

            TestWorlds.FireAimed3D(w, new float2(0f, 12f), muzzleH: 1f,
                targetXY: new float2(6f, 12f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);
            float elitePush = math.length(w.Mobs[1].Vel);

            Assert.Greater(gunnerPush, elitePush,
                "элиту (260 кг) толкает не меньше ганнера (70 кг)");
        }

        // ------------------------------------------------- the collector half

        [Test]
        public void HitCollector_IsShoved_ButTheCocoonDividesIt()
        {
            // WHY Vel IS READABLE THE INSTANT THE HELPER RETURNS, stated
            // because a single extra tick would eat 40 / 30 = 1.33 m/s of it
            // against a tolerance of 0.02: TestWorlds.RunUntilProjectilesDie
            // tests ProjectileCount BEFORE each tick, so the tick that
            // retires the round is the last one it runs; and inside that tick
            // ProjectileSystem runs AFTER every collector has moved
            // (SimulationWorld.TickAll's canonical order), so nothing decays
            // the shove between DamagePlayer writing it and this assertion
            // reading it. The impulse of tick T lands in Vel at the END of T
            // and only moves the body from T+1 (finding A2-C5).
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            // The absence of i-frames is STATED, not inherited from a fresh
            // world's zeros — the negative test below turns exactly this
            // field on, and the pair only means something if both sides say
            // what they are.
            var victim = w.PlayerAt(1); victim.IframeTimer = 0f; w.SetPlayerForTest(1, victim);

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);

            // A witness BY NUMBER, computed from the fixture's own config
            // rather than from a literal (spec §0/Р56) and through
            // Impact.VelocityDelta rather than by repeating the code under
            // test (lesson 428). CocoonDamping is passed here where the mob
            // tests above pass 1f: that divisor IS the difference between the
            // two bodies, so a build that dropped it fails this and nothing
            // else.
            float expected = Ring.Simulation.Combat.Impact.VelocityDelta(
                cfg.Weapon.ProjectileMass, cfg.Weapon.ProjectileSpeed,
                cfg.Hero.Mass, cfg.Hero.ImpactSpeedCap, cfg.Hero.CocoonDamping);
            Assert.AreEqual(expected, math.length(w.PlayerAt(1).Vel), 0.02f,
                "толчок по сборщику не равен формуле с делением на кокон");

            // AND THE ANGULAR HALF OF THE SAME BLOW (implementer's finding on
            // the GREEN step: without this line DamagePlayer's
            // Impact.AngularImpulse call is a branch NO mutation could kill --
            // nothing else in the suite reads a collector's tilt after a real
            // round). It is closed inside the test that already builds the
            // fixture rather than in a new one, which is the precedent Т6 set
            // with its own M6c (rule 2).
            //
            // A SIGN, not a magnitude, and deliberately: the value that lands
            // here has already been through one TiltSystem step in the same
            // tick, so an expectation by number would have to restate the
            // spring integrator -- the tautology lesson 428 forbids. The sign
            // is a real claim all the same: the arm is `hitHeight -
            // CenterOfMassHeight`, this shot is FLAT at 1 m, and the premise
            // below is what keeps that geometry honest if the fixture ever
            // moves. Deleting either the angular impulse or TiltSystem's
            // collector pass leaves this at exactly zero.
            Assert.Greater(1f, cfg.Hero.CenterOfMassHeight,
                "fixture premise: the round must land ABOVE the collector's center of mass, "
                + "or the sign asserted below is not the one this geometry produces");
            Assert.Greater(w.PlayerAt(1).Tilt, 0f,
                "момент попадания выше центра масс не наклонил сборщика по ходу выстрела");
        }

        [Test]
        public void ShotEatenByIframes_ShovesNobody()
        {
            // Finding D2-I13: a dash is immune to the SHOVE as well as to the
            // damage, and it has to be, because the two are decided in one
            // place. DamagePlayer returns on `IframeTimer > 0` before it
            // emits PlayerDamaged at all, so an impulse applied above that
            // guard would be one the server delivered and the client never
            // heard about — a guaranteed divergence rather than a balance
            // question. The impulse therefore lives INSIDE DamagePlayer,
            // after both guards.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(7, cfg, playerCount: 2);
            TestWorlds.RelocatePlayerForTest(w, 1, new float2(6f, 0f));
            // A full second of i-frames, so the window is still open when the
            // round arrives some six ticks later — PlayerMovementSystem counts
            // this timer down every tick, and a value sized to the flight
            // would be a fixture that decides the outcome by arithmetic
            // nobody wrote down.
            var victim = w.PlayerAt(1); victim.IframeTimer = 1f; victim.Vel = float2.zero;
            w.SetPlayerForTest(1, victim);

            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 1f,
                targetXY: new float2(6f, 0f), targetH: 1f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(0f, math.length(w.PlayerAt(1).Vel), 1e-4f,
                "удар, съеденный i-frames, всё-таки толкнул");
        }

        [Test]
        public void CollectorIsNeverKnockedDown_HoweverHardTheHit()
        {
            // Spec test 49 (Р377): the collector has NO knockdown threshold.
            // A mob past MobSimConfig.TiltFallAngle goes to MobAiState.Downed
            // and stops acting for DownedSeconds; HeroSimConfig carries no
            // such angle, because taking control away from a player because a
            // round landed contradicts ADR-001 §9, where evasion is the skill
            // the fight is asking for. What the collector gets instead is the
            // spring: a tilt far past anything a real hit could produce still
            // has to come BACK, on its own, with the body alive and playing.
            //
            // Open() rather than OpenField(), and deliberately (Ruling 14's
            // boundary): nothing is fired, nobody is relocated and the lone
            // collector stays on the spawn ring 159.16 m out, so the core is
            // never occupied and the Director is never woken.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(7, cfg);
            // Three radians is past face-down and far past any angle the
            // arsenal can reach — the claim is about the ABSENCE of a
            // threshold, so the fixture has to stand where a threshold would
            // certainly have fired.
            var p = w.Player; p.Tilt = 3f; p.TiltVel = 0f; w.SetPlayerForTest(p);
            w.Tick(default);
            Assert.IsTrue(w.Player.Alive, "сборщик умер от крена");
            Assert.Less(math.abs(w.Player.Tilt), 3f, "крен сборщика не возвращается пружиной");
        }
    }
}
