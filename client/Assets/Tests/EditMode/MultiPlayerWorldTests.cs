using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// Stage 2 Task 4: turns the single-player world into an N-player one —
    /// array of players, canonical TickAll(inputs), and the multiplayer spawn ring.
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
            // Fix-round 1 M-7: player 2 gets no input at all, so BOTH axes
            // must stay put — the original test only checked x, which
            // wouldn't catch a bug that leaked y-motion (or another player's
            // y) onto an untouched player.
            Assert.AreEqual(b2.y, w.PlayerAt(2).Pos.y, 1e-4f);
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
            // doesn't exist until Stage 2 Task 17): player 1's PlayerDashed (movement
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

        // Fix-round 1 (I-2): the three tests below exist specifically because
        // a reviewer showed, by exhaustive revert, that the pre-fix-round-1
        // MultiPlayerWorldTests suite could not tell the real multiplayer
        // Sanitize/ApplyConfig/SaveState-RestoreState from a version that
        // silently still only handled player 0 — all 195 tests stayed green
        // either way. Each was red/green-proven (task-4-report.md, "Фикс-раунд 1").

        [Test]
        public void SanitizePerPlayer_ClipsAroundOwnPosition_NotPlayer0()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 2);
            // Ring spawn (playerCount > 1): PlayerAt(1).Pos != PlayerAt(0).Pos,
            // so clipping around the wrong player's position is observable.
            float2 p1Pos = w.PlayerAt(1).Pos;
            var farAimPoint = new float2(1e6f, 0f);
            var inputs = new SimInput[2];
            inputs[1] = new SimInput { AimPoint = farAimPoint };
            w.TickAll(inputs);

            // Fixture expression (Global Constraints C14) — same clip radius
            // Sanitize itself computes (Arena.Radius * 2), no literal.
            float maxR = cfg.Arena.Radius * 2f;
            float2 expected = p1Pos + math.normalizesafe(farAimPoint - p1Pos) * maxR;
            Assert.AreEqual(expected.x, w.PlayerAt(1).AimPoint.x, 1e-2f);
            Assert.AreEqual(expected.y, w.PlayerAt(1).AimPoint.y, 1e-2f);
        }

        [Test]
        public void SaveState_RestoreState_RoundTripsAllPlayers()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            var p0 = new PlayerState { Pos = new float2(1f, 2f), Hp = 11f, Alive = true };
            var p1 = new PlayerState { Pos = new float2(3f, 4f), Hp = 22f, Alive = true };
            var p2 = new PlayerState { Pos = new float2(5f, 6f), Hp = 33f, Alive = true };
            w.SetPlayerForTest(0, p0);
            w.SetPlayerForTest(1, p1);
            w.SetPlayerForTest(2, p2);
            var save = w.SaveState();

            // Mutate all three away from the saved snapshot so the restore is observable.
            w.SetPlayerForTest(0, new PlayerState { Alive = true });
            w.SetPlayerForTest(1, new PlayerState { Alive = true });
            w.SetPlayerForTest(2, new PlayerState { Alive = true });

            w.RestoreState(save);

            Assert.AreEqual(p0.Pos, w.PlayerAt(0).Pos);
            Assert.AreEqual(p0.Hp, w.PlayerAt(0).Hp, 1e-5f);
            Assert.AreEqual(p1.Pos, w.PlayerAt(1).Pos);
            Assert.AreEqual(p1.Hp, w.PlayerAt(1).Hp, 1e-5f);
            Assert.AreEqual(p2.Pos, w.PlayerAt(2).Pos);
            Assert.AreEqual(p2.Hp, w.PlayerAt(2).Hp, 1e-5f);
        }

        [Test]
        public void ApplyConfig_ClampsHpForEveryPlayer_NotJustPlayer0()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            w.SetPlayerForTest(0, new PlayerState { Hp = cfg.Hero.MaxHp, Alive = true });
            w.SetPlayerForTest(1, new PlayerState { Hp = cfg.Hero.MaxHp, Alive = true });
            w.SetPlayerForTest(2, new PlayerState { Hp = cfg.Hero.MaxHp, Alive = true });

            var next = cfg;
            next.Hero.MaxHp = 1f; // far below the starting Hp — the clamp is unmistakable
            w.ApplyConfig(next);

            Assert.AreEqual(next.Hero.MaxHp, w.PlayerAt(0).Hp, 1e-5f);
            Assert.AreEqual(next.Hero.MaxHp, w.PlayerAt(1).Hp, 1e-5f);
            Assert.AreEqual(next.Hero.MaxHp, w.PlayerAt(2).Hp, 1e-5f);
        }

        // Stage 2 Task 5: MatchStats[] (personal) vs WorldStats (shared) — the
        // three tests below prove the split actually behaves per-player/per-
        // match rather than just compiling that way.

        [Test]
        public void PersonalStats_DoNotMix()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 2);
            var inputs = new SimInput[2];
            inputs[1] = new SimInput { FireHeld = true, AimPoint = new float2(10f, 0f) };
            // player 0 gets no input at all — stays idle the whole tick.
            w.TickAll(inputs);

            Assert.Greater(w.StatsAt(1).ShotsFired, 0,
                "player 1 fired — it must show up on THEIR OWN personal stats");
            Assert.AreEqual(0, w.StatsAt(0).ShotsFired,
                "player 0 never fired — their personal ShotsFired must stay untouched");
        }

        [Test]
        public void WorldStats_CountedOnce_NotPerPlayer()
        {
            var w = new SimulationWorld(1, TestConfigs.Default(), playerCount: 3);
            TestWorlds.ClearFirstWave(w);
            Assert.AreEqual(1, w.WorldStats.WavesCleared,
                "clearing one wave with three players in the match must bump the WORLD " +
                "counter exactly once, not once per player");
        }

        [Test]
        public void DeathOfOne_DoesNotFreezeOthersStats()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 2);
            w.KillPlayerForTest(); // player 0 dies before player 1 ever acts

            var inputs = new SimInput[2];
            inputs[1] = new SimInput { DashRequested = true };
            w.TickAll(inputs);

            Assert.AreEqual(0, w.StatsAt(0).DashesUsed, "player 0 is dead — no dash of theirs");
            Assert.AreEqual(1, w.StatsAt(1).DashesUsed,
                "player 0's death must not freeze player 1's own personal stats");
        }

        // Fix-round tail (owner-decided, carried into this task): three small
        // gaps left over from Task 4's fix-round review.

        [Test]
        public void TickAll_SpanShorterThanPlayerCount_Throws()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            var shortInputs = new SimInput[2]; // one short of PlayerCount
            Assert.Throws<System.ArgumentException>(() => w.TickAll(shortInputs));
        }

        [Test]
        public void RestoreState_PlayerCountMismatch_Throws()
        {
            var w3 = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            var save3 = w3.SaveState();
            var w2 = new SimulationWorld(1, TestConfigs.Open(), playerCount: 2);
            Assert.Throws<System.ArgumentException>(() => w2.RestoreState(save3));
        }

        [Test]
        public void SanitizePerPlayer_NaNAimPoint_FallsBackToOwnPreviousAimPoint_NotPlayer0()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 2);
            var inputs = new SimInput[2];
            // Tick 1: give each player a distinct, finite AimPoint so their
            // OWN previous AimPoint differs going into tick 2.
            inputs[0] = new SimInput { AimPoint = new float2(3f, 4f) };
            inputs[1] = new SimInput { AimPoint = new float2(7f, 9f) };
            w.TickAll(inputs);

            // Tick 2: player 1's AimPoint goes non-finite — Sanitize must fall
            // back to PLAYER 1's own previous AimPoint (index 1), not player 0's.
            inputs[1] = new SimInput { AimPoint = new float2(float.NaN, float.NaN) };
            w.TickAll(inputs);

            Assert.AreEqual(7f, w.PlayerAt(1).AimPoint.x, 1e-5f);
            Assert.AreEqual(9f, w.PlayerAt(1).AimPoint.y, 1e-5f);
        }
    }
}
