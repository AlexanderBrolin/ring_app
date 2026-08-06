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
        [Test]
        public void EveryConfigNumberAffectsHash() // spec §3.8/§3.15, Р52 — flagman
        {
            // Same reflection-sweep SHAPE as WorldLifecycleTests.
            // EveryPlayerAndStatsFieldAffectsHash (:44-169) — bump one field
            // of a freshly-built fixture, recompute, assert the hash moved,
            // name the exact path on failure. SimConfig nests one level
            // deeper (section -> field, and for Arena, section -> array
            // field -> element), so the sweep runs once per SECTION rather
            // than over one flat type.
            // Each call also asserts WHICH array-typed fields it skipped:
            // the sweep cannot bump an array in place, so it hands them to
            // the element-wise helpers below — and a sixth array field
            // added to a section later would otherwise be skipped SILENTLY
            // by both, hashed by nothing and caught by nothing (fix-round
            // finding of the coordinator; the seven-section guard below
            // only watches the top level).
            AssertSectionAffectsHash("Hero");
            AssertSectionAffectsHash("Weapon");
            AssertSectionAffectsHash("Chaser");
            AssertSectionAffectsHash("Gunner");
            AssertSectionAffectsHash("Wave");
            AssertSectionAffectsHash("Arena", // scalar fields only — arrays below
                "ObstaclePos", "ObstacleRadius", "WallA", "WallB", "WallHalfWidth");
            AssertSectionAffectsHash("Visibility");

            // Arena's five array fields: every element (both float2
            // components where relevant) AND the length itself (appending
            // an element) — coordinator decision, task-23-brief §2.3:
            // hashing "up to the count" would leave a genuinely longer
            // array's tail invisible.
            AssertFloat2ArrayFieldAffectsHash("Arena", "ObstaclePos");
            AssertFloatArrayFieldAffectsHash("Arena", "ObstacleRadius");
            AssertFloat2ArrayFieldAffectsHash("Arena", "WallA");
            AssertFloat2ArrayFieldAffectsHash("Arena", "WallB");
            AssertFloatArrayFieldAffectsHash("Arena", "WallHalfWidth");
        }

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
                $"{path}: null-массив бросил вместо маркера длины");
            Assert.AreNotEqual(nullHash, SimConfigHash.Compute(in withEmpty),
                $"{path}: null и пустой массив дали один хеш");
        }

        [Test]
        public void SimConfig_CarriesExactlySevenSections() // Р52 guard
        {
            // A network config (or any 8th section) landing inside
            // SimConfig would enter SimConfigHash automatically, and a
            // change like NetConfig's own LatencySimRttMs would then break
            // a match on a balance-hash mismatch for a purely dev/deploy
            // knob — that must be an OWNER decision (Р52), never a silent
            // side effect of adding a field. This is a characterization
            // guard: it pins the current field set by name, so an eighth
            // section fails loudly and asks for that decision instead of
            // shipping quietly.
            string[] expected = { "Hero", "Weapon", "Chaser", "Gunner", "Wave", "Arena", "Visibility" };
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
                "NetStats обязан быть классом: структура молча теряла бы инкременты на копиях");
            Assert.AreEqual(10, netFieldNames.Count,
                "ожидались десять сетевых счётчиков — состав NetStats изменился");

            foreach (FieldInfo f in typeof(MatchStats).GetFields())
                Assert.IsFalse(netFieldNames.Contains(f.Name),
                    $"NetStats.{f.Name} совпадает по имени с MatchStats.{f.Name}");
            foreach (FieldInfo f in typeof(WorldStats).GetFields())
                Assert.IsFalse(netFieldNames.Contains(f.Name),
                    $"NetStats.{f.Name} совпадает по имени с WorldStats.{f.Name}");
        }

        // ---- reflection sweep helpers (WorldLifecycleTests-style — see flagman doc) ----

        static void AssertSectionAffectsHash(string sectionName, params string[] expectedArrayFields)
        {
            var baselineCfg = TestConfigs.Default();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);
            FieldInfo sectionField = Section(sectionName);
            var skippedArrayFields = new List<string>();
            foreach (FieldInfo field in sectionField.FieldType.GetFields())
            {
                if (field.FieldType == typeof(float2[]) || field.FieldType == typeof(float[]))
                {
                    // Handed to AssertFloat*ArrayFieldAffectsHash — and
                    // recorded, so the caller's expected list proves it was
                    // handed over rather than lost.
                    skippedArrayFields.Add(field.Name);
                    continue;
                }

                object cfg = TestConfigs.Default();
                object section = sectionField.GetValue(cfg);
                field.SetValue(section, Bump(field.GetValue(section)));
                sectionField.SetValue(cfg, section);
                var mutated = (SimConfig)cfg;
                Assert.AreNotEqual(baseline, SimConfigHash.Compute(in mutated),
                    $"{sectionName}.{field.Name} не в хеше");
            }

            CollectionAssert.AreEquivalent(expectedArrayFields, skippedArrayFields,
                $"{sectionName}: набор массивных полей изменился — новое поле не проверяется " +
                "ни этим свипом, ни поэлементными хелперами, то есть не пинится ничем");
        }

        /// Fails loudly with the section's name instead of a bare NRE deeper
        /// in the helper if SimConfig's field is ever renamed.
        static FieldInfo Section(string sectionName)
        {
            FieldInfo field = typeof(SimConfig).GetField(sectionName);
            Assert.IsNotNull(field, $"SimConfig.{sectionName} не существует");
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
                $"{sectionName}.{fieldName}.Length не в хеше");

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
                    $"{sectionName}.{fieldName}[{index}].{component} не в хеше");
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
                    $"{sectionName}.{fieldName}[{i}] не в хеше");
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
                $"{sectionName}.{fieldName}.Length не в хеше");
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
