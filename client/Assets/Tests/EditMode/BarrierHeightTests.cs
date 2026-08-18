using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 46 (bd app-r8x): interior barriers — the obstacle circles
    /// and the stadium walls — carry a modelled top, so a round whose whole
    /// remaining step sits above `Arena.BarrierTop` passes over them. The
    /// arena's outer ring boundary deliberately does NOT: it holds the edge of
    /// the world (owner decision 2026-08-11).
    ///
    /// Every fixture here states its own `BarrierTop` in the test body. The
    /// shared baseline (`TestConfigs.DefaultArena`) stays at 0, "no modelled
    /// top" — see the first test below and that fixture's own comment for why
    /// the mirror against `ArenaConfig` is broken there on purpose.
    public class BarrierHeightTests
    {
        /// Open arena (no obstacles, no walls) with both mob archetypes frozen
        /// and their weapon pushed out of reach: the only round in flight in
        /// these fixtures is the one the test itself states, so an event count
        /// means what it says. Geometry is added per test through PutObstacle/
        /// PutWall below.
        static SimConfig Field(float barrierTop)
        {
            var c = TestConfigs.Open();
            c.Chaser.MaxSpeed = 0f;
            c.Gunner.MaxSpeed = 0f;
            c.Gunner.StrafeSpeed = 0f;
            c.Gunner.FireInterval = 1e6f;
            c.Arena.BarrierTop = barrierTop;
            return c;
        }

        static void PutObstacle(ref SimConfig c, float2 pos, float radius)
        {
            c.Arena.ObstacleCount = 1;
            c.Arena.ObstaclePos = new[] { pos };
            c.Arena.ObstacleRadius = new[] { radius };
        }

        static void PutWall(ref SimConfig c, float2 a, float2 b, float halfWidth)
        {
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { a };
            c.Arena.WallB = new[] { b };
            c.Arena.WallHalfWidth = new[] { halfWidth };
        }

        /// Ф2 fix-round (review A-3): the arc barrier, stated the same way its
        /// two older siblings are. Spec §3.2 gives the zone rings NO height of
        /// their own — "одно правило на все внутренние барьеры" — so the claim
        /// that they obey Arena.BarrierTop is transitive, and until this round
        /// no test executed it: a mutation removing the height gate from the
        /// arc branch coloured nothing.
        static void PutArc(ref SimConfig c, float ringR, float halfWidth, float doorCenterRad,
            float doorFreeWidth)
        {
            c.Arena.ZoneWallCount = 1;
            c.Arena.ZoneWallRadius = new[] { ringR };
            c.Arena.ZoneWallHalfWidth = new[] { halfWidth };
            c.Arena.ZoneWallDoorStart = new[] { 0 };
            c.Arena.ZoneWallDoorCount = new[] { 1 };
            c.Arena.DoorCenterRad = new[] { doorCenterRad };
            c.Arena.DoorFreeWidth = new[] { doorFreeWidth };
        }

        /// One round, stated as geometry: where it starts, how high, how fast it
        /// climbs or falls. Damage/radius/lifetime come off the world's own
        /// config, same contract as TestWorlds.FireAimed3D — a fixture here only
        /// ever states the flight.
        static SimulationWorld Fire(in SimConfig c, float2 start, float height, float velZ)
        {
            var w = new SimulationWorld(1, c);
            w.SpawnProjectileForTest(ProjectileOwner.Player, start,
                new float2(c.Weapon.ProjectileSpeed, 0f), height, velZ,
                c.Weapon.Damage, c.Weapon.ProjectileRadius, c.Weapon.ProjectileLifetime);
            w.ClearEvents();
            return w;
        }

        static void Tick(SimulationWorld w, int ticks = 1)
        {
            for (int i = 0; i < ticks; i++) w.Tick(default);
        }

        /// Height the barrier's own gate has to be beaten by: the column is
        /// [0, BarrierTop] grown by the round's radius at both ends
        /// (HitZones.Overlaps), so "clear of the crown" starts strictly above
        /// this. An expression, never a number copied out of a fixture.
        static float CrownReach(in SimConfig c) => c.Arena.BarrierTop + c.Weapon.ProjectileRadius;

        [Test]
        public void NoModelledTop_IsWhatTheSharedBaselineAndTheGoldensRunOn()
        {
            // The anchor of the whole task: TestConfigs.DefaultArena — the
            // struct the golden scenarios tick against — must stay on the
            // pre-Task-46 branch, so the new gate is not merely expected to
            // leave the pinned hashes alone, it is never executed by them.
            Assert.LessOrEqual(TestConfigs.DefaultArena().BarrierTop, 0f,
                "the shared arena baseline must mean 'no modelled top' — the golden scenarios "
                + "run off it and a projectile's Height/VelZ both feed StateHash");

            // And the behavior that value stands for: a round on a line no
            // barrier could plausibly reach is still stopped by one.
            SimConfig c = Field(0f);
            const float obstacleX = 6f, obstacleRadius = 1.5f, flightHeight = 12f;
            PutObstacle(ref c, new float2(obstacleX, 0f), obstacleRadius);

            SimulationWorld w = Fire(in c, float2.zero, flightHeight, velZ: 0f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked),
                "with no modelled top the obstacle stops the round at any height");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked, out SimEvent end));
            Assert.AreEqual(flightHeight, end.Amount, 1e-4f,
                "the contact height the event reports is the round's own, unclamped");
            Assert.AreEqual(obstacleX - obstacleRadius - c.Weapon.ProjectileRadius, end.Pos.x, 1e-2f,
                "and the contact sits on the obstacle's padded rim, not somewhere past it");
        }

        [Test]
        public void AboveTheTop_TheRoundClearsAnObstacleAndKillsWhatStandsBehindIt()
        {
            // The rejection branch (M5) inside ONE tick: the round starts inside
            // BOTH the obstacle's padded circle and the mob's, so the gather
            // packs two candidates at t = 0 and the barrier — packed first —
            // takes that exact tie. Above the crown it is handed back, the scan
            // resumes over what is left, and the mob behind it still resolves on
            // the very same tick.
            const float obstacleX = 5f, obstacleRadius = 1.5f, mobX = 7f;
            const float top = 2f, flightHeight = 3f;

            SimulationWorld Shoot(float barrierTop, out SimConfig cfg)
            {
                cfg = Field(barrierTop);
                PutObstacle(ref cfg, new float2(obstacleX, 0f), obstacleRadius);
                var world = new SimulationWorld(1, cfg);
                TestWorlds.SpawnMobsAt(world, (MobType.Gunner, new float2(mobX, 0f)));
                world.SpawnProjectileForTest(ProjectileOwner.Player,
                    new float2(0.5f * (obstacleX + obstacleRadius + mobX - cfg.Gunner.Radius), 0f),
                    new float2(cfg.Weapon.ProjectileSpeed, 0f), flightHeight, 0f,
                    cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime);
                world.ClearEvents();
                Tick(world);
                return world;
            }

            SimulationWorld cleared = Shoot(top, out SimConfig c);
            float muzzleX = 0.5f * (obstacleX + obstacleRadius + mobX - c.Gunner.Radius);
            Assert.Less(muzzleX - obstacleX, obstacleRadius + c.Weapon.ProjectileRadius,
                "fixture premise: the round starts inside the obstacle's padded circle (t = 0)");
            Assert.Less(mobX - muzzleX, c.Gunner.Radius + c.Weapon.ProjectileRadius,
                "fixture premise: the round starts inside the mob's padded circle too (t = 0), "
                + "so barrier and mob really do tie");
            Assert.Greater(flightHeight, CrownReach(in c),
                "fixture premise: the flight clears the crown");
            Assert.LessOrEqual(flightHeight, c.Gunner.HeadTop + c.Weapon.ProjectileRadius,
                "fixture premise: and still falls inside the mob's own column");
            Assert.GreaterOrEqual(c.Weapon.Damage * c.Gunner.HeadDamageMult, c.Gunner.MaxHp,
                "fixture premise: one head hit is lethal, so 'reached the mob' reads as a kill");

            Assert.AreEqual(0, TestEvents.CountOf(cleared, SimEventKind.ProjectileBlocked),
                "the barrier was cleared, so nothing was blocked");
            Assert.AreEqual(0, cleared.MobCount,
                "and the mob behind it died on the same tick the barrier was handed back");

            // Control — the SAME geometry with no modelled top: now the barrier
            // wins the exact tie it is packed first for, and the mob survives.
            SimulationWorld blocked = Shoot(0f, out SimConfig noTop);
            Assert.AreEqual(1, TestEvents.CountOf(blocked, SimEventKind.ProjectileBlocked),
                "with no modelled top the barrier takes the tie against the mob");
            Assert.AreEqual(1, blocked.MobCount);
            Assert.AreEqual(noTop.Gunner.MaxHp, blocked.Mobs[0].Hp, 1e-4f,
                "the mob was screened, not merely left alive");
        }

        [Test]
        public void AboveTheTop_TheRoundClearsAWallSegmentToo()
        {
            // Same rule over the OTHER interior shape: a wall is a stadium
            // (segment inflated by HalfWidth), resolved by SegmentStadium rather
            // than SegmentCircle, and it shares the one BarrierTop.
            const float wallX = 5f, halfWidth = 0.8f, top = 2f, flightHeight = 3f;
            const int ticks = 12;

            SimulationWorld Shoot(float barrierTop, out SimConfig cfg)
            {
                cfg = Field(barrierTop);
                PutWall(ref cfg, new float2(wallX, -4f), new float2(wallX, 4f), halfWidth);
                SimulationWorld world = Fire(in cfg, float2.zero, flightHeight, velZ: 0f);
                Tick(world, ticks);
                return world;
            }

            SimulationWorld cleared = Shoot(top, out SimConfig c);
            float farSide = wallX + halfWidth + c.Weapon.ProjectileRadius;
            Assert.Greater(flightHeight, CrownReach(in c),
                "fixture premise: the flight clears the crown");
            Assert.Greater(ticks * c.Weapon.ProjectileSpeed * SimulationWorld.TickDt, farSide,
                "fixture premise: the window is long enough for the round to be PAST the wall");
            Assert.Less(ticks * SimulationWorld.TickDt, c.Weapon.ProjectileLifetime,
                "fixture premise: and short enough that it has not expired");

            Assert.AreEqual(0, TestEvents.CountOf(cleared, SimEventKind.ProjectileBlocked));
            Assert.AreEqual(1, cleared.ProjectileCount, "the round is still in flight");
            Assert.Greater(cleared.GetProjectileForTest(0).Pos.x, farSide,
                "and it is past the wall, not stalled in front of it");

            // Control — no modelled top: the same round dies on the wall's own
            // padded face.
            SimulationWorld blocked = Shoot(0f, out SimConfig noTop);
            Assert.AreEqual(0, blocked.ProjectileCount);
            Assert.AreEqual(1, TestEvents.CountOf(blocked, SimEventKind.ProjectileBlocked));
            Assert.IsTrue(TestEvents.TryFirstOf(blocked, SimEventKind.ProjectileBlocked,
                out SimEvent end));
            Assert.AreEqual(wallX - halfWidth - noTop.Weapon.ProjectileRadius, end.Pos.x, 1e-2f);
        }

        [Test]
        public void BelowTheTop_TheBarrierStillStopsTheRound()
        {
            // The other side of the same gate: a modelled top changes nothing
            // for a shot on the line the game is actually played on.
            SimConfig c = Field(3f);
            const float obstacleX = 6f, obstacleRadius = 1.5f;
            PutObstacle(ref c, new float2(obstacleX, 0f), obstacleRadius);
            float flightHeight = c.Hero.MuzzleHeight;
            Assert.Less(flightHeight, CrownReach(in c),
                "fixture premise: a horizontal shot from the hero's own muzzle is under the crown");

            SimulationWorld w = Fire(in c, float2.zero, flightHeight, velZ: 0f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked));
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked, out SimEvent end));
            Assert.AreEqual(flightHeight, end.Amount, 1e-4f);
            Assert.AreEqual(obstacleX - obstacleRadius - c.Weapon.ProjectileRadius, end.Pos.x, 1e-2f);
        }

        [Test]
        public void TheRingBoundaryHasNoTop_AndStopsARoundThatClearedAnObstacle()
        {
            // Owner decision 2026-08-11: the outer boundary is the edge of the
            // world. This is also the split's own reason for existing — one
            // candidate standing for both would have been thrown away together
            // with the obstacle the round legitimately cleared.
            SimConfig c = Field(3f);
            // Ф2 review B-m6: shrink the boundaries with the world (see
            // TestConfigs.ShrinkArena) — 20 m is inside the round's own reach,
            // unlike the shipped 113.
            TestConfigs.ShrinkArena(ref c, 20f);
            const float obstacleX = 10f, obstacleRadius = 2f, flightHeight = 12f;
            PutObstacle(ref c, new float2(obstacleX, 0f), obstacleRadius);
            Assert.Greater(flightHeight, CrownReach(in c),
                "fixture premise: the flight clears the obstacle's crown");
            Assert.Greater(c.Weapon.ProjectileSpeed * c.Weapon.ProjectileLifetime, c.Arena.Radius,
                "fixture premise: the round can actually reach the rim before expiring");

            SimulationWorld w = Fire(in c, float2.zero, flightHeight, velZ: 0f);
            TestWorlds.RunUntilProjectilesDie(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked));
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked, out SimEvent end));
            Assert.Greater(end.Pos.x, obstacleX,
                "the obstacle was cleared — the ending is past it, not on it");
            Assert.AreEqual(c.Arena.Radius - c.Weapon.ProjectileRadius, math.length(end.Pos), 1e-2f,
                "the round died on the ring boundary's own padded rim");
            Assert.AreEqual(flightHeight, end.Amount, 1e-4f,
                "at a height far above every interior barrier");
            Assert.AreEqual(-1f, end.HitDir.x, 1e-3f,
                "and the event carries the ring's inward normal, exactly as before the split");
            Assert.AreEqual(0f, end.HitDir.y, 1e-3f);
        }

        [Test]
        public void DescendingRound_EnteringAboveTheCrown_DoesNotTunnelThrough()
        {
            // Why the gate spans the WHOLE remaining step instead of judging the
            // contact point alone: this round is above the crown where it meets
            // the barrier and below it by the end of the same tick. Judged at
            // the contact it would be handed back, fly on through the barrier's
            // body, and start the next tick already behind it.
            SimConfig c = Field(3f);
            const float obstacleX = 5f, obstacleRadius = 1.5f, entryHeight = 3.5f;
            PutObstacle(ref c, new float2(obstacleX, 0f), obstacleRadius);
            float2 start = new float2(obstacleX - 0.5f, 0f);
            // One meter of descent per tick: the whole step is expressed inside
            // the very tick the contact happens on.
            float velZ = -1f / SimulationWorld.TickDt;
            float stepEndHeight = entryHeight + velZ * SimulationWorld.TickDt;

            Assert.Less(math.abs(start.x - obstacleX), obstacleRadius + c.Weapon.ProjectileRadius,
                "fixture premise: the round starts inside the obstacle's padded circle (t = 0)");
            Assert.Greater(entryHeight, CrownReach(in c),
                "fixture premise: at the contact the round IS above the crown");
            Assert.Less(stepEndHeight, CrownReach(in c),
                "fixture premise: by the end of the step it is not");

            SimulationWorld w = Fire(in c, start, entryHeight, velZ);
            Tick(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked),
                "a round that ends the step inside the barrier's column is stopped by it");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked, out SimEvent end));
            Assert.AreEqual(entryHeight, end.Amount, 1e-4f,
                "and the ending is reported at the contact height, above the crown — which is "
                + "exactly the shot the conservative bias of this gate is paid for");
        }

        [Test]
        public void LevelRound_AboveTheCrown_PassesOver()
        {
            // The twin of the descending case above, and the discriminator that
            // keeps that one from being satisfied by "always block": same entry
            // height, no descent, so the whole step stays clear.
            SimConfig c = Field(3f);
            const float obstacleX = 5f, obstacleRadius = 1.5f, entryHeight = 3.5f;
            PutObstacle(ref c, new float2(obstacleX, 0f), obstacleRadius);
            float2 start = new float2(obstacleX - 0.5f, 0f);
            Assert.Greater(entryHeight, CrownReach(in c), "fixture premise: clear of the crown");

            SimulationWorld w = Fire(in c, start, entryHeight, velZ: 0f);
            Tick(w);

            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked));
            Assert.AreEqual(1, w.ProjectileCount, "the round flew on");
            Assert.Greater(w.GetProjectileForTest(0).Pos.x, start.x,
                "and it moved, rather than being frozen where it started");
        }

        [Test]
        public void ClimbingRound_EnteringBelowTheCrown_IsStillBlocked()
        {
            // The mirror hole: judging the step's END alone would let a round
            // that meets the barrier INSIDE its column be handed back merely
            // because it would have been clear a fraction of a tick later.
            SimConfig c = Field(3f);
            const float obstacleX = 5f, obstacleRadius = 1.5f, entryHeight = 2f;
            PutObstacle(ref c, new float2(obstacleX, 0f), obstacleRadius);
            float2 start = new float2(obstacleX - 0.5f, 0f);
            float velZ = 1.5f / SimulationWorld.TickDt;
            float stepEndHeight = entryHeight + velZ * SimulationWorld.TickDt;

            Assert.Less(entryHeight, CrownReach(in c),
                "fixture premise: at the contact the round is inside the column");
            Assert.Greater(stepEndHeight, CrownReach(in c),
                "fixture premise: by the end of the step it is above it");

            SimulationWorld w = Fire(in c, start, entryHeight, velZ);
            Tick(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked),
                "the barrier stops a round it genuinely contains at the contact");
            Assert.AreEqual(0, w.ProjectileCount);
        }

        /// The pair below pins the gate's THIRD argument, the round's own
        /// radius (fix-round 1, Ф-1). Every other fixture in this file clears
        /// or misses the crown by whole meters, so substituting `0f` for
        /// `proj.Radius` there changes no verdict at all: the suite stays green
        /// while a band `ProjectileRadius` tall opens over the crown of every
        /// interior barrier and rounds start passing through it. These two sit
        /// on either lip of that band, stated as fixture expressions.
        [Test]
        public void GrazingTheCrown_IsStoppedByTheRoundsOwnRadius()
        {
            SimConfig c = Field(3f);
            const float obstacleX = 5f, obstacleRadius = 1.5f;
            PutObstacle(ref c, new float2(obstacleX, 0f), obstacleRadius);
            var start = new float2(obstacleX - 0.5f, 0f);
            // Over the barrier's own top, under the column Overlaps grows by the
            // round's radius — the only band where that argument is what decides.
            float grazing = c.Arena.BarrierTop + 0.5f * c.Weapon.ProjectileRadius;

            Assert.Less(math.abs(start.x - obstacleX), obstacleRadius + c.Weapon.ProjectileRadius,
                "fixture premise: the round starts inside the obstacle's padded circle (t = 0)");
            Assert.Greater(grazing, c.Arena.BarrierTop,
                "fixture premise: the flight is ABOVE the modelled top itself");
            Assert.Less(grazing, CrownReach(in c),
                "fixture premise: and still inside the column the round's own radius grows "
                + "that top into");

            SimulationWorld w = Fire(in c, start, grazing, velZ: 0f);
            Tick(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked),
                "a round grazing the crown is stopped — the gate pads the column by the "
                + "round's own radius, which is this task's deliberate bias toward 'the "
                + "barrier stopped it'");
            Assert.AreEqual(0, w.ProjectileCount);
        }

        [Test]
        public void JustClearOfTheGrownColumn_TheRoundPassesOver()
        {
            // The far lip of the same band: a millimetre past the grown column
            // and the barrier lets go. Paired with the test above, this pins
            // where the padding ENDS as well as that it exists — a pad widened
            // to twice the radius fails here, a pad removed fails there.
            SimConfig c = Field(3f);
            const float obstacleX = 5f, obstacleRadius = 1.5f;
            PutObstacle(ref c, new float2(obstacleX, 0f), obstacleRadius);
            var start = new float2(obstacleX - 0.5f, 0f);
            float clear = CrownReach(in c) + 1e-3f;

            Assert.Less(math.abs(start.x - obstacleX), obstacleRadius + c.Weapon.ProjectileRadius,
                "fixture premise: the round starts inside the obstacle's padded circle (t = 0)");
            Assert.Greater(clear, CrownReach(in c),
                "fixture premise: the flight is past the grown column");
            Assert.Less(clear - c.Arena.BarrierTop, 2f * c.Weapon.ProjectileRadius,
                "fixture premise: and past it by less than a second radius' worth, so a pad "
                + "twice as generous would still catch this round");

            SimulationWorld w = Fire(in c, start, clear, velZ: 0f);
            Tick(w);

            Assert.AreEqual(0, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked));
            Assert.AreEqual(1, w.ProjectileCount, "the round flew on");
            Assert.Greater(w.GetProjectileForTest(0).Pos.x, start.x,
                "and it moved, rather than being frozen where it started");
        }

        [Test]
        public void InteriorBarrierTakesAnExactTieAgainstTheRingBoundary()
        {
            // The packing order the split had to preserve: SweepArena consulted
            // the interior circles and walls first and let the ring win only on
            // a strict `<`, so the interior barrier keeps taking an exact tie
            // now that the two are separate candidates. Both are accepted here
            // (no modelled top), so the winner is visible only through the
            // normal the ending carries.
            SimConfig c = Field(0f);
            TestConfigs.ShrinkArena(ref c, 20f);
            // Start exactly on the boundary's own padded rim, moving outward:
            // SegmentRingWall then solves its crossing at t = 0. The expression
            // is the same one the solver uses, so the two agree bit for bit.
            float limit = c.Arena.Radius - c.Weapon.ProjectileRadius;
            var start = new float2(limit, 0f);
            const float obstacleBack = 0.9f, obstacleRadius = 1f;

            // Control first: with nothing else there, the ring really does own
            // this contact, at t = 0 and with its own inward normal. Without
            // this the test below could pass on a fixture where the ring is not
            // a candidate at all, which would pin nothing.
            SimulationWorld ringOnly = Fire(in c, start, c.Hero.MuzzleHeight, velZ: 0f);
            Tick(ringOnly);
            Assert.IsTrue(TestEvents.TryFirstOf(ringOnly, SimEventKind.ProjectileBlocked,
                out SimEvent ringEnd), "fixture premise: the ring boundary is a candidate at t = 0");
            Assert.AreEqual(start.x, ringEnd.Pos.x, 1e-4f,
                "fixture premise: and it resolves at t = 0, so the tie below is exact");
            Assert.AreEqual(-1f, ringEnd.HitDir.x, 1e-3f, "the ring's normal points inward");

            // Now the tie: an obstacle whose padded circle already contains the
            // start point answers t = 0 as well.
            SimConfig tied = c;
            PutObstacle(ref tied, new float2(limit - obstacleBack, 0f), obstacleRadius);
            Assert.Less(obstacleBack, obstacleRadius + tied.Weapon.ProjectileRadius,
                "fixture premise: the round starts inside the obstacle's padded circle (t = 0)");

            SimulationWorld both = Fire(in tied, start, tied.Hero.MuzzleHeight, velZ: 0f);
            Tick(both);
            Assert.IsTrue(TestEvents.TryFirstOf(both, SimEventKind.ProjectileBlocked,
                out SimEvent tiedEnd));
            Assert.AreEqual(1f, tiedEnd.HitDir.x, 1e-3f,
                "the interior barrier is packed first, so it takes the exact tie — the ending "
                + "carries the obstacle's outward normal, not the ring's inward one");
        }

        [Test]
        public void RejectedInteriorBarrier_LeavesTheRingBoundaryStanding()
        {
            // The failure the split exists to prevent, stated on its own: one
            // candidate covering both would be handed back as a unit, and the
            // round would leave the arena through a boundary that has no top.
            // Same fixture as the tie above, with a real height added.
            SimConfig c = Field(3f);
            TestConfigs.ShrinkArena(ref c, 20f);
            float limit = c.Arena.Radius - c.Weapon.ProjectileRadius;
            var start = new float2(limit, 0f);
            PutObstacle(ref c, new float2(limit - 0.9f, 0f), 1f);
            float flightHeight = CrownReach(in c) + 1f;

            SimulationWorld w = Fire(in c, start, flightHeight, velZ: 0f);
            Tick(w);

            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.ProjectileBlocked),
                "the obstacle is cleared, the boundary is not");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileBlocked, out SimEvent end));
            Assert.AreEqual(-1f, end.HitDir.x, 1e-3f,
                "and the ending is the RING's, which is what proves the two are separate slots");
            Assert.AreEqual(flightHeight, end.Amount, 1e-4f);
        }

        [Test]
        public void AboveTheTop_TheRoundClearsAZoneWallArcToo()
        {
            // Ф2 fix-round (review A-3): the arc joins the obstacle and the wall
            // under the ONE BarrierTop (spec §3.2 — "одно правило на все
            // внутренние барьеры", no height of its own). The pair is stated
            // exactly like AboveTheTop_TheRoundClearsAWallSegmentToo above:
            // over the top the round survives the crossing, at no modelled top
            // the same geometry stops it.
            const float ringR = 8f, halfWidth = 1f, top = 2f, flightHeight = 3f;
            const int ticks = 12;

            SimulationWorld Shoot(float barrierTop, out SimConfig cfg)
            {
                cfg = Field(barrierTop);
                // Door on the far side (PI), so the round crossing along +X
                // meets solid ring body, not the cutout.
                PutArc(ref cfg, ringR, halfWidth, math.PI, 4f);
                SimulationWorld world = Fire(in cfg, float2.zero, flightHeight, velZ: 0f);
                Tick(world, ticks);
                return world;
            }

            SimulationWorld cleared = Shoot(top, out SimConfig c);
            Assert.Greater(flightHeight, CrownReach(in c),
                "fixture premise: the round must fly clear of the crown, or it is not the "
                + "height gate being measured");
            Assert.AreEqual(0, TestEvents.CountOf(cleared, SimEventKind.ProjectileBlocked),
                "a round above BarrierTop crosses the ring's body");
            Assert.Greater(cleared.Projectiles[0].Pos.x, ringR + halfWidth + c.Weapon.ProjectileRadius,
                "and it really is past the far face, not stopped short of it");

            SimulationWorld blocked = Shoot(0f, out SimConfig noTop);
            Assert.AreEqual(1, TestEvents.CountOf(blocked, SimEventKind.ProjectileBlocked),
                "control: with no modelled top the same ring stops the same round");
        }

        [Test]
        public void AboveTheTop_TheRoundClearsADoorJambToo()
        {
            // The jamb is a CIRCLE of halfWidth at the cutout's corner (Р246),
            // resolved by SegmentCircle inside SegmentArc rather than by the
            // band arithmetic — a different code path from the test above, and
            // one a height gate could easily be applied to only halfway. The
            // round is aimed along +X straight into a jamb: the door is centred
            // so that its corner sits on the +X axis.
            const float ringR = 8f, halfWidth = 1f, doorFreeWidth = 4f;
            const float top = 2f, flightHeight = 3f;
            const int ticks = 12;
            float halfCutout = Geometry.DoorHalfCutout(doorFreeWidth, ringR, halfWidth);

            SimulationWorld Shoot(float barrierTop, out SimConfig cfg)
            {
                cfg = Field(barrierTop);
                // Cutout centred one half-cutout AWAY from +X, so +X lands on
                // the jamb circle rather than in the free passage.
                PutArc(ref cfg, ringR, halfWidth, halfCutout, doorFreeWidth);
                SimulationWorld world = Fire(in cfg, float2.zero, flightHeight, velZ: 0f);
                Tick(world, ticks);
                return world;
            }

            SimulationWorld blocked = Shoot(0f, out SimConfig noTop);
            Assert.AreEqual(1, TestEvents.CountOf(blocked, SimEventKind.ProjectileBlocked),
                "fixture premise: with no modelled top the jamb must stop this round — otherwise "
                + "the shot misses the jamb and the pair below proves nothing");

            SimulationWorld cleared = Shoot(top, out SimConfig c);
            Assert.Greater(flightHeight, CrownReach(in c),
                "fixture premise: the round flies clear of the crown");
            Assert.AreEqual(0, TestEvents.CountOf(cleared, SimEventKind.ProjectileBlocked),
                "a round above BarrierTop crosses the jamb as well as the ring's body");
        }

    }
}
