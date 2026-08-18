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
            // Stage 3 Task 8: same T13 addressee, same discipline — five new
            // SCALAR Arena numbers (zones/doors/portals/containers). Array
            // fields (ZoneRadius, ZoneWallRadius/HalfWidth/DoorStart/
            // DoorCount, DoorCenterRad/DoorFreeWidth, ExtractPos/Zone/Kind)
            // are NOT listed here — see AssertSectionAffectsHash's own doc
            // below for why arrays never reach this set at all, pending or not.
            "ZoneWallCount", "DoorClearance", "ExtractRadius",
            "MaxContainers", "MaxContainerSlots",
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
            // Stage 3 Task 8: nine array fields join the skip set
            // (ZoneRadius, ZoneWallRadius/HalfWidth/DoorStart/DoorCount,
            // DoorCenterRad/DoorFreeWidth, ExtractPos/Zone/Kind — eleven
            // names, matching AssertSectionAffectsHash's own extended
            // type-skip check below). None of the eleven gets an
            // AssertFloat2/FloatArrayFieldAffectsHash call the way WallA/
            // WallB/WallHalfWidth do a few lines down: every one of them is
            // still PENDING (T13, R-17 — see PendingHashFields above), so a
            // positive "this array moves the hash" assertion would be
            // false. What DOES hold today is the CollectionAssert below:
            // every array field this section declares is accounted for by
            // name, pending or not — a twelfth array field landing on
            // ArenaSimConfig with no entry here fails this line loudly
            // instead of the sweep silently ignoring it (same NotSupportedException
            // gap analysis as this file's own array/Bump split — see this
            // task's report).
            AssertSectionAffectsHash("Arena", // scalar fields only — arrays below
                "ObstaclePos", "ObstacleRadius", "WallA", "WallB", "WallHalfWidth",
                "ZoneRadius", "ZoneWallRadius", "ZoneWallHalfWidth",
                "ZoneWallDoorStart", "ZoneWallDoorCount",
                "DoorCenterRad", "DoorFreeWidth",
                "ExtractPos", "ExtractZone", "ExtractKind");
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

        /// Stage 3 Task 8 (coordinator ledger, post-RED requirement): the
        /// ten new Arena PENDING ARRAY fields (zones/doors/portals) had no
        /// stretch at all — unlike the scalar PendingHashFields entries
        /// above, whose positive assert flips the day Т13 wires the field
        /// in, an untouched array field stays silent whether Т13
        /// remembers it or forgets it (lesson 272/263: a debt with no
        /// enforcement is the same failure mode a Ф1 review Critical
        /// already cost this project once). This single test is that
        /// enforcement — written by hand, not a parallel
        /// AssertInt32/ByteArrayFieldAffectsHash mechanism (that
        /// generalized helper belongs to Т13, the day these arrays
        /// genuinely enter the hash).
        ///
        /// MUST GO RED THE DAY Т13 WIRES ANY of these ten arrays into
        /// SimConfigHash.Compute — that is this test's whole purpose, not
        /// a side effect to tolerate against. TestConfigs carries no zones
        /// (Т12 wires them), so the fixture is built by hand here, with
        /// two elements per array wherever the shape allows, so the
        /// SECOND element (ledger 227) is always the one mutated.
        [Test]
        public void PendingArenaArrays_MutationDoesNotAffectHash_UntilT13WiresThem()
        {
            // CS8156 (same trap this project already hit in Т3 — ledger,
            // SimulationWorld.cs:880-886): a method's return value cannot
            // be passed by `in` directly, it must be copied into a local
            // first — same fix as AssertSectionAffectsHash's own
            // `baselineCfg` a few lines below.
            var baselineCfg = MakeConfigWithZones();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);

            AssertUnchanged(c => c.Arena.ZoneRadius[1] += 1f, "ZoneRadius");
            AssertUnchanged(c => c.Arena.ZoneWallRadius[1] += 1f, "ZoneWallRadius");
            AssertUnchanged(c => c.Arena.ZoneWallHalfWidth[1] += 1f, "ZoneWallHalfWidth");
            AssertUnchanged(c => c.Arena.ZoneWallDoorStart[1] += 1, "ZoneWallDoorStart");
            AssertUnchanged(c => c.Arena.ZoneWallDoorCount[1] += 1, "ZoneWallDoorCount");
            AssertUnchanged(c => c.Arena.DoorCenterRad[1] += 1f, "DoorCenterRad");
            AssertUnchanged(c => c.Arena.DoorFreeWidth[1] += 1f, "DoorFreeWidth");
            AssertUnchanged(c => c.Arena.ExtractPos[1] += new float2(1f, 0f), "ExtractPos");
            AssertUnchanged(c => c.Arena.ExtractZone[1] += 1, "ExtractZone");
            AssertUnchanged(c => c.Arena.ExtractKind[1] += 1, "ExtractKind");

            void AssertUnchanged(Action<SimConfig> mutate, string fieldName)
            {
                // MakeConfigWithZones() allocates fresh arrays on every
                // call (same discipline TestConfigs.Default() itself
                // relies on — see ArrayContentsNotIdentity_DecideTheHash
                // above), so mutating this copy cannot alias `baseline`'s
                // own arrays.
                var cfg = MakeConfigWithZones();
                mutate(cfg);
                Assert.AreEqual(baseline, SimConfigHash.Compute(in cfg),
                    $"Arena.{fieldName}[1]: должен ещё оставаться вне SimConfigHash до Т13 " +
                    "(этот тест обязан покраснеть в день, когда Т13 заведёт зоны/двери/" +
                    "порталы в хеш — это и есть его цель)");
            }
        }

        /// Non-empty zone/door/portal fixture for the stretch test above —
        /// TestConfigs.Default() itself stays zone-less until Т12
        /// (TestConfigs.DefaultArena()'s own comment), so this is built by
        /// hand. Two elements per array wherever the shape allows.
        static SimConfig MakeConfigWithZones()
        {
            var cfg = TestConfigs.Default();
            cfg.Arena.ZoneRadius = new[] { 20f, 40f };
            cfg.Arena.ZoneWallCount = 2;
            cfg.Arena.ZoneWallRadius = new[] { 20f, 40f };
            cfg.Arena.ZoneWallHalfWidth = new[] { 1f, 1f };
            cfg.Arena.ZoneWallDoorStart = new[] { 0, 1 };
            cfg.Arena.ZoneWallDoorCount = new[] { 1, 1 };
            cfg.Arena.DoorCenterRad = new[] { 0f, math.PI };
            cfg.Arena.DoorFreeWidth = new[] { 6f, 6f };
            cfg.Arena.ExtractPos = new[] { float2.zero, new float2(20f, 0f) };
            cfg.Arena.ExtractZone = new byte[] { 0, 1 };
            cfg.Arena.ExtractKind = new byte[] { 0, 1 };
            return cfg;
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
        public void SimConfig_CarriesExactlyTenSections() // Р52 guard
        {
            // A network config (or any further section) landing inside
            // SimConfig would enter SimConfigHash automatically if Compute()
            // ever grew reflective, and a change like NetConfig's own
            // LatencySimRttMs would then break a match on a balance-hash
            // mismatch for a purely dev/deploy knob — that must be an OWNER
            // decision (Р52), never a silent side effect of adding a field.
            // This is a characterization guard: it pins the current field
            // set by name, so an ELEVENTH section fails loudly and asks for
            // that decision instead of shipping quietly.
            //
            // Stage 3 Task 1 (errata E-2) was the EIGHTH section, `Flow`.
            // Stage 3 Task 10 (spec Р213) adds the NINTH and TENTH, `Elite`
            // and `Director` — same RECORDED-decision discipline: this
            // test's rename (Eight -> Ten) and the widened `expected` list
            // below ARE that record. Unlike Flow, Elite/Director's deferral
            // from SimConfigHash is NOT carried by PendingHashFields above
            // (that flat, name-only set cannot express "Elite.MaxSpeed is
            // pending but Chaser.MaxSpeed is not" — the two sections share
            // MobSimConfig's own field names 1:1 with the already-hashed
            // Chaser/Gunner) — see
            // EliteAndDirectorSections_DoNotAffectHash_UntilT13WiresThem
            // below for the executable record of that deferral instead, and
            // SimConfig.Elite/Director's own doc (Core/SimConfig.cs) for the
            // full reasoning trail.
            string[] expected =
            {
                "Hero", "Weapon", "Chaser", "Gunner", "Wave", "Arena", "Visibility", "Flow",
                "Elite", "Director",
            };
            FieldInfo[] fields = typeof(SimConfig).GetFields();
            string[] actual = new string[fields.Length];
            for (int i = 0; i < fields.Length; i++) actual[i] = fields[i].Name;
            CollectionAssert.AreEquivalent(expected, actual);
        }

        /// Stage 3 Task 10 (spec Р213, owner decision R-17): the executable
        /// half of the deferral SimConfig_CarriesExactlyTenSections' own doc
        /// just recorded — a comment alone is a debt with no enforcement,
        /// the exact failure mode a prior Ф1 review already cost this
        /// project once (same reasoning as
        /// PendingArenaArrays_MutationDoesNotAffectHash_UntilT13WiresThem
        /// above). EveryConfigNumberAffectsHash deliberately does NOT call
        /// AssertSectionAffectsHash("Elite"/"Director") — that helper's
        /// PendingHashFields lookup is keyed by bare field NAME, and
        /// MobSimConfig's fields (MaxSpeed, Radius, MaxHp, ...) are the same
        /// identifiers Chaser/Gunner's own, already-wired sections use, so
        /// adding them to the flat set would also, silently, exempt
        /// Chaser's/Gunner's real numbers from the sweep. This test sweeps
        /// both new sections field-by-field with its own local helper
        /// instead, asserting every one of them still leaves the hash
        /// UNCHANGED. MUST GO RED THE DAY Т13 WIRES EITHER SECTION into
        /// SimConfigHash.Compute — that is this test's whole purpose.
        [Test]
        public void EliteAndDirectorSections_DoNotAffectHash_UntilT13WiresThem()
        {
            AssertSectionDoesNotYetAffectHash("Elite");
            AssertSectionDoesNotYetAffectHash("Director");
        }

        static void AssertSectionDoesNotYetAffectHash(string sectionName)
        {
            var baselineCfg = TestConfigs.Default();
            ulong baseline = SimConfigHash.Compute(in baselineCfg);
            FieldInfo sectionField = Section(sectionName);
            foreach (FieldInfo field in sectionField.FieldType.GetFields())
            {
                object cfg = TestConfigs.Default();
                object section = sectionField.GetValue(cfg);
                field.SetValue(section, Bump(field.GetValue(section)));
                sectionField.SetValue(cfg, section);
                var mutated = (SimConfig)cfg;
                Assert.AreEqual(baseline, SimConfigHash.Compute(in mutated),
                    $"{sectionName}.{field.Name} должен ещё оставаться вне SimConfigHash до Т13");
            }
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
                // Stage 3 Task 8 finding: int[]/byte[] joined float2[]/
                // float[] here (Arena's new ZoneWallDoorStart/DoorCount are
                // int[], ExtractZone/ExtractKind are byte[]) — without this,
                // Bump(object) below throws NotSupportedException the
                // moment the sweep reaches one of them (its switch only
                // handles boxed float/int/bool, not an array instance of
                // any element type), crashing EveryConfigNumberAffectsHash
                // outright rather than failing an assertion. This helper
                // had no precedent for a non-float array field before this
                // task — T2/T3/T4 only ever added SCALAR PendingHashFields
                // entries — so unlike those three, an int[]/byte[] field
                // reaching this method used to have no path through it at
                // all. Recorded in skippedArrayFields exactly like
                // float2[]/float[] — the CollectionAssert below still
                // catches an unlisted array field by name — but (deliberate
                // scope decision, see this task's report) NEITHER type gets
                // a per-element "still excluded from the hash" POSITIVE
                // check the way PendingHashFields gives every SCALAR
                // pending field: no AssertInt32/ByteArrayFieldAffectsHash
                // helper exists, and building one is outside what R-17 asks
                // ("skip-set entries for new fields, same form already in
                // this file") — flagged for the coordinator rather than
                // invented silently.
                if (field.FieldType == typeof(float2[]) || field.FieldType == typeof(float[])
                    || field.FieldType == typeof(int[]) || field.FieldType == typeof(byte[]))
                {
                    // Handed to AssertFloat*ArrayFieldAffectsHash (for the
                    // two float-shaped types) — and recorded either way, so
                    // the caller's expected list proves it was handed over
                    // or knowingly deferred, rather than lost.
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
