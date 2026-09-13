using NUnit.Framework;
using Ring.Simulation.Combat;
using Ring.Simulation.Core;
using Unity.Mathematics;

namespace Ring.Simulation.Tests
{
    /// app-94sk T2 (spec §3.4, §4.4 numbers 4/4a/11/12): the volume a round
    /// actually struck, once a body stopped being a circle around its own axis.
    ///
    /// ⚠ WHY A FILE OF ITS OWN, NEXT TO HitZoneTests: the neighbor is the home
    /// of the COLUMN — the silhouette gate and the crown — and stays it. What
    /// is measured here is the CHOICE OF A VOLUME, which the column could not
    /// express at all: its one radius made a shoulder-wide head the only shape
    /// there was.
    public class HitVolumeTests
    {
        [Test]
        public void ALegOutsideTheBodyCircleIsStillGathered()   // fixture 4, witness of M327
        {
            // ⭐⭐ THE CENTRAL TEST OF THE RADIUS SPLIT. The shot misses the body
            // circle (0.5) and hits the foot swung out of it (0.9 + 0.35).
            // Before this task it is a miss; after it, a hit on the legs.
            SimConfig cfg = TestConfigs.OpenField();
            // ⛔⛔ THE FREEZE GOES BEFORE `new SimulationWorld`, AND THAT IS THE
            // WHOLE OF IT: the world copies the configuration in its constructor,
            // so editing the local cfg afterwards does not reach it (the
            // helper's own doc says exactly this). An unfrozen chaser walks
            // 0.173 m per tick and leaves this fixture's geometry.
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);
            var w = new SimulationWorld(11, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            // Premise AS A PROPERTY: the foot has to stick out of the physical
            // circle, or the test is green on any implementation at all.
            Assert.Greater(cfg.Chaser.GatherRadius, cfg.Chaser.Radius,
                "премисса фикстуры: охват шире физического круга");
            // The foot is swung along the body's +z, which ToWorld carries onto
            // the world's +y. Shooting at leg height, aimed exactly where it is.
            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 0.4f,
                targetXY: new float2(6f, 0.9f), targetH: 0.4f);
            TestWorlds.RunUntilProjectilesDie(w);
            Assert.IsTrue(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out SimEvent e),
                "нога за кругом тела не собрана в кандидаты — широкая фаза читает Radius");
            Assert.AreEqual(HitZone.Legs, e.Zone, "зона не нога");
        }

        [Test]
        public void InsideTheGatherRadiusButPastEveryVolumeIsAMiss()   // fixture 4a, witness of M328
        {
            // ⛔⛔ THE NEGATIVE, AND WITHOUT IT AN IMPLEMENTATION THAT SCORES A HIT
            // ON THE CANDIDATE CIRCLE PASSES THE WHOLE PHASE GREEN.
            // ⚠ IT HAS NO RED PHASE BY CONSTRUCTION (the plan lists it among the
            // four such fixtures): "no hit" is true on the constant stub too. Its
            // only witness is M328, and that is said here rather than discovered
            // after the run.
            SimConfig cfg = TestConfigs.OpenField();
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);   // BEFORE the constructor
            var w = new SimulationWorld(12, cfg);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, new float2(6f, 0f)));
            // The foot is swung one way and we shoot the other: inside the gather
            // radius, past every volume.
            TestWorlds.FireAimed3D(w, float2.zero, muzzleH: 0.4f,
                targetXY: new float2(6f, -0.9f), targetH: 0.4f);
            TestWorlds.RunUntilProjectilesDie(w);
            Assert.IsFalse(TestEvents.TryFirstOf(w, SimEventKind.ProjectileHit, out _),
                "засчитано попадание по кандидатскому кругу, а не по объёмам");
        }

        [Test]
        public void TheHeadBeatsTheTorsoOnThePriorityLadder()   // fixture 11 (half), witness of M333
        {
            // ⛔ HALF OF FIXTURE 11: the zone ladder, head over torso. The other
            // half — an arm in front of a face — arrives in PLAN 2 together with
            // the arms zone (a witness may not outrun its own subject).
            // ⛔⛔ IT CALLS HitVolumes.Resolve DIRECTLY RATHER THAN THROUGH A
            // WORLD, and that is deliberate: the subject is THE RULE OF CHOICE,
            // and it has to be readable on one screen with the assertion.
            SimConfig cfg = TestConfigs.OpenField();
            PoseTable table = TestConfigs.ChaserRestPose();
            HitPart[] parts = cfg.Chaser.Parts;
            // The ray climbs through the chest INTO THE HEAD: the torso (wide,
            // R 0.50) is met EARLIER along t, the head (R 0.17) later. By t alone
            // the torso would win; by the zone's priority the head must.
            // ⛔ THE STEP IS IN THE WORLD FRAME: plan in .xy, height in .z. The
            // body stands at plan (6, 0); the ray crosses it along y from -1.2 to
            // +1.2 while climbing from 1.6 to 2.4.
            float3 p0 = new float3(6f, -1.2f, 1.6f), p1 = new float3(6f, 1.2f, 2.4f);
            bool hit = HitVolumes.Resolve(parts, in table, poseRow: 0, bodyTilt: float2.zero,
                bodyOrigin: new float3(6f, 0f, 0f), bodyFacingSin: 0f, bodyFacingCos: 1f,
                p0, p1, projRadius: cfg.Weapon.ProjectileRadius,
                out HitZone zone, out _, out _, out _, out float t);
            Assert.IsTrue(hit, "луч не встретил ни одного объёма — фикстура мерит не свой предмет");
            Assert.AreEqual(HitZone.Head, zone, "корпус отобрал попадание у головы — приоритета зоны нет");
            // Premise AS A PROPERTY: the torso must be struck EARLIER, or the test
            // is green without a priority ladder at all.
            bool torsoFirst = HitVolumes.Resolve(new[] { parts[1] }, in table, 0, float2.zero,
                new float3(6f, 0f, 0f), 0f, 1f, p0, p1, cfg.Weapon.ProjectileRadius,
                out _, out _, out _, out _, out float tTorso);
            Assert.IsTrue(torsoFirst && tTorso < t,
                "премисса фикстуры: корпус входит раньше головы, иначе приоритет не проверяется");
        }

        [Test]
        public void AnExactTieGoesToTheSmallerPartId()   // fixture 12, witness of M334
        {
            // Two MIRRORED capsules of ONE zone with equal t — the winner is
            // decided by the smaller PartId and by nothing else.
            // ⚠ THE TIE IS BUILT BY A STEP THAT STARTS INSIDE BOTH: the solver
            // hands back t = 0 to each (the same discipline SegmentCircleInterval
            // follows, Ruling 194), and then the PartId comparison is all there
            // is left to decide.
            PoseTable table = TestConfigs.TwinLegPose();     // two legs mirrored, +/-0.3 along z
            HitPart[] parts =
            {
                // ⛔ RestBottom/RestTop are the CAPSULE's extent — bone +/- radius
                // (HitPart's own definition): the bones sit at 0 and 0.9, the
                // radius is 0.35, so the extent is [-0.35, 1.25].
                new HitPart { BoneA = 0, BoneB = 1, Radius = 0.35f, Zone = HitZone.Legs,
                    DamageMult = 0.75f, PartId = 7, RestBottom = -0.35f, RestTop = 1.25f },
                new HitPart { BoneA = 2, BoneB = 3, Radius = 0.35f, Zone = HitZone.Legs,
                    DamageMult = 0.75f, PartId = 3, RestBottom = -0.35f, RestTop = 1.25f },
            };
            // In the world frame both legs land on the plan's +/-y; the step
            // stands between them at height 0.45 and creeps 0.1 along y.
            float3 p0 = new float3(0f, 0f, 0.45f), p1 = new float3(0f, 0.1f, 0.45f);
            // ⛔⛔ THE PREMISE IS "BOTH ARE STRUCK, AND BOTH AT t == 0", AND WITHOUT
            // IT MUTANT M334 SURVIVES: if only one capsule is struck, the answer
            // "3" is right on a reversed comparison too. So each is asked alone.
            bool hitFirst = HitVolumes.Resolve(new[] { parts[0] }, in table, 0, float2.zero,
                float3.zero, 0f, 1f, p0, p1, 0f, out _, out _, out _, out _, out float tFirst);
            bool hitSecond = HitVolumes.Resolve(new[] { parts[1] }, in table, 0, float2.zero,
                float3.zero, 0f, 1f, p0, p1, 0f, out _, out _, out _, out _, out float tSecond);
            Assert.IsTrue(hitFirst && hitSecond, "премисса: обе капсулы обязаны быть задеты");
            Assert.AreEqual(tFirst, tSecond, 1e-6f, "премисса: t обеих совпадают — иначе решает не PartId");

            bool hit = HitVolumes.Resolve(parts, in table, 0, float2.zero, float3.zero, 0f, 1f,
                p0, p1, 0f, out _, out _, out _, out byte partId, out float t);
            Assert.IsTrue(hit, "ни одна из двух капсул не задета — фикстура мерит не свой предмет");
            Assert.AreEqual(0f, t, 1e-6f, "шаг начинается внутри обеих: солвер обязан отдать t = 0");
            Assert.AreEqual((byte)3, partId, "тай-брейк недетерминирован — победил больший PartId");
        }

        [Test]
        public void ATallBoneIsNotReadAsAWideOne()   // fixture 12a, witness of M392
        {
            // ⛔⛔ THE WITNESS OF THE BORDER BETWEEN TWO FRAMES (side-quest
            // app-coou). A bone arrives in the BODY frame, where the height is
            // `.y`; the step arrives in the WORLD frame, where it is `.z`. Swap
            // the two components in ToWorld and NOTHING notices: the solver only
            // ever measures distances, the compiler cannot tell two float3 apart,
            // and the determinism goldens say nothing about it. A body handed in
            // lying on its side answers as confidently as an upright one.
            const float boneHeight = 1.75f;   // the middle of the capsule BY HEIGHT
            const float lateral = 0.25f;      // its swing IN PLAN
            const float capsuleR = 0.20f, projR = 0.05f;
            // Premise AS A PROPERTY: the height and the swing must differ by more
            // than the radii add up to, or swapping them is unobservable and this
            // fixture is green whatever the code does.
            Assert.Greater(math.abs(boneHeight - lateral), capsuleR + projR,
                "премисса фикстуры: высота и боковой вынос кости обязаны различаться");

            PoseTable table = new PoseTable
            {
                BoneCount = 2,
                ClipFirstRow = new[] { 0, 1 },
                Bones = new[]
                {
                    new float3(0f, boneHeight - 0.15f, lateral),   // BODY frame: height in .y
                    new float3(0f, boneHeight + 0.15f, lateral),
                },
                BlendThresholds = new[] { 0f },
            };
            HitPart[] parts =
            {
                new HitPart { BoneA = 0, BoneB = 1, Radius = capsuleR, Zone = HitZone.Body,
                    DamageMult = 1f, PartId = 0,
                    RestBottom = boneHeight - 0.15f - capsuleR, RestTop = boneHeight + 0.15f + capsuleR },
            };

            // HIGH and a little ASIDE — where the bone actually stands.
            Assert.IsTrue(HitVolumes.Resolve(parts, in table, 0, float2.zero, float3.zero, 0f, 1f,
                    new float3(-2f, lateral, boneHeight), new float3(2f, lateral, boneHeight), projR,
                    out _, out _, out float hitHeight, out _, out _),
                "кость не найдена там, где она стоит: высота тела не доехала до высоты мира");
            Assert.AreEqual(boneHeight, hitHeight, 1e-4f,
                "высота контакта пришла не из шага — её берут из .z, как и весь мир");

            // ...and LOW, FAR ASIDE — where the bone would have gone had the two
            // components changed places.
            Assert.IsFalse(HitVolumes.Resolve(parts, in table, 0, float2.zero, float3.zero, 0f, 1f,
                    new float3(-2f, boneHeight, lateral), new float3(2f, boneHeight, lateral), projR,
                    out _, out _, out _, out _, out _),
                "попадание засчитано по ЛЕЖАЩЕЙ кости — .y и .z обменялись местами в ToWorld");
        }
    }
}
