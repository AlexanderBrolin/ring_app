using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WorldLifecycleTests
    {
        // Stage 2 Task 10 (history): the PREVIOUS PendingHashFields skip-list
        // (T7 -> T10, ProjectileState.OwnerIndex) was removed once that field
        // joined the hash — removal proven, not assumed, by pulling it back
        // out and watching the sweep name it (task-10-report.md).
        //
        // Stage 3 Task 1 (errata E-1/D-I1): a NEW, unrelated skip-list, same
        // precedent and same discipline. Every field this task declares as
        // inert Ф1 economy state joins the canonical hash at the sanctioned
        // re-pin #1 (Т6) rather than immediately — otherwise the reflective
        // sweep below would fault on a field the state hash deliberately
        // excludes for five more tasks (errata E-1's "structural rebuild").
        // TEMPORARY (T1 -> T6): enters the canonical hash at the sanctioned
        // re-pin. Т6 removes this set UNCONDITIONALLY and proves the removal
        // the same way Т10 proved its own (pull one field back OUT of the
        // hash, one at a time, watch the sweep name it by name).
        static readonly System.Collections.Generic.HashSet<string> PendingHashFields = new()
        {
            // PlayerState (Task 1 Interfaces).
            "Extracted", "ExtractKind", "LootTimer", "RepairTimer", "ExtractTimer",
            "LootTargetContainerId", "LootTargetSlot",
            // PlayerState (Task 2 Interfaces): the ammo counter — hashable
            // behavior, but its own sanctioned entry point is still Т6, same
            // as every other field in this set.
            "Ammo",
            // MatchStats (Task 1 Interfaces, errata R-13 — NOT SurvivedSeconds,
            // which belongs to MatchSummary, Task 24, not to a hashed counter).
            "AmmoSpent", "CellsPicked",
            // WorldStats (Task 1 Interfaces).
            "PickupSpawnsSkipped", "ContainerSpawnsSkipped",
            // ProjectileState (Stage 3 Task 5 Interfaces, brief's own warning:
            // the reflective sweep below reaches ProjectileState too, unlike
            // the PlayerState/MatchStats/WorldStats-only precedent above — see
            // that loop's own PendingHashFields check, added alongside this
            // entry). Adressat Т6, same discipline as every field above.
            "OwnerEntityId",
        };

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
                    if (PendingHashFields.Contains(field.Name))
                    {
                        // TEMPORARY (T1 -> T6): a POSITIVE assertion, not a
                        // silent skip — proves the field is genuinely still
                        // OUTSIDE the hash, not just unchecked (same fix-round
                        // 1 M-4 discipline Т7/Т10 established).
                        Assert.AreEqual(baseline, w.StateHash(),
                            $"PlayerState[{index}].{field.Name} ещё не должен входить в хеш до Т6");
                        continue;
                    }
                    Assert.AreNotEqual(baseline, w.StateHash(),
                        $"PlayerState[{index}].{field.Name} не в хеше");
                }
                foreach (var field in typeof(MatchStats).GetFields())
                {
                    w.RestoreState(save);
                    object boxed = w.StatsAt(index);
                    field.SetValue(boxed, Bump(field.GetValue(boxed)));
                    w.SetStatsForTest(index, (MatchStats)boxed);
                    if (PendingHashFields.Contains(field.Name))
                    {
                        Assert.AreEqual(baseline, w.StateHash(),
                            $"MatchStats[{index}].{field.Name} ещё не должен входить в хеш до Т6");
                        continue;
                    }
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
                if (PendingHashFields.Contains(field.Name))
                {
                    Assert.AreEqual(baseline, w.StateHash(),
                        $"WorldStats.{field.Name} ещё не должен входить в хеш до Т6");
                    continue;
                }
                Assert.AreNotEqual(baseline, w.StateHash(), $"WorldStats.{field.Name} не в хеше");
            }
            // F-4 fix-round: the three passes the old comment here said were
            // deferred to Task 16/22 — SetMobForTest/SetProjectileForTest/
            // SetWaveForTest now exist (SimulationWorld.cs), completing coverage
            // from two passes to all five. T5 fix-round 1 M-1: the tally below
            // was internally inconsistent (components didn't sum to the stated
            // total) — recounted by actual typeof(X).GetFields() count, not
            // restated from memory.
            //
            // Stage 3 Task 5 fix-round 1 (coordinator finding): the PREVIOUS
            // version of this tally (Stage 3 Task 1) claimed PlayerState had
            // 31 fields with 7 PENDING — a stale count, not a live one: a
            // fresh `typeof(PlayerState).GetFields()` count (same discipline
            // this paragraph already asks for) gives **32** fields, because
            // Task 2's own `Ammo` (also PENDING, per this file's own
            // `PendingHashFields` set two screens up) was never folded into
            // this RECEIPT when Task 2 added it to the SET — only the set
            // drifted out of sync with its own tally comment, never the test
            // itself (the loops below read `PendingHashFields`/
            // `GetFields()` directly, not this paragraph's arithmetic).
            // Recounted whole, not incremented from the old number, per the
            // coordinator's own instruction: PlayerState 32 (Extracted,
            // ExtractKind, LootTimer, RepairTimer, ExtractTimer,
            // LootTargetContainerId, LootTargetSlot, Ammo — 8 PENDING) x 2
            // players + MatchStats 10 (AmmoSpent, CellsPicked — 2 PENDING)
            // x 2 players + WorldStats 5 (PickupSpawnsSkipped,
            // ContainerSpawnsSkipped — 2 PENDING) + MobState 9 +
            // ProjectileState 13 (OwnerEntityId, Stage 3 Task 5 — 1 PENDING)
            // + WaveState 6 = **117** bumps swept: **94** asserted NOT to
            // equal baseline (unchanged throughout — every field already in
            // the hash stays in it, no PENDING field was ever counted here)
            // and **23** asserted TO equal baseline (13 distinct PENDING
            // field names, weighted by their per-player multiplicity:
            // 8 x 2 + 2 x 2 + 2 x 1 + 1 x 1 = 23) until Т6's re-pin flips
            // them over. The loops below reflect over the live structs, so a
            // new field is covered the moment it is declared; this tally is
            // a receipt for the reader, not a bound the test enforces.
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
                if (PendingHashFields.Contains(field.Name))
                {
                    // TEMPORARY (T5 -> T6), same discipline as the
                    // PlayerState/MatchStats/WorldStats passes above:
                    // ProjectileState.OwnerEntityId is declared, inert, and
                    // this is the FIRST pass over ProjectileState/MobState/
                    // WaveState to need the PendingHashFields gate at all.
                    Assert.AreEqual(baseline, w.StateHash(),
                        $"ProjectileState.{field.Name} ещё не должен входить в хеш до Т6");
                    continue;
                }
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
            // field the sweep reflects over. The temporary PendingHashFields
            // skip-list that once excluded it is gone (Stage 2 Task 10 — see
            // the file header note above); a byte field is simply a
            // legitimate struct member the hash sweep must be able to bump
            // like any other. Wraps at byte.MaxValue back to 0 — still
            // different from the input on every value, (byte)(255 + 1) == 0
            // != 255 included —
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
