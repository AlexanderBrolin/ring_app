using System.Collections.Generic;
using NUnit.Framework;
using Ring.Editor;
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
    }
}
