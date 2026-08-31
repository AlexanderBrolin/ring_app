using NUnit.Framework;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class WorldLifecycleTests
    {
        // NO SKIP-LIST LIVES HERE, AND TWICE NOW IT HAS BEEN REMOVED RATHER
        // THAN OUTGROWN (history, kept because the pattern recurs every time
        // a phase declares state ahead of its sanctioned golden re-pin):
        //
        // - Stage 2 T7 -> T10 held ProjectileState.OwnerIndex out of the hash
        //   until Task 10's re-pin, then dropped the set — the removal proven,
        //   not assumed, by pulling the field back out of the hash and
        //   watching the sweep name it (task-10-report.md).
        // - Stage 3 T1/T2/T5 -> Т6 held thirteen field names out the same way
        //   (errata E-1's "structural rebuild": every hashable field of the
        //   extraction economy is DECLARED in Ф1 so that all of them enter the
        //   digest at ONE sanctioned re-pin instead of moving the golden once
        //   per task). Т6 is that re-pin, so the set is gone UNCONDITIONALLY —
        //   not shrunk field by field — and its removal is proven the same
        //   way: task-6-report.md records three fields (PlayerState.Ammo,
        //   MatchState.Phase, the backpacks) pulled back out of StateHash one
        //   at a time, each time watching this file name the missing field.
        //
        // Coordinator fix-round (Ф3 review A-9/m4): this used to point at
        // "the other PendingHashFields in the suite — SimConfigHashTests'
        // own — a DIFFERENT set with a different addressee (Т13)" — Т13
        // shipped and removed that skip-set entirely (SimConfigHashTests'
        // own twelve sectional sweeps replaced it, no skip-list left
        // anywhere in the suite). Historical note only, kept so a reader
        // following an old cross-reference lands somewhere true.

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
            // Т6: one live pickup, for the PickupState pass this task adds
            // (the debt Т3 recorded — a hashed struct with no completeness
            // guard is a field waiting to go missing quietly). AT THE ARENA
            // CENTER — deliberately, not incidentally: both players of a
            // two-player world spawn on the ring (Geometry.SpawnPosFor), tens
            // of meters away, so PickupSystem's own auto-collect radius
            // (Hero.PickupRadius, 2 m in TestConfigs) cannot reach it during
            // the tick below and delete the fixture this pass depends on.
            w.SpawnPickup(PickupKind.EnergyCell, float2.zero, 3);
            // Т14: one live container, for the ContainerState pass this task
            // adds — same "hashed struct needs a completeness guard the
            // moment it enters the digest" reasoning as the pickup above.
            // Crate (permanent Ttl, coordinator R-100/ContainerStore's own
            // InitialTtlFor) rather than Ground: the fixture must survive
            // the TickAll below regardless of how many ticks this test ever
            // grows to run, without depending on a TTL race the way a
            // decaying kind would.
            w.SpawnContainer(ContainerKind.Crate, float2.zero, new byte[] { 5 });
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
            // restated from memory.
            //
            // Т6, RECOUNTED WHOLE (same discipline the Stage 3 Task 5
            // fix-round imposed after this receipt was found running one field
            // behind its own set — it is re-derived here from a fresh
            // typeof(X).GetFields() reading of each struct, never incremented
            // from the previous number):
            //
            //   PlayerState 34 x 2 players = 68
            //   MatchStats 10 x 2 players  = 20
            //   WaveState 7 x 3 zones      = 21
            //   WorldStats 5, MobState 12, ProjectileState 14, PickupState 5,
            //   MatchState 2, ContainerState 5 = 43
            //   -> 152 bumps swept, ALL asserted NOT to equal baseline.
            //
            // Stage 3 Task 11 (coordinator R-50/R-51): WaveState grew from
            // 6 fields to 13 (two named Pending counters -> nine, one per
            // zone x archetype pair) -- recounted from a fresh
            // typeof(WaveState).GetFields() reading, not incremented from
            // the previous 124 (the discipline this comment's own header
            // names as the point of this receipt, and the exact mistake a
            // prior fix-round already caught once).
            //
            // Stage 3 Task 14: ContainerState is new (Id/Pos/Kind/SlotCount/
            // Ttl, five fields, same shape as PickupState) -- 131 -> 136,
            // recounted the same way, not incremented from memory.
            //
            // Wave-cadence-per-zone (bd app-ggvz Т1): MobState grew from 9
            // fields to 10 -- SpawnZone, the ring a mob was PUT INTO by
            // whoever spawned it (spec §3.5, SimStates.cs' own field doc) --
            // recounted the same way, from a fresh typeof(MobState).
            // GetFields() reading, not incremented from memory -- 136 -> 137.
            //
            // Wave-cadence-per-zone (bd app-ggvz Т3): WaveState SHRANK from 13
            // fields to 7 (the zone left the nine Pending field NAMES and
            // became the index of the instance) and the world now holds THREE
            // of them, so the wave line goes 13 x 1 -> 7 x 3 = 21 and the
            // whole tally is re-derived, again from fresh typeof(X).
            // GetFields() readings of every struct rather than adjusted from
            // 137 -- 137 -> 145. The wave pass below is nested in a per-ZONE
            // loop for the same reason it is counted per ring: a HashWave
            // truncated to waves[0] passes a sweep that only ever bumps
            // waves[0], and that is precisely the mutation this file is here
            // to kill.
            //
            // app-88jb Т5: MobState grew from 10 fields to 12 -- Tilt and
            // TiltVel, the body's tilt spring and its angular velocity (spec
            // §3.2, SimStates.cs' own field doc) -- and the tally is re-derived
            // once more from fresh typeof(X).GetFields() readings of ALL NINE
            // structs rather than adjusted from 145. Every other count came
            // back unchanged, so only the mob line and the two sums move:
            // 145 -> 147. The mob is counted ONCE, unlike PlayerState and
            // MatchStats, and the reason is in the loop rather than in the
            // fixture -- the pass below bumps w.Mobs[0] and nothing else,
            // where those two are wrapped in a per-index loop (see their own
            // "x 2 players" lines above).
            //
            // app-88jb Т7: PlayerState grew from 32 fields to 34 -- Tilt and
            // TiltVel again, this time the COLLECTOR's own spring (spec §3.2,
            // SimStates.cs' own field doc), folded into HashPlayer at the end
            // of the struct exactly as Т5 folded the mob's. The tally is
            // re-derived from fresh typeof(X).GetFields() readings of all nine
            // structs one more time rather than adjusted from 147; every other
            // count came back unchanged, so only the player line and the two
            // sums move -- and the player line moves by FOUR, not two, because
            // it carries the "x 2 players" multiplier the mob line does not:
            // 147 -> 151.
            // app-88jb Т19: ProjectileState grew from 13 fields to 14 --
            // Ricochets, how many times a round has already reflected off
            // static geometry (spec §3.4, SimStates.cs' own field doc) --
            // folded into HashProjectile at the END, mirroring the end of the
            // struct exactly as Т5 folded the mob's tilt pair. The tally is
            // re-derived one more time from fresh typeof(X).GetFields()
            // readings of ALL NINE structs rather than adjusted from 151, and
            // every other count came back unchanged (PlayerState 34,
            // MatchStats 10, WaveState 7, WorldStats 5, MobState 12,
            // PickupState 5, MatchState 2, ContainerState 5), so only the
            // projectile line and the two sums move: 151 -> 152. The round is
            // counted ONCE, like the mob and unlike PlayerState/MatchStats:
            // the pass below bumps w.Projectiles[0] and nothing else.
            //
            // app-88jb Т24: HistorySlot -- the body's row in the rewind ring
            // (spec §3.6, SimStates.cs' own field docs) -- joins BOTH
            // MobState and PlayerState, folded into HashMob beside SpawnZone
            // and into HashPlayer last. Re-derived one more time from fresh
            // typeof(X).GetFields() readings of ALL NINE structs, and this
            // time the recount did NOT come back agreeing with the receipt:
            //
            // ⚠ THE 152 ABOVE WAS ALREADY WRONG BY TWO. PlayerState carries
            // 35 fields, not 34: app-88jb Т22 added SlideSpeedPenalty (the
            // collision tax on a slide, owner decision Р443, SimStates.cs) and
            // never touched this receipt, so its history simply stops at Т19.
            // The player line carries the "x 2 players" multiplier, so one
            // unrecorded field cost two bumps: the true count BEFORE this task
            // was 154. Said out loud rather than quietly folded into the new
            // number, because a reader who trusts 152 inherits the same error
            // a fourth time -- and because the receipt exists precisely to be
            // recounted, not to be believed.
            //
            // From that corrected base, this task moves two lines:
            //   MobState    12 -> 13, counted ONCE       (+1)
            //   PlayerState 35 -> 36, counted x 2 players (+2)
            // Every other count came back unchanged (MatchStats 10,
            // WaveState 7 x 3 zones, WorldStats 5, ProjectileState 14,
            // PickupState 5, MatchState 2, ContainerState 5), so:
            //   36 x 2 = 72, 10 x 2 = 20, 7 x 3 = 21,
            //   5 + 13 + 14 + 5 + 2 + 5 = 44          -> 154 -> 157.
            //
            // app-88jb Т28: ProjectileState grew from 14 fields to 15 --
            // RewindLeft, how many more steps of this round are asked of the
            // PAST (spec §3.6, SimStates.cs' own field doc) -- folded into
            // HashProjectile at the END, mirroring the end of the struct,
            // exactly as Т19 folded Ricochets. Re-derived once more from fresh
            // typeof(X).GetFields() readings of ALL NINE structs rather than
            // adjusted from 157, and this time the recount came back agreeing
            // with the receipt in every other line: PlayerState 36,
            // MatchStats 10, WaveState 7, WorldStats 5, MobState 13,
            // PickupState 5, MatchState 2, ContainerState 5. WaveState's count
            // is SEVEN and not eight on purpose -- PendingTotal is an
            // expression-bodied property and GetFields() does not see it, which
            // is the very outcome its own doc says it was written that way for.
            // So only the projectile line and the two sums move, and the round
            // is counted ONCE (like the mob, unlike PlayerState/MatchStats --
            // the pass below bumps w.Projectiles[0] and nothing else):
            //   36 x 2 = 72, 10 x 2 = 20, 7 x 3 = 21,
            //   5 + 13 + 15 + 5 + 2 + 5 = 45          -> 157 -> 158.
            //
            // AND, AS AT Т7, THE RECEIPT IS NOT WHAT MOVES THIS TEST -- SAID OF
            // Т24, whose paragraph it closes. (Т28's own paragraph was inserted
            // above it and left this one reading as if it described Т28, which
            // added ONE field and ONE fold, not two: review finding, Т28
            // fix-round. The subject is named here rather than the paragraphs
            // reordered, because the order they were written in is what the
            // numbers step through.) Т24's TWO new fields turned this test red
            // through the reflective sweep below, and only its two folds
            // (HashMob/HashPlayer) turned it green again; editing these numbers
            // would have changed nothing executable in either direction. The
            // same holds of Т28, at one field and one fold.
            // ⚠ THE RECEIPT IS NOT WHAT MAKES THIS TEST PASS OR FAIL, and Т7
            // is where that was measured rather than assumed (coordinator
            // errata 12): the two new fields turned this test red through the
            // reflective sweep above, and only the HashPlayer fold turned it
            // green again. Editing these numbers changes nothing executable --
            // they are for the reader, as the closing line below already says.
            //
            // ZERO asserted TO equal it: the thirteen PENDING names are gone
            // with the skip-list (see this file's header), which is the whole
            // of what Т6 did to this test besides growing it two passes —
            // PickupState (the debt Т3 recorded) and MatchState (the same
            // hole, one struct over: both are top-level hashed state that
            // until now had no completeness guard at all, so the next field
            // appended to either would have joined the struct without joining
            // the digest, silently).
            //
            // The loops below reflect over the live structs, so a new field is
            // covered the moment it is declared; this tally is a receipt for
            // the reader, not a bound the test enforces.
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
            // Т6 (the debt Т3 recorded, task-3-report.md §7.1): PickupState's
            // own pass. Т3 deliberately added NO fictitious names to the
            // skip-list for it — a name in a set no loop reads would have been
            // an imitation of the discipline — and left the real obligation
            // here instead: the array joins the hash in this task, so it gets
            // the completeness guard in this task.
            foreach (var field in typeof(PickupState).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.Pickups[0];
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetPickupForTest(0, (PickupState)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"PickupState.{field.Name} не в хеше");
            }
            // Т14: ContainerState's own pass, same reasoning as PickupState
            // above (Т6's own precedent) — the array joins the hash in this
            // task, so it gets the completeness guard in this task, via
            // SetContainerForTest (this task's own seam).
            foreach (var field in typeof(ContainerState).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.Containers[0];
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetContainerForTest(0, (ContainerState)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"ContainerState.{field.Name} не в хеше");
            }
            // bd app-ggvz Т3: EVERY RING, not just the first. StateHash folds
            // three wave states now, and a digest that walked only waves[0]
            // would satisfy a single-ring sweep completely while dropping two
            // thirds of the wave state out of the hash — the same "a loop
            // silently truncated to index 0 passes every single-player check
            // ever written" argument the backpack pass below states, one
            // struct over.
            for (int z = 0; z < Zones.Count; z++)
            {
                var zone = (Zone)z;
                foreach (var field in typeof(WaveState).GetFields())
                {
                    w.RestoreState(save);
                    // ref-return read: an ordinary value copy here
                    object boxed = w.WaveRef(zone);
                    field.SetValue(boxed, Bump(field.GetValue(boxed)));
                    w.SetWaveForTest(zone, (WaveState)boxed);
                    Assert.AreNotEqual(baseline, w.StateHash(),
                        $"WaveState[{zone}].{field.Name} не в хеше");
                }
            }
            // Т6: MatchState's own pass, on the same reasoning as PickupState
            // above — Т1 declared the struct with no hash pass because it was
            // outside the hash entirely; from this task it is inside, so it
            // needs the guard. SetMatchForTest (Т1's seam) finally has a
            // caller.
            foreach (var field in typeof(MatchState).GetFields())
            {
                w.RestoreState(save);
                object boxed = w.Match;
                field.SetValue(boxed, Bump(field.GetValue(boxed)));
                w.SetMatchForTest((MatchState)boxed);
                Assert.AreNotEqual(baseline, w.StateHash(), $"MatchState.{field.Name} не в хеше");
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
            // MobType/MobAiState/WavePhase/PickupKind/MatchPhase/Zone: step the
            // enum's UNDERLYING value by one — see BumpEnum's own doc for why
            // it no longer walks the declared-member list.
            System.Enum e => BumpEnum(e),
            _ => throw new System.NotSupportedException(v.GetType().Name)
        };

        /// Т6: this used to step to the NEXT DECLARED member, wrapping, on the
        /// stated grounds that "every one of these enums has more than one
        /// member". PickupKind broke that premise the moment its struct
        /// entered the hash: `enum PickupKind : byte { EnergyCell = 0 }` has
        /// exactly one member, so wrapping the declared list handed back the
        /// SAME value — a no-op bump, under an Assert.AreNotEqual that would
        /// then fail while blaming HashPickup for a field it hashes perfectly
        /// well. Stepping the underlying value instead is both correct for
        /// every multi-member enum (identical result: they are all dense from
        /// zero) and honest for the single-member one: an undeclared byte is a
        /// value the struct can physically hold and the digest must react to,
        /// and the day PickupKind.Data lands (spec §3.6) this bump becomes a
        /// declared value again with no edit here.
        ///
        /// The +1 cannot overflow anything the sweep produces: every enum
        /// field it reflects over is read out of a freshly restored world, so
        /// the value is whatever the fixture set (0..5 today), never the
        /// underlying type's maximum.
        static object BumpEnum(System.Enum e)
            => System.Enum.ToObject(e.GetType(), System.Convert.ToInt64(e) + 1L);

        /// Т6. The backpacks are the one piece of canonical state the
        /// reflective sweep above CANNOT reach: Inventory is a class with
        /// private fields (spec Р232 — it owns a byte array, and living
        /// inside PlayerState would make every wholesale PlayerState copy
        /// allocate), so `typeof(PlayerState).GetFields()` never sees it and
        /// no bump/restore pass can be written for it. This is that pass,
        /// written by hand: the four things about a backpack that are state
        /// (that it has contents at all, WHOSE it is, WHICH item, HOW MANY)
        /// and the round trip through a save.
        [Test]
        public void Backpack_IsHashedPerPlayer_AndRestoredWithTheSave()
        {
            const int PlayerCount = 2;
            var w = new SimulationWorld(11, TestConfigs.Default(), PlayerCount);
            WorldSave save = w.SaveState();
            ulong empty = w.StateHash();

            w.SetInventoryForTest(0, (byte)3);
            ulong carried = w.StateHash();
            Assert.AreNotEqual(empty, carried, "рюкзак игрока 0 не в хеше");

            // WHOSE backpack it is. The canonical order walks
            // inventories[0..n), so the same item in another player's hands
            // must reach a different digest — the same argument that made
            // Stage 2 Task 10 hash the whole players array instead of
            // player 0 (a loop silently truncated to index 0 passes every
            // single-player check ever written).
            w.RestoreState(save);
            w.SetInventoryForTest(1, (byte)3);
            Assert.AreNotEqual(empty, w.StateHash(), "рюкзак игрока 1 не в хеше");
            Assert.AreNotEqual(carried, w.StateHash(),
                "хеш не различает, КТО несёт предмет — проход по inventories[0..n) свёрнут");

            // WHICH item.
            w.RestoreState(save);
            w.SetInventoryForTest(0, (byte)4);
            Assert.AreNotEqual(carried, w.StateHash(), "id предмета не в хеше");

            // HOW MANY. Two of the same item must differ from one of it —
            // this is the assertion the leading count step in HashInventory
            // exists for.
            w.RestoreState(save);
            w.SetInventoryForTest(0, (byte)3, (byte)3);
            Assert.AreNotEqual(carried, w.StateHash(), "число предметов в рюкзаке не в хеше");

            w.RestoreState(save);
            Assert.AreEqual(empty, w.StateHash(), "RestoreState не откатывает рюкзаки");
        }

        /// Т6. The other half of the backpack's hash contract, and the reason
        /// HashInventory stops at Count instead of walking the whole
        /// MaxInventoryItems array: Inventory.TryRemoveAt is a SWAP-remove, so
        /// it leaves the vacated tail slot holding a copy of the item that
        /// moved down. Two worlds carrying literally the same items must agree
        /// on the digest no matter which route they took to get there —
        /// otherwise a server and a replay that reached the same backpack by
        /// different remove orders would report a desync that does not exist.
        [Test]
        public void BackpackHash_IgnoresSwapRemoveDebrisPastTheCount()
        {
            var viaEdits = new SimulationWorld(11, TestConfigs.Default());
            Assert.IsTrue(viaEdits.TryAddItem(0, 1), "фикстура: первый предмет обязан влезть");
            Assert.IsTrue(viaEdits.TryAddItem(0, 2), "фикстура: второй предмет обязан влезть");
            Assert.IsTrue(viaEdits.TryRemoveItemAt(0, 0, out byte removed));
            Assert.AreEqual(1, removed);

            // Same contents, never any debris behind the count.
            var direct = new SimulationWorld(11, TestConfigs.Default());
            direct.SetInventoryForTest(0, (byte)2);

            // Premise first: the two backpacks really are the same backpack.
            Assert.AreEqual(direct.InventoryCountOf(0), viaEdits.InventoryCountOf(0));
            Assert.AreEqual(direct.InventoryItemAt(0, 0), viaEdits.InventoryItemAt(0, 0));

            Assert.AreEqual(direct.StateHash(), viaEdits.StateHash(),
                "хеш рюкзака обязан читать только несомое (Count), а не хвост после swap-remove");
        }

        /// Т6 (spec Р230). The third RNG stream enters the hash and the save
        /// here, three tasks before its own consumer (Т15, container layout).
        /// Both halves are load-bearing on their own: a stream outside the
        /// hash lets two worlds that have drawn different numbers of loot
        /// samples claim the same digest, and a stream outside the save lets a
        /// restored world keep drawing from where the LIVE run left off — a
        /// replay divergence that would first appear as "the containers moved"
        /// long after this task is history.
        [Test]
        public void LootRng_IsItsOwnStream_HashedAndSaved()
        {
            var w = new SimulationWorld(23, TestConfigs.Default());

            // Р230's own premise, and the way it breaks in practice: a
            // copy-pasted fold constant. A third stream seeded identically to
            // either of the other two draws the same numbers in the same
            // order, which re-couples container placement to exactly the
            // sequence it was split off from.
            Assert.AreNotEqual(w.SpreadRng.state, w.LootRng.state,
                "поток лута совпал со спредом — свёрнут той же константой");
            Assert.AreNotEqual(w.WaveRng.state, w.LootRng.state,
                "поток лута совпал с волнами — ровно та связка, ради разрыва которой он заведён");

            WorldSave save = w.SaveState();
            ulong baseline = w.StateHash();

            // Т15's placement search does this for real; here the seam is its
            // own witness, since nothing else draws from the stream yet.
            w.LootRng.NextFloat();
            Assert.AreNotEqual(baseline, w.StateHash(), "состояние потока лута не в хеше");

            w.RestoreState(save);
            Assert.AreEqual(baseline, w.StateHash(), "RestoreState не откатывает поток лута");
        }

        /// Т14. The reflective sweep above proves ContainerState's own
        /// STRUCT fields are hashed; it cannot reach the flat
        /// `_containerSlots` byte array (no `typeof` reflects over it —
        /// it isn't a field of ContainerState, same reason Inventory's
        /// content needed Backpack_IsHashedPerPlayer_AndRestoredWithTheSave
        /// above rather than relying on the reflective sweep alone). This
        /// is that proof for containers: taking an item changes NO struct
        /// field (Id/Pos/Kind/SlotCount/Ttl all stay put — only the byte at
        /// that slot position goes from 5 to 0), so a digest that moved
        /// only from this take is a digest that reads slot CONTENT, not
        /// just the struct around it. The second half proves the save/
        /// restore round-trip covers the same content — Containers AND
        /// ContainerSlots both roll back together.
        [Test]
        public void ContainerState_IsHashedAndRestoredWithTheSave()
        {
            var cfg = TestConfigs.Open();
            var w = new SimulationWorld(5, cfg);
            int id = w.SpawnContainer(ContainerKind.Crate, new float2(3f, 0f), new byte[] { 5, 9 });
            Assert.AreNotEqual(-1, id, "premise: the container must actually exist");
            WorldSave save = w.SaveState();
            ulong baseline = w.StateHash();

            Assert.IsTrue(w.TryTakeFromContainer(id, 0, out byte taken));
            Assert.AreEqual(5, taken, "premise: the take must remove the FIRST item, not the second");
            Assert.AreNotEqual(baseline, w.StateHash(),
                "taking an item must change the digest — slot content isn't hashed");

            w.RestoreState(save);
            Assert.AreEqual(baseline, w.StateHash(),
                "RestoreState must roll back both the container and its slot content");
        }

        /// Т6: the render frame's half of the new state. `CopyFrom`'s own
        /// reflective guard (InterpolationBufferTests) proves a frame copies
        /// every public field it has; nothing proved that CaptureSnapshot
        /// FILLS the two new ones, and a forgotten line there is invisible to
        /// that guard.
        [Test]
        public void Snapshot_CopiesPickupsAndMatchState()
        {
            var cfg = TestConfigs.Default();
            var w = new SimulationWorld(5, cfg);
            w.SpawnPickup(PickupKind.EnergyCell, new float2(30f, 0f), 4);
            w.SetMatchForTest(new MatchState { Phase = MatchPhase.GateOpen, DirectorDeathTick = 17 });
            var snap = new RenderSnapshot(cfg);

            w.CaptureSnapshot(snap);

            Assert.AreEqual(1, snap.PickupCount);
            Assert.AreEqual(4, snap.Pickups[0].Amount);
            Assert.AreEqual(PickupKind.EnergyCell, snap.Pickups[0].Kind);
            Assert.AreEqual(MatchPhase.GateOpen, snap.Match.Phase);
            Assert.AreEqual(17, snap.Match.DirectorDeathTick);
        }

        /// Т14, same reasoning as Snapshot_CopiesPickupsAndMatchState above:
        /// InterpolationBufferTests' reflective guard proves CopyFrom copies
        /// every RenderSnapshot field it has — it says nothing about
        /// whether CaptureSnapshot FILLS this one from the live world, and a
        /// forgotten line there is invisible to that guard.
        [Test]
        public void Snapshot_CopiesContainers()
        {
            var cfg = TestConfigs.Default();
            var w = new SimulationWorld(5, cfg);
            w.SpawnContainer(ContainerKind.Crate, new float2(30f, 0f), new byte[] { 4 });
            Assert.AreEqual(1, w.ContainerCount, "premise: the container must actually exist");
            var snap = new RenderSnapshot(cfg);

            w.CaptureSnapshot(snap);

            Assert.AreEqual(1, snap.ContainerCount);
            Assert.AreEqual(ContainerKind.Crate, snap.Containers[0].Kind);
            Assert.AreEqual(new float2(30f, 0f), snap.Containers[0].Pos);
        }

        [Test]
        public void Snapshot_CopiesPlayerAndCounts()
        {
            var cfg = TestConfigs.Default();
            var w = new SimulationWorld(5, cfg);
            w.Tick(default);
            var snap = new RenderSnapshot(cfg);
            w.CaptureSnapshot(snap);
            Assert.AreEqual(w.CurrentTick, snap.Tick);
            Assert.AreEqual(w.Player.Pos, snap.Player.Pos);
            Assert.AreEqual(0, snap.MobCount);
        }
    }
}
