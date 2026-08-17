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
        // TEMPORARY (T2 -> T13): enters SimConfigHash when the stage's world
        // numbers are wired there in one move. Owner decision R-17 (coordinator
        // ledger): errata E-6 D-I9 lists "zones/doors/portals/catalog/backpack/
        // ammo/drop and Flow" as the section's own deferred-wiring set, naming
        // steps "Т8/Т10/Т13/Т22" — four addressees for one debt, which is
        // exactly the unbounded-skip-set failure mode this project has already
        // paid for twice. R-17 collapses them into ONE: T13, and only T13, is
        // the addressee. It removes this WHOLE SET unconditionally, in one
        // move, not field by field (same discipline as WorldLifecycleTests.
        // PendingHashFields, Т1 -> Т6) — removal proven by pulling one entry
        // back out and watching AssertSectionAffectsHash name it. By T13 every
        // new world number exists: weapon (Т2), cells (Т3), backpack (Т4),
        // zones/doors/portals/caps (Т8), elite and Director (Т10), Flow (Т12),
        // catalog and loot (Т13). Weakening this guard itself (e.g. "only check
        // the old fields") is not a fix.
        static readonly System.Collections.Generic.HashSet<string> PendingHashFields = new()
        {
            "ShotsPerCell", "AmmoStart", "AmmoMax", "EmergencyFireInterval",
            // Stage 3 Task 3: same T13 addressee, same discipline — new config
            // numbers, not new state fields (those go in WorldLifecycleTests'
            // own PendingHashFields instead). MaxPickups (Arena),
            // PickupRadius (Hero), CellsOnDeath (Chaser AND Gunner — one
            // MobSimConfig instance each, same field name), CorpseCellFraction
            // (Weapon).
            "MaxPickups", "PickupRadius", "CellsOnDeath", "CorpseCellFraction",
            // Stage 3 Task 4: same T13 addressee, same discipline — the
            // backpack's two capacity numbers (Hero.InventoryCapacity,
            // Hero.MaxInventoryItems).
            "InventoryCapacity", "MaxInventoryItems",
            // Ф1 fix-round (review A-I1 / B-I-2): the five `Flow` numbers, the
            // ONE deferred set that had no executable stretch at all. The
            // section was named in SimConfig_CarriesExactlyEightSections but
            // AssertSectionAffectsHash was never called for it, so Т13 could
            // have wired four of the five — or none — and nothing anywhere
            // would have gone red; a config number outside the handshake hash
            // is precisely how a client and a server come to disagree about
            // the length of an extraction channel while agreeing on the hash
            // (spec §3.12). Their SO and builder wiring arrive in Т12, their
            // HASH wiring in Т13 with everything above — the "Т22 is Flow's
            // own step" reading these entries replace was the second addressee
            // R-17 exists to remove.
            "GateDelaySeconds", "ExtractChannelSeconds", "RetinueCount",
            "RetinueRespawnSeconds", "DirectorReserveSlots",
        };

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
            // Ф1 fix-round (review A-I1 / B-I-2): the EIGHTH section joins the
            // sweep. Its five numbers all sit in PendingHashFields today, so
            // every one of them is checked by the POSITIVE "still outside the
            // hash" assertion rather than skipped — and the moment Т13 lifts
            // that set, this line is what demands all five actually be in
            // SimConfigHash.Compute. Without the call the whole section was
            // invisible to the flagman, name and all.
            AssertSectionAffectsHash("Flow");

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
                $"{path}: a null array must hash as the length marker, not throw");
            Assert.AreNotEqual(nullHash, SimConfigHash.Compute(in withEmpty),
                $"{path}: a null and an empty array must not hash identically");
        }

        [Test]
        public void SimConfig_CarriesExactlyEightSections() // Р52 guard
        {
            // A network config (or any further section) landing inside
            // SimConfig would enter SimConfigHash automatically if Compute()
            // ever grew reflective, and a change like NetConfig's own
            // LatencySimRttMs would then break a match on a balance-hash
            // mismatch for a purely dev/deploy knob — that must be an OWNER
            // decision (Р52), never a silent side effect of adding a field.
            // This is a characterization guard: it pins the current field
            // set by name, so a NINTH section fails loudly and asks for that
            // decision instead of shipping quietly.
            //
            // Stage 3 Task 1 (errata E-2) is the EIGHTH section, `Flow`
            // (MatchFlowSimConfig) — a RECORDED decision, not a silent
            // addition: `SimConfigHash.Compute` does not read it yet, and Т13
            // is the single addressee that wires it (owner decision R-17,
            // over errata E-6 I9's four). This test's rename (Seven -> Eight)
            // and the updated `expected` list below are that record — the
            // decision this guard exists to force, already made. The deferral
            // itself is no longer recorded HERE alone: PendingHashFields above
            // carries the five names, and EveryConfigNumberAffectsHash sweeps
            // the section (Ф1 fix-round, review A-I1/B-I-2).
            string[] expected =
                { "Hero", "Weapon", "Chaser", "Gunner", "Wave", "Arena", "Visibility", "Flow" };
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
                if (PendingHashFields.Contains(field.Name))
                {
                    // TEMPORARY (T2 -> T13, owner decision R-17): a positive
                    // assertion, not a silent skip — proves the field is
                    // genuinely still OUTSIDE SimConfigHash, not just unchecked
                    // (same WorldLifecycleTests.PendingHashFields discipline).
                    Assert.AreEqual(baseline, SimConfigHash.Compute(in mutated),
                        $"{sectionName}.{field.Name} ещё не должен входить в SimConfigHash до Т13");
                    continue;
                }
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

        static object Bump(object v) => v switch
        {
            float f => f + 1f,
            int i => i + 1,
            bool b => !b,
            _ => throw new NotSupportedException(v.GetType().Name)
        };
    }
}
