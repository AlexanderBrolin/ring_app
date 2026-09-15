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
            // ⭐⭐ THE CENTRAL TEST OF THE RADIUS SPLIT: the shot misses the
            // PHYSICAL circle and still strikes a leg. Before this task it is a
            // miss; after it, a hit on the legs.
            //
            // ⛔⛔ app-saqr (T4b): THE SWUNG-OUT FOOT WAS THE FIXTURE'S OWN, AND
            // IT IS GONE. The chaser's table used to carry a foot pushed 0.9 m
            // aside BY HAND so that any leg point cleared his circle; measured,
            // his rest pose keeps his legs under him. The subject survives
            // untouched — his THIGH still reaches 0.83 m into the plan against a
            // physical circle of 0.50 — but WHERE it reaches is now a question
            // about the body, so the fixture asks the geometry instead of
            // restating a number nobody measured.
            SimConfig cfg = TestConfigs.OpenField();
            // ⛔⛔ THE FREEZE GOES BEFORE `new SimulationWorld`, AND THAT IS THE
            // WHOLE OF IT: the world copies the configuration in its constructor,
            // so editing the local cfg afterwards does not reach it (the
            // helper's own doc says exactly this). An unfrozen chaser walks
            // 0.173 m per tick and leaves this fixture's geometry.
            TestWorlds.FreezeArchetype(ref cfg, MobType.Chaser);
            var w = new SimulationWorld(11, cfg);
            var body = new float2(6f, 0f);
            TestWorlds.SpawnMobsAt(w, (MobType.Chaser, body));
            // Premise AS A PROPERTY: the gather circle has to be the wider of
            // the two, or the test is green on any implementation at all.
            Assert.Greater(cfg.Chaser.GatherRadius, cfg.Chaser.Radius,
                "премисса фикстуры: охват шире физического круга");

            // The leg that swings furthest ACROSS the shot line, and the bone
            // that does the swinging. ⚠ The shot runs down +X, so what makes the
            // lateral miss is the body frame's `.z` — `HitVolumes.ToWorld` lays
            // it on the world's `.y` (the same reading fixture 4a relies on).
            HitPart leg = default;
            float3 swung = default;
            bool found = false;
            foreach (HitPart candidate in cfg.Chaser.Parts)
            {
                if (candidate.Zone != HitZone.Legs) continue;
                float3 a = cfg.Chaser.Poses.Bones[candidate.BoneA];
                float3 b = cfg.Chaser.Poses.Bones[candidate.BoneB];
                float3 outer = a.z >= b.z ? a : b;
                if (found && outer.z <= swung.z) continue;
                leg = candidate; swung = outer; found = true;
            }
            Assert.IsTrue(found, "премисса: у чейзера есть объём ноги");

            // Aimed PAST that bone by the volume's own radius: the round then
            // grazes the leg while its axis misses the body's by as much as the
            // leg is wide. Level, at the bone's own height.
            var aim = new float2(body.x, swung.z + leg.Radius);
            var shooter = float2.zero;
            float2 ray = math.normalize(aim - shooter);
            float lateral = math.abs(ray.x * (body.y - shooter.y) - ray.y * (body.x - shooter.x));
            // ⛔ BOTH BOUNDS, AND THEY ARE THE WHOLE POINT: outside the physical
            // circle (or the mutant that gathers by `Radius` passes too), inside
            // the gather circle (or nothing is gathered on correct code either
            // and the fixture witnesses a miss on both sides).
            Assert.Greater(lateral, cfg.Chaser.Radius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: луч проходит ВНЕ физического круга тела — иначе мутант, "
                + "собирающий кандидатов по Radius, остаётся без точки приложения");
            Assert.Less(lateral, cfg.Chaser.GatherRadius + cfg.Weapon.ProjectileRadius,
                "премисса фикстуры: луч внутри круга ОХВАТА — иначе кандидат не соберётся "
                + "и на верном коде");

            TestWorlds.FireAimed3D(w, shooter, muzzleH: swung.y, targetXY: aim, targetH: swung.y);
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
        public void ABodyWithNoVolumesOrNoTableRefusesTheHit()
        {
            // ⛔ CARRIED OVER FROM HitZoneTests.Resolve_EmptyOrAbsentStack_
            // RefusesTheHit, WHICH app-94sk T3 DELETED WITH ITS RESOLVER
            // (Ruling 192). The assertion itself outlived the band era: a body
            // that declares no hit volume presents nothing to hit, and the
            // honest answer is a miss rather than a NullReferenceException.
            // Unreachable through SimConfigBuilder, whose own rule refuses an
            // empty stack ("Parts must not be empty -- a body with no parts
            // cannot be hit at all"); what it guards is the hand-built fixture.
            //
            // ⛔ THE TABLE IS THE SECOND HALF, AND THE BAND ERA HAD NO NOTION OF
            // IT: a volume is a pair of BONE INDICES now, so a body whose pose
            // table is empty names nothing either. That is the very failure
            // validation rule 12 (T4) exists to turn into a refusal, and until
            // it arrives this fixture is the only thing standing between a
            // table-less hand-built section and a body that is unhittable in
            // silence.
            SimConfig cfg = TestConfigs.OpenField();
            PoseTable table = TestConfigs.ChaserRestPose();
            HitPart[] parts = cfg.Chaser.Parts;
            var origin = new float3(1f, 0f, 0f);
            float3 p0 = new float3(0f, 0f, 1f), p1 = new float3(2f, 0f, 1f);
            float projR = cfg.Weapon.ProjectileRadius;

            // ⛔ THE POSITIVE CONTROL FIRST, or all three refusals below are
            // true for the wrong reason -- a step that misses anyway refuses
            // with parts and a table just as readily as without them.
            Assert.IsTrue(HitVolumes.Resolve(parts, in table, 0, float2.zero, origin, 0f, 1f,
                    p0, p1, projR, out HitZone zone, out _, out _, out _, out _),
                "премисса фикстуры: с частями и таблицей этот шаг обязан попадать");
            Assert.AreNotEqual(HitZone.None, zone, "премисса фикстуры: попадание названо зоной");

            Assert.IsFalse(HitVolumes.Resolve(System.Array.Empty<HitPart>(), in table, 0,
                    float2.zero, origin, 0f, 1f, p0, p1, projR,
                    out zone, out _, out _, out _, out _),
                "пустой набор объёмов разрешён в попадание");
            Assert.AreEqual(HitZone.None, zone, "пустой набор вернул небезразличную зону");

            Assert.IsFalse(HitVolumes.Resolve(null, in table, 0, float2.zero, origin, 0f, 1f,
                    p0, p1, projR, out zone, out _, out _, out _, out _),
                "отсутствующий набор объёмов разрешён в попадание");
            Assert.AreEqual(HitZone.None, zone, "отсутствующий набор вернул небезразличную зону");

            PoseTable empty = default;
            Assert.IsFalse(HitVolumes.Resolve(parts, in empty, 0, float2.zero, origin, 0f, 1f,
                    p0, p1, projR, out zone, out _, out _, out _, out _),
                "тело без таблицы поз разрешено в попадание — кости объёмов не названы ничем");
            Assert.AreEqual(HitZone.None, zone, "тело без таблицы вернуло небезразличную зону");
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
