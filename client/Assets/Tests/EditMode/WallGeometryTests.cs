using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 15: regression grid for the movement mechanics walls were
    /// introduced FOR (owner decision C11) — slide-along-a-flat-side, wall-stop
    /// damping, dash ricochet, and no-tunneling — now exercised against the
    /// stadium wall primitive (Task 11/12) instead of the circle obstacles
    /// DashRicochetTests/SlideTests already cover. Adds NO production code:
    /// every fixture below builds its own wall via TestConfigs.Open() plus an
    /// explicit WallCount/WallA/WallB/WallHalfWidth layout laid on top of it.
    /// Task 16 has since landed TestConfigs.DefaultArena()'s own real wall
    /// data (WallCount 6 — the golden-hash fixtures now carry walls too), but
    /// that is TestConfigs.Default()/DefaultArena(), a different baseline from
    /// the TestConfigs.Open() every fixture below actually starts from — Open()
    /// still zeroes WallCount before each fixture lays its own layout on top,
    /// so this file still cannot move either golden hash; it is pure
    /// additional coverage over a baseline the golden scenarios don't use.
    public class WallGeometryTests
    {
        static SimInput Move(float x, float y) => new SimInput { MoveDir = new float2(x, y) };

        // --- 1: SlideAlongFlatWall_DoesNotAccelerate ---

        [Test]
        public void SlideAlongFlatWall_DoesNotAccelerate()
        {
            var cfg = TestConfigs.OpenField();
            cfg.Arena.WallCount = 1;
            // Long horizontal wall (axis along +x) above the player's spawn.
            // Touch boundary from below (fixture expr): wall centreline 2,
            // minus (WallHalfWidth 1 + Hero.Radius 0.45) = 0.55.
            cfg.Arena.WallA = new[] { new float2(-10f, 2f) };
            cfg.Arena.WallB = new[] { new float2(50f, 2f) };
            cfg.Arena.WallHalfWidth = new[] { 1f };
            float touchY = 2f - cfg.Arena.WallHalfWidth[0] - cfg.Hero.Radius;

            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.RunUpTimer = cfg.Hero.RunUpSeconds; // QA1 seam (SlideTests.WallStop_KillsSlide_NoLinkWindow)
            p.Vel = new float2(cfg.Hero.MaxSpeed, 0f);
            w.SetPlayerForTest(p);

            // Shallow +y drift angles the slide up into the wall's flat side
            // (not its rounded end — the wall spans far past where the player
            // reaches it in x) instead of a single glancing touch, so the
            // fixture exercises repeated Slide() calls across many ticks —
            // exactly the "collide-and-slide with a wrong normal adds energy"
            // failure mode this test guards against (Урок 65).
            var input = new SimInput { MoveDir = new float2(1f, 0.5f), SlideRequested = true };
            w.Tick(input); // slide starts
            Assert.AreEqual(1, w.Stats.SlidesUsed, "test setup: slide must have started");

            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt);
            for (int i = 0; i < slideTicks - 1; i++)
            {
                w.Tick(new SimInput { MoveDir = new float2(1f, 0.5f) });
                float speed = math.length(w.Player.Vel);
                Assert.LessOrEqual(speed, cfg.Hero.SlideSpeed + 1e-3f,
                    $"tick {i}: slide speed {speed:F3} exceeded SlideSpeed {cfg.Hero.SlideSpeed:F3} " +
                    "while grazing the wall's flat side");
            }

            // Contact must actually have engaged (Урок 64): unconstrained
            // flight on this input would have carried the player well past
            // touchY + 0.5 long before the loop above finishes — pinned near
            // it instead is the wall's own doing, not a vacuous pass.
            Assert.LessOrEqual(w.Player.Pos.y, touchY + 0.5f,
                "test setup: must have been held near the wall's flat side, not flown clear over it");
        }

        // --- 2: SlideHeadOnIntoWall_IsDamped ---

        [Test]
        public void SlideHeadOnIntoWall_IsDamped()
        {
            var cfg = TestConfigs.OpenField();
            cfg.Arena.WallCount = 1;
            // Vertical wall dead ahead — a head-on hit against the flat side,
            // dot(-normal, SlideDir) == 1 > SlideWallStopDot, same shape as
            // SlideTests.WallStop_KillsSlide_NoLinkWindow but against a wall.
            cfg.Arena.WallA = new[] { new float2(5f, -50f) };
            cfg.Arena.WallB = new[] { new float2(5f, 50f) };
            cfg.Arena.WallHalfWidth = new[] { 1f };

            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.RunUpTimer = cfg.Hero.RunUpSeconds; // QA1 seam
            p.Vel = new float2(cfg.Hero.MaxSpeed, 0f);
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide starts
            Assert.AreEqual(1, w.Stats.SlidesUsed);
            Assert.Greater(w.Player.SlideTimer, 0f, "test setup: must be sliding");

            int budget = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt) + 2;
            bool stopped = false;
            for (int i = 0; i < budget; i++)
            {
                w.Tick(new SimInput { MoveDir = new float2(1f, 0f) });
                if (w.Player.SlideTimer <= 0f) { stopped = true; break; }
            }
            Assert.IsTrue(stopped, "test setup: slide never wall-stopped against the wall's flat side");
            Assert.AreEqual(0f, w.Player.SlideTimer);
            Assert.AreEqual(cfg.Hero.MaxSpeed, math.length(w.Player.Vel), 1e-3f);
            Assert.AreEqual(0f, w.Player.LinkWindowTimer, "wall-stop must not open the link window");

            // Proof by consequence (mirrors WallStop_KillsSlide_NoLinkWindow):
            // if the window had opened, this dash would be linked and cost
            // less — it must cost the FULL price.
            float staminaBeforeDash = w.Player.Stamina;
            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true });
            Assert.AreEqual(1, w.Stats.DashesUsed);
            Assert.AreEqual(staminaBeforeDash - cfg.Hero.DashStaminaCost, w.Player.Stamina, 1e-3f,
                "wall-stop must not leave a link window — the dash must cost the FULL price");
        }

        // --- 3: DashRicochetsOffFlatWall_MirrorsAndRetains ---

        /// Same DashSpeed/DashDuration/RicochetRetention shape as
        /// DashRicochetTests.Fixture() (DashSpeed 30 * dt (1/30 s) = 1 m/tick
        /// exactly), against a flat wall instead of a circular obstacle.
        static SimConfig DiagonalWallFixture()
        {
            var cfg = TestConfigs.OpenField();
            cfg.Hero.DashSpeed = 30f;
            cfg.Hero.DashDuration = 0.09f;
            cfg.Hero.RicochetRetention = 0.8f;
            cfg.Arena.WallCount = 1;
            // Vertical wall at x=1.6, half-width 0.55: touch boundary (band
            // edge 1.05, minus Hero.Radius 0.45) sits at x=0.6 — inside the
            // dash's first 1 m step along the diagonal (1,1) direction
            // (contact at t~=0.849), so the contact tick is also the
            // dash-start tick, same shape as DashRicochetTests.ObstacleFixture.
            cfg.Arena.WallA = new[] { new float2(1.6f, -50f) };
            cfg.Arena.WallB = new[] { new float2(1.6f, 50f) };
            cfg.Arena.WallHalfWidth = new[] { 0.55f };
            return cfg;
        }

        [Test]
        public void DashRicochetsOffFlatWall_MirrorsAndRetains()
        {
            var cfg = DiagonalWallFixture();
            var w = new SimulationWorld(1, cfg);
            float2 dashDirBefore = math.normalize(new float2(1f, 1f));
            var dashDiagonal = new SimInput { MoveDir = new float2(1f, 1f), DashRequested = true };
            // DashDir/DashSpeedCur alone drive Vel on continuation ticks (the
            // branch never re-reads input.MoveDir, per DashRicochetTests.
            // Ricochet_RetentionCompoundsAcrossTwoBounces) — this input only
            // needs DashRequested false so a fresh dash isn't re-triggered.
            var holdDiagonal = new SimInput { MoveDir = new float2(1f, 1f) };

            w.Tick(dashDiagonal); // start tick: dash clips the wall's flat side
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.DashRicocheted, out SimEvent e),
                "test setup: dash must have ricocheted off the wall on its start tick");
            float2 normal = e.HitDir;
            float2 tangent = new float2(-normal.y, normal.x);

            w.Tick(holdDiagonal); // mirrored DashDir now drives Vel

            float2 reflected = math.normalizesafe(w.Player.Vel);
            // Law of reflection, checked against the SHAPE of the bounce (not
            // a hard-coded reflected vector): the tangential component
            // survives unchanged, the normal component flips sign with the
            // same magnitude — angle of incidence == angle of reflection.
            Assert.AreEqual(math.dot(dashDirBefore, tangent), math.dot(reflected, tangent), 1e-3f,
                "tangential component must survive the bounce unchanged");
            Assert.AreEqual(-math.dot(dashDirBefore, normal), math.dot(reflected, normal), 1e-3f,
                "normal component must flip sign with the same magnitude " +
                "(angle of incidence == angle of reflection)");

            Assert.AreEqual(cfg.Hero.DashSpeed * cfg.Hero.RicochetRetention,
                math.length(w.Player.Vel), 1e-3f);
        }

        // --- 4: CorridorSeam_NoSticking ---

        [Test]
        public void CorridorSeam_NoSticking()
        {
            var cfg = TestConfigs.Open();
            cfg.Arena.WallCount = 1;
            // Horizontal wall spanning x in [-5,5]; touch boundary from below
            // (band edge 2.5, minus Hero.Radius 0.45) sits at y=1.05. The
            // player runs a shallow diagonal that keeps grazing the flat
            // side the whole time it is inside the wall's x-span, then must
            // cross the SEAM at WallB (x=5, the flat side (candidate 4)
            // handing off to the rounded cap (candidates 2/3) — spec §3.3,
            // Task 12 review: the normal field is continuous there
            // analytically, so any sticking found here is in
            // MoveWithCollisions (3 iterations, Skin) or the fixture, not
            // in the normal itself (task-15-brief).
            cfg.Arena.WallA = new[] { new float2(-5f, 2f) };
            cfg.Arena.WallB = new[] { new float2(5f, 2f) };
            cfg.Arena.WallHalfWidth = new[] { 0.5f };

            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.Pos = new float2(0f, 1.0f); // 0.05 m short of the touch boundary
            w.SetPlayerForTest(p);

            var input = new SimInput { MoveDir = new float2(1f, 0.3f) };

            // Checkpoint well inside the wall's x-span: must still be pinned
            // near the flat side, not free to climb toward the diagonal's
            // unconstrained y target — evidence the flat side actually
            // engaged (Урок 64), not that the fixture just never touches it.
            for (int i = 0; i < 10; i++) w.Tick(input);
            Assert.Less(w.Player.Pos.y, 1.5f,
                "test setup: must still be constrained by the wall's flat side at this checkpoint");

            // Capped well short of the arena's own outer ring (radius 113,
            // TestConfigs.Open()'s default since Stage 3 Task 12; 65 before it
            // — reached only well past this test's own 100-tick budget either
            // way, and the margin only grew) — this test's job is the CORRIDOR
            // wall's own seam (crossed by ~tick 24), not an incidental second
            // collision against unrelated ring geometry.
            const int ticks = 100;
            for (int i = 10; i < ticks; i++)
            {
                w.Tick(input);
                Assert.IsTrue(math.all(math.isfinite(w.Player.Pos)));
            }

            // Must have advanced past the wall's own end (WallB.x = 5) to
            // have actually crossed the seam, not merely approached it.
            Assert.Greater(w.Player.Pos.x, 5f + cfg.Hero.Radius,
                "test setup: must have travelled past the wall's end to have crossed its seam");

            // The wall only ever clips the INTO (y) component of Vel — the
            // tangential (x) component is untouched by Slide() — so the
            // "expected" forward pace uses the sanitized diagonal's own x
            // share of MaxSpeed, not the full MaxSpeed (fixture expr, PD5).
            float2 dir = math.normalize(new float2(1f, 0.3f)); // same shape SimInputSanitizer applies
            float expectedForward = dir.x * cfg.Hero.MaxSpeed * ticks * SimulationWorld.TickDt;
            float forward = w.Player.Pos.x; // start.x was 0
            Assert.GreaterOrEqual(forward, 0.9f * expectedForward,
                $"stuck at the wall seam: only covered {forward:F2}m of an expected ~{expectedForward:F2}m");
        }

        // --- 5: CorridorTraversal_NoTunneling ---

        static SimConfig ThinWallFixture()
        {
            // DashSpeed cranked well past any real balance number (PD5-style
            // local fixture, same discipline as DashRicochetTests.Fixture()):
            // 90 * dt (1/30 s) = 3 m/tick, deliberately larger than the
            // wall's own padded width (2*(WallHalfWidth 0.8 + Hero.Radius
            // 0.45) = 2.5 m) — the dash is the fastest entity in the game
            // (task-15-context), and this margin is what forces a genuine
            // swept check: a naive "does the TARGET point overlap anything"
            // test would see the dash already clear on the far side and
            // report no collision at all.
            var cfg = TestConfigs.OpenField();
            cfg.Hero.DashSpeed = 90f;
            cfg.Hero.DashDuration = 0.09f;
            cfg.Arena.WallCount = 1;
            cfg.Arena.WallA = new[] { new float2(5f, -50f) };
            cfg.Arena.WallB = new[] { new float2(5f, 50f) };
            cfg.Arena.WallHalfWidth = new[] { 0.8f };
            return cfg;
        }

        [Test]
        public void CorridorTraversal_NoTunneling()
        {
            var cfg = ThinWallFixture();
            float nearTouch = 5f - cfg.Arena.WallHalfWidth[0] - cfg.Hero.Radius;
            float farTouch = 5f + cfg.Arena.WallHalfWidth[0] + cfg.Hero.Radius;
            float step = cfg.Hero.DashSpeed * SimulationWorld.TickDt; // fixture expr
            Assert.Greater(step, farTouch - nearTouch,
                "test setup: the dash's own step must exceed the wall's full padded width — " +
                "otherwise a naive endpoint-only check would catch this too, and the test would prove nothing");

            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            // Start just short of the near touch boundary: one dash tick's
            // straight-line target lands well past the far boundary, clean
            // outside the padded wall entirely.
            p.Pos = new float2(nearTouch - 0.05f, 0f);
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), DashRequested = true });

            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.DashRicocheted, out _),
                "test setup: the swept collision must have registered a contact/ricochet, not a silent pass-through");
            Assert.LessOrEqual(w.Player.Pos.x, nearTouch + 0.1f,
                $"dash tunnelled through the wall: landed at x={w.Player.Pos.x:F3}, " +
                $"expected to stop near the near touch boundary {nearTouch:F3}");
        }

        // --- 6: SlideAlongWallCap_DoesNotStick (coordinator's addition) ---

        [Test]
        public void SlideAlongWallCap_DoesNotStick()
        {
            var cfg = TestConfigs.OpenField();
            cfg.Arena.WallCount = 1;
            // The wall's endpoint B sits exactly where SlideTests.
            // SlideAlongWall_Continues's own circular obstacle sat — (5, 1.3),
            // effective radius 1 — reusing its proven dot(-normal, SlideDir)
            // ~= 0.44 shallow-graze math verbatim, but the SEGMENT extends
            // AWAY from the player's approach (up to y=10), so
            // ClosestPointOnSegment clamps to B for every point along the
            // approach path (y <= 1.3): a genuine end-cap contact
            // (SegmentStadium candidates 2/3), never the flat side
            // (candidate 4) — the coordinator's own rationale for this test
            // (flat side and cap are different candidates with different
            // normal derivations; Task 15's other tests only cover the flat
            // side).
            cfg.Arena.WallA = new[] { new float2(5f, 10f) };
            cfg.Arena.WallB = new[] { new float2(5f, 1.3f) };
            cfg.Arena.WallHalfWidth = new[] { 1f };

            var w = new SimulationWorld(1, cfg);
            var p = w.Player;
            p.RunUpTimer = cfg.Hero.RunUpSeconds; // QA1 seam
            p.Vel = new float2(cfg.Hero.MaxSpeed, 0f);
            w.SetPlayerForTest(p);

            w.Tick(new SimInput { MoveDir = new float2(1f, 0f), SlideRequested = true }); // slide starts
            Assert.AreEqual(1, w.Stats.SlidesUsed);

            int slideTicks = (int)math.ceil(cfg.Hero.SlideDuration / SimulationWorld.TickDt);
            for (int i = 0; i < slideTicks - 1; i++)
                w.Tick(Move(1f, 0f));
            Assert.Greater(w.Player.SlideTimer, 0f,
                "graze against the end cap must not have wall-stopped the slide early");

            w.Tick(Move(1f, 0f)); // this tick ends it naturally
            Assert.AreEqual(0f, w.Player.SlideTimer);
            Assert.Greater(w.Player.LinkWindowTimer, 0f, "natural exit must still open the link window");

            // Evidence the cap actually engaged (Урок 64): the player's
            // straight-ahead SlideDir (1,0) runs along y=0, exactly 1.3 m
            // below the cap center (5,1.3) — inside its 1.45 m touch radius
            // (WallHalfWidth 1 + Hero.Radius 0.45) — so an unobstructed run
            // would have stayed at y=0 the entire time; the cap's push-out
            // must have nudged the player to negative y (away from it).
            Assert.Less(w.Player.Pos.y, -0.01f,
                "test setup: the end cap must have pushed the player off its straight-line path");
        }
    }
}
