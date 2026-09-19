using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Ring.Editor;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Ring.Simulation.Tests
{
    /// app-94sk T4 (spec §3.5): the two witnesses of the BAKED TABLE'S OWN
    /// INTEGRITY — the checksum, and the pair "sample target / measure origin".
    ///
    /// ⛔ WHY THEY LIVE HERE RATHER THAN IN `HitPartsTests`: their subject is
    /// the artifact and the tool that makes it, not the rules a body's volumes
    /// obey. And they call `Ring.Editor`, which this assembly already
    /// references — `MobFootprintAudit` is reached from the test set the same
    /// way.
    public class PoseTableTests
    {
        [Test]
        public void TheChecksumComesFromTheBytes_NotFromTheField()   // test 27, M355
        {
            // ⛔ THE ASSET'S FIELD IS A CACHE, NOT THE SOURCE OF TRUTH (Р514):
            // the config build folds the table AGAIN over the loaded numbers
            // and compares. A mutant that trusted the field would accept a
            // substituted table in silence — which is the whole failure the
            // three-level integrity scheme exists to prevent.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            ref PoseTable table = ref ConfigTests.PoseTableFor(c);
            ulong sealedSum = table.Checksum;

            // ⛔ THE LAST BONE, NOT A LITERAL INDEX: the fixture table is four
            // bones in one row, so a hard-coded `4` runs off the end and the
            // fixture dies of an IndexOutOfRange instead of going red.
            int last = table.Bones.Length - 1;
            table.Bones[last] = table.Bones[last] + new float3(0.05f, 0f, 0f);   // the bytes moved
            Assert.AreEqual(sealedSum, table.Checksum,
                "премисса фикстуры: поле осталось прежним — именно расхождение поля и байтов "
                + "обязана поймать сборка");

            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis));
            Assert.That(ex.Message, Does.Contain("pose table checksum"));
        }

        [Test]
        public void ClipOneOfTheCollectorsBakedTableIsTheSlide()
        {
            // ⛔⛔ THE CLIP-ORDER CONTRACT, ASKED WHERE IT CAN STILL BE ANSWERED.
            // Validation rule 16 reads the slide's crown through
            // `ClipFirstRow[BakedClips.Collector.Slide]`, and the ONLY thing that makes
            // that row the slide is `PoseBaker.BakeSet`'s ordering. A table
            // carries no clip NAMES, so the rule itself can check nothing but a
            // length — lose `Slide_Loop` from the controller and rule 16 would
            // go on measuring the crown of whatever clip landed at index 1,
            // silently.
            //
            // ⚠ THIS FIXTURE IS AN INSTRUMENT and it reads the COMMITTED
            // ARTIFACT rather than a fresh bake: the artifact is what the game
            // ships and what a reviewer cannot see (binary, under LFS).
            // ⚠ It compares against the SET the baker would build, taken from
            // the collector's own animator — not against a literal row number,
            // which would survive any change to the contract.
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                AnimatorCatalog.PrefabPathOf(AnimatorCatalog.BodyKind.Collector));
            Assert.IsNotNull(prefab, "префаб сборщика не найден — фикстура мерит не свой предмет");

            GameObject instance = Object.Instantiate(prefab);
            try
            {
                var animator = instance.GetComponentInChildren<Animator>(true);
                Assert.IsNotNull(animator, "премисса: у сборщика есть Animator");
                string slideTake = AnimatorCatalog.PackClipOf(
                    Ring.Presentation.AnimIds.SlideLoopName);

                var takes = new List<string>();
                foreach (AnimationClip c in PoseSampling.CollectClips(animator))
                    takes.Add(AnimatorCatalog.TakeOf(c.name));
                Assert.Contains(slideTake, takes,
                    "премисса: контроллер сборщика вообще несёт клип слайда");

                PoseTable baked = PoseBaker.BakeOne(prefab);
                Assert.Greater(baked.ClipFirstRow.Length, BakedClips.Collector.Slide + 1,
                    "у запечённой таблицы сборщика обязан быть клип 1");

                // The slide is SHORTER than standing — that is the whole point
                // of sliding — so its crown has to sit below the rest pose's.
                // ⛔ A property, not a literal: a literal would have to be
                // re-typed every time the clip changes, and would then be the
                // thing that broke instead of the contract.
                float restCrown = HitParts.PoseTop(
                    TestConfigs.Default().Hero.Parts, in baked, 0);
                float slideCrown = HitParts.PoseTop(TestConfigs.Default().Hero.Parts, in baked,
                    baked.ClipFirstRow[BakedClips.Collector.Slide]);
                Assert.Less(slideCrown, restCrown,
                    $"клип {BakedClips.Collector.Slide} запечённой таблицы обязан быть СЛАЙДОМ: "
                    + $"его крона {slideCrown:F4} не ниже кроны покоя {restCrown:F4}, то есть "
                    + "порядок клипов пекаря разошёлся с тем, что читает правило 16");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void TheSampleTargetAndTheMeasureOriginArePinnedApart()   // test 28, M356
        {
            // ⛔⛔ A PAIR, NOT ONE VALUE, AND THE COST OF CONFUSING THEM IS
            // MEASURABLE: a x100 scale lives on the mesh nodes inside the FBX,
            // and the gunner's prefab carries a 0.76 scale override on its FBX
            // instance. A baker measuring from the `Animator`'s object instead
            // of the prefab root disagrees with the auditor BY 0.76 ON THE
            // GUNNER — and every number it prints still looks plausible.
            //
            // ⚠ THIS FIXTURE IS AN INSTRUMENT: it calls `PoseSampling` directly
            // on the gunner's prefab and compares TWO heights of ONE bone, from
            // the prefab root and from the object carrying the Animator.
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                AnimatorCatalog.PrefabPathOf(AnimatorCatalog.BodyKind.Gunner));
            Assert.IsNotNull(prefab, "префаб ганнера не найден — фикстура мерит не свой предмет");

            GameObject instance = Object.Instantiate(prefab);
            try
            {
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;

                // ⛔ THE SAME BODY-BONE FILTER THE BAKER APPLIES: the pin below
                // compares against `BakeOne(...).Bones[0]`, and the baker's
                // column 0 is the first BODY bone, not the first skinning one.
                var bones = new List<Transform>();
                foreach (Transform b in PoseSampling.CollectBones(instance))
                    if (PoseSampling.IsBodyBone(b.name)) bones.Add(b);
                Assert.Greater(bones.Count, 0, "премисса: у ганнера есть кости тела");

                var animator = instance.GetComponentInChildren<Animator>(true);
                Assert.IsNotNull(animator, "премисса: у ганнера есть Animator");
                Assert.AreNotSame(animator.gameObject.transform, instance.transform,
                    "премисса фикстуры: Animator ганнера НЕ на корне префаба — иначе два начала "
                    + "отсчёта совпадают и тест не различает их вовсе");

                AnimationClip clip = AnimatorCatalog.IdleClipOf(animator);
                var fromRoot = new float3[bones.Count];
                var fromAnimator = new float3[bones.Count];
                AnimationMode.StartAnimationMode();
                try
                {
                    PoseSampling.SamplePose(animator.gameObject, instance.transform,
                        bones, clip, 0f, fromRoot);
                    PoseSampling.SamplePose(animator.gameObject, animator.gameObject.transform,
                        bones, clip, 0f, fromAnimator);
                }
                finally
                {
                    AnimationMode.StopAnimationMode();
                }

                Assert.Greater(math.abs(fromRoot[0].y - fromAnimator[0].y), 1e-3f,
                    "премисса фикстуры: на инстансе FBX ганнера лежит override масштаба, и два "
                    + "начала отсчёта обязаны давать разные числа");

                // AND THIS IS THE PIN: the baker measures FROM THE PREFAB ROOT,
                // exactly as the auditor does.
                Assert.AreEqual(fromRoot[0].y, PoseBaker.BakeOne(prefab).Bones[0].y, 1e-4f,
                    "пекарь меряет не от корня префаба — он разойдётся с аудитором");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        // ------------------------------------------------ app-94sk T6a (spec §3.6)

        [Test]
        public void TheBlendWeightMovesThePose()   // test 19, M346
        {
            // The blend-tree weight is part of the pose: two different shares
            // between ONE pair of clips must put the bones in different places.
            // ⚠ THE PREMISE IS A PROPERTY, NOT A LITERAL: the fixture's two
            // clips have to DIFFER in this bone, or the test is green on any
            // implementation at all.
            PoseTable table = TestConfigs.TwoClipWalkPose();   // row 0 standing, row 1 the step
            var lowWeight  = new PoseKey { LowerClipA = 0, LowerClipB = 1, LowerBlend = 0,   LowerPhase = 0 };
            var highWeight = new PoseKey { LowerClipA = 0, LowerClipB = 1, LowerBlend = 255, LowerPhase = 0 };
            var a = new float3[table.BoneCount];
            var b = new float3[table.BoneCount];
            PoseTable.Sample(in table, in lowWeight,  singleLayer: true, a);
            PoseTable.Sample(in table, in highWeight, singleLayer: true, b);

            const int Foot = 1;        // the fixture table's foot bone
            Assert.Greater(math.abs(table.Bones[0 * table.BoneCount + Foot].z
                                    - table.Bones[1 * table.BoneCount + Foot].z), 1e-4f,
                "премисса фикстуры: две строки таблицы обязаны различаться в стопе, иначе вес "
                + "дерева нечему двигать и тест зелен на любой реализации");
            Assert.Greater(math.abs(a[Foot].z - b[Foot].z), 1e-4f,
                "вес дерева не двигает позу ног — смешивание строк выброшено");
            // AND THE MIDDLE IS A SUBJECT TOO: a weight read as "the nearest
            // row" would pass the two asserts above and fail this one.
            var half = new PoseKey { LowerClipA = 0, LowerClipB = 1, LowerBlend = 128, LowerPhase = 0 };
            var m = new float3[table.BoneCount];
            PoseTable.Sample(in table, in half, singleLayer: true, m);
            Assert.That(m[Foot].z, Is.InRange(math.min(a[Foot].z, b[Foot].z) + 1e-4f,
                                              math.max(a[Foot].z, b[Foot].z) - 1e-4f),
                "половинный вес дал одну из крайних строк — смешивания нет, есть выбор ближайшей");
        }

        [Test]
        public void TheDampedWeightDoesNotStickAtTheQuantizationStep()   // test 20, M347
        {
            // ⛔ A TRAP THE SPEC NAMES WITH A NUMBER (§3.6): at a tick dt of
            // 1/30 against a damping constant of 0.1 the damper's step per tick
            // can be smaller than 1/255, and a weight rounded to a byte INSIDE
            // the simulation would stop short of its target. ⇒ the simulation
            // keeps the weight in float and quantizes it to a byte ONLY for
            // the key (PoseKey.FromPlayer).
            // ⛔⛔ THE SUBJECT IS THE COLLECTOR, NOT A MOB: `SpeedDampTime`
            // lives on the collector alone (HeroSimConfig, T5b), and a mob has
            // no blend tree at all -- its clips CrossFade over speed thresholds.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            Assert.AreEqual(0f, w.PlayerAt(0).LowerBlend, 1e-6f,
                "премисса фикстуры: сборщик стартует стоя, иначе движение веса не наблюдаемо");
            var input = new SimInput[1];
            input[0].MoveDir = new float2(1f, 0f);          // running flat out

            // FIRST, ONE TICK: the weight moves, and by LESS than the tick's
            // own target -- that is what a damper is. The target is MEASURED
            // off the world (this tick's speed over MaxSpeed), not written as
            // a literal, so the claim survives a change of Accel; on the
            // fixture numbers it is 0.19 against a weight of 0.063. A
            // producer with no damper at all assigns the target and fails
            // here -- and only here: after ten ticks it sits at exactly 1.0,
            // which the grid assert below also refuses, but by then the
            // reader would be sent after the wrong mechanism (review finding).
            w.TickAll(input);
            float target1 = math.length(w.PlayerAt(0).Vel) / cfg.Hero.MaxSpeed;
            Assert.Greater(target1, 0f,
                "премисса фикстуры: за тик сборщик обязан разогнаться, иначе демпферу нечего догонять");
            Assert.Greater(w.PlayerAt(0).LowerBlend, 0f,
                "за тик вес не сдвинулся — производителя у веса нет");
            Assert.Less(w.PlayerAt(0).LowerBlend, target1,
                "за ОДИН тик вес дошёл до цели тика — демпфера нет, вес присваивается целевым");

            // THEN TEN TICKS OF THE SAME WORLD. ⛔ THE TICK COUNT COMES FROM
            // THIS TASK'S OWN FORMULA, an explicit Euler step and not an
            // exponential: k = dt/T = (1/30)/0.1 = 1/3 of the REMAINING way
            // per tick, so after n ticks 1 - (2/3)^n of it is covered -- two
            // ticks give 0.556, three 0.704. Ten is ample even while the speed
            // itself is still ramping up under Accel.
            for (int i = 1; i < 10; i++) w.TickAll(input);
            float weight = w.PlayerAt(0).LowerBlend;
            Assert.Greater(weight, 0.5f,
                "вес залип: за 10 тиков он не дошёл до половины — производителя у веса нет");

            // ⛔ THE TRAP ITSELF, ASKED DIRECTLY: a weight kept in float carries
            // more than eight bits, so after a run of damper steps it sits OFF
            // the 1/255 grid (0.2931 codes off, on the fixture numbers).
            // Rounded to a byte inside the simulation it would land exactly on
            // k/255 every single tick -- and pass the assert above regardless,
            // because that sticking happens near the target, not below the
            // half-way mark (mutant M347).
            float codes = weight * 255f;
            Assert.Greater(math.abs(codes - math.round(codes)), 1e-3f,
                "вес лежит ровно на сетке 1/255 — его квантуют в байт внутри симуляции: "
                + "в float он несёт больше восьми бит (M347)");
        }

        [Test]
        public void APhasePastTheClipsEndHoldsItsLastRow()
        {
            // The row map at the tick rate (T6a): phase p is row p of its clip,
            // and past the clip's end the LAST row is held -- not the next
            // clip's first row, not a row that is not there. Looping is the
            // producer's business (T6b); the rate-aware map is fixture 18/18a.
            // A two-row clip followed by a one-row clip, one bone, rows told
            // apart by z: 0, 1 and then 9 for the foreign clip. ⚠ Both clips at
            // the tick rate, said explicitly: a hand-built table carries the
            // seventh field itself (T6c), or Sample refuses it by name.
            var table = new PoseTable
            {
                BoneCount = 1,
                ClipFirstRow = new[] { 0, 2, 3 },
                Bones = new[] { new float3(0f, 0f, 0f), new float3(0f, 0f, 1f), new float3(0f, 0f, 9f) },
                BlendThresholds = new[] { 0f },
                ClipRate = new[] { SimulationWorld.TickRate, SimulationWorld.TickRate },
            };
            var into = new float3[1];
            var held = new PoseKey { LowerClipA = 0, LowerClipB = 0, LowerPhase = 7 };
            PoseTable.Sample(in table, in held, singleLayer: true, into);
            Assert.AreEqual(table.Bones[1].z, into[0].z, 1e-6f,
                "фаза за концом клипа не удержала его последнюю строку");
            var inside = new PoseKey { LowerClipA = 0, LowerClipB = 0, LowerPhase = 1 };
            PoseTable.Sample(in table, in inside, singleLayer: true, into);
            Assert.AreEqual(table.Bones[1].z, into[0].z, 1e-6f,
                "фаза внутри клипа прочитала не свою строку");
        }

        [Test]
        public void SampleRefusesByName_NotByIndexOutOfRange()
        {
            // The refusals are this task's own, so they get their own witness:
            // each throws with the number in the message, and none is an
            // IndexOutOfRangeException from the middle of the bone loop.
            PoseTable table = TestConfigs.TwoClipWalkPose();
            var key = new PoseKey { LowerClipA = 0, LowerClipB = 1 };
            var into = new float3[table.BoneCount];

            var shortBuffer = new float3[table.BoneCount - 1];
            var ex1 = Assert.Throws<System.ArgumentException>(
                () => PoseTable.Sample(in table, in key, singleLayer: true, shortBuffer));
            Assert.That(ex1.Message, Does.Contain($"holds {table.BoneCount - 1} bones"));

            var missingClip = new PoseKey { LowerClipA = 0, LowerClipB = 2 };   // the table has clips 0 and 1
            var ex2 = Assert.Throws<System.ArgumentException>(
                () => PoseTable.Sample(in table, in missingClip, singleLayer: true, into));
            Assert.That(ex2.Message, Does.Contain("clip 2"));

            // An empty clip: the CSR says clip 1 starts AND ends at row 1.
            PoseTable emptyClip = table;
            emptyClip.ClipFirstRow = new[] { 0, 1, 1 };
            var ex3 = Assert.Throws<System.ArgumentException>(
                () => PoseTable.Sample(in emptyClip, in key, singleLayer: true, into));
            Assert.That(ex3.Message, Does.Contain("no rows"));

            // A sentinel past the bones: the CSR claims clip 1 has rows 1..2,
            // Bones holds two rows; phase 1 of clip 1 is row 2, and that row
            // does not exist. Neither the checksum nor rule 6 sees this.
            PoseTable overclaimed = table;
            overclaimed.ClipFirstRow = new[] { 0, 1, 3 };
            var pastTheBones = new PoseKey { LowerClipA = 0, LowerClipB = 1, LowerPhase = 1 };
            var ex4 = Assert.Throws<System.ArgumentException>(
                () => PoseTable.Sample(in overclaimed, in pastTheBones, singleLayer: true, into));
            Assert.That(ex4.Message, Does.Contain("past the bones"));

            // A clip the table carries no baking rate for (app-94sk T6c): a
            // table from before the seventh field, or a hand-built one that
            // forgot it -- refused by name, never read at some default speed.
            PoseTable noRate = table;
            noRate.ClipRate = new[] { SimulationWorld.TickRate };   // one rate, two clips
            var ex6 = Assert.Throws<System.ArgumentException>(
                () => PoseTable.Sample(in noRate, in key, singleLayer: true, into));
            Assert.That(ex6.Message, Does.Contain("no baking rate"));

            // A mask too short for the table on a two-layer body.
            PoseTable shortMask = TestConfigs.AimLayerPose();
            shortMask.UpperLayerMask = System.Array.Empty<ulong>();   // "no aim layer" -- legal, returns
            PoseTable.Sample(in shortMask, in key, singleLayer: false, into);
            PoseTable wideTable = shortMask;
            wideTable.BoneCount = 65;                                  // needs two words, has one
            wideTable.Bones = new float3[2 * 65];                      // two rows, so the rows themselves pass
            wideTable.UpperLayerMask = new[] { 0UL };
            var ex5 = Assert.Throws<System.ArgumentException>(
                () => PoseTable.Sample(in wideTable, in key, singleLayer: false, new float3[65]));
            Assert.That(ex5.Message, Does.Contain("needs 2"));
        }

        [Test]
        public void TheAimLayerMovesOnlyTheMaskedBones_AndNoneOfASingleLayerBody()
        {
            // Source 2 of the composition (spec §3.6): the aim layer lands on
            // the bones the mask names, by its weight, and a single-layer body
            // never consults it. The outcome-level witness is T6b's fixture 21
            // (through the hit volumes); this one asks the sampler directly.
            PoseTable table = TestConfigs.AimLayerPose();
            int n = table.BoneCount;
            const int Plain = 1, Masked = 2;
            float lo = table.Bones[0 * n + Masked].x, hi = table.Bones[1 * n + Masked].x;
            // Premises as properties: both bones differ between the rows, and
            // the mask names exactly the one it should.
            Assert.Greater(math.abs(table.Bones[1 * n + Plain].x - table.Bones[0 * n + Plain].x), 1e-4f,
                "премисса фикстуры: строки обязаны различаться в немаскированной кости");
            Assert.Greater(math.abs(hi - lo), 1e-4f,
                "премисса фикстуры: строки обязаны различаться в маскированной кости");
            Assert.AreNotEqual(0UL, table.UpperLayerMask[0] & (1UL << Masked),
                "премисса фикстуры: маска обязана называть кость 2");
            Assert.AreEqual(0UL, table.UpperLayerMask[0] & (1UL << Plain),
                "премисса фикстуры: маска НЕ должна называть кость 1");

            var full = new PoseKey { LowerClipA = 0, LowerClipB = 0, UpperClip = 1, UpperWeight = 255 };
            var pose = new float3[n];
            PoseTable.Sample(in table, in full, singleLayer: false, pose);
            Assert.AreEqual(hi, pose[Masked].x, 1e-6f,
                "маскированная кость не взяла позу прицела — верхний слой выброшен");
            Assert.AreEqual(table.Bones[0 * n + Plain].x, pose[Plain].x, 1e-6f,
                "немаскированная кость взяла позу прицела — маска не читается");

            // Half weight: the masked bone sits strictly between the rows.
            var half = new PoseKey { LowerClipA = 0, LowerClipB = 0, UpperClip = 1, UpperWeight = 128 };
            PoseTable.Sample(in table, in half, singleLayer: false, pose);
            Assert.That(pose[Masked].x, Is.InRange(math.min(lo, hi) + 1e-4f, math.max(lo, hi) - 1e-4f),
                "вес верхнего слоя не смешивает — берётся одна из крайних строк");

            // A single-layer body never consults the layer, mask or no mask.
            PoseTable.Sample(in table, in full, singleLayer: true, pose);
            Assert.AreEqual(lo, pose[Masked].x, 1e-6f,
                "однослойное тело взяло позу прицела — singleLayer не читается");
        }

        // ------------------------------------------------ app-94sk T6b (spec §3.5а/§3.6/§3.7)

        /// The collector's fixture table widened to every position BakedClips
        /// names for him (the death take is the last), `rows` rows per added
        /// clip. One factory for the four fixtures below, so they read one
        /// table rather than four spellings of it.
        static PoseTable CollectorTableWithEveryClip(int rows = 1)
            => TestConfigs.PaddedToClips(TestConfigs.HeroRestAndSlidePose(),
                BakedClips.Collector.Death + 1, rows);

        /// A shot spawned CLOSE, along +x, at a stated plan y and height --
        /// the shape fixtures 26/26a use, for the reason they give: a body
        /// this fixture leans or slides is not frozen in that state by the
        /// world (the tilt spring settles, the slide clock runs), so the round
        /// has to meet it on its first steps.
        static void ShootAlongX(SimulationWorld w, in SimConfig cfg, float fromX, float y, float height)
            => w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(fromX, y),
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height, velZ: 0f,
                cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime);

        static HitZone FirstHitZone(SimulationWorld w)
            => TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e) ? e.Zone : HitZone.None;

        [Test]
        public void ASlidingCollectorIsShotInTheSlidePose()   // fixture 17, the outcome half
        {
            // ⭐⭐ THE POSE CHANGES THE OUTCOME. Sliding, the collector's head
            // drops to 0.48 m and swings 0.41 m forward (his table's own slide
            // row); a round passing that point strikes his HEAD sliding and
            // nothing standing -- his standing legs stand 0.44 m off that line
            // and everything else stands higher. Not the profile ceiling of
            // Task 11 (SlideProfileTop, 0.55 in the fixtures), which the shot
            // passes under and which knows no plan: the round meets the slide
            // ROW through the volumes, with the clip the producer named.
            SimConfig cfg = TestConfigs.OpenField();
            // The slide's own thrust would carry him out of the geometry
            // between the spawn and the round; the fixture states a slide that
            // stays put (a fixture input, not a balance number).
            cfg.Hero.SlideSpeed = 0f;

            int slideRow = cfg.Hero.Poses.ClipFirstRow[BakedClips.Collector.Slide];
            HitPart head = TestWorlds.VolumeOfZone(cfg.Hero.Parts, HitZone.Head, "сборщик");
            float3 slidHead = cfg.Hero.Poses.Bones[slideRow * cfg.Hero.Poses.BoneCount + head.BoneB];
            Assert.Less(slidHead.y + head.Radius, cfg.Hero.SlideProfileTop + 0.5f,
                "премисса фикстуры: голова в слайде лежит низко, иначе слайд ничего не меняет");
            // The line: PAST the slid head's plan by a quarter of a meter and
            // a little above it -- 0.25 from the head bone against the head
            // capsule's reach of 0.38 (0.26 + the round's 0.12), and 0.51 from
            // the standing calf's axis against its 0.38 (measured on the T4b
            // rig: on the head's own plan that shin is struck at 0.26). The
            // body faces -y, the identity yaw of
            // a rig that looks down -z (BakedClips.CollectorForward), so the
            // table's plan IS the world's plan and no turn enters the numbers.
            float lineY = slidHead.z + 0.24f;
            float height = slidHead.y + 0.07f;
            Assert.Less(math.distance(new float2(lineY, height), new float2(slidHead.z, slidHead.y)),
                head.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: линия проходит в пределах досягаемости лежащей головы");

            HitZone Shoot(bool sliding)
            {
                var w = new SimulationWorld(17, cfg);
                var p = w.PlayerAt(0);
                p.Dir = new float2(0f, -1f);
                if (sliding) p.SlideTimer = cfg.Hero.SlideDuration;
                w.SetPlayerForTest(0, p);
                w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(-1.6f, lineY),
                    new float2(cfg.Weapon.ProjectileSpeed, 0f), height, velZ: 0f,
                    cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime,
                    ownerIndex: ProjectileIds.NoOwner);
                TestWorlds.RunUntilProjectilesDie(w);
                return TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent e)
                    ? e.Zone : HitZone.None;
            }

            Assert.AreEqual(HitZone.None, Shoot(sliding: false),
                "премисса фикстуры: стоящего сборщика этот выстрел обязан миновать");
            Assert.AreEqual(HitZone.Head, Shoot(sliding: true),
                "скользящий сборщик не подставил голову там, где она лежит в слайде — поза в объёмы не пришла");
        }

        [Test]
        public void ADeadCollectorsKeyNamesTheDeathTakeFromTheTickHeDied()   // fixture 17, the dead row (spec §3.5а)
        {
            // Dead: the death take, its phase counting from the tick of death
            // and HELD past the clip's end (a corpse does not loop and does
            // not stand back up into locomotion), the aim layer off, the
            // reaction fields cleared -- the pose PersistentPropsDirector
            // reads debris heights off (spec §3.5а).
            const int DeathRows = 3;
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.Poses = CollectorTableWithEveryClip(DeathRows);
            var w = new SimulationWorld(17, cfg);
            var input = new SimInput[1];
            input[0].MoveDir = new float2(1f, 0f);
            for (int i = 0; i < 6; i++) w.TickAll(input);   // alive and moving: the pair is a locomotion pair
            var alive = w.PlayerAt(0);
            Assert.AreNotEqual(BakedClips.Collector.Death, alive.LowerClipA,
                "премисса фикстуры: живой сборщик не в клипе смерти");
            alive.ReactionClip = 3; alive.ReactionPhase = 9;   // a gesture half played when the blow lands
            w.SetPlayerForTest(0, alive);

            w.KillPlayerForTest();
            const int DeadTicks = 5;
            for (int i = 0; i < DeadTicks; i++) w.TickAll(new SimInput[1]);
            var dead = w.PlayerAt(0);
            Assert.IsFalse(dead.Alive, "премисса фикстуры: сборщик мёртв");
            Assert.AreEqual(BakedClips.Collector.Death, dead.LowerClipA, "мёртвое тело не в клипе смерти (A)");
            Assert.AreEqual(BakedClips.Collector.Death, dead.LowerClipB, "мёртвое тело не в клипе смерти (B)");
            Assert.AreEqual(DeadTicks - 1, dead.LowerPhase,
                "фаза мёртвого тела не считается от тика смерти (или зациклилась, или встала на последней строке)");
            Assert.AreEqual(0, dead.UpperWeight, "слой прицела мёртвого тела не выключен");
            Assert.AreEqual(0, dead.UpperClip, "мёртвое тело всё ещё называет клип прицела");
            Assert.AreEqual(0, dead.ReactionClip, "поле реакции пережило смерть (клип)");
            Assert.AreEqual(0, dead.ReactionPhase, "поле реакции пережило смерть (фаза)");
            Assert.AreEqual(0, dead.LowerShare, "у мёртвого тела нет пары — доля обязана быть нулевой");
        }

        [Test]
        public void AMobsOneShotPlaysFromItsStateAndFreezesWhenItGoesDown()   // fixture 17, the downed row (spec §3.5а)
        {
            // A mob's lower layer follows its FSM: Telegraph plays the melee
            // take from its first row, one row a tick; going Downed FREEZES
            // the last live clip and phase (Ruling 45: the fall is the tilt
            // spring, there is no clip for it) and clears the reaction fields
            // (spec §3.5а, D-I3). Three rows per take so that a phase is
            // observable at all.
            const int Rows = 3;
            SimConfig cfg = TestConfigs.OpenField();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);
            cfg.Chaser.Poses = TestConfigs.PaddedToClips(cfg.Chaser.Poses,
                BakedClips.HighestPositionOf(MobType.Chaser) + 1, Rows);
            var w = new SimulationWorld(17, cfg);
            // "Already within AttackRange" (MobAiTests' own fixture): the FSM
            // walks Idle -> Chase -> Telegraph on its own clock, and the take
            // starts on the tick the ENTRY happens -- the number of ticks is
            // the FSM's, not a literal (lesson 839).
            w.SpawnMobForTest(MobType.Chaser, new float2(1.0f, 0f));
            Assert.Greater(cfg.Chaser.TelegraphSeconds, 4f * SimulationWorld.TickDt,
                "премисса фикстуры: замах длится дольше четырёх тиков, иначе FSM уйдёт из Telegraph раньше замера");
            int ticksToTelegraph = 0;
            while (w.Mobs[0].Ai != MobAiState.Telegraph && ticksToTelegraph < 10)
            {
                w.TickAll(new SimInput[1]);
                ticksToTelegraph++;
            }
            Assert.AreEqual(MobAiState.Telegraph, w.Mobs[0].Ai, "премисса фикстуры: тело вошло в замах");
            Assert.AreEqual(BakedClips.MeleeOf(MobType.Chaser), w.Mobs[0].LowerClipA,
                "замах не переключил нижний клип на удар");
            Assert.AreEqual(0, w.Mobs[0].LowerPhase, "одиночный тейк не начался с первой строки на тике входа");
            w.TickAll(new SimInput[1]);
            Assert.AreEqual(MobAiState.Telegraph, w.Mobs[0].Ai, "премисса фикстуры: тело ещё в замахе");
            Assert.AreEqual(1, w.Mobs[0].LowerPhase, "фаза одиночного тейка не идёт по строке за тик");

            // Knocked over on the next tick: the producer sees Telegraph once
            // more (phase 2), then TiltSystem puts the body down.
            var m = w.Mobs[0];
            m.Tilt = new float2(cfg.Chaser.TiltFallAngle * 1.2f, 0f);
            m.ReactionClip = 4; m.ReactionPhase = 7;
            w.SetMobForTest(0, m);
            w.TickAll(new SimInput[1]);
            Assert.AreEqual(MobAiState.Downed, w.Mobs[0].Ai, "премисса фикстуры: тело опрокинуто");
            for (int i = 0; i < 4; i++) w.TickAll(new SimInput[1]);
            Assert.AreEqual(BakedClips.MeleeOf(MobType.Chaser), w.Mobs[0].LowerClipA,
                "опрокинутое тело сменило клип — последний живой клип не заморожен");
            Assert.AreEqual(2, w.Mobs[0].LowerPhase,
                "фаза опрокинутого тела идёт дальше — она обязана замереть на входе в Downed");
            Assert.AreEqual(0, w.Mobs[0].ReactionClip, "поле реакции пережило вход в Downed (клип)");
            Assert.AreEqual(0, w.Mobs[0].ReactionPhase, "поле реакции пережило вход в Downed (фаза)");
        }

        [Test]
        public void TheCollectorsAimLayerMovesTheVolumesItMasks()   // fixture 21, witness of M348
        {
            // ⭐ TWO LAYERS, THROUGH THE OUTCOME. The aim layer's pose holds
            // the collector's head 0.8 m off his axis in this table, on the
            // bones the mask names; the locomotion rows keep it on the axis.
            // A round down the world's y axis at x = 0.8 strikes the HEAD only
            // if the producer names the aim clip at full weight AND the
            // resolver samples the collector with both layers -- the sampler
            // alone was witnessed in T6a.
            SimConfig cfg = TestConfigs.OpenField();
            HitPart head = TestWorlds.VolumeOfZone(cfg.Hero.Parts, HitZone.Head, "сборщик");
            const float Aside = 0.8f;
            PoseTable table = CollectorTableWithEveryClip();
            table = TestConfigs.WithBoneMoved(in table, BakedClips.Collector.AimNeutral, head.BoneA, new float3(Aside, 0f, 0f));
            table = TestConfigs.WithBoneMoved(in table, BakedClips.Collector.AimNeutral, head.BoneB, new float3(Aside, 0f, 0f));
            table.UpperLayerMask = new[] { (1UL << head.BoneA) | (1UL << head.BoneB) };
            cfg.Hero.Poses = TestConfigs.Sealed(table);
            float headHeight = 0.5f * (cfg.Hero.Poses.Bones[head.BoneA].y + cfg.Hero.Poses.Bones[head.BoneB].y);

            var w = new SimulationWorld(21, cfg);
            var p = w.PlayerAt(0);
            p.Dir = new float2(0f, -1f);   // the identity yaw of a -z rig: the table's plan is the world's
            w.SetPlayerForTest(0, p);
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(Aside, -1.6f),
                new float2(0f, cfg.Weapon.ProjectileSpeed), headHeight, velZ: 0f,
                cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime,
                ownerIndex: ProjectileIds.NoOwner);
            TestWorlds.RunUntilProjectilesDie(w);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent e),
                "голова, отведённая слоем прицела, не встретила выстрел — верхний слой сборщика выброшен (M348)");
            Assert.AreEqual(HitZone.Head, e.Zone, "встречен не тот объём");
            Assert.AreEqual(255, w.PlayerAt(0).UpperWeight, "живой сборщик держит слой прицела на полном весе");
            Assert.AreEqual(BakedClips.Collector.AimNeutral, w.PlayerAt(0).UpperClip, "клип прицела не назван");
        }

        [Test]
        public void TheCollectorsLocomotionPairFollowsTheTreesThresholds()   // the pair, the share and the loop
        {
            // The lower layer of a moving collector: the pair of tree children
            // around the damped parameter s, the SHARE within that pair from
            // the thresholds (not s itself -- PlayerState's own block), and a
            // phase that walks the pair's rows and WRAPS. Two rows per tree
            // clip, so the wrap is observable; the fixture's expectation is
            // its own arithmetic over the table's thresholds.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.Poses = CollectorTableWithEveryClip(rows: 2);
            float[] t = cfg.Hero.Poses.BlendThresholds;
            Assert.AreEqual(BakedClips.Collector.Tree.Length, t.Length,
                "премисса фикстуры: у дерева столько же порогов, сколько детей");
            var w = new SimulationWorld(19, cfg);
            var input = new SimInput[1];
            input[0].MoveDir = new float2(1f, 0f);

            int inTheMiddle = 0;
            int phasesSeenPastRest = 0;   // a bit per phase value seen while the pair is off the rest clip
            for (int tick = 1; tick <= 12; tick++)
            {
                w.TickAll(input);
                PlayerState p = w.PlayerAt(0);
                float s = p.LowerBlend;
                int i = t.Length - 1;
                for (int k = 0; k + 1 < t.Length; k++) if (s >= t[k] && s < t[k + 1]) { i = k; break; }
                int a = BakedClips.Collector.Tree[i];
                int b = BakedClips.Collector.Tree[math.min(i + 1, t.Length - 1)];
                Assert.AreEqual(a, p.LowerClipA, $"тик {tick}, s = {s:F4}: не тот клип A");
                Assert.AreEqual(b, p.LowerClipB, $"тик {tick}, s = {s:F4}: не тот клип B");
                float share = a == b ? 0f : (s - t[i]) / (t[i + 1] - t[i]);
                Assert.AreEqual(share, p.LowerShare / 255f, 1f / 255f + 1e-5f,
                    $"тик {tick}, s = {s:F4}: доля в паре не по порогам дерева");
                if (i > 0 && i + 1 < t.Length) inTheMiddle++;
                // The phase walks the pair's rows and wraps: two rows a clip
                // on this table, so it alternates once the pair has left the
                // one-row rest clip.
                if (a != BakedClips.Rest)
                {
                    // T6c: the wrap is at the clip's length in TICKS (TicksOf);
                    // on a fixture table every clip runs at the tick rate, so a
                    // row is a tick and the two-row clips alternate 0/1.
                    Assert.Less(p.LowerPhase, PoseTable.TicksOf(in cfg.Hero.Poses, a),
                        $"тик {tick}: фаза {p.LowerPhase} вышла за тики клипа {a} — петли нет");
                    phasesSeenPastRest |= 1 << p.LowerPhase;
                }
            }
            Assert.Greater(inTheMiddle, 0,
                "премисса фикстуры: за двенадцать тиков s обязано побывать между внутренними порогами");
            Assert.AreEqual(0b11, phasesSeenPastRest,
                "фаза локомоции не идёт по строкам клипа (0, 1, 0, 1 на двухстрочных клипах) — производитель её не двигает (M341)");
            // And the share is the SHARE, not s: at the last tick s sits past
            // the second threshold, where the two numbers differ by construction.
            // ⛔ THE LAST TICK'S PAIR IS WRITTEN BY HAND, not by the loop above
            // (which re-spells the producer's bracket and would agree with it
            // on a shared mistake): after twelve ticks flat out s is past
            // 0.66 -- 0.95 on the fixture numbers (T6a's own chain) -- and
            // under 1, so the pair is the tree's THIRD and FOURTH children,
            // Jog_Fwd_Loop and Sprint_Loop.
            PlayerState last = w.PlayerAt(0);
            Assert.That(last.LowerBlend, Is.InRange(t[2], 1f - 1e-6f),
                "премисса: к концу разгона s между третьим порогом и единицей");
            Assert.AreEqual(BakedClips.Collector.Tree[2], last.LowerClipA, "последний тик: клип A — не третий ребёнок дерева");
            Assert.AreEqual(BakedClips.Collector.Tree[3], last.LowerClipB, "последний тик: клип B — не четвёртый ребёнок дерева");
            Assert.Greater(last.LowerBlend, t[1], "премисса: к концу разгона s выше первого внутреннего порога");
            Assert.AreNotEqual((byte)math.round(last.LowerBlend * 255f), last.LowerShare,
                "доля равна s — она обязана считаться по порогам пары, а не копировать параметр дерева");
        }

        [Test]
        public void ATiltedMobIsShotWhereItLies()   // fixture 26, witness of M353
        {
            // ⭐⭐ THE TILT ENTERS THE POSE AND THE VOLUMES FOLLOW IT: a mob
            // lying at 85 degrees is shot LYING, not standing. The lying
            // chaser's chest capsule (0.83 m wide) rests 0.12 m above the
            // ground, spread along the side he fell to, 0.6 m wide of his axis
            // on either side; standing, that same lateral line at that height
            // meets nothing -- his shins end 0.55 m out and his chest floor is
            // 0.40 m up. ⚠ THE PLAN'S NUMBERS (0.185 / 0.511 / 0.695) WERE OF AN
            // OLDER TABLE and put the shot down the axis, where the standing
            // shins are hit at any height under 0.6; recomputed on the T4b rig.
            const int ChestBone = 2;                               // Chest in ChaserRestPose
            const float Tilt85 = 1.4835f;                          // 85 degrees, past TiltFallAngle 0.9
            const float Aside = -0.6f;                             // plan y of the line
            SimConfig cfg = TestConfigs.OpenField();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);   // BEFORE the constructor
            float3 chest = cfg.Chaser.Poses.Bones[ChestBone];
            // The chest bone's height once laid over towards +x: its height
            // times cos, LESS its plan offset along the fall times sin (the
            // side of the axis it stands on rises as the body goes down) --
            // 1.3567 * cos 85 + 0.0182 * sin 85 = 0.136 on this rig. The line
            // through it hits the 0.83 m capsule with 0.5 m to spare either way.
            float lyingHeight = chest.y * math.cos(Tilt85) - chest.x * math.sin(Tilt85);

            int ShotsLanded(float2 tilt)
            {
                var w = new SimulationWorld(26, cfg);
                w.SpawnMobForTest(MobType.Chaser, new float2(8f, 0f));
                var m = w.Mobs[0];
                m.Dir = new float2(0f, 1f);   // the table's own orientation
                m.Tilt = tilt;
                m.TiltVel = float2.zero;
                w.SetMobForTest(0, m);
                int before = w.StatsAt(0).ShotsHit;
                // Spawned CLOSE: the tilt spring keeps settling the lean
                // every tick (FreezeArchetype zeroes MaxSpeed/Accel, not the
                // spring), so the round has to judge the lean it was given.
                ShootAlongX(w, in cfg, fromX: 7.2f, y: Aside, height: lyingHeight);
                TestWorlds.RunUntilProjectilesDie(w);
                return w.StatsAt(0).ShotsHit - before;
            }

            // PREMISE, A WORLD OF ITS OWN: standing, the same round is a miss,
            // or the fixture is green without any tilt in the pose.
            Assert.AreEqual(0, ShotsLanded(float2.zero),
                "премисса фикстуры: по стоящему телу этот выстрел обязан быть промахом");
            Assert.AreEqual(1, ShotsLanded(new float2(Tilt85, 0f)),
                "выстрел в грудь лежащего тела прошёл мимо — объёмы остались стоять, крен в позу не вошёл (M353)");
        }

        [Test]
        public void ATiltedMobIsNotImmuneToFlatFire()   // fixture 26a, witness of M441 (spec risk Р-K)
        {
            // ⛔ THE PRICE OF WITHDRAWING Р375, PAID IN FULL: with the volumes
            // going down with the body, a toppled mob MUST NOT become
            // unhittable by flat fire -- and the way it WOULD is not the
            // volumes but the BROAD PHASE. The gather circle (GatherRadius,
            // rule 9) is the reach of a STANDING body; at exactly
            // TiltFallAngle (0.9 rad, the number "toppled" means here) the
            // chaser's chest lies 1.06 m along the fall, past that circle,
            // and a round fired ACROSS the fallen body through its chest at
            // muzzle height never enters the standing circle at all. A gather
            // that keeps the standing circle for a leaning body never asks
            // the volumes about him: immune. ⚠ The plan's 26a fired DOWN the
            // axis from behind the body, through the standing circle, where
            // the lying chest (0.83 m wide) is met from inside the circle on
            // any gather -- and an upright body is met at 1.0 m too, so it
            // witnessed nothing and had no red phase. This one has both.
            const int ChestBone = 2;
            SimConfig cfg = TestConfigs.OpenField();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);
            float3 chest = cfg.Chaser.Poses.Bones[ChestBone];
            float fall = cfg.Chaser.TiltFallAngle;
            float2 body = new float2(8f, 0f);

            // Premises IN NUMBERS: the lying chest's crown covers the muzzle
            // line (or a miss would be physics), and the line across it lies
            // OUTSIDE the standing gather circle (or the broad phase would
            // find it standing and the witness would be blind).
            float chestTop = chest.y * math.cos(fall) + cfg.Chaser.Parts[1].Radius + cfg.Weapon.ProjectileRadius;
            Assert.Greater(chestTop, cfg.Hero.MuzzleHeight,
                "премисса фикстуры: на этом крене линия дула обязана пересекать корпус");
            float chestAlong = chest.y * math.sin(fall) + chest.x * math.cos(fall);
            Assert.Greater(chestAlong + 0.3f, cfg.Chaser.GatherRadius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: линия поперёк лежащего корпуса проходит ВНЕ круга охвата стоящего тела");

            var w = new SimulationWorld(26, cfg);
            w.SpawnMobForTest(MobType.Chaser, body);
            var m = w.Mobs[0];
            m.Dir = new float2(0f, 1f);   // the table's own orientation
            m.Tilt = new float2(fall, 0f);   // exactly the fall threshold, towards +x
            m.TiltVel = float2.zero;
            w.SetMobForTest(0, m);
            int before = w.StatsAt(0).ShotsHit;
            // Flat fire ACROSS the fallen body: down +y, through the chest's
            // plan, at the collector's muzzle height, spawned close for the
            // reason fixture 26 gives.
            // 0.3 m further along the fall than the chest bone itself: still
            // deep inside the 0.83 m capsule, and 0.3 m clear of the standing
            // circle's edge rather than a hundredth.
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(body.x + chestAlong + 0.3f, -1.2f),
                new float2(0f, cfg.Weapon.ProjectileSpeed), cfg.Hero.MuzzleHeight, velZ: 0f,
                cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime);
            TestWorlds.RunUntilProjectilesDie(w);
            Assert.AreEqual(before + 1, w.StatsAt(0).ShotsHit,
                "опрокинутый моб стал непоражаемым настильным огнём — широкая фаза держит круг стоящего тела (M441)");
        }

        [Test]
        public void TheMemoAnswersFreshOnceAndForgetsOnInvalidate()   // PoseMemo's contract, witness of M445
        {
            // The memo's whole contract on one screen: an entry is fresh only
            // for a second reader in the same generation, each (slot, depth)
            // pair is its own entry, and Invalidate forgets them all at once.
            // The world bumps the generation after PoseSystem moves the mobs'
            // keys (a catch-up step inside the weapon phase samples before
            // that) and on RestoreState; a memo that forgot to forget would
            // hand a round yesterday's pose with today's stamp.
            var memo = new PoseMemo(bodies: 2, depths: 3, maxBones: 4);
            float3[] a = memo.Entry(1, 2, out bool fresh);
            Assert.IsFalse(fresh, "первое чтение записи обязано быть несвежим");
            Assert.AreEqual(4, a.Length, "буфер записи — на самое широкое тело");
            Assert.AreSame(a, memo.Entry(1, 2, out fresh), "второе чтение той же пары — тот же буфер");
            Assert.IsTrue(fresh, "второе чтение той же пары в том же поколении обязано быть свежим");
            memo.Entry(1, 1, out fresh);
            Assert.IsFalse(fresh, "другая глубина того же тела — другая запись (ключ — пара)");
            memo.Invalidate();
            memo.Entry(1, 2, out fresh);
            Assert.IsFalse(fresh, "после Invalidate ни одна запись не свежа (M445)");
        }

        [Test]
        public void TheBakedClipPositionsAreTheBakersOwn()   // instrument: BakedClips against PoseBaker.BakeSet
        {
            // ⛔⛔ THE POSITIONS THE PRODUCER NAMES, RE-MEASURED. BakedClips is
            // a list of numbers the baker's ordering fixes; nothing in a table
            // can confirm them, so this reads the CONTROLLERS the baker walks
            // (the same BakeSet, the same take names) and the COMMITTED tables'
            // clip counts, and prints the whole order on every mismatch -- the
            // measurement itself, not a hint. ⚠ AN INSTRUMENT, green on the
            // committed tree by construction; what it guards is a controller
            // gaining or losing a clip.
            SimConfig shipped = EditorBootstrapUtils.BuildShippedConfig();
            foreach (AnimatorCatalog.BodyEntry body in AnimatorCatalog.Bodies)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(body.PrefabPath);
                Assert.IsNotNull(prefab, $"{body.Kind}: префаб не найден");
                GameObject instance = Object.Instantiate(prefab);
                try
                {
                    var animator = instance.GetComponentInChildren<Animator>(true);
                    var takes = new List<string>();
                    foreach (AnimationClip c in PoseBaker.BakeSet(animator))
                        takes.Add(AnimatorCatalog.TakeOf(c.name));
                    string order = string.Join(", ", takes);

                    ref readonly PoseTable table = ref ShippedTableOf(in shipped, body.Kind);
                    Assert.AreEqual(takes.Count, PoseTable.ClipCount(in table),
                        $"{body.Kind}: коммитнутая таблица несёт не столько клипов, сколько набор пекаря [{order}]");

                    if (body.Kind == AnimatorCatalog.BodyKind.Collector)
                    {
                        Assert.AreEqual(AnimatorCatalog.CollectorIdleClip, takes[BakedClips.Rest],
                            $"сборщик: клип покоя не на месте [{order}]");
                        Assert.AreEqual(AnimatorCatalog.PackClipOf(Ring.Presentation.AnimIds.SlideLoopName),
                            takes[BakedClips.Collector.Slide], $"сборщик: слайд не на месте [{order}]");
                        int child = 0;
                        foreach (AnimatorCatalog.Entry e in AnimatorCatalog.LocomotionChildren())
                        {
                            Assert.AreEqual(e.PackClip, takes[BakedClips.Collector.Tree[child]],
                                $"сборщик: ребёнок дерева {child} не на месте [{order}]");
                            child++;
                        }
                        Assert.AreEqual(BakedClips.Collector.Tree.Length, child, "сборщик: детей дерева четыре");
                        Assert.AreEqual(Ring.Presentation.AnimIds.PistolAimNeutralName,
                            takes[BakedClips.Collector.AimNeutral], $"сборщик: поза прицела не на месте [{order}]");
                        Assert.AreEqual(takes.Count - 1, BakedClips.Collector.Death,
                            $"сборщик: смерть обязана быть последней [{order}]");
                        Assert.AreEqual(AnimatorCatalog.PackClipOf(Ring.Presentation.AnimIds.DeathName),
                            takes[BakedClips.Collector.Death], $"сборщик: клип смерти не на месте [{order}]");
                        continue;
                    }

                    // The mobs: which pack a body's controller came out of is
                    // measured against the generated controllers (AnimIds' own
                    // doc): the mechs for the two wave mobs, the Sci-Fi kit for
                    // the elite and the Director.
                    MobType type = MobTypeOf(body.Kind);
                    Ring.Presentation.AnimIds.MobClipSet clips = Ring.Presentation.AnimIds.ClipsFor(
                        type == MobType.Chaser || type == MobType.Gunner
                            ? Ring.Presentation.AnimIds.MobClipFamily.Mech
                            : Ring.Presentation.AnimIds.MobClipFamily.SciFiEnemy);
                    Assert.AreEqual(clips.Idle, Animator.StringToHash(takes[BakedClips.Rest]),
                        $"{body.Kind}: клип покоя не на месте [{order}]");
                    Assert.AreEqual(clips.Melee, Animator.StringToHash(takes[BakedClips.MeleeOf(type)]),
                        $"{body.Kind}: удар не на месте [{order}]");
                    Assert.AreEqual(clips.Ranged, Animator.StringToHash(takes[BakedClips.RangedOf(type)]),
                        $"{body.Kind}: выстрел не на месте [{order}]");
                    Assert.AreEqual(clips.Death, Animator.StringToHash(takes[takes.Count - 1]),
                        $"{body.Kind}: смерть обязана быть последней [{order}]");
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }
        }

        // ------------------------------------------------ app-94sk T6c (spec §3.5/§3.5а/§3.13)

        /// A hand-built table for the row map: ONE bone, ONE clip of `rows`
        /// rows whose bone's x IS its row index, at `rate` rows a second -- so
        /// the number a sample returns is the (fractional) row it read.
        static PoseTable OneClipRuler(int rows, int rate)
        {
            var bones = new float3[rows];
            for (int r = 0; r < rows; r++) bones[r] = new float3(r, 0f, 0f);
            return new PoseTable
            {
                BoneCount = 1,
                ClipFirstRow = new[] { 0, rows },
                Bones = bones,
                BlendThresholds = new[] { 0f },
                ClipRate = new[] { rate },
            };
        }

        /// One bone's x after sampling `clip` at `phase` (both lower slots on
        /// the clip, no blend, no aim layer).
        static float SampledX(in PoseTable table, int clip, int phase, int bone = 0)
        {
            var key = new PoseKey { LowerClipA = (byte)clip, LowerClipB = (byte)clip, LowerPhase = (ushort)phase };
            var into = new float3[table.BoneCount];
            PoseTable.Sample(in table, in key, singleLayer: true, into);
            return into[bone].x;
        }

        [Test]
        public void TheHeightGateShortensTheWorkWithoutMovingAnOutcome()   // fixture 8, an INSTRUMENT (no mutant, Р529)
        {
            // The cheap necessary condition in front of the capsules
            // (HitVolumes.Resolve, through HitZones.Overlaps against the LIVE
            // crown): a step wholly above the crown is refused before a single
            // capsule is probed, a step through the body walks every volume,
            // and NEITHER answer differs from the capsule test's own.
            // ⚠ AN INSTRUMENT, green on the committed tree by construction --
            // the gate has stood since T2; what is measured is that it refuses
            // only what the capsules refuse, and that it saves the work it
            // claims to. ⚠ A LEANING body skips the gate (Resolve's own doc: a
            // leaning crown is not a function of the pose's heights), so its
            // probes are counted in full and are not the gate's to save --
            // the third part says so in a number.
            SimConfig cfg = TestConfigs.OpenField();
            PoseTable table = TestConfigs.ChaserRestPose();
            HitPart[] parts = cfg.Chaser.Parts;
            float3[] pose = TestWorlds.RestPoseOf(in table);
            float3 origin = new float3(6f, 0f, 0f);
            float r = cfg.Weapon.ProjectileRadius;
            float crown = HitParts.PoseTop(parts, pose, table.BoneCount);
            Assert.Greater(crown, 0f, "премисса: у стоящего чейзера есть крона");
            int volumes = 0;
            foreach (HitPart part in parts)
                if (part.BoneA < table.BoneCount && part.BoneB < table.BoneCount) volumes++;
            Assert.AreEqual(parts.Length, volumes, "премисса: все объёмы чейзера стоят на костях таблицы");

            // (1) A level step ABOVE the crown, across the body: refused with
            // ZERO probes -- and the honest capsule test agrees on every volume,
            // so the gate moved no outcome.
            float3 hi0 = new float3(6f, -1.2f, crown + r + 0.05f), hi1 = new float3(6f, 1.2f, crown + r + 0.05f);
            HitVolumes.ResolveProbesForTest = 0;
            bool hitHigh = HitVolumes.Resolve(parts, in table, pose, float2.zero, origin, 0f, 1f, hi0, hi1, r,
                out _, out _, out _, out _, out _);
            Assert.IsFalse(hitHigh, "шаг над кроной засчитан попаданием");
            Assert.AreEqual(0, HitVolumes.ResolveProbesForTest,
                "шаг над кроной дошёл до капсул — высотный гейт не сокращает работу");
            foreach (HitPart part in parts)
            {
                float3 a = HitVolumes.ToWorld(pose[part.BoneA], origin, 0f, 1f, float2.zero);
                float3 b = HitVolumes.ToWorld(pose[part.BoneB], origin, 0f, 1f, float2.zero);
                Assert.IsFalse(Geometry.SegmentCapsule(hi0, hi1, r, a, b, part.Radius, out _),
                    $"гейт отказал шагу, который капсула объёма {part.PartId} принимает — гейт сдвинул исход");
            }

            // (2) The same step through the chest: a hit, and every volume was
            // asked -- the gate let it through and saved nothing, as it must.
            float3 lo0 = new float3(6f, -1.2f, 1.2f), lo1 = new float3(6f, 1.2f, 1.2f);
            HitVolumes.ResolveProbesForTest = 0;
            bool hitLow = HitVolumes.Resolve(parts, in table, pose, float2.zero, origin, 0f, 1f, lo0, lo1, r,
                out _, out _, out _, out _, out _);
            Assert.IsTrue(hitLow, "премисса: шаг сквозь корпус обязан попасть");
            Assert.AreEqual(volumes, HitVolumes.ResolveProbesForTest,
                "шаг сквозь тело не спросил каждый объём — узкая фаза срезает объёмы");

            // (3) A LEANING body, the high step again: the gate stands down and
            // every volume is probed -- the skip is the contract, not a saving.
            HitVolumes.ResolveProbesForTest = 0;
            HitVolumes.Resolve(parts, in table, pose, new float2(cfg.Chaser.TiltFallAngle, 0f), origin, 0f, 1f,
                hi0, hi1, r, out _, out _, out _, out _, out _);
            Assert.AreEqual(volumes, HitVolumes.ResolveProbesForTest,
                "накренённое тело прошло через высотный гейт — гейт читает крону стоящего у лежащего");
        }

        [Test]
        public void ARowBetweenTwoRowsIsInterpolated()   // fixture 18, witness of M342
        {
            // Spec §3.5: `row = phase_in_ticks * (rate / TickRate)`, and a row
            // that lands BETWEEN two baked rows is the linear mix of them --
            // not the nearer one, not the one below. A rate that is not a
            // multiple of the tick rate is where that fraction lives: at 24
            // rows a second a whole tick is 0.8 of a row.
            // ⚠ NO SHIPPED CLIP IS BAKED AT 24 (PoseBaker.RateOf bakes at 30
            // and 60, both multiples of the tick rate, so their fraction is
            // always zero -- fixture 18a is the shipped case); the table's
            // format admits any rate, and this is the one that makes the law
            // observable at all.
            const int Rate = 24, Rows = 5;
            PoseTable table = OneClipRuler(Rows, Rate);
            Assert.AreNotEqual(0, Rate % SimulationWorld.TickRate,
                "премисса фикстуры: один тик на этой частоте — дробная строка, иначе интерполяции нечего показать");
            // The fixture's own arithmetic, not the sampler's: phase 1 is 24/30
            // of a row, phase 2 is 48/30 = 1.6 rows.
            Assert.AreEqual(0.8f, SampledX(in table, 0, 1), 1e-6f,
                "фаза 1 на 24 строках в секунду обязана лечь на 0.8 строки — соседние строки не смешиваются (M342)");
            Assert.AreEqual(1.6f, SampledX(in table, 0, 2), 1e-6f,
                "фаза 2 на 24 строках в секунду обязана лечь на 1.6 строки");
            // A phase landing exactly on a row reads that row, nothing mixed in.
            Assert.AreEqual(4f, SampledX(in table, 0, 5), 1e-6f,
                "фаза 5 (120 строко-тиков = ровно строка 4) прочитала не строку 4");
            // Past the clip's end the LAST row is held, with no fraction into a
            // row that is not there (T6a's contract, kept).
            Assert.AreEqual(Rows - 1, SampledX(in table, 0, 9), 1e-6f,
                "фаза за концом клипа не удержала последнюю строку");
        }

        [Test]
        public void AFastTakeIsMappedAtItsOwnRate()   // fixture 18a, witness of M343 (the sampler) and M449 (the producer's clock)
        {
            // ⭐ THE SHIPPED CASE OF THE RATE: the fast takes -- the melee and
            // ranged strikes, the hit reactions, the slide's entry and exit --
            // are baked at 60 rows a second (PoseBaker.FastTakes, spec §3.5)
            // while the phase counts whole ticks at 30. One tick of such a take
            // is TWO rows, and the take lasts ceil(rows / 2) ticks -- an ODD
            // row count, so that the ceiling is the subject and not a
            // coincidence: the shipped chaser's punch is 35 rows, 18 ticks,
            // and a floor would drop its last row (review B1). Until this
            // task both readers took a row for a tick, and the punch played
            // at half the doll's speed.
            // ⚠ THE PLAN SAID "24 frames a second -- the gunner's pack": that
            // is the SOURCE clips' frame rate (COMBAT-001 §2.3), which the
            // baker resamples by absolute time; no baked clip is at 24.
            const int Rows = 5, Rate = 60;
            SimConfig cfg = TestConfigs.OpenField();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);   // BEFORE the constructor
            int melee = BakedClips.MeleeOf(MobType.Chaser);
            PoseTable table = TestConfigs.PaddedToClips(cfg.Chaser.Poses,
                BakedClips.HighestPositionOf(MobType.Chaser) + 1, Rows);
            // The take's rows are told apart by ONE bone's x: row r of the take
            // moves bone 0 by r along x from its rest position.
            int n = table.BoneCount;
            int first = table.ClipFirstRow[melee];
            for (int rr = 0; rr < Rows; rr++) table.Bones[(first + rr) * n] += new float3(rr, 0f, 0f);
            table.ClipRate[melee] = Rate;
            table = TestConfigs.Sealed(table);
            float restX = table.Bones[0].x;

            // THE SAMPLER: a whole tick of a 60-row-a-second take is two rows.
            // The fixture's own arithmetic: phase p is row p * 60 / 30.
            int rowOfTick1 = 1 * Rate / SimulationWorld.TickRate;
            Assert.AreEqual(2, rowOfTick1, "премисса фикстуры: на 60 строках в секунду тик — две строки");
            Assert.AreEqual(restX, SampledX(in table, melee, 0), 1e-6f, "фаза 0 — не первая строка тейка");
            Assert.AreEqual(restX + rowOfTick1, SampledX(in table, melee, 1), 1e-6f,
                "фаза 1 быстрого тейка прочитала не его третью строку — частота клипа не читается, строка = фаза (M343)");
            Assert.AreEqual(restX + (Rows - 1), SampledX(in table, melee, 2), 1e-6f,
                "фаза 2 пятистрочного тейка — ровно его последняя строка");
            Assert.AreEqual(restX + (Rows - 1), SampledX(in table, melee, 3), 1e-6f,
                "фаза за концом быстрого тейка не удержала его последнюю строку");

            // THE PRODUCER'S CLOCK: the take LASTS ceil(5 * 30 / 60) = 3 ticks
            // (not 2: a fifth row is half a tick, and half a tick is a tick),
            // and on the fourth the body is back in the rest loop -- the FSM
            // walk fixture 17's downed row makes (Idle -> Chase -> Telegraph on
            // the FSM's own clock, lesson 839).
            int takeTicks = (Rows * SimulationWorld.TickRate + Rate - 1) / Rate;   // ceil, the fixture's own
            Assert.AreEqual(3, takeTicks, "премисса фикстуры: тейк из пяти строк на 60 Гц длится три тика (ceil 2.5)");
            cfg.Chaser.Poses = table;
            var w = new SimulationWorld(18, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(1.0f, 0f));
            Assert.Greater(cfg.Chaser.TelegraphSeconds, (takeTicks + 2) * SimulationWorld.TickDt,
                "премисса фикстуры: замах длится дольше тейка с запасом, иначе FSM уйдёт из Telegraph раньше замера");
            int guard = 0;
            while (w.Mobs[0].Ai != MobAiState.Telegraph && guard < 10) { w.TickAll(new SimInput[1]); guard++; }
            Assert.AreEqual(MobAiState.Telegraph, w.Mobs[0].Ai, "премисса фикстуры: тело вошло в замах");
            Assert.AreEqual(melee, w.Mobs[0].LowerClipA, "замах не переключил нижний клип на удар");
            Assert.AreEqual(0, w.Mobs[0].LowerPhase, "тик входа — не первая фаза тейка");
            w.TickAll(new SimInput[1]);
            Assert.AreEqual(melee, w.Mobs[0].LowerClipA, "второй тик тейка: клип сменился раньше срока");
            Assert.AreEqual(1, w.Mobs[0].LowerPhase, "второй тик тейка: фаза не 1");
            w.TickAll(new SimInput[1]);
            Assert.AreEqual(MobAiState.Telegraph, w.Mobs[0].Ai, "премисса фикстуры: тело ещё в замахе");
            Assert.AreEqual(melee, w.Mobs[0].LowerClipA,
                "третий тик пятистрочного тейка на 60 Гц — ещё тейк: ceil(2.5) = 3, а не 2 — потолок сменился на пол (M456)");
            Assert.AreEqual(2, w.Mobs[0].LowerPhase, "третий тик тейка: фаза не 2");
            w.TickAll(new SimInput[1]);
            Assert.AreEqual(MobAiState.Telegraph, w.Mobs[0].Ai, "премисса фикстуры: тело всё ещё в замахе");
            Assert.AreEqual(BakedClips.Rest, w.Mobs[0].LowerClipA,
                "тейк из пяти строк на 60 Гц не кончился за три тика — часы производителя считают строки за тики (M449)");
            Assert.AreEqual(0, w.Mobs[0].LowerPhase, "после тейка петля покоя не началась с нуля");
        }

        [Test]
        public void ApplyConfigCarriesThePoseTableThrough()   // fixture 18б, witness of M344
        {
            // ⛔ A LIVE EDIT OF A NUMBER ON THE MILESTONE REBUILDS THE
            // CONFIGURATION. The table does not change, but it has to ARRIVE:
            // if it does not, the first such edit zeroes the poses and every
            // volume stands in the first phase of the first clip.
            // ⚠ HotTweakTests does not catch this on its own -- it walks the
            // FIELDS of PlayerState. ⚠ Green on `_config = next;` alone, so
            // its red phase is the state of mutant M344 (a field-by-field copy
            // that forgot the table) -- plan Step 1a.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(51, cfg);
            SimConfig tweaked = cfg;
            tweaked.Chaser.MaxSpeed += 0.5f;                  // a live edit of a number, not of topology
            w.ApplyConfig(in tweaked);                     // has to pass without a refusal
            // Premise: the table is NOT empty before the edit -- otherwise the
            // test is green on a zeroing too.
            Assert.Greater(cfg.Chaser.Poses.BoneCount, 0,
                "премисса фикстуры: у чейзера есть таблица, иначе ассерт ниже ничего не значит");
            Assert.AreEqual(cfg.Chaser.Poses.BoneCount, w.Config.Chaser.Poses.BoneCount,
                "ApplyConfig обнулил таблицу поз — объёмы встанут в первую фазу первого клипа (M344)");
            Assert.AreSame(cfg.Chaser.Poses.Bones, w.Config.Chaser.Poses.Bones,
                "ApplyConfig подменил массив костей — доехала не та таблица");
        }

        [Test]
        public void HotSwappingADifferentPoseTableIsRefused()   // fixture 18в, witness of M345
        {
            // ⛔⛔ WHY A REFUSAL AND NOT A SILENT SHIFT: PoseKey.LowerPhase
            // indexes the table's ROWS and lives in six rows of the rewind
            // history. Swapping the table under a live world puts already
            // written keys out of bounds. The rule's home is
            // ArenaTopologyMatches, where RewindCapTicks landed for the same
            // argument ("sizes PositionHistory's rows... and never resizes
            // them") and the item catalog too.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(51, cfg);
            SimConfig other = cfg;
            other.Chaser.Poses = TestConfigs.WithOneMoreRow(cfg.Chaser.Poses);   // another table of ONE body
            var ex = Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(in other),
                "чужая таблица чейзера принята на горячую (M345)");
            Assert.That(ex.Message, Does.Contain("pose table"));
            Assert.That(ex.Message, Does.Contain("restart"));
        }

        [Test]
        public void HotSwappingAnyBodysPoseTableIsRefused()   // fixture 18г, witness of M345 (the other four bodies)
        {
            // ⛔ FIVE BODIES, FIVE TABLES (T2), and a comparison written on the
            // chaser alone is green on exactly the body it was written on --
            // the Director's table would be swapped in silence.
            SimConfig cfg = TestConfigs.Open();
            foreach (string body in new[] { "Hero", "Chaser", "Gunner", "Elite", "Director" })
            {
                var w = new SimulationWorld(51, cfg);
                SimConfig other = cfg;
                ref PoseTable t = ref TestConfigs.PoseTableOfSection(ref other, body);
                t = TestConfigs.WithOneMoreRow(t);
                var ex = Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(in other),
                    $"подмена таблицы тела {body} принята на горячую");
                Assert.That(ex.Message, Does.Contain("pose table"));
            }
        }

        [Test]
        public void HotSwappingADifferentPartCountIsRefused()   // fixture 18е, witness of M450 (rule 11's other half)
        {
            // Rule 11's other half: the NUMBER of volumes on a body is
            // topology -- a PartId names a volume on the wire and in the events
            // already journaled -- while the volumes' NUMBERS (radius,
            // multiplier) stay the owner's hot knobs (spec §3.5а, item 4). One
            // more volume on the chaser, nothing else moved: a restart.
            SimConfig cfg = TestConfigs.Open();
            var w = new SimulationWorld(51, cfg);
            SimConfig other = cfg;
            var grown = new HitPart[cfg.Chaser.Parts.Length + 1];
            System.Array.Copy(cfg.Chaser.Parts, grown, cfg.Chaser.Parts.Length);
            grown[grown.Length - 1] = cfg.Chaser.Parts[0];
            other.Chaser.Parts = grown;
            var ex = Assert.Throws<System.ArgumentException>(() => w.ApplyConfig(in other),
                "лишний объём чейзера принят на горячую (M450)");
            Assert.That(ex.Message, Does.Contain("hit parts"));
            Assert.That(ex.Message, Does.Contain("restart"));
            // And a RADIUS is not topology: the same body, one capsule wider,
            // is a tweak and goes through.
            SimConfig wider = cfg;
            wider.Chaser.Parts = (HitPart[])cfg.Chaser.Parts.Clone();
            wider.Chaser.Parts[0].Radius += 0.05f;
            Assert.DoesNotThrow(() => new SimulationWorld(51, cfg).ApplyConfig(in wider),
                "радиус капсулы — горячая ручка владельца, а не топология");
        }

        [Test]
        public void TheKeyCannotSaturateOnAnyArchetypesFallAngle()   // fixture 18д, witness of M396 (rule 15a)
        {
            // ⛔ THE SUBJECT IS NOT THE FORMULA BUT WHAT THE KEY CAN SAY: a body
            // fallen to exactly TiltFallAngle has to rewind to where it fell.
            // The packer clamps a lean BY LENGTH at PoseKey.TiltCeiling (127
            // codes of TiltQuantStep), and rule 15a is that same comparison
            // asked of the configuration: the clamp must not bite at any
            // archetype's threshold. Three parts: the fixture numbers pack
            // unsaturated (the plan's own half); the build refuses a threshold
            // the key cannot hold; and the Inspector's [Range] ceiling is the
            // rule's, so the slider never offers what the build refuses.
            SimConfig cfg = TestConfigs.Default();
            foreach (MobType t in new[] { MobType.Chaser, MobType.Gunner, MobType.Elite, MobType.Director })
            {
                ref readonly MobSimConfig m = ref SimConfig.MobConfigFor(in cfg, t);
                var fallen = new MobState { Type = t, Tilt = new float2(m.TiltFallAngle, 0f), Dir = new float2(0f, 1f) };
                PoseKey key = PoseKey.FromMob(in fallen);
                // ⚠ NOT `|TiltX| < 127` (the plan's own line): 127 codes IS the
                // ceiling and expresses 1.27 exactly, and a threshold set to
                // the slider's maximum packs to 127 without saturating. A
                // saturated pack shows up as an unpack that MISSES the
                // threshold -- the length clamp scaled it -- and that is what
                // is asked (review B2).
                Assert.AreEqual(m.TiltFallAngle, key.TiltX * PoseKey.TiltQuantStep, PoseKey.TiltQuantStep,
                    $"{t}: порог падения насыщает ключ (кламп по длине сработал) — отмотанная поза разойдётся с судейской");
            }

            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            c.TiltFallAngle = PoseKey.TiltCeiling * 1.5f;
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis),
                "порог падения выше потолка ключа принят сборкой (M396)");
            Assert.That(ex.Message, Does.Contain("Chaser.TiltFallAngle"));

            var field = typeof(Ring.Data.MobConfig).GetField(nameof(Ring.Data.MobConfig.TiltFallAngle));
            var range = (UnityEngine.RangeAttribute)field.GetCustomAttributes(typeof(UnityEngine.RangeAttribute), false)[0];
            Assert.LessOrEqual(range.max, PoseKey.TiltCeiling,
                "инспектор предлагает порог падения выше потолка ключа — ползунок и сборка разошлись");
        }

        [Test]
        public void ATableMissingAPositionTheProducerNamesIsRefusedAtBuild()   // fixture 18ж, witness of M451 (rule 15)
        {
            // Rule 15, read at the configuration (spec §3.13): every clip the
            // producer can put in a key is a POSITION fixed by BakedClips, and
            // a table without that position makes the producer hold the rest
            // pose for it IN SILENCE (PoseSystem.ClipOrRest) -- a fixture
            // table's deliberate shape, a shipped table's defect. The build is
            // the one gate the shipped game runs, so it refuses there, by
            // section name and by the position that is missing.
            // ⚠ The builder's test-side door widens a fixture table for the
            // builder (TestConfigs.ForTheBuilder); this fixture hands the
            // narrow table in DIRECTLY, past the door, to reach the rule.
            var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
            c.Poses = ConfigTests.FixtureTable(TestConfigs.ChaserRestPose());   // one clip, no strike take
            int needed = BakedClips.HighestPositionOf(MobType.Chaser);
            Assert.Less(PoseTable.ClipCount(in ConfigTests.PoseTableFor(c)), needed + 1,
                "премисса: таблица чейзера короче позиции, которую называет производитель");
            var ex = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis),
                "таблица без клипа удара принята сборкой — производитель будет держать покой молча (M451)");
            Assert.That(ex.Message, Does.Contain("Chaser.Poses"));
            Assert.That(ex.Message, Does.Contain($"position {needed}"));

            // The collector's positions reach the death take, last by the
            // baker's contract: his two-clip fixture table is short of it too.
            var (h2, w2, c2, g2, wv2, a2, vis2) = ConfigTests.MakeDefaults();
            h2.Poses = ConfigTests.FixtureTable(TestConfigs.HeroRestAndSlidePose());
            var ex2 = Assert.Throws<System.ArgumentException>(
                () => ConfigTests.BuildShipped(h2, w2, c2, g2, wv2, a2, vis2),
                "таблица сборщика без клипа смерти принята сборкой");
            Assert.That(ex2.Message, Does.Contain("Hero.Poses"));
            Assert.That(ex2.Message, Does.Contain($"position {BakedClips.Collector.HighestPosition}"));
        }

        [Test]
        public void AMalformedTableIsRefusedAtBuild_NotInTheCombatPath()   // fixture 18з, witness of M452/M453/M454/M455 (rule 15)
        {
            // The refusals PoseTable.Sample makes by name (an empty clip, a
            // sentinel past the bones, a rate the table does not carry) are
            // CRASHES when reached from the combat path; rule 15 makes each of
            // them a build refusal instead, so a shipped table can never reach
            // them. Four shapes, each on a table the checksum has been re-sealed
            // over -- rule 27 is silent, only the shape rule speaks.
            const int Clips = 6;
            PoseTable Padded() => TestConfigs.PaddedToClips(TestConfigs.ChaserRestPose(), Clips);

            void Refuses(System.Func<PoseTable, PoseTable> mangle, string phrase, string why)
            {
                var (h, w, c, g, wv, a, vis) = ConfigTests.MakeDefaults();
                c.Poses = ConfigTests.FixtureTable(TestConfigs.Sealed(mangle(Padded())));
                var ex = Assert.Throws<System.ArgumentException>(
                    () => ConfigTests.BuildShipped(h, w, c, g, wv, a, vis), why);
                Assert.That(ex.Message, Does.Contain("Chaser.Poses"), why);
                Assert.That(ex.Message, Does.Contain(phrase), why);
            }

            // (1) The sentinel claims a row the bones do not hold -- the one
            // malformation neither the checksum nor rule 6 sees (RowOf's doc).
            Refuses(t => { t.ClipFirstRow[Clips] += 1; return t; }, "row count",
                "сентинел за костями принят сборкой (M452)");
            // (2) A clip with no rows: clip 3 starts where clip 4 starts.
            Refuses(t => { t.ClipFirstRow[4] = t.ClipFirstRow[3]; return t; }, "no rows",
                "клип без строк принят сборкой (M453)");
            // (3) Rates for two clips on a table of six.
            Refuses(t => { t.ClipRate = new[] { SimulationWorld.TickRate, SimulationWorld.TickRate }; return t; },
                "rates", "таблица с частотами не на каждый клип принята сборкой (M454)");
            // (4) The first clip does not start at row 0 -- the rest row rule 13
            // stands on is no longer row 0. One extra row of bones so that the
            // sentinel and every clip stay honest, and ONLY the first row is off.
            Refuses(t =>
            {
                PoseTable wide = TestConfigs.PaddedToClips(t, Clips + 1);   // seven clips, seven rows
                var first = new int[Clips + 1];
                for (int i = 0; i < first.Length; i++) first[i] = i + 1;   // 1..7: six clips, sentinel 7 = the rows
                wide.ClipFirstRow = first;
                wide.ClipRate = new int[Clips];
                for (int i = 0; i < Clips; i++) wide.ClipRate[i] = SimulationWorld.TickRate;
                return wide;
            }, "first row", "таблица, чей первый клип начинается не со строки 0, принята сборкой (M455)");
            // (5) A rate of zero on one clip: RateOf refuses it by name from the
            // combat path, so the build has to first (review B4).
            Refuses(t => { t.ClipRate[2] = 0; return t; }, "rows a second",
                "таблица с нулевой частотой клипа принята сборкой (M457)");
        }

        // ------------------------------------------------ app-94sk T7 (spec §3.6/§3.7): the pose in the rewind history

        /// A round along +x with a rewind DEPTH -- fixture 17's shape plus the
        /// depth the T7 fixtures count on; owned by nobody, so it may strike
        /// player 0.
        static void ShootAlongXRewound(SimulationWorld w, in SimConfig cfg, float fromX, float y, float height,
            byte depth)
            => w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(fromX, y),
                new float2(cfg.Weapon.ProjectileSpeed, 0f), height, velZ: 0f,
                cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime,
                ownerIndex: ProjectileIds.NoOwner, rewindLeft: depth);

        /// The line of fixture 17 -- past the slid head's plan by a quarter of
        /// a meter, a little above it -- and where a round has to START so
        /// that its FIRST step meets the head: the step whose rewind depth the
        /// fixtures below count on. The premises are properties of the table.
        static void SlidHeadLine(in SimConfig cfg, out float lineY, out float height, out float fromX)
        {
            int slideRow = cfg.Hero.Poses.ClipFirstRow[BakedClips.Collector.Slide];
            HitPart head = TestWorlds.VolumeOfZone(cfg.Hero.Parts, HitZone.Head, "сборщик");
            float3 slidHead = cfg.Hero.Poses.Bones[slideRow * cfg.Hero.Poses.BoneCount + head.BoneB];
            lineY = slidHead.z + 0.24f;
            height = slidHead.y + 0.07f;
            float reach = head.Radius + cfg.Weapon.ProjectileRadius;
            float step = cfg.Weapon.ProjectileSpeed * SimulationWorld.TickDt;
            fromX = slidHead.x - 0.7f;
            Assert.Less(math.distance(new float2(lineY, height), new float2(slidHead.z, slidHead.y)), reach,
                "премисса фикстуры: линия проходит в пределах досягаемости лежащей головы");
            Assert.Less(fromX, slidHead.x - reach, "премисса: старт раньше входа в капсулу головы");
            Assert.Less(slidHead.x, fromX + step, "премисса: голова внутри ПЕРВОГО шага снаряда");
        }

        /// A collector who slid on exactly ONE recorded tick and stands now --
        /// the past fixtures 22/22a/25 rewind into. Returns the world at the
        /// end of tick 4 with the slide on row 2; `slidTick` says which. He
        /// faces the table's own way, so the table's plan is the world's.
        static SimulationWorld CollectorWhoSlidOnce(in SimConfig cfg, out int slidTick)
        {
            var w = new SimulationWorld(22, cfg);
            var p = w.PlayerAt(0);
            p.Dir = BakedClips.CollectorForward;
            w.SetPlayerForTest(0, p);
            var idle = new SimInput[1];
            w.TickAll(idle);                                                   // row 1: standing
            p = w.PlayerAt(0); p.SlideTimer = cfg.Hero.SlideDuration; w.SetPlayerForTest(0, p);
            w.TickAll(idle);                                                   // row 2: sliding
            slidTick = w.CurrentTick;
            p = w.PlayerAt(0); p.SlideTimer = 0f; w.SetPlayerForTest(0, p);
            w.TickAll(idle); w.TickAll(idle);                                  // rows 3, 4: standing again
            Assert.AreEqual(0f, w.PlayerAt(0).SlideTimer, "премисса: сейчас сборщик стоит");
            return w;
        }

        [Test]
        public void ARewoundRoundMeetsThePoseTheBodyHeldThen()   // fixture 22, witness of M349
        {
            // ⭐⭐ THE WITNESS OF T7: the round is judged against the pose the
            // body HELD at the rewound tick, not the pose it holds now. The
            // collector slid on one recorded tick and stands now; a round with
            // exactly the depth that lands on that tick meets his HEAD where
            // it lay in the slide (fixture 17's line: a miss on the standing
            // body, a head shot on the sliding one), because the record of
            // that tick carries the slide's key and the resolver samples it.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.SlideSpeed = 0f;
            SlidHeadLine(in cfg, out float lineY, out float height, out float fromX);
            // Un-rewound, the standing body is a miss -- or the fixture is
            // green on any key at all (fixture 17's own premise, re-asked here
            // on the world this fixture builds).
            var live = CollectorWhoSlidOnce(in cfg, out _);
            ShootAlongXRewound(live, in cfg, fromX, lineY, height, depth: 0);
            live.TickAll(new SimInput[1]);
            Assert.IsFalse(TestEvents.TryFirstOf(live, SimEventKind.PlayerDamaged, out _),
                "премисса фикстуры: по стоящему телу без отмотки этот выстрел обязан быть промахом");

            var w = CollectorWhoSlidOnce(in cfg, out int slidTick);
            // The first step is judged on the NEXT tick and asks CurrentTick+1 - depth.
            int depth = w.CurrentTick + 1 - slidTick;
            Assert.That(depth, Is.InRange(1, cfg.Arena.RewindCapTicks), "премисса: глубина внутри капа отмотки");
            ShootAlongXRewound(w, in cfg, fromX, lineY, height, (byte)depth);
            w.TickAll(new SimInput[1]);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent e),
                "отмотанный выстрел не встретил лежащую в слайде голову — отмотка берёт текущую позу, а не прошлую (M349)");
            Assert.AreEqual(HitZone.Head, e.Zone, "встречен не тот объём");
        }

        [Test]
        public void ARewoundRoundMeetsTheCourseTheBodyHeldThen()   // fixture 22a, witness of M458
        {
            // The record's key carries the body's COURSE too (Facing), and the
            // rewound volumes turn by it: the collector who slid facing the
            // table's way has since turned a quarter turn, and the round with
            // the depth of the slid tick still meets his head where it lay --
            // a resolver that placed the past pose on the PRESENT course would
            // swing that head a quarter turn off the line (the plan `(x, z)`
            // turns to `(-z, x)` under YawOf for a course of (1, 0) on a -z
            // rig: the head at y = 0.41 goes to y = 0, and the line at 0.65
            // passes 0.65 wide of it against a reach of 0.38).
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.SlideSpeed = 0f;
            SlidHeadLine(in cfg, out float lineY, out float height, out float fromX);
            var w = CollectorWhoSlidOnce(in cfg, out int slidTick);
            var p = w.PlayerAt(0);
            p.Dir = new float2(1f, 0f);   // turned since: a quarter turn from the table's -z
            w.SetPlayerForTest(0, p);
            w.TickAll(new SimInput[1]);   // one standing row on the new course, so the turn is on record too
            Assert.AreEqual(new float2(1f, 0f), w.PlayerAt(0).Dir, "премисса: курс удержан холостым тиком");
            int depth = w.CurrentTick + 1 - slidTick;
            Assert.That(depth, Is.InRange(1, cfg.Arena.RewindCapTicks), "премисса: глубина внутри капа отмотки");
            ShootAlongXRewound(w, in cfg, fromX, lineY, height, (byte)depth);
            w.TickAll(new SimInput[1]);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent e),
                "отмотанный выстрел не встретил голову там, где она лежала при ТОМ курсе — отмотанная поза поставлена на нынешний курс (M458)");
            Assert.AreEqual(HitZone.Head, e.Zone, "встречен не тот объём");
        }

        [Test]
        public void ARewoundRoundMeetsTheLeanTheBodyHeldThen()   // fixture 22b, witness of M459
        {
            // The record's key carries the LEAN too (TiltX/TiltZ), and the
            // rewound volumes go down by it: a chaser who lay at the fall angle
            // on one recorded tick and stands upright now is met LYING by the
            // round whose depth lands on that tick -- fixture 26a's line across
            // the fallen chest, OUTSIDE the standing circle, so a resolver that
            // leaned the past pose by the PRESENT tilt would not even gather
            // him. The spring is overruled every tick by the test seam: the
            // lean is an input here, not a number.
            const int ChestBone = 2;
            SimConfig cfg = TestConfigs.OpenField();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);
            float3 chest = cfg.Chaser.Poses.Bones[ChestBone];
            float fall = cfg.Chaser.TiltFallAngle;
            float2 body = new float2(8f, 0f);
            float chestTop = chest.y * math.cos(fall) + cfg.Chaser.Parts[1].Radius + cfg.Weapon.ProjectileRadius;
            Assert.Greater(chestTop, cfg.Hero.MuzzleHeight,
                "премисса фикстуры: на этом крене линия дула обязана пересекать корпус");
            float chestAlong = chest.y * math.sin(fall) + chest.x * math.cos(fall);
            Assert.Greater(chestAlong + 0.3f, cfg.Chaser.GatherRadius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: линия поперёк лежащего корпуса проходит ВНЕ круга охвата стоящего тела");

            var w = new SimulationWorld(26, cfg);
            w.SpawnMobForTest(MobType.Chaser, body);
            var idle = new SimInput[1];
            void SetTilt(float angle)
            {
                var m = w.Mobs[0];
                m.Dir = BakedClips.MobForward;   // the table's own orientation
                m.Tilt = new float2(angle, 0f);
                m.TiltVel = float2.zero;
                w.SetMobForTest(0, m);
            }
            SetTilt(0f); w.TickAll(idle);          // row 1: upright
            SetTilt(fall); w.TickAll(idle);        // row 2: lying at the fall angle (the key is packed before the spring steps)
            int lyingTick = w.CurrentTick;
            SetTilt(0f); w.TickAll(idle);          // rows 3, 4: upright again
            SetTilt(0f); w.TickAll(idle);
            SetTilt(0f);
            int depth = w.CurrentTick + 1 - lyingTick;
            Assert.That(depth, Is.InRange(1, cfg.Arena.RewindCapTicks), "премисса: глубина внутри капа отмотки");
            Assert.Less(math.length(w.Mobs[0].Tilt), 1e-3f, "премисса: сейчас тело стоит прямо");
            int before = w.StatsAt(0).ShotsHit;
            w.SpawnProjectileForTest(ProjectileOwner.Player, new float2(body.x + chestAlong + 0.3f, -1.2f),
                new float2(0f, cfg.Weapon.ProjectileSpeed), cfg.Hero.MuzzleHeight, velZ: 0f,
                cfg.Weapon.Damage, cfg.Weapon.ProjectileRadius, cfg.Weapon.ProjectileLifetime,
                rewindLeft: (byte)depth);
            w.TickAll(idle);
            Assert.AreEqual(before + 1, w.StatsAt(0).ShotsHit,
                "отмотанный выстрел поперёк лежавшего корпуса прошёл мимо — отмотанная поза накренена нынешним креном (M459)");
        }

        [Test]
        public void TheHistorysFoldWalksEveryFieldOfTheKey()   // fixture 23, witness of M350
        {
            // ⛔ A REFLECTIVE SWEEP CANNOT PROVE THIS: it would show that
            // "something of the record is hashed". So the key's fields are
            // walked BY NAME, one bumped at a time inside ONE recorded row of a
            // saved world, and the digest of the restored world has to move on
            // every one of them. The record's size and the key's offset are
            // pinned beside it -- the spec's 24 bytes, 190.3 KiB of history.
            System.Type rt = typeof(PositionHistory.Record);
            Assert.AreEqual(24, Marshal.SizeOf(rt), "запись истории обязана занимать ровно 24 байта");
            Assert.AreEqual(10, (int)Marshal.OffsetOf(rt, nameof(PositionHistory.Record.Key)),
                "ключ — за позицией и флагами, с одним байтом выравнивания");

            SimConfig cfg = TestConfigs.OpenField();
            var a = new SimulationWorld(23, cfg);
            a.TickAll(new SimInput[1]);
            ulong baseline = a.StateHash();
            // The restore is faithful before anything is bumped -- or every
            // inequality below would be the restore's, not the fold's.
            var same = new SimulationWorld(23, cfg);
            same.RestoreState(a.SaveState());
            Assert.AreEqual(baseline, same.StateHash(), "премисса: нетронутое сохранение восстанавливается в тот же хеш");

            int slot = a.PlayerAt(0).HistorySlot;
            int bodies = cfg.Arena.MaxMobs + cfg.Arena.MaxPlayers;
            FieldInfo[] fields = typeof(PoseKey).GetFields(BindingFlags.Public | BindingFlags.Instance);
            Assert.AreEqual(12, fields.Length, "сторож: у ключа двенадцать полей");
            foreach (FieldInfo f in fields)
            {
                WorldSave save = a.SaveState();
                int rowIndex = System.Array.IndexOf(save.HistoryRowTicks, a.CurrentTick);
                Assert.GreaterOrEqual(rowIndex, 0, "премисса: строка этого тика есть в сохранении");
                int at = rowIndex * bodies + slot;   // the ring's own index arithmetic, re-spelled (lesson 427)
                PositionHistory.Record r = save.HistoryRows[at];
                Assert.AreNotEqual(0, r.Flags & PositionHistory.FlagAlive, "премисса: запись живого сборщика");
                object box = r.Key;
                f.SetValue(box, BumpKeyField(f.GetValue(box)));
                save.HistoryRows[at] = new PositionHistory.Record(r.Pos, r.Flags, (PoseKey)box);
                var b = new SimulationWorld(23, cfg);
                b.RestoreState(save);
                Assert.AreNotEqual(baseline, b.StateHash(), $"PoseKey.{f.Name} не входит в свёртку истории (M350)");
            }
        }

        /// A step of one FOR THE KEY'S OWN TYPES -- ushort, byte, sbyte -- which
        /// the state sweeps' Bump never meets (they live only inside the packing).
        static object BumpKeyField(object v) => v switch
        {
            ushort u => (object)(ushort)(u + 1),
            byte b => (object)(byte)(b + 1),
            sbyte sb => (object)(sbyte)(sb + 1),
            _ => throw new System.NotSupportedException(v.GetType().Name),
        };

        [Test]
        public void TheKeyCarriesTheTiltAtJudgementTime_NotAtTheEndOfTheTick()   // fixture 23a, witness of M382
        {
            // ⛔ THE INVARIANT, STATED HONESTLY (spec §3.6, D-I2). Tick order:
            // movement -> weapon -> AI -> separation -> PoseSystem -> rounds ->
            // TiltSystem -> ... -> PositionHistory.Write on the last line. The
            // key is packed BEFORE the rounds are judged, so it carries the
            // lean of the START of the tick, and the record has to carry THAT
            // key: a Write that packed a key itself, off the live fields on
            // the last line, would record the lean TiltSystem left at the END
            // of the tick, one tick off from what the shot was judged against.
            // The one-tick disagreement between the record's TiltX and m.Tilt
            // is LEGAL and pinned here so that nobody "fixes" it.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(1, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            var m = w.Mobs[0];
            m.Tilt = new float2(0.30f, 0f);
            // A lean that WILL move by at least one code this tick. ⚠ The
            // plan's 0.80 rad/s does not: the spring (k ~ 65, c ~ 8.9 on the
            // fixture's 0.55 / 0.9 s) pulls it back within the tick to 0.297,
            // still code 30. At 3.0 rad/s the step is v' = 3 - (19.6 + 26.7)/30
            // = 1.46, x' = 0.30 + 1.46/30 = 0.349 -> code 35 (replica).
            m.TiltVel = new float2(3.0f, 0f);
            w.SetMobForTest(0, m);
            float2 tiltBefore = w.Mobs[0].Tilt;
            w.TickAll(new SimInput[1]);
            float2 tiltAfter = w.Mobs[0].Tilt;
            Assert.Greater(math.abs(tiltBefore.x - tiltAfter.x), 1e-4f,
                "премисса фикстуры: за тик крен обязан сдвинуться, иначе «до» и «после» неразличимы");
            Assert.IsTrue(w.History.PosAt(w.Mobs[0].HistorySlot, w.CurrentTick, w.Mobs[0].Pos,
                    out PositionHistory.Record rec, out bool fromRow) && fromRow,
                "премисса фикстуры: запись этого тика обязана быть в истории");
            Assert.AreNotEqual(Quant(tiltAfter.x), Quant(tiltBefore.x),
                "премисса: квантованные «до» и «после» обязаны различаться, иначе ассерт ниже зелен на обеих реализациях");
            Assert.AreEqual(Quant(tiltBefore.x), rec.Key.TiltX,
                "ключ снят ПОСЛЕ TiltSystem — расхождение на тик стало нулевым, и порядок тика молча изменился (M382)");
        }

        /// The tilt's quantization, re-spelled from the packer's law ON PURPOSE
        /// (lesson 427): a witness that called the code under test would prove
        /// self-consistency, not correctness.
        static sbyte Quant(float v) => (sbyte)math.clamp((int)math.round(v / PoseKey.TiltQuantStep),
            sbyte.MinValue, sbyte.MaxValue);

        [Test]
        public void ARewindWithNoRowMeetsTheLivePose()   // fixture 24, witness of M351
        {
            // ⭐⭐ THE DEGENERATE BRANCH: the ring holds no row for the asked
            // tick (tick 0 is never written), PosAt hands back a record it
            // BUILT, and its key is as invented as its flags -- so the resolver
            // reads the LIVE key, the way it reads the live stand and profile
            // there (RewoundBody's contract, extended to the pose). The
            // collector is sliding NOW; a round asking about tick 0 meets his
            // head where it lies in the slide. A resolver that read the built
            // record's zero key would judge a standing body and miss.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.SlideSpeed = 0f;
            SlidHeadLine(in cfg, out float lineY, out float height, out float fromX);
            var w = new SimulationWorld(24, cfg);
            var p = w.PlayerAt(0);
            p.Dir = BakedClips.CollectorForward;
            p.SlideTimer = cfg.Hero.SlideDuration;
            w.SetPlayerForTest(0, p);
            w.TickAll(new SimInput[1]);   // tick 1, sliding (row 1); tick 0 has no row EVER
            int depth = w.CurrentTick + 1;   // the first step, judged on tick 2, asks tick 0
            Assert.That(depth, Is.InRange(1, cfg.Arena.RewindCapTicks), "премисса: глубина внутри капа отмотки");
            w.History.PosAt(w.PlayerAt(0).HistorySlot, 0, w.PlayerAt(0).Pos, out _, out bool fromRow);
            Assert.IsFalse(fromRow, "премисса фикстуры: у тика 0 нет строки — иначе ветка не вырожденная");
            ShootAlongXRewound(w, in cfg, fromX, lineY, height, (byte)depth);
            w.TickAll(new SimInput[1]);
            Assert.Greater(w.PlayerAt(0).SlideTimer, 0f, "премисса фикстуры: при судействе сборщик всё ещё скользит");
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.PlayerDamaged, out SimEvent e),
                "вырожденная отмотка не взяла живую позу — судит по нулевому ключу построенной записи (M351)");
            Assert.AreEqual(HitZone.Head, e.Zone, "встречен не тот объём");
        }

        [Test]
        public void TwoRoundsWithDifferentDepthsGetDifferentPoses()   // fixture 25, witness of M352
        {
            // ⭐⭐ THE MEMO IS KEYED BY THE PAIR (slot, depth), NOT BY THE BODY:
            // two rounds judged on ONE tick against ONE collector, one with the
            // depth of the tick he slid on and one a tick shallower, meet two
            // different poses -- the first his sliding head, the second the
            // standing body's nothing. A memo keyed by the body alone would
            // hand the second round the first's sample, fresh, and both would
            // land.
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.SlideSpeed = 0f;
            SlidHeadLine(in cfg, out float lineY, out float height, out float fromX);
            var w = CollectorWhoSlidOnce(in cfg, out int slidTick);
            int deep = w.CurrentTick + 1 - slidTick;   // lands on the sliding row
            int shallow = deep - 1;                     // lands on the standing row after it
            Assert.That(deep, Is.InRange(2, cfg.Arena.RewindCapTicks), "премисса: обе глубины внутри капа и различны");
            ShootAlongXRewound(w, in cfg, fromX, lineY, height, (byte)deep);
            ShootAlongXRewound(w, in cfg, fromX, lineY, height, (byte)shallow);
            w.TickAll(new SimInput[1]);
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.PlayerDamaged),
                "два снаряда с разной глубиной получили одну позу — мемо ключуется телом, а не парой (M352)");
        }

        [Test]
        public void AReusedSlotCarriesItsNewTenantsKey_AndAFreeSlotNone()   // fixture 25a, witness of M460 and M461
        {
            // ⛔ THE JUDGED KEY TRAVELS BY SLOT, AND A SLOT CHANGES HANDS. A mob
            // born by a wave lands AFTER the producer's pass of its tick, so
            // its spawn-tick row is written with whatever its slot's key holds
            // -- which has to be ITS key, seeded at the spawn, not the last
            // tenant's (another archetype, another table: review A-1/B-2).
            // And a slot given back holds nothing, the invariant the rows keep.
            SimConfig cfg = TestConfigs.OpenField();
            var w = new SimulationWorld(25, cfg);
            w.SpawnMobForTest(MobType.Chaser, new float2(6f, 0f));
            var m = w.Mobs[0];
            m.Tilt = new float2(0.5f, 0f);   // a lean the key remembers: 50 codes
            w.SetMobForTest(0, m);
            w.TickAll(new SimInput[1]);
            int slot = w.Mobs[0].HistorySlot;
            Assert.AreEqual(50, w.JudgedKeyOf(slot).TiltX, "премисса: ключ жильца несёт его крен");

            w.ClearMobsForTest();   // the slot goes back
            Assert.AreEqual(0, w.JudgedKeyOf(slot).TiltX,
                "свободный слот держит ключ мертвеца — при возврате слота ключ не очищен (M461)");

            // ⚠ NOT AT (7, 0): a mob spawns facing the center, and the course
            // (-1, 0) encodes to the ZERO byte (ByteCodecs.Dir: (pi + pi) / 2pi *
            // 256 = 256 & 0xFF = 0), which made a fresh mob's key bit-identical
            // to the zero key and the seed invisible -- M460 survived on it.
            // From (0, 7) the course is (0, -1), byte 64, and the premise says so.
            w.SpawnMobForTest(MobType.Elite, new float2(0f, 7f));   // the lowest free slot: the same one
            Assert.AreEqual(slot, w.Mobs[0].HistorySlot, "премисса: новый жилец получил тот же слот");
            PoseKey seeded = w.JudgedKeyOf(slot);
            PoseKey own = PoseKey.FromMob(in w.Mobs[0]);
            Assert.AreNotEqual(0, own.Facing,
                "премисса: курс спавна кодируется ненулевым байтом, иначе засев неотличим от нулевого ключа");
            Assert.AreEqual(0, seeded.TiltX, "ключ переиспользованного слота — ключ прежнего жильца (M460)");
            Assert.AreEqual(own.Facing, seeded.Facing, "ключ переиспользованного слота не засеян новым жильцом (M460)");
        }

        [Test]
        public void ALiveRoundAndAOneTickRewoundRoundGetTheirOwnPoses()   // fixture 25b, witness of M462
        {
            // The pair the memo could alias: a round with no depth (the live
            // pose) and a round one tick deep (the last row) judged on ONE
            // tick against ONE collector, who slid on that last row and stands
            // now. The deep round meets his sliding head, the live one his
            // standing body's nothing -- one hit. A memo depth read AFTER the
            // round's countdown put both on entry (slot, 0) and handed the
            // live round the rewound pose (review B-1).
            SimConfig cfg = TestConfigs.OpenField();
            cfg.Hero.SlideSpeed = 0f;
            SlidHeadLine(in cfg, out float lineY, out float height, out float fromX);
            var w = new SimulationWorld(25, cfg);
            var p = w.PlayerAt(0);
            p.Dir = BakedClips.CollectorForward;
            w.SetPlayerForTest(0, p);
            var idle = new SimInput[1];
            w.TickAll(idle);                                                   // row 1: standing
            p = w.PlayerAt(0); p.SlideTimer = cfg.Hero.SlideDuration; w.SetPlayerForTest(0, p);
            w.TickAll(idle);                                                   // row 2: sliding -- the LAST row
            int slidTick = w.CurrentTick;
            p = w.PlayerAt(0); p.SlideTimer = 0f; w.SetPlayerForTest(0, p);    // standing now
            int deep = w.CurrentTick + 1 - slidTick;
            Assert.AreEqual(1, deep, "премисса: глубина ровно один тик — последняя строка");
            ShootAlongXRewound(w, in cfg, fromX, lineY, height, (byte)deep);   // asks the sliding row
            ShootAlongXRewound(w, in cfg, fromX, lineY, height, depth: 0);     // the live, standing body
            w.TickAll(idle);
            Assert.AreEqual(0f, w.PlayerAt(0).SlideTimer, "премисса: при судействе сборщик стоит");
            Assert.AreEqual(1, TestEvents.CountOf(w, SimEventKind.PlayerDamaged),
                "живой снаряд и снаряд глубины 1 получили одну позу — глубина мемо взята после декремента (M462)");
        }

        static ref readonly PoseTable ShippedTableOf(in SimConfig cfg, AnimatorCatalog.BodyKind kind)
        {
            switch (kind)
            {
                case AnimatorCatalog.BodyKind.Collector: return ref cfg.Hero.Poses;
                default: return ref SimConfig.MobConfigFor(in cfg, MobTypeOf(kind)).Poses;
            }
        }

        static MobType MobTypeOf(AnimatorCatalog.BodyKind kind) => kind switch
        {
            AnimatorCatalog.BodyKind.Chaser => MobType.Chaser,
            AnimatorCatalog.BodyKind.Gunner => MobType.Gunner,
            AnimatorCatalog.BodyKind.Elite => MobType.Elite,
            AnimatorCatalog.BodyKind.Director => MobType.Director,
            _ => throw new System.ArgumentOutOfRangeException(nameof(kind), kind, "not a mob"),
        };
    }
}
