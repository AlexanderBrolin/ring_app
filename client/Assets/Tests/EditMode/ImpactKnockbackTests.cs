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
    /// at float2.zero — the point these tests shoot FROM. No test here calls
    /// TestWorlds.RelocatePlayerForTest, and none may start doing so while
    /// staying on Open(): a collector moved inside ZoneRadius[0] = 65 m would
    /// spawn the Director and his retinue on top of the shot, which is what
    /// TestConfigs.OpenField() exists for (R-173).
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
    }
}
