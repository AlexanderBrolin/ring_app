using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// ⚠ EVERY TEST IN THIS FILE PINS AN IDENTITY, NOT A NEW RULE (rule 427),
    /// and saying so is the whole point of this doc. app-8dv T3 moves the shot's
    /// geometry out of `WeaponSystem.SpawnShot` into `ShotGeometry` and the aimed
    /// half of the cone out of an inline expression into `Spread.AimRadians`. The
    /// move is meant to be VERBATIM -- the same expressions, in the same order,
    /// in the same grouping -- so these tests testify to nothing about how a shot
    /// is aimed; their job is to catch a move that changed the answer.
    ///
    /// ⛔⛔ WHAT THEY DO NOT GUARD, SAID FIRST AND MEASURED RATHER THAN
    /// REASONED. The witness that the move was verbatim is NOT in this file --
    /// it is the three golden replay hashes, pinned in T2 to values taken
    /// BEFORE the move, on the last sanction the owner granted. These tests
    /// cannot be that witness, and the reason is structural: since T3 the
    /// authoritative tick reaches its geometry by calling `ShotGeometry.Solve`
    /// itself, so an expectation read off the world's own `ProjectileFired`
    /// event and an expectation read off `Solve` are the SAME number arriving
    /// twice. Any change inside `Solve` moves both sides of such a comparison
    /// together -- the tautology `f(x) == f(x)` (rule 428) wearing the clothes
    /// of a cross-check.
    ///   That is a measurement, not a worry. Mutation M270 (the K9 pre-step
    /// taken before the pattern instead of after) was run in exactly the shape
    /// the plan describes, with the muzzle-point assertions below in place: it
    /// SURVIVED this file 3/3, and it was killed by all three goldens at once.
    /// The muzzle assertions are kept because they do guard something real --
    /// see their own note at the call -- but not that.
    ///
    /// ⭐ WHAT THEY DO GUARD, then, is the seam rather than the arithmetic:
    /// that the fields of one solution agree with each other and with what the
    /// sink actually hands the round (the angle assertion below caught exactly
    /// that -- an early M270 shape that left `Dir` unrotated while `Vel` was
    /// rotated died on it), that `Dir` is still a unit vector, that the depth
    /// split answers all three of its numbers, and that no movement multiplier
    /// reached the aimed branch. ⚠ `HorizSpeed` needs no assertion of its own
    /// and gets none: it is a property computed from `Vel`, so it cannot
    /// disagree with it -- the review that found the gap in `Dir` found the
    /// same gap in `HorizSpeed`, and that one was closed in the structure
    /// rather than in a test. What
    /// they buy over the goldens is WHERE: a golden says "something moved over
    /// a thousand ticks", these say which field of the first shot of a fresh
    /// world disagrees with which.
    public class ShotGeometryTests
    {
        /// ⚠ THE STATE IS TAKEN BEFORE THE TICK ON PURPOSE. `Solve` must answer
        /// the state the world fires FROM, and for this fixture the two are the
        /// same state: hip fire holds still, so the movement phase leaves Pos,
        /// Vel and SlideTimer untouched, RecoilOffset decays from zero to zero,
        /// and both counters the pattern seeds off (BurstShots, ShotOrdinal) are
        /// read by the shot BEFORE the loop increments them. The one field that
        /// does move -- FireCooldown -- is not read by the geometry at all; it
        /// reaches `Solve` as `overshoot`, computed below.
        ///
        /// The seed also reads `input.AimPoint` rather than `p.AimPoint` (T1
        /// Step 8), so the uninitialized aim point of a fresh world's `p` cannot
        /// reach the answer -- otherwise this guard would be red on correct code.
        [Test]
        public void Solve_ReproducesTheAngleTheWorldFires()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            SimInput fire = TestWorlds.HipFire();
            w.ClearEvents();
            var before = w.Player;
            w.Tick(fire);

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileFired, out SimEvent shot),
                "премисса: тик под тестом обязан выстрелить");
            // ⚠ THE FIRST SHOT'S OVERSHOOT IS dt, NOT ZERO -- a number read off
            // the code rather than guessed at: `Advance` subtracts dt from
            // FireCooldown BEFORE its loop, so on entry FireCooldown is -dt and
            // math.min(-FireCooldown, dt) is dt. At the fixture's ProjectileSpeed
            // of 35 m/s a one-tick error here would be 1.17 m of muzzle offset,
            // which is why it is stated instead of assumed.
            ShotSolution s = ShotGeometry.Solve(in before, in fire, in cfg,
                overshoot: SimulationWorld.TickDt);
            // `shot.Amount` is atan2 of the velocity the sink put into the
            // round, `s.Dir` is the direction the same solution answered: the
            // two are different FIELDS of one answer, which is why this
            // comparison is not the tautology the muzzle ones are.
            Assert.AreEqual(shot.Amount, math.atan2(s.Dir.y, s.Dir.x), 1e-5f,
                "направление решения разошлось со скоростью, которую сток положил в снаряд");
            // ⚠ AND THE LENGTH, BECAUSE atan2 CANNOT SEE IT (review finding).
            // Without this line a `Dir` scaled by any positive factor passes the
            // assertion above -- the mutant that doubled it survived all 1858
            // tests, since nothing else in the tree reads this field yet. `Dir`
            // is a unit vector by construction (normalizesafe, whose fallback
            // `new float2(1f, 0f)` is unit too), and T5 will multiply it by a
            // speed to seed the predicted trail, so the length is load-bearing
            // rather than decorative.
            Assert.AreEqual(1f, math.length(s.Dir), 1e-5f,
                "направление перестало быть единичным — трассер T5 умножит его на скорость");

            // ⭐ AND THE MUZZLE POINT, NOT ONLY THE ANGLE -- but for the sink's
            // sake, not the geometry's. ⚠ THESE TWO DO NOT KILL M270, MEASURED:
            // the mutant that takes the K9 pre-step before the pattern moves the
            // event and `s.SpawnPos` by the same amount, because the event is
            // built from `s.SpawnPos`, and it survived this file 3/3 (the
            // goldens killed it). What they DO catch is the sink handing the
            // round a point that is not the one the solution answered -- the
            // wiring, which no golden localizes and which T4 is about to add a
            // second consumer to.
            // ⛔⛔ THE POINT IS READ OFF THE EVENT, NEVER OFF THE ROUND'S BODY.
            // The birth event reports the MUZZLE, a pre-step point, while every
            // projectile body is an end-of-tick state (RewindTests says the same
            // of the same event), and by the end of the tick the round has
            // already taken its ordinary step. Comparing against
            // `GetProjectileForTest(0).Pos` would be off by
            // ProjectileSpeed * TickDt = 1.17 m against a 1e-4 tolerance -- red
            // on correct code.
            Assert.AreEqual(shot.Pos.x, s.SpawnPos.x, 1e-4f, "точка вылета уехала по X");
            Assert.AreEqual(shot.Pos.y, s.SpawnPos.y, 1e-4f, "точка вылета уехала по Y");
        }

        /// Ruling 291: the rewind depth is split ONCE and spent twice -- the
        /// input half is both the round's birth-tick step count and the bound of
        /// the catch-up that walks it. Two calls would be one number with two
        /// homes, so `ShotSolution` answers all three numbers and `BirthSteps` is
        /// computed from `InputTicks` rather than stored beside it.
        [Test]
        public void Solve_SplitsTheRewindDepthOnce()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            SimInput fire = TestWorlds.HipFire();
            // The premise is a PROPERTY of the fixture, not a literal (rules
            // 307/308): the deepest legal claim. The cast is the one
            // SimInputSanitizer makes for the same pair -- RewindTicks is a byte,
            // RewindCapTicks an int.
            fire.RewindTicks = (byte)cfg.Arena.RewindCapTicks;
            ShotSolution s = ShotGeometry.Solve(in p, in fire, in cfg, overshoot: 0f);
            Assert.AreEqual(cfg.Arena.RewindPictureTicks, s.PictureTicks, "картинная половина");
            Assert.AreEqual(cfg.Arena.RewindCapTicks - cfg.Arena.RewindPictureTicks, s.InputTicks,
                "входная половина");
            Assert.AreEqual(s.InputTicks + 1, s.BirthSteps,
                "birthSteps — догоняющие шаги ПЛЮС один обычный шаг тика рождения");
        }

        /// Р469: the aimed branch of the cone is NOT scaled by movement. That is
        /// a fact of the code -- SpreadRunMult and SpreadSlideMult live in
        /// `Spread.HipRadians` and nowhere else -- and it has to survive the move
        /// of the aimed half into `Spread.AimRadians` beside it.
        ///
        /// ⚠ A GUARD, AND GREEN ON A CONSTANT STUB (rule 427): both calls answer
        /// the stub's single value, so this test says nothing until the real body
        /// is in. Its predicted color on the stub step is therefore GREEN, and
        /// the step that predicts otherwise is the one that would stop the task
        /// on a false alarm.
        [Test]
        public void AimRadians_CarriesNoMovementMultiplier()
        {
            SimConfig cfg = TestConfigs.OpenField();
            var p = new PlayerState { RecoilOffset = cfg.Weapon.RecoilMaxRad };
            float still = Spread.AimRadians(in cfg.Weapon, in p, in cfg.Hero);
            p.SlideTimer = cfg.Hero.SlideDuration;
            Assert.AreEqual(still, Spread.AimRadians(in cfg.Weapon, in p, in cfg.Hero), 1e-6f,
                "прицельный конус расширился слайдом — в него протёк множитель движения");
        }
    }
}
