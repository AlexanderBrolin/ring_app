using NUnit.Framework;
using Ring.Simulation.AI;
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

        /// Stage 3 Ф5-0 (owner decision R-173) rewrote what this pins. It used
        /// to assert the OPPOSITE of the line below — "solo spawns at the arena
        /// center" — which was the Stage 2 special case Geometry.SpawnPosFor
        /// carried until the three-zone arena made the center the Director's
        /// own ground (that method's own account). The fixture is Open(), not
        /// OpenField(): OpenField zeroes PlayerSpawnRingFrac so that movement
        /// fixtures can keep stating their geometry around the origin, and a
        /// rule about the spawn RING cannot be pinned on a fixture that has
        /// no ring.
        [Test]
        public void SoloTakesTheOnePlayerRingPoint_MultiplayerSpreadsAroundIt()
        {
            var cfg = TestConfigs.Open();
            float ring = cfg.Arena.Radius * cfg.Arena.PlayerSpawnRingFrac;

            var solo = new SimulationWorld(1, cfg);
            // The n=1 ring point is angle 0 — the same formula every other
            // lobby size takes, with no special case in front of it. Checked
            // as a POSITION, not as "not the origin": the arena center is
            // where the Director spawns and where the gate stands (spec
            // §3.4/§3.15), so "solo is not there" is exactly half of what
            // this test exists to say, and the other half is where it IS.
            Assert.AreEqual(ring, solo.Player.Pos.x, 1e-4f);
            Assert.AreEqual(0f, solo.Player.Pos.y, 1e-4f);
            Assert.Greater(math.length(solo.Player.Pos), cfg.Arena.ZoneRadius[1],
                "a solo collector must start OUTSIDE the middle boundary, i.e. nowhere near the core — " +
                "a live collector standing in the core is what activates the Director (Р299)");

            var multi = new SimulationWorld(1, cfg, playerCount: 3);
            // Fixture arithmetic (Global Constraints C14): expected ring points
            // built from the SAME TestConfigs numbers the world was constructed
            // with, not a literal copied out of the .asset.
            for (int i = 0; i < 3; i++)
            {
                float angle = i * 2f * math.PI / 3;
                float2 expected = new float2(math.cos(angle), math.sin(angle)) * ring;
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
        // either way. Each was red/green-proven (task-4-report.md, fix-round 1).

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
            // ⚠ THE FIXTURE NARROWED IN Т4 (app-ggvz), THE CLAIM DID NOT.
            // With three independent ring cadences all three rings seat their
            // first wave on the same tick and would come out clear on the same
            // tick too — the counter would read 3, correctly, and this test
            // would be measuring the number of RINGS rather than the number of
            // PLAYERS. So exactly one ring is left able to clear: the
            // neighbors are handed debt, and the clearing tick therefore fails
            // BOTH terms of their clear check at once (the debt they cannot
            // finish, and the mobs that debt puts on the arena) — whichever of
            // the two bites, neither neighbor can be counted.
            //
            // TestWorlds.ClearFirstWave cannot be used for the same narrowing:
            // it kills every mob after every tick, so the neighbors' debt
            // drains in one tick and they clear on the next, inside the
            // helper's own loop.
            // ⚠ THE WAIT IS THE WHOLE ARRIVAL OF THE WAVE, not one tick past
            // its start (Т5): a ring seats at most MaxSpawnsPerZonePerTick mobs
            // per tick, so a wave of `waveSize` needs ceil(waveSize / cap)
            // ticks before its debt is closed — and a ring that still owes mobs
            // cannot be cleared, which is what a one-tick wait quietly turned
            // this test into. Stated as fixture arithmetic so a change to
            // either number moves it.
            SimConfig cfg = TestConfigs.Default();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            int waveSize = WaveSystem.CountForTest(in cfg.Wave, 0, w.PlayerCount);
            int cap = cfg.Wave.MaxSpawnsPerZonePerTick;
            int seatTicks = (waveSize + cap - 1) / cap;
            TestWorlds.IdleTicks(w, SimulationWorld.TicksFromSeconds(cfg.Wave.FirstWaveDelay)
                + seatTicks);
            for (int z = 0; z < Zones.Count; z++)
                Assert.AreEqual(WavePhase.Active, w.WaveRef((Zone)z).Phase,
                    $"premise: ring {(Zone)z} has seated its first wave — the sentence says EVERY "
                    + "ring, so every ring is what it reads");
            Assert.AreEqual(0, w.WaveRef(Zone.Outer).PendingTotal,
                "premise: the outer ring owes nothing any more — a ring still holding debt "
                + "cannot be cleared, and the assertion below would be measuring the wait");

            foreach (Zone z in new[] { Zone.Middle, Zone.Core })
            {
                WaveState debt = w.WaveRef(z);
                debt.PendingChaser = 99;
                w.SetWaveForTest(z, debt);
            }
            w.ClearMobsForTest();
            w.TickAll(new SimInput[w.PlayerCount]);

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

        // T5 fix-round 1 I-1: StateHash in T5 only hashes _matchStats[0], and no
        // test read snapshot.PlayerStats — so a save/restore or CaptureSnapshot
        // regression that quietly truncated the personal-stats array copy to one
        // element would have left all 204 tests green. Both proven falsifiable
        // below (temporary truncation, red observed, reverted — see task-5-report.md).

        [Test]
        public void SaveState_RestoreState_RoundTripsAllPlayersStats()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            var s0 = new MatchStats { Kills = 1, ShotsFired = 11 };
            var s1 = new MatchStats { Kills = 2, ShotsFired = 22 };
            var s2 = new MatchStats { Kills = 3, ShotsFired = 33 };
            w.SetStatsForTest(0, s0);
            w.SetStatsForTest(1, s1);
            w.SetStatsForTest(2, s2);
            var save = w.SaveState();

            // Mutate all three away from the saved snapshot so the restore is observable.
            w.SetStatsForTest(0, new MatchStats());
            w.SetStatsForTest(1, new MatchStats());
            w.SetStatsForTest(2, new MatchStats());

            w.RestoreState(save);

            Assert.AreEqual(s0.Kills, w.StatsAt(0).Kills);
            Assert.AreEqual(s0.ShotsFired, w.StatsAt(0).ShotsFired);
            Assert.AreEqual(s1.Kills, w.StatsAt(1).Kills);
            Assert.AreEqual(s1.ShotsFired, w.StatsAt(1).ShotsFired);
            Assert.AreEqual(s2.Kills, w.StatsAt(2).Kills);
            Assert.AreEqual(s2.ShotsFired, w.StatsAt(2).ShotsFired);
        }

        [Test]
        public void CaptureSnapshot_CopiesEveryPlayersStats()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(1, cfg, playerCount: 3);
            w.SetStatsForTest(1, new MatchStats { Kills = 7, ShotsFired = 9 });
            var snap = new RenderSnapshot(cfg);
            w.CaptureSnapshot(snap);

            Assert.AreEqual(7, snap.PlayerStats[1].Kills);
            Assert.AreEqual(9, snap.PlayerStats[1].ShotsFired);
        }

        // Fix-round tail (owner-decided, carried into this task): three small
        // gaps left over from Task 4's fix-round review.

        [Test]
        public void TickAll_SpanShorterThanPlayerCount_Throws()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 3);
            float2 posBefore = w.PlayerAt(0).Pos;
            var shortInputs = new SimInput[2]; // one short of PlayerCount
            Assert.Throws<System.ArgumentException>(() => w.TickAll(shortInputs));
            // T5 fix-round 1 I-3: the guard's whole point is refusing BEFORE any
            // mutation — asserting only the exception type would not catch a
            // regression that moved the check past _tick++/the movement loop.
            Assert.AreEqual(0, w.CurrentTick,
                "the guard must fire before any mutation, including _tick++");
            Assert.AreEqual(posBefore, w.PlayerAt(0).Pos,
                "no player should have moved either — the guard must fire before the movement loop");
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
            // T5 fix-round 1 M-5: named fixtures (Global Constraints C14) instead
            // of three unrelated-looking literals — aim1 is both what tick 1
            // sends AND what tick 2's assertion expects back.
            var aim0 = new float2(3f, 4f);
            var aim1 = new float2(7f, 9f);
            // Tick 1: give each player a distinct, finite AimPoint so their
            // OWN previous AimPoint differs going into tick 2.
            inputs[0] = new SimInput { AimPoint = aim0 };
            inputs[1] = new SimInput { AimPoint = aim1 };
            w.TickAll(inputs);

            // Tick 2: player 1's AimPoint goes non-finite — Sanitize must fall
            // back to PLAYER 1's own previous AimPoint (index 1), not player 0's.
            inputs[1] = new SimInput { AimPoint = new float2(float.NaN, float.NaN) };
            w.TickAll(inputs);

            Assert.AreEqual(aim1.x, w.PlayerAt(1).AimPoint.x, 1e-5f);
            Assert.AreEqual(aim1.y, w.PlayerAt(1).AimPoint.y, 1e-5f);
        }

        // Stage 2 Task 8: KillPlayerNoDamage — a player exits the match with no
        // damage dealt and no kill credited to anyone; DamagePlayer's own death
        // branch and this new no-damage path both now go through the single
        // shared KillPlayer (task-8-context.md, scope items 2/3).

        [Test]
        public void KillPlayerNoDamage_KillsWithoutDamageOrCredit()
        {
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 2);
            float damageTakenBefore = w.StatsAt(0).DamageTaken;

            w.KillPlayerNoDamage(0);

            Assert.IsFalse(w.PlayerAt(0).Alive, "the departed player must be dead");
            Assert.AreEqual(damageTakenBefore, w.StatsAt(0).DamageTaken, 1e-5f,
                "a no-damage kill must not touch DamageTaken");
            // M-7 (fix-round 1): reuse TestEvents.CountOf instead of a
            // hand-rolled scan loop (Global Constraints — existing test
            // helpers are reused, not re-implemented).
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.PlayerDied),
                "exactly one PlayerDied must fire");
            Assert.AreEqual(0, w.StatsAt(1).Kills,
                "there is no attacker — nobody is credited a kill for a departure");

            // Fix-round 1 I-3: a repeat call on an already-dead index (real
            // scenario — a disconnect handler and a later timeout cleanup both
            // calling this for the same player) must be a no-op, not a second
            // PlayerDied/DeathTick overwrite — same idempotency contract
            // DeathTests.PlayerDied_EmittedOnce_DeathTickRecorded pins for the
            // damage-death path.
            int deathTickAfterFirstKill = w.StatsAt(0).DeathTick;
            w.KillPlayerNoDamage(0);
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.PlayerDied),
                "a repeat no-damage kill on an already-dead player must not emit a second PlayerDied");
            Assert.AreEqual(deathTickAfterFirstKill, w.StatsAt(0).DeathTick,
                "a repeat no-damage kill must not overwrite DeathTick");
        }

        [Test]
        public void KillPlayerNoDamage_IndexOutOfRange_Throws()
        {
            // Fix-round 1 M-8: KillPlayerNoDamage is public — a future
            // Networking disconnect handler calls it with an externally
            // sourced index — so an out-of-range value must fail with a clear
            // ArgumentOutOfRangeException, same style as the constructor's
            // playerCount guard, not an opaque IndexOutOfRangeException from
            // deep inside the player array.
            var w = new SimulationWorld(1, TestConfigs.Open(), playerCount: 2);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => w.KillPlayerNoDamage(-1));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => w.KillPlayerNoDamage(2));
        }

        [Test]
        public void KillPlayerNoDamage_DepartedPlayersInFlightProjectile_StillDamagesMob()
        {
            var c = TestConfigs.Open();
            var w = new SimulationWorld(2, c, playerCount: 2);
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(3f, 0f),
                new float2(35f, 0f), 1f, 0f, 100f, 0.12f, 2f);

            w.KillPlayerNoDamage(0); // the shooter leaves before the shot connects

            var inputs = new SimInput[2];
            for (int i = 0; i < 10; i++) w.TickAll(inputs); // projectile arrives and kills

            Assert.AreEqual(0, w.MobCount,
                "the departed player's already-flying shot must still land");
        }
    }
}
