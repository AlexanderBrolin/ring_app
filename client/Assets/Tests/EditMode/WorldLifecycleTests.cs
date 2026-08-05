using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WorldLifecycleTests
    {
        // Stage 2 Task 10: the TEMPORARY PendingHashFields skip-list (T7 -> T10,
        // holding ProjectileState.OwnerIndex out of the sweep until the
        // sanctioned re-pin) is GONE — the field is in the hash now, so the
        // sweep below asserts on it like on every other field. Removal proven,
        // not assumed: with OwnerIndex (and, one at a time, each of the two new
        // edge-request counters) temporarily pulled back out of the hash, this
        // sweep goes red naming exactly that field — quoted in task-10-report.md.

        [Test]
        public void SaveRestore_ReplaysToSameHash()
        {
            var w = new SimulationWorld(42, TestConfigs.Default());
            var input = new SimInput { FireHeld = true };
            for (int i = 0; i < 100; i++) w.Tick(input);
            WorldSave save = w.SaveState();
            for (int i = 0; i < 500; i++) w.Tick(input);
            ulong straight = w.StateHash();
            w.RestoreState(save);
            for (int i = 0; i < 500; i++) w.Tick(input);
            Assert.AreEqual(straight, w.StateHash());
        }

        [Test]
        public void TwoWorldsSameSeed_NoStaticState()
        {
            ulong a = Run(42); ulong b = Run(42);
            Assert.AreEqual(a, b);
            static ulong Run(long seed)
            {
                var w = new SimulationWorld(seed, TestConfigs.Default());
                for (int i = 0; i < 300; i++) w.Tick(default);
                return w.StateHash();
            }
        }

        [Test]
        public void EveryPlayerAndStatsFieldAffectsHash() // spec §3.13 item 12 / §3.3
        {
            // Stage 2 Task 10: TWO players, not one. The canonical hash order now
            // folds in every player and every MatchStats slot
            // (playerCount + players[0..n), statsCount + stats[0..n)), so a sweep
            // that only ever bumped index 0 could not tell that order from one
            // whose loops were silently truncated back to `_players[0]` /
            // `_matchStats[0]` — exactly the shape the pre-Task-10 hash had.
            const int PlayerCount = 2;
            var w = new SimulationWorld(3, TestConfigs.Default(), PlayerCount);
            // F-4 fix-round: one live mob and one live projectile, spawned via the
            // test seams BEFORE SaveState, so the MobState/ProjectileState passes
            // below have a slot 0 to bump/restore/re-assert against — the
            // PlayerState/MatchStats passes above needed no such fixture (the
            // player always exists).
            w.SpawnMobForTest(MobType.Chaser, new float2(5f, 0f));
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(1f, 0f), new float2(1f, 0f),
                1f, 0f, 10f, 0.1f, 1f);
            w.TickAll(new SimInput[PlayerCount]);
            WorldSave save = w.SaveState();
            ulong baseline = w.StateHash();
            for (int index = 0; index < PlayerCount; index++)
            {
                foreach (var field in typeof(PlayerState).GetFields())
                {
                    w.RestoreState(save);
                    object boxed = w.PlayerAt(index);
                    field.SetValue(boxed, Bump(field.GetValue(boxed)));
                    w.SetPlayerForTest(index, (PlayerState)boxed);
                    Assert.AreNotEqual(baseline, w.StateHash(),
                        $"PlayerState[{index}].{field.Name} не в хеше");
                }
                foreach (var field in typeof(MatchStats).GetFields())
                {
                    w.RestoreState(save);
                    object boxed = w.StatsAt(index);
                    field.SetValue(boxed, Bump(field.GetValue(boxed)));
                    w.SetStatsForTest(index, (MatchStats)boxed);
                    Assert.AreNotEqual(baseline, w.StateHash(),
                        $"MatchStats[{index}].{field.Name} не в хеше");
                }
            }
            // Stage 2 Task 10: WorldStats is hashed by its own HashWorldStats at
            // its own canonical position (right after the wave, before the stats
            // array) instead of riding inside HashStats as it did in Task 5 —
            // so it needs a pass of its own here, same bump/restore/re-assert
            // shape as the per-player passes above.
            foreach (var field in typeof(WorldStats).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.WorldStats;
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetWorldStatsForTest((WorldStats)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"WorldStats.{field.Name} не в хеше");
            }
            // F-4 fix-round: the three passes the old comment here said were
            // deferred to Task 16/22 — SetMobForTest/SetProjectileForTest/
            // SetWaveForTest now exist (SimulationWorld.cs), completing coverage
            // from two passes to all five. T5 fix-round 1 M-1: the tally below
            // was internally inconsistent (components didn't sum to the stated
            // total) — recounted by actual typeof(X).GetFields() count, not
            // restated from memory. Stage 2 Task 10 recount: PlayerState 24 (the
            // two edge-request counters are new) x 2 players + MatchStats 8 x 2
            // players + WorldStats 3 + MobState 9 + ProjectileState 12 +
            // WaveState 6 = 94 asserted bumps. The loops below reflect over the
            // live structs, so a new field is covered the moment it is declared;
            // this tally is a receipt for the reader, not a bound the test enforces.
            foreach (var field in typeof(MobState).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.Mobs[0];
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetMobForTest(0, (MobState)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"MobState.{field.Name} не в хеше");
            }
            foreach (var field in typeof(ProjectileState).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.Projectiles[0];
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetProjectileForTest(0, (ProjectileState)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"ProjectileState.{field.Name} не в хеше");
            }
            foreach (var field in typeof(WaveState).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.WaveRef; // ref-return read: an ordinary value copy here
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetWaveForTest((WaveState)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"WaveState.{field.Name} не в хеше");
            }
        }

        static object Bump(object v) => v switch
        {
            float f => f + 1f,
            int i => i + 1,
            bool b => !b,
            float2 f2 => f2 + new float2(1f, 0f),
            // Stage 2 Task 7: ProjectileState.OwnerIndex is the first byte
            // field the sweep reflects over — permanent (not part of the
            // temporary skip-list above), since a byte field is a legitimate
            // struct member the hash sweep must be able to bump regardless of
            // whether any ONE such field is currently in PendingHashFields.
            // Wraps at byte.MaxValue back to 0 — still different from the
            // input on every value, (byte)(255 + 1) == 0 != 255 included —
            // which is all callers need (they only check the value changed,
            // never a specific new one).
            byte b8 => (byte)(b8 + 1),
            // MobType/MobAiState/WavePhase (F-4 fix-round): step to the next
            // declared enum value, wrapping — every one of these enums has more
            // than one member, so the wrapped value is always different from the
            // original, which is all Bump's callers need (they only check the
            // hash changed, never a specific new value).
            System.Enum e => BumpEnum(e),
            _ => throw new System.NotSupportedException(v.GetType().Name)
        };

        static object BumpEnum(System.Enum e)
        {
            System.Array values = System.Enum.GetValues(e.GetType());
            int index = System.Array.IndexOf(values, e);
            return values.GetValue((index + 1) % values.Length);
        }

        [Test]
        public void Snapshot_CopiesPlayerAndCounts()
        {
            var cfg = TestConfigs.Default();
            var w = new SimulationWorld(5, cfg);
            w.Tick(default);
            var snap = new RenderSnapshot(cfg.Arena);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(w.CurrentTick, snap.Tick);
            Assert.AreEqual(w.Player.Pos, snap.Player.Pos);
            Assert.AreEqual(0, snap.MobCount);
        }
    }
}
