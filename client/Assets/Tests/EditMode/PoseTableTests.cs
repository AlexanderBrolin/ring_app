using System.Collections.Generic;
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
            // `ClipFirstRow[SlideClipIndex]`, and the ONLY thing that makes
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
                Assert.Greater(baked.ClipFirstRow.Length, TestConfigs.SlideClipIndex + 1,
                    "у запечённой таблицы сборщика обязан быть клип 1");

                // The slide is SHORTER than standing — that is the whole point
                // of sliding — so its crown has to sit below the rest pose's.
                // ⛔ A property, not a literal: a literal would have to be
                // re-typed every time the clip changes, and would then be the
                // thing that broke instead of the contract.
                float restCrown = HitParts.PoseTop(
                    TestConfigs.Default().Hero.Parts, in baked, 0, float2.zero);
                float slideCrown = HitParts.PoseTop(TestConfigs.Default().Hero.Parts, in baked,
                    baked.ClipFirstRow[TestConfigs.SlideClipIndex], float2.zero);
                Assert.Less(slideCrown, restCrown,
                    $"клип {TestConfigs.SlideClipIndex} запечённой таблицы обязан быть СЛАЙДОМ: "
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
            // The one-to-one row map of T6a: phase p is row p of its clip, and
            // past the clip's end the LAST row is held -- not the next clip's
            // first row, not a row that is not there. Looping is the
            // producer's business (T6b); the rate-aware map is T6c's.
            // A two-row clip followed by a one-row clip, one bone, rows told
            // apart by z: 0, 1 and then 9 for the foreign clip.
            var table = new PoseTable
            {
                BoneCount = 1,
                ClipFirstRow = new[] { 0, 2, 3 },
                Bones = new[] { new float3(0f, 0f, 0f), new float3(0f, 0f, 1f), new float3(0f, 0f, 9f) },
                BlendThresholds = new[] { 0f },
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
    }
}
