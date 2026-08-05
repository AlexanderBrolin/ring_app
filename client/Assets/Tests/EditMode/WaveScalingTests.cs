using NUnit.Framework;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 16 (spec §3.4/§4): the wave director under three players —
    /// the per-player size scale and its cap, the "nearest ALIVE player"
    /// spawn-distance rule, and the frozen-director contract when nobody is
    /// alive. Every expectation is either an explicit in-test fixture (the
    /// numbers are stated right here, convention app-n6g C14) or a fixture
    /// expression over TestConfigs — never a literal lifted out of a .asset.
    public class WaveScalingTests
    {
        const float Eps = 1e-4f;

        /// The one number spec §3.4 states end to end: BaseCount 4, three
        /// players, PerPlayerCountFrac 0.7 => 4 x 2.4 = 9.6 => round = 10.
        /// Stated as an explicit fixture so this is a real expectation rather
        /// than the production formula copied into the assert.
        [Test]
        public void Count_ThreePlayers_ScalesAndRoundsToTen()
        {
            var cfg = new WaveSimConfig
            {
                BaseCount = 4,
                CountGrowth = 2,
                MaxMobsPerWave = 36,
                PerPlayerCountFrac = 0.7f
            };

            Assert.AreEqual(10, WaveSystem.CountForTest(in cfg, 0, 3),
                "wave 0 with three players must be round(4 * (1 + 2 * 0.7)) = round(9.6) = 10");
        }

        [Test]
        public void Count_SoloIsUnscaled_RegardlessOfFrac()
        {
            var cfg = new WaveSimConfig
            {
                BaseCount = 4,
                CountGrowth = 2,
                MaxMobsPerWave = 36,
                PerPlayerCountFrac = 0.7f
            };

            // playerCount 1 => (1 + 0 * frac) == 1: Stage 1 solo sizes stay put.
            Assert.AreEqual(cfg.BaseCount, WaveSystem.CountForTest(in cfg, 0, 1));
            Assert.AreEqual(cfg.BaseCount + cfg.CountGrowth,
                WaveSystem.CountForTest(in cfg, 1, 1));
        }

        [Test]
        public void Count_WaveIndexIsZeroBased_AndGrowthScalesToo()
        {
            var cfg = new WaveSimConfig
            {
                BaseCount = 4,
                CountGrowth = 2,
                MaxMobsPerWave = 36,
                PerPlayerCountFrac = 0.5f
            };

            // wave 0 is the FIRST wave: 4 * (1 + 2 * 0.5) = 8.
            Assert.AreEqual(8, WaveSystem.CountForTest(in cfg, 0, 3));
            // wave 1 adds one CountGrowth step BEFORE the scale: (4 + 2) * 2 = 12.
            Assert.AreEqual(12, WaveSystem.CountForTest(in cfg, 1, 3));
        }

        [Test]
        public void Count_CappedAtMaxMobsPerWave()
        {
            var cfg = new WaveSimConfig
            {
                BaseCount = 20,
                CountGrowth = 2,
                MaxMobsPerWave = 36,
                PerPlayerCountFrac = 0.7f
            };

            // Unclamped this would be round(20 * 2.4) = 48 — the cap has to bite
            // AFTER the player scale, not before it.
            Assert.AreEqual(cfg.MaxMobsPerWave, WaveSystem.CountForTest(in cfg, 0, 3));
        }

        [Test]
        public void MinSpawnDistance_MeasuredToNearestAlivePlayer_DeadPlayersIgnored()
        {
            // Two players on opposite sides of the arena, one alive and one
            // dead. MinSpawnDistanceToPlayer must keep every spawn away from the
            // ALIVE one while the dead one constrains nothing at all — the
            // asymmetry is the whole assertion.
            var c = TestConfigs.Default();
            c.Wave.FirstWaveDelay = 0.1f;
            c.Wave.MaxSpawnAttempts = 0; // fixed FallbackSlots grid only — no RNG luck
            float ringRadius = c.Arena.Radius - c.Wave.SpawnRingInset;
            // Half the spawn ring's radius: comfortably larger than the gap
            // between a player parked on the ring and the ring itself, so the
            // rule visibly bites near the alive player.
            c.Wave.MinSpawnDistanceToPlayer = ringRadius * 0.5f;

            var w = new SimulationWorld(11, c, 2);

            // The fallback grid is walked from slot 0 (angle 0) upward and the
            // first valid slot wins, so the DEAD player is parked on slot 0 and
            // the alive one on the opposite side: the very first mob placed
            // proves a corpse reserves nothing, while the far half of the ring
            // stays off limits because of the alive player.
            float2 alivePos = new float2(-ringRadius, 0f);
            float2 deadPos = new float2(ringRadius, 0f);

            PlayerState p0 = w.PlayerAt(0);
            p0.Pos = alivePos;
            p0.Alive = true;
            w.SetPlayerForTest(0, p0);

            PlayerState p1 = w.PlayerAt(1);
            p1.Pos = deadPos;
            p1.Alive = false;
            p1.Hp = 0f;
            w.SetPlayerForTest(1, p1);

            var snap = new RenderSnapshot(c.Arena);
            var idle = new SimInput[w.PlayerCount];
            for (int i = 0; i < 200 && w.MobCount == 0; i++) w.TickAll(idle);
            w.CaptureSnapshot(snap);
            Assert.Greater(snap.MobCount, 0, "the wave never spawned at all");

            bool anyNearDead = false;
            for (int m = 0; m < snap.MobCount; m++)
            {
                float2 pos = snap.Mobs[m].Pos;
                Assert.GreaterOrEqual(math.distance(pos, alivePos),
                    c.Wave.MinSpawnDistanceToPlayer - 1f,
                    "a mob spawned inside MinSpawnDistanceToPlayer of the ALIVE player");
                if (math.distance(pos, deadPos) < c.Wave.MinSpawnDistanceToPlayer)
                    anyNearDead = true;
            }

            Assert.IsTrue(anyNearDead,
                "a DEAD player must not reserve any space: the ring next to it has to stay usable");
        }

        [Test]
        public void NoAlivePlayers_WaveDirectorFreezes_PhaseAndTimerStandStill()
        {
            // Nobody alive => WaveSystem.Update returns before touching the
            // phase timer at all: neither the countdown nor the phase may move.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c, 2);

            for (int i = 0; i < w.PlayerCount; i++)
            {
                PlayerState p = w.PlayerAt(i);
                p.Alive = false;
                p.Hp = 0f;
                w.SetPlayerForTest(i, p);
            }

            var snap = new RenderSnapshot(c.Arena);
            w.CaptureSnapshot(snap);
            WavePhase phaseBefore = snap.Wave.Phase;
            float timerBefore = snap.Wave.PhaseTimer;
            int indexBefore = snap.Wave.WaveIndex;

            var idle = new SimInput[w.PlayerCount];
            for (int i = 0; i < 120; i++) w.TickAll(idle);

            w.CaptureSnapshot(snap);
            Assert.AreEqual(phaseBefore, snap.Wave.Phase, "phase moved with nobody alive");
            Assert.AreEqual(timerBefore, snap.Wave.PhaseTimer, Eps,
                "the wave timer kept counting down with nobody alive");
            Assert.AreEqual(indexBefore, snap.Wave.WaveIndex, "a wave started with nobody alive");
            Assert.AreEqual(0, snap.MobCount);
        }
    }
}
