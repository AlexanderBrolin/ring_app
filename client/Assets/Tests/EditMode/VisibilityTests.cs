using NUnit.Framework;
using Ring.Simulation.AI;
using Ring.Simulation.Core;
using Ring.Simulation.Visibility;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 19 (spec §3.5, Р18-Р21): server-side visibility filter core.
    /// Every fixture below builds its own arena via TestConfigs.Open() plus an
    /// explicit obstacle/wall layout (same discipline as WallGeometryTests/
    /// MobAiTests' LineOfFire_* fixtures) so the visibility numbers under test
    /// (cfg.Visibility.*) are never entangled with DefaultArena()'s own
    /// obstacle/wall geometry.
    public class VisibilityTests
    {
        static int Capacity(in SimConfig cfg) => cfg.Arena.MaxMobs + cfg.Arena.MaxPlayers;

        // --- 1: BeyondSightRadius_NotVisible ---

        [Test]
        public void BeyondSightRadius_NotVisible()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            // Open arena: no obstacles/walls, so LoS is never the reason this
            // mob is hidden — only the plain distance gate is exercised.
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(cfg.Visibility.SightRadius + 1f, 0f));

            var previous = new VisibilitySet(Capacity(cfg));
            var result = new VisibilitySet(Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsFalse(result.Contains(mobId));
        }

        // --- 2: BehindObstacle_NotVisible ---

        [Test]
        public void BehindObstacle_NotVisible()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            // Dead-centre on the ray, radius well past the mob's own (a
            // Chaser radius of 0.5), so this stays blocked even after the
            // conservative -targetRadius pad — unlike EdgePeek below, this is
            // NOT an edge case.
            cfg.Arena.ObstaclePos = new[] { new float2(5f, 0f) };
            cfg.Arena.ObstacleRadius = new[] { 2f };

            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f)); // dead ahead, well within SightRadius

            var previous = new VisibilitySet(Capacity(cfg));
            var result = new VisibilitySet(Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsFalse(result.Contains(mobId));
        }

        // --- 3: BehindWall_NotVisible ---

        [Test]
        public void BehindWall_NotVisible()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.WallCount = 1;
            // Vertical wall straddling the ray's crossing point on its flat
            // side (same shape as MobAiTests.LineOfFire_BlockedByWall),
            // half-width well past the mob's own radius so this stays
            // blocked even after the conservative pad.
            cfg.Arena.WallA = new[] { new float2(5f, -5f) };
            cfg.Arena.WallB = new[] { new float2(5f, 5f) };
            cfg.Arena.WallHalfWidth = new[] { 1f };

            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f));

            var previous = new VisibilitySet(Capacity(cfg));
            var result = new VisibilitySet(Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsFalse(result.Contains(mobId));
        }

        // --- 4: EdgePeek_IsVisible_ConservativeLos ---

        [Test]
        public void EdgePeek_IsVisible_ConservativeLos()
        {
            var cfg = TestConfigs.Open();
            float mobRadius = cfg.Chaser.Radius; // 0.5 — the target's own radius the LoS gate must pad by
            cfg.Arena.ObstacleCount = 1;
            // Obstacle's perpendicular offset from the ray sits strictly
            // between (obstacleRadius - mobRadius) and obstacleRadius: a
            // strict centre-to-centre ray (padR 0) sees a blocking circle of
            // the FULL obstacleRadius and is blocked; the conservative pad
            // (-mobRadius) shrinks the blocking radius to
            // (obstacleRadius - mobRadius), and the same offset clears it —
            // exactly the "corpus peeks past the edge, only the centre is
            // hidden" case spec Р18 describes (mirrors
            // MobAiTests.LineOfFire_NegativePadClamped's own numbers, scaled
            // to a mob's actual radius instead of Hero.Radius).
            float obstacleRadius = mobRadius + 0.1f; // 0.6
            float offset = obstacleRadius - 0.05f;   // 0.55: inside 0.6 (strict-blocked), outside 0.1 (conservative-clear)
            cfg.Arena.ObstaclePos = new[] { new float2(5f, offset) };
            cfg.Arena.ObstacleRadius = new[] { obstacleRadius };

            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(10f, 0f)); // straight ahead along y=0

            // test setup: prove the STRICT (centre-to-centre) ray really is
            // blocked, so this fixture actually exercises the edge-peek case
            // instead of a trivially clear line. This is also the exact
            // mutation this test exists to catch: if VisibilitySystem ever
            // passed 0f instead of -targetRadius to HasLineOfFire, the whole
            // test would collapse onto this "blocked" branch.
            Assert.IsFalse(Targeting.HasLineOfFire(float2.zero, new float2(10f, 0f), 0f, cfg.Arena),
                "test setup: strict centre-to-centre LoS must be blocked for this to be an edge-peek case");

            var previous = new VisibilitySet(Capacity(cfg));
            var result = new VisibilitySet(Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            Assert.IsTrue(result.Contains(mobId));
            Assert.AreEqual(0, result.LingerOf(mobId));
        }

        // --- 5: Hysteresis_KeepsVisibleUntilExitRadius ---

        [Test]
        public void Hysteresis_KeepsVisibleUntilExitRadius()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(cfg.Visibility.SightRadius - 1f, 0f));

            var setA = new VisibilitySet(Capacity(cfg));
            var setB = new VisibilitySet(Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 0: clearly inside SightRadius
            Assert.IsTrue(setB.Contains(mobId), "test setup: must start visible");
            Assert.AreEqual(0, setB.LingerOf(mobId));

            // Move into the hysteresis band: beyond the plain SightRadius but
            // within SightRadius + ExitHysteresis, LoS still clear (open arena).
            float hysteresisDist = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis * 0.5f;
            Assert.Greater(hysteresisDist, cfg.Visibility.SightRadius,
                "test setup: fixture distance must sit past the plain sight radius");
            Assert.LessOrEqual(hysteresisDist, cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis,
                "test setup: fixture distance must sit within the hysteresis band");
            MobState m = w.Mobs[0];
            m.Pos = new float2(hysteresisDist, 0f);
            w.SetMobForTest(0, m);

            VisibilitySystem.Compute(w, 0, cfg.Visibility, setB, setA); // tick 1: previous/result swap
            Assert.IsTrue(setA.Contains(mobId),
                "hysteresis must keep a previously-visible entity visible past SightRadius");
            // Critical: must read as VISIBLE NOW (LingerOf 0), not merely
            // "still tracked via the linger fallback" — a mutant that drops
            // the hysteresis bonus entirely would still leave the entity
            // Contains()==true (via LingerTicks grace) but with a nonzero
            // LingerOf, which this assertion catches and Contains() alone would not.
            Assert.AreEqual(0, setA.LingerOf(mobId),
                "still WITHIN the hysteresis band counts as visible now, not lingering");
        }

        // --- 6: LingerTicks_KeepVisibleAfterLosBreak ---

        [Test]
        public void LingerTicks_KeepVisibleAfterLosBreak()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);
            int mobId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f)); // clearly visible

            var setA = new VisibilitySet(Capacity(cfg));
            var setB = new VisibilitySet(Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 0: visible
            Assert.IsTrue(setB.Contains(mobId), "test setup: must start visible");
            Assert.AreEqual(0, setB.LingerOf(mobId));

            // Move far outside even the hysteresis band: fully invisible from here on.
            float farDist = cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis + 1f;
            MobState m = w.Mobs[0];
            m.Pos = new float2(farDist, 0f);
            w.SetMobForTest(0, m);

            // The interface always passes TWO DISTINCT VisibilitySet instances
            // (previous, result) — ping-pong setA/setB below to match real
            // per-tick usage instead of reusing one buffer for both roles.
            VisibilitySet prev = setB, cur = setA;
            for (int expectedLinger = cfg.Visibility.LingerTicks; expectedLinger >= 1; expectedLinger--)
            {
                VisibilitySystem.Compute(w, 0, cfg.Visibility, prev, cur);
                Assert.IsTrue(cur.Contains(mobId), $"must still linger at counter {expectedLinger}");
                Assert.AreEqual(expectedLinger, cur.LingerOf(mobId));
                (prev, cur) = (cur, prev);
            }

            // The grace period (exactly LingerTicks ticks, spec Р19) is now
            // spent — one more invisible tick must drop the entity entirely.
            VisibilitySystem.Compute(w, 0, cfg.Visibility, prev, cur);
            Assert.IsFalse(cur.Contains(mobId), "linger must expire after exactly LingerTicks ticks");
        }

        // --- 7: SwapRemove_DoesNotTransferState ---

        [Test]
        public void SwapRemove_DoesNotTransferState()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg);

            // Fresh world: SimulationWorld's own _nextEntityId counter starts
            // at 1 and only grows, so mobA below is guaranteed id 1 — the
            // SAME integer as the slot index mobC will occupy after the
            // swap-remove below. This is deliberate: a VisibilitySystem that
            // (incorrectly) queried `previous` by loop index instead of by
            // the entity's own Id would, by this coincidence, read mobA's
            // PREVIOUS visibility entry when it means to read the id-3
            // survivor's — which has none. A correctly id-keyed
            // implementation cannot be fooled by this coincidence at all.
            int mobAId = w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f)); // slot 0, visible
            int mobBId = w.SpawnMobForTest(MobType.Chaser, new float2(2f, 0f)); // slot 1, visible — about to die
            // Placed in the exit-hysteresis dead zone (beyond plain
            // SightRadius, inside SightRadius + ExitHysteresis): only
            // visible if some stale previous-tick entry wrongly applies to
            // it. It has never been seen before under its OWN id, so the
            // correct answer both before and after the swap is "not visible".
            int mobCId = w.SpawnMobForTest(MobType.Chaser,
                new float2(cfg.Visibility.SightRadius + cfg.Visibility.ExitHysteresis * 0.5f, 0f)); // slot 2

            var setA = new VisibilitySet(Capacity(cfg));
            var setB = new VisibilitySet(Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, setA, setB); // tick 0
            Assert.IsTrue(setB.Contains(mobAId), "test setup: mobA must be visible before the swap");
            Assert.IsFalse(setB.Contains(mobCId), "test setup: mobC must not be visible before ever being seen");

            // Kill the MIDDLE mob (slot 1): SimulationWorld's swap-remove
            // (`_mobs[index] = _mobs[--_mobCount]`) moves the LAST live mob
            // — mobC — into slot 1, the slot index that coincides with
            // mobA's id.
            w.DamageMob(1, 1e9f, w.Mobs[1].Pos, HitZone.Body, float2.zero, ownerIndex: 0);
            Assert.AreEqual(2, w.MobCount, "test setup: mobB must have died and been swap-removed");
            Assert.AreEqual(mobCId, w.Mobs[1].Id, "test setup: mobC must now occupy the dead mob's slot");

            VisibilitySystem.Compute(w, 0, cfg.Visibility, setB, setA); // tick 1

            Assert.IsFalse(setA.Contains(mobCId),
                "the survivor that moved into the dead mob's slot must not inherit that slot's PREVIOUS visibility state");
        }

        // --- 8: OwnPlayer_AlwaysVisibleToSelf ---

        [Test]
        public void OwnPlayer_AlwaysVisibleToSelf()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.ObstacleCount = 1;
            var selfPos = new float2(3f, -4f);
            // Obstacle sits exactly ON the observer's own position, radius
            // well past Hero.Radius: a naive self-evaluation that
            // (incorrectly) routed the observer through the ordinary
            // distance+LoS gate would call HasLineOfFire on a ZERO-LENGTH
            // segment sitting inside this obstacle — Geometry.SegmentCircle's
            // own degenerate-sweep branch (`math.lengthsq(f) < r*r`) reports
            // that as BLOCKED, so the naive path would wrongly conclude the
            // player cannot see themselves. The spec's "always visible to
            // self" rule exists precisely to skip that gate entirely.
            cfg.Arena.ObstaclePos = new[] { selfPos };
            cfg.Arena.ObstacleRadius = new[] { cfg.Hero.Radius + 1f };

            var w = new SimulationWorld(1, cfg);
            PlayerState p = w.Player;
            p.Pos = selfPos;
            w.SetPlayerForTest(p);

            // test setup: confirm the degenerate self-segment really would
            // read as blocked under the ordinary gate, so this fixture
            // actually exercises the self special-case rather than a line
            // that happens to be clear anyway.
            Assert.IsFalse(Targeting.HasLineOfFire(selfPos, selfPos, -cfg.Hero.Radius, cfg.Arena),
                "test setup: the degenerate self-segment must read as blocked under the ordinary LoS gate");

            var previous = new VisibilitySet(Capacity(cfg));
            var result = new VisibilitySet(Capacity(cfg));
            VisibilitySystem.Compute(w, 0, cfg.Visibility, previous, result);

            int selfId = VisibilityIds.ForPlayer(0);
            Assert.IsTrue(result.Contains(selfId),
                "own player must always be visible to self, bypassing the LoS gate entirely");
            Assert.AreEqual(0, result.LingerOf(selfId));
        }
    }
}
