using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Task 4: turns the single-player world into an N-player one — array of
    /// players, canonical TickAll(inputs), and the multiplayer spawn ring.
    public class MultiPlayerWorldTests
    {
        [Test]
        public void ThreePlayers_MoveIndependently()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            var inputs = new SimInput[3];
            inputs[0] = new SimInput { MoveDir = new float2(1f, 0f) };
            inputs[1] = new SimInput { MoveDir = new float2(-1f, 0f) };
            var b0 = w.PlayerAt(0).Pos; var b1 = w.PlayerAt(1).Pos; var b2 = w.PlayerAt(2).Pos;
            for (int t = 0; t < 10; t++) w.TickAll(inputs);
            Assert.Greater(w.PlayerAt(0).Pos.x, b0.x);
            Assert.Less(w.PlayerAt(1).Pos.x, b1.x);
            Assert.AreEqual(b2.x, w.PlayerAt(2).Pos.x, 1e-4f);
        }

        [Test]
        public void SoloOverload_ThrowsWhenMultiplayer()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            Assert.Throws<System.InvalidOperationException>(() => w.Tick(default));
        }

        [Test]
        public void SoloSpawnsAtOrigin_MultiplayerSpawnsOnRing()
        {
            var cfg = TestConfigs.Open();
            var solo = new SimulationWorld(1, cfg);
            Assert.AreEqual(0f, solo.Player.Pos.x, 1e-5f);
            Assert.AreEqual(0f, solo.Player.Pos.y, 1e-5f);

            var multi = new SimulationWorld(1, cfg, playerCount: 3);
            // Fixture arithmetic (Global Constraints C14): expected ring points
            // built from the SAME TestConfigs numbers the world was constructed
            // with, not a literal copied out of the .asset.
            float ringRadius = cfg.Arena.Radius * cfg.Arena.PlayerSpawnRingFrac;
            for (int i = 0; i < 3; i++)
            {
                float angle = i * 2f * math.PI / 3;
                float2 expected = new float2(math.cos(angle), math.sin(angle)) * ringRadius;
                Assert.AreEqual(expected.x, multi.PlayerAt(i).Pos.x, 1e-4f);
                Assert.AreEqual(expected.y, multi.PlayerAt(i).Pos.y, 1e-4f);
            }
        }

        [Test]
        public void SpawnRing_DoesNotDependOnSeed()
        {
            var cfg = TestConfigs.Open();
            var w1 = new SimulationWorld(1, cfg, playerCount: 3);
            var w999 = new SimulationWorld(999, cfg, playerCount: 3);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(w1.PlayerAt(i).Pos.x, w999.PlayerAt(i).Pos.x, 1e-5f);
                Assert.AreEqual(w1.PlayerAt(i).Pos.y, w999.PlayerAt(i).Pos.y, 1e-5f);
            }
        }

        [Test]
        public void CanonicalTickOrder_MovementBeforeWeapon()
        {
            // Canonical tick order (brief/context): movement of ALL players by
            // increasing index, THEN weapon of ALL players. Proven by event
            // order rather than a shared-target hit (player-vs-player damage
            // doesn't exist until Task 17): player 1's PlayerDashed (movement
            // phase) must land in the event buffer strictly before player 0's
            // ProjectileFired (weapon phase) within the SAME tick. A naive
            // per-player interleave (move0, weapon0, move1, weapon1) would
            // emit them in the opposite order.
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 2);
            var inputs = new SimInput[2];
            inputs[0] = new SimInput { FireHeld = true, AimPoint = new float2(5f, 0f) };
            inputs[1] = new SimInput { DashRequested = true };
            w.TickAll(inputs);

            int dashIndex = -1, fireIndex = -1;
            for (int i = 0; i < w.EventCount; i++)
            {
                var e = w.GetEvent(i);
                if (e.Kind == SimEventKind.PlayerDashed && dashIndex < 0) dashIndex = i;
                if (e.Kind == SimEventKind.ProjectileFired && fireIndex < 0) fireIndex = i;
            }
            Assert.GreaterOrEqual(dashIndex, 0, "player 1 should have dashed this tick");
            Assert.GreaterOrEqual(fireIndex, 0, "player 0 should have fired this tick");
            Assert.Less(dashIndex, fireIndex,
                "movement phase (player 1's dash) must be emitted before the weapon phase (player 0's shot)");
        }

        [Test]
        public void Constructor_PlayerCountOutOfRange_Throws()
        {
            var cfg = TestConfigs.Open();
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new SimulationWorld(1, cfg, playerCount: 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new SimulationWorld(1, cfg, playerCount: cfg.Arena.MaxPlayers + 1));
        }
    }
}
