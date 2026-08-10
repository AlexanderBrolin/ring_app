using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 45c (bd `app-bej`): the closed-form floor cut of an aimed
    /// shot — how far along the muzzle→aim line a descending round gets before
    /// `ProjectileSystem` retires it on the ground. The defect that motivated
    /// it is a picture, not a rule: the aim ray and the crosshair marker
    /// promised the mathematical point on the floor, which the round never
    /// reaches because the ground contact is resolved at the round's CENTRE
    /// height, one radius up.
    ///
    /// EVERY EXPECTATION IS A FIXTURE EXPRESSION, never a number copied out of
    /// `Assets/Data/*.asset` — the balance is data (CR 6) and a test that
    /// restated it would go green on a stale copy of it. `MatchesTheSimulations
    /// OwnFloorCut` below is the anchor of the whole class: it does not check
    /// the formula against algebra at all, it fires a real round through
    /// `ProjectileSystem` and measures where that round actually ended.
    public class TrajectoryTests
    {
        /// Open arena, no spread and no recoil: these fixtures measure where a
        /// single round comes down, so a randomized muzzle angle would move the
        /// contact point every expectation is built from. Same shape (and same
        /// reason) as `HitZoneTests.Range()`/`PvpDamageTests.Range()`.
        /// `TestConfigs.Open()` also pushes the first wave out of reach, so no
        /// mob and no mob's round ever enters the event buffer this class reads.
        static SimConfig Range()
        {
            var c = TestConfigs.Open();
            c.Weapon.SpreadRad = 0f;
            c.Weapon.RecoilPerShotRad = 0f;
            return c;
        }

        /// Fires a genuine aimed round from the arena origin at the hero's own
        /// standing muzzle height, down the +X axis at a FLOOR point `distance`
        /// metres away, runs it to its end, and returns the fraction of that
        /// muzzle→aim line the round actually covered.
        ///
        /// `TestWorlds.FireAimed3D` rather than a held trigger on purpose: it
        /// spawns the round at the stated origin with no `MuzzleOffset`
        /// pre-advance and no cooldown overshoot, so the line the fraction is
        /// measured along is exactly the line the caller named.
        static float SimulatedFloorCut(in SimConfig c, float distance)
        {
            var w = new SimulationWorld(1, c);
            float2 origin = float2.zero;
            TestWorlds.FireAimed3D(w, origin, c.Hero.MuzzleHeight, new float2(distance, 0f), 0f);
            w.ClearEvents();

            int ticks = TestWorlds.RunUntilProjectilesDie(w);
            Assert.Less(ticks, 120, "fixture premise: the round ends well inside the stall guard");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked, out SimEvent end),
                "fixture premise: a descending round ends on the floor, not on TTL");
            Assert.AreEqual(0f, math.length(end.HitDir), 1e-6f,
                "fixture premise: HitDir is exactly zero only on ProjectileSystem's FLOOR branch");
            Assert.AreEqual(c.Weapon.ProjectileRadius, end.Amount, 1e-4f,
                "the ground contact happens at the round's CENTRE height, i.e. one radius up");
            return math.distance(origin, end.Pos) / distance;
        }

        [Test]
        public void AimAtTheFloor_FallsShortByTheRoundsOwnRadius()
        {
            var c = Range();
            float h = c.Hero.MuzzleHeight;
            float r = c.Weapon.ProjectileRadius;
            Assert.Greater(h, r, "fixture premise: the hero's muzzle sits above the round's radius");

            float cut = Trajectory.FloorCutFraction(h, 0f, r);

            Assert.Less(cut, 1f, "a round aimed at the floor never reaches the aimed point");
            Assert.AreEqual((h - r) / h, cut, 1e-6f,
                "the shortfall is the radius as a share of the drop the shot had to make");
        }

        [Test]
        public void LevelShot_ReachesTheAimedPoint()
        {
            var c = Range();
            float h = c.Hero.MuzzleHeight;

            Assert.AreEqual(1f, Trajectory.FloorCutFraction(h, h, c.Weapon.ProjectileRadius), 1e-6f,
                "a shot on the muzzle's own height never descends, so it has no floor cut");
        }

        [Test]
        public void ClimbingShot_ReachesTheAimedPoint()
        {
            var c = Range();
            float h = c.Hero.MuzzleHeight;
            float aim = c.Chaser.HeadTop; // a mob's head — above the hero's muzzle
            Assert.Greater(aim, h, "fixture premise: the aimed point is ABOVE the muzzle");

            Assert.AreEqual(1f, Trajectory.FloorCutFraction(h, aim, c.Weapon.ProjectileRadius), 1e-6f,
                "a climbing round moves away from the floor, so nothing cuts it short");
        }

        [Test]
        public void AimExactlyAtTheCutHeight_ReachesTheAimedPoint()
        {
            var c = Range();
            float r = c.Weapon.ProjectileRadius;

            Assert.AreEqual(1f, Trajectory.FloorCutFraction(c.Hero.MuzzleHeight, r, r), 1e-6f,
                "the aimed point IS the height the ground contact happens at — the cut lands on it");
        }

        [Test]
        public void AimJustBelowTheCutHeight_FallsShortButLessThanTheFloorDoes()
        {
            var c = Range();
            float h = c.Hero.MuzzleHeight;
            float r = c.Weapon.ProjectileRadius;

            float shallow = Trajectory.FloorCutFraction(h, r * 0.5f, r);
            float floor = Trajectory.FloorCutFraction(h, 0f, r);

            Assert.Less(shallow, 1f, "an aimed point under the contact height is still unreachable");
            Assert.Greater(shallow, floor, "the shallower the aim, the further the round gets");
        }

        [Test]
        public void MuzzleAtOrBelowTheCutHeight_GroundsAtTheMuzzle()
        {
            var c = Range();
            float r = c.Weapon.ProjectileRadius;

            Assert.AreEqual(0f, Trajectory.FloorCutFraction(r, 0f, r), 1e-6f,
                "a muzzle already at the contact height has no line left to travel");
            Assert.AreEqual(0f, Trajectory.FloorCutFraction(r * 0.5f, 0f, r), 1e-6f,
                "a muzzle below it is grounded on the spot rather than sent backwards");
        }

        [Test]
        public void SlidingMuzzle_CutsAtItsOwnHeight()
        {
            var c = Range();
            float r = c.Weapon.ProjectileRadius;
            Assert.Less(c.Hero.SlideMuzzleHeight, c.Hero.MuzzleHeight,
                "fixture premise: the hero fires from lower while sliding");

            float sliding = Trajectory.FloorCutFraction(c.Hero.SlideMuzzleHeight, 0f, r);
            float standing = Trajectory.FloorCutFraction(c.Hero.MuzzleHeight, 0f, r);

            Assert.Less(sliding, standing,
                "the lower the muzzle, the bigger the share of the drop the radius eats");
            Assert.AreEqual((c.Hero.SlideMuzzleHeight - r) / c.Hero.SlideMuzzleHeight, sliding, 1e-6f);
        }

        [Test]
        public void MatchesTheSimulationsOwnFloorCut_AndTheMissGrowsWithRange()
        {
            var c = Range();
            float expected = Trajectory.FloorCutFraction(c.Hero.MuzzleHeight, 0f, c.Weapon.ProjectileRadius);

            const float near = 10f;
            const float far = 20f;
            float nearCut = SimulatedFloorCut(in c, near);
            float farCut = SimulatedFloorCut(in c, far);

            Assert.AreEqual(expected, nearCut, 1e-3f,
                "the helper must answer what ProjectileSystem actually did at 10 m");
            Assert.AreEqual(expected, farCut, 1e-3f,
                "and at 20 m — the cut is a fraction of the line, not a fixed distance");
            Assert.AreEqual(2f * (1f - nearCut) * near, (1f - farCut) * far, 1e-2f,
                "the owner-visible symptom: twice the range, twice the miss in metres");
        }
    }
}
