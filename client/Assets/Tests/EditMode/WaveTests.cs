using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WaveTests
    {
        [Test]
        public void FirstWave_SpawnsAfterDelay_WithBaseCount()
        {
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(c.Wave.BaseCount, snap.MobCount);
            Assert.AreEqual(1, snap.Wave.WaveIndex);
        }

        [Test]
        public void SpawnPositions_RespectRules()
        {
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c);
            // snapshot taken right after the first wave spawns — mobs haven't had a
            // chance to move yet
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.Greater(snap.MobCount, 0);
            for (int m = 0; m < snap.MobCount; m++)
            {
                float2 pos = snap.Mobs[m].Pos;
                Assert.Greater(math.distance(pos, w.Player.Pos),
                    c.Wave.MinSpawnDistanceToPlayer - 1f); // minus up to 2 ticks of movement
                for (int o = 0; o < c.Arena.ObstacleCount; o++)
                    Assert.IsFalse(Geometry.CircleOverlap(pos, 0.4f,
                        c.Arena.ObstaclePos[o], c.Arena.ObstacleRadius[o]));
            }
        }

        [Test]
        public void SameSeed_SameWaveComposition()
        {
            ulong Run(long seed)
            {
                var w = new SimulationWorld(seed, TestConfigs.Default());
                for (int i = 0; i < 400; i++) w.Tick(default);
                return w.StateHash();
            }
            Assert.AreEqual(Run(77), Run(77));
            Assert.AreNotEqual(Run(77), Run(78));
        }

        [Test]
        public void FullyBlockedRing_NoHang_DebtCarriesOver()
        {
            var c = TestConfigs.Open();
            c.Wave.FirstWaveDelay = 0.1f;
            // Stage 3 Task 12: "no valid points at all" is a claim about EVERY
            // zone's spawn ring now, not about one. The literal 100 blocked the
            // single 63 m ring of the Stage 2 arena; on the three-zone one the
            // rings are 63 / 90 / 111, and 111 > 100 left the OUTER ring legal
            // — the wave duly seated round(BaseCount 4 * ZoneWeights[Outer]
            // 0.45) = 2 mobs there, which is exactly what this assertion saw.
            // Derived from the arena so it can never fall behind a ring again.
            c.Wave.MinSpawnDistanceToPlayer = c.Arena.Radius + 10f; // no valid points on ANY ring
            var w = new SimulationWorld(11, c);
            for (int i = 0; i < 60; i++) w.Tick(default); // not hanging is already success
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(0, snap.MobCount);
            // Stage 3 Task 11: the debt is nine fields now (zone x
            // archetype) -- PendingTotal is the one computed home for
            // "how much debt is outstanding" (coordinator R-52), not a
            // hand-summed pair.
            Assert.Greater(snap.Wave.PendingTotal, 0);
            // the debt clears once conditions allow it (spec §3.13 item 5)
            var relaxed = c;
            relaxed.Wave.MinSpawnDistanceToPlayer = 8f;
            w.ApplyConfig(relaxed);
            for (int i = 0; i < 60; i++) w.Tick(default);
            w.CaptureSnapshot(snap);
            Assert.Greater(snap.MobCount, 0);
            Assert.AreEqual(0, snap.Wave.PendingTotal);
        }

        [Test]
        public void WaveComposition_FollowsGunnerShare()
        {
            var c = TestConfigs.Default();
            // Stage 3 Task 12: this test's subject is the GUNNER SHARE, which
            // is a within-zone number — so the fixture states one zone
            // outright instead of inheriting the shipped three-way split. Left
            // implicit, the wave now divides 4 mobs into Outer 2 / Middle 2,
            // the middle pair spends its EliteShareMiddle 0.35 on an Elite,
            // and round(2 * 0.2) rounds to zero gunners in both zones — the
            // arithmetic below would be measuring zone routing (WaveZoneTests'
            // subject, Т11) rather than the share it names.
            c.Wave.ZoneWeights = new[] { 1f, 0f, 0f };
            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 2;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            int gunners = 0;
            for (int m = 0; m < snap.MobCount; m++)
                if (snap.Mobs[m].Type == MobType.Gunner) gunners++;
            // wave 1: count = BaseCount = 4; gunners = round(4 x 0.2) = 1
            Assert.AreEqual(c.Wave.BaseCount, snap.MobCount);
            Assert.AreEqual(1, gunners);
        }

        [Test]
        public void MobCap_SkipsSpawnsDeterministically()
        {
            var c = TestConfigs.Default();
            c.Arena.MaxMobs = 2;
            c.Wave.BaseCount = 6;
            var w = new SimulationWorld(11, c);
            for (int i = 0; i < 200; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.LessOrEqual(snap.MobCount, 2);
            Assert.Greater(w.WorldStats.MobSpawnsSkipped, 0);
            static ulong Run(SimConfig cc)
            {
                var ww = new SimulationWorld(11, cc);
                for (int i = 0; i < 200; i++) ww.Tick(default);
                return ww.StateHash();
            }
            Assert.AreEqual(Run(c), Run(c)); // deterministic degradation under the cap
        }

        [Test]
        public void Spawn_InsideWall_Rejected()
        {
            // Stage 2 Task 14 (spec §3.3): IsValidSpawn grows a wall-overlap
            // loop mirroring the existing obstacle loop, via the same
            // Geometry.OverlapsStadium idiom the obstacle/mob loops already
            // use — no second overlap function. MaxSpawnAttempts = 0 forces
            // every candidate through the FIXED FallbackSlots grid instead
            // of WaveRng draws — fully deterministic (no seed-dependent luck
            // needed to actually land a candidate on the blocked arc below),
            // and BaseCount well past the open-slot count forces the
            // deterministic sequence to spill into it once the open slots
            // fill up.
            var c = TestConfigs.Default();
            c.Wave.MaxSpawnAttempts = 0;
            c.Wave.BaseCount = 48;
            c.Wave.MaxMobsPerWave = 48;
            float ringRadius = c.Arena.Radius - c.Wave.SpawnRingInset;
            // A wide band crossing the ring well north of the player/default
            // obstacles (all within radius ~17 of the origin) — covers
            // roughly a third of the ring's candidate angles without
            // touching anything else already in the arena.
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(-ringRadius - 5f, 25f) };
            c.Arena.WallB = new[] { new float2(ringRadius + 5f, 25f) };
            c.Arena.WallHalfWidth = new[] { 10f };

            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 10;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.Greater(snap.MobCount, 0); // the wave still found room elsewhere on the ring
            // M-7 (fix-round T14): IsValidSpawn checks each mob against its
            // OWN archetype radius (c.Chaser.Radius == c.Gunner.Radius ==
            // 0.5 in TestConfigs, not the 0.4 literal an earlier revision
            // of this assertion used) — reading it from config instead of
            // hardcoding it keeps this test honest if the archetypes' radii
            // ever diverge.
            for (int m = 0; m < snap.MobCount; m++)
                Assert.IsFalse(Geometry.OverlapsStadium(snap.Mobs[m].Pos, c.Chaser.Radius,
                    c.Arena.WallA[0], c.Arena.WallB[0], c.Arena.WallHalfWidth[0]));
        }

        [Test]
        public void Spawn_InsideWallCap_Rejected()
        {
            // Coordinator addition: Spawn_InsideWall_Rejected above only
            // exercises rejection via a wall's FLAT side. This fixture is a
            // short wall ending a few metres short of the spawn ring, so a
            // candidate at that angle can only be rejected through the
            // ROUNDED END CAP (Geometry.ClosestPointOnSegment clamps to the
            // endpoint out there, not the flat band) — proving IsValidSpawn's
            // wall loop catches that case too, not only the straight side.
            // MaxSpawnAttempts = 0 (see Spawn_InsideWall_Rejected above) makes
            // slot 0 — angle zero, squarely on this wall's cap — the very
            // FIRST candidate the very first pending mob ever tries, with no
            // RNG involved at all.
            var c = TestConfigs.Default();
            c.Wave.MaxSpawnAttempts = 0;
            c.Wave.BaseCount = 48;
            c.Wave.MaxMobsPerWave = 48;
            float ringRadius = c.Arena.Radius - c.Wave.SpawnRingInset;
            c.Arena.WallCount = 1;
            c.Arena.WallA = new[] { new float2(ringRadius - 21f, 0f) };
            c.Arena.WallB = new[] { new float2(ringRadius - 2f, 0f) }; // ends short of the ring
            c.Arena.WallHalfWidth = new[] { 8f }; // the cap alone still reaches the ring near angle 0

            var w = new SimulationWorld(11, c);
            int delayTicks = (int)math.ceil(c.Wave.FirstWaveDelay / SimulationWorld.TickDt) + 10;
            for (int i = 0; i < delayTicks; i++) w.Tick(default);
            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            Assert.Greater(snap.MobCount, 0);
            // M-7 (fix-round T14): same fix as Spawn_InsideWall_Rejected
            // above — the archetype radius comes from config, not a 0.4
            // literal.
            for (int m = 0; m < snap.MobCount; m++)
                Assert.IsFalse(Geometry.OverlapsStadium(snap.Mobs[m].Pos, c.Chaser.Radius,
                    c.Arena.WallA[0], c.Arena.WallB[0], c.Arena.WallHalfWidth[0]));
        }
    }
}
