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
                math.max(BakedClips.MeleeOf(MobType.Chaser), BakedClips.RangedOf(MobType.Chaser)) + 1, Rows);
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
                    Assert.Less(p.LowerPhase, PoseTable.RowsOf(in cfg.Hero.Poses, a),
                        $"тик {tick}: фаза {p.LowerPhase} вышла за строки клипа {a} — петли нет");
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
