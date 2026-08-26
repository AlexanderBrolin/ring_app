using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Ring.Networking;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    public class SimConfigHashTests
    {
        // Stage 3 Task 13 (coordinator R-93): SPLIT by section — was one
        // monolithic EveryConfigNumberAffectsHash (spec §3.8/§3.15, Р52 —
        // flagman). A single reflection sweep over all twelve sections
        // stops at the FIRST mismatch (Assert.AreNotEqual throws), which is
        // a genuine COVERAGE gap, not a rerun inconvenience — the exact
        // class of finding R-41 (Т8, ZoneOf_OnExactBoundary) and F1 (Т11,
        // SpawnRing_OfZone) already cost this project twice: a monolith
        // hides every OTHER gap behind whichever one happens to sort first,
        // and it collapses "remove one Add call" mutations across five
        // different sections into a single shared victim, where R-25's own
        // exact-set-equality criterion needs FIVE independent ones. Same
        // reflection-sweep SHAPE as WorldLifecycleTests.
        // EveryPlayerAndStatsFieldAffectsHash (:44-169) in every method
        // below — bump one field of a freshly-built fixture, recompute,
        // assert the hash moved, name the exact path on failure. Each
        // section's own call also asserts WHICH array-typed fields it
        // skipped: the sweep cannot bump an array in place, so it hands
        // them to the element-wise helpers alongside it — a new array field
        // added to a section later would otherwise be skipped SILENTLY by
        // both, hashed by nothing and caught by nothing (fix-round finding
        // of the coordinator; the field-count guard below only watches the
        // top level).
        // app-88jb Т13 (coordinator Ruling 59): `Parts` is skip-listed for the
        // scalar sweep and covered instead by AssertHitPartArrayFieldAffects
        // Hash below, which bumps all FIVE fields of every part plus the
        // length — the same split every other array in this file already uses.
        // The name must be in the skip-set from the moment the field exists,
        // or the sweep dies on a NotSupportedException instead of asserting
        // anything (a raised exception is not a RED). Five bodies carry one:
        // Hero, Chaser, Gunner, Elite, Director.
        [Test]
        public void EveryConfigNumberAffectsHash_Hero()
        {
            AssertSectionAffectsHash("Hero", "Parts");
            AssertHitPartArrayFieldAffectsHash("Hero", "Parts");
        }

        [Test]
        public void EveryConfigNumberAffectsHash_Weapon() => AssertSectionAffectsHash("Weapon");

        [Test]
        public void EveryConfigNumberAffectsHash_Chaser()
        {
            AssertSectionAffectsHash("Chaser", "Parts");
            AssertHitPartArrayFieldAffectsHash("Chaser", "Parts");
        }

        [Test]
        public void EveryConfigNumberAffectsHash_Gunner()
        {
            AssertSectionAffectsHash("Gunner", "Parts");
            AssertHitPartArrayFieldAffectsHash("Gunner", "Parts");
        }

        [Test]
        public void EveryConfigNumberAffectsHash_Wave()
        {
            // Task Т2 (app-ggvz, spec §3.8): WavePauseByZone/MaxAliveByZone
            // are skipped-by-the-sweep array fields, each covered by its own
            // dedicated element-wise helper below. MaxSpawnsPerZonePerTick/
            // DifficultyStepSeconds are plain scalars —
            // AssertSectionAffectsHash's own reflection sweep already reaches
            // them with no test-code change needed, the same way it already
            // reaches BaseCount/GunnerShareBase/etc.
            //
            // ⚠ Т4: ZoneWeights was the third array here and is gone with the
            // shared wave budget (owner decision К3). Its name had to leave
            // BOTH lines — the skip-set is checked by NAME against the
            // section's real fields, so a stale entry is itself a failure.
            AssertSectionAffectsHash("Wave", "WavePauseByZone", "MaxAliveByZone");
            AssertFloatArrayFieldAffectsHash("Wave", "WavePauseByZone");
            AssertInt32ArrayFieldAffectsHash("Wave", "MaxAliveByZone");
        }

        [Test]
        public void EveryConfigNumberAffectsHash_Arena()
        {
            AssertSectionAffectsHash("Arena", // scalar fields only — arrays below
                "ObstaclePos", "ObstacleRadius", "WallA", "WallB", "WallHalfWidth",
                "ZoneRadius", "ZoneWallRadius", "ZoneWallHalfWidth",
                "ZoneWallDoorStart", "ZoneWallDoorCount",
                "DoorCenterRad", "DoorFreeWidth",
                "ExtractPos", "ExtractZone", "ExtractKind");

            // Every element (both float2 components where relevant) AND the
            // length itself (appending an element) — coordinator decision,
            // task-23-brief §2.3: hashing "up to the count" would leave a
            // genuinely longer array's tail invisible.
            AssertFloat2ArrayFieldAffectsHash("Arena", "ObstaclePos");
            AssertFloatArrayFieldAffectsHash("Arena", "ObstacleRadius");
            AssertFloat2ArrayFieldAffectsHash("Arena", "WallA");
            AssertFloat2ArrayFieldAffectsHash("Arena", "WallB");
            AssertFloatArrayFieldAffectsHash("Arena", "WallHalfWidth");

            // Stage 3 Task 13 (owner decision R-17/R-90): the eleven arrays
            // that rode the "pending" stretch test since Т8/Т11 — TestConfigs.
            // Default() carries real, non-empty zone/door/portal data since
            // Т12 (DefaultArena's own layout), so no hand-built fixture is
            // needed here the way the removed stretch test's
            // MakeConfigWithZones() once was.
            AssertFloatArrayFieldAffectsHash("Arena", "ZoneRadius");
            AssertFloatArrayFieldAffectsHash("Arena", "ZoneWallRadius");
            AssertFloatArrayFieldAffectsHash("Arena", "ZoneWallHalfWidth");
            AssertInt32ArrayFieldAffectsHash("Arena", "ZoneWallDoorStart");
            AssertInt32ArrayFieldAffectsHash("Arena", "ZoneWallDoorCount");
            AssertFloatArrayFieldAffectsHash("Arena", "DoorCenterRad");
            AssertFloatArrayFieldAffectsHash("Arena", "DoorFreeWidth");
            AssertFloat2ArrayFieldAffectsHash("Arena", "ExtractPos");
            AssertByteArrayFieldAffectsHash("Arena", "ExtractZone");
            AssertByteArrayFieldAffectsHash("Arena", "ExtractKind");
        }

        [Test]
        public void EveryConfigNumberAffectsHash_Visibility() => AssertSectionAffectsHash("Visibility");

        [Test]
        public void EveryConfigNumberAffectsHash_Flow() => AssertSectionAffectsHash("Flow");

        // Stage 3 Task 13 (owner decision R-17): Elite/Director wire into
        // the hash alongside everything else this task lifts in one move.
        // MobSimConfig's field NAMES are shared 1:1 with Chaser/Gunner's own
        // already-hashed section, but that is harmless here —
        // AssertSectionAffectsHash mutates ONE named section's own copy of
        // TestConfigs.Default() per call, never Chaser's/Gunner's.
        [Test]
        public void EveryConfigNumberAffectsHash_Elite()
        {
            AssertSectionAffectsHash("Elite", "Parts");
            AssertHitPartArrayFieldAffectsHash("Elite", "Parts");
        }

        [Test]
        public void EveryConfigNumberAffectsHash_Director()
        {
            AssertSectionAffectsHash("Director", "Parts");
            AssertHitPartArrayFieldAffectsHash("Director", "Parts");
        }

        [Test]
        public void EveryConfigNumberAffectsHash_Loot()
        {
            // Three array fields (DropChance, CellsPerMob, TransferSeconds)
            // join the same "skip the scalar sweep, cover with a dedicated
            // element-wise helper" convention as every other section's
            // arrays.
            AssertSectionAffectsHash("Loot", "DropChance", "CellsPerMob", "TransferSeconds");
            AssertFloatArrayFieldAffectsHash("Loot", "DropChance");
            AssertInt32ArrayFieldAffectsHash("Loot", "CellsPerMob");
            AssertFloatArrayFieldAffectsHash("Loot", "TransferSeconds");
        }

        // Stage 3 Task 13: SimConfig.Items is a TOP-LEVEL array (not nested
        // under a section struct), so it cannot go through
        // AssertSectionAffectsHash/Section() the way every array above
        // does — its own dedicated element-wise check.
        [Test]
        public void EveryConfigNumberAffectsHash_Items() => AssertItemsArrayAffectsHash();

        [Test]
        public void SameConfig_SameHash()
        {
            // task-23-brief §5: the naive form of this test (just the
            // AreEqual below) was confirmed GREEN against the constant
            // Compute=>0UL RED-stage stub — 0 trivially equals 0 — despite
            // the stub being wrong (Task 23 report's mutation table). The
            // second assertion strengthens it before Step 2 (GREEN) starts.
            var a = TestConfigs.Default();
            var b = TestConfigs.Default(); // independently built — see ArrayContentsNotIdentity below
            Assert.AreEqual(SimConfigHash.Compute(in a), SimConfigHash.Compute(in b));
            Assert.AreNotEqual(0UL, SimConfigHash.Compute(in a));
        }

        [Test]
        public void DifferentConfigs_DifferentHash()
        {
            var a = TestConfigs.Default();
            var b = TestConfigs.Default();
            b.Hero.MaxSpeed += 1f;
            Assert.AreNotEqual(SimConfigHash.Compute(in a), SimConfigHash.Compute(in b));
        }

        [Test]
        public void ArrayContentsNotIdentity_DecideTheHash()
        {
            var a = TestConfigs.Default();
            var b = TestConfigs.Default(); // independent array instances, same content
            Assert.AreNotSame(a.Arena.ObstaclePos, b.Arena.ObstaclePos); // sanity: genuinely different instances
            Assert.AreEqual(SimConfigHash.Compute(in a), SimConfigHash.Compute(in b));

            b.Arena.ObstaclePos[0] += new float2(1f, 0f);
            Assert.AreNotEqual(SimConfigHash.Compute(in a), SimConfigHash.Compute(in b));
        }

        [Test]
        public void NullArray_DoesNotThrow_AndDiffersFromEmpty()
        {
            // BOTH array helpers get a leg. Fix-round finding I-6: the first
            // draft nulled ObstaclePos only, which exercises HashFloat2Array
            // alone — the identical `a == null` guard in HashFloatArray was
            // covered by nothing, and deleting it survived the whole suite.
            // default(ArenaSimConfig) carries all five arrays null, and this
            // class's own doc promises hand-built fixtures never reach the
            // builder, so the branch is reachable by design, not in theory.
            var nullFloat2 = TestConfigs.Default();
            nullFloat2.Arena.ObstaclePos = null;
            var emptyFloat2 = TestConfigs.Default();
            emptyFloat2.Arena.ObstaclePos = Array.Empty<float2>();
            AssertNullDiffersFromEmpty(nullFloat2, emptyFloat2, "ObstaclePos (HashFloat2Array)");

            var nullFloat = TestConfigs.Default();
            nullFloat.Arena.ObstacleRadius = null;
            var emptyFloat = TestConfigs.Default();
            emptyFloat.Arena.ObstacleRadius = Array.Empty<float>();
            AssertNullDiffersFromEmpty(nullFloat, emptyFloat, "ObstacleRadius (HashFloatArray)");
        }

        static void AssertNullDiffersFromEmpty(SimConfig withNull, SimConfig withEmpty, string path)
        {
            ulong nullHash = 0UL;
            Assert.DoesNotThrow(() => nullHash = SimConfigHash.Compute(in withNull),
                $"{path}: a null array must hash as the length marker, not throw");
            Assert.AreNotEqual(nullHash, SimConfigHash.Compute(in withEmpty),
                $"{path}: a null and an empty array must not hash identically");
        }

        [Test]
        public void SimConfig_CarriesExactlyTwelveFields() // Р52 guard
        {
            // A network config (or any further section) landing inside
            // SimConfig would enter SimConfigHash automatically if Compute()
            // ever grew reflective, and a change like NetConfig's own
            // LatencySimRttMs would then break a match on a balance-hash
            // mismatch for a purely dev/deploy knob — that must be an OWNER
            // decision (Р52), never a silent side effect of adding a field.
            // This is a characterization guard: it pins the current field
            // set by name, so a THIRTEENTH field fails loudly and asks for
            // that decision instead of shipping quietly.
            //
            // RENAMED (Stage 3 Task 13, coordinator "name the guard so the
            // body and the name agree" requirement): eleven of the twelve
            // fields below ARE sections (a struct nested one level), but
            // `Items` is a bare top-level array — "TwelveSections" would
            // have kept lying about the shape the moment `Items` joined.
            // "TwelveFields" is true regardless of what shape any one of
            // them takes.
            string[] expected =
            {
                "Hero", "Weapon", "Chaser", "Gunner", "Wave", "Arena", "Visibility", "Flow",
                "Elite", "Director", "Loot", "Items",
            };
            FieldInfo[] fields = typeof(SimConfig).GetFields();
            string[] actual = new string[fields.Length];
            for (int i = 0; i < fields.Length; i++) actual[i] = fields[i].Name;
            CollectionAssert.AreEquivalent(expected, actual);
        }

        [Test]
        public void NetStatsCounters_DoNotOverlapMatchStatsOrWorldStats() // Task 58 guard
        {
            var netFieldNames = new HashSet<string>();
            foreach (FieldInfo f in typeof(NetStats).GetFields()) netFieldNames.Add(f.Name);

            // Fix-round finding I-5: without these two the sweep below is
            // VACUOUS — `public sealed class NetStats { }` passed it, so the
            // ten counters were pinned by nothing at all, and the
            // class-vs-struct decision (NetStats.cs's own doc: a struct
            // silently drops increments applied to copies, the exact defect
            // class phase Ф5 hit four times) was pinned only by a comment.
            Assert.IsFalse(typeof(NetStats).IsValueType,
                "NetStats must be a class: a struct would silently drop increments applied to copies");
            // 14, not 10: Stage 2 Task 33 (plan :1603-1606) added the four
            // LatencySim* fields — a deliberate, sanctioned composition
            // change (task-33-brief.md §2.3), not drift. This literal is a
            // characterization pin; a FIFTEENTH field should fail here too
            // and prompt the same update, not slide through silently.
            Assert.AreEqual(14, netFieldNames.Count,
                "fourteen NetStats fields expected (ten counters + four latency-simulator " +
                "facts) — the composition of NetStats has changed");

            foreach (FieldInfo f in typeof(MatchStats).GetFields())
                Assert.IsFalse(netFieldNames.Contains(f.Name),
                    $"NetStats.{f.Name} collides by name with MatchStats.{f.Name}");
            foreach (FieldInfo f in typeof(WorldStats).GetFields())
                Assert.IsFalse(netFieldNames.Contains(f.Name),
                    $"NetStats.{f.Name} collides by name with WorldStats.{f.Name}");
        }

        // ---- reflection sweep helpers (WorldLifecycleTests-style — see flagman doc) ----

        /// Stage 3 Task 13 (owner decision R-17/R-90): the flat, name-only
        /// PENDING skip-set this helper used to consult
        /// (`SimConfigHashTests.PendingHashFields`) is GONE — every scalar
        /// this method reaches is, as of this task, either genuinely wired
        /// into SimConfigHash.Compute (assert AreNotEqual) or the caller
        /// listed it in `expectedArrayFields` (an array, handled by a
        /// dedicated element-wise helper below, never a positive/negative
        /// branch here). There is no third case left.
        static void AssertSectionAffectsHash(string sectionName, params string[] expectedArrayFields)
        {
            var baselineCfg = TestConfigs.Default();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);
            FieldInfo sectionField = Section(sectionName);
            var skippedArrayFields = new List<string>();
            foreach (FieldInfo field in sectionField.FieldType.GetFields())
            {
                // int[]/byte[] joined float2[]/float[] here (Arena's
                // ZoneWallDoorStart/DoorCount are int[], ExtractZone/
                // ExtractKind are byte[]; Loot's CellsPerMob is int[]) —
                // Bump(object) below throws NotSupportedException the
                // moment the sweep reaches one of them (its switch only
                // handles boxed float/int/bool, not an array instance of
                // any element type). Recorded in skippedArrayFields exactly
                // like float2[]/float[] — the CollectionAssert below still
                // catches an unlisted array field by name.
                // app-88jb Т13 (coordinator Ruling 59): HitPart[] joins the
                // four above, and the list is NOT decoration — the guard is
                // what routes an array into skippedArrayFields. A field type
                // missing from it falls through to Bump(object), whose switch
                // understands boxed float/int/bool only, and the section test
                // dies with NotSupportedException. A raised exception is not a
                // RED (332/498): it hides which fields the sweep did reach and
                // makes the run's own prediction unverifiable.
                if (field.FieldType == typeof(float2[]) || field.FieldType == typeof(float[])
                    || field.FieldType == typeof(int[]) || field.FieldType == typeof(byte[])
                    || field.FieldType == typeof(HitPart[]))
                {
                    skippedArrayFields.Add(field.Name);
                    continue;
                }

                object cfg = TestConfigs.Default();
                object section = sectionField.GetValue(cfg);
                field.SetValue(section, Bump(field.GetValue(section)));
                sectionField.SetValue(cfg, section);
                var mutated = (SimConfig)cfg;
                Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutated),
                    $"{sectionName}.{field.Name} is not in the hash");
            }

            CollectionAssert.AreEquivalent(expectedArrayFields, skippedArrayFields,
                $"{sectionName}: the set of array fields has changed — a new one is checked neither " +
                "by this sweep nor by the per-element helpers, i.e. pinned by nothing");
        }

        /// Fails loudly with the section's name instead of a bare NRE deeper
        /// in the helper if SimConfig's field is ever renamed.
        static FieldInfo Section(string sectionName)
        {
            FieldInfo field = typeof(SimConfig).GetField(sectionName);
            Assert.IsNotNull(field, $"SimConfig.{sectionName} does not exist");
            return field;
        }

        static void AssertFloat2ArrayFieldAffectsHash(string sectionName, string fieldName)
        {
            var baselineCfg = TestConfigs.Default();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);
            FieldInfo sectionField = Section(sectionName);
            FieldInfo arrayField = sectionField.FieldType.GetField(fieldName);

            object probeCfg = TestConfigs.Default();
            int length = ((float2[])arrayField.GetValue(sectionField.GetValue(probeCfg))).Length;

            for (int i = 0; i < length; i++)
            {
                AssertElementBump(i, new float2(1f, 0f), "x");
                AssertElementBump(i, new float2(0f, 1f), "y");
            }

            // Length: appending an element must move the hash too — the
            // mutation this guards against is Compute hashing "up to
            // ObstacleCount/WallCount" instead of the array's real length.
            object lenCfg = TestConfigs.Default();
            object lenSection = sectionField.GetValue(lenCfg);
            var original = (float2[])arrayField.GetValue(lenSection);
            var extended = new float2[original.Length + 1];
            Array.Copy(original, extended, original.Length);
            extended[original.Length] = new float2(1234f, 5678f);
            arrayField.SetValue(lenSection, extended);
            sectionField.SetValue(lenCfg, lenSection);
            var mutatedLen = (SimConfig)lenCfg;
            Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutatedLen),
                $"{sectionName}.{fieldName}.Length is not in the hash");

            void AssertElementBump(int index, float2 delta, string component)
            {
                object cfg = TestConfigs.Default();
                object section = sectionField.GetValue(cfg);
                var clone = (float2[])((float2[])arrayField.GetValue(section)).Clone();
                clone[index] += delta;
                arrayField.SetValue(section, clone);
                sectionField.SetValue(cfg, section);
                var mutated = (SimConfig)cfg;
                Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutated),
                    $"{sectionName}.{fieldName}[{index}].{component} is not in the hash");
            }
        }

        static void AssertFloatArrayFieldAffectsHash(string sectionName, string fieldName)
        {
            var baselineCfg = TestConfigs.Default();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);
            FieldInfo sectionField = Section(sectionName);
            FieldInfo arrayField = sectionField.FieldType.GetField(fieldName);

            object probeCfg = TestConfigs.Default();
            int length = ((float[])arrayField.GetValue(sectionField.GetValue(probeCfg))).Length;

            for (int i = 0; i < length; i++)
            {
                object cfg = TestConfigs.Default();
                object section = sectionField.GetValue(cfg);
                var clone = (float[])((float[])arrayField.GetValue(section)).Clone();
                clone[i] += 1f;
                arrayField.SetValue(section, clone);
                sectionField.SetValue(cfg, section);
                var mutated = (SimConfig)cfg;
                Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutated),
                    $"{sectionName}.{fieldName}[{i}] is not in the hash");
            }

            object lenCfg = TestConfigs.Default();
            object lenSection = sectionField.GetValue(lenCfg);
            var original = (float[])arrayField.GetValue(lenSection);
            var extended = new float[original.Length + 1];
            Array.Copy(original, extended, original.Length);
            extended[original.Length] = 1234f;
            arrayField.SetValue(lenSection, extended);
            sectionField.SetValue(lenCfg, lenSection);
            var mutatedLen = (SimConfig)lenCfg;
            Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutatedLen),
                $"{sectionName}.{fieldName}.Length is not in the hash");
        }

        /// Stage 3 Task 13 (coordinator fix-round Ф3 review m3 — rewritten
        /// for clarity, content unchanged): same shape as
        /// AssertFloatArrayFieldAffectsHash right above — see that method's
        /// own doc for the full per-element/length reasoning — for int[]
        /// fields.
        static void AssertInt32ArrayFieldAffectsHash(string sectionName, string fieldName)
        {
            var baselineCfg = TestConfigs.Default();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);
            FieldInfo sectionField = Section(sectionName);
            FieldInfo arrayField = sectionField.FieldType.GetField(fieldName);

            object probeCfg = TestConfigs.Default();
            int length = ((int[])arrayField.GetValue(sectionField.GetValue(probeCfg))).Length;

            for (int i = 0; i < length; i++)
            {
                object cfg = TestConfigs.Default();
                object section = sectionField.GetValue(cfg);
                var clone = (int[])((int[])arrayField.GetValue(section)).Clone();
                clone[i] += 1;
                arrayField.SetValue(section, clone);
                sectionField.SetValue(cfg, section);
                var mutated = (SimConfig)cfg;
                Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutated),
                    $"{sectionName}.{fieldName}[{i}] is not in the hash");
            }

            object lenCfg = TestConfigs.Default();
            object lenSection = sectionField.GetValue(lenCfg);
            var original = (int[])arrayField.GetValue(lenSection);
            var extended = new int[original.Length + 1];
            Array.Copy(original, extended, original.Length);
            extended[original.Length] = 1234;
            arrayField.SetValue(lenSection, extended);
            sectionField.SetValue(lenCfg, lenSection);
            var mutatedLen = (SimConfig)lenCfg;
            Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutatedLen),
                $"{sectionName}.{fieldName}.Length is not in the hash");
        }

        /// Stage 3 Task 13 (coordinator fix-round Ф3 review m3 — rewritten
        /// for clarity, content unchanged): same shape as
        /// AssertFloatArrayFieldAffectsHash above — see that method's own
        /// doc for the full per-element/length reasoning — for byte[]
        /// fields.
        static void AssertByteArrayFieldAffectsHash(string sectionName, string fieldName)
        {
            var baselineCfg = TestConfigs.Default();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);
            FieldInfo sectionField = Section(sectionName);
            FieldInfo arrayField = sectionField.FieldType.GetField(fieldName);

            object probeCfg = TestConfigs.Default();
            int length = ((byte[])arrayField.GetValue(sectionField.GetValue(probeCfg))).Length;

            for (int i = 0; i < length; i++)
            {
                object cfg = TestConfigs.Default();
                object section = sectionField.GetValue(cfg);
                var clone = (byte[])((byte[])arrayField.GetValue(section)).Clone();
                clone[i] += 1;
                arrayField.SetValue(section, clone);
                sectionField.SetValue(cfg, section);
                var mutated = (SimConfig)cfg;
                Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutated),
                    $"{sectionName}.{fieldName}[{i}] is not in the hash");
            }

            object lenCfg = TestConfigs.Default();
            object lenSection = sectionField.GetValue(lenCfg);
            var original = (byte[])arrayField.GetValue(lenSection);
            var extended = new byte[original.Length + 1];
            Array.Copy(original, extended, original.Length);
            extended[original.Length] = 234;
            arrayField.SetValue(lenSection, extended);
            sectionField.SetValue(lenCfg, lenSection);
            var mutatedLen = (SimConfig)lenCfg;
            Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutatedLen),
                $"{sectionName}.{fieldName}.Length is not in the hash");
        }

        /// Stage 3 Task 13: SimConfig.Items is a TOP-LEVEL ItemDef[] field,
        /// not nested under a section struct — every helper above resolves
        /// a section field first and then an array field inside it, which
        /// Items has no first half of. Same "element bump + length append"
        /// shape as the others, addressed directly against SimConfig
        /// instead of through Section()/a FieldInfo pair.
        static void AssertItemsArrayAffectsHash()
        {
            var baselineCfg = TestConfigs.Default();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);
            Assert.GreaterOrEqual(baselineCfg.Items.Length, 2,
                "premise: TestConfigs.Default().Items must carry at least two records " +
                "for the second-element convention (lesson 227) to have anywhere to mutate");

            var cfg = TestConfigs.Default();
            var clone = (ItemDef[])cfg.Items.Clone();
            clone[1].SlotCost += 1;
            cfg.Items = clone;
            Assert.AreNotEqual(baseline, SimConfigHash.Compute(in cfg), "Items[1].SlotCost is not in the hash");

            var lenCfg = TestConfigs.Default();
            var original = lenCfg.Items;
            var extended = new ItemDef[original.Length + 1];
            Array.Copy(original, extended, original.Length);
            extended[original.Length] = new ItemDef
                { Id = 250, Tier = 1, SlotCost = 1, CreditValue = 1, Kind = ItemKind.Trophy };
            lenCfg.Items = extended;
            Assert.AreNotEqual(baseline, SimConfigHash.Compute(in lenCfg), "Items.Length is not in the hash");
        }

        /// app-88jb Т13: `HitPart[]` — every one of the FIVE fields of every
        /// part, plus the length. Same shape and same reason as
        /// AssertItemsArrayAffectsHash above: the scalar sweep cannot bump an
        /// array in place, so an array's coverage has to be spelled out field
        /// by field, or a fold that quietly drops one of them — DamageMult is
        /// the easiest to forget, being the only one no geometry reads — is
        /// pinned by nothing at all.
        static void AssertHitPartArrayFieldAffectsHash(string sectionName, string fieldName)
        {
            var baselineCfg = TestConfigs.Default();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);
            FieldInfo sectionField = Section(sectionName);
            FieldInfo arrayField = sectionField.FieldType.GetField(fieldName);

            object probeCfg = TestConfigs.Default();
            int length = ((HitPart[])arrayField.GetValue(sectionField.GetValue(probeCfg))).Length;
            Assert.GreaterOrEqual(length, 2,
                $"premise: {sectionName}.{fieldName} must carry at least two parts, or the " +
                "second-element convention (lesson 227) has nowhere to put a violation");

            for (int i = 0; i < length; i++)
            {
                AssertFieldBump(i, "Radius", part => { part.Radius += 1f; return part; });
                AssertFieldBump(i, "Bottom", part => { part.Bottom += 1f; return part; });
                AssertFieldBump(i, "Top", part => { part.Top += 1f; return part; });
                // Rotated inside the enum's own domain rather than incremented
                // past it: a value outside HitZone would test the hash against
                // a body no config can express.
                AssertFieldBump(i, "Zone",
                    part => { part.Zone = (HitZone)(((byte)part.Zone + 1) % 4); return part; });
                AssertFieldBump(i, "DamageMult", part => { part.DamageMult += 1f; return part; });
            }

            // Length: appending a part must move the hash too — the mutation
            // this guards is a fold that walks a fixed three parts instead of
            // the array's real length.
            object lenCfg = TestConfigs.Default();
            object lenSection = sectionField.GetValue(lenCfg);
            var original = (HitPart[])arrayField.GetValue(lenSection);
            var extended = new HitPart[original.Length + 1];
            Array.Copy(original, extended, original.Length);
            extended[original.Length] = new HitPart
            {
                Radius = 1f, Bottom = 90f, Top = 91f, Zone = HitZone.None, DamageMult = 1f
            };
            arrayField.SetValue(lenSection, extended);
            sectionField.SetValue(lenCfg, lenSection);
            var mutatedLen = (SimConfig)lenCfg;
            Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutatedLen),
                $"{sectionName}.{fieldName}.Length is not in the hash");

            void AssertFieldBump(int index, string fieldLabel, Func<HitPart, HitPart> bump)
            {
                object cfg = TestConfigs.Default();
                object section = sectionField.GetValue(cfg);
                var clone = (HitPart[])((HitPart[])arrayField.GetValue(section)).Clone();
                clone[index] = bump(clone[index]);
                arrayField.SetValue(section, clone);
                sectionField.SetValue(cfg, section);
                var mutated = (SimConfig)cfg;
                Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutated),
                    $"{sectionName}.{fieldName}[{index}].{fieldLabel} is not in the hash");
            }
        }

        static object Bump(object v) => v switch
        {
            float f => f + 1f,
            int i => i + 1,
            bool b => !b,
            _ => throw new NotSupportedException(v.GetType().Name)
        };
    }
}
