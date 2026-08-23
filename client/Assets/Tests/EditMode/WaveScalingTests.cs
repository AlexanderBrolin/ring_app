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
            // Ask for more mobs than the grid has slots, so the search is forced
            // to try EVERY slot, including the ones next to the alive player.
            // Without this the wave is small enough to be seated entirely on the
            // far side and the distance rule is never exercised at all —
            // confirmed by mutation (deleting the rule left an earlier revision
            // of this test green).
            c.Wave.BaseCount = c.Wave.FallbackSlots;
            c.Wave.MaxMobsPerWave = c.Wave.FallbackSlots * 2;
            // Stage 3 Task 12: the closing assertion counts the wave against
            // ONE ring's worth of slots, so the fixture says so instead of
            // letting three rings share the grid. With three rings the grid
            // offers 3 * FallbackSlots = 72 seats, the two-player wave of
            // round(24 * 1.7) = 41 fits into them without a single refusal,
            // and the distance rule this test exists for is never exercised —
            // the assertion said exactly that ("no ring slot was refused at
            // all", 41 against 24) and was right.
            //
            // ⚠ Т4: the isolation was ZoneWeights = {1,0,0}; with the weights
            // gone a ring is isolated by making the arena ZONELESS, which
            // WaveSystem.RingIsFrozen answers by running Zone.Outer and
            // freezing the other two. Same one-ring world, through the
            // mechanism that still exists — and now the ring owes a WHOLE
            // wave of 41 against 24 slots rather than a share of one.
            c.Arena.ZoneRadius = System.Array.Empty<float>();
            // ⚠ Т5: THIS TEST SWITCHES THE CADENCE'S TWO LIMITS OFF FOR ITSELF,
            // and that is what keeps it a test of the PLACEMENT FILTER rather
            // than of the cadence. Both would otherwise answer the closing
            // assertion for it, silently:
            //  - the per-tick budget (2) means the loop below, which stops at
            //    the FIRST mob, would examine two mobs instead of a whole wave,
            //    and "fewer seated than there are slots" would be trivially
            //    true of any implementation at all — a vacuum, not a witness;
            //  - the ring's ceiling in TestConfigs is 24, exactly FallbackSlots,
            //    so a wave stopped by the CEILING would read as a wave stopped
            //    by the distance rule.
            // With the whole wave attempted in one tick, the mobs are also read
            // where they were SEATED rather than wherever they have walked to,
            // which is what the two distance assertions below are about.
            c.Wave.MaxSpawnsPerZonePerTick = c.Wave.MaxMobsPerWave;
            c.Wave.MaxAliveByZone = new[] { c.Wave.MaxMobsPerWave, 16, 8 };
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

            var snap = new RenderSnapshot(c);
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
            // The wave asked for every slot on the grid, so some of them MUST
            // have been refused — otherwise the assertion above never had a
            // chance to bite.
            Assert.Less(snap.MobCount, c.Wave.FallbackSlots,
                "no ring slot was refused at all — the distance rule was never exercised");
        }

        [Test]
        public void SpawnCandidateInsideArc_IsRejected()
        {
            // Stage 3 Task 9: WaveSystem.IsValidSpawn grows an arc-overlap
            // rejection (Geometry.OverlapsArc), the same "reuse the existing
            // overlap primitive" idiom as the obstacle/wall checks above it
            // (IsValidSpawn's own doc). A zone wall's body placed squarely ON
            // the spawn ring, with NO doors, must reject every fallback-grid
            // candidate — none of the RNG-free grid slots can land outside
            // the wall's body when the whole ring sits inside its band.
            var c = TestConfigs.Default();
            c.Wave.FirstWaveDelay = 0.1f;
            c.Wave.MaxSpawnAttempts = 0; // fixed FallbackSlots grid only — no RNG luck
            //
            // ⚠ PREMISE REPAIRED IN Ф2's FIX-ROUND (review B-I3) — the same
            // class of defect Т12 found in TrioSaturated and failed to sweep
            // for here. The inset is chosen against Arena.Radius so the OUTER
            // ring lands at 20, dead center of the band below; but Т12 turned
            // the zones on, and the same inset put the Middle ring at
            // 92 - 93 = -1 and the Core ring at 65 - 93 = -28. The budget then
            // split across the rings, and the Middle candidates were refused
            // by MinSpawnDistanceToPlayer rather than by any arc: green by a
            // foreign rule for two zones out of three, with this method's own
            // sentence ("the whole ring sits inside its band") false for both.
            // Leaving only the Outer ring running makes the sentence true
            // again; the premise assertions below make it checkable rather
            // than narrated.
            //
            // ⚠ Т4: that used to be ZoneWeights = {1,0,0}; the ring is now
            // left alone by making the arena ZONELESS (WaveSystem.RingIsFrozen
            // runs Outer and freezes the other two). The ZONE WALL fixture
            // below is untouched by that — a wall is arena geometry, not a
            // zone boundary — which is exactly why the two can be set
            // independently here.
            c.Arena.ZoneRadius = System.Array.Empty<float>();
            c.Wave.SpawnRingInset = c.Arena.Radius - 20f; // OUTER spawn ring lands at radius 20
            c.Arena.ZoneWallCount = 1;
            c.Arena.ZoneWallRadius = new[] { 20f };
            c.Arena.ZoneWallHalfWidth = new[] { 5f }; // band covers [15,25] — the spawn ring sits dead center
            c.Arena.ZoneWallDoorStart = new[] { 0 };
            c.Arena.ZoneWallDoorCount = new[] { 0 };
            c.Arena.DoorCenterRad = System.Array.Empty<float>();
            c.Arena.DoorFreeWidth = System.Array.Empty<float>();

            var w = new SimulationWorld(11, c);
            for (int i = 0; i < 200; i++) w.Tick(default);

            var snap = new RenderSnapshot(c);
            w.CaptureSnapshot(snap);
            // Wave-cadence-per-zone (bd app-ggvz Т3): the frame carries the
            // world AGGREGATE of the three per-ring WaveState instances, and
            // this fixture leaves only Outer running -- so the aggregate IS
            // the outer debt here.
            int outerDebt = snap.Wave.PendingChaser + snap.Wave.PendingGunner
                + snap.Wave.PendingElite;
            Assert.Greater(outerDebt, 0,
                "fixture premise: the wave must actually owe mobs to the Outer zone, or the arc "
                + "rejection below is never even attempted");
            // ⚠ WAS A TAUTOLOGY until the review of this task: it compared
            // snap.Wave.PendingTotal against the sum of the same three fields
            // of the same snapshot, and PendingTotal is the computed property
            // of exactly that sum — true under any implementation whatsoever.
            // The claim it MEANT to make is about the OTHER two rings, so it
            // now reads them.
            Assert.AreEqual(0,
                w.WaveRef(Zone.Middle).PendingTotal + w.WaveRef(Zone.Core).PendingTotal,
                "fixture premise: the WHOLE debt belongs to Outer — the middle and core rings are "
                + "frozen on this zoneless arena and own nothing, so the aggregate above IS the "
                + "outer ring's own debt");
            Assert.AreEqual(0, w.MobCount,
                "every fallback slot sits inside the zone wall's body — none should have spawned");
        }

        [Test]
        public void NoAlivePlayers_WaveDirectorFreezes_PhaseAndTimerStandStill()
        {
            // Nobody alive => WaveSystem.Update returns before touching the
            // phase timer at all: neither the countdown nor the phase may move.
            //
            // ⚠ EXTENDED IN Т4 (app-ggvz), not duplicated: the early exit is
            // ONE return, above the whole per-ring loop, so it either freezes
            // ALL THREE rings or none — and a mutant that moved the exit
            // INSIDE the loop, or applied it to the ring it happens to reach
            // first, would leave the world aggregate below looking untouched
            // while the middle and core rings ticked on. The aggregate is
            // therefore no longer trusted alone: every ring is read by name.
            var c = TestConfigs.Default();
            var w = new SimulationWorld(11, c, 2);
            var before = new WaveState[Zones.Count];
            for (int z = 0; z < Zones.Count; z++) before[z] = w.WaveRef((Zone)z);

            for (int i = 0; i < w.PlayerCount; i++)
            {
                PlayerState p = w.PlayerAt(i);
                p.Alive = false;
                p.Hp = 0f;
                w.SetPlayerForTest(i, p);
            }

            var snap = new RenderSnapshot(c);
            w.CaptureSnapshot(snap);
            WavePhase phaseBefore = snap.Wave.Phase;
            int ticksBefore = snap.Wave.PhaseTicks;
            int indexBefore = snap.Wave.WaveIndex;

            var idle = new SimInput[w.PlayerCount];
            for (int i = 0; i < 120; i++) w.TickAll(idle);

            w.CaptureSnapshot(snap);
            Assert.AreEqual(phaseBefore, snap.Wave.Phase, "phase moved with nobody alive");
            Assert.AreEqual(ticksBefore, snap.Wave.PhaseTicks,
                "the wave timer kept counting down with nobody alive");
            Assert.AreEqual(indexBefore, snap.Wave.WaveIndex, "a wave started with nobody alive");
            Assert.AreEqual(0, snap.MobCount);

            for (int z = 0; z < Zones.Count; z++)
            {
                WaveState now = w.WaveRef((Zone)z);
                CollectionAssert.AreEqual(
                    new[] { (int)before[z].Phase, before[z].PhaseTicks, before[z].WaveIndex,
                        before[z].PendingTotal },
                    new[] { (int)now.Phase, now.PhaseTicks, now.WaveIndex, now.PendingTotal },
                    $"ring {(Zone)z} moved with nobody alive — the early exit is one return "
                    + "above the ring loop and must freeze every ring, not the first one");
            }
        }
    }
}
